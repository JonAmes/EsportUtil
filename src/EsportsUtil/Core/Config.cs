using System.Text.Json;

namespace EsportsUtil.Core;

/// <summary>esportsutil.config.json — role selection, endpoints, cadences, and the
/// operator-tunable resource budgets from PLAN.md.</summary>
public sealed class EsportsConfig
{
    public string Role { get; set; } = "agent";                 // server | agent
    public string ServerUrl { get; set; } = "http://127.0.0.1:4300";
    public int Port { get; set; } = 4300;                        // server API + dashboard bind port
    public string DataDir { get; set; } = "data";
    public List<string> ScanDirs { get; set; } = new();          // game install roots for folder scan
    public int HeartbeatIntervalSec { get; set; } = 10;
    public int HeartbeatTimeoutSec { get; set; } = 120;
    public int SlowScanIntervalMin { get; set; } = 15;           // never scan per-heartbeat
    public int LocateTtlSec { get; set; } = 30;
    public string Printer { get; set; } = "";                    // named Windows queue; "" = file fallback
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
    }
}
