# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Active Directory (LDAP) authentication integration for the dashboard.
- Cross-platform agent scaffolding.

---

## [0.1.0-alpha] - 2026-08-04

### Added
- **Core Architecture:** Dual WebSocket & Named Pipe communication system.
- **Agent:** .NET 8 `POpsAgent`, `POpsTray`, `POpsVision`, `POpsWatchdog`, and `POpsUpdater`.
- **Backend:** Python FastAPI backend with PostgreSQL and Uvicorn.
- **Dashboard:** Modern PHP 8 frontend with JWT-based role access control.
- **POpsVision:** 1-5 FPS dynamic remote screen viewing and I/O control.
- **Deployment Engine:** Mass ZIP/MSI orchestration with progress tracking.
- **Policy Engine:** Network isolation and Kiosk lockdown capabilities.
- **Audit Logging:** Immutable `agent_logs_v2` tracking all management actions.
