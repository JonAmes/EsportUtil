# EsportsUtil Project

This repository holds the core services for the Game Update Monitoring infrastructure, including the Client Agents and the central Server API.

## 🗺️ Architecture Goals
The system is designed as a decoupled service architecture:
1.  **Client Agents:** Report current game version data to the Server.
2.  **Server API:** Ingests data, compares it against the Master Manifest, and generates Alert Payloads.
3.  **Message Broker:** Queues high-priority alerts asynchronously.
4.  **Notification Daemon:** Runs locally on the server to consume alerts and trigger native OS notifications.

## 🛠️ Getting Started
... [TODO: Add dependencies and setup instructions]