using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Media;
using SixLabors.ImageSharp;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Serves a clip the way Cove does (token header, range requests) and generates from the URL.</summary>
public class HttpSourceTests(ClipFixture clips) : IClassFixture<ClipFixture>
{
    private const string Token = FakeCove.Token;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CoverAndSpriteFromAuthenticatedUrl()
    {
        Ffmpeg.RequireOrSkip();
        await using var cove = await FakeCove.StartAsync(clips.Clip30);
        using var tmp = new TempDir();
        var source = MediaSource.ForCove(cove.Url, Token, new FileInfo(clips.Clip30).Length);
        var ctx = Engines.Cli;

        Assert.Equal(30, await ctx.ProbeDurationAsync(source, Ct), 0.1);

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

        Assert.Equal(0, await Engines.Cli.ProbeDurationAsync(source, Ct));
        var ex = await Assert.ThrowsAsync<MediaException>(() =>
            CoverGenerator.GenerateAsync(Engines.Cli, source, 30, Specs.Cover(), tmp.Path, tmp["cover.jpg"], Ct));
        Assert.StartsWith("cover:", ex.Message);
        Assert.Equal(ErrorCodes.GenerationFailed, ex.Code);
        Assert.False(File.Exists(tmp["cover.jpg"]));
        Assert.All(cove.Requests, r => Assert.False(r.Authorized));
    }

    [Fact]
    public async Task CachedSourceGivesTheSameOutputsWithFewerRequests()
    {
        Ffmpeg.RequireOrSkip();
        var size = new FileInfo(clips.Clip30).Length;
        using var tmp = new TempDir();
        var ctx = Engines.Cli;

        await using var direct = await FakeCove.StartAsync(clips.Clip30);
        var directSource = MediaSource.ForCove(direct.Url, Token, size);
        await CoverGenerator.GenerateAsync(ctx, directSource, 30, Specs.Cover(), tmp["d"], tmp["d.jpg"], Ct);
        await SpriteGenerator.GenerateAsync(ctx, directSource, 30, Specs.Sprite("9_sprite.jpg"), tmp["d"], tmp["d_s.jpg"], tmp["d.vtt"], Ct);

        await using var cove = await FakeCove.StartAsync(clips.Clip30);
        await using var host = await CacheHost.StartAsync(1 << 30);
        using (var cached = await host.Cache.OpenAsync(cove.Url, size, Ct))
        {
            var source = MediaSource.ForLocal(cached!.LocalUrl, size);
            await CoverGenerator.GenerateAsync(ctx, source, 30, Specs.Cover(), tmp["c"], tmp["c.jpg"], Ct);
            var afterCover = cached.Stats;
            await SpriteGenerator.GenerateAsync(ctx, source, 30, Specs.Sprite("9_sprite.jpg"), tmp["c"], tmp["c_s.jpg"], tmp["c.vtt"], Ct);

            var sprite = cached.Stats - afterCover;
            Assert.Equal((1, 1), (afterCover.Misses, afterCover.Fetches));
            Assert.Equal(0, sprite.Misses);
            Assert.Equal(0, sprite.BytesFetched);
            Assert.True(sprite.Hits > 0 && sprite.Reads > 0 && sprite.BytesServed > 0);
        }

        Assert.Equal(Outputs.Sha256File(tmp["d.jpg"]), Outputs.Sha256File(tmp["c.jpg"]));
        Assert.Equal(Outputs.Sha256File(tmp["d_s.jpg"]), Outputs.Sha256File(tmp["c_s.jpg"]));
        Assert.Equal(await File.ReadAllTextAsync(tmp["d.vtt"], Ct), await File.ReadAllTextAsync(tmp["c.vtt"], Ct));
        Assert.All(cove.Requests, r => Assert.True(r.Authorized));
        // The whole clip fits in one read-ahead run: one upstream request instead of one per ffmpeg input.
        Assert.Single(cove.Requests);
        Assert.True(direct.Requests.Count > 10, $"{direct.Requests.Count} direct requests");
        Assert.Equal(0, host.Cache.CachedBytes);
    }
}
