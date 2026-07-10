# Orchard for Windows — architecture & porting notes

This is a Windows port of [Orchard](https://github.com/andrew-waters/orchard), a native
macOS app for managing Apple `container` workloads, targeted at Microsoft's
[WSL container](https://devblogs.microsoft.com/commandline/wsl-container-is-now-available-for-public-preview/)
public preview (`wslc.exe`, announced at Build 2026) instead.

This file exists because a faithful "convert this to Windows" is not a mechanical
translation — Apple's `container` and WSL containers are architecturally similar (both run
Linux containers inside a lightweight per-workload or per-session VM) but not identical, and
a couple of Orchard's headline features (MLX inference, per-machine kernel swap) have no
Windows equivalent at all. Where the mapping isn't 1:1, that's called out below instead of
being silently glossed over.

## Methodology — how this port was actually approached

Recorded here because the *process*, not just the resulting decisions, is worth keeping:

1. **Read the real source before designing anything.** The macOS repo was cloned locally
   and every `Services/*.swift` file (the business logic) plus a representative sample of
   `Views/Features/*.swift` (the UI shape) was read before a single line of C# was written.
   "Convert this to Windows" only means something once you know precisely what "this" does —
   guessing at Orchard's feature set from its README would have missed load-bearing details
   (e.g. that a "container machine" is *itself* a backing container with a `containerId`,
   which is exactly why `Machine.ContainerId` exists in this port too).
2. **Research the target platform primary sources, not assumptions.** The WSL container
   preview is newer than this model's training data, so its CLI/API surface was fetched from
   Microsoft's own announcement, `wsl.dev/api-reference`, and the WSL docs repo rather than
   inferred from "how Docker probably works." Where those sources only gave partial
   information (class names but not method signatures for the native `Microsoft.WSL.Containers`
   NuGet package), that gap was treated as a hard constraint on the design, not smoothed over.
3. **Prefer the verifiable path over the plausible-looking one.** This is the single biggest
   judgment call in the port: given a documented CLI (`wslc.exe`, confirmed to exist, confirmed
   docker-like) and an undocumented native API (real C# classes exist, but their method
   signatures don't), the CLI was chosen as the primary backend *specifically because it can be
   checked* — a wrong `// VERIFY:`-tagged CLI flag fails loudly at runtime with a clear error;
   a wrong guess at an undocumented API's method signature fails to compile, or worse, compiles
   against a wrong overload and does the wrong thing silently. Every place this port had to
   guess is tagged `// VERIFY:` in the source rather than presented as settled.
4. **Don't force 1:1 mappings that don't exist.** Apple's per-machine kernel swap and MLX
   local inference have no Windows equivalent, full stop. Rather than fake a UI control that
   does nothing (kernel picker) or silently drop a feature without saying so (MLX), both are
   documented as intentional cuts with the reasoning inline, in the section below.
5. **Verify what's verifiable, from where you're standing.** Development happened on macOS,
   with no access to a real Windows/WSL box. Rather than treat that as a reason to skip
   verification entirely, the project is split so the half that *can* be checked from here
   (`OrchardWin.Core`, a plain .NET class library with the actual business logic) *is* checked
   — it's built with `dotnet build` after every batch of changes, not just written and hoped
   over. The half that can't be checked here (`OrchardWin.App`, WinUI 3) is clearly labeled as
   unverified rather than implicitly claimed to work.
6. **Fan out mechanical work, keep judgment calls centralized.** The shared contracts (domain
   models, backend interfaces, the command-runner abstraction, error types) were designed by
   one hand so every downstream piece agrees on them; the bulk per-domain implementation work
   (five parallel workers, then a second wave for the UI layer) was then fanned out against
   those fixed contracts, each with an explicit self-verification step (`dotnet build` must
   stay clean) before being considered done. Cross-cutting architecture decisions were never
   delegated — only their mechanical application was.

## Framework choice

**.NET 10 + WinUI 3** (C#), MVVM via CommunityToolkit.Mvvm. Reasoning:

- Orchard's own stated design philosophy is "native, not a heavyweight cross-platform tool"
  — talk to the OS/runtime directly, no Electron, no CLI screen-scraping where a typed API
  exists. WinUI 3 is the closest Windows analogue to native SwiftUI: Fluent Design, a real
  Win32/WinRT app, not a browser shell.
- The WSL container preview ships **`Microsoft.WSL.Containers`**, a NuGet package with a C#
  projection specifically for building tools like this one — see "Backend: CLI vs. native
  API" below for why this app defaults to the CLI instead, with that package as a
  documented upgrade path.
- A system tray presence (`H.NotifyIcon.WinUI`) replaces Orchard's macOS menu-bar extra.

## Solution layout

```
OrchardWin.sln
src/
  OrchardWin.Core/       net10.0 class library — models, services, backends.
                         Deliberately Windows-API-free except System.Diagnostics.Process
                         (cross-platform) and a couple of path constants — this project
                         restores and builds with plain `dotnet build` on macOS/Linux too,
                         which is how it was verified during development (see below).
  OrchardWin.App/         net10.0-windows10.0.19041.0, WinUI 3. Windows-only — cannot be
                         built from this repo checkout on macOS (see "What was and wasn't
                         verified").
```

### App project contents

```
OrchardWin.App/
  App.xaml(.cs)                  Composition root: builds AppServices once, activates
                                  MainWindow, constructs the tray icon, wires shutdown cleanup.
  MainWindow.xaml(.cs)            NavigationView shell routing to one Page per feature domain.
  TrayIcon.xaml(.cs)               System tray presence (H.NotifyIcon.WinUI) — replaces
                                  Orchard's menu-bar extra; see its own remarks for the
                                  unverified H.NotifyIcon surface and the deliberate
                                  simplifications (no donut-ring gauges, closing the main
                                  window exits the app rather than hiding to tray).
  Controls/
    ListItemRow, StatTile, Sparkline    Shared, reusable across every list/detail page.
  Views/ + ViewModels/
    Dashboard{Page,ViewModel}            System disk usage tiles + container/machine
                                        utilisation tables with Sparkline history.
    Containers{Page,ViewModel},
    RunContainerDialog                   Two-pane list/detail, full lifecycle actions, a
                                        tabbed (Basic/Ports/Volumes/Environment/Advanced)
                                        run dialog with an opt-in local-model bridge section.
    Images{Page,ViewModel},
    ImageSearchDialog                    Two-pane list/detail, Docker Hub search + pull.
    Machines{Page,ViewModel},
    CreateMachineDialog                   Two-pane list/detail (WSL distros), create dialog
                                        with CPU/memory/home-mount/virtualization options.
    Networks{Page,ViewModel},
    AddNetworkDialog                      Network list + create/delete.
    Dns{Page,ViewModel},
    AddDnsDomainDialog                    Hosts-file-backed DNS domain list + create/
                                        delete/set-default (each write prompts UAC once).
    Models{Page,ViewModel},
    CreateModelServerDialog,
    TestModelPromptDialog                 Ollama/LM Studio detection + managed-server
                                        lifecycle + an in-app chat tester.
    Sandboxes{Page,ViewModel}             Derived view over containers wired to a model,
                                        with isolation badges and quick actions. Reachable
                                        both as a NavigationView entry and as a nested tab
                                        inside ModelsPage (mirrors Orchard's "Local AI &
                                        Sandboxes" single-entry grouping).
    Logs{Page,ViewModel}                   Single-pane log viewer (containers + machines),
                                        reduced in scope from Orchard's split-pane
                                        multi-pane MultiLogView — see LogsPage's remarks.
    Settings{Page,ViewModel}               General (terminal, binary path, default DNS
                                        domain) + System (read-only wslc/WSL properties,
                                        kernel version) in one Page reached via
                                        NavigationView's built-in Settings item.
```

Every page follows one navigation contract (a parameterless constructor + `OnNavigatedTo`
casting `e.Parameter` to the shared `AppServices` instance) and one MVVM shape (a thin
`ObservableObject` ViewModel that wires UI-only state — selection, filters, sort, dialog
orchestration — directly to the already-observable Core services, never duplicating state
those services already publish). `DashboardPage`/`DashboardViewModel` is the reference
implementation of both conventions; every other page was written to match it.

## Backend: CLI vs. native API

Orchard talks to Apple's `container` daemon over typed XPC (`ContainerAPIClient`), not by
shelling out — that's the whole point of the "native XPC integration (no CLI shelling)" row
in its own comparison table. The most faithful Windows equivalent would be the
`Microsoft.WSL.Containers` NuGet package (classes: `Session`, `Container`, `Process`,
`WslcService`, plus settings/enum types — see the WSL dev docs at
https://wsl.dev/api-reference/).

This port does **not** use that package as the primary backend. Reason: as of this port,
the package is a public preview with only class/enum *names* documented publicly — no
verified method signatures. Guessing signatures and shipping code that "looks like it
compiles" against an API surface I can't check would be worse than being upfront about the
gap. Instead, every backend (`IContainerBackend`, `IMachineBackend`) is implemented against
**`wslc.exe`**, the confirmed Docker-CLI-compatible tool ("existing muscle memory" per
Microsoft's own framing), via `System.Diagnostics.Process` + `--format json` parsing. This
mirrors the *shape* of Orchard's design (a backend interface with app-owned types, a single
implementation behind it) so swapping in a native-API-backed implementation later is a
localized change, not a rewrite — implement `IContainerBackend`/`IMachineBackend` again
against `Microsoft.WSL.Containers` once you can see its real signatures in Visual Studio's
IntelliSense on Windows, and wire it up in `AppServices`.

**Every CLI subcommand/flag this app calls that wasn't in Microsoft's public examples is
tagged `// VERIFY:`** in the source. Grep for that tag once you're on Windows with
`wslc --help` / `wsl --help` available, and fix any that don't match. The confirmed surface
going in was: `wslc run`, `wslc image ls`, `wslc container ps`, `wslc container stop`, plus
the general claim that the CLI mirrors Docker's command set.

## Feature-by-feature mapping

| Orchard (macOS) | Orchard for Windows | Notes |
|---|---|---|
| Container management (XPC) | `wslc.exe container ...` | Direct mapping. |
| Image management (XPC + Docker Hub search) | `wslc.exe image ...` + Docker Hub HTTP search | Direct mapping; search is unchanged (plain HTTPS call). |
| Container Machines (native XPC, `MachineAPIClient`) | WSL distros (`wsl.exe --list/--terminate/--unregister/--set-default`) + backing `wslc` container | **Reinterpreted**, not 1:1 — see "Machines" below. |
| Networks & DNS | Networks: `wslc network ...`. DNS: Windows hosts file. | DNS is **reinterpreted** — see "DNS" below. |
| Local AI (MLX discovery + launch, Ollama, LM Studio) | Ollama + LM Studio discovery only; launch wraps `ollama.exe` | MLX is Apple Silicon-only — **feature dropped**, not portable. See "Local AI" below. |
| Builder (BuildKit via `container builder`) | `wslc build ...` (best-effort subcommand names) | Unverified surface — tag `// VERIFY:`. |
| Kernel management (set recommended/custom kernel per machine) | Read-only WSL kernel version display | **Feature dropped** — WSL ships one shared kernel per WSL install, not a per-machine swappable one. See "Kernel management". |
| Terminal launcher (Terminal.app / iTerm2 / Ghostty) | Windows Terminal / PowerShell / Command Prompt | Direct mapping to the Windows equivalents. |
| Menu bar extra (rings + container list) | System tray icon + flyout (H.NotifyIcon) | Direct mapping. |
| Sandboxes (containers wired to a local model) | Same concept, same isolation-badge logic | Direct port — `ModelBridge`'s gateway-reachability logic is unchanged; **re-verify the "container's default route is the network gateway, and a loopback-only host server is unreachable from inside a container" assumption against wslc's actual networking mode** (WSL2 has historically used NAT *or* "mirrored" mode depending on config, which changes this). |

### Machines

Apple's container Machine is an init-capable **container** that behaves like a persistent
VM — it boots, gets an IP, has a home-directory mount, and (per Orchard's own model) is
*backed by a container id* under the hood. There is no single WSL primitive that is exactly
this. The closest fit is a **WSL distro** (also a persistent, stateful Linux environment
with its own filesystem, also boots on demand) registered from a container/OCI rootfs,
combined with `wslc` for the backing container's stats/logs — which is why the `Machine`
model keeps a `ContainerId` field, same as Orchard's.

Two real platform differences fell out of this and are **not** papered over in code:

- **Per-machine CPU/memory limits don't exist on WSL2.** `.wslconfig` sets a ceiling for the
  *entire* WSL VM, not per-distro. `SetMachineConfigAsync` writes CPU/memory to the global
  `%UserProfile%\.wslconfig`, and the code comments say so loudly — don't be surprised when
  changing one machine's "cores" setting affects every distro.
- **Creating a machine from an arbitrary container image** needs an export→import round
  trip (`wslc` image export → `wsl --import`) whose exact `wslc` subcommand name is
  unverified — tagged `// VERIFY:` in `WslMachineBackend.CreateMachineAsync`.

### DNS

Orchard's DNS page manages `container system dns` domains — a feature of Apple's
container-network daemon with no Windows/WSL equivalent. This port reimplements the same
*user-facing* feature (map a custom domain to a container, set a default) via the Windows
hosts file (`%SystemRoot%\System32\drivers\etc\hosts`), writing only lines tagged with a
trailing `# orchardwin` marker so it never touches entries the user or another tool added.
Writing the hosts file needs admin rights — every write goes through `RunElevatedAsync`,
which triggers one UAC prompt per action, the direct equivalent of Orchard's AppleScript
admin-privileges prompt.

### Local AI & Sandboxes

MLX is Apple Silicon-only (it's Apple's own ML framework, built on Metal). There is no
Windows build and no meaningful equivalent — porting "launch an `mlx_lm.server` instance"
verbatim would mean shipping a feature that cannot work. Instead:

- **Detection** keeps working exactly as before for Ollama (`:11434`) and LM Studio
  (`:1234`) — both already run natively on Windows, no change needed.
- **Launching your own local model server** now wraps `ollama.exe serve` / `ollama run`
  instead of `mlx_lm.server`. This is a genuine, if imperfect, substitute: Ollama's
  per-invocation model semantics differ slightly from mlx_lm.server's one-model-per-port
  model — see `// VERIFY:` comments in `OllamaServerEngine`.
- Everything downstream of detection (the container↔model bridge, environment injection,
  sandbox isolation badges, the in-app chat tester) is unchanged, since none of it is
  Apple-Silicon-specific.

### Kernel management

Apple's `container system kernel set --recommended/--binary/--tar` lets you swap the guest
kernel per install. WSL ships one Microsoft-maintained kernel per WSL version — there is no
per-machine kernel to set. Rather than fake a picker that does nothing, the Settings page
shows a **read-only** WSL/kernel version readout (`wsl --version`). This is an intentional,
documented feature drop, not an oversight.

## What was and wasn't verified

This port was written on macOS, without access to a Windows machine or the actual
`wslc.exe`/`wsl.exe` preview build. What that means concretely:

- **`OrchardWin.Core` builds clean with `dotnet build` on macOS** (verified during
  development — it's a plain net10.0 class library with no Windows-only APIs). Its business
  logic (rate-math for stats, sandbox isolation detection, the model bridge, hosts-file
  line management, CLI argument construction) is exercised by ordinary C# you can read and,
  ideally, unit test — it was not run against a live `wslc`/`wsl` binary.
- **`OrchardWin.App` (the WinUI 3 project) cannot be built or run from this checkout** — WinUI
  3 requires the Windows App SDK and Windows-only MSBuild targets. It was written to compile
  based on the documented WinUI 3 / CommunityToolkit.Mvvm / H.NotifyIcon APIs, but the first
  build on your Windows machine is the first real compiler check it's had.
- **Every `// VERIFY:` comment marks a `wslc`/`wsl` command, flag, or output shape that was
  inferred from Microsoft's public preview announcement rather than confirmed** against
  real `--help` output or a real JSON response. Search for them first when something doesn't
  work.
- **The `OrchardWin.App` code was cross-checked by hand, member-by-member, against the real
  `OrchardWin.Core` source** (rather than left as "written but never re-read") — every
  `AppServices`/service/model reference in every page, ViewModel, and dialog was verified to
  match a real property/method signature, and every dialog constructor call was checked
  against its actual declared constructor. This is the closest substitute available for a
  real compiler on this platform, not a replacement for one — build it on Windows before
  trusting it. One real bug was caught and fixed this way: `SandboxesPage.xaml.cs` originally
  looked up `Resources["SystemFillColorSuccessBrush"]` from code-behind, which resolves
  against the page's own (empty) `Page.Resources` dictionary, not the Fluent theme
  dictionary merged in `App.xaml` — it's now a literal `SolidColorBrush`, matching the same
  workaround `ContainersPage.xaml.cs`'s `Well()` helper already used for the identical
  pitfall. If you hit a similar `Resources["ThemeKey"]` lookup failing from any code-behind
  added later, this is why — reach for a literal brush or a `{ThemeResource}` in XAML instead.
- **A dedicated threading audit was run after the port was "done", and it caught the most
  serious bug in the codebase**: several Core services raise change notifications from
  thread-pool threads (StatsService's sampler runs on a `System.Threading.Timer`,
  ModelServerService's crash detection fires from `Process.Exited`, ImageService clears pull
  progress from a `Task.Delay` continuation, SystemService refreshes properties via
  `Task.Run`), and every page/dialog/tray handler downstream of them touched WinUI controls
  directly — which throws `RPC_E_WRONG_THREAD` off the dispatcher thread. Orchard's Swift
  originals never faced this because `@MainActor` serialized everything; the port initially
  carried the single-threaded assumption over silently. Fixed by routing every such handler
  through `UiThreadExtensions.RunOnUi` (`DispatcherQueue.TryEnqueue` unless already on the
  UI thread). If you add a new page, follow the same pattern:
  `viewModel.PropertyChanged += (_, _) => DispatcherQueue.RunOnUi(ApplyViewModelState);`
  The same audit also fixed: a missing `using Microsoft.UI.Input;` (compile error —
  `InputKeyboardSource` is not in `Microsoft.UI.Xaml.Input`), MainWindow navigating twice
  per click (both `ItemInvoked` and `SelectionChanged` were wired; now selection-only plus a
  current-tag guard), a RunContainerDialog volume-row delete that orphaned its wrapper
  border, and the tray icon having no `IconSource` at all (a generated
  `Assets/TrayIcon.ico` is now wired, with a `VERIFY` note on the URI scheme for unpackaged
  apps).

## Getting it running on Windows

1. Copy this whole folder to a Windows 11 machine.
2. Install WSL container preview: `wsl --update --pre-release` (or install WSL 2.9.3+ from
   the WSL GitHub releases page). Confirm with `wslc --version` / `wsl --version`.
3. Install the .NET 10 SDK, then either:
   - `dotnet workload install windowsappsdk` from a Developer PowerShell, or
   - open `OrchardWin.sln` in Visual Studio 2022 (17.14+) / Visual Studio 2026 with the
     "Windows application development" workload, which pulls in the Windows App SDK
     build tools automatically.
4. `dotnet build OrchardWin.sln` (or build from Visual Studio — recommended for the first
   build, since the XAML designer will flag binding/markup issues faster than the CLI).
5. Grep the repo for `// VERIFY:` and work through each one against your real `wslc`/`wsl`
   install before relying on the corresponding feature.
6. Run `OrchardWin.App`. First launch needs an elevated (admin) prompt the first time you
   touch a DNS domain — that's expected (see "DNS" above).
