namespace RemoteHeavylifter.Generation;

/// <summary>Selective-generate folder filter, matching Cove's GeneratePathFilter.</summary>
public static class PathFilter
{
    public static List<string> Normalize(IEnumerable<string>? paths)
        => paths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

    /// <summary>True when no filter is set, or the path is one of the folders or inside one.</summary>
    public static bool Contains(string candidatePath, IReadOnlyList<string> normalizedFilters)
    {
        if (normalizedFilters.Count == 0)
            return true;

        var candidate = NormalizePath(candidatePath);
        return normalizedFilters.Any(path =>
            candidate.Equals(path, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Forward slashes, no trailing slash: the form both Cove paths and filter folders are compared in.</summary>
    public static string NormalizePath(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
