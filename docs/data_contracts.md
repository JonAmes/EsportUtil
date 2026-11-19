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

## 🔐 7. Operator Sign-in & LAN Autodetection (§auth — T35)

**Operator accounts** live in `<dataDir>/operators.json` — a JSON array of
`{username, password_hash}`. `username` is stored lowercased; `password_hash` is
the self-describing PBKDF2 recipe `pbkdf2-sha256$<iters>$<b64-salt>$<b64-hash>`
(SHA-256, 100 000 iterations, per-account 16-byte salt, constant-time verify).
Plaintext is never persisted. Config `bootstrap_operator` `{username,password}`
is imported into that store **once**, only while it is empty — after that the
store is the source of truth and the config value is ignored.

**Auth model** — the gate engages only when the store is NON-empty; an empty
store keeps today's fully-open LAN dashboard. When armed:
- every `/api/*` call without a valid session returns `401 {ok:false,error:"sign-in required"}`;
- every page (except `/login`) returns `302 → /login`;
- agent traffic is NEVER gated: `register`, `heartbeat`, `report`, `job_update`
  and `factory-state` stay open (fleet auth is client_id + pairing, not a web session).

| Endpoint | Shape |
|---|---|
| `POST /api/login {username,password}` | `200 {ok:true,username}` + `Set-Cookie: esu_session` (HttpOnly, SameSite=Lax, 12 h sliding); `401` wrong creds; `429` throttled |
| `POST /api/logout` | revokes session, deletes cookie |
| `GET /api/auth-status` | `{auth_required, signed_in, username}` — always open, never 401 |
| `GET /api/operators` | `{ok, operators:[username…]}` |
| `POST /api/operators {username,password}` | `400` bad username (2–32 chars `[a-z0-9._-]`, must start alphanumeric) / short password (<6) / duplicate |
| `POST /api/operators/password {username,password}` | change password, revokes that user's sessions |
| `DELETE /api/operators/{username}` | removing the LAST operator disarms the gate again |

Failed logins are throttled per `(ip, username)`: 5 strikes → 60 s cool-off (`429`).
Sessions are random 32-byte tokens in the `esu_session` cookie (12 h sliding TTL,
revoked in-memory on logout/password-change/operator-removal).

**LAN autodetection** — UDP port `discovery_port` (default **4301**, `0` disables;
bind collision = warn, never fatal). Agent wizard broadcasts the ASCII probe
`ESU-DISCOVER` to `255.255.255.255` plus every NIC's directed broadcast; every
server answers one JSON datagram:
`{service:"esportsutil", server_url:"http://<reachable>:4300", version:"x.y.z"}`.
A wildcard-bound server advertises the address of the very interface the probe
arrived on, so multi-NIC boxes answer each requester with a directly reachable URL.

## 🏭 8. Factory Baselines & Compliance Dismissals (§factory — T36)

**Factory baseline pull** — the dashboard's per-station `factory` button calls
`POST /api/factory-state/{clientId}` (operator-side; agent does nothing new).
The server snapshots the station's registry NAME plus the installed application
**names** from its last full report (`<data>/reports/<clientId>.json`, field
`machine_state.installedApps[].display_name`) into the master manifest's
`factory_state_map` (one entry per `client_id`; re-pull REPLACES). `404` if the
device is unknown or has never sent a full report.
**Version numbers are deliberately NOT part of a factory baseline** — the
updates manager owns currency; a baseline is a *what was on the box when it was
born* manifest (`display_name`, `installed_apps: [string…]`).

**Compliance dismissals** — per-station `clear` calls `POST /api/issues/clear
{client_id}` and silences *all current findings* for that station; dismissals
persist in `<data>/issue-dismissals.json` keyed `(client_id, kind, subject)`
and stay armed across restarts until `POST /api/issues/restore {client_id}`
lifts them (findings return iff still true). A changed/renamed finding is a new
key and speaks again on its own. Dismissed findings are excluded from the
`/api/state` device `issues` array (the row shows `N cleared ↺` instead) and
never (re-)fire the `compliance` alert. The dashboard header shows ONE orange
pill `⚠ N issues` (replaces the old out-of-sync stat tile); clicking it jumps
to the Device Fleet section where each affected row offers `clear`.

