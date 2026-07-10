using System.Text;
using System.Text.Json;
using OrchardWin.Core.Models;

namespace OrchardWin.Core.Services.Backends;

/// <see cref="IContainerBackend"/> backed by shelling out to <c>wslc.exe</c>. Ported from
/// Orchard's `LiveContainerBackend` (see ContainerBackend.swift), which talks to the real
/// container XPC client; here every operation instead becomes a `wslc` subcommand run
/// through <see cref="ICommandRunner"/> and its stdout (where present) is parsed as JSON
/// against the existing <c>Models</c> types.
///
/// Confirmed subcommands (from the Build 2026 preview docs / task brief): `run`, `image ls`,
/// `container ps`, `container stop`. Every other subcommand/flag below is a best-effort
/// mirror of Docker CLI conventions and is tagged `// VERIFY:` - grep for that marker once
/// `wslc --help` / `wslc <subcommand> --help` is available on real Windows and fix up any
/// that are wrong. No unverified command is allowed to crash the app: every call funnels
/// through <see cref="RunAsync"/>/<see cref="RunJsonAsync{T}"/> which wrap failures in
/// <see cref="OrchardWinException"/> and let callers decide how to degrade.
public sealed class WslcCliContainerBackend : IContainerBackend
{
    private readonly ICommandRunner _runner;
    private readonly string _wslcBinaryPath;

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
        // VERIFY: `container ps` is confirmed to exist; the `--all`/`--format json` flags and
        // the assumption that it returns the *full* Container shape (not a slimmed-down
        // docker-ps-style projection) are guesses. If `ps` turns out to return only
        // id/name/status, switch this to `ps` for ids followed by `container inspect <ids...>`
        // for full detail, mirroring how ListImagesAsync/InspectImageAsync are split.
        var result = await RunAsync(["container", "ps", "--all", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container ps", result.ExitCode, result.Stderr);

        var containers = ParseJsonArrayOrLines<Container>(result.Stdout, "container list");
        return containers
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
        // VERIFY: `container kill` with `--signal <n>` mirrors `docker kill --signal=N id`;
        // Orchard's own client accepts the signal as a name/number string too.
        var result = await RunAsync(["container", "kill", "--signal", signal.ToString(), id], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container kill", result.ExitCode, result.Stderr);
    }

    public async Task DeleteContainerAsync(string id, bool force, CancellationToken ct = default)
    {
        // VERIFY: `container rm` (Docker-familiar mirror per task brief).
        var args = new List<string> { "container", "rm" };
        if (force) args.Add("--force");
        args.Add(id);
        var result = await RunAsync(args, ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container rm", result.ExitCode, result.Stderr);
    }

    public async Task BootstrapAndStartAsync(string id, CancellationToken ct = default)
    {
        // Orchard's bootstrapAndStart is the two-phase "create, then bootstrap+start" second
        // half, used to (re)start a container that already exists but isn't running. The
        // CLI-shelling equivalent of that is simply starting the existing container.
        // VERIFY: `container start` - not in the confirmed subcommand list.
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
        // VERIFY: `container stats --no-stream --format json`, and critically the assumed
        // JSON shape - a flat object with camelCase keys matching ContainerStats field names
        // 1:1 (id, cpuUsageUsec, memoryUsageBytes, memoryLimitBytes, blockReadBytes,
        // blockWriteBytes, networkRxBytes, networkTxBytes, numProcesses), mirroring Apple's
        // raw ContainerResource.ContainerStats shape that Orchard's mapContainerStats reads
        // from. Docker's own `docker stats --format json` instead emits human-formatted
        // strings (e.g. "5.3MiB / 7.775GiB") which would need unit parsing - if wslc mirrors
        // that instead of raw byte counts, this needs a text-parsing rewrite.
        var result = await RunAsync(["container", "stats", id, "--no-stream", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("container stats", result.ExitCode, result.Stderr);

        var stats = ParseJsonObjectOrFirstOfArray<ContainerStats>(result.Stdout, "container stats");
        if (stats is null) throw OrchardWinException.DecodeFailed("container stats");
        return stats.Id == id ? stats : stats.With(id);
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
        // VERIFY: `network ls --format json` (Docker-familiar mirror per task brief).
        var result = await RunAsync(["network", "ls", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("network ls", result.ExitCode, result.Stderr);
        return ParseNetworks(result.Stdout);
    }

    /// `ContainerNetwork.IsHostOnly` is `[JsonIgnore]`d - Orchard derives it at mapping time
    /// from the network's mode (`resource.configuration.mode == .hostOnly`), not from a wire
    /// field the model itself carries. Deserialize the wire fields normally, then separately
    /// peek the raw JSON for a mode-ish string to fill in IsHostOnly.
    /// VERIFY: the field name/location ("mode" at the top level or nested under "config") and
    /// the token spelling ("host-only" / "hostOnly" / "host_only" / "isolated") are all
    /// guesses - there is no confirmed wslc network JSON to check this against.
    private static List<ContainerNetwork> ParseNetworks(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return [];
        var trimmed = stdout.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var elements = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : new[] { doc.RootElement }.AsEnumerable();

            var result = new List<ContainerNetwork>();
            foreach (var element in elements)
            {
                var network = element.Deserialize<ContainerNetwork>(CaseInsensitiveJson);
                if (network is null) continue;
                result.Add(network with { IsHostOnly = LooksHostOnly(element) });
            }
            return result;
        }
        catch (JsonException)
        {
            Log.Containers.Error($"network ls: could not decode wslc output: {Preview(trimmed)}");
            return [];
        }
    }

    private static bool LooksHostOnly(JsonElement element)
    {
        string? mode = null;
        if (element.TryGetProperty("mode", out var modeProp) && modeProp.ValueKind == JsonValueKind.String)
            mode = modeProp.GetString();
        else if (element.TryGetProperty("config", out var configProp) && configProp.ValueKind == JsonValueKind.Object
                 && configProp.TryGetProperty("mode", out var nestedMode) && nestedMode.ValueKind == JsonValueKind.String)
            mode = nestedMode.GetString();

        if (mode is null) return false;
        return mode.Equals("host-only", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("hostOnly", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("host_only", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("isolated", StringComparison.OrdinalIgnoreCase);
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
        // There is no `docker ping` equivalent; `system info` is used as the health probe
        // (a successful, parseable response means the service is reachable), matching how
        // Orchard's ping() surfaces the running apiServerVersion.
        // VERIFY: `system info --format json` and the field name carrying a version string
        // (tried: "version", "serverVersion", "apiServerVersion").
        var result = await RunAsync(["system", "info", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.ServiceUnavailable();

        var version = TryExtractVersion(result.Stdout) ?? "unknown";
        return new SystemHealthInfo(version);
    }

    private static string? TryExtractVersion(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        try
        {
            using var doc = JsonDocument.Parse(stdout.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var key in new[] { "apiServerVersion", "version", "serverVersion" })
            {
                if (doc.RootElement.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    return prop.GetString();
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<SystemDiskUsage> DiskUsageAsync(CancellationToken ct = default)
    {
        // VERIFY: `system df --format json` (best-effort mirror per task brief), and the
        // assumed flat shape { containers: {...}, images: {...}, volumes: {...} } with each
        // section's fields matching DiskUsageSection (active/reclaimable/sizeInBytes/total)
        // 1:1 camelCase, mirroring Orchard's mapDiskUsageStats/mapResourceUsage.
        var result = await RunAsync(["system", "df", "--format", "json"], ct);
        if (result.Failed) throw OrchardWinException.CliFailed("system df", result.ExitCode, result.Stderr);

        var usage = ParseJsonObjectOrFirstOfArray<SystemDiskUsage>(result.Stdout, "disk usage");
        if (usage is null) throw OrchardWinException.DecodeFailed("disk usage");
        return usage;
    }

    // MARK: - wslc image wire shapes (Docker-compatible)

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
