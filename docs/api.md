# API Reference

The POps backend (FastAPI) serves a REST API under `/api/` and WebSockets under `/ws/`. Behind the
reverse proxy both are reached on the panel's own origin, for example `https://pops.example.com/api/devices`.
The backend itself listens on `127.0.0.1:8000` (see [`installation.md`](installation.md)).

The tables below were produced from the running app's route table (`server.app.routes`: 102 HTTP routes, 3
WebSocket routes and 2 static mounts) together with the authentication dependency of each route, and each purpose line was checked against the endpoint code in
`Backend/pops/routers/` and `Backend/system_routes.py`.

There is no interactive API documentation: the app is created with `docs_url=None`, `redoc_url=None` and
`openapi_url=None`, so `/docs`, `/redoc` and `/openapi.json` are not served.

## Authentication

### Panel users (JWT)

1. `POST /api/admin/login` with `{"username": "...", "password": "..."}`.
   - Without 2FA the reply is `{"status": "success", "token": "<jwt>", "role": ..., "username": ..., "permissions": ...}`.
   - With 2FA enabled the reply is `{"status": "totp_required", "challenge": "<token>"}`. Send the challenge and the
     authenticator code to `POST /api/admin/login/totp` (`{"challenge": "...", "otp": "123456"}`) to get the token.
     The challenge is valid for 5 minutes and is rejected everywhere else.
2. Send the token on every request, in one of two ways:
   - `Authorization: Bearer <jwt>` (scripts and other clients), or
   - the `pops_jwt` cookie. The dashboard sets it `httpOnly` and `SameSite=Strict` after login. A request that is
     authenticated **by the cookie** and uses any method other than `GET`, `HEAD` or `OPTIONS` must also send
     `X-Requested-With: XMLHttpRequest`; otherwise it is refused with `403` (CSRF protection). Bearer requests do
     not need this header.

The token is an HS256 JWT signed with `JWT_SECRET`. It carries the user name, the role and a `token_version`,
and expires after `JWT_EXPIRE_HOURS` (default 12). On **every** request the backend looks the user up in the
database: the user must still exist and the token's `token_version` must match the stored one. The role used
for the permission check is read from the database, not from the token. Editing a user (`PUT /api/admin/users/{id}`,
including a role or password change) increments `token_version`, and deleting a user removes the row, so
their existing tokens stop working immediately.

### Roles

| Dependency | Who passes | Used for |
| --- | --- | --- |
| `require_auth` | any signed-in user (`viewer`, `admin`, `superadmin`) | reading data (including reports, licences and CSV exports), own 2FA settings |
| `require_admin` | `admin`, `superadmin` | day-to-day operations (devices, labs, tasks and scheduled tasks, remote control, policies, Windows Update commands, licence definitions, helpdesk tickets, the notification list, release notes) |
| `require_superadmin` | `superadmin` | users, releases and agent updates, enrollment, enforcement, capabilities, self-update, audit verification, notification settings |

Missing or invalid token: `401`. Valid token but insufficient role, or a failed CSRF check: `403`.

The per-user `permissions` list (page names) only controls which dashboard pages a non-superadmin sees; the
API itself checks roles only. See [`dashboard.md`](dashboard.md).

### Agents

Agents do not use JWTs. They authenticate with headers:

| Header | Sent on | Meaning |
| --- | --- | --- |
| `X-Enroll-Token` | `/ws/agent/…`, `/ws/vision/…` | One-time (or limited-use) enrollment token from the panel. Accepted while it is unexpired and has uses left. |
| `X-Agent-Secret` | `/ws/agent/…`, `/ws/vision/…`, agent HTTP endpoints | Per-device secret issued by the server at enrollment. The server stores only its SHA-256. |
| `X-Agent-Id` | agent HTTP endpoints | The device's hardware ID (`HW-…`). Checked together with `X-Agent-Secret`. |
| `X-Agent-Version` | `/ws/agent/…` | Agent version, stored in `agent_versions`. |

