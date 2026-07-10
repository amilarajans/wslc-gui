using System.Diagnostics;
using System.Text.Json;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// <summary>
/// <see cref="IMachineBackend"/> backed by <c>wsl.exe</c> distro lifecycle commands
/// cross-referenced with <c>wslc.exe</c> for the backing container's resource/image info.
/// Ported from Orchard's <c>LiveMachineBackend</c> (see MachineBackend.swift) - same method
/// shapes, but there is no XPC client on Windows: every call shells a CLI through
/// <see cref="ICommandRunner"/> (or, for the one streaming case, a raw <see cref="Process"/>,
/// since <see cref="ICommandRunner"/> only supports run-to-completion calls). See
/// ARCHITECTURE.md for why this talks to CLIs instead of the Microsoft.WSL.Containers preview
/// NuGet package directly.
/// </summary>
public sealed class WslMachineBackend : IMachineBackend
{
    private readonly ICommandRunner _runner;
    private readonly string _wslBinaryPath;
    private readonly string _wslcBinaryPath;

    /// Guessed platform for a machine whose backing container couldn't be inspected (see
    /// <see cref="BuildMachineAsync"/>). // VERIFY: assumes x86_64; WSL2 also runs on ARM64
    /// hosts, where this guess would be wrong until a real `wslc container inspect` succeeds.
    private static readonly Platform DefaultPlatform = new() { Architecture = "x86_64" };

    public WslMachineBackend(ICommandRunner runner, string wslBinaryPath, string wslcBinaryPath)
    {
        _runner = runner;
        _wslBinaryPath = wslBinaryPath;
        _wslcBinaryPath = wslcBinaryPath;
    }

    // MARK: - List / inspect

    public async Task<IReadOnlyList<Machine>> ListMachinesAsync(CancellationToken ct = default)
    {
        // VERIFY: `wsl.exe --list --verbose` column layout (NAME/STATE/VERSION, `*` marking
        // the default distro) matches Microsoft's documented output as of the WSL container
        // preview announcement, but exact spacing/locale text is unconfirmed - parsed
        // defensively below so an unexpected row is skipped, never thrown.
        var result = await ExecuteAsync(_wslBinaryPath, ["--list", "--verbose"], "wsl --list --verbose", ct).ConfigureAwait(false);
        var rows = ParseWslListVerbose(result.Stdout ?? "");

        var machines = new List<Machine>(rows.Count);
        foreach (var row in rows)
        {
            machines.Add(await BuildMachineAsync(row, ct).ConfigureAwait(false));
        }
        return machines;
    }

    public async Task<Machine> InspectMachineAsync(string id, CancellationToken ct = default)
    {
        // VERIFY: wsl.exe has no single-distro inspect verb (unlike the machine XPC client's
        // `inspect(id:)`) - re-listing and filtering is the only documented way to get one
        // distro's state, same source of truth as ListMachinesAsync.
        var machines = await ListMachinesAsync(ct).ConfigureAwait(false);
        var match = machines.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return match ?? throw OrchardWinException.Generic($"Machine '{id}' was not found.");
    }

    // MARK: - Create

