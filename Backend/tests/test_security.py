#!/usr/bin/env python3
"""POps güvenlik değişmezleri — tekrarlanabilir entegrasyon testi.

"Kanıtladık" yerine "şu testi çalıştır" demek için. Çalışan bir sunucuya (varsayılan
http://127.0.0.1:8099) ve onun DB'sine karşı şunları doğrular:

  1. Enroll: geçerli enroll token'la bağlanan ajana secret verilir (accept-both).
  2. enforce açıkken KİMLİKSİZ /ws/agent bağlantısı 4401 ile kapatılır (impersonation kapalı).
  3. enforce açıkken GEÇERLİ secret ile /ws/agent açık kalır.
  4. enforce açıkken kimliksiz ajan HTTP ucu 401; X-Agent-Id + X-Agent-Secret ile 200.
  5. upload-release: YANLIŞ anahtarla imzalanmış manifest reddedilir (imza zorlaması).

Ortam: DB_HOST/PORT/USER/PASS/NAME (test DB), JWT_SECRET (sunucuyla aynı),
POPS_TEST_HTTP (varsayılan http://127.0.0.1:8099). Sunucu ayrıca çalışıyor olmalı.
Python 3.9 uyumlu. Bkz. tests/run_local.sh (yerel) ve .github/workflows/ci.yml (CI).
"""
import asyncio
import hashlib
import json
import os
import sys
import urllib.error
import urllib.request

import asyncpg
import websockets
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

HTTP = os.environ.get("POPS_TEST_HTTP", "http://127.0.0.1:8099").rstrip("/")
WS = "ws" + HTTP[4:]  # http->ws, https->wss
SERVER_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir)

_ok = True


def check(name, cond):
    global _ok
    print(("  ✔ " if cond else "  ✘ ") + name)
    _ok = _ok and bool(cond)


async def db():
    return await asyncpg.connect(host=os.environ.get("DB_HOST", "localhost"),
                                 port=int(os.environ.get("DB_PORT", "5432")),
                                 user=os.environ["DB_USER"], password=os.environ["DB_PASS"],
                                 database=os.environ["DB_NAME"])


def dna(host, mac):
    return json.dumps({"dna_payload": {"hardware": {"uuid": host + "-U", "bios_sn": host + "-B",
                                                    "disk_sn": host + "-D", "mac": mac, "ram_sn": host + "-R"},
                                       "capabilities": {"ram_readable": True}},
                       "hostname": host, "status": "Online"})


def http_post(path, body, headers=None):
    req = urllib.request.Request(HTTP + path, data=json.dumps(body).encode(),
                                 method="POST", headers={"Content-Type": "application/json", **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=8) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code


async def ws_closed_code(pc, headers=None):
    try:
        kw = {"additional_headers": headers} if headers else {}
        async with websockets.connect(WS + "/ws/agent/" + pc, **kw) as ws:
            try:
                await asyncio.wait_for(ws.recv(), timeout=2)
            except asyncio.TimeoutError:
                return None  # açık kaldı
    except websockets.exceptions.ConnectionClosed as e:
        return getattr(e, "code", None) or (e.rcvd.code if getattr(e, "rcvd", None) else None)
    return None


def _superadmin_jwt():
    sys.path.insert(0, SERVER_DIR)
    import server
    return server.create_jwt("integration-test", "superadmin")


def _wrong_key_signed_manifest():
    """Gerçek olmayan bir ed25519 anahtarıyla imzalanmış manifest + imza (base64)."""
    manifest = {"schema": "pops-manifest/1", "version": "9.9.9", "tag": "v9.9.9",
                "released_at": 1700000000,
                "artifacts": [{"name": "POps-Agent-9.9.9-win-x64.msi", "sha256": "0" * 64, "size": 1}]}
    payload = (json.dumps(manifest, indent=2, sort_keys=True, ensure_ascii=False) + "\n").encode("utf-8")
    key = Ed25519PrivateKey.generate()
    import base64
    return payload, base64.b64encode(key.sign(payload))


def _multipart(fields):
    """fields: list of (name, filename, bytes). Basit multipart/form-data gövdesi kurar."""
    boundary = "----popstest7f3a"
    out = b""
    for name, filename, data in fields:
        out += ("--%s\r\nContent-Disposition: form-data; name=\"%s\"; filename=\"%s\"\r\n"
                "Content-Type: application/octet-stream\r\n\r\n" % (boundary, name, filename)).encode()
        out += data + b"\r\n"
    out += ("--%s--\r\n" % boundary).encode()
    return out, "multipart/form-data; boundary=%s" % boundary


async def main():
    conn = await db()

    async def set_enforce(v):
        await conn.execute("INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth',$1) "
                           "ON CONFLICT (key) DO UPDATE SET value=$1", v)

    # 1) enroll -> secret (accept-both)
    await set_enforce("0")
    await conn.execute("INSERT INTO enroll_tokens (token, lab_name, expires_at, max_uses) "
                       "VALUES ('INTEGTOK1', NULL, NOW()+interval '1 hour', 5) ON CONFLICT (token) DO NOTHING")
    secret = None
    async with websockets.connect(WS + "/ws/agent/HW-INTEG", additional_headers=[("X-Enroll-Token", "INTEGTOK1")]) as ws:
        await ws.send(dna("HW-INTEG", "AA:BB:CC:00:11:01"))
        try:
            for _ in range(6):
                d = json.loads(await asyncio.wait_for(ws.recv(), timeout=3))
                if d.get("action") == "set_secret":
                    secret = d["secret"]
                    break
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            pass
    check("1) enroll token -> secret verildi", bool(secret))
    enr_id = await conn.fetchval("SELECT pc_name FROM agent_secrets ORDER BY created_at DESC LIMIT 1")

    # 2-4) enforce ON
    await set_enforce("1")
    check("2) enforce: kimliksiz /ws/agent -> 4401", (await ws_closed_code("HW-IMPOSTOR")) == 4401)
    check("3) enforce: gecerli secret -> acik", (await ws_closed_code(enr_id, [("X-Agent-Secret", secret)])) is None)
    logout = {"hw_id": enr_id, "hostname": "h", "student_id": "s"}
    check("4a) enforce: kimliksiz HTTP -> 401", http_post("/api/auth/logout", logout) == 401)
    check("4b) enforce: gecerli secret HTTP -> 200",
          http_post("/api/auth/logout", logout, {"X-Agent-Id": enr_id, "X-Agent-Secret": secret}) == 200)

    # 5) yanlis anahtarla imzali paket reddedilir
    payload, sig = _wrong_key_signed_manifest()
    body, ctype = _multipart([("files", "manifest.json", payload), ("files", "manifest.json.sig", sig)])
    req = urllib.request.Request(HTTP + "/api/system/upload-release", data=body, method="POST",
                                 headers={"Content-Type": ctype, "Authorization": "Bearer " + _superadmin_jwt()})
    try:
        with urllib.request.urlopen(req, timeout=8) as r:
            code = r.status
    except urllib.error.HTTPError as e:
        code = e.code
    check("5) yanlis-imzali upload-release -> 400 red", code == 400)

    await set_enforce("0")
    await conn.close()
    print("SONUC:", "TUM GUVENLIK DEGISMEZLERI GECTI" if _ok else "BASARISIZ")
    sys.exit(0 if _ok else 1)


asyncio.run(main())
