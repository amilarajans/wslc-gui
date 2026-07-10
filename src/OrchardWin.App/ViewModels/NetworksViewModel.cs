using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// One row in the networks list (Orchard ListNetworks).
public sealed record NetworkRow(
    ContainerNetwork Network,
    int ConnectedContainerCount,
    bool HasRunningContainers)
{
    public string Id => Network.Id;

    /// Default/bridge cannot be deleted; also disabled when containers are attached.
    public bool CanDelete =>
        !IsDefaultNetwork(Network.Id)
        && ConnectedContainerCount == 0;

    public string AddressText =>
        !string.IsNullOrWhiteSpace(Network.Status.Address) ? Network.Status.Address! : "No address";

    public string GatewayText =>
        !string.IsNullOrWhiteSpace(Network.Status.Gateway) ? Network.Status.Gateway! : "N/A";

    public string ContainerCountText =>
        ConnectedContainerCount == 0
            ? "No containers"
            : $"{ConnectedContainerCount} container{(ConnectedContainerCount == 1 ? "" : "s")}";

    public Visibility RunningIconVisibility =>
        HasRunningContainers ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IdleIconVisibility =>
        HasRunningContainers ? Visibility.Collapsed : Visibility.Visible;

    public static bool IsDefaultNetwork(string name) =>
        string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "bridge", StringComparison.OrdinalIgnoreCase);
}

/// One row in “Containers using this network”.
public sealed record NetworkUserRow(
    string ContainerId,
    string DisplayName,
    string Address,
    string Hostname,
    bool IsRunning);

/// Thin glue for NetworksPage: search, selection, detail fields, users table.
public sealed partial class NetworksViewModel : ObservableObject
{
    private readonly AppServices _services;
    private Dictionary<string, IReadOnlyList<ContainerNetworkAttachment>> _attachmentsByContainer =
        new(StringComparer.OrdinalIgnoreCase);
    private int _refreshGeneration;

