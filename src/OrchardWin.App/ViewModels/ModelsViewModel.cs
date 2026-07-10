using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

public sealed record ModelRow(string Id, string PrimaryText, string SecondaryText, Color IconColor);

public sealed partial class ModelsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public AppServices Services => _services;

    public ObservableCollection<ModelRow> ServerRows { get; } = [];
    public ObservableCollection<ModelRow> DetectedRows { get; } = [];
    public ObservableCollection<ModelProvider> DetectedProviders { get; } = [];

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
                EngineAvailable = _services.ModelServerService.EngineAvailable;
        };

        _services.ModelServerService.Servers.CollectionChanged += (_, _) => Refresh();
        _services.ModelService.Providers.CollectionChanged += (_, _) => Refresh();
        _services.ModelService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ModelService.Providers) or nameof(ModelService.IsLoading))
                Refresh();
        };
        Refresh();
    }

    public string? DefaultGateway
    {
        get
        {
            // Prefer named "default"/"bridge", else any network that has a gateway.
            foreach (var n in _services.NetworkService.Networks)
            {
                if (string.Equals(n.Id, "default", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n.Id, "bridge", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(n.Status.Gateway)) return n.Status.Gateway;
                }
            }
            return _services.NetworkService.Networks
                .Select(n => n.Status.Gateway)
                .FirstOrDefault(g => !string.IsNullOrEmpty(g));
        }
    }

    public async Task LoadAsync(bool showLoading = true)
    {
        await _services.ModelService.LoadAsync(showLoading);
        await _services.NetworkService.LoadAsync(showLoading: false);
        // Re-check engine path each load (user may install ollama while app is open).
        EngineAvailable = _services.ModelServerService.EngineAvailable
            || LocateOllama() is not null;
        Refresh();
    }

    public Task RefreshQuietAsync() => LoadAsync(showLoading: false);

    private static string? LocateOllama()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(candidate)) return candidate;
            }
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Ollama", "ollama.exe");
            if (File.Exists(local)) return local;
        }
        catch { /* ignore */ }
        return null;
    }

    private void Refresh()
    {
        var managedPorts = _services.ModelServerService.ManagedPorts;
        var detected = _services.ModelService.Providers.Where(p => !managedPorts.Contains(p.Port)).ToList();

        ObservableCollectionSync.Sync(DetectedProviders, detected, (a, b) =>
            string.Equals(a.Id, b.Id, StringComparison.Ordinal)
            && a.Port == b.Port
            && a.Models.Count == b.Models.Count);

        var serverRows = _services.ModelServerService.Servers.Select(s => new ModelRow(
            s.Id,
            s.Model,
            $"port {s.Port}",
            s.Status == ManagedModelServerStatus.Running ? Colors.Green : Colors.Red)).ToList();

        // Secondary line: "port 11434" matching Orchard; sparkles-ish gray for detected.
        var detectedRows = detected.Select(p => new ModelRow(
            p.Id,
            p.Kind.DisplayName(),
            $"port {p.Port}",
            Color.FromArgb(255, 156, 163, 175))).ToList();

        var serversChanged = ObservableCollectionSync.Sync(ServerRows, serverRows, (a, b) =>
            string.Equals(a.Id, b.Id, StringComparison.Ordinal)
            && string.Equals(a.PrimaryText, b.PrimaryText, StringComparison.Ordinal)
            && string.Equals(a.SecondaryText, b.SecondaryText, StringComparison.Ordinal)
            && a.IconColor.Equals(b.IconColor));

        var detectedChanged = ObservableCollectionSync.Sync(DetectedRows, detectedRows, (a, b) =>
            string.Equals(a.Id, b.Id, StringComparison.Ordinal)
            && string.Equals(a.PrimaryText, b.PrimaryText, StringComparison.Ordinal)
            && string.Equals(a.SecondaryText, b.SecondaryText, StringComparison.Ordinal));

        if (serversChanged) OnPropertyChanged(nameof(ServerRows));
        if (detectedChanged) OnPropertyChanged(nameof(DetectedRows));
        OnPropertyChanged(nameof(DetectedProviders));
        OnPropertyChanged(nameof(DefaultGateway));
        RefreshSelection();

        // Auto-select first detected/managed when nothing selected (Orchard list behaviour).
        if (SelectedId is null)
        {
            var first = ServerRows.FirstOrDefault()?.Id ?? DetectedRows.FirstOrDefault()?.Id;
            if (first is not null) SelectedId = first;
        }
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
