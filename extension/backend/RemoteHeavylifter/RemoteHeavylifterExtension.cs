using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RemoteHeavylifter.Api;
using RemoteHeavylifter.Generation;
using RemoteHeavylifter.Remote;
using RemoteHeavylifter.Servers;

namespace RemoteHeavylifter;

/// <summary>
/// Offloads video cover / preview / sprite generation to remote generation servers that see the same
/// media library, then places the results in Cove's generated folder.
/// </summary>
public sealed class RemoteHeavylifterExtension : JobExtensionBase, IApiExtension, IStatefulExtension
{
    public const string ExtensionId = "com.cove.remote-heavylifter";
    public const string SettingsTabKey = "remote-generation";
    public const string TaskListJobId = "remote-generate";

    private readonly StoreHolder _store = new();
    private IServiceProvider? _services;

    protected override void DefineJobs()
        => Job(
            TaskListJobId,
            "Remote generate (last used options)",
            RunFromTaskListAsync,
            "Generate covers, previews and sprites on the live remote generation servers, using the options last started from Settings → Remote Generation.",
            supportsParameters: false,
            showInTaskList: true);

    public override void ConfigureServices(IServiceCollection services, ExtensionContext context)
    {
        services.AddSingleton(_store);
        services.AddSingleton<ServerRegistry>();
        services.AddSingleton<GenerationOptionsStore>();
        services.AddSingleton(sp => new HealthMonitor(sp.GetRequiredService<IRemoteClientFactory>(), TimeProvider.System));
        services.AddHttpClient(HttpRemoteClientFactory.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<IRemoteClientFactory, HttpRemoteClientFactory>();
        services.AddSingleton(CoordinatorTimings.Default);
        services.AddSingleton(sp => new GenerationCoordinator(
            sp.GetRequiredService<ServerRegistry>(),
            sp.GetRequiredService<HealthMonitor>(),
            sp.GetRequiredService<IRemoteClientFactory>(),
            sp.GetRequiredService<IExtensionServiceScopeFactory>(),
            sp.GetRequiredService<Cove.Core.Interfaces.CoveConfiguration>(),
            sp.GetRequiredService<CoordinatorTimings>(),
            TimeProvider.System,
            sp.GetRequiredService<ILogger<GenerationCoordinator>>()));
        services.AddScoped<VideoWorkSelector>();
    }

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _services = services;
        return Task.CompletedTask;
    }

    public override Task ShutdownAsync(CancellationToken ct = default)
    {
        _services?.GetService<GenerationCoordinator>()?.CancelAll();
        _services = null;
        return Task.CompletedTask;
    }

    public void SetStore(IExtensionStore store) => _store.Set(store);

    public override UIManifest GetUIManifest()
        => ManifestBuilder()
            .AddSettingsTab(
                SettingsTabKey,
                "Remote Generation",
                order: 60,
                icon: "server",
                description: "Generate covers, previews and sprites on remote servers.",
                searchKeywords: ["remote", "heavylifter", "generate", "offload", "preview", "sprite", "cover"])
            .AddSettingsSection(SettingsTabKey, "Generation servers", "RemoteServersPanel", id: $"{ExtensionId}:servers", order: 10)
            .AddSettingsSection(SettingsTabKey, "Generate", "RemoteGeneratePanel", id: $"{ExtensionId}:generate", order: 20)
            .Build();

    public void MapEndpoints(IEndpointRouteBuilder endpoints) => RemoteEndpoints.Map(endpoints, ExtensionId);

    private async Task RunFromTaskListAsync(
        IReadOnlyDictionary<string, string>? parameters,
        Cove.Plugins.IJobProgress progress,
        CancellationToken ct)
    {
        var services = _services ?? throw new InvalidOperationException("The extension is not initialized.");
        var options = await services.GetRequiredService<GenerationOptionsStore>().GetAsync(ct);
        // The task list has no server picker: use every enabled server that is live.
        var request = options with { ServerIds = [] };
        await services.GetRequiredService<GenerationCoordinator>()
            .RunAsync(request, new PluginProgressAdapter(progress), ct);
    }
}
