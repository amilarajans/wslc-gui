using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Owns image state and operations: listing, inspection, pulling, deletion, and Docker Hub
/// search. Backed by `IContainerBackend` for everything except search, which hits Docker
/// Hub's public API directly over HTTP exactly like the Swift original. Ported 1:1 from
/// Orchard's `ImageService.swift`.
public sealed partial class ImageService : ObservableObject
{
    private readonly IContainerBackend _backend;
    private readonly AlertCenter _alertCenter;

    // Reused across calls - the standard .NET guidance is one long-lived HttpClient per
    // logical endpoint set, not one per request (socket/DNS exhaustion otherwise).
    private static readonly HttpClient HttpClient = new();

    [ObservableProperty]
    private ObservableCollection<ContainerImage> _images = [];

    [ObservableProperty]
    private bool _isImagesLoading;

    // Dictionary<string, ImagePullProgress> in the Swift original. A ConcurrentDictionary
    // (rather than a plain Dictionary behind a lock) because the delayed "clear after 3s"
    // continuation below runs on the thread pool, concurrently with a fresh Pull() call -
    // unlike Swift's @MainActor, nothing here is a single-threaded confinement guarantee.
    private readonly ConcurrentDictionary<string, ImagePullProgress> _pullProgress = new();
    public IReadOnlyDictionary<string, ImagePullProgress> PullProgress => _pullProgress;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private ObservableCollection<RegistrySearchResult> _searchResults = [];

    public ImageService(IContainerBackend backend, AlertCenter alertCenter)
    {
        _backend = backend;
        _alertCenter = alertCenter;
    }

    private static bool ImagesEqual(IReadOnlyList<ContainerImage> old, IReadOnlyList<ContainerImage> updated)
    {
        // ContainerImage/ContainerImageDescriptor carry a Dictionary<string,string>?
        // (Annotations); record-synthesized equality compares that by reference, not
        // value, so a JSON round-trip is the simplest reliable structural comparison.
        if (old.Count != updated.Count) return false;
        return JsonSerializer.Serialize(old) == JsonSerializer.Serialize(updated);
    }

    /// Refresh the image list. Driven by a poll, so failures are logged, not modal -
    /// pull/delete (user actions) alert on their own.
    public async Task LoadAsync(bool showLoading = false, CancellationToken ct = default)
    {
        if (showLoading)
        {
            IsImagesLoading = true;
        }

        try
        {
            var newImages = await _backend.ListImagesAsync(ct);
            // Only republish when the list actually changed - otherwise every poll tick
            // invalidates the whole view tree for nothing.
            if (!ImagesEqual(Images, newImages))
            {
                Images = new ObservableCollection<ContainerImage>(newImages);
            }
            IsImagesLoading = false;
        }
        catch (Exception error)
        {
            _alertCenter.Error(error.Message, showLoading ? AlertSource.User : AlertSource.Background);
            IsImagesLoading = false;
            Log.Containers.Error(error.Message);
        }
    }

    public Task<ImageInspection> InspectAsync(string reference, CancellationToken ct = default) =>
        _backend.InspectImageAsync(reference, ct);

    public async Task PullAsync(string imageName, CancellationToken ct = default)
    {
        var cleanImageName = imageName.Trim();

        SetPullProgress(cleanImageName, new ImagePullProgress
        {
            ImageName = cleanImageName,
            Status = PullStatus.Pulling,
            Progress = 0.0,
            Message = "Pulling image...",
        });

        try
        {
            // The Swift original never wires the backend's per-chunk progress callback
            // either - the XPC pull call is all-or-nothing from this service's point of
            // view - so the optional IProgress<> here is left unused, same as upstream.
            await _backend.PullImageAsync(cleanImageName, ct: ct);

            SetPullProgress(cleanImageName, new ImagePullProgress
            {
                ImageName = cleanImageName,
                Status = PullStatus.Completed,
                Progress = 1.0,
                Message = "Pull completed successfully",
            });
            FireAndForget(LoadAsync(ct: ct));

            // Swift schedules the clear on the main run loop 3s out; a plain delayed
            // continuation achieves the same "leave the completed badge up briefly" effect
            // without a UI dispatcher dependency in this layer.
            _ = Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None)
                .ContinueWith(_ => RemovePullProgress(cleanImageName), TaskScheduler.Default);
        }
        catch (Exception error)
        {
            var errorMsg = error.Message;
            SetPullProgress(cleanImageName, new ImagePullProgress
            {
                ImageName = cleanImageName,
                Status = PullStatus.Failed,
                Progress = 0.0,
                Message = $"Pull failed: {errorMsg}",
            });
            _alertCenter.Error($"Failed to pull image: {errorMsg}");
        }
    }

    public async Task SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query))
        {
            SearchResults = [];
            return;
        }

        IsSearching = true;

        try
        {
            var encodedQuery = Uri.EscapeDataString(query);
            var urlString = $"https://hub.docker.com/v2/search/repositories/?query={encodedQuery}&page_size=25";

            if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url))
            {
                IsSearching = false;
                _alertCenter.Error("Invalid search query");
                return;
            }

            var json = await HttpClient.GetStringAsync(url, ct);
            var results = CliParsers.ParseDockerHubSearch(json);
            SearchResults = new ObservableCollection<RegistrySearchResult>(results);
            IsSearching = false;
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to search images: {error.Message}");
            IsSearching = false;
            SearchResults = [];
        }
    }

    public void ClearSearchResults() => SearchResults = [];

    public async Task DeleteAsync(string imageReference, CancellationToken ct = default)
    {
        _alertCenter.Dismiss();

        try
        {
            await _backend.DeleteImageAsync(imageReference, ct);
            Images = new ObservableCollection<ContainerImage>(Images.Where(i => i.Reference != imageReference));
            FireAndForget(LoadAsync(ct: ct));
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to delete image: {error.Message}");
        }
    }

    private void SetPullProgress(string key, ImagePullProgress progress)
    {
        _pullProgress[key] = progress;
        OnPropertyChanged(nameof(PullProgress));
    }

    private void RemovePullProgress(string key)
    {
        if (_pullProgress.TryRemove(key, out _))
        {
            OnPropertyChanged(nameof(PullProgress));
        }
    }

    /// See ContainerListService's identical helper for the rationale.
    private static void FireAndForget(Task task)
    {
        _ = task.ContinueWith(
            static t => Log.Containers.Error($"Background task failed: {t.Exception?.GetBaseException().Message}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
