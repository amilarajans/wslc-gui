using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.Controls;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace OrchardWin.App.Views;

/// Orchard-style containers: list + detail with metric cards (CPU/Memory/Network/Disk charts)
/// and paired config wells (Overview / Environment / Image / Process / Network / Labels).
public sealed partial class ContainersPage : Page
{
    private ContainersViewModel? _viewModel;
    private DispatcherTimer? _pollTimer;
    private string? _pendingSelectId;
    private bool _isRestoringSelection;
    private bool _listBound;
    private string? _detailContainerId;

    // Retained metric cards so stats ticks update series in place (no Children.Clear flicker).
    private MetricChart? _cpuChart, _memChart, _netChart, _diskChart;
    private TextBlock? _cpuPrimary, _cpuSecondary, _memPrimary, _memSecondary;
    private ProgressBar? _cpuBar, _memBar;
    private TextBlock? _netRxText, _netTxText, _diskRText, _diskWText;
    private bool _metricsStructureBuilt;

    private static readonly Color CpuColor = Color.FromArgb(255, 64, 156, 255);
    private static readonly Color MemColor = Color.FromArgb(255, 176, 100, 255);
    private static readonly Color NetRxColor = Color.FromArgb(255, 107, 203, 119);
    private static readonly Color NetTxColor = Color.FromArgb(255, 244, 162, 97);
    private static readonly Color DiskRColor = Color.FromArgb(255, 78, 205, 196);
    private static readonly Color DiskWColor = Color.FromArgb(255, 231, 111, 155);

