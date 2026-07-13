using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.Controls;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.Views;

public sealed partial class DashboardPage : Page
{
    private DashboardViewModel? _viewModel;
    private DispatcherTimer? _diskRefreshTimer;
    private bool _listsBound;

    private static readonly Color CpuColor = Color.FromArgb(255, 64, 156, 255);
    private static readonly Color MemColor = Color.FromArgb(255, 176, 100, 255);
    private static readonly Color NetRxColor = Color.FromArgb(255, 107, 203, 119);
    private static readonly Color NetTxColor = Color.FromArgb(255, 244, 162, 97);
    private static readonly Color DiskRColor = Color.FromArgb(255, 78, 205, 196);
    private static readonly Color DiskWColor = Color.FromArgb(255, 231, 111, 155);
    private static readonly Color ReclaimColor = Color.FromArgb(255, 245, 158, 11);

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var args = NavigationArgs.From(e.Parameter);
        _viewModel = new DashboardViewModel(args.Services);
        ContainerRowsList.ItemsSource = _viewModel.ContainerRows;
        MachineRowsList.ItemsSource = _viewModel.MachineRows;
        _listsBound = true;

        _viewModel.PropertyChanged += (_, e2) =>
        {
            // MetricsRevision → charts only (once per sample). Disk/chrome → tiles + empty state.
            // Do NOT subscribe to every nested SystemMetrics property — that redraws charts ~15× per tick.
            if (e2.PropertyName is nameof(DashboardViewModel.MetricsRevision)
                or nameof(DashboardViewModel.SelectedWindow))
            {
                DispatcherQueue.RunOnUi(ApplySystemCharts);
                return;
            }

            if (e2.PropertyName is null
                or nameof(DashboardViewModel.DiskUsage)
                or nameof(DashboardViewModel.StatsUnavailable)
                or nameof(DashboardViewModel.EmptyContainersMessage))
            {
                DispatcherQueue.RunOnUi(ApplyChromeState);
            }
        };

