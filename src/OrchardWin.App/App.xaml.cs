using Microsoft.UI.Xaml;
using OrchardWin.Core.Services;

namespace OrchardWin.App;

/// Composition root + lifecycle entry point. Mirrors Orchard's `OrchardApp.swift`: build
/// `AppServices` once, hand it to the main window, activate.
public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    /// Exposed so pages/dialogs that need a window handle (FileOpenPicker's
    /// InitializeWithWindow) or need to bring the app to the foreground (TrayIcon's "Open")
    /// have one place to reach it, mirroring how `AppServices` is this app's one composition
    /// root for services.
    public static MainWindow MainWindow { get; private set; } = null!;

    private TrayIcon? _trayIcon;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = AppServices.ForLaunch();

        MainWindow = new MainWindow(Services);
        MainWindow.Activate();

        _trayIcon = new TrayIcon(Services);
        _trayIcon.ForceCreate();

        // Simplification vs. the Swift original: Orchard's menu-bar extra is an independent
        // NSStatusItem that outlives the main window closing, so quitting only happens
        // explicitly. Making the tray icon truly outlive MainWindow the same way needs
        // intercepting the window close with a cancelable AppWindow.Closing (Win32 interop,
        // not exposed on Window directly) to hide-instead-of-close - deliberately not done
        // here given it's unverifiable from macOS; closing the main window exits the whole
        // app, same as most Windows tray-icon apps' default behavior. TrayIcon's "Exit" item
        // and this handler both funnel through the same cleanup either way.
        MainWindow.Closed += (_, _) =>
        {
            // Stop everything with a process lifetime of its own (managed model servers, the
            // stats sampling timer) so closing the window doesn't leak child processes -
            // mirrors Orchard's NSApplication.willTerminateNotification handlers.
            Services.Dispose();
            _trayIcon?.Dispose();
        };
    }
}
