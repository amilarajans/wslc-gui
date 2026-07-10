using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the networks list: the raw <see cref="ContainerNetwork"/> plus the
/// cross-service-derived fields (connected container count, whether any attached container is
/// running) the row's cells bind to. Recomputed on every NetworkService/ContainerListService
/// change - mirrors <c>DashboardViewModel</c>'s <c>UtilisationRow</c> pattern, since the
/// shared row control can't itself call back into two services per row.
public sealed record NetworkRow(ContainerNetwork Network, int ConnectedContainerCount, bool HasRunningContainers)
{
    public string Id => Network.Id;

    /// Mirrors Orchard's `ListNetworks.swift`, which disables deletion for the network whose
    /// id is literally "default" - there is no Core-exposed "is the default network" flag to
    /// query instead, so this is the same string-literal check ported as-is.
    public bool CanDelete => Network.Id != "default";

    // x:Bind requires exact type matches without a converter - expose pre-formatted
    // strings/colors rather than relying on implicit conversions in the DataTemplate.
    public Color IconColor => HasRunningContainers ? Colors.Green : Colors.Gray;

    public string GatewayText
    {
        get
        {
            var address = Network.Status.Gateway ?? Network.Status.Address ?? "No address";
            var kind = Network.IsHostOnly ? "Host-only" : "Open";
            return $"{address} · {kind}";
        }
    }

    public string ContainerCountText =>
        ConnectedContainerCount == 0 ? "No containers" : $"{ConnectedContainerCount} container{(ConnectedContainerCount == 1 ? "" : "s")}";
}

/// Thin glue over <see cref="NetworkService"/>: forwards its state, wires the
/// container-count/running-state derivation from <see cref="ContainerListService"/>, and
/// passes create/delete calls straight through. NetworkService/ContainerListService already
/// own all persistent state - nothing here is re-implemented, only recombined per row.
public sealed partial class NetworksViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private ObservableCollection<NetworkRow> _rows = [];

    public NetworksViewModel(AppServices services)
    {
        _services = services;

        // Rebuild derived rows whenever the underlying network or container list changes,
        // rather than polling ourselves - NetworkService/ContainerListService already own
        // their own load lifecycle; this only recombines their published state.
        _services.NetworkService.PropertyChanged += (_, _) => Refresh();
        _services.ContainerListService.PropertyChanged += (_, _) => Refresh();

        Refresh();
    }

    public AppServices Services => _services;

    public bool IsNetworksLoading => _services.NetworkService.IsNetworksLoading;

    public Task LoadAsync() => _services.NetworkService.LoadAsync();

    public int ConnectedContainerCount(string networkId) =>
        _services.ContainerListService.Containers.Count(c => c.Networks.Any(n => n.Network == networkId));

    public Task DeleteAsync(string networkId) => _services.NetworkService.DeleteAsync(networkId);

    private void Refresh()
    {
        var containers = _services.ContainerListService.Containers;
        var rows = new List<NetworkRow>();
        foreach (var network in _services.NetworkService.Networks)
        {
            var attached = containers.Where(c => c.Networks.Any(n => n.Network == network.Id)).ToList();
            var hasRunning = attached.Any(c => c.Status.Equals("running", StringComparison.OrdinalIgnoreCase));
            rows.Add(new NetworkRow(network, attached.Count, hasRunning));
        }
        Rows = new ObservableCollection<NetworkRow>(rows);
    }
}
