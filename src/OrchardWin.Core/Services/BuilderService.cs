using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services;

/// Owns BuildKit-equivalent builder state and lifecycle, backed by the `wslc build` CLI.
/// Ported from Orchard's `BuilderService` - same polling/degrade-silently-on-failure shape,
/// using `CliParsers.ParseBuilderStatus` for the actual JSON decode.
public sealed partial class BuilderService : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<Builder> _builders = [];

    [ObservableProperty]
    private BuilderStatus _builderStatus = BuilderStatus.Stopped;

    [ObservableProperty]
    private bool _isBuilderLoading;

    [ObservableProperty]
    private bool _isBuildersLoading;

    private readonly ICommandRunner _runner;
    private readonly SettingsStore _settings;
    private readonly AlertCenter _alertCenter;

    public BuilderService(ICommandRunner runner, SettingsStore settings, AlertCenter alertCenter)
    {
        _runner = runner;
        _settings = settings;
        _alertCenter = alertCenter;
    }

    /// This runs on a poll - a spawn failure (binary missing), a nonzero exit, and a decode
    /// failure all degrade silently to `.Stopped` and log; only user-initiated builder actions
    /// (start/stop/delete) surface alerts. Same KNOWN-ISSUE as Swift's original: a zero-exit
    /// decode failure means the builder state is genuinely *unknown* (possibly running), yet
    /// this reports it as definitively `.Stopped` - revisit if that proves misleading.
    public async Task LoadBuildersAsync(CancellationToken ct = default)
    {
        IsBuildersLoading = true;

        ProcessResult result;
        try
        {
            // VERIFY: `wslc build status --format json` - Docker's nearest equivalent is
            // buildx; wslc's build-management surface is unconfirmed from macOS.
            result = await _runner.RunAsync(_settings.SafeContainerBinaryPath(), ["build", "status", "--format", "json"], ct);
        }
        catch (Exception error)
        {
            Builders = [];
            BuilderStatus = BuilderStatus.Stopped;
            IsBuildersLoading = false;
            Log.Containers.Error($"Builder status command could not run: {error.Message}");
            return;
        }

        if (result.Failed)
        {
            Builders = [];
            BuilderStatus = BuilderStatus.Stopped;
            IsBuildersLoading = false;
            var detail = result.Stderr?.Trim();
            if (!string.IsNullOrEmpty(detail))
                Log.Containers.Error($"Builder status command failed (exit {result.ExitCode}). Stderr:\n{detail}");
            else
                Log.Containers.Error($"Builder status command failed with unknown error (exit {result.ExitCode}).");
            return;
        }

        var parsed = CliParsers.ParseBuilderStatus(result.Stdout ?? "");
        switch (parsed.Kind)
        {
            case CliParsers.BuilderParseKind.NotRunning:
                Builders = [];
                BuilderStatus = BuilderStatus.Stopped;
                IsBuildersLoading = false;
                Log.Containers.Debug("Builder status indicates no builder present.");
                break;

            case CliParsers.BuilderParseKind.Builders:
                Builders = new ObservableCollection<Builder>(parsed.Builders);
                BuilderStatus = parsed.Builders.Count > 0
                    && string.Equals(parsed.Builders[0].Status, "running", StringComparison.OrdinalIgnoreCase)
                    ? BuilderStatus.Running
                    : BuilderStatus.Stopped;
                IsBuildersLoading = false;
                foreach (var b in parsed.Builders)
                    Log.Containers.Debug($"Builder: {b.Configuration.Id}, Status: {b.Status}");
                break;

            case CliParsers.BuilderParseKind.DecodeFailure:
                Log.Containers.Error($"Failed to decode builder status. Stdout preview (first 200 chars):\n{parsed.Preview}");
                Builders = [];
                BuilderStatus = BuilderStatus.Stopped;
                IsBuildersLoading = false;
                break;
        }
    }

    public Task StartBuilderAsync(CancellationToken ct = default) =>
        RunBuilderCommandAsync("start", () => LoadBuildersAsync(ct), ct);

    public Task StopBuilderAsync(CancellationToken ct = default) =>
        RunBuilderCommandAsync("stop", () => LoadBuildersAsync(ct), ct);

    public Task DeleteBuilderAsync(CancellationToken ct = default) =>
        RunBuilderCommandAsync("delete", () => { Builders = []; return Task.CompletedTask; }, ct);

    /// Run `wslc build <verb>` (a user-initiated action), alerting on failure and running
    /// `onSuccess` when it succeeds.
    private async Task RunBuilderCommandAsync(string verb, Func<Task> onSuccess, CancellationToken ct)
    {
        IsBuilderLoading = true;
        _alertCenter.Dismiss();

        try
        {
            // VERIFY: `wslc build start|stop|delete` subcommand names - best-effort mirror of
            // Apple's `container builder start|stop|delete`.
            var result = await _runner.RunAsync(_settings.SafeContainerBinaryPath(), ["build", verb], ct);
            IsBuilderLoading = false;
            if (result.Failed)
            {
                _alertCenter.Error(OrchardWinException.CliFailed($"build {verb}", result.ExitCode, result.Stderr));
            }
            else
            {
                Log.Containers.Debug($"Builder {verb} command sent successfully");
                await onSuccess();
            }
        }
        catch (Exception error)
        {
            IsBuilderLoading = false;
            _alertCenter.Error($"Failed to {verb} builder: {error.Message}");
            Log.Containers.Error($"Error running builder {verb}: {error.Message}");
        }
    }
}
