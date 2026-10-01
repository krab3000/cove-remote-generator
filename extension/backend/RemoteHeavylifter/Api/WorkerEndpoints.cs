using System.Security.Cryptography;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter.Api;

/// <summary>
/// The endpoints workers call. They are anonymous to Cove's own auth: the worker is not a Cove user. Instead every
/// request carries the worker's token and may only touch a task currently assigned to that worker
/// (<see cref="WorkerAccess"/>), so a worker can read the videos it was given and upload their artifacts — nothing else.
/// </summary>
internal static class WorkerEndpoints
{
    public const long MaxUploadBytes = 4L * 1024 * 1024 * 1024;
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void Map(IEndpointRouteBuilder endpoints, string extensionId)
    {
        var root = $"/api/ext/{extensionId}";

        // Cove never enables WebSockets globally, so this endpoint gets its own small pipeline.
        var socketApp = endpoints.CreateApplicationBuilder();
        socketApp.UseWebSockets();
        socketApp.Run(http => http.RequestServices.GetRequiredService<WorkerHub>().HandleInboundAsync(http));
        endpoints.Map($"{root}/{ProtocolInfo.CoveSocketPath}", socketApp.Build()).AllowCoveAnonymous();

        endpoints.MapMethods($"{root}/worker/tasks/{{taskId}}/source", ["GET", "HEAD"], ReadSource).AllowCoveAnonymous();
        endpoints.MapPut($"{root}/worker/tasks/{{taskId}}/artifacts/{{kind}}", UploadArtifactAsync).AllowCoveAnonymous();
    }

    private static IResult ReadSource(string taskId, HttpContext http, WorkerAccess access)
    {
        var (decision, assignment) = access.Authorize(http.Request.Headers[ProtocolInfo.TokenHeader], taskId, kind: null);
        if (decision != AccessDecision.Allowed)
            return Refused(decision);

        FileStream stream;
        try
        {
            stream = new FileStream(assignment!.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1, FileOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Results.NotFound();
        }

        var contentType = ContentTypes.TryGetContentType(assignment.SourcePath, out var type) ? type : "application/octet-stream";
        return Results.File(stream, contentType, enableRangeProcessing: true);
    }

    private static async Task<IResult> UploadArtifactAsync(string taskId, string kind, HttpContext http, WorkerAccess access)
    {
        var (decision, assignment) = access.Authorize(http.Request.Headers[ProtocolInfo.TokenHeader], taskId, kind);
        if (decision != AccessDecision.Allowed)
            return Refused(decision);

        // Previews can exceed Kestrel's ~28 MB default body limit.
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxUploadBytes;
        if (http.Request.ContentLength is > MaxUploadBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        Directory.CreateDirectory(assignment!.UploadDirectory);
        var final = Path.Combine(assignment.UploadDirectory, FileName(kind));
        var part = final + ".part";
        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
                {
                    size += read;
                    if (size > MaxUploadBytes)
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                }
            }
            File.Move(part, final, overwrite: true);
        }
        finally
        {
            if (File.Exists(part))
                File.Delete(part);
        }

        var receipt = new UploadReceipt(size, Convert.ToHexStringLower(hash.GetHashAndReset()));
        WorkerAccess.RecordUpload(assignment, kind, new UploadedArtifact(final, receipt.Size, receipt.Sha256));
        return Results.Ok(receipt);
    }

    private static string FileName(string kind) => kind switch
    {
        ArtifactKinds.Cover => "cover.jpg",
        ArtifactKinds.Preview => "preview.mp4",
        ArtifactKinds.Sprite => "sprite.jpg",
        ArtifactKinds.Vtt => "thumbs.vtt",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static IResult Refused(AccessDecision decision) => decision == AccessDecision.Unauthorized
        ? Results.StatusCode(StatusCodes.Status401Unauthorized)
        : Results.StatusCode(StatusCodes.Status403Forbidden);
}
