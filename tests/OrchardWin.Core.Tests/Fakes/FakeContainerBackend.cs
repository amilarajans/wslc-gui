using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Tests.Fakes;

/// In-memory <see cref="IContainerBackend"/> for unit tests — no wslc required.
public sealed class FakeContainerBackend : IContainerBackend
{
    public List<ContainerImage> Images { get; set; } = [];
    public List<Container> Containers { get; set; } = [];
    public List<ContainerNetwork> Networks { get; set; } = [];
    public int ListImagesCallCount { get; private set; }
    public int ListContainersCallCount { get; private set; }

    public Task<IReadOnlyList<Container>> ListContainersAsync(CancellationToken ct = default)
    {
        ListContainersCallCount++;
        return Task.FromResult<IReadOnlyList<Container>>(Containers.ToList());
    }

    public Task StopContainerAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task KillContainerAsync(string id, int signal, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteContainerAsync(string id, bool force, CancellationToken ct = default) => Task.CompletedTask;
    public Task BootstrapAndStartAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Stream> ContainerLogsAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new MemoryStream());

    public Task<ContainerStats> StatsAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(new ContainerStats { Id = id });

    public Task CreateContainerAsync(ContainerCreateSpec spec, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ContainerImage>> ListImagesAsync(CancellationToken ct = default)
    {
        ListImagesCallCount++;
        return Task.FromResult<IReadOnlyList<ContainerImage>>(Images.ToList());
    }

    public Task PullImageAsync(string reference, IProgress<ImagePullProgress>? progress = null, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteImageAsync(string reference, CancellationToken ct = default)
    {
        Images.RemoveAll(i => i.Reference == reference);
        return Task.CompletedTask;
    }

    public Task<ImageInspection> InspectImageAsync(string reference, CancellationToken ct = default) =>
        Task.FromResult(new ImageInspection
        {
            Name = reference,
            Digest = "sha256:test",
            MediaType = "application/vnd.oci.image.config.v1+json",
            Size = 1,
        });

    public Task<IReadOnlyList<ContainerNetwork>> ListNetworksAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ContainerNetwork>>(Networks.ToList());

    public Task CreateNetworkAsync(string name, string? subnet, Dictionary<string, string> labels, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task DeleteNetworkAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public Task<SystemHealthInfo> PingAsync(CancellationToken ct = default) =>
        Task.FromResult(new SystemHealthInfo("test"));

    public Task<SystemDiskUsage> DiskUsageAsync(CancellationToken ct = default) =>
        Task.FromResult(new SystemDiskUsage
        {
            Containers = new DiskUsageSection(),
            Images = new DiskUsageSection { Total = Images.Count, SizeInBytes = Images.Sum(i => i.Descriptor.Size) },
            Volumes = new DiskUsageSection(),
        });

    public static ContainerImage MakeImage(string reference, long size = 1000) => new()
    {
        Reference = reference,
        Descriptor = new ContainerImageDescriptor
        {
            Digest = $"sha256:{reference.GetHashCode():x8}",
            MediaType = "application/vnd.oci.image.index.v1+json",
            Size = size,
        },
    };

    public static Container MakeContainer(string id, string name, string status = "exited", string image = "alpine:latest") =>
        new()
        {
            Status = status,
            Configuration = new ContainerConfiguration
            {
                Id = id,
                Hostname = name,
                InitProcess = new InitProcess { Executable = "/bin/sh" },
                Platform = new Platform { Architecture = "amd64" },
                Image = new ImageReference
                {
                    Reference = image,
                    Descriptor = new ImageDescriptor
                    {
                        Digest = "sha256:x",
                        MediaType = "application/vnd.oci.image.index.v1+json",
                        Size = 0,
                    },
                },
                Resources = new Resources(),
            },
        };
}
