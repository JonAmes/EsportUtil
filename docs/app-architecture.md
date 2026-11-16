# App Architecture — one EXE, two roles (Phase 1)

EsportsUtil is **one native Windows binary: `esportsutil.exe`, C# / .NET 8,
self-contained, single-file**. Role comes from `esportsutil.config.json`
(or `--role server|agent`). No PowerShell and no Python are deployed anywhere.

The server and agent halves share a `Core` library, so the State Module, the JSON
contracts, and the game scanner exist **exactly once** in the build — the halves
can't drift because they compile against the same code.

## Solution / Module Map

```
esportsutil.exe
├── Core                       compiled once, used by both roles
│   ├── Contracts              Local Manifest / Master Manifest models ↔ schemas/
│   ├── State Module           ported MachineState.ps1 semantics — single owner of capture
│   │   ├── Record / Apply / List / Init semantics
│   │   ├── per-display resolution (P/Invoke ChangeDisplaySettingsEx)
│   │   ├── startup items (Registry Run/RunOnce/StartupApproved), screensaver, wallpaper
│   │   ├── software inventory (10-app catalog detection + full HKLM/HKCU Uninstall-key
│   │   │   inventory: name, publisher, version, install location, uninstall strings)
│   │   ├── software removal (Phase 4 remove_software job: quiet uninstall / winget)
│   │   ├── installs (Init only: winget via Process.Start, verified)
│   │   └── model-match selection (exact name → machineModel → newest → override)
│   └── GameScanner            folder-scan game_id discovery
├── Server role                ASP.NET Core host (venue server machine)
│   ├── REST API               ingest manifest, PIN onboarding, heartbeat, pull
│   │                          Factory State Map, rename, print-label, jobs
│   ├── Dashboard              E-Sports Dashboard (served HTML)
│   ├── Stores                 MasterManifest.json, device registry, ContentManifest.json
│   │                          + content/ file store (GET /content/{id}, sha256)
│   └── Alerts                 in-process queue + notification hosted service
│   └── Software policy        Approved Software Policy evaluation (allow-list match,
│                              protected veto) + operator approval / auto_remove job queue
└── Agent role                 BackgroundService loop (each game PC)
    ├── register / PIN, heartbeat, report (wraps State Module + GameScanner)
    │   └── heartbeat ack carries desired_state (active|downed) → agent reconciles
    ├── pull + apply baseline  (fetches baseline content_refs into local cache first)
    ├── content cache          ProgramData\EsportsUtil\content\, sha256-keyed, idempotent
    ├── job runner             (Phase 4: apply / init / steamcmd / remove_software /
    │                          deploy_content / set_downed / clear_downed)
    └── UI surfaces            localhost HTML listener (:4310) + same-EXE --tray stub
                               (WinForms NotifyIcon, user session) — Edge renders both
                               dashboards; no Electron/WebView2 unless later required
```

The agent UI (operator window + tray flyout, `mockups/client_app.html`) is a thin
shell over the Agent role; the service reports even when the window is closed.

## Feature Ownership (dedupe matrix)

| Capability | Owner (one place only) | NOT implemented in |
|---|---|---|
| Screen res / startup / screensaver / wallpaper capture & restore | Core.StateModule | agent loop, server |
| Known-app inventory + version detection | Core.StateModule | agent loop |
| Full installed-software inventory (Uninstall keys) | Core.StateModule | agent loop, server |
| Approved-software policy evaluation + protected veto | Server role (policy is data, not agent logic) | agent, dashboard |
| Software removal execution (quiet uninstall / winget) | Core.StateModule (job handler) | server, dashboard |
| Content Library storage + /content endpoint | Server role | agent (agent only caches) |
| desired_state storage + downed-wallpaper selection (registry) | Server role | agent (agent only reconciles) |
| Downed wallpaper apply/restore (user-session wallpaper swap) | Core.StateModule wallpaper path | server decision logic, dashboard |
| Content fetch/cache/verify (sha256) + deploy_content handler | Core.StateModule / agent job runner | server decision logic |
| Software install (winget, Init only) | Core.StateModule | server |
| Baseline model-matching | Core.StateModule selection, driven by server data | dashboard |
| game_id discovery (folder scan) | Core.GameScanner | StateModule |
| PIN onboarding, heartbeat, HTTP client | Agent role | Core, server |
| API, dashboard, registry, comparison, duplicate resolution, Factory State storage, alerts, label printing | Server role | agent, Core |

## Porting oracle

The original script's Record JSON — field names and shapes from its 936-line proven
implementation — is the test oracle: a C# Record on the same PC class must produce
a schema-valid, field-for-field comparable record. The script itself is a behavior
spec in project history; it is never deployed.

## Data Flow (no format duplication)

1. Agent calls `StateModule.Record()` + `GameScanner.Scan()` → manifest.
2. Record object is serialized **verbatim** under `machine_state` — no copying,
   renaming, or re-mapping. `schemas/local_manifest.schema.json` declares it as an
   opaque shaped object precisely so contracts can't drift.
3. Server stores Factory State Map entries as the same record JSON + factory
   metadata; Apply on any agent feeds it back into `StateModule.Apply()` — one
   format end to end, both directions, same code compiled in.

## Guardrails (enforced inside State Module)

- Never touch machine naming, Windows Update settings, or drivers.
- Only Init installs third-party software; Apply reports missing apps into the
  manifest for server-side remediation.
- Apply is idempotent; safe to re-run after every pulled baseline.
