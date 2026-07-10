using OrchardWin.Core.Services.Backends;

namespace OrchardWin.Core.Services;

/// Constructs and wires the per-domain services and owns their lifetime. Not a facade -
/// consumers (ViewModels) observe the individual services directly; this only holds them and
/// the cross-service callbacks Orchard's `AppServices.init` used to wire in one place. Ported
/// 1:1 from Orchard's `AppServices.swift` composition root.
public sealed class AppServices : IDisposable
{
    public AlertCenter AlertCenter { get; }
    public SettingsStore Settings { get; }
    public TerminalLauncher TerminalLauncher { get; }
    public BuilderService BuilderService { get; }
    public NetworkService NetworkService { get; }
    public ImageService ImageService { get; }
    public StatsService StatsService { get; }
    public DnsService DnsService { get; }
    public SystemService SystemService { get; }
    public ContainerListService ContainerListService { get; }
    public MachineService MachineService { get; }
    public ModelService ModelService { get; }
    public ModelServerService ModelServerService { get; }

    /// Shared wslc-backed container API (lists, inspect attachments, create, …).
    public IContainerBackend ContainerBackend { get; }

    /// The services for app launch: the live backend, wired the same way every time. Orchard
    /// has a DEBUG/UITest branch here that swaps in an in-memory stub backend for its XCUITest
    /// smoke suite; there is no equivalent Windows UI-test harness yet, so that branch isn't
    /// ported - add one the same way (an `IContainerBackend`/`IMachineBackend`/`IModelBackend`
    /// triple of in-memory fakes) if/when this app gets its own UI test suite.
    public static AppServices ForLaunch()
    {
        var services = new AppServices();
        services.StatsService.Activate();
        return services;
    }

    public AppServices(
        IContainerBackend? backend = null,
        IMachineBackend? machineBackend = null,
        IModelBackend? modelBackend = null,
        IModelServerEngine? modelServerEngine = null,
        ICommandRunner? runner = null)
    {
        var commandRunner = runner ?? new ProcessCommandRunner();

        AlertCenter = new AlertCenter();
        Settings = new SettingsStore(AlertCenter);
        TerminalLauncher = new TerminalLauncher(Settings, AlertCenter);

        // NOTE: WslcCliContainerBackend/WslMachineBackend take the wslc/wsl binary path as a
        // fixed string, resolved once here from Settings.SafeContainerBinaryPath() - unlike
        // SystemService/DnsService/BuilderService, which hold a SettingsStore reference and
        // re-resolve it on every call. A custom binary path change in Settings therefore takes
        // effect immediately for system/DNS/builder operations but only after an app restart
        // for container/machine operations. Worth tightening (change the constructor to accept
        // a `Func<string>` instead) if that inconsistency proves annoying in practice - flagged
        // here rather than silently left for someone to puzzle out later.
        var wslcPath = Settings.SafeContainerBinaryPath();
        var containerBackend = backend ?? new WslcCliContainerBackend(commandRunner, wslcPath);
        ContainerBackend = containerBackend;

        var builderService = new BuilderService(commandRunner, Settings, AlertCenter);
        BuilderService = builderService;

        NetworkService = new NetworkService(containerBackend, AlertCenter);
        ImageService = new ImageService(containerBackend, AlertCenter);
        DnsService = new DnsService(commandRunner, AlertCenter);
        SystemService = new SystemService(containerBackend, commandRunner, Settings, AlertCenter);

        var containerListService = new ContainerListService(containerBackend, AlertCenter);
        ContainerListService = containerListService;

        StatsService = new StatsService(containerBackend, AlertCenter, containerListService);

        var machineBackendImpl = machineBackend ?? new WslMachineBackend(commandRunner, "wsl.exe", wslcPath);
        var machineService = new MachineService(machineBackendImpl, AlertCenter);
        MachineService = machineService;

        var modelBackendImpl = modelBackend ?? new HttpModelBackend();
        ModelService = new ModelService(modelBackendImpl);

        var serverEngine = modelServerEngine ?? new OllamaServerEngine();
        ModelServerService = new ModelServerService(serverEngine, AlertCenter);

        // Cross-service wiring - mirrors the closures Swift's AppServices.init sets up after
        // constructing every service.

        // ContainerListService is built before StatsService's targets are read, same ordering
        // constraint as the Swift original (a builder-status change should refresh the builder
        // list after any lifecycle action).
        containerListService.ReloadBuilders = () => builderService.LoadBuildersAsync();

        // Stats samples running machines through their backing container (re-keyed to the
        // stable machine id). Supplied lazily so the sampler always sees the current machines.
        StatsService.MachineStatTargets = () =>
            [.. machineService.Machines
                .Where(m => m.IsRunning && m.ContainerId is not null)
                .Select(m => new MachineStatTarget(m.Id, m.ContainerId!, m.Cpus))];

        // System -> containers side effects. There is no DNS <-> System wiring here unlike
        // Orchard: this port's DnsService is self-contained (the hosts file *is* the source of
        // truth for both domains and the default), whereas Orchard's DNS default is a system
        // property SystemService owns - see DnsService's doc comment.
        SystemService.OnSystemStarted = () => containerListService.LoadAsync();
        SystemService.OnSystemStopped = () => containerListService.Containers.Clear();
    }

    /// Stop everything with a process lifetime of its own (managed model servers, the stats
    /// sampling timer) so the app doesn't leak child processes or a running timer past exit.
    /// Call from the App project's shutdown path (e.g. `Window.Closed`), mirroring Orchard's
    /// `NSApplication.willTerminateNotification` handlers.
    public void Dispose()
    {
        ModelServerService.StopAll();
        StatsService.Shutdown();
        StatsService.Dispose();
    }
}
