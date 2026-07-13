using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Services.Backends;
using ContainerStatsModel = OrchardWin.Core.Models.ContainerStats;

namespace OrchardWin.Core.Services;

/// A running machine's sampling target: sample its *backing container* but record/report
/// the result under the stable *machine* id (via <see cref="StatsService.MachineStatKey"/>),
/// since the backing container id changes across reboots and would otherwise fork history.
/// Supplied lazily via <see cref="StatsService.MachineStatTargets"/> so the sampler always
/// sees the current machines - wired from MachineService in AppServices, mirroring the
/// Swift original's `machineStatTargets` closure.
public sealed record MachineStatTarget(string MachineId, string BackingId, int Cpus);

/// Owns per-container resource stats. Reads the running containers from the container list
/// (owned by <see cref="ContainerListService"/>), fetches stats for each, derives plottable
/// samples, accumulates history, and persists it across launches. Ported 1:1 from Orchard's
/// `StatsService.swift` for the rate math / recording / retention-eviction logic.
///
/// Deliberately NOT ported: the AppKit/SwiftUI visibility-gated sampling cadence (occlusion
/// state, `beginSampling`/`endSampling` consumer ref-counting, a separate menu-bar-open
/// signal, a fast-vs-idle interval switch). That machinery exists on macOS to save
/// battery/XPC calls while nothing is on screen. A Windows tray app polling every
/// <see cref="SamplingInterval"/> is cheap enough to just always run once activated - see
/// ARCHITECTURE.md's port notes for this file.
public sealed partial class StatsService : ObservableObject, IDisposable
{
    /// Always-on sampling cadence - replaces Swift's fast (2s, view visible) / idle (10s,
    /// backgrounded) split with a single rate, since there's no cheap visibility signal to
    /// gate on here. 1s keeps dashboard / container detail charts feeling live.
    public static readonly TimeSpan SamplingInterval = TimeSpan.FromSeconds(1);

    private const string MachineKeyPrefix = "machine::";

    private readonly IContainerBackend _backend;
    private readonly AlertCenter _alertCenter;
    private readonly ContainerListService _containerList;
    private readonly StatsPersistence _persistence;

    [ObservableProperty]
    private ObservableCollection<ContainerStatsModel> _containerStats = [];

    [ObservableProperty]
    private bool _isStatsLoading;

    /// <summary>
    /// Monotonic UI pulse, incremented once per <see cref="SamplingInterval"/> whether or not
    /// a sample batch produced new data. Dashboard, container detail, tray, and sparklines
    /// subscribe so charts keep redrawing (zero baseline when empty).
    /// </summary>
    [ObservableProperty]
    private long _tickRevision;

    // Dictionary<string, StatsSample> in the Swift original - hand-rolled (rather than
    // [ObservableProperty]) because it's mutated key-by-key from RecordSamples, not
    // wholesale-replaced.
    private readonly Dictionary<string, StatsSample> _latestSamples = new();

    /// Latest derived sample per container id - drives the table's real CPU% and the
    /// current-value cards. Absent for a container until it has two raw reads.
    public IReadOnlyDictionary<string, StatsSample> LatestSamples => _latestSamples;

    /// Latest raw stats per **machine id** (re-keyed off the backing container). Container
    /// machines are sampled through their backing container but tracked under the stable
    /// machine id so history survives the backing id changing across reboots.
    [ObservableProperty]
    private ObservableCollection<ContainerStatsModel> _machineStats = [];

    /// Supplies the running machines to sample each tick. Wired from `MachineService` in
    /// AppServices; empty until then.
    public Func<IReadOnlyList<MachineStatTarget>> MachineStatTargets { get; set; } = static () => [];

    /// The internal sampling key for a machine - namespaced so it never collides with a
    /// container id in the shared history/sample maps.
    public static string MachineStatKey(string machineId) => $"{MachineKeyPrefix}{machineId}";

    /// Accumulated time-series history, keyed `(Host, Id)`. Survives view switches and (via
    /// `_persistence`) app relaunches. Read by charts.
    public StatsHistoryStore History { get; } = new();

    public StatsService(
        IContainerBackend backend,
        AlertCenter alertCenter,
        ContainerListService containerList,
        StatsPersistence? persistence = null)
    {
        _backend = backend;
        _alertCenter = alertCenter;
        _containerList = containerList;
        _persistence = persistence ?? new StatsPersistence();
    }

    private bool _isRefreshing;

    // MARK: - Sampling

