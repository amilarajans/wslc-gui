using CommunityToolkit.Mvvm.ComponentModel;

namespace OrchardWin.Core.Services;

/// A single alert to present to the user.
public sealed record AppAlert(string Message, DateTimeOffset Date)
{
    public Guid Id { get; } = Guid.NewGuid();
}

/// Where an error came from, which decides whether it's allowed to interrupt the user.
public enum AlertSource
{
    /// A user-initiated action (button press, explicit refresh). May present a modal.
    User,
    /// A background poll / auto-refresh. Never presents a modal and never dismisses one.
    Background,
}

/// Owns the app's current user-facing alert. Errors from *user* actions are presented as a
/// dialog/InfoBar; errors from *background* polls are logged only - otherwise the 1-5s
/// refresh timers would storm dialogs and dismiss ones the user is mid-read. Ported 1:1 from
/// Orchard's `AlertCenter`.
public sealed partial class AlertCenter : ObservableObject
{
    [ObservableProperty]
    private AppAlert? _current;

    public void Error(string message, AlertSource source = AlertSource.User)
    {
        if (source != AlertSource.User)
        {
            Log.Ui.Debug($"suppressed background alert: {message}");
            return;
        }
        Current = new AppAlert(message, DateTimeOffset.Now);
    }

    public void Error(OrchardWinException error, AlertSource source = AlertSource.User) =>
        Error(error.Message, source);

    public void Dismiss() => Current = null;
}
