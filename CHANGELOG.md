# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Active Directory (LDAP) authentication integration for the dashboard.
- Cross-platform agent scaffolding.

---

## [0.1.1-alpha] - 2026-09-25

Security release. Upgrading requires a `.env` file; see the upgrade notes below.

### Security
- **Backend:** `POST /api/update_agent/{hw_id}`, `POST /api/upload`, `POST /api/remote_input`, `DELETE /api/devices/{pc_name}`, `POST /api/tasks/action` and `POST /api/agent_policies` now require a valid JWT. All of them except `/api/remote_input` also require an admin role.
- **Backend:** `/api/upload` cleans file names with `werkzeug.utils.secure_filename` and only writes inside the fixed storage directory, which prevents path traversal.
- **Agent updates:** Update commands no longer accept a `download_url` from the caller. Only the package uploaded to the server through the Update Center is distributed, and a SHA-256 digest is mandatory. The agent downloads the package from its own configured server and verifies the digest before it stops any service. Integrity is checked with SHA-256 only, without code signing.
- **Dashboard:** The JWT is kept in an `httpOnly`, `SameSite=Strict` cookie instead of `localStorage`, and it no longer appears in URLs (`?_jwt=`, `?token=`). State-changing API calls authenticated by the cookie must send the `X-Requested-With` header (CSRF protection). The PHP session cookie is now `httpOnly` too.
- **Dashboard:** Device names (`pc_name`, hostname, display name), lab names, logs, task output and other API values are HTML-escaped before rendering, which fixes stored XSS.
- **Configuration:** Hardcoded passwords, secrets and IP addresses were removed from the backend, dashboard and agent. This includes the database and initial admin passwords, the server IP, the "wake all devices" confirmation password and the offline quarantine bypass salt. The offline bypass code now uses a configurable secret and stays disabled until one is set.

### Added
- `.env.example`, which documents every backend and dashboard setting.
- `Backend/requirements.txt`.
- Agent settings `BypassSecret` (`POPS_BYPASS_SECRET`) and the `POPS_SERVER_URL` override.

### Changed
- The backend reads `JWT_SECRET`, `DB_HOST`, `DB_PORT`, `DB_USER`, `DB_PASS`, `DB_NAME`, `PANEL_ADMIN_USER`, `PANEL_ADMIN_PASS`, `CORS_ALLOWED_ORIGINS`, `WOL_BROADCAST_ADDR` and `WOL_PORT` from the environment. Startup fails when a required value is missing.
- The dashboard reads `POPS_API_INTERNAL_URL` and `POPS_WOL_CONFIRM_PASSWORD` from the web server environment or the project `.env`. When `POPS_WOL_CONFIRM_PASSWORD` is empty, "wake all devices" asks for confirmation instead of a password.
- `/ws/panel` authenticates with the JWT cookie. The `?token=` query parameter is no longer accepted.
- `/api/upload` returns the stored `filename`.
- The Update Center reports per-device errors, such as an offline device or a missing package or digest.

### Fixed
- Packages uploaded from the Deployment page no longer get a broken download link (`undefined` file name).
- Device names containing special characters no longer break dashboard API calls (URL encoding).
- The *Lint Python Backend* CI job failed on flake8 `F824` in `server.py`.

### Upgrade notes
1. Copy `.env.example` to `.env`. Fill in at least `JWT_SECRET`, `DB_USER`, `DB_PASS` and `DB_NAME`, and also `PANEL_ADMIN_PASS` on a fresh database.
2. Install the backend dependencies with `pip install -r Backend/requirements.txt`. This adds `werkzeug`.
3. The built-in default server address was removed from the agent. Set `ServerUrl` in `C:\POps\appsettings.json` on each endpoint, or define `POPS_SERVER_URL`. To keep using the offline bypass, also set `BypassSecret`.
4. Existing dashboard sessions keep working; the JWT cookie is issued on the next page load. Ask users to sign in again once so that their PHP session cookie is re-issued with the `httpOnly` flag.
5. Agents that are already deployed can still be updated from the Update Center; every update command now carries a SHA-256 digest.

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

[Unreleased]: https://github.com/PashaCore/POps/compare/v0.1.1-alpha...HEAD
[0.1.1-alpha]: https://github.com/PashaCore/POps/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/PashaCore/POps/releases/tag/v0.1.0-alpha
