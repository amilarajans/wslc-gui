using System.Text.Json;

namespace OrchardWin.Core.Services;

/// Reads and writes container stats history to disk so the 1h/24h windows survive an app
/// relaunch. One JSON file holding every series; pruned to the retention window on load.
/// Ported 1:1 from Orchard's `StatsPersistence.swift`.
public sealed class StatsPersistence
{
    /// Current on-disk schema version. Bump when `StatsSample`/`PersistedSeries` change
    /// shape; `Load` drops (or, in future, migrates) anything stamped with a different
    /// version rather than letting a decode mismatch silently wipe history.
    public const int CurrentVersion = 1;

    public string FilePath { get; }

    public StatsPersistence(string? filePath = null)
    {
        FilePath = filePath ?? DefaultPath();
    }

    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrchardWin", "stats-history.json");

    public void Save(IReadOnlyDictionary<StatsKey, List<StatsSample>> snapshot)
    {
        var series = snapshot.Select(kv => new PersistedSeries(kv.Key.Host, kv.Key.Id, kv.Value)).ToList();
        var file = new PersistedFile(CurrentVersion, series);
        var data = JsonSerializer.Serialize(file);

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-to-temp-then-move mirrors Swift's `.atomic` write option: a crash mid-write
        // can't leave a truncated/corrupt history file in place.
        var tempFile = FilePath + ".tmp";
        File.WriteAllText(tempFile, data);
        File.Move(tempFile, FilePath, overwrite: true);
    }

    /// Best-effort load, dropping samples older than `retention`. Returns empty on any
    /// error (missing file, corrupt JSON, or a schema version this build doesn't
    /// understand) - history simply starts fresh rather than crashing or mis-decoding.
    public Dictionary<StatsKey, List<StatsSample>> Load(TimeSpan? retention = null, DateTimeOffset? now = null)
    {
        var effectiveRetention = retention ?? TimeSpan.FromHours(24);
        var effectiveNow = now ?? DateTimeOffset.Now;

        PersistedFile? file;
        try
        {
            if (!File.Exists(FilePath)) return [];
            var data = File.ReadAllText(FilePath);
            file = JsonSerializer.Deserialize<PersistedFile>(data);
        }
        catch (Exception)
        {
            return [];
        }

        if (file is null || file.Version != CurrentVersion) return [];

        var cutoff = effectiveNow - effectiveRetention;
        var result = new Dictionary<StatsKey, List<StatsSample>>();
        foreach (var entry in file.Series)
        {
            var kept = entry.Samples.Where(s => s.Timestamp >= cutoff).ToList();
            if (kept.Count > 0)
            {
                result[new StatsKey(entry.Host, entry.Id)] = kept;
            }
        }
        return result;
    }

    /// On-disk shape: a versioned envelope around a flat list of series (JSON object keys
    /// can't be structs/records). `Version` lets a future format change migrate or drop
    /// cleanly.
    private sealed record PersistedFile(int Version, List<PersistedSeries> Series);

    private sealed record PersistedSeries(string Host, string Id, List<StatsSample> Samples);
}
