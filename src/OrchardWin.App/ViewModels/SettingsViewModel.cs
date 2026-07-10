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

    [ObservableProperty]
    private ObservableCollection<TerminalApp> _installedTerminals = [];

    [ObservableProperty]
    private TerminalApp _preferredTerminal;

    [ObservableProperty]
    private string _containerBinaryPath = "";

    [ObservableProperty]
    private bool _isUsingCustomBinary;

    [ObservableProperty]
    private ObservableCollection<DnsDomain> _dnsDomains = [];

    [ObservableProperty]
    private string? _selectedDefaultDomain;

    [ObservableProperty]
    private ObservableCollection<SystemProperty> _systemProperties = [];

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
                SystemProperties = new ObservableCollection<SystemProperty>(Services.SystemService.SystemProperties);
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
    }

    private void RefreshFromSettings()
    {
        InstalledTerminals = new ObservableCollection<TerminalApp>(Services.Settings.InstalledTerminals);
        PreferredTerminal = Services.Settings.PreferredTerminal;
        ContainerBinaryPath = Services.Settings.ContainerBinaryPath;
        IsUsingCustomBinary = Services.Settings.IsUsingCustomBinary;
    }

    private void RefreshDnsDomains() =>
        DnsDomains = new ObservableCollection<DnsDomain>(Services.DnsService.DnsDomains);

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
