namespace OrchardWin.Core.Models;

public enum SystemStatus { Unknown, Stopped, Running, NewerVersion, UnsupportedVersion }

public static class SystemStatusExtensions
{
    public static string Text(this SystemStatus status) => status switch
    {
        SystemStatus.Unknown => "unknown",
        SystemStatus.Stopped => "stopped",
        SystemStatus.Running => "running",
        SystemStatus.NewerVersion => "version not yet supported",
        SystemStatus.UnsupportedVersion => "unsupported version",
        _ => status.ToString(),
    };
}

public enum BuilderStatus { Stopped, Running }

/// Read-only WSL kernel info shown on Settings > Kernel. Unlike Apple's container tool,
/// WSL does not support swapping the kernel per-machine - one Microsoft-maintained kernel
/// ships per WSL version - so there is no "set recommended / set custom" action here, only
/// a version readout from `wsl --version`. See ARCHITECTURE.md "Kernel management".
public sealed record WslKernelInfo
{
    public string? KernelVersion { get; init; }
    public string? WslVersion { get; init; }
}

/// Terminal Orchard-Win can hand a container/machine shell off to. Replaces Orchard's
/// Terminal.app / iTerm2 / Ghostty set with the Windows equivalents.
public enum TerminalApp { WindowsTerminal, PowerShell, Cmd }

public static class TerminalAppExtensions
{
    public static string DisplayName(this TerminalApp app) => app switch
    {
        TerminalApp.WindowsTerminal => "Windows Terminal",
        TerminalApp.PowerShell => "PowerShell",
        TerminalApp.Cmd => "Command Prompt",
        _ => app.ToString(),
    };

    /// Executable to probe for on PATH / well-known install locations.
    public static string ExecutableName(this TerminalApp app) => app switch
    {
        TerminalApp.WindowsTerminal => "wt.exe",
        TerminalApp.PowerShell => "pwsh.exe",
        TerminalApp.Cmd => "cmd.exe",
        _ => throw new ArgumentOutOfRangeException(nameof(app)),
    };
}
