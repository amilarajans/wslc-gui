using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns the container list and lifecycle: load, start (with retry/recovery), stop, kill,
/// remove, run, recreate, logs, and the mounts derived from the list. Ported 1:1 from
/// Orchard's `ContainerListService.swift`; the only structural change is dropping
/// SwiftUI's `@MainActor`/`withAnimation` (no UI layer in this project) and swapping the
/// `DispatchQueue`-backed operation lock for a plain `lock` gate.
public sealed partial class ContainerListService : ObservableObject
{
    /// Number of polls before a refresh loop gives up and clears the loading state.
    public const int MaxRefreshAttempts = 10;

    private readonly IContainerBackend _backend;
    private readonly AlertCenter _alertCenter;

    /// Delay between polls in the `RefreshUntilContainer…` loops. Injected; production uses
    /// the 0.5s default, tests can pass `TimeSpan.Zero` to drive the loops without real waits.
    private readonly TimeSpan _pollInterval;

    [ObservableProperty]
    private ObservableCollection<Container> _containers = [];

    /// Unique mounts across the current containers. Recomputed only when `Containers`
    /// actually changes, not on every read - several views read it on each tick.
    [ObservableProperty]
    private IReadOnlyList<ContainerMount> _allMounts = [];

    [ObservableProperty]
    private bool _isContainersLoading;

    // Set<string> in the Swift original. CommunityToolkit's [ObservableProperty] only
    // notifies on whole-field replacement, which doesn't fit "insert/remove one id" - so
    // these are hand-rolled with a lock (fire-and-forget Task continuations run on the
    // thread pool here, unlike Swift's @MainActor serialization) and an explicit
    // OnPropertyChanged only when membership actually changes.
    private readonly object _stateGate = new();
    private readonly HashSet<string> _loadingContainers = [];
    private readonly HashSet<string> _recoveryFailedContainerIds = [];

    /// Containers currently mid-operation (start/stop/kill/remove) - drives per-row spinners.
    public IReadOnlyCollection<string> LoadingContainers => _loadingContainers;

    /// Containers whose automatic recovery failed - drives the persistent "Recreate"
    /// affordance, which must outlive the transient alert. Cleared on a successful start.
    public IReadOnlyCollection<string> RecoveryFailedContainerIds => _recoveryFailedContainerIds;

    /// Refresh builder state after a lifecycle change. Set by the owner (AppServices).
    public Func<Task>? ReloadBuilders { get; set; }

    // Prevent multiple simultaneous operations on the same container.
    private readonly HashSet<string> _containerOperationLocks = [];
    // Configuration snapshots for recovery.
    private readonly Dictionary<string, Container> _containerSnapshots = new();

    public ContainerListService(IContainerBackend backend, AlertCenter alertCenter, TimeSpan? pollInterval = null)
    {
        _backend = backend;
        _alertCenter = alertCenter;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(0.5);
    }

    private static List<ContainerMount> ComputeMounts(IEnumerable<Container> containers)
    {
        var mountDict = new Dictionary<string, ContainerMount>();
        foreach (var container in containers)
        {
            foreach (var mount in container.Configuration.Mounts)
            {
                var mountId = $"{mount.Source}->{mount.Destination}";
                if (mountDict.TryGetValue(mountId, out var existingMount))
                {
                    var updatedContainerIds = existingMount.ContainerIds;
                    if (!updatedContainerIds.Contains(container.Configuration.Id))
                    {
                        updatedContainerIds = [.. updatedContainerIds, container.Configuration.Id];
                    }
                    mountDict[mountId] = new ContainerMount { Mount = mount, ContainerIds = updatedContainerIds };
                }
                else
                {
                    mountDict[mountId] = new ContainerMount { Mount = mount, ContainerIds = [container.Configuration.Id] };
                }
            }
        }
        return [.. mountDict.Values.OrderBy(m => m.Mount.Source, StringComparer.Ordinal)];
    }

