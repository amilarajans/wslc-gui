using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.Views;

/// Two-pane containers view: a filterable/sortable list on the left, the selected
/// container's full configuration + lifecycle actions on the right. Follows the same
/// page-navigation contract and ViewModel-PropertyChanged-drives-ApplyViewModelState shape as
/// DashboardPage; ported from Orchard's ListContainers.swift/ContainerDetail.swift.
public sealed partial class ContainersPage : Page
{
    private ContainersViewModel? _viewModel;
    private DispatcherTimer? _pollTimer;
    private string? _pendingSelectId;

    // Guards against the synthetic SelectionChanged events raised while ApplyViewModelState
    // reassigns ItemsSource/replays SelectedItems from ViewModel.SelectedIds - without this,
    // that replay would immediately clobber the very selection it's trying to restore.
    private bool _isRestoringSelection;

    public ContainersPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var args = NavigationArgs.From(e.Parameter);
        _pendingSelectId = args.SelectContainerId;
        _viewModel = new ContainersViewModel(args.Services);
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);
        ApplyViewModelState();

        _ = LoadAndMaybeSelectAsync();
    }

    /// Called from Dashboard (and MainWindow when already on this page) to highlight a container.
    public void SelectContainer(string containerId)
    {
        _pendingSelectId = containerId;
        TryApplyPendingSelection();
    }

    private async Task LoadAndMaybeSelectAsync()
    {
        if (_viewModel is null) return;
        await _viewModel.LoadAsync();
        TryApplyPendingSelection();
    }

    private void TryApplyPendingSelection()
    {
        if (_viewModel is null || string.IsNullOrEmpty(_pendingSelectId)) return;

        var id = _pendingSelectId;
        // Match full id or hostname (dashboard may surface either depending on display).
        var row = _viewModel.ContainerRows.FirstOrDefault(r =>
            string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.PrimaryText, id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.Container.Configuration.Hostname, id, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            // Rows may still be empty until Refresh; keep pending for next ApplyViewModelState.
            return;
        }

        _pendingSelectId = null;
        _viewModel.SelectedIds.Clear();
        _viewModel.SelectedIds.Add(row.Id);
        _viewModel.SelectedContainer = row.Container;
        ApplyViewModelState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ContainerListService has no self-refresh timer of its own (unlike StatsService),
        // so the page polls while visible - mirrors the Swift view's onAppear refresh loop.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        _isRestoringSelection = true;
        try
        {
            ContainersListView.ItemsSource = _viewModel.ContainerRows;
            ContainersListView.SelectedItems.Clear();
            foreach (var row in _viewModel.ContainerRows)
            {
                if (_viewModel.SelectedIds.Contains(row.Id))
                {
                    ContainersListView.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _isRestoringSelection = false;
        }

        // Dashboard → Containers deep-link: apply once rows exist.
        if (!string.IsNullOrEmpty(_pendingSelectId) && _viewModel.ContainerRows.Count > 0)
            TryApplyPendingSelection();

        var alert = _viewModel.AlertMessage;
        AlertBar.IsOpen = !string.IsNullOrEmpty(alert);
        AlertBar.Message = alert ?? "";
        AlertBar.Title = string.IsNullOrEmpty(alert) ? "" : "Error";

        UpdateDetailPane();
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
        {
            _viewModel.SortOption = option;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isRestoringSelection) return;

        _viewModel.SelectedIds.Clear();
        foreach (var row in ContainersListView.SelectedItems.OfType<ContainerRowVm>())
        {
            _viewModel.SelectedIds.Add(row.Id);
        }

        var last = e.AddedItems.OfType<ContainerRowVm>().LastOrDefault()
                   ?? ContainersListView.SelectedItems.OfType<ContainerRowVm>().LastOrDefault();
        _viewModel.SelectedContainer = last?.Container;
    }

    /// Rebuilds the right-click menu each time it opens, mirroring Swift's
    /// `contextMenu(for:)`: WinUI's ListView already selects a right-clicked row that isn't
    /// part of the current selection (and preserves the selection when it is), so
    /// `SelectedItems` at Opening-time is already the correct target set.
    private void OnContextMenuOpening(object sender, object e)
    {
        RowContextMenu.Items.Clear();
        if (_viewModel is null) return;

        var rows = ContainersListView.SelectedItems.OfType<ContainerRowVm>().ToList();
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

            var forceStop = new MenuFlyoutItem { Text = multiple ? $"Force Stop {ids.Count} Containers" : "Force Stop" };
            forceStop.Click += async (_, _) => await _viewModel.ForceStopManyAsync(ids);
            RowContextMenu.Items.Add(forceStop);
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
        var dialog = new RunContainerDialog(_viewModel.Services, imageName: "");
        dialog.XamlRoot = this.XamlRoot;
        await dialog.ShowAsync();
    }

    // Call ViewModel methods directly (not RelayCommand.Execute): CanExecute can lag behind
    // selection until NotifyCanExecuteChanged runs, which previously made Start look enabled
    // while silently no-oping.
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
        ForceStopButton.IsEnabled = false;
        try { await _viewModel.StopSelectedContainerAsync(); }
        finally { UpdateDetailPane(); }
    }

    private async void OnForceStopClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        ForceStopButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        try { await _viewModel.ForceStopSelectedContainerAsync(); }
        finally { UpdateDetailPane(); }
    }

    private async void OnTerminalClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        await _viewModel.OpenTerminalSelectedContainerAsync();
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        RemoveButton.IsEnabled = false;
        try { await _viewModel.RemoveSelectedContainerAsync(); }
        finally { UpdateDetailPane(); }
    }

    private void UpdateDetailPane()
    {
        var container = _viewModel?.SelectedContainer;
        if (_viewModel is null || container is null)
        {
            EmptyState.Visibility = Visibility.Visible;
            DetailRoot.Visibility = Visibility.Collapsed;
            DetailScroller.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Collapsed;
        DetailRoot.Visibility = Visibility.Visible;
        DetailScroller.Visibility = Visibility.Visible;

        var running = ContainersViewModel.IsRunning(container);
        var displayName = !string.IsNullOrWhiteSpace(container.Configuration.Hostname)
            ? container.Configuration.Hostname!
            : container.Configuration.Id;
        DetailIcon.Foreground = new SolidColorBrush(running ? Colors.LimeGreen : Colors.Gray);
        DetailNameText.Text = displayName;
        DetailStatusText.Text = $"{container.Status} · {container.Configuration.Image.Reference}";

        var busy = _viewModel.IsSelectedBusy;
        StartButton.IsEnabled = !running && !busy;
        StopButton.IsEnabled = running && !busy;
        ForceStopButton.IsEnabled = running && !busy;
        TerminalButton.IsEnabled = running && !busy;
        RemoveButton.IsEnabled = !busy;

        DetailContent.Children.Clear();

        // Resources - allocation plus the live sample StatsService is already sampling in
        // the background (Activate() runs once at app launch), not a page-local poller.
        var resources = new StackPanel { Spacing = 10 };
        resources.Children.Add(InfoRow("CPU Cores", container.Configuration.Resources.Cpus.ToString()));
        resources.Children.Add(InfoRow("Memory Limit", ByteFormat.Memory(container.Configuration.Resources.MemoryInBytes)));

        var statsRow = new Grid { ColumnSpacing = 16 };
        statsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cpuStack = new StackPanel { Spacing = 4 };
        cpuStack.Children.Add(Muted($"CPU {_viewModel.SelectedCpuPercentText}"));
        cpuStack.Children.Add(new Controls.Sparkline { Values = _viewModel.SelectedCpuHistory, Height = 28, StrokeColor = Colors.DodgerBlue });
        Grid.SetColumn(cpuStack, 0);
        statsRow.Children.Add(cpuStack);

        var memStack = new StackPanel { Spacing = 4 };
        memStack.Children.Add(Muted($"Memory {_viewModel.SelectedMemoryPercentText}"));
        memStack.Children.Add(new Controls.Sparkline { Values = _viewModel.SelectedMemoryHistory, Height = 28, StrokeColor = Colors.MediumPurple });
        Grid.SetColumn(memStack, 1);
        statsRow.Children.Add(memStack);

        resources.Children.Add(statsRow);
        DetailContent.Children.Add(SectionHeader("Resources"));
        DetailContent.Children.Add(Well(resources));

        // Overview
        var overview = new StackPanel { Spacing = 6 };
        overview.Children.Add(InfoRow("Container ID", container.Configuration.Id));
        overview.Children.Add(InfoRow("Runtime", container.Configuration.RuntimeHandler));
        overview.Children.Add(InfoRow("Platform", $"{container.Configuration.Platform.Os}/{container.Configuration.Platform.Architecture}"));
        if (!string.IsNullOrEmpty(container.Configuration.Hostname))
        {
            overview.Children.Add(InfoRow("Hostname", container.Configuration.Hostname!));
        }
        DetailContent.Children.Add(SectionHeader("Overview"));
        DetailContent.Children.Add(Well(overview));

        // Image
        var image = new StackPanel { Spacing = 6 };
        image.Children.Add(InfoRow("Reference", container.Configuration.Image.Reference));
        image.Children.Add(InfoRow("Media Type", container.Configuration.Image.Descriptor.MediaType));
        var digest = container.Configuration.Image.Descriptor.Digest.Replace("sha256:", "");
        image.Children.Add(InfoRow("Digest", digest.Length > 12 ? digest[..12] : digest));
        image.Children.Add(InfoRow("Size", ByteFormat.String(container.Configuration.Image.Descriptor.Size)));
        DetailContent.Children.Add(SectionHeader("Image"));
        DetailContent.Children.Add(Well(image));

        // Network + published ports + DNS
        var network = new StackPanel { Spacing = 6 };
        if (container.Networks.Count > 0)
        {
            foreach (var attachment in container.Networks)
            {
                var hostname = attachment.Hostname.EndsWith('.') ? attachment.Hostname[..^1] : attachment.Hostname;
                network.Children.Add(InfoRow("Hostname", hostname));
                network.Children.Add(InfoRow("Address", attachment.Address.StrippingCidrSuffix()));
                network.Children.Add(InfoRow("Gateway", attachment.Gateway));
                network.Children.Add(InfoRow("Network", attachment.Network));
            }
        }
        else
        {
            network.Children.Add(Muted("No network attachments"));
        }
        if (!string.IsNullOrEmpty(container.Configuration.Dns?.Domain))
        {
            network.Children.Add(InfoRow("DNS Domain", container.Configuration.Dns!.Domain!));
        }
        DetailContent.Children.Add(SectionHeader("Network"));
        DetailContent.Children.Add(Well(network));

        var ports = new StackPanel { Spacing = 6 };
        if (container.Configuration.PublishedPorts.Count > 0)
        {
            foreach (var port in container.Configuration.PublishedPorts)
            {
                var spec = !string.IsNullOrEmpty(port.HostAddress)
                    ? $"{port.HostAddress}:{port.HostPort} -> {port.ContainerPort}/{port.TransportProtocol}"
                    : $"{port.HostPort} -> {port.ContainerPort}/{port.TransportProtocol}";
                ports.Children.Add(Muted(spec));
            }
        }
        else
        {
            ports.Children.Add(Muted("No published ports"));
        }
        DetailContent.Children.Add(SectionHeader("Published Ports"));
        DetailContent.Children.Add(Well(ports));

        // Mounts
        var mounts = new StackPanel { Spacing = 6 };
        if (container.Configuration.Mounts.Count > 0)
        {
            foreach (var mount in container.Configuration.Mounts)
            {
                mounts.Children.Add(InfoRow(mount.Type, $"{mount.Source} -> {mount.Destination}"));
            }
        }
        else
        {
            mounts.Children.Add(Muted("No mounts"));
        }
        DetailContent.Children.Add(SectionHeader("Mounts"));
        DetailContent.Children.Add(Well(mounts));

        // Environment
        var env = new StackPanel { Spacing = 6 };
        if (container.Configuration.InitProcess.Environment.Count > 0)
        {
            foreach (var entry in container.Configuration.InitProcess.Environment)
            {
                env.Children.Add(Muted(entry));
            }
        }
        else
        {
            env.Children.Add(Muted("No environment variables"));
        }
        DetailContent.Children.Add(SectionHeader("Environment"));
        DetailContent.Children.Add(Well(env));

        // Labels
        var labels = new StackPanel { Spacing = 6 };
        if (container.Configuration.Labels.Count > 0)
        {
            foreach (var pair in container.Configuration.Labels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                labels.Children.Add(InfoRow(pair.Key, pair.Value));
            }
        }
        else
        {
            labels.Children.Add(Muted("No labels"));
        }
        DetailContent.Children.Add(SectionHeader("Labels"));
        DetailContent.Children.Add(Well(labels));
    }

    private static TextBlock SectionHeader(string title) => new()
    {
        Text = title,
        FontSize = 14,
        Opacity = 0.9,
        Margin = new Thickness(0, 4, 0, -6),
    };

    /// A subtle theme-agnostic card background - avoids depending on a specific
    /// ThemeResource key resolving correctly when looked up outside a XAML markup context.
    private static Border Well(Panel content) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(18, 128, 128, 128)),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12),
        Child = content,
    };

    private static UIElement InfoRow(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelBlock = new TextBlock { Text = label, Opacity = 0.65, FontSize = 12 };
        Grid.SetColumn(labelBlock, 0);

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        };
        Grid.SetColumn(valueBlock, 1);

        grid.Children.Add(labelBlock);
        grid.Children.Add(valueBlock);
        return grid;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Opacity = 0.65,
        FontSize = 12,
        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        TextWrapping = TextWrapping.Wrap,
    };
}
