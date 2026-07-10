using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

public sealed partial class DashboardPage : Page
{
    private DashboardViewModel? _viewModel;
    private DispatcherTimer? _diskRefreshTimer;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var services = (AppServices)e.Parameter;
        _viewModel = new DashboardViewModel(services);
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);
        ApplyViewModelState();

        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Slow re-fetch of disk usage while the page is visible, mirroring the Swift
        // original's 20s diskRefreshTimer - container/machine stats themselves come from
        // StatsService's own always-on sampler, not a page-local timer.
        _diskRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _diskRefreshTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.RefreshDiskUsageQuietAsync();
        };
        _diskRefreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _diskRefreshTimer?.Stop();
        _diskRefreshTimer = null;
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        UnavailableBar.IsOpen = _viewModel.StatsUnavailable;
        MachineSection.Visibility = _viewModel.MachineRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContainerRowsList.ItemsSource = _viewModel.ContainerRows;
        MachineRowsList.ItemsSource = _viewModel.MachineRows;

        var usage = _viewModel.DiskUsage;
        ContainersTile.Value = usage is null ? "--" : ByteFormat.String(usage.Containers.SizeInBytes);
        ContainersTile.Detail = usage is null ? "--" : $"{usage.Containers.Active}/{usage.Containers.Total}";
        ImagesTile.Value = usage is null ? "--" : ByteFormat.String(usage.Images.SizeInBytes);
        ImagesTile.Detail = usage is null ? "--" : $"{usage.Images.Active}/{usage.Images.Total}";
        VolumesTile.Value = usage is null ? "--" : ByteFormat.String(usage.Volumes.SizeInBytes);
        VolumesTile.Detail = usage is null ? "--" : $"{usage.Volumes.Active}/{usage.Volumes.Total}";
        ReclaimableTile.Value = usage is null ? "--" : ByteFormat.String(usage.TotalReclaimable);
        ReclaimableTile.Detail = "Space";
    }
}
