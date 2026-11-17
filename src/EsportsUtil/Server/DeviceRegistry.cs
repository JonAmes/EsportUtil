using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using EsportsUtil.Core;

namespace EsportsUtil.Server;

public sealed class RegistryEntry
{
    public string ClientId { get; set; } = "";
    public string DevicePin { get; set; } = "";
    public string DisplayName { get; set; } = "";            // "" until operator names it
    public string? Location { get; set; }                    // venue seat/zone, set during onboarding
    public string? MachineModel { get; set; }
    public DateTimeOffset RegisteredTimestamp { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public string DesiredState { get; set; } = "active";     // active | downed
    public string? DownedContentId { get; set; }
    public string? DownedReason { get; set; }
    public string? Notes { get; set; }
    // Last Windows Update summary reported by this agent (ingested from machine_state.windowsUpdate).
    public string? WuStatus { get; set; }        // ok | updates | error | unsupported
    public int? WuPending { get; set; }
    public long? WuScanUnix { get; set; }
    // Wallpaper drift verdict from an agent report (machine_state.wallpaperSync): true only when an
    // enforced target exists AND the desktop's wallpaper file no longer hashes to it.
    public bool WpDrift { get; set; }
    public string? WpExpectedSha { get; set; }
    public string? WpActualSha { get; set; }
    // Venue-app install verdicts (machine_state.apps, T29): raw JSON array kept verbatim so the
    // dashboard/settings page render exactly what the agent reported. Null = never reported.
    public string? AppsJson { get; set; }
    public string? InstalledAppsJson { get; set; }
    public string? SelfUpdateJson { get; set; }

    [JsonIgnore] public List<Directive> PendingDirectives { get; set; } = new();

    public bool Pending => string.IsNullOrEmpty(DisplayName);
}

/// <summary>Device registry — persistent identity + desired state. PIN onboarding gate,
/// operator rename, downed reconciliation, and TTL directive stamping. One-shot directives
/// are transient (never persisted): they ride the next heartbeat ack or expire.</summary>
public sealed class DeviceRegistry
{
    private static readonly Regex PinRx = new("^[0-9]{4}$", RegexOptions.Compiled);
    private readonly string _path;
    private readonly string _revokedPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, RegistryEntry> _devices = new();
    private readonly HashSet<string> _revoked = new();   // offboarded client_ids — never re-register

    public DeviceRegistry(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "registry.json");
        _revokedPath = Path.Combine(dataDir, "revoked.json");
        if (File.Exists(_path))
            foreach (var e in Wire.FromJson<List<RegistryEntry>>(File.ReadAllText(_path)) ?? new())
                _devices[e.ClientId] = e;
        if (File.Exists(_revokedPath))
            foreach (var id in Wire.FromJson<List<string>>(File.ReadAllText(_revokedPath)) ?? new())
                _revoked.Add(id);
    }

    public bool TryRegister(string clientId, string devicePin, string? machineModel, out RegistryEntry entry)
    {
        if (!PinRx.IsMatch(devicePin)) { entry = null!; return false; }
        lock (_gate)
        {
            if (_revoked.Contains(clientId)) { entry = null!; return false; }   // offboarded: hard gate
            if (_devices.TryGetValue(clientId, out var existing))
            {
                entry = existing;
                existing.MachineModel ??= machineModel;   // re-registration updates nothing but model hint
                Save();
                return true;
            }
            entry = new RegistryEntry
            {
                ClientId = clientId, DevicePin = devicePin, MachineModel = machineModel,
                RegisteredTimestamp = DateTimeOffset.UtcNow,
            };
            _devices[clientId] = entry;
            Save();
            return true;
        }
    }

