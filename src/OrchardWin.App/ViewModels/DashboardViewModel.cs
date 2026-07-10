using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// Time window for System charts — mirrors Orchard's StatsWindow (5m / 15m / 1h / 24h).
public enum StatsWindow
{
    FiveMin,
    FifteenMin,
    OneHour,
    TwentyFourHours,
}

public static class StatsWindowExtensions
{
    public static string Label(this StatsWindow w) => w switch
    {
        StatsWindow.FiveMin => "5m",
        StatsWindow.FifteenMin => "15m",
        StatsWindow.OneHour => "1h",
        StatsWindow.TwentyFourHours => "24h",
        _ => w.ToString(),
    };

    public static TimeSpan Duration(this StatsWindow w) => w switch
    {
        StatsWindow.FiveMin => TimeSpan.FromMinutes(5),
        StatsWindow.FifteenMin => TimeSpan.FromMinutes(15),
        StatsWindow.OneHour => TimeSpan.FromHours(1),
        StatsWindow.TwentyFourHours => TimeSpan.FromHours(24),
        _ => TimeSpan.FromMinutes(5),
    };
}

/// One utilisation table row. Mutable so stats ticks update in place (no ListView flicker).
public sealed partial class UtilisationRow : ObservableObject
{
    public UtilisationRow(string id)
    {
        Id = id;
        Name = id;
    }

    public string Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _status = "unknown";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _cpuPercentText = "--";
    [ObservableProperty] private string _memoryText = "--";
    [ObservableProperty] private string _memoryPercentText = "";
    [ObservableProperty] private string _networkRxText = "↓ --";
    [ObservableProperty] private string _networkTxText = "↑ --";
    [ObservableProperty] private string _blockReadText = "R --";
    [ObservableProperty] private string _blockWriteText = "W --";
    [ObservableProperty] private string _pidsText = "--";
    [ObservableProperty] private IReadOnlyList<double> _cpuHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _memoryHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _networkHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _diskHistory = Array.Empty<double>();

    public void Apply(
        string name,
        string status,
        bool isRunning,
        double cpuPercent,
        double memoryPercent,
        string memoryText,
        string networkRxText,
        string networkTxText,
        string blockReadText,
        string blockWriteText,
        string pidsText,
        IReadOnlyList<double> cpuHistory,
        IReadOnlyList<double> memoryHistory,
        IReadOnlyList<double> networkHistory,
        IReadOnlyList<double> diskHistory)
    {
        Name = name;
        Status = status;
        IsRunning = isRunning;
        CpuPercent = cpuPercent;
        MemoryPercent = memoryPercent;
        CpuPercentText = isRunning ? $"{cpuPercent:F1}%" : "--";
        MemoryText = memoryText;
        MemoryPercentText = isRunning ? $"{memoryPercent:F1}%" : "";
        NetworkRxText = networkRxText;
        NetworkTxText = networkTxText;
        BlockReadText = blockReadText;
        BlockWriteText = blockWriteText;
        PidsText = pidsText;
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
            if (Math.Abs(a[i] - b[i]) > 0.0001) return false;
        return true;
    }
}

/// System-wide aggregate snapshot for the 2×2 System charts (Orchard SystemStatsDashboard).
public sealed partial class SystemMetricsSnapshot : ObservableObject
{
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _cpuPrimary = "--";
    [ObservableProperty] private string _cpuSecondary = "";
    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private string _memoryPrimary = "--";
    [ObservableProperty] private string _memorySecondary = "";
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private long _memoryBytes;
    [ObservableProperty] private long _memoryLimitBytes;
    [ObservableProperty] private string _networkRxText = "↓ 0.0 MB/s";
    [ObservableProperty] private string _networkTxText = "↑ 0.0 MB/s";
    [ObservableProperty] private string _diskReadText = "R 0 KB/s";
    [ObservableProperty] private string _diskWriteText = "W 0 KB/s";
    [ObservableProperty] private IReadOnlyList<double> _cpuSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _memorySeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _networkRxSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _networkTxSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _diskReadSeries = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _diskWriteSeries = Array.Empty<double>();
    [ObservableProperty] private int _reservedCores;
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly SynchronizationContext? _ui;
    private int _refreshState;

    [ObservableProperty] private ObservableCollection<UtilisationRow> _containerRows = [];
    [ObservableProperty] private ObservableCollection<UtilisationRow> _machineRows = [];
    [ObservableProperty] private bool _statsUnavailable;
    [ObservableProperty] private SystemDiskUsage? _diskUsage;
    [ObservableProperty] private StatsWindow _selectedWindow = StatsWindow.FiveMin;
    [ObservableProperty] private SystemMetricsSnapshot _systemMetrics = new();
    [ObservableProperty] private string _emptyContainersMessage = "No running containers or stats unavailable";

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        _ui = SynchronizationContext.Current;

