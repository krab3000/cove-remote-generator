using System.Net.WebSockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Tasks;
using StreamJsonRpc;

namespace RemoteHeavylifter.Worker.Transport;

/// <summary>Facts about this worker reported to Cove at hello.</summary>
public sealed class WorkerFacts(WorkerOptions options, TaskRunner runner)
{
    public static string Version { get; } =
        typeof(WorkerFacts).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string? FfmpegVersion { get; set; }

    public WorkerInfo Info() => new(ProtocolInfo.Version, Version, options.Name, FfmpegVersion, options.Encoder, runner.Capacity, runner.Running);
}

/// <summary>
/// One JSON-RPC session with Cove over an open WebSocket, whichever side dialed. Cove calls the methods below;
/// the worker reports task progress and results back as notifications. Tasks belong to their session and are
/// cancelled when it ends.
/// </summary>
public sealed class CoveSession(TaskRunner runner, WorkerFacts facts, ILogger<CoveSession> logger)
{
    public async Task RunAsync(WebSocket socket, string peer, CancellationToken ct)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var target = new Target(runner, facts, logger, sessionCts.Token);
        using var rpc = RpcChannel.Create(socket, target);
        target.Rpc = rpc;
        rpc.StartListening();
        logger.LogInformation("Connected to Cove ({Peer})", peer);
        try
        {
            await rpc.Completion.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ConnectionLostException or WebSocketException)
        {
        }
        finally
        {
            // Nothing keeps running for a Cove that is gone: it re-queues the work itself.
            await sessionCts.CancelAsync();
            if (socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, CloseReasons.ShuttingDown, close.Token);
                }
                catch (Exception closeEx) when (closeEx is WebSocketException or OperationCanceledException)
                {
                }
            }
            logger.LogInformation("Disconnected from Cove ({Peer})", peer);
        }
    }

    private sealed class Target(TaskRunner runner, WorkerFacts facts, ILogger logger, CancellationToken session) : ITaskReporter
    {
        public JsonRpc? Rpc { get; set; }

        [JsonRpcMethod(RpcMethods.Hello)]
        public WorkerInfo Hello(HelloParams hello)
        {
            if (hello.ProtocolVersion != ProtocolInfo.Version)
                logger.LogWarning("Cove speaks protocol {Theirs}, this worker {Ours}; update the older side", hello.ProtocolVersion, ProtocolInfo.Version);
            logger.LogInformation("Cove says hello; files are exchanged through {CoveUrl}", hello.CoveBaseUrl);
            return facts.Info();
        }

        [JsonRpcMethod(RpcMethods.GetInfo)]
        public WorkerInfo GetInfo() => facts.Info();

        [JsonRpcMethod(RpcMethods.Submit)]
        public bool Submit(TaskRequest request)
        {
            if (!runner.TryStart(request, this, session))
                throw new LocalRpcException($"the worker is busy ({runner.Running}/{runner.Capacity})") { ErrorCode = RpcErrorCodes.Busy };
            return true;
        }

        [JsonRpcMethod(RpcMethods.Cancel)]
        public void Cancel(string taskId) => runner.Cancel(taskId);

        [JsonRpcMethod(RpcMethods.ProbeSource)]
        public Task<ProbeResult> ProbeSource(ProbeParams request, CancellationToken ct) => runner.ProbeAsync(request, ct);

        public Task ProgressAsync(TaskProgress progress) => NotifyAsync(RpcMethods.TaskProgress, progress);

        public Task FinishedAsync(TaskResult result) => NotifyAsync(RpcMethods.TaskFinished, result);

        private async Task NotifyAsync(string method, object argument)
        {
            if (Rpc is not { IsDisposed: false } rpc)
                return;
            try
            {
                await rpc.NotifyAsync(method, argument);
            }
            catch (Exception ex) when (ex is ConnectionLostException or ObjectDisposedException or WebSocketException)
            {
            }
        }
    }
}
