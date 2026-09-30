using System.Globalization;
using System.Reflection;
using Cove.Core.Entities;

namespace RemoteHeavylifter.Generation;

/// <summary>A VR video's layout, as the extension needs it for the flat one-eye filter.</summary>
public sealed record VrLayout(string Projection, int FieldOfView, string StereoMode);

/// <summary>
/// The flat, one-eye ffmpeg filter Cove uses for a VR video's cover, preview and sprite frames.
/// A port of Cove.Api's internal VrFrameFilter.OneEyeFlat (stereo output is out of scope here).
/// </summary>
public static class VrFilter
{
    private const double HorizontalFov = 100;
    private static readonly double VerticalFov = 2 * Math.Atan(Math.Tan(HorizontalFov / 2 * Math.PI / 180) * 9 / 16) * 180 / Math.PI;

    public static string? OneEyeFlat(VrLayout? vr, int width)
    {
        if (vr == null)
            return null;

        var evenWidth = Math.Max(2, width / 2 * 2);
        var evenHeight = Math.Max(2, (int)Math.Round(evenWidth * 9 / 16.0 / 2) * 2);

        if (vr.Projection == "Flat")
        {
            if (vr.StereoMode is not ("SideBySide" or "TopBottom"))
                return null;
            var leftEye = vr.StereoMode == "SideBySide" ? "crop=iw/2:ih:0:0" : "crop=iw:ih/2:0:0";
            return string.Create(CultureInfo.InvariantCulture, $"{leftEye},scale={evenWidth}:{evenHeight},setsar=1");
        }

        var input = vr.Projection switch
        {
            "Equirectangular" when vr.FieldOfView >= 360 => "input=e",
            "Equirectangular" => "input=he",
            _ => string.Create(CultureInfo.InvariantCulture, $"input=fisheye:ih_fov={vr.FieldOfView}:iv_fov={vr.FieldOfView}"),
        };
        var stereo = vr.StereoMode switch
        {
            "SideBySide" => "sbs",
            "TopBottom" => "tb",
            _ => "2d",
        };

        return string.Create(CultureInfo.InvariantCulture,
            $"v360={input}:output=flat:in_stereo={stereo}:out_stereo=2d:h_fov={HorizontalFov:0.##}:v_fov={VerticalFov:0.##}:w={evenWidth}:h={evenHeight}");
    }
}

/// <summary>
/// Reads VR layouts only when the host Cove has them. VR layout metadata (Video.VrProjection & co. and
/// Cove.Core.Helpers.VrDescriptorDetector) arrived after Cove 1.5.1, and Cove 1.5.1 itself renders VR
/// videos without reprojection; binding late keeps one extension build working on both.
/// </summary>
public static class VrSupport
{
    private static readonly PropertyInfo? ProjectionProperty = typeof(Video).GetProperty("VrProjection");
    private static readonly PropertyInfo? FieldOfViewProperty = typeof(Video).GetProperty("VrFieldOfView");
    private static readonly PropertyInfo? StereoModeProperty = typeof(Video).GetProperty("VrStereoMode");

    private static readonly MethodInfo? Resolve = typeof(Video).Assembly
        .GetType("Cove.Core.Helpers.VrDescriptorDetector")
        ?.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .FirstOrDefault(m => m.Name == "Resolve" && m.GetParameters().Length == 7);

    /// <summary>True when this Cove stores VR layouts (and generates reprojected VR covers).</summary>
    public static bool Available => Resolve is not null && ProjectionProperty is not null;

    public static VrLayout? Layout(Video video, string? path, int width, int height)
    {
        if (!video.IsVr || !Available)
            return null;
        try
        {
            var descriptor = Resolve!.Invoke(null,
            [
                true,
                ProjectionProperty!.GetValue(video),
                FieldOfViewProperty?.GetValue(video),
                StereoModeProperty?.GetValue(video),
                path,
                width,
                height,
            ]);
            if (descriptor is null)
                return null;
            var type = descriptor.GetType();
            return new VrLayout(
                type.GetProperty("Projection")?.GetValue(descriptor)?.ToString() ?? "Equirectangular",
                Convert.ToInt32(type.GetProperty("FieldOfView")?.GetValue(descriptor) ?? 180, CultureInfo.InvariantCulture),
                type.GetProperty("StereoMode")?.GetValue(descriptor)?.ToString() ?? "Mono");
        }
        catch (Exception ex) when (ex is TargetInvocationException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }
}
