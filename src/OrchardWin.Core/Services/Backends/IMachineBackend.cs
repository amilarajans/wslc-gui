using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// The container-machine runtime surface, expressed entirely in app domain models. Mirrors
/// `IContainerBackend`'s design rule: no CLI/wire types cross this boundary. Ported from
/// Orchard's `MachineBackend` protocol; see <see cref="Machine"/> for the WSL-distro mapping.
public interface IMachineBackend
{
    Task<IReadOnlyList<Machine>> ListMachinesAsync(CancellationToken ct = default);
    Task<Machine> InspectMachineAsync(string id, CancellationToken ct = default);
    Task CreateMachineAsync(MachineCreateSpec spec, CancellationToken ct = default);
    /// Update a machine's boot config. Takes effect on the next boot.
    Task SetMachineConfigAsync(string id, MachineConfigSpec config, CancellationToken ct = default);
    Task BootMachineAsync(string id, CancellationToken ct = default);
    Task StopMachineAsync(string id, CancellationToken ct = default);
    /// Delete a machine. This removes its persistent storage (unregisters the WSL distro).
    Task DeleteMachineAsync(string id, CancellationToken ct = default);
    Task SetDefaultMachineAsync(string id, CancellationToken ct = default);
    Task<Stream> MachineLogsAsync(string id, CancellationToken ct = default);
}
