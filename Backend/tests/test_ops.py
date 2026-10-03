"""İşletim uçları — entegrasyon testi (CI 'security' job'ı): request_id, /metrics, sağlık özeti, Genel bakış verisi.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET + METRICS_TOKEN (sunucuyla aynı değer).
"""

import asyncio
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
METRICS_TOKEN = os.environ["METRICS_TOKEN"]
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, headers=None, raw=False):
    r = urllib.request.Request(HTTP + path)
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=30) as resp:
            body = resp.read().decode("utf-8")
            return resp.status, dict(resp.headers), (body if raw else json.loads(body or "{}"))
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers), None


async def q(sql, *args):
    c = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )
    try:
        return await c.fetch(sql, *args)
    finally:
        await c.close()


async def setup():
    for u, role in (("opsuper", "superadmin"), ("opadmin", "admin")):
        await q("DELETE FROM users WHERE username=$1", u)
        await q(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0)",
            u,
            role,
        )


def main():
    asyncio.run(setup())
    sa = server.create_jwt("opsuper", "superadmin", 0)
    ad = server.create_jwt("opadmin", "admin", 0)

    print("== request_id")
    s, h, _ = req("/api/health")
    rid = {k.lower(): v for k, v in h.items()}.get("x-request-id", "")
    chk(s == 200 and len(rid) >= 8, "yanıtta X-Request-ID var (%s)" % rid)
    s, h, _ = req("/api/health", headers={"X-Request-ID": "test-rid-12345678"})
    chk({k.lower(): v for k, v in h.items()}.get("x-request-id") == "test-rid-12345678", "güvenli gelen kimlik korunur")
    s, h, _ = req("/api/health", headers={"X-Request-ID": "bad id\"<x>"})
    got = {k.lower(): v for k, v in h.items()}.get("x-request-id", "")
    chk(got and got != "bad id\"<x>", "güvensiz gelen kimlik yerine yenisi üretilir")

    print("== /metrics")
    chk(req("/metrics")[0] == 401, "jetonsuz 401")
    chk(req("/metrics", "yanlis-jeton-yanlis-jeton")[0] == 401, "yanlış jeton 401")
    chk(req("/metrics", sa)[0] == 401, "panel oturumu metrik jetonu yerine geçmez")
    req("/api/patches/HW-OPS-METRIC", ad)  # parametreli rota: etiket şablon olmalı, cihaz adı değil
    s, _, text = req("/metrics", METRICS_TOKEN, raw=True)
    chk(s == 200 and "pops_build_info" in text, "doğru jetonla metrikler döner")
    chk('route="/api/health"' in text, "istek sayacı rota şablonuyla")
    chk('route="/api/patches/{pc_name}"' in text and "HW-OPS-METRIC" not in text, "cihaz adı etikete sızmıyor")
    chk("pops_http_request_duration_seconds_bucket" in text, "süre histogramı var")
    chk("pops_db_pool_connections" in text and "pops_agents_connected" in text, "havuz ve ajan göstergeleri var")
    chk('pops_devices{state="total"}' in text, "cihaz sayıları var")

    print("== /api/system/diagnostics")
    chk(req("/api/system/diagnostics")[0] == 401, "oturumsuz 401")
    chk(req("/api/system/diagnostics", ad)[0] == 403, "admin 403 (yalnız superadmin)")
    s, _, d = req("/api/system/diagnostics", sa)
    chk(s == 200, "superadmin 200")
    d = d or {}
    for key in ("version", "uptime_seconds", "db_pool", "devices", "log_counts", "recent_errors", "slowest_routes",
                "backup"):
        chk(key in d, "alan var: %s" % key)
    chk(d.get("metrics_enabled") is True, "metrik ucunun açık olduğu görünüyor")
    chk(isinstance(d.get("recent_errors"), list), "son hatalar liste")

    print("== /api/system/overview")
    chk(req("/api/system/overview")[0] == 401, "oturumsuz 401")
    chk(req("/api/system/overview", ad)[0] == 403, "admin 403 (yalnız superadmin)")
    chk(req("/api/system/overview?span=1y", sa)[0] == 422, "bilinmeyen aralık 422")
    # 6 saat önceki 15 dakikalık dilimde yalnızca bu ölçüm olsun (dilim başı saat başıdır)
    slot = "date_trunc('hour', NOW()) - interval '6 hours'"
    asyncio.run(q("DELETE FROM server_metrics WHERE ts >= %s AND ts < %s + interval '15 minutes'" % (slot, slot)))
    asyncio.run(q(
        "INSERT INTO server_metrics (ts, agents, panels, cpu_pct, mem_pct, disk_pct, db_mb, rss_mb, requests, errors) "
        "VALUES (%s, 7, 1, 12.5, 40, 55, 30, 80, 120, 3)" % slot))
    s, _, d = req("/api/system/overview", sa)
    d = d or {}
    chk(s == 200 and d.get("span") == "24h", "superadmin 200, varsayılan 24 saat")
    series = d.get("series") or []
    chk(len(series) == 96, "24 saatte 15 dakikalık 96 nokta (%d)" % len(series))
    chk(any(p.get("agents") == 7 and p.get("requests") == 120 and p.get("errors") == 3 for p in series),
        "yazılan ölçüm noktada görünür")
    chk(len(d.get("tasks") or []) == 24 and len(d.get("events") or []) == 24, "saatlik 24 görev ve olay çubuğu")
    chk(set((d.get("updates") or {}).keys()) == {"success", "rolled_back", "failed"}, "güncelleme sonuçları")
    chk("devices" in d and "agents_connected" in d and isinstance(d.get("disk"), list), "anlık değerler")
    s, _, d = req("/api/system/overview?span=30d", sa)
    chk(s == 200 and len((d or {}).get("series") or []) == 120 and len((d or {}).get("tasks") or []) == 30,
        "30 gün: 6 saatlik 120 nokta, günlük 30 çubuk")
    asyncio.run(q("DELETE FROM server_metrics WHERE ts = %s" % slot))

    asyncio.run(q("DELETE FROM users WHERE username = ANY($1::text[])", ["opsuper", "opadmin"]))
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM ISLETIM TESTLERI GECTI")


if __name__ == "__main__":
    main()
