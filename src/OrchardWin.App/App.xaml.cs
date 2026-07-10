using System.Drawing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
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
        // File logs + .NET mini-dumps before anything else can throw.
        var logDir = Log.Initialize();
        Log.Ui.Info($"App starting; logs → {logDir}");

        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            InitializeComponent();
            Log.Ui.Info("InitializeComponent OK");
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("App.InitializeComponent", ex);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Ui.Info("OnLaunched");
        try
        {
            Services = AppServices.ForLaunch();
            Log.Ui.Info("AppServices ready");

            MainWindow = new MainWindow(Services);
            MainWindow.Activate();
            Log.Ui.Info("MainWindow activated");

            CreateTrayIcon();

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
                Log.Ui.Info("MainWindow.Closed — disposing services");
                // Stop everything with a process lifetime of its own (managed model servers, the
                // stats sampling timer) so closing the window doesn't leak child processes -
                // mirrors Orchard's NSApplication.willTerminateNotification handlers.
                Services.Dispose();
                _trayIcon?.Dispose();
                _trayIcon = null;
            };
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("App.OnLaunched", ex);
            throw;
        }
    }

    private void CreateTrayIcon()
    {
        try
        {
            // IMPORTANT: H.NotifyIcon treats Visibility.Collapsed as "remove tray icon".
            // Keep Visible; zero size so parenting into the window tree costs no layout.
            _trayIcon = new TrayIcon(Services)
            {
                Visibility = Visibility.Visible,
                Width = 0,
                Height = 0,
                IsHitTestVisible = false,
                Opacity = 0,
            };

            // Prefer a real file path for unpackaged WinUI (ms-appx:// can fail to resolve).
            TryAssignTrayIconFile(_trayIcon);

            // Parent under the main grid so TrayPopup can inherit XamlRoot.
            if (MainWindow.Content is Panel rootPanel)
                rootPanel.Children.Add(_trayIcon);

            _trayIcon.ForceCreate(enablesEfficiencyMode: false);
            AttachTrayXamlRoot();

            if (MainWindow.Content is FrameworkElement content)
                content.Loaded += (_, _) => AttachTrayXamlRoot();

            Log.Ui.Info($"Tray icon created; IsCreated={_trayIcon.IsCreated}");
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("App.CreateTrayIcon", ex);
        }
    }

    private static void TryAssignTrayIconFile(TrayIcon tray)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
            if (!System.IO.File.Exists(path))
            {
                Log.Ui.Info($"Tray icon file missing at {path}; keeping XAML IconSource");
                return;
            }

            // BitmapImage for IconSource (WinUI path).
            tray.IconSource = new BitmapImage(new Uri(path, UriKind.Absolute));

            // Also set GDI Icon — most reliable for Shell_NotifyIcon.
            tray.Icon = new Icon(path);
            Log.Ui.Info($"Tray icon loaded from {path}");
        }
        catch (Exception ex)
        {
            Log.Ui.Error($"Tray icon file load failed: {ex.Message}");
        }
    }

    private void AttachTrayXamlRoot()
    {
        if (_trayIcon is null) return;
        try
        {
            var root = (MainWindow.Content as UIElement)?.XamlRoot;
            if (root is null)
            {
                Log.Ui.Info("Tray XamlRoot not ready yet");
                return;
            }
            _trayIcon.EnsureXamlRoot(root);
            // Ensure still visible after tree attach (some layouts collapse children).
            if (_trayIcon.Visibility != Visibility.Visible)
                _trayIcon.Visibility = Visibility.Visible;
            Log.Ui.Info($"Tray XamlRoot attached; IsCreated={_trayIcon.IsCreated}");
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("App.AttachTrayXamlRoot", ex);
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // WinUI surfaces many XAML/layout failures here before the process dies with 0xc000027b.
        Log.WriteCrashReport(
            "Application.UnhandledException",
            e.Exception,
            extra: $"Message={e.Message}; Handled will be set true to attempt soft recovery");
        // Mark handled so we keep a log trail and (when possible) avoid an immediate hard kill.
        // UI may be partially broken after this — user can still copy logs from %LOCALAPPDATA%.
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Log.WriteCrashReport(
            "AppDomain.UnhandledException",
            e.ExceptionObject as Exception,
            extra: $"IsTerminating={e.IsTerminating}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.WriteCrashReport("TaskScheduler.UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
