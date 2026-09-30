using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using RemoteHeavylifter.Contract;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter.Remote;

/// <summary>
/// A failed call to a generation server. <see cref="Transient"/> failures (unreachable, timeouts, 5xx,
/// full queue/disk, a task that vanished) are retried elsewhere; the rest are not.
/// </summary>
public sealed class RemoteException(string message, bool transient, int? statusCode = null, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Transient { get; } = transient;
    public int? StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsAuthFailure => StatusCode is 401 or 403;
}

public sealed record DownloadResult(long Length, string Sha256);

public interface IRemoteClient
{
    Task<RemoteInfo> GetInfoAsync(TimeSpan timeout, CancellationToken ct);
    Task<IReadOnlyList<PathCheckResult>> CheckPathsAsync(IReadOnlyList<string> paths, CancellationToken ct);
    Task<RemoteTaskStatus> SubmitAsync(RemoteTaskRequest request, CancellationToken ct);
    /// <summary>The task's status, or null when the server no longer knows it (404).</summary>
    Task<RemoteTaskStatus?> GetTaskAsync(string taskId, CancellationToken ct);
    Task<DownloadResult> DownloadArtifactAsync(string taskId, string kind, string destinationPath, CancellationToken ct);
    /// <summary>Delete a task and its files. An unknown task is not an error.</summary>
    Task DeleteTaskAsync(string taskId, CancellationToken ct);
}

public interface IRemoteClientFactory
{
    IRemoteClient Create(ServerDefinition server);
}

internal sealed class HttpRemoteClientFactory(IHttpClientFactory httpClients) : IRemoteClientFactory
{
    public const string HttpClientName = "remote-heavylifter";

    public IRemoteClient Create(ServerDefinition server)
        => new HttpRemoteClient(httpClients.CreateClient(HttpClientName), server);
}

internal sealed class HttpRemoteClient(HttpClient http, ServerDefinition server) : IRemoteClient
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    private readonly Uri _base = new(server.BaseUrl.TrimEnd('/') + "/");

    public async Task<RemoteInfo> GetInfoAsync(TimeSpan timeout, CancellationToken ct)
        => await SendAsync<RemoteInfo>(HttpMethod.Get, "v1/info", null, timeout, ct)
            ?? throw new RemoteException("Empty response from /v1/info", transient: true);

    public async Task<IReadOnlyList<PathCheckResult>> CheckPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        => await SendAsync<List<PathCheckResult>>(HttpMethod.Post, "v1/paths/check", new PathCheckRequest(paths), DefaultTimeout, ct)
            ?? [];

    public async Task<RemoteTaskStatus> SubmitAsync(RemoteTaskRequest request, CancellationToken ct)
        => await SendAsync<RemoteTaskStatus>(HttpMethod.Post, "v1/tasks", request, DefaultTimeout, ct)
            ?? throw new RemoteException("Empty response when submitting a task", transient: true);

    public async Task<RemoteTaskStatus?> GetTaskAsync(string taskId, CancellationToken ct)
    {
        try
        {
            return await SendAsync<RemoteTaskStatus>(HttpMethod.Get, $"v1/tasks/{Uri.EscapeDataString(taskId)}", null, DefaultTimeout, ct);
        }
        catch (RemoteException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task DeleteTaskAsync(string taskId, CancellationToken ct)
    {
        try
        {
            await SendAsync<object>(HttpMethod.Delete, $"v1/tasks/{Uri.EscapeDataString(taskId)}", null, DefaultTimeout, ct);
        }
        catch (RemoteException ex) when (ex.StatusCode == 404)
        {
        }
    }

    public async Task<DownloadResult> DownloadArtifactAsync(string taskId, string kind, string destinationPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);
        using var request = CreateRequest(HttpMethod.Get, $"v1/tasks/{Uri.EscapeDataString(taskId)}/artifacts/{kind}", null);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw await ErrorFromAsync(response, timeout.Token);

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    length += read;
                }
            }

            return new DownloadResult(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException($"Download of {kind} failed: {ex.Message}", transient: true, inner: ex);
        }
        catch (IOException ex) when (!ct.IsCancellationRequested && ex.InnerException is HttpRequestException or System.Net.Sockets.SocketException)
        {
            throw new RemoteException($"Download of {kind} was interrupted: {ex.Message}", transient: true, inner: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException($"Download of {kind} timed out", transient: true, inner: ex);
        }
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        using var request = CreateRequest(method, path, body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token);
            if (!response.IsSuccessStatusCode)
                throw await ErrorFromAsync(response, linked.Token);
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return default;
            return await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, linked.Token);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteException($"{server.Name} is unreachable: {ex.Message}", transient: true, inner: ex);
        }
        catch (JsonException ex)
        {
            throw new RemoteException($"{server.Name} sent an unexpected response: {ex.Message}", transient: false, inner: ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteException($"{server.Name} did not answer within {timeout.TotalSeconds:0}s", transient: true, inner: ex);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, new Uri(_base, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: RemoteJson.Options);
        return request;
    }

    private async Task<RemoteException> ErrorFromAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        RemoteError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<RemoteError>(RemoteJson.Options, ct);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
        }

        var transient = status >= 500 || status is 408 or 429;
        var message = error?.Message is { Length: > 0 } text ? text : $"HTTP {status} {response.ReasonPhrase}";
        return new RemoteException($"{server.Name}: {message}", transient, status, error?.Code);
    }
}
