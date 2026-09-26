# POps — Benchmarks

Measured numbers instead of a "thousands of devices" claim. Reproduce with
[`tools/agent_simulator.py`](tools/agent_simulator.py) on your own hardware; these are a
first honest baseline, not a marketing figure.

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
