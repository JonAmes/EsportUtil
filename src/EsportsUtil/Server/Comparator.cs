using System.Text.Json.Nodes;
using EsportsUtil.Core;

namespace EsportsUtil.Server;

// ---- ApprovedSoftwarePolicy.json — operator-maintained allow-list (schemas/approved_software.schema.json).
// Phase 2 evaluates it report-only: unapproved apps are flagged data, never acted on client-side. ----

public sealed record SoftwareRule(string Name, string? NamePrefix = null, string? Publisher = null, string? Notes = null);

public sealed record ApprovedSoftwarePolicy(int Version, DateTimeOffset UpdatedTimestamp, string DefaultAction,
    List<SoftwareRule> Allow, List<SoftwareRule> Protected, string? IdleWindow = null);

/// <summary>One compliance finding for one device. Kind ∈ update_required | rollback_required |
/// missing_from_machine | unmanaged_game | duplicate_resolved | unapproved_app.</summary>
public sealed record Issue(string Kind, string Subject, string? ReportedVersion = null, string? WantedVersion = null,
    string? KeepPath = null, bool Protected = false);

/// <summary>Dotted-numeric version ordering: 1.9 &lt; 1.10; missing segments compare as 0;
/// non-numeric segments fall back to ordinal string compare.</summary>
public static class Versions
{
    public static int Cmp(string a, string b)
    {
        var pa = a.Split('.', '-', '+', '_');
        var pb = b.Split('.', '-', '+', '_');
        for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            var sa = i < pa.Length ? pa[i] : "0";
            var sb = i < pb.Length ? pb[i] : "0";
            var na = long.TryParse(sa, out var va);
            var nb = long.TryParse(sb, out var vb);
            if (na && nb) { if (va != vb) return va.CompareTo(vb); }
            else if (na) return 1;   // numeric segment outranks non-numeric ("unknown", "beta") — junk versions never win
            else if (nb) return -1;
            else { var c = string.CompareOrdinal(sa, sb); if (c != 0) return c; }
        }
        return 0;
    }
}

/// <summary>Phase 2 comparison engine: each device's last Local Manifest diffed against the
/// Master Manifest (source of truth) and the Approved Software Policy. Pure functions — the server
/// calls these on demand and alerts only when a device's issue set changes.</summary>
public static class Comparator
{
    public static bool RuleMatches(SoftwareRule r, string name, string? publisher)
    {
        // Keys present in a rule AND together; name "*" is the wildcard that lets prefix/publisher carry the rule.
        if (r.Name != "*" && !name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (r.NamePrefix is not null && !name.StartsWith(r.NamePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (r.Publisher is not null && !(publisher ?? "").Equals(r.Publisher, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static List<Issue> Games(LocalManifest r, MasterManifest m)
    {
        var issues = new List<Issue>();
        var byId = new Dictionary<string, MasterGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in m.MasterList) byId[g.GameId] = g;

        var reported = r.GameReports
            .Where(g => g.Status != "DUPLICATE_FOUND")
            .GroupBy(g => g.GameId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var rep in reported.Values)
        {
            if (!byId.TryGetValue(rep.GameId, out var want))
            {
                issues.Add(new Issue("unmanaged_game", rep.GameId, rep.CurrentVersion, KeepPath: rep.InstallPath));
                continue;
            }
            var c = Versions.Cmp(rep.CurrentVersion, want.LatestVersion);
            if (c < 0) issues.Add(new Issue("update_required", rep.GameId, rep.CurrentVersion, want.LatestVersion, rep.InstallPath));
            else if (c > 0) issues.Add(new Issue("rollback_required", rep.GameId, rep.CurrentVersion, want.LatestVersion, rep.InstallPath));
        }

        // Factory State Map priority: a master game absent from the machine is remediated toward the
        // factory baseline version when this machine's model has a factory entry.
        var model = r.MachineState?["machineModel"]?.GetValue<string>();
        var factory = m.FactoryStateMap.FirstOrDefault(f =>
            f.MachineModel != null && model != null && f.MachineModel.Equals(model, StringComparison.OrdinalIgnoreCase));
        foreach (var (id, want) in byId)
        {
            if (reported.ContainsKey(id)) continue;
            var baseline = factory?.GameBaselines.FirstOrDefault(b => b.GameId.Equals(id, StringComparison.OrdinalIgnoreCase));
            issues.Add(new Issue("missing_from_machine", id, null, baseline?.Version ?? want.LatestVersion));
        }

        // Duplicate resolution: server chooses which copy to keep — master-match wins, else highest version.
        foreach (var dup in r.Duplicates ?? new())
        {
            var copies = r.GameReports.Where(g => g.GameId == dup.GameId).ToList();
            if (copies.Count < 2) continue;
            GameReport? keep = null;
            if (byId.TryGetValue(dup.GameId, out var master))
                keep = copies.FirstOrDefault(c => c.CurrentVersion == master.LatestVersion);
            keep ??= copies.OrderByDescending(c => c.CurrentVersion, Comparer<string>.Create(Versions.Cmp)).First();
            issues.Add(new Issue("duplicate_resolved", dup.GameId,
                string.Join("|", copies.Select(c => c.CurrentVersion)), keep.CurrentVersion, KeepPath: keep.InstallPath));
        }
        return issues;
    }

    public static List<Issue> Software(LocalManifest r, ApprovedSoftwarePolicy? policy)
    {
        var issues = new List<Issue>();
        if (policy is null) return issues;   // no policy = evaluation disabled (report-only baseline needs the allow-list)
        if (r.MachineState?["installedApps"] is not JsonArray apps) return issues;
        foreach (var a in apps)
        {
            var name = a?["display_name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var pub = a?["publisher"]?.GetValue<string>();
            if (policy.Allow.Any(rule => RuleMatches(rule, name, pub!))) continue;
            var protectedHit = policy.Protected.Any(rule => RuleMatches(rule, name, pub!));
            issues.Add(new Issue("unapproved_app", name, pub, Protected: protectedHit));
        }
        return issues;
    }

    /// <summary>Stable identity of a device's issue set — used to alert only on change, not every poll.</summary>
    public static string Hash(IEnumerable<Issue> issues) =>
        string.Join(";", issues.Select(i => $"{i.Kind}:{i.Subject}:{i.ReportedVersion}:{i.WantedVersion}").OrderBy(s => s, StringComparer.Ordinal));
}