On the agent HTTP endpoints (`agent_http_auth` in the tables below) a valid `X-Agent-Id` + `X-Agent-Secret` pair
binds the request to that device: writing data for another device returns `403`. Requests without valid
credentials are accepted (without that binding) while `enforce_agent_auth` is off, and refused with `401` once
it is on. The WebSocket rules are described under [WebSockets](#websockets). Enrollment and enforcement are
explained in [`agent.md`](agent.md) and [`security.md`](security.md).

### Rate limits

`POST /api/admin/login`, `POST /api/admin/login/totp` and `POST /api/admin/2fa/setup|enable|disable` are limited to
10 requests per minute per client address. Exceeding the limit returns `429`.

## Endpoint reference

`Auth` is the dependency attached to the route. `none` means the route is reachable without credentials.

### Health and system information

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/health` | none | Database reachability and running version: `{"status": "ok" \| "degraded", "database": bool, "version": "..."}`. Used by the deploy health check. |
| GET | `/api/system/version` | require_admin | Running version, latest GitHub release, staged (verified) agent release, server update status (commits on GitHub `main` since the last self-update), enrolled/total agent counts, `enforce_agent_auth`. `?check=true` bypasses the hourly GitHub cache. Offline-safe: GitHub failures give `latest: null`. |
| GET | `/api/system/release-notes` | require_admin | Release notes parsed from `CHANGELOG.md` on GitHub: `installed` (the running version and Unreleased entries of the installed commit), `incoming` (entries on GitHub `main` that the installed commit does not have; needs a successful self-update so the installed commit is known) and `agent` (notes of the latest GitHub release). `{"available": false}` when GitHub cannot be reached. The `main` copy is cached for 10 minutes. |
| GET | `/api/system/audit-verify` | require_superadmin | Walks the hash chain of `device_audit_logs`; returns `{"ok": true, "checked": n}` or the first broken entry id. |

### Panel login, 2FA and users

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/admin/login` | none | Password login (bcrypt hashes only). Returns a token, or a 2FA challenge. Rate-limited. |
| POST | `/api/admin/login/totp` | none | Second login step: `{challenge, otp}`. Rate-limited. |
| GET | `/api/admin/2fa/status` | require_auth | Whether 2FA is enabled for the current user. |
| POST | `/api/admin/2fa/setup` | require_auth | Creates a new TOTP secret (not yet enforced); returns `secret` and an `otpauth://` URI. `400` if 2FA is already on. |
| POST | `/api/admin/2fa/enable` | require_auth | `{otp}`: confirms a code and turns 2FA on. |
| POST | `/api/admin/2fa/disable` | require_auth | `{otp}`: turns 2FA off; a valid code is required while it is on. |
| GET | `/api/admin/users` | require_admin | Lists users (id, username, role, last login, permissions). |
| POST | `/api/admin/users` | require_superadmin | `{username, password, role, permissions}`. Roles: `superadmin`, `admin`, `viewer`; `permissions` is a JSON array string. `409` if the name exists. |
| PUT | `/api/admin/users/{user_id}` | require_superadmin | Updates name, role, permissions and optionally password; invalidates the user's tokens. The last superadmin cannot be demoted. |
| DELETE | `/api/admin/users/{user_id}` | require_superadmin | Deletes a user. You cannot delete yourself or the last superadmin. |

### Devices and labs

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/devices` | require_auth | All devices with status, lab, IP, current user, active window, quarantine flag, agent version, running version and capability state. |
| DELETE | `/api/devices/{pc_name}` | require_admin | Deletes the device, its hardware and software inventory, its Windows Update status, its `agent_logs_v2` rows, its version row and its **device secret**, and closes its socket (code `4000`). |
| GET | `/api/inventory` | require_auth | Hardware inventory of all devices (`hw_inventory`). |
| GET | `/api/logs` | require_auth | Latest event log entries (`agent_logs_v2`), `?limit=` (default 1000). |
| POST | `/api/rename_device` | require_admin | `{pc_name, display_name}`: sets the display name. |
| POST | `/api/move_pc` | require_admin | `{pc_name, new_lab}`. |
| POST | `/api/move_pcs` | require_admin | `{pc_names: [...], new_lab}`. |
| GET | `/api/custom_labs` | require_auth | Names of the labs created in the panel. |
| POST | `/api/create_lab` | require_admin | `{lab_name}`. |
| POST | `/api/rename_lab` | require_admin | `{old_name, new_name}`; moves devices, the seating layout and task records in one transaction. |
| POST | `/api/delete_lab` | require_admin | `{lab_name}`; its devices go back to `Atanmamis_Cihazlar` (unassigned). |
| GET | `/api/lab_settings` | require_auth | Per lab: main PC and seating layout JSON. |
| POST | `/api/set_main_pc` | require_admin | `{lab_name, pc_name}`; sets the lab's main PC, or clears it if it is already that PC. |
| POST | `/api/save_lab_layout` | require_admin | `{lab_name, layout_json}`. |
| POST | `/api/set_auto_enroll` | require_admin | `{target_lab, expire_date: "YYYY-MM-DD"}`. Devices that connect for the first time on or before that date go into `target_lab`; an enrollment token's lab takes precedence. Stored as `auto_enroll_lab` and written to the audit log. `400` for an invalid date or an empty lab. |

### Wake-on-LAN

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/wake_pc/{pc_name}` | require_admin | Sends a magic packet to the device's MAC (from its inventory) and asks one online agent in the same lab to send one too. |
| POST | `/api/wake_lab/{lab_name}` | require_admin | Same for every device in the lab; returns `woken_pcs`. |
| POST | `/api/wake_all` | require_admin | Same for every device with a known MAC. |

### Tasks, deployment and packages

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/deploy_orchestration` | require_admin | `{target_mode: "ALL" \| "LAB" \| "PC", targets: [...], taskSequence: [{name, type, command}]}`. Queues one task per target and step, recording the requesting user, then starts the queue. |
| GET | `/api/tasks` | require_auth | Task list, newest first, `?limit=` (default 1000). |
| POST | `/api/tasks/action` | require_admin | `{action: CANCEL \| RETRY \| PAUSE \| RESUME, target_mode: TASK \| LAB \| PC \| ALL, target_id}`. |
| POST | `/api/flush_queue` | require_admin | Deletes all task records; the deletion (who, how many) is written to the hash-chained audit log first. |
| GET | `/api/get_concurrent_limit` | require_auth | Current `concurrent_limit` (default 5). |
| POST | `/api/set_concurrent_limit` | require_admin | `{limit}`: how many devices may run a task at the same time; `0` means no limit. |
| POST | `/api/upload` | require_admin | Multipart `file`. Stored under `Backend/storage` with a sanitised name. Returns `sig` (and `url`) for the signed download link and the file's `sha256`. |
| GET | `/api/packages` | require_auth | Saved package definitions of the Deployment page. |
| POST | `/api/add_package` | require_admin | `{id, name, type, meta, command, icon, color}`; insert or update. |
| POST | `/api/delete_package` | require_admin | `{id}`. |
| GET | `/api/storage` | require_auth | Size of uploaded files and update packages, event log table size and a 7-day log trend. |

### Remote control and Vision

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/audit/session/start` | require_admin | `{target_pc, reason, is_mandatory}`. Opens a recorded remote-control session and sends `start_vision_session` to the agent. A mandatory session needs a reason. Returns `session_id` and `countdown_seconds`. |
| POST | `/api/audit/session/end` | require_admin | `{session_id, status}`: closes the session and withdraws the session grant. |
| GET | `/api/thumbnail/{pc_name}` | require_admin | Asks the agent for a screen preview and waits up to 5 seconds. |
| POST | `/api/remote_input` | require_admin | `{type, device, input_type, data}`. Refused with `403` unless the caller has an open session for that device. |
| GET | `/api/stream/stop/{pc_name}` | require_auth | Sends `stop_stream` to the agent. |

See [`vision.md`](vision.md) for the session rules.

### Quarantine and offline bypass

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/security/lockdown` | require_admin | `{target_pc, reason}`: marks the device quarantined and sends `lockdown`. Logged to both audit tables. |
| POST | `/api/security/unlock` | require_admin | `{target_pc, reason}`: clears quarantine and sends `unlock`. |
| POST | `/api/security/bypass_token/{pc_name}` | require_admin | The device's next offline bypass code for today (per-device key; the legacy `BYPASS_SECRET` code for older agents). Each request returns the next of up to 10 daily codes (`n`), because 0.1.13+ agents accept each code once. Logged, `Cache-Control: no-store`. |

### Agent policies

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/agent_policies` | none | Fair-use text, DNS categories, `auto_quarantine`, `quarantine_threshold` and `dns_domains`. Read by agents; contains no secrets. |
| POST | `/api/agent_policies` | require_admin | Saves the policy object above. `dns_domains` is cleaned (lower case, no scheme or path, no leading `*.`, no duplicates, at most 5000 per category); if the field is omitted, the stored lists are kept. |

### Agent-facing HTTP endpoints

Meant for agents, not for the dashboard. Agents up to 0.1.4-alpha send `POST /api/inventory/{hw_id}` (when the
server asks for hardware data) and have code for `POST /api/policy_alert`; they do not call the sign-in or log
endpoints. All of these accept the same agent authentication (accept-both while enforcement is off), but the
side effects listed below happen only for an **enrolled** agent (valid `X-Agent-Id` + `X-Agent-Secret`). The
software, Windows Update and helpdesk endpoints are stricter and are listed in their own sections.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/auth/login` | agent_http_auth | `{hw_id, hostname, student_id}`: sign-in event; logs it and sets the device's current user. |
| POST | `/api/auth/failed` | agent_http_auth | Failed sign-in event. |
| POST | `/api/auth/logout` | agent_http_auth | Sign-out event; clears the current user. |
| POST | `/api/inventory/{pc_name}` | agent_http_auth | Hardware inventory (CPU, RAM, board, GPU, OS, IP, MAC, disks). |
| POST | `/api/logs/{pc_name}` | agent_http_auth | Event log entry into `agent_logs_v2`. Two event types also change the device state when an enrolled agent sends them (see below). |
| POST | `/api/policy_alert` | agent_http_auth | `{hw_id, domain, category}`: DNS policy violation, logged as a high-risk event. A notification is raised only when an enrolled agent sends it (one per device and category per 10 minutes). |

Special `event_type` values on `POST /api/logs/{pc_name}`, applied only for an enrolled agent:

| `event_type` | Effect |
| --- | --- |
| `agent.auto_quarantine` | The agent quarantined itself at the DNS violation threshold: the device is marked quarantined, the event is written to the hash-chained audit log (`auto_quarantine`), and a high-severity notification is raised. |
| `agent.offline_bypass` | The quarantine was lifted on the PC with an offline bypass code: the quarantine flag is cleared, the event is audited (`offline_bypass`), and a medium-severity notification is raised. |

From an agent without a valid secret these are stored as ordinary log entries and change nothing. Agents up to
0.1.4-alpha do not send them.

### Scheduled tasks

A scheduled task queues a command for its targets at a set time; when due it goes through the normal task queue.
Times are in the server's time zone. Every change and every run is written to the hash-chained audit log.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/scheduled_tasks` | require_admin | All scheduled tasks with `next_run`, `last_run`, `last_result`, plus the current `server_time`. |
| POST | `/api/scheduled_tasks` | require_admin | `{name, command, target_mode: ALL \| LAB \| PC, targets, schedule_type: once \| daily \| weekly, run_at?, time_of_day?, weekdays?, enabled}`. `once` needs a future `run_at` (`YYYY-MM-DDTHH:MM`); `daily` and `weekly` need `time_of_day` (`HH:MM`); `weekly` needs `weekdays` (1 = Monday … 7 = Sunday). Name up to 100, command up to 4000 characters. |
| POST | `/api/scheduled_tasks/{task_id}/toggle` | require_admin | `{enabled}`: pause or resume. A one-time task whose time has passed cannot be resumed (`400`). |
| POST | `/api/scheduled_tasks/{task_id}/run` | require_admin | Queues the command once, now; the caller is recorded as the requester. Returns `queued` (number of tasks). |
| DELETE | `/api/scheduled_tasks/{task_id}` | require_admin | Deletes the scheduled task. Already queued tasks are not affected. |

### Notifications

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/notifications` | require_admin | Latest notifications (`?limit=`, default 30, at most 200) with `channels`, `delivery_error`, `is_read`, and the `unread` count. |
| POST | `/api/notifications/read` | require_admin | `{ids: [...]}` marks those as read; an empty list marks all. |
| GET | `/api/system/notify-settings` | require_superadmin | `enabled`, `min_severity`, `email_to`, `webhook_url` and `smtp_configured` (SMTP values themselves are never returned). |
| POST | `/api/system/notify-settings` | require_superadmin | Saves the same fields. `min_severity` is `info`, `medium`, `high` or `critical`; up to 20 comma-separated addresses; the webhook must start with `http://` or `https://`. Audited. |
| POST | `/api/system/notify-test` | require_superadmin | Sends a test notification with the settings in the request body (saved or not) and returns the channels that worked and any `error`. |

### Software inventory and Windows updates

The two agent endpoints accept **only enrolled agents** with a valid `X-Agent-Id` + `X-Agent-Secret` for that
device, even while `enforce_agent_auth` is off: without credentials the answer is `401`, for another device `403`.
Agents send this data from 0.1.5-alpha on.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/software/{pc_name}` | agent_http_auth, enrolled only | `{items: [{name, version, publisher, install_date}]}`: the device's complete list of installed programs; replaces the previous list. At most 5000 items (`413` above). |
| POST | `/api/patches/{pc_name}` | agent_http_auth, enrolled only | Windows Update state: `{pending_count, pending_security, pending_critical, reboot_required, last_search, last_install, updates: [{kb, title, severity, categories, is_security}], last_result}`. Timestamps are ISO 8601; at most 500 updates are stored. |
| POST | `/api/patches/scan` | require_admin | `{target_mode, targets, scope}`: sends `scan_updates` to the online targets. Returns `dispatched` and `skipped_offline`. Audited. |
| POST | `/api/patches/install` | require_admin | Same body; sends `install_updates` with `scope` `security` (security and critical updates) or `all`. The agent does not restart the PC; it reports `reboot_required`. Audited. |
| GET | `/api/patches` | require_auth | Windows Update state of every device, including devices that never reported (`reported: false`). |
| GET | `/api/software` | require_auth | Programs across the fleet (`?q=` searches name and publisher, `?limit=` default 300, at most 2000) with publisher, versions and the number of devices; also `reporting_devices`. |
| GET | `/api/software/devices` | require_auth | `?name=<exact program name>`: devices with that program and their versions. |
| GET | `/api/devices/{pc_name}/software` | require_auth | Installed programs of one device. |

`/api/patches/scan` and `/api/patches/install` are registered before `/api/patches/{pc_name}`, so they are not
taken as device IDs.

### Reports

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/reports/summary` | require_auth | `?days=` (1–365, default 30). Device counts (total, online, quarantined, enrolled, per lab), agent versions, Windows Update and software coverage, events by risk and by day, most-violated domains, devices with the most high/critical events, and agent update results in the period. |
| GET | `/api/reports/export` | require_auth | `?kind=devices \| software \| patches \| licenses \| events&days=`: CSV download (semicolon-separated, UTF-8 with BOM). `events` covers the chosen period, at most 50000 rows. Cells starting with `=`, `+`, `-`, `@`, tab or carriage return are prefixed with `'` so spreadsheets do not run them as formulas. |

### Licences

A licence counts the devices whose installed programs match it, using the software inventory (agents 0.1.5-alpha
and later). A program matches when its name contains `match_pattern` and, if `publisher` is set, its publisher
contains that text (both case-insensitive). Every change is written to the hash-chained audit log.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/licenses` | require_auth | All licences with `installed`, `free` and `state` (`ok`, `over`, `expiring` within 30 days, `expired`), plus a `summary` count per state. |
| GET | `/api/licenses/{license_id}/devices` | require_auth | Devices with a matching program, and the program name and version. |
| POST | `/api/licenses` | require_admin | `{name, match_pattern, publisher?, seats?, license_type: per_device \| site \| subscription, expires_at?: "YYYY-MM-DD", notes?}`. `seats` empty means unlimited. `match_pattern` is 2–200 characters of plain text; `%`, `_` and `\` are rejected in the pattern and the publisher filter. |
| POST | `/api/licenses/{license_id}` | require_admin | Replaces a licence definition (same body). |
| DELETE | `/api/licenses/{license_id}` | require_admin | Deletes a licence definition. |

Once a day the scheduler raises a notification for each licence that is over its seats (high), expired (high) or
ends within 30 days (medium).

### Helpdesk

Tickets are opened from the panel or by an agent on behalf of the signed-in user. Categories: `donanim`,
`yazilim`, `ag`, `yazici`, `hesap`, `diger`; statuses: `open`, `in_progress`, `waiting`, `resolved`, `closed`;
priorities: `low`, `normal`, `high`.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/tickets/agent/{pc_name}` | agent_http_auth, enrolled only | `{subject, body?, category?, reporter?}`: opens a ticket for that device. `429` when the device already has 5 open tickets (open, in progress or waiting) or opened 10 in the last hour. Raises a medium-severity notification. |
| GET | `/api/tickets/agent/{pc_name}` | agent_http_auth, enrolled only | The device's latest 20 tickets with their status and the replies, **without** internal notes. |
| GET | `/api/tickets` | require_admin | Ticket list: `?status=active` (default: open, in progress, waiting) or one status, `?q=` searches subject, text, reporter and host name; at most 300, high priority first; plus counts per status. |
| GET | `/api/tickets/{ticket_id}` | require_admin | One ticket with the device's current state and the full thread, internal notes included. |
| POST | `/api/tickets` | require_admin | `{subject, body?, category?, priority?, pc_name?, reporter?}`: opens a ticket from the panel (reporter defaults to the current user). |
| POST | `/api/tickets/{ticket_id}/update` | require_admin | `{status?, priority?, assignee?}`. Each change is added to the thread as an internal note. |
| POST | `/api/tickets/{ticket_id}/messages` | require_admin | `{body, internal}`. A reply (`internal: false`) to an `open` ticket sets it to `waiting`; an internal note does not change the status. |

The agent endpoints require a valid `X-Agent-Id` + `X-Agent-Secret` for that device even while enforcement is
off (`401` without, `403` for another device). The subject must have at least 3 characters. Agents up to
0.1.4-alpha have no ticket function in the tray.

### Signed releases and agent updates

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/system/upload-release` | require_superadmin | Multipart `files` (`manifest.json`, `manifest.json.sig` and packages) and form field `force`. Verifies the ed25519 signature against `keys/pops_release_ed25519.pub.pem` and every file's SHA-256, then stages the release under `Backend/releases/<version>/`. `409` if it is not newer than the staged release (unless `force`). |
| POST | `/api/system/fetch-release` | require_superadmin | `{tag?, force}`: downloads `manifest.json`, its signature and the agent MSI of a GitHub release (latest if `tag` is empty) and runs the same verification as an upload. `502` if GitHub cannot be reached. |
| POST | `/api/system/deploy-update` | require_superadmin | `{target_mode: "ALL" \| "LAB" \| "PC", targets}`: copies the staged MSI to `/updates/` and sends `update_agent` with the signed manifest to the **online** targets. Returns `dispatched` and `skipped_offline`. |

### Enrollment, identity and capabilities

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/system/enroll-token` | require_superadmin | `{lab_name?, note?, ttl_hours = 72, max_uses = 1}` (TTL 1 h – 30 days, uses 1–10000). Returns the token. |
| GET | `/api/system/enroll-tokens` | require_superadmin | Latest 200 tokens with use counts and expiry. |
| DELETE | `/api/system/enroll-token/{token_id}` | require_superadmin | Revokes (deletes) a token. |
| POST | `/api/system/enforce-auth` | require_superadmin | `{enabled}`: turns `enforce_agent_auth` on or off. |
| POST | `/api/system/allow-reenroll` | require_superadmin | `{pc_name, allow}`: lets an already enrolled device obtain a new secret with an enrollment token once. |
| POST | `/api/system/set-capabilities` | require_superadmin | `{pc_name, terminal_enabled?, vision_enabled?}`. `false` disables the capability on the agent and is re-sent when the device reconnects; `true` only clears that standing request (agents ignore an "enable" from the server). |

### Server self-update

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/system/self-update/status` | require_admin | `configured`, `pending`, and the last `deploy-status.json`. |
| POST | `/api/system/self-update` | require_superadmin | Writes `deploy-request.json` into `POPS_SELFUPDATE_DIR` for the root path unit. `503` when self-update is not installed. See [`self-update.md`](self-update.md). |

### Static files

| Path | Served from | Notes |
| --- | --- | --- |
| `/download/<name>?sig=…` | `Backend/storage` | Files uploaded with `/api/upload`, only with the signed link (no login, because agents download packages from here; a wrong or missing signature gets 404). |
| `/updates/<name>` | `Backend/updates` | The agent MSI copied there by `deploy-update`. Served without authentication; agents check its size and SHA-256 against the signed manifest. |

## WebSockets

### `/ws/agent/{hw_id}` — agent command channel

- **Auth:** `X-Agent-Secret` matching the stored hash for `{hw_id}`, or a valid `X-Enroll-Token`. With neither, the
  connection is accepted while `enforce_agent_auth` is off; when it is on, the rejection is written to
  `device_audit_logs` and the socket is closed with code `4401`.
- **Enrollment:** on a connection that used an enrollment token, the server creates a device secret, stores its
  SHA-256, counts one use of the token, moves the device to the token's lab and sends
  `{"action": "set_secret", "secret": "..."}`. If the device already has a secret, this is refused (critical
  audit entry, close `4401`) unless a superadmin allowed re-enrollment for it.
- **Heartbeat:** the agent sends a heartbeat every 5 seconds: an object without `type` that carries `status`,
  `hostname` and the hardware fingerprint `dna_payload`. On the first message of a connection the server
  reconciles the fingerprint with the known devices and may answer with `set_identity` to give the agent a
  different `HW-…` ID (clone detection or identity recovery).
- **Other agent → server messages:** `result` (task output), `thumbnail`, `vision_rejected`, `update_result`,
  `capabilities`, `capability_denied`.
- **Server → agent actions:** `execute`, `get_hardware`, `set_secret`, `set_identity`, `update_agent`,
  `set_capabilities`, `lockdown`, `unlock`, `start_vision_session`, `stop_stream`, `wake_peer`,
  `scan_updates` and `install_updates` (`{"scope": "security" | "all"}`; handled by agents from 0.1.5-alpha on),
  and `remote_input` messages forwarded from the panel (for example `get_thumbnail`).
- **Other close codes:** `4000` when the device is deleted in the panel, `1011` after a malformed message or
  server error.

### `/ws/vision/{hw_id}` — agent screen stream

- **Auth:** same rule as `/ws/agent` (`X-Agent-Secret` or `X-Enroll-Token`; close `4401` when enforcement is on and
  neither is valid).
- The agent sends `stream_frame` and `thumbnail` messages. Each frame is forwarded **only** to the panel sockets of
  admins who hold an open, unexpired remote-control session for that device; with no such panel the frame is
  dropped. Remote mouse/keyboard input from the panel reaches the agent over this socket when it is open.

### `/ws/panel` — dashboard

- **Auth:** the `pops_jwt` cookie only (the `Authorization` header and query-string tokens are not accepted).
  An invalid or revoked session is closed with code `4001`.
- **Panel → server:** `{"type": "ping"}` (answered with `pong`) and `{"type": "remote_input", "device": ..., ...}`.
  Remote input is ignored for `viewer`. Real mouse/keyboard input (`input_type` set) and `action: "execute"`
  additionally require an open remote-control session for that device. When remote input arrives and more than
  10 seconds have passed since the last check, the user's session is re-checked against the database; a revoked
  session closes the socket (`4001`).
- **Server → panel:** `terminal_output` (task results), `update_result`, `capabilities`, `capability_denied`,
  `vision_rejected`, `ticket_new` (a ticket opened by an agent); `thumbnail` replies go to admin/superadmin panels only; live `stream_frame`s go only to the
  session holder (see above).

## Example

```bash
# Log in (an account without 2FA) and list devices
TOKEN=$(curl -s https://pops.example.com/api/admin/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"<password>"}' | python3 -c 'import sys,json; print(json.load(sys.stdin)["token"])')

curl -s https://pops.example.com/api/devices -H "Authorization: Bearer $TOKEN"

# Unauthenticated health check (on the server itself)
curl -s http://127.0.0.1:8000/api/health
```

Error responses use FastAPI's format, `{"detail": "..."}`. Most messages are in Turkish.
