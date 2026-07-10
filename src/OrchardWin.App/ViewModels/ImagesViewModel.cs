using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the images list: the backing <see cref="ContainerImage"/> plus the derived
/// display strings the row template binds to. Thin glue, same shape as Dashboard's
/// UtilisationRow - recomputed from AppServices.ImageService/ContainerListService on every
/// relevant change, not a re-implementation of what those services already track.
public sealed record ImageRow(
    ContainerImage Image,
    string Name,
    string Tag,
    string SizeText,
    bool IsInUseByRunning,
    string Reference)
{
    // x:Bind needs a Color-typed value to feed ListItemRow.IconColor directly - no converter
    // is registered anywhere in this project, so expose the already-resolved color here
    // rather than binding the raw bool.
    public Color IconColor => IsInUseByRunning ? Colors.Green : Colors.Gray;
}

public sealed partial class ImagesViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private ObservableCollection<ImageRow> _rows = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ImageSortOption _sortBy = ImageSortOption.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private ImageRow? _selectedRow;

    [ObservableProperty]
    private ImageInspection? _inspection;

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    private string? _inspectionError;

    /// Whether any running container is currently using SelectedRow's image - drives the
    /// detail pane's "Delete" enablement, mirroring ImageDetailHeader.swift's
    /// containersUsingImage check.
    [ObservableProperty]
    private bool _selectedImageInUse;

    public ImagesViewModel(AppServices services)
    {
        _services = services;

        // ImageService/ContainerListService are already ObservableObject and self-refresh on
        // their own polls - rebuild the derived row list whenever either publishes a change,
        // rather than this ViewModel polling on its own.
        _services.ImageService.PropertyChanged += (_, e) =>
        {
            // Include IsImagesLoading: LoadAsync may leave Images unchanged (equality short-
            // circuit), so without this a re-navigated page would never rebuild Rows.
            if (e.PropertyName is null
                or nameof(Core.Services.ImageService.Images)
                or nameof(Core.Services.ImageService.IsImagesLoading))
                Refresh();
        };
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(Core.Services.ContainerListService.Containers))
                Refresh();
        };

        // Critical: hydrate from any already-loaded service state. Frame navigation constructs
        // a new ViewModel each visit; if LoadAsync finds unchanged images it won't re-raise
        // Images PropertyChanged, and Rows would stay empty (blank page after tab switch).
        Refresh();
    }

    public async Task LoadAsync()
    {
        await _services.ImageService.LoadAsync(showLoading: true);
        // Always re-project after load — service may short-circuit when the list is unchanged.
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSortByChanged(ImageSortOption value) => Refresh();
    partial void OnSortAscendingChanged(bool value) => Refresh();

    partial void OnSelectedRowChanged(ImageRow? value)
    {
        UpdateSelectedImageInUse();
        _ = InspectSelectedAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await _services.ImageService.LoadAsync(showLoading: true);
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteAsync(ImageRow? row)
    {
        row ??= SelectedRow;
        if (row is null) return;

        await _services.ImageService.DeleteAsync(row.Reference);

        if (SelectedRow is not null && SelectedRow.Reference == row.Reference)
        {
            SelectedRow = null;
            Inspection = null;
        }
    }

    private async Task InspectSelectedAsync()
    {
        var row = SelectedRow;
        if (row is null)
        {
            Inspection = null;
            InspectionError = null;
            return;
        }

        IsInspecting = true;
        InspectionError = null;
        try
        {
            // Unlike LoadAsync/PullAsync/DeleteAsync, ImageService.InspectAsync passes the
            // backend call straight through without a try/catch of its own (it never routes
            // through AlertCenter) - so this is the one place in this ViewModel that must
            // catch failures itself.
            Inspection = await _services.ImageService.InspectAsync(row.Reference);
        }
        catch (Exception ex)
        {
            Inspection = null;
            InspectionError = ex.Message;
        }
        finally
        {
            IsInspecting = false;
        }
    }

    private void UpdateSelectedImageInUse()
    {
        var row = SelectedRow;
        SelectedImageInUse = row is not null && _services.ContainerListService.Containers
            .Any(c => c.Configuration.Image.Reference == row.Reference);
    }

    private void Refresh()
    {
        var containers = _services.ContainerListService.Containers;
        IEnumerable<ContainerImage> filtered = _services.ImageService.Images;

        if (!string.IsNullOrEmpty(SearchText))
        {
            filtered = filtered.Where(i => i.Reference.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered.Select(image =>
        {
            var (name, tag) = ParseReference(image.Reference);
            var inUseByRunning = containers.Any(c =>
                c.Configuration.Image.Reference == image.Reference &&
                string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase));
            return new ImageRow(image, name, tag, ByteFormat.String(image.Descriptor.Size), inUseByRunning, image.Reference);
        }).ToList();

        rows = SortBy switch
        {
            ImageSortOption.Name => SortAscending
                ? rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            ImageSortOption.Tag => SortAscending
                ? rows.OrderBy(r => r.Tag, StringComparer.OrdinalIgnoreCase).ToList()
                : rows.OrderByDescending(r => r.Tag, StringComparer.OrdinalIgnoreCase).ToList(),
            ImageSortOption.Size => SortAscending
                ? rows.OrderBy(r => r.Image.Descriptor.Size).ToList()
                : rows.OrderByDescending(r => r.Image.Descriptor.Size).ToList(),
            _ => rows,
        };

        var previousSelectedReference = SelectedRow?.Reference;
        Rows = new ObservableCollection<ImageRow>(rows);

        // Re-point selection at the refreshed row with the same reference (if it still
        // exists) so the detail pane survives a background image-list poll instead of being
        // silently cleared out from under the user.
        if (previousSelectedReference is not null)
        {
            SelectedRow = Rows.FirstOrDefault(r => r.Reference == previousSelectedReference);
        }

        UpdateSelectedImageInUse();
    }

    /// Mirrors ListImages.swift's `imageName(from:)`/`imageTag(from:)`: the name is the last
    /// path segment minus any `:tag` suffix; the tag is whatever follows the last `:` in that
    /// same last segment, defaulting to "latest" when there isn't one.
    private static (string Name, string Tag) ParseReference(string reference)
    {
        var lastSegment = reference.Contains('/') ? reference[(reference.LastIndexOf('/') + 1)..] : reference;
        var colonIndex = lastSegment.LastIndexOf(':');
        return colonIndex >= 0
            ? (lastSegment[..colonIndex], lastSegment[(colonIndex + 1)..])
            : (lastSegment, "latest");
    }
}
