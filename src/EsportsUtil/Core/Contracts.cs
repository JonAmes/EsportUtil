using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace EsportsUtil.Core;

/// <summary>Shared wire contracts — field-for-field with schemas/*.schema.json.
/// All serialization uses Json.Default (snake_case) options below.</summary>

public sealed record GameReport(
    string GameId,
    string FolderNameUsed,
    string InstallPath,
    string CurrentVersion,
    string Launcher,
    string Status,
    string? SteamAppId = null);

public sealed record DuplicateReport(string GameId, List<string> Paths);

public sealed record LocalManifest(
    string ClientId,
    string DevicePin,
    string? DisplayName,
    DateTimeOffset ReportTimestamp,
    string AgentVersion,
    JsonNode MachineState,
    List<GameReport> GameReports,
    List<DuplicateReport>? Duplicates = null);

public sealed record RegisterRequest(string ClientId, string DevicePin, string? MachineModel);

public sealed record HeartbeatRequest(string ClientId, string DevicePin, DateTimeOffset Timestamp, string AgentVersion);

/// <summary>Transient one-shot directive stamped on the heartbeat ack. Never persisted in the registry.</summary>
public sealed record Directive(string Type, string? Payload, long ExpiresUnix);

public sealed record AckEnvelope(
    bool Ok,
    string? Error = null,
    string DesiredState = "active",
    string? DownedContentId = null,
    List<Directive>? Directives = null,
    Job? Job = null,
    string? DisplayName = null,    // heartbeat ack only: operator's display_name once the device is paired (null = pending)
    string? DownedReason = null,   // heartbeat ack only: operator's downed note — drives the sticky downed overlay
    string? VenueWallpaperSha256 = null);  // heartbeat/register ack: sha of Content Library item `venue-wallpaper` (null = none set; agents converge on it)

public sealed record RenameRequest(string ClientId, string DisplayName, string? Location = null);

// ---- Jobs (schemas/job.schema.json) — structured remediation, never raw commands ----

/// <summary>Server-queued, agent-executed remediation. Kind is an allow-listed handler name;
/// Arg/Path are validated by the server at creation against the device's issues and the master/policy
/// allow-lists, and re-validated by the agent before execution.</summary>
public sealed record Job(
    string JobId,
    string ClientId,
    string Kind,
    string Subject,
    string? Arg = null,
    string? Path = null,
    string State = "queued",
    string? Detail = null,
    long CreatedUnix = 0,
    long? StartedUnix = null,
    long? FinishedUnix = null,
    int? Progress = null);          // 0–100 for streaming installs; null = indeterminate

public sealed record NewJobRequest(string ClientId, string Kind, string Subject);
public sealed record JobUpdateRequest(string JobId, string ClientId, string State, string? Detail = null, int? Progress = null);
public sealed record AppInstallRequest(string AppId);   // T29: install_app / update_app subject selector

public sealed record DownedRequest(
    string ClientId,
    string DesiredState,
    string? DownedContentId = null,
    string? DownedReason = null);

// ---- MasterManifest.json (server source of truth) ----

public sealed record MasterGame(
    string GameId,
    string LatestVersion,
    string? MinimumVersion = null,
    string? Launcher = null,
    string? AppId = null,
    string? DisplayName = null);   // T42: human name from steamcmd; GameId is the fleet (folder-normalized) id

public sealed record GameBaseline(string GameId, string Version, string? InstallPath = null);

public sealed record ContentRef(string Category, string ContentId);

public sealed record FactoryEntry(
    string EntryId,
    string? SourceClientId,
    string MachineModel,
    DateTimeOffset CapturedTimestamp,
    List<GameBaseline> GameBaselines,
    JsonNode MachineState,
    List<ContentRef>? ContentRefs = null,
    // T36: operator-pulled factory baseline — the station's name + every installed app NAME.
    // Deliberately NO version numbers: keeping apps current is the updates manager's job.
    string? DisplayName = null,
    List<string>? InstalledApps = null);

public sealed record MasterManifest(
    int Version,
    DateTimeOffset UpdatedTimestamp,
    List<MasterGame> MasterList,
    List<FactoryEntry> FactoryStateMap);

// ---- ContentManifest.json ----

public sealed record ContentItem(string ContentId, string Filename, string Sha256, string Category, long SizeBytes = 0);

public sealed record ContentManifest(int Version, DateTimeOffset UpdatedTimestamp, List<ContentItem> Items);

// ---- T35: operator sign-in + accounts ----

public sealed record LoginRequest(string Username, string Password);
public sealed record OperatorAddRequest(string Username, string Password);
public sealed record OperatorPasswordRequest(string Username, string Password);

// ---- T36: compliance dismissals ----

public sealed record IssueActionRequest(string ClientId);
public sealed record FactoryDeployRequest(string? SourceClientId = null);   // T39: null = target's own baseline

// ---- T41: master-list add by Steam AppID (steamcmd resolves name + depot BuildID) ----

public sealed record MasterAddRequest(string AppId);
public sealed record MasterRemoveRequest(string AppId);
public sealed record MasterJobRequest(string AppId, string? ClientId = null);

public static class Wire
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string ToJson<T>(T v) => JsonSerializer.Serialize(v, Options);
    public static T? FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}

/// <summary>Content hash of a Local Manifest ignoring volatile timestamps, so an unchanged
/// machine state costs ~zero bandwidth/disk between reports (PLAN resource budget). Both the
/// agent (skip-send) and the server (skip-store) use this exact same normalization.</summary>
public static class ReportHash
{
    public static string Of(LocalManifest m)
    {
        var node = JsonNode.Parse(Wire.ToJson(m))!.AsObject();
        node.Remove("report_timestamp");
        if (node["machine_state"] is JsonObject ms) ms.Remove("captured_timestamp");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()))).ToLowerInvariant();
    }
}
