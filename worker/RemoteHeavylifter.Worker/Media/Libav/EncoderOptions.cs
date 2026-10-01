using FFmpeg.AutoGen.Abstractions;

namespace RemoteHeavylifter.Worker.Media.Libav;

/// <summary>An H.264 encoder as libav takes it.</summary>
/// <param name="Codec">Encoder name (libx264, h264_nvenc …).</param>
/// <param name="Options">Encoder options for avcodec_open2 (preset, crf, rc, cq, profile, level …).</param>
/// <param name="PixelFormat">The command line's <c>-pix_fmt</c>, when it sets one.</param>
/// <param name="QScale">The command line's <c>-q:v</c> (a fixed quantiser), when it sets one.</param>
public sealed record EncoderSetup(string Codec, IReadOnlyList<KeyValuePair<string, string>> Options, string? PixelFormat, int? QScale);

/// <summary>
/// Turns the preview's command line encoder arguments (<see cref="EncoderArgs.VideoEncodeArgs"/> plus the profile and
/// level) into libav settings, so both engines encode with the same settings by construction.
/// </summary>
public static class EncoderOptions
{
    public static EncoderSetup For(string encoder, int crf, string? preset)
    {
        var args = EncoderArgs.VideoEncodeArgs(encoder, crf, Timing.NormalizePreset(preset)).Concat(PreviewGenerator.ProfileArgs).ToList();
        var codec = encoder;
        string? pixelFormat = null;
        int? qscale = null;
        var options = new List<KeyValuePair<string, string>>();
        for (var i = 0; i + 1 < args.Count; i += 2)
        {
            var (key, value) = (args[i], args[i + 1]);
            switch (key)
            {
                case "-c:v":
                    codec = value;
                    break;
                case "-pix_fmt":
                    pixelFormat = value;
                    break;
                case "-q:v":
                    qscale = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    // -b:v → b, -profile:v → profile; the rest (-preset, -crf, -rc, -cq, -level …) by name.
                    var name = key.TrimStart('-');
                    var colon = name.IndexOf(':');
                    options.Add(new(colon < 0 ? name : name[..colon], value));
                    break;
            }
        }
        return new EncoderSetup(codec, options, pixelFormat, qscale);
    }
}

/// <summary>
/// Constant frame rate output like fftools' for MP4: each frame takes the output slot nearest its time; a frame landing
/// on a slot already filled is dropped, one skipping slots is repeated to fill them. Segments are laid end to end, each
/// starting at its first frame (the command line's <c>setpts=PTS-STARTPTS</c> before concatenating).
/// </summary>
public sealed class CfrClock(AVRational rate)
{
    private readonly double _fps = rate.num / (double)rate.den;

    private long _segmentOffset;
    private double? _segmentStart;

    /// <summary>The output frame rate.</summary>
    public AVRational Rate => rate;

    public double Fps => _fps;

    /// <summary>The next free output slot = frames written so far.</summary>
    public long Next { get; private set; }

    /// <summary>Frames written since <see cref="StartSegment"/>.</summary>
    public long SegmentFrames => Next - _segmentOffset;

    public void StartSegment()
    {
        _segmentOffset = Next;
        _segmentStart = null;
    }

    /// <summary>How many times to write the frame at <paramref name="seconds"/> (0 = drop), starting at slot <see cref="Next"/>
    /// before the call.</summary>
    public int Place(double seconds)
    {
        _segmentStart ??= seconds;
        // lrint: to nearest, ties to even.
        var slot = _segmentOffset + (long)Math.Round((seconds - _segmentStart.Value) * _fps, MidpointRounding.ToEven);
        if (slot < Next)
            return 0;
        var count = (int)(slot - Next + 1);
        Next = slot + 1;
        return count;
    }
}
