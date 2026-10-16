using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Protocol;

namespace RemoteHeavylifter.Worker.Hardware;

/// <summary>What an Intel GPU decodes, from VA-API on its render node: the profiles with a decode (VLD) entrypoint and
/// their maximum picture size.</summary>
internal static partial class VaapiProbe
{
    private const int VaStatusSuccess = 0;
    private const int VaEntrypointVld = 1;
    private const int VaConfigAttribMaxPictureWidth = 18;
    private const int VaConfigAttribMaxPictureHeight = 19;
    private const uint VaAttribNotSupported = 0x80000000;
    private const int ORdWr = 2;

    /// <summary>VAProfile values (va.h) and the libav decoders they serve.</summary>
    internal static string? Codec(int profile) => profile switch
    {
        0 or 1 => "mpeg2video",
        2 or 3 or 4 => "mpeg4",
        6 or 7 or 13 or 36 or 40 or 41 => "h264",
        8 or 9 => "wmv3", // VC-1 Simple/Main are WMV3 in libav
        10 => "vc1",
        12 => "mjpeg",
        14 => "vp8",
        17 or 18 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 34 => "hevc",
        19 or 20 or 21 or 22 => "vp9",
        32 or 33 or 39 => "av1",
        37 or 38 => "vvc",
        _ => null,
    };

    public static GpuDevice? Probe(string renderNode, string pci, string name, int? engines, int index, ILogger log)
    {
        int fd;
        try
        {
            fd = Open(renderNode, ORdWr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.LogWarning("{Node}: cannot open ({Error})", renderNode, ex.Message);
            return null;
        }
        if (fd < 0)
        {
            log.LogWarning("{Node} ({Name}): cannot open the render node; is this user in the render group?", renderNode, name);
            return null;
        }
        try
        {
            var decoders = Decoders(fd, renderNode, log);
            if (decoders is null)
                return null;
            return new GpuDevice(GpuVendors.Intel, index, pci, name, "vaapi", renderNode, "h264_vaapi", renderNode, engines, decoders);
        }
        finally
        {
            Close(fd);
        }
    }

    private static List<DecoderCap>? Decoders(int fd, string renderNode, ILogger log)
    {
        IntPtr display;
        try
        {
            display = GetDisplayDrm(fd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.LogWarning("{Node}: libva not available ({Error})", renderNode, ex.Message);
            return null;
        }
        if (display == IntPtr.Zero || Initialize(display, out _, out _) != VaStatusSuccess)
        {
            log.LogWarning("{Node}: VA-API does not initialise (no Intel media driver?)", renderNode);
            return null;
        }
        try
        {
            var profiles = new int[Math.Max(MaxNumProfiles(display), 1)];
            if (QueryConfigProfiles(display, profiles, out var profileCount) != VaStatusSuccess)
                return [];
            var entrypoints = new int[Math.Max(MaxNumEntrypoints(display), 1)];
            var caps = new Dictionary<string, DecoderCap>(StringComparer.Ordinal);
            foreach (var profile in profiles.Take(profileCount))
            {
                if (Codec(profile) is not { } codec
                    || QueryConfigEntrypoints(display, profile, entrypoints, out var entrypointCount) != VaStatusSuccess
                    || !entrypoints.Take(entrypointCount).Contains(VaEntrypointVld))
                    continue;
                VaConfigAttrib[] attribs = [new(VaConfigAttribMaxPictureWidth, 0), new(VaConfigAttribMaxPictureHeight, 0)];
                var ok = GetConfigAttributes(display, profile, VaEntrypointVld, attribs, attribs.Length) == VaStatusSuccess;
                var width = ok && attribs[0].Value is > 0 and not VaAttribNotSupported ? (int)attribs[0].Value : 4096;
                var height = ok && attribs[1].Value is > 0 and not VaAttribNotSupported ? (int)attribs[1].Value : 4096;
                // Several profiles map to one codec: keep the largest size.
                caps[codec] = caps.TryGetValue(codec, out var known)
                    ? known with { MaxWidth = Math.Max(known.MaxWidth, width), MaxHeight = Math.Max(known.MaxHeight, height) }
                    : new DecoderCap(codec, width, height);
            }
            return caps.Values.OrderBy(c => c.Codec, StringComparer.Ordinal).ToList();
        }
        finally
        {
            Terminate(display);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VaConfigAttrib(int type, uint value)
    {
        public int Type = type;
        public uint Value = value;
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("libva-drm.so.2", EntryPoint = "vaGetDisplayDRM")]
    private static partial IntPtr GetDisplayDrm(int fd);

    [LibraryImport("libva.so.2", EntryPoint = "vaInitialize")]
    private static partial int Initialize(IntPtr display, out int major, out int minor);

    [LibraryImport("libva.so.2", EntryPoint = "vaTerminate")]
    private static partial int Terminate(IntPtr display);

    [LibraryImport("libva.so.2", EntryPoint = "vaMaxNumProfiles")]
    private static partial int MaxNumProfiles(IntPtr display);

    [LibraryImport("libva.so.2", EntryPoint = "vaMaxNumEntrypoints")]
    private static partial int MaxNumEntrypoints(IntPtr display);

    [LibraryImport("libva.so.2", EntryPoint = "vaQueryConfigProfiles")]
    private static partial int QueryConfigProfiles(IntPtr display, [Out] int[] profiles, out int count);

    [LibraryImport("libva.so.2", EntryPoint = "vaQueryConfigEntrypoints")]
    private static partial int QueryConfigEntrypoints(IntPtr display, int profile, [Out] int[] entrypoints, out int count);

    [LibraryImport("libva.so.2", EntryPoint = "vaGetConfigAttributes")]
    private static partial int GetConfigAttributes(IntPtr display, int profile, int entrypoint, [In, Out] VaConfigAttrib[] attribs, int count);
}
