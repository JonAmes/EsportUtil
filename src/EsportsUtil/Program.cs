using EsportsUtil.Agent;
using EsportsUtil.Core;
using EsportsUtil.Server;
using Microsoft.AspNetCore.Builder;

string? roleFlag = null;
string? configPath = null, foundConfig = null, serviceNameFlag = null, finalizeTarget = null;
int parentPidFlag = -1;
bool overlayMode = false, downedOverlayMode = false, updateOverlayMode = false, trayFlag = false, serviceFlag = false;
var rawArgs = new List<string>(args);
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case LocateOverlayRequest.Flag: overlayMode = true; break;
        case DownedOverlayRequest.Flag: downedOverlayMode = true; break;
        case UpdateOverlayRequest.Flag: updateOverlayMode = true; break;
        case "--role" when i + 1 < args.Length: roleFlag = args[++i]; break;
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--tray": trayFlag = true; break;
        case "--service": serviceFlag = true; break;
        case "--name" when i + 1 < args.Length: serviceNameFlag = args[++i]; break;
        case SelfUpdateWatch.FinalizeFlag when i + 1 < args.Length: finalizeTarget = args[++i]; break;
        case "--ppid" when i + 1 < args.Length: parentPidFlag = int.TryParse(args[++i], out var pp) ? pp : -1; break;
    }

// Double-click, not CLI (T30): a Windows foreground start with no explicit host flag and no
// same-EXE child duty (overlay/downed-overlay/handoff) lands in the tray — "the app loads into
// tray mode automatically". Console/Linux behavior is unchanged.
if (roleFlag is null && !trayFlag && !serviceFlag && !overlayMode && !downedOverlayMode && !updateOverlayMode
    && finalizeTarget is null && OperatingSystem.IsWindows())
    trayFlag = true;

// Ops verbs never start a role loop: service install/remove/start/stop/status, autostart enable/disable/status.
var lifecycle = Lifecycle.Parse(args, trayFlag, serviceFlag, serviceNameFlag);
if (lifecycle.IsOpsVerb)
{
    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("'service' and 'autostart' are Windows-only operations.");
        return 2;
    }
#if WINDOWS
    return await ServiceOps.RunAsync(lifecycle.Verb!, lifecycle.SubVerb, lifecycle.ServiceName, configPath);
#else
    return 2;
#endif
}

