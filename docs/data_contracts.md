# EsportsUtil — Data Contracts (Phase 1)

**Authoritative contracts are the JSON Schemas in `schemas/`.** This document is a
human-readable orientation to them; if it ever disagrees with a schema, the schema
wins and this file gets fixed. Field names below are schema field names — do not
introduce variants (`detected_version`, `installation_path`, `is_current`, and hex
`device_pin` forms that appeared in an early Python spike are NOT contract).

All machine-state data is captured by the C# agent's **State Module** (ported
MachineState.ps1 semantics — no PowerShell is deployed on game PCs) and passed
verbatim as an opaque object, per `docs/app-architecture.md`.

## 📦 1. Local Manifest (Client → Server)

Schema: `schemas/local_manifest.schema.json`. Sent on onboarding, periodic report,
and as proof-of-restoration after an Apply.

```json
{
  "client_id": "assigned_at_onboarding",
  "device_pin": "3316",
  "display_name": "LAB-PC-05",
  "report_timestamp": "2026-11-16T18:00:00Z",
  "agent_version": "0.1.0",
  "machine_state": {
    "//": "Opaque verbatim State Module Record: computerName, machineManufacturer, machineModel, per-display resolution, startup items + StartupApproved state, screensaver, wallpaper, known-app inventory, full installed-software inventory (Uninstall-key entries: name, publisher, version, install location, uninstall strings)."
  },
  "game_reports": [
    {
      "game_id": "counter_strike_2",
      "folder_name_used": "Counter-Strike Global Offensive",
      "install_path": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Counter-Strike Global Offensive",
      "current_version": "1.39.7.0",
      "launcher": "steam",
      "status": "OK"
    }
  ],
  "duplicates": []
}
```

**Notes:**
* `client_id` is assigned server-side at PIN onboarding; the client never invents it.
* `device_pin` is the immutable 4-digit hardware identity minted at first contact (matches the dashboard pairing flow); `display_name` is operator-assigned metadata.
* Duplicate installs are reported, never resolved client-side.

## 📜 2. Master Manifest (Server-side source of truth)

Schema: `schemas/master_manifest.schema.json`. Holds both halves:

* **`master_list`** — global latest/minimum versions per `game_id`, plus the
  launcher-native `app_id` used by Phase 4 patch jobs.
* **`factory_state_map`** — one entry per golden capture:

```json
{
  "entry_id": "fs-aurora-r12-2026-11",
  "source_client_id": "c-0142",
  "machine_model": "Alienware Aurora R12",
  "captured_timestamp": "2026-11-10T14:00:00Z",
  "game_baselines": [
    { "game_id": "counter_strike_2", "version": "1.39.7.0", "install_path": "C:\\..." }
  ],
  "machine_state": { "//": "verbatim State Module Record from the golden PC" }
}
```

**Notes:**
* Client baseline selection uses the engine-proven order: exact `client_id` match →
  `machine_model` match (newest `captured_timestamp` wins ties) → operator override.
* `machine_state` is fed straight into the agent's Apply primitive; no field
  translation exists anywhere in the pipeline.

## 🛡️ 3. Approved Software Policy (Server-side, Phase 2+)

Schema: `schemas/approved_software.schema.json`. An operator-maintained allow-list:
any installed app (from the full inventory inside `machine_state`) not matched by an
`allow` rule is **unapproved**. `default_action` sets venue behavior —
`report_only` (Phase 2), `flag_for_removal` (operator-approved `remove_software`
jobs), or `auto_remove` (unattended, explicit opt-in). The `protected` list vetoes
removal unconditionally (drivers, Windows components, security products, the agent
itself). It is a separate file/contract; the Master Manifest stays two-part.

## 🗂️ 4. Content Library & Manifest (Server → agents)

Schema: `schemas/content_manifest.schema.json`. Operator-managed files addressed by
stable `content_id`, served at `GET /content/{content_id}`, verified by `sha256`, and
cached per-agent under `ProgramData\EsportsUtil\content\` (cache key = hash; match =
no re-download). Factory State entries reference needed files via optional
`content_refs` (category + `content_id`) so Apply restores — wallpaper especially —
work on machines that never had the file. `content_id` is the only handle used in
factories, jobs, and dashboards; URLs and local paths never cross the wire.

## 🖥️ 5. Device Registry (Server-side per-machine control)

Schema: `schemas/device_registry.schema.json`. One entry per registered client:
immutable identity (`device_pin`, `client_id`), operator metadata (`display_name`,
`notes`), and **`desired_state: active | downed`** returned on every heartbeat ack —
the agent converges to it, so the downed wallpaper is re-applied after reboots and
logons. Downed is visual status only (wallpaper swap), never a session lock.
`downed_content_id` selects the Content Library out-of-service image;
`downed_reason` is the operator note — it also rides the heartbeat **ack**
(`downed_reason`) so the agent can raise the sticky downed overlay
(`--downed-overlay`: no expiry, staff bypass, killed on flip to active).
Liveness is derived (last heartbeat vs
timeout), never stored as a flag. Transient one-shot directives (e.g. `locate`
with expiry) ride the heartbeat ack and are **not** persisted in the registry.

## 📟 6. Client Status (§client-status — agent-local, read-only)

The agent serves a read-only client dashboard on **127.0.0.1 only**
(`agent_dashboard_port`, default 4310; `agent_dashboard: false` disables it).
`GET /api/local_status` returns a `LocalStatus` projection of the agent loop:
identity (`client_id`, `device_pin`), link health (`registered`, `last_ack_at`,
`last_ack_ok`, `last_ack_error`), the operator's `display_name` (streamed down on
heartbeat acks — `null` while pairing is pending), convergence state
(`desired_state`, `downed_reason`, `last_report_at`, `reports_sent`, `report_hash`), the current
manifest (`machine_model`, `games`, `duplicates`) and the last 20 executed
server-issued jobs (`recent_jobs`). It is a view, not a control plane: no
endpoint here mutates fleet state, and closing the page never pauses
registration, heartbeat, reporting, jobs or repairs.
