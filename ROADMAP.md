# POps Roadmap

This document outlines the development roadmap for **POps (Pasha Operations Platform)**. We use this to track progress, set milestones, and provide visibility into what we are working on.

## Phase 1: Foundation (v1.0 - Current)
Our initial release focuses on establishing a rock-solid, scalable baseline for Windows environments.

- [x] **Agent Architecture** (.NET 8, BackgroundService, Watchdog)
- [x] **Real-time Telemetry** (WebSockets, Heartbeats, CPU/RAM tracking)
- [x] **Dashboard Infrastructure** (PHP 8, JWT Auth, FastAPI Backend)
- [x] **POpsVision** (Dynamic 1-5 FPS Remote Desktop)
- [x] **Software Deployment** (Mass orchestration for ZIP/MSI)
- [x] **Remote Terminal** (PowerShell execution via web)
- [x] **Kiosk & Lockdown Mode** (Tamper protection, bypass tokens)
- [x] **Audit Logging** (Immutable event records)

---

## Phase 2: Enterprise Expansion (v2.0)
The next major iteration will focus on scaling out the platform across different environments and integrating with existing enterprise identity systems.

- [ ] **Active Directory (LDAP) Integration** 
  - Allow users to authenticate to the dashboard using enterprise credentials.
- [ ] **Advanced Group Policies (GPO Sync)**
  - Synchronize and enforce local security policies directly from the POps dashboard.
- [ ] **Extensible Plugin SDK**
  - Allow developers to write Python plugins for the backend and C# modules for the agent.
- [ ] **Cross-Platform Agent**
  - Initial support for Linux and macOS endpoints for heterogeneous environments.
- [ ] **Advanced Reporting**
  - Scheduled PDF reports for asset inventory and policy violations.

---

## Phase 3: Automation & AI (v3.0)
Transforming POps from an administrative tool into an intelligent assistant.

- [ ] **Anomaly Detection**
  - Identify unusual usage patterns (e.g., massive file copying, suspicious network activity) in real-time.
- [ ] **Automated Remediation**
  - Configure triggers to run specific deployment scripts if a policy is violated.
- [ ] **Mobile Console**
  - Native iOS and Android applications for on-the-go lab management.

---

## How to Contribute
If you have a feature request or want to contribute to the roadmap, please open a discussion in our [GitHub Discussions](https://github.com/PashaCore/POps/discussions) or submit an Issue.
