using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// System health returned by Ping().
public sealed record SystemHealthInfo(string ApiServerVersion);

/// Everything needed to create and start a container, expressed in app-owned types so the
/// backend boundary never leaks CLI/wire shapes to callers.
public sealed record ContainerCreateSpec
{
    public required string Id { get; init; }
    public required string ImageRef { get; init; }
    public List<string> Environment { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
    public List<string> CommandOverride { get; init; } = [];
    public List<ContainerVolumeSpec> Volumes { get; init; } = [];
    public List<ContainerPortSpec> PublishedPorts { get; init; } = [];
    public string DnsDomain { get; init; } = "";
    public string NetworkName { get; init; } = "";
    public bool AutoRemove { get; init; }
    /// Key/value labels stamped on the container at creation - e.g. the sandbox marker that
    /// lets the Sandboxes view recognise a container Orchard-Win wired to a model.
    public Dictionary<string, string> Labels { get; init; } = [];
}

public sealed record ContainerVolumeSpec(string HostPath, string ContainerPath, bool ReadOnly);

public sealed record ContainerPortSpec(ushort HostPort, ushort ContainerPort, string TransportProtocol);

/// The container runtime surface, expressed entirely in app domain models. A test double
/// implementing this needs no wslc/process dependency. Ported 1:1 from Orchard's
/// `ContainerBackend` protocol - same method set, same "no package types cross this
/// boundary" rule, just backed by `wslc.exe` (CLI + JSON) instead of an XPC client, since
/// the Microsoft.WSL.Containers native API is still a preview NuGet package whose exact
/// surface wasn't verifiable from macOS (see ARCHITECTURE.md). Swap in a native-API-backed
/// implementation later without touching any caller.
public interface IContainerBackend
{
    Task<IReadOnlyList<Container>> ListContainersAsync(CancellationToken ct = default);
    Task StopContainerAsync(string id, CancellationToken ct = default);
    Task KillContainerAsync(string id, int signal, CancellationToken ct = default);
    Task DeleteContainerAsync(string id, bool force, CancellationToken ct = default);
    Task BootstrapAndStartAsync(string id, CancellationToken ct = default);
    Task<Stream> ContainerLogsAsync(string id, CancellationToken ct = default);
    Task<ContainerStats> StatsAsync(string id, CancellationToken ct = default);
    Task CreateContainerAsync(ContainerCreateSpec spec, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerImage>> ListImagesAsync(CancellationToken ct = default);
    Task PullImageAsync(string reference, IProgress<ImagePullProgress>? progress = null, CancellationToken ct = default);
    Task DeleteImageAsync(string reference, CancellationToken ct = default);
    Task<ImageInspection> InspectImageAsync(string reference, CancellationToken ct = default);
    Task<IReadOnlyList<ContainerNetwork>> ListNetworksAsync(CancellationToken ct = default);
    Task CreateNetworkAsync(string name, string? subnet, Dictionary<string, string> labels, CancellationToken ct = default);
    Task DeleteNetworkAsync(string id, CancellationToken ct = default);

    /// Network attachments for one container (from <c>container inspect</c> NetworkSettings).
    /// Empty when the container has no networks or inspect fails.
    Task<IReadOnlyList<ContainerNetworkAttachment>> ListContainerNetworkAttachmentsAsync(
        string containerId, CancellationToken ct = default);
    Task<SystemHealthInfo> PingAsync(CancellationToken ct = default);
    Task<SystemDiskUsage> DiskUsageAsync(CancellationToken ct = default);
}

/// A container's labels marking it as the backing container of a Machine, mirroring
/// Orchard's `MachineBackingContainer`: the raw `wslc container ls` list includes these;
/// the CLI hides them client-side and so does this app, keeping them out of the Containers
/// list where their actions would be meaningless.
public static class MachineBackingContainer
{
    public const string PluginLabelKey = "dev.orchardwin.plugin";
    public const string MachinePluginValue = "machine";

    public static bool IsMachine(IReadOnlyDictionary<string, string> labels) =>
        labels.TryGetValue(PluginLabelKey, out var value) && value == MachinePluginValue;
}
