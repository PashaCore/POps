#!/usr/bin/env python3
"""Bir panel sekmesinin cihaz listesi için dakikada çektiği bayt: eski yoklama ile ETag, ?since= ve panel soketi.

Aynı ölçüm penceresinde dört "sanal sekme" yan yana çalışır (aynı değişiklikleri görürler):
  full   eski panel: GET /api/devices her 5 sn, bütün liste
  etag   yalnızca ETag: GET /api/devices her 5 sn, If-None-Match (değişmediyse 304)
  delta  ?since=<sürüm> her 5 sn, If-None-Match; soket yok (panelin soketsiz yedek yolu)
  push   bugünkü panel: /ws/panel?topics=devices + "devices_changed" gelince ?since=, ayrıca 30 sn'de bir yoklama

Sayılan: yanıt gövdesi (sıkıştırmasız ve gzip ile; gzip ters vekildeki gibi hesaplanır: nginx varsayılanı seviye 1,
1024 bayttan küçük gövde sıkıştırılmaz; Apache mod_deflate'in varsayılanı seviye 6 ayrıca verilir), yanıt başlıkları
ve soket çerçeveleri (iki yön, çerçeve başlığıyla). İlk yükleme (sayfa açılışındaki tam liste) sayılmaz; pencere
ondan sonra başlar. Sayfanın her turda çektiği küçük /api/custom_labs ve /api/lab_settings dahil değildir.

Değişiklik yükünü agent_simulator.py üretir (ör. --flap, --app-churn). Örnek:
    python tools/agent_simulator.py --n 2000 --url ws://127.0.0.1:8099 --duration 900 --hb 5 \\
        --flap 20 --app-churn 200 &
    python tools/bench_devices_delta.py --url http://127.0.0.1:8099 --user admin --password ... --seconds 120

Python 3.9 uyumlu; yalnızca websockets + standart kütüphane.
"""
import argparse
import asyncio
import gzip
import http.client
import json
import os
import time
import urllib.parse

import websockets

FULL_EVERY = 5.0
SOCKET_POLL_EVERY = 30.0
NGINX_GZIP_MIN = 1024


def gz(body: bytes, level: int) -> int:
    if len(body) < NGINX_GZIP_MIN:
        return len(body)
    return len(gzip.compress(body, compresslevel=level, mtime=0))


class Tab:
    def __init__(self, name, base, token):
        self.name = name
        u = urllib.parse.urlsplit(base)
        self.host, self.port = u.hostname, u.port or 80
        self.token = token
        self.conn = None
        self.measuring = False
        self.n = {"requests": 0, "304": 0, "full": 0, "delta": 0, "body": 0, "gzip1": 0, "gzip6": 0, "headers": 0,
                  "ws_frames": 0, "ws_bytes": 0, "changed_rows": 0}
        self.version = None
        self.etag = None

    def _get(self, path, headers):
        for attempt in (0, 1):
            try:
                if self.conn is None:
                    self.conn = http.client.HTTPConnection(self.host, self.port, timeout=60)
                self.conn.request("GET", path, headers=dict(headers, Authorization="Bearer " + self.token))
                r = self.conn.getresponse()
                body = r.read()
                head = len("HTTP/1.1 %d %s\r\n" % (r.status, r.reason)) + 2
                head += sum(len(k) + len(v) + 4 for k, v in r.getheaders())
                return r.status, dict(r.getheaders()), body, head
            except (http.client.HTTPException, OSError):
                self.conn = None
                if attempt:
                    raise

    async def get(self, path, headers=None):
        status, hdrs, body, head = await asyncio.to_thread(self._get, path, headers or {})
        if status not in (200, 304):
            raise RuntimeError("%s: HTTP %d" % (path, status))
        if self.measuring:
            self.n["requests"] += 1
            self.n["headers"] += head
            if status == 304:
                self.n["304"] += 1
            else:
                self.n["body"] += len(body)
                self.n["gzip1"] += gz(body, 1)
                self.n["gzip6"] += gz(body, 6)
        return status, hdrs, (json.loads(body) if body else None)

    async def fetch(self):
        """Bir tur: moduna göre liste ya da değişiklikler."""
        if self.name == "full":
            await self.get("/api/devices")
            if self.measuring:
                self.n["full"] += 1
            return
        if self.name == "etag":
            status, hdrs, _body = await self.get("/api/devices", {"If-None-Match": self.etag} if self.etag else {})
            if status == 200:
                self.etag = hdrs.get("ETag") or hdrs.get("etag")
                if self.measuring:
                    self.n["full"] += 1
            return
        headers = {"If-None-Match": 'W/"d%d"' % self.version} if self.version is not None else {}
        status, _h, body = await self.get("/api/devices?since=%d" % (self.version or 0), headers)
        if status == 304:
            return
        self.version = body.get("version")
        if self.measuring:
            self.n["full" if body.get("full") else "delta"] += 1
            self.n["changed_rows"] += len(body.get("changed") or body.get("devices") or [])


