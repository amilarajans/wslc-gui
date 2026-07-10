namespace OrchardWin.Core;

/// Typed errors surfaced to the user. User-facing copy lives in <see cref="Message"/>.
/// Ported from Orchard's `OrchardError` enum; cases that were XPC/machine-API-specific are
/// reworded for the wslc.exe/wsl.exe CLI boundary this app talks to instead.
public sealed class OrchardWinException : Exception
{
    public OrchardWinExceptionKind Kind { get; }

    private OrchardWinException(OrchardWinExceptionKind kind, string message) : base(message)
    {
        Kind = kind;
    }

    public static OrchardWinException BinaryNotFound(IReadOnlyList<string> searched) =>
        new(OrchardWinExceptionKind.BinaryNotFound,
            $"The wslc/wsl binary could not be found. Searched: {string.Join(", ", searched)}.");

    public static OrchardWinException CliFailed(string command, int exitCode, string? stderr)
    {
        var detail = stderr?.Trim();
        var message = !string.IsNullOrEmpty(detail)
            ? $"{command} failed: {detail}"
            : $"{command} failed (exit {exitCode}).";
        return new OrchardWinException(OrchardWinExceptionKind.CliFailed, message);
    }

    public static OrchardWinException DecodeFailed(string what) =>
        new(OrchardWinExceptionKind.DecodeFailed,
            $"Could not read {what}: the WSL container service returned an unexpected response.");

    public static OrchardWinException ServiceUnavailable() =>
        new(OrchardWinExceptionKind.ServiceUnavailable,
            "The WSL container service is unavailable. Make sure WSL is running (`wsl --status`) and the container preview is installed.");

    public static OrchardWinException ContainerNotFound(string id) =>
        new(OrchardWinExceptionKind.ContainerNotFound, $"Container {id} was not found.");

    public static OrchardWinException ContainerInTransition(string id) =>
        new(OrchardWinExceptionKind.ContainerInTransition, $"Container {id} is changing state. Try again in a moment.");

    public static OrchardWinException SearchFailed() =>
        new(OrchardWinExceptionKind.SearchFailed, "Image search failed. Check your connection and try again.");

    public static OrchardWinException NoEntrypoint() =>
        new(OrchardWinExceptionKind.NoEntrypoint, "No entrypoint or command specified for the container.");

    public static OrchardWinException MachineApiUnavailable() =>
        new(OrchardWinExceptionKind.MachineApiUnavailable,
            "Container machines are unavailable. Update WSL (`wsl --update --pre-release`) to use the container preview.");

    public static OrchardWinException Generic(string message) =>
        new(OrchardWinExceptionKind.Generic, message);

    /// Classify a raw error thrown while starting/bootstrapping a container. The runtime
    /// reports these as opaque messages, so this is where the message-string matching is
    /// pinned - one place, easy to update once real wslc error text is known.
    public static OrchardWinException ClassifyStartError(Exception error, string id)
    {
        var message = error.Message;
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return ContainerNotFound(id);
        if (message.Contains("shutting down", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid state", StringComparison.OrdinalIgnoreCase)
            || message.Contains("expected to be in created state", StringComparison.OrdinalIgnoreCase))
            return ContainerInTransition(id);
        return Generic(message);
    }

    /// Whether a CLI stderr indicates the resource already exists - treated as an
    /// idempotent success.
    public static bool IsAlreadyExistsError(string stderr) =>
        stderr.Contains("item with the same name already exists", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("File exists", StringComparison.Ordinal);
}

public enum OrchardWinExceptionKind
{
    BinaryNotFound,
    CliFailed,
    DecodeFailed,
    ServiceUnavailable,
    ContainerNotFound,
    ContainerInTransition,
    SearchFailed,
    NoEntrypoint,
    MachineApiUnavailable,
    Generic,
}
