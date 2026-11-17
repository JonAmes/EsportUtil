# Building & deploying the Windows EXE

## Build (from the Linux dev box — no Windows needed to compile)

```bash
./deploy/publish-win.sh          # -> dist/win-x64/esportsutil.exe
```

The csproj already sets `EnableWindowsTargeting=true`, so the Linux SDK cross-compiles
the `net8.0-windows10.0.19041.0` TFM (WinRT toast projection included) straight to a
Windows binary. The publish is:

| Flag | Why |
|---|---|
| `-r win-x64 --self-contained` | no .NET runtime install needed on game PCs |
| `PublishSingleFile` + `IncludeAllContentForSelfExtract` | one EXE; `wwwroot/` dashboard is embedded in the bundle and served by the server role |
| `EnableCompressionInSingleFile` | ~52 MB compressed bundle |
| `ReadyToRun` | pre-JITted — faster start, lower working set (cross-OS R2R works from Linux) |
| workstation GC, InvariantGlobalization | set in the csproj (resource budget) |

Current artifact: **52 MB**, PE32+ x64 console.

### Trimming (opt-in, not the default)

`./deploy/publish-win.sh --trim` produces ~18 MB but emits **133 IL trim warnings**
(reflection-based `System.Text.Json`, WinRT projections, ASP.NET minimal API). Those
paths may break at runtime in ways the compile can't catch. Trimming stays off until
JSON source-generation is in; the memory/CPU budgets in PLAN.md are enforced by the
perf gate, not by disk size.

### Version stamping

Set the release version at publish time: `-p:Version=0.4.1` (or add `<Version>` to the csproj).

## Dev loop (stays on Linux)

```bash
dotnet build src/EsportsUtil                      # both TFMs
dotnet build src/EsportsUtil -f net8.0-windows10.0.19041.0   # Windows TFM only
dotnet run --project src/EsportsUtil.Tests        # 66 checks incl. resource gate
PERF_SECS=300 dotnet run --project src/EsportsUtil.Tests     # nightly 5-min idle gate
```

## Deploy to a Windows machine

1. Copy `esportsutil.exe` to the PC (e.g. `C:\EsportsUtil\`).
2. Put `esportsutil.config.json` next to the EXE (or pass `--config path`); templates:
   `deploy/esportsutil.server.example.json` / `deploy/esportsutil.agent.example.json`
   (data dirs `C:\ProgramData\EsportsUtil\{server,agent}`).
3. First run manually: `esportsutil.exe --role agent` — it prints the 4-digit PIN;
   name the device in the dashboard.
4. Then pick the lifecycle mode:
   - **Game PCs (recommended): system tray.** `esportsutil.exe --tray` runs the full agent
     with a status dot in the tray (green linked / amber pairing or downed / red server
     unreachable); double-click opens the client dashboard, right-click has identity+PIN and Exit.
     `esportsutil.exe autostart enable` adds it to the user's sign-in (HKCU Run) — with the venue
     auto-login account the agent is up minutes after boot, console hidden, output in
     `C:\ProgramData\EsportsUtil\agent\esportsutil.log`. `autostart status|disable` to inspect/remove.
   - **Server box / headless: Windows service.** From an **elevated** shell:
     `esportsutil.exe service install` (auto-start + crash-restart 5s/30s/60s), then
     `service start|stop|status|remove`. A service runs in session 0, so locate/downed overlays
     need a logged-in session — that's why game PCs use tray mode, not the service.

## Windows-box gates (run once on real Windows, e.g. the P5000 Win11 VM)

These can't be exercised on Linux and gate a release:
- Self-update (T30): on a game PC running the tray agent, push a newer GitHub release (tag `v*` →
  workflow publishes esportsutil.exe). Within one check window the device shows an `app 0.5.0 → 0.5.1`
  chip; clicking it (or just waiting — apply is on by default) downloads from GitHub, verifies the
  asset sha256, and the PC comes back in the tray running the new version with state `current`
  matching the tag. Staged file lives in <dataDir>/updates/esportsutil.exe.new and is deleted by
  the handoff. `self_update_apply:false` shows the verdict but never swaps.
- Tray auto-load (T30): double-click esportsutil.exe (no arguments) on a configured PC → tray
  agent, no CLI. `reg query HKCU\...\Run /v EsportsUtil` shows `"<exe>" --tray`; sign out/in and
  the tray dot reappears without any launch.
- Service host self-update (T30): `esportsutil service install` host marks `restart_pending` and
  the replaced EXE loads on the next SCM restart — verify the state chip clears.

- [ ] `esportsutil.exe --role server` → dashboard at `http://<host>:4300/`, toast fires on an alert (AUMID shortcut registered on first run)
- [ ] `esportsutil.exe --role agent` → PIN onboarding, report visible
- [ ] `update_game` job with real `steamcmd` — or let the agent bootstrap it: first beat with no
      steamcmd found fires `winget install -e --id Valve.SteamCMD` unattended; the discovered path
      (WinGet Links/Packages) is what the job then executes; `machine_state.steamCmd` should read
      `found:true, source:winget_link|winget_package`, and `steamCmd.install_state` tells you if
      the bootstrap failed (retried every 30 min; `steamcmd_auto_install:false` opts out)
- [ ] Games already installed under the default Steam library
      (`C:\Program Files (x86)\Steam\steamapps\common`) report with launcher `steam` with no
      `scan_dirs` config at all
- [ ] Settings page → "Venue app installs": pick a station, click Install on Chrome — the agent
      streams the vendor MSI directly (https only), the tile's progress bar climbs, then
      `msiexec /i … /qn /norestart` runs silently and Chrome appears on the PC. Repeat for
      Firefox (`-msa`), Steam, Epic (`/qn`), Battle.net (`/quiet`), Riot (`--skip-to-install`).
      `machine_state.apps` shows six rows; Chrome/Firefox compare against the vendor version API —
      an outdated install shows red "update available", dashboard chip `outdated chrome`, and
      `POST /api/update-app/{clientId}` (the chip) queues exactly one update_app job; Steam/Epic/
      Battle.net/Riot self-update so their `up_to_date` stays null by design
- [ ] `cleanup_duplicate` + `remove_software` (protected veto) on real folders/registry
- [ ] downed toggle → `SPI_SETDESKWALLPAPER` changes wallpaper; survives logoff/logon
- [ ] `--tray` → icon + color changes (pairing→linked), tooltip/menu correct, Run-key console hidden, log file written
- [ ] `service install|start|status|stop|remove` from elevated shell; crash-restart actions fire
- [ ] `autostart enable` → tray agent back after sign-out/sign-in (and reboot with auto-login)
- [ ] StateModule record: registry/WMI reads, label print path
- [ ] `PERF_SECS=300` idle: <1% CPU, <60 MB working set on a game PC
