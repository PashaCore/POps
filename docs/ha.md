# Several backend workers (Redis)

By default the backend is one uvicorn process: it holds every agent, panel and Vision WebSocket and routes between
them in memory ([`decisions.md`](decisions.md) D-01). One process is enough for a whole district (see the
[capacity report](kapasite/README.md)). This page describes the optional mode for more than one process: several
uvicorn workers on one server, or several servers behind a load balancer, sharing one PostgreSQL database and one
Redis. It is **off unless `REDIS_URL` is set**; without it nothing on this page applies and the backend behaves
exactly as before. Why it is built this way: D-26.

## Turning it on

1. Run Redis 6 or newer where every backend process can reach it (Redis on the database server is fine). Keep it
   off the public network; set a password and put it in the URL if it is not on loopback.
2. Set in the backend's `.env` on every server:

   ```ini
   REDIS_URL=redis://127.0.0.1:6379/0     # or redis://:password@redis.okul.local:6379/0, rediss:// for TLS
   REDIS_PREFIX=pops                      # optional; different per installation if they share one Redis
   ```

3. Start several workers. On one server, add `--workers 4` (for example) to the `uvicorn` line of the systemd unit.
   On several servers, run the same backend version on each and put them behind the load balancer.
4. `GET /api/system/diagnostics` (superadmin) then returns a `cluster` block: this worker, whether Redis is
   reachable, whether this worker runs the periodic jobs, and every live worker with its agent and panel counts.
   The connected-agent counts on **Sistem → Sağlık ve yedek** and in `/metrics` are cluster-wide.

The `redis` Python package is in `Backend/requirements.txt`; nothing else is installed.

## Topology

```
Windows PCs, browsers ──https/wss──▶ nginx (TLS) ──▶ worker 1 ─┐
                                                 ├──▶ worker 2 ─┼──▶ PostgreSQL (shared state of record)
                                                 └──▶ worker N ─┼──▶ Redis (routing + shared counters)
```

Any worker can hold any agent socket, any panel socket and any Vision tunnel. The load balancer does **not** need
sticky sessions (`ip_hash` or cookies): everything that has to reach a socket on another worker goes through Redis.
A plain round-robin upstream works:

```nginx
upstream pops_backend {
    server 127.0.0.1:8000;          # one entry per server; uvicorn --workers N shares one port per server
    server 10.0.0.12:8000;
}
# proxy_pass http://pops_backend; in the /api/, /ws/, /updates/ and /download/ locations, as in
# Installer/server/nginx.example.conf (keep the WebSocket Upgrade headers and the long read timeout)
```

With **several servers**, the folders the backend writes must be the same on all of them: `storage/` (uploaded
deployment packages), `updates/` (the agent packages being dispatched), `releases/` (staged signed releases) and
`transfers/` (`POPS_FILES_DIR`, file transfers). Mount them from shared storage, or keep all workers on one server.
Self-update (`POPS_SELFUPDATE_DIR`) runs per server.

## What goes through Redis

Each worker has one subscription connection.

| Channel | Who listens | Carries |
| --- | --- | --- |
| `<prefix>:ch:agent:<device>` | the worker holding that agent's socket | commands for the agent; `evict` when the device reconnected to another worker; `close` when it was deleted |
| `<prefix>:ch:vision:<device>` | the worker holding that device's Vision tunnel | remote input, viewer commands (`select_monitor`, `set_quality`) and clipboard text from a panel on another worker; `evict` when a new tunnel opened elsewhere |
| `<prefix>:ch:user:<user>` | workers where that user has a panel open | screen frames (JSON, and Vision v2 binary frames base64-encoded) and the session holder's Vision messages (`monitors`, `clipboard`) |
| `<prefix>:ch:events` | every worker | panel broadcasts (task output, update results, capabilities, tickets, file transfers), admin broadcasts (screen previews), session grant and Vision tunnel changes, device list changes |

Frames keep the F12 rule across workers: the worker that receives a frame from the Vision tunnel looks up who holds
an open session for that device and publishes the frame only to those users' channels; the worker holding the
panel checks the session and the admin role (and, for binary frames, that the panel announced `vision_binary`)
again before it queues the frame. Frames are never broadcast to all workers or all panels. The clipboard rule
holds too: the clipboard belongs to the owner of the "ask the user" session that opened the tunnel, which the
tunnel's worker decides and shares with the others.

Organisational-unit scopes ([`decisions.md`](decisions.md) D-25) hold across workers: every device broadcast
carries its device, and the worker that holds a panel applies that panel's scope before it queues the message, as
on one process. The device list's `devices_changed` goes to each worker's own panels from its own copy of the list,
with the same per-scope rule.

A command to an agent on another worker counts as delivered when that worker received it; if its socket closed in
that moment the command is lost, as it would be on one process.

## Shared state

