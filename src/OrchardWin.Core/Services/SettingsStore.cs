using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services;

/// On-disk shape for `%LOCALAPPDATA%\OrchardWin\settings.json`. Windows has no per-app
/// UserDefaults equivalent reachable from a plain class library (`Windows.Storage.
/// ApplicationData` is a WinUI/App-layer API - see the App project), so settings persist to a
/// small JSON file instead, following the same read/write-whole-file pattern as Orchard's
/// `StatsPersistence`.
internal sealed record PersistedSettings
{
    public string? CustomBinaryPath { get; init; }
    public string? PreferredTerminal { get; init; }
}

/// Owns user settings: the `wslc` binary path and the preferred terminal. Ported from
/// Orchard's `SettingsStore` - same default-path candidate list / validate / cache shape,
/// re-targeted at `wslc.exe` and Windows terminal executables, and backed by a JSON file on
/// disk instead of `UserDefaults`.
public sealed partial class SettingsStore : ObservableObject
{
    [ObservableProperty]
    private string? _customBinaryPath;

    [ObservableProperty]
    private TerminalApp _preferredTerminal = TerminalApp.Cmd;

    [ObservableProperty]
    private ObservableCollection<TerminalApp> _installedTerminals = new([TerminalApp.Cmd]);

    private readonly AlertCenter _alertCenter;
    private readonly string _settingsFilePath;
    private PersistedSettings _persisted;

