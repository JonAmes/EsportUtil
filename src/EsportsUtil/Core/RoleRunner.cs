using EsportsUtil.Agent;
using EsportsUtil.Server;

namespace EsportsUtil.Core;

/// <summary>The one role dispatch, shared by every launch mode (console foreground, --tray,
/// --service): the transport loop object graph is identical everywhere — mode only decides
/// where the UI lives. Extracted from Program.cs so hosts never re-implement startup.</summary>
public static class RoleRunner
{
    /// <param name="onAgentLoop">lets a host (tray) keep a handle on the live loop for status
    /// projection; foreground callers pass nothing.</param>
    public static async Task RunAsync(EsportsConfig cfg, CancellationToken ct, Action<AgentLoop>? onAgentLoop = null)
    {
        if (cfg.Role == "server")
        {
            await ServerRole.RunAsync(cfg, ct);
            return;
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var loop = new AgentLoop(http, cfg);
        onAgentLoop?.Invoke(loop);
        ClientDashboard? dash = null;
        if (cfg.AgentDashboard)
        {
            // PLAN.md: read-only, loopback-only; reporting continues whether or not the page is open.
            dash = ClientDashboard.Start(loop, cfg);
            Console.WriteLine($"client dashboard: {dash.Url} (open in this PC's browser)");
        }
        await loop.RunAsync(ct);
        dash?.Stop();
    }
}
