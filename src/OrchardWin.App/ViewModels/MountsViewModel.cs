using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// One row in the mounts list (Orchard ListMounts / Mounts.png).
public sealed record MountRow(
    ContainerMount Mount,
    bool UsedByRunningContainer)
{
    public string Id => Mount.Id;
    public string Destination => Mount.Mount.Destination;
    public string Source => Mount.Mount.Source;
    public string MountType => Mount.MountType;
    public string OptionsString => Mount.OptionsString;
    public int ContainerCount => Mount.ContainerIds.Count;

    /// Secondary list line; empty source shows blank like Orchard tmpfs rows.
    public string SourceDisplay => Source ?? "";

    /// Orchard MountDetailHeader: last path component of source (e.g. "/" for tmpfs root).
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Source))
            {
                // tmpfs and similar often have empty source; fall back to destination leaf.
                return PathLeaf(Destination);
            }
            return PathLeaf(Source);
        }
    }

    public Visibility RunningIconVisibility =>
        UsedByRunningContainer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IdleIconVisibility =>
        UsedByRunningContainer ? Visibility.Collapsed : Visibility.Visible;

    private static string PathLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        // Preserve Unix root.
        if (path is "/" or "\\") return "/";
        var trimmed = path.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(trimmed)) return "/";
        // Handle both Windows and Unix separators without Uri quirks on empty.
        var slash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}

/// One row in “Used By Containers” (Orchard: Container | IP Address | Hostname).
public sealed record MountUserRow(
    string ContainerId,
    string DisplayName,
    string Hostname,
    string Address,
    bool IsRunning);

