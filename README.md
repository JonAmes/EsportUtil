# EsportsUtil — E-Sports Dashboard

Fleet management for Windows esports venues. One application, two roles:
`esportsutil.exe` (C# / .NET 8, self-contained single-file EXE) runs as **server** on the venue
machine (API + live dashboard + device registry + Master Manifest / Factory State Map + alerting
+ job queue + label printing) and as **agent** on each game PC (state reporting, job execution,
downed-state wallpaper). Same binary, same version, same wire protocol — role is pure config.

**Status: Phases 1–4 implemented and test-passing** (66 dependency-free checks incl. the
agent resource gate). Windows-box runtime gates remain — see `deploy/BUILD.md`.

## How it works
- **Report, compare — never command-push.** Agents heartbeat + send a content-hashed Local
  Manifest (`schemas/local_manifest.schema.json`); the server diffs it against the Master
  Manifest and surfaces compliance issues (update, rollback, missing, unmanaged, duplicate,
  unapproved app).
- **Jobs are structured data, never command strings** (`schemas/job.schema.json`): the server
  queues allow-listed jobs (`update_game`, `cleanup_duplicate`, `remove_software`,
  `deploy_content`); the agent revalidates locally and builds argv itself — command injection
  is structurally impossible.
- **PIN onboarding:** a new agent reports a 4-digit PIN; the operator names it in the dashboard.
- **Downed state:** per-PC toggle; the agent converges the desktop wallpaper to a Content
  Library "out of service" image (baseline restored when cleared).
- **Alerting:** in-process queue drained by a hosted service → Windows toast on the server box
  (console on dev), fired only on state change.
- **Resource budget (hard gate):** idle agent <1% CPU avg, <60 MB working set — asserted by the
  test suite's perf gate.

## Repo map
```
src/EsportsUtil/            single app: Core/ (contracts, scanner, state module, content,
                            wallpaper) · Server/ (registry, manifests, comparator, jobs,
                            alerts, notifications) · Agent/ (loop, job runner)
src/EsportsUtil.Tests/      dependency-free test runner (T1–T14; exit code = failures)
schemas/                    wire contracts are the authority (snake_case JSON)
deploy/                     publish script + config examples + BUILD.md
wwwroot/                    live server dashboard (embedded in the EXE)
mockups/                    design mockups (dashboard, locate overlay)
```

## Build & run (dev box = Linux)
```bash
dotnet build src/EsportsUtil                    # both TFMs (net8.0 + Windows TFM)
dotnet run --project src/EsportsUtil.Tests      # full suite + resource gate
PERF_SECS=300 dotnet run --project src/EsportsUtil.Tests   # nightly 5-min idle gate

# roles locally (multi-target csproj: dotnet run needs -f; the EXE itself takes --role)
dotnet run --project src/EsportsUtil -f net8.0 -- --role server --config deploy/esportsutil.server.local.json
dotnet run --project src/EsportsUtil -f net8.0 -- --role agent  --config <agent.json>
```

## Build the Windows EXE
```bash
./deploy/publish-win.sh        # -> dist/win-x64/esportsutil.exe (single file, self-contained, R2R)
```
Copy the EXE + a config (see `deploy/*.example.json`) to any Windows PC; first agent run prints
the PIN. Details, trim caveats, and the pre-release Windows gate checklist: `deploy/BUILD.md`.
