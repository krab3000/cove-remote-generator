using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>Settings the libav engine takes from the worker's options.</summary>
/// <param name="DecodeThreads">Decoder threads; null picks per operation (see <see cref="LibavMediaEngine"/>).</param>
/// <param name="Concurrency">Videos the worker generates at once, which share the CPU.</param>
/// <param name="FormatOptions">HL_FFMPEG_INPUT_ARGS as demuxer/protocol options.</param>
public sealed record LibavEngineOptions(
    int? DecodeThreads, int Concurrency, string Encoder, IReadOnlyList<KeyValuePair<string, string>> FormatOptions);

/// <summary>
/// libav in this process: each step opens its source once and keeps one decoder (on the GPU when asked), seeks only when
/// decoding on would cost more, and hands frames over in memory. Seeks, filters and the cover's JPEG settings mirror
/// the command line's, so outputs match <see cref="CliMediaEngine"/>'s closely (not bit for bit).
/// </summary>
public sealed class LibavMediaEngine : IMediaEngine
{
    private readonly LibavInfo _info;
    private readonly LibavEngineOptions _options;
    private readonly IMediaEngine _fallback;
    private readonly ILogger _log;

    public LibavMediaEngine(LibavInfo info, LibavEngineOptions options, IMediaEngine fallback, ILogger? logger = null)
    {
        _info = info;
        _options = options;
        _fallback = fallback;
        _log = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// HL_FFMPEG_INPUT_ARGS (<c>-key value</c> pairs) as demuxer/protocol options. Hardware decoding options are left
    /// out: libav decodes on the GPU through HL_HWACCEL.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> FormatOptionsFrom(IReadOnlyList<string> args, Action<string>? ignored = null)
    {
        var options = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < args.Count; i++)
        {
            var key = args[i].TrimStart('-');
            // A flag without a value (the next argument is another option) is switched on.
            var value = i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : "1";
            if (key.StartsWith("hwaccel", StringComparison.Ordinal))
                ignored?.Invoke(key);
            else
                options.Add(new(key, value));
        }
        return options;
    }

    public string Description => $"libav (FFmpeg {_info.Version}) from {_info.Directory}";

    /// <summary>
    /// Decoder threads for frame grabs: this video's share of the CPU (every video the worker runs gets one). Frame
    /// threading, as the decoder walks whole GOPs between targets; its few frames of delay after a seek cost far less.
    /// </summary>
    private int FrameThreads => _options.DecodeThreads ?? Math.Clamp(Environment.ProcessorCount / Math.Max(1, _options.Concurrency), 1, 16);

