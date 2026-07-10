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
            // wsl may put text on stderr on some builds; use either stream.
            var text = FirstNonEmpty(result.Stdout, result.Stderr);
            KernelInfo = !string.IsNullOrWhiteSpace(text) ? ParseKernelInfo(text!) : new WslKernelInfo();
            if (KernelInfo.WslVersion is null && KernelInfo.KernelVersion is null && !result.Failed)
            {
                Log.Containers.Error($"wsl --version produced unparseable output: {Preview(text)}");
            }
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

    /// Parses `wsl --version` "Label: value" lines (English Windows 11 shape). Defensive for
    /// locale variants and accidental UTF-16 null padding left after a mis-decode.
    ///   WSL version: 2.x.x.x
    ///   Kernel version: 5.x.x.x
    internal static WslKernelInfo ParseKernelInfo(string stdout)
    {
        string? kernelVersion = null;
        string? wslVersion = null;
        string? wslgVersion = null;
        string? windowsVersion = null;

        foreach (var rawLine in stdout.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            // Strip leftover NUL chars from UTF-16 mis-decoded as a single-byte encoding.
            var line = rawLine.Replace("\0", "", StringComparison.Ordinal).Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;

            var key = NormalizeVersionKey(line[..colon]);
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0) continue;

            if (key is "kernel version" or "kernel") kernelVersion = value;
            else if (key is "wsl version" or "wsl") wslVersion = value;
            else if (key is "wslg version" or "wslg") wslgVersion = value;
            else if (key is "windows version" or "windows") windowsVersion = value;
        }

        return new WslKernelInfo
        {
            KernelVersion = kernelVersion,
            WslVersion = wslVersion,
            WslgVersion = wslgVersion,
            WindowsVersion = windowsVersion,
        };
    }

    private static string NormalizeVersionKey(string key) =>
        key.Replace("\0", "", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant()
            .Replace("  ", " ", StringComparison.Ordinal);

    // MARK: - System properties

    public async Task LoadSystemPropertiesAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading)
        {
            IsSystemPropertiesLoading = true;
            _alertCenter.Dismiss();
        }

        // Real wslc has no `system property list` (only `system session`). Build a useful
        // read-only property table from `wsl --version`, `wslc version`, and `wsl --status`.
        try
        {
            var props = new List<SystemProperty>();

            var versionResult = await _runner.RunAsync("wsl.exe", ["--version"], ct);
            var versionText = FirstNonEmpty(versionResult.Stdout, versionResult.Stderr);
            if (!string.IsNullOrWhiteSpace(versionText))
            {
                foreach (var rawLine in versionText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Replace("\0", "", StringComparison.Ordinal).Trim();
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var id = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (id.Length == 0 || value.Length == 0) continue;
                    props.Add(new SystemProperty
                    {
                        Id = id,
                        Type = PropertyType.String,
                        Value = value,
                        Description = "From wsl --version",
                    });
                }

                // Keep KernelInfo in sync when properties load first / alone.
                var parsed = ParseKernelInfo(versionText);
                if (parsed.WslVersion is not null || parsed.KernelVersion is not null)
                    KernelInfo = parsed;
            }

            var wslcPath = _settings.SafeContainerBinaryPath();
            var wslcResult = await _runner.RunAsync(wslcPath, ["version"], ct);
            var wslcText = FirstNonEmpty(wslcResult.Stdout, wslcResult.Stderr)?.Trim();
            if (!string.IsNullOrWhiteSpace(wslcText))
            {
                // "wslc 2.9.3.0" → take last token as version.
                var parts = wslcText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var wslcVersion = parts.Length > 0 ? parts[^1] : wslcText;
                props.Add(new SystemProperty
                {
                    Id = "wslc version",
                    Type = PropertyType.String,
                    Value = wslcVersion,
                    Description = "From wslc version",
                });
                ContainerVersion = wslcVersion;
                ParsedContainerVersion = wslcVersion;
            }

            var statusResult = await _runner.RunAsync("wsl.exe", ["--status"], ct);
            var statusText = FirstNonEmpty(statusResult.Stdout, statusResult.Stderr);
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                foreach (var rawLine in statusText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = rawLine.Replace("\0", "", StringComparison.Ordinal).Trim();
                    var colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    var id = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (id.Length == 0 || value.Length == 0) continue;
                    props.Add(new SystemProperty
                    {
                        Id = id,
                        Type = PropertyType.String,
                        Value = value,
                        Description = "From wsl --status",
                    });
                }
            }

            props.Add(new SystemProperty
            {
                Id = "wslc path",
                Type = PropertyType.String,
                Value = wslcPath,
                Description = "Resolved container CLI path",
            });

            SystemProperties = new ObservableCollection<SystemProperty>(props);
        }
        catch (Exception error)
        {
            Log.Containers.Error($"Failed to load system properties: {error.Message}");
            if (showLoading)
                _alertCenter.Error($"Failed to load system properties: {error.Message}");
            SystemProperties = [];
        }
        finally
        {
            IsSystemPropertiesLoading = false;
        }
    }

    private static string? FirstNonEmpty(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) ? a : !string.IsNullOrWhiteSpace(b) ? b : null;

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        var cleaned = text.Replace("\0", "\\0", StringComparison.Ordinal);
        return cleaned.Length > 120 ? cleaned[..120] + "…" : cleaned;
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
