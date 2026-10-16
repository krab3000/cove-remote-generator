using System.Globalization;

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
    /// <summary>Extra demuxer/protocol options for every source (HL_FFMPEG_INPUT_ARGS), as ffmpeg input options.</summary>
    public IReadOnlyList<string> FfmpegInputArgs { get; init; } = [];

    /// <summary>Slots per Intel GPU, as (GPU index, slots); null gives every detected GPU <see cref="DefaultGpuSlots"/>
    /// unless another --hv-*-slots is set.</summary>
    public IReadOnlyList<(int Gpu, int Slots)>? IntelSlots { get; init; }

    /// <summary>Slots per NVIDIA GPU, as <see cref="IntelSlots"/>.</summary>
    public IReadOnlyList<(int Gpu, int Slots)>? NvidiaSlots { get; init; }

    /// <summary>Slots for the Mac's media engine (VideoToolbox), as <see cref="IntelSlots"/> (its index is 0).</summary>
    public IReadOnlyList<(int Gpu, int Slots)>? AppleSlots { get; init; }

    /// <summary>How Intel GPUs decode and encode: <c>qsv</c> (Quick Sync) or <c>vaapi</c>.</summary>
    public string IntelApi { get; init; } = "qsv";

    /// <summary>Videos at once decoded and encoded in software.</summary>
    public int CpuSlots { get; init; } = 1;

    /// <summary>Slots each detected GPU gets when no --hv-*-slots flag is given.</summary>
    public const int DefaultGpuSlots = 2;

    /// <summary>Codecs decoded in software on every GPU, whatever it reports.</summary>
    public IReadOnlyList<string> HwSkipCodecs { get; init; } = [];

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

    /// <summary><c>bench</c>: the videos-at-once counts to try per slot pool.</summary>
    public IReadOnlyList<int> BenchCounts { get; init; } = [1, 2, 3, 4];

    /// <summary><c>bench</c>: the clips to run (1080p, 4k).</summary>
    public IReadOnlyList<string> BenchClips { get; init; } = ["1080p"];

    /// <summary>Malformed values found while reading; reported by <see cref="Validate"/>.</summary>
    public IReadOnlyList<string> ParseErrors { get; init; } = [];

    public string TasksDir => Path.Combine(DataDir, "tasks");

    /// <summary>Every setting's environment variable, with its <c>--help</c> text.</summary>
    public static readonly IReadOnlyList<(string Env, string Help)> Settings =
    [
        ("HL_LISTEN_URL", "Listen for Cove here (Cove connects to the worker), e.g. http://0.0.0.0:8750."),
        ("HL_COVE_URL", "Connect to Cove here (the worker connects to Cove), e.g. http://192.168.1.10:5073."),
        ("HL_WORKER_NAME", "Name shown in Cove. Default: the host name."),
        ("HL_WORKER_TOKEN", "Token Cove knows the worker by (16+ characters). Default: generated into the data directory."),
        ("HL_DATA_DIR", "Token, scratch and bench clips. Default: ./data."),
        ("HL_HV_INTEL_SLOTS", "gpu_idx,slots for an Intel GPU (repeat the flag, or 0,2;1,2): videos at once on it; 0 turns it off. Default: every detected GPU gets 2, unless an --hv-*-slots flag is given."),
        ("HL_HV_NVIDIA_SLOTS", "gpu_idx,slots for an NVIDIA GPU, as --hv-intel-slots."),
        ("HL_HV_APPLE_SLOTS", "0,slots for the Mac's media engine (VideoToolbox), as --hv-intel-slots."),
        ("HL_CPU_SLOTS", "Videos at once decoded and encoded in software. Default: 1."),
        ("HL_INTEL_API", "qsv (Quick Sync) or vaapi: how Intel GPUs decode and encode. Default: qsv."),
        ("HL_HWACCEL_SKIP_CODECS", "Comma-separated codecs decoded in software on every GPU, e.g. vc1,mpeg2video."),
        ("HL_SOURCE_CACHE_MB", "RAM for source bytes read over HTTP, shared by all tasks; 0 turns it off. Default: 1024."),
        ("HL_FFMPEG_INPUT_ARGS", "Extra demuxer/protocol options for every source, as ffmpeg input options separated by spaces."),
        ("HL_FFMPEG_LIBS", "Directory with the FFmpeg shared libraries. Default: the bundled ffmpeg folder."),
        ("HL_SPRITE_SEEK", "exact or keyframe. Default: exact."),
        ("HL_DECODE_THREADS", "Decoder threads per video (libav). Default: chosen per operation."),
        ("HL_BENCH_COUNTS", "bench: comma-separated videos-at-once counts to try per GPU and the CPU. Default: 1,2,3,4."),
        ("HL_BENCH_CLIPS", "bench: comma-separated clips, 1080p and/or 4k. Default: 1080p."),
    ];

    /// <summary>Settings a flag may be given for more than once; the values are joined with <c>;</c>.</summary>
    private static readonly HashSet<string> Repeatable = ["HL_HV_INTEL_SLOTS", "HL_HV_NVIDIA_SLOTS", "HL_HV_APPLE_SLOTS"];

    /// <summary>The command-line flag for a setting: HL_COVE_URL → --cove-url.</summary>
    public static string Flag(string env) => "--" + env["HL_".Length..].ToLowerInvariant().Replace('_', '-');

    /// <summary>The <c>--help</c> text.</summary>
    public static string Usage()
    {
        var width = Settings.Max(s => Flag(s.Env).Length + s.Env.Length) + 4;
        return "Usage: RemoteHeavylifter.Worker [bench] [--flag value | --flag=value] ...\n"
               + "  bench: measure each GPU and the CPU on Big Buck Bunny clips (downloaded once) and suggest slots.\n"
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

        void Set(string name, string value) =>
            flags[name] = Repeatable.Contains(name) && flags.TryGetValue(name, out var earlier) ? $"{earlier};{value}" : value;

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
                Set(name, arg[(eq + 1)..]);
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                Set(name, args[++i]);
            else
                errors.Add($"{flag} needs a value.");
        }
        return (FromEnvironment(name => flags.TryGetValue(name, out var value) ? value : env(name)), errors, help);
    }

    public static WorkerOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        string? Get(string name) => read(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        string[] List(string? value) => value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        var defaults = new WorkerOptions();
        var parseErrors = new List<string>();
        var counts = List(Get("HL_BENCH_COUNTS"))
            .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : -1).ToList();
        if (counts.Contains(-1))
            parseErrors.Add("HL_BENCH_COUNTS must be positive numbers, e.g. 1,2,3,4.");
        return new WorkerOptions
        {
            ListenUrl = Get("HL_LISTEN_URL"),
            CoveUrl = Get("HL_COVE_URL")?.TrimEnd('/'),
            Name = Get("HL_WORKER_NAME") ?? defaults.Name,
            Token = Get("HL_WORKER_TOKEN"),
            DataDir = Get("HL_DATA_DIR") ?? defaults.DataDir,
            FfmpegInputArgs = Get("HL_FFMPEG_INPUT_ARGS")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [],
            IntelSlots = ParseSlots(Get("HL_HV_INTEL_SLOTS"), "HL_HV_INTEL_SLOTS", parseErrors),
            NvidiaSlots = ParseSlots(Get("HL_HV_NVIDIA_SLOTS"), "HL_HV_NVIDIA_SLOTS", parseErrors),
            AppleSlots = ParseSlots(Get("HL_HV_APPLE_SLOTS"), "HL_HV_APPLE_SLOTS", parseErrors),
            CpuSlots = Get("HL_CPU_SLOTS") is { } cpu
                ? int.TryParse(cpu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpuSlots) && cpuSlots >= 0
                    ? cpuSlots
                    : Error(parseErrors, "HL_CPU_SLOTS must be 0 or more.", defaults.CpuSlots)
                : defaults.CpuSlots,
            HwSkipCodecs = List(Get("HL_HWACCEL_SKIP_CODECS")?.ToLowerInvariant()),
            IntelApi = Get("HL_INTEL_API")?.ToLowerInvariant() ?? defaults.IntelApi,
            FfmpegLibs = Get("HL_FFMPEG_LIBS"),
            SpriteSeek = Get("HL_SPRITE_SEEK")?.ToLowerInvariant() ?? defaults.SpriteSeek,
            DecodeThreads = int.TryParse(Get("HL_DECODE_THREADS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var threads) && threads > 0
                ? threads
                : null,
            SourceCacheMb = int.TryParse(Get("HL_SOURCE_CACHE_MB"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cache) && cache >= 0
                ? cache
                : defaults.SourceCacheMb,
            BenchCounts = counts.Count > 0 && !counts.Contains(-1) ? counts : defaults.BenchCounts,
            BenchClips = List(Get("HL_BENCH_CLIPS")?.ToLowerInvariant()) is { Length: > 0 } clips ? clips : defaults.BenchClips,
            ParseErrors = parseErrors,
        };
    }

    /// <summary>"gpu,slots;gpu,slots" (repeated flags arrive joined with ;); null when not set.</summary>
    internal static IReadOnlyList<(int Gpu, int Slots)>? ParseSlots(string? value, string name, List<string> errors)
    {
        if (value is null)
            return null;
        var result = new List<(int, int)>();
        foreach (var entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gpu) && gpu >= 0
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slots) && slots >= 0)
            {
                if (result.Any(r => r.Item1 == gpu))
                    errors.Add($"{name}: GPU {gpu} is given more than once.");
                result.Add((gpu, slots));
            }
            else
                errors.Add($"{name}: \"{entry}\" is not gpu_idx,slots (e.g. 0,2).");
        }
        return result;
    }

    private static T Error<T>(List<string> errors, string message, T fallback)
    {
        errors.Add(message);
        return fallback;
    }

    /// <summary>Configuration errors that stop the worker from starting; <paramref name="bench"/> needs no Cove.</summary>
    public IReadOnlyList<string> Validate(bool bench = false)
    {
        var errors = new List<string>(ParseErrors);
        if (!bench && ListenUrl is null && CoveUrl is null)
            errors.Add("Set HL_LISTEN_URL (Cove connects to the worker) and/or HL_COVE_URL (the worker connects to Cove).");
        if (ListenUrl is not null && !IsHttp(ListenUrl))
            errors.Add("HL_LISTEN_URL must be an http:// or https:// address, e.g. http://0.0.0.0:8750.");
        if (CoveUrl is not null && !IsHttp(CoveUrl))
            errors.Add("HL_COVE_URL must be Cove's http:// or https:// address, e.g. http://192.168.1.10:5073.");
        if (SpriteSeek is not ("exact" or "keyframe"))
            errors.Add("HL_SPRITE_SEEK must be exact or keyframe.");
        if (IntelApi is not ("qsv" or "vaapi"))
            errors.Add("HL_INTEL_API must be qsv or vaapi.");
        if (BenchClips.Any(c => c is not ("1080p" or "4k")))
            errors.Add("HL_BENCH_CLIPS must be 1080p and/or 4k.");
        if (Token is { Length: < Protocol.WorkerTokens.MinLength })
            errors.Add($"HL_WORKER_TOKEN must be at least {Protocol.WorkerTokens.MinLength} characters.");
        return errors;
    }

    private static bool IsHttp(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
