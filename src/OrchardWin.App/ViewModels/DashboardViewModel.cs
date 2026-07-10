using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// One row in the container/machine utilisation table: the raw stats plus the derived
/// sparkline series the row's cells bind to. Recomputed each refresh tick from
/// AppServices.StatsService's published collections + history - thin glue, no state of its
/// own beyond what the service already tracks.
public sealed record UtilisationRow(
    string Name,
    string Status,
    ContainerStats Stats,
    double CpuPercent,
    double MemoryPercent,
    IReadOnlyList<double> CpuHistory,
    IReadOnlyList<double> MemoryHistory,
    IReadOnlyList<double> NetworkHistory,
    IReadOnlyList<double> DiskHistory)
{
    // x:Bind requires exact type matches for TextBlock.Text without a converter - expose
    // pre-formatted strings rather than relying on implicit double->string conversion.
    public string CpuPercentText => $"{CpuPercent:F0}%";
    public string MemoryPercentText => $"{MemoryPercent:F0}%";
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private ObservableCollection<UtilisationRow> _containerRows = [];

    [ObservableProperty]
    private ObservableCollection<UtilisationRow> _machineRows = [];

    [ObservableProperty]
    private bool _statsUnavailable;

    [ObservableProperty]
    private SystemDiskUsage? _diskUsage;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        // Rebuild derived rows whenever the underlying stats/container lists change, rather
        // than polling ourselves - StatsService/ContainerListService/MachineService already
        // self-refresh on their own timers/polls.
        _services.StatsService.PropertyChanged += (_, _) => Refresh();
        _services.ContainerListService.PropertyChanged += (_, _) => Refresh();
        _services.MachineService.PropertyChanged += (_, _) => Refresh();
        _services.SystemService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Core.Services.SystemService.SystemDiskUsage))
                DiskUsage = _services.SystemService.SystemDiskUsage;
        };
    }

    public async Task LoadAsync()
    {
        await _services.SystemService.LoadSystemDiskUsageAsync();
        DiskUsage = _services.SystemService.SystemDiskUsage;
        Refresh();
    }

    public async Task RefreshDiskUsageQuietAsync()
    {
        await _services.SystemService.LoadSystemDiskUsageAsync(showLoading: false);
        DiskUsage = _services.SystemService.SystemDiskUsage;
    }

    private void Refresh()
    {
        StatsUnavailable = _services.StatsService.StatsUnavailable;

        var containers = _services.ContainerListService.Containers;
        var rows = new List<UtilisationRow>();
        foreach (var stats in _services.StatsService.ContainerStats)
        {
            var container = containers.FirstOrDefault(c => c.Configuration.Id == stats.Id);
            var history = _services.StatsService.History.Samples(new StatsKey(stats.Id));
            var recent = history.Count > 60 ? history.TakeLast(60).ToList() : history;
            var sample = _services.StatsService.LatestSamples.GetValueOrDefault(stats.Id);
            rows.Add(new UtilisationRow(
                Name: stats.Id,
                Status: container?.Status ?? "unknown",
                Stats: stats,
                CpuPercent: sample?.CpuPercent ?? 0,
                MemoryPercent: sample?.MemoryPercent ?? stats.MemoryUsagePercent,
                CpuHistory: recent.Select(s => s.CpuPercent).ToList(),
                MemoryHistory: recent.Select(s => s.MemoryPercent).ToList(),
                NetworkHistory: recent.Select(s => (s.NetworkRxPerSec + s.NetworkTxPerSec) / 1024).ToList(),
                DiskHistory: recent.Select(s => (s.BlockReadPerSec + s.BlockWritePerSec) / 1024).ToList()));
        }
        ContainerRows = new ObservableCollection<UtilisationRow>(rows);

        var machineRows = new List<UtilisationRow>();
        foreach (var stats in _services.StatsService.MachineStats)
        {
            var machine = _services.MachineService.Machines.FirstOrDefault(m => m.Id == stats.Id);
            var history = _services.StatsService.MachineHistory(stats.Id);
            var recent = history.Count > 60 ? history.TakeLast(60).ToList() : history;
            var sample = _services.StatsService.MachineSample(stats.Id);
            machineRows.Add(new UtilisationRow(
                Name: stats.Id,
                Status: machine?.Status ?? "unknown",
                Stats: stats,
                CpuPercent: sample?.CpuPercent ?? 0,
                MemoryPercent: sample?.MemoryPercent ?? stats.MemoryUsagePercent,
                CpuHistory: recent.Select(s => s.CpuPercent).ToList(),
                MemoryHistory: recent.Select(s => s.MemoryPercent).ToList(),
                NetworkHistory: recent.Select(s => (s.NetworkRxPerSec + s.NetworkTxPerSec) / 1024).ToList(),
                DiskHistory: recent.Select(s => (s.BlockReadPerSec + s.BlockWritePerSec) / 1024).ToList()));
        }
        MachineRows = new ObservableCollection<UtilisationRow>(machineRows);
    }
}
