# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security
- **Agent:** Agent updates must now be signed. The `update_agent` command has to carry the release `manifest.json` and its ed25519 signature. The agent verifies the signature against the public key compiled into it, refuses a version that is not newer than its own, and downloads the MSI named in the signed manifest only from its own server. It applies the MSI only when the size and SHA-256 match the manifest. Unsigned `.zip` updates (the old `download_url` + `hash` command) are rejected, and the `apply_update.bat` / `C:\.pops_tmp` path is gone. **The server has to send the signed manifest before agents on this version can be updated remotely.**
- **Agent:** Every logged-on user could read `BypassSecret`. Only `C:\POps\appsettings.json` used to be locked down, so the copy in the install folder (for example `C:\Program Files (x86)\POps`) kept the `Users: Read` permission it inherits from `Program Files`, and the `POPS_BYPASS_SECRET` system environment variable is readable by every user. Secrets (`BypassSecret`, `EnrollToken`) now live only in `C:\POpsData\secure`, which only SYSTEM and Administrators can open. On start the agent moves any value it finds in `appsettings.json` or in those environment variables into it and removes the value from its source. The service still re-applies the protected SYSTEM/Administrators ACL to both `appsettings.json` files on every start; it now also reads the result back and logs an error when another account can still open the file.

### Added
- **Dashboard/Backend:** The **Sistem & Sürüm** page now drives the whole agent lifecycle: generate / list / revoke enrollment tokens (single- or multi-use, per lab), dispatch the staged signed update to all agents / a lab / specific devices (`POST /api/system/deploy-update`), and turn `enforce_agent_auth` on/off (`POST /api/system/enforce-auth`, superadmin) behind a confirmation warning. `GET /api/system/version` now also reports the enforce state.
- **Backend:** Agent authentication now also covers the Vision WebSocket (`/ws/vision`) and the agent HTTP endpoints (`/api/auth/login|failed|logout`, `/api/inventory/{pc}`, `/api/logs/{pc}`, `/api/policy_alert`) in the same accept-both mode. When `enforce_agent_auth` is on, an agent must present `X-Agent-Secret` on the Vision WebSocket and `X-Agent-Id` + `X-Agent-Secret` on the HTTP calls, otherwise it is rejected (WS close 4401 / HTTP 401) and the Vision rejection is audited. `GET /api/agent_policies` stays open (read-only fair-use/DNS policy, no sensitive data, and the deploy health check depends on it). Agents send these headers once the fleet is updated; until `enforce_agent_auth` is enabled nothing changes for existing agents.
- **Backend:** Server side of the signed agent update. `POST /api/system/deploy-update` (superadmin) sends the staged, verified release to the chosen agents (all / a lab / specific PCs) as `{"action":"update_agent","manifest":<base64 of manifest.json>,"manifest_sig":…}` and serves the MSI at `/updates/<name>` for the agent to download and verify itself; the agent's `update_result` report is recorded to `device_audit_logs` and pushed to panels. Enrollment tokens can now be multi-use (migration `0003` adds `max_uses`/`use_count`), so one MSI and token can enroll a whole lab instead of only the first machine.
- **Agent:** Updates are installed and checked by `POpsUpdater`. It runs outside the install folder, holds `C:\POpsData\update.lock`, backs up the install folder and runs `msiexec /i … /qn /norestart`. It then waits up to 90 seconds for the new version to write `C:\POpsData\health.json`, which the service now writes on every start. If the new version does not come up healthy, the updater reinstalls the previous MSI (each install keeps its package as `C:\POpsData\packages\installed.msi`) or restores the file backup. The outcome goes to `C:\POpsData\update-result.json`. While the lock is fresh (under 15 minutes), the watchdog restarts neither the service nor the tray.
- **Agent/Release:** Windows MSI installer, `POps-Agent-<version>-win-x64.msi` (WiX 5). The release workflow builds it and lists it in the signed manifest. It installs to `C:\Program Files\POps`, registers the `POpsAgent` service (LocalSystem, automatic start; Windows now restarts it on failure) and starts the tray for every user from `HKLM\...\Run`. The tray no longer writes its own `HKCU` Run entry and removes the one older versions left. `SERVER_URL`, `ENROLL_TOKEN`, `BYPASS_SECRET` and `PERSIST_DIR` are written by a custom action (secrets into `C:\POpsData\secure`, hidden from the MSI log), so an upgrade without properties keeps the existing settings. An upgrade runs in one transaction and rolls back to the installed version if it fails. Downgrades are refused, and the fourth version field is the CI run number. On a machine with a pre-MSI install, the MSI takes over the `POpsAgent` service and migrates the settings, reading `C:\POps` first because that is what released builds read. It then removes the old `C:\Program Files (x86)\POps` and `C:\POps` folders and leftovers such as `PashaCoreAgent.*` and `apply_update.bat`. The installer checks for the .NET 8 Desktop Runtime (x64) first. See `Installer/README.md`.
- **Agent:** The agent now authenticates on `/ws/agent`. It sends the one-time enrollment token from the installer as `X-Enroll-Token`, stores the per-device secret that the server returns in `set_secret`, and presents it as `X-Agent-Secret` on every later connection. When it has both, it sends both, so a device that lost its secret can re-enroll with a new token. The secret lives in `C:\POpsData\secure`, a folder only SYSTEM and Administrators can open. For machines behind freeze software, the optional `PersistDir` setting mirrors the secret into a folder that is not rolled back, and the newest copy wins on start. When the server rejects the agent (close code 4401), it retries every 60 seconds instead of every 5, since each rejected attempt writes an audit record. See `Agent/README.md`.
- **Backend:** Agents can authenticate on `/ws/agent`. Migration `0002` adds `enroll_tokens` (one-time, lab-bound, expiring) and `agent_secrets` (per-device SHA-256 hash, never plaintext); superadmin endpoints create/list/revoke enrollment tokens (`POST` / `GET` / `DELETE /api/system/enroll-token[s]`). On first connect a valid enrollment token is consumed and the server issues a per-device secret keyed to the reconciled DNA identity (moved when the identity is renamed, purged when the device is deleted); later connects present that secret. Authentication runs in **accept-both** mode by default (`global_settings.enforce_agent_auth` off) so existing secret-less agents keep working; once enabled, unauthenticated connects are rejected with WS code 4401 and audited to `device_audit_logs` (which agents cannot write). The Vision WebSocket and the agent HTTP endpoints (`/api/logs`, `/api/inventory`, `/api/policy_alert`, `/api/agent_policies`) plus the agent-side client come next.
- **Backend/Dashboard:** New superadmin **Sistem & Sürüm** panel page (`Dashboard/system.php`) that shows the running version, the latest GitHub release and any offline-staged release, plus `POST /api/system/upload-release`: a superadmin uploads a signed release (`manifest.json` + `manifest.json.sig` + packages) and the server verifies the ed25519 signature (against the public key in `keys/`) and each file's SHA-256 before staging it, with a released-at downgrade guard. This is the offline install path — applying a staged release comes in a later phase. `Backend/release_verify.py` holds the reusable verification, `cryptography` is now a pinned backend dependency, and the deploy script syncs `release_verify.py` and the public key.
- **Backend:** New `GET /api/health` (unauthenticated: database reachability plus the running version, no sensitive data) and `GET /api/system/version` (admin: running version plus the latest GitHub release when reachable). The GitHub check is offline-safe — short timeout, off the event loop, cached hourly and never fatal — so an offline server still answers. The server reads its version from a `VERSION` file synced next to the app, with a CHANGELOG fallback. These live in a separate `Backend/system_routes.py` router to keep `server.py` from growing, and the deploy script now syncs `system_routes.py` and `VERSION`.
- **Release/CI:** Release packages are now signed. The release workflow builds a `manifest.json` (the SHA-256 of every package plus the version, git tag and a timestamp) and signs it with an ed25519 key held only in the `POPS_RELEASE_PRIVATE_KEY` GitHub secret, attaching `manifest.json` and `manifest.json.sig` to the release. The public key ships in the repo and server package at `keys/pops_release_ed25519.pub.pem`; `tools/sign_release.py` signs and verifies (cross-checked against OpenSSL), and CI runs its self-test. Because the version is inside the signed manifest, a verifier can refuse a replayed older release. The release workflow now also fails unless the git tag equals `v<VERSION>` and the CHANGELOG has an entry for it. Server- and agent-side verification land in later phases.

