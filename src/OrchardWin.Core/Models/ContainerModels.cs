using System.Text.Json.Serialization;

namespace OrchardWin.Core.Models;

public enum ContainerSortOption
{
    Name,
    Status,
    Image,
}

public static class ContainerSortOptionExtensions
{
    public static string Label(this ContainerSortOption option) => option switch
    {
        ContainerSortOption.Name => "Name",
        ContainerSortOption.Status => "Status",
        ContainerSortOption.Image => "Image",
        _ => option.ToString(),
    };
}

public enum ImageSortOption
{
    Name,
    Tag,
    Size,
}

public static class ImageSortOptionExtensions
{
    public static string Label(this ImageSortOption option) => option switch
    {
        ImageSortOption.Name => "Name",
        ImageSortOption.Tag => "Tag",
        ImageSortOption.Size => "Size",
        _ => option.ToString(),
    };
}

/// <summary>
/// A running or stopped WSL container, as reported by <c>wslc container inspect</c>.
/// Mirrors Orchard's Apple-container-backed <c>Container</c> model field-for-field so the
/// rest of the app (stats, run config, list views) needed no reshaping.
/// </summary>
public sealed record Container
{
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("configuration")]
    public required ContainerConfiguration Configuration { get; init; }

    [JsonPropertyName("networks")]
    public List<ContainerNetworkAttachment> Networks { get; init; } = [];
}

public sealed record ContainerConfiguration
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; init; }

    [JsonPropertyName("runtimeHandler")]
    public string RuntimeHandler { get; init; } = "wslc";

    [JsonPropertyName("initProcess")]
    public required InitProcess InitProcess { get; init; }

    [JsonPropertyName("mounts")]
    public List<Mount> Mounts { get; init; } = [];

    [JsonPropertyName("platform")]
    public required Platform Platform { get; init; }

    [JsonPropertyName("image")]
    public required ImageReference Image { get; init; }

    [JsonPropertyName("dns")]
    public DnsConfig? Dns { get; init; }

    [JsonPropertyName("resources")]
    public required Resources Resources { get; init; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string> Labels { get; init; } = [];

    [JsonPropertyName("publishedPorts")]
    public List<PublishedPort> PublishedPorts { get; init; } = [];

    [JsonPropertyName("sysctls")]
    public Dictionary<string, string> Sysctls { get; init; } = [];
}

public sealed record Mount
{
    [JsonPropertyName("type")]
    public required string Type { get; init; } // "bind" | "volume" (WSL container mounts are virtiofs-backed binds)

    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("destination")]
    public required string Destination { get; init; }

    [JsonPropertyName("options")]
    public List<string> Options { get; init; } = [];
}

public sealed record InitProcess
{
    [JsonPropertyName("terminal")]
    public bool Terminal { get; init; }

    [JsonPropertyName("environment")]
    public List<string> Environment { get; init; } = [];

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; init; } = "/";

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; init; } = [];

    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    [JsonPropertyName("user")]
    public string? User { get; init; }
}

public sealed record Platform
{
    [JsonPropertyName("os")]
    public string Os { get; init; } = "linux";

    [JsonPropertyName("architecture")]
    public required string Architecture { get; init; }

    [JsonPropertyName("variant")]
    public string? Variant { get; init; }
}

public sealed record ImageReference
{
    [JsonPropertyName("descriptor")]
    public required ImageDescriptor Descriptor { get; init; }

    [JsonPropertyName("reference")]
    public required string Reference { get; init; }
}

public sealed record ImageDescriptor
{
    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    [JsonPropertyName("digest")]
    public required string Digest { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }
}

public sealed record DnsConfig
{
    [JsonPropertyName("nameservers")]
    public List<string> Nameservers { get; init; } = [];

    [JsonPropertyName("searchDomains")]
    public List<string> SearchDomains { get; init; } = [];

    [JsonPropertyName("options")]
    public List<string> Options { get; init; } = [];

    [JsonPropertyName("domain")]
    public string? Domain { get; init; }
}

public sealed record Resources
{
    [JsonPropertyName("cpus")]
    public int Cpus { get; init; }

