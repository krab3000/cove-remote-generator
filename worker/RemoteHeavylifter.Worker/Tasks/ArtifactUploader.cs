using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Tasks;

/// <summary>Raised when Cove's HTTP endpoints cannot be reached (or refuse the worker), as opposed to a generation failure.</summary>
public sealed class CoveUnreachableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Talks to the extension's worker HTTP endpoints: checks a source is readable and uploads artifacts.</summary>
public sealed class CoveHttpClient(HttpClient http, WorkerToken token)
{
    private const int UploadAttempts = 3;

    /// <summary>A HEAD on the source, so an unreachable Cove (or a refused token) fails fast and is reported as such.</summary>
    public async Task CheckSourceAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);
        request.Headers.Add(ProtocolInfo.TokenHeader, token.Value);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new CoveUnreachableException($"cannot reach Cove at {Host(url)}: {ex.Message}", ex);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new CoveUnreachableException($"Cove refused the source ({(int)response.StatusCode} {Describe(response.StatusCode)})");
        }
    }

    /// <summary>PUT the file and check Cove received exactly it. Retries transient failures.</summary>
    public async Task UploadAsync(string url, string path, CancellationToken ct)
    {
        var size = new FileInfo(path).Length;
        var sha = await Outputs.Sha256FileAsync(path, ct);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var file = File.OpenRead(path);
                using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StreamContent(file) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Content.Headers.ContentLength = size;
                request.Headers.Add(ProtocolInfo.TokenHeader, token.Value);
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode;
                    var message = $"upload refused ({status} {Describe(response.StatusCode)})";
                    if (status >= 500 && attempt < UploadAttempts)
                        throw new HttpRequestException(message);
                    throw new CoveUnreachableException(message);
                }

                var receipt = await response.Content.ReadFromJsonAsync<UploadReceipt>(RpcChannel.JsonOptions, ct)
                    ?? throw new InvalidDataException("empty upload receipt");
                if (receipt.Size != size || !string.Equals(receipt.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException($"Cove received {receipt.Size} bytes that do not match the {size}-byte file");
                return;
            }
            catch (HttpRequestException) when (attempt < UploadAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (HttpRequestException ex)
            {
                throw new CoveUnreachableException($"upload to {Host(url)} failed: {ex.Message}", ex);
            }
        }
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

    private static string Describe(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => "the worker token is not trusted",
        HttpStatusCode.Forbidden => "the task is not assigned to this worker",
        HttpStatusCode.NotFound => "not found",
        _ => status.ToString(),
    };
}