    /// VERIFY: exact `wslc` image-export subcommand/flags are unconfirmed - see
    /// ARCHITECTURE.md. This whole method is a best-effort two-step (export the image to a
    /// rootfs tar via wslc, then `wsl --import` that tar as a new distro) since there is no
    /// documented single "create a WSL distro straight from an OCI image reference" command.
    public async Task CreateMachineAsync(MachineCreateSpec spec, CancellationToken ct = default)
    {
        var installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrchardWin", "machines", spec.Name);
        var tarPath = Path.Combine(Path.GetTempPath(), $"orchardwin-{spec.Name}-{Guid.NewGuid():N}.tar");

        try
        {
            Directory.CreateDirectory(installRoot);

            // VERIFY: `wslc image pull` mirrors Docker's `docker image pull`; treating
            // "already exists" as success mirrors OrchardWinException.IsAlreadyExistsError's
            // documented use elsewhere in this port.
            var pull = await _runner.RunAsync(_wslcBinaryPath, ["image", "pull", spec.ImageRef], ct).ConfigureAwait(false);
            if (pull.Failed && !OrchardWinException.IsAlreadyExistsError(pull.Stderr ?? ""))
                throw OrchardWinException.CliFailed($"wslc image pull {spec.ImageRef}", pull.ExitCode, pull.Stderr);

            // VERIFY: `wslc image export --format tar --output <path>` is a guessed shape -
            // Docker has no direct "export an image (not a container) to a rootfs tar"
            // equivalent, so this assumes wslc exposes one directly since it has to build a
            // rootfs for containers anyway. If unconfirmed on real wslc, an alternative is
            // `wslc run --detach` + `wslc container export`, which needs a bootstrapped
            // container first - swap this in if `image export` doesn't exist.
            var export = await _runner.RunAsync(
                _wslcBinaryPath, ["image", "export", spec.ImageRef, "--format", "tar", "--output", tarPath], ct).ConfigureAwait(false);
            if (export.Failed)
                throw OrchardWinException.CliFailed($"wslc image export {spec.ImageRef}", export.ExitCode, export.Stderr);

            var import = await _runner.RunAsync(
                _wslBinaryPath, ["--import", spec.Name, installRoot, tarPath, "--version", "2"], ct).ConfigureAwait(false);
            if (import.Failed)
                throw OrchardWinException.CliFailed($"wsl --import {spec.Name}", import.ExitCode, import.Stderr);

            // Write the boot-time config before first boot so a `NoBoot` create still leaves a
            // correctly configured distro for whenever it's later started.
            await WriteWslConfAsync(spec.Name, spec.HomeMount ?? "rw", spec.Virtualization, ct).ConfigureAwait(false);
            UpdateGlobalWslConfig(spec.Cpus, spec.MemoryGiB);

            if (spec.SetDefault)
                await SetDefaultMachineAsync(spec.Name, ct).ConfigureAwait(false);

            if (!spec.NoBoot)
                await BootMachineAsync(spec.Name, ct).ConfigureAwait(false);
        }
        catch (OrchardWinException)
        {
            throw;
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            throw OrchardWinException.MachineApiUnavailable();
        }
        catch (Exception ex)
        {
            throw OrchardWinException.Generic($"Failed to create machine '{spec.Name}': {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tarPath)) File.Delete(tarPath); } catch { /* best-effort cleanup */ }
        }
    }

    // MARK: - Config

    /// Applies the parts of <see cref="MachineConfigSpec"/> that are actually real on WSL2
    /// (home-mount mode + a virtualization marker, written per-distro into `/etc/wsl.conf`),
    /// plus cpus/memoryGiB, which - unlike Apple's per-machine container tool - are not a
    /// per-distro setting on WSL2 today: they resize the *global* lightweight VM every distro
    /// shares, via `%UserProfile%\.wslconfig`'s `[wsl2]` section. This is a genuine platform
    /// difference, not a shortcut; see <see cref="UpdateGlobalWslConfig"/>.
    public async Task SetMachineConfigAsync(string id, MachineConfigSpec config, CancellationToken ct = default)
    {
        try
        {
            await WriteWslConfAsync(id, config.HomeMount, config.Virtualization, ct).ConfigureAwait(false);
            UpdateGlobalWslConfig(config.Cpus, config.MemoryGiB);
        }
        catch (OrchardWinException)
        {
            throw;
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            throw OrchardWinException.MachineApiUnavailable();
        }
        catch (Exception ex)
        {
            throw OrchardWinException.Generic($"Failed to update machine '{id}': {ex.Message}");
        }
    }

    // MARK: - Lifecycle

    public async Task BootMachineAsync(string id, CancellationToken ct = default)
    {
        // WSL2 lazily boots a distro on its first command; there is no separate "just start
        // it, run nothing" verb, so this runs a trivial no-op command inside it.
        await ExecuteAsync(_wslBinaryPath, ["-d", id, "--", "true"], $"wsl -d {id} -- true", ct).ConfigureAwait(false);
    }

    public async Task StopMachineAsync(string id, CancellationToken ct = default)
    {
        await ExecuteAsync(_wslBinaryPath, ["--terminate", id], $"wsl --terminate {id}", ct).ConfigureAwait(false);
    }

    public async Task DeleteMachineAsync(string id, CancellationToken ct = default)
    {
        await ExecuteAsync(_wslBinaryPath, ["--unregister", id], $"wsl --unregister {id}", ct).ConfigureAwait(false);
    }

    public async Task SetDefaultMachineAsync(string id, CancellationToken ct = default)
    {
        await ExecuteAsync(_wslBinaryPath, ["--set-default", id], $"wsl --set-default {id}", ct).ConfigureAwait(false);
    }

    // MARK: - Logs

    public async Task<Stream> MachineLogsAsync(string id, CancellationToken ct = default)
    {
        var machine = await InspectMachineAsync(id, ct).ConfigureAwait(false);
        // VERIFY: falls back to the distro name itself when BuildMachineAsync couldn't
        // resolve a backing container id - unconfirmed that wslc always accepts the distro
        // name as a container id.
        var containerId = machine.ContainerId ?? id;

        var psi = new ProcessStartInfo
        {
            FileName = _wslcBinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // VERIFY: `wslc container logs <id>` subcommand/flags mirror Docker CLI convention
        // (`docker logs`). Deliberately NOT passing a `-f`/follow flag: this returns a Stream
        // that reaches EOF once wslc finishes dumping the current log buffer, matching how
        // Orchard's own `machineLogs` is consumed (MachineService.fetchLogs polls on an
        // interval and reads each FileHandle to completion) - a follow stream would never
        // reach EOF and that read loop would hang forever.
        foreach (var arg in new[] { "container", "logs", containerId }) psi.ArgumentList.Add(arg);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw OrchardWinException.ServiceUnavailable();
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            throw OrchardWinException.MachineApiUnavailable();
        }

        ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
        });

