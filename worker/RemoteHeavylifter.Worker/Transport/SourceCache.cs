using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using RemoteHeavylifter.Worker.Tasks;

namespace RemoteHeavylifter.Worker.Transport;

/// <summary>Reads <paramref name="url"/> bytes <c>from..to</c> (inclusive) from Cove.</summary>
public delegate Task<byte[]> RangeFetcher(string url, long from, long to, CancellationToken ct);

/// <summary>
/// A loopback copy of each running task's source, so ffmpeg's ~100 opens and seeks per video don't each cost a new
/// HTTPS connection to Cove and a fresh download of the container header. Bytes are fetched from Cove in blocks over
/// pooled connections and kept in RAM (never on disk) in one LRU shared by all tasks; a task's blocks go as soon as
/// its source is disposed.
/// </summary>
public sealed class SourceCache(long budgetBytes, RangeFetcher fetch)
{
    public const int BlockSize = 1 << 20;
    public const string PathPrefix = "/local/source/";

    /// <summary>A miss fetches this many following missing blocks too: ffmpeg mostly reads forward.</summary>
    internal const int ReadAheadBlocks = 4;

    private readonly ConcurrentDictionary<string, CachedSource> _sources = new(StringComparer.Ordinal);
    private readonly LinkedList<(CachedSource Source, long Index)> _lru = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource<string?> _localBase = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _bytes;
    private int _fetches;

    public bool Enabled => budgetBytes > 0;

    /// <summary>Bytes currently cached (completed blocks).</summary>
    public long CachedBytes
    {
        get
        {
            lock (_gate)
                return _bytes;
        }
    }

    /// <summary>Upstream range requests made so far.</summary>
    public int Fetches => Volatile.Read(ref _fetches);

    /// <summary>The worker's loopback address, e.g. http://127.0.0.1:51234, known once the host has started;
    /// null when there is none, which turns the cache off.</summary>
    public void SetLocalBase(string? baseUrl) => _localBase.TrySetResult(baseUrl?.TrimEnd('/'));

    /// <summary>Registers a source for ffmpeg to read at <see cref="CachedSource.LocalUrl"/>; null when the cache is
    /// off (ffmpeg then reads from Cove directly).</summary>
    public async Task<CachedSource?> OpenAsync(string url, long length, CancellationToken ct)
    {
        if (!Enabled || length <= 0)
            return null;
        if (await _localBase.Task.WaitAsync(ct) is not { } baseUrl)
            return null;
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var source = new CachedSource(this, key, url, length, $"{baseUrl}{PathPrefix}{key}");
        _sources[key] = source;
        return source;
    }