    /// `Container` (and its nested records) hold `List<>`/`Dictionary<>` properties, which
    /// the compiler-synthesized record equality compares by reference, not by value - unlike
    /// Swift's synthesized `Equatable`, which IS structural for arrays. A JSON round-trip
    /// comparison is the simplest reliable "did anything actually change" check.
    private static bool AreContainersEqual(IReadOnlyList<Container> old, IReadOnlyList<Container> updated)
    {
        if (old.Count != updated.Count) return false;
        return JsonSerializer.Serialize(old) == JsonSerializer.Serialize(updated);
    }

    public async Task LoadAsync(bool showLoading = false, CancellationToken ct = default)
    {
        if (showLoading)
        {
            IsContainersLoading = true;
            _alertCenter.Dismiss();
        }

        try
        {
            var newContainers = await _backend.ListContainersAsync(ct);

            if (!AreContainersEqual(Containers, newContainers))
            {
                Containers = new ObservableCollection<Container>(newContainers);
                AllMounts = ComputeMounts(Containers);
            }
            IsContainersLoading = false;

            foreach (var container in newContainers)
            {
                _containerSnapshots[container.Configuration.Id] = container;
            }

            foreach (var container in newContainers)
            {
                Log.Containers.Debug($"Container: {container.Configuration.Id}, Status: {container.Status}");
            }
        }
        catch (Exception error)
        {
            // Background refreshes stay silent; only a user-initiated load alerts.
            _alertCenter.Error(error.Message, showLoading ? AlertSource.User : AlertSource.Background);
            IsContainersLoading = false;
            Log.Containers.Error(error.Message);
        }
    }

    public async Task ForceStopContainerAsync(string id, CancellationToken ct = default)
    {
        AddLoading(id);
        _alertCenter.Dismiss();

        try
        {
            await _backend.KillContainerAsync(id, 9, ct);
            Log.Containers.Debug($"Container {id} force stop (SIGKILL) sent");
            FireAndForget(InvokeReloadBuildersAsync());
            FireAndForget(RefreshUntilContainerStoppedAsync(id, ct));
        }
        catch (Exception error)
        {
            RemoveLoading(id);
            _alertCenter.Error($"Failed to force stop container: {error.Message}");
            Log.Containers.Error($"Error force stopping container: {error.Message}");
        }
    }

    public async Task StopContainerAsync(string id, CancellationToken ct = default)
    {
        AddLoading(id);
        _alertCenter.Dismiss();

        try
        {
            await _backend.StopContainerAsync(id, ct);
            Log.Containers.Debug($"Container {id} stop command sent successfully");
            FireAndForget(InvokeReloadBuildersAsync());
            FireAndForget(RefreshUntilContainerStoppedAsync(id, ct));
        }
        catch (Exception error)
        {
            RemoveLoading(id);
            _alertCenter.Error($"Failed to stop container: {error.Message}");
            Log.Containers.Error($"Error stopping container: {error.Message}");
        }
    }

    public async Task StartContainerAsync(string id, int maxRetries = 3, double retryDelaySeconds = 1.0, CancellationToken ct = default)
    {
        bool shouldProceed;
        lock (_stateGate)
        {
            if (_containerOperationLocks.Contains(id))
            {
                shouldProceed = false;
            }
            else
            {
                _containerOperationLocks.Add(id);
                shouldProceed = true;
            }
        }

        if (!shouldProceed)
        {
            Log.Containers.Debug($"Container {id} operation already in progress, ignoring duplicate call");
            return;
        }

        try
        {
            await StartContainerWithRetryAsync(id, maxRetries, retryDelaySeconds, ct);
        }
        finally
        {
            lock (_stateGate) { _containerOperationLocks.Remove(id); }
        }
    }

