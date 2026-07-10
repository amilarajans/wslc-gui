using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App;

/// System tray presence for Orchard-Win, replacing Orchard's macOS menu-bar extra. See the
/// XAML file's remarks for the H.NotifyIcon.WinUI surface this was written against
/// (unverified from macOS - grep this pair of files for `VERIFY` once building on Windows).
///
/// Simplified from the Swift original: no custom-drawn CPU/memory donut rings (a compact
/// tooltip summary + per-container quick-actions instead), and no live per-container
/// mini-panel popover - the context menu is the whole surface here.
public sealed partial class TrayIcon : TaskbarIcon
{
    private readonly AppServices _services;

    public IRelayCommand OpenCommand { get; }

    public TrayIcon(AppServices services)
    {
        _services = services;
        OpenCommand = new RelayCommand(BringMainWindowToForeground);

        InitializeComponent();

        // Marshalled: StatsService raises from its thread-pool sampling timer, and the other
        // services can raise from background continuations - all three handlers below touch
        // DependencyProperties (flyout items, ToolTipText), which are UI-thread-only in WinUI.
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ContainerListService.Containers))
                DispatcherQueue.RunOnUi(RebuildContainerItems);
        };
        _services.StatsService.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(UpdateTooltip);
        _services.SystemService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemService.SystemStatus))
                DispatcherQueue.RunOnUi(UpdateSystemVisibility);
        };

        RebuildContainerItems();
        UpdateTooltip();
        UpdateSystemVisibility();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => BringMainWindowToForeground();

    private void BringMainWindowToForeground()
    {
        App.MainWindow.Activate();
        // VERIFY: WinUI 3 desktop apps have no direct "un-minimize" API on Window itself;
        // AppWindow.Restore() (via WindowNative + Win32 interop, same pattern as
        // SettingsPage's FileOpenPicker window handle) may be needed here if the window was
        // minimized rather than merely occluded - untested from macOS.
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        _services.ModelServerService.StopAll();
        _services.StatsService.Shutdown();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    private async void OnStartSystemClick(object sender, RoutedEventArgs e) =>
        await _services.SystemService.StartSystemAsync();

    private void UpdateSystemVisibility()
    {
        // Only surface system status when stopped, matching the Swift original's menu-bar
        // rule - a running system needs no tray affordance.
        var stopped = _services.SystemService.SystemStatus == SystemStatus.Stopped;
        StartSystemMenuItem.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
        StartSystemSeparator.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTooltip()
    {
        var running = _services.ContainerListService.Containers
            .Where(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (running.Count == 0)
        {
            ToolTipText = "Orchard for Windows - no running containers";
            return;
        }

        var totalMemory = running
            .Select(c => _services.StatsService.LatestSamples.GetValueOrDefault(c.Configuration.Id)?.MemoryBytes ?? 0)
            .Sum();
        var avgCpu = running
            .Select(c => _services.StatsService.LatestSamples.GetValueOrDefault(c.Configuration.Id)?.CpuPercent ?? 0)
            .DefaultIfEmpty(0)
            .Average();

        ToolTipText = $"Orchard for Windows - {running.Count} running - CPU {avgCpu:F0}% - Mem {ByteFormat.Memory(totalMemory)}";
    }

    /// Rebuilds the per-container quick-action items between "Open" and the system/exit
    /// section, mirroring the Swift menu-bar's per-container start/stop rows.
    private void RebuildContainerItems()
    {
        var flyout = (MenuFlyout)ContextFlyout!;
        var items = flyout.Items;

        // Remove any previously-inserted container items (tagged below) before rebuilding.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is MenuFlyoutItem { Tag: "container-item" })
            {
                items.RemoveAt(i);
            }
        }

        var insertAt = items.IndexOf(OpenMenuItem) + 2; // after Open + its separator
        foreach (var container in _services.ContainerListService.Containers.OrderByDescending(IsRunning))
        {
            var running = IsRunning(container);
            var item = new MenuFlyoutItem
            {
                Text = running ? $"Stop {container.Configuration.Id}" : $"Start {container.Configuration.Id}",
                Tag = "container-item",
            };
            var id = container.Configuration.Id;
            item.Click += async (_, _) =>
            {
                if (running) await _services.ContainerListService.StopContainerAsync(id);
                else await _services.ContainerListService.StartContainerAsync(id);
            };
            items.Insert(insertAt, item);
            insertAt++;
        }
    }

    private static bool IsRunning(Container c) => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase);
}
