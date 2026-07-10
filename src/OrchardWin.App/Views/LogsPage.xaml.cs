using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;

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
        var services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new LogsViewModel(services);
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);
        ApplyViewModelState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // LogsViewModel holds no timer of its own (matches this app's convention - see
        // DashboardPage) - poll on a ~2s cadence while this page is visible, mirroring
        // MultiLogView's LogPaneView refresh timer.
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
            TargetCombo.ItemsSource = _viewModel.Targets;
            TargetCombo.SelectedItem = _viewModel.SelectedTarget;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        PauseIcon.Glyph = _viewModel.IsPaused ? "" : "";
        PauseButtonText.Text = _viewModel.IsPaused ? "Resume" : "Pause";

        MatchCountText.Text = _viewModel.MatchCountText ?? "";

        var hasTarget = _viewModel.SelectedTarget is not null;
        var hasLines = _viewModel.DisplayLines.Count > 0;

        LogLinesControl.ItemsSource = _viewModel.DisplayLines;

        if (!hasTarget)
        {
            EmptyStateText.Text = "Select a container or machine above";
            EmptyStateText.Visibility = Visibility.Visible;
            LogScroller.Visibility = Visibility.Collapsed;
        }
        else if (!hasLines)
        {
            EmptyStateText.Text = _viewModel.IsLoading ? "Loading logs..." : "No logs available";
            EmptyStateText.Visibility = Visibility.Visible;
            LogScroller.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyStateText.Visibility = Visibility.Collapsed;
            LogScroller.Visibility = Visibility.Visible;
            LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null);
        }
    }
}
