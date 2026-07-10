using System.Text.RegularExpressions;

namespace OrchardWin.Core.Services;

/// Pure input validators used by the Add/Run/Create forms. Kept out of the views so they can
/// be unit-tested and reused. Ported from Orchard's `InputValidation.swift` (domain/network/
/// subnet validators) plus container/machine-name and port validators the Swift original left
/// to `ContainerConfigForm` — Orchard-Win's Wave-2 create forms need them as pure statics
/// instead, so they're consolidated here.
public static class InputValidation
{
    /// Dot-separated labels, each 1-63 chars, alphanumeric with internal dashes (RFC 1035).
    private static readonly Regex DomainRegex = new(
        "^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$",
        RegexOptions.Compiled);

    /// A single label, 1-63 chars, alphanumeric with internal dashes.
    private static readonly Regex SingleLabelRegex = new(
        "^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?$",
        RegexOptions.Compiled);

    private const string Octet = "(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)";

    /// A CIDR subnet: dotted-quad IPv4 (each octet 0-255) followed by a /0-/32 prefix length.
    private static readonly Regex SubnetRegex = new(
        $"^({Octet}\\.){{3}}{Octet}/([0-9]|[1-2][0-9]|3[0-2])$",
        RegexOptions.Compiled);

    /// A DNS domain used for the hosts-file-backed DNS domains feature, e.g. "test.local".
    public static bool IsValidDomainName(string domain) => DomainRegex.IsMatch(domain);

    /// A `wslc network create` name: a single label, 1-63 chars, alphanumeric with internal
    /// dashes.
    public static bool IsValidNetworkName(string name) => SingleLabelRegex.IsMatch(name);

    /// A CIDR subnet passed to `wslc network create --subnet`.
    public static bool IsValidSubnet(string subnet) => SubnetRegex.IsMatch(subnet);

    /// A container name. wslc container names, like Docker's, double as DNS hostnames on the
    /// container network, so they share the network-name shape (single DNS label).
    public static bool IsValidContainerName(string name) => SingleLabelRegex.IsMatch(name);

    /// A machine name (the WSL distro registration name backing it). Real WSL distro names
    /// accept a broader charset than a DNS label, but constraining to the DNS-safe subset
    /// keeps a machine name always usable as a hostname too, mirroring how Orchard's own
    /// container-machine naming reuses container-name rules.
    /// VERIFY: confirm `wsl --list`/registration's actual accepted charset once on Windows;
    /// this is deliberately more restrictive than WSL technically requires.
    public static bool IsValidMachineName(string name) => SingleLabelRegex.IsMatch(name);

    /// A TCP/UDP port number in the valid 1-65535 range (0 is reserved/"any").
    public static bool IsValidPortNumber(int port) => port is > 0 and <= 65535;

    /// String-typed overload for form fields bound to raw text input.
    public static bool IsValidPortNumber(string port) =>
        int.TryParse(port, out var value) && IsValidPortNumber(value);

    /// A non-empty string within an inclusive length bound, trimming surrounding whitespace
    /// before measuring - the common "name" field guard shared by every create form.
    public static bool IsNonEmptyWithinLength(string value, int maxLength) =>
        value.Trim() is { Length: > 0 } trimmed && trimmed.Length <= maxLength;
}
