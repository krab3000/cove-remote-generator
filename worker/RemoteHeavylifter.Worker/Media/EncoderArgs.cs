using System.Globalization;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>H.264 encoder argument mapping, ported from Cove's FfmpegHwAccel (VideoEncodeArgs / InputArgsForEncoder).</summary>
public static class EncoderArgs
{
    public const string SoftwareEncoder = "libx264";

    public static readonly IReadOnlySet<string> KnownEncoders = new HashSet<string>
    {
        "libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi", "h264_videotoolbox",
    };

    /// <summary>The device of a GPU slot's encoder: <c>h264_nvenc@1</c> (a CUDA ordinal), <c>h264_vaapi@/dev/dri/renderD129</c>.</summary>
    public static string WithDevice(string encoder, string? device) => device is null ? encoder : $"{encoder}@{device}";

    /// <summary>An encoder as <see cref="WithDevice"/> writes it: its name and device (null: the default).</summary>
    public static (string Name, string? Device) Split(string encoder)
    {
        var at = encoder.IndexOf('@');
        return at < 0 ? (encoder, null) : (encoder[..at], encoder[(at + 1)..]);
    }

    /// <summary>The render node VA-API encodes on when the encoder names none.</summary>
    public const string DefaultVaapiDevice = "/dev/dri/renderD128";

    public static IReadOnlyList<string> VideoEncodeArgs(string encoder, int quality, string softwarePreset)
    {
        var q = quality.ToString(CultureInfo.InvariantCulture);
        var (name, device) = Split(encoder);
        return name switch
        {
            "h264_nvenc" when device is not null => ["-c:v", "h264_nvenc", "-gpu", device, "-rc", "vbr", "-cq", q, "-b:v", "0"],
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", q, "-b:v", "0"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-global_quality", q],
            "h264_amf" => ["-c:v", "h264_amf", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q],
            "h264_vaapi" => ["-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", q],
            "h264_videotoolbox" => ["-c:v", "h264_videotoolbox", "-q:v",
                Math.Clamp(65 - quality, 1, 100).ToString(CultureInfo.InvariantCulture)],
            _ => ["-c:v", "libx264", "-preset", softwarePreset, "-crf", q, "-pix_fmt", "yuv420p"],
        };
    }

    public static string UploadChain(string encoder) =>
        Split(encoder).Name == "h264_vaapi" ? ",format=nv12,hwupload" : ",format=yuv420p";
}
