# wslc-gui (OrchardWin)

A native **Windows** GUI for [WSL containers](https://devblogs.microsoft.com/commandline/wsl-container-is-now-available-for-public-preview/) (`wslc.exe`).

This project is a Windows port of [Orchard](https://github.com/andrew-waters/orchard) — a macOS app for Apple’s `container` runtime — retargeted at Microsoft’s WSL container CLI instead of Apple’s XPC stack.

| | |
|---|---|
| **UI** | WinUI 3 + Windows App SDK |
| **Runtime** | .NET 10 (`net10.0` / `net10.0-windows10.0.19041.0`) |
| **Backend** | Shells out to `wslc.exe` / `wsl.exe` (CLI-first, verifiable) |
| **Repo** | https://github.com/amilarajans/wslc-gui |

## Features

- **Dashboard** — disk usage tiles (synthesized from image/container/volume lists) and utilisation sparklines  
- **Containers** — list, start/stop/kill/delete, logs, stats, run dialog (ports, volumes, env)  
- **Images** — list local images, inspect, pull from Docker Hub search, delete  
- **Machines** — WSL distros as “machines”, create/import-style flows  
- **Networks / DNS** — network list; hosts-file DNS domains (UAC on write)  
- **Models + sandboxes** — local model / Ollama-style server bridge and sandbox list (combined page)  
- **Logs** — container log viewer  
- **Settings** — paths, terminals, system info  
- **System tray** — tray presence via H.NotifyIcon.WinUI  
- **Custom title bar** — branded chrome with system caption buttons  

## Orchard feature comparison

Compared to upstream [Orchard](https://github.com/andrew-waters/orchard) sidebar tabs / features:

| Orchard (macOS) | wslc-gui | Notes |
|-----------------|----------|--------|
| Dashboard | Yes | Disk tiles from image/volume/container lists (`wslc` has no `system df`) |
| Containers | Yes | List/lifecycle/run; full “edit container” flow is thinner than Orchard |
| Images + Hub search | Yes | Verified against real `wslc image ls/inspect` JSON |
| Mounts (dedicated tab) | Partial | Mounts derived in Core; no standalone Mounts page yet |
| Machines | Yes | Backed by `wsl.exe` + best-effort `wslc` |
| AI Models | Yes | Combined with sandboxes |
| Sandboxes | Yes | Same “Local AI & Sandboxes” page |
| DNS | Yes | Hosts-file based (UAC) |
| Networks | Yes | List/create/delete; inspect detail is limited |
| Registries | No | Orchard registries UI not ported yet (`wslc registry` / login exist) |
| System Logs | Yes | Logs page |
| Menu-bar dashboard | Tray | System tray instead of macOS menu bar |
| Auto-updater | No | Orchard Sparkle-style updater not ported |
| Kernel picker | N/A | No per-machine kernel on WSL |
| MLX inference | N/A | Apple-only |

## Requirements

1. **Windows 11** (Windows App SDK / WinUI 3)  
2. **WSL container preview** — install/update so `wslc` is on PATH:
   ```powershell
   wsl --update --pre-release
   wslc --version
   ```
3. **.NET 10 SDK** — https://dotnet.microsoft.com/download/dotnet/10.0  
4. **Windows App SDK workload** (or Visual Studio with *Windows application development*):
   ```powershell
   dotnet workload install windowsappsdk
   ```
   Visual Studio **2026** is recommended for full `net10.0` IDE support (CLI builds work with the SDK alone).

## Build & run

```powershell
git clone https://github.com/amilarajans/wslc-gui.git
cd wslc-gui

dotnet restore OrchardWin.sln
dotnet build OrchardWin.sln -c Debug -p:Platform=x64
dotnet run --project src/OrchardWin.App/OrchardWin.App.csproj -c Debug -p:Platform=x64
```

## Tests

```powershell
dotnet test tests/OrchardWin.Core.Tests/OrchardWin.Core.Tests.csproj
dotnet test tests/OrchardWin.App.Tests/OrchardWin.App.Tests.csproj -p:Platform=x64
```

Core tests cover service load / equality short-circuit. App tests cover ViewModel tab re-entry
(hydration after navigation) so Images/Containers don’t go blank when switching sections.

Or open `OrchardWin.sln` in Visual Studio and run **OrchardWin.App** (x64).

## Project layout

```
OrchardWin.sln
src/
  OrchardWin.Core/   # net10.0 — models, services, wslc/wsl backends (no WinUI)
  OrchardWin.App/    # net10.0-windows — WinUI 3 shell, pages, view models
ARCHITECTURE.md      # Porting decisions, VERIFY notes, platform gaps
```

Business logic lives in **Core** and talks to the OS only through `ICommandRunner` + backend interfaces. The **App** project is UI and Windows-only hosting.

## How it talks to WSL containers

Default backend: **`WslcCliContainerBackend`** — runs `wslc` subcommands (`image ls`, `container list`, …) and maps JSON into domain models.

Image list JSON from real `wslc` looks like Docker:

```json
{ "Id": "sha256:…", "Repository": "alpine", "Tag": "latest", "Size": 8415579, "Created": … }
```

The backend maps that into the app’s `ContainerImage` shape (`reference` + `descriptor`). Image inspect uses Docker-style inspect output (no `--format` flag on current `wslc`).

A future path is the native `Microsoft.WSL.Containers` package; the CLI path was chosen so behaviour can be verified against a real install.

## Credits

- **Upstream inspiration:** [Orchard](https://github.com/andrew-waters/orchard) by Andrew Waters (macOS / Apple container)  
- **Runtime:** Microsoft WSL containers (`wslc`) public preview  

This repository does **not** vendor the Orchard source tree; it is an independent Windows reimplementation of the UI/service patterns against `wslc`.

## License

Add a `LICENSE` file when you choose a license for this port. Upstream Orchard has its own license — do not assume it applies here.

## Contributing / known gaps

Many CLI flag and JSON shape assumptions are marked `// VERIFY:` in source. Grep for that tag when something fails against your `wslc` build:

```powershell
rg "VERIFY:" src
```

See [ARCHITECTURE.md](./ARCHITECTURE.md) for intentional platform cuts (e.g. MLX, per-machine kernel swap) and the full porting methodology.