async def poller(tab, every, stop):
    while not stop.is_set():
        await tab.fetch()
        try:
            await asyncio.wait_for(stop.wait(), every)
        except asyncio.TimeoutError:
            pass


async def pusher(tab, ws_url, stop):
    """Panelin bugünkü davranışı: soket + değişince ?since=, soket açıkken 30 sn'de bir yoklama, 45 sn'de bir ping."""
    busy = asyncio.Lock()

    async def refresh():
        async with busy:
            await tab.fetch()

    cookie = {"Cookie": "pops_jwt=" + tab.token}
    try:
        conn = websockets.connect(ws_url + "/ws/panel?topics=devices", additional_headers=cookie)
    except TypeError:   # websockets < 14
        conn = websockets.connect(ws_url + "/ws/panel?topics=devices", extra_headers=cookie)
    async with conn as ws:
        async def reader():
            async for raw in ws:
                if tab.measuring:
                    tab.n["ws_frames"] += 1
                    tab.n["ws_bytes"] += len(raw) + 2   # sunucu → istemci çerçevesi maskesiz: 2 bayt başlık
                msg = json.loads(raw)
                newer = tab.version is None or msg.get("version", 0) > tab.version
                if msg.get("type") == "devices_changed" and newer:
                    asyncio.ensure_future(refresh())

        async def pinger():
            while not stop.is_set():
                try:
                    await asyncio.wait_for(stop.wait(), 45)
                except asyncio.TimeoutError:
                    await ws.send('{"type":"ping"}')
                    if tab.measuring:
                        tab.n["ws_frames"] += 1
                        tab.n["ws_bytes"] += 15 + 6   # istemci çerçevesi maskeli: 6 bayt başlık

        async def poll():
            while not stop.is_set():
                try:
                    await asyncio.wait_for(stop.wait(), SOCKET_POLL_EVERY)
                except asyncio.TimeoutError:
                    await refresh()

        tasks = [asyncio.ensure_future(t()) for t in (reader, pinger, poll)]
        await stop.wait()
        for t in tasks:
            t.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


def login(base, user, password):
    u = urllib.parse.urlsplit(base)
    c = http.client.HTTPConnection(u.hostname, u.port or 80, timeout=30)
    c.request("POST", "/api/admin/login", body=json.dumps({"username": user, "password": password}),
              headers={"Content-Type": "application/json"})
    data = json.loads(c.getresponse().read())
    if not data.get("token"):
        raise SystemExit("giriş başarısız: %s" % data)
    return data["token"]


async def main():
    p = argparse.ArgumentParser()
    p.add_argument("--url", default="http://127.0.0.1:8099", help="backend (ters vekil değil; gzip hesaplanır)")
    p.add_argument("--jwt", default=os.environ.get("POPS_SIM_JWT"))
    p.add_argument("--user")
    p.add_argument("--password")
    p.add_argument("--seconds", type=float, default=120)
    p.add_argument("--label", default="")
    args = p.parse_args()
    token = args.jwt or login(args.url, args.user, args.password)
    ws_url = args.url.replace("https://", "wss://").replace("http://", "ws://")
    tabs = {name: Tab(name, args.url, token) for name in ("full", "etag", "delta", "push")}
    for tab in tabs.values():   # sayfa açılışı: ilk tam liste (sayılmaz)
        await tab.fetch()
    stop = asyncio.Event()
    for tab in tabs.values():
        tab.measuring = True
    started = time.monotonic()
    tasks = [asyncio.ensure_future(poller(tabs[n], FULL_EVERY, stop)) for n in ("full", "etag", "delta")]
    tasks.append(asyncio.ensure_future(pusher(tabs["push"], ws_url, stop)))
    await asyncio.sleep(args.seconds)
    stop.set()
    await asyncio.gather(*tasks)
    window = time.monotonic() - started
    per_min = 60.0 / window
    print("%s pencere %.0f sn; dakika başına (bir sekme):" % (args.label, window))
    print("%-6s %8s %6s %6s %6s %12s %12s %12s %10s %10s %8s" % (
        "mod", "istek", "304", "tam", "delta", "gövde", "gzip-1", "gzip-6", "başlık", "soket", "satır"))
    out = {}
    for name, tab in tabs.items():
        n = {k: v * per_min for k, v in tab.n.items()}
        out[name] = {k: round(v, 1) for k, v in n.items()}
        print("%-6s %8.1f %6.1f %6.1f %6.1f %12.0f %12.0f %12.0f %10.0f %10.0f %8.1f" % (
            name, n["requests"], n["304"], n["full"], n["delta"], n["body"], n["gzip1"], n["gzip6"], n["headers"],
            n["ws_bytes"], n["changed_rows"]))
    print(json.dumps({"label": args.label, "window_s": round(window, 1), "per_minute": out}, ensure_ascii=False))


if __name__ == "__main__":
    asyncio.run(main())
