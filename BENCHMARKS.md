# POps — Benchmarks

Measured numbers instead of a "thousands of devices" claim. Reproduce with
[`tools/agent_simulator.py`](tools/agent_simulator.py) on your own hardware; these are a
first honest baseline, not a marketing figure.

## Update 2026-09-29: restart storm with real reconnect behaviour, and the bottleneck it found

Scenario: the server restarts and every agent reconnects at the same moment (`--ramp 0`), using the agent's
real reconnect logic from 0.1.8 (`--reconnect`: full-jitter backoff, random 0 – min(60 s, 2 s × 2^n)). Devices
already known to the server (a warm-up run registered 2000 first). One uvicorn worker, database pool 20,
PostgreSQL 13 shared with other sites, 8 vCPU / 11.7 GB, simulator on the same host. Heartbeat every 5 s,
20 s measurement window after everyone connected.

| Agents | All connected after | Failed attempts | Heartbeat writes/s | Server CPU (one core) before → after fix | Server RSS |
|---:|---:|---:|---:|---:|---:|
| 500  | 0.9 s | 0 | 90  | 1 % → 2 %      | 149 MB |
| 1000 | 1.9 s | 0 | 200 | **83 % → 5 %** | 228 MB |
| 2000 | 4.0 s | 0 | 371 | **84 % → 11 %** | 385 MB |

**What the first run found.** Every agent connection called the task queue, and the queue ran one database query
**per online device** each time. With N agents arriving together that is about N²/2 queries (≈ 2 million for
2000): the server stayed busy for minutes after the storm, and even after all clients had left, and a restart hung
in shutdown. This, not the single worker or the connection count, is what made the earlier 500-agent burst drop
connections. The queue now returns after one cheap query when nothing is pending, fetches the oldest pending task
of every idle online device in a single query, and coalesces concurrent calls into one extra pass
(`Backend/pops/taskqueue.py`).

**What it means.** On one worker, 2000 agents come back within about 4 seconds of a restart with no failed
attempt, and steady state costs about a tenth of one core. The single process is not the limit at district scale;
the remaining costs are one database write per heartbeat (batching `last_seen` would cut it ~6–12×) and the
identity reconciliation on every connect. Multiple workers with shared state (Redis) are about availability
(surviving a process crash), not capacity, and are not needed for these numbers. Not measured here: Vision frame
relay (limited to 5 fps per device and to open audit sessions), and agents on a real network with latency.

Reproduce: `python tools/agent_simulator.py --n 2000 --url ws://127.0.0.1:8099 --ramp 0 --reconnect --duration 20
--hb 5 --server-pid <uvicorn pid>` after one warm-up run (`ulimit -n 65536` for both processes).

## Method

`tools/agent_simulator.py` opens N WebSocket connections to `/ws/agent`, each sending a
`dna_payload` (device DNA, hardware inventory trigger, first-seen upsert) and then a
`status` heartbeat every 5 s — the same traffic a real agent produces. It reports how many
connected, wall time to connect, and the sustained heartbeat (DB-write) rate; alongside it
we sample the server process RSS and CPU.

- **Reproduce:** `python tools/agent_simulator.py --n 500 --url ws://<server> --duration 20`

## Setup under test

| | |
|---|---|
| Host | 8 vCPU, ~11.7 GB RAM (shared dev box, other sites also running) |
| Server | **single** `uvicorn` worker, `asyncpg` pool `min=5 max=100` |
| DB | PostgreSQL 13, localhost |
| Client | simulator connecting **all N within ~1–2 s** (synthetic thundering-herd) |

This is a deliberately un-tuned, single-worker baseline — the floor, not the ceiling.

## Results (first baseline, 2026-09-26)

| Agents (N) | Connected | Connect wall | Server RSS | Server CPU | Notes |
|-----------:|----------:|-------------:|-----------:|-----------:|-------|
| 100  | 100 / 100 | 0.3 s | ~85 MB  | ~0.7 core | clean |
| 500  | ~418 / 500 | 1.4 s | ~162 MB | ~0.6 core | ~90 dropped during the burst (reconnect) |
| 1000 | ~545 / 1000 | 2.8 s | ~295 MB | ~0.8 core | more dropped during the burst |