    public ContainersPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Log.Ui.Info("ContainersPage.OnNavigatedTo");
        try
        {
            var args = NavigationArgs.From(e.Parameter);
            _pendingSelectId = args.SelectContainerId;
            _viewModel = new ContainersViewModel(args.Services);
            // Bind list once — later polls mutate the same ObservableCollection (no ItemsSource thrash).
            ContainersListView.ItemsSource = _viewModel.ContainerRows;
            _listBound = true;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SafeApplyViewModelState();
            HighlightWindow(_viewModel.SelectedStatsWindow);
            _ = LoadAndMaybeSelectAsync();
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("ContainersPage.OnNavigatedTo", ex);
            throw;
        }
    }

    public void SelectContainer(string containerId)
    {
        _pendingSelectId = containerId;
        TryApplyPendingSelection();
    }

    private async Task LoadAndMaybeSelectAsync()
    {
        if (_viewModel is null) return;
        try
        {
            Log.Ui.Info("ContainersPage.LoadAndMaybeSelectAsync start");
            await _viewModel.LoadAsync();
            Log.Ui.Info($"ContainersPage loaded rows={_viewModel.ContainerRows.Count}");
            // Always re-enter UI thread after await — XAML updates must not run on the thread pool.
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    TryApplyPendingSelection();
                    SafeApplyViewModelState();
                }
                catch (Exception ex)
                {
                    Log.WriteCrashReport("ContainersPage.LoadAndMaybeSelectAsync.UI", ex);
                }
            });
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("ContainersPage.LoadAndMaybeSelectAsync", ex);
        }
    }

    private void TryApplyPendingSelection()
    {
        if (_viewModel is null || string.IsNullOrEmpty(_pendingSelectId)) return;

        var id = _pendingSelectId;
        var row = _viewModel.ContainerRows.FirstOrDefault(r =>
            string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.PrimaryText, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.Container.Configuration.Hostname, id, StringComparison.OrdinalIgnoreCase));

        if (row is null) return;

        _pendingSelectId = null;
        _viewModel.SelectedIds.Clear();
        _viewModel.SelectedIds.Add(row.Id);
        _viewModel.SelectedContainer = row.Container;
        ApplyViewModelState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _pollTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.PollAsync();
        };
        _pollTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer?.Stop();
        _pollTimer = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Route changes: stats → metrics only; list/selection → full chrome. Never rebuild
        // everything on every PropertyChanged (that was the flicker).
        DispatcherQueue.RunOnUi(() =>
        {
            try
            {
                if (_viewModel is null) return;

                if (e.PropertyName is nameof(ContainersViewModel.StatsRevision))
                {
                    UpdateMetricsOnly();
                    return;
                }

                if (e.PropertyName is nameof(ContainersViewModel.IsSelectedBusy))
                {
                    UpdateActionButtons();
                    return;
                }

                if (e.PropertyName is nameof(ContainersViewModel.AlertMessage))
                {
                    ApplyAlert();
                    return;
                }

                // ContainerRows, SelectedContainer, or wholesale reset.
                SafeApplyViewModelState();
            }
            catch (Exception ex)
            {
                Log.WriteCrashReport("ContainersPage.OnViewModelPropertyChanged", ex);
            }
        });
    }

    private void SafeApplyViewModelState()
    {
        try
        {
            ApplyViewModelState();
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("ContainersPage.ApplyViewModelState", ex);
        }
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        _isRestoringSelection = true;
        try
        {
            // Keep a single ItemsSource reference (set in OnNavigatedTo). Reassign only if lost.
            if (!_listBound || !ReferenceEquals(ContainersListView.ItemsSource, _viewModel.ContainerRows))
            {
                ContainersListView.ItemsSource = _viewModel.ContainerRows;
                _listBound = true;
            }

            ContainerRowVm? selectedRow = null;
            if (_viewModel.SelectedContainer is { } sel)
            {
                selectedRow = _viewModel.ContainerRows.FirstOrDefault(r =>
                    string.Equals(r.Id, sel.Configuration.Id, StringComparison.OrdinalIgnoreCase));
            }
            else if (_viewModel.SelectedIds.Count > 0)
            {
                var id = _viewModel.SelectedIds.First();
                selectedRow = _viewModel.ContainerRows.FirstOrDefault(r =>
                    string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            }

            // Avoid selection churn that re-fires SelectionChanged / detail rebuild.
            if (!ReferenceEquals(ContainersListView.SelectedItem, selectedRow))
                ContainersListView.SelectedItem = selectedRow;
        }
        finally
        {
            _isRestoringSelection = false;
        }

        if (!string.IsNullOrEmpty(_pendingSelectId) && _viewModel.ContainerRows.Count > 0)
            TryApplyPendingSelection();

        ApplyAlert();
        UpdateDetailPane();
    }

    private void ApplyAlert()
    {
        if (_viewModel is null) return;
        var alert = _viewModel.AlertMessage;
        AlertBar.IsOpen = !string.IsNullOrEmpty(alert);
        AlertBar.Message = alert ?? "";
        AlertBar.Title = string.IsNullOrEmpty(alert) ? "" : "Error";
    }

    private void OnAlertBarClose(InfoBar sender, object args)
    {
        _viewModel?.Services.AlertCenter.Dismiss();
        AlertBar.IsOpen = false;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
    }

    private void OnRunningOnlyToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.ShowOnlyRunning = RunningOnlyToggle.IsOn;
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (SortCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            Enum.TryParse<ContainerSortOption>(tag, out var option))
            _viewModel.SortOption = option;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isRestoringSelection) return;

        _viewModel.SelectedIds.Clear();
        if (ContainersListView.SelectedItem is ContainerRowVm selected)
        {
            _viewModel.SelectedIds.Add(selected.Id);
            _viewModel.SelectedContainer = selected.Container;
        }
        else
        {
            _viewModel.SelectedContainer = null;
        }
    }

    private void OnContextMenuOpening(object sender, object e)
    {
        RowContextMenu.Items.Clear();
        if (_viewModel is null) return;

        var rows = new List<ContainerRowVm>();
        if (ContainersListView.SelectedItem is ContainerRowVm one)
            rows.Add(one);
        if (rows.Count == 0) return;

        var ids = rows.Select(r => r.Id).ToList();
        var multiple = ids.Count > 1;
        var anyRunning = rows.Any(r => ContainersViewModel.IsRunning(r.Container));
        var anyStopped = rows.Any(r => !ContainersViewModel.IsRunning(r.Container));

        if (anyRunning)
        {
            var stop = new MenuFlyoutItem { Text = multiple ? $"Stop {ids.Count} Containers" : "Stop Container" };
            stop.Click += async (_, _) => await _viewModel.StopManyAsync(ids);
            RowContextMenu.Items.Add(stop);
        }

        if (anyStopped)
        {
            var start = new MenuFlyoutItem { Text = multiple ? $"Start {ids.Count} Containers" : "Start Container" };
            start.Click += async (_, _) => await _viewModel.StartManyAsync(ids);
            RowContextMenu.Items.Add(start);
        }

        RowContextMenu.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem { Text = multiple ? $"Remove {ids.Count} Containers" : "Remove Container" };
        remove.Click += async (_, _) => await _viewModel.RemoveManyAsync(ids);
        RowContextMenu.Items.Add(remove);
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        var dialog = new RunContainerDialog(_viewModel.Services, imageName: "") { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        StartButton.IsEnabled = false;
        try { await _viewModel.StartSelectedContainerAsync(); }
        finally { UpdateDetailPane(); }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        StopButton.IsEnabled = false;
        try { await _viewModel.StopSelectedContainerAsync(); }
        finally { UpdateDetailPane(); }
    }

    private async void OnTerminalClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.OpenTerminalSelectedContainerAsync();
    }

    private async void OnTerminalBashClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.OpenTerminalBashAsync();
    }

    private void OnLogsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedContainer is null) return;
        // Prefer name for the log CLI; fall back to id.
        var c = _viewModel.SelectedContainer;
        var key = !string.IsNullOrWhiteSpace(c.Configuration.Hostname)
            ? c.Configuration.Hostname!
            : c.Configuration.Id;
        App.MainWindow.NavigateTo("logs", selectContainerId: key);
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        RemoveButton.IsEnabled = false;
        try { await _viewModel.RemoveSelectedContainerAsync(); }
        finally { UpdateDetailPane(); }
    }

    private void OnStatsWindowClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse<StatsWindow>(tag, out var window)) return;
        _viewModel.SelectedStatsWindow = window;
        HighlightWindow(window);
        // Window change only affects chart series — not list/config cards.
        UpdateMetricsOnly();
    }

    private void HighlightWindow(StatsWindow window)
    {
        void Style(Button b, bool on) =>
            b.Background = on
                ? ThemeBrush("AccentFillColorDefaultBrush", new SolidColorBrush(CpuColor))
                : new SolidColorBrush(Colors.Transparent);

        Style(Win5m, window == StatsWindow.FiveMin);
        Style(Win15m, window == StatsWindow.FifteenMin);
        Style(Win1h, window == StatsWindow.OneHour);
        Style(Win24h, window == StatsWindow.TwentyFourHours);
    }

    private void UpdateDetailPane()
    {
        try
        {
            var container = _viewModel?.SelectedContainer;
            if (_viewModel is null || container is null)
            {
                EmptyState.Visibility = Visibility.Visible;
                DetailRoot.Visibility = Visibility.Collapsed;
                _detailContainerId = null;
                MetricsHost.Children.Clear();
                _metricsStructureBuilt = false;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            DetailRoot.Visibility = Visibility.Visible;

            var running = ContainersViewModel.IsRunning(container);
            var displayName = !string.IsNullOrWhiteSpace(container.Configuration.Hostname)
                ? container.Configuration.Hostname!
                : container.Configuration.Id;

            DetailNameText.Text = displayName;
            DetailImageText.Text = container.Configuration.Image.Reference;
            UpdateActionButtons();

            var selectionChanged = !string.Equals(_detailContainerId, container.Configuration.Id, StringComparison.Ordinal);
            if (selectionChanged)
            {
                _detailContainerId = container.Configuration.Id;
                _metricsStructureBuilt = false;
                MetricsHost.Children.Clear();
                BuildConfigCards(container);
            }

            // Metrics: build once per selection, then SetSeries/text only on ticks.
            UpdateOrBuildMetricCards(container, running);
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("ContainersPage.UpdateDetailPane", ex);
        }
    }

    /// Live stats tick: update chart series + labels only (no list/config rebuild).
    private void UpdateMetricsOnly()
    {
        try
        {
            if (_viewModel?.SelectedContainer is not { } container) return;
            if (DetailRoot.Visibility != Visibility.Visible) return;
            UpdateOrBuildMetricCards(container, ContainersViewModel.IsRunning(container));
        }
        catch (Exception ex)
        {
            Log.WriteCrashReport("ContainersPage.UpdateMetricsOnly", ex);
        }
    }

    private void UpdateActionButtons()
    {
        if (_viewModel?.SelectedContainer is not { } container) return;
        var running = ContainersViewModel.IsRunning(container);
        var busy = _viewModel.IsSelectedBusy;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = !running && !busy;
        StopButton.IsEnabled = running && !busy;
        TerminalShButton.IsEnabled = running && !busy;
        TerminalBashButton.IsEnabled = running && !busy;
        LogsButton.IsEnabled = true;
        RemoveButton.IsEnabled = !busy;
    }

    private void UpdateOrBuildMetricCards(Container container, bool running)
    {
        if (_viewModel is null) return;

        if (!_metricsStructureBuilt || MetricsHost.Children.Count == 0)
        {
            BuildMetricCardsStructure(container);
            _metricsStructureBuilt = true;
        }

        ApplyMetricData(running);
    }

    private void BuildMetricCardsStructure(Container container)
    {
        MetricsHost.Children.Clear();
        if (_viewModel is null) return;

        // CPU
        (_cpuChart, _cpuPrimary, _cpuSecondary, _cpuBar) = AddSimpleMetricCard("CPU", CpuColor, showBar: true);
        // MEMORY
        (_memChart, _memPrimary, _memSecondary, _memBar) = AddSimpleMetricCard("MEMORY", MemColor, showBar: true);

        // NETWORK — left column holds live legend texts
        _netRxText = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Foreground = new SolidColorBrush(NetRxColor) };
        _netTxText = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Foreground = new SolidColorBrush(NetTxColor) };
        var netLeft = new StackPanel { Spacing = 8 };
        var netLegend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        netLegend.Children.Add(_netRxText);
        netLegend.Children.Add(_netTxText);
        netLeft.Children.Add(netLegend);
        var hostname = ContainersViewModel.Hostname(container) ?? container.Configuration.Hostname ?? "";
        var address = ContainersViewModel.NetworkAddress(container) ?? "";
        if (!string.IsNullOrEmpty(hostname) || !string.IsNullOrEmpty(address))
        {
            netLeft.Children.Add(Muted("Info"));
            if (!string.IsNullOrEmpty(hostname)) netLeft.Children.Add(CopyableRow("Hostname", hostname));
            if (!string.IsNullOrEmpty(address)) netLeft.Children.Add(CopyableRow("Address", address));
        }
        _netChart = AddCustomMetricCard("NETWORK", netLeft);

        // DISK
        _diskRText = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Foreground = new SolidColorBrush(DiskRColor) };
        _diskWText = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Foreground = new SolidColorBrush(DiskWColor) };
        var diskLeft = new StackPanel { Spacing = 8 };
        var diskLegend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        diskLegend.Children.Add(_diskRText);
        diskLegend.Children.Add(_diskWText);
        diskLeft.Children.Add(diskLegend);
        if (container.Configuration.Mounts.Count > 0)
        {
            diskLeft.Children.Add(Muted("Mounts"));
            foreach (var m in container.Configuration.Mounts.Take(6))
            {
                diskLeft.Children.Add(new TextBlock
                {
                    Text = m.Destination,
                    FontSize = 12,
                    FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                    Foreground = new SolidColorBrush(CpuColor),
                });
            }
        }
        _diskChart = AddCustomMetricCard("DISK", diskLeft);
    }

    private void ApplyMetricData(bool running)
    {
        if (_viewModel is null) return;

        var cores = _viewModel.SelectedCoresAllocated;
        var memLimit = _viewModel.SelectedMemoryLimitBytes;

        if (_cpuPrimary is not null) _cpuPrimary.Text = _viewModel.SelectedCpuPercentText;
        if (_cpuSecondary is not null)
            _cpuSecondary.Text = cores > 0 ? $"{cores} {(cores == 1 ? "core" : "cores")} allocated" : "";
        if (_cpuBar is not null)
        {
            _cpuBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (running) _cpuBar.Value = Math.Clamp(_viewModel.SelectedSample?.CpuPercent ?? 0, 0, 100);
        }
        // Match Memory style: smooth stroke + soft area fill under the curve.
        _cpuChart?.SetSeries(
        [
            new ChartSeries { Values = _viewModel.SelectedCpuHistory, Stroke = CpuColor, Thickness = 1.6, Fill = true },
        ], fixedMax: 100);

        if (_memPrimary is not null) _memPrimary.Text = _viewModel.SelectedMemoryText;
        if (_memSecondary is not null) _memSecondary.Text = _viewModel.SelectedMemorySecondaryText;
        if (_memBar is not null)
        {
            var show = running && memLimit > 0;
            _memBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                _memBar.Value = Math.Clamp(
                    _viewModel.SelectedRawStats?.MemoryUsagePercent
                    ?? _viewModel.SelectedSample?.MemoryPercent ?? 0, 0, 100);
            }
        }
        _memChart?.SetSeries(
        [
            new ChartSeries { Values = _viewModel.SelectedMemoryHistory, Stroke = MemColor, Thickness = 1.6, Fill = true },
        ], guideValue: memLimit > 0 ? memLimit : null);

        if (_netRxText is not null) _netRxText.Text = _viewModel.SelectedNetworkRxText;
        if (_netTxText is not null) _netTxText.Text = _viewModel.SelectedNetworkTxText;
        // Uploads (tx) above center, downloads (rx) below.
        _netChart?.SetSeries(
        [
            new ChartSeries { Values = _viewModel.SelectedNetworkTxHistory, Stroke = NetTxColor, Thickness = 1.6, Fill = true, PlotBelow = false },
            new ChartSeries { Values = _viewModel.SelectedNetworkRxHistory, Stroke = NetRxColor, Thickness = 1.6, Fill = true, PlotBelow = true },
        ], mirrored: true);

        if (_diskRText is not null) _diskRText.Text = _viewModel.SelectedDiskReadText;
        if (_diskWText is not null) _diskWText.Text = _viewModel.SelectedDiskWriteText;
        // Read above, write below.
        _diskChart?.SetSeries(
        [
            new ChartSeries { Values = _viewModel.SelectedDiskReadHistory, Stroke = DiskRColor, Thickness = 1.6, Fill = true, PlotBelow = false },
            new ChartSeries { Values = _viewModel.SelectedDiskWriteHistory, Stroke = DiskWColor, Thickness = 1.6, Fill = true, PlotBelow = true },
        ], mirrored: true);
    }

    private (MetricChart Chart, TextBlock Primary, TextBlock Secondary, ProgressBar? Bar) AddSimpleMetricCard(
        string title, Color barColor, bool showBar)
    {
        var primary = new TextBlock
        {
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        };
        var secondary = new TextBlock { FontSize = 12, Opacity = 0.55, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
        ProgressBar? bar = showBar
            ? new ProgressBar { Maximum = 100, Height = 4, Foreground = new SolidColorBrush(barColor) }
            : null;

        var left = new StackPanel { Spacing = 6, Width = 160 };
        left.Children.Add(primary);
        left.Children.Add(secondary);
        if (bar is not null) left.Children.Add(bar);

        var chart = AddCustomMetricCard(title, left);
        return (chart, primary, secondary, bar);
    }

    private MetricChart AddCustomMetricCard(string title, UIElement leftContent)
    {
        var chart = new MetricChart { Height = 110, MinWidth = 200 };
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCol = new StackPanel { Spacing = 8 };
        leftCol.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 40,
            Opacity = 0.85,
        });
        leftCol.Children.Add(leftContent);
        Grid.SetColumn(leftCol, 0);
        Grid.SetColumn(chart, 1);
        grid.Children.Add(leftCol);
        grid.Children.Add(chart);

        MetricsHost.Children.Add(new Border
        {
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush",
                new SolidColorBrush(Color.FromArgb(20, 128, 128, 128))),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush",
                new SolidColorBrush(Color.FromArgb(40, 128, 128, 128))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Child = grid,
        });
        return chart;
    }

    private void BuildConfigCards(Container container)
    {
        // Overview
        var overview = new StackPanel { Spacing = 8 };
        overview.Children.Add(CardTitle("Overview"));
        overview.Children.Add(CopyableRow("Container ID", container.Configuration.Id));
        overview.Children.Add(InfoRow("Runtime", container.Configuration.RuntimeHandler));
        overview.Children.Add(InfoRow("Platform",
            $"{container.Configuration.Platform.Os}/{container.Configuration.Platform.Architecture}"));
        if (!string.IsNullOrEmpty(container.Configuration.Hostname))
            overview.Children.Add(InfoRow("Hostname", container.Configuration.Hostname!));
        OverviewCard.Child = overview;

        // Environment
        var env = new StackPanel { Spacing = 6 };
        env.Children.Add(CardTitle("Environment"));
        if (container.Configuration.InitProcess.Environment.Count == 0)
        {
            env.Children.Add(Muted("No environment variables"));
        }
        else
        {
            // Header
            var header = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            header.Children.Add(Muted("Variable"));
            var vh = Muted("Value"); Grid.SetColumn(vh, 1); header.Children.Add(vh);
            env.Children.Add(header);

            foreach (var entry in container.Configuration.InitProcess.Environment)
            {
                var eq = entry.IndexOf('=');
                var key = eq >= 0 ? entry[..eq] : entry;
                var val = eq >= 0 ? entry[(eq + 1)..] : "";
                env.Children.Add(EnvRow(key, val));
            }
        }
        EnvironmentCard.Child = env;

        // Image
        var image = new StackPanel { Spacing = 8 };
        image.Children.Add(CardTitle("Image"));
        image.Children.Add(InfoRow("Reference", container.Configuration.Image.Reference));
        image.Children.Add(InfoRow("Media Type", container.Configuration.Image.Descriptor.MediaType));
        var digest = container.Configuration.Image.Descriptor.Digest.Replace("sha256:", "");
        image.Children.Add(CopyableRow("Digest", digest.Length > 12 ? digest[..12] : digest));
        image.Children.Add(InfoRow("Size", ByteFormat.String(container.Configuration.Image.Descriptor.Size)));
        ImageCard.Child = image;

        // Process
        var proc = new StackPanel { Spacing = 8 };
        proc.Children.Add(CardTitle("Process"));
        var init = container.Configuration.InitProcess;
        proc.Children.Add(InfoRow("Executable", string.IsNullOrEmpty(init.Executable) ? "--" : init.Executable));
        proc.Children.Add(InfoRow("Working Directory", init.WorkingDirectory));
        proc.Children.Add(InfoRow("Terminal", init.Terminal ? "enabled" : "disabled"));
        proc.Children.Add(InfoRow("UID:GID", string.IsNullOrEmpty(init.User) ? "--" : init.User!));
        if (init.Arguments.Count > 0)
            proc.Children.Add(InfoRow("Arguments", string.Join(' ', init.Arguments)));
        ProcessCard.Child = proc;

        // Network
        var network = new StackPanel { Spacing = 8 };
        network.Children.Add(CardTitle("Network"));
        if (container.Networks.Count > 0)
        {
            foreach (var a in container.Networks)
            {
                var host = a.Hostname.EndsWith('.') ? a.Hostname[..^1] : a.Hostname;
                if (!string.IsNullOrEmpty(host)) network.Children.Add(CopyableRow("Hostname", host));
                if (!string.IsNullOrEmpty(a.Address))
                    network.Children.Add(CopyableRow("Address", a.Address));
                if (!string.IsNullOrEmpty(a.Gateway)) network.Children.Add(InfoRow("Gateway", a.Gateway));
                if (!string.IsNullOrEmpty(a.Network))
                {
                    network.Children.Add(new TextBlock
                    {
                        Text = a.Network,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(CpuColor),
                        Margin = new Thickness(0, 2, 0, 0),
                    });
                }
            }
        }
        else
        {
            network.Children.Add(Muted("No network attachments"));
        }

        if (container.Configuration.PublishedPorts.Count > 0)
        {
            network.Children.Add(Muted("Published Ports"));
            foreach (var p in container.Configuration.PublishedPorts)
            {
                var spec = !string.IsNullOrEmpty(p.HostAddress)
                    ? $"{p.HostAddress}:{p.HostPort}:{p.ContainerPort}/{p.TransportProtocol}"
                    : $"0.0.0.0:{p.HostPort}:{p.ContainerPort}/{p.TransportProtocol}";
                network.Children.Add(CopyableRow("Port", spec));
            }
        }
        NetworkCard.Child = network;

        // Labels
        var labels = new StackPanel { Spacing = 8 };
        labels.Children.Add(CardTitle("Labels"));
        if (container.Configuration.Labels.Count == 0)
        {
            labels.Children.Add(Muted("No labels"));
        }
        else
        {
            foreach (var kv in container.Configuration.Labels.OrderBy(k => k.Key, StringComparer.Ordinal))
                labels.Children.Add(InfoRow(kv.Key, kv.Value));
        }
        LabelsCard.Child = labels;
    }

    // MARK: - Card builders

    private static Brush ThemeBrush(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
                return brush;
        }
        catch (Exception ex)
        {
            Log.Ui.Error($"ThemeBrush missing/failed for {key}: {ex.Message}");
        }
        return fallback;
    }

    private static TextBlock CardTitle(string title) => new()
    {
        Text = title,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 4),
    };

    private static UIElement InfoRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, Opacity = 0.55, FontSize = 12 };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetColumn(v, 1);
        grid.Children.Add(l);
        grid.Children.Add(v);
        return grid;
    }

    private static UIElement CopyableRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, Opacity = 0.55, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copy = new HyperlinkButton
        {
            Content = "Copy",
            FontSize = 11,
            Padding = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        copy.Click += (_, _) =>
        {
            var pkg = new DataPackage();
            pkg.SetText(value);
            Clipboard.SetContent(pkg);
        };
        Grid.SetColumn(v, 1);
        Grid.SetColumn(copy, 2);
        grid.Children.Add(l);
        grid.Children.Add(v);
        grid.Children.Add(copy);
        return grid;
    }

    private static UIElement EnvRow(string key, string value)
    {
        var grid = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        grid.Children.Add(new TextBlock
        {
            Text = key,
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var masked = new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "" : "••••••••",
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
            Opacity = 0.7,
        };
        Grid.SetColumn(masked, 1);
        var show = new HyperlinkButton { Content = "Show", FontSize = 11, Padding = new Thickness(0) };
        var shown = false;
        show.Click += (_, _) =>
        {
            shown = !shown;
            masked.Text = shown ? value : (string.IsNullOrEmpty(value) ? "" : "••••••••");
            show.Content = shown ? "Hide" : "Show";
        };
        Grid.SetColumn(show, 2);
        grid.Children.Add(masked);
        grid.Children.Add(show);
        return grid;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Opacity = 0.55,
        FontSize = 12,
        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        TextWrapping = TextWrapping.Wrap,
    };
}