    /// wslc.exe candidate locations, checked in order. The container preview is expected to
    /// ship as an MSIX app-execution alias (the same mechanism `wsl.exe` itself uses), so the
    /// WindowsApps alias directory is the primary guess; a bare filename falls back to
    /// resolving against PATH.
    /// VERIFY: wslc.exe's actual install location(s) - unconfirmed from macOS; grep this
    /// list once `wslc --help` / a real install is available on Windows.
    private static readonly IReadOnlyList<string> CandidateBinaryPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wslc.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WSL", "wslc.exe"),
    ];

    /// Last-resort default: a bare filename, resolved against PATH at spawn time even if our
    /// own `ValidateBinaryPath` probe can't find it up front.
    private const string FallbackBinaryPath = "wslc.exe";

    /// Cache the resolved default so we don't re-probe several candidate paths on every CLI
    /// call. Invalidated whenever the binary configuration changes.
    private string? _cachedDefaultBinaryPath;

    private string DefaultBinaryPath
    {
        get
        {
            if (_cachedDefaultBinaryPath is { } cached) return cached;
            var resolved = CandidateBinaryPaths.FirstOrDefault(ValidateBinaryPath) ?? FallbackBinaryPath;
            _cachedDefaultBinaryPath = resolved;
            return resolved;
        }
    }

    public string ContainerBinaryPath
    {
        get
        {
            var path = CustomBinaryPath ?? DefaultBinaryPath;
            return ValidateBinaryPath(path) ? path : DefaultBinaryPath;
        }
    }

    public bool IsUsingCustomBinary =>
        CustomBinaryPath is { } customPath && customPath != DefaultBinaryPath && ValidateBinaryPath(customPath);

    /// <param name="settingsFilePath">Overridable for tests so they never read/write the
    /// real user's settings file. Production uses the default `%LOCALAPPDATA%` path.</param>
    public SettingsStore(AlertCenter alertCenter, string? settingsFilePath = null)
    {
        _alertCenter = alertCenter;
        _settingsFilePath = settingsFilePath ?? DefaultSettingsFilePath();
        _persisted = LoadPersisted();

        if (!string.IsNullOrEmpty(_persisted.CustomBinaryPath))
        {
            CustomBinaryPath = _persisted.CustomBinaryPath;
        }

        LoadPreferredTerminal();
    }

    private static string DefaultSettingsFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrchardWin", "settings.json");

    // MARK: - Binary path

    public void SetCustomBinaryPath(string? path)
    {
        CustomBinaryPath = path;
        _cachedDefaultBinaryPath = null; // re-detect on any binary-config change
        _persisted = _persisted with { CustomBinaryPath = string.IsNullOrEmpty(path) ? null : path };
        Persist();
    }

    public void ResetToDefaultBinary() => SetCustomBinaryPath(null);

    public bool ValidateAndSetCustomBinaryPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            SetCustomBinaryPath(null);
            return true;
        }

        if (!ValidateBinaryPath(path)) return false;

        // If the selected path is the same as the resolved default, treat it as default.
        if (path == DefaultBinaryPath)
        {
            SetCustomBinaryPath(null);
        }
        else
        {
            SetCustomBinaryPath(path);
        }
        return true;
    }

    /// The binary path to use, falling back to the default (and clearing an invalid custom
    /// path) if the current one is unusable.
    public string SafeContainerBinaryPath()
    {
        var currentPath = CustomBinaryPath ?? DefaultBinaryPath;
        if (ValidateBinaryPath(currentPath)) return currentPath;

        if (CustomBinaryPath is not null)
        {
            var fallback = DefaultBinaryPath;
            CustomBinaryPath = null;
            _persisted = _persisted with { CustomBinaryPath = null };
            Persist();
            _alertCenter.Error($"Invalid binary path detected. Reset to default: {fallback}");
        }
        return DefaultBinaryPath;
    }

    /// A path "validates" if it names a file that exists - either directly (rooted/contains a
    /// separator) or, for a bare filename, somewhere on PATH. Windows has no POSIX executable
    /// bit to check; existence is the best signal available without actually spawning it.
    private static bool ValidateBinaryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (Path.IsPathRooted(path) || path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            return File.Exists(path);
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, path))) return true;
        }
        return false;
    }

    // MARK: - Preferred terminal

    private void LoadPreferredTerminal()
    {
        var detected = DetectInstalledTerminals();
        InstalledTerminals = new ObservableCollection<TerminalApp>(detected);

        if (_persisted.PreferredTerminal is { } saved
            && Enum.TryParse<TerminalApp>(saved, out var terminal)
            && detected.Contains(terminal))
        {
            PreferredTerminal = terminal;
        }
        else if (detected.Count > 0)
        {
            PreferredTerminal = detected[0];
        }
        else
        {
            PreferredTerminal = TerminalApp.Cmd; // always available - ships with every Windows install
        }
    }

    public void SetPreferredTerminal(TerminalApp terminal)
    {
        PreferredTerminal = terminal;
        _persisted = _persisted with { PreferredTerminal = terminal.ToString() };
        Persist();
    }

    /// Probe well-known locations / PATH for each terminal's executable. `cmd.exe` ships with
    /// every Windows install, so it's always reported installed - the direct equivalent of
    /// Terminal.app always being present on macOS.
    private static IReadOnlyList<TerminalApp> DetectInstalledTerminals()
    {
        var result = new List<TerminalApp>();
        foreach (var app in new[] { TerminalApp.WindowsTerminal, TerminalApp.PowerShell, TerminalApp.Cmd })
        {
            if (app == TerminalApp.Cmd || IsTerminalInstalled(app)) result.Add(app);
        }
        return result;
    }

    /// VERIFY: well-known install locations below are best-effort guesses, not confirmed on
    /// a real Windows box - `wt.exe` ships as an app-execution alias under WindowsApps for
    /// Store installs; `pwsh.exe`'s MSI installer defaults to Program Files\PowerShell\7.
    private static bool IsTerminalInstalled(TerminalApp app)
    {
        if (ValidateBinaryPath(app.ExecutableName())) return true;

        IReadOnlyList<string> candidates = app switch
        {
            TerminalApp.WindowsTerminal =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe"),
            ],
            TerminalApp.PowerShell =>
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            ],
            _ => [],
        };
        return candidates.Any(File.Exists);
    }

    // MARK: - Persistence

    private PersistedSettings LoadPersisted()
    {
        try
        {
            if (!File.Exists(_settingsFilePath)) return new PersistedSettings();
            var json = File.ReadAllText(_settingsFilePath);
            return JsonSerializer.Deserialize<PersistedSettings>(json) ?? new PersistedSettings();
        }
        catch (Exception ex)
        {
            // Missing/corrupt file - start fresh rather than crashing on launch, mirroring
            // StatsPersistence's best-effort load.
            Log.Ui.Error($"Failed to load settings, starting fresh: {ex.Message}");
            return new PersistedSettings();
        }
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_settingsFilePath, JsonSerializer.Serialize(_persisted));
        }
        catch (Exception ex)
        {
            Log.Ui.Error($"Failed to persist settings: {ex.Message}");
        }
    }
}
