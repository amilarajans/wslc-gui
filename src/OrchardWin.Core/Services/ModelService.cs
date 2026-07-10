using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns discovered local-model providers and bridges them to containers. Read-only: detects
/// providers whenever <see cref="LoadAsync"/> is called and exposes them for the
/// container-create bridge. Mirrors Orchard's `ModelService` - `load()` is meant to be
/// driven by an external refresh tick (or a manual on-demand call from the UI), not
/// self-scheduled here, exactly like the original's "the refresh loop calls load()" comment.
/// Detection never alerts: a missing provider is a normal, expected state, not an error.
public sealed partial class ModelService : ObservableObject
{
    [ObservableProperty]
    private bool _isLoading;

    /// Currently detected providers. An `ObservableCollection` (rather than a
    /// `[ObservableProperty] List<T>`) so a WinUI/WPF list view can bind directly and see
    /// incremental updates instead of a full rebind on every refresh tick.
    public ObservableCollection<ModelProvider> Providers { get; } = [];

    private readonly IModelBackend _backend;

    public ModelService(IModelBackend backend)
    {
        _backend = backend;
    }

    /// Probe the host for running model providers and refresh <see cref="Providers"/>.
    /// `showLoading` mirrors Orchard's parameter: pass `false` for a silent background poll
    /// so the UI doesn't flash a loading state on every refresh tick.
    public async Task LoadAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading) IsLoading = true;
        try
        {
            var providers = await _backend.DetectProvidersAsync(ct);
            if (!ProvidersEqual(providers, Providers))
            {
                Providers.Clear();
                foreach (var provider in providers) Providers.Add(provider);
                // Notify even when only CollectionChanged fires — some pages bind to IsLoading
                // / count badges and need a property pulse.
                OnPropertyChanged(nameof(Providers));
            }
            Log.Backend.Debug($"ModelService.LoadAsync → {Providers.Count} provider(s)");
        }
        catch (Exception ex)
        {
            Log.Backend.Error($"ModelService.LoadAsync failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// Send a chat conversation to a provider running on the host and return its reply.
    /// Surfaces transport/HTTP errors to the caller (the tester shows them inline).
    public Task<string> CompleteAsync(ushort port, ModelApiStyle api, string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default) =>
        _backend.CompleteAsync(port, api, model, messages, ct);

    /// The environment-variable pairs to inject so a container attached to `network` reaches
    /// `provider` on the host. Returns null when the network has no usable gateway (the
    /// container would have no route to the host).
    public List<(string Key, string Value)>? BridgeEnvironment(ModelProvider provider, ContainerNetwork network)
    {
        var gateway = network.Status.Gateway;
        if (string.IsNullOrEmpty(gateway)) return null;
        var baseUrl = ModelBridge.ContainerBaseUrl(gateway, provider.Port, provider.Api);
        return ModelBridge.InjectionEnvironment(baseUrl, provider.Api);
    }

    /// Structural equality for the "did anything actually change" guard. `ModelProvider` is
    /// a record, but its `Models` member is a `List&lt;string&gt;` - records only synthesize
    /// value equality for members that are themselves value-equatable, and `List&lt;T&gt;`
    /// compares by reference, not by content. Swift's `[ModelProvider]` `Equatable`
    /// conformance is fully structural (Swift arrays compare element-wise), so a naive `==`
    /// port here would treat every fresh probe result as "changed" even when nothing moved,
    /// clearing and rebuilding the collection - and the UI list bound to it - every tick.
    private static bool ProvidersEqual(IReadOnlyList<ModelProvider> a, IReadOnlyList<ModelProvider> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.Kind != y.Kind || x.Port != y.Port || x.Api != y.Api) return false;
            if (!x.Models.SequenceEqual(y.Models)) return false;
        }
        return true;
    }
}