        ApplyChromeState();
        HighlightWindowButton(StatsWindow.FiveMin);
        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _diskRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _diskRefreshTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.RefreshDiskUsageQuietAsync();
        };
        _diskRefreshTimer.Start();

        // Immediate paint; ongoing refresh is StatsService.TickRevision (1 Hz, app-wide).
        if (_viewModel is not null)
        {
            _viewModel.Pulse();
            ApplySystemCharts();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _diskRefreshTimer?.Stop();
        _diskRefreshTimer = null;
    }

    private void OnWindowClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse<StatsWindow>(tag, out var window)) return;
        _viewModel.SetWindow(window);
        HighlightWindowButton(window);
    }

    /// Orchard posts NavigateToContainer when a utilisation row name is clicked.
    private void OnContainerNameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        App.MainWindow.NavigateTo("containers", selectContainerId: id);
    }

    private void HighlightWindowButton(StatsWindow window)
    {
        void Style(Button b, bool on)
        {
            b.Background = on
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : new SolidColorBrush(Colors.Transparent);
        }

        Style(Win5m, window == StatsWindow.FiveMin);
        Style(Win15m, window == StatsWindow.FifteenMin);
        Style(Win1h, window == StatsWindow.OneHour);
        Style(Win24h, window == StatsWindow.TwentyFourHours);
    }

    private void ApplyChromeState()
    {
        if (_viewModel is null) return;

        UnavailableBar.IsOpen = _viewModel.StatsUnavailable;
        MachineSection.Visibility = _viewModel.MachineRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_listsBound)
        {
            if (!ReferenceEquals(ContainerRowsList.ItemsSource, _viewModel.ContainerRows))
                ContainerRowsList.ItemsSource = _viewModel.ContainerRows;
            if (!ReferenceEquals(MachineRowsList.ItemsSource, _viewModel.MachineRows))
                MachineRowsList.ItemsSource = _viewModel.MachineRows;
        }

        EmptyContainersText.Text = _viewModel.EmptyContainersMessage;
        EmptyContainersText.Visibility = _viewModel.ContainerRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var usage = _viewModel.DiskUsage;
        ContainersTile.Value = usage is null ? "--" : ByteFormat.String(usage.Containers.SizeInBytes);
        ContainersTile.Detail = usage is null ? "--" : $"{usage.Containers.Active}/{usage.Containers.Total}";
        ImagesTile.Value = usage is null ? "--" : ByteFormat.String(usage.Images.SizeInBytes);
        ImagesTile.Detail = usage is null ? "--" : $"{usage.Images.Active}/{usage.Images.Total}";
        VolumesTile.Value = usage is null ? "--" : ByteFormat.String(usage.Volumes.SizeInBytes);
        VolumesTile.Detail = usage is null ? "--" : $"{usage.Volumes.Active}/{usage.Volumes.Total}";
        ReclaimableTile.Value = usage is null ? "--" : ByteFormat.String(usage.TotalReclaimable);
        ReclaimableTile.Detail = "Space";
        ReclaimableTile.ValueColor = ReclaimColor;

        ApplySystemCharts();
    }

    private void ApplySystemCharts()
    {
        if (_viewModel is null) return;
        var m = _viewModel.SystemMetrics;

        // Charts always stay visible; empty series still draws a zero baseline.
        SystemChartsGrid.Visibility = Visibility.Visible;
        SystemEmptyText.Visibility = Visibility.Collapsed;

        CpuPrimaryText.Text = m.CpuPrimary;
        CpuSecondaryText.Text = m.CpuSecondary;
        CpuBar.Value = Math.Clamp(m.CpuPercent, 0, 100);
        CpuBar.Foreground = new SolidColorBrush(CpuColor);

        MemPrimaryText.Text = m.MemoryPrimary;
        MemSecondaryText.Text = m.MemorySecondary;
        MemBar.Value = Math.Clamp(m.MemoryPercent, 0, 100);
        MemBar.Foreground = new SolidColorBrush(MemColor);

        NetRxText.Text = m.NetworkRxText;
        NetTxText.Text = m.NetworkTxText;
        DiskReadText.Text = m.DiskReadText;
        DiskWriteText.Text = m.DiskWriteText;

        var cpuSeries = ChartPulse.EnsureDrawable(m.CpuSeries);
        var memSeries = ChartPulse.EnsureDrawable(m.MemorySeries);
        var netTx = ChartPulse.EnsureDrawable(m.NetworkTxSeries);
        var netRx = ChartPulse.EnsureDrawable(m.NetworkRxSeries);
        var diskR = ChartPulse.EnsureDrawable(m.DiskReadSeries);
        var diskW = ChartPulse.EnsureDrawable(m.DiskWriteSeries);

        // Match Memory style: smooth stroke + soft area fill under the curve.
        CpuChart.SetSeries(
        [
            new ChartSeries { Values = cpuSeries, Stroke = CpuColor, Thickness = 1.6, Fill = true },
        ]);

        MemChart.SetSeries(
        [
            new ChartSeries { Values = memSeries, Stroke = MemColor, Thickness = 1.6, Fill = true },
        ], guideValue: m.MemoryLimitBytes > 0 ? m.MemoryLimitBytes : null);

        // Mirrored I/O: uploads (tx) above center, downloads (rx) below.
        NetChart.SetSeries(
        [
            new ChartSeries { Values = netTx, Stroke = NetTxColor, Thickness = 1.6, Fill = true, PlotBelow = false },
            new ChartSeries { Values = netRx, Stroke = NetRxColor, Thickness = 1.6, Fill = true, PlotBelow = true },
        ], mirrored: true);

        DiskChart.SetSeries(
        [
            new ChartSeries { Values = diskR, Stroke = DiskRColor, Thickness = 1.6, Fill = true, PlotBelow = false },
            new ChartSeries { Values = diskW, Stroke = DiskWColor, Thickness = 1.6, Fill = true, PlotBelow = true },
        ], mirrored: true);
    }

}
