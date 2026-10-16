namespace RemoteHeavylifter.Worker.Media;

/// <param name="InputArgs">Global ffmpeg args (HL_FFMPEG_INPUT_ARGS) prepended to cover and preview commands.</param>
/// <param name="Encoder">H.264 encoder for previews; anything but libx264 falls back to libx264 on failure.</param>
public sealed record MediaContext(string Ffmpeg, string Ffprobe, IReadOnlyList<string> InputArgs, string Encoder)
{
    public static MediaContext Default { get; } = new("ffmpeg", "ffprobe", [], EncoderArgs.SoftwareEncoder);
}
