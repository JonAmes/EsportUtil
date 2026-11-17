using EsportsUtil.Agent;
using EsportsUtil.Core;
using EsportsUtil.Server;

string? roleFlag = null;
string? configPath = null;
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

var cfg = EsportsConfig.Load(configPath ?? FindDefaultConfig());
cfg.ApplyRoleOverride(roleFlag);
Directory.CreateDirectory(cfg.DataDir);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

if (cfg.Role == "server")
    await ServerRole.RunAsync(cfg, cts.Token);
else
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    await new AgentLoop(http, cfg).RunAsync(cts.Token);
}
return 0;

static string? FindDefaultConfig()
{
    var beside = Path.Combine(AppContext.BaseDirectory, "esportsutil.config.json");
    return File.Exists(beside) ? beside : File.Exists("esportsutil.config.json") ? "esportsutil.config.json" : null;
}
