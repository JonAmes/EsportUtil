using System.Collections.Concurrent;
using EsportsUtil.Core;

namespace EsportsUtil.Server;

/// <summary>MasterManifest.json storage — the venue source of truth (master list + factory state map).
/// Also stores each client's last Local Manifest as proof-of-state, with a small summary for the dashboard.</summary>
public sealed class ManifestStore
{
    private readonly string _dataDir;
    private readonly object _gate = new();

    public ManifestStore(string dataDir)
    {
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(ReportsDir);
        // T44: summaries (agent version, game counts) drive the dashboard chips AND the queue
        // capability gate — hydrate from stored reports at startup so a server restart never
        // blanks the agent build chip or blinds the gate until every station reports again.
        foreach (var f in Directory.GetFiles(ReportsDir, "*.json"))
        {
            try
            {
                var r = Wire.FromJson<LocalManifest>(File.ReadAllText(f));
                if (r is null || string.IsNullOrEmpty(r.ClientId)) continue;
                Summaries[r.ClientId] = new ManifestSummary(r.ClientId, r.ReportTimestamp, "",
                    r.GameReports?.Count ?? 0, r.Duplicates?.Count ?? 0, r.AgentVersion,
                    r.MachineState?["machineModel"]?.GetValue<string>());
            }
            catch { /* a corrupt report file skips; the station's next report replaces it */ }
        }
    }

    private string ManifestPath => Path.Combine(_dataDir, "MasterManifest.json");
    private string PolicyPath => Path.Combine(_dataDir, "ApprovedSoftwarePolicy.json");
    public string ReportsDir => Path.Combine(_dataDir, "reports");

    /// <summary>Approved Software Policy — operator-maintained; absent = report-only evaluation disabled.</summary>
    public ApprovedSoftwarePolicy? LoadPolicy()
    {
        lock (_gate)
            return File.Exists(PolicyPath)
                ? Wire.FromJson<ApprovedSoftwarePolicy>(File.ReadAllText(PolicyPath))
                : null;
    }

    public MasterManifest LoadMaster()
    {
        lock (_gate)
        {
            return File.Exists(ManifestPath)
                ? Wire.FromJson<MasterManifest>(File.ReadAllText(ManifestPath))
                  ?? new MasterManifest(1, DateTimeOffset.UtcNow, new(), new())
                : new MasterManifest(1, DateTimeOffset.UtcNow, new(), new());
        }
    }

    public void SaveMaster(MasterManifest m)
    {
        lock (_gate)
            File.WriteAllText(ManifestPath, Wire.ToJson(m with { UpdatedTimestamp = DateTimeOffset.UtcNow }));
    }

    public void SaveReport(LocalManifest report, string contentHash)
    {
        lock (_gate)
            File.WriteAllText(Path.Combine(ReportsDir, report.ClientId + ".json"), Wire.ToJson(report));
        Summaries[report.ClientId] = new ManifestSummary(
            report.ClientId, report.ReportTimestamp, contentHash, report.GameReports.Count,
            report.Duplicates?.Count ?? 0, report.AgentVersion,
            report.MachineState?["machineModel"]?.GetValue<string>());
    }

    /// <summary>T36: the device's last full Local Manifest as proof-of-state (null = never reported).</summary>
    public LocalManifest? LoadReport(string clientId)
    {
        var p = Path.Combine(ReportsDir, clientId + ".json");
        return File.Exists(p) ? Wire.FromJson<LocalManifest>(File.ReadAllText(p)) : null;
    }

    public sealed record ManifestSummary(string ClientId, DateTimeOffset ReportTimestamp, string ContentHash,
        int GameCount, int DuplicateCount, string AgentVersion, string? MachineModel);

    public ConcurrentDictionary<string, ManifestSummary> Summaries { get; } = new();
}
