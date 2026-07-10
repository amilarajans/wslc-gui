using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;
using Windows.UI;

namespace OrchardWin.App.ViewModels;

/// One row in the DNS domains list: the raw <see cref="DnsDomain"/> plus the container count
/// derived from <see cref="ContainerListService"/> (a container is "using" a domain if it's
/// either the container's DNS domain or one of its search domains). Recomputed on every
/// DnsService/ContainerListService change, mirroring <c>NetworkRow</c>/<c>UtilisationRow</c>.
public sealed record DnsDomainRow(DnsDomain Domain, int ContainerCount)
{
    public string DomainName => Domain.Domain;
    public bool IsDefault => Domain.IsDefault;

    /// The default domain can't be re-defaulted or deleted (mirrors `ListDNS.swift`, which
    /// only shows "Make Default"/"Delete Domain" when `!domain.isDefault`); menu items stay
    /// visible but disabled here instead of being removed from the flyout.
    public bool CanManage => !Domain.IsDefault;

    public Color IconColor => IsDefault ? Colors.Green : Colors.Gray;

    public string? DefaultBadgeText => IsDefault ? "DEFAULT" : null;

    public string ContainerCountText =>
        ContainerCount == 0 ? "No containers" : $"{ContainerCount} container{(ContainerCount == 1 ? "" : "s")}";
}

/// Thin glue over <see cref="DnsService"/>: forwards its state, derives the per-domain
/// container count from <see cref="ContainerListService"/>, and passes create/delete/
/// set-default calls straight through. Every Create/Delete/SetDefault call on DnsService
/// writes the hosts file through an elevated PowerShell copy - the first such call in a
/// session pops a UAC prompt; that's expected, not a bug.
public sealed partial class DnsViewModel : ObservableObject
{
    private readonly AppServices _services;

    [ObservableProperty]
    private ObservableCollection<DnsDomainRow> _rows = [];

    public DnsViewModel(AppServices services)
    {
        _services = services;

        _services.DnsService.PropertyChanged += (_, _) => Refresh();
        _services.ContainerListService.PropertyChanged += (_, _) => Refresh();

        Refresh();
    }

    public AppServices Services => _services;

    public bool IsDnsLoading => _services.DnsService.IsDnsLoading;

    public Task LoadAsync() => _services.DnsService.LoadAsync();

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
        Rows = new ObservableCollection<DnsDomainRow>(rows);
    }
}
