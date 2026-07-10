using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Services;
using OrchardWin.App.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace OrchardWin.App;

/// The main shell window: a NavigationView routing to one page per feature domain, mirroring
/// Orchard's `MainInterface`/`ThreeColumnLayout`/`Sidebar` split. Each page owns its own
/// ViewModel bound to the services on <see cref="AppServices"/>.
public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;

    private static readonly Dictionary<string, Type> Routes = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["containers"] = typeof(ContainersPage),
        ["images"] = typeof(ImagesPage),
        ["machines"] = typeof(MachinesPage),
        ["networks"] = typeof(NetworksPage),
        ["dns"] = typeof(DnsPage),
        ["models"] = typeof(ModelsPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage),
    };

    /// The currently shown route. Guards Navigate against duplicate calls: clicking a nav
    /// item raises SelectionChanged, and the constructor's SelectedItem assignment plus its
    /// explicit Navigate would otherwise construct the dashboard page twice at startup
    /// (each page instance spins up its own refresh timer, so duplicates aren't free).
    private string? _currentTag;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Title = "wslc-gui";

        ConfigureTitleBar();
        TrySetWindowSize(1280, 800);

        RootNav.SelectedItem = RootNav.MenuItems[0];
        Navigate("dashboard");
    }

    private void ConfigureTitleBar()
    {
        // Client area draws into the caption so AppTitleBar can host branding; system min/max/close remain.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow.TitleBar is { } titleBar)
            {
                titleBar.ExtendsContentIntoTitleBar = true;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }
        catch
        {
            // Title-bar chrome is cosmetic; fall back to default caption buttons if AppWindow is unavailable.
        }
    }

    private void TrySetWindowSize(int width, int height)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new SizeInt32(width, height));
        }
        catch
        {
            // Non-fatal.
        }
    }

    /// Selection alone drives navigation - clicking a menu item changes the selection, and
    /// the built-in Settings item surfaces as IsSettingsSelected. A separate ItemInvoked
    /// handler would fire *in addition to* this for every click, navigating twice.
    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { Navigate("settings"); return; }
        if (args.SelectedItemContainer?.Tag is string tag) Navigate(tag);
    }

    private void Navigate(string tag)
    {
        if (tag == _currentTag) return;
        if (!Routes.TryGetValue(tag, out var pageType)) return;
        _currentTag = tag;
        ContentFrame.Navigate(pageType, _services);
    }
}
