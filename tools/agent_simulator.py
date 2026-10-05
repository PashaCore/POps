#!/usr/bin/env python3
"""POps agent simülatörü — gerçek PC gerektirmeden N sahte ajan çalıştırır.

Her sahte ajan gerçek ajan gibi /ws/agent'a bağlanır, dna_payload gönderir ve periyodik heartbeat atar.
İsteğe bağlı gerçekçi yük profili:
  --enroll-token   ajanlar jetonla kaydolur ve sunucunun verdiği anahtarı (set_secret) alır; sonraki HTTP
                   istekleri X-Agent-Id + X-Agent-Secret ile gider (yazılım/yama uçları yalnız anahtarlı ajanı
                   kabul eder)
  --software N     bağlandıktan sonra N kayıtlık yazılım listesi gönderir (--software-every ile tekrar)
  --patches        Windows Update durumu gönderir (--patch-every ile tekrar)
  --panels K       K panel WebSocket'i açar (--jwt ile) ve yayın (broadcast) yükünü sayar
  --server-pid P   ölçüm penceresinde sunucu sürecinin CPU ve bellek kullanımını /proc'tan okur (Linux)
  --reconnect      bağlantı düşerse ya da kurulamazsa gerçek ajan (0.1.8+) gibi yeniden dener: bekleme =
                   rastgele(0, min(60 sn, 2 sn × 2^deneme)), sağlam bağlantıda sayaç sıfırlanır. "Sunucu yeniden
                   başladı, herkes aynı anda geliyor" senaryosu için --ramp 0 ile kullanın; toparlanma süresi ölçülür.
  --flap K         dakikada (filo genelinde, ortalama) K ajan bağlantıyı kapatır, --flap-down sn sonra yeniden
                   bağlanır (panelde Çevrimdışı → Çevrimiçi; cihaz listesi değişikliği)
  --app-churn K    dakikada (filo genelinde, ortalama) K heartbeat'te ön plandaki uygulama değişir
                   (bkz. tools/bench_devices_delta.py)

Ölçülenler: bağlanan ajan, heartbeat/sn, HTTP istek/sn ve gecikme yüzdelikleri (p50/p95/p99), hatalar,
panellere düşen mesaj/sn, sunucu CPU/RSS. Vision kare akışı simüle edilmez (açık denetim oturumu ve tepsi
kanalı gerektirir). Sonuçlar BENCHMARKS.md için; ölçümü kendi donanımınızda tekrarlayın.

Örnek:
    python agent_simulator.py --n 1000 --url ws://127.0.0.1:8099 --duration 30 --hb 5
    python agent_simulator.py --n 300 --url ws://127.0.0.1:8099 --enroll-token TOKEN --software 150 \\
        --patches --panels 3 --jwt "$JWT" --server-pid "$(pgrep -f 'uvicorn server:app' | head -1)"

Python 3.9 uyumlu; yalnızca websockets + standart kütüphane.
"""
import argparse
import asyncio
import json
import os
import random
import statistics
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

import websockets

_http_pool = ThreadPoolExecutor(max_workers=64)


def hwid(i):
    return "HW-SIM-%06d" % i


def dna(i):
    h = hwid(i)
    mac = "AA:BB:%02X:%02X:%02X:%02X" % (i >> 24 & 255, i >> 16 & 255, i >> 8 & 255, i & 255)
    return json.dumps({
        "dna_payload": {"hardware": {"uuid": "%s-U" % h, "bios_sn": "%s-B" % h, "disk_sn": "%s-D" % h,
                                     "mac": mac, "ram_sn": "%s-R" % h},
                        "capabilities": {"ram_readable": True}},
        "hostname": h, "status": "Online"})


def software_payload(i, n):
    # Gerçekçi dağılım: ortak programlar + cihaza özgü birkaç kayıt
    common = ["Google Chrome", "Mozilla Firefox", "7-Zip", "Microsoft Office LTSC 2021", "VLC media player",
              "Notepad++", "Adobe Acrobat Reader", "Python 3.12", "Visual Studio Code", "GeoGebra"]
    items = [{"name": common[k % len(common)] + ("" if k < len(common) else " Eklenti %d" % k),
              "version": "%d.%d" % (1 + k % 9, i % 7), "publisher": "Yayinci %d" % (k % 13)} for k in range(n)]
    return {"items": items}


