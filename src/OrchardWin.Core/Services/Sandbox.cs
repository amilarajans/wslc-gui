using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services;

/// How this app recognises a "sandbox": a workload wired to a local model. Two signals — an
/// explicit label stamped on containers this app runs against a model, and an env-var
/// heuristic that also catches sandboxes wired up elsewhere. Pure and dependency-free so
/// detection unit-tests in isolation. Ported 1:1 from Orchard's `SandboxMarker`.
public static class SandboxMarker
{
    /// Label stamped on containers this app runs against a model.
    public const string SandboxLabelKey = "dev.orchardwin.sandbox";
    /// Label recording the model endpoint the container was wired to.
    public const string EndpointLabelKey = "dev.orchardwin.model.endpoint";

    /// Env vars whose presence implies the workload targets a model endpoint.
    public static readonly string[] EndpointEnvKeys = ["OPENAI_BASE_URL", "OLLAMA_HOST", "ANTHROPIC_BASE_URL"];

    public static bool HasSandboxLabel(IReadOnlyDictionary<string, string> labels) =>
        labels.TryGetValue(SandboxLabelKey, out var value) && value == "true";

    /// The model endpoint a workload targets - from its label first, else its env. Null when
    /// there is no signal at all.
    public static string? ModelEndpoint(IReadOnlyDictionary<string, string> labels, IReadOnlyList<string> environment)
    {
        if (labels.TryGetValue(EndpointLabelKey, out var fromLabel) && !string.IsNullOrEmpty(fromLabel))
            return fromLabel;

        foreach (var entry in environment)
        {
            var eq = entry.IndexOf('=');
            if (eq < 0) continue;
            var key = entry[..eq];
            if (!EndpointEnvKeys.Contains(key)) continue;
            var value = entry[(eq + 1)..];
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return null;
    }

    /// The labels to stamp when this app runs a sandbox wired to `endpoint`.
    public static Dictionary<string, string> Labels(string endpoint) => new()
    {
        [SandboxLabelKey] = "true",
        [EndpointLabelKey] = endpoint,
    };

    /// The chat API style this app can actually drive for this workload, or null when its
    /// endpoint speaks an API the tester doesn't (e.g. Anthropic - a recognised sandbox
    /// signal but not an OpenAI/Ollama chat shape). A label-stamped endpoint is always
    /// OpenAI-style, since that's the only kind this app stamps.
    public static ModelApiStyle? ChatApiStyle(IReadOnlyDictionary<string, string> labels, IReadOnlyList<string> environment)
    {
        if (labels.ContainsKey(EndpointLabelKey)) return ModelApiStyle.OpenAI;
        foreach (var entry in environment)
        {
            var eq = entry.IndexOf('=');
            if (eq < 0) continue;
            switch (entry[..eq])
            {
                case "OPENAI_BASE_URL": return ModelApiStyle.OpenAI;
                case "OLLAMA_HOST": return ModelApiStyle.Ollama;
            }
        }
        return null;
    }
}

public enum SandboxKind { Container, Machine }
public enum SandboxSource { Managed, Detected }

/// A workload recognised as a sandbox - a derived view over a container (or, later, a
/// machine), not a new backend resource. Ported from Orchard's `Sandbox`.
public sealed record Sandbox
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SandboxKind Kind { get; init; }
    /// How we know it's a sandbox: an explicit label, or only the env-var heuristic.
    public required SandboxSource Source { get; init; }
    public string? ModelEndpoint { get; init; }
    /// The chat API the tester can drive, or null when the endpoint isn't OpenAI/Ollama.
    public ModelApiStyle? ChatApi { get; init; }
    public bool IsRunning { get; init; }
    /// True when the workload is on a host-only (no-egress) network.
    public bool IsIsolated { get; init; }

    /// Build a sandbox from a container if it shows any sandbox signal, else null.
    /// `hostOnlyNetworks` is the set of network names with no internet egress.
    public static Sandbox? From(Container container, IReadOnlySet<string> hostOnlyNetworks)
    {
        var labels = container.Configuration.Labels;
        var environment = container.Configuration.InitProcess.Environment;
        var endpoint = SandboxMarker.ModelEndpoint(labels, environment);
        var hasLabel = SandboxMarker.HasSandboxLabel(labels);
        if (!hasLabel && endpoint is null) return null;

        var networkName = container.Networks.Count > 0 ? container.Networks[0].Network : "";
        return new Sandbox
        {
            Id = container.Configuration.Id,
            Name = container.Configuration.Id,
            Kind = SandboxKind.Container,
            Source = hasLabel ? SandboxSource.Managed : SandboxSource.Detected,
            ModelEndpoint = endpoint,
            ChatApi = SandboxMarker.ChatApiStyle(labels, environment),
            IsRunning = container.Status.Equals("running", StringComparison.OrdinalIgnoreCase),
            IsIsolated = hostOnlyNetworks.Contains(networkName),
        };
    }
}

public static class SandboxDetection
{
    /// Derive the current sandboxes from the container list, enriched with network
    /// isolation. Shared by the Sandboxes view and any sidebar count so they never disagree.
    public static List<Sandbox> DetectSandboxes(IReadOnlyList<Container> containers, IReadOnlyList<ContainerNetwork> networks)
    {
        var hostOnly = new HashSet<string>(networks.Where(n => n.IsHostOnly).Select(n => n.Id));
        return containers.Select(c => Sandbox.From(c, hostOnly)).Where(s => s is not null).Select(s => s!).ToList();
    }

    /// The model endpoint this container is wired to, if any.
    public static string? SandboxEndpoint(this Container container) =>
        SandboxMarker.ModelEndpoint(container.Configuration.Labels, container.Configuration.InitProcess.Environment);

    /// Whether this container is a sandbox - explicitly marked or carrying a model-endpoint
    /// env var. Lets the container views flag it, since a sandbox shows in both the
    /// Containers and Sandboxes lists.
    public static bool IsSandbox(this Container container) =>
        SandboxMarker.HasSandboxLabel(container.Configuration.Labels) || container.SandboxEndpoint() is not null;
}
