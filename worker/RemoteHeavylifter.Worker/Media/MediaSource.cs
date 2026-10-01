using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>A video ffmpeg reads directly (normally an HTTP URL served by Cove).</summary>
/// <param name="InputOptions">Per-input ffmpeg options placed immediately before every <c>-i</c> of this source,
/// because ffmpeg applies input options only to the next input.</param>
/// <param name="Size">Byte size as reported by Cove; only shapes decode timeouts (&lt;= 0 means the minimum).</param>
/// <param name="DecodeOptions">Hardware decoding options (<c>-hwaccel cuda</c> …), placed after <paramref name="InputOptions"/>
/// before every <c>-i</c> of an ffmpeg command; never given to ffprobe. Null decodes in software.</param>
public sealed record MediaSource(string Url, IReadOnlyList<string> InputOptions, long Size, IReadOnlyList<string>? DecodeOptions = null)
{
    public bool HardwareDecode => DecodeOptions is { Count: > 0 };

    /// <summary>The same source decoded in software: the fallback when hardware decoding fails.</summary>
    public MediaSource Software => HardwareDecode ? this with { DecodeOptions = null } : this;

    /// <summary><c>-hwaccel {accel} [-hwaccel_device {device}]</c>; frames come back to system memory, so the CPU
    /// filters (scale, v360) and encoders work unchanged.</summary>
    public MediaSource WithHardwareDecode(string accel, string? device) =>
        this with { DecodeOptions = device is null ? ["-hwaccel", accel] : ["-hwaccel", accel, "-hwaccel_device", device] };

    /// <summary>A Cove-served source: the worker token on every request, and reconnects so a dropped
    /// connection mid-encode resumes with a range request instead of failing the artifact.</summary>
    public static MediaSource ForCove(string url, string token, long size) => new(url,
    [
        "-headers", $"{ProtocolInfo.TokenHeader}: {token}\r\n",
        "-reconnect", "1",
        "-reconnect_on_network_error", "1",
        "-reconnect_delay_max", "5",
        "-rw_timeout", "30000000",
    ], size);

    /// <summary>The worker's own loopback copy of a Cove source (<see cref="Transport.SourceCache"/>): no token, same
    /// reconnects, since a failed upstream fetch drops the connection and ffmpeg resumes with a range request.</summary>
    public static MediaSource ForLocal(string url, long size) => new(url,
    [
        "-reconnect", "1",
        "-reconnect_on_network_error", "1",
        "-reconnect_delay_max", "5",
        "-rw_timeout", "30000000",
    ], size);

    /// <summary><see cref="InputOptions"/> as libav dictionary entries: <c>-headers "X: y"</c> → (<c>headers</c>, <c>X: y</c>).</summary>
    public IEnumerable<KeyValuePair<string, string>> ProtocolOptions()
    {
        for (var i = 0; i + 1 < InputOptions.Count; i += 2)
            yield return new(InputOptions[i].TrimStart('-'), InputOptions[i + 1]);
    }

    /// <summary>The hwaccel and device <see cref="DecodeOptions"/> ask for; null when decoding in software.</summary>
    public (string Accel, string? Device)? HwDecode
    {
        get
        {
            if (DecodeOptions is not { Count: > 0 } options)
                return null;
            string? accel = null, device = null;
            for (var i = 0; i + 1 < options.Count; i += 2)
            {
                if (options[i] == "-hwaccel")
                    accel = options[i + 1];
                else if (options[i] == "-hwaccel_device")
                    device = options[i + 1];
            }
            return accel is null ? null : (accel, device);
        }
    }

    /// <summary><c>[..InputOptions, ..DecodeOptions, "-i", Url]</c>, the only way an input is ever added to an ffmpeg
    /// command line.</summary>
    internal IEnumerable<string> Input()
    {
        foreach (var option in InputOptions)
            yield return option;
        foreach (var option in DecodeOptions ?? [])
            yield return option;
        yield return "-i";
        yield return Url;
    }
}