    private async Task StartContainerWithRetryAsync(string id, int maxRetries, double retryDelaySeconds, CancellationToken ct)
    {
        AddLoading(id);
        _alertCenter.Dismiss();

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await _backend.BootstrapAndStartAsync(id, ct);

                Log.Containers.Debug($"Container {id} start command sent successfully (attempt {attempt})");
                RemoveRecoveryFailed(id);

                FireAndForget(InvokeReloadBuildersAsync());
                FireAndForget(RefreshUntilContainerStartedAsync(id, ct));
                return;
            }
            catch (Exception error)
            {
                var errorMsg = error.Message;
                Log.Containers.Error($"Container {id} failed to start (attempt {attempt}): {errorMsg}");

                var classified = OrchardWinException.ClassifyStartError(error, id);
                var containerNotFound = classified.Kind == OrchardWinExceptionKind.ContainerNotFound;
                var isTransitionError = classified.Kind == OrchardWinExceptionKind.ContainerInTransition;

                if (containerNotFound)
                {
                    Log.Containers.Debug($"Container {id} was auto-removed by runtime, attempting automatic recovery...");

                    if (await RecoverContainerAsync(id, ct))
                    {
                        // RecoverContainerAsync -> RunContainerAsync -> CreateContainerAsync
                        // already bootstraps and starts the container, so this is a completed
                        // start - do NOT loop back into another BootstrapAndStartAsync on the
                        // now-running container.
                        Log.Containers.Debug($"Container {id} successfully recovered and started");
                        RemoveRecoveryFailed(id);
                        FireAndForget(InvokeReloadBuildersAsync());
                        FireAndForget(RefreshUntilContainerStartedAsync(id, ct));
                        return;
                    }

                    Log.Containers.Error($"Container {id} recovery failed");
                    AddRecoveryFailed(id);
                    _alertCenter.Error("Container was automatically removed and could not be recovered. Original configuration may be lost.");
                    RemoveLoading(id);
                    FireAndForget(LoadAsync(ct: ct));
                    return;
                }

                if (isTransitionError)
                {
                    if (attempt == maxRetries)
                    {
                        _alertCenter.Error($"Container failed to start after {maxRetries} attempts. The container may be corrupted.");
                        RemoveLoading(id);
                        FireAndForget(LoadAsync(ct: ct));
                        return;
                    }

                    _alertCenter.Error("Container is in transition state, retrying...");
                }
                else
                {
                    _alertCenter.Error($"Failed to start container: {errorMsg}");
                    RemoveLoading(id);
                    FireAndForget(LoadAsync(ct: ct));
                    return;
                }
            }

