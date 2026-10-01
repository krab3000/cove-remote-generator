using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Media;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>
/// The phash port against values Cove's own FingerprintService produced for the same images (contract/parity, "phash").
/// The images are regenerated here exactly as they were when the golden values were taken.
/// </summary>
public class PhashTests(ClipFixture clips) : IClassFixture<ClipFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static Image<Rgba32> TestImage(int kind, int w, int h)
    {
        var img = new Image<Rgba32>(w, h);
        var rng = new Random(1234 + kind);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            img[x, y] = kind switch
            {
                0 => new Rgba32((byte)(x * 255 / (w - 1)), (byte)(y * 255 / (h - 1)), (byte)((x + y) % 256), 255),
                1 => new Rgba32((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 255),
                _ => ((x / 40) + (y / 30)) % 2 == 0 ? new Rgba32(230, 40, 40, 255) : new Rgba32(20, 60, 200, 255),
            };
        }
        return img;
    }

    [Fact]
    public void Perception_hash_matches_coves()
    {
        foreach (var c in Parity.Root["phash"]!["images"]!.AsArray())
        {
            using var image = TestImage((int)c!["kind"]!, (int)c["width"]!, (int)c["height"]!);
            Assert.Equal((string)c["expected"]!, PhashGenerator.PerceptionHash(image));
        }
    }

    [Fact]
    public void Frame_grid_hash_matches_coves()
    {
        var c = Parity.Root["phash"]!["grid"]!;
        int count = (int)c["frames"]!, w = (int)c["frameWidth"]!, h = (int)c["frameHeight"]!;
        var frames = Enumerable.Range(0, count).Select(i =>
        {
            var f = new Image<Rgba32>(w, h);
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                f[x, y] = new Rgba32((byte)((x * 7 + i * 31) % 256), (byte)((y * 5 + i * 17) % 256), (byte)((x + y + i * 11) % 256), 255);
            return f;
        }).ToList();
        try
        {
            Assert.Equal((string)c["expected"]!, PhashGenerator.HashGrid(frames, 5));
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void Timestamps_cover_the_middle_ninety_percent()
    {
        var t = PhashGenerator.Timestamps(100, 25);
        Assert.Equal(25, t.Count);
        Assert.Equal(5.0, t[0], 9);
        Assert.Equal(3.6, t[1] - t[0], 9);
        Assert.Equal(5.0 + 24 * 3.6, t[^1], 9);
    }

    [Fact]
    public async Task Hashes_a_real_video_deterministically()
    {
        Ffmpeg.RequireOrSkip();
        var source = new MediaSource(clips.Clip30, [], new FileInfo(clips.Clip30).Length);
        using var dir = new TempDir();

        var first = await PhashGenerator.GenerateAsync(Engines.Cli, source, 30, new PhashSpec(), Path.Combine(dir.Path, "a"), Ct);
        var second = await PhashGenerator.GenerateAsync(Engines.Cli, source, 30, new PhashSpec(), Path.Combine(dir.Path, "b"), Ct);

        Assert.Matches("^[0-9a-f]{1,16}$", first);
        Assert.Equal(first, second);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "a", "frames")), "phash frames are deleted once loaded");
    }

    [Fact]
    public async Task A_frame_that_cannot_be_read_means_no_hash()
    {
        Ffmpeg.RequireOrSkip();
        using var dir = new TempDir();
        var missing = new MediaSource(Path.Combine(dir.Path, "missing.mp4"), [], 0);

        var ex = await Assert.ThrowsAsync<MediaException>(() =>
            PhashGenerator.GenerateAsync(Engines.Cli, missing, 30, new PhashSpec(), dir.Path, Ct));
        Assert.Contains("25 of 25", ex.Message);
    }
}
