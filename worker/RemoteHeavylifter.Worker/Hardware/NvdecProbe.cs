using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>What an NVIDIA GPU decodes, from NVDEC (cuvidGetDecoderCaps) in a CUDA context on it. Its CUDA ordinal,
/// found by PCI address, is the device ffmpeg's cuda hwaccel and nvenc take.</summary>
internal static unsafe partial class NvdecProbe
{
    private const int CudaSuccess = 0;
    private const string Cuda = "libcuda.so.1";
    private const string Nvcuvid = "libnvcuvid.so.1";

    // The driver's libraries are nvcuda.dll and nvcuvid.dll on Windows.
    static NvdecProbe()
    {
        if (OperatingSystem.IsWindows())
            NativeLibrary.SetDllImportResolver(typeof(NvdecProbe).Assembly, (name, assembly, path) => name switch
            {
                Cuda => NativeLibrary.Load("nvcuda.dll", assembly, path),
                Nvcuvid => NativeLibrary.Load("nvcuvid.dll", assembly, path),
                _ => IntPtr.Zero,
            });
    }

    /// <summary>The GPUs CUDA sees, by PCI address (0000:01:00.0) and name; empty without the NVIDIA driver.</summary>
    public static List<(string Pci, string Name)> Devices(ILogger log)
    {
        var devices = new List<(string, string)>();
        try
        {
            if (CuInit(0) != CudaSuccess || CuDeviceGetCount(out var count) != CudaSuccess)
                return devices;
            var buffer = stackalloc byte[256];
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                if (CuDeviceGet(out var device, ordinal) != CudaSuccess || CuDeviceGetPciBusId(buffer, 256, device) != CudaSuccess)
                    continue;
                var pci = Marshal.PtrToStringUTF8((IntPtr)buffer)!.ToLowerInvariant();
                var name = CuDeviceGetName(buffer, 256, device) == CudaSuccess ? Marshal.PtrToStringUTF8((IntPtr)buffer)! : $"NVIDIA GPU ({pci})";
                devices.Add((pci, name));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.LogDebug("No NVIDIA driver ({Error})", ex.Message);
        }
        return devices;
    }

    /// <summary>cudaVideoCodec values (cuviddec.h: MPEG1=0 … JPEG=5, H264_SVC=6, H264_MVC=7, HEVC=8 …) and the libav
    /// decoders they serve.</summary>
    internal static readonly (int Codec, string[] Names)[] Codecs =
    [
        (0, ["mpeg1video"]), (1, ["mpeg2video"]), (2, ["mpeg4"]), (3, ["vc1", "wmv3"]), (4, ["h264"]), (5, ["mjpeg"]),
        (8, ["hevc"]), (9, ["vp8"]), (10, ["vp9"]), (11, ["av1"]),
    ];

    public static GpuDevice? Probe(string pci, string name, int index, ILogger log)
    {
        try
        {
            if (CuInit(0) != CudaSuccess)
            {
                log.LogWarning("{Name} ({Pci}): CUDA does not initialise", name, pci);
                return null;
            }
            if (CuDeviceGetByPciBusId(out var ordinal, pci) != CudaSuccess)
            {
                log.LogWarning("{Name} ({Pci}): CUDA does not know this GPU", name, pci);
                return null;
            }
            if (CuCtxCreate(out var context, 0, ordinal) != CudaSuccess)
            {
                log.LogWarning("{Name} ({Pci}): cannot create a CUDA context", name, pci);
                return null;
            }
            try
            {
                var decoders = new List<DecoderCap>();
                int? engines = null;
                foreach (var (codec, names) in Codecs)
                {
                    var caps = new DecodeCaps { CodecType = codec, ChromaFormat = 1 /* 4:2:0 */, BitDepthMinus8 = 0 };
                    if (CuvidGetDecoderCaps(ref caps) != CudaSuccess || caps.IsSupported == 0)
                        continue;
                    engines = Math.Max(engines ?? 0, caps.NumNvdecs);
                    decoders.AddRange(names.Select(n => new DecoderCap(n, (int)caps.MaxWidth, (int)caps.MaxHeight)));
                }
                var device = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new GpuDevice(GpuVendors.Nvidia, index, pci, name, "cuda", device, "h264_nvenc", device, engines,
                    decoders.OrderBy(d => d.Codec, StringComparer.Ordinal).ToList());
            }
            finally
            {
                CuCtxDestroy(context);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.LogWarning("{Name} ({Pci}): NVIDIA driver libraries not available ({Error})", name, pci, ex.Message);
            return null;
        }
    }

    /// <summary>CUVIDDECODECAPS (88 bytes).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DecodeCaps
    {
        public int CodecType;
        public int ChromaFormat;
        public uint BitDepthMinus8;
        public fixed uint Reserved1[3];
        public byte IsSupported;
        public byte NumNvdecs;
        public ushort OutputFormatMask;
        public uint MaxWidth;
        public uint MaxHeight;
        public uint MaxMbCount;
        public ushort MinWidth;
        public ushort MinHeight;
        public byte IsHistogramSupported;
        public byte CounterBitDepth;
        public ushort MaxHistogramBins;
        public byte IsDecodeStatsSupported;
        public fixed byte Reserved4[3];
        public fixed uint Reserved3[9];
    }

    [LibraryImport(Cuda, EntryPoint = "cuInit")]
    private static partial int CuInit(uint flags);

    [LibraryImport(Cuda, EntryPoint = "cuDeviceGetByPCIBusId", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CuDeviceGetByPciBusId(out int device, string pciBusId);

    [LibraryImport(Cuda, EntryPoint = "cuDeviceGetCount")]
    private static partial int CuDeviceGetCount(out int count);

    [LibraryImport(Cuda, EntryPoint = "cuDeviceGet")]
    private static partial int CuDeviceGet(out int device, int ordinal);

    [LibraryImport(Cuda, EntryPoint = "cuDeviceGetPCIBusId")]
    private static partial int CuDeviceGetPciBusId(byte* pciBusId, int length, int device);

    [LibraryImport(Cuda, EntryPoint = "cuDeviceGetName")]
    private static partial int CuDeviceGetName(byte* name, int length, int device);

    [LibraryImport(Cuda, EntryPoint = "cuCtxCreate_v2")]
    private static partial int CuCtxCreate(out IntPtr context, uint flags, int device);

    [LibraryImport(Cuda, EntryPoint = "cuCtxDestroy_v2")]
    private static partial int CuCtxDestroy(IntPtr context);

    [LibraryImport(Nvcuvid, EntryPoint = "cuvidGetDecoderCaps")]
    private static partial int CuvidGetDecoderCaps(ref DecodeCaps caps);
}