### What this says honestly

- **Memory scales well:** roughly **~0.3 MB of server RAM per connected agent** (~295 MB for ~550 live sockets). Memory is not the limit.
- **The limit is the connect-storm on one worker.** A single `uvicorn` worker with a
  100-connection DB pool accepts a few hundred agents cleanly, but when **500–1000 agents
  all connect within ~1–2 s** (each doing several DB writes on connect — reconcile, client
  upsert, version, inventory), the pool saturates and the server sheds the excess
  connections (WebSocket close 1011). A real fleet does **not** connect all at once and
  reconnects with backoff, so steady-state capacity is higher than this burst figure — but
  we report the burst because it is the honest stress point.
- **CPU** stayed near a single core (the worker is single-process), confirming the ceiling
  is per-worker, not the machine.

### Conclusion

On this hardware, **one un-tuned worker comfortably serves a single lab / a few hundred
agents.** We do **not** claim "thousands" on a single worker. Scaling further is a known,
ordinary path and is on the roadmap:

1. Run multiple `uvicorn` workers (or Gunicorn/Uvicorn workers) behind the proxy.
2. Raise the `asyncpg` pool and add a short connect-jitter so agents don't stampede.
3. For many workers, fan out panel/agent events through Redis pub/sub instead of in-process.

Until those are measured, treat **"a lab to a few hundred agents per worker"** as the
supported figure. Re-run the simulator after tuning and update this file with the new numbers.

---

## Update 2026-09-27: pool fix and a realistic load profile

Same host (8 cores, shared PostgreSQL 13 with `max_connections = 100`), one `uvicorn` worker, simulator
`tools/agent_simulator.py` (now with enrollment, software/Windows-update reports, panel sockets and server
CPU/RSS sampling).

**Correction to the conclusion above.** The WebSocket 1011 closes were not the pool being too small. With
`max_size = 100` the pool tried to open as many connections as the whole PostgreSQL server allows; on a server
shared with other applications the database refused new connections ("remaining connection slots are
reserved"), and those agents were dropped. The pool is now configurable (`DB_POOL_MIN` / `DB_POOL_MAX`,
default 2 / 20): when it is busy, requests wait for a connection instead of failing. Raising the pool is **not**
the fix; keep `DB_POOL_MAX` well below `max_connections`.

| Profile | Result |
|---|---|
| **A** — 1000 agents connect within 2.6 s, heartbeat every 5 s | 1000/1000 connected, 0 errors, ~200 heartbeat writes/s, server ~83 % of one core, RSS 281 MB, at most 8 PostgreSQL connections in use |
| **B** — 300 agents enroll with a token, each sends a 150-item software list and a Windows-update status every 30 s, 3 panel sockets open | 0 errors; 45 000 software rows and 300 update states written. Latency software p50 203 ms / p95 1.6 s, update status p50 15 ms / p95 1.4 s (all 300 agents report in the same few seconds on purpose); server ~84 % of one core, RSS 313 MB |

**What this says.** One worker holds 1000 idle agents comfortably. Bulk reports are the expensive part: when
hundreds of agents send their full software list at the same moment, p95 latency rises above a second and the
single core is busy. Real agents spread these reports (software on start and when the list changes, Windows
update 10–70 minutes after start and then daily), so this profile is a worst case.

Not measured yet: Vision streams (they need open audit sessions and the tray capture channel) and several
workers. Next steps are unchanged: multiple workers with events fanned out through Redis, and connect jitter.

Reproduce:

```bash
python tools/agent_simulator.py --n 1000 --url ws://127.0.0.1:8099 --duration 20 --hb 5 --server-pid <uvicorn pid>
python tools/agent_simulator.py --n 300 --url ws://127.0.0.1:8099 --duration 30 --hb 5 --enroll-token <multi-use token> \
    --software 150 --software-every 30 --patches --patch-every 30 --panels 3 --jwt <superadmin JWT> --server-pid <pid>
```
