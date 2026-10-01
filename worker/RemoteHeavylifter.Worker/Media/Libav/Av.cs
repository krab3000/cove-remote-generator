using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>libav return codes, rationals and dictionaries.</summary>
internal static unsafe class Av
{
    public static readonly int Again = ffmpeg.AVERROR(ffmpeg.EAGAIN);
    public static readonly int Eof = ffmpeg.AVERROR_EOF;
    public static readonly int Exit = ffmpeg.AVERROR_EXIT;
    public static readonly AVRational TimeBaseQ = new() { num = 1, den = ffmpeg.AV_TIME_BASE };

    /// <summary>buffersrc flag: the filter graph takes its own reference instead of the caller's frame.</summary>
    public const int BufferSrcKeepRef = 8;

    /// <summary>AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX.</summary>
    public const int HwConfigDeviceCtx = 1;

    public static string Error(int code)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];
        ffmpeg.av_strerror(code, buffer, size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {code}";
    }

    /// <summary>Throws a <see cref="MediaException"/> (or the interrupt's cancellation/timeout) when <paramref name="ret"/> is an error.</summary>
    public static int Check(this int ret, string what, Interrupt? interrupt = null)
    {
        if (ret >= 0)
            return ret;
        if (ret == Exit && interrupt is not null)
            interrupt.ThrowIfStopped();
        throw new MediaException($"{what}: {Error(ret)}");
    }

    public static double Seconds(long ts, AVRational timeBase) => ts * ffmpeg.av_q2d(timeBase);

    /// <summary>An AVDictionary built from pairs; the caller frees it with <see cref="Free"/> after the open call.</summary>
    public static AVDictionary* Dictionary(IEnumerable<KeyValuePair<string, string>> entries)
    {
        AVDictionary* dict = null;
        foreach (var (key, value) in entries)
            ffmpeg.av_dict_set(&dict, key, value, 0);
        return dict;
    }

    /// <summary>Keys an open call left unused (libav removes the ones it consumed), then frees the dictionary.</summary>
    public static List<string> Free(AVDictionary* dict)
    {
        var unused = new List<string>();
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(dict, "", entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) is not null)
            unused.Add(Marshal.PtrToStringUTF8((IntPtr)entry->key) ?? "");
        ffmpeg.av_dict_free(&dict);
        return unused;
    }
}

/// <summary>
/// Lets a blocking libav call (an HTTP read, a probe) notice cancellation: libav polls the callback and fails the call
/// with AVERROR_EXIT. A deadline turns into a <see cref="MediaException"/> ("timed out"), so a stuck step fails like a
/// broken one; the caller's own cancellation stays an <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed unsafe class Interrupt : IDisposable
{
    private static readonly AVIOInterruptCB_callback Callback = opaque =>
        GCHandle.FromIntPtr((IntPtr)opaque).Target is Interrupt { Stopped: true } ? 1 : 0;

    private readonly CancellationToken _outer;
    private readonly CancellationTokenSource _deadline;
    private readonly string _what;
    private GCHandle _handle;

    public Interrupt(CancellationToken outer, TimeSpan timeout, string what)
    {
        _outer = outer;
        _what = what;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _deadline.CancelAfter(timeout);
        _handle = GCHandle.Alloc(this);
    }

    public bool Stopped => _deadline.IsCancellationRequested;

    public AVIOInterruptCB Native => new() { callback = Callback, opaque = (void*)GCHandle.ToIntPtr(_handle) };

    /// <summary>Between libav calls: stop when cancelled or past the deadline.</summary>
    public void ThrowIfStopped()
    {
        _outer.ThrowIfCancellationRequested();
        if (_deadline.IsCancellationRequested)
            throw new MediaException($"{_what}: timed out");
    }

    public void Dispose()
    {
        if (_handle.IsAllocated)
            _handle.Free();
        _deadline.Dispose();
    }
}

/// <summary>Routes libav's log to the worker's: errors as warnings, warnings at debug, each message rate-limited.</summary>
internal static unsafe class LibavLog
{
    private const int LineSize = 1024;
    private static readonly ConcurrentDictionary<string, (long Window, int Count)> Recent = new(StringComparer.Ordinal);
    private static readonly av_log_set_callback_callback Callback = OnLog;
    private static ILogger? _logger;

    public static void Install(ILogger logger)
    {
        _logger = logger;
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        ffmpeg.av_log_set_callback(Callback);
    }

    private static void OnLog(void* avcl, int level, string format, byte* args)
    {
        if (_logger is not { } logger || level > ffmpeg.av_log_get_level())
            return;
        var line = stackalloc byte[LineSize];
        var printPrefix = 1;
        ffmpeg.av_log_format_line2(avcl, level, format, args, line, LineSize, &printPrefix);
        var text = (Marshal.PtrToStringUTF8((IntPtr)line) ?? "").Trim();
        if (text.Length == 0)
            return;

        // At most 20 lines per 10 s for messages that start alike (a corrupt stream repeats the same complaint).
        var key = text.Length > 48 ? text[..48] : text;
        var window = Environment.TickCount64 / 10_000;
        var (seen, count) = Recent.AddOrUpdate(key, (window, 1), (_, old) => old.Window == window ? (window, old.Count + 1) : (window, 1));
        if (seen == window && count > 20)
            return;
        if (Recent.Count > 1000)
            Recent.Clear();
        if (level <= ffmpeg.AV_LOG_ERROR)
            logger.LogWarning("libav: {Message}", text);
        else
            logger.LogDebug("libav: {Message}", text);
    }
}
