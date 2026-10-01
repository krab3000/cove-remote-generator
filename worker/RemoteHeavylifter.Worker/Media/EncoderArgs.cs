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

    public static IReadOnlyList<string> VideoEncodeArgs(string encoder, int quality, string softwarePreset)
    {
        var q = quality.ToString(CultureInfo.InvariantCulture);
        return encoder switch
        {
            "h264_nvenc" => ["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", q, "-b:v", "0"],
            "h264_qsv" => ["-c:v", "h264_qsv", "-global_quality", q],
            "h264_amf" => ["-c:v", "h264_amf", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q],
            "h264_vaapi" => ["-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", q],
            "h264_videotoolbox" => ["-c:v", "h264_videotoolbox", "-q:v",
                Math.Clamp(65 - quality, 1, 100).ToString(CultureInfo.InvariantCulture)],
            _ => ["-c:v", "libx264", "-preset", softwarePreset, "-crf", q, "-pix_fmt", "yuv420p"],
        };
    }

    public static IReadOnlyList<string> InputArgsForEncoder(string encoder) =>
        encoder.EndsWith("_vaapi", StringComparison.Ordinal) ? ["-vaapi_device", "/dev/dri/renderD128"] : [];

    public static string UploadChain(string encoder) =>
        encoder == "h264_vaapi" ? ",format=nv12,hwupload" : ",format=yuv420p";
}
