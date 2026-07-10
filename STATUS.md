# Project status — wslc-gui (OrchardWin)

Living document of **what we are doing right now**, what is done, and what is still open.  
Update this file when direction or priorities change.

**Repo:** https://github.com/amilarajans/wslc-gui  
**Upstream inspiration:** https://github.com/andrew-waters/orchard (macOS)  
**Target runtime:** Microsoft WSL containers (`wslc.exe`) on Windows 11  

---

## Current focus

**Match the Orchard macOS UI and behaviour on Windows**, backed by real `wslc` / `wsl` CLI output (not guessed Apple-container JSON shapes).

| Area | Status |
|------|--------|
| .NET 10 + WinUI 3 project targeting | **Done** |
| Wire `wslc` list/inspect to real Docker-style JSON | **Done** (images, containers, networks, disk tiles) |
| Blank tabs after navigation | **Done** (ViewModel re-hydration + tests) |
| Settings → System versions “Unknown” | **Done** (UTF-16 decode for `wsl --version`) |
| Dashboard look like Orchard screenshot | **Done** (tiles + System 2×2 charts + utilisation table) |
| Sidebar grouped like Orchard | **Done** (Compute / Resources / Networking / Observability) |
| Dashboard → Containers deep-link (click row) | **Done** |
| Containers detail like Orchard screenshots | **Done** (metric cards + config wells) |
| Containers nav crash (SolidColorBrush off UI thread) | **Done** — no DependencyObjects in row VMs |
| Full feature parity (Mounts, Registries, etc.) | **In progress / partial** |
| Polish remaining pages (Images, Machines, Networks, DNS, Models) to Orchard look | **Next** |

---

## What this project is

A native **Windows** GUI for WSL containers:

- **UI:** WinUI 3 + Windows App SDK 1.8  
- **Runtime:** .NET 10 (`net10.0` / `net10.0-windows10.0.19041.0`)  
- **Backend:** Shell out to `wslc.exe` / `wsl.exe` via `ProcessCommandRunner`  
- **Layout:** `OrchardWin.Core` (business logic) + `OrchardWin.App` (WinUI)  

Reference macOS source lives only under `_reference/orchard` (gitignored; not published).

---

## Done recently (chronological)

1. **Retarget to .NET 10** — Core + App TFMs; WASDK 1.8; package bumps.  
2. **First push** to `amilarajans/wslc-gui` with `.gitignore` excluding `_reference/`, AI configs, `bin/`/`obj/`.  
3. **Images empty** — `wslc image ls` is Docker-shaped (`Repository`/`Tag`/`Id`/`Size`); mapped into app models.  
4. **Containers / networks / dashboard empty** — same class of bug; list mappers + synthesized disk usage.  
5. **Start/Stop no-op** — RelayCommand `CanExecute` never refreshed; call lifecycle methods directly.  
6. **Dashboard chart flicker** — update utilisation rows in place; bind `ItemsSource` once.  
7. **Tab switch clears lists** — new ViewModel + service equality short-circuit; hydrate in ctor + after `LoadAsync`; regression tests.  
8. **Settings System “Unknown”** — `wsl --version` is UTF-16 LE; decode correctly; synthesize system property table from `wsl`/`wslc`.  
9. **Dashboard UI** — System charts (CPU/Memory/Network/Disk), 5m–24h windows, utilisation table.  
10. **Nav pane** — Orchard-style groups + count badges.  
11. **Dashboard container click** → Containers page + select that container.  
12. **Containers UI** — list + detail with metric cards, time windows, Overview/Environment/Image/Process/Network/Labels.  

---

## Architecture notes we keep hitting

### Real `wslc` ≠ Orchard JSON

| Command | Real shape |
|---------|------------|
| `image ls --format json` | `{ Id, Repository, Tag, Size, Created }` |
| `container list -a --format json` | `{ Id, Name, Image, State (int), Ports, … }` — State: `1=created`, `2=running`, `3=exited` |
| `network ls --format json` | `{ Id, Name, Driver }` |
| `image inspect` | Docker-style array; **no** `--format` |
| `system` | Only `session` — **no** `df` / `property list` |
| `wsl --version` | UTF-16 LE label lines |

Mappers live mainly in `WslcCliContainerBackend`.

### Navigation / ViewModels

- Frame navigation creates a **new** page + ViewModel each visit.  
- Services keep state; `LoadAsync` may **not** raise list PropertyChanged if data unchanged.  
- **Rule:** constructor `Refresh()` + always re-project after `LoadAsync`.  

### Process output encoding

- `ProcessCommandRunner` reads **bytes** and detects UTF-16 LE vs UTF-8 (needed for `wsl.exe`).  

---

## Solution layout

```
OrchardWin.sln
src/
  OrchardWin.Core/     # models, services, wslc/wsl backends
  OrchardWin.App/      # WinUI shell, pages, view models, controls
tests/
  OrchardWin.Core.Tests/
  OrchardWin.App.Tests/
ARCHITECTURE.md        # porting decisions
STATUS.md              # this file
README.md              # user-facing setup
```

---

## How to run / test

```powershell
# App (x64)
dotnet build OrchardWin.sln -c Debug -p:Platform=x64
dotnet run --project src/OrchardWin.App/OrchardWin.App.csproj -c Debug -p:Platform=x64

# Tests
dotnet test tests/OrchardWin.Core.Tests/OrchardWin.Core.Tests.csproj
dotnet test tests/OrchardWin.App.Tests/OrchardWin.App.Tests.csproj -p:Platform=x64
```

**Requirements:** Windows 11, .NET 10 SDK, WSL + `wslc` (`wsl --update --pre-release`), Windows App SDK workload.

---

## Next candidates (not started / partial)

- [ ] **Images** page polish to match Orchard (detail header, inspect layout).  
- [ ] **Machines** detail / create flow polish.  
- [ ] **Networks / DNS** detail UI closer to Orchard.  
- [ ] **Mounts** dedicated tab (Orchard has it; we only derive mounts on containers).  
- [ ] **Registries** (Orchard has it; `wslc registry` / login exist).  
- [ ] **Logs** deep-link from Containers “Logs” button (currently navigates to Logs only).  
- [ ] Richer **container inspect** from real `wslc container inspect` (fill env/ports/mounts when list is slim).  
- [ ] Optional: hide-to-tray on close (Orchard menu-bar style).  
- [ ] LICENSE file.  

---

## Working agreements

- Do **not** commit `_reference/`, `.claude/`, other AI agent configs, or `bin/`/`obj/`.  
- Prefer real CLI probes over assumptions; tag remaining guesses `// VERIFY:`.  
- When fixing “empty UI” bugs, add a regression test when practical.  
- Keep **STATUS.md** updated when the active focus or major milestones change.  

---

## Recent commits (high level)

| Commit theme | Topic |
|--------------|--------|
| .NET 10 retarget | TFMs + packages |
| Initial push | Clean source to GitHub |
| Image / container / network mappers | Real wslc JSON |
| Start/Stop CanExecute | Lifecycle actions |
| Dashboard flicker | In-place row updates |
| Tab re-entry | ViewModel hydration + tests |
| Settings versions | UTF-16 + system details |
| Dashboard UI | System charts + table |
| Nav + deep-link | Sidebar groups; dashboard → containers |
| Containers UI | Orchard-like detail metrics + cards |

*(See `git log` for exact SHAs.)*

---

*Last updated: 2026-07-10 — Fixed Containers crash (no SolidColorBrush in ViewModels / off-UI-thread).*
