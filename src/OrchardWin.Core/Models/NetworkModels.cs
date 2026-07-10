using System.Text.Json.Serialization;

namespace OrchardWin.Core.Models;

public sealed record ContainerNetwork
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("config")]
    public required NetworkConfig Config { get; init; }

    [JsonPropertyName("status")]
    public required NetworkStatus Status { get; init; }

    /// True for a host-only network: reachable from the host but with no internet egress.
    /// This is the sandbox-network property the model bridge relies on. Derived at mapping
    /// time from the networking mode wslc reports, not part of the wire shape.
    [JsonIgnore]
    public bool IsHostOnly { get; init; }
}

public sealed record NetworkConfig
{
    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels { get; init; } = [];

    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

public sealed record NetworkStatus
{
    [JsonPropertyName("gateway")]
    public string? Gateway { get; init; }

    [JsonPropertyName("address")]
    public string? Address { get; init; }
}

public sealed record DnsDomain
{
    public required string Domain { get; init; }
    public bool IsDefault { get; init; }
    public string Id => Domain;
}

public sealed record SystemDiskUsage
{
    public required DiskUsageSection Containers { get; init; }
    public required DiskUsageSection Images { get; init; }
    public required DiskUsageSection Volumes { get; init; }

    public long TotalSize => Containers.SizeInBytes + Images.SizeInBytes + Volumes.SizeInBytes;
    public long TotalReclaimable => Containers.Reclaimable + Images.Reclaimable + Volumes.Reclaimable;
}

public sealed record DiskUsageSection
{
    public int Active { get; init; }
    public long Reclaimable { get; init; }
    public long SizeInBytes { get; init; }
    public int Total { get; init; }

    public double ReclaimablePercent => SizeInBytes > 0 ? (double)Reclaimable / SizeInBytes * 100.0 : 0.0;
}

public sealed record ContainerStats
{
    public required string Id { get; init; }
    public long CpuUsageUsec { get; init; }
    public long MemoryUsageBytes { get; init; }
    public long MemoryLimitBytes { get; init; }
    public long BlockReadBytes { get; init; }
    public long BlockWriteBytes { get; init; }
    public long NetworkRxBytes { get; init; }
    public long NetworkTxBytes { get; init; }
    public int NumProcesses { get; init; }

    public double MemoryUsagePercent => MemoryLimitBytes > 0 ? (double)MemoryUsageBytes / MemoryLimitBytes * 100.0 : 0.0;

    /// A copy with a different id. Used to re-key a machine's backing-container stats onto
    /// the stable machine id, so history survives the backing container id changing across
    /// distro restarts.
    public ContainerStats With(string newId) => this with { Id = newId };
}

/// Builder = the WSL container equivalent of Orchard's BuildKit-backed builder: a persistent
/// helper container that runs <c>wslc build</c> / <c>wslc buildx</c> jobs.
public sealed record Builder
{
    public required string Status { get; init; }
    public required BuilderConfiguration Configuration { get; init; }
}

public sealed record BuilderConfiguration
{
    public required string Id { get; init; }
    public required ImageReference Image { get; init; }
    public Dictionary<string, string> Labels { get; init; } = [];
}