            if (attempt < maxRetries)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), ct); }
                catch (OperationCanceledException) { /* cancellation ends the retry loop below */ }
            }
        }

        RemoveLoading(id);
    }

    private async Task RefreshUntilContainerStoppedAsync(string id, CancellationToken ct)
    {
        var attempts = 0;

        while (attempts < MaxRefreshAttempts)
        {
            await LoadAsync(ct: ct);

            bool shouldStop;
            var container = Containers.FirstOrDefault(c => c.Configuration.Id == id);
            if (container is not null)
            {
                Log.Containers.Debug($"Checking stop status for {id}: {container.Status}");
                shouldStop = !string.Equals(container.Status, "running", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Log.Containers.Debug($"Container {id} not found, assuming stopped");
                shouldStop = true;
            }

            if (shouldStop)
            {
                Log.Containers.Debug($"Container {id} has stopped, removing loading state");
                RemoveLoading(id);
                return;
            }

            attempts++;
            Log.Containers.Debug($"Container {id} still running, attempt {attempts}/{MaxRefreshAttempts}");
            try { await Task.Delay(_pollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }

        Log.Containers.Debug($"Timeout reached for container {id}, removing loading state");
        RemoveLoading(id);
    }

    private async Task RefreshUntilContainerStartedAsync(string id, CancellationToken ct)
    {
        var attempts = 0;

        while (attempts < MaxRefreshAttempts)
        {
            await LoadAsync(ct: ct);

            bool isRunning;
            var container = Containers.FirstOrDefault(c => c.Configuration.Id == id);
            if (container is not null)
            {
                Log.Containers.Debug($"Checking start status for {id}: {container.Status}");
                isRunning = string.Equals(container.Status, "running", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                isRunning = false;
            }

            if (isRunning)
            {
                Log.Containers.Debug($"Container {id} has started, removing loading state");
                RemoveLoading(id);
                return;
            }

            attempts++;
            Log.Containers.Debug($"Container {id} not running yet, attempt {attempts}/{MaxRefreshAttempts}");
            try { await Task.Delay(_pollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }

        Log.Containers.Debug($"Timeout reached for container {id}, removing loading state");
        RemoveLoading(id);
    }

    public async Task RemoveContainerAsync(string id, CancellationToken ct = default)
    {
        AddLoading(id);
        _alertCenter.Dismiss();

        try
        {
            await _backend.DeleteContainerAsync(id, false, ct);
            Log.Containers.Debug($"Container {id} remove command sent successfully");
            FireAndForget(InvokeReloadBuildersAsync());
            var remaining = Containers.Where(c => c.Configuration.Id != id).ToList();
            Containers = new ObservableCollection<Container>(remaining);
            AllMounts = ComputeMounts(Containers);
            RemoveLoading(id);
        }
        catch (Exception error)
        {
            RemoveLoading(id);
            _alertCenter.Error($"Failed to remove container: {error.Message}");
            Log.Containers.Error($"Error removing container: {error.Message}");
        }
    }

    public async Task RemoveContainersAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        foreach (var id in ids)
        {
            await RemoveContainerAsync(id, ct);
        }
    }

    /// The Swift original reads `[containerLog, bootlog]` file handles and only returns the
    /// first (container log). `IContainerBackend.ContainerLogsAsync` collapses that to a
    /// single stream at the backend boundary, so there is nothing to select between here.
    public async Task<IReadOnlyList<string>> FetchContainerLogsAsync(string containerId, int tailLines = 5000, CancellationToken ct = default)
    {
        await using var stream = await _backend.ContainerLogsAsync(containerId, ct);
        using var reader = new StreamReader(stream);
        var fullText = await reader.ReadToEndAsync(ct);
        var lines = fullText.Split('\n');
        return lines.Length > tailLines ? lines[^tailLines..] : lines;
    }

    public async Task RecreateContainerAsync(string oldContainerId, ContainerRunConfig newConfig, CancellationToken ct = default)
    {
        try
        {
            await _backend.DeleteContainerAsync(oldContainerId, true, ct);
            await RunContainerAsync(newConfig, ct);
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to recreate container: {error.Message}");
        }
    }

    // Recovery recreates an auto-removed container from its snapshot. It preserves every
    // field the create path (ContainerRunConfig -> ContainerCreateSpec -> CreateContainerAsync)
    // supports: env, ports, volumes (incl. readonly), working directory, network, DNS.
    // Labels, resources (cpus/memory), and hostname are NOT preserved - no field exists for
    // them anywhere in the create path (a normal `run` drops them too); adding them means
    // extending the spec and the backend create call. Command override is intentionally not
    // reconstructed: the snapshot stores the fully-resolved argv (image entrypoint already
    // applied), so re-feeding it as an override would double-apply the entrypoint.
    private async Task<bool> RecoverContainerAsync(string id, CancellationToken ct)
    {
        if (!_containerSnapshots.TryGetValue(id, out var snapshot))
        {
            Log.Containers.Debug($"No snapshot available for container {id}");
            return false;
        }

        Log.Containers.Debug($"Attempting to recover container {id} from snapshot...");

        var config = snapshot.Configuration;

        var envVars = new List<ContainerRunConfig.EnvironmentVariable>();
        foreach (var env in config.InitProcess.Environment)
        {
            var parts = env.Split('=', 2);
            if (parts.Length == 2)
            {
                envVars.Add(new ContainerRunConfig.EnvironmentVariable { Key = parts[0], Value = parts[1] });
            }
        }

        var portMappings = new List<ContainerRunConfig.PortMapping>();
        foreach (var port in config.PublishedPorts)
        {
            portMappings.Add(new ContainerRunConfig.PortMapping
            {
                HostPort = port.HostPort.ToString(),
                ContainerPort = port.ContainerPort.ToString(),
                TransportProtocol = port.TransportProtocol,
            });
        }

        var volumeMappings = new List<ContainerRunConfig.VolumeMapping>();
        foreach (var mount in config.Mounts)
        {
            volumeMappings.Add(new ContainerRunConfig.VolumeMapping
            {
                HostPath = mount.Source,
                ContainerPath = mount.Destination,
                ReadOnly = mount.Options.Contains("ro"),
            });
        }

        var runConfig = new ContainerRunConfig
        {
            Name = id,
            Image = config.Image.Reference,
            Detached = true,
            EnvironmentVariables = envVars,
            PortMappings = portMappings,
            VolumeMappings = volumeMappings,
            WorkingDirectory = config.InitProcess.WorkingDirectory,
            DnsDomain = config.Dns?.Domain ?? "",
            Network = snapshot.Networks.FirstOrDefault()?.Network ?? "",
        };

        var started = await RunContainerAsync(runConfig, ct);
        if (started)
        {
            Log.Containers.Debug($"Container {id} recovered successfully");
            return true;
        }

        Log.Containers.Error("Container recovery failed");
        return false;
    }

    public async Task<bool> RunContainerAsync(ContainerRunConfig config, CancellationToken ct = default)
    {
        try
        {
            // Swift takes the first 12 chars of a dashed UUID string, which can itself
            // include a literal '-'. Using the "N" (no-dashes) Guid format instead avoids
            // handing the CLI a name that starts with what looks like a flag.
            var id = string.IsNullOrEmpty(config.Name) ? Guid.NewGuid().ToString("N")[..12] : config.Name;

            var envStrings = new List<string>();
            foreach (var envVar in config.EnvironmentVariables)
            {
                if (string.IsNullOrEmpty(envVar.Key)) continue;
                envStrings.Add($"{envVar.Key}={envVar.Value}");
            }

            var volumes = new List<ContainerVolumeSpec>();
            foreach (var vol in config.VolumeMappings)
            {
                if (string.IsNullOrEmpty(vol.HostPath) || string.IsNullOrEmpty(vol.ContainerPath)) continue;
                volumes.Add(new ContainerVolumeSpec(vol.HostPath, vol.ContainerPath, vol.ReadOnly));
            }

            var ports = new List<ContainerPortSpec>();
            foreach (var pm in config.PortMappings)
            {
                if (ushort.TryParse(pm.HostPort, out var hp) && ushort.TryParse(pm.ContainerPort, out var cp))
                {
                    ports.Add(new ContainerPortSpec(hp, cp, pm.TransportProtocol));
                }
            }

            var commandArgs = new List<string>();
            if (!string.IsNullOrEmpty(config.CommandOverride))
            {
                commandArgs = [.. config.CommandOverride.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
            }

            var spec = new ContainerCreateSpec
            {
                Id = id,
                ImageRef = config.Image,
                Environment = envStrings,
                WorkingDirectory = config.WorkingDirectory,
                CommandOverride = commandArgs,
                Volumes = volumes,
                PublishedPorts = ports,
                DnsDomain = config.DnsDomain,
                NetworkName = config.Network,
                AutoRemove = config.RemoveAfterStop,
                Labels = config.Labels,
            };
            await _backend.CreateContainerAsync(spec, ct);

            RemoveRecoveryFailed(id);
            FireAndForget(LoadAsync(ct: ct));
            return true;
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to run container: {error.Message}");
            return false;
        }
    }

    private void AddLoading(string id)
    {
        bool changed;
        lock (_stateGate) { changed = _loadingContainers.Add(id); }
        if (changed) OnPropertyChanged(nameof(LoadingContainers));
    }

    private void RemoveLoading(string id)
    {
        bool changed;
        lock (_stateGate) { changed = _loadingContainers.Remove(id); }
        if (changed) OnPropertyChanged(nameof(LoadingContainers));
    }

    private void AddRecoveryFailed(string id)
    {
        bool changed;
        lock (_stateGate) { changed = _recoveryFailedContainerIds.Add(id); }
        if (changed) OnPropertyChanged(nameof(RecoveryFailedContainerIds));
    }

    private void RemoveRecoveryFailed(string id)
    {
        bool changed;
        lock (_stateGate) { changed = _recoveryFailedContainerIds.Remove(id); }
        if (changed) OnPropertyChanged(nameof(RecoveryFailedContainerIds));
    }

    private Task InvokeReloadBuildersAsync() => ReloadBuilders?.Invoke() ?? Task.CompletedTask;

    /// Swift's `Task { await self.foo() }` fire-and-forget calls run serialized on
    /// `@MainActor`; the closest equivalent here is a discarded `Task` whose faults are
    /// logged instead of silently lost (an un-awaited faulted `Task`'s exception would
    /// otherwise only surface via the finalizer's unobserved-exception event, if at all).
    private static void FireAndForget(Task task)
    {
        _ = task.ContinueWith(
            static t => Log.Containers.Error($"Background task failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
