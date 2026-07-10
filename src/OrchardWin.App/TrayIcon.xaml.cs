using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OrchardWin.App.Controls;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App;

/// System tray presence matching Orchard's menu-bar panel: segmented CPU/MEMORY rings,
/// per-container rows (memory + CPU + start/stop), Open / Exit, hover history flyouts.
public sealed partial class TrayIcon : TaskbarIcon
{
    private readonly AppServices _services;
    private DispatcherTimer? _refreshTimer;
    private string? _hoveredContainerId;

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 59, 130, 246),   // blue
        Color.FromArgb(255, 168, 85, 247),   // purple
        Color.FromArgb(255, 34, 197, 94),    // green
        Color.FromArgb(255, 249, 115, 22),   // orange
        Color.FromArgb(255, 20, 184, 166),   // teal
        Color.FromArgb(255, 236, 72, 153),   // pink
        Color.FromArgb(255, 234, 179, 8),    // yellow
        Color.FromArgb(255, 99, 102, 241),   // indigo
    ];

    private static readonly Color FreeColor = Color.FromArgb(50, 160, 160, 160);

    private sealed record TrayRow(
        string Id,
        string DisplayName,
        Color Color,
        bool IsRunning,
        bool IsLoading,
        long MemoryBytes,
        long MemoryLimitBytes,
        double CpuPercent,
        int Cores);

    public TrayIcon(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers)
                or nameof(ContainerListService.LoadingContainers))
                DispatcherQueue.RunOnUi(RefreshPanel);
        };
        _services.StatsService.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(RefreshPanel);
        _services.SystemService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemService.SystemStatus))
                DispatcherQueue.RunOnUi(UpdateSystemStoppedCard);
        };

        RefreshPanel();
        UpdateSystemStoppedCard();
    }

    /// <summary>
    /// H.NotifyIcon WinUI requires an explicit <see cref="UIElement.XamlRoot"/> on the
    /// tray popup when the TaskbarIcon is not in a Window visual tree. Without this,
    /// left/right-click crashes with "XamlRoot must be explicitly set for unparented popup".
    /// </summary>
    public void EnsureXamlRoot(XamlRoot? xamlRoot = null)
    {
        try
        {
            xamlRoot ??= ResolveXamlRoot();
            if (xamlRoot is null) return;

            if (TrayPopup is FrameworkElement popupRoot && !ReferenceEquals(popupRoot.XamlRoot, xamlRoot))
                popupRoot.XamlRoot = xamlRoot;

            if (TrayPopupResolved is { } resolved && !ReferenceEquals(resolved.XamlRoot, xamlRoot))
                resolved.XamlRoot = xamlRoot;

            if (ContextFlyout is { } flyout && !ReferenceEquals(flyout.XamlRoot, xamlRoot))
                flyout.XamlRoot = xamlRoot;
        }
        catch (Exception ex)
        {
            Log.Ui.Error($"Tray EnsureXamlRoot failed: {ex.Message}");
        }
    }

    private static XamlRoot? ResolveXamlRoot()
    {
        try
        {
            if (App.MainWindow?.Content is UIElement content && content.XamlRoot is { } root)
                return root;
        }
        catch
        {
            // MainWindow may not be ready during shutdown.
        }
        return null;
    }

    private void TrayPanel_Loaded(object sender, RoutedEventArgs e)
    {
        // Keep data fresh while the popup is open (Orchard menu-bar 5s refresh).
        _refreshTimer?.Stop();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, _) =>
        {
            try
            {
                await _services.ContainerListService.LoadAsync(showLoading: false);
            }
            catch { /* ignore */ }
            RefreshPanel();
        };
        _refreshTimer.Start();
        RefreshPanel();
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        CloseTrayPopup();
        App.MainWindow.Activate();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        CloseTrayPopup();
        _services.ModelServerService.StopAll();
        _services.StatsService.Shutdown();
        Application.Current.Exit();
    }

    private async void OnStartSystemClick(object sender, RoutedEventArgs e) =>
        await _services.SystemService.StartSystemAsync();

    private void UpdateSystemStoppedCard()
    {
        var stopped = _services.SystemService.SystemStatus == SystemStatus.Stopped;
        SystemStoppedCard.Visibility = stopped ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshPanel()
    {
        var rows = BuildRows();
        UpdateRings(rows);
        RebuildContainerList(rows);
        UpdateTooltip(rows);
        UpdateSystemStoppedCard();
    }

    private List<TrayRow> BuildRows()
    {
        var containers = _services.ContainerListService.Containers;
        var samples = _services.StatsService.LatestSamples;
        var loading = _services.ContainerListService.LoadingContainers;

        long Mem(Container c) => samples.GetValueOrDefault(c.Configuration.Id)?.MemoryBytes ?? 0;

        var ranked = containers
            .Where(IsRunning)
            .Where(c => samples.ContainsKey(c.Configuration.Id))
            .OrderByDescending(Mem)
            .Select(c => c.Configuration.Id)
            .ToList();

        var colorFor = new Dictionary<string, Color>(StringComparer.Ordinal);
        for (var i = 0; i < ranked.Count; i++)
            colorFor[ranked[i]] = Palette[i % Palette.Length];

        var sorted = containers
            .OrderByDescending(IsRunning)
            .ThenByDescending(Mem)
            .ToList();

        return sorted.Select(c =>
        {
            var id = c.Configuration.Id;
            var sample = samples.GetValueOrDefault(id);
            var name = !string.IsNullOrWhiteSpace(c.Configuration.Hostname)
                ? c.Configuration.Hostname!
                : (id.Length > 16 ? id[..16] : id);
            return new TrayRow(
                id,
                name,
                colorFor.GetValueOrDefault(id, FreeColor),
                IsRunning(c),
                loading.Contains(id),
                sample?.MemoryBytes ?? 0,
                sample?.MemoryLimitBytes ?? c.Configuration.Resources.MemoryInBytes,
                sample?.CpuPercent ?? 0,
                Math.Max(0, c.Configuration.Resources.Cpus));
        }).ToList();
    }

    private void UpdateRings(List<TrayRow> rows)
    {
        var running = rows.Where(r => r.IsRunning).ToList();
        var memUsed = running.Sum(r => r.MemoryBytes);
        var memLimit = running.Sum(r => r.MemoryLimitBytes);
        var totalCores = running.Sum(r => (double)Math.Max(1, r.Cores));
        var busyCores = running.Sum(r => r.CpuPercent / 100.0 * Math.Max(1, r.Cores));

        var memSegs = new List<RingSegment>();
        foreach (var r in running)
            memSegs.Add(new RingSegment { Value = Math.Max(0, r.MemoryBytes), Color = r.Color });
        if (memLimit > 0)
            memSegs.Add(new RingSegment { Value = Math.Max(0, memLimit - memUsed), Color = FreeColor });
        else if (memSegs.Count == 0)
            memSegs.Add(new RingSegment { Value = 1, Color = FreeColor });

        var cpuSegs = new List<RingSegment>();
        foreach (var r in running)
            cpuSegs.Add(new RingSegment
            {
                Value = Math.Max(0, r.CpuPercent / 100.0 * Math.Max(1, r.Cores)),
                Color = r.Color,
            });
        if (totalCores > 0)
            cpuSegs.Add(new RingSegment { Value = Math.Max(0, totalCores - busyCores), Color = FreeColor });
        else
            cpuSegs.Add(new RingSegment { Value = 1, Color = FreeColor });

        var cpuCenter = totalCores > 0 ? $"{(int)Math.Round(busyCores / totalCores * 100)}%" : "0%";
        var memCenter = memLimit > 0 ? $"{(int)Math.Round(100.0 * memUsed / memLimit)}%" : (running.Count == 0 ? "—" : "—");

        CpuRing.SetData("CPU", cpuCenter, cpuSegs);
        MemRing.SetData("MEMORY", memCenter, memSegs);

        SystemCpuNow.Text = cpuCenter;
        SystemMemNow.Text = ByteFormat.Memory(memUsed);
    }

    private void RebuildContainerList(List<TrayRow> rows)
    {
        ContainerRowsHost.Children.Clear();
        ContainersCard.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (i > 0)
            {
                ContainerRowsHost.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                    Margin = new Thickness(0, 4, 0, 4),
                });
            }
            ContainerRowsHost.Children.Add(BuildContainerRow(row));
        }
    }

    private FrameworkElement BuildContainerRow(TrayRow row)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(row.IsRunning ? row.Color : FreeColor),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = row.DisplayName,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var mem = new TextBlock
        {
            Text = row.IsRunning ? ByteFormat.Memory(row.MemoryBytes) : "Stopped",
            FontSize = 11,
            Opacity = 0.6,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var cpu = new TextBlock
        {
            Text = row.IsRunning ? $"{row.CpuPercent:F0}%" : "",
            FontSize = 11,
            Opacity = 0.6,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            VerticalAlignment = VerticalAlignment.Center,
            Width = 36,
            TextAlignment = TextAlignment.Right,
        };

        FrameworkElement control;
        if (row.IsLoading)
        {
            control = new ProgressRing { Width = 16, Height = 16, IsActive = true };
        }
        else
        {
            var btn = new Button
            {
                Content = new FontIcon
                {
                    Glyph = row.IsRunning ? "\uE71A" : "\uE768", // stop / play
                    FontSize = 11,
                    Foreground = new SolidColorBrush(row.IsRunning
                        ? Color.FromArgb(180, 180, 180, 180)
                        : Color.FromArgb(255, 34, 197, 94)),
                },
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Tag = row.Id,
            };
            var running = row.IsRunning;
            var id = row.Id;
            btn.Click += async (_, _) =>
            {
                if (running) await _services.ContainerListService.StopContainerAsync(id);
                else await _services.ContainerListService.StartContainerAsync(id);
                RefreshPanel();
            };
            control = btn;
        }

        Grid.SetColumn(name, 1);
        Grid.SetColumn(mem, 2);
        Grid.SetColumn(cpu, 3);
        Grid.SetColumn(control, 5);
        grid.Children.Add(dot);
        grid.Children.Add(name);
        grid.Children.Add(mem);
        grid.Children.Add(cpu);
        grid.Children.Add(control);

        // Hover → container history flyout (running only).
        if (row.IsRunning)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Left };
            flyout.Content = BuildContainerHistoryPanel(row);
            grid.PointerEntered += (_, _) =>
            {
                _hoveredContainerId = row.Id;
                // Refresh flyout content each open
                flyout.Content = BuildContainerHistoryPanel(row);
                FlyoutBase.SetAttachedFlyout(grid, flyout);
                FlyoutBase.ShowAttachedFlyout(grid);
            };
            grid.PointerExited += (_, _) =>
            {
                if (_hoveredContainerId == row.Id)
                {
                    _hoveredContainerId = null;
                    flyout.Hide();
                }
            };
        }

        return grid;
    }

    private UIElement BuildContainerHistoryPanel(TrayRow row)
    {
        var cpuHist = ContainerHistory(row.Id, cpu: true);
        var memHist = ContainerHistory(row.Id, cpu: false);

        var cpuBars = new MiniBarChart { Height = 56 };
        cpuBars.SetValues(cpuHist, Color.FromArgb(255, 59, 130, 246));
        var memBars = new MiniBarChart { Height = 56 };
        memBars.SetValues(memHist, Color.FromArgb(255, 168, 85, 247));

        return new Border
        {
            Width = 220,
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = row.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 },
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new Grid
                            {
                                Children =
                                {
                                    new TextBlock { Text = "CPU", FontSize = 11, Opacity = 0.55 },
                                    new TextBlock
                                    {
                                        Text = $"{row.CpuPercent:F1}%",
                                        HorizontalAlignment = HorizontalAlignment.Right,
                                        FontSize = 11,
                                        Opacity = 0.7,
                                        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                                    },
                                },
                            },
                            cpuBars,
                        },
                    },
                    new StackPanel
                    {
                        Spacing = 4,
                        Children =
                        {
                            new Grid
                            {
                                Children =
                                {
                                    new TextBlock { Text = "Memory", FontSize = 11, Opacity = 0.55 },
                                    new TextBlock
                                    {
                                        Text = ByteFormat.Memory(row.MemoryBytes),
                                        HorizontalAlignment = HorizontalAlignment.Right,
                                        FontSize = 11,
                                        Opacity = 0.7,
                                        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                                    },
                                },
                            },
                            memBars,
                        },
                    },
                },
            },
        };
    }

    private void RingsCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var histCpu = SystemHistory(cpu: true);
        var histMem = SystemHistory(cpu: false);
        SystemCpuBars.SetValues(histCpu, Color.FromArgb(255, 59, 130, 246));
        SystemMemBars.SetValues(histMem, Color.FromArgb(255, 168, 85, 247));
        SystemHistoryFlyout.ShowAt(RingsCard);
    }

    private void RingsCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SystemHistoryFlyout.Hide();
    }

    private IReadOnlyList<double> ContainerHistory(string id, bool cpu)
    {
        var history = _services.StatsService.History.Samples(new StatsKey(id));
        var cutoff = DateTimeOffset.Now - TimeSpan.FromHours(1);
        var windowed = history.Where(s => s.Timestamp >= cutoff).ToList();
        if (windowed.Count == 0) windowed = history.TakeLast(60).ToList();
        var values = windowed.Select(s => cpu ? s.CpuPercent : s.MemoryPercent).ToList();
        return Thin(values);
    }

    private IReadOnlyList<double> SystemHistory(bool cpu)
    {
        var running = _services.ContainerListService.Containers
            .Where(IsRunning)
            .ToList();
        if (running.Count == 0) return [];

        var cutoff = DateTimeOffset.Now - TimeSpan.FromHours(1);
        var pairs = running
            .Select(c => (
                Container: c,
                Samples: _services.StatsService.History.Samples(new StatsKey(c.Configuration.Id))
                    .Where(s => s.Timestamp >= cutoff)
                    .ToList()))
            .Where(p => p.Samples.Count > 0)
            .ToList();
        if (pairs.Count == 0) return [];

        if (!cpu)
        {
            var agg = StatsMath.Aggregate(pairs.Select(p => (IEnumerable<StatsSample>)p.Samples));
            return Thin(agg.Select(s => s.MemoryPercent).ToList());
        }

        // Core-weighted CPU
        var byTime = new SortedDictionary<DateTimeOffset, (double num, double den)>();
        foreach (var (container, samples) in pairs)
        {
            var cores = Math.Max(1.0, container.Configuration.Resources.Cpus);
            foreach (var s in samples)
            {
                byTime.TryGetValue(s.Timestamp, out var cur);
                byTime[s.Timestamp] = (cur.num + s.CpuPercent / 100.0 * cores, cur.den + cores);
            }
        }
        var values = byTime.Values.Select(v => v.den > 0 ? v.num / v.den * 100 : 0).ToList();
        return Thin(values);
    }

    private static List<double> Thin(IReadOnlyList<double> values, int max = 48)
    {
        if (values.Count <= max) return values.ToList();
        var step = (int)Math.Ceiling(values.Count / (double)max);
        var list = new List<double>();
        for (var i = 0; i < values.Count; i += step)
            list.Add(values[i]);
        return list;
    }

    private void UpdateTooltip(List<TrayRow> rows)
    {
        var running = rows.Where(r => r.IsRunning).ToList();
        if (running.Count == 0)
        {
            ToolTipText = "wslc-gui — no running containers";
            return;
        }
        var mem = running.Sum(r => r.MemoryBytes);
        var avgCpu = running.Average(r => r.CpuPercent);
        ToolTipText = $"wslc-gui — {running.Count} running · CPU {avgCpu:F0}% · Mem {ByteFormat.Memory(mem)}";
    }

    private static bool IsRunning(Container c) =>
        string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase);
}
