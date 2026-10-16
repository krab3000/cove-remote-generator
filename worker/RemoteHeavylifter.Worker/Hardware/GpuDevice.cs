using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Hardware;

public static class GpuVendors
{
    public const string Intel = "intel";
    public const string Nvidia = "nvidia";
    public const string Apple = "apple";
}

/// <summary>A GPU the worker can decode and encode on.</summary>
/// <param name="Vendor"><see cref="GpuVendors"/>.</param>
/// <param name="Index">Its number among this vendor's GPUs, in PCI order: the gpu_idx of --hv-intel-slots / --hv-nvidia-slots.</param>
/// <param name="HwAccel">The ffmpeg hwaccel decoding on it (vaapi, cuda).</param>
/// <param name="DecodeDevice">The hwaccel's device: a render node for VA-API, a CUDA ordinal for CUDA; null: the default.</param>
/// <param name="Encoder">The preview encoder on it (h264_vaapi, h264_nvenc).</param>
/// <param name="EncoderDevice">The encoder's device, as <paramref name="DecodeDevice"/>.</param>
/// <param name="VideoEngines">Hardware video decode engines, when known (more engines decode more videos at once).</param>
/// <param name="Decoders">Codecs (libav names) it decodes, with size limits.</param>
public sealed record GpuDevice(
    string Vendor, int Index, string PciAddress, string Name, string HwAccel, string? DecodeDevice, string Encoder,
    string? EncoderDevice, int? VideoEngines, IReadOnlyList<DecoderCap> Decoders)
{
    /// <summary>The slot pool id: intel0, nvidia1 …</summary>
    public string Id => $"{Vendor}{Index}";
}

/// <summary>Finds GPUs and what they decode. One per platform; a GPU or capability it cannot query is left out with a
/// warning, never an exception.</summary>
public interface IGpuDetector
{
    IReadOnlyList<GpuDevice> Detect(ILogger log);
}

public static class GpuDetection
{
    /// <summary>The detector for this platform.</summary>
    public static IGpuDetector ForPlatform() =>
        OperatingSystem.IsLinux() ? new LinuxGpuDetector()
        : OperatingSystem.IsMacOS() ? new MacGpuDetector()
        : new UnsupportedGpuDetector();

    private sealed class UnsupportedGpuDetector : IGpuDetector
    {
        public IReadOnlyList<GpuDevice> Detect(ILogger log)
        {
            log.LogWarning("GPU detection is not implemented on {Os} yet; using CPU slots only", Environment.OSVersion.Platform);
            return [];
        }
    }
}
