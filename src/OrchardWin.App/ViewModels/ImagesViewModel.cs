using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// One row in the images list. Icon colour is theme-brush Visibility toggles (green when a
/// running container uses the image), matching Orchard ListImages / ListItemRow.
public sealed record ImageRow(
    ContainerImage Image,
    string Name,
    string Tag,
    string SizeText,
    bool IsInUseByRunning,
    string Reference)
{
    public Visibility RunningIconVisibility => IsInUseByRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IdleIconVisibility => IsInUseByRunning ? Visibility.Collapsed : Visibility.Visible;
}

/// One row in the "Used By Containers" table on the image detail pane.
public sealed record ImageUserRow(
    string ContainerId,
    string DisplayName,
    string Address,
    string Hostname,
    bool IsRunning);

/// Thin glue for ImagesPage over ImageService + ContainerListService. Mirrors Orchard's
/// ListImages / ImageDetail (Overview, Technical Details, Configuration, Used By Containers).
public sealed partial class ImagesViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// Mutated in place so ListView.ItemsSource stays stable across polls.
    public ObservableCollection<ImageRow> Rows { get; } = [];

    public ObservableCollection<ImageUserRow> ContainersUsingSelected { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private ImageSortOption _sortBy = ImageSortOption.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    [ObservableProperty]
    private bool _showOnlyInUse;

    [ObservableProperty]
    private ImageRow? _selectedRow;

    [ObservableProperty]
    private ImageInspection? _inspection;

    [ObservableProperty]
    private bool _isInspecting;

    [ObservableProperty]
    private string? _inspectionError;

    /// Whether any container (running or not) uses SelectedRow — drives Delete enablement.
    [ObservableProperty]
    private bool _selectedImageInUse;

    public ImagesViewModel(AppServices services)
    {
        _services = services;

        _services.ImageService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(ImageService.Images)
                or nameof(ImageService.IsImagesLoading))
                Refresh();
        };
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers))
            {
                Refresh();
                RebuildContainersUsingSelected();
            }
        };

        // Hydrate from already-loaded service state (tab re-entry).
        Refresh();
    }

    public AppServices Services => _services;

    public bool IsImagesLoading => _services.ImageService.IsImagesLoading;

    public async Task LoadAsync()
    {
        await _services.ImageService.LoadAsync(showLoading: true);
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSortByChanged(ImageSortOption value) => Refresh();
    partial void OnSortAscendingChanged(bool value) => Refresh();
    partial void OnShowOnlyInUseChanged(bool value) => Refresh();

    partial void OnSelectedRowChanged(ImageRow? value)
    {
        UpdateSelectedImageInUse();
        RebuildContainersUsingSelected();
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
            .Any(c => string.Equals(c.Configuration.Image.Reference, row.Reference, StringComparison.OrdinalIgnoreCase)
                      || ReferencesMatch(c.Configuration.Image.Reference, row.Reference));
    }

    private void RebuildContainersUsingSelected()
    {
        ContainersUsingSelected.Clear();
        var row = SelectedRow;
        if (row is null) return;

        foreach (var c in _services.ContainerListService.Containers)
        {
            if (!ReferencesMatch(c.Configuration.Image.Reference, row.Reference)
                && !string.Equals(c.Configuration.Image.Reference, row.Reference, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = !string.IsNullOrWhiteSpace(c.Configuration.Hostname)
                ? c.Configuration.Hostname!
                : (c.Configuration.Id.Length > 12 ? c.Configuration.Id[..12] : c.Configuration.Id);
            var address = c.Networks.Count > 0
                ? c.Networks[0].Address.StrippingCidrSuffix()
                : "N/A";
            var hostname = c.Networks.Count > 0
                ? (c.Networks[0].Hostname.EndsWith('.')
                    ? c.Networks[0].Hostname[..^1]
                    : c.Networks[0].Hostname)
                : "N/A";
            if (string.IsNullOrEmpty(hostname)) hostname = "N/A";
            if (string.IsNullOrEmpty(address)) address = "N/A";

            ContainersUsingSelected.Add(new ImageUserRow(
                c.Configuration.Id,
                name,
                address,
                string.IsNullOrEmpty(hostname) ? "N/A" : hostname,
                string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)));
        }
    }

    private void Refresh()
    {
        var containers = _services.ContainerListService.Containers;
        IEnumerable<ContainerImage> filtered = _services.ImageService.Images;

        if (ShowOnlyInUse)
        {
            filtered = filtered.Where(image =>
                containers.Any(c => ReferencesMatch(c.Configuration.Image.Reference, image.Reference)));
        }

        if (!string.IsNullOrEmpty(SearchText))
        {
            filtered = filtered.Where(i => i.Reference.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
        }

        var rows = filtered.Select(image =>
        {
            var (name, tag) = ParseReference(image.Reference);
            var inUseByRunning = containers.Any(c =>
                ReferencesMatch(c.Configuration.Image.Reference, image.Reference) &&
                string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase));
            return new ImageRow(
                image, name, tag,
                ByteFormat.String(image.Descriptor.Size),
                inUseByRunning,
                image.Reference);
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
        var changed = ObservableCollectionSync.Sync(Rows, rows, RowEquals);
        if (changed) OnPropertyChanged(nameof(Rows));

        if (previousSelectedReference is not null)
        {
            var match = Rows.FirstOrDefault(r => r.Reference == previousSelectedReference);
            if (!ReferenceEquals(SelectedRow, match))
            {
                // Silent when only the row instance was replaced with same reference.
                if (match is not null
                    && SelectedRow is not null
                    && SelectedRow.Reference == match.Reference
                    && SelectedRow.IsInUseByRunning == match.IsInUseByRunning
                    && SelectedRow.SizeText == match.SizeText)
                {
#pragma warning disable MVVMTK0034
                    _selectedRow = match;
#pragma warning restore MVVMTK0034
                }
                else
                {
                    SelectedRow = match;
                }
            }
        }

        UpdateSelectedImageInUse();
        RebuildContainersUsingSelected();
        OnPropertyChanged(nameof(IsImagesLoading));
    }

    private static bool RowEquals(ImageRow a, ImageRow b) =>
        string.Equals(a.Reference, b.Reference, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && string.Equals(a.Tag, b.Tag, StringComparison.Ordinal)
        && string.Equals(a.SizeText, b.SizeText, StringComparison.Ordinal)
        && a.IsInUseByRunning == b.IsInUseByRunning;

    /// Loose match: alpine:latest vs docker.io/library/alpine:latest, or digest prefixes.
    private static bool ReferencesMatch(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        var na = NormalizeRef(a);
        var nb = NormalizeRef(b);
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRef(string reference)
    {
        var r = reference
            .Replace("docker.io/library/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("docker.io/", "", StringComparison.OrdinalIgnoreCase);
        return r;
    }

    /// Mirrors ListImages.swift's imageName/imageTag helpers.
    private static (string Name, string Tag) ParseReference(string reference)
    {
        var lastSegment = reference.Contains('/') ? reference[(reference.LastIndexOf('/') + 1)..] : reference;
        // digest-style name@sha256:...
        var at = lastSegment.IndexOf('@');
        if (at >= 0)
            return (lastSegment[..at], lastSegment[(at + 1)..]);

        var colonIndex = lastSegment.LastIndexOf(':');
        return colonIndex >= 0
            ? (lastSegment[..colonIndex], lastSegment[(colonIndex + 1)..])
            : (lastSegment, "latest");
    }
}
