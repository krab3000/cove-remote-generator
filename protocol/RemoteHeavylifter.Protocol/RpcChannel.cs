using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamJsonRpc;

namespace RemoteHeavylifter.Protocol;

/// <summary>
/// Builds the JSON-RPC connection over a WebSocket. Uses System.Text.Json and no generated proxies: the extension
/// runs in a collectible AssemblyLoadContext, where Reflection.Emit-based proxies and MessagePack resolvers can fail.
/// </summary>
public static class RpcChannel
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static JsonRpc Create(WebSocket socket, object? target)
    {
        var formatter = new SystemTextJsonFormatter { JsonSerializerOptions = CreateJsonOptions() };
        var rpc = new JsonRpc(new WebSocketMessageHandler(socket, formatter))
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
        };
        if (target is not null)
            rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions { AllowNonPublicInvocation = false });
        return rpc;
    }

    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
