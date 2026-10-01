using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Workers;

/// <summary>
/// Keeps one JSON-RPC session per worker. Workers with a URL are dialed (and redialed with backoff) from the
/// extension's background loop; workers without one dial in through <see cref="HandleInboundAsync"/>. Unknown
/// tokens that dial in are recorded as pending until someone trusts them.
/// </summary>
public sealed class WorkerHub : IWorkerDirectory
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LoopInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private sealed class DialState
    {
        public int Failures;
        public DateTimeOffset NextAttempt;
        public bool Dialing;
        public string State = WorkerStates.Offline;
        public string? Error;
    }

    private readonly WorkerRegistry _registry;
    private readonly PendingWorkers _pending;
    private readonly WorkerSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<WorkerHub> _logger;
    private readonly ConcurrentDictionary<Guid, WorkerSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, DialState> _dial = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wake = new(0);

    public WorkerHub(WorkerRegistry registry, PendingWorkers pending, WorkerSettingsStore settings, TimeProvider time, ILogger<WorkerHub> logger)
    {
        _registry = registry;
        _pending = pending;
        _settings = settings;
        _time = time;
        _logger = logger;
        _registry.Changed += () => _wake.Release();
    }

    public IWorkerSession? GetSession(Guid workerId)
        => _sessions.TryGetValue(workerId, out var session) && session.IsOpen ? session : null;

    /// <summary>The base URL a worker must use to reach Cove: its override, then the global setting, then the URL it dialed.</summary>
    public static string? ResolveCoveBaseUrl(WorkerDefinition worker, WorkerSettings settings, string? inboundBaseUrl)
        => worker.CoveUrlOverride ?? settings.CoveUrlForWorkers ?? inboundBaseUrl?.TrimEnd('/');

    // ---- background loop (outbound dialing + reconciliation) --------------------------------------

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        var token = linked.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var workers = await _registry.GetAllAsync(token);
                await ReconcileAsync(workers);
                foreach (var worker in workers.Where(w => w.Enabled && w.CoveDials))
                    StartDialIfDue(worker, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote worker loop failed; retrying");
            }

            try
            {
                await _wake.WaitAsync(LoopInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        await CloseAllAsync(CloseReasons.ShuttingDown);
    }

    public async Task CloseAllAsync(string reason)
    {
        await _shutdown.CancelAsync();
        await Task.WhenAll(_sessions.Values.Select(s => s.CloseAsync(reason)));
    }

    /// <summary>Drop sessions whose worker was removed, disabled, or given a different token.</summary>
    private async Task ReconcileAsync(IReadOnlyList<WorkerDefinition> workers)
    {
        foreach (var (id, session) in _sessions)
        {
            var worker = workers.FirstOrDefault(w => w.Id == id);
            if (worker is null || !worker.Enabled || !WorkerTokens.HashEquals(worker.TokenHash, session.TokenHash))
            {
                _logger.LogInformation("Disconnecting remote worker {Worker}: it was removed or changed", session.Name);
                await session.CloseAsync(CloseReasons.Removed);
                _sessions.TryRemove(new KeyValuePair<Guid, WorkerSession>(id, session));
            }
        }
        foreach (var id in _dial.Keys)
        {
            if (workers.All(w => w.Id != id || !w.CoveDials))
                _dial.TryRemove(id, out _);
        }
    }

    private void StartDialIfDue(WorkerDefinition worker, CancellationToken ct)
    {
        if (_sessions.ContainsKey(worker.Id))
            return;
        var state = _dial.GetOrAdd(worker.Id, _ => new DialState());
        lock (state)
        {
            if (state.Dialing || _time.GetUtcNow() < state.NextAttempt)
                return;
            state.Dialing = true;
        }
        _ = Task.Run(() => DialAndServeAsync(worker, state, ct), CancellationToken.None);
    }

    private async Task DialAndServeAsync(WorkerDefinition worker, DialState state, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ProtocolInfo.TokenHeader, worker.Token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(HandshakeTimeout);
                await socket.ConnectAsync(new Uri(worker.Url!), connect.Token);
            }
            await ServeAsync(worker, socket, inboundBaseUrl: null, remoteAddress: worker.Url, state, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var unauthorized = socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            Fail(state, unauthorized ? WorkerStates.Unauthorized : WorkerStates.Offline,
                unauthorized ? "the worker rejected the token" : Describe(ex));
            _logger.LogDebug(ex, "Could not connect to remote worker {Worker} at {Url}", worker.Name, worker.Url);
        }
        finally
        {
            lock (state)
                state.Dialing = false;
        }
    }

    // ---- inbound (worker dials Cove) -------------------------------------------------------------

    public async Task HandleInboundAsync(HttpContext http)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("A WebSocket upgrade is required.");
            return;
        }

        var token = http.Request.Headers[ProtocolInfo.TokenHeader].ToString();
        if (token.Length == 0)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _registry.GetAllAsync(http.RequestAborted);
        var hash = WorkerTokens.Hash(token);
        var worker = _registry.FindByHash(hash);
        var remoteAddress = http.Connection.RemoteIpAddress?.ToString();
        if (worker is null)
        {
            await _pending.RecordAsync(hash,
                http.Request.Headers[ProtocolInfo.WorkerNameHeader].ToString(),
                http.Request.Headers[ProtocolInfo.WorkerVersionHeader].ToString(),
                remoteAddress, http.RequestAborted);
            Refuse(http, StatusCodes.Status403Forbidden, CloseReasons.PendingApproval);
            return;
        }
        if (!worker.Enabled)
        {
            Refuse(http, StatusCodes.Status403Forbidden, WorkerStates.Disabled);
            return;
        }
        if (_sessions.ContainsKey(worker.Id))
        {
            Refuse(http, StatusCodes.Status409Conflict, CloseReasons.DuplicateSession);
            return;
        }

        var inboundBase = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, _shutdown.Token);
        await ServeAsync(worker, socket, inboundBase, remoteAddress, state: null, linked.Token);
    }

    private static void Refuse(HttpContext http, int status, string reason)
    {
        http.Response.StatusCode = status;
        http.Response.Headers[RefusalHeader.Name] = reason;
    }

    // ---- a live session --------------------------------------------------------------------------

    private async Task ServeAsync(WorkerDefinition worker, WebSocket socket, string? inboundBaseUrl, string? remoteAddress, DialState? state, CancellationToken ct)
    {
        var session = new WorkerSession(worker, socket, inboundBaseUrl, remoteAddress, _logger);
        if (!_sessions.TryAdd(worker.Id, session))
        {
            await session.CloseAsync(CloseReasons.DuplicateSession);
            return;
        }

        try
        {
            var settings = await _settings.GetAsync(ct);
            WorkerInfo info;
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hello.CancelAfter(HandshakeTimeout);
                info = await session.HelloAsync(ResolveCoveBaseUrl(worker, settings, inboundBaseUrl) ?? "", hello.Token);
            }
            if (info.ProtocolVersion != ProtocolInfo.Version)
            {
                var message = $"protocol {info.ProtocolVersion}, Cove expects {ProtocolInfo.Version}";
                if (state is not null)
                    Fail(state, WorkerStates.Incompatible, message);
                _logger.LogWarning("Remote worker {Worker} is incompatible: {Message}", worker.Name, message);
                await session.CloseAsync(CloseReasons.Incompatible);
                return;
            }

            if (state is not null)
            {
                lock (state)
                {
                    state.Failures = 0;
                    state.State = WorkerStates.Live;
                    state.Error = null;
                }
            }
            _logger.LogInformation("Remote worker {Worker} connected ({Direction}, {Capacity} slots, {Encoder})",
                worker.Name, worker.Connection, info.Capacity, info.Encoder);

            await session.Completion.WaitAsync(ct);
            if (state is not null)
                Fail(state, WorkerStates.Offline, "the connection closed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (state is not null)
                Fail(state, WorkerStates.Offline, Describe(ex));
            _logger.LogInformation("Remote worker {Worker} disconnected: {Error}", worker.Name, Describe(ex));
        }
        finally
        {
            _sessions.TryRemove(new KeyValuePair<Guid, WorkerSession>(worker.Id, session));
            await session.CloseAsync(ct.IsCancellationRequested ? CloseReasons.ShuttingDown : CloseReasons.Removed);
        }
    }

    private void Fail(DialState state, string status, string error)
    {
        lock (state)
        {
            state.Failures++;
            state.State = status;
            state.Error = error;
            var backoff = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(state.Failures - 1, 6))));
            state.NextAttempt = _time.GetUtcNow() + backoff;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: { } inner } => inner.Message,
        OperationCanceledException => "timed out",
        _ => ex.Message,
    };

    // ---- status ----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WorkerHealthView>> GetHealthAsync(bool refresh, CancellationToken ct)
    {
        var workers = await _registry.GetAllAsync(ct);
        if (refresh)
        {
            await Task.WhenAll(_sessions.Values.Where(s => s.IsOpen).Select(async session =>
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    await session.RefreshInfoAsync(cts.Token);
                }
                catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
                {
                }
            }));
        }

        var now = _time.GetUtcNow();
        return workers.Select(worker => Health(worker, now)).ToList();
    }

    private WorkerHealthView Health(WorkerDefinition worker, DateTimeOffset now)
    {
        if (_sessions.TryGetValue(worker.Id, out var session) && session.IsOpen && TryInfo(session) is { } info)
        {
            return new WorkerHealthView(worker.Id, worker.Name, worker.Enabled, WorkerStates.Live, true, worker.Connection,
                info.WorkerVersion, info.FfmpegVersion, info.Encoder, info.Capacity, info.Running,
                session.RemoteAddress, session.ConnectedSince, null, now);
        }

        string state;
        string? error = null;
        if (!worker.Enabled)
        {
            state = WorkerStates.Disabled;
        }
        else if (worker.CoveDials)
        {
            var dial = _dial.GetValueOrDefault(worker.Id);
            state = dial?.State == WorkerStates.Live ? WorkerStates.Offline : dial?.State ?? WorkerStates.Offline;
            error = dial?.Error ?? "connecting…";
        }
        else
        {
            state = WorkerStates.Waiting;
            error = "waiting for the worker to connect";
        }

        return new WorkerHealthView(worker.Id, worker.Name, worker.Enabled, state, false, worker.Connection,
            null, null, null, null, null, null, null, error, now);
    }

    private static WorkerInfo? TryInfo(WorkerSession session)
    {
        try
        {
            return session.Info;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
