# Client Agent Architecture (Phase 1)

The Client Agent is one **native Windows binary: C# / .NET 8, self-contained,
single-file EXE**. No PowerShell is deployed on game PCs — no `ExecutionPolicy`
requirements, no script friction. It runs as a Windows service or a boot-triggered
Scheduled Task.

The MachineState.ps1 engine's **behavior** is ported into the agent as one internal
**State Module**; its code is not shipped. The JSON record format the engine produced
is preserved verbatim as the wire contract, so records, the Local Manifest, and the
Factory State Map all speak one format. Nothing else in the agent re-implements
capture, apply, or install logic.

## Module Map (one EXE, internal modules)

```
esportsutil-agent.exe  (C# / .NET 8, self-contained)
├── Transport            the only net-new runtime logic
│   ├── register / PIN onboarding
│   ├── heartbeat (liveness)
│   ├── manifest build + POST  (wraps State Module JSON, adds game_reports)
│   ├── pull Factory State Map
│   └── job runner          (Phase 4 hook: apply / init / steamcmd)
├── State Module         ported MachineState.ps1 semantics — single owner of capture
│   ├── Record / Apply / List / Init semantics
│   ├── per-display resolution  (P/Invoke ChangeDisplaySettingsEx / EnumDisplaySettings)
│   ├── startup items           (Registry: HKCU/HKLM Run, RunOnce, StartupApproved)
│   ├── screensaver, wallpaper
│   ├── known-app inventory     (10-app catalog, regex detection)
│   ├── installs                (Init only: winget via Process.Start, verified)
│   └── model-match selection   (exact name → machineModel → pick list)
├── GameScanner          folder-scan game_id discovery (net-new)
└── agent.config.json    server URL, scan roots, folder→game_id map, PIN
```

## Feature Ownership (dedupe matrix)

| Capability | Owner (one place only) | NOT implemented in |
|---|---|---|
| Screen res / startup / screensaver / wallpaper capture & restore | State Module | transport, server |
| Known-app inventory + version detection | State Module (catalog regex) | transport |
| Software install (winget, verified, Init only) | State Module | transport, server |
| Baseline model-matching (name → model → newest) | State Module selection, driven by server data | transport, dashboard |
| game_id discovery (folder scan) | GameScanner — net-new | State Module |
| PIN onboarding, heartbeat, HTTP transport | Transport — net-new | State Module |
| Version comparison, duplicate-game resolution, Factory State storage | **server** | agent (any module) |

Net-new surface is exactly three modules: Transport, GameScanner, job runner.
The State Module is a port with a fixed output contract, not a rewrite opportunity.

## Porting oracle (how we know the port is faithful)

The original script's Record JSON — field names and shapes from its 936-line
proven implementation — is the test oracle: a C# Record on the same PC class must
produce a schema-valid, field-for-field comparable record. The original script is
kept as a behavior spec in project history and is **never deployed** to game PCs.

## Data Flow (no format duplication)

1. Transport calls `StateModule.Record()` → record object.
2. It is **serialized verbatim** under `machine_state` in the Local Manifest —
   no copying, renaming, or re-mapping of State Module fields. Schema
   `schemas/local_manifest.schema.json` declares `machine_state` as an opaque
   shaped object precisely so transport and state code can never drift.
3. Factory State Map entries are the same record JSON + factory metadata
   (`game_id`, `version`) — one format end to end.

## Guardrails (inherited design rules, enforced inside the State Module)

- Never touch machine naming, Windows Update settings, or drivers.
- Only Init installs third-party software; Apply reports missing apps into the
  manifest for server-side remediation.
- Apply is idempotent; safe to re-run after every pulled baseline.
