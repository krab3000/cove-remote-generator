using RemoteHeavylifter.Worker.Media;
using SixLabors.ImageSharp;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Ported from the former Python server's test_ffmpeg_integration.py, reading local files (ffmpeg treats a path as a URL).</summary>
public class FfmpegIntegrationTests(ClipFixture clips) : IClassFixture<ClipFixture>
{
    private static readonly MediaContext Ctx = MediaContext.Default;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProbeAndCover()
    {
        Ffmpeg.RequireOrSkip();
        using var tmp = new TempDir();
        var source = Specs.Local(clips.Clip30, new FileInfo(clips.Clip30).Length);
        Assert.Equal(30, await MediaProbe.DurationAsync(Ctx, source, Ct), 0.1);
        var output = tmp["cover.jpg"];
        await CoverGenerator.GenerateAsync(Ctx, source, 30, Specs.Cover(), tmp.Path, output, Ct);
        var info = Image.Identify(output);
        Assert.Equal((320, 180), (info.Width, info.Height));
    }

    [Fact]
    public async Task CoverFilterFallsBack()
    {
        Ffmpeg.RequireOrSkip();
        using var tmp = new TempDir();
        var source = Specs.Local(clips.Clip30, 1);
        var output = tmp["cover.jpg"];
        var spec = Specs.Cover(filter: "definitely_not_a_filter=1");
        await CoverGenerator.GenerateAsync(Ctx, source, 30, spec, tmp.Path, output, Ct);
        Assert.True(new FileInfo(output).Length > 0);
        await Assert.ThrowsAsync<MediaException>(() =>
            CoverGenerator.GenerateAsync(Ctx, source, 30, spec with { FallbackWithoutFilter = false }, tmp.Path, output + "2", Ct));
    }

    [Fact]
    public async Task SplicedPreview()
    {
        Ffmpeg.RequireOrSkip();
        using var tmp = new TempDir();
        var output = tmp["preview.mp4"];
        await PreviewGenerator.GenerateAsync(Ctx, Specs.Local(clips.Clip30), 30, Specs.Preview(preset: "ultrafast"), tmp.Path, output, Ct);
        var (width, height, duration) = Ffmpeg.ProbeStream(output);
        Assert.Equal((640, 360), (width, height));
        Assert.Equal(12 * 0.75, duration, 0.3);
    }

    [Fact]
    public async Task ShortPreviewAndAudioChunks()
    {
        Ffmpeg.RequireOrSkip();
        using var tmp = new TempDir();
        var shortClip = Ffmpeg.MakeClip(tmp["2s.mp4"], 2);
        var output = tmp["short.mp4"];
        await PreviewGenerator.GenerateAsync(Ctx, Specs.Local(shortClip), 2, Specs.Preview(preset: "ultrafast"), tmp.Path, output, Ct);
        Assert.Equal(2, Ffmpeg.ProbeStream(output).Duration, 0.3);

        var loud = Ffmpeg.MakeClip(tmp["12s.mp4"], 12, audio: true);
        output = tmp["audio.mp4"];
        var spec = Specs.Preview(preset: "ultrafast", audio: true, segments: 4);
        await PreviewGenerator.GenerateAsync(Ctx, Specs.Local(loud), 12, spec, tmp["w"], output, Ct);
        Assert.Equal(4 * 0.75, Ffmpeg.ProbeStream(output).Duration, 0.4);
    }

    [Fact]
    public async Task SpriteAndVtt()
    {
        Ffmpeg.RequireOrSkip();
        using var tmp = new TempDir();
        string sprite = tmp["s.jpg"], vtt = tmp["t.vtt"];
        await SpriteGenerator.GenerateAsync(Ctx, Specs.Local(clips.Clip30), 30, Specs.Sprite("9_sprite.jpg"), tmp.Path, sprite, vtt, Ct);
        var info = Image.Identify(sprite);
        Assert.Equal((160 * 4, 90 * 4), (info.Width, info.Height));
        var text = await File.ReadAllTextAsync(vtt, Ct);
        Assert.StartsWith("WEBVTT\n\n00:00:00.000 --> 00:00:02.000\n9_sprite.jpg#xywh=0,0,160,90\n", text);
        Assert.Equal(15, text.Split("#xywh=").Length - 1);
    }

    [Fact]
    public async Task TinyClipSprite()
    {
        Ffmpeg.RequireOrSkip();
        using var tmp = new TempDir();
        var tiny = Ffmpeg.MakeClip(tmp["half.mp4"], 0.5);
        string sprite = tmp["s.jpg"], vtt = tmp["t.vtt"];
        await SpriteGenerator.GenerateAsync(Ctx, Specs.Local(tiny), 0.5, Specs.Sprite("1_sprite.jpg"), tmp.Path, sprite, vtt, Ct);
        var info = Image.Identify(sprite);
        Assert.Equal((160, 90), (info.Width, info.Height));
    }

    [Fact]
    public async Task FfmpegVersion()
    {
        Ffmpeg.RequireOrSkip();
        Assert.StartsWith("ffmpeg version", await MediaProbe.FfmpegVersionAsync(Ctx, Ct));
        Assert.Null(await MediaProbe.FfmpegVersionAsync(Ctx with { Ffmpeg = "/nonexistent/ffmpeg" }, Ct));
    }
}