    public bool Heartbeat(string clientId, out RegistryEntry entry)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var dev)) { entry = null!; return false; }
            entry = dev;
            dev.LastHeartbeat = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    public bool Rename(string clientId, string displayName, string? location = null)
    {
        displayName = displayName.Trim();
        if (displayName.Length is 0 or > 48) return false;
        location = location?.Trim();
        if (location is { Length: > 48 }) return false;
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return false;
            if (e.Pending && displayName.Equals("pending", StringComparison.OrdinalIgnoreCase)) return false;
            e.DisplayName = displayName;
            if (location is not null) e.Location = location.Length == 0 ? null : location;
            Save();
            return true;
        }
    }

    public bool SetDowned(string clientId, string desiredState, string? contentId, string? reason)
    {
        if (desiredState is not ("active" or "downed")) return false;
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return false;
            e.DesiredState = desiredState;
            e.DownedContentId = desiredState == "downed" ? contentId : null;
            e.DownedReason = desiredState == "downed" ? reason : null;
            Save();
            return true;
        }
    }

    /// <summary>Current operator name ("" before rename) — locate directives carry it as payload so the
    /// overlay shows the human label without the agent having to ask.</summary>
    public string? DisplayNameOf(string clientId)
    {
        lock (_gate) return _devices.TryGetValue(clientId, out var e) ? e.DisplayName : null;
    }

    /// <summary>Stamp a transient one-shot directive (e.g. locate). Not persisted; expires if undelivered.</summary>
    public bool QueueDirective(string clientId, Directive directive)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return false;
            e.PendingDirectives.Add(directive);
            return true;
        }
    }

    /// <summary>Store the latest Windows Update summary from an accepted report (a null summary
    /// clears stale values when an older agent re-reports without the section).</summary>
    public void SetUpdateState(string clientId, string? status, int? pending, long? scanUnix)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return;
            if (e.WuStatus == status && e.WuPending == pending && e.WuScanUnix == scanUnix) return;
            e.WuStatus = status; e.WuPending = pending; e.WuScanUnix = scanUnix;
            Save();
        }
    }

    /// <summary>Store the latest wallpaper drift verdict from an accepted report (no-op on
    /// unchanged triples so heartbeats don't rewrite the registry file every beat).</summary>
    public void SetWallpaperState(string clientId, bool drift, string? expectedSha, string? actualSha)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return;
            if (e.WpDrift == drift && e.WpExpectedSha == expectedSha && e.WpActualSha == actualSha) return;
            e.WpDrift = drift; e.WpExpectedSha = expectedSha; e.WpActualSha = actualSha;
            Save();
        }
    }

    /// <summary>Store the latest venue-app verdicts JSON (machine_state.apps) verbatim.</summary>
    public void SetApps(string clientId, string? appsJson)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return;
            if (e.AppsJson == appsJson) return;
            e.AppsJson = appsJson;
            Save();
        }
    }

    /// <summary>Store the latest full installed-app inventory JSON (machine_state.installedApps,
    /// four-view registry Uninstall scan) verbatim.</summary>
    public void SetInstalledApps(string clientId, string? installedAppsJson)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return;
            if (e.InstalledAppsJson == installedAppsJson) return;
            e.InstalledAppsJson = installedAppsJson;
            Save();
        }
    }

    /// <summary>Store the latest self-update verdict JSON (machine_state.selfUpdate) verbatim.</summary>
    public void SetSelfUpdate(string clientId, string? json)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return;
            if (e.SelfUpdateJson == json) return;
            e.SelfUpdateJson = json;
            Save();
        }
    }

    /// <summary>Take undelivered, unexpired directives for this device and clear them (one-shot).</summary>
    public List<Directive> TakeDirectives(string clientId)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return new();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var live = e.PendingDirectives.Where(d => d.ExpiresUnix > now).ToList();
            e.PendingDirectives.Clear();
            return live;
        }
    }

    /// <summary>Operator offboarding: remove the device AND tombstone its client_id so the same
    /// identity can never silently re-register. A still-running agent gets "revoked" on its next
    /// call, rotates to a fresh client_id, and returns as a PENDING pairing — operator-gated again.</summary>
    public bool Offboard(string clientId)
    {
        lock (_gate)
        {
            if (!_devices.Remove(clientId)) return false;
            _revoked.Add(clientId);
            Save();
            return true;
        }
    }

    public bool IsRevoked(string clientId) { lock (_gate) return _revoked.Contains(clientId); }

    public bool Exists(string clientId) { lock (_gate) return _devices.ContainsKey(clientId); }
    public bool UpdateModel(string clientId, string? model)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return false;
            if (!string.IsNullOrEmpty(model)) e.MachineModel = model;
            Save();
            return true;
        }
    }

    public List<RegistryEntry> Snapshot() { lock (_gate) return _devices.Values.ToList(); }

    private void Save()
    {
        File.WriteAllText(_path, Wire.ToJson(_devices.Values.ToList()));
        File.WriteAllText(_revokedPath, Wire.ToJson(_revoked.ToList()));
    }
}
