using Microsoft.Extensions.Logging.Abstractions;
using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Bench;
using RemoteHeavylifter.Worker.Hardware;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;
using RemoteHeavylifter.Worker.Tasks;

namespace RemoteHeavylifter.Worker.Tests;

/// <summary>Slot pools per GPU and CPU: planning from options and detected GPUs, capacity, encoder devices, probes.</summary>
public sealed class SlotTests
{
    private static GpuDevice Gpu(string vendor, int index, params string[] codecs) => new(
        vendor, index, $"0000:0{index}:00.0", $"{vendor} {index}", vendor == GpuVendors.Intel ? "vaapi" : "cuda",
        vendor == GpuVendors.Intel ? $"/dev/dri/renderD{128 + index}" : $"{index}", vendor == GpuVendors.Intel ? "h264_vaapi" : "h264_nvenc",
        vendor == GpuVendors.Intel ? $"/dev/dri/renderD{128 + index}" : $"{index}", 1, codecs.Select(c => new DecoderCap(c, 4096, 4096)).ToList());

    private static readonly GpuDevice[] Gpus = [Gpu(GpuVendors.Intel, 0, "h264", "vc1"), Gpu(GpuVendors.Nvidia, 0, "h264"), Gpu(GpuVendors.Nvidia, 1, "hevc")];

    private static WorkerOptions Options(params string[] args) => WorkerOptions.Load(args, _ => null).Options;

    [Fact]
    public void Every_detected_gpu_gets_two_slots_by_default()
    {
        var (slots, errors) = SlotPlanner.Plan(Options(), Gpus);
        Assert.Empty(errors);
        Assert.Equal([("intel0", 2), ("nvidia0", 2), ("nvidia1", 2), ("cpu", 1)], slots.Select(s => (s.Id, s.Capacity)));
        Assert.All(slots.Take(3), s => Assert.Equal(SlotKinds.Gpu, s.Kind));
        Assert.Equal(SlotKinds.Cpu, slots[^1].Kind);
    }

    [Fact]
    public void Flags_list_the_gpus_used()
    {
        var (slots, errors) = SlotPlanner.Plan(Options("--hv-nvidia-slots=1,3", "--hv-nvidia-slots=0,0", "--cpu-slots=4"), Gpus);
        Assert.Empty(errors);
        Assert.Equal([("nvidia1", 3), ("cpu", 4)], slots.Select(s => (s.Id, s.Capacity)));

        (_, errors) = SlotPlanner.Plan(Options("--hv-intel-slots=1,2"), Gpus);
        Assert.Contains(errors, e => e.Contains("no intel GPU 1") && e.Contains("0 (intel 0)"));

        var apple = Gpu(GpuVendors.Apple, 0, "h264") with { DecodeDevice = null, EncoderDevice = null, Encoder = "h264_videotoolbox" };
        (slots, errors) = SlotPlanner.Plan(Options("--hv-apple-slots=0,3", "--cpu-slots=0"), [apple]);
        Assert.Empty(errors);
        Assert.Equal([("apple0", 3)], slots.Select(s => (s.Id, s.Capacity)));

        (_, errors) = SlotPlanner.Plan(Options("--cpu-slots=0"), []);
        Assert.Contains(errors, e => e.Contains("No slots"));
    }

    [Fact]
    public void Intel_gpus_use_quick_sync_unless_told_vaapi()
    {
        var intel = Gpu(GpuVendors.Intel, 0, "h264", "hevc", "vc1", "wmv3", "mpeg2video", "mpeg4");
        bool HasQsv(string name) => name is "h264_qsv" or "hevc_qsv" or "vc1_qsv" or "mpeg2_qsv";

        var qsv = SlotBuilder.ForIntelApi(intel, "qsv", HasQsv);
        Assert.Equal(("qsv", "h264_qsv", "/dev/dri/renderD128"), (qsv.HwAccel, qsv.Encoder, qsv.DecodeDevice));
        // vc1_qsv crashes; wmv3 and mpeg4 have no Quick Sync decoder: those decode in software (and Cove routes them so).
        Assert.Equal(["h264", "hevc", "mpeg2video"], qsv.Decoders.Select(d => d.Codec));

        Assert.Same(intel, SlotBuilder.ForIntelApi(intel, "vaapi", HasQsv));
        Assert.Same(Gpus[1], SlotBuilder.ForIntelApi(Gpus[1], "qsv", HasQsv));

        Assert.Equal("qsv", Options().IntelApi);
        Assert.Equal("vaapi", Options("--intel-api=VAAPI").IntelApi);
        Assert.Contains(Options("--intel-api=amf", "--cove-url=http://c").Validate(), e => e.Contains("HL_INTEL_API"));
    }

