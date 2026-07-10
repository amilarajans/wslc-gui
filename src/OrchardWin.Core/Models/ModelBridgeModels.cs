namespace OrchardWin.Core.Models;

/// The wire API a model provider speaks. Decides which environment variables a container
/// needs so an in-container client reaches the host provider.
public enum ModelApiStyle { OpenAI, Ollama }

/// One turn in a chat conversation, in the shape the OpenAI/Ollama chat APIs expect.
public sealed record ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ChatRole Role { get; init; }
    public required string Content { get; set; }
}

public enum ChatRole { User, Assistant }

/// A local inference provider discovered running on the host - Ollama, LM Studio, and so
/// on. There is no Windows equivalent of Apple's MLX (Apple Silicon-only ML framework), so
/// unlike Orchard, this app does not offer to *launch* its own inference server - only
/// detect and bridge providers the user already has running. See ARCHITECTURE.md.
public sealed record ModelProvider
{
    public required ModelProviderKind Kind { get; init; }
    /// The loopback port the provider listens on, as seen from the host.
    public required ushort Port { get; init; }
    public required ModelApiStyle Api { get; init; }
    /// Model identifiers the provider advertises, when it exposes a listing endpoint.
    public List<string> Models { get; init; } = [];

    /// Stable across refreshes: a provider is identified by its kind and port.
    public string Id => $"{Kind}:{Port}";

    /// The base URL reachable *from the host* (e.g. http://127.0.0.1:11434).
    public string HostBaseUrl => $"http://127.0.0.1:{Port}";
}

public enum ModelProviderKind { Ollama, LmStudio, Custom }

public static class ModelProviderKindExtensions
{
    public static string DisplayName(this ModelProviderKind kind) => kind switch
    {
        ModelProviderKind.Ollama => "Ollama",
        ModelProviderKind.LmStudio => "LM Studio",
        ModelProviderKind.Custom => "Custom",
        _ => kind.ToString(),
    };
}

/// A model server Orchard-Win started and supervises (as opposed to a
/// <see cref="ModelProvider"/>, which is any server merely detected running). On Windows
/// this wraps `ollama serve` / `ollama run &lt;model&gt;` rather than Apple's mlx_lm.server,
/// since MLX has no Windows build.
public sealed record ManagedModelServer
{
    /// Stable identity: one server per model+port. Also the log-file key.
    public required string Id { get; init; }
    public required string Model { get; init; }
    public required string Host { get; init; }
    public required ushort Port { get; init; }
    public ManagedModelServerStatus Status { get; set; } = ManagedModelServerStatus.Running;
    public required string LogPath { get; init; }

    public ModelApiStyle Api => ModelApiStyle.Ollama;
    public bool ReachableFromContainers => Host == "0.0.0.0";
}

public enum ManagedModelServerStatus { Running, Failed }

/// Computes how a container reaches a model server running on the host, and the
/// environment a container needs to talk to it. Pure and dependency-free so it unit-tests
/// in isolation - ported verbatim from Orchard's ModelBridge.
///
/// Load-bearing fact carried over from the macOS implementation, to be re-verified against
/// wslc's networking model once compiled on Windows: a container's default route is its
/// network's gateway, and the host is reachable at that gateway address *if* the server
/// binds all interfaces (0.0.0.0). A loopback-only (127.0.0.1) server is not reachable from
/// inside a container. WSL's "mirrored" networking mode may change this - see
/// ARCHITECTURE.md "Networking assumptions to verify on Windows".
public static class ModelBridge
{
    public static string ContainerBaseUrl(string gateway, ushort hostPort, ModelApiStyle api)
    {
        var root = $"http://{gateway}:{hostPort}";
        return api switch
        {
            ModelApiStyle.OpenAI => root + "/v1",
            ModelApiStyle.Ollama => root,
            _ => root,
        };
    }

    public static List<(string Key, string Value)> InjectionEnvironment(string baseUrl, ModelApiStyle api) => api switch
    {
        ModelApiStyle.OpenAI =>
        [
            ("OPENAI_BASE_URL", baseUrl),
            ("OPENAI_API_KEY", "not-needed"),
        ],
        ModelApiStyle.Ollama => [("OLLAMA_HOST", baseUrl)],
        _ => [],
    };
}
