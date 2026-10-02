namespace RemoteHeavylifter.Workers;

/// <summary>Turns a source path as Cove stores it into the same file's path on a worker.</summary>
public static class PathMapper
{
    /// <summary>The worker path for <paramref name="covePath"/> under the longest matching mapping (whole path
    /// segments only: /media maps /media/a.mp4, not /media2/a.mp4); null when no mapping covers it.</summary>
    public static string? Map(string covePath, IReadOnlyList<PathMapping> mappings)
    {
        static string Key(string path) => path.Replace('\\', '/').TrimEnd('/');
        var path = covePath.Replace('\\', '/');
        var best = mappings
            .Select(m => (Mapping: m, Prefix: Key(m.CovePath)))
            .Where(m => m.Prefix.Length == 0
                ? path.StartsWith('/')
                : path == m.Prefix || path.StartsWith(m.Prefix + "/", StringComparison.Ordinal))
            .OrderByDescending(m => m.Prefix.Length)
            .FirstOrDefault();
        if (best.Mapping is null)
            return null;

        var rest = path[best.Prefix.Length..].TrimStart('/');
        var root = best.Mapping.WorkerPath;
        var separator = IsWindows(root) ? '\\' : '/';
        if (rest.Length == 0)
            return root;
        return root.TrimEnd('/', '\\') + separator + rest.Replace('/', separator);
    }

    /// <summary>A mapping path as stored: trimmed, without a trailing separator (except a root such as / or C:\).</summary>
    public static string Trim(string? path)
    {
        var trimmed = path?.Trim() ?? "";
        var stripped = trimmed.TrimEnd('/', '\\');
        if (stripped.Length == 0)
            return trimmed.Length == 0 ? "" : trimmed[..1];
        return stripped.Length == 2 && stripped[1] == ':' ? stripped + "\\" : stripped;
    }

    private static bool IsWindows(string path)
        => path.StartsWith(@"\\", StringComparison.Ordinal) || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]));
}
