namespace RemoteHeavylifter.Protocol;

/// <summary>Wire-level constants shared by the Cove extension and the worker.</summary>
public static class ProtocolInfo
{
    /// <summary>Bumped on any incompatible change to the RPC methods or DTOs.</summary>
    public const int Version = 2;

    /// <summary>Carries the worker token on the WebSocket upgrade and on every worker HTTP request.
    /// Deliberately not <c>Authorization: Bearer</c>: Cove's JWT and API-token handlers would try to parse it.</summary>
    public const string TokenHeader = "X-Heavylifter-Token";
    public const string WorkerNameHeader = "X-Heavylifter-Worker-Name";
    public const string WorkerVersionHeader = "X-Heavylifter-Worker-Version";

    /// <summary>Path the worker listens on when Cove dials it.</summary>
    public const string WorkerSocketPath = "/rpc";

    /// <summary>Path (under the extension's API root) a worker dials to reach Cove.</summary>
    public const string CoveSocketPath = "worker/ws";

    public const string ExtensionId = "com.cove.remote-heavylifter";

    /// <summary>The WebSocket URL a worker dials, from Cove's base URL (http(s)://host[:port][/base]).</summary>
    public static Uri CoveSocketUri(string coveBaseUrl)
    {
        var builder = new UriBuilder(coveBaseUrl.TrimEnd('/') + $"/api/ext/{ExtensionId}/{CoveSocketPath}");
        builder.Scheme = builder.Scheme switch { "https" => "wss", "http" => "ws", var other => other };
        if (builder.Uri.IsDefaultPort)
            builder.Port = -1;
        return builder.Uri;
    }
}

/// <summary>JSON-RPC method names. Cove calls the first group on the worker; the worker notifies Cove with the second.</summary>
public static class RpcMethods
{
    public const string Hello = "hello";
    public const string GetInfo = "getInfo";
    public const string Submit = "submit";
    public const string Cancel = "cancel";
    public const string ProbeSource = "probeSource";

    public const string TaskProgress = "taskProgress";
    public const string TaskFinished = "taskFinished";
}

/// <summary>WebSocket close descriptions with a meaning the other side acts on.</summary>
public static class CloseReasons
{
    public const string PendingApproval = "pending-approval";
    public const string DuplicateSession = "duplicate-session";
    public const string Unauthorized = "unauthorized";
    public const string Incompatible = "incompatible";
    public const string Removed = "removed";
    public const string ShuttingDown = "shutting-down";
}

/// <summary>JSON-RPC error codes the worker raises (application range, below -32000).</summary>
public static class RpcErrorCodes
{
    public const int Busy = -32010;
    public const int Incompatible = -32011;
    public const int UnknownTask = -32012;
}

/// <summary>HTTP status detail Cove sends when it refuses a worker's WebSocket upgrade.</summary>
public static class RefusalHeader
{
    public const string Name = "X-Heavylifter-Refusal";
}

public static class ErrorCodes
{
    public const string Busy = "busy";
    public const string SourceUnreachable = "source_unreachable";
    public const string GenerationFailed = "generation_failed";
    public const string Incompatible = "incompatible";
    public const string Internal = "internal_error";
}

public static class ArtifactKinds
{
    public const string Cover = "cover";
    public const string Preview = "preview";
    public const string Sprite = "sprite";
    public const string Vtt = "vtt";

    public static readonly IReadOnlyList<string> All = [Cover, Preview, Sprite, Vtt];
}

public static class TaskStates
{
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public static class ArtifactStates
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed record HelloParams(int ProtocolVersion, string CoveBaseUrl);

public sealed record WorkerInfo(
    int ProtocolVersion,
    string WorkerVersion,
    string? Name,
    string? FfmpegVersion,
    string Encoder,
    int Capacity,
    int Running);

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

/// <summary>One video to generate. URLs are absolute; the worker sends its token with every request to them.</summary>
public sealed record TaskRequest(
    string TaskId,
    int VideoId,
    string SourceUrl,
    long SourceSize,
    double Duration,
    IReadOnlyDictionary<string, string> UploadUrls,
    CoverSpec? Cover,
    PreviewSpec? Preview,
    SpriteSpec? Sprite);

public sealed record TaskProgress(string TaskId, double Progress, string? Stage);

public sealed record ArtifactResult(string Status, long? Size, string? Sha256, string? Error)
{
    public bool Succeeded => Status == ArtifactStates.Succeeded;
}

public sealed record TaskResult(
    string TaskId,
    string Status,
    string? Error,
    string? ErrorCode,
    IReadOnlyDictionary<string, ArtifactResult> Artifacts);

public sealed record ProbeParams(string TaskId, string SourceUrl);

public sealed record ProbeResult(bool Ok, double? Duration, string? Error);

/// <summary>Body of a successful artifact upload response.</summary>
public sealed record UploadReceipt(long Size, string Sha256);
