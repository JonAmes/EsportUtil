using EsportsUtil.Agent;
using EsportsUtil.Core;
using EsportsUtil.Server;
using Microsoft.AspNetCore.Builder;

string? roleFlag = null;
string? configPath = null, foundConfig = null, serviceNameFlag = null;
bool overlayMode = false, downedOverlayMode = false, trayFlag = false, serviceFlag = false;
var rawArgs = new List<string>(args);
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case LocateOverlayRequest.Flag: overlayMode = true; break;
        case DownedOverlayRequest.Flag: downedOverlayMode = true; break;
        case "--role" when i + 1 < args.Length: roleFlag = args[++i]; break;
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
        case "--tray": trayFlag = true; break;
        case "--service": serviceFlag = true; break;
        case "--name" when i + 1 < args.Length: serviceNameFlag = args[++i]; break;
    }

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
        picked = ConsoleSetupWizard();
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
Directory.CreateDirectory(cfg.DataDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// One role loop, three hosts (PLAN.md lifecycle): console foreground, tray in the user session,
// SCM-driven service. The loop object graph is identical — only the UI placement differs.
switch (lifecycle.Mode)
{
    case RunMode.Tray:
#if WINDOWS
        if (OperatingSystem.IsWindows()) return TrayApp.Run(cfg);
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

static (string role, string? url)? ConsoleSetupWizard()
{
    Console.WriteLine("What should this PC run?");
    Console.WriteLine("  1 = SERVER  (venue control machine — dashboard + fleet jobs)");
    Console.WriteLine("  2 = AGENT   (game PC — reports to the server)");
    Console.Write("choice [1/2, or blank to cancel]: ");
    var choice = Console.ReadLine()?.Trim();
    if (choice is not ("1" or "2")) return null;
    if (choice == "1") return ("server", null);
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
