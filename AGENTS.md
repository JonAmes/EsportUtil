# Development Agent Ruleset (AGENTS.md)

The following rules define the operational behavior and execution methodology for all components within the EsportsUtil project (Client Agents, Server API, Daemon).

## 🎯 Core Process Rules
*   **Planning First:** Any significant feature or change must be documented and approved in the `PLAN.md` file before implementation begins.
*   **Phased Execution:** Work is strictly iterative. Only the scope defined in the current active phase of `PLAN.md` is permitted.
*   **Continuous Verification:** Testing is mandatory. Unit and system tests must be written and pass for every newly developed feature or phase iteration.

## ♻️ State Management & Iteration
*   **Master State:** The Server's Master Manifest is the single source of truth for desired game versions.
*   **Reporting:** Clients operate by reporting their current state; they do not independently push commands.
*   **Iteration:** The project moves forward by completing phases, verifying all associated tests, and updating `PLAN.md` to initiate the next phase.
