using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>
/// The DXGI adapters, and what an Intel one decodes: the D3D11 video decoder profiles it reports and the largest picture
/// each decodes. The adapter's DXGI index is the device Quick Sync opens (FFmpeg's qsv <c>child_device</c>) and encodes on.
/// </summary>
internal static unsafe partial class D3d11Probe
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DriverTypeUnknown = 0;
    private const uint CreateDeviceVideoSupport = 0x800;
    private const uint SdkVersion = 7;
    private const int FormatNv12 = 103;
    private const int FormatP010 = 104;

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidVideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");

    /// <summary>D3D11_DECODER_PROFILE_* GUIDs, the libav decoders they serve, and their output format.</summary>
    internal static readonly (Guid Profile, string[] Codecs, int Format)[] Profiles =
    [
        (new("ee27417f-5e28-4e65-beea-1d26b508adc9"), ["mpeg2video"], FormatNv12),   // MPEG2_VLD
        (new("86695f12-340e-4f04-9fd3-9253dd327460"), ["mpeg2video"], FormatNv12),   // MPEG2and1_VLD
        (new("efd64d74-c9e8-41d7-a5e9-e9b0e39fa319"), ["mpeg4"], FormatNv12),        // MPEG4PT2_VLD_SIMPLE
        (new("ed418a9f-010d-4eda-9ae3-9a65358d8d2e"), ["mpeg4"], FormatNv12),        // MPEG4PT2_VLD_ADVSIMPLE_NOGMC
        (new("1b81be68-a0c7-11d3-b984-00c04f2e73c5"), ["h264"], FormatNv12),         // H264_VLD_NOFGT
        (new("1b81be69-a0c7-11d3-b984-00c04f2e73c5"), ["h264"], FormatNv12),         // H264_VLD_FGT
        (new("1b81bea3-a0c7-11d3-b984-00c04f2e73c5"), ["vc1", "wmv3"], FormatNv12),  // VC1_VLD
        (new("1b81bea4-a0c7-11d3-b984-00c04f2e73c5"), ["vc1", "wmv3"], FormatNv12),  // VC1_D2010
        (new("5b11d51b-2f4c-4452-bcc3-09f2a1160cc0"), ["hevc"], FormatNv12),         // HEVC_VLD_MAIN
        (new("107af0e0-ef1a-4d19-aba8-67a163073d13"), ["hevc"], FormatP010),         // HEVC_VLD_MAIN10
        (new("90b899ea-3a62-4705-88b3-8df04b2744e7"), ["vp8"], FormatNv12),          // VP8_VLD
        (new("463707f8-a1d0-4585-876d-83aa6d60b89e"), ["vp9"], FormatNv12),          // VP9_VLD_PROFILE0
        (new("a4c749ef-6ecf-48aa-8448-50a7a1165ff7"), ["vp9"], FormatP010),          // VP9_VLD_10BIT_PROFILE2
        (new("b8be4ccb-cf53-46ba-8d59-d6b8a6da5d2a"), ["av1"], FormatNv12),          // AV1_VLD_PROFILE0
    ];

    /// <summary>Picture sizes tried for each profile, largest first: D3D11 reports no maximum, only whether a size has
    /// a decoder configuration.</summary>
    internal static readonly (int Width, int Height)[] Sizes =
        [(16384, 16384), (8192, 8192), (8192, 4320), (4096, 4096), (4096, 2304), (3840, 2160), (2048, 2048), (1920, 1088)];

    /// <summary>A hardware DXGI adapter. <paramref name="Index"/> counts every adapter, as FFmpeg's d3d11va and qsv
    /// devices do.</summary>
    internal sealed record Adapter(int Index, uint VendorId, uint DeviceId, string Name);

    public static List<Adapter> Adapters(ILogger log)
    {
        var adapters = new List<Adapter>();
        IntPtr factory;
        try
        {
            var iid = IidDxgiFactory1;
            if (CreateDxgiFactory1(&iid, &factory) < 0)
            {
                log.LogWarning("DXGI does not initialise; no GPUs found");
                return adapters;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.LogWarning("DXGI not available ({Error}); no GPUs found", ex.Message);
            return adapters;
        }
        try
        {
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                // IDXGIFactory1::EnumAdapters1
                var hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Method(factory, 12))(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0)
                    break;
                try
                {
                    AdapterDesc1 desc;
                    // IDXGIAdapter1::GetDesc1
                    if (((delegate* unmanaged<IntPtr, AdapterDesc1*, int>)Method(adapter, 10))(adapter, &desc) < 0
                        || (desc.Flags & DxgiAdapterFlagSoftware) != 0)
                        continue;
                    adapters.Add(new Adapter((int)i, desc.VendorId, desc.DeviceId, new string(desc.Description).TrimEnd('\0').Trim()));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return adapters;
    }

    /// <summary>The Intel GPU on <paramref name="adapter"/>, decoding with D3D11VA and encoding with h264_qsv there
    /// (<see cref="Tasks.SlotBuilder.ForIntelApi"/> switches decoding to Quick Sync).</summary>
    public static GpuDevice? Probe(Adapter adapter, int index, ILogger log)
    {
        var decoders = Decoders(adapter, log);
        if (decoders is null)
            return null;
        var device = adapter.Index.ToString(CultureInfo.InvariantCulture);
        return new GpuDevice(GpuVendors.Intel, index, $"dxgi {adapter.Index}", adapter.Name, "d3d11va", device, "h264_qsv", device,
            null, decoders);
    }

    private static List<DecoderCap>? Decoders(Adapter adapter, ILogger log)
    {
        var dxgiAdapter = AdapterAt(adapter.Index);
        if (dxgiAdapter == IntPtr.Zero)
        {
            log.LogWarning("{Name} (dxgi {Index}): adapter disappeared", adapter.Name, adapter.Index);
            return null;
        }
        IntPtr device = IntPtr.Zero, context = IntPtr.Zero, videoDevice = IntPtr.Zero;
        try
        {
            int level;
            if (D3d11CreateDevice(dxgiAdapter, DriverTypeUnknown, IntPtr.Zero, CreateDeviceVideoSupport, IntPtr.Zero, 0, SdkVersion,
                    &device, &level, &context) < 0)
            {
                log.LogWarning("{Name} (dxgi {Index}): cannot create a D3D11 device", adapter.Name, adapter.Index);
                return null;
            }
            var iid = IidVideoDevice;
            // IUnknown::QueryInterface
            if (((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Method(device, 0))(device, &iid, &videoDevice) < 0)
            {
                log.LogWarning("{Name} (dxgi {Index}): no D3D11 video decoding", adapter.Name, adapter.Index);
                return null;
            }

            // ID3D11VideoDevice::GetVideoDecoderProfileCount, GetVideoDecoderProfile, GetVideoDecoderConfigCount
            var profileCount = ((delegate* unmanaged<IntPtr, uint>)Method(videoDevice, 11))(videoDevice);
            var supported = new HashSet<Guid>();
            for (uint i = 0; i < profileCount; i++)
            {
                Guid profile;
                if (((delegate* unmanaged<IntPtr, uint, Guid*, int>)Method(videoDevice, 12))(videoDevice, i, &profile) >= 0)
                    supported.Add(profile);
            }

            var caps = new Dictionary<string, DecoderCap>(StringComparer.Ordinal);
            foreach (var (profile, codecs, format) in Profiles)
            {
                if (!supported.Contains(profile) || LargestSize(videoDevice, profile, format) is not var (width, height))
                    continue;
                // Several profiles serve one codec: keep the largest size.
                foreach (var codec in codecs)
                    caps[codec] = caps.TryGetValue(codec, out var known)
                        ? known with { MaxWidth = Math.Max(known.MaxWidth, width), MaxHeight = Math.Max(known.MaxHeight, height) }
                        : new DecoderCap(codec, width, height);
            }
            return caps.Values.OrderBy(c => c.Codec, StringComparer.Ordinal).ToList();
        }
        finally
        {
            Release(videoDevice);
            Release(context);
            Release(device);
            Release(dxgiAdapter);
        }
    }

    private static (int Width, int Height)? LargestSize(IntPtr videoDevice, Guid profile, int format)
    {
        foreach (var (width, height) in Sizes)
        {
            var desc = new VideoDecoderDesc { Guid = profile, SampleWidth = (uint)width, SampleHeight = (uint)height, OutputFormat = format };
            uint configs;
            if (((delegate* unmanaged<IntPtr, VideoDecoderDesc*, uint*, int>)Method(videoDevice, 14))(videoDevice, &desc, &configs) >= 0
                && configs > 0)
                return (width, height);
        }
        return null;
    }

    /// <summary>The DXGI adapter at <paramref name="index"/> (caller releases), or zero.</summary>
    private static IntPtr AdapterAt(int index)
    {
        IntPtr factory, adapter = IntPtr.Zero;
        var iid = IidDxgiFactory1;
        if (CreateDxgiFactory1(&iid, &factory) < 0)
            return IntPtr.Zero;
        try
        {
            var hr = ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Method(factory, 12))(factory, (uint)index, &adapter);
            return hr < 0 ? IntPtr.Zero : adapter;
        }
        finally
        {
            Release(factory);
        }
    }

    /// <summary>A COM method: slot <paramref name="slot"/> of the object's vtable.</summary>
    private static void* Method(IntPtr com, int slot) => (*(void***)com)[slot];

    private static void Release(IntPtr com)
    {
        if (com != IntPtr.Zero)
            ((delegate* unmanaged<IntPtr, uint>)Method(com, 2))(com);
    }

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    /// <summary>D3D11_VIDEO_DECODER_DESC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VideoDecoderDesc
    {
        public Guid Guid;
        public uint SampleWidth;
        public uint SampleHeight;
        public int OutputFormat;
    }

    [LibraryImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
    private static partial int CreateDxgiFactory1(Guid* riid, IntPtr* factory);

    [LibraryImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
    private static partial int D3d11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* context);
}
