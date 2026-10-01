using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>The libav libraries the worker bound to.</summary>
/// <param name="Version">FFmpeg's own version string, e.g. "n9.0.2-17-g2a571b6068-20260930".</param>
public sealed record LibavInfo(string Directory, string Version);

/// <summary>
/// Finds and binds the FFmpeg shared libraries (avcodec-63.dll, libavcodec.so.63 …) that match the FFmpeg.AutoGen
/// bindings. Binding is process-wide and happens once: the first directory that holds every library is used, and a
/// version mismatch there is a failure rather than a reason to try the next one.
/// </summary>
public static class LibavLoader
{
    /// <summary>Libraries the worker uses, in dependency order.</summary>
    private static readonly string[] Libraries = ["avutil", "swresample", "swscale", "avcodec", "avformat", "avfilter"];

    private static readonly object Gate = new();
    private static bool _attempted;
    private static LibavInfo? _info;
    private static string _failure = "";

    /// <summary>Where to look: <paramref name="configured"/> (HL_FFMPEG_LIBS), the bundled <c>ffmpeg</c> folder next to
    /// the worker (flat on Windows, <c>ffmpeg/lib</c> on Linux; scripts/fetch-ffmpeg.sh), then /opt/ffmpeg/lib (Docker).</summary>
    public static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "lib");
        yield return "/opt/ffmpeg/lib";
    }

    /// <summary>The file name of one library on this platform, e.g. avcodec-63.dll.</summary>
    public static string FileName(string library)
    {
        var major = DynamicallyLoadedBindings.LibraryVersionMap[library];
        if (OperatingSystem.IsWindows())
            return $"{library}-{major}.dll";
        return OperatingSystem.IsMacOS() ? $"lib{library}.{major}.dylib" : $"lib{library}.so.{major}";
    }

    /// <summary>Whether the loaded libraries know the hwaccel (cuda, d3d11va, vaapi …). Call after a successful load.</summary>
    public static bool SupportsHwAccel(string name) => ffmpeg.av_hwdevice_find_type_by_name(name) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

    /// <summary>Whether the loaded libraries have the encoder (h264_nvenc, libx264 …). Call after a successful load.</summary>
    public static unsafe bool HasEncoder(string name) => ffmpeg.avcodec_find_encoder_by_name(name) is not null;

    public static bool TryLoad(string? configured, out LibavInfo? info, out string failure)
    {
        lock (Gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                Load(configured);
            }
            info = _info;
            failure = _failure;
            return _info is not null;
        }
    }

    private static void Load(string? configured)
    {
        var directory = Candidates(configured).FirstOrDefault(dir => Libraries.All(lib => File.Exists(Path.Combine(dir, FileName(lib)))));
        if (directory is null)
        {
            _failure = $"no FFmpeg {DynamicallyLoadedBindings.LibraryVersionMap["avcodec"]}.x shared libraries ({FileName("avcodec")} …) in "
                       + string.Join(", ", Candidates(configured).Distinct());
            return;
        }

        try
        {
            DynamicallyLoadedBindings.LibrariesPath = Path.GetFullPath(directory);
            DynamicallyLoadedBindings.Initialize();
            var mismatch = new (string Name, uint Version, int Expected)[]
                {
                    ("avutil", ffmpeg.avutil_version(), ffmpeg.LIBAVUTIL_VERSION_MAJOR),
                    ("swresample", ffmpeg.swresample_version(), ffmpeg.LIBSWRESAMPLE_VERSION_MAJOR),
                    ("swscale", ffmpeg.swscale_version(), ffmpeg.LIBSWSCALE_VERSION_MAJOR),
                    ("avcodec", ffmpeg.avcodec_version(), ffmpeg.LIBAVCODEC_VERSION_MAJOR),
                    ("avformat", ffmpeg.avformat_version(), ffmpeg.LIBAVFORMAT_VERSION_MAJOR),
                    ("avfilter", ffmpeg.avfilter_version(), ffmpeg.LIBAVFILTER_VERSION_MAJOR),
                }
                .Where(l => (int)(l.Version >> 16) != l.Expected)
                .Select(l => $"{l.Name} {l.Version >> 16} (need {l.Expected})")
                .ToList();
            if (mismatch.Count > 0)
            {
                _failure = $"libraries in {directory} do not match the bindings: {string.Join(", ", mismatch)}";
                return;
            }
            unsafe
            {
                if (ffmpeg.avfilter_get_by_name("scale") is null || ffmpeg.avcodec_find_encoder_by_name("libx264") is null)
                {
                    _failure = $"libraries in {directory} lack the scale filter or the libx264 encoder";
                    return;
                }
            }
            _info = new LibavInfo(Path.GetFullPath(directory), ffmpeg.av_version_info());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException
                                       or BadImageFormatException or InvalidOperationException)
        {
            _failure = $"cannot load the libraries in {directory}: {ex.Message}";
        }
    }
}
