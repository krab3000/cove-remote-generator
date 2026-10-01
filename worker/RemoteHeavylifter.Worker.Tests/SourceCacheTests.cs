using System.Net;
using System.Net.Http.Headers;
using RemoteHeavylifter.Worker.Transport;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>The worker's loopback source cache between ffmpeg and Cove.</summary>
public sealed class SourceCacheTests : IDisposable
{
    private const int Block = SourceCache.BlockSize;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempDir _tmp = new();
    private readonly HttpClient _http = new();
    private readonly byte[] _data;
    private readonly string _file;

    public SourceCacheTests()
    {
        // Several blocks plus a short last one.
        _data = new byte[5 * Block + 12345];
        new Random(7).NextBytes(_data);
        _file = _tmp["source.bin"];
        File.WriteAllBytes(_file, _data);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tmp.Dispose();
    }

    private async Task<(HttpStatusCode Status, byte[] Body, HttpResponseMessage Response)> GetAsync(string url, long? from = null, long? to = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from is not null || to is not null)
            request.Headers.Range = new RangeHeaderValue(from, to);
        var response = await _http.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(Ct), response);
    }

    [Theory]
    [InlineData(0L, 99L)]
    [InlineData(Block - 10L, Block + 10L)]
    [InlineData(3L * Block + 5, null)]
    [InlineData(null, 500L)]
    [InlineData(0L, 100L * Block)]
    public async Task Ranges_match_the_file(long? from, long? to)
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(1 << 30);
        using var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;

        var (status, body, response) = await GetAsync(source.LocalUrl, from, to);

        Assert.Equal(HttpStatusCode.PartialContent, status);
        var start = from ?? _data.Length - to!.Value;
        var end = from is null || to is null ? _data.Length - 1 : Math.Min(to.Value, _data.Length - 1);
        Assert.Equal(_data[(int)start..(int)(end + 1)], body);
        Assert.Equal(_data.Length, response.Content.Headers.ContentRange!.Length);
    }

    [Fact]
    public async Task Whole_file_without_range_and_head()
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(1 << 30);
        using var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;

        var (status, body, _) = await GetAsync(source.LocalUrl);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_data, body);

        using var head = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head, source.LocalUrl), Ct);
        Assert.Equal(_data.Length, head.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task Rereading_the_header_does_not_go_back_to_cove()
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(1 << 30);
        using var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;

        for (var i = 0; i < 5; i++)
            Assert.Equal(_data[..1000], (await GetAsync(source.LocalUrl, 0, 999)).Body);

        Assert.Single(cove.Requests);
        Assert.Equal("bytes=0-" + (SourceCache.ReadAheadBlocks * Block - 1), cove.Requests.Single().Range);
    }

    [Fact]
    public async Task Concurrent_readers_fetch_each_block_once()
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(1 << 30);
        using var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;

        var reads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => GetAsync(source.LocalUrl, 0, null)));

        Assert.All(reads, r => Assert.Equal(_data, r.Body));
        var blocks = (_data.Length + Block - 1) / Block;
        Assert.InRange(cove.Requests.Count, 1, blocks);
        Assert.Equal(blocks * (long)Block - (Block - _data.Length % Block), host.Cache.CachedBytes);
    }

    [Fact]
    public async Task A_small_budget_evicts_and_still_serves_the_right_bytes()
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(2L * Block);
        using var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;

        for (var i = 0; i < 2; i++)
            Assert.Equal(_data, (await GetAsync(source.LocalUrl, 0, null)).Body);

        Assert.InRange(host.Cache.CachedBytes, 1, 2L * Block);
        Assert.True(cove.Requests.Count > 2);
    }

    [Fact]
    public async Task A_refused_token_keeps_coves_status()
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(1 << 30, token: "not-the-token");
        using var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(source.LocalUrl, 0, 10)).Status);
    }

    [Fact]
    public async Task Unknown_and_disposed_sources_are_not_served()
    {
        await using var cove = await FakeCove.StartAsync(_file);
        await using var host = await CacheHost.StartAsync(1 << 30);
        var source = (await host.Cache.OpenAsync(cove.Url, _data.Length, Ct))!;
        Assert.Equal(HttpStatusCode.PartialContent, (await GetAsync(source.LocalUrl, 0, 10)).Status);

        var unknown = source.LocalUrl[..source.LocalUrl.LastIndexOf('/')] + "/0123456789abcdef0123456789abcdef";
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(unknown, 0, 10)).Status);

        source.Dispose();
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(source.LocalUrl, 0, 10)).Status);
        Assert.Equal(0, host.Cache.CachedBytes);
    }

    [Fact]
    public async Task Disabled_cache_opens_nothing()
    {
        var cache = new SourceCache(0, (_, _, _, _) => throw new InvalidOperationException());
        cache.SetLocalBase("http://127.0.0.1:1");
        Assert.Null(await cache.OpenAsync("http://cove/source", 100, Ct));
    }
}