### Changed
- **Backend:** The database schema is now owned by a migration runner (`Backend/migrate.py` + numbered `Backend/migrations/NNNN_*.sql`) instead of the inline `init_db()` function, which is removed. On startup the server applies any pending migration under a PostgreSQL advisory lock; `0001_baseline.sql` is idempotent, so an existing live database only gains a `schema_migrations` bookkeeping table. New tables and columns must be added as a new numbered migration, never inline in `server.py`. The deploy script now syncs `migrate.py` and `migrations/` to the app directory, and CI runs the migration on an empty PostgreSQL 13 to prove it builds the full schema and is idempotent.
- **Agent/CI:** Version numbers now come from a single source. The repository-root `VERSION` file feeds `<Version>` to every agent project through `Agent/Directory.Build.props`, and each component reads its version from its own assembly at runtime instead of a hardcoded constant. The stale `POpsWatchdog` (`2.0.1-GHOST-SERGEANT`) and `POpsVision` (`1.0.7-FULL-COMMAND`) version strings are gone. A CI job fails the build when `VERSION`, the top CHANGELOG release heading and the built assembly `<Version>` disagree.

### Fixed
- **Agent:** Settings are read from `appsettings.json` in the agent's install folder first, then from `C:\POps\appsettings.json`. Before, only `C:\POps` was checked, so an agent installed elsewhere (for example `C:\Program Files (x86)\POps`) found no `ServerUrl` and, since the built-in server address was removed in 0.1.1, connected to `127.0.0.1` and stopped reporting after an update.
- **Agent:** The startup log showed the version as `vv0.1.2-alpha`.

