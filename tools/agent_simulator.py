#!/usr/bin/env python3
"""POps agent simülatörü — gerçek PC gerektirmeden N sahte ajan çalıştırır.

Her sahte ajan bir gerçek ajan gibi /ws/agent'a bağlanır, bir dna_payload gönderir ve
periyodik heartbeat (status) atar. Amaç ölçmek: kaç eşzamanlı ajan taşınabiliyor, bağlanma
süresi, sunucunun CPU/RAM'i ve heartbeat (DB yazma) hızı. Sonuçlar BENCHMARKS.md için.

Kullanım:
    python agent_simulator.py --n 1000 --url ws://127.0.0.1:8099 --duration 20 --hb 5

Not: "binlerce" iddiası yerine gerçek sayı koymak için; ölçümü kendi donanımınızda tekrarlayın.
Python 3.9 uyumlu.
"""
import argparse
import asyncio
import json
import time

import websockets


def dna(i):
    h = "HW-SIM-%06d" % i
    return json.dumps({
        "dna_payload": {"hardware": {"uuid": "%s-U" % h, "bios_sn": "%s-B" % h, "disk_sn": "%s-D" % h,
                                     "mac": "AA:BB:%02X:%02X:%02X:%02X" % (i >> 24 & 255, i >> 16 & 255, i >> 8 & 255, i & 255),
                                     "ram_sn": "%s-R" % h},
                        "capabilities": {"ram_readable": True}},
        "hostname": h, "status": "Online"})


async def agent(i, base, stop_at, hb, stats):
    uri = base.rstrip("/") + "/ws/agent/HW-SIM-%06d" % i
    try:
        async with websockets.connect(uri, open_timeout=30, close_timeout=5, max_queue=8) as ws:
            await ws.send(dna(i))
            stats["connected"] += 1
            hostname = "HW-SIM-%06d" % i
            while time.monotonic() < stop_at:
                # gelen komutları yut (server get_hardware isteyebilir); heartbeat gönder
                try:
                    await asyncio.wait_for(ws.recv(), timeout=0.01)
                except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
                    pass
                await ws.send(json.dumps({"status": "Online", "active_window": "sim", "hostname": hostname}))
                stats["heartbeats"] += 1
                await asyncio.sleep(hb)
    except Exception as e:
        stats["errors"] += 1
        if stats["errors"] <= 3:
            stats["last_error"] = repr(e)


async def main():
    p = argparse.ArgumentParser()
    p.add_argument("--n", type=int, default=500)
    p.add_argument("--url", default="ws://127.0.0.1:8099")
    p.add_argument("--duration", type=int, default=20)
    p.add_argument("--hb", type=float, default=5.0)
    p.add_argument("--ramp", type=float, default=0.002, help="ajanlar arası bağlanma gecikmesi (sn)")
    args = p.parse_args()

    stats = {"connected": 0, "heartbeats": 0, "errors": 0, "last_error": None}
    t0 = time.monotonic()
    stop_at = t0 + args.duration + args.n * args.ramp
    tasks = []
    for i in range(args.n):
        tasks.append(asyncio.ensure_future(agent(i, args.url, stop_at, args.hb, stats)))
        if args.ramp:
            await asyncio.sleep(args.ramp)
    connect_done = time.monotonic()
    # tüm ajanlar bağlanana kadar kısa bekle
    for _ in range(50):
        if stats["connected"] + stats["errors"] >= args.n:
            break
        await asyncio.sleep(0.2)
    print("connected=%d/%d  connect_wall=%.1fs  errors=%d  %s"
          % (stats["connected"], args.n, connect_done - t0, stats["errors"], stats["last_error"] or ""))
    # steady-state penceresi
    window = args.duration
    hb_before = stats["heartbeats"]
    await asyncio.sleep(window)
    hb_rate = (stats["heartbeats"] - hb_before) / window
    print("sustained=%d  heartbeat_writes_per_sec=%.0f  (window=%ds, hb=%.1fs)"
          % (stats["connected"] - stats["errors"], hb_rate, window, args.hb))
    for t in tasks:
        t.cancel()
    await asyncio.gather(*tasks, return_exceptions=True)


if __name__ == "__main__":
    asyncio.run(main())
