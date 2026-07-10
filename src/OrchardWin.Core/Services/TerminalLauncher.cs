using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services;

/// What kind of thing `TerminalLauncher.OpenShellAsync` is opening a shell into. Orchard's
/// `TerminalLauncher` only ever shelled into a container (`container exec`); Orchard-Win adds
/// Machines (WSL distros), which use a different inner command (`wsl -d <name>` rather than
/// `wslc container exec`), so a discriminator is needed where Swift's single `containerId`
/// parameter was enough.
public enum ShellTargetKind
{
    /// A container Machine - a WSL distro. Shells in via `wsl.exe -d <name>`.
    Machine,
    /// A plain container. Shells in via `wslc.exe container exec -it <id> <shell>`.
    Container,
}

/// Opens a container/machine shell in the user's preferred terminal. Holds no published state;
/// still an `ObservableObject` for DI-container consistency with the rest of this app's
/// services. Ported from Orchard's `TerminalLauncher` - same "build a command line, hand it to
/// the chosen terminal app" shape, retargeted from Terminal.app/iTerm2/Ghostty + AppleScript to
/// Windows Terminal/PowerShell/cmd.exe + `Process.Start`.
public sealed partial class TerminalLauncher : ObservableObject
{
    private readonly SettingsStore _settings;
    private readonly AlertCenter _alertCenter;

    public TerminalLauncher(SettingsStore settings, AlertCenter alertCenter)
    {
        _settings = settings;
        _alertCenter = alertCenter;
    }

    /// Opens the user's preferred terminal (`settings.PreferredTerminal`) running a shell for
    /// `id`. Mirrors Swift's `openTerminal(for:shell:)`.
    public Task OpenShellAsync(ShellTargetKind targetKind, string id, string shell = "sh", CancellationToken ct = default) =>
        OpenShellAsync(_settings.PreferredTerminal, targetKind, id, shell, ct);

    /// Convenience matching Swift's `openTerminalWithBash(for:)`.
    public Task OpenShellWithBashAsync(ShellTargetKind targetKind, string id, CancellationToken ct = default) =>
        OpenShellAsync(targetKind, id, "bash", ct);

    /// Opens `app`, running the shell command for `targetKind`/`id` inside it. This is
    /// fire-and-forget by nature - the launched terminal window outlives the call - so it
    /// returns once the launcher process has been *started*, not when the interactive session
    /// ends, exactly like Swift's `openTerminal`, which spawns via AppleScript/`Process` and
    /// returns immediately too.
    public Task OpenShellAsync(TerminalApp app, ShellTargetKind targetKind, string id, string shell = "sh", CancellationToken ct = default)
    {
        var (program, args) = BuildInnerCommand(targetKind, id, shell);
        Log.Ui.Debug($"Opening terminal - terminal: {app.DisplayName()}, command: {program} {string.Join(' ', args)}");

        try
        {
            switch (app)
            {
                case TerminalApp.WindowsTerminal:
                    LaunchWindowsTerminal(program, args);
                    break;
                case TerminalApp.PowerShell:
                    // No dedicated "open a window running this command" verb for pwsh, unlike
                    // `wt.exe --`; route through `cmd.exe /c start` instead.
                    LaunchViaCmdStart("pwsh.exe", ["-NoExit", "-Command"], program, args);
                    break;
                case TerminalApp.Cmd:
                default:
                    LaunchViaCmdStart("cmd.exe", ["/k"], program, args);
                    break;
            }
            Log.Ui.Debug("Terminal launched successfully");
        }
        catch (Exception ex)
        {
            Log.Ui.Error($"Failed to open terminal: {ex.Message}");
            _alertCenter.Error($"Failed to open terminal: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private (string Program, List<string> Args) BuildInnerCommand(ShellTargetKind targetKind, string id, string shell) => targetKind switch
    {
        // A Machine is a WSL distro - attach an interactive shell directly, no wslc involved.
        ShellTargetKind.Machine => ("wsl.exe", ["-d", id]),

        // A Container shells out through wslc's exec verb, mirroring `docker exec -it`.
        // VERIFY: `wslc container exec -it <id> <shell>` - Docker-familiar spelling assumed;
        // confirm against `wslc container exec --help` once on Windows.
        ShellTargetKind.Container => (_settings.SafeContainerBinaryPath(), ["container", "exec", "-it", id, shell]),

        _ => throw new ArgumentOutOfRangeException(nameof(targetKind), targetKind, null),
    };

    /// VERIFY: `wt.exe -- <program> <args...>` opens a new Windows Terminal window running
    /// the given command line - this is documented `wt` behaviour as of recent releases, but
    /// not re-verified for this task.
    private static void LaunchWindowsTerminal(string program, List<string> args)
    {
        List<string> wtArgs = ["--", program, .. args];
        Launch("wt.exe", wtArgs);
    }

    /// Fallback launcher for shells with no dedicated "open a window running a command" CLI
    /// (pwsh.exe, cmd.exe): route through `cmd.exe /c start` so a new window reliably opens
    /// regardless of whether this process currently owns a console - `ProcessStartInfo`'s own
    /// new-window heuristics for `UseShellExecute` are inconsistent in that situation.
    /// VERIFY: the quoting round-trip here (our `CmdQuote` -> cmd.exe's `start` parsing ->
    /// pwsh's `-Command` parsing) is untested on a real machine for arguments containing
    /// spaces/quotes (e.g. a machine name with spaces).
    private static void LaunchViaCmdStart(string shellExe, IReadOnlyList<string> shellArgsPrefix, string program, List<string> args)
    {
        var innerCommand = string.Join(' ', new[] { program }.Concat(args).Select(ProcessCommandRunner.CmdQuote));
        List<string> cmdArgs = ["/c", "start", "\"\"", shellExe, .. shellArgsPrefix, innerCommand];
        Launch("cmd.exe", cmdArgs);
    }

    private static void Launch(string fileName, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            // Hand off to the shell so a real terminal window opens instead of running
            // attached to (and captured by) this process, mirroring NSWorkspace/AppleScript's
            // "launch and detach" semantics on macOS.
            UseShellExecute = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        // Fire-and-forget: we don't track the terminal window's lifetime, same as the Swift
        // original never awaiting the spawned Terminal.app/iTerm2/Ghostty process.
        Process.Start(psi)?.Dispose();
    }
}
