using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;

namespace RemoteHeavylifter.Worker.Transport;

/// <summary>
/// Worker-dials mode: keeps a WebSocket open to Cove (<c>HL_COVE_URL</c>), reconnecting with backoff. Until someone
/// trusts this worker's token in Cove, Cove answers "pending approval" and the worker keeps asking.
/// </summary>
public sealed class CoveConnector(WorkerOptions options, WorkerToken token, CoveSession session, ILogger<CoveConnector> logger) : BackgroundService
{
    // Short, so trusting a pending worker in Cove takes effect quickly.
    private static readonly TimeSpan PendingRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var uri = ProtocolInfo.CoveSocketUri(options.CoveUrl!);
        var failures = 0;
        string? lastRefusal = null;

        while (!ct.IsCancellationRequested)
        {
            TimeSpan wait;
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader(ProtocolInfo.TokenHeader, token.Value);
            socket.Options.SetRequestHeader(ProtocolInfo.WorkerNameHeader, options.Name);
            socket.Options.SetRequestHeader(ProtocolInfo.WorkerVersionHeader, Transport.WorkerFacts.Version);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.CollectHttpResponseDetails = true;
            try
            {
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(uri, connect.Token);
                }
                failures = 0;
                lastRefusal = null;
                await session.RunAsync(socket, uri.Authority, ct);
                wait = MinBackoff;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var refusal = socket.HttpResponseHeaders?.TryGetValue(RefusalHeader.Name, out var values) == true ? values.FirstOrDefault() : null;
                (wait, var message) = (socket.HttpStatusCode, refusal) switch
                {
                    (HttpStatusCode.Forbidden, CloseReasons.PendingApproval) =>
                        (PendingRetry, $"Waiting for approval in Cove: trust worker ID {token.Id} under Settings → Remote Generation → Pending workers."),
                    (HttpStatusCode.Forbidden, _) => (PendingRetry, "Cove has this worker disabled."),
                    (HttpStatusCode.Conflict, _) => (PendingRetry, "Cove already has a session with this worker (it may be dialing the worker directly)."),
                    (HttpStatusCode.Unauthorized, _) => (PendingRetry, "Cove rejected the request: no worker token was sent."),
                    _ => (Backoff(++failures), $"Cannot connect to Cove at {uri}: {Describe(ex)}"),
                };
                // Don't repeat the same refusal every 30 s.
                if (message != lastRefusal)
                    logger.LogWarning("{Message}", message);
                lastRefusal = message;
            }

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static TimeSpan Backoff(int failures)
        => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 6))));

    private static string Describe(Exception ex) => ex switch
    {
        WebSocketException { InnerException: { } inner } => inner.Message,
        OperationCanceledException => "timed out",
        _ => ex.Message,
    };
}
