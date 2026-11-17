using System.Text.Json;
using System.Text.RegularExpressions;

namespace EsportsUtil.Core;

/// <summary>First-run setup: with no esportsutil.config.json present (and no --role flag),
/// the operator is asked — Server or Agent — and a ready-to-run config is written next to
/// the executable. Role is still never auto-detected: the app asks instead of guessing.
/// Pure logic lives here (dialog/console are thin shells) so it is testable everywhere.</summary>
public static class FirstRunSetup
{
    public const string ConfigFileName = "esportsutil.config.json";
    public const int DefaultPort = 4300;

    /// <summary>Where the config belongs: next to the real esportsutil executable (single-file
    /// AppContext.BaseDirectory resolves to the self-extract dir, so ProcessPath is checked
    /// first); for `dotnet app.dll` runs that falls back to the assembly directory.</summary>
    public static string ConfigHomeDir()
    {
        var pp = Environment.ProcessPath;
        if (pp is not null && Path.GetFileNameWithoutExtension(pp).Equals("esportsutil", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(pp) ?? AppContext.BaseDirectory;
        return AppContext.BaseDirectory;
    }

    /// <summary>Config lookup order: next to the executable, then the current directory.
    /// Returns null on first run.</summary>
    public static string? FindConfigFile()
    {
        foreach (var dir in new[] { ConfigHomeDir(), Directory.GetCurrentDirectory() })
        {
            var p = Path.Combine(dir, ConfigFileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Accepts a bare IP/host ("10.140.0.212"), host:port, or a full http(s) URL and
    /// normalizes to scheme://host[:port] with no trailing slash. Null on garbage input.</summary>
    public static string? NormalizeServerUrl(string? input)
    {
        var s = (input ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return null;
        if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return null;
            return $"{u.Scheme}://{u.Authority}";
        }
        if (!Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9._-]*(:\d{1,5})?$")) return null;
        if (!s.Contains(':')) return $"http://{s}:{DefaultPort}";
        var port = int.Parse(s[(s.IndexOf(':') + 1)..]);
        return port is >= 1 and <= 65535 ? $"http://{s}" : null;
    }

    public static EsportsConfig BuildConfig(string role, string? serverUrl, string baseDir)
    {
        if (role is not ("server" or "agent"))
            throw new ArgumentException($"role must be server or agent, got '{role}'");
        var cfg = new EsportsConfig
        {
            Role = role,
            // data lives under the exe so a double-click launch never depends on the CWD
            DataDir = Path.Combine(baseDir, "esportsutil-data", role),
        };
        if (role == "agent")
            cfg.ServerUrl = NormalizeServerUrl(serverUrl) ?? throw new ArgumentException($"invalid server address: '{serverUrl}'");
        else
            cfg.ServerUrl = $"http://0.0.0.0:{DefaultPort}";   // server binds all NICs so LAN agents reach it
        return cfg;
    }

    public static string WriteConfig(string dir, EsportsConfig cfg)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ConfigFileName);
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, WriteOptions));
        return path;
    }

    private static readonly JsonSerializerOptions WriteOptions = new(Wire.Options) { WriteIndented = true };
}