// Same-EXE locate overlay surface (PLAN.md Phase 4): spawned by the agent loop into the user session.
if (overlayMode)
{
    if (!LocateOverlayRequest.TryParse(rawArgs, out var req))
    {
        Console.Error.WriteLine("usage: esportsutil --overlay --display-name NAME --pin NNNN --client-id ID --expires UNIX [--model M]");
        return 2;
    }
    if (!req.ShouldShow(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
    {
        Console.WriteLine("stale locate directive — overlay suppressed");   // a late ack never pops a stale overlay
        return 0;
    }
#if WINDOWS
    if (OperatingSystem.IsWindows())
    {
        LocateOverlayWindow.Run(req);
        return 0;
    }
#endif
    await ConsoleOverlay.RunAsync(req, CancellationToken.None);
    return 0;
}

// Same-EXE sticky downed overlay (no expiry; staff bypass closes it, the agent kills it on clear).
if (downedOverlayMode)
{
    if (!DownedOverlayRequest.TryParse(rawArgs, out var dreq))
    {
        Console.Error.WriteLine("usage: esportsutil --downed-overlay --display-name NAME --pin NNNN --client-id ID [--model M] [--reason R]");
        return 2;
    }
#if WINDOWS
    if (OperatingSystem.IsWindows())
    {
        DownedOverlayWindow.Run(dreq);
        return 0;
    }
#endif
    await ConsoleOverlay.RunDownedAsync(dreq, CancellationToken.None);
    return 0;
}

// Same-EXE update overlay (T33): full-screen install card; nothing on the machine dismisses it —
// the agent kills the child when the install reaches a terminal state (console self-exit is the backstop).
if (updateOverlayMode)
{
    if (!UpdateOverlayRequest.TryParse(rawArgs, out var ureq))
    {
        Console.Error.WriteLine("usage: esportsutil --update-overlay --display-name NAME --pin NNNN --client-id ID [--model M] --progress-file PATH");
        return 2;
    }
#if WINDOWS
    if (OperatingSystem.IsWindows())
    {
        UpdateOverlayWindow.Run(ureq);
        return 0;
    }
#endif
    await ConsoleOverlay.RunUpdateAsync(ureq, CancellationToken.None);
    return 0;
}

// Self-update handoff (T30): we ARE the staged new EXE — wait out the old process,
// replace its path, relaunch it in tray mode, exit.
if (finalizeTarget is not null)
    return await SelfUpdateWatch.FinalizeAsync(finalizeTarget, parentPidFlag);

EsportsConfig cfg;
foundConfig = configPath ?? FirstRunSetup.FindConfigFile();
if (foundConfig is null && roleFlag is null && lifecycle.Mode != RunMode.Service)
{
    // First run: ask the operator instead of defaulting to a silent localhost agent
    // (the classic "PC never shows up on the dashboard" trap).
    Console.WriteLine("No esportsutil.config.json found — starting first-run setup.");
    (string role, string? url)? picked = null;
#if WINDOWS
    if (OperatingSystem.IsWindows())
        picked = FirstRunDialog.Run();
    else
#endif
        picked = await ConsoleSetupWizard();
    if (picked is null)
    {
        Console.WriteLine("setup cancelled — no config written, exiting.");
        return 3;
    }
    cfg = FirstRunSetup.BuildConfig(picked.Value.role, picked.Value.url, FirstRunSetup.ConfigHomeDir());
    var written = FirstRunSetup.WriteConfig(FirstRunSetup.ConfigHomeDir(), cfg);
    Console.WriteLine($"wrote {written} — this PC will run as {cfg.Role}.");
}
else if (foundConfig is null && roleFlag is null)
{
    // Headless service mode has no one at the keyboard to answer a first-run dialog.
    Console.Error.WriteLine("--service needs esportsutil.config.json next to the EXE (or --config <path>) — run the first-run setup interactively first.");
    return 4;
}
else
{
    cfg = EsportsConfig.Load(foundConfig);
    cfg.ApplyRoleOverride(roleFlag);
}
if (cfg is not null) cfg.ResolveDataDir(foundConfig);   // T37: relative data_dir binds to the config file, not the CWD
Directory.CreateDirectory(cfg.DataDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// One role loop, three hosts (PLAN.md lifecycle): console foreground, tray in the user session,
// SCM-driven service. The loop object graph is identical — only the UI placement differs.
switch (lifecycle.Mode)
{
    case RunMode.Tray:
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            if (cfg.TrayAutoStart) EnsureRunKey();   // auto-load at logon — one less CLI dependency ever
            return TrayApp.Run(cfg);
        }
#endif
        Console.Error.WriteLine("--tray is Windows-only (no tray surface on this OS) — run the console foreground loop instead.");
        return 2;

    case RunMode.Service:
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            Lifecycle.TeeConsoleToLog(cfg.DataDir);   // SCM gives us no console — operator reads the log file
            System.ServiceProcess.ServiceBase.Run(new EsportsUtilService(cfg));
            return 0;
        }
#endif
        Console.Error.WriteLine("--service is Windows-only (no SCM on this OS).");
        return 2;

    default:
        await RoleRunner.RunAsync(cfg, cts.Token);
        return 0;
}

#if WINDOWS
// Idempotent HKCU Run write (same value as `autostart enable`) so a logon always brings the
// tray agent back — the venue PC never needs anyone to launch esportsutil by hand.
static void EnsureRunKey()
{
    var exe = Environment.ProcessPath;
    if (exe is null) return;
    try
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Lifecycle.RunKeyPath);
        if (key?.GetValue(Lifecycle.RunValueName) as string != Lifecycle.RunKeyValue(exe))
            key?.SetValue(Lifecycle.RunValueName, Lifecycle.RunKeyValue(exe));
    }
    catch (Exception e) { Console.Error.WriteLine($"autostart Run key write failed: {e.Message}"); }
}
#endif

static async Task<(string role, string? url)?> ConsoleSetupWizard()
{
    Console.WriteLine("What should this PC run?");
    Console.WriteLine("  1 = SERVER  (venue control machine — dashboard + fleet jobs)");
    Console.WriteLine("  2 = AGENT   (game PC — reports to the server)");
    Console.Write("choice [1/2, or blank to cancel]: ");
    var choice = Console.ReadLine()?.Trim();
    if (choice is not ("1" or "2")) return null;
    if (choice == "1") return ("server", null);
    // T35: autodetect first — the operator should never have to dig out the server's IP.
    Console.WriteLine("searching the LAN for esportsutil servers (~2 s)…");
    var found = await Discovery.DetectAsync(Discovery.DefaultPort, 2000);
    foreach (var r in found)
        Console.WriteLine($"  found server: {r.ServerUrl}  (build v{r.Version})");
    if (found.Count > 0)
    {
        Console.Write($"press Enter to use {found[0].ServerUrl}, or type another address: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return ("agent", found[0].ServerUrl);
        var typed = FirstRunSetup.NormalizeServerUrl(input);
        if (typed is not null) return ("agent", typed);
        Console.WriteLine("  that doesn't look like an address — using the detected server");
        return ("agent", found[0].ServerUrl);
    }
    Console.WriteLine("no esportsutil server answered on the LAN — type its address instead.");
    while (true)
    {
        Console.Write("server address (IP or http://host:port, blank to cancel): ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input)) return null;
        var url = FirstRunSetup.NormalizeServerUrl(input);
        if (url is not null) return ("agent", url);
        Console.WriteLine("  that doesn't look like an address — try 10.140.0.212 or http://host:4300");
    }
}
