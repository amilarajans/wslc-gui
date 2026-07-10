namespace OrchardWin.Core.Services;

/// One place for human-readable byte sizes, replacing scattered manual formatting. Ported
/// from Orchard's `ByteFormat` enum: `String` is decimal (1000-based, matches disk-vendor
/// sizing), `Memory` is binary (1024-based, matches Task Manager-style RAM readings).
public static class ByteFormat
{
    private static readonly string[] DecimalUnits = ["B", "kB", "MB", "GB", "TB", "PB"];
    private static readonly string[] BinaryUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    public static string String(long bytes) => Format(bytes, 1000, DecimalUnits);

    /// Binary (1024-based) sizing for RAM-style readings, so memory usage reads in MiB/GiB.
    /// Use this for every live-stats memory value - mixing it with decimal sizing made the
    /// same container show different byte strings across the dashboard/tray flyout.
    public static string Memory(long bytes) => Format(bytes, 1024, BinaryUnits);

    private static string Format(long bytes, int baseValue, string[] units)
    {
        if (bytes == 0) return $"0 {units[0]}";
        var negative = bytes < 0;
        double value = Math.Abs(bytes);
        var unitIndex = 0;
        while (value >= baseValue && unitIndex < units.Length - 1)
        {
            value /= baseValue;
            unitIndex++;
        }
        var formatted = unitIndex == 0 ? value.ToString("F0") : value.ToString("F1");
        return $"{(negative ? "-" : "")}{formatted} {units[unitIndex]}";
    }
}