/// Thin glue for MountsPage: search, in-use filter, selection, detail fields.
public sealed partial class MountsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public ObservableCollection<MountRow> Rows { get; } = [];
    public ObservableCollection<MountRow> FilteredRows { get; } = [];
    public ObservableCollection<MountUserRow> UsersOnSelected { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showOnlyInUse;

    [ObservableProperty]
    private MountRow? _selectedRow;

    public MountsViewModel(AppServices services)
    {
        _services = services;
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(ContainerListService.Containers)
                or nameof(ContainerListService.AllMounts)
                or nameof(ContainerListService.IsContainersLoading))
                Refresh();
        };
        Refresh();
    }

    public AppServices Services => _services;
    public bool IsLoading => _services.ContainerListService.IsContainersLoading;

    public string SelectedSource => SelectedRow?.Source ?? "";
    public string SelectedDestination => SelectedRow?.Destination ?? "";
    public string SelectedType => SelectedRow?.MountType ?? "";
    public string SelectedOptions => SelectedRow?.OptionsString ?? "";
    public string SelectedContainerCount =>
        SelectedRow is null ? "0" : SelectedRow.ContainerCount.ToString();
    public string SelectedFilesystem => MapFilesystem(SelectedRow?.MountType);
    public bool HasSelectedOptions => !string.IsNullOrWhiteSpace(SelectedOptions);
    public bool CanOpenSource =>
        !string.IsNullOrWhiteSpace(SelectedSource)
        && (Directory.Exists(SelectedSource) || File.Exists(SelectedSource));

    public async Task LoadAsync()
    {
        await _services.ContainerListService.LoadAsync(showLoading: false);
        Refresh();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnShowOnlyInUseChanged(bool value) => ApplyFilter();

    partial void OnSelectedRowChanged(MountRow? value)
    {
        RebuildUsersOnSelected();
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(SelectedDestination));
        OnPropertyChanged(nameof(SelectedType));
        OnPropertyChanged(nameof(SelectedOptions));
        OnPropertyChanged(nameof(SelectedContainerCount));
        OnPropertyChanged(nameof(SelectedFilesystem));
        OnPropertyChanged(nameof(HasSelectedOptions));
        OnPropertyChanged(nameof(CanOpenSource));
    }

    private void Refresh()
    {
        var containers = _services.ContainerListService.Containers;
        var runningIds = new HashSet<string>(
            containers
                .Where(c => string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Configuration.Id),
            StringComparer.OrdinalIgnoreCase);

        // List order: destination primary (Orchard sorts by source in Core; UI shows dest first).
        var rows = _services.ContainerListService.AllMounts
            .Select(m => new MountRow(
                m,
                m.ContainerIds.Any(id => runningIds.Contains(id))))
            .OrderBy(r => r.Destination, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ObservableCollectionSync.Sync(Rows, rows, (a, b) =>
                string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                && a.UsedByRunningContainer == b.UsedByRunningContainer
                && a.ContainerCount == b.ContainerCount
                && string.Equals(a.Source, b.Source, StringComparison.Ordinal)
                && string.Equals(a.Destination, b.Destination, StringComparison.Ordinal)
                && string.Equals(a.MountType, b.MountType, StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(Rows));
        }

        ApplyFilter();

        if (SelectedRow is not null)
        {
            var match = FilteredRows.FirstOrDefault(r =>
                string.Equals(r.Id, SelectedRow.Id, StringComparison.Ordinal));
            SelectedRow = match ?? FilteredRows.FirstOrDefault();
        }
        else if (FilteredRows.Count > 0)
        {
            SelectedRow = FilteredRows[0];
        }

        OnPropertyChanged(nameof(IsLoading));
    }

    private void ApplyFilter()
    {
        IEnumerable<MountRow> q = Rows;

        if (ShowOnlyInUse)
            q = q.Where(r => r.UsedByRunningContainer);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            q = q.Where(r =>
                r.Source.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.Destination.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.MountType.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var list = q.ToList();
        if (ObservableCollectionSync.Sync(FilteredRows, list, (a, b) =>
                string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                && a.UsedByRunningContainer == b.UsedByRunningContainer
                && a.ContainerCount == b.ContainerCount))
        {
            OnPropertyChanged(nameof(FilteredRows));
        }

        if (SelectedRow is not null
            && !FilteredRows.Any(r => string.Equals(r.Id, SelectedRow.Id, StringComparison.Ordinal)))
        {
            SelectedRow = FilteredRows.FirstOrDefault();
        }
        else if (SelectedRow is null && FilteredRows.Count > 0)
        {
            SelectedRow = FilteredRows[0];
        }
    }

    private void RebuildUsersOnSelected()
    {
        var users = new List<MountUserRow>();
        if (SelectedRow is not null)
        {
            var idSet = new HashSet<string>(SelectedRow.Mount.ContainerIds, StringComparer.OrdinalIgnoreCase);
            foreach (var c in _services.ContainerListService.Containers)
            {
                if (!idSet.Contains(c.Configuration.Id)) continue;
                var running = string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase);
                var addr = c.Networks.Count > 0
                    ? c.Networks[0].Address.StrippingCidrSuffix()
                    : "No network";
                var hostname = string.IsNullOrWhiteSpace(c.Configuration.Hostname)
                    ? c.Configuration.Id
                    : c.Configuration.Hostname!;
                // Prefer attachment hostname when present (Orchard shows network hostname).
                if (c.Networks.Count > 0 && !string.IsNullOrWhiteSpace(c.Networks[0].Hostname))
                    hostname = c.Networks[0].Hostname;
                var name = string.IsNullOrWhiteSpace(c.Configuration.Hostname)
                    ? c.Configuration.Id
                    : c.Configuration.Hostname!;
                users.Add(new MountUserRow(
                    c.Configuration.Id,
                    name,
                    hostname,
                    addr,
                    running));
            }
        }

        ObservableCollectionSync.Sync(UsersOnSelected, users, (a, b) =>
            string.Equals(a.ContainerId, b.ContainerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Hostname, b.Hostname, StringComparison.Ordinal)
            && string.Equals(a.Address, b.Address, StringComparison.Ordinal)
            && a.IsRunning == b.IsRunning);
        OnPropertyChanged(nameof(UsersOnSelected));
    }

    /// Orchard Technical Details filesystem label.
    private static string MapFilesystem(string? type) => type?.ToLowerInvariant() switch
    {
        "bind" => "bind",
        "volume" => "volume",
        "tmpfs" => "tmpfs",
        "virtiofs" => "VirtioFS",
        null or "" => "Unknown",
        _ => type!,
    };
}
