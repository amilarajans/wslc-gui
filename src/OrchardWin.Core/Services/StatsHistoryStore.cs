using ContainerStatsModel = OrchardWin.Core.Models.ContainerStats;

namespace OrchardWin.Core.Services;

/// A derived, per-tick resource sample for one container. Unlike <see cref="ContainerStatsModel"/>
/// (raw cumulative counters), every field here is an instantaneous value or a rate, ready to
/// plot directly. Produced by <see cref="StatsMath.ComputeSample"/> from two consecutive raw
/// reads. Ported 1:1 from Orchard's `StatsSample` (StatsHistory.swift).
///
/// The timestamp is wall-clock (<see cref="DateTimeOffset"/>) so samples are meaningful
/// across app launches and can be persisted. Rate math never uses it - <see cref="StatsMath.ComputeSample"/>
/// takes a separate monotonic `elapsed` so clock adjustments can't distort rates.
public sealed record StatsSample
{
    public required DateTimeOffset Timestamp { get; init; }

    /// CPU use as a percentage of the container's allocated cores, clamped 0…100.
    public required double CpuPercent { get; init; }
    public required long MemoryBytes { get; init; }
    public required long MemoryLimitBytes { get; init; }
    public required double NetworkRxPerSec { get; init; }
    public required double NetworkTxPerSec { get; init; }
    public required double BlockReadPerSec { get; init; }
    public required double BlockWritePerSec { get; init; }
    public required int Pids { get; init; }

    public double MemoryPercent => MemoryLimitBytes > 0 ? (double)MemoryBytes / MemoryLimitBytes * 100.0 : 0.0;
}

/// Identity for a container's history: `(Host, Id)` from day one so multi-host work
/// inherits the store unchanged. Today `Host` is always the local daemon. A value-type
/// record so it works as a `Dictionary` key with structural equality out of the box.
public readonly record struct StatsKey(string Host, string Id)
{
    public const string LocalHost = "local";

    public StatsKey(string id) : this(LocalHost, id) { }
}

/// Pure: turn two consecutive raw reads into one plottable sample, and fold many
/// per-container series into one system-wide series. No clock, no state, no I/O - ported
/// 1:1 from the free functions in Orchard's `StatsHistory.swift`.
public static class StatsMath
{
    /// - `at` stamps the sample's wall-clock timestamp only; all rate math uses `elapsed`.
    /// - `elapsed` is the monotonic gap between the two reads.
    /// - `cpuCount` is the container's allocated cores - the CPU% denominator.
    ///
    /// Rules: a non-positive `elapsed` (a repeated/zero-gap read) yields zero rates rather
    /// than dividing by zero; a negative counter delta (a container restart resets its
    /// cumulative counters) clamps to zero rather than drawing a huge negative spike; CPU%
    /// is normalized to the allocation and clamped to 0…100.
    public static StatsSample ComputeSample(
        ContainerStatsModel prev,
        ContainerStatsModel curr,
        DateTimeOffset at,
        TimeSpan elapsed,
        int cpuCount)
    {
        var seconds = elapsed.TotalSeconds;
        var cores = Math.Max(1, cpuCount);

        double PerSecond(long previous, long current)
        {
            if (seconds <= 0) return 0;
            var delta = current - previous;
            if (delta <= 0) return 0; // counter reset or no progress
            return delta / seconds;
        }

        double cpuPercent;
        if (seconds <= 0)
        {
            cpuPercent = 0;
        }
        else
        {
            var deltaUsec = curr.CpuUsageUsec - prev.CpuUsageUsec;
            if (deltaUsec <= 0)
            {
                cpuPercent = 0;
            }
            else
            {
                // Δ CPU-seconds over Δ wall-seconds = cores busy; normalize to allocated cores.
                var coresBusy = deltaUsec / 1_000_000.0 / seconds;
                var pct = coresBusy / cores * 100.0;
                cpuPercent = Math.Min(100.0, Math.Max(0.0, pct));
            }
        }

        return new StatsSample
        {
            Timestamp = at,
            CpuPercent = cpuPercent,
            MemoryBytes = curr.MemoryUsageBytes,
            MemoryLimitBytes = curr.MemoryLimitBytes,
            NetworkRxPerSec = PerSecond(prev.NetworkRxBytes, curr.NetworkRxBytes),
            NetworkTxPerSec = PerSecond(prev.NetworkTxBytes, curr.NetworkTxBytes),
            BlockReadPerSec = PerSecond(prev.BlockReadBytes, curr.BlockReadBytes),
            BlockWritePerSec = PerSecond(prev.BlockWriteBytes, curr.BlockWriteBytes),
            Pids = curr.NumProcesses,
        };
    }

    /// Fold many per-container series into one system-wide series by summing every field of
    /// samples that share a timestamp. All running containers are sampled together (one
    /// wall-clock stamp per tick), so their samples align exactly. `CpuPercent` becomes total
    /// load across containers (can exceed 100), memory becomes total used vs total limit, and
    /// rates/pids sum. Result is chronological.
    public static List<StatsSample> Aggregate(IEnumerable<IEnumerable<StatsSample>> histories)
    {
        var byTime = new Dictionary<DateTimeOffset, StatsSample>();
        foreach (var series in histories)
        {
            foreach (var sample in series)
            {
                if (!byTime.TryGetValue(sample.Timestamp, out var running))
                {
                    byTime[sample.Timestamp] = sample;
                    continue;
                }
                byTime[sample.Timestamp] = new StatsSample
                {
                    Timestamp = sample.Timestamp,
                    CpuPercent = running.CpuPercent + sample.CpuPercent,
                    MemoryBytes = running.MemoryBytes + sample.MemoryBytes,
                    MemoryLimitBytes = running.MemoryLimitBytes + sample.MemoryLimitBytes,
                    NetworkRxPerSec = running.NetworkRxPerSec + sample.NetworkRxPerSec,
                    NetworkTxPerSec = running.NetworkTxPerSec + sample.NetworkTxPerSec,
                    BlockReadPerSec = running.BlockReadPerSec + sample.BlockReadPerSec,
                    BlockWritePerSec = running.BlockWritePerSec + sample.BlockWritePerSec,
                    Pids = running.Pids + sample.Pids,
                };
            }
        }
        return [.. byTime.Values.OrderBy(s => s.Timestamp)];
    }
}

