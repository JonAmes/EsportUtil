using EsportsUtil.Agent;
using EsportsUtil.Core;
using EsportsUtil.Server;
using Microsoft.AspNetCore.Builder;

string? roleFlag = null;
string? configPath = null, foundConfig = null;
bool overlayMode = false;
var rawArgs = new List<string>(args);
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case LocateOverlayRequest.Flag: overlayMode = true; break;
        case "--role" when i + 1 < args.Length: roleFlag = args[++i]; break;
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
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

EsportsConfig cfg;
foundConfig = configPath ?? FirstRunSetup.FindConfigFile();
if (foundConfig is null && roleFlag is null)
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
else
{
    cfg = EsportsConfig.Load(foundConfig);
    cfg.ApplyRoleOverride(roleFlag);
}
Directory.CreateDirectory(cfg.DataDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (cfg.Role == "server")
    await ServerRole.RunAsync(cfg, cts.Token);
else
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var loop = new AgentLoop(http, cfg);
    ClientDashboard? dash = null;
    if (cfg.AgentDashboard)
    {
        // PLAN.md: the agent role serves the client operator view from a small localhost listener —
        // read-only, loopback-only, reporting continues whether or not the page is open.
        dash = ClientDashboard.Start(loop, cfg);
        Console.WriteLine($"client dashboard: {dash.Url} (open in this PC's browser)");
    }
    await loop.RunAsync(cts.Token);
    dash?.Stop();
}
return 0;

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
