using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns container-system state: health/version, kernel info, system properties, disk usage,
/// and the start/stop/restart lifecycle. Ported from Orchard's `SystemService`, retargeted at
/// `wsl.exe`/`wslc.exe` - see method-level comments for where WSL's lifecycle model departs
/// from Apple's `container system start/stop/restart` verbs.
public sealed partial class SystemService : ObservableObject
{
    [ObservableProperty]
    private SystemStatus _systemStatus = SystemStatus.Unknown;

    [ObservableProperty]
    private string? _systemStatusError;

    [ObservableProperty]
    private bool _systemStatusVersionOverride;

    [ObservableProperty]
    private bool _isSystemLoading;

    [ObservableProperty]
    private string? _containerVersion;

    [ObservableProperty]
    private string? _parsedContainerVersion;

    /// WSL ships one Microsoft-maintained kernel per WSL version - there is no per-machine
    /// swappable kernel the way Apple's `container` tool has, so unlike Swift's `KernelConfig`
    /// (which has `binary`/`tar`/`arch`/`isRecommended` for a picker), this is a read-only
    /// version readout. See <see cref="WslKernelInfo"/>'s doc comment and ARCHITECTURE.md
    /// "Kernel management" - `SetRecommendedKernel`/`SetCustomKernel` are intentionally not
    /// ported; there is nothing on Windows for them to configure.
    [ObservableProperty]
    private WslKernelInfo _kernelInfo = new();

    [ObservableProperty]
    private bool _isKernelLoading;

    [ObservableProperty]
    private ObservableCollection<SystemProperty> _systemProperties = [];

    [ObservableProperty]
    private bool _isSystemPropertiesLoading;

    [ObservableProperty]
    private SystemDiskUsage? _systemDiskUsage;

    [ObservableProperty]
    private bool _isSystemDiskUsageLoading;

    private readonly IContainerBackend _backend;
    private readonly ICommandRunner _runner;
    private readonly SettingsStore _settings;
    private readonly AlertCenter _alertCenter;

    /// Refresh the container list after the system starts. Set by the owner (mirrors Swift's
    /// `onSystemStarted` closure).
    public Func<Task> OnSystemStarted { get; set; } = () => Task.CompletedTask;

    /// Clear the container list after the system stops. Set by the owner.
    public Action OnSystemStopped { get; set; } = () => { };

    public SystemService(IContainerBackend backend, ICommandRunner runner, SettingsStore settings, AlertCenter alertCenter)
    {
        _backend = backend;
        _runner = runner;
        _settings = settings;
        _alertCenter = alertCenter;
    }

    // MARK: - Status / version

