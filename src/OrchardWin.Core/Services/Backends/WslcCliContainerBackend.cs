using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// <see cref="IContainerBackend"/> backed by shelling out to <c>wslc.exe</c>. Ported from
/// Orchard's `LiveContainerBackend` (see ContainerBackend.swift), which talks to the real
/// container XPC client; here every operation instead becomes a `wslc` subcommand run
/// through <see cref="ICommandRunner"/> and its stdout (where present) is parsed as JSON
/// against the existing <c>Models</c> types.
///
/// Real <c>wslc</c> (verified 2.9.x) emits Docker-compatible list/inspect JSON — not Orchard's
/// nested Apple-container shapes. List/inspect/network/stats/disk-usage methods map the wire
/// format into domain models. Remaining unconfirmed flags stay tagged <c>// VERIFY:</c>.
public sealed class WslcCliContainerBackend : IContainerBackend
{
    private readonly ICommandRunner _runner;
    private readonly string _wslcBinaryPath;

    /// Accumulates pseudo-CPU usec from Docker-style percent stats so
    /// <see cref="StatsMath.ComputeSample"/> can still compute a percent from deltas.
    private readonly ConcurrentDictionary<string, (long CpuUsec, long LastTickMs)> _cpuAccum = new();

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public WslcCliContainerBackend(ICommandRunner runner, string wslcBinaryPath)
    {
        _runner = runner;
        _wslcBinaryPath = wslcBinaryPath;
    }

    // MARK: - Containers

