using Microsoft.Extensions.Logging;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>
/// GPUs from /sys/class/drm (render nodes, vendor, PCI address): Intel ones are queried through VA-API on their render
/// node, NVIDIA ones through CUDA and NVDEC. Each vendor's GPUs are numbered in PCI order.
/// </summary>
public sealed class LinuxGpuDetector(string sysDrm = "/sys/class/drm") : IGpuDetector
{
    private const string IntelVendorId = "0x8086";
    private const string NvidiaVendorId = "0x10de";

    public IReadOnlyList<GpuDevice> Detect(ILogger log)
    {
        var nodes = RenderNodes(sysDrm);
        var gpus = new List<GpuDevice>();
        foreach (var vendorGroup in nodes.GroupBy(n => n.Vendor))
        {
            var index = 0;
            foreach (var node in vendorGroup.OrderBy(n => n.Pci, StringComparer.Ordinal))
            {
                var gpu = vendorGroup.Key switch
                {
                    IntelVendorId => VaapiProbe.Probe(node.Path, node.Pci, IntelName(node), IntelEngines(node), index, log),
                    NvidiaVendorId => NvdecProbe.Probe(node.Pci, NvidiaName(node.Pci), index, log),
                    _ => null,
                };
                if (gpu is null)
                    continue;
                gpus.Add(gpu);
                index++;
            }
        }
        return gpus;
    }

    /// <summary>A render node with its PCI device.</summary>
    internal sealed record RenderNode(string Path, string Vendor, string DeviceId, string Pci, string SysDevice);

    internal static List<RenderNode> RenderNodes(string sysDrm)
    {
        var nodes = new List<RenderNode>();
        if (!Directory.Exists(sysDrm))
            return nodes;
        foreach (var dir in Directory.EnumerateDirectories(sysDrm, "renderD*"))
        {
            var device = Path.Combine(dir, "device");
            var vendor = Read(Path.Combine(device, "vendor"));
            if (vendor is null)
                continue;
            var sysDevice = new DirectoryInfo(device).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? device;
            nodes.Add(new RenderNode($"/dev/dri/{Path.GetFileName(dir)}", vendor, Read(Path.Combine(device, "device")) ?? "?",
                Path.GetFileName(sysDevice), sysDevice));
        }
        return nodes;
    }

    private static string IntelName(RenderNode node) => $"Intel GPU {node.DeviceId} ({node.Pci})";

    /// <summary>The video decode engines i915/xe list for this GPU's card (vcs0, vcs1 …).</summary>
    private int? IntelEngines(RenderNode node)
    {
        var drm = Path.Combine(node.SysDevice, "drm");
        if (!Directory.Exists(drm))
            return null;
        foreach (var card in Directory.EnumerateDirectories(drm, "card*"))
        {
            var engines = Path.Combine(card, "engine");
            if (Directory.Exists(engines))
                return Directory.EnumerateDirectories(engines, "vcs*").Count();
        }
        return null;
    }

    private static string NvidiaName(string pci)
    {
        var info = Read($"/proc/driver/nvidia/gpus/{pci}/information");
        var model = info?.Split('\n').FirstOrDefault(l => l.StartsWith("Model:", StringComparison.Ordinal))?["Model:".Length..].Trim();
        return string.IsNullOrEmpty(model) ? $"NVIDIA GPU ({pci})" : model;
    }

    private static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