        _services.StatsService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(StatsService.ContainerStats)
                or nameof(StatsService.MachineStats)
                or nameof(StatsService.IsStatsLoading))
                QueueRefresh();
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
                RunOnUi(() => DiskUsage = _services.SystemService.SystemDiskUsage);
        };
    }

    partial void OnSelectedWindowChanged(StatsWindow value) => QueueRefresh();

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

    public void SetWindow(StatsWindow window) => SelectedWindow = window;

    private void QueueRefresh()
    {
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
        if (_ui is null || SynchronizationContext.Current == _ui) { action(); return; }
        _ui.Post(_ => action(), null);
    }

    private void Refresh()
    {
        StatsUnavailable = _services.StatsService.StatsUnavailable;
        EmptyContainersMessage = _services.StatsService.IsStatsLoading
            ? "Loading container statistics..."
            : "No running containers or stats unavailable";

        RefreshSystemMetrics();

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
                var isRunning = string.Equals(container?.Status, "running", StringComparison.OrdinalIgnoreCase)
                    || sample is not null;
                var netSample = sample;
                return BuildRowData(
                    stats.Id, name, container?.Status ?? "unknown", isRunning,
                    sample?.CpuPercent ?? 0,
                    sample?.MemoryPercent ?? stats.MemoryUsagePercent,
                    stats, netSample, recent);
            }).ToList());

        SyncRows(
            MachineRows,
            _services.StatsService.MachineStats.Select(stats =>
            {
                var machine = _services.MachineService.Machines.FirstOrDefault(m => m.Id == stats.Id);
                var history = _services.StatsService.MachineHistory(stats.Id);
                var recent = history.Count > 60 ? history.TakeLast(60).ToList() : history.ToList();
                var sample = _services.StatsService.MachineSample(stats.Id);
                var isRunning = machine?.IsRunning == true || sample is not null;
                return BuildRowData(
                    stats.Id, stats.Id, machine?.Status ?? "unknown", isRunning,
                    sample?.CpuPercent ?? 0,
                    sample?.MemoryPercent ?? stats.MemoryUsagePercent,
                    stats, sample, recent);
            }).ToList());
    }

    private void RefreshSystemMetrics()
    {
        var reserved = _services.ContainerListService.Containers
            .Where(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase))
            .Sum(c => Math.Max(0, c.Configuration.Resources.Cpus));

        var cutoff = DateTimeOffset.Now - SelectedWindow.Duration();
        var all = _services.StatsService.History.AllSamples();
        // Exclude machine:: keys from system aggregate (container fleet only).
        var containerHistories = all
            .Select(series => (IEnumerable<StatsSample>)series.Where(s => s.Timestamp >= cutoff))
            .Where(s => s.Any())
            .ToList();

        // Prefer series that belong to current container stats ids.
        var containerIds = _services.StatsService.ContainerStats.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (containerIds.Count > 0)
        {
            var keyed = new List<IEnumerable<StatsSample>>();
            foreach (var id in containerIds)
            {
                var samples = _services.StatsService.History.Samples(new StatsKey(id))
                    .Where(s => s.Timestamp >= cutoff);
                if (samples.Any()) keyed.Add(samples);
            }
            if (keyed.Count > 0) containerHistories = keyed;
        }

        var aggregate = StatsMath.Aggregate(containerHistories);
        if (aggregate.Count < 2)
        {
            SystemMetrics.HasData = false;
            SystemMetrics.ReservedCores = reserved;
            SystemMetrics.CpuPrimary = "--";
            SystemMetrics.CpuSecondary = reserved > 0 ? $"{reserved} cores reserved" : "";
            SystemMetrics.MemoryPrimary = "--";
            SystemMetrics.MemorySecondary = "";
            SystemMetrics.CpuSeries = Array.Empty<double>();
            SystemMetrics.MemorySeries = Array.Empty<double>();
            SystemMetrics.NetworkRxSeries = Array.Empty<double>();
            SystemMetrics.NetworkTxSeries = Array.Empty<double>();
            SystemMetrics.DiskReadSeries = Array.Empty<double>();
            SystemMetrics.DiskWriteSeries = Array.Empty<double>();
            return;
        }

        var latest = aggregate[^1];
        SystemMetrics.HasData = true;
        SystemMetrics.ReservedCores = reserved;
        SystemMetrics.CpuPercent = latest.CpuPercent;
        SystemMetrics.CpuPrimary = $"{latest.CpuPercent:F0}%";
        SystemMetrics.CpuSecondary = $"{reserved} {(reserved == 1 ? "core" : "cores")} reserved";
        SystemMetrics.MemoryBytes = latest.MemoryBytes;
        SystemMetrics.MemoryLimitBytes = latest.MemoryLimitBytes;
        SystemMetrics.MemoryPercent = latest.MemoryPercent;
        SystemMetrics.MemoryPrimary = ByteFormat.Memory(latest.MemoryBytes);
        SystemMetrics.MemorySecondary = latest.MemoryLimitBytes > 0
            ? $"of {ByteFormat.Memory(latest.MemoryLimitBytes)}"
            : "";
        SystemMetrics.NetworkRxText = $"↓ {latest.NetworkRxPerSec / 1_048_576:F1} MB/s";
        SystemMetrics.NetworkTxText = $"↑ {latest.NetworkTxPerSec / 1_048_576:F1} MB/s";
        SystemMetrics.DiskReadText = $"R {latest.BlockReadPerSec / 1024:F0} KB/s";
        SystemMetrics.DiskWriteText = $"W {latest.BlockWritePerSec / 1024:F0} KB/s";
        SystemMetrics.CpuSeries = aggregate.Select(s => s.CpuPercent).ToList();
        SystemMetrics.MemorySeries = aggregate.Select(s => (double)s.MemoryBytes).ToList();
        SystemMetrics.NetworkRxSeries = aggregate.Select(s => s.NetworkRxPerSec / 1_048_576).ToList();
        SystemMetrics.NetworkTxSeries = aggregate.Select(s => s.NetworkTxPerSec / 1_048_576).ToList();
        SystemMetrics.DiskReadSeries = aggregate.Select(s => s.BlockReadPerSec / 1024).ToList();
        SystemMetrics.DiskWriteSeries = aggregate.Select(s => s.BlockWritePerSec / 1024).ToList();
    }

    private static (
        string Id, string Name, string Status, bool IsRunning,
        double Cpu, double Mem, string MemText,
        string NetRx, string NetTx, string BlkR, string BlkW, string Pids,
        IReadOnlyList<double> CpuHist, IReadOnlyList<double> MemHist,
        IReadOnlyList<double> NetHist, IReadOnlyList<double> DiskHist)
        BuildRowData(
            string id, string name, string status, bool isRunning,
            double cpu, double mem, ContainerStats stats, StatsSample? sample,
            List<StatsSample> recent)
    {
        // Cumulative totals for display (Orchard shows lifetime totals with sparkline of rates).
        return (
            id, name, status, isRunning, cpu, mem,
            ByteFormat.Memory(stats.MemoryUsageBytes),
            $"↓ {ByteFormat.String((long)stats.NetworkRxBytes)}",
            $"↑ {ByteFormat.String((long)stats.NetworkTxBytes)}",
            $"R {ByteFormat.String(stats.BlockReadBytes)}",
            $"W {ByteFormat.String(stats.BlockWriteBytes)}",
            stats.NumProcesses.ToString(),
            recent.Select(s => s.CpuPercent).ToList(),
            recent.Select(s => s.MemoryPercent).ToList(),
            recent.Select(s => (s.NetworkRxPerSec + s.NetworkTxPerSec) / 1024).ToList(),
            recent.Select(s => (s.BlockReadPerSec + s.BlockWritePerSec) / 1024).ToList());
    }

    private static void SyncRows(
        ObservableCollection<UtilisationRow> target,
        IReadOnlyList<(
            string Id, string Name, string Status, bool IsRunning,
            double Cpu, double Mem, string MemText,
            string NetRx, string NetTx, string BlkR, string BlkW, string Pids,
            IReadOnlyList<double> CpuHist, IReadOnlyList<double> MemHist,
            IReadOnlyList<double> NetHist, IReadOnlyList<double> DiskHist)> next)
    {
        var nextIds = next.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!nextIds.Contains(target[i].Id))
                target.RemoveAt(i);
        }

        for (var i = 0; i < next.Count; i++)
        {
            var data = next[i];
            var existingIndex = IndexOfId(target, data.Id);
            if (existingIndex < 0)
            {
                var row = new UtilisationRow(data.Id);
                row.Apply(data.Name, data.Status, data.IsRunning, data.Cpu, data.Mem, data.MemText,
                    data.NetRx, data.NetTx, data.BlkR, data.BlkW, data.Pids,
                    data.CpuHist, data.MemHist, data.NetHist, data.DiskHist);
                if (i >= target.Count) target.Add(row);
                else target.Insert(i, row);
            }
            else
            {
                var row = target[existingIndex];
                row.Apply(data.Name, data.Status, data.IsRunning, data.Cpu, data.Mem, data.MemText,
                    data.NetRx, data.NetTx, data.BlkR, data.BlkW, data.Pids,
                    data.CpuHist, data.MemHist, data.NetHist, data.DiskHist);
                if (existingIndex != i) target.Move(existingIndex, i);
            }
        }
    }

    private static int IndexOfId(ObservableCollection<UtilisationRow> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
            if (string.Equals(rows[i].Id, id, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static string ShortId(string id) => id.Length > 12 ? id[..12] : id;
}
