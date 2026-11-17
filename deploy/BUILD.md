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
4. Service install/upgrade is a planned ops tail (not yet in this build).

## Windows-box gates (run once on real Windows, e.g. the P5000 Win11 VM)

These can't be exercised on Linux and gate a release:

- [ ] `esportsutil.exe --role server` → dashboard at `http://<host>:4300/`, toast fires on an alert (AUMID shortcut registered on first run)
- [ ] `esportsutil.exe --role agent` → PIN onboarding, report visible
- [ ] `update_game` job with real `steamcmd` (`steamcmd_path` in config)
- [ ] `cleanup_duplicate` + `remove_software` (protected veto) on real folders/registry
- [ ] downed toggle → `SPI_SETDESKWALLPAPER` changes wallpaper; survives logoff/logon
- [ ] StateModule record: registry/WMI reads, label print path
- [ ] `PERF_SECS=300` idle: <1% CPU, <60 MB working set on a game PC