def patch_payload(i):
    pending = i % 5
    return {"pending_count": pending, "pending_security": pending // 2, "pending_critical": 1 if pending == 4 else 0,
            "reboot_required": i % 11 == 0, "last_search": "2026-09-27T10:00:00Z",
            "updates": [{"kb": "KB50%05d" % k, "title": "Simule guncelleme %d" % k, "severity": "Important",
                         "categories": ["Security Updates"], "is_security": True} for k in range(pending)]}


def _post(url, body, headers, timeout):
    data = json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method="POST", headers={"Content-Type": "application/json", **headers})
    t = time.perf_counter()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            resp.read()
            return resp.status, time.perf_counter() - t
    except urllib.error.HTTPError as e:
        return e.code, time.perf_counter() - t
    except Exception:
        return 0, time.perf_counter() - t


async def http_post(stats, name, url, body, headers):
    loop = asyncio.get_running_loop()
    status, dt = await loop.run_in_executor(_http_pool, _post, url, body, headers, 30)
    s = stats["http"].setdefault(name, {"ok": 0, "err": 0, "lat": [], "codes": {}})
    if 200 <= status < 300:
        s["ok"] += 1
    else:
        s["err"] += 1
        s["codes"][status] = s["codes"].get(status, 0) + 1
    s["lat"].append(dt)


def backoff(attempt):
    # POps.Shared.ReconnectBackoff ile aynı: full jitter, tavan 60 sn
    return random.random() * min(60.0, 2.0 * (2 ** min(attempt, 5)))


APPS = ("chrome", "msedge", "winword", "excel", "powerpnt", "explorer", "code", "geogebra", "vlc", "notepad")


def per_beat(per_minute, args):
    """Dakikada filo genelinde 'per_minute' olay için bir heartbeat'teki olasılık."""
    return per_minute / (args.n * 60.0 / args.hb) if per_minute and args.n else 0.0


async def agent(i, args, stop_at, stats):
    attempt = 0
    while True:
        ok = await agent_once(i, args, stop_at, stats)
        if ok == "flap" and time.monotonic() < stop_at:
            await asyncio.sleep(args.flap_down)
            continue
        if not args.reconnect or time.monotonic() >= stop_at:
            return
        attempt = 0 if ok else attempt + 1
        stats["retries"] += 1
        await asyncio.sleep(backoff(attempt))


async def agent_once(i, args, stop_at, stats):
    """Bir bağlantı ömrü. Dönen: bağlantı sağlam kuruldu mu (en az bir heartbeat gitti)."""
    base = args.url.rstrip("/")
    http_base = base.replace("wss://", "https://").replace("ws://", "http://")
    uri = base + "/ws/agent/" + hwid(i)
    headers = {"X-Agent-Version": "sim"}
    if args.enroll_token:
        headers["X-Enroll-Token"] = args.enroll_token
    secret = None
    try:
        try:
            conn = websockets.connect(uri, open_timeout=30, close_timeout=5, max_queue=8, additional_headers=headers)
        except TypeError:   # websockets < 14
            conn = websockets.connect(uri, open_timeout=30, close_timeout=5, max_queue=8, extra_headers=headers)
        async with conn as ws:
            await ws.send(dna(i))
            if i not in stats["ever"]:
                stats["ever"].add(i)
                stats["connected"] += 1
                if stats["connected"] == args.n:
                    stats["all_at"] = time.monotonic()
            next_sw = time.monotonic() + (i % 10) if args.software else float("inf")
            next_patch = time.monotonic() + 5 + (i % 10) if args.patches else float("inf")
            app = "sim"
            while time.monotonic() < stop_at:
                # gelen komutlar: set_secret'ı yakala, gerisini yut
                try:
                    msg = await asyncio.wait_for(ws.recv(), timeout=0.01)
                    try:
                        m = json.loads(msg)
                        if m.get("action") == "set_secret":
                            secret = m.get("secret")
                            stats["enrolled"] += 1
                    except (ValueError, AttributeError):
                        pass
                except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
                    pass
                if random.random() < per_beat(args.flap, args):
                    stats["flaps"] += 1
                    await ws.close()
                    return "flap"
                if random.random() < per_beat(args.app_churn, args):
                    app = random.choice([a for a in APPS if a != app])
                    stats["app_changes"] += 1
                await ws.send(json.dumps({"status": "Online", "active_window": app, "hostname": hwid(i)}))
                stats["heartbeats"] += 1
                now = time.monotonic()
                auth = {"X-Agent-Id": hwid(i), "X-Agent-Secret": secret} if secret else {}
                if now >= next_sw and secret:
                    await http_post(stats, "software", "%s/api/software/%s" % (http_base, hwid(i)),
                                    software_payload(i, args.software), auth)
                    next_sw = now + args.software_every if args.software_every else float("inf")
                if now >= next_patch and secret:
                    await http_post(stats, "patches", "%s/api/patches/%s" % (http_base, hwid(i)), patch_payload(i),
                                    auth)
                    next_patch = now + args.patch_every if args.patch_every else float("inf")
                await asyncio.sleep(args.hb)
        return True
    except Exception as e:
        stats["errors"] += 1
        if stats["errors"] <= 3:
            stats["last_error"] = repr(e)
        return False


