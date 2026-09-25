# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Active Directory (LDAP) authentication integration for the dashboard.
- Cross-platform agent scaffolding.

### Removed
- **Dashboard:** Dark mode. The panel now uses a single light theme; the theme toggle buttons and the saved `pops_theme` preference are gone, and native form controls stay light even when the operating system prefers dark.

---

## [0.1.1-alpha] - 2026-09-25

Security release. The backend now needs a `.env` file; run `python3 Backend/setup_env.py` on the server to create it (see the upgrade notes below).

### Security
- **Backend:** `POST /api/update_agent/{hw_id}`, `POST /api/upload`, `POST /api/remote_input`, `DELETE /api/devices/{pc_name}`, `POST /api/tasks/action`, `POST /api/agent_policies` and `DELETE /api/updates/{filename}` now require a valid JWT. All of them except `/api/remote_input` also require an admin role.
- **Backend:** `/api/upload` cleans file names with `werkzeug.utils.secure_filename` and only writes inside the fixed storage directory, which prevents path traversal. Deleting update packages is limited to `.zip` files inside the updates directory.
- **Agent updates:** Update commands no longer accept a `download_url` from the caller. Only the package uploaded to the server through the Update Center is distributed, and a SHA-256 digest is mandatory. The agent downloads the package from its own configured server and verifies the digest before it stops any service. Integrity is checked with SHA-256 only, without code signing.
- **Dashboard:** The JWT is kept in an `httpOnly`, `SameSite=Strict` cookie instead of `localStorage`, and it no longer appears in URLs (`?_jwt=`, `?token=`). State-changing API calls authenticated by the cookie must send the `X-Requested-With` header (CSRF protection). The PHP session cookie is now `httpOnly` too.
- **Dashboard:** Device names (`pc_name`, hostname, display name), lab names, logs, task output and other API values are HTML-escaped before rendering, which fixes stored XSS.
- **Configuration:** Hardcoded passwords, secrets and IP addresses were removed from the backend, dashboard and agent. This covers the database and initial admin passwords, the server IP, the "wake all devices" confirmation password and the offline bypass salt. Because these values remain readable in the git history, `setup_env.py` replaces the database and admin passwords with new random ones.

### Added
- `Backend/setup_env.py`: installs the dependencies, creates `.env` with random secrets, sets new database and panel admin passwords, and prints the command that distributes `BypassSecret` to the agents.
- `.env.example`, which documents every backend and dashboard setting.
- `Backend/requirements.txt`.
- Offline bypass codes: the device list shows a key button (admin roles) that returns the day's 6-character code for a quarantined device. The code is derived from `BYPASS_SECRET`, which must match the agents' `BypassSecret`. Every generated code is written to the audit log.
- Agent settings `BypassSecret` (`POPS_BYPASS_SECRET`) and the `POPS_SERVER_URL` override.

### Changed
- The backend reads `JWT_SECRET`, `DB_HOST`, `DB_PORT`, `DB_USER`, `DB_PASS`, `DB_NAME`, `PANEL_ADMIN_USER`, `PANEL_ADMIN_PASS`, `BYPASS_SECRET`, `CORS_ALLOWED_ORIGINS`, `WOL_BROADCAST_ADDR` and `WOL_PORT` from the environment. Startup fails when a required value is missing.
- The dashboard reads `POPS_API_INTERNAL_URL` and `POPS_WOL_CONFIRM_PASSWORD` from the web server environment or the project `.env`. When `POPS_WOL_CONFIRM_PASSWORD` is empty, "wake all devices" asks for confirmation instead of a password.
- `/api/security/bypass_token/{pc_name}` returns the code the agent verifies offline instead of an unused random token, and requires an admin role.
- `GET /api/agent_policies` no longer requires a JWT, because agents poll it without one. Changing policies still requires an admin.
- `/ws/panel` authenticates with the JWT cookie. The `?token=` query parameter is no longer accepted.
- `/api/upload` returns the stored `filename`.
- The Update Center reports per-device errors, such as an offline device or a missing package or digest.

### Fixed
- `DELETE /api/devices/{pc_name}` always answered with an error (it used a non-existent connection manager attribute) and never disconnected online devices.
- Agents could not load their policies, because every poll of `/api/agent_policies` was rejected with 401.
- `POpsUpdater` overwrote the endpoint's `appsettings.json` with the one in the update package. It now keeps the existing file, so an update cannot reset `ServerUrl` or `BypassSecret`.
- Packages uploaded from the Deployment page no longer get a broken download link (`undefined` file name).
- Device names containing special characters no longer break dashboard API calls (URL encoding).
- The *Lint Python Backend* CI job failed on flake8 `F824` in `server.py`.

### Upgrade notes
1. On the server, run `python3 Backend/setup_env.py` from the project root. It asks for the current database password when it is not in the environment, and it prints the new panel admin password once, so store it right away.
2. Restart the backend. Remove `DB_PASS`, `JWT_SECRET` and similar variables from the service definition (for example systemd `Environment=`), because they take precedence over `.env`.
3. If the dashboard is not served from the same checkout, set `POPS_API_INTERNAL_URL` and `POPS_WOL_CONFIRM_PASSWORD` in the web server environment.
4. Build and publish the new agent package through the Update Center as usual. Agents keep their existing `ServerUrl`. New installations need `ServerUrl` in `C:\POps\appsettings.json` or `POPS_SERVER_URL`, because the repository no longer contains a default server address.
5. To enable offline bypass, run the command printed by `setup_env.py` on all agents as a script module on the *Dosya Dağıtımı* (Deployment) page. Agents that are offline receive it when they come back online.
6. Existing dashboard sessions keep working; the JWT cookie is issued on the next page load. Ask users to sign in again once so that their PHP session cookie is re-issued with the `httpOnly` flag.

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
