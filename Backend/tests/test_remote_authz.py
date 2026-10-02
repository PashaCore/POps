"""Uzaktan girdi/önizleme yetkisi + kare scope'lama testi (pentest F1 + F12).

F1: uzaktan girdi (POST /api/remote_input ve /ws/panel) YALNIZCA admin + o cihaz için AÇIK
    denetim oturumu olan kullanıcıdan kabul edilir; viewer ve oturumsuz reddedilir. Ekran
    önizlemesi (/api/thumbnail) viewer'a kapalı.
F12: canlı ekran kareleri yalnızca o cihaz için açık oturumu olan admin paneline gider;
     oturumsuz/viewer paneller kareyi ALMAZ.

Ortam: POPS_TEST_HTTP + POPS_TEST_WS + DB_* + JWT_SECRET (uvicorn ile aynı JWT_SECRET şart!).
"""

import asyncio
import hashlib
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import server  # noqa: E402  (create_jwt, JWT_COOKIE_NAME)
import asyncpg  # noqa: E402
import websockets  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http", "ws", 1))


def http(path, method="POST", body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method)
    if data is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=10) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read().decode())
        except Exception:
            return e.code, {}


async def recv_timeout(ws, t):
    try:
        return await asyncio.wait_for(ws.recv(), timeout=t)
    except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
        return None


def cookie(jwt):
    return {"Cookie": "%s=%s" % (server.JWT_COOKIE_NAME, jwt)}


