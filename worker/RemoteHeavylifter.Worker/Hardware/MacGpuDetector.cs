using Microsoft.Extensions.Logging;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>
/// The Mac's media engine: it encodes previews (h264_videotoolbox) and decodes nothing, so the slot reports no decoders
/// and the extension sends it previews only. VideoToolbox decoding was several times slower than the CPU's
/// multithreaded decoder (a 1080p H.264 video on an M1 Pro: 49 s against 9.3 s for all four artifacts).
/// </summary>
public sealed class MacGpuDetector : IGpuDetector
{
    public IReadOnlyList<GpuDevice> Detect(ILogger log) =>
        [new GpuDevice(GpuVendors.Apple, 0, "-", "Apple media engine", "videotoolbox", null, "h264_videotoolbox", null, null, [])];
}