    public async Task<IReadOnlyList<Container>> ListContainersAsync(CancellationToken ct = default)
    {
        // Real wslc: `container ps`/`list`/`ls` with `--format json` returns slim Docker rows
        //   { Id, Name, Image, State (int), Ports, CreatedAt, StateChangedAt }
        // — not the nested Orchard Container shape. Map via MapWslcContainerListRow.
        var result = await RunAsync(["container", "ps", "--all", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container ps", result.ExitCode, result.Stderr);

        return ParseJsonArrayOrLines<WslcContainerListRow>(result.Stdout, "container list")
            .Select(MapWslcContainerListRow)
            .Where(c => !MachineBackingContainer.IsMachine(c.Configuration.Labels))
            .ToList();
    }

    public async Task StopContainerAsync(string id, CancellationToken ct = default)
    {
        // Confirmed subcommand.
        var result = await RunAsync(["container", "stop", id], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container stop", result.ExitCode, result.Stderr);
    }

    public async Task KillContainerAsync(string id, int signal, CancellationToken ct = default)
    {
        // Real wslc: `container kill --signal <n|NAME> <id>` (verified).
        var result = await RunAsync(["container", "kill", "--signal", signal.ToString(), id], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container kill", result.ExitCode, result.Stderr);
    }

    public async Task DeleteContainerAsync(string id, bool force, CancellationToken ct = default)
    {
        // Real wslc: `container remove` (`rm`/`delete` aliases).
        var args = new List<string> { "container", "remove" };
        if (force) args.Add("--force");
        args.Add(id);
        var result = await RunAsync(args, ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container remove", result.ExitCode, result.Stderr);
    }

    public async Task BootstrapAndStartAsync(string id, CancellationToken ct = default)
    {
        // Real wslc: `container start <id|name>` (verified against 2.9.x).
        var result = await RunAsync(["container", "start", id], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container start", result.ExitCode, result.Stderr);
    }

    /// Non-follow snapshot: `ICommandRunner` has no streaming primitive (`RunAsync` only
    /// resolves after the child exits), so a live tail (`container logs -f`) can't be
    /// modeled through this abstraction without changing `ICommandRunner`, which is a shared
    /// file this backend must not modify. Instead this runs a one-shot, non-follow
    /// `container logs <id>` to completion and wraps the captured bytes (stdout, with any
    /// stderr appended) in a `MemoryStream`. Callers that need a true live tail should page
    /// this method on an interval rather than treat the returned stream as infinite.
    public async Task<Stream> ContainerLogsAsync(string id, CancellationToken ct = default)
    {
        // VERIFY: `container logs` (Docker-familiar mirror per task brief).
        var result = await RunAsync(["container", "logs", id], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container logs", result.ExitCode, result.Stderr);

        var text = result.Stdout ?? "";
        if (!string.IsNullOrEmpty(result.Stderr)) text += result.Stderr;
        return new MemoryStream(Encoding.UTF8.GetBytes(text));
    }

    public async Task<ContainerStats> StatsAsync(string id, CancellationToken ct = default)
    {
        // Real wslc: Docker-style one-shot JSON (no --no-stream flag), e.g.
        // { ID, Name, CPUPerc: "0.00%", MemUsage: "0 B / 0 B", NetIO, BlockIO, PIDs }.
        var result = await RunAsync(["container", "stats", id, "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container stats", result.ExitCode, result.Stderr);

        var row = ParseJsonObjectOrFirstOfArray<WslcStatsRow>(result.Stdout, "container stats");
        if (row is null) throw OrchardWinException.DecodeFailed("container stats");
        return MapWslcStatsRow(row, id);
    }

    public async Task CreateContainerAsync(ContainerCreateSpec spec, CancellationToken ct = default)
    {
        // Mirror Orchard's createContainer: read the image's entrypoint/cmd/env/workingDir/
        // user, merge with the spec's overrides, then start. Reuses InspectImageAsync
        // instead of duplicating the image-inspect call/parse.
        var inspection = await InspectImageAsync(spec.ImageRef, ct);
        var variant = inspection.Variants.FirstOrDefault();

        var mergedEnv = new List<string>(variant?.Env ?? []);
        mergedEnv.AddRange(spec.Environment);

        var processArgs = ResolveProcessArguments(variant?.Entrypoint, variant?.Cmd, spec.CommandOverride);
        if (processArgs.Count == 0) throw OrchardWinException.NoEntrypoint();

        var workingDirectory = string.IsNullOrEmpty(spec.WorkingDirectory)
            ? (string.IsNullOrEmpty(variant?.WorkingDir) ? "/" : variant!.WorkingDir!)
            : spec.WorkingDirectory;
        var user = string.IsNullOrEmpty(variant?.User) ? null : variant!.User;

        // VERIFY: `run -d` for detached create+start, and every flag below, mirror `docker
        // run` conventions - the flag names/shorthands are a best-effort guess, not verified
        // against `wslc run --help`.
        var args = new List<string> { "run", "-d", "--name", spec.Id };
        if (spec.AutoRemove) args.Add("--rm");
        foreach (var env in mergedEnv) { args.Add("-e"); args.Add(env); }
        foreach (var vol in spec.Volumes)
        {
            var mount = $"{vol.HostPath}:{vol.ContainerPath}";
            if (vol.ReadOnly) mount += ":ro";
            args.Add("-v");
            args.Add(mount);
        }
        foreach (var port in spec.PublishedPorts)
        {
            args.Add("-p");
            args.Add($"{port.HostPort}:{port.ContainerPort}/{port.TransportProtocol}");
        }
        if (!string.IsNullOrEmpty(spec.NetworkName)) { args.Add("--network"); args.Add(spec.NetworkName); }
        // VERIFY: `--dns-search` is Docker's flag for a search domain; Apple's per-container
        // DNS "domain" field isn't quite the same concept, but it's the closest docker-style
        // equivalent available without a dedicated `--dns-domain` flag.
        if (!string.IsNullOrEmpty(spec.DnsDomain)) { args.Add("--dns-search"); args.Add(spec.DnsDomain); }
        foreach (var label in spec.Labels) { args.Add("--label"); args.Add($"{label.Key}={label.Value}"); }
        if (!string.IsNullOrEmpty(workingDirectory)) { args.Add("-w"); args.Add(workingDirectory); }
        if (user is not null) { args.Add("-u"); args.Add(user); }
        // Apple's process model is a single executable + argument vector with no separate
        // entrypoint/cmd split; `--entrypoint` pins the resolved executable and the
        // remaining tokens ride along as its arguments, so the merge in
        // ResolveProcessArguments still fully determines what actually runs.
        args.Add("--entrypoint");
        args.Add(processArgs[0]);
        args.Add(spec.ImageRef);
        if (processArgs.Count > 1) args.AddRange(processArgs.Skip(1));

        var result = await RunAsync(args, ct);
        if (result.Failed) throw OrchardWinException.CliFailed("run", result.ExitCode, result.Stderr);
    }

    /// Combine an image's entrypoint and cmd with a user command override into the final
    /// process argument vector. Override replaces cmd; entrypoint is always prefixed. Ported
    /// 1:1 from Orchard's free function `resolveProcessArguments` (ContainerBackend.swift).
    private static List<string> ResolveProcessArguments(IReadOnlyList<string>? imageEntrypoint, IReadOnlyList<string>? imageCmd, IReadOnlyList<string> commandOverride)
    {
        var processArgs = new List<string>();
        if (imageEntrypoint is { Count: > 0 })
        {
            processArgs = [.. imageEntrypoint];
        }
        if (commandOverride.Count > 0)
        {
            if (processArgs.Count == 0)
            {
                processArgs = [.. commandOverride];
            }
            else
            {
                processArgs.AddRange(commandOverride);
            }
        }
        else if (imageCmd is { Count: > 0 } && (processArgs.Count == 0 || imageEntrypoint is not null))
        {
            processArgs.AddRange(imageCmd);
        }
        return processArgs;
    }

    // MARK: - Images

    public async Task<IReadOnlyList<ContainerImage>> ListImagesAsync(CancellationToken ct = default)
    {
        // Real wslc (verified on Windows): `image ls` is an alias for `image list`, and
        // `--format json` emits Docker-style rows
        //   { "Created", "Id", "Repository", "Size", "Tag" }
        // — not Orchard's nested { reference, descriptor: { digest, mediaType, size } }.
        // Mapping into ContainerImage happens in MapWslcImageListRow; deserializing the wire
        // shape straight into ContainerImage used to fail on the required properties and
        // ParseJsonArrayOrLines swallowed that into an empty list (images page looked blank).
        var result = await RunAsync(["image", "ls", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("image ls", result.ExitCode, result.Stderr);
        return ParseJsonArrayOrLines<WslcImageListRow>(result.Stdout, "image list")
            .Select(MapWslcImageListRow)
            .ToList();
    }

    public async Task PullImageAsync(string reference, IProgress<ImagePullProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new ImagePullProgress { ImageName = reference, Status = PullStatus.Pulling, Progress = 0, Message = "Pulling image..." });

        // VERIFY: `image pull` (Docker-familiar mirror per task brief). `ICommandRunner` has
        // no incremental-output hook, so a live percentage readout (as `docker pull` prints
        // per-layer) isn't obtainable through this abstraction - only report what can be
        // recovered after the process has already exited: a best-effort scan of the
        // captured stdout for a trailing percentage, else just Pulling -> Completed/Failed.
        var result = await RunAsync(["image", "pull", reference], ct);

        if (result.Failed)
        {
            progress?.Report(new ImagePullProgress { ImageName = reference, Status = PullStatus.Failed, Progress = 0, Message = result.Stderr ?? "Pull failed" });
            throw OrchardWinException.CliFailed("image pull", result.ExitCode, result.Stderr);
        }

        var percent = TryParseTrailingPercent(result.Stdout);
        if (percent is not null)
        {
            progress?.Report(new ImagePullProgress { ImageName = reference, Status = PullStatus.Pulling, Progress = percent.Value, Message = "Pulling image..." });
        }
        progress?.Report(new ImagePullProgress { ImageName = reference, Status = PullStatus.Completed, Progress = 1, Message = "Pull completed successfully" });
    }

    /// Best-effort scan for a "NN%" (or "NN.N%") token anywhere in pull output, taking the
    /// last match found (progress lines overwrite earlier ones in most CLI pull UIs). Returns
    /// null - not zero - when nothing matches, so callers can tell "unknown" from "0%".
    private static double? TryParseTrailingPercent(string? stdout)
    {
        if (string.IsNullOrEmpty(stdout)) return null;
        double? last = null;
        var span = stdout.AsSpan();
        var i = 0;
        while (i < span.Length)
        {
            if (span[i] == '%')
            {
                var start = i;
                while (start > 0 && (char.IsDigit(span[start - 1]) || span[start - 1] == '.')) start--;
                if (start < i && double.TryParse(span[start..i], out var value))
                {
                    last = Math.Clamp(value / 100.0, 0, 1);
                }
            }
            i++;
        }
        return last;
    }

    public async Task DeleteImageAsync(string reference, CancellationToken ct = default)
    {
        // VERIFY: `image rm` (Docker-familiar mirror per task brief).
        var result = await RunAsync(["image", "rm", reference], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("image rm", result.ExitCode, result.Stderr);
    }

    public async Task<ImageInspection> InspectImageAsync(string reference, CancellationToken ct = default)
    {
        // Real wslc (verified): `image inspect` has no `--format` flag (passing it errors)
        // and always prints a Docker-style JSON array of image objects (Id, RepoTags,
        // RepoDigests, Size, Architecture, Os, Config, ...). Map that into ImageInspection.
        var result = await RunAsync(["image", "inspect", reference], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("image inspect", result.ExitCode, result.Stderr);

        var row = ParseJsonObjectOrFirstOfArray<WslcImageInspectRow>(result.Stdout, "image inspection");
        if (row is null) throw OrchardWinException.DecodeFailed("image inspection");
        return MapWslcImageInspect(row, reference);
    }

    // MARK: - Networks

    public async Task<IReadOnlyList<ContainerNetwork>> ListNetworksAsync(CancellationToken ct = default)
    {
        // Real wslc: { Driver, Id, Name } — not Orchard's nested config/status shape.
        var result = await RunAsync(["network", "ls", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("network ls", result.ExitCode, result.Stderr);
        return ParseJsonArrayOrLines<WslcNetworkListRow>(result.Stdout, "network list")
            .Select(MapWslcNetworkListRow)
            .ToList();
    }

    public async Task CreateNetworkAsync(string name, string? subnet, Dictionary<string, string> labels, CancellationToken ct = default)
    {
        // VERIFY: `network create` plus `--subnet`/`--label` flags (Docker-familiar mirror
        // per task brief).
        var args = new List<string> { "network", "create", name };
        if (!string.IsNullOrEmpty(subnet)) { args.Add("--subnet"); args.Add(subnet); }
        foreach (var label in labels) { args.Add("--label"); args.Add($"{label.Key}={label.Value}"); }

        var result = await RunAsync(args, ct);
        if (result.Failed && !OrchardWinException.IsAlreadyExistsError(result.Stderr ?? ""))
            throw OrchardWinException.CliFailed("network create", result.ExitCode, result.Stderr);
    }

    public async Task DeleteNetworkAsync(string id, CancellationToken ct = default)
    {
        // VERIFY: `network rm` (Docker-familiar mirror per task brief).
        var result = await RunAsync(["network", "rm", id], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("network rm", result.ExitCode, result.Stderr);
    }

    // MARK: - System

    public async Task<SystemHealthInfo> PingAsync(CancellationToken ct = default)
    {
        // Real wslc has no `system info`; `version` prints e.g. "wslc 2.9.3.0".
        var result = await RunAsync(["version"], ct);
        if (result.Failed) throw OrchardWinException.ServiceUnavailable();

        var text = (result.Stdout ?? result.Stderr ?? "").Trim();
        if (string.IsNullOrEmpty(text)) throw OrchardWinException.ServiceUnavailable();

        // Prefer the trailing version token.
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var version = parts.Length > 0 ? parts[^1] : text;
        return new SystemHealthInfo(version);
    }

    public async Task<SystemDiskUsage> DiskUsageAsync(CancellationToken ct = default)
    {
        // Real wslc has no `system df`. Synthesize dashboard tiles from image list +
        // container list + volume list (sizes only known for images today).
        var images = await ListImagesAsync(ct);
        var containers = await ListContainersAsync(ct);
        var volumeResult = await RunAsync(["volume", "ls", "--format", "json"], ct);
        IReadOnlyList<WslcVolumeListRow> volumes = volumeResult.Failed
            ? Array.Empty<WslcVolumeListRow>()
            : ParseJsonArrayOrLines<WslcVolumeListRow>(volumeResult.Stdout, "volume list");

        var imageBytes = images.Sum(i => i.Descriptor.Size);
        var running = containers.Count(c =>
            string.Equals(c.Status, "running", StringComparison.OrdinalIgnoreCase));
        var exited = containers.Count - running;

        // Unique image digests (same Id can appear once per tag).
        var uniqueImageCount = images
            .Select(i => i.Descriptor.Digest)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (uniqueImageCount == 0) uniqueImageCount = images.Count;

        return new SystemDiskUsage
        {
            Images = new DiskUsageSection
            {
                Total = uniqueImageCount,
                Active = uniqueImageCount,
                SizeInBytes = imageBytes,
                Reclaimable = 0,
            },
            Containers = new DiskUsageSection
            {
                Total = containers.Count,
                Active = running,
                SizeInBytes = 0,
                Reclaimable = exited,
            },
            Volumes = new DiskUsageSection
            {
                Total = volumes.Count,
                Active = volumes.Count,
                SizeInBytes = 0,
                Reclaimable = 0,
            },
        };
    }

    // MARK: - wslc wire shapes (Docker-compatible, verified against wslc 2.9.x)

    /// Wire row from `wslc image ls --format json`. Property names match the real CLI
    /// (PascalCase); PropertyNameCaseInsensitive handles either casing.
    private sealed class WslcImageListRow
    {
        public long Created { get; set; }
        public string Id { get; set; } = "";
        public string Repository { get; set; } = "";
        public long Size { get; set; }
        public string Tag { get; set; } = "";
    }

    private sealed class WslcImageInspectRow
    {
        public string Id { get; set; } = "";
        public string Architecture { get; set; } = "";
        public string Os { get; set; } = "linux";
        public long Size { get; set; }
        public List<string>? RepoTags { get; set; }
        public List<string>? RepoDigests { get; set; }
        public WslcImageConfig? Config { get; set; }
    }

    private sealed class WslcImageConfig
    {
        public List<string>? Cmd { get; set; }
        public List<string>? Entrypoint { get; set; }
        public List<string>? Env { get; set; }
        public string? WorkingDir { get; set; }
        public string? User { get; set; }
    }

    private sealed class WslcContainerListRow
    {
        public long CreatedAt { get; set; }
        public string Id { get; set; } = "";
        public string Image { get; set; } = "";
        public string Name { get; set; } = "";
        public List<string>? Ports { get; set; }
        /// Verified: 1=created, 2=running, 3=exited (wslc 2.9.x).
        public int State { get; set; }
        public long StateChangedAt { get; set; }
    }

    private sealed class WslcNetworkListRow
    {
        public string Driver { get; set; } = "";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class WslcVolumeListRow
    {
        public string Driver { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class WslcStatsRow
    {
        public string ID { get; set; } = "";
        public string Name { get; set; } = "";
        public string CPUPerc { get; set; } = "";
        public string MemUsage { get; set; } = "";
        public string MemPerc { get; set; } = "";
        public string NetIO { get; set; } = "";
        public string BlockIO { get; set; } = "";
        public int PIDs { get; set; }
    }

    /// Map wslc container State int → status string used by IsRunning checks.
    private static string MapWslcContainerState(int state) => state switch
    {
        1 => "created",
        2 => "running",
        3 => "exited",
        4 => "paused",
        5 => "restarting",
        6 => "removing",
        7 => "dead",
        _ => $"state-{state}",
    };

    private static Container MapWslcContainerListRow(WslcContainerListRow row)
    {
        var id = string.IsNullOrEmpty(row.Id) ? row.Name : row.Id;
        var name = string.IsNullOrEmpty(row.Name) ? id : row.Name;
        var imageRef = string.IsNullOrEmpty(row.Image) ? "unknown" : row.Image;

        return new Container
        {
            Status = MapWslcContainerState(row.State),
            Configuration = new ContainerConfiguration
            {
                Id = id,
                Hostname = name,
                RuntimeHandler = "wslc",
                InitProcess = new InitProcess
                {
                    Executable = "",
                    Arguments = [],
                    WorkingDirectory = "/",
                },
                Platform = new Platform
                {
                    Os = "linux",
                    Architecture = "amd64",
                },
                Image = new ImageReference
                {
                    Reference = imageRef,
                    Descriptor = new ImageDescriptor
                    {
                        Digest = "",
                        MediaType = "application/vnd.oci.image.index.v1+json",
                        Size = 0,
                    },
                },
                Resources = new Resources { Cpus = 0, MemoryInBytes = 0 },
                Labels = new Dictionary<string, string>(),
                Mounts = [],
                PublishedPorts = [],
                Sysctls = new Dictionary<string, string>(),
            },
            Networks = [],
        };
    }

    private static ContainerNetwork MapWslcNetworkListRow(WslcNetworkListRow row)
    {
        // Prefer human name as Id for delete/display (wslc accepts name); keep hash in labels.
        var name = string.IsNullOrEmpty(row.Name) ? row.Id : row.Name;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(row.Driver)) labels["driver"] = row.Driver;
        if (!string.IsNullOrEmpty(row.Id)) labels["id"] = row.Id;

        return new ContainerNetwork
        {
            Id = name,
            State = "active",
            Config = new NetworkConfig { Id = name, Labels = labels },
            Status = new NetworkStatus
            {
                Gateway = null,
                Address = string.IsNullOrEmpty(row.Driver) ? null : row.Driver,
            },
            IsHostOnly = string.Equals(row.Driver, "host", StringComparison.OrdinalIgnoreCase),
        };
    }

    private ContainerStats MapWslcStatsRow(WslcStatsRow row, string requestedId)
    {
        var id = string.IsNullOrEmpty(row.ID) ? requestedId : row.ID;
        ParseSlashPair(row.MemUsage, out var memUsed, out var memLimit);
        ParseSlashPair(row.NetIO, out var netRx, out var netTx);
        ParseSlashPair(row.BlockIO, out var blkRead, out var blkWrite);
        var cpuPerc = ParsePercent(row.CPUPerc);

        // Accumulate CPU usec from instantaneous percent so StatsMath can derive % from deltas.
        var now = Environment.TickCount64;
        var accum = _cpuAccum.AddOrUpdate(
            id,
            _ => ((long)(cpuPerc / 100.0 * 1_000_000), now),
            (_, prev) =>
            {
                var elapsedSec = Math.Max(0.001, (now - prev.LastTickMs) / 1000.0);
                var delta = (long)(cpuPerc / 100.0 * elapsedSec * 1_000_000);
                return (prev.CpuUsec + Math.Max(0, delta), now);
            });

        return new ContainerStats
        {
            Id = id,
            CpuUsageUsec = accum.CpuUsec,
            MemoryUsageBytes = memUsed,
            MemoryLimitBytes = memLimit,
            NetworkRxBytes = netRx,
            NetworkTxBytes = netTx,
            BlockReadBytes = blkRead,
            BlockWriteBytes = blkWrite,
            NumProcesses = row.PIDs,
        };
    }

    private static double ParsePercent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var s = text.Trim().TrimEnd('%');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0, 100)
            : 0;
    }

    /// Parse Docker "a / b" human sizes (e.g. "1.5MiB / 2GiB", "0 B / 0 B").
    private static void ParseSlashPair(string? text, out long left, out long right)
    {
        left = 0;
        right = 0;
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length >= 1) left = ParseHumanSize(parts[0]);
        if (parts.Length >= 2) right = ParseHumanSize(parts[1]);
    }

    private static readonly Regex HumanSizeRegex = new(
        @"^\s*([0-9]*\.?[0-9]+)\s*([kKmMgGtTpP]?i?[bB])?\s*$",
        RegexOptions.Compiled);

    private static long ParseHumanSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = HumanSizeRegex.Match(text);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return 0;
        var unit = m.Groups[2].Success ? m.Groups[2].Value.ToLowerInvariant() : "b";
        double mult = unit switch
        {
            "b" => 1,
            "kb" or "k" => 1000,
            "kib" or "ki" => 1024,
            "mb" or "m" => 1_000_000,
            "mib" or "mi" => 1024d * 1024,
            "gb" or "g" => 1_000_000_000,
            "gib" or "gi" => 1024d * 1024 * 1024,
            "tb" or "t" => 1_000_000_000_000,
            "tib" or "ti" => 1024d * 1024 * 1024 * 1024,
            "pb" or "p" => 1_000_000_000_000_000,
            "pib" or "pi" => 1024d * 1024 * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)Math.Round(n * mult);
    }

    private static ContainerImage MapWslcImageListRow(WslcImageListRow row)
    {
        var repo = string.IsNullOrWhiteSpace(row.Repository) || row.Repository == "<none>"
            ? null
            : row.Repository;
        var tag = string.IsNullOrWhiteSpace(row.Tag) || row.Tag == "<none>"
            ? null
            : row.Tag;

        // Prefer repository:tag; fall back to bare repo, then image id for dangling images.
        var reference = (repo, tag) switch
        {
            (not null, not null) => $"{repo}:{tag}",
            (not null, null) => repo,
            _ => string.IsNullOrEmpty(row.Id) ? "unknown" : row.Id,
        };

        return new ContainerImage
        {
            Reference = reference,
            Descriptor = new ContainerImageDescriptor
            {
                Digest = string.IsNullOrEmpty(row.Id) ? reference : row.Id,
                // wslc list rows don't carry a media type; a generic OCI index type is fine
                // for display (ImageInspection fills in a better value when selected).
                MediaType = "application/vnd.oci.image.index.v1+json",
                Size = row.Size,
            },
        };
    }

    private static ImageInspection MapWslcImageInspect(WslcImageInspectRow row, string requestedReference)
    {
        var name = row.RepoTags is { Count: > 0 } tags
            ? tags[0]
            : (string.IsNullOrEmpty(requestedReference) ? row.Id : requestedReference);

        var digest = row.RepoDigests is { Count: > 0 } digests
            ? digests[0]
            : row.Id;

        var os = string.IsNullOrEmpty(row.Os) ? "linux" : row.Os;
        var arch = string.IsNullOrEmpty(row.Architecture) ? "unknown" : row.Architecture;
        var config = row.Config;

        return new ImageInspection
        {
            Name = name,
            Digest = digest,
            MediaType = "application/vnd.oci.image.config.v1+json",
            Size = row.Size,
            Variants =
            [
                new ImageInspectionVariant
                {
                    Platform = $"{os}/{arch}",
                    Size = row.Size,
                    Entrypoint = config?.Entrypoint,
                    Cmd = config?.Cmd,
                    Env = config?.Env,
                    WorkingDir = config?.WorkingDir,
                    User = string.IsNullOrEmpty(config?.User) ? null : config!.User,
                },
            ],
        };
    }

    // MARK: - Process/JSON plumbing

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
        _runner.RunAsync(_wslcBinaryPath, arguments, ct);

    /// Parse stdout as either a JSON array, or NDJSON (one object per line, as several
    /// Docker-family CLIs emit for `--format json` list commands) - whichever the output
    /// looks like. Malformed/empty output degrades to an empty list rather than throwing, so
    /// a single unparsable entry (or an entirely wrong assumed shape) doesn't take down the
    /// whole list view - the same "log and degrade" behaviour Orchard's list loaders use for
    /// CLI failures.
    private static List<T> ParseJsonArrayOrLines<T>(string? stdout, string what)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];
        var trimmed = stdout.Trim();

        if (trimmed.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<List<T>>(trimmed, CaseInsensitiveJson) ?? [];
            }
            catch (JsonException)
            {
                Log.Containers.Error($"{what}: could not decode JSON array: {Preview(trimmed)}");
                return [];
            }
        }

        // NDJSON: one JSON object per non-empty line.
        var result = new List<T>();
        foreach (var line in trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var item = JsonSerializer.Deserialize<T>(line, CaseInsensitiveJson);
                if (item is not null) result.Add(item);
            }
            catch (JsonException)
            {
                Log.Containers.Error($"{what}: could not decode line: {Preview(line)}");
            }
        }
        return result;
    }

    /// Parse stdout as a single JSON object, or the first element of a JSON array (several
    /// Docker-family `inspect`/`stats` commands return a one-element array even for a single
    /// target). Returns null - never throws - on empty/malformed input; callers decide
    /// whether that's a hard failure (DecodeFailed) or a soft one.
    private static T? ParseJsonObjectOrFirstOfArray<T>(string? stdout, string what)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return default;
        var trimmed = stdout.Trim();
        try
        {
            if (trimmed.StartsWith('['))
            {
                var array = JsonSerializer.Deserialize<List<T>>(trimmed, CaseInsensitiveJson);
                return array is { Count: > 0 } ? array[0] : default;
            }
            return JsonSerializer.Deserialize<T>(trimmed, CaseInsensitiveJson);
        }
        catch (JsonException)
        {
            Log.Containers.Error($"{what}: could not decode JSON: {Preview(trimmed)}");
            return default;
        }
    }

    private static string Preview(string text) => text.Length > 200 ? text[..200] : text;
}
