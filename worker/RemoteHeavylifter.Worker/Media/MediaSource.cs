using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>A video ffmpeg reads directly (normally an HTTP URL served by Cove).</summary>
/// <param name="InputOptions">Per-input ffmpeg options placed immediately before every <c>-i</c> of this source,
/// because ffmpeg applies input options only to the next input.</param>
/// <param name="Size">Byte size as reported by Cove; only shapes decode timeouts (&lt;= 0 means the minimum).</param>
public sealed record MediaSource(string Url, IReadOnlyList<string> InputOptions, long Size)
{
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

    /// <summary><c>[..InputOptions, "-i", Url]</c>, the only way an input is ever added to a command line.</summary>
    internal IEnumerable<string> Input()
    {
        foreach (var option in InputOptions)
            yield return option;
        yield return "-i";
        yield return Url;
    }
}
