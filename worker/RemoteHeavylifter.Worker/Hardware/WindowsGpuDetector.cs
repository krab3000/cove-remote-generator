using Microsoft.Extensions.Logging;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>
/// Intel GPUs from DXGI, queried through D3D11 video decoding on their adapter; NVIDIA GPUs from CUDA, queried through
/// NVDEC as on Linux. Intel GPUs are numbered in DXGI adapter order, NVIDIA ones in PCI order.
/// </summary>
public sealed class WindowsGpuDetector : IGpuDetector
{
    private const uint IntelVendorId = 0x8086;

    public IReadOnlyList<GpuDevice> Detect(ILogger log)
    {
        var gpus = new List<GpuDevice>();

        var intelIndex = 0;
        foreach (var adapter in D3d11Probe.Adapters(log).Where(a => a.VendorId == IntelVendorId))
        {
            if (D3d11Probe.Probe(adapter, intelIndex, log) is not { } gpu)
                continue;
            gpus.Add(gpu);
            intelIndex++;
        }

        var nvidiaIndex = 0;
        foreach (var (pci, name) in NvdecProbe.Devices(log).OrderBy(d => d.Pci, StringComparer.Ordinal))
        {
            if (NvdecProbe.Probe(pci, name, nvidiaIndex, log) is not { } gpu)
                continue;
            gpus.Add(gpu);
            nvidiaIndex++;
        }
        return gpus;
    }
}
