using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.Views;

/// Orchard-style Networks: list (search / +) + detail (properties + containers table).
public sealed partial class NetworksPage : Page
{
    private NetworksViewModel? _viewModel;
    private AppServices? _services;
    private DispatcherTimer? _refreshTimer;
    private bool _isSyncingSelection;
    private bool _listBound;

    private static readonly Color AccentLink = Color.FromArgb(255, 64, 156, 255);

    public NetworksPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new NetworksViewModel(_services);
        NetworksList.ItemsSource = _viewModel.FilteredRows;
        _listBound = true;
        _viewModel.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName is null
                or nameof(NetworksViewModel.FilteredRows)
                or nameof(NetworksViewModel.Rows)
                or nameof(NetworksViewModel.SelectedRow)
                or nameof(NetworksViewModel.IsNetworksLoading)
                or nameof(NetworksViewModel.UsersOnSelected)
                or nameof(NetworksViewModel.SelectedLabels)
                or nameof(NetworksViewModel.CanDeleteSelected))
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || _isSyncingSelection) return;
        _viewModel.SelectedRow = NetworksList.SelectedItem as NetworkRow;
    }

    private async void OnAddNetworkClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;
        var dialog = new AddNetworkDialog(_services) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (_viewModel is not null) await _viewModel.LoadAsync();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.SelectedRow is not { } row) return;
        await DeleteNetworkAsync(row);
    }

    private async void OnDeleteFromMenuClick(object sender, RoutedEventArgs e)
    {
        if (NetworksList.SelectedItem is NetworkRow row)
            await DeleteNetworkAsync(row);
        else if (_viewModel?.SelectedRow is { } sel)
            await DeleteNetworkAsync(sel);
    }

    private async Task DeleteNetworkAsync(NetworkRow row)
    {
        if (_viewModel is null) return;
        if (!row.CanDelete)
        {
            var why = NetworkRow.IsDefaultNetwork(row.Id)
                ? "The default/bridge network cannot be deleted."
                : $"'{row.Id}' still has {row.ConnectedContainerCount} attached container(s).";
            var info = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Cannot Delete Network",
                Content = why,
                CloseButtonText = "OK",
            };
            await info.ShowAsync();
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete Network",
            Content = $"Are you sure you want to delete '{row.Id}'?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        await _viewModel.DeleteAsync(row.Id);
        await _viewModel.LoadAsync();
    }

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        var isLoading = _viewModel.IsNetworksLoading && _viewModel.Rows.Count == 0;
        var isEmpty = !isLoading && _viewModel.FilteredRows.Count == 0;

        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        NetworksList.Visibility = !isLoading && !isEmpty ? Visibility.Visible : Visibility.Collapsed;

        _isSyncingSelection = true;
        try
        {
            if (!_listBound || !ReferenceEquals(NetworksList.ItemsSource, _viewModel.FilteredRows))
            {
                NetworksList.ItemsSource = _viewModel.FilteredRows;
                _listBound = true;
            }

            if (!ReferenceEquals(NetworksList.SelectedItem, _viewModel.SelectedRow))
                NetworksList.SelectedItem = _viewModel.SelectedRow;
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

        DetailNameText.Text = row.Id;
        NetworkIdText.Text = row.Id;
        AddressRangeText.Text = row.AddressText;
        GatewayText.Text = row.GatewayText;
        DeleteButton.IsEnabled = row.CanDelete;
        DeleteButton.Opacity = row.CanDelete ? 1.0 : 0.45;

        // Labels chips
        LabelsHost.Children.Clear();
        if (_viewModel.SelectedLabels.Count == 0)
        {
            LabelsHost.Children.Add(new TextBlock { Text = "None", Opacity = 0.5, FontSize = 13 });
        }
        else
        {
            foreach (var kv in _viewModel.SelectedLabels)
            {
                var chip = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(40, 59, 130, 246)),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 4, 8, 4),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(kv.Value) ? kv.Key : $"{kv.Key}: {kv.Value}",
                        FontSize = 11,
                        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
                    },
                };
                LabelsHost.Children.Add(chip);
            }
        }

        BuildUsersTable();
    }

    private void BuildUsersTable()
    {
        UsersHost.Children.Clear();
        if (_viewModel is null) return;

        var users = _viewModel.UsersOnSelected;
        UsersEmptyText.Visibility = users.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UsersHeader.Visibility = users.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        foreach (var u in users)
        {
            var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(4, 8, 4, 8) };
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
                Foreground = u.Address is "N/A" or ""
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

            Grid.SetColumn(addr, 1);
            Grid.SetColumn(host, 2);
            grid.Children.Add(nameBtn);
            grid.Children.Add(addr);
            grid.Children.Add(host);
            UsersHost.Children.Add(grid);

            UsersHost.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)),
                Margin = new Thickness(4, 0, 4, 0),
            });
        }
    }

    private static UIElement BuildContainerLinkContent(NetworkUserRow u)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon
        {
            Glyph = "\uE7B8",
            FontSize = 12,
            Foreground = new SolidColorBrush(u.IsRunning
                ? Color.FromArgb(255, 107, 203, 119)
                : Color.FromArgb(255, 128, 128, 128)),
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

    private void OnUserContainerClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrEmpty(id)) return;
        App.MainWindow.NavigateTo("containers", selectContainerId: id);
    }
}