    /// Previous raw read per container id, with the monotonic instant (in
    /// `Environment.TickCount64` milliseconds) it was taken - the other half of each
    /// `StatsMath.ComputeSample` call. Monotonic so rates ignore clock changes.
    ///
    /// NOTE (multi-host): this and `_latestSamples` key on the bare container id, while
    /// `History` keys on `StatsKey(Host, Id)`. Today `Host` is always local so they align,
    /// but multi-host support must re-key these to `(Host, Id)` too - otherwise two hosts'
    /// same-id containers would share one rate baseline here and delta across each other.
    private readonly Dictionary<string, (ContainerStatsModel Stats, long MonotonicMs)> _previousRaw = new();

    private System.Threading.Timer? _samplingTimer;
    private bool _backgroundSamplingEnabled;
    private int _ticksSinceSave;

    /// Start always-on background sampling and restore persisted history. Call once at app
    /// launch (not from the constructor, so unit tests that build the service stay
    /// side-effect free). Idempotent.
    public void Activate()
    {
        if (_backgroundSamplingEnabled) return;
        _backgroundSamplingEnabled = true;

        // Start sampling immediately - don't block on disk I/O first.
        _samplingTimer = new System.Threading.Timer(OnTimerTick, null, SamplingInterval, SamplingInterval);

        // Load persisted history off-thread, then merge it into whatever the live sampler
        // has already recorded. `_latestSamples` is deliberately NOT seeded: restored
        // samples can be up to 24h old and must never render as a live "current" reading -
        // consumers stay in their "--"/"Collecting…" state until the first fresh tick.
        var persistence = _persistence;
        var history = History;
        _ = Task.Run(() =>
        {
            var restored = persistence.Load();
            history.MergeRestored(restored);
        });

        // Best-effort save on process exit - a clean UI-driven shutdown should call
        // `Shutdown()` directly instead, since `ProcessExit` handlers run under a tight,
        // unguaranteed time budget.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// Explicit shutdown hook for the UI layer's own exit path - persists synchronously.
    public void Shutdown() => PersistNow(inBackground: false);

    private void OnProcessExit(object? sender, EventArgs e) => PersistNow(inBackground: false);

    private void OnTimerTick(object? state) => _ = TickAsync();

    private async Task TickAsync()
    {
        try
        {
            await LoadAsync(showLoading: false);
        }
        finally
        {
            if (_backgroundSamplingEnabled)
            {
                // Always pulse so UI charts refresh even when LoadAsync no-ops (overlap) or
                // returns empty results (no running containers / stats errors).
                TickRevision++;

                // Persist roughly once a minute; a clean shutdown also saves via Shutdown()/ProcessExit.
                _ticksSinceSave++;
                var savesEvery = Math.Max(1, (int)Math.Round(60.0 / Math.Max(SamplingInterval.TotalSeconds, 1)));
                if (_ticksSinceSave >= savesEvery)
                {
                    _ticksSinceSave = 0;
                    PersistNow(inBackground: true);
                }
            }
        }
    }

    private void PersistNow(bool inBackground)
    {
        var snapshot = History.Snapshot();
        if (inBackground)
        {
            _ = Task.Run(() => TrySave(snapshot));
        }
        else
        {
            TrySave(snapshot);
        }

        void TrySave(Dictionary<StatsKey, List<StatsSample>> s)
        {
            try { _persistence.Save(s); }
            catch (Exception error) { Log.Containers.Error($"Failed to persist stats history: {error.Message}"); }
        }
    }

    public async Task LoadAsync(bool showLoading = true, CancellationToken ct = default)
    {
        // Overlapping loads must not pile up if one runs slow.
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            if (showLoading)
            {
                IsStatsLoading = true;
                _alertCenter.Dismiss();
            }

            var running = _containerList.Containers.Where(c => c.Status == "running").ToList();
            var runningIds = running.Select(c => c.Configuration.Id).ToList();

            // Allocated cores per container/machine - the CPU% denominator for ComputeSample.
            var cpuCounts = new Dictionary<string, int>();
            foreach (var container in running)
            {
                cpuCounts.TryAdd(container.Configuration.Id, container.Configuration.Resources.Cpus);
            }

            // Running machines are sampled through their backing container, but re-keyed
            // onto the stable machine id so a reboot (which changes the backing id) doesn't
            // fork history.
            var machineTargets = MachineStatTargets();
            foreach (var target in machineTargets)
            {
                cpuCounts[MachineStatKey(target.MachineId)] = target.Cpus;
            }

            // Fetch every container's/machine's stats concurrently rather than serially.
            var containerResultsTask = FetchStatsAsync(runningIds, ct);
            var machineResultsTask = FetchMachineStatsAsync(machineTargets, ct);
            await Task.WhenAll(containerResultsTask, machineResultsTask);
            var containerResults = containerResultsTask.Result;
            var machineResults = machineResultsTask.Result;

            RecordSamples(containerResults.Concat(machineResults), cpuCounts);

            // Mutate in place — replacing ObservableCollection every tick forces every listener
            // to treat the list as brand-new (dashboard/containers flicker).
            var machineMapped = machineResults
                .Select(s => s.With(s.Id[MachineKeyPrefix.Length..]))
                .ToList();
            ObservableCollectionSync.SyncByKey(ContainerStats, containerResults, s => s.Id);
            ObservableCollectionSync.SyncByKey(MachineStats, machineMapped, s => s.Id);
            // Always pulse once so consumers refresh derived samples/charts for this tick.
            OnPropertyChanged(nameof(ContainerStats));
            OnPropertyChanged(nameof(MachineStats));

            IsStatsLoading = false;

            // Alert only when every running container failed (results empty) AND the load
            // was user-initiated - the background poll stays silent; the dashboard shows a
            // passive panel via StatsUnavailable.
            if (showLoading && runningIds.Count > 0 && containerResults.Count == 0)
            {
                _alertCenter.Error("Unable to read container stats. Check that the container service is running.");
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task<List<ContainerStatsModel>> FetchStatsAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var tasks = ids.Select(async id =>
        {
            try { return await _backend.StatsAsync(id, ct); }
            catch { return null; }
        });
        var results = await Task.WhenAll(tasks);
        return [.. results.Where(r => r is not null).Select(r => r!)];
    }

    private async Task<List<ContainerStatsModel>> FetchMachineStatsAsync(IReadOnlyList<MachineStatTarget> targets, CancellationToken ct)
    {
        var tasks = targets.Select(async target =>
        {
            try
            {
                var stats = await _backend.StatsAsync(target.BackingId, ct);
                return stats.With(MachineStatKey(target.MachineId));
            }
            catch { return null; }
        });
        var results = await Task.WhenAll(tasks);
        return [.. results.Where(r => r is not null).Select(r => r!)];
    }

    /// Whether the stats page should show its passive "unavailable" panel: there are
    /// running containers but no stats came back. Drives non-modal UI in the dashboard.
    public bool StatsUnavailable =>
        _containerList.Containers.Any(c => c.Status == "running") && ContainerStats.Count == 0;

    // MARK: - Machine stats accessors (keyed by stable machine id)

    /// Latest derived sample for a machine, or null until it has two raw reads.
    public StatsSample? MachineSample(string machineId) =>
        _latestSamples.TryGetValue(MachineStatKey(machineId), out var sample) ? sample : null;

    /// Latest raw stats for a machine (absolute memory/net/disk values).
    public ContainerStatsModel? MachineRawStats(string machineId) =>
        MachineStats.FirstOrDefault(s => s.Id == machineId);

    /// Chronological sample history for a machine (drives its charts/sparklines).
    public IReadOnlyList<StatsSample> MachineHistory(string machineId) =>
        History.Samples(new StatsKey(MachineStatKey(machineId)));

    /// Derive a sample from each raw read against its predecessor, append to history, and
    /// republish the latest per container. Containers with no prior read only seed the
    /// baseline (need two points for a rate). Stopped/vanished containers are pruned from
    /// the live maps (history is retained) so a restart deltas fresh, not across the gap.
    private void RecordSamples(IEnumerable<ContainerStatsModel> reads, IReadOnlyDictionary<string, int> cpuCounts)
    {
        var readList = reads.ToList();
        var monotonicNow = Environment.TickCount64; // rate math
        var wallNow = DateTimeOffset.Now;            // sample stamp (persistable, cross-launch)

        foreach (var read in readList)
        {
            if (_previousRaw.TryGetValue(read.Id, out var prev))
            {
                var elapsed = TimeSpan.FromMilliseconds(monotonicNow - prev.MonotonicMs);
                var cpuCount = cpuCounts.TryGetValue(read.Id, out var c) ? c : 1;
                var sample = StatsMath.ComputeSample(prev.Stats, read, wallNow, elapsed, cpuCount);
                History.Record(sample, new StatsKey(read.Id));
                _latestSamples[read.Id] = sample;
            }
            _previousRaw[read.Id] = (read, monotonicNow);
        }

        var live = readList.Select(r => r.Id).ToHashSet();
        foreach (var key in _previousRaw.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _previousRaw.Remove(key);
        }
        foreach (var key in _latestSamples.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _latestSamples.Remove(key);
        }

        // Evict whole series for containers that have been gone longer than the retention
        // window. Their buffers never get a fresh write, so per-write time-pruning never
        // touches them - without this they'd persist (and re-serialize) in memory forever.
        // Recently-stopped containers stay until their newest sample ages out, so they can
        // still be charted.
        var liveKeys = readList.Select(r => new StatsKey(r.Id)).ToHashSet();
        History.EvictSeries(wallNow - History.Retention, liveKeys);
    }

    public void Dispose()
    {
        _samplingTimer?.Dispose();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
    }
}
