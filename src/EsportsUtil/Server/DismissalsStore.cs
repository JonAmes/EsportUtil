using EsportsUtil.Core;

namespace EsportsUtil.Server;

/// <summary>T36: operator compliance dismissals — "clear" means THIS (kind, subject) on THIS device is
/// intentional, kept in <data>/issue-dismissals.json. Keys stay quiet until the operator restores or
/// re-clears; a NEW (kind, subject) finding is never auto-cleared. Restoring does not fix anything —
/// it just lets the Comparator speak again if the condition is still true.</summary>
public sealed class DismissalsStore
{
    public sealed record Dismissal(string ClientId, string Kind, string Subject, long ClearedUnix);

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Dismissal> _rows = new();

    public DismissalsStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "issue-dismissals.json");
        Directory.CreateDirectory(dataDir);
        if (File.Exists(_path))
            _rows = Wire.FromJson<List<Dismissal>>(File.ReadAllText(_path)) ?? new();
    }

    /// <summary>Dismiss the device's CURRENT findings (union — earlier dismissals stay armed).
    /// Returns how many NEW keys were armed.</summary>
    public int DismissAll(string clientId, IEnumerable<Issue> issues, DateTimeOffset now)
    {
        lock (_gate)
        {
            var added = 0;
            foreach (var i in issues)
            {
                if (_rows.Any(r => Key(r.ClientId, r.Kind, r.Subject) == Key(clientId, i.Kind, i.Subject))) continue;
                _rows.Add(new Dismissal(clientId, i.Kind, i.Subject, now.ToUnixTimeSeconds()));
                added++;
            }
            if (added > 0) Save();
            return added;
        }
    }

    /// <summary>Remove every dismissal for the device; findings re-speak if still true. Returns count removed.</summary>
    public int Restore(string clientId)
    {
        lock (_gate)
        {
            var n = _rows.RemoveAll(r => r.ClientId == clientId);
            if (n > 0) Save();
            return n;
        }
    }

    public bool IsDismissed(string clientId, string kind, string subject)
    {
        lock (_gate)
            return _rows.Any(r => Key(r.ClientId, r.Kind, r.Subject) == Key(clientId, kind, subject));
    }

    public int CountFor(string clientId)
    {
        lock (_gate) return _rows.Count(r => r.ClientId == clientId);
    }

    // kind+subject compared case-insensitively (app/game names are), client_id exactly.
    private static string Key(string cid, string kind, string subject) =>
        $"{cid}\u0000{kind.ToLowerInvariant()}\u0000{subject.ToLowerInvariant()}";

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, Wire.ToJson(_rows));
        File.Move(tmp, _path, overwrite: true);
    }
}
