using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in either the "Wired by Orchard-Win" or "Detected" sandbox list: the raw sandbox's
/// id plus the display-ready glyph/text/color the row needs. Same shape as ModelsViewModel's
/// `ModelRow` - a thin per-row projection, not a re-implementation of `Sandbox`.
public sealed record SandboxRow(string Id, string Icon, Color IconColor, string PrimaryText, string SecondaryText);

/// Thin glue for the Sandboxes page: a sandbox is a *derived* view over the container list
/// (via `SandboxDetection.DetectSandboxes`), not a service-owned collection, so this view
/// model recomputes it whenever the containers or networks it depends on change - mirroring
/// Swift's `sandboxes`/`managed`/`detected` computed properties in `SandboxesListView`.
public sealed partial class SandboxesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public AppServices Services => _services;

    [ObservableProperty]
    private ObservableCollection<Sandbox> _managedSandboxes = [];

    [ObservableProperty]
    private ObservableCollection<Sandbox> _detectedSandboxes = [];

    [ObservableProperty]
    private ObservableCollection<SandboxRow> _managedRows = [];

    [ObservableProperty]
    private ObservableCollection<SandboxRow> _detectedRows = [];

    [ObservableProperty]
    private string? _selectedId;

    [ObservableProperty]
    private Sandbox? _selectedSandbox;

    public SandboxesViewModel(AppServices services)
    {
        _services = services;
        _services.ContainerListService.PropertyChanged += (_, _) => Refresh();
        _services.NetworkService.PropertyChanged += (_, _) => Refresh();
        Refresh();
    }

    public async Task LoadAsync(bool showLoading = true)
    {
        await _services.ContainerListService.LoadAsync(showLoading);
        await _services.NetworkService.LoadAsync(showLoading: false);
    }

    public Task RefreshQuietAsync() => LoadAsync(showLoading: false);

    private void Refresh()
    {
        var all = SandboxDetection.DetectSandboxes(_services.ContainerListService.Containers, _services.NetworkService.Networks);
        ManagedSandboxes = new ObservableCollection<Sandbox>(all.Where(s => s.Source == SandboxSource.Managed));
        DetectedSandboxes = new ObservableCollection<Sandbox>(all.Where(s => s.Source == SandboxSource.Detected));
        ManagedRows = new ObservableCollection<SandboxRow>(ManagedSandboxes.Select(ToRow));
        DetectedRows = new ObservableCollection<SandboxRow>(DetectedSandboxes.Select(ToRow));
        RefreshSelection();
    }

    private static SandboxRow ToRow(Sandbox sandbox) => new(
        sandbox.Id,
        sandbox.Kind == SandboxKind.Container ? "\uE7B8" : "\uE950",
        sandbox.IsRunning ? Colors.Green : Colors.Gray,
        sandbox.Name,
        sandbox.IsIsolated ? "Isolated" : "Egress open");

    partial void OnSelectedIdChanged(string? value) => RefreshSelection();

    private void RefreshSelection()
    {
        SelectedSandbox = ManagedSandboxes.Concat(DetectedSandboxes).FirstOrDefault(s => s.Id == SelectedId);
    }

    [RelayCommand]
    private async Task StopSelectedAsync()
    {
        if (SelectedSandbox is { } sandbox) await _services.ContainerListService.StopContainerAsync(sandbox.Id);
    }

    [RelayCommand]
    private async Task OpenTerminalAsync()
    {
        if (SelectedSandbox is { } sandbox) await _services.TerminalLauncher.OpenShellAsync(ShellTargetKind.Container, sandbox.Id);
    }
}
