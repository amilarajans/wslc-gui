using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the machines list: the raw machine plus the derived display fields its
/// ListItemRow cells bind to via x:Bind (which needs exact-typed properties, not expressions -
/// same reasoning as DashboardViewModel's UtilisationRow). Recomputed whenever
/// MachineService.Machines changes.
public sealed record MachineRow(
    string Id,
    string PrimaryText,
    string SecondaryLeftText,
    string SecondaryRightText,
    Color IconColor);

/// Thin glue for the Machines page: selection state (by id, mirroring the Swift view's
/// `selectedMachine: String?` rather than holding a `Machine` reference that would go stale
/// the moment MachineService replaces its collection on the next refresh) plus the derived
/// list-row projection. Lifecycle actions and config edits are forwarded straight to
/// AppServices.MachineService, which already owns all the real state (IsLoading,
/// ApiUnavailable, IsCreating) and alert routing.
public sealed partial class MachinesViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// The underlying service, exposed directly so the page can read IsLoading/ApiUnavailable/
    /// Machines without this ViewModel re-declaring pass-through properties for state the
    /// service already tracks (see AppServices.MachineService).
    public MachineService Service => _services.MachineService;

    [ObservableProperty]
    private ObservableCollection<MachineRow> _machineRows = [];

    [ObservableProperty]
    private string? _selectedMachineId;

    /// Looked up fresh by id every time (rather than cached) because MachineService.LoadAsync
    /// replaces the whole Machines collection with new Machine instances on every refresh.
    public Machine? SelectedMachine => Service.Machines.FirstOrDefault(m => m.Id == SelectedMachineId);

    public MachinesViewModel(AppServices services)
    {
        _services = services;

        // Rebuild derived rows whenever the service's collection changes, rather than polling
        // ourselves - MachineService already self-refreshes via the app's poll loop / its own
        // LoadAsync calls after each lifecycle action.
        Service.Machines.CollectionChanged += (_, _) => RebuildRows();

        // Forward the service's own state flags so a single subscription on this view model's
        // PropertyChanged (see MachinesPage) is enough to catch every state change that should
        // repaint the page (loading/empty/unavailable states).
        Service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MachineService.IsLoading) or nameof(MachineService.ApiUnavailable))
                OnPropertyChanged(nameof(Service));
        };

        RebuildRows();
    }

    partial void OnSelectedMachineIdChanged(string? value) => OnPropertyChanged(nameof(SelectedMachine));

    public Task LoadAsync() => Service.LoadAsync();

    public async Task BootSelectedAsync()
    {
        if (SelectedMachine is { } m) await Service.BootAsync(m.Id);
    }

    public async Task StopSelectedAsync()
    {
        if (SelectedMachine is { } m) await Service.StopAsync(m.Id);
    }

    public async Task SetDefaultSelectedAsync()
    {
        if (SelectedMachine is { } m) await Service.SetDefaultAsync(m.Id);
    }

    public async Task DeleteSelectedAsync()
    {
        if (SelectedMachine is { } m) await Service.DeleteAsync(m.Id);
    }

    /// Applies an edited boot config to the currently selected machine. `restartNow` is the
    /// port's single-checkbox stand-in for the Swift original's separate "Apply" / "Apply &
    /// Restart" buttons - see MachineService.ApplyConfigAsync.
    public async Task<bool> ApplyConfigAsync(MachineConfigSpec config, bool restartNow)
    {
        if (SelectedMachine is not { } m) return false;
        return await Service.ApplyConfigAsync(config, m.Id, restartNow);
    }

    private void RebuildRows()
    {
        var rows = Service.Machines
            .Select(m => new MachineRow(
                Id: m.Id,
                PrimaryText: m.Id,
                SecondaryLeftText: Capitalize(m.Status) + (m.IsDefault ? "  (default)" : ""),
                SecondaryRightText: m.IpAddress ?? "—",
                IconColor: m.IsRunning ? Colors.Green : Colors.Gray))
            .ToList();
        MachineRows = new ObservableCollection<MachineRow>(rows);
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