async def panel(k, args, stop_at, stats):
    uri = args.url.rstrip("/") + "/ws/panel"
    cookie = {"Cookie": "pops_jwt=%s" % args.jwt}
    try:
        try:
            conn = websockets.connect(uri, open_timeout=30, additional_headers=cookie, max_queue=None)
        except TypeError:
            conn = websockets.connect(uri, open_timeout=30, extra_headers=cookie, max_queue=None)
        async with conn as ws:
            stats["panels"] += 1
            while time.monotonic() < stop_at:
                try:
                    await asyncio.wait_for(ws.recv(), timeout=1)
                    stats["panel_msgs"] += 1
                except asyncio.TimeoutError:
                    pass
    except Exception as e:
        stats["panel_errors"] += 1
        stats["last_error"] = stats["last_error"] or repr(e)


def proc_sample(pid):
    """(cpu_ticks, rss_kb) — Linux /proc; okunamazsa None."""
    try:
        with open("/proc/%d/stat" % pid) as f:
            parts = f.read().rsplit(")", 1)[1].split()
        ticks = int(parts[11]) + int(parts[12])
        with open("/proc/%d/status" % pid) as f:
            rss = next(int(line.split()[1]) for line in f if line.startswith("VmRSS:"))
        return ticks, rss
    except (OSError, ValueError, StopIteration):
        return None


def pct(values, p):
    if not values:
        return 0.0
    values = sorted(values)
    return values[min(len(values) - 1, int(round(p / 100.0 * (len(values) - 1))))] * 1000


