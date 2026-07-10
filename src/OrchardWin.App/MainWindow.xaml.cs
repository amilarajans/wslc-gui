using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.App.Views;
using OrchardWin.Core.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace OrchardWin.App;

/// The main shell window: a NavigationView routing to one page per feature domain, mirroring
/// Orchard's grouped sidebar (Dashboard / Compute / Resources / Networking / Observability).
public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _badgeTimer;

    private static readonly Dictionary<string, Type> Routes = new()
    {
        ["dashboard"] = typeof(DashboardPage),
        ["containers"] = typeof(ContainersPage),
        ["images"] = typeof(ImagesPage),
        ["machines"] = typeof(MachinesPage),
        ["networks"] = typeof(NetworksPage),
        ["dns"] = typeof(DnsPage),
        ["models"] = typeof(ModelsPage),
        ["sandboxes"] = typeof(SandboxesPage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage),
    };

    /// The currently shown route. Guards Navigate against duplicate calls.
    private string? _currentTag;

    /// Suppress SelectionChanged while we programmatically select a nav item from NavigateTo.
    private bool _suppressNavSelection;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Title = "wslc-gui";

        ConfigureTitleBar();
        TrySetWindowSize(1360, 860);

        // Keep Orchard-style count badges fresh without a tight poll.
        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _badgeTimer.Tick += (_, _) => RefreshNavBadges();
        _badgeTimer.Start();
        Closed += (_, _) => _badgeTimer.Stop();

        _services.ContainerListService.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshNavBadges);
        _services.ImageService.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshNavBadges);
        _services.NetworkService.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshNavBadges);
        _services.MachineService.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(RefreshNavBadges);

        RootNav.SelectedItem = NavDashboard;
        Navigate("dashboard");
        RefreshNavBadges();

        // Warm lists used by badges so counts appear quickly.
        _ = _services.ContainerListService.LoadAsync(showLoading: false);
        _ = _services.ImageService.LoadAsync(showLoading: false);
        _ = _services.NetworkService.LoadAsync(showLoading: false);
        _ = _services.MachineService.LoadAsync();
    }

    private void ConfigureTitleBar()
    {
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
            // Cosmetic only.
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

    private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNavSelection) return;
        if (args.IsSettingsSelected) { Navigate("settings"); return; }
        if (args.SelectedItemContainer?.Tag is string tag) Navigate(tag);
    }

    /// Navigate to a route, optionally selecting a container after the Containers page loads.
    /// Used by Dashboard row clicks (Orchard's NavigateToContainer).
    public void NavigateTo(string tag, string? selectContainerId = null, string? selectMachineId = null)
    {
        // Already on Containers: select without tearing down the page.
        if (tag == _currentTag && tag == "containers" && selectContainerId is not null
            && ContentFrame.Content is ContainersPage containersPage)
        {
            SelectNavItem(tag);
            containersPage.SelectContainer(selectContainerId);
            return;
        }

        if (tag == _currentTag && selectContainerId is null && selectMachineId is null)
            return;

        if (!Routes.TryGetValue(tag, out var pageType)) return;

        _currentTag = tag;
        SelectNavItem(tag);
        ContentFrame.Navigate(pageType, new NavigationArgs
        {
            Services = _services,
            SelectContainerId = selectContainerId,
            SelectMachineId = selectMachineId,
        });
    }

    private void Navigate(string tag)
    {
        if (tag == _currentTag) return;
        if (!Routes.TryGetValue(tag, out var pageType)) return;
        _currentTag = tag;
        ContentFrame.Navigate(pageType, new NavigationArgs { Services = _services });
    }

    private void SelectNavItem(string tag)
    {
        _suppressNavSelection = true;
        try
        {
            foreach (var item in EnumerateNavItems(RootNav.MenuItems))
            {
                if (item.Tag is string t && t == tag)
                {
                    RootNav.SelectedItem = item;
                    return;
                }
            }

            if (tag == "settings")
                RootNav.SelectedItem = RootNav.SettingsItem;
        }
        finally
        {
            _suppressNavSelection = false;
        }
    }

    private static IEnumerable<NavigationViewItem> EnumerateNavItems(IList<object> items)
    {
        foreach (var obj in items)
        {
            if (obj is NavigationViewItem nvi)
            {
                yield return nvi;
                if (nvi.MenuItems.Count > 0)
                {
                    foreach (var child in EnumerateNavItems(nvi.MenuItems))
                        yield return child;
                }
            }
        }
    }

    private void RefreshNavBadges()
    {
        SetBadge(ContainersBadge, _services.ContainerListService.Containers.Count);
        SetBadge(ImagesBadge, _services.ImageService.Images.Count);
        SetBadge(NetworksBadge, _services.NetworkService.Networks.Count);
        SetBadge(MachinesBadge, _services.MachineService.Machines.Count);
        SetBadge(ModelsBadge, _services.ModelService.Providers.Count);
    }

    private static void SetBadge(InfoBadge badge, int count)
    {
        if (count <= 0)
        {
            badge.Visibility = Visibility.Collapsed;
            badge.Value = 0;
            return;
        }

        badge.Value = count;
        badge.Visibility = Visibility.Visible;
    }
}
