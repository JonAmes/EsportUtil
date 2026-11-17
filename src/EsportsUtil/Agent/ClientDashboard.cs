using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using EsportsUtil.Core;

namespace EsportsUtil.Agent;

/// <summary>Read-only projection of the agent loop's live state — the payload the localhost client
/// view polls. Field-for-field with docs/data_contracts.md §client-status. Nothing here pushes:
/// the page just watches the loop, so closing it costs nothing and reporting never depends on it.</summary>
public sealed record LocalStatus(
    string AgentVersion,
    string? ClientId,
    string? DevicePin,
    string ServerUrl,
    int HeartbeatIntervalSec,
    bool Registered,
    string? DisplayName,                 // operator pairing name; null = still pending
    string DesiredState,
    string? DownedReason,
    DateTimeOffset? LastAckAt,
    bool LastAckOk,
    string? LastAckError,
    DateTimeOffset? LastReportAt,
    int ReportsSent,
    string? ReportHash,
    string? MachineModel,
    List<GameReport> Games,
    List<DuplicateReport> Duplicates,
    List<Job> RecentJobs,
    JsonNode? WindowsUpdate,               // machine_state.windowsUpdate passthrough; null = never polled
    JsonNode? WallpaperSync);              // machine_state.wallpaperSync passthrough; null = nothing enforced

/// <summary>The client operator view (PLAN.md: agent role serves it from a small localhost listener,
/// default port 4310). Loopback-only: this PC's own browser — the fleet view stays on the server.
/// Deliberately a raw HttpListener, not WebApplication: PLAN's agent budget (< 60 MB working set)
/// is non-negotiable, and Kestrel's hosting stack measured ~14 MB — two GET routes do not need it.</summary>
public sealed class ClientDashboard
{
    private readonly HttpListener _listener = new();
    private readonly AgentLoop _loop;
    private readonly EsportsConfig _cfg;
    private readonly string _pagePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "client.html");

    public string Url { get; }

    /// <summary>Binds 127.0.0.1:{AgentDashboardPort} (0 → free ephemeral port) and serves; returns once bound.</summary>
    public static ClientDashboard Start(AgentLoop loop, EsportsConfig cfg)
    {
        int port = cfg.AgentDashboardPort == 0 ? FreePort() : cfg.AgentDashboardPort;
        var dash = new ClientDashboard(loop, cfg, port);
        dash._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        dash._listener.Start();
        _ = Task.Run(dash.ServeLoop);
        return dash;
    }

    private ClientDashboard(AgentLoop loop, EsportsConfig cfg, int port)
    {
        _loop = loop; _cfg = cfg;
        Url = $"http://127.0.0.1:{port}";
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int p = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return p;
    }

    private async Task ServeLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { break; }        // listener closed (shutdown)
            try
            {
                if (ctx.Request.HttpMethod != "GET" || ctx.Request.Url is null)
                {
                    ctx.Response.StatusCode = 405; ctx.Response.Close(); continue;
                }
                string path = ctx.Request.Url.AbsolutePath;
                if (path == "/")
                {
                    byte[] page = File.Exists(_pagePath)
                        ? Encoding.UTF8.GetBytes(File.ReadAllText(_pagePath))
                        : Encoding.UTF8.GetBytes("client.html missing next to esportsutil");
                    Write(ctx, "text/html", page);
                }
                else if (path == "/api/local_status")
                {
                    Write(ctx, "application/json", Encoding.UTF8.GetBytes(Wire.ToJson(Snapshot(_loop, _cfg))));
                }
                else { ctx.Response.StatusCode = 404; ctx.Response.Close(); }
            }
            catch (Exception) { try { ctx.Response.Abort(); } catch { } }
        }
    }

    private static void Write(HttpListenerContext ctx, string contentType, byte[] body)
    {
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.OutputStream.Write(body);
        ctx.Response.Close();
    }

    public void Stop()
    {
        try { _listener.Close(); } catch { }
    }

    public static LocalStatus Snapshot(AgentLoop loop, EsportsConfig cfg)
    {
        var rep = loop.LastReport;
        return new LocalStatus(
            AgentInfo.Version,
            loop.CurrentIdentity?.ClientId, loop.CurrentIdentity?.DevicePin,
            cfg.ServerUrl, cfg.HeartbeatIntervalSec,
            loop.IsRegistered, loop.PairedDisplayName, loop.LastDesiredState, loop.LastDownedReason,
            loop.LastAckAt, loop.LastAckOk, loop.LastAckError,
            loop.LastReportAt, loop.ReportsSent, loop.LastReportHash,
            rep?.MachineState?["machineModel"]?.GetValue<string>(),
            rep?.GameReports ?? new(), rep?.Duplicates ?? new(),
            loop.RecentJobs.ToList(),
            // detached copy — a JsonNode can't be re-parented into the status object directly
            rep?.MachineState?["windowsUpdate"] is JsonObject wuNode ? JsonNode.Parse(wuNode.ToJsonString()) : null,
            rep?.MachineState?["wallpaperSync"] is JsonObject wsNode ? JsonNode.Parse(wsNode.ToJsonString()) : null);
    }
}