    public async Task CheckSystemStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var health = await _backend.PingAsync(ct);
            ContainerVersion = health.ApiServerVersion;
            ParsedContainerVersion = health.ApiServerVersion;
            SystemStatus = SystemStatus.Running;
            SystemStatusError = null;
        }
        catch (Exception error)
        {
            ContainerVersion = null;
            ParsedContainerVersion = null;
            SystemStatus = SystemStatus.Stopped;
            SystemStatusError = $"{error.GetType().Name}: {error}";
        }
    }

    public async Task CheckSystemStatusIgnoreVersionAsync(CancellationToken ct = default)
    {
        SystemStatusVersionOverride = true;
        await CheckSystemStatusAsync(ct);
    }

    public Task CheckContainerVersionAsync(CancellationToken ct = default) => CheckSystemStatusAsync(ct);

    // MARK: - Lifecycle
    //
    // Apple's `container` CLI has explicit `system start`/`stop`/`restart` verbs that own a
    // background launchd-managed API server. WSL has no equivalent single verb: the WSL VM and
    // the `wslc` container service spin up implicitly on first `wsl`/`wslc` invocation, and the
    // only whole-VM lifecycle control exposed is `wsl.exe --shutdown` (tears down *every*
    // running distro, not just the container one - there is no narrower "stop just the
    // container service" command). These three methods are reshaped around that reality rather
    // than pretending a 1:1 verb mapping exists.

    /// There's nothing to "start" - WSL/wslc start implicitly on first invocation. This issues
    /// a no-op "wake" call purely to trigger that implicit startup, then re-derives
    /// `SystemStatus` from an actual `backend.PingAsync()` rather than assuming success.
    public async Task StartSystemAsync(CancellationToken ct = default)
    {
        IsSystemLoading = true;
        _alertCenter.Dismiss();

        try
        {
            // VERIFY: `wsl.exe --status` alone is assumed sufficient to spin up the WSL VM;
            // whether the `wslc` container service also needs its own explicit "wake" call is
            // unconfirmed from macOS.
            await _runner.RunAsync("wsl.exe", ["--status"], ct);
        }
        catch (Exception error)
        {
            Log.Containers.Error($"Error waking WSL: {error.Message}");
        }

        await CheckSystemStatusAsync(ct);
        IsSystemLoading = false;

        if (SystemStatus == SystemStatus.Running)
        {
            Log.Containers.Debug("Container system started successfully");
            await OnSystemStarted();
        }
        else
        {
            _alertCenter.Error(OrchardWinException.ServiceUnavailable());
        }
    }

    /// `wsl.exe --shutdown` stops *every* running WSL distro, not just the container one -
    /// there is no scoped "stop the container service" verb. Callers should treat this as a
    /// whole-VM operation, matching the note above.
    public async Task StopSystemAsync(CancellationToken ct = default)
    {
        IsSystemLoading = true;
        _alertCenter.Dismiss();

        try
        {
            var result = await _runner.RunAsync("wsl.exe", ["--shutdown"], ct);
            IsSystemLoading = false;
            if (result.Failed)
            {
                _alertCenter.Error(!string.IsNullOrEmpty(result.Stderr) ? result.Stderr! : "Failed to stop system");
                await CheckSystemStatusAsync(ct); // don't assume .Stopped - re-derive
                return;
            }
            SystemStatus = SystemStatus.Stopped;
            OnSystemStopped();
            Log.Containers.Debug("Container system stopped successfully (wsl --shutdown)");
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to stop system: {error.Message}");
            IsSystemLoading = false;
            await CheckSystemStatusAsync(ct);
            Log.Containers.Error($"Error stopping system: {error.Message}");
        }
    }

    /// No `wsl --restart` verb exists either; a restart is shutdown-then-implicit-restart,
    /// the same "no single verb" situation Start/Stop are in.
    public async Task RestartSystemAsync(CancellationToken ct = default)
    {
        IsSystemLoading = true;
        _alertCenter.Dismiss();

        try
        {
            await _runner.RunAsync("wsl.exe", ["--shutdown"], ct);
            await _runner.RunAsync("wsl.exe", ["--status"], ct); // wake it back up
            await CheckSystemStatusAsync(ct);
            IsSystemLoading = false;

            if (SystemStatus == SystemStatus.Running)
            {
                Log.Containers.Debug("Container system restarted successfully");
                await OnSystemStarted();
            }
            else
            {
                _alertCenter.Error(OrchardWinException.ServiceUnavailable());
            }
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to restart system: {error.Message}");
            IsSystemLoading = false;
            await CheckSystemStatusAsync(ct);
            Log.Containers.Error($"Error restarting system: {error.Message}");
        }
    }

    // MARK: - Disk usage

    public async Task LoadSystemDiskUsageAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading) IsSystemDiskUsageLoading = true;

        try
        {
            SystemDiskUsage = await _backend.DiskUsageAsync(ct);
            IsSystemDiskUsageLoading = false;
        }
        catch (Exception error)
        {
            SystemDiskUsage = null;
            IsSystemDiskUsageLoading = false;
            // Runs on the poll cycle too - only a user-initiated load may alert.
            _alertCenter.Error($"Failed to load system disk usage: {error.Message}", showLoading ? AlertSource.User : AlertSource.Background);
        }
    }

    // MARK: - Kernel

    /// Reads the shared WSL kernel version via `wsl.exe --version`. No `SetRecommendedKernel`/
    /// `SetCustomKernel` counterpart - see the `KernelInfo` doc comment above.
    public async Task LoadKernelConfigAsync(CancellationToken ct = default)
    {
        IsKernelLoading = true;

        try
        {
            var result = await _runner.RunAsync("wsl.exe", ["--version"], ct);
            KernelInfo = !result.Failed && result.Stdout is { } stdout ? ParseKernelInfo(stdout) : new WslKernelInfo();
        }
        catch (Exception error)
        {
            Log.Containers.Error($"Failed to load kernel info: {error.Message}");
            KernelInfo = new WslKernelInfo();
        }
        finally
        {
            IsKernelLoading = false;
        }
    }

    /// VERIFY: `wsl --version` output format/locale - this parses the documented
    /// English/Windows-11-era "Label: value" line shape:
    ///   WSL version: 2.x.x.x
    ///   Kernel version: 5.x.x.x
    ///   ...
    /// Localized Windows builds may emit translated labels; parse defensively (unmatched
    /// lines are simply ignored) rather than throwing on an unexpected format.
    internal static WslKernelInfo ParseKernelInfo(string stdout)
    {
        string? kernelVersion = null;
        string? wslVersion = null;

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();

            if (key == "kernel version") kernelVersion = value;
            else if (key == "wsl version") wslVersion = value;
        }

        return new WslKernelInfo { KernelVersion = kernelVersion, WslVersion = wslVersion };
    }

    // MARK: - System properties

    public async Task LoadSystemPropertiesAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading)
        {
            IsSystemPropertiesLoading = true;
            _alertCenter.Dismiss();
        }

        // Reached on a background poll too - only alert on a user-initiated load.
        var source = showLoading ? AlertSource.User : AlertSource.Background;

        ProcessResult result;
        try
        {
            // VERIFY: `wslc system property list --format=json` subcommand/flag spelling -
            // best-effort mirror of Apple's `container system property list`.
            result = await _runner.RunAsync(_settings.SafeContainerBinaryPath(), ["system", "property", "list", "--format=json"], ct);
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to load system properties: {error.Message}", source);
            IsSystemPropertiesLoading = false;
            return;
        }

        if (result.Failed)
        {
            _alertCenter.Error(OrchardWinException.CliFailed("system property list", result.ExitCode, result.Stderr), source);
            IsSystemPropertiesLoading = false;
            return;
        }

        if (result.Stdout is not { } output)
        {
            SystemProperties = [];
            IsSystemPropertiesLoading = false;
            return;
        }

        SystemProperties = new ObservableCollection<SystemProperty>(CliParsers.ParseSystemProperties(output));
        IsSystemPropertiesLoading = false;
    }

    /// Optimistically record `value` for a property already in the loaded list, e.g. so a UI
    /// edit reflects instantly while the CLI round-trip is still in flight.
    public void SetSystemPropertyOptimistically(string id, string value)
    {
        for (var i = 0; i < SystemProperties.Count; i++)
        {
            if (SystemProperties[i].Id != id) continue;
            SystemProperties[i] = SystemProperties[i] with { Value = value };
            break;
        }
    }

    public async Task SetSystemPropertyAsync(string id, string value, CancellationToken ct = default)
    {
        // Swift's NSApplication focus-restore dance works around AppleScript's admin-prompt
        // stealing focus. `ProcessCommandRunner` never shows a window for a plain `RunAsync`
        // call (`CreateNoWindow = true`), so there is nothing to restore focus from here.
        SetSystemPropertyOptimistically(id, value);

        ProcessResult result;
        try
        {
            // VERIFY: `wslc system property set <id> <value>` subcommand spelling.
            result = await _runner.RunAsync(_settings.SafeContainerBinaryPath(), ["system", "property", "set", id, value], ct);
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to set system property: {error.Message}");
            await LoadSystemPropertiesAsync(showLoading: false, ct: ct); // revert the optimistic update
            return;
        }

        if (result.Failed)
        {
            _alertCenter.Error(OrchardWinException.CliFailed($"system property set {id}", result.ExitCode, result.Stderr));
            await LoadSystemPropertiesAsync(showLoading: false, ct: ct);
            return;
        }

        // Success - refresh in the background to ensure consistency, matching Swift's detached
        // `Task { }` (fire-and-forget; the caller doesn't wait on this).
        _ = Task.Run(() => LoadSystemPropertiesAsync(showLoading: false, ct: CancellationToken.None), CancellationToken.None);
    }
}
