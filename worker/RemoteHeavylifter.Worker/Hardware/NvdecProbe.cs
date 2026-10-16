using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>What an NVIDIA GPU decodes, from NVDEC (cuvidGetDecoderCaps) in a CUDA context on it. Its CUDA ordinal,
/// found by PCI address, is the device ffmpeg's cuda hwaccel and nvenc take.</summary>
internal static partial class NvdecProbe
{
    private const int CudaSuccess = 0;

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
    private unsafe struct DecodeCaps
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

    [LibraryImport("libcuda.so.1", EntryPoint = "cuInit")]
    private static partial int CuInit(uint flags);

    [LibraryImport("libcuda.so.1", EntryPoint = "cuDeviceGetByPCIBusId", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int CuDeviceGetByPciBusId(out int device, string pciBusId);

    [LibraryImport("libcuda.so.1", EntryPoint = "cuCtxCreate_v2")]
    private static partial int CuCtxCreate(out IntPtr context, uint flags, int device);

    [LibraryImport("libcuda.so.1", EntryPoint = "cuCtxDestroy_v2")]
    private static partial int CuCtxDestroy(IntPtr context);

    [LibraryImport("libnvcuvid.so.1", EntryPoint = "cuvidGetDecoderCaps")]
    private static partial int CuvidGetDecoderCaps(ref DecodeCaps caps);
}
