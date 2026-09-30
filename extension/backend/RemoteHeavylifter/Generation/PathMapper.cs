using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Generation;

/// <summary>Translates a Cove file path into the path a generation server sees for the same file.</summary>
public static class PathMapper
{
    /// <summary>
    /// Map <paramref name="covePath"/> through the longest matching Cove prefix. Prefixes match whole
    /// path segments only ("/media" never matches "/media2") and case-insensitively, like Cove's own
    /// generate folder filter; the rest of the path keeps its case. Null when no mapping applies.
    /// </summary>
    public static string? Map(string covePath, IReadOnlyList<PathMapping> mappings)
    {
        var path = Normalize(covePath);
        PathMapping? best = null;
        var bestLength = -1;
        string remainder = "";

        foreach (var mapping in mappings)
        {
            var prefix = Normalize(mapping.CovePrefix);
            if (prefix.Length <= bestLength)
                continue;
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            {
                best = mapping;
                bestLength = prefix.Length;
                remainder = "";
            }
            else if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) || (prefix == "" && path.StartsWith('/')))
            {
                best = mapping;
                bestLength = prefix.Length;
                remainder = path[prefix.Length..];
            }
        }

        if (best is null)
            return null;

        var remote = best.RemotePrefix.Trim();
        var windowsStyle = remote.Contains('\\') && !remote.Contains('/');
        remote = remote.TrimEnd('/', '\\');
        return windowsStyle ? remote + remainder.Replace('/', '\\') : remote + remainder;
    }

    /// <summary>Forward slashes, no trailing slash ("/" becomes "").</summary>
    public static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
