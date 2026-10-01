using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Media;
using SixLabors.ImageSharp;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Serves a clip the way Cove does (token header, range requests) and generates from the URL.</summary>
public class HttpSourceTests(ClipFixture clips) : IClassFixture<ClipFixture>
{
    private const string Token = "t0ken";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeCove : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeCove(WebApplication app) => _app = app;

        public ConcurrentQueue<(bool Authorized, string? Range)> Requests { get; } = new();

        public string Url => _app.Urls.First().TrimEnd('/') + "/source/7";

        public static async Task<FakeCove> StartAsync(string clip)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var cove = new FakeCove(app);
            app.MapGet("/source/7", (HttpContext http) =>
            {
                var authorized = http.Request.Headers[ProtocolInfo.TokenHeader] == Token;
                cove.Requests.Enqueue((authorized, http.Request.Headers.Range.FirstOrDefault()));
                return authorized ? Results.File(clip, "video/mp4", enableRangeProcessing: true) : Results.Unauthorized();
            });
            await app.StartAsync();
            return cove;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task CoverAndSpriteFromAuthenticatedUrl()
    {
        Ffmpeg.RequireOrSkip();
        await using var cove = await FakeCove.StartAsync(clips.Clip30);
        using var tmp = new TempDir();
        var source = MediaSource.ForCove(cove.Url, Token, new FileInfo(clips.Clip30).Length);
        var ctx = MediaContext.Default;

        Assert.Equal(30, await MediaProbe.DurationAsync(ctx, source, Ct), 0.1);

        var cover = tmp["cover.jpg"];
        await CoverGenerator.GenerateAsync(ctx, source, 30, Specs.Cover(), tmp.Path, cover, Ct);
        var coverInfo = Image.Identify(cover);
        Assert.Equal((320, 180), (coverInfo.Width, coverInfo.Height));

        string sprite = tmp["s.jpg"], vtt = tmp["t.vtt"];
        await SpriteGenerator.GenerateAsync(ctx, source, 30, Specs.Sprite("9_sprite.jpg"), tmp.Path, sprite, vtt, Ct);
        var spriteInfo = Image.Identify(sprite);
        Assert.Equal((640, 360), (spriteInfo.Width, spriteInfo.Height));
        Assert.Equal(15, (await File.ReadAllTextAsync(vtt, Ct)).Split("#xywh=").Length - 1);

        Assert.All(cove.Requests, r => Assert.True(r.Authorized));
        // Seeking into the file has to go through byte ranges past the start.
        Assert.Contains(cove.Requests, r => r.Range is not null && !r.Range.StartsWith("bytes=0-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WrongTokenFailsTheArtifact()
    {
        Ffmpeg.RequireOrSkip();
        await using var cove = await FakeCove.StartAsync(clips.Clip30);
        using var tmp = new TempDir();
        var source = MediaSource.ForCove(cove.Url, "wrong", 0);

        Assert.Equal(0, await MediaProbe.DurationAsync(MediaContext.Default, source, Ct));
        var ex = await Assert.ThrowsAsync<MediaException>(() =>
            CoverGenerator.GenerateAsync(MediaContext.Default, source, 30, Specs.Cover(), tmp.Path, tmp["cover.jpg"], Ct));
        Assert.StartsWith("cover:", ex.Message);
        Assert.Equal(ErrorCodes.GenerationFailed, ex.Code);
        Assert.False(File.Exists(tmp["cover.jpg"]));
        Assert.All(cove.Requests, r => Assert.False(r.Authorized));
    }
}
