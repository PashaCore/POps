"""Cihaz listesi: ETag / 304, ?since= değişiklikleri ve panel soketine devices_changed — entegrasyon testi
(CI 'security' job'ı).

- Parametresiz /api/devices'ın biçimi değişmedi (dizi, aynı alanlar); zayıf ETag döner, If-None-Match tutarsa 304
  (gövdesiz), Apache'nin "-gzip" eklediği ETag de tutar; eski ETag 200 alır.
- Değişmeyen heartbeat sürümü artırmaz; durum ya da uygulama değişince artar ve satır değişenlerde gelir.
- Panelden yapılan işlem (ad, sınıf) yanıt dönmeden sürümü artırır; silinen cihaz "removed"da; çok eski ya da ileri
  bir since tam liste (full: true) alır.
- ?topics=devices soketi devices_changed alır (saniyede en fazla bir) ve başka yayın almaz; konusuz panel soketi
  eskisi gibi diğer yayınları alır, devices_changed almaz; kopan cihaz yaklaşık bir saniyede bildirilir.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import hashlib
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
PCS = ["HW-DLT1", "HW-DLT2"]
# Parametresiz yanıtın alanları (başka araçlar okur; değişmemeli)
KEYS = {
    "hostname", "real_hostname", "display_name", "pc_name", "hw_id", "ip", "lab", "status", "last_seen",
    "active_window", "boot_count", "current_user", "is_quarantined", "agent_version", "running_version",
    "cap_terminal_enabled", "cap_vision_enabled", "cap_server_ca", "cap_terminal_disable_requested",
    "cap_vision_disable_requested", "agent_health", "last_disconnect_at", "last_disconnect_reason", "bypass_key",
    # dosya aktarımı, Linux ajanı ve winget dallarının eklediği alanlar
    "cap_files_enabled", "platform", "agent_features",
}
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def http(path, token=None, body=None, method=None, headers=None):
    """(durum, başlıklar, gövde: JSON ya da b'')"""
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            raw = resp.read()
            return resp.status, resp.headers, json.loads(raw) if raw else b""
    except urllib.error.HTTPError as e:
        raw = e.read()
        return e.code, e.headers, raw


def changes(token, since):
    s, _h, body = http("/api/devices?since=%d" % since, token)
    return body if s == 200 and isinstance(body, dict) else {}


def row(body, pc):
    return next((d for d in body.get("changed", []) if d.get("hw_id") == pc), None)


async def wait_changes(token, since, pred, timeout=8.0):
    end = time.monotonic() + timeout
    body = {}
    while time.monotonic() < end:
        body = changes(token, since)
        if pred(body):
            return body
        await asyncio.sleep(0.25)
    return body


async def collect(ws, seconds):
    out = []
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - time.monotonic()))
        except asyncio.TimeoutError:
            break
        except websockets.exceptions.ConnectionClosed:
            break
        try:
            out.append(json.loads(raw))
        except ValueError:
            pass
    return out


def beat(pc, status="Online", window="W1"):
    return json.dumps({"hw_id": pc, "hostname": pc.lower(), "status": status, "active_window": window})


def dna(pc):
    return json.dumps({
        "hw_id": pc,
        "dna_payload": {"hardware": {"uuid": pc + "-U", "bios_sn": pc + "-B", "disk_sn": pc + "-D", "mac": "-",
                                     "ram_sn": "-"}, "capabilities": {"ram_readable": True}},
        "hostname": pc.lower(), "status": "Online", "active_window": "W1",
    })


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    for table in ("agent_secrets", "agent_bypass_keys", "clients", "agent_versions", "hw_inventory"):
        await c.execute("DELETE FROM %s WHERE pc_name = ANY($1::text[])" % table, PCS)
    await c.execute("DELETE FROM users WHERE username = 'dltadmin'")


async def main():
    c = await conn()
    await cleanup(c)
    await c.execute("INSERT INTO users (username,password_hash,role,permissions,token_version) "
                    "VALUES ('dltadmin','x','admin','[]',0)")
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", "HW-DLT1",
                    hashlib.sha256(b"HW-DLT1-s").hexdigest())
    admin = server.create_jwt("dltadmin", "admin", 0)
    try:
        await run(c, admin)
    finally:
        await cleanup(c)
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM CIHAZ LISTESI TESTLERI GECTI")


async def run(c, admin):
    print("== parametresiz yanıt: biçim aynı, ETag, 304")
    # Sunucu açılışta listeyi bir kez okur; sürüm ancak ondan sonra verilir
    for _ in range(40):
        s, h, plain = http("/api/devices", admin)
        if h.get("ETag"):
            break
        await asyncio.sleep(0.25)
    tag = h.get("ETag") or ""
    chk(s == 200 and isinstance(plain, list), "parametresiz yanıt hâlâ dizi")
    chk(all(set(d) == KEYS for d in plain), "alanlar değişmedi")
    chk(tag.startswith('W/"d') and h.get("Cache-Control") == "private, no-cache", "zayıf ETag: %s" % tag)
    s, h304, body = http("/api/devices", admin, headers={"If-None-Match": tag})
    chk(s == 304 and body == b"" and h304.get("ETag") == tag, "If-None-Match tutunca 304, gövde yok")
    chk(http("/api/devices", admin, headers={"If-None-Match": tag[:-1] + '-gzip"'})[0] == 304,
        "Apache'nin -gzip eklediği ETag de tutar")
    s, _h, body = http("/api/devices", admin, headers={"If-None-Match": 'W/"d1"'})
    chk(s == 200 and isinstance(body, list), "eski ETag tam liste alır")
    chk(http("/api/devices", None, headers={"If-None-Match": tag})[0] == 401, "kimliksiz istek 401 (304 değil)")
    s, _h, full = http("/api/devices?since=0", admin)
    v = full.get("version") if isinstance(full, dict) else None
    chk(s == 200 and full.get("full") is True and isinstance(full.get("devices"), list) and isinstance(v, int),
        "since=0: tam liste ve sürüm")
    chk(changes(admin, v + 10 ** 9).get("full") is True, "ileri bir since tam liste alır (ör. yeniden başlatma)")
    chk(http("/api/devices?since=abc", admin)[0] == 422, "sayı olmayan since 422")

    print("== değişmeyen heartbeat sürümü artırmaz, durum değişikliği artırır")
    agent = await websockets.connect("%s/ws/agent/HW-DLT1" % WS,
                                     additional_headers={"X-Agent-Secret": "HW-DLT1-s", "X-Agent-Version": "0.1.22"})
    await agent.send(dna("HW-DLT1"))
    body = await wait_changes(admin, v, lambda b: (row(b, "HW-DLT1") or {}).get("active_window") == "W1")
    chk((row(body, "HW-DLT1") or {}).get("status") == "Online", "bağlanan cihaz değişenlerde")
    v = body.get("version", v)
    quiet = False
    for _attempt in range(3):
        # Dakikalık tarama last_seen'i yayımlarken sürüm artar; o turla çakışırsa ölçüm yinelenir
        before = changes(admin, v).get("version", v)
        for _ in range(3):
            await agent.send(beat("HW-DLT1"))
            await asyncio.sleep(1.0)
        await asyncio.sleep(2.5)   # toplu yazma (2 sn) + bir tur
        after = changes(admin, before)
        chk(row(after, "HW-DLT1") is None, "aynı heartbeat satırı değişenlere koymadı")
        if after.get("version") == before:
            quiet = True
            break
        v = after.get("version", v)
    chk(quiet, "değişmeyen heartbeat'ler sürüm açmadı")
    v = changes(admin, v).get("version", v)
    started = time.monotonic()
    await agent.send(beat("HW-DLT1", status="Idle"))
    body = await wait_changes(admin, v, lambda b: (row(b, "HW-DLT1") or {}).get("status") == "Idle")
    chk(body.get("version", 0) > v and (row(body, "HW-DLT1") or {}).get("status") == "Idle",
        "durum değişti: sürüm arttı, satır değişenlerde (%.1f sn)" % (time.monotonic() - started))
    v = body.get("version", v)
    await agent.send(beat("HW-DLT1", status="Idle", window="excel"))
    body = await wait_changes(admin, v, lambda b: (row(b, "HW-DLT1") or {}).get("active_window") == "excel")
    chk((row(body, "HW-DLT1") or {}).get("active_window") == "excel" and body.get("removed") == [],
        "ön plandaki uygulama değişti: satır değişenlerde")
    v = body.get("version", v)

    print("== panel işlemi hemen görünür; silinen cihaz; eski since")
    chk(http("/api/rename_device", admin, {"pc_name": "HW-DLT1", "display_name": "Delta Test"})[0] == 200, "ad")
    body = changes(admin, v)
    chk((row(body, "HW-DLT1") or {}).get("display_name") == "Delta Test" and set(row(body, "HW-DLT1")) == KEYS,
        "yanıt dönmeden sürüm arttı: yeni ad değişenlerde, satır biçimi aynı")
    v = body.get("version", v)
    # Veritabanına doğrudan eklenen (listenin henüz bilmediği) cihaz, panelden taşınınca listeye girer
    await c.execute("INSERT INTO clients (pc_name, hostname, status, lab_name) VALUES ('HW-DLT2','hw-dlt2','Offline',"
                    "'Atanmamis_Cihazlar')")
    chk(http("/api/move_pc", admin, {"pc_name": "HW-DLT2", "new_lab": "DLT-Lab"})[0] == 200, "taşıma")
    body = changes(admin, v)
    chk((row(body, "HW-DLT2") or {}).get("lab") == "DLT-Lab", "taşınan cihaz değişenlerde")
    v2 = body.get("version", v)
    chk(http("/api/devices/HW-DLT2", admin, method="DELETE")[0] == 200, "silme")
    body = changes(admin, v2)
    chk(body.get("removed") == ["HW-DLT2"] and row(body, "HW-DLT2") is None, "silinen cihaz removed'da")
    body = changes(admin, v)
    chk(body.get("removed") == ["HW-DLT2"] and row(body, "HW-DLT2") is None,
        "eklenip silinen cihaz daha eski since'te de yalnızca removed'da")
    s, _h, old = http("/api/devices?since=1", admin)
    chk(old.get("full") is True and not any(d["hw_id"] == "HW-DLT2" for d in old.get("devices", []))
        and any(d["hw_id"] == "HW-DLT1" and d["display_name"] == "Delta Test" for d in old.get("devices", [])),
        "günlükten eski since: tam liste (full: true)")
    v = body.get("version", v)

    print("== panel soketi: devices_changed")
    cookie = {"Cookie": "pops_jwt=" + admin}
    panel = await websockets.connect(WS + "/ws/panel", additional_headers=cookie)
    only = await websockets.connect(WS + "/ws/panel?topics=devices", additional_headers=cookie)
    await asyncio.sleep(0.3)
    http("/api/rename_device", admin, {"pc_name": "HW-DLT1", "display_name": "Delta Test 2"})
    got_all, got_only = await asyncio.gather(collect(panel, 2.0), collect(only, 2.0))
    v_now = changes(admin, v).get("version")
    dc = [m for m in got_only if m.get("type") == "devices_changed"]
    chk(dc and dc[-1].get("version") == v_now and set(dc[-1]) == {"type", "version"},
        "topics=devices soketi devices_changed aldı (%s)" % dc)
    chk(not any(m.get("type") == "devices_changed" for m in got_all),
        "konusuz soket devices_changed almadı (yalnızca isteyen alır)")
    v = v_now
    # Ajanın yetenek bildirimi: bütün panellere "capabilities" yayını, satır değiştiği için devices_changed
    await agent.send(json.dumps({"type": "capabilities", "terminal_enabled": True, "vision_enabled": False}))
    got_all, got_only = await asyncio.gather(collect(panel, 2.5), collect(only, 2.5))
    chk(any(m.get("type") == "capabilities" for m in got_all)
        and not any(m.get("type") == "devices_changed" for m in got_all), "konusuz soket yetenek yayınını aldı")
    chk(got_only and all(m.get("type") == "devices_changed" for m in got_only),
        "topics=devices soketi yalnızca devices_changed aldı (%s)" % [m.get("type") for m in got_only])
    body = changes(admin, v)
    chk((row(body, "HW-DLT1") or {}).get("cap_vision_enabled") is False, "yetenek değişikliği listede")
    v = body.get("version", v)
    # Art arda değişiklik: bildirim saniyede en fazla bir kez
    for i in range(6):
        http("/api/rename_device", admin, {"pc_name": "HW-DLT1", "display_name": "Seri %d" % i})
        await asyncio.sleep(0.15)
    msgs = [m for m in await collect(only, 2.2) if m.get("type") == "devices_changed"]
    v_now = changes(admin, v).get("version")
    chk(1 <= len(msgs) <= 3 and msgs[-1].get("version") == v_now,
        "6 değişiklik ~1 sn içinde: %d bildirim, sonuncusu güncel sürüm" % len(msgs))
    v = v_now

    print("== kopan cihaz yaklaşık bir saniyede bildirilir")
    await collect(only, 0.3)
    started = time.monotonic()
    await agent.close()
    msgs = []
    while time.monotonic() - started < 5 and not msgs:
        msgs = [m for m in await collect(only, 0.2) if m.get("type") == "devices_changed"]
    elapsed = time.monotonic() - started
    body = changes(admin, v)
    chk(msgs and (row(body, "HW-DLT1") or {}).get("status") == "Offline" and elapsed < 2.5,
        "Offline bildirimi %.2f sn sonra geldi, satır değişenlerde" % elapsed)
    await panel.close()
    await only.close()


if __name__ == "__main__":
    t0 = time.time()
    asyncio.run(main())
    print("(%.1f sn)" % (time.time() - t0))