    public ObservableCollection<NetworkRow> Rows { get; } = [];
    public ObservableCollection<NetworkRow> FilteredRows { get; } = [];
    public ObservableCollection<NetworkUserRow> UsersOnSelected { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> SelectedLabels { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private NetworkRow? _selectedRow;

    public NetworksViewModel(AppServices services)
    {
        _services = services;
        _services.NetworkService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(NetworkService.Networks)
                or nameof(NetworkService.IsNetworksLoading))
                _ = RefreshAsync();
        };
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers))
                _ = RefreshAsync();
        };
        _ = RefreshAsync();
    }

    public AppServices Services => _services;
    public bool IsNetworksLoading => _services.NetworkService.IsNetworksLoading;

    public string SelectedNetworkId => SelectedRow?.Id ?? "";
    public string SelectedAddressRange => SelectedRow?.AddressText ?? "N/A";
    public string SelectedGateway => SelectedRow?.GatewayText ?? "N/A";
    public bool CanDeleteSelected => SelectedRow?.CanDelete == true;
    public bool HasSelectedLabels => SelectedLabels.Count > 0;

    public async Task LoadAsync()
    {
        await Task.WhenAll(
            _services.NetworkService.LoadAsync(),
            _services.ContainerListService.LoadAsync(showLoading: false));
        await RefreshAsync();
    }

    public Task DeleteAsync(string networkId) => _services.NetworkService.DeleteAsync(networkId);

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedRowChanged(NetworkRow? value)
    {
        RebuildSelectedLabels();
        RebuildUsersOnSelected();
        OnPropertyChanged(nameof(SelectedNetworkId));
        OnPropertyChanged(nameof(SelectedAddressRange));
        OnPropertyChanged(nameof(SelectedGateway));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(HasSelectedLabels));
    }

    private async Task RefreshAsync()
    {
        var gen = Interlocked.Increment(ref _refreshGeneration);
        await RefreshAttachmentsAsync();
        if (gen != _refreshGeneration) return;

        var containers = _services.ContainerListService.Containers;
        var rows = new List<NetworkRow>();
        foreach (var network in _services.NetworkService.Networks)
        {
            var users = ContainersOnNetwork(network.Id, containers);
            var hasRunning = users.Any(c =>
                string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase));
            rows.Add(new NetworkRow(network, users.Count, hasRunning));
        }

        if (ObservableCollectionSync.Sync(Rows, rows, (a, b) =>
                string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                && a.ConnectedContainerCount == b.ConnectedContainerCount
                && a.HasRunningContainers == b.HasRunningContainers
                && string.Equals(a.AddressText, b.AddressText, StringComparison.Ordinal)
                && string.Equals(a.GatewayText, b.GatewayText, StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(Rows));
        }

        ApplyFilter();

        if (SelectedRow is not null)
        {
            var match = FilteredRows.FirstOrDefault(r =>
                string.Equals(r.Id, SelectedRow.Id, StringComparison.OrdinalIgnoreCase));
            SelectedRow = match ?? FilteredRows.FirstOrDefault();
        }
        else if (FilteredRows.Count > 0)
        {
            SelectedRow = FilteredRows[0];
        }

        OnPropertyChanged(nameof(IsNetworksLoading));
    }

    private void ApplyFilter()
    {
        IEnumerable<NetworkRow> q = Rows;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            q = q.Where(r =>
                r.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.AddressText.Contains(term, StringComparison.OrdinalIgnoreCase)
                || r.GatewayText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var list = q.ToList();
        if (ObservableCollectionSync.Sync(FilteredRows, list, (a, b) =>
                string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                && a.ConnectedContainerCount == b.ConnectedContainerCount
                && a.HasRunningContainers == b.HasRunningContainers
                && string.Equals(a.AddressText, b.AddressText, StringComparison.Ordinal)))
        {
            OnPropertyChanged(nameof(FilteredRows));
        }
    }

    private async Task RefreshAttachmentsAsync()
    {
        var containers = _services.ContainerListService.Containers;
        if (containers.Count == 0)
        {
            _attachmentsByContainer = new(StringComparer.OrdinalIgnoreCase);
            return;
        }

        var backend = _services.ContainerBackend;
        var tasks = containers.Select(async c =>
        {
            if (c.Networks.Count > 0)
                return (c.Configuration.Id, (IReadOnlyList<ContainerNetworkAttachment>)c.Networks);
            try
            {
                var atts = await backend.ListContainerNetworkAttachmentsAsync(c.Configuration.Id);
                return (c.Configuration.Id, atts);
            }
            catch
            {
                return (c.Configuration.Id, (IReadOnlyList<ContainerNetworkAttachment>)Array.Empty<ContainerNetworkAttachment>());
            }
        });
        var results = await Task.WhenAll(tasks);
        var map = new Dictionary<string, IReadOnlyList<ContainerNetworkAttachment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, atts) in results)
            map[id] = atts;
        _attachmentsByContainer = map;
    }

    private List<Container> ContainersOnNetwork(string networkId, IReadOnlyList<Container> containers)
    {
        var list = new List<Container>();
        foreach (var c in containers)
        {
            if (GetAttachments(c).Any(a => NetworkNamesMatch(a.Network, networkId)))
                list.Add(c);
        }
        return list;
    }

    private IReadOnlyList<ContainerNetworkAttachment> GetAttachments(Container c)
    {
        if (c.Networks.Count > 0) return c.Networks;
        if (_attachmentsByContainer.TryGetValue(c.Configuration.Id, out var atts))
            return atts;
        return [];
    }

    private static bool NetworkNamesMatch(string attachmentNetwork, string networkId)
    {
        if (string.Equals(attachmentNetwork, networkId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (NetworkRow.IsDefaultNetwork(attachmentNetwork) && NetworkRow.IsDefaultNetwork(networkId))
            return true;
        return false;
    }

    private void RebuildSelectedLabels()
    {
        var labels = SelectedRow?.Network.Config.Labels
            .Where(kv => !string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(kv.Key, "driver", StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value))
            .ToList()
            ?? [];

        // Also show driver as a chip when present.
        if (SelectedRow?.Network.Config.Labels.TryGetValue("driver", out var driver) == true
            && !string.IsNullOrEmpty(driver))
        {
            labels.Insert(0, new KeyValuePair<string, string>("driver", driver));
        }

        ObservableCollectionSync.Sync(SelectedLabels, labels, (a, b) =>
            string.Equals(a.Key, b.Key, StringComparison.Ordinal)
            && string.Equals(a.Value, b.Value, StringComparison.Ordinal));
        OnPropertyChanged(nameof(SelectedLabels));
        OnPropertyChanged(nameof(HasSelectedLabels));
    }

    private void RebuildUsersOnSelected()
    {
        UsersOnSelected.Clear();
        if (SelectedRow is null)
        {
            OnPropertyChanged(nameof(UsersOnSelected));
            return;
        }

        var containers = ContainersOnNetwork(SelectedRow.Id, _services.ContainerListService.Containers);
        foreach (var c in containers)
        {
            var atts = GetAttachments(c);
            var att = atts.FirstOrDefault(a => NetworkNamesMatch(a.Network, SelectedRow.Id));
            var name = !string.IsNullOrWhiteSpace(c.Configuration.Hostname)
                ? c.Configuration.Hostname!
                : (c.Configuration.Id.Length > 12 ? c.Configuration.Id[..12] : c.Configuration.Id);
            var address = att is not null && !string.IsNullOrEmpty(att.Address)
                ? att.Address.StrippingCidrSuffix()
                : "N/A";
            var hostname = name;
            if (att is not null && !string.IsNullOrEmpty(att.Hostname))
            {
                hostname = att.Hostname.EndsWith('.') ? att.Hostname[..^1] : att.Hostname;
            }

            UsersOnSelected.Add(new NetworkUserRow(
                c.Configuration.Id,
                name,
                address,
                hostname,
                string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase)));
        }
        OnPropertyChanged(nameof(UsersOnSelected));
    }
}
