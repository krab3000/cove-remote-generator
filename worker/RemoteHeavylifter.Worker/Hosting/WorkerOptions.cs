using System.Globalization;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Hosting;

/// <summary>Worker settings, read from <c>HL_*</c> environment variables.</summary>
public sealed record WorkerOptions
{
    /// <summary>Listen for Cove here (Cove dials the worker), e.g. http://0.0.0.0:8750.</summary>
    public string? ListenUrl { get; init; }

    /// <summary>Dial Cove here (the worker dials Cove), e.g. http://192.168.1.10:5073.</summary>
    public string? CoveUrl { get; init; }

    public string Name { get; init; } = Environment.MachineName;

    /// <summary>A fixed token; otherwise one is generated and kept in <see cref="DataDir"/>.</summary>
    public string? Token { get; init; }

    public string DataDir { get; init; } = "./data";
    public int MaxConcurrency { get; init; } = Math.Max(1, Environment.ProcessorCount / 4);
    public string Ffmpeg { get; init; } = Bundled("ffmpeg");
    public string Ffprobe { get; init; } = Bundled("ffprobe");
    public IReadOnlyList<string> FfmpegInputArgs { get; init; } = [];
    public string Encoder { get; init; } = EncoderArgs.SoftwareEncoder;

    /// <summary>ffmpeg hwaccel for decoding sources, e.g. <c>cuda</c>; null decodes in software.</summary>
    public string? HwAccel { get; init; }

    /// <summary>Hardware decoding devices (e.g. GPU indexes 0 and 1); tasks take them in turn. Empty uses ffmpeg's default.</summary>
    public IReadOnlyList<string> HwAccelDevices { get; init; } = [];

    /// <summary>How video is read and encoded: <c>auto</c> (libav in-process when its libraries load, else the command
    /// line), <c>libav</c> (required) or <c>cli</c> (ffmpeg/ffprobe processes).</summary>
    public string MediaEngine { get; init; } = "auto";

    /// <summary>Directory with the FFmpeg shared libraries; default: the bundled <c>ffmpeg</c> folder.</summary>
    public string? FfmpegLibs { get; init; }

    /// <summary>Decoder threads per video for the libav engine; null chooses per operation.</summary>
    public int? DecodeThreads { get; init; }

    /// <summary>How sprite frames are found: <c>exact</c> (the frame at each timestamp, as Cove does) or <c>keyframe</c>
    /// (the keyframe at or before it: only keyframes are decoded, many times faster, up to a GOP earlier).</summary>
    public string SpriteSeek { get; init; } = "exact";

    public bool SpriteKeyframes => SpriteSeek == "keyframe";

    /// <summary>RAM for source bytes shared by all running tasks; 0 makes ffmpeg read straight from Cove.</summary>
    public int SourceCacheMb { get; init; } = 1024;

    public string TasksDir => Path.Combine(DataDir, "tasks");

    public MediaContext Media => new(Ffmpeg, Ffprobe, FfmpegInputArgs, Encoder);

    public static WorkerOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        string? Get(string name) => read(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var defaults = new WorkerOptions();
        return new WorkerOptions
        {
            ListenUrl = Get("HL_LISTEN_URL"),
            CoveUrl = Get("HL_COVE_URL")?.TrimEnd('/'),
            Name = Get("HL_WORKER_NAME") ?? defaults.Name,
            Token = Get("HL_WORKER_TOKEN"),
            DataDir = Get("HL_DATA_DIR") ?? defaults.DataDir,
            MaxConcurrency = int.TryParse(Get("HL_MAX_CONCURRENCY"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) && max > 0
                ? max
                : defaults.MaxConcurrency,
            Ffmpeg = Get("HL_FFMPEG") ?? defaults.Ffmpeg,
            Ffprobe = Get("HL_FFPROBE") ?? defaults.Ffprobe,
            FfmpegInputArgs = Get("HL_FFMPEG_INPUT_ARGS")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [],
            Encoder = Get("HL_H264_ENCODER") ?? defaults.Encoder,
            HwAccel = Get("HL_HWACCEL") is { } accel && !accel.Equals("none", StringComparison.OrdinalIgnoreCase) ? accel : null,
            HwAccelDevices = Get("HL_HWACCEL_DEVICES")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            MediaEngine = Get("HL_MEDIA_ENGINE")?.ToLowerInvariant() ?? defaults.MediaEngine,
            FfmpegLibs = Get("HL_FFMPEG_LIBS"),
            SpriteSeek = Get("HL_SPRITE_SEEK")?.ToLowerInvariant() ?? defaults.SpriteSeek,
            DecodeThreads = int.TryParse(Get("HL_DECODE_THREADS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) && threads > 0
                ? threads
                : null,
            SourceCacheMb = int.TryParse(Get("HL_SOURCE_CACHE_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cache) && cache >= 0
                ? cache
                : defaults.SourceCacheMb,
        };
    }

    /// <summary>Configuration errors that stop the worker from starting.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ListenUrl is null && CoveUrl is null)
            errors.Add("Set HL_LISTEN_URL (Cove connects to the worker) and/or HL_COVE_URL (the worker connects to Cove).");
        if (ListenUrl is not null && !IsHttp(ListenUrl))
            errors.Add("HL_LISTEN_URL must be an http:// or https:// address, e.g. http://0.0.0.0:8750.");
        if (CoveUrl is not null && !IsHttp(CoveUrl))
            errors.Add("HL_COVE_URL must be Cove's http:// or https:// address, e.g. http://192.168.1.10:5073.");
        if (SpriteSeek is not ("exact" or "keyframe"))
            errors.Add("HL_SPRITE_SEEK must be exact or keyframe.");
        if (MediaEngine is not ("auto" or "libav" or "cli"))
            errors.Add("HL_MEDIA_ENGINE must be auto, libav or cli.");
        if (HwAccelDevices.Count > 0 && HwAccel is null)
            errors.Add("HL_HWACCEL_DEVICES needs HL_HWACCEL, e.g. HL_HWACCEL=cuda.");
        if (Token is { Length: < Protocol.WorkerTokens.MinLength })
            errors.Add($"HL_WORKER_TOKEN must be at least {Protocol.WorkerTokens.MinLength} characters.");
        return errors;
    }

    /// <summary>The ffmpeg/ffprobe bundled next to the worker (<c>ffmpeg/</c> on Windows, <c>ffmpeg/bin/</c> on Linux),
    /// else the one on PATH.</summary>
    private static string Bundled(string tool)
    {
        var name = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        var bundled = new[] { Path.Combine(AppContext.BaseDirectory, "ffmpeg", name), Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", name) };
        return bundled.FirstOrDefault(File.Exists) ?? tool;
    }

    private static bool IsHttp(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
