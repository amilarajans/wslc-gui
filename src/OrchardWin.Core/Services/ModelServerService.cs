using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns the model servers Orchard-Win has started and supervises their processes.
/// Complements <see cref="ModelService"/> (which only *detects* running servers): this one
/// *runs* them. Ported 1:1 from Orchard's `ModelServerService` at the logic level -
/// `servers`/`engineAvailable` published state, `AlertCenter` for user-facing failures, and
/// the `stopping` id-set that tells an intentional `Stop` from a crash.
///
/// A managed server never exits on its own, so lifecycle is driven entirely by the process's
/// termination handler: a user-requested stop is expected (remove quietly); any other exit
/// is a crash (mark failed + alert so the user can go read its log).
///
/// Orchard's macOS original also registers for `NSApplication.willTerminateNotification` and
/// calls `stopAll()` from it, since managed child processes outlive the app if left alone.
/// There is no direct equivalent notification at this (Core, platform-agnostic) layer - per
/// the task, that's the App project's job (call <see cref="StopAll"/> from its
/// startup/shutdown lifecycle handling, e.g. OnLaunched's exit path). This service only
/// exposes the method; wiring it to an actual exit event is a Wave-2 concern.
public sealed partial class ModelServerService : ObservableObject
{
    /// Servers this instance has started. An `ObservableCollection` so a bound list view
    /// sees incremental adds/removes/status-changes rather than a full rebind.
    public ObservableCollection<ManagedModelServer> Servers { get; } = [];

    /// Whether the engine binary is installed; drives the create affordance and guidance.
    [ObservableProperty]
    private bool _engineAvailable;

    private readonly IModelServerEngine _engine;
    private readonly AlertCenter _alertCenter;
    private readonly Dictionary<string, IServerProcess> _processes = [];

    /// Ids the user asked to stop, so the termination handler can tell an intended stop from
    /// a crash.
    private readonly HashSet<string> _stopping = [];

    public ModelServerService(IModelServerEngine engine, AlertCenter alertCenter)
    {
        _engine = engine;
        _alertCenter = alertCenter;
        _engineAvailable = engine.LocateBinary() is not null;
    }

    /// Terminate every managed server. Intended to be called from the App layer's exit
    /// handling (see class remarks); also safe to call directly (e.g. a "stop all" button).
    public void StopAll()
    {
        foreach (var (id, process) in _processes)
        {
            _stopping.Add(id);
            process.Terminate();
        }
    }

    /// Ports currently bound by managed servers, so the detected-provider list
    /// (<see cref="ModelService.Providers"/>) can hide the duplicates they would otherwise
    /// appear as.
    public HashSet<ushort> ManagedPorts => new(Servers.Select(server => server.Port));

    private static string ServerId(string model, ushort port) => $"{model}@{port}";

    private static string LogPath(string id)
    {
        var dir = Path.Combine(Path.GetTempPath(), "orchardwin-model-servers");
        Directory.CreateDirectory(dir);
        var safe = id.Replace('/', '_').Replace(':', '_');
        return Path.Combine(dir, $"{safe}.log");
    }

    /// Start a managed server for `model`, bound to `host:port`. Returns false (and raises a
    /// user-facing alert) for the same guard conditions Orchard checks: an empty model name,
    /// or a server already running for that model+port pair.
    public bool Start(string model, string host, ushort port)
    {
        var trimmed = model.Trim();
        if (trimmed.Length == 0)
        {
            _alertCenter.Error("Enter a model to serve (e.g. llama3.2:1b).");
            return false;
        }

        var id = ServerId(trimmed, port);
        if (_processes.ContainsKey(id))
        {
            _alertCenter.Error($"A server for {trimmed} on port {port} is already running.");
            return false;
        }

        var logPath = LogPath(id);
        try
        {
            var process = _engine.Launch(trimmed, host, port, logPath);
            process.TerminationHandler = code => HandleTermination(id, code);
            _processes[id] = process;
            Servers.Add(new ManagedModelServer
            {
                Id = id,
                Model = trimmed,
                Host = host,
                Port = port,
                Status = ManagedModelServerStatus.Running,
                LogPath = logPath,
            });
            return true;
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to start server: {ex.Message}");
            return false;
        }
    }

    /// Stop a specific managed server by id. A no-op if `id` isn't currently running.
    public void Stop(string id)
    {
        if (!_processes.TryGetValue(id, out var process)) return;
        _stopping.Add(id);
        process.Terminate();
    }

    private void HandleTermination(string id, int code)
    {
        _processes.Remove(id);
        var wasIntentional = _stopping.Remove(id);

        if (wasIntentional)
        {
            var existing = Servers.FirstOrDefault(server => server.Id == id);
            if (existing is not null) Servers.Remove(existing);
            return;
        }

        var index = IndexOf(id);
        if (index < 0) return;

        // Unexpected exit - keep it visible as failed so the user can read its log, exactly
        // like Orchard's original. `ManagedModelServer` is a record, so mutating `Status` in
        // place wouldn't raise a collection change notification for a bound list -
        // reassigning the slot does.
        var failed = Servers[index] with { Status = ManagedModelServerStatus.Failed };
        Servers[index] = failed;
        _alertCenter.Error($"Model server {failed.Model} stopped unexpectedly (exit {code}). Check its log.");
    }

    private int IndexOf(string id)
    {
        for (var i = 0; i < Servers.Count; i++)
        {
            if (Servers[i].Id == id) return i;
        }
        return -1;
    }
}