    /// <summary>Serves <c>GET/HEAD /local/source/{key}</c> with single byte ranges, to loopback callers only.</summary>
    public async Task ServeAsync(HttpContext http, string key)
    {
        var ct = http.RequestAborted;
        if (http.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer) || !_sources.TryGetValue(key, out var source))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        source.CountRead();
        var length = source.Length;
        long from = 0, to = length - 1;
        var partial = false;
        var range = http.Request.GetTypedHeaders().Range;
        if (range is { Ranges.Count: 1 } && range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase))
        {
            var r = range.Ranges.First();
            if (r.From is { } start)
            {
                from = start;
                if (r.To is { } end)
                    to = Math.Min(end, length - 1);
            }
            else if (r.To is { } suffix)
            {
                from = Math.Max(0, length - suffix);
            }
            partial = true;
        }
        http.Response.Headers.AcceptRanges = "bytes";
        if (from >= length || from > to)
        {
            http.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            http.Response.Headers.ContentRange = $"bytes */{length.ToString(CultureInfo.InvariantCulture)}";
            return;
        }

        // The first block is fetched before any header goes out, so a refusal by Cove keeps its status.
        byte[] block;
        try
        {
            block = await source.GetBlockAsync(from / BlockSize, ct);
        }
        catch (SourceRefusedException ex)
        {
            http.Response.StatusCode = ex.Status;
            return;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        http.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        http.Response.ContentType = "application/octet-stream";
        http.Response.ContentLength = to - from + 1;
        if (partial)
            http.Response.Headers.ContentRange = new ContentRangeHeaderValue(from, to, length).ToString();
        if (HttpMethods.IsHead(http.Request.Method))
            return;

        try
        {
            var position = from;
            while (true)
            {
                var offset = (int)(position % BlockSize);
                var count = (int)Math.Min(block.Length - offset, to - position + 1);
                await http.Response.Body.WriteAsync(block.AsMemory(offset, count), ct);
                source.CountServed(count);
                position += count;
                if (position > to)
                    return;
                block = await source.GetBlockAsync(position / BlockSize, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ffmpeg closed the connection (seek or done).
        }
        catch (Exception)
        {
            // Mid-stream the status is gone; drop the connection so ffmpeg reconnects with a range request.
            http.Abort();
        }
    }

    internal void Remove(CachedSource source)
    {
        _sources.TryRemove(source.Key, out _);
        lock (_gate)
        {
            foreach (var node in source.Nodes.Values)
            {
                _lru.Remove(node);
                _bytes -= node.Value.Source.BlockLength(node.Value.Index);
            }
            source.Nodes.Clear();
        }
    }

    internal Task<byte[]> FetchAsync(string url, long from, long to, CancellationToken ct)
    {
        Interlocked.Increment(ref _fetches);
        return fetch(url, from, to, ct);
    }

    /// <summary>Records a completed block as most recently used, then evicts the least recently used over budget.</summary>
    internal void Touch(CachedSource source, long index, bool added)
    {
        lock (_gate)
        {
            if (source.Disposed)
                return;
            if (source.Nodes.TryGetValue(index, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return;
            }
            if (!added)
                return;
            source.Nodes[index] = _lru.AddFirst((source, index));
            _bytes += source.BlockLength(index);
            while (_bytes > budgetBytes && _lru.Last is { } last && last != _lru.First)
            {
                var (owner, evicted) = last.Value;
                _lru.RemoveLast();
                owner.Nodes.Remove(evicted);
                owner.Forget(evicted);
                _bytes -= owner.BlockLength(evicted);
            }
        }
    }
}

/// <summary>Per-source cache counters. Block lookups are hits (cached), waits (another reader's fetch was already
/// under way) or misses (started a fetch from Cove); reads are ffmpeg's HTTP requests to the loopback endpoint.</summary>
public sealed record CacheStats(
    long Hits, long Waits, long Misses, long Fetches, long BytesFetched, TimeSpan FetchTime, long Reads, long BytesServed)
{
    public double HitRate => Hits + Waits + Misses == 0 ? 0 : (double)(Hits + Waits) / (Hits + Waits + Misses);

    /// <summary>What happened between two snapshots of the same source.</summary>
    public static CacheStats operator -(CacheStats after, CacheStats before) => new(
        after.Hits - before.Hits, after.Waits - before.Waits, after.Misses - before.Misses, after.Fetches - before.Fetches,
        after.BytesFetched - before.BytesFetched, after.FetchTime - before.FetchTime, after.Reads - before.Reads,
        after.BytesServed - before.BytesServed);
}

/// <summary>One task's source in the <see cref="SourceCache"/>; ffmpeg reads it at <see cref="LocalUrl"/>.</summary>
public sealed class CachedSource : IDisposable
{
    private readonly SourceCache _cache;
    private readonly ConcurrentDictionary<long, Task<byte[]>> _blocks = new();
    private readonly CancellationTokenSource _disposed = new();

    internal CachedSource(SourceCache cache, string key, string url, long length, string localUrl)
    {
        _cache = cache;
        Key = key;
        Url = url;
        Length = length;
        LocalUrl = localUrl;
        BlockCount = (length + SourceCache.BlockSize - 1) / SourceCache.BlockSize;
    }

    internal string Key { get; }
    public string Url { get; }
    public long Length { get; }
    public string LocalUrl { get; }
    internal long BlockCount { get; }

    /// <summary>LRU nodes of this source's completed blocks; guarded by the cache's lock.</summary>
    internal Dictionary<long, LinkedListNode<(CachedSource Source, long Index)>> Nodes { get; } = [];

    internal bool Disposed => _disposed.IsCancellationRequested;

    internal int BlockLength(long index) => (int)Math.Min(SourceCache.BlockSize, Length - index * SourceCache.BlockSize);

    internal void Forget(long index) => _blocks.TryRemove(index, out _);

    private long _hits, _waits, _misses, _fetches, _bytesFetched, _fetchTicks, _reads, _bytesServed;

    /// <summary>What the cache did for this source so far.</summary>
    public CacheStats Stats => new(
        Interlocked.Read(ref _hits), Interlocked.Read(ref _waits), Interlocked.Read(ref _misses),
        Interlocked.Read(ref _fetches), Interlocked.Read(ref _bytesFetched), TimeSpan.FromTicks(Interlocked.Read(ref _fetchTicks)),
        Interlocked.Read(ref _reads), Interlocked.Read(ref _bytesServed));

    internal void CountRead() => Interlocked.Increment(ref _reads);

    internal void CountServed(int bytes) => Interlocked.Add(ref _bytesServed, bytes);

    public async Task<byte[]> GetBlockAsync(long index, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (!_blocks.TryGetValue(index, out var block))
        {
            Interlocked.Increment(ref _misses);
            // A fetch that failed straight away has already dropped its claim; claim again.
            do
                StartFetch(index);
            while (!_blocks.TryGetValue(index, out block));
        }
        else if (block.IsCompletedSuccessfully)
        {
            Interlocked.Increment(ref _hits);
        }
        else
        {
            Interlocked.Increment(ref _waits);
        }
        var bytes = await block.WaitAsync(ct);
        _cache.Touch(this, index, added: false);
        return bytes;
    }

    /// <summary>Claims <paramref name="index"/> and the missing blocks right after it, then fetches them in one request.
    /// Other readers of those blocks wait on the same tasks instead of fetching again.</summary>
    private void StartFetch(long index)
    {
        var pending = new List<(long Index, TaskCompletionSource<byte[]> Tcs)>();
        for (var i = index; i < BlockCount && pending.Count < SourceCache.ReadAheadBlocks; i++)
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_blocks.TryAdd(i, tcs.Task))
                break;
            pending.Add((i, tcs));
        }
        if (pending.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            var first = pending[0].Index * SourceCache.BlockSize;
            var last = Math.Min(Length, (pending[^1].Index + 1) * SourceCache.BlockSize) - 1;
            try
            {
                // Not the reader's token: other readers may be waiting on these blocks after it has gone.
                var started = Stopwatch.GetTimestamp();
                var bytes = await _cache.FetchAsync(Url, first, last, _disposed.Token);
                Interlocked.Add(ref _fetchTicks, Stopwatch.GetElapsedTime(started).Ticks);
                Interlocked.Increment(ref _fetches);
                Interlocked.Add(ref _bytesFetched, bytes.Length);
                foreach (var (i, tcs) in pending)
                {
                    var offset = (int)((i - pending[0].Index) * SourceCache.BlockSize);
                    tcs.SetResult(bytes.AsSpan(offset, BlockLength(i)).ToArray());
                    _cache.Touch(this, i, added: true);
                }
            }
            catch (Exception ex)
            {
                // Failed blocks are dropped so the next read tries again.
                foreach (var (i, tcs) in pending)
                {
                    _blocks.TryRemove(new KeyValuePair<long, Task<byte[]>>(i, tcs.Task));
                    tcs.TrySetException(ex);
                }
            }
        });
    }

    public void Dispose()
    {
        if (Disposed)
            return;
        _disposed.Cancel();
        _cache.Remove(this);
        _blocks.Clear();
    }
}
