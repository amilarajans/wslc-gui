using System.Diagnostics;

namespace OrchardWin.Core.Services;

/// A categorized logger, standing in for Orchard's OSLog-backed `Log` enum (no direct
/// Windows equivalent that's this lightweight; Debug.WriteLine shows up in the VS Output
/// window and DebugView, which covers the same "watch it while developing" use case).
/// Swap the sink for Microsoft.Extensions.Logging / ETW if you want Windows Event Log
/// integration later.
public sealed class Logger
{
    private readonly string _category;
    internal Logger(string category) => _category = category;

    public void Debug(string message) => Write("DEBUG", message);
    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message) =>
        Trace.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] [{_category}] {level}: {message}");
}

/// Categorized loggers, one per subsystem - mirrors Orchard's `Log.cli` / `.xpc` /
/// `.containers` / `.ui` split (`.xpc` renamed `.backend` since there is no XPC here).
public static class Log
{
    public static readonly Logger Cli = new("cli");
    public static readonly Logger Backend = new("backend");
    public static readonly Logger Containers = new("containers");
    public static readonly Logger Ui = new("ui");
}
