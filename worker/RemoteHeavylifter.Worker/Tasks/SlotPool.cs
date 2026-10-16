using RemoteHeavylifter.Protocol;
using RemoteHeavylifter.Worker.Hardware;
using RemoteHeavylifter.Worker.Hosting;
using RemoteHeavylifter.Worker.Media;

namespace RemoteHeavylifter.Worker.Tasks;

/// <summary>A slot pool to run: a GPU (or the CPU) and how many videos it takes at once.</summary>
public sealed record SlotPlan(string Id, string Kind, GpuDevice? Gpu, int Capacity);

/// <summary>Which slot pools a worker runs, from its options and the GPUs it found.</summary>
public static class SlotPlanner
{
    /// <summary>
    /// Without --hv-*-slots every detected GPU gets <see cref="WorkerOptions.DefaultGpuSlots"/>; with any of them, only
    /// the GPUs listed (slots 0 leaves one out). The CPU pool follows <see cref="WorkerOptions.CpuSlots"/>.
    /// </summary>
    public static (IReadOnlyList<SlotPlan> Slots, IReadOnlyList<string> Errors) Plan(WorkerOptions options, IReadOnlyList<GpuDevice> gpus)
    {
        var slots = new List<SlotPlan>();
        var errors = new List<string>();
        var configured = options.IntelSlots is not null || options.NvidiaSlots is not null || options.AppleSlots is not null;
        foreach (var (vendor, spec, flag) in new[]
                 {
                     (GpuVendors.Intel, options.IntelSlots, "--hv-intel-slots"),
                     (GpuVendors.Nvidia, options.NvidiaSlots, "--hv-nvidia-slots"),
                     (GpuVendors.Apple, options.AppleSlots, "--hv-apple-slots"),
                 })
        {
            var detected = gpus.Where(g => g.Vendor == vendor).OrderBy(g => g.Index).ToList();
            if (!configured)
            {
                slots.AddRange(detected.Select(g => new SlotPlan(g.Id, SlotKinds.Gpu, g, WorkerOptions.DefaultGpuSlots)));
                continue;
            }
            foreach (var (index, count) in spec ?? [])
            {
                if (detected.FirstOrDefault(g => g.Index == index) is not { } gpu)
                {
                    var found = detected.Count == 0 ? "none" : string.Join(", ", detected.Select(g => $"{g.Index} ({g.Name})"));
                    errors.Add($"{flag}: there is no {vendor} GPU {index}; detected: {found}.");
                    continue;
                }
                if (count > 0)
                    slots.Add(new SlotPlan(gpu.Id, SlotKinds.Gpu, gpu, count));
            }
        }
        if (options.CpuSlots > 0)
            slots.Add(new SlotPlan(SlotIds.Cpu, SlotKinds.Cpu, null, options.CpuSlots));
        if (slots.Count == 0 && errors.Count == 0)
            errors.Add("No slots to run videos on: no GPU detected or enabled, and --cpu-slots is 0.");
        return (slots, errors);
    }
}

public static class SlotIds
{
    public const string Cpu = "cpu";
}

/// <summary>A slot pool at run time: its engine (encoding on its GPU) and how many of its slots are taken.</summary>
public sealed class Slot(SlotPlan plan, IMediaEngine engine, string encoder)
{
    private int _running;

    public SlotPlan Plan { get; } = plan;
    public string Id => Plan.Id;
    public IMediaEngine Engine { get; } = engine;

    /// <summary>The preview encoder: the GPU's (with its device), or libx264.</summary>
    public string Encoder { get; } = encoder;

    public int Running => Volatile.Read(ref _running);

    /// <summary>Takes a slot unless all are taken.</summary>
    public bool TryTake()
    {
        while (true)
        {
            var running = Volatile.Read(ref _running);
            if (running >= Plan.Capacity)
                return false;
            if (Interlocked.CompareExchange(ref _running, running + 1, running) == running)
                return true;
        }
    }

    public void Release() => Interlocked.Decrement(ref _running);

    /// <summary>The source as this slot decodes it: on its GPU when that decodes anything (codecs it cannot decode fall
    /// back to software in the engine).</summary>
    public MediaSource Decode(MediaSource source) =>
        Plan.Gpu is { Decoders.Count: > 0 } gpu ? source.WithHardwareDecode(gpu.HwAccel, gpu.DecodeDevice) : source;

    public SlotInfo Info() => new(Id, Plan.Kind, Plan.Gpu?.Vendor, Plan.Gpu?.Name ?? "CPU", Plan.Capacity, Running,
        EncoderArgs.Split(Encoder).Name, Plan.Gpu?.Decoders ?? []);
}

/// <summary>The worker's slot pools.</summary>
public sealed class SlotPool(IReadOnlyList<Slot> slots)
{
    public IReadOnlyList<Slot> Slots { get; } = slots;

    public int Running => Slots.Sum(s => s.Running);

    /// <summary>The slot pool <paramref name="id"/> names, or (null) the first with room.</summary>
    public Slot? Find(string? id) => id is null
        ? Slots.FirstOrDefault(s => s.Running < s.Plan.Capacity) ?? Slots.FirstOrDefault()
        : Slots.FirstOrDefault(s => s.Id == id);

    public IReadOnlyList<SlotInfo> Info() => Slots.Select(s => s.Info()).ToList();
}
