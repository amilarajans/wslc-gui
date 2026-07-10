using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace OrchardWin.App.Views;

/// Orchard Mounts.png layout: list (search / in-use) + detail (Overview / Technical / Used By).
public sealed partial class MountsPage : Page
{
    private MountsViewModel? _viewModel;
    private AppServices? _services;
    private DispatcherTimer? _refreshTimer;
    private bool _isSyncingSelection;
    private bool _listBound;

    private static readonly Color AccentLink = Color.FromArgb(255, 64, 156, 255);
    private static readonly Color RunningGreen = Color.FromArgb(255, 107, 203, 119);
    private static readonly Color IdleGray = Color.FromArgb(255, 128, 128, 128);

    public MountsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new MountsViewModel(_services);
        MountsList.ItemsSource = _viewModel.FilteredRows;
        _listBound = true;
        _viewModel.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName is null
                or nameof(MountsViewModel.FilteredRows)
                or nameof(MountsViewModel.Rows)
                or nameof(MountsViewModel.SelectedRow)
                or nameof(MountsViewModel.IsLoading)
                or nameof(MountsViewModel.UsersOnSelected)
                or nameof(MountsViewModel.CanOpenSource)
                or nameof(MountsViewModel.ShowOnlyInUse))
            {
                DispatcherQueue.RunOnUi(ApplyViewModelState);
            }
        };
        ApplyViewModelState();
        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_viewModel is not null) await _viewModel.LoadAsync();
        };
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer?.Stop();
        _refreshTimer = null;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.SearchText = SearchBox.Text;
    }

    private void OnInUseFilterClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        _viewModel.ShowOnlyInUse = InUseFilterButton.IsChecked == true;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isSyncingSelection) return;
        _viewModel.SelectedRow = MountsList.SelectedItem as MountRow;
    }

    private void OnCopySourceClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_viewModel?.SelectedSource))
            CopyText(_viewModel.SelectedSource);
    }

    private void OnCopyDestinationClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_viewModel?.SelectedDestination))
            CopyText(_viewModel.SelectedDestination);
    }

    private void OnCopyTypeClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_viewModel?.SelectedType))
            CopyText(_viewModel.SelectedType);
    }

    private void OnCopySourceMenuClick(object sender, RoutedEventArgs e)
    {
        var row = MountsList.SelectedItem as MountRow ?? _viewModel?.SelectedRow;
        if (row is null || string.IsNullOrEmpty(row.Source)) return;
        CopyText(row.Source);
    }

    private void OnCopyDestinationMenuClick(object sender, RoutedEventArgs e)
    {
        var row = MountsList.SelectedItem as MountRow ?? _viewModel?.SelectedRow;
        if (row is null || string.IsNullOrEmpty(row.Destination)) return;
        CopyText(row.Destination);
    }

    private void OnOpenSourceClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || string.IsNullOrWhiteSpace(_viewModel.SelectedSource)) return;
        try
        {
            var path = _viewModel.SelectedSource;
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true,
                });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{path}\"",
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // Non-fatal: guest-only paths cannot open in Explorer.
        }
    }

    private void OnUserContainerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        App.MainWindow.NavigateTo("containers", selectContainerId: id);
    }

    private static void CopyText(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        var isLoading = _viewModel.IsLoading && _viewModel.Rows.Count == 0;
        var isEmpty = !isLoading && _viewModel.FilteredRows.Count == 0;

        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        MountsList.Visibility = !isLoading && !isEmpty ? Visibility.Visible : Visibility.Collapsed;

        EmptyHintText.Text = _viewModel.ShowOnlyInUse && _viewModel.Rows.Count > 0
            ? "No mounts are currently used by running containers"
            : "Mounts appear when containers use -v / bind volumes";

        InUseFilterButton.IsChecked = _viewModel.ShowOnlyInUse;

        _isSyncingSelection = true;
        try
        {
            if (!_listBound || !ReferenceEquals(MountsList.ItemsSource, _viewModel.FilteredRows))
            {
                MountsList.ItemsSource = _viewModel.FilteredRows;
                _listBound = true;
            }

            if (!ReferenceEquals(MountsList.SelectedItem, _viewModel.SelectedRow))
                MountsList.SelectedItem = _viewModel.SelectedRow;
        }
        finally
        {
            _isSyncingSelection = false;
        }

        var row = _viewModel.SelectedRow;
        if (row is null)
        {
            EmptyDetail.Visibility = Visibility.Visible;
            DetailRoot.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyDetail.Visibility = Visibility.Collapsed;
        DetailRoot.Visibility = Visibility.Visible;

        // Orchard MountDetailHeader: last path component of source (often "/" for tmpfs).
        DetailNameText.Text = row.DisplayName;

        var hasSource = !string.IsNullOrWhiteSpace(row.Source);
        SourceText.Text = hasSource ? row.Source : "";
        CopySourceButton.Visibility = hasSource ? Visibility.Visible : Visibility.Collapsed;

        DestinationText.Text = row.Destination ?? "";
        CopyDestinationButton.Visibility = string.IsNullOrEmpty(row.Destination)
            ? Visibility.Collapsed
            : Visibility.Visible;

        TypeText.Text = row.MountType;
        CopyTypeButton.Visibility = string.IsNullOrEmpty(row.MountType)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ContainerCountText.Text = row.ContainerCount.ToString();
        FilesystemText.Text = _viewModel.SelectedFilesystem;

        var hasOpts = _viewModel.HasSelectedOptions;
        OptionsRow.Visibility = hasOpts ? Visibility.Visible : Visibility.Collapsed;
        OptionsText.Text = _viewModel.SelectedOptions;

        OpenSourceButton.IsEnabled = _viewModel.CanOpenSource;
        OpenSourceButton.Opacity = _viewModel.CanOpenSource ? 1.0 : 0.45;

        BuildUsersTable();
    }

    private void BuildUsersTable()
    {
        UsersHost.Children.Clear();
        if (_viewModel is null) return;

        var users = _viewModel.UsersOnSelected;
        UsersEmptyText.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsersHeader.Visibility = users.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        for (var i = 0; i < users.Count; i++)
        {
            var u = users[i];
            var grid = new Grid
            {
                ColumnSpacing = 8,
                Padding = new Thickness(12, 10, 12, 10),
                Background = i % 2 == 1
                    ? new SolidColorBrush(Color.FromArgb(18, 255, 255, 255))
                    : null,
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nameBtn = new HyperlinkButton
            {
                Content = BuildContainerLinkContent(u),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = u.ContainerId,
            };
            nameBtn.Click += OnUserContainerClick;

            var addr = new TextBlock
            {
                Text = u.Address,
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                Foreground = u.Address is "No network" or ""
                    ? new SolidColorBrush(Colors.Gray)
                    : new SolidColorBrush(AccentLink),
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = true,
            };
            var host = new TextBlock
            {
                Text = u.Hostname,
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                Foreground = new SolidColorBrush(AccentLink),
                VerticalAlignment = VerticalAlignment.Center,
                IsTextSelectionEnabled = true,
            };

            Grid.SetColumn(nameBtn, 0);
            Grid.SetColumn(addr, 1);
            Grid.SetColumn(host, 2);
            grid.Children.Add(nameBtn);
            grid.Children.Add(addr);
            grid.Children.Add(host);
            UsersHost.Children.Add(grid);
        }
    }

    private static UIElement BuildContainerLinkContent(MountUserRow u)
    {
        // Orchard: green cube when running.
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon
        {
            Glyph = "\uE7B8",
            FontSize = 12,
            Foreground = new SolidColorBrush(u.IsRunning ? RunningGreen : IdleGray),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = u.DisplayName,
            FontSize = 13,
            Foreground = new SolidColorBrush(AccentLink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }
}
