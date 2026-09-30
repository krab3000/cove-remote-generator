using Cove.Plugins;

namespace RemoteHeavylifter.Servers;

/// <summary>
/// The host hands the extension its key-value store through <see cref="IStatefulExtension.SetStore"/>,
/// before the extension's service container exists. This singleton carries it into DI.
/// </summary>
public sealed class StoreHolder
{
    private volatile IExtensionStore? _store;

    public IExtensionStore Store
        => _store ?? throw new InvalidOperationException("The extension store has not been initialized.");

    public void Set(IExtensionStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));
}
