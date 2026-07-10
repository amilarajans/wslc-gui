using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns container-machine state and lifecycle, backed by <see cref="IMachineBackend"/>.
/// Mirrors the other per-domain services: observable state, <see cref="AlertCenter"/> for
/// user-facing errors, and a <see cref="LoadAsync"/> the app's refresh loop calls. Ported 1:1
/// from Orchard's `MachineService`. Machines never enter a container "god object" - this is a
/// standalone service, same as the original.
public sealed partial class MachineService : ObservableObject
{
    private readonly IMachineBackend _backend;
    private readonly AlertCenter _alertCenter;

    public MachineService(IMachineBackend backend, AlertCenter alertCenter)
    {
        _backend = backend;
        _alertCenter = alertCenter;
    }

    public ObservableCollection<Machine> Machines { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    /// Whether a create is in flight - drives the create form's spinner and disables
    /// re-submit.
    [ObservableProperty]
    private bool _isCreating;

    /// True when the machine API is unreachable - typically a WSL / container-preview install
    /// that predates machine support (see <see cref="OrchardWinExceptionKind.MachineApiUnavailable"/>).
    /// Drives an explanatory empty state instead of an error alert.
    [ObservableProperty]
    private bool _apiUnavailable;

    public async Task LoadAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading) IsLoading = true;

        try
        {
            var machines = await _backend.ListMachinesAsync(ct);
            ReplaceMachines(machines);
            ApiUnavailable = false;
        }
        catch (OrchardWinException ex) when (ex.Kind == OrchardWinExceptionKind.MachineApiUnavailable)
        {
            // Expected on installs without machine support: show the empty state, never
            // alert - mirrors Swift's dedicated `catch OrchardError.machineApiUnavailable`.
            ApiUnavailable = true;
            Machines.Clear();
        }
        catch (Exception ex)
        {
            if (showLoading)
                _alertCenter.Error($"Failed to load machines: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ReplaceMachines(IReadOnlyList<Machine> machines)
    {
        if (Machines.Count == machines.Count && Machines.SequenceEqual(machines)) return;
        Machines.Clear();
        foreach (var machine in machines) Machines.Add(machine);
    }

    public async Task<bool> CreateAsync(MachineCreateSpec spec, CancellationToken ct = default)
    {
        IsCreating = true;
        try
        {
            await _backend.CreateMachineAsync(spec, ct);
            await LoadAsync(showLoading: false, ct);
            return true;
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to create machine: {ex.Message}");
            return false;
        }
        finally
        {
            IsCreating = false;
        }
    }

    /// Applies an edited boot config. `SetMachineConfigAsync` only takes effect on the next
    /// boot; when `restartNow` is set (offered while running), stop-then-boot so the change
    /// goes live in one action - the differentiator over the CLI's manual stop/restart.
    public async Task<bool> ApplyConfigAsync(MachineConfigSpec config, string id, bool restartNow, CancellationToken ct = default)
    {
        try
        {
            await _backend.SetMachineConfigAsync(id, config, ct);
            if (restartNow)
            {
                // Ignore a stop error (already stopped); the boot is what makes the change
                // live.
                try { await _backend.StopMachineAsync(id, ct); } catch { /* already stopped is fine */ }
                await _backend.BootMachineAsync(id, ct);
            }
            await LoadAsync(showLoading: false, ct);
            return true;
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to update machine: {ex.Message}");
            return false;
        }
    }

    public async Task BootAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _backend.BootMachineAsync(id, ct);
            await LoadAsync(showLoading: false, ct);
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to start machine: {ex.Message}");
        }
    }

    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _backend.StopMachineAsync(id, ct);
            await LoadAsync(showLoading: false, ct);
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to stop machine: {ex.Message}");
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _backend.DeleteMachineAsync(id, ct);
            await LoadAsync(showLoading: false, ct);
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to delete machine: {ex.Message}");
        }
    }

    public async Task SetDefaultAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _backend.SetDefaultMachineAsync(id, ct);
            await LoadAsync(showLoading: false, ct);
        }
        catch (Exception ex)
        {
            _alertCenter.Error($"Failed to set default machine: {ex.Message}");
        }
    }

    /// Reads a machine's logs as text lines, tailed to `tailLines`. Unlike Orchard's
    /// `fetchLogs(id:boot:tailLines:)`, there is no separate boot-log route here:
    /// `IMachineBackend.MachineLogsAsync` exposes a single combined stream (the backing
    /// container's `wslc container logs`), so there is no `boot` parameter to select a
    /// second file handle.
    public async Task<IReadOnlyList<string>> FetchLogsAsync(string id, int tailLines = 5000, CancellationToken ct = default)
    {
        await using var stream = await _backend.MachineLogsAsync(id, ct);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync(ct);
        var lines = text.Split('\n');
        return lines.Length > tailLines ? lines[^tailLines..] : lines;
    }
}
