# EsportsUtil Master Plan

## 📜 Development Philosophy
This project will follow a rigorous, phased development cycle, ensuring continuous testing and verifiable milestones.

**Rules of Engagement:**
1.  **Phased Approach:** Development will be structured into distinct, sequential phases. Only the current active phase will be worked on.
2.  **Mandatory Planning:** All major changes must begin by updating the `PLAN.md` file, clearly defining the scope of the next phase.
3.  **Iterative Testing:** After implementation of a phase's features, corresponding unit and system tests must be created and passed before the phase is considered complete.

## 🖥️ Deployment Context
*   **Server:** Runs on Windows, physically at the venue, with the label printer attached locally to the server machine. All server-side OS integrations (label printing via the Windows print spooler, local notifications for the daemon) use Windows facilities.
*   **Clients:** Client Agents run on the venue's game PCs as **native Windows binaries (C# / .NET 8 self-contained single-file EXE)** — no PowerShell scripts are deployed on game PCs (avoids `ExecutionPolicy Bypass` requirements and shell-policy friction). The agent runs as a Windows service or Scheduled Task, reporting state to the Server over the API.

## 🚀 Phases (Current Phase: Local Manifest Reporting & Inventory)
The project will proceed through the following phases:

### Phase 1: Local Manifest Reporting & Inventory (CURRENT)
*   **Goal:** Establish the definitive data contracts for client and server integration. The Client Agent will report a detailed Local Manifest of its installed software and configuration to the Server API. The Server will store two primary structures: a Master Version List and a Factory State Map.
*   **Milestones:**
    1.  **Game Identification & Data Contract:** Client Agent scans predefined installation directories for subdirectories to deduce the `game_id`. The Local Manifest JSON schema must define the structure, including the folder name-to-ID mapping. Full setup details (version, patch level, hardware) are captured based on this discovery.
    2.  **Server Master Structure:** Designing and implementing the dual structure of the `MasterManifest.json`:
        *   **Master List:** The globally accepted latest stable versions for all installed games.
        *   **Factory State Map:** Defines a known, reliable baseline state (version, config files, patch level) to which any out-of-sync client *must* roll back.
    *   **Device Onboarding (Pin System):** The Server API must include a registration endpoint where clients provide a unique hardware identifier (`DevicePin`) upon initial contact. This creates a permanent linkage between the reported `client_id` and the physical hardware, serving as an onboarding gate.
    *   **Client Naming:** Clients onboard anonymously: the registration payload carries only the `DevicePin` and derived `client_id`, no human-readable name. The Server holds newly registered devices in a *pending* state, and the dashboard surfaces them so an operator can assign a human-readable `display_name` (e.g. machine or station label, like "Lab-PC-3") via a rename endpoint. Until named, a device is displayed by its short pin. The Server stores the name alongside the `DevicePin` and `client_id` in the device registry; it is the primary label shown in the dashboard and used in all alerts and reports. The `DevicePin` remains the immutable machine identity; the name is metadata and may be edited later without breaking the hardware linkage.
    *   **Label Printing API:** The Server API exposes a print-label endpoint that takes a registered `client_id` and sends a physical sticker label to a configured printer, so a named machine can be physically identified at the venue. The label renders the `display_name` plus a barcode/QR encoding the `DevicePin`. Printing is decoupled from registration: the dashboard offers a "print label" action per device (including reprints). The Server runs on Windows and the label printer is physically attached to the server machine, so the default provider is the native Windows print spooler (Win32 print API / PowerShell Start-Print) targeting a named local printer queue. For demo purposes, Windows' built-in "Microsoft Print to PDF" queue stands in as the demo printer, exercising the full pipeline and producing a print-ready PDF without hardware; on the Linux dev box the same provider interface falls back to writing rendered labels to an output directory.
4.  **Health Check & Liveness:** The Client Agent must implement a periodic Heartbeat status transmission to the Server API. The Server must monitor this heartbeat and trigger a high-priority alert (e.g., via the internal notification service) if a registered device fails to check in within a defined timeout period.
*   **State Provisioning Mechanism:** Defining the Server API endpoint for Clients to *pull* the current Factory State Map, ensuring all clients are synchronized to the latest baseline state upon connection or request. Pulling is only half the contract — the client must also be able to *apply* the pulled baseline locally (see Machine State Module below), and the post-Apply Local Manifest is the proof-of-restoration the Server records.

