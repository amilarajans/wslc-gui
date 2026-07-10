using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.System;

namespace OrchardWin.App.Views;

/// Flattened, x:Bind-friendly view of one Docker Hub <see cref="RegistrySearchResult"/> plus
/// this dialog's live pull/pulled state for it - ImageService itself only tracks pull state
/// keyed by image name (PullProgress) and the pulled-images list (Images), so this wrapper is
/// rebuilt every time either of those changes, mirroring SearchResultRow's computed
/// isPulling/isAlreadyPulled in ImageSearch.swift.
public sealed record SearchResultRow(RegistrySearchResult Result, bool IsPulling, bool IsAlreadyPulled)
{
    public string Name => Result.Name;
    public string DisplayName => Result.DisplayName;
    public string? Description => Result.Description;

    public string MetaText => (Result.IsOfficial, Result.StarCount) switch
    {
        (true, > 0) => $"Official · {Result.StarCount} stars",
        (true, _) => "Official",
        (false, > 0) => $"{Result.StarCount} stars",
        _ => "",
    };

    public string BadgeGlyph => Result.IsOfficial ? "" : "";
    // Brush (not Color): FontIcon.Foreground is Brush; WASDK 1.8+ x:Bind rejects Color→Brush.
    public Brush BadgeColor => new SolidColorBrush(
        Result.IsOfficial ? Colors.DodgerBlue : Colors.Gray);
    public bool ShowPullButton => !IsPulling && !IsAlreadyPulled;
}

/// Flattened view of one in-flight <see cref="ImagePullProgress"/> for the Active Downloads
/// list - same rationale as SearchResultRow above.
public sealed record PullProgressDisplay(ImagePullProgress Progress)
{
    public string ImageName => Progress.ImageName;
    public string Message => Progress.Message;
    public bool IsPulling => Progress.Status == PullStatus.Pulling;
    public double ProgressPercent => Progress.Progress * 100;

    // Brush (not Color): FontIcon.Foreground is Brush; WASDK 1.8+ x:Bind rejects Color→Brush.
    public Brush StatusColor => new SolidColorBrush(Progress.Status switch
    {
        PullStatus.Pulling => Colors.DodgerBlue,
        PullStatus.Completed => Colors.Green,
        PullStatus.Failed => Colors.Red,
        _ => Colors.Gray,
    });

    public string StatusGlyph => Progress.Status switch
    {
        PullStatus.Pulling => "",
        PullStatus.Completed => "",
        PullStatus.Failed => "",
        _ => "",
    };
}

/// Docker Hub image search + pull, ported from ImageSearchView/SearchResultRow/PullProgressRow
/// in ImageSearch.swift. Shown as a modal ContentDialog from ImagesPage rather than a sheet;
/// state lives directly on AppServices.ImageService (already ObservableObject) - this
/// code-behind only holds the debounce/cancellation plumbing and the two display wrappers
/// above, mirroring DashboardPage's "subscribe then re-render everything" code-behind shape
/// rather than introducing a separate ViewModel class for a single dialog.
public sealed partial class ImageSearchDialog : ContentDialog
{
    private readonly AppServices _services;
    private System.Threading.CancellationTokenSource? _searchCts;

    public ImageSearchDialog(AppServices services)
    {
        _services = services;
        InitializeComponent();
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
        // Mirrors ImageSearchView.swift's onDisappear: don't leave stale search results lying
        // around in the shared ImageService for the next time this dialog is opened.
        _services.ImageService.ClearSearchResults();
    }

    private void ImageService_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        // ImageService clears pull progress from a Task.Delay continuation (thread pool),
        // so this can fire off the UI thread - marshal before touching controls.
        DispatcherQueue.RunOnUi(ApplyState);

    private void ApplyState()
    {
        var query = QueryBox.Text;
        var isSearching = _services.ImageService.IsSearching;
        var results = _services.ImageService.SearchResults;

        SearchButton.IsEnabled = !string.IsNullOrWhiteSpace(query) && !isSearching;
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
            ResultsGrid.ItemsSource = results
                .Take(12)
                .Select(r => new SearchResultRow(
                    r,
                    IsPulling: pullProgress.ContainsKey(r.Name),
                    IsAlreadyPulled: images.Any(i => i.Reference.Contains(r.DisplayName))))
                .ToList();
        }
        else if (!string.IsNullOrEmpty(query))
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
        NoResultsText.Visibility = noResults ? Visibility.Visible : Visibility.Collapsed;
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
        // Fire-and-forget from a UI event handler, per this project's async-void convention -
        // ImageService.PullAsync already alerts on failure via AlertCenter and raises
        // PropertyChanged for PullProgress/Images as it progresses, which ApplyState picks up.
        _ = _services.ImageService.PullAsync(name);
    }

    /// Cancel-then-debounce, mirroring ImageSearchView.swift's performSearch(): every call
    /// cancels whatever search is still in flight, waits 300ms, then searches - so rapid
    /// Enter/Search-button/quick-search clicks only ever let the last one through.
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
        _ = DebouncedSearchAsync(query, cts.Token);
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
