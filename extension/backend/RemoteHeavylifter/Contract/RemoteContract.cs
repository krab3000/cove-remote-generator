using System.Text.Json;
using System.Text.Json.Serialization;

namespace RemoteHeavylifter.Contract;

// Wire types of the generation server's API v1 (contract/openapi.json). JSON is snake_case.

public static class RemoteJson
{
    public const int ApiVersion = 1;

    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class ArtifactKinds
{
    public const string Cover = "cover";
    public const string Preview = "preview";
    public const string Sprite = "sprite";
    public const string Vtt = "vtt";
}

public static class RemoteTaskStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status)
        => status is Succeeded or Partial or Failed or Cancelled;
}

public sealed record RemoteInfo(
    int ApiVersion,
    string ServerVersion,
    string? FfmpegVersion,
    string Encoder,
    int Capacity,
    int Running,
    int Queued,
    int MaxQueue,
    long DiskFreeBytes,
    IReadOnlyList<string> MediaRoots);

public sealed record CoverSpec(double? SeekSeconds, string? Filter, bool FallbackWithoutFilter = true);

public sealed record PreviewSpec(
    int Segments,
    double SegmentDuration,
    string ExcludeStart,
    string ExcludeEnd,
    string Preset,
    bool Audio,
    int Crf,
    int Width,
    string? ScaleFilter);

public sealed record SpriteSpec(int MaxFrames, int FrameWidth, string? PreFilter, string SpriteFilename);

public sealed record RemoteTaskRequest(
    string ClientTaskId,
    int VideoId,
    string SourcePath,
    double Duration,
    CoverSpec? Cover,
    PreviewSpec? Preview,
    SpriteSpec? Sprite);

public sealed record RemoteArtifact(string Status, long? Size, string? Sha256, string? Error)
{
    public bool Succeeded => Status == RemoteTaskStates.Succeeded;
}

public sealed record RemoteTaskStatus(
    string TaskId,
    string ClientTaskId,
    int VideoId,
    string Status,
    double Progress,
    string? Error,
    string? ErrorCode,
    IReadOnlyDictionary<string, RemoteArtifact> Artifacts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt)
{
    [JsonIgnore]
    public bool IsTerminal => RemoteTaskStates.IsTerminal(Status);
}

public sealed record PathCheckRequest(IReadOnlyList<string> Paths);

public sealed record PathCheckResult(string Path, bool Allowed, bool Exists, bool Readable);

public sealed record RemoteError(string Code, string Message);