async def main():
    p = argparse.ArgumentParser()
    p.add_argument("--n", type=int, default=500)
    p.add_argument("--url", default="ws://127.0.0.1:8099")
    p.add_argument("--duration", type=int, default=20, help="ölçüm penceresi (sn)")
    p.add_argument("--hb", type=float, default=5.0, help="heartbeat aralığı (sn)")
    p.add_argument("--ramp", type=float, default=0.002, help="ajanlar arası bağlanma gecikmesi (sn)")
    p.add_argument("--enroll-token", default=None)
    p.add_argument("--software", type=int, default=0, help="ajan başına yazılım kaydı (0 = gönderme)")
    p.add_argument("--software-every", type=float, default=0, help="yazılım listesini tekrar gönderme aralığı (sn)")
    p.add_argument("--patches", action="store_true")
    p.add_argument("--patch-every", type=float, default=0)
    p.add_argument("--panels", type=int, default=0)
    p.add_argument("--jwt", default=os.environ.get("POPS_SIM_JWT"))
    p.add_argument("--server-pid", type=int, default=None)
    p.add_argument("--reconnect", action="store_true", help="düşen/kurulamayan bağlantıyı ajan gibi yeniden dene")
    p.add_argument("--flap", type=float, default=0, help="dakikada kopup yeniden bağlanan ajan (filo geneli)")
    p.add_argument("--flap-down", type=float, default=5.0, help="kopan ajanın yeniden bağlanmadan önce beklediği sn")
    p.add_argument("--app-churn", type=float, default=0, help="dakikada ön plandaki uygulama değişimi (filo geneli)")
    args = p.parse_args()
    if (args.software or args.patches) and not args.enroll_token:
        p.error("--software/--patches için --enroll-token gerekir (bu uçlar yalnız anahtarlı ajanı kabul eder)")
    if args.panels and not args.jwt:
        p.error("--panels için --jwt (ya da POPS_SIM_JWT) gerekir")

    stats = {"connected": 0, "enrolled": 0, "heartbeats": 0, "errors": 0, "last_error": None, "http": {},
             "panels": 0, "panel_msgs": 0, "panel_errors": 0, "ever": set(), "retries": 0, "all_at": None,
             "flaps": 0, "app_changes": 0}
    t0 = time.monotonic()
    stop_at = t0 + 15 + args.duration + args.n * args.ramp
    tasks = [asyncio.ensure_future(panel(k, args, stop_at, stats)) for k in range(args.panels)]
    for i in range(args.n):
        tasks.append(asyncio.ensure_future(agent(i, args, stop_at, stats)))
        if args.ramp:
            await asyncio.sleep(args.ramp)
    connect_done = time.monotonic()
    for _ in range(300 if args.reconnect else 75):
        if stats["connected"] >= args.n or (not args.reconnect and stats["connected"] + stats["errors"] >= args.n):
            break
        await asyncio.sleep(0.2)
    print("connected=%d/%d  connect_wall=%.1fs  enrolled=%d  errors=%d  panels=%d  %s"
          % (stats["connected"], args.n, connect_done - t0, stats["enrolled"], stats["errors"], stats["panels"],
             stats["last_error"] or ""))
    if args.reconnect:
        print("reconnect: all_connected_after=%s  failed_attempts=%d  retries=%d"
              % ("%.1fs" % (stats["all_at"] - t0) if stats["all_at"] else "henüz değil", stats["errors"],
                 stats["retries"]))

    # Isınma: ilk yazılım/yama gönderimleri bağlanmayla çakışmasın diye pencereden önce kısa bekle
    await asyncio.sleep(min(10.0, args.hb * 2))
    hb0, msgs0 = stats["heartbeats"], stats["panel_msgs"]
    http0 = {k: v["ok"] + v["err"] for k, v in stats["http"].items()}
    for v in stats["http"].values():
        v["lat"].clear()
    s0 = proc_sample(args.server_pid) if args.server_pid else None
    await asyncio.sleep(args.duration)
    s1 = proc_sample(args.server_pid) if args.server_pid else None
    w = args.duration
    print("sustained=%d  heartbeat_writes_per_sec=%.0f  panel_msgs_per_sec=%.0f  (window=%ds, hb=%.1fs)"
          % (stats["connected"] - (0 if args.reconnect else stats["errors"]), (stats["heartbeats"] - hb0) / w,
             (stats["panel_msgs"] - msgs0) / w, w, args.hb))
    if args.flap or args.app_churn:
        print("churn: flaps=%d  app_changes=%d (toplam)" % (stats["flaps"], stats["app_changes"]))
    for name, v in sorted(stats["http"].items()):
        total = v["ok"] + v["err"]
        print("http %-9s total=%d  in_window=%d  ok=%d  err=%d %s  p50=%.0fms  p95=%.0fms  p99=%.0fms  mean=%.0fms"
              % (name, total, total - http0.get(name, 0), v["ok"], v["err"], v["codes"] or "",
                 pct(v["lat"], 50), pct(v["lat"], 95), pct(v["lat"], 99),
                 (statistics.mean(v["lat"]) * 1000) if v["lat"] else 0))
    if s0 and s1:
        hz = os.sysconf("SC_CLK_TCK")
        cpu = (s1[0] - s0[0]) / hz / w * 100
        print("server cpu=%.0f%% (bir çekirdeğin yüzdesi)  rss=%.0f MB" % (cpu, s1[1] / 1024))
    for t in tasks:
        t.cancel()
    await asyncio.gather(*tasks, return_exceptions=True)
    _http_pool.shutdown(wait=False)


if __name__ == "__main__":
    asyncio.run(main())
