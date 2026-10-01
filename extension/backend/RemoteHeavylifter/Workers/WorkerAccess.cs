using System.Collections.Concurrent;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Workers;

/// <summary>A file a worker uploaded for one artifact of a task.</summary>
public sealed record UploadedArtifact(string Path, long Size, string Sha256);

/// <summary>
/// What one task lets its worker do over HTTP: read <see cref="SourcePath"/> and upload the requested kinds into
/// <see cref="UploadDirectory"/>. Registered when the task is submitted and removed when it ends.
/// </summary>
public sealed class TaskAssignment(string taskId, Guid workerId, string sourcePath, IReadOnlyCollection<string> kinds, string uploadDirectory)
{
    public string TaskId { get; } = taskId;
    public Guid WorkerId { get; } = workerId;
    public string SourcePath { get; } = sourcePath;
    public IReadOnlySet<string> Kinds { get; } = new HashSet<string>(kinds, StringComparer.Ordinal);
    public string UploadDirectory { get; } = uploadDirectory;
    internal ConcurrentDictionary<string, UploadedArtifact> Uploads { get; } = new(StringComparer.Ordinal);

    public UploadedArtifact? Upload(string kind) => Uploads.TryGetValue(kind, out var upload) ? upload : null;
}

public enum AccessDecision
{
    Allowed,
    /// <summary>No token, or one that matches no enabled worker.</summary>
    Unauthorized,
    /// <summary>A trusted worker asking for a task (or kind) that is not assigned to it.</summary>
    Forbidden,
}

/// <summary>
/// Authorizes the worker-facing HTTP endpoints. A request must carry the token of an enabled worker, and may only
/// touch a task currently assigned to that worker — so a worker can read the videos it was given and nothing else.
/// </summary>
public sealed class WorkerAccess(WorkerRegistry registry)
{
    private readonly ConcurrentDictionary<string, TaskAssignment> _tasks = new(StringComparer.Ordinal);

    public void Register(TaskAssignment assignment) => _tasks[assignment.TaskId] = assignment;

    public void Remove(string taskId) => _tasks.TryRemove(taskId, out _);

    public void RemoveForWorker(Guid workerId)
    {
        foreach (var (taskId, assignment) in _tasks)
        {
            if (assignment.WorkerId == workerId)
                _tasks.TryRemove(taskId, out _);
        }
    }

    public TaskAssignment? Get(string taskId) => _tasks.TryGetValue(taskId, out var assignment) ? assignment : null;

    /// <param name="kind">The artifact kind for uploads; null for reading the source.</param>
    public (AccessDecision Decision, TaskAssignment? Assignment) Authorize(string? token, string taskId, string? kind)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (AccessDecision.Unauthorized, null);
        if (registry.FindEnabledByHash(WorkerTokens.Hash(token)) is not { } worker)
            return (AccessDecision.Unauthorized, null);
        if (!_tasks.TryGetValue(taskId, out var assignment) || assignment.WorkerId != worker.Id)
            return (AccessDecision.Forbidden, null);
        if (kind is not null && !assignment.Kinds.Contains(kind))
            return (AccessDecision.Forbidden, null);
        return (AccessDecision.Allowed, assignment);
    }

    internal static void RecordUpload(TaskAssignment assignment, string kind, UploadedArtifact upload)
        => assignment.Uploads[kind] = upload;
}
