using EsportsUtil.Agent;
using EsportsUtil.Core;
using EsportsUtil.Server;

string? roleFlag = null;
string? configPath = null;
for (int i = 0; i < args.Length; i++)
    switch (args[i])
    {
        case "--role" when i + 1 < args.Length: roleFlag = args[++i]; break;
        case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
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

static string? FindDefaultConfig()
{
    var beside = Path.Combine(AppContext.BaseDirectory, "esportsutil.config.json");
    return File.Exists(beside) ? beside : File.Exists("esportsutil.config.json") ? "esportsutil.config.json" : null;
}
