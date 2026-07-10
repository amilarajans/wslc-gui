namespace OrchardWin.App;

/// Segoe Fluent / MDL2 glyph codes that map to Orchard's SF Symbols
/// (`TabSelection.icon` and list-row icons in the macOS reference).
///
/// Orchard uses SF Symbols (cube, sparkles, …); Windows has no SF Symbol font, so we pick the
/// closest Fluent glyph that carries the same meaning.
public static class AppIcons
{
    // Sidebar — from Orchard TabSelection.icon
    /// SF: gauge.with.dots.needle.bottom.50percent
    public const string Dashboard = "\uE9D9";
    /// SF: cube
    public const string Containers = "\uE7B8";
    /// SF: cpu
    public const string Machines = "\uE950";
    /// SF: shield.lefthalf.filled
    public const string Sandboxes = "\uE72E";
    /// SF: sparkles
    public const string Models = "\uE113";
    /// SF: cube.transparent
    public const string Images = "\uE81E";
    /// SF: network
    public const string Dns = "\uE968";
    /// SF: arrow.down.left.arrow.up.right
    public const string Networks = "\uE8CB";
    /// SF: doc.text.below.ecg
    public const string Logs = "\uE7C3";
    /// SF: externaldrive
    public const string Mounts = "\uE8B7";
    /// SF: server.rack (Registries — when added)
    public const string Registries = "\uF246";
    /// SF: gear
    public const string Settings = "\uE713";

    // List / actions
    /// SF: cube (container row)
    public const string ContainerCube = "\uE7B8";
    /// SF: cube.transparent (empty state)
    public const string ContainerEmpty = "\uE81E";
    /// SF: plus
    public const string Plus = "\uE710";
    /// SF: shield.lefthalf.filled (sandbox badge)
    public const string SandboxBadge = "\uE72E";
}