## 🪪 9. Agent Identity & Update Durability (§identity — T37)

`<data_dir>/esportsutil.identity.json` holds `{client_id, device_pin, rotate_salt}`.
- **client_id** (T37): when the file is missing (data dir moved with the EXE, fresh install) the
  agent re-mints a **deterministic** client_id = SHA-256(`"esportsutil-client|" + machineName +
  "|" + hardware-seed`) shaped as a name-based UUID — the SAME id on the same machine. Updating
  the agent EXE or moving its folder therefore never surfaces a new device; the server simply
  recognizes its own client again. A collision would require the same PC name AND model (an
  image-clone bug). A persisted file always wins, so pre-T37 random ids are untouched.
- **device_pin**: unchanged rule — minted once from the machine seed and then persisted.
- **rotate_salt**: operator offboarding (`403 offboarded`) rotates the identity; each Rotate
  increments the salt so a deterministic re-mint can never be permanently pinned by the tombstone.
- **data_dir resolution** (T37): a RELATIVE `data_dir` in `esportsutil.config.json` binds to the
  config FILE's directory, never the launch CWD; the self-update handoff relaunches with
  WorkingDirectory = the EXE's folder. Absolute `data_dir` (wizard default) is untouched.

## 🚀 10. Factory Deploy — pushing a baseline ONTO a machine (§deploy — T39)

`GET /api/factory-deploy-preview/{clientId}?source=<entry_id>` and
`POST /api/factory-deploy/{clientId}` with body `{ "source_client_id": "<entry_id>" | null }`.

The server diffs the baseline's `installed_apps` (T36: **names only**, no versions) against the
target's **last full report** `machine_state.installedApps` and expands it into jobs the agent
already executes — no new agent verbs:

- **Missing + in the venue AppCatalog** → `install_app` job; the installer is the vendor's
  current download (the updates manager owns version currency; baselines never pin versions).
- **Missing + NOT in the catalog** → returned as `not_auto_installable`; nothing is guessed and
  the compliance flag keeps speaking on the row.
- **Present but not in the baseline** → `remove_software` job — a **true factory reset** (operator
  decision, T39). Guardrails: entries matching a policy `Protected` rule and anything named
  *esportsutil* (the agent itself) are never removed.
- Open jobs are **deduped** — re-deploying a running reconcile queues nothing new.
- `source_client_id` omitted = the target's **own** baseline (self-reconcile); a named source =
  template copy from any station that has a saved baseline.
- 404s: unknown device, no baseline for the source, or a target that has not sent a first report.

Dashboard: a per-row `deploy` button (visible once any baseline exists) opens a modal showing the
exact install/uninstall diff before anything runs; destructive removals additionally require an
explicit confirm listing every app that will be uninstalled.

## 🎮 11. Master List by Steam AppID (§master-add — T41)

`POST /api/master-add {app_id}` — the box under Device Fleet. The server runs
`steamcmd +login anonymous +app_info_print <appid> +quit` (located via `steamcmd_path`
config, `STEAMCMD` env, `~/steamcmd`, `PATH`) and parses the VDF server-side for the
game name and the main depot's `buildid` (depot key == AppID). The user's
`awk '/BuildID/{print$8}'` slot is unreliable — the raw VDF carries lowercase quoted
`"buildid"` once per depot — so parsing happens in `SteamInfo.Parse`.
Uniqueness is enforced BEFORE anything runs: a known AppID (or a different AppID whose
resolved name is already listed, compared case-insensitively) returns 409. Non-numeric
ids return 400 (the id never reaches a shell — a `^\d{1,10}$` gate guards the process
seam too). Unresolvable AppID → 502 with the reason; steamcmd absent → 503.
On success the entry is recorded as a `MasterGame` with `app_id` + `game_id` = resolved
name + `latest_version` = depot BuildID + `launcher: "steam"`, and the manifest version
bumps — so agents see the recorded game immediately (the updates manager owns actual
game versions from here on). The dashboard box re-renders the recorded list each poll;
the manifest modal shows the AppID per game. Test seam: `ESU_FAKE_STEAMCMD` (a shell
command substituted for steamcmd — set only by tests).
