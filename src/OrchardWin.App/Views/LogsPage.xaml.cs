using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;

namespace OrchardWin.App.Views;

public sealed partial class LogsPage : Page
{
    private LogsViewModel? _viewModel;
    private DispatcherTimer? _pollTimer;
    private bool _isSyncingSelection;

    public LogsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var args = NavigationArgs.From(e.Parameter);
        _viewModel = new LogsViewModel(args.Services);
        TargetCombo.ItemsSource = _viewModel.Targets;
        LogLinesControl.ItemsSource = _viewModel.DisplayLines;
        _viewModel.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName is nameof(LogsViewModel.LinesRevision)
                or nameof(LogsViewModel.DisplayLines)
                or nameof(LogsViewModel.Targets)
                or nameof(LogsViewModel.SelectedTarget)
                or nameof(LogsViewModel.IsPaused)
                or nameof(LogsViewModel.IsLoading)
                or nameof(LogsViewModel.MatchCountText)
                or nameof(LogsViewModel.StatusMessage)
                or null)
            {
                DispatcherQueue.RunOnUi(ApplyViewModelState);
            }
        };

        if (!string.IsNullOrEmpty(args.SelectContainerId))
            _viewModel.PreferContainer(args.SelectContainerId);

        ApplyViewModelState();
        // Ensure container list is warm so the target picker is not empty on first visit.
        _ = WarmAndRefreshAsync(args.Services);
    }

    private async Task WarmAndRefreshAsync(OrchardWin.Core.Services.AppServices services)
    {
        try
        {
            await services.ContainerListService.LoadAsync(showLoading: false);
        }
        catch
        {
            // best-effort
        }
        if (_viewModel is not null)
            await _viewModel.RefreshAsync();
    }

    /// Called when navigating to Logs while already on the page.
    public void PreferContainer(string containerId)
    {
        _viewModel?.PreferContainer(containerId);
        ApplyViewModelState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.RefreshAsync();
        };
        _pollTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void OnTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isSyncingSelection) return;
        _viewModel.SelectedTarget = TargetCombo.SelectedItem as LogTargetItem;
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.FilterText = FilterBox.Text;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.IsPaused = !_viewModel.IsPaused;
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        _isSyncingSelection = true;
        try
        {
            if (!ReferenceEquals(TargetCombo.ItemsSource, _viewModel.Targets))
                TargetCombo.ItemsSource = _viewModel.Targets;
            if (!ReferenceEquals(TargetCombo.SelectedItem, _viewModel.SelectedTarget))
                TargetCombo.SelectedItem = _viewModel.SelectedTarget;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        PauseIcon.Glyph = _viewModel.IsPaused ? "\uE768" : "\uE769";
        PauseButtonText.Text = _viewModel.IsPaused ? "Resume" : "Pause";
        MatchCountText.Text = _viewModel.MatchCountText ?? "";

        var hasTarget = _viewModel.SelectedTarget is not null;
        var hasLines = _viewModel.DisplayLines.Count > 0;
        var status = _viewModel.StatusMessage;

        if (!ReferenceEquals(LogLinesControl.ItemsSource, _viewModel.DisplayLines))
            LogLinesControl.ItemsSource = _viewModel.DisplayLines;

        if (!hasTarget)
        {
            EmptyStateText.Text = status ?? "Select a container or machine above";
            EmptyStateText.Visibility = Visibility.Visible;
            LogScroller.Visibility = Visibility.Collapsed;
        }
        else if (!hasLines)
        {
            EmptyStateText.Text = status
                ?? (_viewModel.IsLoading ? "Loading logs..." : "No logs available");
            EmptyStateText.Visibility = Visibility.Visible;
            LogScroller.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            LogScroller.Visibility = Visibility.Visible;
            var nearBottom = LogScroller.ScrollableHeight <= 0
                || LogScroller.VerticalOffset >= LogScroller.ScrollableHeight - 48;
            if (nearBottom)
                LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        }
    }
}
