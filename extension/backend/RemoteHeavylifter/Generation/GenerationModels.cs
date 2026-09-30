using Cove.Core.Interfaces;

namespace RemoteHeavylifter.Generation;

/// <summary>What the Generate panel submits (and what the task-list job replays).</summary>
public sealed record GenerateRequest
{
    public IReadOnlyList<Guid> ServerIds { get; init; } = [];
    public IReadOnlyList<string> Paths { get; init; } = [];
    public bool Cover { get; init; } = true;
    public bool Preview { get; init; } = true;
    public bool Sprite { get; init; } = true;
    public bool Overwrite { get; init; }

    public bool AnyArtifact => Cover || Preview || Sprite;
}

/// <summary>Cove's preview settings, snapshotted when a run starts and clamped the way Cove clamps them.</summary>
public sealed record PreviewSettings(int Segments, double SegmentDuration, string ExcludeStart, string ExcludeEnd, string Preset, bool Audio)
{
    public const int Width = 640;
    public const int Crf = 21;

    public static PreviewSettings From(CoveConfiguration config) => new(
        Math.Clamp(config.Ui.PreviewSegments <= 0 ? 12 : config.Ui.PreviewSegments, 1, 100),
        Math.Clamp(config.Ui.PreviewSegmentDuration <= 0 ? 0.75 : config.Ui.PreviewSegmentDuration, 0.1, 30d),
        config.Ui.PreviewExcludeStart ?? "0",
        config.Ui.PreviewExcludeEnd ?? "0",
        string.IsNullOrWhiteSpace(config.PreviewPreset) ? "slow" : config.PreviewPreset.Trim(),
        string.Equals(config.PreviewAudio, "true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One video to generate on some server.</summary>
public sealed class WorkItem
{
    public required int VideoId { get; init; }
    public required string Label { get; init; }
    public required string CovePath { get; init; }
    public required double Duration { get; init; }
    public bool Cover { get; init; }
    public bool Preview { get; init; }
    public bool Sprite { get; init; }
    public string? CoverFilter { get; init; }
    public string? PreviewScale { get; init; }
    public string? SpriteFilter { get; init; }

    /// <summary>The source path as each candidate server sees it. Shrinks when a server cannot find the file.</summary>
    public required Dictionary<Guid, string> RemotePaths { get; init; }

    public int Attempts { get; set; }
    public IJobUnit? Unit { get; set; }
    public List<string> Notes { get; } = [];

    public double CoverSeek => Duration * 0.2 is var seek && seek > 0 ? seek : 1;
}

/// <summary>A video decided before any remote work: skipped or failed up front.</summary>
public sealed record SettledItem(int VideoId, string Label, JobUnitOutcome Outcome, string Reason);

public sealed record SelectionResult(List<WorkItem> Work, List<SettledItem> Settled, int Examined);
