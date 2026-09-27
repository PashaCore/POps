# Backend Documentation

The POps Backend is built with Python 3.9+ and FastAPI. It exposes a standard REST API for the dashboard and runs a highly-concurrent WebSocket server for real-time agent connections.

## Code layout

`Backend/server.py` only builds the FastAPI app (rate limiter, CORS, static mounts, startup/shutdown with the
database pool and migrations) and wires the routers. Everything else lives in the `Backend/pops/` package:

| Module | Responsibility |
|---|---|
| `pops/config.py` | Environment/config constants (`.env`, JWT, paths, Wake-on-LAN). No dependency on other `pops` modules. |
| `pops/db.py` | PostgreSQL pool and `execute_query`. The pool is created at startup; other modules reach it as `db.db_pool` (never `from pops.db import db_pool`, which would bind `None` at import time). |
| `pops/security.py` | Panel auth: JWT with `token_version` revocation, role dependencies, CSRF, TOTP 2FA, rate limiter. |
| `pops/agent_auth.py` | Agent auth: enrollment tokens, per-device secrets, identity-to-target binding on agent HTTP endpoints. |
| `pops/audit.py` | `agent_logs_v2` event log and the agent-unwritable, hash-chained `device_audit_logs`. |
| `pops/manager.py` | WebSocket connection manager (agents, panels, vision) and time-limited remote-control session grants. |
| `pops/models.py` | Pydantic request models. |
| `pops/taskqueue.py`, `pops/dna.py`, `pops/wol.py` | Task queue dispatch, hardware-DNA identity reconciliation, Wake-on-LAN. |
| `pops/routers/*.py` | Endpoint groups, one `APIRouter` each: `auth` (login, 2FA, users), `control` (audit sessions, lockdown, bypass codes, panel/vision WebSockets, preview, remote input), `agents` (`/ws/agent` and the agent HTTP endpoints), `devices` (devices, labs, inventory, logs, WoL), `tasks` (queue, orchestration, packages, storage), `updates` (legacy unsigned-zip update endpoints kept for compatibility). |

`Backend/system_routes.py` (version, signed releases, enrollment tokens, deploy/self-update, capabilities) is a
separate router built with injected dependencies.

Lint: `flake8 Backend/` must stay clean (config in the repo-root `.flake8`, max line length 120); CI enforces it.
