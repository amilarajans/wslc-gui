using System.Diagnostics;
using System.Text;

namespace OrchardWin.Core.Services;

/// Categorized logger. Mirrors Orchard's OSLog-backed `Log` enum.
/// Writes to Trace (VS Output / DebugView) and, after <see cref="Log.Initialize"/>,
/// to a rolling file under %LOCALAPPDATA%\wslc-gui\logs\.
public sealed class Logger
{
    private readonly string _category;
    internal Logger(string category) => _category = category;

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{_category}] {level}: {message}";
        Trace.WriteLine(line);
        Log.AppendToFile(line);
    }
}

/// Categorized loggers, one per subsystem - mirrors Orchard's `Log.cli` / `.xpc` /
/// `.containers` / `.ui` split (`.xpc` renamed `.backend` since there is no XPC here).
public static class Log
{
    public static readonly Logger Cli = new("cli");
    public static readonly Logger Backend = new("backend");
    public static readonly Logger Containers = new("containers");
    public static readonly Logger Ui = new("ui");
    public static readonly Logger Crash = new("crash");

    private static readonly object FileGate = new();
    private static string? _sessionLogPath;
    private static string? _logDirectory;
    private static string? _dumpDirectory;

    /// Directory for log files, e.g. %LOCALAPPDATA%\wslc-gui\logs
    public static string? LogDirectory => _logDirectory;

    /// Directory for .NET mini-dumps, e.g. %LOCALAPPDATA%\wslc-gui\dumps
    public static string? DumpDirectory => _dumpDirectory;

    /// Path of the current session log file (null until Initialize).
    public static string? SessionLogPath => _sessionLogPath;

    /// Creates log/dump folders, opens a session log, and enables .NET mini-dumps on hard crash.
    /// Safe to call once at process start (idempotent).
    public static string Initialize()
    {
        if (_sessionLogPath is not null && _logDirectory is not null)
            return _logDirectory;

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "wslc-gui");
        _logDirectory = Path.Combine(root, "logs");
        _dumpDirectory = Path.Combine(root, "dumps");
        Directory.CreateDirectory(_logDirectory);
        Directory.CreateDirectory(_dumpDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _sessionLogPath = Path.Combine(_logDirectory, $"app-{stamp}.log");

        // Always-latest convenience path (overwritten each launch).
        var latest = Path.Combine(_logDirectory, "latest.log");
        try
        {
            File.WriteAllText(latest, $"# wslc-gui session {stamp}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // ignore - session file below is the durable one
        }

        // .NET runtime mini-dumps on unhandled managed crashes (complements WER native dumps).
        try
        {
            var dumpPattern = Path.Combine(_dumpDirectory, "crash-%p-%t.dmp");
            Environment.SetEnvironmentVariable("DOTNET_DbgEnableMiniDump", "1");
            Environment.SetEnvironmentVariable("DOTNET_DbgMiniDumpType", "2"); // Heap
            Environment.SetEnvironmentVariable("DOTNET_DbgMiniDumpName", dumpPattern);
            Environment.SetEnvironmentVariable("COMPlus_DbgEnableMiniDump", "1");
            Environment.SetEnvironmentVariable("COMPlus_DbgMiniDumpType", "2");
            Environment.SetEnvironmentVariable("COMPlus_DbgMiniDumpName", dumpPattern);
        }
        catch
        {
            // best-effort
        }

        AppendToFile($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [crash] INFO: logging initialized");
        AppendToFile($"  logDir={_logDirectory}");
        AppendToFile($"  dumpDir={_dumpDirectory}");
        AppendToFile($"  session={_sessionLogPath}");
        AppendToFile($"  pid={Environment.ProcessId}");
        AppendToFile($"  runtime={Environment.Version}");
        AppendToFile($"  os={Environment.OSVersion}");
        AppendToFile($"  baseDir={AppContext.BaseDirectory}");

        PruneOldFiles(_logDirectory, "*.log", keep: 20);
        PruneOldFiles(_dumpDirectory, "*.dmp", keep: 10);

        return _logDirectory;
    }

    /// Writes a hard-crash report (unhandled exception / fatal XAML). Always flushes to disk.
    public static void WriteCrashReport(string source, Exception? ex, string? extra = null)
    {
        Initialize();
        var sb = new StringBuilder();
        sb.AppendLine("========== CRASH REPORT ==========");
        sb.AppendLine($"Time:   {DateTimeOffset.Now:O}");
        sb.AppendLine($"Source: {source}");
        sb.AppendLine($"PID:    {Environment.ProcessId}");
        if (!string.IsNullOrWhiteSpace(extra))
            sb.AppendLine($"Extra:  {extra}");
        if (ex is not null)
        {
            sb.AppendLine($"Type:   {ex.GetType().FullName}");
            sb.AppendLine($"Message:{ex.Message}");
            sb.AppendLine("---- Exception ----");
            sb.AppendLine(ex.ToString());
            if (ex.InnerException is { } inner)
            {
                sb.AppendLine("---- Inner ----");
                sb.AppendLine(inner.ToString());
            }
        }
        else
        {
            sb.AppendLine("Exception: (null)");
        }
        sb.AppendLine("==================================");

        var text = sb.ToString();
        Trace.WriteLine(text);
        AppendToFile(text);

        try
        {
            var crashPath = Path.Combine(
                _logDirectory!,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            File.WriteAllText(crashPath, text, Encoding.UTF8);
            AppendToFile($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [crash] INFO: wrote {crashPath}");
        }
        catch (Exception writeEx)
        {
            Trace.WriteLine($"[crash] failed to write crash file: {writeEx.Message}");
        }
    }

    internal static void AppendToFile(string line)
    {
        var path = _sessionLogPath;
        var latest = _logDirectory is null ? null : Path.Combine(_logDirectory, "latest.log");
        if (path is null) return;

        lock (FileGate)
        {
            try
            {
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                if (latest is not null)
                    File.AppendAllText(latest, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // never throw from logging
            }
        }
    }

    private static void PruneOldFiles(string directory, string pattern, int keep)
    {
        try
        {
            var files = new DirectoryInfo(directory)
                .GetFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep)
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); } catch { /* ignore */ }
            }
        }
        catch
        {
            // ignore
        }
    }
}
