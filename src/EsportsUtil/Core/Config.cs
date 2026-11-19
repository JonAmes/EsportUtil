using System.Text.Json;

namespace EsportsUtil.Core;

/// <summary>esportsutil.config.json — role selection, endpoints, cadences, and the
/// operator-tunable resource budgets from PLAN.md.</summary>
public sealed class EsportsConfig
{
    /// <summary>T37: a RELATIVE data_dir binds to the config FILE's directory, never the launch
    /// CWD — an EXE moved or relaunched from elsewhere keeps its identity/PIN/state where the
    /// operator put them. Absolute data_dir values (wizard default, examples) are untouched.</summary>
    public void ResolveDataDir(string? configPath)
    {
        if (configPath is not null && !Path.IsPathRooted(DataDir))
            DataDir = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(configPath))!, DataDir));
    }

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
    public bool SteamCmdAutoInstall { get; set; } = true;        // bootstrap missing steamcmd via winget (Valve.SteamCMD)
    // ---- dashboard sign-in (T35): accounts live in <data>/operators.json; this seeds that store once ----
    public OperatorSeed? BootstrapOperator { get; set; }         // {username,password} imported only while the store is empty
    // ---- LAN autodetection (T35): server answers UDP probes on this port; 0 disables ----
    public int DiscoveryPort { get; set; } = Discovery.DefaultPort;
    // ---- self-update (T30): agent pulls the latest GitHub release of itself and restarts ----
    public int SelfUpdateCheckIntervalMin { get; set; } = 360;   // 0 disables timed checks (dashboard button still works)
    public bool SelfUpdateApply { get; set; } = true;            // download+verify+restart when a newer release exists
    public string GitHubRepo { get; set; } = "JonAmes/EsportUtil"; // releases/latest + esportsutil.exe asset
    public bool TrayAutoStart { get; set; } = true;              // tray writes HKCU Run key: auto-loads at logon, no CLI
    public int JobTimeoutMin { get; set; } = 90;                 // per-job wall clock cap; running jobs past this + ack window are abandoned
    public Budget Budgets { get; set; } = new();

    public sealed class Budget
    {
        public double IdleCpuPercent { get; set; } = 1.0;
        public int WorkingSetMb { get; set; } = 64;   // was 60 at lifecycle phase (57.3 MB measured);
                                                       // WU COM + SteamCMD + app catalog + self-update grew the floor
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

/// <summary>T35: first-start convenience for the server config — imported into
/// operators.json ONCE (only while that store is empty), never re-seeded after.</summary>
public sealed class OperatorSeed
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}
