using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Api;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Workers;

namespace RemoteHeavylifter;

/// <summary>
/// Offloads video cover / preview / sprite generation to remote workers. Workers connect over a WebSocket
/// (Cove dials them, or they dial Cove), read sources and upload artifacts through this extension's HTTP
/// endpoints, and the results land in Cove's generated folder.
/// </summary>
public sealed class RemoteHeavylifterExtension : JobExtensionBase, IApiExtension, IStatefulExtension, IBackgroundExtension
{
    public const string ExtensionId = Protocol.ProtocolInfo.ExtensionId;
    public const string SettingsTabKey = "remote-generation";
    public const string TaskListJobId = "remote-generate";

    private readonly StoreHolder _store = new();
    private IServiceProvider? _services;

    protected override void DefineJobs()
        => Job(
            TaskListJobId,
            "Remote generate (last used options)",
            RunFromTaskListAsync,
            "Generate covers, previews and sprites on the connected remote workers, using the options last started from Settings → Remote Generation.",
            supportsParameters: false,
            showInTaskList: true);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(_store);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerRegistry>();
        services.AddSingleton<PendingWorkers>();
        services.AddSingleton<WorkerSettingsStore>();
        services.AddSingleton<WorkerAccess>();
        services.AddSingleton<WorkerHub>();
        services.AddSingleton<IFingerprintStore, FingerprintStore>();
        services.AddSingleton<GenerationOptionsStore>();
        services.AddSingleton(CoordinatorTimings.Default);
        services.AddSingleton(sp => new GenerationCoordinator(
            sp.GetRequiredService<WorkerRegistry>(),
            sp.GetRequiredService<WorkerHub>(),
            sp.GetRequiredService<WorkerSettingsStore>(),
            sp.GetRequiredService<WorkerAccess>(),
            sp.GetRequiredService<IFingerprintStore>(),
            ExtensionId,
            sp.GetRequiredService<IExtensionServiceScopeFactory>(),
            sp.GetRequiredService<Cove.Core.Interfaces.CoveConfiguration>(),
            sp.GetRequiredService<CoordinatorTimings>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<GenerationCoordinator>>()));
        services.AddScoped<VideoWorkSelector>();
    }

    /// <summary>Keeps worker connections up for as long as the extension is enabled.</summary>
    public Task RunAsync(IServiceProvider services, CancellationToken ct)
        => services.GetRequiredService<WorkerHub>().RunAsync(ct);

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public override async Task ShutdownAsync(CancellationToken ct = default)
    {
        _services?.GetService<GenerationCoordinator>()?.CancelAll();
        // Open sockets hold the extension's request scopes; close them so the container can be released.
        if (_services?.GetService<WorkerHub>() is { } hub)
            await hub.CloseAllAsync(Protocol.CloseReasons.ShuttingDown);
        _services = null;
    }

    public void SetStore(IExtensionStore store) => _store.Set(store);

    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                SettingsTabKey,
                "Remote Generation",
                // Page layout: the panels draw their own headers, so skip the host's per-panel card chrome.
                SettingsTabLayout.Page,
                order: 60,
                icon: "server",
                description: "Generate covers, previews and sprites on remote workers.",
                searchKeywords: ["remote", "heavylifter", "worker", "generate", "offload", "preview", "sprite", "cover"])
            .AddSettingsSection(SettingsTabKey, "Generation workers", "RemoteWorkersPanel", id: $"{ExtensionId}:workers", order: 10)
            .AddSettingsSection(SettingsTabKey, "Generate", "RemoteGeneratePanel", id: $"{ExtensionId}:generate", order: 20)
            .Build();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RemoteEndpoints.Map(endpoints, ExtensionId);
        WorkerEndpoints.Map(endpoints, ExtensionId);
    }

    private async Task RunFromTaskListAsync(
        IReadOnlyDictionary<string, string>? parameters,
        Cove.Plugins.IJobProgress progress,
        CancellationToken ct)
    {
        var services = _services ?? throw new InvalidOperationException("The extension is not initialized.");
        var options = await services.GetRequiredService<GenerationOptionsStore>().GetAsync(ct);
        // The task list has no worker picker: use every enabled worker that is connected.
        var request = options with { WorkerIds = [] };
        await services.GetRequiredService<GenerationCoordinator>()
            .RunAsync(request, new PluginProgressAdapter(progress), ct);
    }
}
