using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OrchardWin.App.ViewModels;
using OrchardWin.Core.Services;

namespace OrchardWin.App.Views;

public sealed partial class DnsPage : Page
{
    private DnsViewModel? _viewModel;
    private AppServices? _services;
    private DispatcherTimer? _refreshTimer;

    public DnsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _services = NavigationArgs.From(e.Parameter).Services;
        _viewModel = new DnsViewModel(_services);
        DomainsList.ItemsSource = _viewModel.Rows;
        _viewModel.PropertyChanged += (_, e) =>
        {
            // Rows are stable; only chrome (loading/empty) needs a pass when counts change.
            if (e.PropertyName is null
                or nameof(DnsViewModel.Rows)
                or nameof(DnsViewModel.IsDnsLoading))
                DispatcherQueue.RunOnUi(ApplyViewModelState);
        };
        ApplyViewModelState();

        _ = _viewModel.LoadAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Mirrors Orchard's `Content.swift` 5s refresh timer for the networks/DNS lists.
        // DnsService here is hosts-file-backed (see its doc comment) - reads are cheap local
        // file I/O, so a plain poll is fine; only the mutating calls (create/delete/set
        // default) pay the elevated-write cost.
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

        var isLoading = _viewModel.IsDnsLoading;
        var isEmpty = !isLoading && _viewModel.Rows.Count == 0;

        LoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        DomainsList.Visibility = !isLoading && !isEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (!ReferenceEquals(DomainsList.ItemsSource, _viewModel.Rows))
            DomainsList.ItemsSource = _viewModel.Rows;
    }

    private async void OnAddDomainClick(object sender, RoutedEventArgs e)
    {
        if (_services is null) return;

        var dialog = new AddDnsDomainDialog(_services) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private async void OnMakeDefaultClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is not MenuFlyoutItem { Tag: string domain }) return;

        await _viewModel.SetDefaultAsync(domain);
    }

    private async void OnDeleteDomainClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null) return;
        if (sender is not MenuFlyoutItem { Tag: string domain }) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete DNS Domain",
            Content = $"Are you sure you want to delete '{domain}'? This requires administrator privileges.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await confirm.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        await _viewModel.DeleteAsync(domain);
    }
}
