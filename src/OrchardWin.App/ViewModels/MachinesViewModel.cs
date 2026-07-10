using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the machines list.
public sealed record MachineRow(
    string Id,
    string PrimaryText,
    string SecondaryLeftText,
    string SecondaryRightText,
    Color IconColor);

/// Thin glue for Machines: selection by id + stable row projection (no collection replace).
public sealed partial class MachinesViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MachineService Service => _services.MachineService;

    public ObservableCollection<MachineRow> MachineRows { get; } = [];

    [ObservableProperty]
    private string? _selectedMachineId;

    public Machine? SelectedMachine => Service.Machines.FirstOrDefault(m => m.Id == SelectedMachineId);

    public MachinesViewModel(AppServices services)
    {
        _services = services;
        Service.Machines.CollectionChanged += (_, _) => RebuildRows();
        Service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MachineService.IsLoading)
                or nameof(MachineService.ApiUnavailable)
                or nameof(MachineService.Machines))
            {
                RebuildRows();
                OnPropertyChanged(nameof(Service));
                OnPropertyChanged(nameof(SelectedMachine));
            }
        };
        RebuildRows();
    }

    partial void OnSelectedMachineIdChanged(string? value) => OnPropertyChanged(nameof(SelectedMachine));

    public async Task LoadAsync()
    {
        await Service.LoadAsync();
        RebuildRows();
    }

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

        if (ObservableCollectionSync.Sync(MachineRows, rows, (a, b) =>
                string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                && string.Equals(a.SecondaryLeftText, b.SecondaryLeftText, StringComparison.Ordinal)
                && string.Equals(a.SecondaryRightText, b.SecondaryRightText, StringComparison.Ordinal)
                && a.IconColor.Equals(b.IconColor)))
        {
            OnPropertyChanged(nameof(MachineRows));
        }

        OnPropertyChanged(nameof(SelectedMachine));
    }

    private static string Capitalize(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