    [Fact]
    public void A_slot_pool_takes_up_to_its_capacity()
    {
        var slot = new Slot(new SlotPlan("nvidia0", SlotKinds.Gpu, Gpus[1], 2), Engines.Cli, "h264_nvenc@0");
        Assert.True(slot.TryTake());
        Assert.True(slot.TryTake());
        Assert.False(slot.TryTake());
        slot.Release();
        Assert.True(slot.TryTake());

        var info = slot.Info();
        Assert.Equal(("nvidia0", 2, 2, "h264_nvenc"), (info.Id, info.Capacity, info.Running, info.Encoder));
        Assert.True(info.Decodes("H264", 1920, 1080));
        Assert.False(info.Decodes("h264", 8192, 4320));
        Assert.False(info.Decodes("hevc", 0, 0));

        Assert.Equal(["-hwaccel", "cuda", "-hwaccel_device", "0"], slot.Decode(Specs.Local("/v.mp4")).DecodeOptions);
        Assert.Null(new Slot(new SlotPlan("cpu", SlotKinds.Cpu, null, 1), Engines.Cli, "libx264").Decode(Specs.Local("/v.mp4")).DecodeOptions);
    }

    [Fact]
    public void Encoders_run_on_their_slots_gpu()
    {
        Assert.Equal(["-c:v", "h264_nvenc", "-gpu", "1"], EncoderArgs.VideoEncodeArgs("h264_nvenc@1", 21, "slow").Take(4));
        Assert.DoesNotContain("-gpu", EncoderArgs.VideoEncodeArgs("h264_nvenc", 21, "slow"));
        Assert.Equal(["-vaapi_device", "/dev/dri/renderD129"], CommandLine.InputArgsForEncoder("h264_vaapi@/dev/dri/renderD129"));
        Assert.Equal(["-vaapi_device", EncoderArgs.DefaultVaapiDevice], CommandLine.InputArgsForEncoder("h264_vaapi"));
        Assert.Equal(",format=nv12,hwupload", EncoderArgs.UploadChain("h264_vaapi@/dev/dri/renderD129"));
        Assert.Equal(("h264_nvenc", "1"), EncoderArgs.Split("h264_nvenc@1"));
        Assert.Contains(new KeyValuePair<string, string>("gpu", "1"), Media.Libav.EncoderOptions.For("h264_nvenc@1", 21, "slow").Options);
    }

    [Fact]
    public void Probe_tables_name_libav_decoders()
    {
        Assert.Equal("vc1", VaapiProbe.Codec(10));
        Assert.Equal("wmv3", VaapiProbe.Codec(9));
        Assert.Equal("hevc", VaapiProbe.Codec(18));
        Assert.Equal("av1", VaapiProbe.Codec(32));
        Assert.Null(VaapiProbe.Codec(35)); // protected content
        Assert.Equal(["hevc"], NvdecProbe.Codecs.Single(c => c.Codec == 8).Names);
        Assert.Equal(["av1"], NvdecProbe.Codecs.Single(c => c.Codec == 11).Names);
        Assert.DoesNotContain(NvdecProbe.Codecs, c => c.Codec is 6 or 7); // H.264 SVC/MVC
    }

    [Fact]
    public void Linux_render_nodes_come_from_sysfs()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux");
        var root = Directory.CreateTempSubdirectory("hl-drm").FullName;
        try
        {
            var device = Directory.CreateDirectory(Path.Combine(root, "devices", "0000:01:00.0")).FullName;
            File.WriteAllText(Path.Combine(device, "vendor"), "0x10de\n");
            File.WriteAllText(Path.Combine(device, "device"), "0x2182\n");
            var node = Directory.CreateDirectory(Path.Combine(root, "renderD128")).FullName;
            File.CreateSymbolicLink(Path.Combine(node, "device"), device);
            Directory.CreateDirectory(Path.Combine(root, "card0-HDMI-A-1"));

            var found = Assert.Single(LinuxGpuDetector.RenderNodes(root));
            Assert.Equal(("/dev/dri/renderD128", "0x10de", "0x2182", "0000:01:00.0"), (found.Path, found.Vendor, found.DeviceId, found.Pci));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Live_detection_finds_working_gpus()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("HL_TEST_GPU") == "1", "set HL_TEST_GPU=1 on a machine with a GPU");
        var gpus = GpuDetection.ForPlatform().Detect(NullLogger.Instance);
        Assert.NotEmpty(gpus);
        Assert.All(gpus, g => Assert.Contains(g.Decoders, d => d.Codec == "h264"));
    }

    [Fact]
    public void Bench_suggests_the_fewest_videos_near_the_best()
    {
        Assert.Equal(2, BenchRunner.Suggest([(1, 3.4, 0), (2, 3.9, 0), (3, 4.0, 0), (4, 4.0, 0)]));
        Assert.Equal(1, BenchRunner.Suggest([(1, 6.0, 0), (2, 6.1, 0)]));
        Assert.Equal(3, BenchRunner.Suggest([(1, 3.0, 0), (2, 5.0, 0), (3, 6.0, 0), (4, 6.0, 1)]));
        Assert.Equal(0, BenchRunner.Suggest([(1, 3.0, 2)]));
    }
}
