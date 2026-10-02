using System.Globalization;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Hosting;

/// <summary>Worker settings, read from command-line flags and <c>HL_*</c> environment variables (a flag wins): every
/// variable has a flag named after it, e.g. <c>HL_COVE_URL</c> is <c>--cove-url</c>.</summary>
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

    /// <summary>Codecs decoded in software even with <see cref="HwAccel"/>; null means <see cref="DefaultHwSkipCodecs"/>.</summary>
    public IReadOnlyList<string>? HwAccelSkipCodecs { get; init; }

    /// <summary>The codecs actually decoded in software: <see cref="HwAccelSkipCodecs"/>, or the hwaccel's known-broken ones.</summary>
    public IReadOnlyList<string> HwSkipCodecs => HwAccelSkipCodecs ?? DefaultHwSkipCodecs(HwAccel);

    /// <summary>Decoders libav has that crash the process: FFmpeg's vc1_qsv segfaults (FFmpeg 9.0, Intel media
    /// runtime 23.2 and 26.3).</summary>
    public static IReadOnlyList<string> DefaultHwSkipCodecs(string? hwAccel) => hwAccel == "qsv" ? ["vc1"] : [];

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

    /// <summary>Every setting's environment variable, with its <c>--help</c> text.</summary>
    public static readonly IReadOnlyList<(string Env, string Help)> Settings =
    [
        ("HL_LISTEN_URL", "Listen for Cove here (Cove connects to the worker), e.g. http://0.0.0.0:8750."),
        ("HL_COVE_URL", "Connect to Cove here (the worker connects to Cove), e.g. http://192.168.1.10:5073."),
        ("HL_WORKER_NAME", "Name shown in Cove. Default: the host name."),
        ("HL_WORKER_TOKEN", "Token Cove knows the worker by (16+ characters). Default: generated into the data directory."),
        ("HL_DATA_DIR", "Token and scratch files. Default: ./data."),
        ("HL_MAX_CONCURRENCY", "Videos at once. Default: CPU cores / 4, at least 1."),
        ("HL_SOURCE_CACHE_MB", "RAM for source bytes read over HTTP, shared by all tasks; 0 turns it off. Default: 1024."),
        ("HL_H264_ENCODER", "Preview encoder, e.g. h264_nvenc, h264_qsv. Default: libx264."),
        ("HL_HWACCEL", "Decode on the GPU with this ffmpeg hwaccel, e.g. cuda, qsv, vaapi; none decodes in software."),
        ("HL_HWACCEL_DEVICES", "Comma-separated hwaccel devices that tasks take in turn, e.g. 0,1."),
        ("HL_HWACCEL_SKIP_CODECS", "Comma-separated codecs decoded in software even with a hwaccel, e.g. vc1,mpeg2video; none skips nothing. Default: vc1 for qsv, else none."),
        ("HL_FFMPEG_INPUT_ARGS", "Extra ffmpeg input options, separated by spaces."),
        ("HL_MEDIA_ENGINE", "auto, libav or cli. Default: auto."),
        ("HL_FFMPEG_LIBS", "Directory with the FFmpeg shared libraries. Default: the bundled ffmpeg folder."),
        ("HL_SPRITE_SEEK", "exact or keyframe. Default: exact."),
        ("HL_DECODE_THREADS", "Decoder threads per video (libav). Default: chosen per operation."),
        ("HL_FFMPEG", "ffmpeg executable. Default: the bundled one, else ffmpeg on PATH."),
        ("HL_FFPROBE", "ffprobe executable. Default: the bundled one, else ffprobe on PATH."),
    ];

    /// <summary>The command-line flag for a setting: HL_COVE_URL → --cove-url.</summary>
    public static string Flag(string env) => "--" + env["HL_".Length..].ToLowerInvariant().Replace('_', '-');

    /// <summary>The <c>--help</c> text.</summary>
    public static string Usage()
    {
        var width = Settings.Max(s => Flag(s.Env).Length + s.Env.Length) + 4;
        return "Usage: RemoteHeavylifter.Worker [--flag value | --flag=value] ...\n"
               + "Each flag can be set as its environment variable instead; a flag wins.\n\n"
               + string.Concat(Settings.Select(s => $"  {(Flag(s.Env) + "  " + s.Env).PadRight(width)}{s.Help}\n"));
    }

    /// <summary>Settings from <paramref name="args"/> (<c>--cove-url http://…</c> or <c>--cove-url=http://…</c>), then
    /// from the environment. <c>Errors</c> lists unknown flags and flags without a value; <c>Help</c> is set by
    /// <c>--help</c> or <c>-h</c>.</summary>
    public static (WorkerOptions Options, IReadOnlyList<string> Errors, bool Help) Load(IReadOnlyList<string> args, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var byFlag = Settings.ToDictionary(s => Flag(s.Env), s => s.Env);
        var flags = new Dictionary<string, string>();
        var errors = new List<string>();
        var help = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--help" or "-h")
            {
                help = true;
                continue;
            }
            var eq = arg.IndexOf('=');
            var flag = eq < 0 ? arg : arg[..eq];
            if (!byFlag.TryGetValue(flag, out var name))
                errors.Add($"Unknown option {arg}; see --help.");
            else if (eq >= 0)
                flags[name] = arg[(eq + 1)..];
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                flags[name] = args[++i];
            else
                errors.Add($"{flag} needs a value.");
        }
        return (FromEnvironment(name => flags.TryGetValue(name, out var value) ? value : env(name)), errors, help);
    }

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
            HwAccelSkipCodecs = Get("HL_HWACCEL_SKIP_CODECS") is { } skip
                ? skip.Equals("none", StringComparison.OrdinalIgnoreCase)
                    ? []
                    : skip.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null,
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
