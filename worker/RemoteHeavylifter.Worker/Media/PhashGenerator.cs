using System.Globalization;
using RemoteHeavylifter.Protocol;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RemoteHeavylifter.Worker.Media;

/// <summary>
/// Cove's video perceptual hash (FingerprintService.ComputeVideoPhashAsync / BuildSpritePhash / ComputePerceptionHash),
/// itself a port of Stash's videophash + goimagehash. The frame timestamps, grid, resampler, luminance weights, DCT and
/// median all define the hash: any change makes these hashes stop matching the ones Cove computes, so this is a
/// line-for-line port, not a reimplementation.
/// </summary>
public static class PhashGenerator
{
    private const int DctImageSize = 64;
    private const int DctLowFreqSize = 8;

    /// <summary>Evenly over the middle 90%: 5% offset, then 0.9·duration / count apart.</summary>
    public static IReadOnlyList<double> Timestamps(double duration, int count)
    {
        var offset = 0.05 * duration;
        var step = 0.9 * duration / count;
        return Enumerable.Range(0, count).Select(i => offset + i * step).ToList();
    }

    public static async Task<string> GenerateAsync(
        IMediaEngine engine, MediaSource src, double duration, PhashSpec spec, string workDir, CancellationToken ct)
    {
        if (duration <= 0)
            throw new MediaException("phash: unknown duration");
        var columns = (int)Math.Sqrt(spec.FrameCount);
        if (columns * columns != spec.FrameCount)
            throw new MediaException($"phash: {spec.FrameCount} frames do not form a square grid");

        Directory.CreateDirectory(workDir);
        // No VR pre-filter: Cove hashes the frame as stored, so a VR video's hash covers both eyes like Cove's does.
        var decoded = await engine.ExtractFramesAsync(src, Timestamps(duration, spec.FrameCount), spec.FrameWidth, preFilter: null, workDir, ct);
        var frames = new Image<Rgba32>?[decoded.Length];
        try
        {
            // Unlike a sprite, a hash cannot borrow a neighbouring frame: that would give a hash no other extraction of
            // the same file reproduces. Any missing frame means no hash (as in Cove).
            var missing = decoded.Count(f => f is null);
            if (missing > 0)
                throw new MediaException($"phash: {missing} of {decoded.Length} sample frames could not be decoded");
            for (var i = 0; i < decoded.Length; i++)
                frames[i] = decoded[i]!.CloneAs<Rgba32>();
            return HashGrid(frames!, columns);
        }
        finally
        {
            foreach (var frame in decoded)
                frame?.Dispose();
            foreach (var frame in frames)
                frame?.Dispose();
        }
    }

    /// <summary>Lays the frames out in a <paramref name="columns"/>-wide grid (row-major) and hashes the grid.</summary>
    internal static string HashGrid(IReadOnlyList<Image<Rgba32>> frames, int columns)
    {
        var rows = (frames.Count + columns - 1) / columns;
        var frameWidth = frames[0].Width;
        var frameHeight = frames[0].Height;
        using var grid = new Image<Rgba32>(frameWidth * columns, frameHeight * rows);
        for (var index = 0; index < frames.Count; index++)
        {
            var x = frameWidth * (index % columns);
            // Cove divides by its row count here; for its square grid that is the same as the column count.
            var y = frameHeight * (int)Math.Floor((double)index / rows);
            var frame = frames[index];
            grid.Mutate(g => g.DrawImage(frame, new Point(x, y), 1f));
        }
        return PerceptionHash(grid);
    }

