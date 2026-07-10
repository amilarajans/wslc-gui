using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// The local-model discovery surface. Read-only: detect providers running on the host and
/// list the models they advertise, plus send a one-off chat completion for the in-app
/// tester. Ported from Orchard's `ModelBackend` protocol.
public interface IModelBackend
{
    /// Probe the host for running model providers and return those that responded.
    /// Best-effort: never throws, since a missing provider is a normal state.
    Task<IReadOnlyList<ModelProvider>> DetectProvidersAsync(CancellationToken ct = default);

    /// Send a chat conversation to a provider on the host (127.0.0.1:port) and return the
    /// assistant's reply. `messages` is the full history so the model has context.
    Task<string> CompleteAsync(ushort port, ModelApiStyle api, string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}

/// Launches and supervises a locally-run model server process. Orchard's `ModelServerEngine`
/// wraps Apple's `mlx_lm.server`; there is no Windows/MLX equivalent (MLX is Apple-Silicon
/// only), so this wraps `ollama serve` + `ollama run <model>` instead - see
/// ARCHITECTURE.md "Local AI & Sandboxes".
public interface IModelServerEngine
{
    string? LocateBinary();
    IServerProcess Launch(string model, string host, ushort port, string logPath);
}

public interface IServerProcess
{
    Action<int>? TerminationHandler { get; set; }
    void Terminate();
}