| State | Where | Notes |
| --- | --- | --- |
| Online agents | Redis hash `<prefix>:agents` (device → worker) | Written when an agent registers, removed when its socket closes, unless the device has already registered on another worker. |
| Worker liveness | Redis `<prefix>:worker:<id>`, 15 s TTL, renewed every 5 s, with the worker's agent and panel counts | When a worker's key expires (crash, power loss), another worker removes its agents from the registry and marks those devices Offline. |
| Remote-control session grants | Redis hash `<prefix>:vision` (device, user → expiry, start time, mandatory) | Each worker keeps a copy in memory, updated by events and re-read every 10 s. Changes are written in order in the background; expired grants still end on their own (fail-closed). |
| Vision tunnels | Redis hash `<prefix>:tunnels` (device → worker, clipboard owner, monitor list) | Lets a worker without the tunnel answer "is the screen open", start a session with the current monitor list and check the clipboard rule. |
| Device list version and change log | Redis counter `<prefix>:devlist:ver`; changes on `ch:events` | Every worker keeps the list, the log and the version in memory as before, but versions come from the shared counter and the worker that finds a change publishes the changed rows, so `ETag` and `?since=` mean the same on every worker. The minute's full scan runs on one worker. While Redis is unreachable, `ETag` and `?since=` are off (full list). |
| Updates awaiting a result and their stages | PostgreSQL `pending_updates` (already there) | Each worker's memory is a cache; it is re-read from the table before it is used. |
| Notification dedupe and send limit | Redis (`SET NX` with TTL; a sorted set) | One notification per event per 10 minutes and 30 sends per 10 minutes for the whole system, not per worker. |
| Rate limits (login, 2FA) | Redis through slowapi's storage | 10 per minute per address counts across workers. |
| Task queue | PostgreSQL advisory lock around each dispatch round | Two workers never send the same task, and the concurrency limit holds across workers. |
| Periodic jobs | One "leader" worker holding a PostgreSQL advisory lock on its own connection | Scheduled tasks, stuck-task timeouts, silent-update alerts, licence, retention, disk and certificate checks and server metrics run once, not once per worker. If the leader stops, its connection closes and another worker takes over within 30 s. |
| Device status, tasks, audit, everything else | PostgreSQL | As on one process. |

## Process-local state

Classification of what each worker keeps in its own memory, starting with `pops/manager.py`.

| `pops/manager.py` | Kind | With several workers |
| --- | --- | --- |
| `active_agents` | sockets | This worker's agent sockets. Other workers find them in the Redis registry and reach them through the device channel. |
| `active_panels`, `panel_users`, `panel_roles`, `panel_senders` | sockets | This worker's panel sockets, their user and role (re-checked every 10 s) and send queues. Broadcasts reach them through `ch:events`, frames through the user's channel. |
| `active_vision_ws` | sockets | This worker's Vision tunnels; input from other workers arrives on the device's Vision channel. |
| `panel_binary`, `panel_topics`, `panel_scopes` | sockets | Per panel socket: can take binary frames (`panel_hello`), the `?topics=` filter, the organisational-unit scope (refreshed every 10 s). Applied by the worker holding the panel. |
| `vision_sessions`, `vision_session_modes` | shared state | A copy of the Redis grants with their start time and mandatory flag (see above). |
| `vision_clipboard_owner`, `vision_monitors` | per tunnel | Kept by the tunnel's worker and shared through `<prefix>:tunnels`; `remote_tunnels` is the copy of the other workers' tunnels. |
| `pending_thumbnails` | waiting requests | Futures of `GET /api/thumbnail` on this worker. A preview from an agent on another worker arrives as an admin broadcast and resolves them. |
| `pending_updates`, `update_stages` | shared state | A cache of the `pending_updates` table, refreshed before reads. |
| `_tasks`, `_chains` | housekeeping | Deliveries received from Redis that are being written to sockets, in arrival order per socket. |

Elsewhere:

