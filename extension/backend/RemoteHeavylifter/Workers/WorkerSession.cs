using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using StreamJsonRpc;

namespace RemoteHeavylifter.Workers;

/// <summary>A failure talking to a worker. Transient failures put the video back on the queue.</summary>
public sealed class WorkerException(string message, bool transient, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public string? Code { get; } = code;
}

/// <summary>What the run executor needs from a connected worker; faked in tests.</summary>
public interface IWorkerSession
{
    Guid WorkerId { get; }
    string Name { get; }
    WorkerInfo Info { get; }
    /// <summary>Base URL of the request the worker dialed in on, when it dialed; a fallback for building task URLs.</summary>
    string? InboundBaseUrl { get; }
    bool IsOpen { get; }

    /// <summary>Submit one task and wait for the worker to report it finished (artifacts already uploaded).</summary>
    Task<TaskResult> RunTaskAsync(TaskRequest request, Action<TaskProgress>? progress, CancellationToken ct);

    Task<ProbeResult> ProbeAsync(ProbeParams request, CancellationToken ct);

    Task<WorkerInfo> RefreshInfoAsync(CancellationToken ct);
}

/// <summary>Looks up the live session of a worker; implemented by <see cref="WorkerHub"/>.</summary>
public interface IWorkerDirectory
{
    IWorkerSession? GetSession(Guid workerId);
}

/// <summary>One open JSON-RPC connection to a worker, whichever side dialed.</summary>
internal sealed class WorkerSession : IWorkerSession, IAsyncDisposable
{
    private sealed record PendingTask(TaskCompletionSource<TaskResult> Completion, Action<TaskProgress>? Progress);

    private readonly WebSocket _socket;
    private readonly JsonRpc _rpc;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, PendingTask> _pending = new(StringComparer.Ordinal);
    private WorkerInfo? _info;

    public WorkerSession(WorkerDefinition worker, WebSocket socket, string? inboundBaseUrl, string? remoteAddress, ILogger logger)
    {
        WorkerId = worker.Id;
        Name = worker.Name;
        TokenHash = worker.TokenHash;
        InboundBaseUrl = inboundBaseUrl;
        RemoteAddress = remoteAddress;
        _socket = socket;
        _logger = logger;
        _rpc = RpcChannel.Create(socket, new CoveTarget(this));
        _rpc.Disconnected += (_, e) => FailPending(new WorkerException($"{Name}: connection lost ({e.Description})", transient: true));
        _rpc.StartListening();
    }

    public Guid WorkerId { get; }
    public string Name { get; }
    public string TokenHash { get; }
    public string? InboundBaseUrl { get; }
    public string? RemoteAddress { get; }
    public DateTimeOffset ConnectedSince { get; } = DateTimeOffset.UtcNow;
    public WorkerInfo Info => _info ?? throw new InvalidOperationException("The worker has not said hello yet.");
    public bool IsOpen => _socket.State == WebSocketState.Open && !_rpc.IsDisposed && !_rpc.Completion.IsCompleted;
    public Task Completion => _rpc.Completion;

    public async Task<WorkerInfo> HelloAsync(string coveBaseUrl, CancellationToken ct)
    {
        var info = await CallAsync<WorkerInfo>(RpcMethods.Hello, new HelloParams(ProtocolInfo.Version, coveBaseUrl), ct);
        _info = info;
        return info;
    }

    public async Task<WorkerInfo> RefreshInfoAsync(CancellationToken ct)
    {
        var info = await CallAsync<WorkerInfo>(RpcMethods.GetInfo, null, ct);
        _info = info;
        return info;
    }

    public Task<ProbeResult> ProbeAsync(ProbeParams request, CancellationToken ct)
        => CallAsync<ProbeResult>(RpcMethods.ProbeSource, request, ct);

    public async Task<TaskResult> RunTaskAsync(TaskRequest request, Action<TaskProgress>? progress, CancellationToken ct)
    {
        var pending = new PendingTask(new TaskCompletionSource<TaskResult>(TaskCreationOptions.RunContinuationsAsynchronously), progress);
        _pending[request.TaskId] = pending;
        try
        {
            await CallAsync<bool>(RpcMethods.Submit, request, ct);
            return await pending.Completion.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await CancelQuietlyAsync(request.TaskId);
            throw;
        }
        finally
        {
            _pending.TryRemove(request.TaskId, out _);
        }
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, cts.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _rpc.Dispose();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync(CloseReasons.ShuttingDown);

    private async Task<T> CallAsync<T>(string method, object? argument, CancellationToken ct)
    {
        try
        {
            return await _rpc.InvokeWithCancellationAsync<T>(method, argument is null ? [] : [argument], ct);
        }
        catch (RemoteInvocationException ex) when (ex.ErrorCode == RpcErrorCodes.Busy)
        {
            throw new WorkerException($"{Name} is busy", transient: true, ErrorCodes.Busy, ex);
        }
        catch (RemoteInvocationException ex)
        {
            throw new WorkerException($"{Name}: {ex.Message}", transient: false, inner: ex);
        }
        catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or WebSocketException)
        {
            throw new WorkerException($"{Name}: connection lost", transient: true, inner: ex);
        }
    }

    private async Task CancelQuietlyAsync(string taskId)
    {
        try
        {
            if (IsOpen)
                await _rpc.NotifyAsync(RpcMethods.Cancel, taskId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not cancel task {TaskId} on {Worker}", taskId, Name);
        }
    }

    private void FailPending(Exception error)
    {
        foreach (var (_, pending) in _pending)
            pending.Completion.TrySetException(error);
    }

    /// <summary>Methods the worker calls on Cove.</summary>
    private sealed class CoveTarget(WorkerSession session)
    {
        [JsonRpcMethod(RpcMethods.TaskProgress)]
        public void OnProgress(TaskProgress progress)
        {
            if (session._pending.TryGetValue(progress.TaskId, out var pending))
                pending.Progress?.Invoke(progress);
        }

        [JsonRpcMethod(RpcMethods.TaskFinished)]
        public void OnFinished(TaskResult result)
        {
            if (session._pending.TryGetValue(result.TaskId, out var pending))
                pending.Completion.TrySetResult(result);
        }
    }
}