async def main():
    c = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','0') "
        "ON CONFLICT (key) DO UPDATE SET value='0'"
    )
    await c.execute("DELETE FROM enterprise_audit_logs WHERE target_pc IN ('HW-X','HW-Y')")
    # Vision tüneli yalnızca cihaz anahtarıyla açılır (bkz. test_device_keys.py)
    await c.execute(
        "INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-X', $1) "
        "ON CONFLICT (pc_name) DO UPDATE SET secret_hash=$1",
        hashlib.sha256(b"hwx-secret").hexdigest(),
    )
    # F4: verify_session JWT 'sub'unun DB'de olmasını ister → panel kullanıcılarını seed et (token_version 0).
    for uname, role in (("admin1", "admin"), ("viewer1", "viewer")):
        await c.execute(
            "INSERT INTO users (username, password_hash, role, permissions, token_version) "
            "VALUES ($1, 'x', $2, '[]', 0) ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            uname,
            role,
        )
    passed = 0

    def chk(cond, msg):
        nonlocal passed
        assert cond, "FAIL: " + msg
        passed += 1
        print("  ok:", msg)

    admin_jwt = server.create_jwt("admin1", "admin")
    viewer_jwt = server.create_jwt("viewer1", "viewer")

    def rinput(dev):
        return {"type": "remote_input", "device": dev, "input_type": "mouse_move", "data": {"x": 1, "y": 1}}

    # Geçersiz token ile panel bağlantısı reddedilmeli
    try:
        bad = await websockets.connect(WS + "/ws/panel", additional_headers={"Cookie": "pops_jwt=bogus"})
        closed = await recv_timeout(bad, 2)
        chk(closed is None, "geçersiz token panel bağlantısı kapatıldı")
        await bad.close()
    except websockets.exceptions.InvalidStatus:
        chk(True, "geçersiz token panel bağlantısı reddedildi")

    admin_panel = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(admin_jwt))
    viewer_panel = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(viewer_jwt))

    # Admin, HW-X için denetim oturumu açar
    s, b = http(
        "/api/audit/session/start", body={"target_pc": "HW-X", "reason": "test", "is_mandatory": True}, token=admin_jwt
    )
    chk(s == 200 and b.get("session_id"), "admin denetim oturumu açtı")
    session_id = b["session_id"]

    # Ajanı taklit et: HW-X vision tüneli açıp kare gönder
    vision = await websockets.connect(WS + "/ws/vision/HW-X", additional_headers={"X-Agent-Secret": "hwx-secret"})
    await vision.send(json.dumps({"type": "stream_frame", "pc_name": "HW-X", "image": "FRAME1"}))
    admin_got = await recv_timeout(admin_panel, 3)
    viewer_got = await recv_timeout(viewer_panel, 2)
    chk(admin_got and "FRAME1" in admin_got, "F12: oturumlu admin kareyi ALDI")
    chk(viewer_got is None, "F12: oturumsuz viewer kareyi ALMADI")

    # F1: /api/remote_input
    s, _ = http("/api/remote_input", body=rinput("HW-X"), token=admin_jwt)
    chk(s != 403, "F1: admin + açık oturum → girdi geçti (403 değil)")
    s, _ = http("/api/remote_input", body=rinput("HW-Y"), token=admin_jwt)
    chk(s == 403, "F1: admin ama oturumsuz cihaza girdi → 403")
    s, _ = http("/api/remote_input", body=rinput("HW-X"), token=viewer_jwt)
    chk(s == 403, "F1: viewer girdi → 403 (require_admin)")

    # F1: önizleme viewer'a kapalı
    s, _ = http("/api/thumbnail/HW-X", method="GET", token=viewer_jwt)
    chk(s == 403, "F1: viewer thumbnail → 403")
    s, _ = http("/api/thumbnail/HW-X", method="GET", token=admin_jwt)
    chk(s == 200, "F1: admin thumbnail → 200 (ajan yoksa image=null ama yetki var)")

    # Oturum bitince kare akışı durur
    http("/api/audit/session/end", body={"session_id": session_id, "status": "Completed"}, token=admin_jwt)
    await vision.send(json.dumps({"type": "stream_frame", "pc_name": "HW-X", "image": "FRAME2"}))
    admin_got2 = await recv_timeout(admin_panel, 2)
    chk(admin_got2 is None, "F12: oturum bitince admin artık kare ALMIYOR")
    # ve girdi de reddedilir
    s, _ = http("/api/remote_input", body=rinput("HW-X"), token=admin_jwt)
    chk(s == 403, "F1: oturum bitince admin girdi → 403")

    # F1 kalıntısı (final review): thumbnail (ekran görüntüsü) /ws/agent'tan gelir; YALNIZCA admin
    # panellere gitmeli, viewer'a ASLA. (Oturum yok bile olsa admin rolüyle alır; viewer alamaz.)
    agent = await websockets.connect(
        WS + "/ws/agent/HW-X", additional_headers={"X-Agent-Version": "test", "X-Agent-Secret": "hwx-secret"}
    )
    await agent.send(
        json.dumps(
            {
                "dna_payload": {
                    "hardware": {"uuid": "HW-X-U", "bios_sn": "HW-X-B", "disk_sn": "-", "mac": "-", "ram_sn": "-"},
                    "capabilities": {"ram_readable": True},
                },
                "hostname": "hwx",
                "status": "Online",
            }
        )
    )
    await agent.send(json.dumps({"type": "thumbnail", "hw_id": "HW-X", "image": "THUMB1"}))
    admin_thumb = await recv_timeout(admin_panel, 3)
    viewer_thumb = await recv_timeout(viewer_panel, 2)
    chk(admin_thumb and "THUMB1" in admin_thumb, "F1: thumbnail admin panele ulaştı (rol bazlı)")
    chk(viewer_thumb is None, "F1: thumbnail viewer'a SIZMADI (broadcast_to_admin_panels)")
    try:
        await agent.close()
    except Exception:
        pass

    for w in (admin_panel, viewer_panel, vision):
        try:
            await w.close()
        except Exception:
            pass
    await c.close()
    print("\nTUM UZAKTAN-YETKI TESTLERI GECTI (%d kontrol)" % passed)


if __name__ == "__main__":
    asyncio.get_event_loop().run_until_complete(main())
