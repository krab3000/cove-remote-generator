namespace RemoteHeavylifter.Generation;

/// <summary>Video codec names as libav (and so a worker's GPU decoder list) uses them.</summary>
public static class CodecNames
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avc"] = "h264", ["avc1"] = "h264", ["x264"] = "h264", ["h.264"] = "h264",
        ["h265"] = "hevc", ["h.265"] = "hevc", ["hev1"] = "hevc", ["hvc1"] = "hevc", ["x265"] = "hevc",
        ["mpeg2"] = "mpeg2video", ["mpeg-2"] = "mpeg2video", ["mpeg1"] = "mpeg1video", ["mpeg-1"] = "mpeg1video",
        ["xvid"] = "mpeg4", ["divx"] = "mpeg4", ["mp4v"] = "mpeg4",
        ["wvc1"] = "vc1", ["vc-1"] = "vc1", ["wmv9"] = "wmv3",
        ["av01"] = "av1", ["vp09"] = "vp9", ["vp08"] = "vp8",
    };

    /// <summary>Cove's stored codec (ffprobe's codec_name, or a fourcc or common alias) as a lower-case libav name;
    /// null when unknown.</summary>
    public static string? Normalize(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return null;
        var trimmed = codec.Trim();
        return Aliases.TryGetValue(trimmed, out var name) ? name : trimmed.ToLowerInvariant();
    }
}