5.  **Client Machine State Module (ports `MachineState.ps1` semantics into the C# agent — no PS deployed):** The proven 936-line engine's behavior — not its code — is ported natively into the client EXE: Record/Apply/List/Init semantics, per-display resolution (P/Invoke `ChangeDisplaySettingsEx`), startup items (HKCU/HKLM `Run`/`RunOnce`, Startup folders, `StartupApproved` state via registry), screensaver/wallpaper, known-app inventory, and winget-driven Init installs are all first-class .NET operations (`Microsoft.Win32.Registry`, `System.Management` WMI, P/Invoke, `Process.Start`). Its JSON state format is preserved **verbatim as the wire contract** so captured records and the Factory State Map stay compatible with everything already designed.
    *   **No-duplication rule:** the state module exists in exactly one place inside the EXE; the agent's transport loop calls into it, never re-implements capture. See `docs/client-architecture.md` (feature ownership matrix) and both schemas, where `machine_state` is an opaque verbatim-shaped object; the agent's other net-new logic is folder-scan game discovery, HTTP transport, and the job runner.
    *   **Manifest superset:** The Local Manifest's machine-state section carries everything the ported module captures — machine identity (`computerName`, `machineManufacturer`/`machineModel` from `Win32_ComputerSystem` via WMI), known-app inventory (installed/version/location/starts-at-boot, catalog-driven regex detection), startup items (HKCU/HKLM `Run`/`RunOnce`, Startup folders, `StartupApproved` enable/disable state), per-display resolution, screensaver and wallpaper settings — plus Phase 1's `game_id` discovery from scanned install directories.
    *   **Factory State capture & matching:** A Factory State Map entry is produced by running **Record** on a golden PC of that model. Selection logic on the client mirrors the proven order: exact registered-client match → `machineModel` match (newest capture wins on ties) → operator override. This makes "which baseline applies to this PC" a data decision, not agent intelligence.
    *   **Apply semantics:** **Apply** restores resolution, startup set + enabled/disabled state, screensaver, wallpaper, and runs updates (server-suppressed via a `SkipUpdates`-equivalent flag). Missing known apps are *reported into the manifest*, never silently installed — remediation stays a Phase 4 (patching) server command.
    *   **Init provisioning:** **Init** semantics become the first-run flow after `DevicePin` onboarding: operator- or server-selected software list installs via winget (explicit agreement flags; vendor URL fallback when winget is absent), each install verified, then the baseline is **Record**ed and pushed as the machine's first manifest.
    *   **Guardrails (hard, inherited from the script's design):** never touch machine naming, Windows Update *settings*, or drivers; only Init installs third-party software; Apply is idempotent against repeated runs.
    *   **Engineering note:** the PS engine's recorded JSON (field names and shapes from its 936-line proven implementation) is the port's test oracle: a C# Record on the same PC class must produce a schema-valid, field-for-field comparable record. Reference implementation lives in docs/ and prior session history as a behavior spec only; it is never deployed to game PCs.

### Phase 2: Master Manifest & Comparison Logic
*   **Goal:** Implement the Server's "Source of Truth" functionality. This phase focuses on refinement and scalability of the comparison engine using the Factory State Map as a priority for remediation.
*   **Milestones:** Server compares Local Manifest against Master Manifest, identifies outdated games, and generates status reports (Update Required/Rollback Required).

### Phase 3: Asynchronous Alerting & Notification
*   **Goal:** Decouple alerting from the main ingestion flow using a Message Broker.
*   **Milestones:** Client report triggers a queue message $\rightarrow$ Server Notification Daemon consumes the message and successfully triggers a local OS notification.

### Phase 4: Implementation of Patching Mechanism
*   **Goal:** Integrate with external update mechanisms (Steam/Epic CLI wrappers).
*   **Milestones:** Server commands are delivered to the client $\rightarrow$ Client executes the patch command $\rightarrow$ Client reports successful version increment.
*   **Machine State Module interaction:** the remediation surface (install a missing known app, rollback to Factory State) executes through the C# agent's Init/Apply state-module primitives — Phase 4 gives the Server the remote trigger for what Init/Apply already do locally. First patch handler: a SteamCMD job type (`app_update` against the game's `app_id`, allow-listed game/app ids only, agent builds the command line itself).

## ⏳ Current Status
Status: Planning Complete. Readiness check for: Device Onboarding (PIN System) and Heartbeat Implementation.