| State | With several workers |
| --- | --- |
| `heartbeats._pending` | Per worker: each worker batches and writes its own agents' heartbeats. |
| `metrics.*`, `logs.recent_errors`, `logs.level_counts` | Per worker. Prometheus `/metrics` and the diagnostics page show the worker that answered; agent and panel counts and `pops_cluster_workers` are cluster-wide. |
| `scheduler.last_tick` | Per worker; on a worker that is not the leader it shows that its loop is alive. |
| `health_alerts.last`, `retention`, `server_metrics` state | Only the leader runs these checks; another worker's diagnostics page shows empty disk and certificate results until it becomes the leader. |
| `routers/agents.py`: `_quarantine_resent`, `_isolation_warned`, `_reject_seen`; `dna._mismatch_seen` | Per worker. Throttles for resending a lock and for thinning repeated audit rows; after an agent moves to another worker a lock can be resent or an audit row written one extra time. |
| `routers/helpdesk.py`, `routers/activity.py`: `_agent_last` | Per worker request throttles for agents, so an agent spread over N workers could send up to N times as often. |
| `routers/tasks.py`: `_recent_orchestrations` | Per worker. A double-clicked task submission whose two requests land on two workers creates the tasks twice. |
| `routers/files.py`: `_agent_hits`; `routers/control.py`: `_clipboard_times` | Per worker rate limits (file transfer endpoints for agents, clipboard per device and minute). |
| `filestore._last_purge`, exam expiry | Run by the leader's periodic jobs. |
| `devicelist` copy, log and version | See Shared state: kept per worker, versions and changes shared. |
| `peer_cache` (`_announce`, `_rollouts`) | Per worker, so with several workers the update peer cache does not stage: every PC gets the update directly, as with the peer cache off. |
| `sso_oidc._cache` | Per worker cache of the identity provider's discovery document and keys. Sign-in flows themselves are in PostgreSQL (`sso_flows`), so the callback may land on any worker. |
| GLPI sync | Started by the leader's periodic jobs. |
| `modules._cache` | Per worker, 5 s: a module switched on or off applies on the other workers within 5 s. |
| `tenancy._cache` | Per worker, 5 s: a change to organisational units or a lab's unit applies on the other workers within 5 s; a user's own scope is read from the database on every request. |
| `routers/system/` GitHub and changelog caches, `_fetch_state` | Per worker; two workers could download the same release at the same time (staging is idempotent). |

## When something fails

| Failure | What happens |
| --- | --- |
| **Redis down or unreachable** | Every worker keeps serving its own sockets: commands to agents on the same worker, broadcasts to its own panels, heartbeats, tasks for its own agents and rate limits (in memory) keep working. Commands and frames for sockets on other workers are dropped and the API reports the agent as not reached (`delivered: false`). The device list answers with full lists (no `ETag` or `?since=`). The worker logs a warning (again every 30 s) and reconnects in the background (0.5 s to 5 s between tries). Session grant changes made meanwhile are queued and written when Redis is back. |
| **Redis back** | Workers resubscribe, write their own agents and tunnels to Redis again, remove entries for sockets that closed meanwhile, apply the queued grant changes and rescan the device list. If Redis lost its data (restarted without persistence), open remote-control sessions end and have to be started again; agents reappear in the registry from their workers. |
| **A worker crashes** | Its sockets close; the agents reconnect (with back-off) through the load balancer to any worker. Within 15 s its liveness key expires and another worker removes its registry entries and marks those devices Offline until they are back. If it was the leader, another worker takes the periodic jobs within 30 s. |
| **A worker is restarted** | As on one process: it closes its sockets, marks its agents Offline and releases its registry entries before it exits. The agents reconnect and spread over the workers the load balancer offers at that moment. |
| **PostgreSQL down** | As on one process: requests fail and the backend retries. |

Agents do not move back on their own: after a worker restart the others keep the extra agents until they reconnect
for another reason. The load check below shows a typical spread.

## What is not highly available

- **PostgreSQL** is the system of record and a single point of failure in this setup. For a standby use
  PostgreSQL streaming replication with a failover manager (for example Patroni or repmgr) or a managed database,
  and point `DB_HOST` at the address that follows the primary. POps needs nothing special for this; the migrations
  run on the primary at startup.
- **Redis** is one instance. Losing it degrades the system to "every worker on its own" (above) rather than
  stopping it. Redis Sentinel or a managed Redis can make it highly available; give the backend the address that
  follows the primary.
- **Files** in `storage/`, `updates/`, `releases/` and `transfers/` with several servers (see Topology).

## Tests and measurements

- `Backend/tests/test_ha.py` starts two workers against one database and one Redis and checks commands, broadcasts,
  frames (JSON and Vision v2 binary), monitor lists, viewer commands and the clipboard in both directions, task
  results, update stages, organisational-unit scopes on broadcasts, the device list version (`ETag`, `?since=`,
  `devices_changed`), rate limits and the
  takeover of an agent across workers, then cuts Redis and checks that a worker keeps running on its own and
  recovers. CI runs it in the `ha` job; the other jobs stay
  single-process. How to run it locally: [`testing.md`](testing.md).
- Load check (2026-10-05, two workers on one 8-core host shared with other services, Redis 7, agents from
  `tools/agent_simulator.py --url ws://…:9997,ws://…:9998 --reconnect`, a heartbeat every 10 s): 1,000 agents
  connected and split 500 / 500; the registry, the diagnostics endpoint and the database all counted 1,000. Worker A
  was then restarted (stopped in 0.8 s, answering again after 4.1 s): its 500 agents were all back within 30 s, 299
  of them on B and 201 on A again, ending at 201 / 799, with no error in either worker's log. Each worker used
  100–140 MB of memory and 7–20 % of one core in steady state (up to about 30 % while agents reconnected).