    /// <summary>Runs blocking libav work on its own thread, so it never holds a thread-pool thread for minutes.</summary>
    private static Task<T> Run<T>(Func<T> work, CancellationToken ct) =>
        Task.Factory.StartNew(work, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public Task<double> ProbeDurationAsync(MediaSource source, CancellationToken ct) => Run(() =>
    {
        using var interrupt = new Interrupt(ct, TimeSpan.FromSeconds(30), "probe");
        try
        {
            using var input = LibavInput.OpenFormatOnly(source.Software, interrupt, _log, "probe");
            unsafe
            {
                var duration = input.Format->duration;
                return duration == ffmpeg.AV_NOPTS_VALUE || duration <= 0 ? 0.0 : duration / (double)ffmpeg.AV_TIME_BASE;
            }
        }
        catch (MediaException ex)
        {
            _log.LogDebug("probe: {Error}", ex.Message);
            return 0.0;
        }
    }, ct);

    /// <summary>Opens <paramref name="source"/>, falling back to software decoding when the GPU cannot be set up.</summary>
    private LibavInput Open(MediaSource source, DecodeSettings settings, Interrupt interrupt, string what)
    {
        if (!source.HardwareDecode)
            return LibavInput.Open(source, settings, interrupt, _log, what);
        try
        {
            return LibavInput.Open(source, settings, interrupt, _log, what);
        }
        catch (HardwareDecodeException ex)
        {
            _log.LogDebug("{Error}; decoding in software", ex.Message);
            return LibavInput.Open(source.Software, settings, interrupt, _log, what);
        }
    }

    public Task CoverAsync(MediaSource source, double seek, CoverSpec spec, string workDir, string output, CancellationToken ct) => Run(() =>
    {
        const string what = "cover";
        using var interrupt = new Interrupt(ct, Timing.FrameDecodeTimeout(source.Size), what);
        var settings = new DecodeSettings(FrameThreads, SliceThreads: false, Lenient: true, _options.FormatOptions);
        var input = Open(source, settings, interrupt, what);
        try
        {
            byte[] jpeg;
            unsafe
            {
                var target = SeekPlanner.TargetMicroseconds(seek, 2, input.StartMicroseconds);
                AVFrame* frame;
                try
                {
                    frame = input.FrameAt(target, what);
                }
                catch (HardwareDecodeException ex) when (input.OnGpu)
                {
                    _log.LogDebug("{Error}; decoding in software", ex.Message);
                    input.Dispose();
                    input = Open(source.Software, settings, interrupt, what);
                    frame = input.FrameAt(target, what);
                }
                if (frame is null)
                    throw new MediaException($"cover: no frame at {Timing.Fixed(seek, 2)} s");

                var rotate = FilterChain.Join(input.SoftwareFormatFilter(frame), AutoRotate.Filters(input.DisplayMatrix(frame)));
                try
                {
                    jpeg = Encode(frame, input.TimeBase, FilterChain.Join(rotate, spec.Filter, "format=yuvj420p"));
                }
                catch (MediaException ex) when (!string.IsNullOrEmpty(spec.Filter) && spec.FallbackWithoutFilter)
                {
                    // A build without v360, or a layout the filter rejects, still gets the full frame.
                    _log.LogDebug("{Error}; cover without its filter", ex.Message);
                    jpeg = Encode(frame, input.TimeBase, FilterChain.Join(rotate, "format=yuvj420p"));
                }
            }
            File.WriteAllBytes(output, jpeg);
            return true;
        }
        finally
        {
            input.Dispose();
        }

        unsafe byte[] Encode(AVFrame* frame, AVRational timeBase, string chain)
        {
            var filtered = FilterChain.RunOnce(frame, timeBase, chain, 1, what);
            try
            {
                return Frames.EncodeJpeg(filtered, 2, what);
            }
            finally
            {
                ffmpeg.av_frame_free(&filtered);
            }
        }
    }, ct);

    public Task<Image<Rgb24>?[]> ExtractFramesAsync(
        MediaSource source, IReadOnlyList<double> timestamps, int width, string? preFilter, string workDir, CancellationToken ct) => Run(() =>
    {
        const string what = "frames";
        using var interrupt = new Interrupt(ct, TimeSpan.FromSeconds(60 + 6 * timestamps.Count), what);
        var settings = new DecodeSettings(FrameThreads, SliceThreads: false, FormatOptions: _options.FormatOptions);
        var images = new Image<Rgb24>?[timestamps.Count];
        var input = Open(source, settings, interrupt, what);
        try
        {
            var scale = width > 0 ? $"scale={width}:-2" : null;
            // Ascending, so the decoder only ever moves forward (callers already pass them in order).
            foreach (var i in Enumerable.Range(0, timestamps.Count).OrderBy(i => timestamps[i]))
            {
                unsafe
                {
                    var target = SeekPlanner.TargetMicroseconds(Math.Max(0, timestamps[i]), 3, input.StartMicroseconds);
                    AVFrame* frame;
                    try
                    {
                        frame = input.FrameAt(target, what);
                    }
                    catch (HardwareDecodeException ex) when (input.OnGpu)
                    {
                        _log.LogDebug("{Error}; decoding in software", ex.Message);
                        input.Dispose();
                        input = Open(source.Software, settings, interrupt, what);
                        frame = input.FrameAt(target, what);
                    }
                    if (frame is null)
                        continue;

                    var chain = FilterChain.Join(input.SoftwareFormatFilter(frame), AutoRotate.Filters(input.DisplayMatrix(frame)), preFilter, scale, "format=rgb24");
                    AVFrame* rgb = null;
                    try
                    {
                        rgb = FilterChain.RunOnce(frame, input.TimeBase, chain, 1, what);
                        images[i] = Frames.ToImage(rgb);
                    }
                    catch (MediaException ex)
                    {
                        // Like a failed command line batch: this frame is missing, the others still count.
                        _log.LogDebug("frame {Index} at {Seconds} s: {Error}", i, Timing.Fixed(timestamps[i], 3), ex.Message);
                    }
                    finally
                    {
                        ffmpeg.av_frame_free(&rgb);
                    }
                }
            }
            return images;
        }
        catch
        {
            foreach (var image in images)
                image?.Dispose();
            throw;
        }
        finally
        {
            input.Dispose();
        }
    }, ct);

    public Task PreviewAsync(
        MediaSource source, double duration, PreviewPlan plan, PreviewSpec spec, string workDir, string output, CancellationToken ct) => Run(() =>
    {
        var timeout = plan.Mode == PreviewMode.Chunks ? TimeSpan.FromSeconds(Math.Min(900, 60 * plan.SegmentCount)) : TimeSpan.FromSeconds(300);
        using var interrupt = new Interrupt(ct, timeout, "preview");
        // One decoder for the whole preview: libav picks the threads, frame threading included.
        var settings = new DecodeSettings(_options.DecodeThreads ?? 0, SliceThreads: false, FormatOptions: _options.FormatOptions);
        var encoder = _options.Encoder;
        try
        {
            Attempt(source, encoder);
        }
        catch (HardwareDecodeException ex) when (source.HardwareDecode)
        {
            _log.LogDebug("{Error}; decoding in software", ex.Message);
            Attempt(source.Software, encoder);
        }
        return true;

        void Attempt(MediaSource src, string enc)
        {
            try
            {
                PreviewEncoder.Encode(src, duration, plan, spec, enc, output, settings, interrupt, _log);
            }
            catch (EncodeException ex) when (enc != EncoderArgs.SoftwareEncoder)
            {
                // Like the command line: any hardware encoder failure gets a fresh libx264 encode.
                _log.LogDebug("hardware encode ({Encoder}) failed: {Error}; falling back to libx264", enc, ex.Message);
                Outputs.RemoveQuietly(output);
                PreviewEncoder.Encode(src, duration, plan, spec, EncoderArgs.SoftwareEncoder, output, settings, interrupt, _log);
            }
        }
    }, ct);
}
