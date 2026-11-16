# EsportsUtil — E-Sports Dashboard

One application, two roles: `esportsutil.exe` (C# / .NET 8, self-contained single-file EXE) runs as
**server** on the venue machine (API + dashboard + device registry + Master Manifest / Factory State
Map + alerting + label printing) and as **agent** on each game PC (Windows service reporting state,
pulling factory baselines, executing jobs). Same binary, same version, same wire protocol — role is
pure config.

## 🗺️ Architecture Goals
1.  **Core (shared):** JSON contracts (`schemas/`), State Module (Record/Apply/List/Init, ported
    MachineState.ps1 semantics), GameScanner (folder-scan `game_id` discovery).
2.  **Server role:** ASP.NET Core host — ingestion, comparison (source of truth), PIN onboarding,
    dashboard, label printing, in-process alert queue + notification hosted service.
3.  **Agent role:** BackgroundService loop — heartbeat, manifest reports, baseline pull/apply,
    job runner (SteamCMD et al. in Phase 4).

## 🛠️ Getting Started
... [TODO: Add dependencies and setup instructions — created at Phase 1 implementation]
