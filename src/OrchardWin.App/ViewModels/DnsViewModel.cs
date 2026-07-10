using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the DNS domains list: the raw <see cref="DnsDomain"/> plus the container count
/// derived from <see cref="ContainerListService"/>.
public sealed record DnsDomainRow(DnsDomain Domain, int ContainerCount)
{
    public string DomainName => Domain.Domain;
    public bool IsDefault => Domain.IsDefault;
    public bool CanManage => !Domain.IsDefault;
    public Color IconColor => IsDefault ? Colors.Green : Colors.Gray;
    public string? DefaultBadgeText => IsDefault ? "DEFAULT" : null;
    public string ContainerCountText =>
        ContainerCount == 0 ? "No containers" : $"{ContainerCount} container{(ContainerCount == 1 ? "" : "s")}";
}

public sealed partial class DnsViewModel : ObservableObject
{
    private readonly AppServices _services;

    /// Stable collection — mutated in place so ListView does not flicker on poll.
    public ObservableCollection<DnsDomainRow> Rows { get; } = [];

    public DnsViewModel(AppServices services)
    {
        _services = services;
        _services.DnsService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null
                or nameof(DnsService.DnsDomains)
                or nameof(DnsService.IsDnsLoading))
                Refresh();
        };
        _services.ContainerListService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or nameof(ContainerListService.Containers))
                Refresh();
        };
        Refresh();
    }

    public AppServices Services => _services;
    public bool IsDnsLoading => _services.DnsService.IsDnsLoading;

    public async Task LoadAsync()
    {
        await _services.DnsService.LoadAsync();
        Refresh();
    }

    public Task SetDefaultAsync(string domain) => _services.DnsService.SetDefaultAsync(domain);
    public Task DeleteAsync(string domain) => _services.DnsService.DeleteAsync(domain);

    private void Refresh()
    {
        var containers = _services.ContainerListService.Containers;
        var rows = new List<DnsDomainRow>();
        foreach (var domain in _services.DnsService.DnsDomains)
        {
            var count = containers.Count(c =>
            {
                var dns = c.Configuration.Dns;
                if (dns is null) return false;
                return dns.Domain is not null ? dns.Domain == domain.Domain : dns.SearchDomains.Contains(domain.Domain);
            });
            rows.Add(new DnsDomainRow(domain, count));
        }

        if (ObservableCollectionSync.Sync(Rows, rows, (a, b) =>
                string.Equals(a.DomainName, b.DomainName, StringComparison.Ordinal)
                && a.IsDefault == b.IsDefault
                && a.ContainerCount == b.ContainerCount))
        {
            OnPropertyChanged(nameof(Rows));
        }

        OnPropertyChanged(nameof(IsDnsLoading));
    }
}
