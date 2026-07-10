using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services;

/// Owns DNS domain state and operations. Ported from Orchard's `DNSService`, which backs
/// onto Apple's `container system dns` CLI (a feature of the container-network daemon with
/// no Windows/WSL equivalent - see ARCHITECTURE.md "DNS"). Reimplemented against the Windows
/// hosts file instead: every domain this app manages is written as a `hostname # orchardwin`
/// line, so Load/Create/Delete only ever touch lines carrying that marker and never disturb
/// entries the user or another tool added. Writes need admin rights, so Create/Delete/
/// SetDefault all go through <see cref="ICommandRunner.RunElevatedAsync"/> - one UAC prompt
/// per action, the direct analogue of Orchard's AppleScript admin-privileges prompt.
public sealed partial class DnsService : ObservableObject
{
    private const string Marker = "# orchardwin";
    /// Loopback target every managed hosts-file line points at. A DNS "domain" here just
    /// means "this hostname resolves to this machine" - there is no wildcard/subdomain
    /// matching (the hosts file can't express that), unlike Apple's real DNS domain server.
    private const string LoopbackAddress = "127.0.0.1";

    private static readonly string HostsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private readonly ICommandRunner _runner;
    private readonly AlertCenter _alertCenter;

    /// The settings-file-persisted default domain (there is no Windows equivalent of Apple's
    /// `dns.domain` system property to read this back from - see SystemService, which has no
    /// `dns.domain` entry in its `SystemProperties` on this platform). Persisted alongside the
    /// hosts-file entries themselves via a trailing `*` marker, e.g. `# orchardwin default`,
    /// so a relaunch doesn't lose which domain was the default.
    private const string DefaultMarker = "# orchardwin default";

    [ObservableProperty]
    private ObservableCollection<DnsDomain> _dnsDomains = [];

    [ObservableProperty]
    private bool _isDnsLoading;

    public DnsService(ICommandRunner runner, AlertCenter alertCenter)
    {
        _runner = runner;
        _alertCenter = alertCenter;
    }

    /// Reading the hosts file needs no elevation - only writes do.
    public Task LoadAsync(bool showLoading = true, CancellationToken ct = default)
    {
        if (showLoading)
        {
            IsDnsLoading = true;
            _alertCenter.Dismiss();
        }

        try
        {
            var domains = ReadManagedDomains();
            DnsDomains = new ObservableCollection<DnsDomain>(domains);
        }
        catch (Exception error)
        {
            // Leave the existing domains untouched rather than blanking them on a transient
            // read failure; only alert when the user asked for this load.
            if (showLoading)
                _alertCenter.Error($"Failed to load DNS domains: {error.Message}");
        }
        finally
        {
            IsDnsLoading = false;
        }

        return Task.CompletedTask;
    }

    public async Task<bool> CreateAsync(string domain, CancellationToken ct = default)
    {
        var trimmed = domain.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;

        try
        {
            var lines = ReadAllLinesSafe();
            if (lines.Any(l => ManagedDomainOf(l) == trimmed))
            {
                // Idempotent: already present.
                await LoadAsync(ct: ct);
                return true;
            }

            lines.Add($"{LoopbackAddress}\t{trimmed}\t{Marker}");
            var ok = await WriteHostsFileAsync(lines, ct);
            if (!ok)
            {
                _alertCenter.Error("Failed to create DNS domain (elevation was declined or the write failed).");
                return false;
            }

            await LoadAsync(ct: ct);
            return true;
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to create DNS domain: {error.Message}");
            return false;
        }
    }

    public async Task DeleteAsync(string domain, CancellationToken ct = default)
    {
        try
        {
            var lines = ReadAllLinesSafe();
            var kept = lines.Where(l => ManagedDomainOf(l) != domain).ToList();
            if (kept.Count == lines.Count)
            {
                await LoadAsync(ct: ct);
                return; // nothing to remove
            }

            var ok = await WriteHostsFileAsync(kept, ct);
            if (!ok)
            {
                _alertCenter.Error("Failed to delete DNS domain (elevation was declined or the write failed).");
                return;
            }

            await LoadAsync(ct: ct);
        }
        catch (Exception error)
        {
            _alertCenter.Error($"Failed to delete DNS domain: {error.Message}");
        }
    }

