using System.Text.Json;

namespace EsportsUtil.Core;

/// <summary>esportsutil.config.json — role selection, endpoints, cadences, and the
/// operator-tunable resource budgets from PLAN.md.</summary>
public sealed class EsportsConfig
{
    public string Role { get; set; } = "agent";                 // server | agent
    public string ServerUrl { get; set; } = "";                  // "" = role default: server binds 0.0.0.0, agent loopback
    public int Port { get; set; } = 4300;                        // server API + dashboard bind port
    public string DataDir { get; set; } = "data";
    public List<string> ScanDirs { get; set; } = new();          // game install roots for folder scan
    public int HeartbeatIntervalSec { get; set; } = 10;
    public int HeartbeatTimeoutSec { get; set; } = 120;
    public int SlowScanIntervalMin { get; set; } = 15;           // never scan per-heartbeat
    public int UpdateScanIntervalMin { get; set; } = 360;        // Windows Update poll cadence; 0 = timer off (directives still work)
    public int LocateTtlSec { get; set; } = 30;
    public string Printer { get; set; } = "";                    // named Windows queue; "" = file fallback
    public bool ToastNotifications { get; set; } = true;         // Windows toast on the server box (Phase 3)
    public bool AgentDashboard { get; set; } = true;             // agent role serves the client view on localhost
    public int AgentDashboardPort { get; set; } = 4310;          // loopback only; 0 = ephemeral (tests)
    public string SteamCmdPath { get; set; } = "steamcmd";       // agent-side updater executable (allow-listed handler only)
    public int JobTimeoutMin { get; set; } = 90;                 // per-job wall clock cap; running jobs past this + ack window are abandoned
    public Budget Budgets { get; set; } = new();

    public sealed class Budget
    {
        public double IdleCpuPercent { get; set; } = 1.0;
        public int WorkingSetMb { get; set; } = 60;
        public int TrayStubMb { get; set; } = 15;
    }

    public static EsportsConfig Load(string? path)
    {
        var cfg = new EsportsConfig();
        if (path is not null && File.Exists(path))
            cfg = JsonSerializer.Deserialize<EsportsConfig>(File.ReadAllText(path), Wire.Options) ?? cfg;
        return cfg;
    }

    public void ApplyRoleOverride(string? roleFlag)
    {
        if (!string.IsNullOrEmpty(roleFlag)) Role = roleFlag;
        if (Role is not ("server" or "agent"))
            throw new ArgumentException($"--role must be server or agent, got '{Role}'");
        // Server defaults to binding every NIC so LAN agents can reach it; agents default to loopback.
        if (string.IsNullOrWhiteSpace(ServerUrl))
            ServerUrl = Role == "server" ? $"http://0.0.0.0:{Port}" : $"http://127.0.0.1:{Port}";
    }
}
