using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Media.Libav;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>
/// WMV (ASF) whose simple index leaves out the preroll, as Windows Media encoders write it (entry i points at the
/// packet near i s, where FFmpeg's demuxer expects i − preroll): libav's seek then lands seconds past the target, which
/// left every short preview chunk empty ("preview: no frames") and put sprites and covers late.
/// </summary>
public sealed class AsfSeekTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    /// <summary>60 s of WMV2 at 25 fps, a keyframe every 0.48 s, written by ffmpeg (3.1 s preroll, 1 s simple index whose
    /// entry i points near i − 3.1 s), its index entries then moved 4 s later (entry i near i + 0.9 s), as in the real
    /// files.</summary>
    private string MakeWmv()
    {
        var path = _tmp["v.wmv"];
        Ffmpeg.Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=25:duration=60",
            "-c:v", "wmv2", "-g", "12", path);
        var data = File.ReadAllBytes(path);
        var patched = false;
        // Top-level ASF objects: a 16-byte GUID and a 64-bit little-endian size. The simple index object (GUID starting
        // 90 08 00 33) holds: file ID (16), interval (8), max packet count (4), entry count (4), then 6-byte entries.
        for (var pos = 0; pos + 24 <= data.Length;)
        {
            var size = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 16, 8));
            if (data[pos] == 0x90 && data[pos + 1] == 0x08 && data[pos + 2] == 0x00 && data[pos + 3] == 0x33)
            {
                const int Shift = 4, Entry = 6;
                var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 52, 4));
                var entries = data.AsSpan(pos + 56, count * Entry);
                var original = entries.ToArray();
                for (var i = 0; i < count; i++)
                    original.AsSpan(Math.Min(i + Shift, count - 1) * Entry, Entry).CopyTo(entries[(i * Entry)..]);
                patched = true;
            }
            pos += size;
        }
        Assert.True(patched, "the ASF muxer wrote no simple index");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public unsafe void Exact_seeks_land_on_the_target_frame()
    {
        LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var source = Specs.Local(MakeWmv());
        using var interrupt = new Interrupt(Ct, TimeSpan.FromSeconds(60), "test");
        using var input = LibavInput.Open(source, new DecodeSettings(1, false), interrupt, NullLogger.Instance, "test");

        foreach (var seconds in new[] { 5.0, 23.4, 41.0, 12.0, 55.2 })
        {
            var target = input.StartMicroseconds + (long)(seconds * 1_000_000);
            var frame = input.FrameAt(target, "test");
            Assert.True(frame is not null, $"no frame at {seconds} s");
            Assert.Equal(input.ToStream(target), frame->pts);
        }
    }

    [Fact]
    public async Task Previews_of_wmv_have_every_chunk()
    {
        var engine = LibavEngines.Require();
        Ffmpeg.RequireOrSkip();
        var output = _tmp["p.mp4"];
        await PreviewGenerator.GenerateAsync(engine, Specs.Local(MakeWmv()), 60, Specs.Preview(), _tmp["p"], output, Ct);
        Assert.Equal(12 * 0.75, Ffmpeg.ProbeStream(output).Duration, 0.3);
    }
}
