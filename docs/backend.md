# Backend Documentation

The POps Backend is built with Python 3.10+ (target 3.12) and FastAPI. It exposes a standard REST API for the dashboard and runs a highly-concurrent WebSocket server for real-time agent connections.

## Code layout

`Backend/server.py` only builds the FastAPI app (rate limiter, CORS, static mounts, startup/shutdown with the
database pool, migrations and the scheduler loop) and wires the routers. Everything else lives in the `Backend/pops/` package:

| Module | Responsibility |
|---|---|
| `pops/config.py` | Environment/config constants (`.env`, JWT, database connection and pool size, paths, Wake-on-LAN, SMTP, `NOTIFY_WEBHOOK_ALLOW_PRIVATE`). No dependency on other `pops` modules. |
| `pops/db.py` | PostgreSQL pool and `execute_query`. The pool is created at startup; other modules reach it as `db.db_pool` (never `from pops.db import db_pool`, which would bind `None` at import time). |
| `pops/security.py` | Panel auth: JWT with `token_version` revocation, role dependencies, CSRF, TOTP 2FA, rate limiter. |
| `pops/agent_auth.py` | Agent auth: enrollment tokens, per-device secrets, identity-to-target binding on agent HTTP endpoints. |
| `pops/audit.py` | `agent_logs_v2` event log and the agent-unwritable, hash-chained `device_audit_logs`. |
| `pops/manager.py` | WebSocket connection manager (agents, panels, vision) and time-limited remote-control session grants. |
| `pops/models.py` | Pydantic request models. |
| `pops/devicelist.py` | The device list version behind `GET /api/devices` (ETag, `?since=` changes) and the `devices_changed` push to panel sockets; see [Device list version](#device-list-version). |
| `pops/taskqueue.py`, `pops/dna.py`, `pops/wol.py` | Task queue dispatch and target resolution, hardware-DNA identity reconciliation, Wake-on-LAN. |
| `pops/notify.py` | Notifications: the `notifications` table behind the panel's **Bildirimler**, optional e-mail (SMTP from `.env`) and webhook delivery in the background, dedupe and send cap. The webhook target is resolved and must be a public address (unless `NOTIFY_WEBHOOK_ALLOW_PRIVATE`); the connection is pinned to the checked address and redirects are not followed. |
| `pops/scheduler.py` | Background loop started at startup (every 30 s): queues due scheduled tasks (one process at a time, advisory lock), alerts on agents that never answered an update, and once a day (`license_check_date`) raises notifications for licences that are over their seats, expired or ending within 30 days. |
| `pops/routers/*.py` | Endpoint groups, one `APIRouter` each: `auth` (login, 2FA, users), `control` (audit sessions, lockdown, bypass codes, panel/vision WebSockets, preview, remote input), `agents` (`/ws/agent` and the agent HTTP endpoints), `devices` (devices, labs, inventory, logs, WoL), `tasks` (queue, orchestration, packages, storage), `schedules` (scheduled tasks), `notifications` (the notification list, channel settings, test), `inventory` (software inventory and Windows update status), `reports` (summary and CSV export), `licenses` (licence definitions counted against the software inventory), `helpdesk` (tickets from the panel and from enrolled agents, with per-device limits). |

`Backend/system_routes.py` (version and update checks, release notes from the GitHub `CHANGELOG.md`, signed
releases from GitHub or upload, enrollment tokens, agent deploy, server self-update, capabilities) is a separate
router built with injected dependencies. Agent updates
are signed only; the old unsigned-zip endpoints (`/api/upload_update`, `/api/update_agent/…`, `/api/broadcast_update`)
were removed together with the old `update.php` page.

## Dependencies

`Backend/requirements.txt` pins the direct dependencies (and a few indirect ones that carry security fixes) with
`==` and holds the `# requires-python: >=3.10` line the installer and the deploy script read; Dependabot updates it.
`Backend/requirements.lock` is generated from it: every package the backend installs, indirect ones included,
pinned with the SHA-256 hashes of its files, in one file that is valid for Python 3.10 and newer on every platform
(uv's universal resolution). `install.sh`, `pops-deploy-backend`, the Docker image and CI install with
`pip install --require-hashes -r Backend/requirements.lock`, so pip refuses a file from the package index whose hash
differs from the lock, before it installs anything. A checkout without the lock (older releases) falls back to
`requirements.txt`.

Regenerate the lock whenever `requirements.txt` changes, and commit both:

```bash
pip install uv==0.12.23            # the version CI uses; another version may format the file differently
tools/backend_lock.sh              # keeps every other pin; only what requirements.txt needs changes
tools/backend_lock.sh --upgrade    # also moves indirect dependencies to their newest allowed versions
tools/backend_lock.sh --check      # what CI's "Backend lock file" job runs
pip-audit -r Backend/requirements.lock    # with Python 3.10 or newer
```

Indirect dependencies change only in the lock and Dependabot does not see them: run `--upgrade` from time to time
and whenever `pip-audit` reports one of them.

**Dependabot PRs.** `.github/workflows/lock-refresh.yml` regenerates the lock on a Dependabot PR that changes
`requirements.txt` and pushes it to the PR branch (the only job with write access; it runs only for Dependabot's own
branches). A push made with the workflow token does not start another workflow run, so CI does not run on the new
commit by itself: close and reopen the PR, then review the green run. Until then the Dependabot commit shows a
failing "Backend lock file" check, which is expected. If the job cannot push (for example when branch rules require
signed commits), regenerate the lock locally and push it to the Dependabot branch.

Lint: `flake8 Backend/ tools/ assets/readme/` must stay clean (config in the repo-root `.flake8`, max line length 120); CI enforces it.

The endpoint list is in [`api.md`](api.md) and the schema in [`database.md`](database.md).

## Tests

`Backend/tests/` holds integration tests that run against a live backend and an empty PostgreSQL database. CI's
`security` job runs them in this order: `test_security.py`, `test_2fa.py`, `test_agent_authz.py`,
`test_remote_authz.py`, `test_f4_accountability.py`, `test_features.py`, `test_helpdesk_licenses.py`, `test_ops.py`,
`test_api_tokens.py`, `test_devices_delta.py`.
`test_units.py` and `test_protocol.py` (the agent protocol in [`protocol/`](protocol/README.md); it needs
`pip install jsonschema==4.26.0`, which is not a runtime dependency) need no server. `Backend/tests/run_local.sh`
does the same locally: it applies the migrations, starts a temporary backend on `127.0.0.1:8099` and runs the
scripts (`COVERAGE=1` adds a coverage report). What is and is not covered: [`testing.md`](testing.md). Export
`DB_HOST`, `DB_PORT`, `DB_USER`, `DB_PASS`, `DB_NAME` (an empty test database) and `JWT_SECRET` first; never point it
at a production database.

## Device list version

The panel used to fetch the whole device list every 5 seconds per open tab (1.39 MB for 2,040 devices). Now the
server keeps a version of the list and the panel fetches only changes (`Backend/pops/devicelist.py`; the HTTP side
is in [`api.md`](api.md#device-list-etag-changes-and-push), measurements in
[`BENCHMARKS.md`](../BENCHMARKS.md)).

- **Snapshot and version.** At startup the server reads every row of the list once and keeps it in memory with a
  version number, which starts at the start time in milliseconds. One round at a time (at most one per second, under
  a lock) re-reads rows from the database and compares them with the snapshot, ignoring `last_seen`. If a row
  differs, appeared or disappeared, the version grows by one for that round. Nothing else bumps it.
- **Who marks rows.** Code that writes a device row marks it: hot paths (`heartbeats.flush`, an agent connecting,
  disconnecting, reporting capabilities, quarantine state, update results, user login and logout) call
  `devicelist.touch(pcs)`, and the next round reads those rows in one query; panel actions (rename, move, lab rename
  or delete, device delete, quarantine, capability policy) call `await devicelist.sync(pcs)`, which re-reads the
  rows before the response is sent, so the panel's next `?since=` already sees the change. The heartbeat flush marks
  only devices whose status, foreground app, address, name or health summary differ from the snapshot, so a
  heartbeat that only moves `last_seen` costs no query.
- **Minute scan.** Every 60 seconds the whole list is read and compared. It catches writes that nobody marked (new
  code that forgot to, SQL by hand) and publishes `last_seen`: rows whose only change is `last_seen` get the new
  version as a `seen` entry. The scan reads about 2,000 rows once a minute.
- **Change log.** For each device the log keeps only its latest change (version, time, deleted or not), in version
  order, bounded to 5,000 devices and 10 minutes. An entry that falls out raises the log's floor; a `since` below
  the floor gets the whole list (`full: true`). `seen` uses a per-device version instead, so it needs no bound.
- **Push.** After a round that changed the version, the loop sends `{"type": "devices_changed", "version": n}`
  through `manager.broadcast_to_panels`; rounds are at least a second apart, so this goes out at most once a second.
  Only panel sockets opened with `?topics=devices` get it, and they get nothing else (`manager.PANEL_TOPICS`);
  sockets without `topics` keep receiving exactly the messages they did before.
- **Limits.** The version, snapshot and log are in the one uvicorn process (like the rest of the connection state;
  see [`decisions.md`](decisions.md)). A failed round is logged (`cihaz listesi turu başarısız`) and its rows are
  read again in the next round.
- **Metrics:** `pops_events_total{event="device_list_versions"}` counts new versions;
  `device_list_304`, `device_list_delta` and `device_list_full` count `/api/devices` answers by kind.

## Logs, metrics and diagnostics

- **Logs** are one JSON object per line on stderr (journald under systemd): `ts`, `level`, `logger`, `msg`, the
  request's `request_id` and any extra fields. `LOG_FORMAT=text` gives readable lines for development;
  `LOG_LEVEL` sets the level (default `INFO`). uvicorn's own lines (access log included) use the same format.
  Find one request: `journalctl -u <service> | grep '"request_id": "<id>"'`.
  A background thread writes the lines, so a slow destination (stalled disk, journald, Docker log pipe) never
  holds up requests or agent connections. If 10 000 lines are waiting, new ones are dropped and a
  `log yazımı yetişemedi` warning with the `dropped` count follows.
- **Request ID:** every HTTP request and WebSocket connection gets one; it is returned in the `X-Request-ID` response
  header and appears on every log line written while handling it. A safe incoming `X-Request-ID` (8–64 characters of
  `A-Z a-z 0-9 . _ -`) is kept, so a reverse proxy can pass its own.
- **`/metrics`** (Prometheus text format) is off unless `METRICS_TOKEN` (at least 16 characters) is set, and then
  needs `Authorization: Bearer <token>`. It exposes request counts and durations per route template (never the raw
  path, so device names do not leak into labels), WebSocket sessions, unhandled errors, WARNING/ERROR log counts,
  connected agents and panels, device counts, database pool usage, the scheduler's last tick and memory use.
  The endpoint is not under `/api`, so the panel's reverse proxy does not expose it; scrape it on `127.0.0.1`.
- **Load figures** (both in `/metrics` and diagnostics): heartbeats received, queries per heartbeat, heartbeat rows
  written in batches, database reads and writes (writes per second over the last minute), the time from queueing a
  task to sending it (`pops_task_dispatch_seconds`) and the time to write a command to the agent socket
  (`pops_command_send_seconds`). These are the numbers to watch when a server grows.
- **Diagnostics** (`GET /api/system/diagnostics`, superadmin) returns the same health figures plus the last 50
  errors (with request IDs), the slowest routes, the load figures as `load` (95th percentiles from the histogram
  buckets) and the last disk (`disk`) and certificate (`tls`) checks; the panel shows them on the **Sistem** page. The error list lives in
  memory and resets when the backend restarts; journald keeps the full history.
- **History** (`GET /api/system/overview?span=24h|7d|30d`, superadmin) feeds the charts on **Sistem → Genel bakış**.
  The scheduler writes one row a minute to `server_metrics` (connected agents and panels, processor, memory and disk
  use in percent, database size and process memory in MB, API requests and 5xx responses in that minute) and
  deletes rows older than 30 days (`pops/server_metrics.py`). The endpoint returns these samples averaged into
  equal steps (15 minutes, 2 hours or 6 hours; `null` where there was no sample), task results and events by risk
  level per hour, 6 hours or day of the server's local time, and the agent update results of the period.
