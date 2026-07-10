using OrchardWin.Core.Services;

namespace OrchardWin.App;

/// Parameter passed through <see cref="Microsoft.UI.Xaml.Controls.Frame.Navigate"/>.
/// Always carries <see cref="AppServices"/>; optional fields request post-navigation selection.
public sealed class NavigationArgs
{
    public required AppServices Services { get; init; }

    /// When opening Containers, select this container id (full id from the daemon).
    public string? SelectContainerId { get; init; }

    /// When opening Machines, select this machine id.
    public string? SelectMachineId { get; init; }

    public static NavigationArgs From(object? parameter)
    {
        return parameter switch
        {
            NavigationArgs args => args,
            AppServices services => new NavigationArgs { Services = services },
            _ => throw new ArgumentException(
                "Frame navigation expected NavigationArgs or AppServices.", nameof(parameter)),
        };
    }

    public static implicit operator NavigationArgs(AppServices services) =>
        new() { Services = services };
}