## [0.1.2-alpha] - 2026-09-26

Security and bug-fix release, and the first release published as two install files: `pops-server-0.1.2-alpha.tar.gz` (backend and dashboard) and `POps-Agent-0.1.2-alpha-win-x64.zip` (agent, tray, watchdog and updater; needs the .NET 8 Desktop Runtime, x64). Unsigned pilot build.

Upgrading the server: install the pinned requirements (`pip install -r Backend/requirements.txt`, which replaces `python-jose` with `PyJWT`), restart the backend, and delete `POPS_WOL_CONFIRM_PASSWORD` from `.env`.

### Security
- **Backend:** Panel login accepts bcrypt password hashes only. The fallback that accepted legacy unsalted SHA-256 hashes is removed; an account still stored that way can no longer log in until an admin sets a new password.
- **Backend:** `/docs`, `/redoc` and `/openapi.json` are no longer served.
- **Backend:** Removed `GET /api/stream/start/{pc_name}`, which started screen capture without the Vision consent flow. The dashboard never used it.
- **Backend:** An agent can only complete tasks that are assigned to it.
- **Backend:** Creating, editing and deleting panel users now requires the `superadmin` role; before, any admin could create a superadmin. Roles are limited to `superadmin`, `admin` and `viewer`, the permission list must be a JSON array, a user cannot delete their own account, and the last active superadmin cannot be deleted or demoted. Errors now return 4xx with a message the dashboard shows.
- **Backend:** Vision session and quarantine audit records take the administrator's identity from the JWT instead of trusting `admin_id` / `admin_name` / `admin_role` from the request body. Starting a mandatory (no-consent) Vision session requires a reason.
- **Backend:** Replaced `python-jose` with `PyJWT` and pinned every backend dependency to an exact version. Existing tokens stay valid.
- **Dashboard:** Login rate limiting (10 attempts per minute) now counts per browser IP; before, every login came from the dashboard's own address and shared one quota.
- **Dashboard:** The "wake all devices" confirmation password is no longer written into the page's JavaScript, where every logged-in user could read it. The action now asks the user to type `TÜMÜ`.
- **Agent:** `C:\POpsData` was created with Everyone:FullControl, so any logged-in student could edit `identity.key` and connect as another device, or pause the watchdog. The service now resets the folder's ACL on every start to SYSTEM and Administrators (full control) and Users (read only), and restricts `C:\POps\appsettings.json`, which holds `BypassSecret`, to SYSTEM and Administrators.
- **Agent:** The tray pipe accepts interactive users, SYSTEM and Administrators instead of Everyone.
- **Agent:** Removed the tray's `--stealth` mode (hidden icon, no notifications) and the student menu items "Çıkış", "Koruyucuyu (WatchDog) Duraklat" and "Ekran İzlemeyi Duraklat" (the last one never did anything). The service ignores the old pause commands.
- **Agent:** Screen previews were captured without telling the user. Every preview now updates the tray icon's tooltip with the time and shows a notification at most every five minutes.

