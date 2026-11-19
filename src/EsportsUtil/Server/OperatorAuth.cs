using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EsportsUtil.Core;

namespace EsportsUtil.Server;

/// <summary>T35: operator password hashes — PBKDF2-SHA256, per-account random salt, 100k rounds.
/// The stored string is the whole recipe ("pbkdf2-sha256$iters$salt$hash") so verification needs
/// no other state and a plaintext password is NEVER kept anywhere. Verification is constant-time.</summary>
public static class PasswordHash
{
    public const int Iterations = 100_000;
    private const string Prefix = "pbkdf2-sha256";

    public static string Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Prefix}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = (stored ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != Prefix) return false;
        if (!int.TryParse(parts[1], out var iters) || iters < 1000) return false;
        byte[] salt, want;
        try { salt = Convert.FromBase64String(parts[2]); want = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        var got = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt,
            iters, HashAlgorithmName.SHA256, want.Length);
        return CryptographicOperations.FixedTimeEquals(got, want);
    }
}

/// <summary>T35: per-operator accounts — <dataDir>/operators.json, the single source of truth for
/// whether the dashboard requires sign-in (empty store = today's open dashboard). Atomic tmp+move
/// write like every other server artifact. Usernames are normalized to lowercase and validated —
/// passwords never leave this file as anything but a PBKDF2 recipe.</summary>
public sealed class OperatorsStore
{
    public sealed record StoredOperator(string Username, string PasswordHash);
    public const string FileName = "operators.json";
    private const int MinPasswordLen = 6;
    private static readonly Regex UserPat = new(@"^[a-z0-9][a-z0-9._-]{1,31}$", RegexOptions.Compiled);

    private readonly string _path;
    private readonly List<StoredOperator> _ops = new();
    private readonly object _gate = new();

    public OperatorsStore(string dataDir)
    {
        _path = Path.Combine(dataDir, FileName);
        if (File.Exists(_path))
            _ops = JsonSerializer.Deserialize<List<StoredOperator>>(File.ReadAllText(_path), Wire.Options) ?? new();
    }

    /// <summary>Seed from config once: only when the store is empty, so deleting every operator
    /// is a deliberate "back to open" and never silently re-seeds on the next restart.</summary>
    public void BootstrapFromConfig(EsportsConfig cfg)
    {
        var seed = cfg.BootstrapOperator;
        if (seed is null || string.IsNullOrWhiteSpace(seed.Username) || string.IsNullOrEmpty(seed.Password)) return;
        lock (_gate)
        {
            if (_ops.Count > 0) return;
            if (!ValidUser(seed.Username, out var user) || !ValidPassword(seed.Password, out _)) return;
            _ops.Add(new StoredOperator(user, PasswordHash.Create(seed.Password)));
            Save();
        }
    }

    public bool Any() { lock (_gate) return _ops.Count > 0; }
    public List<string> Usernames() { lock (_gate) return _ops.Select(o => o.Username).ToList(); }

    public bool Add(string username, string password, out string? error)
    {
        error = null;
        if (!ValidUser(username, out var user)) { error = "username must be 2-32 chars: letters, digits, dot, dash, underscore"; return false; }
        if (!ValidPassword(password, out error)) return false;
        lock (_gate)
        {
            if (_ops.Any(o => o.Username == user)) { error = $"operator '{user}' already exists"; return false; }
            _ops.Add(new StoredOperator(user, PasswordHash.Create(password)));
            Save();
        }
        return true;
    }

    public bool Remove(string username)
    {
        var user = (username ?? "").Trim().ToLowerInvariant();
        lock (_gate)
        {
            var n = _ops.RemoveAll(o => o.Username == user);
            if (n == 0) return false;
            Save();
            return true;
        }
    }

    public bool SetPassword(string username, string password, out string? error)
    {
        error = null;
        var user = (username ?? "").Trim().ToLowerInvariant();
        if (!ValidPassword(password, out error)) return false;
        lock (_gate)
        {
            var i = _ops.FindIndex(o => o.Username == user);
            if (i < 0) { error = "unknown operator"; return false; }
            _ops[i] = _ops[i] with { PasswordHash = PasswordHash.Create(password) };
            Save();
            return true;
        }
    }

    /// <summary>Verify credentials; null = reject. Returns the normalized username on success.</summary>
    public string? Verify(string? username, string? password)
    {
        var user = (username ?? "").Trim().ToLowerInvariant();
        lock (_gate)
        {
            var op = _ops.FirstOrDefault(o => o.Username == user);
            return op is not null && PasswordHash.Verify(password ?? "", op.PasswordHash) ? user : null;
        }
    }

    private static bool ValidUser(string? raw, out string user)
    {
        user = (raw ?? "").Trim().ToLowerInvariant();
        return UserPat.IsMatch(user);
    }
    private static bool ValidPassword(string? raw, out string? error)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length < MinPasswordLen) { error = $"password must be at least {MinPasswordLen} characters"; return false; }
        error = null;
        return true;
    }

    private void Save()   // caller holds _gate
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_ops, Wire.Options));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>T35: in-memory dashboard sessions (random 32-byte tokens, 12 h sliding window).
/// Deliberately not persisted: a server restart signs everyone out, which is the safe default.</summary>
public sealed class SessionStore
{
    public const string CookieName = "esu_session";
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(12);

    private sealed class Session(string username)
    {
        public readonly string Username = username;
        public long ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)Ttl.TotalSeconds;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Session> _sessions = new();

    public string NewToken()
    {
        string token;
        do token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                     .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        while (_sessions.ContainsKey(token));
        return token;
    }
    public void Add(string token, string username) => _sessions[token] = new Session(username);
    public string? User(string token) =>
        _sessions.TryGetValue(token, out var s) && s.ExpiresUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? s.Username : null;
    /// <summary>Validate + slide the expiry in one step (the middleware hits this on every request).</summary>
    public bool Touch(string token)
    {
        if (!_sessions.TryGetValue(token, out var s)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (s.ExpiresUnix <= now) { _sessions.TryRemove(token, out _); return false; }
        s.ExpiresUnix = now + (long)Ttl.TotalSeconds;
        return true;
    }
    public void Revoke(string token) => _sessions.TryRemove(token, out _);
    public void RevokeUser(string username)
    {
        foreach (var (k, s) in _sessions)
            if (s.Username == username) _sessions.TryRemove(k, out _);
    }
}

/// <summary>T35: sign-in throttle — 5 failures per (ip, username) lock that pair out for a minute.
/// In-memory on purpose: a restart clears it, and the venue LAN is not an adversarial internet.</summary>
public sealed class LoginGuard
{
    public const int MaxFails = 5;
    public static readonly TimeSpan CoolOff = TimeSpan.FromSeconds(60);

    private sealed record Strike(int Fails, long LockUntilUnix);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Strike> _strikes = new();

    public bool Locked(string key) =>
        _strikes.TryGetValue(key, out var s) && s.LockUntilUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Fail(string key) => _strikes.AddOrUpdate(key,
        _ => new Strike(1, 0),
        (_, old) => old.Fails + 1 >= MaxFails ? new Strike(old.Fails + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)CoolOff.TotalSeconds)
                                              : new Strike(old.Fails + 1, 0));

    public void Pass(string key) => _strikes.TryRemove(key, out _);
}