        // Drain stderr concurrently rather than leaving it unread: once wslc writes enough to
        // stderr to fill the OS pipe buffer it would block trying to write more, and since
        // nothing would ever read it the caller's stdout stream would silently stall forever -
        // the same deadlock ProcessCommandRunner.RunAsync's stdout/stderr comment guards
        // against. Discarded except for logging; the returned stdout stream is what callers
        // consume.
        _ = DrainStderrAsync(process, containerId, ct);

        return process.StandardOutput.BaseStream;
    }

    private static async Task DrainStderrAsync(Process process, string containerId, CancellationToken ct)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            if (!string.IsNullOrWhiteSpace(stderr))
                Log.Backend.Debug($"wslc container logs {containerId} stderr: {stderr}");
        }
        catch
        {
            // Best-effort: a failure here must never affect the stdout stream callers hold.
        }
    }

    // MARK: - wsl --list --verbose parsing

    private sealed record WslListRow(string Name, string State, string Version, bool IsDefault);

    /// Parses `wsl --list --verbose` text-table output defensively: guards missing columns,
    /// skips unrecognised rows instead of throwing. Known fragility per ARCHITECTURE.md -
    /// column spacing/locale text is unconfirmed.
    private static List<WslListRow> ParseWslListVerbose(string stdout)
    {
        // wsl.exe is documented to write UTF-16LE to stdout when it isn't attached to a real
        // console (i.e. whenever it's spawned redirected, as here) - decoding that as
        // UTF-8/ASCII interleaves NUL bytes between characters. Stripping them recovers the
        // intended text regardless of which encoding ends up applied upstream by
        // ICommandRunner. This is a documented wsl.exe quirk, not a parsing guess.
        var cleaned = stdout.Replace("\0", "");
        var rows = new List<WslListRow>();

        foreach (var raw in cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var trimmedStart = line.TrimStart();
            var isDefault = trimmedStart.StartsWith('*');
            var withoutMarker = isDefault ? trimmedStart[1..] : trimmedStart;

            // Skip the header row by column-name match rather than line position, in case a
            // locale changes header casing/spacing but keeps the same first token.
            if (withoutMarker.TrimStart().StartsWith("NAME", StringComparison.OrdinalIgnoreCase)) continue;

            // Distro names are assumed not to contain whitespace (true of every observed
            // registration name - Ubuntu, Debian, docker-desktop, etc.). If that's ever
            // violated this row is skipped below rather than misparsed.
            var columns = withoutMarker.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 2) continue;

            var name = columns[0];
            var state = columns[1];
            var version = columns.Length > 2 ? columns[2] : "2";
            rows.Add(new WslListRow(name, state, version, isDefault));
        }

        return rows;
    }

    /// Machine.Status is documented as `"running" | "stopped"` (see Machine's doc comment) -
    /// collapses WSL's other transient states (`Installing`, `Uninstalling`, etc.) into
    /// `stopped` rather than modeling a third state.
    private static string MapState(string wslState) => wslState.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase)
        ? "running"
        : "stopped";

    /// Builds a <see cref="Machine"/> for one `wsl --list --verbose` row, best-effort
    /// cross-referencing `wslc container inspect` for the backing container's resource/image
    /// info. A distro wslc can't resolve (not Orchard-Win-created, or the lookup simply fails)
    /// still lists as a Machine with defaults instead of failing the whole listing.
    private async Task<Machine> BuildMachineAsync(WslListRow row, CancellationToken ct)
    {
        var status = MapState(row.State);

        var imageReference = "";
        var platform = DefaultPlatform;
        var cpus = 0;
        long memoryBytes = 0;
        string? containerId = null;
        string? ipAddress = null;

        try
        {
            // VERIFY: assumes the backing container's wslc id equals the WSL distro
            // registration name - the exact machine<->container id mapping wslc uses is
            // unconfirmed (see ARCHITECTURE.md). Falls back to the defaults above if wrong.
            var inspect = await _runner.RunAsync(
                _wslcBinaryPath, ["container", "inspect", row.Name, "--format", "json"], ct).ConfigureAwait(false);
            if (!inspect.Failed && !string.IsNullOrWhiteSpace(inspect.Stdout))
            {
                var container = DeserializeContainer(inspect.Stdout);
                if (container is not null)
                {
                    imageReference = container.Configuration.Image.Reference;
                    platform = container.Configuration.Platform;
                    cpus = container.Configuration.Resources.Cpus;
                    memoryBytes = container.Configuration.Resources.MemoryInBytes;
                    containerId = container.Configuration.Id;
                    var attachment = container.Networks.FirstOrDefault();
                    ipAddress = attachment is null ? null : attachment.Address.StrippingCidrSuffix();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Backend.Debug($"wslc container inspect {row.Name} failed, using defaults: {ex.Message}");
        }

        return new Machine
        {
            Id = row.Name,
            Status = status,
            IsDefault = row.IsDefault,
            Cpus = cpus,
            MemoryBytes = memoryBytes,
            DiskSizeBytes = null,
            // VERIFY: there is no side-effect-free way to read a stopped distro's
            // /etc/wsl.conf back (running a command inside it via `wsl -d <name>` would boot
            // it, an unacceptable side effect for a list operation) - these report the
            // create/configure-time default rather than the live value.
            HomeMount = "rw",
            Virtualization = false,
            KernelPath = null,
            ImageReference = imageReference,
            Platform = platform,
            IpAddress = ipAddress,
            ContainerId = containerId,
            CreatedDate = null,
            StartedDate = null,
            Initialized = status == "running",
            UserSetup = null,
        };
    }

    private static Container? DeserializeContainer(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Container>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // MARK: - /etc/wsl.conf (per-distro home-mount + virtualization marker)

    /// Writes home-mount mode and a virtualization marker into the distro's `/etc/wsl.conf`.
    /// Reads the existing file back first and only touches the keys/lines this app owns (the
    /// `[automount]` enabled/options keys, plus `# orchardwin:*` marker comments), so any
    /// other wsl.conf content the user or another tool set - e.g. `[boot] systemd=true` -
    /// survives, mirroring the "never clobber entries this app doesn't own" rule the DNS
    /// hosts-file integration also follows (see ARCHITECTURE.md).
    ///
    /// Reading the file requires a command to run inside the distro, which has the side
    /// effect of starting it if it was stopped - acceptable here since
    /// `SetMachineConfigAsync`'s own contract is "takes effect on next boot," so an extra
    /// boot cycle during the edit itself doesn't change that contract.
    ///
    /// `virtualization` has no real per-distro WSL2 setting today (nested virtualization is a
    /// `.wslconfig`-wide flag, not per-distro) - it's tracked here only as app metadata via
    /// the `# orchardwin:virtualization=` marker so this app can remember what the user asked
    /// for; it does not actually toggle anything WSL enforces per-distro.
    private async Task WriteWslConfAsync(string name, string homeMount, bool virtualization, CancellationToken ct)
    {
        var existing = "";
        var read = await _runner.RunAsync(
            _wslBinaryPath, ["-d", name, "-u", "root", "--", "sh", "-c", "cat /etc/wsl.conf 2>/dev/null || true"], ct).ConfigureAwait(false);
        if (!read.Failed) existing = read.Stdout ?? "";

        var lines = SplitConfLines(existing);
        // VERIFY: mapping "home mount" (Apple container tool: mount the host's home directory
        // into the machine) onto WSL's `[automount]` (which controls Windows drive automount
        // under /mnt, a different concept - WSL has no per-distro "mount my Windows user
        // profile" toggle) is an approximation, not a faithful port. `none` disables
        // automount entirely; `ro` enables it read-only; `rw` (default) enables it normally.
        UpsertIniValue(lines, "automount", "enabled", homeMount == "none" ? "false" : "true");
        UpsertIniValue(lines, "automount", "options", homeMount == "ro" ? "\"ro\"" : "\"\"");
        UpsertMarkerComment(lines, "orchardwin:homeMount", homeMount);
        UpsertMarkerComment(lines, "orchardwin:virtualization", virtualization ? "true" : "false");
        var content = string.Join('\n', lines) + '\n';

        var script = $"cat > /etc/wsl.conf <<'ORCHARDWIN_EOF'\n{content}ORCHARDWIN_EOF\n";
        var write = await _runner.RunAsync(_wslBinaryPath, ["-d", name, "-u", "root", "--", "sh", "-c", script], ct).ConfigureAwait(false);
        if (write.Failed)
            throw OrchardWinException.CliFailed($"wsl -d {name} (write /etc/wsl.conf)", write.ExitCode, write.Stderr);
    }

    /// Resizes the *global* WSL2 VM's CPU/memory ceiling via `%UserProfile%\.wslconfig`'s
    /// `[wsl2]` section - Windows-side file I/O, not a `wsl.exe` subcommand, so it doesn't go
    /// through <see cref="ICommandRunner"/>. GENUINE PLATFORM DIFFERENCE from Apple's
    /// container tool (see ARCHITECTURE.md): WSL2 has no per-distro CPU/memory ceiling, only
    /// one shared VM sized for every distro (and every wslc container) at once - calling this
    /// resizes *all* machines simultaneously, not just the one being edited. A null value
    /// leaves that key untouched (the caller's "use the runtime default" case) rather than
    /// forcing a guessed number into it. Note this also does not take effect until the next
    /// `wsl --shutdown`, which is disruptive enough (kills every running distro) that this
    /// deliberately does not run it automatically.
    private static void UpdateGlobalWslConfig(int? cpus, int? memoryGiB)
    {
        if (cpus is null && memoryGiB is null) return;

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var lines = SplitConfLines(existing);
        if (cpus is { } cpuCount) UpsertIniValue(lines, "wsl2", "processors", cpuCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (memoryGiB is { } memory) UpsertIniValue(lines, "wsl2", "memory", $"{memory}GB");
        File.WriteAllText(path, string.Join('\n', lines) + '\n');
    }

    private static List<string> SplitConfLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var lines = new List<string>(content.Replace("\r\n", "\n").Split('\n'));
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// Sets `key = value` under `[section]` in an ini-style line list, creating the section if
    /// missing and replacing an existing key in place rather than duplicating it. A
    /// line-based best-effort merge (not a full ini parser), sufficient for the small,
    /// predictable files this app writes into.
    private static void UpsertIniValue(List<string> lines, string section, string key, string value)
    {
        var sectionHeader = $"[{section}]";
        var sectionIndex = lines.FindIndex(l => l.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key} = {value}");
            return;
        }

        var sectionEnd = lines.Count;
        for (var i = sectionIndex + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('[')) { sectionEnd = i; break; }
        }

        for (var i = sectionIndex + 1; i < sectionEnd; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';')) continue;
            var eq = lines[i].IndexOf('=');
            if (eq < 0) continue;
            if (lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key} = {value}";
                return;
            }
        }

        lines.Insert(sectionEnd, $"{key} = {value}");
    }

    /// Sets a `# marker=value` app-owned metadata comment, replacing an existing one in place.
    private static void UpsertMarkerComment(List<string> lines, string marker, string value)
    {
        var prefix = $"# {marker}=";
        var index = lines.FindIndex(l => l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var line = $"{prefix}{value}";
        if (index >= 0) lines[index] = line;
        else lines.Add(line);
    }

    // MARK: - Error classification

    private static bool IsBinaryMissing(Exception ex) =>
        ex is System.ComponentModel.Win32Exception or FileNotFoundException;

    /// Best-effort detection that WSL / the container preview isn't reachable at all, as
    /// opposed to a single command failing for another reason. Mirrors Orchard's
    /// `isMachineServiceUnavailable`, which matches connection-style phrases in the XPC
    /// error; ported to best-effort phrases wsl.exe/wslc.exe are expected to print when the
    /// feature isn't installed or is out of date.
    /// VERIFY: exact stderr text is unconfirmed - these are substring guesses, easy to update
    /// once real wsl.exe/wslc.exe error text is known.
    private static bool IsMachineServiceUnavailable(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        var message = stderr.ToLowerInvariant();
        return message.Contains("wsl is not installed")
            || message.Contains("wsl_e_")
            || message.Contains("please update wsl")
            || message.Contains("update wsl")
            || message.Contains("is not recognized as an internal or external command")
            || (message.Contains("the term") && message.Contains("is not recognized"))
            || message.Contains("not supported")
            || message.Contains("no installed distributions");
    }

    /// Runs a `wsl.exe`/`wslc.exe` command, classifying failure as
    /// <see cref="OrchardWinException.MachineApiUnavailable"/> when the process couldn't even
    /// be found/started or its stderr looks like a "WSL/preview isn't installed" message (see
    /// <see cref="IsMachineServiceUnavailable"/>), and as a normal
    /// <see cref="OrchardWinException.CliFailed"/> otherwise - so the UI can tell "update WSL"
    /// apart from "this one command failed" the way Orchard's `mapMachineError` does.
    private async Task<ProcessResult> ExecuteAsync(string program, IReadOnlyList<string> arguments, string commandLabel, CancellationToken ct)
    {
        ProcessResult result;
        try
        {
            result = await _runner.RunAsync(program, arguments, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsBinaryMissing(ex))
        {
            throw OrchardWinException.MachineApiUnavailable();
        }

        if (result.Failed)
        {
            if (IsMachineServiceUnavailable(result.Stderr))
                throw OrchardWinException.MachineApiUnavailable();
            throw OrchardWinException.CliFailed(commandLabel, result.ExitCode, result.Stderr);
        }

        return result;
    }
}
