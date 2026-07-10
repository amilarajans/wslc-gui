using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.System;
using Windows.UI;

namespace OrchardWin.App.Views;

/// One Docker Hub hit + live pull/pulled state for the result card (Orchard SearchResultRow).
public sealed record SearchResultRow(RegistrySearchResult Result, bool IsPulling, bool IsAlreadyPulled)
{
    public string Name => Result.Name;
    public string DisplayName => Result.DisplayName;
    public string Description => string.IsNullOrWhiteSpace(Result.Description) ? "" : Result.Description!;

    public Visibility OfficialVisibility =>
        Result.IsOfficial ? Visibility.Visible : Visibility.Collapsed;

    public Visibility StarsVisibility =>
        Result.StarCount is > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string StarsText => Result.StarCount is { } n ? n.ToString("N0") : "";

    /// Official: checkmark seal; community: cube.transparent (Segoe).
    public string BadgeGlyph => Result.IsOfficial ? "\uE73E" : "\uE81E";

    public Brush BadgeColor => new SolidColorBrush(
        Result.IsOfficial ? Color.FromArgb(255, 59, 130, 246) : Color.FromArgb(255, 156, 163, 175));

    public Visibility PullingVisibility => IsPulling ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PullVisibility => !IsPulling && !IsAlreadyPulled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunVisibility => !IsPulling && IsAlreadyPulled ? Visibility.Visible : Visibility.Collapsed;
}

public sealed record PullProgressDisplay(ImagePullProgress Progress)
{
    public string ImageName => Progress.ImageName;
    public string Message => Progress.Message;
    public bool IsPulling => Progress.Status == PullStatus.Pulling;
    public double ProgressPercent => Progress.Progress * 100;
    public Visibility PullingVisibility => IsPulling ? Visibility.Visible : Visibility.Collapsed;

    public Brush StatusColor => new SolidColorBrush(Progress.Status switch
    {
        PullStatus.Pulling => Color.FromArgb(255, 59, 130, 246),
        PullStatus.Completed => Color.FromArgb(255, 22, 163, 74),
        PullStatus.Failed => Color.FromArgb(255, 220, 38, 38),
        _ => Colors.Gray,
    });

    public string StatusGlyph => Progress.Status switch
    {
        PullStatus.Pulling => "\uE896",
        PullStatus.Completed => "\uE73E",
        PullStatus.Failed => "\uE711",
        _ => "\uE946",
    };
}

/// Docker Hub search + pull, layout matching Orchard ImageSearchView screenshots.
public sealed partial class ImageSearchDialog : ContentDialog
{
    private readonly AppServices _services;
    private System.Threading.CancellationTokenSource? _searchCts;

    public ImageSearchDialog(AppServices services)
    {
        _services = services;
        InitializeComponent();
        // Theme defaults clamp ContentDialog to ~548px; force Orchard 920×600 sheet.
        Resources["ContentDialogMaxWidth"] = 980.0;
        Resources["ContentDialogMinWidth"] = 920.0;
        Resources["ContentDialogMaxHeight"] = 680.0;
        Resources["ContentDialogMinHeight"] = 600.0;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _services.ImageService.PropertyChanged += ImageService_PropertyChanged;
        ApplyState();
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _services.ImageService.PropertyChanged -= ImageService_PropertyChanged;
        _searchCts?.Cancel();
        _searchCts = null;
        _services.ImageService.ClearSearchResults();
    }

    private void ImageService_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        DispatcherQueue.RunOnUi(ApplyState);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void ClearQueryButton_Click(object sender, RoutedEventArgs e)
    {
        QueryBox.Text = "";
        _services.ImageService.ClearSearchResults();
        ApplyState();
        QueryBox.Focus(FocusState.Programmatic);
    }

    private void ApplyState()
    {
        var query = QueryBox.Text?.Trim() ?? "";
        var isSearching = _services.ImageService.IsSearching;
        var results = _services.ImageService.SearchResults;

        SearchButton.IsEnabled = query.Length > 0 && !isSearching;
        ClearQueryButton.Visibility = query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchingRing.IsActive = isSearching;

        if (string.IsNullOrEmpty(query) && results.Count == 0)
        {
            SetResultsState(quickSearch: true);
        }
        else if (isSearching)
        {
            SetResultsState(searching: true);
        }
        else if (results.Count > 0)
        {
            SetResultsState(hasResults: true);
            var images = _services.ImageService.Images;
            var pullProgress = _services.ImageService.PullProgress;
            // Orchard: images.contains { $0.reference.contains(result.displayName) }
            ResultsRepeater.ItemsSource = results
                .Take(12)
                .Select(r => new SearchResultRow(
                    r,
                    IsPulling: pullProgress.ContainsKey(r.Name),
                    IsAlreadyPulled: images.Any(i =>
                        i.Reference.Contains(r.DisplayName, StringComparison.OrdinalIgnoreCase))))
                .ToList();
        }
        else if (query.Length > 0)
        {
            SetResultsState(noResults: true);
        }
        else
        {
            SetResultsState(quickSearch: true);
        }

        var pulls = _services.ImageService.PullProgress.Values.ToList();
        ActivePullsPanel.Visibility = pulls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivePullsList.ItemsSource = pulls.Select(p => new PullProgressDisplay(p)).ToList();
    }

    private void SetResultsState(bool quickSearch = false, bool searching = false, bool hasResults = false, bool noResults = false)
    {
        QuickSearchPanel.Visibility = quickSearch ? Visibility.Visible : Visibility.Collapsed;
        SearchingPanel.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        ResultsScroller.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        NoResultsPanel.Visibility = noResults ? Visibility.Visible : Visibility.Collapsed;
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyState();

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        PerformSearch(QueryBox.Text);
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => PerformSearch(QueryBox.Text);

    private void QuickSearch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string query) return;
        QueryBox.Text = query;
        PerformSearch(query);
    }

    private void PullButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        _ = _services.ImageService.PullAsync(name);
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name) return;
        // Prefer a local reference that matches the search hit.
        var local = _services.ImageService.Images
            .Select(i => i.Reference)
            .FirstOrDefault(r =>
                r.Contains(name, StringComparison.OrdinalIgnoreCase)
                || r.Contains(name.Split('/').Last(), StringComparison.OrdinalIgnoreCase));
        var imageRef = local ?? name;

        var run = new RunContainerDialog(_services, imageRef) { XamlRoot = XamlRoot };
        await run.ShowAsync();
    }

    private void PerformSearch(string query)
    {
        _searchCts?.Cancel();

        if (string.IsNullOrWhiteSpace(query))
        {
            _services.ImageService.ClearSearchResults();
            ApplyState();
            return;
        }

        var cts = new System.Threading.CancellationTokenSource();
        _searchCts = cts;
        _ = DebouncedSearchAsync(query.Trim(), cts.Token);
    }

    private async Task DebouncedSearchAsync(string query, System.Threading.CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested) return;
        await _services.ImageService.SearchAsync(query, ct);
    }
}