    [JsonPropertyName("memoryInBytes")]
    public long MemoryInBytes { get; init; }
}

public sealed record PublishedPort
{
    [JsonPropertyName("hostPort")]
    public int HostPort { get; init; }

    [JsonPropertyName("containerPort")]
    public int ContainerPort { get; init; }

    [JsonPropertyName("proto")]
    public string TransportProtocol { get; init; } = "tcp";

    [JsonPropertyName("hostAddress")]
    public string? HostAddress { get; init; }

    /// Stable identity for list rendering: containerPort alone repeats when the same port
    /// is published over more than one transport.
    public string UniqueId => $"{HostAddress}|{HostPort}|{ContainerPort}|{TransportProtocol}";
}

public sealed record ContainerNetworkAttachment
{
    [JsonPropertyName("ipv4Gateway")]
    public string Gateway { get; init; } = "";

    [JsonPropertyName("hostname")]
    public string Hostname { get; init; } = "";

    [JsonPropertyName("network")]
    public string Network { get; init; } = "";

    [JsonPropertyName("ipv4Address")]
    public string Address { get; init; } = "";
}

// MARK: - Top-level image list entity (distinct shape/identity from the embedded ImageReference)

public sealed record ContainerImage
{
    [JsonPropertyName("descriptor")]
    public required ContainerImageDescriptor Descriptor { get; init; }

    [JsonPropertyName("reference")]
    public required string Reference { get; init; }

    public string Id => Reference;
}

public sealed record ContainerImageDescriptor
{
    [JsonPropertyName("digest")]
    public required string Digest { get; init; }

    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("annotations")]
    public Dictionary<string, string>? Annotations { get; init; }
}

public sealed record ImageInspection
{
    public required string Name { get; init; }
    public required string Digest { get; init; }
    public required string MediaType { get; init; }
    public long Size { get; init; }
    public List<ImageInspectionVariant> Variants { get; init; } = [];
}

public sealed record ImageInspectionVariant
{
    public required string Platform { get; init; }
    public long Size { get; init; }
    public List<string>? Entrypoint { get; init; }
    public List<string>? Cmd { get; init; }
    public List<string>? Env { get; init; }
    public string? WorkingDir { get; init; }
    public string? User { get; init; }
    public List<string>? ExposedPorts { get; init; }
    public List<string>? Volumes { get; init; }
}

public sealed record ContainerMount
{
    public required Mount Mount { get; init; }
    public List<string> ContainerIds { get; init; } = [];

    public string Id => $"{Mount.Source}->{Mount.Destination}";
    public string MountType => Mount.Type;
    public string OptionsString => string.Join(", ", Mount.Options);
}

public sealed record ImagePullProgress
{
    public required string ImageName { get; init; }
    public PullStatus Status { get; set; } = PullStatus.Pulling;
    public double Progress { get; set; }
    public string Message { get; set; } = "";
}

public enum PullStatus { Pulling, Completed, Failed }

public sealed record RegistrySearchResult
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsOfficial { get; init; }
    public int? StarCount { get; init; }

    public string DisplayName
    {
        get
        {
            if (Name.StartsWith("docker.io/library/", StringComparison.Ordinal)) return Name["docker.io/library/".Length..];
            if (Name.StartsWith("docker.io/", StringComparison.Ordinal)) return Name["docker.io/".Length..];
            return Name;
        }
    }
}

public sealed record SystemProperty
{
    public required string Id { get; init; }
    public required PropertyType Type { get; init; }
    public required string Value { get; init; }
    public required string Description { get; init; }

    public string DisplayValue
    {
        get
        {
            if (Type == PropertyType.Bool) return Value == "true" ? "✓ Enabled" : "✗ Disabled";
            if (Value == "*undefined*") return "Not set";
            return Value;
        }
    }

    public bool IsUndefined => Value == "*undefined*";
}

public enum PropertyType { Bool, String }

public static class StringExtensions
{
    /// A network address with any CIDR suffix removed, e.g. "10.0.0.2/24" -> "10.0.0.2".
    public static string StrippingCidrSuffix(this string value)
    {
        var slash = value.IndexOf('/');
        return slash < 0 ? value : value[..slash];
    }
}
