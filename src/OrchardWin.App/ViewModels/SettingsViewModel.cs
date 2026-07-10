using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;
using OrchardWin.Core.Services;

namespace OrchardWin.App.ViewModels;

/// Thin glue for SettingsPage. Ported from Orchard's `SettingsView`/`GeneralSettingsView`/
/// `SystemSettingsView`, split across a General/System layout (see SettingsPage.xaml's
/// class remarks for why this port uses a Pivot rather than a separate window/scene).
///
/// One behavioral deviation from the Swift original, called out in ARCHITECTURE.md too: the
/// default-DNS-domain picker here writes straight to <see cref="Services.DnsService"/> (this
/// port's DNS is a self-contained hosts-file store with its own default marker - see
/// DnsService's doc comment), not through
/// <c>SystemService.SetSystemPropertyAsync("dns.domain", ...)</c> like Orchard does, since
/// there is no `dns.domain` system property on this platform to round-trip through.
public sealed partial class SettingsViewModel : ObservableObject
{
    public AppServices Services { get; }

    public ObservableCollection<TerminalApp> InstalledTerminals { get; } = [];
    public ObservableCollection<DnsDomain> DnsDomains { get; } = [];
    public ObservableCollection<SystemProperty> SystemProperties { get; } = [];

    [ObservableProperty]
    private TerminalApp _preferredTerminal;

    [ObservableProperty]
    private string _containerBinaryPath = "";

    [ObservableProperty]
    private bool _isUsingCustomBinary;

    [ObservableProperty]
    private string? _selectedDefaultDomain;

    [ObservableProperty]
    private WslKernelInfo _kernelInfo = new();

    public SettingsViewModel(AppServices services)
    {
        Services = services;

        Services.Settings.PropertyChanged += (_, _) => RefreshFromSettings();
        Services.DnsService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DnsService.DnsDomains)) RefreshDnsDomains();
        };
        Services.SystemService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemService.SystemProperties))
                RefreshSystemProperties();
            if (e.PropertyName == nameof(SystemService.KernelInfo))
                KernelInfo = Services.SystemService.KernelInfo;
        };

        RefreshFromSettings();
    }

    public async Task LoadAsync()
    {
        await Services.SystemService.LoadSystemPropertiesAsync();
        await Services.SystemService.LoadKernelConfigAsync();
        await Services.DnsService.LoadAsync();
        RefreshFromSettings();
        RefreshDnsDomains();
        RefreshSystemProperties();
        KernelInfo = Services.SystemService.KernelInfo;
    }

    private void RefreshFromSettings()
    {
        ObservableCollectionSync.Sync(InstalledTerminals, Services.Settings.InstalledTerminals.ToList(),
            (a, b) => a == b);
        OnPropertyChanged(nameof(InstalledTerminals));
        PreferredTerminal = Services.Settings.PreferredTerminal;
        ContainerBinaryPath = Services.Settings.ContainerBinaryPath;
        IsUsingCustomBinary = Services.Settings.IsUsingCustomBinary;
    }

    private void RefreshDnsDomains()
    {
        ObservableCollectionSync.Sync(DnsDomains, Services.DnsService.DnsDomains.ToList(),
            (a, b) => string.Equals(a.Domain, b.Domain, StringComparison.Ordinal) && a.IsDefault == b.IsDefault);
        OnPropertyChanged(nameof(DnsDomains));
    }

    private void RefreshSystemProperties()
    {
        ObservableCollectionSync.Sync(SystemProperties, Services.SystemService.SystemProperties.ToList(),
            (a, b) => string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                      && string.Equals(a.Value, b.Value, StringComparison.Ordinal));
        OnPropertyChanged(nameof(SystemProperties));
    }

    public void SetPreferredTerminal(TerminalApp app) => Services.Settings.SetPreferredTerminal(app);

    public bool ChooseBinaryPath(string path)
    {
        var ok = Services.Settings.ValidateAndSetCustomBinaryPath(path);
        if (ok) RefreshFromSettings();
        return ok;
    }

    public void ResetBinaryPath()
    {
        Services.Settings.ResetToDefaultBinary();
        RefreshFromSettings();
    }

    public async Task SetDefaultDomainAsync(string domain)
    {
        await Services.DnsService.SetDefaultAsync(domain);
        RefreshDnsDomains();
    }
}
