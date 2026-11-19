using System.Text.Json;
using EsportsUtil.Core;

namespace EsportsUtil.Server;

/// <summary>Phase 4 durable job queue (jobs.json). One job in flight per machine: heartbeat acks
/// hand out the oldest queued job; running jobs the agent never finishes (crash/offline) go
/// 'failed: abandoned' past the timeout so the fleet never stalls on a zombie job.</summary>
public sealed class JobStore
{
    private readonly string _path;
    private readonly List<Job> _jobs = new();
    private readonly object _gate = new();

    public JobStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "Jobs.json");
        if (File.Exists(_path))
        {
            try { _jobs.AddRange(JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_path), Wire.Options) ?? new()); }
            catch (JsonException) { /* corrupt queue: start clean rather than crash the server */ }
        }
    }

    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_jobs, Wire.Options));

    public Job Enqueue(string clientId, string kind, string subject, string? arg, string? path)
    {
        lock (_gate)
        {
            var j = new Job(Guid.NewGuid().ToString("N"), clientId, kind, subject, arg, path,
                CreatedUnix: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _jobs.Add(j);
            Save();
            return j;
        }
    }

    public bool HasOpen(string clientId, string kind, string subject)
    {
        lock (_gate)
            return _jobs.Any(j => j.ClientId == clientId && j.Kind == kind && j.Subject == subject
                && j.State is "queued" or "running");
    }

    /// <summary>Hand the oldest queued job for this machine (heartbeat ack). Jobs are NOT flipped to
    /// running here — the agent reports that explicitly, so a dropped ack just redelivers.</summary>
    public Job? NextQueued(string clientId)
    {
        lock (_gate)
        {
            // T45: one job in flight per machine, enforced — the docstring always promised it. A
            // running job means SteamCMD (or an installer) owns this machine right now; handing a
            // second job around a stalled/zombie state caused concurrent SteamCMDs, which collide
            // on the content platform and surface as opaque 'steamcmd exit 7'. FailAbandoned still
            // times the zombie out, so the queue can never stall forever.
            if (_jobs.Any(j => j.ClientId == clientId && j.State == "running")) return null;
            return _jobs.Where(j => j.ClientId == clientId && j.State == "queued")
                        .OrderBy(j => j.CreatedUnix).FirstOrDefault();
        }
    }

    /// <summary>Agent-reported state transition. Returns the updated job, or null for an unknown/mismatched id.</summary>
    public Job? Update(string jobId, string clientId, string state, string? detail, int? progress = null)
    {
        lock (_gate)
        {
            var idx = _jobs.FindIndex(x => x.JobId == jobId && x.ClientId == clientId);
            if (idx < 0) return null;
            var j = _jobs[idx];
            if (j.State is "succeeded" or "failed" or "cancelled") return null;   // terminal states are final
            var updated = j with
            {
                State = state,
                Detail = detail ?? j.Detail,
                Progress = progress ?? j.Progress,
                StartedUnix = state == "running" ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : j.StartedUnix,
                FinishedUnix = state is "succeeded" or "failed" ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : j.FinishedUnix,
            };
            _jobs[idx] = updated;
            Save();
            return updated;
        }
    }

    /// <summary>Queue-safety sweep: a running job older than its timeout never reported — the agent
    /// died mid-flight. Fail it so the dashboard and future retries are honest.</summary>
    public List<Job> FailAbandoned(int timeoutMin)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)timeoutMin * 60 - 120;
        lock (_gate)
        {
            var hit = _jobs.Where(j => j.State == "running" && (j.StartedUnix ?? j.CreatedUnix) < cutoff).ToList();
            foreach (var j in hit) Update(j.JobId, j.ClientId, "failed", "abandoned — agent stopped reporting");
            return hit;
        }
    }

    public List<Job> ForClient(string clientId)
    {
        lock (_gate)
            return _jobs.Where(j => j.ClientId == clientId)
                        .OrderByDescending(j => j.CreatedUnix).Take(10).ToList();
    }

    public List<Job> Recent(int n = 30)
    {
        lock (_gate)
            return _jobs.OrderByDescending(j => j.CreatedUnix).Take(n).ToList();
    }
}