    /// Optimistically mark `domain` as the default in the local list, mirroring Orchard's
    /// `markDefault` - called by the owner before the elevated write round-trips.
    public void MarkDefault(string domain)
    {
        for (var i = 0; i < DnsDomains.Count; i++)
        {
            DnsDomains[i] = DnsDomains[i] with { IsDefault = DnsDomains[i].Domain == domain };
        }
    }

    public async Task SetDefaultAsync(string domain, CancellationToken ct = default)
    {
        MarkDefault(domain);

        try
        {
            var lines = ReadAllLinesSafe();
            var rewritten = new List<string>();
            var sawDomain = false;
            foreach (var line in lines)
            {
                var managed = ManagedDomainOf(line);
                if (managed is null) { rewritten.Add(line); continue; }

                if (managed == domain)
                {
                    sawDomain = true;
                    rewritten.Add($"{LoopbackAddress}\t{managed}\t{DefaultMarker}");
                }
                else
                {
                    // Strip a stale default marker from every other managed line - only one
                    // domain is ever the default.
                    rewritten.Add($"{LoopbackAddress}\t{managed}\t{Marker}");
                }
            }
            if (!sawDomain)
            {
                rewritten.Add($"{LoopbackAddress}\t{domain}\t{DefaultMarker}");
            }

            var ok = await WriteHostsFileAsync(rewritten, ct);
            if (!ok)
            {
                await LoadAsync(showLoading: false, ct: ct);
                _alertCenter.Error("Failed to set default DNS domain (elevation was declined or the write failed).");
                return;
            }

            await LoadAsync(showLoading: false, ct: ct);
        }
        catch (Exception error)
        {
            await LoadAsync(showLoading: false, ct: ct);
            _alertCenter.Error($"Failed to set default DNS domain: {error.Message}");
        }
    }

    // MARK: - Hosts-file plumbing

    private static List<string> ReadAllLinesSafe()
    {
        try
        {
            return File.Exists(HostsFilePath) ? [.. File.ReadAllLines(HostsFilePath)] : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<DnsDomain> ReadManagedDomains()
    {
        var result = new List<DnsDomain>();
        foreach (var line in ReadAllLinesSafe())
        {
            var isDefault = line.TrimEnd().EndsWith(DefaultMarker, StringComparison.Ordinal);
            var domain = ManagedDomainOf(line);
            if (domain is not null) result.Add(new DnsDomain { Domain = domain, IsDefault = isDefault });
        }
        return result;
    }

    /// The hostname of a hosts-file line this app owns (carries the trailing `# orchardwin`
    /// or `# orchardwin default` marker), or null if the line is unmanaged/blank/a comment.
    /// Format expected: `127.0.0.1<ws>hostname<ws># orchardwin[ default]`.
    private static string? ManagedDomainOf(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;
        if (!trimmed.Contains(Marker, StringComparison.Ordinal)) return null;

        var beforeComment = trimmed.Split('#')[0].Trim();
        var parts = beforeComment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    /// Write the full hosts-file content through an elevated PowerShell copy: build the new
    /// content in-process, stage it to a temp file, then have the elevated child overwrite
    /// the real hosts file with it. Safer than piping multi-line content through cmd/AppleScript-
    /// style quoting, and it's one UAC prompt regardless of how many lines changed.
    private async Task<bool> WriteHostsFileAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        var stagingFile = Path.Combine(Path.GetTempPath(), $"orchardwin-hosts-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllLinesAsync(stagingFile, lines, ct);

            // VERIFY: Copy-Item onto a file the OS keeps open for reading is expected to
            // succeed on Windows (unlike a delete/replace), but this hasn't been exercised
            // against a real, in-use hosts file - if it fails in practice, fall back to
            // `Move-Item -Force` (rename-over-existing, which Windows does allow even for an
            // open-for-read file) instead of a plain copy.
            var psCommand = $"Copy-Item -LiteralPath '{stagingFile}' -Destination '{HostsFilePath}' -Force";
            var result = await _runner.RunElevatedAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command", psCommand],
                ct);

            return !result.Failed;
        }
        finally
        {
            try { File.Delete(stagingFile); } catch { /* best-effort cleanup */ }
        }
    }
}
