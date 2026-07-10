using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

public sealed record SandboxRow(string Id, string Icon, Color IconColor, string PrimaryText, string SecondaryText);

public sealed partial class SandboxesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public AppServices Services => _services;

    public ObservableCollection<Sandbox> ManagedSandboxes { get; } = [];
    public ObservableCollection<Sandbox> DetectedSandboxes { get; } = [];
    public ObservableCollection<SandboxRow> ManagedRows { get; } = [];
    public ObservableCollection<SandboxRow> DetectedRows { get; } = [];

    [ObservableProperty]
    private string? _selectedId;

    [ObservableProperty]
    private Sandbox? _selectedSandbox;

    public SandboxesViewModel(AppServices services)
    {
        _services = services;
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers))
                Refresh();
        };
        _services.NetworkService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(NetworkService.Networks))
                Refresh();
        };
        Refresh();
    }

    public async Task LoadAsync(bool showLoading = true)
    {
        await _services.ContainerListService.LoadAsync(showLoading);
        await _services.NetworkService.LoadAsync(showLoading: false);
        Refresh();
    }

    public Task RefreshQuietAsync() => LoadAsync(showLoading: false);

    private void Refresh()
    {
        var all = SandboxDetection.DetectSandboxes(_services.ContainerListService.Containers, _services.NetworkService.Networks);
        var managed = all.Where(s => s.Source == SandboxSource.Managed).ToList();
        var detected = all.Where(s => s.Source == SandboxSource.Detected).ToList();

        ObservableCollectionSync.Sync(ManagedSandboxes, managed, (a, b) =>
            string.Equals(a.Id, b.Id, StringComparison.Ordinal) && a.IsRunning == b.IsRunning);
        ObservableCollectionSync.Sync(DetectedSandboxes, detected, (a, b) =>
            string.Equals(a.Id, b.Id, StringComparison.Ordinal) && a.IsRunning == b.IsRunning);

        var managedRows = managed.Select(ToRow).ToList();
        var detectedRows = detected.Select(ToRow).ToList();

        var mChanged = ObservableCollectionSync.Sync(ManagedRows, managedRows, RowEquals);
        var dChanged = ObservableCollectionSync.Sync(DetectedRows, detectedRows, RowEquals);
        if (mChanged) OnPropertyChanged(nameof(ManagedRows));
        if (dChanged) OnPropertyChanged(nameof(DetectedRows));
        RefreshSelection();
    }

    private static bool RowEquals(SandboxRow a, SandboxRow b) =>
        string.Equals(a.Id, b.Id, StringComparison.Ordinal)
        && string.Equals(a.PrimaryText, b.PrimaryText, StringComparison.Ordinal)
        && string.Equals(a.SecondaryText, b.SecondaryText, StringComparison.Ordinal)
        && a.IconColor.Equals(b.IconColor);

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