    /// <summary>
    /// goimagehash's PerceptionHash: 64×64 bilinear resize, BT.601 luminance, 2D DCT (Lee 1984), the top-left 8×8
    /// coefficients, bits set MSB-first where above their median. Hex without leading zeros, like Go's <c>%x</c>.
    /// </summary>
    internal static string PerceptionHash(Image<Rgba32> image)
    {
        image.Mutate(g => g.Resize(new ResizeOptions
        {
            Size = new Size(DctImageSize, DctImageSize),
            Sampler = KnownResamplers.Triangle,
            Mode = ResizeMode.Stretch,
        }));

        var pixels = new double[DctImageSize * DctImageSize];
        for (var y = 0; y < DctImageSize; y++)
        {
            for (var x = 0; x < DctImageSize; x++)
            {
                var px = image[x, y];
                pixels[y * DctImageSize + x] = 0.299 * px.R + 0.587 * px.G + 0.114 * px.B;
            }
        }

        Dct2DInPlace64(pixels);

        var low = new double[DctLowFreqSize * DctLowFreqSize];
        for (var i = 0; i < DctLowFreqSize; i++)
        {
            for (var j = 0; j < DctLowFreqSize; j++)
                low[DctLowFreqSize * i + j] = pixels[i * DctImageSize + j];
        }

        var median = Median(low);
        ulong hash = 0;
        for (var idx = 0; idx < low.Length; idx++)
        {
            if (low[idx] > median)
                hash |= 1UL << (63 - idx);
        }
        return hash.ToString("x", CultureInfo.InvariantCulture);
    }

    private static void Dct2DInPlace64(double[] pixels)
    {
        for (var i = 0; i < DctImageSize; i++)
            Dct1DInPlace64(pixels.AsSpan(i * DctImageSize, DctImageSize));

        Span<double> column = stackalloc double[DctImageSize];
        for (var i = 0; i < DctImageSize; i++)
        {
            for (var j = 0; j < DctImageSize; j++)
                column[j] = pixels[i + j * DctImageSize];
            Dct1DInPlace64(column);
            for (var j = 0; j < DctImageSize; j++)
                pixels[i + j * DctImageSize] = column[j];
        }
    }

    private static void Dct1DInPlace64(Span<double> input) => ForwardTransform(input, stackalloc double[64], 64);

    private static void ForwardTransform(Span<double> input, Span<double> temp, int len)
    {
        if (len == 1)
            return;

        var halfLen = len / 2;
        for (var i = 0; i < halfLen; i++)
        {
            double x = input[i], y = input[len - 1 - i];
            temp[i] = x + y;
            temp[i + halfLen] = (x - y) / (Math.Cos((i + 0.5) * Math.PI / len) * 2);
        }

        ForwardTransform(temp, input, halfLen);
        ForwardTransform(temp.Slice(halfLen), input, halfLen);

        for (var i = 0; i < halfLen - 1; i++)
        {
            input[i * 2] = temp[i];
            input[i * 2 + 1] = temp[i + halfLen] + temp[i + halfLen + 1];
        }
        input[len - 2] = temp[halfLen - 1];
        input[len - 1] = temp[len - 1];
    }

    /// <summary>Go's MedianOfPixelsFast64: quickselect to len/2, averaging the two middle values for an even length.</summary>
    private static double Median(double[] input)
    {
        var tmp = (double[])input.Clone();
        var pos = tmp.Length / 2;
        QuickSelect(tmp, 0, tmp.Length - 1, pos);
        return tmp.Length % 2 == 0 ? tmp[pos - 1] / 2 + tmp[pos] / 2 : tmp[pos];
    }

    private static void QuickSelect(double[] seq, int low, int hi, int k)
    {
        if (low == hi)
            return;

        while (low < hi)
        {
            var pivot = low / 2 + hi / 2;
            var pivotValue = seq[pivot];
            var storeIdx = low;
            (seq[pivot], seq[hi]) = (seq[hi], seq[pivot]);
            for (var i = low; i < hi; i++)
            {
                if (seq[i] < pivotValue)
                {
                    (seq[storeIdx], seq[i]) = (seq[i], seq[storeIdx]);
                    storeIdx++;
                }
            }
            (seq[hi], seq[storeIdx]) = (seq[storeIdx], seq[hi]);

            if (k <= storeIdx)
                hi = storeIdx;
            else
                low = storeIdx + 1;
        }
    }
}
