using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hardware;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tasks;

/// <summary>Builds each slot pool's engine: decoding on its GPU (the codecs it reports) and encoding previews there.</summary>
public static class SlotBuilder
{
    /// <summary>The GPUs as the worker uses them: without the codecs --hwaccel-skip-codecs names.</summary>
    public static IReadOnlyList<GpuDevice> Detect(WorkerOptions options, ILogger log)
    {
        var gpus = GpuDetection.ForPlatform().Detect(log);
        return options.HwSkipCodecs.Count == 0
            ? gpus
            : gpus.Select(g => g with { Decoders = g.Decoders.Where(d => !options.HwSkipCodecs.Contains(d.Codec)).ToList() }).ToList();
    }

    /// <summary>Codecs Quick Sync is never asked to decode: FFmpeg's vc1_qsv segfaults (FFmpeg 9.0, Intel media
    /// runtime 23.2 and 26.3), taking the worker down.</summary>
    internal static readonly IReadOnlySet<string> BrokenQsvDecoders = new HashSet<string> { "vc1" };

    /// <summary>
    /// An Intel GPU as <paramref name="api"/> runs it. Detection describes it through VA-API; Quick Sync decodes through
    /// its own decoders (h264_qsv …) on the same render node, so it keeps the codecs libav has a Quick Sync decoder for,
    /// less the broken ones, and encodes with h264_qsv.
    /// </summary>
    internal static GpuDevice ForIntelApi(GpuDevice gpu, string api, Func<string, bool> hasQsvDecoder) =>
        gpu.Vendor != GpuVendors.Intel || api != "qsv"
            ? gpu
            : gpu with
            {
                HwAccel = "qsv",
                Encoder = "h264_qsv",
                Decoders = gpu.Decoders
                    .Where(d => !BrokenQsvDecoders.Contains(d.Codec) && hasQsvDecoder((d.Codec == "mpeg2video" ? "mpeg2" : d.Codec) + "_qsv"))
                    .ToList(),
            };

    /// <param name="libav">The loaded libav libraries.</param>
    /// <param name="warnings">Where a GPU feature ffmpeg lacks is reported (that slot pool then decodes or encodes in software).</param>
    public static SlotPool Build(
        IReadOnlyList<SlotPlan> plans, WorkerOptions options, LibavInfo libav, ILoggerFactory loggers, List<string> warnings)
    {
        var mediaLog = loggers.CreateLogger("Media");
        var formatOptions = LibavMediaEngine.FormatOptionsFrom(options.FfmpegInputArgs,
                ignored => mediaLog.LogWarning("HL_FFMPEG_INPUT_ARGS: -{Option} is ignored by the libav engine; GPU slots decode on their GPU", ignored));
        var concurrency = Math.Max(1, plans.Sum(p => p.Capacity));
        var slots = new List<Slot>();
        foreach (var plan in plans)
        {
            var gpu = plan.Gpu is { } detected ? ForIntelApi(detected, options.IntelApi, LibavLoader.HasDecoder) : null;
            if (gpu is not null && !LibavLoader.SupportsHwAccel(gpu.HwAccel))
            {
                warnings.Add($"{plan.Id}: ffmpeg has no {gpu.HwAccel} hwaccel; decoding in software");
                gpu = gpu with { Decoders = [] };
            }
            var encoder = gpu?.Encoder ?? EncoderArgs.SoftwareEncoder;
            if (encoder != EncoderArgs.SoftwareEncoder && !LibavLoader.HasEncoder(encoder))
            {
                warnings.Add($"{plan.Id}: ffmpeg has no {encoder} encoder; encoding previews with {EncoderArgs.SoftwareEncoder}");
                encoder = EncoderArgs.SoftwareEncoder;
            }
            var withDevice = encoder == EncoderArgs.SoftwareEncoder ? encoder : EncoderArgs.WithDevice(encoder, gpu!.EncoderDevice);
            var engine = new LibavMediaEngine(libav, new LibavEngineOptions(options.DecodeThreads, concurrency, withDevice, formatOptions,
                options.HwSkipCodecs, gpu?.Decoders.Select(d => d.Codec).ToHashSet(StringComparer.OrdinalIgnoreCase)), mediaLog);
            slots.Add(new Slot(plan with { Gpu = gpu }, engine, withDevice));
        }
        return new SlotPool(slots);
    }

    /// <summary>One line per slot pool for the startup log.</summary>
    public static string Describe(Slot slot)
    {
        var gpu = slot.Plan.Gpu;
        var where = gpu is null ? "CPU" : $"{gpu.Name}, {gpu.HwAccel}{(gpu.DecodeDevice is null ? "" : " " + gpu.DecodeDevice)}";
        var engines = gpu?.VideoEngines is { } n ? $", {n} video engine{(n == 1 ? "" : "s")}" : "";
        var decodes = gpu is null ? "decodes in software"
            : gpu.Decoders.Count == 0 ? "decodes in software (no GPU decoders)"
            : "decodes " + string.Join(" ", gpu.Decoders.Select(d => d.Codec));
        return $"{slot.Id} ({where}{engines}): {slot.Plan.Capacity} slot{(slot.Plan.Capacity == 1 ? "" : "s")}, {slot.Encoder}, {decodes}";
    }
}
