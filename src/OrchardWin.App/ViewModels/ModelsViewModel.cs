using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in either the "Managed by Orchard-Win" or "Detected" list: the raw entity's id
/// plus the display-ready text/color the row needs. Mirrors DashboardViewModel's
/// `UtilisationRow` - a thin per-row projection over service state, not a re-implementation
/// of it, so `ListItemRow` can bind without a converter.
public sealed record ModelRow(string Id, string PrimaryText, string SecondaryText, Color IconColor);

/// Thin glue for the Models page: derives the "Detected" list (providers minus ports our own
/// managed servers already answer on, mirroring Swift's `ModelsListView.detected`), tracks
/// the current cross-list selection, and wires the managed-server lifecycle actions. Talks to
/// `ModelService`/`ModelServerService`/`NetworkService` directly - no state here duplicates
/// what those services already track beyond the row/selection projections.
public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// Exposed so the page can reach services directly for dialogs (CreateModelServerDialog,
    /// TestModelPromptDialog) that take a service reference rather than the whole AppServices.
    public AppServices Services => _services;

    [ObservableProperty]
    private ObservableCollection<ModelRow> _serverRows = [];

    [ObservableProperty]
    private ObservableCollection<ModelRow> _detectedRows = [];

    /// Detected providers minus the ports our own managed servers already cover - the same
    /// filter `DetectedRows` uses, kept as the real entities (not rows) for the detail pane.
    [ObservableProperty]
    private ObservableCollection<ModelProvider> _detectedProviders = [];

    [ObservableProperty]
    private bool _engineAvailable;

    [ObservableProperty]
    private string? _selectedId;

    [ObservableProperty]
    private ManagedModelServer? _selectedServer;

    [ObservableProperty]
    private ModelProvider? _selectedProvider;

    public ModelsViewModel(AppServices services)
    {
        _services = services;
        _engineAvailable = _services.ModelServerService.EngineAvailable;

        _services.ModelServerService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModelServerService.EngineAvailable))
            {
                EngineAvailable = _services.ModelServerService.EngineAvailable;
            }
        };

        // Servers/Providers are get-only ObservableCollections (never reassigned), so
        // subscribing once here is safe for the lifetime of this view model.
        _services.ModelServerService.Servers.CollectionChanged += (_, _) => Refresh();
        _services.ModelService.Providers.CollectionChanged += (_, _) => Refresh();

        Refresh();
    }

    /// The gateway of the "default" network, for the container-reachable URL - mirrors the
    /// Swift original's `containerURL(port:api:)` gateway lookup.
    public string? DefaultGateway
    {
        get
        {
            var gateway = _services.NetworkService.Networks.FirstOrDefault(n => n.Id == "default")?.Status.Gateway;
            return string.IsNullOrEmpty(gateway) ? null : gateway;
        }
    }

    public async Task LoadAsync(bool showLoading = true)
    {
        await _services.ModelService.LoadAsync(showLoading);
        await _services.NetworkService.LoadAsync(showLoading: false);
    }

    public Task RefreshQuietAsync() => LoadAsync(showLoading: false);

    private void Refresh()
    {
        var managedPorts = _services.ModelServerService.ManagedPorts;
        var detected = _services.ModelService.Providers.Where(p => !managedPorts.Contains(p.Port)).ToList();
        DetectedProviders = new ObservableCollection<ModelProvider>(detected);

        ServerRows = new ObservableCollection<ModelRow>(_services.ModelServerService.Servers.Select(s => new ModelRow(
            s.Id,
            s.Model,
            $"port {s.Port}",
            s.Status == ManagedModelServerStatus.Running ? Colors.Green : Colors.Red)));

        DetectedRows = new ObservableCollection<ModelRow>(detected.Select(p => new ModelRow(
            p.Id,
            p.Kind.DisplayName(),
            $"port {p.Port}",
            Colors.Gray)));

        RefreshSelection();
    }

    partial void OnSelectedIdChanged(string? value) => RefreshSelection();

    private void RefreshSelection()
    {
        SelectedServer = _services.ModelServerService.Servers.FirstOrDefault(s => s.Id == SelectedId);
        SelectedProvider = SelectedServer is null
            ? _services.ModelService.Providers.FirstOrDefault(p => p.Id == SelectedId)
            : null;
    }

    [RelayCommand]
    private void StopSelected()
    {
        if (SelectedServer is { } server) _services.ModelServerService.Stop(server.Id);
    }

    /// Reveal the managed server's log file in File Explorer - the closest Windows analogue
    /// to Swift's `NSWorkspace.shared.selectFile`.
    [RelayCommand]
    private void ShowLog()
    {
        if (SelectedServer is not { } server) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{server.LogPath}\"",
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            _services.AlertCenter.Error($"Failed to open log: {ex.Message}");
        }
    }
}