### Fixed
- **Dashboard:** The Terminal page never showed command output because it opened its WebSocket at `/panel` instead of `/ws/panel`.
- **Dashboard:** The sidebar never highlighted the current page.
- **Dashboard:** Bulk "wake" on the devices page showed a success message without sending anything; it now wakes each selected device and reports how many succeeded.
- **Dashboard:** Eight CSS variables were used but never defined, so the home page KPI accent bars and some other elements did not render. Chart grid lines, the storage chart's free slice, the log inspector and loading placeholders still used dark-theme colors.
- **Backend:** The device list always showed the agent version as unknown.
- **Backend:** Peer Wake-on-LAN never found a peer in the lab (status case mismatch); it now also requires the peer to be connected.
- **Backend:** Storage statistics and device deletion used the retired `agent_logs` table instead of `agent_logs_v2`.
- **Backend:** Renaming a lab lost its seating layout and left tasks pointing at the old name; deleting a lab left its settings behind.
- **Backend:** A device could stay "Online" after its connection failed, and an agent's old socket closing late could drop its new connection. All devices are marked offline when the server starts.
- **Vision:** The frame-rate selector offered 15-60 FPS but the setting was dropped on the way to the tray, which caps capture at 5 FPS anyway. The selector now offers 1, 2 and 5 FPS and the tray applies the change to a running capture.
- **Agent:** The watchdog no longer retries a WebSocket connection to `/ws/watchdog` every 15 seconds; the backend never had that endpoint.

### Removed
- **Configuration:** `POPS_WOL_CONFIRM_PASSWORD` is no longer used and can be deleted from `.env`.
- **Dashboard:** Dark mode. The panel now uses a single light theme; the theme toggle buttons and the saved `pops_theme` preference are gone, and native form controls stay light even when the operating system prefers dark.

### Known issues
- **Agent updates:** Updating through the Update Center does not replace `POpsTray`, because the updater does not close the running tray and its files stay locked; the copy then stops part-way. Until the updater is fixed, leave `POpsTray.*` out of packages uploaded to the Update Center. The tray changes in this release (no stealth mode, preview notices, reduced menu) reach a device only through a fresh install.
- **Agent connections** are not authenticated with per-device credentials yet. Allow the backend port or `/ws/agent` only from lab networks.
- **.NET 8** reaches end of support on 10 November 2026; the agent moves to .NET 10 in a later release.
- **Updating agents older than 0.1.1:** those agents had the server address compiled in. Updated agents read it only from `C:\POps\appsettings.json` (the install folder is also checked from the next release), so set `ServerUrl` there before updating; otherwise the agent connects to `127.0.0.1` and stops reporting.

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

[Unreleased]: https://github.com/PashaCore/POps/compare/v0.1.2-alpha...HEAD
[0.1.2-alpha]: https://github.com/PashaCore/POps/compare/v0.1.1-alpha...v0.1.2-alpha
[0.1.1-alpha]: https://github.com/PashaCore/POps/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/PashaCore/POps/releases/tag/v0.1.0-alpha
