# API Reference

The POps backend (FastAPI) serves a REST API under `/api/v1/` (and, for the panel and existing agents, the same
endpoints under plain `/api/`) and WebSockets under `/ws/`. Behind the reverse proxy both are reached on the panel's
own origin, for example `https://pops.example.com/api/v1/devices`. The backend itself listens on `127.0.0.1:8000`
(see [`installation.md`](installation.md)).

The tables below were produced from the running app's route table (157 HTTP routes, among them the 27 REST names
listed under [REST names and deprecated paths](#rest-names-and-deprecated-paths), 3 WebSocket routes and the
`/updates` static mount) together with the authentication dependency of each route, and each purpose line was
checked against the endpoint code in `Backend/pops/routers/` and `Backend/system_routes.py`. The tables list the
plain `/api/...` paths; every one of them also answers under `/api/v1/...` (see [Versioning](#versioning)).

## OpenAPI specification

The running server does not serve interactive documentation: the app is created with `docs_url=None`,
`redoc_url=None` and `openapi_url=None`, so `/docs`, `/redoc` and `/openapi.json` are not served. The specification
is kept in the repository instead: [`openapi.json`](openapi.json) (OpenAPI 3.1, every path written under
`/api/v1`, operations tagged by endpoint group, deprecated paths marked `deprecated`). It is generated from the code
with `python tools/export_openapi.py`; the CI `backend` job runs `python tools/export_openapi.py --check` and fails
when the committed file differs, so a change to an endpoint or a request model must commit the regenerated file.
Open it in any OpenAPI viewer or client generator.

## Versioning

- `/api/v1/...` is the stable surface for integrations (scripts, monitoring, other systems). Every HTTP endpoint
  under `/api/...` also answers under `/api/v1/...`: the same handler, the same permissions, the same response. A
  small middleware (`Backend/pops/apiversion.py`) rewrites the `/api/v1/` prefix to `/api/` before routing.
- Plain `/api/...` stays for the panel and existing agents and keeps working; agents are not changed.
- WebSockets (`/ws/...`), `/download/...`, `/updates/...` and `/metrics` are not versioned.
- Within v1 changes are additive: new endpoints and new optional request or response fields. Removing or renaming a
  path or a field, or changing what it means, would need `/api/v2`. Paths marked deprecated keep working for the
  life of v1.
- Rate limits and metrics count both forms together: `/api/v1/admin/login` shares the login limit with
  `/api/admin/login`, and `pops_http_requests_total` labels both with the same route template (no separate `v1`
  series).
- Field names are snake_case. The one camelCase request field, `taskSequence` of `POST /api/deploy_orchestration`,
  is also accepted as `task_sequence`; new clients should use `task_sequence` (the panel still sends
  `taskSequence`). Sending both is `422`.
- Uploading large files through `/api/v1` (`POST /api/v1/files`, `/api/v1/upload`,
  `/api/v1/system/upload-release`) needs the larger request body limit of the reverse proxy on those paths too: the
  nginx and Apache templates in `Installer/server/` and `docker/` match
  `^/api/(v1/)?(upload|files|files/push|files/[A-Za-z0-9_-]+/upload|system/upload-release)$` (the last two are the
  [file transfer](#file-transfer) uploads).
  An existing server keeps its own site configuration: until it is updated, use plain `/api/upload` for files over 8 MB.

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

### API tokens (automation)

For scripts and other systems that should not use a person's login. A superadmin creates them in the panel
(**Ayarlar → Güvenlik → API jetonları**) or with `POST /api/v1/tokens`; see [Automation](#automation).

- **Format:** `pops_` followed by 43 URL-safe characters (32 random bytes). The token is returned **once**, in the
  reply to its creation. The server stores only its SHA-256 hash and the first 8 characters after `pops_`
  (`token_prefix`, to recognise it in lists).
- **Sending it:** `Authorization: Bearer pops_...`. It is not accepted in the `pops_jwt` cookie. Because it is not a
  cookie, a request with a token needs no `X-Requested-With` header; cookie sessions still do.
- **Role `viewer`:** `GET` and `HEAD` requests on everything a viewer may read; any other method is `403`, even on
  endpoints that only read (`POST /api/tasks/status`; use `GET /api/v1/tasks`).
- **Role `admin`:** what an admin can do in the panel, except the endpoints below. A token can never be
  `superadmin`.
- **Never reachable with a token (`403`):** every superadmin endpoint (users, tokens, releases and agent updates,
  enrollment, enforcement, capabilities, modules, self-update, notification settings, branding, retention, audit
  verification); the user list and the 2FA endpoints (`require_user_session`, `require_admin_session`); remote
  control and screen access (`/api/audit/session/start|end`, `/api/thumbnail/{pc_name}`, `/api/remote_input`,
  `/api/stream/stop`), which stay tied to a person's panel session. `/ws/panel` accepts only the panel cookie.
- **Expiry and revocation** are checked against the database on every request: a revoked or expired token gets `401`
  at once. `last_used_at` is written at most once a minute per token.
- **Accountability:** actions done with a token are recorded as `token:<name>`: `tasks.created_by`, the `by` / `admin`
  fields of the hash-chained audit log, `actor_id` of the event log. Creating and revoking a token are audit-logged
  (`api_token_created`, `api_token_revoked`). Token names are unique among all tokens, revoked ones included, and
  user names cannot start with `token:`, so `token:<name>` always means one token.

### Roles

| Dependency | Who passes | Used for |
| --- | --- | --- |
| `require_auth` | any signed-in user (`viewer`, `admin`, `superadmin`) or API token | reading data (including reports, licences and CSV exports) |
| `require_admin` | `admin`, `superadmin` (users or `admin` API tokens) | day-to-day operations (devices, labs, tasks and scheduled tasks, quarantine, policies, Windows Update commands, licence definitions, helpdesk tickets, the notification list, release notes) |
| `require_superadmin` | `superadmin` user (never an API token) | users, API tokens, releases and agent updates, enrollment, enforcement, capabilities, self-update, audit verification, notification settings |
| `require_user_session` | any signed-in user (login JWT or panel cookie), not an API token | own 2FA settings |
| `require_admin_session` | `admin`, `superadmin` user, not an API token | the user list, remote-control sessions, screen previews, remote input, file transfer (send, fetch, download a fetched file) |

API tokens have the role `viewer` or `admin` and pass `require_auth` and (as `admin`) `require_admin`; see
[API tokens](#api-tokens-automation).

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
| `X-Agent-Platform` | `/ws/agent/…` (and agent HTTP endpoints) | `linux` from the Linux agent; stored in `clients.platform` on every connection. Windows agents do not send it and count as `windows`. |
| `X-Agent-Features` | `/ws/agent/…` | Comma-separated features the agent implements (lowercase `[a-z0-9_]`, at most 32), for example `winget`. Stored per connection in `agent_versions.features` (empty when the header is missing); the server sends `winget_install` only to agents that announce `winget`. |

On the agent HTTP endpoints (`agent_http_auth` in the tables below) a valid `X-Agent-Id` + `X-Agent-Secret` pair
binds the request to that device: writing data for another device returns `403`. Requests without valid
credentials are accepted (without that binding) while `enforce_agent_auth` is off, and refused with `401` once
it is on. The WebSocket rules are described under [WebSockets](#websockets). Enrollment and enforcement are
explained in [`agent.md`](agent.md) and [`security.md`](security.md).

### Rate limits

`POST /api/admin/login`, `POST /api/admin/login/totp` and `POST /api/admin/2fa/setup|enable|disable` are limited to
10 requests per minute per client address. Exceeding the limit returns `429`. The agent's file transfer endpoints
(`GET /api/files/{id}/download`, `POST /api/files/{id}/upload`) take at most 30 requests per minute per device and
endpoint, like the other agent endpoints that are limited per device (helpdesk, activity).

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
| GET | `/api/admin/2fa/status` | require_user_session | Whether 2FA is enabled for the current user. |
| POST | `/api/admin/2fa/setup` | require_user_session | Creates a new TOTP secret (not yet enforced); returns `secret` and an `otpauth://` URI. `400` if 2FA is already on. |
| POST | `/api/admin/2fa/enable` | require_user_session | `{otp}`: confirms a code and turns 2FA on. |
| POST | `/api/admin/2fa/disable` | require_user_session | `{otp}`: turns 2FA off; a valid code is required while it is on. |
| GET | `/api/admin/users` | require_admin_session | Lists users (id, username, role, last login, permissions). |
| POST | `/api/admin/users` | require_superadmin | `{username, password, role, permissions}`. Roles: `superadmin`, `admin`, `viewer`; `permissions` is a JSON array string. `409` if the name exists. |
| PUT | `/api/admin/users/{user_id}` | require_superadmin | Updates name, role, permissions and optionally password; invalidates the user's tokens. The last superadmin cannot be demoted. |
| DELETE | `/api/admin/users/{user_id}` | require_superadmin | Deletes a user. You cannot delete yourself or the last superadmin. |

### API tokens

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/tokens` | require_superadmin | All tokens, newest first (at most 500): `id`, `name`, `role`, `token_prefix`, `created_by`, `created_at`, `expires_at`, `last_used_at`, `revoked_at` and `state` (`active`, `expired`, `revoked`). Never the token itself. |
| POST | `/api/tokens` | require_superadmin | `{name, role: "viewer" \| "admin", expires_days?}`: `expires_days` 1–3650, omitted or `null` for no expiry. The name has 1–64 letters, digits, spaces, `.`, `_` or `-` (`400` otherwise) and is unique among all tokens, revoked ones included (`409`). Returns `token` (**only here**) and the stored fields. An unknown field or role `superadmin` is `422`. Audit-logged (`api_token_created`). |
| DELETE | `/api/tokens/{token_id}` | require_superadmin | Revokes the token (`revoked_at`); it stops working at once and stays in the list. `already_revoked: true` if it was revoked before; `404` for an unknown id. Audit-logged (`api_token_revoked`). |

### Devices and labs

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/devices` | require_auth | All devices with status, lab, IP, current user, active window, quarantine flag, agent version, running version, capability state, `platform` (`windows` or `linux`) and `agent_features` (what the agent announced in `X-Agent-Features`, for example `["winget"]`; empty for older agents). |
| GET | `/api/devices/{pc_name}/activity` | require_auth | The latest operations on one device, newest first, `?limit=` (default 15, at most 50): its tasks (`kind: "task"` with `id`, `title`, `command` (first 300 characters), `status`, `exit_code`, `at`, `by`, `source`, `reason`, `ip`, `started_at`, `batch_id`, `task_kind`: `"winget"` for a winget step, else `null`) and its remote-control sessions (`kind: "vision"` with `status`, `at`, `ended_at`, `by`, `reason`, `mandatory`), merged. Returns `{"items": [...]}`. The panel shows it as "Son işlemler" in the PC detail panel. |
| DELETE | `/api/devices/{pc_name}` | require_admin | Deletes the device, its hardware and software inventory, its Windows Update status, its `agent_logs_v2` rows, its version row and its **device secret**, and closes its socket (code `4000`). |
| GET | `/api/inventory` | require_auth | Hardware inventory of all devices (`hw_inventory`). |
| GET | `/api/logs` | require_auth | Latest event log entries (`agent_logs_v2`), newest first. `?limit=` (default 1000, at most 20000), optional `pc` (device ID), `since` and `until` (`YYYY-MM-DD`, both days included; `422` if malformed). |
| POST | `/api/rename_device` | require_admin | `{pc_name, display_name}`: sets the display name. **Deprecated:** `PATCH /api/v1/devices/{pc_name}`. |
| POST | `/api/move_pc` | require_admin | `{pc_name, new_lab}`. **Deprecated:** `POST /api/v1/devices/move`. |
| POST | `/api/move_pcs` | require_admin | `{pc_names: [...], new_lab}`. **Deprecated:** `POST /api/v1/devices/move`. |
| GET | `/api/custom_labs` | require_auth | Names of the labs created in the panel. **Deprecated:** `GET /api/v1/labs`. |
| POST | `/api/create_lab` | require_admin | `{lab_name}`. **Deprecated:** `POST /api/v1/labs`. |
| POST | `/api/rename_lab` | require_admin | `{old_name, new_name}`; moves devices, the seating layout and task records in one transaction. **Deprecated:** `PATCH /api/v1/labs/{lab_name}`. |
| POST | `/api/delete_lab` | require_admin | `{lab_name}`; its devices go back to `Atanmamis_Cihazlar` (unassigned). **Deprecated:** `DELETE /api/v1/labs/{lab_name}`. |
| GET | `/api/lab_settings` | require_auth | Per lab: main PC and seating layout JSON. |
| POST | `/api/set_main_pc` | require_admin | `{lab_name, pc_name}`; sets the lab's main PC, or clears it if it is already that PC. **Deprecated:** `PUT` / `DELETE /api/v1/labs/{lab_name}/main-pc` (no toggle). |
| POST | `/api/save_lab_layout` | require_admin | `{lab_name, layout_json}`. **Deprecated:** `PUT /api/v1/labs/{lab_name}/layout`. |
| POST | `/api/set_auto_enroll` | require_admin | **Deprecated:** `PUT /api/v1/settings/auto-enroll`. `{target_lab, expire_date: "YYYY-MM-DD"}`. Devices that connect for the first time on or before that date go into `target_lab`; an enrollment token's lab takes precedence. Stored as `auto_enroll_lab` and written to the audit log. `400` for an invalid date or an empty lab. |

### Wake-on-LAN

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/wake_pc/{pc_name}` | require_admin | **Deprecated:** `POST /api/v1/devices/{pc_name}/wake`. Sends a magic packet to the device's MAC (from its inventory) and asks one online agent in the same lab to send one too. |
| POST | `/api/wake_lab/{lab_name}` | require_admin | **Deprecated:** `POST /api/v1/labs/{lab_name}/wake`. Same for every device in the lab; returns `woken_pcs`, the number of devices a magic packet was **sent** to. Wake-on-LAN has no acknowledgement: whether a PC started shows only when its agent connects. |
| POST | `/api/wake_all` | require_admin | Same for every device with a known MAC. **Deprecated:** `POST /api/v1/devices/wake`. |

### Tasks, deployment and packages

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/deploy_orchestration` | require_admin | **Deprecated:** `POST /api/v1/tasks` (same body). `{target_mode: "ALL" \| "LAB" \| "PC", targets: [...], task_sequence: [{name, type, command}], title?, source?, reason?}` (`taskSequence` is accepted too; not both). A step of `type` `WINGET` (any case) carries `winget: {id, version}` instead of `command` (see [winget steps](#winget-steps)); any other step needs `command` and may not have `winget`. Queues one task per target and step in one transaction, recording the requesting user, then starts the queue. `title` (at most 200 characters), `source` (the panel page the request comes from, at most 40) and `reason` (at most 500) are optional; they are stored on every created task together with the caller's IP address and a `batch_id` shared by the tasks of the request. A task's title is the step's `name`, or `title` when the step has none. Returns `created` (number of tasks) and `task_ids`. The same request from the same user within 5 seconds (double click, retry) creates nothing and returns `duplicate: true`. `target_mode` is not case-sensitive (`"lab"` is `LAB`). `422` for an unknown `target_mode`, a field the endpoint does not know (also inside `task_sequence`), or a PC ID that is not registered (nothing is created). |
| GET | `/api/tasks` | require_auth | Task list, newest first, `?limit=` (default 1000). Besides the queue columns each row has `title`, `source`, `reason`, `client_ip` and `batch_id` (empty on tasks created before migration `0019`); the panel groups the tasks of one `batch_id` into one job. `kind` is `"winget"` for a winget step (`null` for a command) and `payload` its package `{id, version}`. |
| GET | `/api/deploy/winget/catalog` | require_auth, module `deploy` | The winget catalog of the **Dağıtım** page (see [winget steps](#winget-steps)). `?q=` (at most 100 characters; every word must appear in the id, name, publisher, category or description; Turkish letters and case do not matter), `?category=`, `?limit=` (1–500, default 200). `{"items": [{id, name, publisher, category, description, description_en, note?, note_en?}], "matched", "total", "categories": [{id, label, count}], "updated", "source"}`. Ids that start with or equal the query come first. |
| POST | `/api/tasks/status` | require_auth | `{ids: [...]}` (at most 5000): `{"items": [{id, target_pc, target_lab, status, exit_code, dispatched_at}]}` for the tasks that still exist. The panel's job center polls it for the progress of what was sent. |
| POST | `/api/tasks/action` | require_admin | `{action: CANCEL \| RETRY \| PAUSE \| RESUME, target_mode: TASK \| LAB \| PC \| ALL, target_id}`. RETRY opens a **new** task for each finished task (`retry_of` = the old one) unless a retry of it is still pending or running; the old task keeps its result. A retry keeps the title and reason, gets `source` `tasks` and a new `batch_id`, and the reply lists `task_ids`. |
| POST | `/api/flush_queue` | require_admin | **Deprecated:** `DELETE /api/v1/tasks`. Deletes all task records; the deletion (who, how many) is written to the hash-chained audit log first. |
| GET | `/api/get_concurrent_limit` | require_auth | Current `concurrent_limit` (default 5). **Deprecated:** `GET /api/v1/settings/task-concurrency`. |
| POST | `/api/set_concurrent_limit` | require_admin | **Deprecated:** `PUT /api/v1/settings/task-concurrency`. `{limit}` (0–10000): how many devices may run a task at the same time; `0` means no limit. A negative value is refused (`422`). |
| POST | `/api/upload` | require_admin | **Deprecated:** `POST /api/v1/files`. Multipart `file`. Stored under `Backend/storage` with a sanitised name. Returns `sig` (and `url`) for the signed download link and the file's `sha256`. |
| GET | `/api/packages` | require_auth | Saved package definitions of the **Dağıtım** page. |
| POST | `/api/add_package` | require_admin | `{id, name, type, meta, command, icon, color}`; insert or update. **Deprecated:** `POST /api/v1/packages`. |
| POST | `/api/delete_package` | require_admin | `{id}`. **Deprecated:** `DELETE /api/v1/packages/{package_id}`. |
| GET | `/api/storage` | require_auth | Size of uploaded files and update packages, event log table size and a 7-day log trend. |

### File transfer

An admin sends a file to PCs (**push**) or fetches a file from one PC (**pull**). Server side: `Backend/pops/routers/files.py`
and `Backend/pops/filestore.py`; table `file_transfers` (migration `0025`). Module `files` (on by default). The
agent side (Windows) is built by the agent team against the messages below.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/files/push` | admin, panel session only | Multipart: `file`, `pcs` (once per PC, at most 500), `dest` (`public_desktop` \| `inbox`), `reason` (3–300 characters), `allow_exec` (`true`/`false`). The body is read only after the role check; `Content-Length` is required (`411`) and a body over 200 MB is refused before it is read (`413`). The file name is cleaned (path parts, characters Windows does not allow, control and direction characters removed; reserved names such as `CON` get a `_` prefix). `.lnk`, `.url` and `.scr` need `allow_exec` (`400`). An empty file is `400`. The file is stored once with its SHA-256; every target that is online and reports `files_enabled: true` gets its own `transfer_id`, a one-time download token (1 hour) and a `file_push`. Returns `{batch_id, name, size, sha256, transfers: [{pc_name, transfer_id}], skipped: [{pc_name, reason}]}`; `reason` is `unknown`, `module_closed`, `offline`, `unsupported` (the agent does not report `files_enabled`), `disabled` (turned off on the PC) or `send_failed`. `409` if no target can receive it. Audited per PC (`file_push`). |
| POST | `/api/files/pull` | admin, panel session only | `{pc, path, max_size, reason, any_profile = false}`. `path`: a full path with a drive letter (`C:\...`); network (`\\server\share`) and device paths (`\\?\`), `.`/`..` parts, wildcards, `:` after the drive (alternate data streams), control characters and a trailing `\` are `400`. `max_size`: 1 byte – 200 MB (`422` otherwise). `reason` is required (3 characters). `any_profile: true` (also from other users' profiles) needs a superadmin (`403`). The PC must be online with `files_enabled: true` (`409` otherwise). Sends `file_pull` with a one-time upload token (1 hour). Returns `{transfer_id, name}`. Audited (`file_pull`, with path, size limit, `any_profile`, reason, role). |
| GET | `/api/files` | require_auth | `?pc=&limit=` (default 50, at most 200): transfers, newest first, with `transfer_id`, `direction` (`push` \| `pull`), `pc_name`, `batch_id`, `name`, `size`, `sha256`, `dest`, `path`, `max_size`, `allow_exec`, `any_profile`, `reason`, `status`, `detail`, `created_by`, `created_at`, `finished_at`, `downloadable` (a pulled file that is still on the server), `expires_at` (when it is deleted) and `purged`. Also `push_max_bytes`, `pull_max_bytes`, `pull_keep_days`. Never the token or the server path. |
| GET | `/api/files/{transfer_id}/content` | admin, panel session only | The pulled file, always `Content-Disposition: attachment`, `Content-Type: application/octet-stream`, `X-Content-Type-Options: nosniff`, `Cache-Control: no-store`, `Content-Security-Policy: default-src 'none'; sandbox`, `X-Content-SHA256`. `404` for a pushed file, a transfer that has not finished or a file already deleted. Audited (`file_content_download`). |
| GET | `/api/files/{transfer_id}/download?t=<token>` | agent (secret required) | The agent fetches a pushed file. `X-Agent-Id` + `X-Agent-Secret` are required even while `enforce_agent_auth` is off (`401` without). The token is single-use and expires after 1 hour; the transfer must belong to the calling PC. A wrong token, another PC, a used or expired token and an unknown transfer all get the same `404` and do not use up the token. The response carries `X-Content-SHA256` and `nosniff`; the transfer becomes `downloading`. |
| POST | `/api/files/{transfer_id}/upload?t=<token>` | agent (secret required) | The agent uploads a requested file: the raw file as the body (`application/octet-stream`, recommended) or a multipart field `file`. Same token and device rules as the download. Optional `X-Content-SHA256`: a mismatch is `400`. A body larger than `max_size` is `413` (by `Content-Length` before reading, or while streaming). The token is used up when the upload starts: a failed, cut or too large upload makes the transfer `failed`. On success the transfer is `done`; audited (`file_pull_received`, with size and SHA-256). |

Statuses: `sent` (the agent got the command), `downloading` / `uploading` (the token was used), `done`, `rejected`
(the agent or the module refused it), `failed`, `expired` (the token ran out unused; checked every 5 minutes). A
pulled file is kept for 7 days and then deleted (the row stays, `purged: true`); a pushed file is deleted when all
its tokens are used (10 minutes after the last download) or have expired. Finished rows without a file are removed
with the task retention setting (`retention_days_tasks`). Files live in `Backend/transfers` (`POPS_FILES_DIR`),
under names the server makes up; the folder is not served.

Agent messages (on `/ws/agent/{hw_id}`; the server lists `file_transfer` in `server_info.features`). The schemas and
test vectors are in [`protocol/`](protocol/README.md): [`file_push`](protocol/server-to-agent/file_push.json),
[`file_pull`](protocol/server-to-agent/file_pull.json), [`file_result`](protocol/agent-to-server/file_result.json).

```json
{"action": "file_push", "transfer_id": "…", "name": "Ödev 1.pdf", "size": 300026, "sha256": "…",
 "url": "/api/files/<transfer_id>/download?t=<token>", "dest": "public_desktop", "reason": "…", "allow_exec": false}
{"action": "file_pull", "transfer_id": "…", "path": "C:\\Users\\Public\\Documents\\rapor.pdf", "max_size": 52428800,
 "upload": "/api/files/<transfer_id>/upload?t=<token>", "reason": "…", "any_profile": false}
{"type": "file_result", "transfer_id": "…", "outcome": "done", "path": "C:\\Users\\Public\\Desktop\\Ödev 1.pdf", "detail": null}
```

- `url` and `upload` are relative: the agent puts its own server address in front and talks to no other host. It
  checks `size` and `sha256` and writes only to the allowed destinations; it refuses `.lnk`, `.url` and `.scr` unless
  `allow_exec` is set.
- `file_result.status` is `done`, `rejected` or `failed`; `path` (at most 1024 characters) and `detail` (at most 500)
  are optional. Only an open transfer of the same PC is updated; for a push, `done` counts only after the file was
  downloaded. A pulled file is `done` when the upload is stored; a later `file_result` for it changes nothing. A
  `detail` starting with `[REDDEDİLDİ]` is stored as `rejected`. Each stored result is audited (`file_result`).
- The agent reports the capability in its `capabilities` message as `files_enabled` (`true` / `false`). An agent that
  does not send the field is treated as not supporting file transfer (`cap_files_enabled` stays `NULL`), so the
  server sends it nothing. `{"type": "capability_denied", "capability": "files", "action": "file_push" | "file_pull",
  "transfer_id": "…"}` marks the transfer `rejected` and the capability off.

### winget steps

A `WINGET` step installs a package from the winget community source on the target, as SYSTEM, silently and for all
users:

```json
{"name": "winget: Mozilla Firefox", "type": "WINGET", "winget": {"id": "Mozilla.Firefox", "version": null}}
```

- `id` must match `^[A-Za-z0-9][A-Za-z0-9.+_-]{1,127}$`, `version` (optional; `null` or missing = latest) must match
  `^[0-9A-Za-z.+_-]{1,40}$`. Anything else, a `command` on a `WINGET` step or an unknown field in `winget` is `422`.
  These sets leave out spaces, quotes and every shell character, and the agent passes the id and version as
  separate arguments without a shell (see [`agent.md`](agent.md#winget_install-contract)).
- Like package steps, it needs the `deploy` module in the target's lab.
- The task is stored with `kind = "winget"`, `payload = {"id", "version"}` and, as `script_path`, the command line
  the agent runs (`winget install --id Mozilla.Firefox -e --silent --scope machine --accept-package-agreements
  --accept-source-agreements --disable-interactivity [--version …]`), for display only. Without a `name` the title
  is `winget: <id>`.
- The queue sends `{"action": "winget_install", "task_id", "id", "version", "requested_by"}` only to an agent whose
  connection announced `winget` (`X-Agent-Features`). Pending winget tasks of other online PCs become `Denied` with
  exit code -8 and an explanation in `output`; nothing is sent to them. With the `deploy` module off in the lab they
  become `Denied` with `[MODÜL KAPALI]`, and closing the module denies pending and paused winget tasks at once.
- Results: exit code 0, and winget's "already installed / up to date" (-1978335189, -1978335135) and "installed,
  restart pending" (-1978334967, -1978334965) are `Completed`. `[REDDEDİLDİ] …` with -5 (refused) or -7 (no
  winget on the PC) is `Denied`. Any other code is `Failed`. A retry stays a winget task.
- `GET /api/devices` returns `agent_features` per device; the panel uses it to warn before deploying.

### Remote control and Vision

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/audit/session/start` | require_admin_session | `{target_pc, reason, is_mandatory}`. Opens a recorded remote-control session and sends `start_vision_session` to the agent. A mandatory session needs a reason. Returns `session_id` and `countdown_seconds`. |
| POST | `/api/audit/session/end` | require_admin_session | `{session_id, status}`: closes the session and withdraws the session grant. |
| GET | `/api/thumbnail/{pc_name}` | require_admin_session | Asks the agent for a screen preview and waits up to 5 seconds. |
| POST | `/api/remote_input` | require_admin_session | `{device, input_type, ...fields}`: `input_type` is `mouse_move`, `mouse_click`, `mouse_wheel` or `keyboard`; fields (`x`, `y`, `relative`, `button`, `is_down`, `double`, `delta`, `horizontal`, `key`, `code`, `ctrl`, `alt`, `shift`, `meta`, `altgr`) go at the top level, as the panel sends them (the older `data: {...}` object is still accepted). Only these fields are forwarded. Refused with `403` unless the caller has an open session for that device. |
| POST | `/api/stream/stop` | require_admin_session | `{pc_name}`: sends `stop_stream` to the agent. (A `GET` before 0.1.14.) |

See [`vision.md`](vision.md) for the session rules.

### Quarantine and offline bypass

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/security/lockdown` | require_admin | **Deprecated:** `POST /api/v1/devices/{pc_name}/quarantine` `{reason}`. `{target_pc, reason}`: marks the device quarantined and sends `lockdown`. Logged to both audit tables. `409` for a Linux PC (`clients.platform = linux`): the Linux agent has no quarantine yet. |
| POST | `/api/security/unlock` | require_admin | `{target_pc, reason}`: clears quarantine and sends `unlock`. **Deprecated:** `DELETE /api/v1/devices/{pc_name}/quarantine` `{reason}`. |
| POST | `/api/security/bypass_token/{pc_name}` | require_admin | **Deprecated:** `POST /api/v1/devices/{pc_name}/bypass-code`. The device's next offline bypass code for today (per-device key; the legacy `BYPASS_SECRET` code for older agents). Each request returns the next of up to 10 daily codes (`n`), because 0.1.13+ agents accept each code once. Logged, `Cache-Control: no-store`. |

### Exam mode

Exam mode restricts the network of every PC in one lab for a limited time: the agents block all traffic except
the POps server, DNS, DHCP and an allow list, show a message in the tray, optionally block programs, and leave exam
mode on their own at the end time, even when they are offline. It is a network restriction and a notice, not
proctoring; see [`security.md`](security.md#exam-mode). The paths are resource-style already and answer under
`/api/v1` like every route; a lab name with a slash is written as it is (`/api/v1/labs/9/A/exam`). The module
`exam` (on by default; off in the "Kurum" profile) gates starting an exam.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/labs/{lab_name}/exam` | require_admin | Starts exam mode in the lab and sends `exam_mode` to its connected agents; PCs that are off get it when they connect. Body: `{allow: [...], until?: <unix seconds>, duration_minutes?: 1–480, message?, block_apps?: [...], reason}`. Give exactly one of `until` and `duration_minutes`; the end must be at least one minute and at most 8 hours ahead. `allow`: domain names (a pasted URL is reduced to its host, Turkish letters become punycode, no `*` wildcards), IP addresses or CIDR networks (IPv4 at least `/8`, IPv6 at least `/32`); duplicates merge, at most 50. `block_apps`: program file names (`cmd.exe`; a path is reduced to the file name, `.exe` is added when missing, at most 50); the agent's own processes and Windows session processes (`explorer.exe`, `winlogon.exe`, …) are refused. `message`: tray text, at most 200 characters (empty: "Sınav modu: yalnızca izin verilen adresler açık."). `reason` is required. `400` names the first invalid entry; `404` unknown lab; `400` for `Atanmamis_Cihazlar`; `409` when the lab already has an exam or the `exam` module is off for it; `422` for an unknown field. Returns `exam`, `devices` (PCs in the lab) and `delivered` (agents the message was written to). Audit-logged (`exam_start`). |
| DELETE | `/api/labs/{lab_name}/exam` | require_admin | Ends the lab's exam and sends `{"action": "exam_mode", "enabled": false}` to its connected agents and to every connected PC that got the exam (also those moved out); the others get it when they connect. Optional body `{reason}`. `404` when the lab has no exam. Returns `exam` and `delivered`. Audit-logged (`exam_end`). |
| GET | `/api/labs/{lab_name}/exam` | require_auth | `{lab, module_enabled, active, exam, devices, counts}`; a lab without an exam (also a lab name that does not exist) gives `active: false`. `devices` (only while an exam runs): one row per PC in the lab with `pc_name`, `name`, `online`, `state`, `agent_version`, the agent's last `exam_state` (`enabled`, `since`, `until` as unix seconds, `reported_at`) and `sent_at`, `entered_at`, `left_at`, `denied_at`. `state`: `in_exam`, `left` (said "not in exam" while the exam runs), `unreachable` (not connected), `unsupported` (connected but never answered within 20 seconds: an agent without exam mode), `pending` (sent less than 20 seconds ago) or `denied` (exam capability switched off locally). `counts` has every state. |
| GET | `/api/exams` | require_auth | Exam history, newest first: `{"items": [...]}`. `?lab=` one lab, `?active=true` only running exams (each with `counts`), `?active=false` only ended ones, `?limit=` 1–500 (default 50). Each item also has `devices_sent`, `devices_entered` and `devices_left`. |

An exam object has `id`, `lab`, `allow`, `until` (unix seconds), `until_at` (ISO 8601), `message`, `block_apps`,
`reason`, `started_by`, `started_at`, `ended_by`, `ended_at`, `end_reason` (`admin`, `expired`, `lab_deleted`,
`module_off`), `active` and `remaining_seconds`.

- **One exam per lab.** A partial unique index allows one running exam per lab; of two simultaneous starts one gets
  `409`.
- **End time.** The scheduler ends expired exams every 30 seconds (`ended_by` `system`, `end_reason` `expired`,
  audit `exam_auto_end`) and sends `enabled: false`; reading an exam (or starting one) ends an expired one first.
  Agents leave exam mode at `until` themselves.
- **Moving PCs.** A connected PC moved into a lab with a running exam gets it at once; one moved out of it gets
  `enabled: false`. Renaming a lab keeps its running exam; deleting a lab ends it (`lab_deleted`); switching the
  `exam` module off for a lab ends it (`module_off`; `exams_ended` in the reply of `POST /api/modules/exam`).

### Agent policies

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/agent_policies` | none | Fair-use text, DNS categories, `auto_quarantine`, `quarantine_threshold` and `dns_domains`. Read by agents; contains no secrets. An agent that sends `X-Agent-Id` and `X-Agent-Secret` also gets `modules` (`{module_id: bool}` for its lab) and the DNS settings of its lab; without them the organisation-wide setting applies. If DNS policy is off, `dns_domains` is `{}`, `dns_categories` `[]` and `auto_quarantine` `false`; if quarantine is off, `auto_quarantine` is `false`. |
| POST | `/api/agent_policies` | require_admin | **Deprecated:** `PUT /api/v1/agent_policies`. Saves the policy object above. `dns_domains` is cleaned (lower case, no scheme or path, no leading `*.`, no duplicates, at most 5000 per category); if the field is omitted, the stored lists are kept. |

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
A queued run must be sent within `SCHEDULE_VALID_MINUTES` (default 60) of its time, otherwise it becomes `Expired`
instead of running late (a PC that was off at 08:00 does not run the 08:00 command at 15:00). If the server itself
was down for more than `SCHEDULE_MISFIRE_MINUTES` (default 60) past the run time, the run is skipped, recorded as
missed and a `schedule_missed` notification is raised. A PC that still has a pending copy from the same schedule does
not get a second one.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/scheduled_tasks` | require_admin | All scheduled tasks with `next_run`, `last_run`, `last_result`, plus the current `server_time`. |
| POST | `/api/scheduled_tasks` | require_admin | `{name, command, target_mode: ALL \| LAB \| PC, targets, schedule_type: once \| daily \| weekly, run_at?, time_of_day?, weekdays?, enabled}`. `once` needs a future `run_at` (`YYYY-MM-DDTHH:MM`); `daily` and `weekly` need `time_of_day` (`HH:MM`); `weekly` needs `weekdays` (1 = Monday … 7 = Sunday). Name up to 100, command up to 4000 characters. |
| POST | `/api/scheduled_tasks/{task_id}/toggle` | require_admin | **Deprecated:** `PATCH /api/v1/scheduled_tasks/{task_id}`. `{enabled}`: pause or resume. A one-time task whose time has passed cannot be resumed (`400`). |
| POST | `/api/scheduled_tasks/{task_id}/run` | require_admin | Queues the command once, now; the caller is recorded as the requester. Returns `queued` (number of tasks). |
| DELETE | `/api/scheduled_tasks/{task_id}` | require_admin | Deletes the scheduled task. Already queued tasks are not affected. |

### Notifications

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/notifications` | require_admin | Latest notifications (`?limit=`, default 30, at most 200) with `channels`, `delivery_error`, `is_read`, and the `unread` count. |
| POST | `/api/notifications/read` | require_admin | `{ids: [...]}` marks those as read; an empty list marks all. |
| POST | `/api/notifications/clear` | require_admin | `{ids: [...]}` deletes those notifications; an empty list deletes all read ones. Returns `deleted`. |
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
| POST | `/api/licenses/{license_id}` | require_admin | Replaces a licence definition (same body). **Deprecated:** `PUT /api/v1/licenses/{license_id}`. |
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
| POST | `/api/tickets/{ticket_id}/update` | require_admin | **Deprecated:** `PATCH /api/v1/tickets/{ticket_id}`. `{status?, priority?, assignee?}`. Each change is added to the thread as an internal note. |
| POST | `/api/tickets/{ticket_id}/messages` | require_admin | `{body, internal}`. A reply (`internal: false`) to an `open` ticket sets it to `waiting`; an internal note does not change the status. |

The agent endpoints require a valid `X-Agent-Id` + `X-Agent-Secret` for that device even while enforcement is
off (`401` without, `403` for another device). The subject must have at least 3 characters. Agents up to
0.1.4-alpha have no ticket function in the tray.

### Modules and install profiles

Features that can be turned off for the whole organisation or per lab. The most
specific setting wins (lab, then organisation); without a setting a module is on, so an upgrade changes nothing. A
module whose dependency is off is off too (`deploy` needs `terminal`, `licenses` needs `software`, `schedules` needs
`terminal`). Modules: `vision`, `terminal`, `deploy`, `files`, `schedules`, `patches`, `software`, `licenses`,
`helpdesk`, `dns_policy`, `quarantine`, `wol`, `exam`, `reports`. Devices, labs, enrollment, agent updates, the audit log, users,
notifications and server health are core and always on.

When a module is off, its endpoints answer `409` with `detail` "'<name>' modülü kapalı." and the headers
`X-POps-Module: <id>` and `X-POps-Module-State: disabled`. Device-specific calls use the device's lab; organisation
pages (lists, reports) are available when the module is on anywhere. Requests for several devices skip the devices
where the module is off and report `skipped_module_closed`; they fail with `409` only if it is off for all of them.
The task queue does not send a command to a device whose lab has `terminal` off (the task becomes `Denied`), the
scheduler skips devices whose lab has `schedules` off, and software lists, Windows Update results and DNS alerts from
such a lab are answered `{"status": "ignored"}` and not stored. Lifting a quarantine and bypass codes always work.

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| GET | `/api/modules` | require_auth | Every module with `setting` (organisation, `null` = none), `enabled` (organisation, with dependencies), `lab_overrides`, `lab_enabled`, dependencies and profile defaults, plus `profile`, `labs`. |
| POST | `/api/modules/{module_id}` | require_superadmin | `{enabled: true \| false \| null, lab?}`: sets (or with `null` removes) the organisation or lab setting. Turning `vision` off closes open Vision sessions; turning `terminal` off denies pending and paused tasks there; turning `files` off rejects file transfers that were sent but not started (their tokens stop working); turning `exam` off ends the running exams there. Returns `vision_sessions_closed`, `tasks_denied`, `transfers_cancelled`, `exams_ended`. Audited (`module_setting`); an organisation change sets the profile to `custom`. |
| GET | `/api/system/install-profile/{name}` | require_superadmin | Preview of `school` or `org`: the organisation settings that would change and the number of lab overrides. |
| POST | `/api/system/install-profile` | require_superadmin | `{profile: "school" \| "org", reset_labs}`: applies the profile's defaults organisation-wide (and with `reset_labs` deletes lab overrides). Audited (`module_profile`). |

### Signed releases and agent updates

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/system/upload-release` | require_superadmin | Multipart `files` (`manifest.json`, `manifest.json.sig` and packages) and form field `force`. Verifies the ed25519 signature against `keys/pops_release_ed25519.pub.pem` and every file's SHA-256, then stages the release under `Backend/releases/<version>/`. `409` if it is not newer than the staged release (unless `force`). |
| POST | `/api/system/fetch-release` | require_superadmin | `{tag?, force}`: downloads `manifest.json`, its signature and the agent packages the manifest lists (the Windows MSI and, from releases that have it, the Linux `pops-agent_<version>_all.deb`) from a GitHub release (latest if `tag` is empty) and runs the same verification as an upload. `502` if GitHub cannot be reached or a listed package is missing. |
| POST | `/api/system/deploy-update` | require_superadmin | `{target_mode: "ALL" \| "LAB" \| "PC", targets}`: copies the staged agent packages (at most one MSI and one `.deb`) to `/updates/` and sends `update_agent` with the signed manifest to the **online** targets; each agent picks its own package from the manifest. Targets whose platform (`clients.platform`) has no package in the staged release are skipped (`skipped_no_package`). A target that already has a pending update to the same version, sent less than 15 minutes ago or with a stage reported in the last 15 minutes, is not sent again (the agent ignores a second command while its update lock is fresh): it is listed in `already_pending`, online or not. Returns `msi`, `deb`, `dispatched`, `skipped_offline`, `skipped_no_package` and `already_pending`. `target_mode` is not case-sensitive; an unknown mode or field is `422`. |
| POST | `/api/system/update-progress` | require_admin | `{pcs: [...], version, since}` (`since` = Unix time of the dispatch; at most 5000 devices): per device `known`, `online`, `version`, `on_target` (running `version`), `pending` (an update was sent and not answered yet), `sent_at` (when it was sent), the last stage the agent reported for it (`stage`, `detail`, `attempt`, `of`, `stage_at`; all `null` when there is none, see [`update_progress`](#update_progress-agent-update-stages)) and `result` (the update result received since `since`: `status`, `rollback`, `to_version`, `detail`, `agent_state`). `now` is the server's time. Times are Unix seconds. The panel follows an agent update with it. |

### Enrollment, identity and capabilities

| Method | Path | Auth | Purpose |
| --- | --- | --- | --- |
| POST | `/api/system/enroll-token` | require_superadmin | `{lab_name?, note?, ttl_hours = 72, max_uses = 1}` (TTL 1 h – 30 days, uses 1–10000). Returns the token. A field it does not know (for example `lab` instead of `lab_name`) is `422`, so a typo does not create a token with default values. |
| GET | `/api/system/enroll-tokens` | require_superadmin | Latest 200 tokens with use counts and expiry. |
| GET | `/api/branding` | none | `{org_name, logo, logo_v}`: the organisation name and whether a logo is set (`logo_v` changes with the logo). The sign-in page reads it before anyone signs in. |
| GET | `/api/branding/logo` | none | The logo image (`image/png`, `image/jpeg` or `image/webp`, `nosniff`, cached for a day); `404` without a logo. |
| POST | `/api/system/branding` | require_superadmin | `{org_name}` (at most 80 characters; empty or `null` removes it). Audit-logged. |
| POST | `/api/system/branding/logo` | require_superadmin | Multipart `file`: PNG, JPEG or WebP by content (not by name or declared type), at most 256 KB (`413`), else `415`. Audit-logged. |
| DELETE | `/api/system/branding/logo` | require_superadmin | Removes the logo. Audit-logged. |
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
| `/updates/<name>` | `Backend/updates` | The agent packages (MSI, `.deb`) copied there by `deploy-update`. Served without authentication; agents check size and SHA-256 against the signed manifest. |

## REST names and deprecated paths

Endpoints whose path is an action (`/api/create_lab`, `/api/move_pcs`, `/api/set_concurrent_limit` ...) have a
resource-style name under `/api/v1`. Each REST name calls the same handler as the old path (`Backend/pops/routers/rest.py`):
same checks, same module gating, same response. The old paths keep working for the panel and existing scripts;
they are marked deprecated here and in [`openapi.json`](openapi.json). Like every route, the REST names also answer
without the version prefix (`/api/labs`), but integrations should use `/api/v1`.

A lab name in a path is URL-encoded (spaces as `%20`, Turkish letters as UTF-8). A name with a slash, common for
classes such as `9/A`, is written with the slash as it is: `/api/v1/labs/9/A/main-pc`. `9%2FA` works against the
backend and behind nginx, but Apache refuses an encoded slash with `404` unless `AllowEncodedSlashes` is set, so
do not rely on it. A lab whose name ends in `/wake`, `/main-pc` or `/layout` can only be changed with the old paths.

| Deprecated path (still works) | REST name | Notes |
| --- | --- | --- |
| `GET /api/custom_labs` | `GET /api/v1/labs` | |
| `POST /api/create_lab` `{lab_name}` | `POST /api/v1/labs` `{lab_name}` | |
| `POST /api/rename_lab` `{old_name, new_name}` | `PATCH /api/v1/labs/{lab_name}` `{new_name}` | |
| `POST /api/delete_lab` `{lab_name}` | `DELETE /api/v1/labs/{lab_name}` | |
| `POST /api/set_main_pc` `{lab_name, pc_name}` | `PUT /api/v1/labs/{lab_name}/main-pc` `{pc_name}` and `DELETE /api/v1/labs/{lab_name}/main-pc` | The old path toggles (the same PC again clears it); `PUT` sets it, every time, and `DELETE` clears it. |
| `POST /api/save_lab_layout` `{lab_name, layout_json}` | `PUT /api/v1/labs/{lab_name}/layout` `{layout_json}` | |
| `POST /api/wake_lab/{lab_name}` | `POST /api/v1/labs/{lab_name}/wake` | |
| `POST /api/move_pc` `{pc_name, new_lab}`, `POST /api/move_pcs` `{pc_names, new_lab}` | `POST /api/v1/devices/move` `{pc_names: [...], new_lab}` | One or many PCs. |
| `POST /api/rename_device` `{pc_name, display_name}` | `PATCH /api/v1/devices/{pc_name}` `{display_name}` | |
| `POST /api/wake_pc/{pc_name}` | `POST /api/v1/devices/{pc_name}/wake` | |
| `POST /api/wake_all` | `POST /api/v1/devices/wake` | |
| `POST /api/security/lockdown` `{target_pc, reason}` | `POST /api/v1/devices/{pc_name}/quarantine` `{reason}` | |
| `POST /api/security/unlock` `{target_pc, reason}` | `DELETE /api/v1/devices/{pc_name}/quarantine` `{reason}` | The reason goes in the JSON body. |
| `POST /api/security/bypass_token/{pc_name}` | `POST /api/v1/devices/{pc_name}/bypass-code` | |
| `GET /api/get_concurrent_limit` | `GET /api/v1/settings/task-concurrency` | |
| `POST /api/set_concurrent_limit` `{limit}` | `PUT /api/v1/settings/task-concurrency` `{limit}` | |
| `POST /api/set_auto_enroll` `{target_lab, expire_date}` | `PUT /api/v1/settings/auto-enroll` `{target_lab, expire_date}` | |
| `POST /api/deploy_orchestration` | `POST /api/v1/tasks` | Same body; use `task_sequence`. |
| `POST /api/flush_queue` | `DELETE /api/v1/tasks` | Deletes every task record (audit-logged first). |
| `POST /api/upload` | `POST /api/v1/files` | Multipart `file`. See the upload size note under [Versioning](#versioning). |
| `POST /api/add_package` | `POST /api/v1/packages` | Insert or update by `id`. |
| `POST /api/delete_package` `{id}` | `DELETE /api/v1/packages/{package_id}` | |
| `POST /api/agent_policies` | `PUT /api/v1/agent_policies` | |
| `POST /api/scheduled_tasks/{task_id}/toggle` `{enabled}` | `PATCH /api/v1/scheduled_tasks/{task_id}` `{enabled}` | |
| `POST /api/licenses/{license_id}` | `PUT /api/v1/licenses/{license_id}` | |
| `POST /api/tickets/{ticket_id}/update` | `PATCH /api/v1/tickets/{ticket_id}` | |

Not renamed, on purpose:

- superadmin endpoints (`/api/system/...`, `/api/modules/{module_id}`, `/api/admin/users...`): panel operations that
  an API token cannot call;
- remote control and screen access (`/api/audit/session/*`, `/api/thumbnail/*`, `/api/remote_input`,
  `/api/stream/stop`): panel session only;
- the agent endpoints (`/api/auth/*`, `/api/inventory/{pc_name}`, `/api/logs/{pc_name}`, `/api/policy_alert`,
  `/api/software/{pc_name}`, `/api/patches/{pc_name}`, `/api/tickets/agent/*`, `/api/activity/agent/*`): the agent
  protocol does not change;
- actions on a named resource, which are already resource-style: `/api/tasks/action`, `/api/tasks/status`,
  `/api/patches/scan`, `/api/patches/install`, `/api/notifications/read`, `/api/notifications/clear`,
  `/api/scheduled_tasks/{task_id}/run`, `/api/tickets/{ticket_id}/messages`.

## WebSockets

The agent channels are specified message by message, with JSON Schemas, test vectors and the versioning rules, in
[`protocol/`](protocol/README.md). In short:

### `/ws/agent/{hw_id}` — agent command channel

- **Auth:** `X-Agent-Secret` matching the stored hash for `{hw_id}`, or a valid `X-Enroll-Token`. With neither, the
  connection is accepted while `enforce_agent_auth` is off (and the device has no secret); otherwise the rejection
  is written to `device_audit_logs` and the socket is closed with code `4401`.
- **Enrollment:** on a connection that used an enrollment token, the server creates a device secret, stores its
  SHA-256, counts one use of the token, moves the device to the token's lab and sends `set_secret`. If the device
  already has a secret, this is refused (critical audit entry, close `4401`) unless a superadmin allowed
  re-enrollment for it.
- **Messages:** the first message is a heartbeat with the hardware fingerprint; the server answers with
  `server_info` (protocol version and features) and then sends commands. Every message in both directions, the
  connection sequence and the close codes (`4401`, `4409`, `4000`, `1011`) are in [`protocol/`](protocol/README.md);
  the update stages an agent reports are described [below](#update_progress-agent-update-stages), the file transfer
  messages (`file_push`, `file_pull`, `file_result`) under [File transfer](#file-transfer), exam mode
  (`exam_mode`, `exam_state`) [further down](#exam_mode-and-exam_state).
- **Platform:** the agent's `X-Agent-Platform` header (`linux` from the Linux agent; or `platform` in the first
  message) is stored in `clients.platform`; without it the device is `windows`.
- **Not supported:** the Linux agent answers actions it does not have yet (`lockdown`, `unlock`, Vision,
  `scan_updates`, `install_updates`) with `capability_denied` and `reason: "not_supported"`. For `quarantine` the
  server then clears the device's quarantine flag and pending quarantine action, so the panel does not show it locked.
- When a registered connection closes, the reason (the close code in words, for example "bağlantı koptu" for
  `1006`) and the time are stored in `clients.last_disconnect_reason` / `last_disconnect_at`.

#### `update_progress`: agent update stages

Between `update_agent` and `update_result` the agent reports where the update stands. Agents up to 0.1.21 send no
stages; the panel then shows the update as before ("Kuruluyor" until the result).

```json
{"type": "update_progress", "stage": "waiting_installer", "to_version": "0.1.22-alpha", "attempt": 2, "of": 5}
```

| `stage` | Sent when | Panel |
| --- | --- | --- |
| `received` | The service accepted an `update_agent` command (no other update preparing, `update.lock` not fresh), before checking it. | Alındı |
| `downloaded` | The download of the MSI named in the signed manifest finished, before its size and SHA-256 are compared. | İndirildi |
| `verified` | Size and SHA-256 match the signed manifest. | Doğrulandı |
| `updater_started` | `POpsUpdater` was started and `update.lock` written. | Kurulum başladı |
| `waiting_installer` | `msiexec` returned 1618 (another Windows Installer job is running) and the updater waits before trying again. `attempt` = attempts made so far, `of` = attempts allowed (5). | Windows Installer meşgul, bekleniyor (2/5) |
| `installing` | The updater starts `msiexec` for the new package (again after each wait). | Kuruluyor |
| `rejected` | The service did not apply the command: no or bad manifest, bad signature, version not newer, more than one MSI, download failed, size or SHA-256 mismatch, updater files missing, or an error while preparing (for example the server could not be reached during the download). `detail` = the reason. Sent instead of an `update_result` (the updater's own refusal, a SHA-256 mismatch when it starts, still ends with an `update_result` with status `rejected`). | Reddedildi: \<reason\> |
| `ignored_busy` | A second `update_agent` came while an update was preparing or `update.lock` was fresh; the command was ignored; no `received` is sent for it. `to_version` = the version of the update in progress if known, else omitted. | Kuruluyor, "Önceki güncelleme sürüyor; bu gönderim yok sayıldı." |

- Like every message other than the heartbeat, it must not be the first message of a connection (the server
  registers the device from the first message's `dna_payload`).
- `type` and `stage` are required. Optional: `to_version` (the manifest version; `[0-9A-Za-z.+_-]`, at most 64
  characters), `attempt` and `of` (integers 1–100, `attempt` ≤ `of`), `detail` (text). An invalid optional value is
  dropped; the rest of the message still counts. The message carries **no `status` field**: servers handle any
  message with `status` as a heartbeat.
- The server keeps a stage only while an update it sent to this device is pending, and only when `to_version`
  (if given) is that update's version; `ignored_busy` is kept whatever its version. An unknown stage, a stage for
  a device without a pending update, or one for another version is ignored and logged at info level; the
  connection stays open.
- `detail` is cut to 300 characters; control characters become spaces, Unicode format characters (for example
  U+202E) are removed and runs of spaces collapse to one.
- The latest stage per device is kept in memory and in `pending_updates` (`stage`, `detail`, `attempt`,
  `attempt_of`, `stage_at`), so it survives a server restart. The same stage with the same `detail`, `attempt` and
  `of` sent again changes nothing, and `stage_at` stays the time of the first report: the agent may send its
  latest stage again after reconnecting.
- `rejected` ends the update. The server stores it like an `update_result` with status `rejected`,
  `to_version` (the pending version if the message has none), `from_version` (the connection's `X-Agent-Version`)
  and `detail` (the reason, or "ajan sebep bildirmedi"): audit entry `update_result`, critical event,
  notification `update_problem` ("Ajan … güncellemesini reddetti"), the pending update is closed and
  `update-progress` returns it as the `result`.
- An `update_result`, a device deletion or the 20-minute silence check (counted from the dispatch or the last
  stage, whichever is later) closes the pending update and clears its stage. A new dispatch clears it too.
- **Older servers** ignore the message. The agent loop in `Backend/pops/routers/agents.py` handles the types it
  knows (`thumbnail`, `vision_rejected`, `update_result`, `capabilities`, `bypass_secret_ack`,
  `capability_denied`, `exam_state`) and passes everything else to the routine handler, which acts only on `type: "result"`
  and on messages with a `status` field; anything else is dropped without an error, and the connection stays
  open. A server that handles `update_progress` lists it in `server_info.features`; an agent may skip the
  messages when the feature is missing, but does not have to.

#### `exam_mode` and `exam_state`

The server sends a lab's running exam to each of its agents when the exam starts, after every (re)connect and when
a connected PC is moved into the lab:

```json
{"action": "exam_mode", "enabled": true, "allow": ["sinav.meb.gov.tr", "10.0.0.5", "10.1.0.0/24"], "until": 1791207689, "message": "Sınav modu: yalnızca sınav sitesi açık", "block_apps": ["cmd.exe"]}
```

and `{"action": "exam_mode", "enabled": false}` when it ends (by an admin or at `until`), when the PC is moved out of
the lab, and on reconnect when the exam ended early while the PC was off. A new `exam_mode` replaces the previous
one. `until` is unix seconds and always set by this server (the contract allows `null` for "no end"); `allow` holds
normalised domain names, IP addresses and CIDR networks; `block_apps` lower-case file names (may be empty).

The agent reports its state, on connect and whenever it changes:

```json
{"type": "exam_state", "enabled": true, "since": 1791205289, "until": 1791207689}
```

- `enabled` is required (anything else is ignored); `since` and `until` are unix seconds, kept only when they lie
  between 2000 and 2100.
- `enabled: true` while the lab has no running exam (it ended, the PC was moved, the module was switched off): the
  server sends `enabled: false` again, at most once a minute per PC. `enabled: true` with an `until` more than 5
  seconds off the running exam's: the exam is sent again (same limit).
- `enabled: false` while the lab's exam runs, more than 20 seconds after the last `exam_mode` sent to the PC and
  more than a minute before `until`: the PC **left early** (exam mode switched off locally, or tampering). The
  first time per exam and PC this writes `left_at`, the audit entry `exam_left` and the notification `exam_left`
  (high). A report within the 20 seconds is the agent's state before it applied the exam and is not counted.
- The capability `exam` can be switched off on the PC. The agent then answers `exam_mode` with
  `{"type": "capability_denied", "capability": "exam", "action": "exam_mode"}`: logged and notified like every
  refusal, and the PC's state becomes `denied`.
- Agents without exam mode ignore `exam_mode` (unknown action) and send no `exam_state`; the PC shows as
  `unsupported`. The server announces the feature as `exam_mode` in `server_info.features`.

### `/ws/vision/{hw_id}` — agent screen stream

- **Auth:** only the device's `X-Agent-Secret` (an enrollment token is not accepted here, whatever
  `enforce_agent_auth` says); otherwise close `4401`.
- The agent sends `stream_frame` (and `thumbnail`) messages. Each frame is forwarded **only** to the panel sockets
  of admins who hold an open, unexpired remote-control session for that device; with no such panel the frame is
  dropped. Remote mouse/keyboard input from the panel reaches the agent over this socket when it is open. Message
  formats: [`protocol/`](protocol/README.md).

### `/ws/panel` — dashboard

- **Auth:** the `pops_jwt` cookie only (the `Authorization` header and query-string tokens are not accepted).
  An invalid or revoked session is closed with code `4001`.
- **Panel → server:** `{"type": "ping"}` (answered with `pong`) and `{"type": "remote_input", "device": ..., ...}`.
  Remote input is ignored for `viewer`. Real mouse/keyboard input (`input_type` set) and `action: "execute"`
  additionally require an open remote-control session for that device. The user's session is re-checked against
  the database every 10 seconds; a revoked session closes the socket (`4001`) and drops its screen and control
  grants.
- **Delivery:** each panel socket has its own send queue. Screen frames and previews keep only the newest one per
  device (a slow panel skips frames); a panel whose queue grows past 500 messages, or whose send takes longer than
  10 seconds, is closed with `1013` and the browser reconnects.
- **Server → panel:** `terminal_output` (task results), `update_result`, `capabilities`, `capability_denied`,
  `vision_rejected`, `ticket_new` (a ticket opened by an agent); `file_transfer` (`{pc_name, transfer_id, direction,
  status}` when a transfer is downloaded, uploaded or answered) goes to admin/superadmin panels only; `thumbnail` replies go to admin/superadmin panels only; live `stream_frame`s go only to the
  session holder (see above).

## Automation

1. **Create a token** (superadmin): **Ayarlar → Güvenlik → API jetonları → Jeton oluştur**. Give it a name that says
   what uses it (it appears in the logs as `token:<name>`), pick **Görüntüleyici** for read-only use (inventory
   exports, dashboards, monitoring) or **Yönetici** for jobs that change things (queueing commands, moving PCs,
   quarantine), and a validity in days (empty: no expiry). Copy the token from the dialog: it is not shown again.
   The same can be done with `POST /api/v1/tokens` from a superadmin's panel session or login JWT.
2. **Call `/api/v1`** with `Authorization: Bearer <token>`. No cookie, no `X-Requested-With`.
3. **Revoke** the token when the script is retired or the token may have leaked (trash icon in the list, or
   `DELETE /api/v1/tokens/{id}`). It stops working at once.

Keep the token in a secret store or an environment variable, not in the script or its repository.

```bash
export POPS=https://pops.example.com
export POPS_TOKEN=pops_...            # from Ayarlar → Güvenlik → API jetonları

# Devices (viewer or admin token)
curl -s "$POPS/api/v1/devices" -H "Authorization: Bearer $POPS_TOKEN"

# Queue a command on one PC (admin token); the task is recorded as created by token:<name>
curl -s "$POPS/api/v1/tasks" -H "Authorization: Bearer $POPS_TOKEN" -H 'Content-Type: application/json' \
  -d '{"target_mode": "PC", "targets": ["HW-..."], "source": "api", "reason": "weekly disk check",
       "task_sequence": [{"name": "Disk", "type": "CMD", "command": "wmic logicaldisk get size,freespace"}]}'

# Follow the returned task_ids
curl -s "$POPS/api/v1/tasks?limit=50" -H "Authorization: Bearer $POPS_TOKEN"

# Move two PCs into the lab "9/A", then make one of them its main PC (the slash stays a slash in the path)
curl -s "$POPS/api/v1/devices/move" -H "Authorization: Bearer $POPS_TOKEN" -H 'Content-Type: application/json' \
  -d '{"pc_names": ["HW-...", "HW-..."], "new_lab": "9/A"}'
curl -s -X PUT "$POPS/api/v1/labs/9/A/main-pc" -H "Authorization: Bearer $POPS_TOKEN" \
  -H 'Content-Type: application/json' -d '{"pc_name": "HW-..."}'
```

A `401` means the token is unknown, expired or revoked; a `403` means its role does not allow the request (a viewer
token sending anything but `GET`, or an endpoint tokens cannot use).

## Example

```bash
# Log in (an account without 2FA) and list devices
TOKEN=$(curl -s https://pops.example.com/api/admin/login \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"<password>"}' | python3 -c 'import sys,json; print(json.load(sys.stdin)["token"])')

curl -s https://pops.example.com/api/v1/devices -H "Authorization: Bearer $TOKEN"

# Unauthenticated health check (on the server itself)
curl -s http://127.0.0.1:8000/api/health
```

Error responses use FastAPI's format, `{"detail": "..."}`. Most messages are in Turkish.
