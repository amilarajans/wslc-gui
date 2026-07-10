using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

public sealed partial class NetworksPage : Page
{
    private NetworksViewModel? _viewModel;
    private AppServices? _services;
    private DispatcherTimer? _refreshTimer;

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
        _viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);
        ApplyViewModelState();

        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mirrors Orchard's `Content.swift` 5s refresh timer for the networks/DNS lists -
        // NetworkService has no self-refresh loop of its own (unlike ContainerListService's
        // start/stop polling), so the page drives the periodic reload while visible.
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

    private void ApplyViewModelState()
    {
        if (_viewModel is null) return;

        var isLoading = _viewModel.IsNetworksLoading;
        var isEmpty = !isLoading && _viewModel.Rows.Count == 0;

        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        NetworksList.Visibility = !isLoading && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
        NetworksList.ItemsSource = _viewModel.Rows;
    }

    private async void OnAddNetworkClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        var dialog = new AddNetworkDialog(_services) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void OnDeleteNetworkClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is not MenuFlyoutItem { Tag: string networkId }) return;

        var attachedCount = _viewModel.ConnectedContainerCount(networkId);
        if (attachedCount > 0)
        {
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete Network",
                Content = $"'{networkId}' has {attachedCount} attached container{(attachedCount == 1 ? "" : "s")}. " +
                          "Are you sure you want to delete it? This requires administrator privileges.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            var result = await confirm.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
        }

        await _viewModel.DeleteAsync(networkId);
    }
}