/// Bounded per-container sample history. Each key keeps samples within a rolling
/// `Retention` window (default 24h) plus a hard count cap as a runaway backstop. Ported
/// 1:1 from Orchard's `StatsHistoryStore` (StatsHistory.swift); Swift's single-threaded
/// `@MainActor` confinement is replaced with an internal `lock` since this app's sampler
/// timer callback runs on the thread pool, not a fixed UI thread.
public sealed class StatsHistoryStore
{
    public int Capacity { get; }
    public TimeSpan Retention { get; }

    private readonly object _gate = new();
    private readonly Dictionary<StatsKey, List<StatsSample>> _buffers = new();

    public StatsHistoryStore(int capacity = 50_000, TimeSpan? retention = null)
    {
        Capacity = Math.Max(1, capacity);
        Retention = retention ?? TimeSpan.FromHours(24);
    }

    public void Record(StatsSample sample, StatsKey key)
    {
        lock (_gate)
        {
            if (!_buffers.TryGetValue(key, out var buffer))
            {
                buffer = [];
                _buffers[key] = buffer;
            }
            buffer.Add(sample);

            // Prune from the head only: samples arrive in chronological order, so
            // everything older than the retention window is a contiguous prefix - a linear
            // scan to the first kept sample beats re-scanning the whole buffer each tick.
            var cutoff = sample.Timestamp - Retention;
            var firstKept = buffer.FindIndex(s => s.Timestamp >= cutoff);
            if (firstKept > 0)
            {
                buffer.RemoveRange(0, firstKept);
            }

            if (buffer.Count > Capacity)
            {
                buffer.RemoveRange(0, buffer.Count - Capacity);
            }
        }
    }

    /// Chronological samples for one container (oldest first). Empty if none recorded.
    public IReadOnlyList<StatsSample> Samples(StatsKey key)
    {
        lock (_gate)
        {
            return _buffers.TryGetValue(key, out var buffer) ? buffer.ToList() : [];
        }
    }

    public StatsSample? Latest(StatsKey key)
    {
        lock (_gate)
        {
            return _buffers.TryGetValue(key, out var buffer) && buffer.Count > 0 ? buffer[^1] : null;
        }
    }

    /// Every series, for cross-container aggregation (system dashboard).
    public IReadOnlyList<IReadOnlyList<StatsSample>> AllSamples()
    {
        lock (_gate)
        {
            return _buffers.Values.Select(b => (IReadOnlyList<StatsSample>)b.ToList()).ToList();
        }
    }

    /// A copy of the whole store, for persistence.
    public Dictionary<StatsKey, List<StatsSample>> Snapshot()
    {
        lock (_gate)
        {
            return _buffers.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        }
    }

    /// Replace all in-memory history - used to seed from disk on launch.
    public void ReplaceAll(IReadOnlyDictionary<StatsKey, List<StatsSample>> snapshot)
    {
        lock (_gate)
        {
            _buffers.Clear();
            foreach (var (key, value) in snapshot)
            {
                _buffers[key] = [.. value];
            }
        }
    }

    /// Merge restored-from-disk history into the live store. Because the sampler starts
    /// before the (off-thread) load finishes, a key may already have fresh samples: for
    /// those, splice in only the strictly-older restored samples ahead of what's live so
    /// nothing is clobbered or reordered. Keys with no live samples yet are taken wholesale.
    public void MergeRestored(IReadOnlyDictionary<StatsKey, List<StatsSample>> restored)
    {
        lock (_gate)
        {
            foreach (var (key, samples) in restored)
            {
                if (!_buffers.TryGetValue(key, out var live) || live.Count == 0)
                {
                    _buffers[key] = [.. samples];
                    continue;
                }

                var earliestLive = live[0].Timestamp;
                var older = samples.Where(s => s.Timestamp < earliestLive).ToList();
                if (older.Count > 0)
                {
                    older.AddRange(live);
                    _buffers[key] = older;
                }
            }
        }
    }

    /// Drop entire series whose newest sample predates `cutoff` and whose key isn't
    /// currently live - evicts dead containers' buffers so they stop consuming memory and
    /// re-serializing.
    public void EvictSeries(DateTimeOffset cutoff, IReadOnlySet<StatsKey> liveKeys)
    {
        lock (_gate)
        {
            foreach (var key in _buffers.Keys.ToList())
            {
                if (liveKeys.Contains(key)) continue;
                var buffer = _buffers[key];
                if (buffer.Count == 0 || buffer[^1].Timestamp < cutoff)
                {
                    _buffers.Remove(key);
                }
            }
        }
    }

    public void Clear(StatsKey key)
    {
        lock (_gate) { _buffers.Remove(key); }
    }

    public void RemoveAll()
    {
        lock (_gate) { _buffers.Clear(); }
    }
}
