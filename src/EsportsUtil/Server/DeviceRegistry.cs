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
    public string? MachineModel { get; set; }
    public DateTimeOffset RegisteredTimestamp { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public string DesiredState { get; set; } = "active";     // active | downed
    public string? DownedContentId { get; set; }
    public string? DownedReason { get; set; }
    public string? Notes { get; set; }

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
    private readonly object _gate = new();
    private readonly Dictionary<string, RegistryEntry> _devices = new();

    public DeviceRegistry(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "registry.json");
        if (File.Exists(_path))
            foreach (var e in Wire.FromJson<List<RegistryEntry>>(File.ReadAllText(_path)) ?? new())
                _devices[e.ClientId] = e;
    }

    public bool TryRegister(string clientId, string devicePin, string? machineModel, out RegistryEntry entry)
    {
        if (!PinRx.IsMatch(devicePin)) { entry = null!; return false; }
        lock (_gate)
        {
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

    public bool Rename(string clientId, string displayName)
    {
        displayName = displayName.Trim();
        if (displayName.Length is 0 or > 48) return false;
        lock (_gate)
        {
            if (!_devices.TryGetValue(clientId, out var e)) return false;
            if (e.Pending && displayName.Equals("pending", StringComparison.OrdinalIgnoreCase)) return false;
            e.DisplayName = displayName;
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

    private void Save() => File.WriteAllText(_path, Wire.ToJson(_devices.Values.ToList()));
}
