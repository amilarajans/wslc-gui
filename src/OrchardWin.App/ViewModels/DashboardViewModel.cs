using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// One utilisation table row. Mutable <see cref="ObservableObject"/> (not a record) so
/// stats ticks update existing rows/sparklines in place instead of replacing the whole
/// list — replacing <see cref="ObservableCollection{T}"/> every sample was flashing the
/// dashboard ListView.
public sealed partial class UtilisationRow : ObservableObject
{
    public UtilisationRow(string id)
    {
        Id = id;
        Name = id;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _status = "unknown";

    [ObservableProperty]
    private double _cpuPercent;

    [ObservableProperty]
    private double _memoryPercent;

    [ObservableProperty]
    private string _cpuPercentText = "0%";

    [ObservableProperty]
    private string _memoryPercentText = "0%";

    [ObservableProperty]
    private IReadOnlyList<double> _cpuHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _memoryHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _networkHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _diskHistory = Array.Empty<double>();

    public void Apply(
        string name,
        string status,
        double cpuPercent,
        double memoryPercent,
        IReadOnlyList<double> cpuHistory,
        IReadOnlyList<double> memoryHistory,
        IReadOnlyList<double> networkHistory,
        IReadOnlyList<double> diskHistory)
    {
        Name = name;
        Status = status;
        CpuPercent = cpuPercent;
        MemoryPercent = memoryPercent;
        CpuPercentText = $"{cpuPercent:F0}%";
        MemoryPercentText = $"{memoryPercent:F0}%";
        // Only replace series references when content changed — avoids redundant Sparkline redraws.
        if (!HistoryEquals(CpuHistory, cpuHistory)) CpuHistory = cpuHistory;
        if (!HistoryEquals(MemoryHistory, memoryHistory)) MemoryHistory = memoryHistory;
        if (!HistoryEquals(NetworkHistory, networkHistory)) NetworkHistory = networkHistory;
        if (!HistoryEquals(DiskHistory, diskHistory)) DiskHistory = diskHistory;
    }

    private static bool HistoryEquals(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i] - b[i]) > 0.0001) return false;
        }
        return true;
    }
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly SynchronizationContext? _ui;
    private int _refreshState; // 0 idle, 1 scheduled, 2 running (needs another pass)

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
        // Constructed from the page on the UI thread — capture so stats timer callbacks
        // (thread-pool) can marshal ObservableCollection updates safely.
        _ui = SynchronizationContext.Current;

        // Coalesce multi-property change storms from StatsService (ContainerStats +
        // MachineStats + IsStatsLoading each tick) into a single Refresh.
        _services.StatsService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(StatsService.ContainerStats)
                or nameof(StatsService.MachineStats)
                or nameof(StatsService.IsStatsLoading))
            {
                QueueRefresh();
            }
        };
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers))
                QueueRefresh();
        };
        _services.MachineService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(MachineService.Machines))
                QueueRefresh();
        };
        _services.SystemService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemService.SystemDiskUsage))
            {
                RunOnUi(() => DiskUsage = _services.SystemService.SystemDiskUsage);
            }
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

    /// Collapse rapid PropertyChanged bursts into one UI-thread Refresh.
    private void QueueRefresh()
    {
        // 0→1 schedule; if already scheduled/running, mark "run again" (1 or 2 → 2).
        var prior = Interlocked.Exchange(ref _refreshState, 1);
        if (prior != 0)
        {
            Interlocked.Exchange(ref _refreshState, 2);
            return;
        }

        RunOnUi(DrainRefresh);
    }

    private void DrainRefresh()
    {
        do
        {
            Interlocked.Exchange(ref _refreshState, 1);
            Refresh();
        }
        while (Interlocked.CompareExchange(ref _refreshState, 0, 1) != 1);
    }

    private void RunOnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        _ui.Post(_ => action(), null);
    }

    private void Refresh()
    {
        StatsUnavailable = _services.StatsService.StatsUnavailable;

        var containers = _services.ContainerListService.Containers;
        SyncRows(
            ContainerRows,
            _services.StatsService.ContainerStats.Select(stats =>
            {
                var container = containers.FirstOrDefault(c => c.Configuration.Id == stats.Id);
                var history = _services.StatsService.History.Samples(new StatsKey(stats.Id));
                var recent = history.Count > 60 ? history.TakeLast(60).ToList() : history.ToList();
                var sample = _services.StatsService.LatestSamples.GetValueOrDefault(stats.Id);
                var name = !string.IsNullOrWhiteSpace(container?.Configuration.Hostname)
                    ? container!.Configuration.Hostname!
                    : ShortId(stats.Id);
                return (
                    Id: stats.Id,
                    Name: name,
                    Status: container?.Status ?? "unknown",
                    Cpu: sample?.CpuPercent ?? 0,
                    Mem: sample?.MemoryPercent ?? stats.MemoryUsagePercent,
                    CpuHist: (IReadOnlyList<double>)recent.Select(s => s.CpuPercent).ToList(),
                    MemHist: (IReadOnlyList<double>)recent.Select(s => s.MemoryPercent).ToList(),
                    NetHist: (IReadOnlyList<double>)recent.Select(s => (s.NetworkRxPerSec + s.NetworkTxPerSec) / 1024).ToList(),
                    DiskHist: (IReadOnlyList<double>)recent.Select(s => (s.BlockReadPerSec + s.BlockWritePerSec) / 1024).ToList());
            }).ToList());

        SyncRows(
            MachineRows,
            _services.StatsService.MachineStats.Select(stats =>
            {
                var machine = _services.MachineService.Machines.FirstOrDefault(m => m.Id == stats.Id);
                var history = _services.StatsService.MachineHistory(stats.Id);
                var recent = history.Count > 60 ? history.TakeLast(60).ToList() : history.ToList();
                var sample = _services.StatsService.MachineSample(stats.Id);
                return (
                    Id: stats.Id,
                    Name: stats.Id,
                    Status: machine?.Status ?? "unknown",
                    Cpu: sample?.CpuPercent ?? 0,
                    Mem: sample?.MemoryPercent ?? stats.MemoryUsagePercent,
                    CpuHist: (IReadOnlyList<double>)recent.Select(s => s.CpuPercent).ToList(),
                    MemHist: (IReadOnlyList<double>)recent.Select(s => s.MemoryPercent).ToList(),
                    NetHist: (IReadOnlyList<double>)recent.Select(s => (s.NetworkRxPerSec + s.NetworkTxPerSec) / 1024).ToList(),
                    DiskHist: (IReadOnlyList<double>)recent.Select(s => (s.BlockReadPerSec + s.BlockWritePerSec) / 1024).ToList());
            }).ToList());
    }

    /// Update existing rows in place; only add/remove when the id set changes.
    private static void SyncRows(
        ObservableCollection<UtilisationRow> target,
        IReadOnlyList<(
            string Id,
            string Name,
            string Status,
            double Cpu,
            double Mem,
            IReadOnlyList<double> CpuHist,
            IReadOnlyList<double> MemHist,
            IReadOnlyList<double> NetHist,
            IReadOnlyList<double> DiskHist)> next)
    {
        var nextById = next.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var nextIds = next.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        // Remove rows that disappeared (iterate backwards for index stability).
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!nextIds.Contains(target[i].Id))
                target.RemoveAt(i);
        }

        // Update existing / insert new in next order.
        for (var i = 0; i < next.Count; i++)
        {
            var data = next[i];
            var existingIndex = IndexOfId(target, data.Id);
            if (existingIndex < 0)
            {
                var row = new UtilisationRow(data.Id);
                row.Apply(data.Name, data.Status, data.Cpu, data.Mem, data.CpuHist, data.MemHist, data.NetHist, data.DiskHist);
                if (i >= target.Count) target.Add(row);
                else target.Insert(i, row);
            }
            else
            {
                var row = target[existingIndex];
                row.Apply(data.Name, data.Status, data.Cpu, data.Mem, data.CpuHist, data.MemHist, data.NetHist, data.DiskHist);
                if (existingIndex != i)
                {
                    target.Move(existingIndex, i);
                }
            }
        }
    }

    private static int IndexOfId(ObservableCollection<UtilisationRow> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Id, id, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;
}
