"""Ajan 0.1.12'nin sunucu tarafı — entegrasyon testi (CI 'security' job'ı).

- Vision tüneli yalnızca cihazın kalıcı anahtarıyla açılır ("Kimlik zorlaması" kapalıyken de); kayıt jetonu geçmez.
- Cihaz başına bypass anahtarı: 0.1.12+ ajana anahtarlı bağlantıda gönderilir, parmak iziyle onaylanır, panel
  onaya göre cihaz kodunu (ve onay beklerken eski kodu) verir.
- Komut ve Vision oturumu mesajları isteyeni taşır (requested_by).
- Heartbeat'teki agent_health temizlenip saklanır ve /api/devices'ta döner.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import datetime
import hashlib
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import bypass  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
PCS = ["HW-K1", "HW-K2", "HW-K3"]
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    r.add_header("Content-Type", "application/json")
    r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=15) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


def dna(pc):
    return json.dumps(
        {
            "hw_id": pc,
            "dna_payload": {
                "hardware": {"uuid": pc + "-U", "bios_sn": pc + "-B", "disk_sn": "-", "mac": "-", "ram_sn": "-"},
                "capabilities": {"ram_readable": True},
            },
            "hostname": pc.lower(),
            "status": "Online",
        }
    )


async def collect(ws, seconds):
    """Süre dolana kadar gelen JSON mesajları (kapanırsa ('closed', kod) eklenir)."""
    out = []
    loop = asyncio.get_event_loop()
    end = loop.time() + seconds
    while loop.time() < end:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - loop.time()))
        except asyncio.TimeoutError:
            break
        except websockets.exceptions.ConnectionClosed as e:
            out.append(("closed", e.code))
            break
        try:
            out.append(json.loads(raw))
        except ValueError:
            pass
    return out


def action(msgs, name):
    return next((m for m in msgs if isinstance(m, dict) and m.get("action") == name), None)


async def vision_close_code(pc, headers):
    """Vision tünelini açmayı dener; sunucu kapatırsa kodu, açık kalırsa None döner."""
    try:
        async with websockets.connect("%s/ws/vision/%s" % (WS, pc), additional_headers=headers) as ws:
            msgs = await collect(ws, 1.5)
            return next((m[1] for m in msgs if isinstance(m, tuple)), None)
    except websockets.exceptions.ConnectionClosed as e:
        return e.code


async def wait_for(c, sql, *args, timeout=6):
    for _ in range(int(timeout / 0.2)):
        value = await c.fetchval(sql, *args)
        if value:
            return value
        await asyncio.sleep(0.2)
    return None


async def cleanup(c):
    # Önceki bir koşuda DNA eşleşmesiyle yeniden adlandırılmış test cihazları da silinir
    renamed = await c.fetch("SELECT pc_name FROM clients WHERE dna_uuid = ANY($1::text[])", [p + "-U" for p in PCS])
    names = PCS + [r["pc_name"] for r in renamed]
    for table, col in (("agent_secrets", "pc_name"), ("agent_bypass_keys", "pc_name"), ("clients", "pc_name"),
                       ("tasks", "target_pc"), ("agent_versions", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), names)


async def main():
    c = await conn()
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','0') "
        "ON CONFLICT (key) DO UPDATE SET value='0'"
    )
    await cleanup(c)
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-K1',$1)", _sha("k1-secret"))
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-K2',$1)", _sha("k2-secret"))
    await c.execute("DELETE FROM enroll_tokens WHERE token_hash=encode(sha256(convert_to('K-ENROLL','UTF8')),'hex')")
    await c.execute(
        "INSERT INTO enroll_tokens (token_hash, token_hint, expires_at, max_uses) "
        "VALUES (encode(sha256(convert_to('K-ENROLL','UTF8')),'hex'), 'K-ENRO', NOW() + interval '1 hour', 5)"
    )
    await c.execute(
        "INSERT INTO users (username,password_hash,role,permissions,token_version) "
        "VALUES ('kadmin','x','admin','[]',0) "
        "ON CONFLICT (username) DO UPDATE SET role='admin', token_version=0"
    )
    admin = server.create_jwt("kadmin", "admin", 0)
    today = datetime.date.today()

    print("== Vision: yalnızca cihaz anahtarı (zorlama kapalıyken de)")
    chk(await vision_close_code("HW-K1", {}) == 4401, "anahtarsız Vision tüneli → 4401")
    chk(await vision_close_code("HW-K1", {"X-Enroll-Token": "K-ENROLL"}) == 4401, "yalnız kayıt jetonu → 4401")
    chk(await vision_close_code("HW-K1", {"X-Agent-Secret": "k2-secret"}) == 4401, "başka cihazın anahtarı → 4401")
    chk(await vision_close_code("HW-K1", {"X-Agent-Secret": "k1-secret"}) is None, "doğru anahtar → tünel açık")
    first = await websockets.connect("%s/ws/vision/HW-K1" % WS, additional_headers={"X-Agent-Secret": "k1-secret"})
    await asyncio.sleep(0.3)
    second = await websockets.connect("%s/ws/vision/HW-K1" % WS, additional_headers={"X-Agent-Secret": "k1-secret"})
    await asyncio.sleep(0.3)
    await first.close()
    await asyncio.sleep(0.5)
    # Eski tünelin kapanması yeni tünelin kaydını silmemeli: uzaktan girdi yeni tünele ulaşır
    s, b = req("/api/audit/session/start", admin, {"target_pc": "HW-K1", "reason": "k", "is_mandatory": False})
    s2, r = req("/api/remote_input", admin, {"type": "remote_input", "device": "HW-K1", "input_type": "mouse_move",
                                             "data": {"x": 1, "y": 1}})
    got = await collect(second, 1.5)
    chk(r.get("status") == "success" and any(isinstance(m, dict) and m.get("input_type") for m in got),
        "eski tünel kapanınca yeni tünel kayıtlı kaldı (girdi ulaştı)")
    if s == 200:
        req("/api/audit/session/end", admin, {"session_id": b.get("session_id"), "status": "Completed"})
    await second.close()

    print("== bypass anahtarı + requested_by (0.1.12 ajan)")
    await c.execute(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, created_by) "
        "VALUES ('HW-K1', 'echo k', 'Pending', NOW()::text, 'kadmin')"
    )
    headers = {"X-Agent-Secret": "k1-secret", "X-Agent-Version": "0.1.12-alpha"}
    agent = await websockets.connect("%s/ws/agent/HW-K1" % WS, additional_headers=headers)
    await agent.send(dna("HW-K1"))
    msgs = await collect(agent, 3)
    sent = action(msgs, "set_bypass_secret")
    key = sent and sent.get("secret")
    chk(bool(key) and len(key) == 43, "anahtarlı bağlantıda set_bypass_secret geldi")
    execute = action(msgs, "execute")
    chk(execute is not None and execute.get("requested_by") == "kadmin", "execute mesajı isteyeni taşıyor")
    row = await c.fetchrow("SELECT fingerprint, confirmed_at FROM agent_bypass_keys WHERE pc_name='HW-K1'")
    chk(row is not None and row["confirmed_at"] is None, "anahtar kaydedildi, onay bekliyor")

    s, b = req("/api/security/bypass_token/HW-K1", admin, {})
    chk(s == 200 and b.get("method") == "pending", "onay gelmeden panel 'pending' diyor")
    n0 = b.get("n") or 0
    chk(key and b.get("token") == bypass.device_code(key, "HW-K1", today, n0), "pending: kod cihaz anahtarıyla")
    chk(b.get("fallback_token") == bypass.legacy_code("HW-K1", today), "pending: yedek kod eski formülle")

    await agent.send(json.dumps({"type": "bypass_secret_ack", "fingerprint": "0000000000000000"}))
    await asyncio.sleep(0.5)
    chk(
        await c.fetchval("SELECT confirmed_at FROM agent_bypass_keys WHERE pc_name='HW-K1'") is None,
        "yanlış parmak izi anahtarı onaylamadı",
    )
    await agent.send(json.dumps({"type": "bypass_secret_ack", "fingerprint": bypass.fingerprint(key)}))
    chk(
        await wait_for(c, "SELECT confirmed_at FROM agent_bypass_keys WHERE pc_name='HW-K1'") is not None,
        "doğru parmak izi anahtarı onayladı",
    )
    s, b = req("/api/security/bypass_token/HW-K1", admin, {})
    chk(
        b.get("method") == "device" and b.get("token") == bypass.device_code(key, "HW-K1", today, n0 + 1)
        and not b.get("fallback_token"),
        "onaydan sonra yalnızca cihaz kodu",
    )
    chk(b.get("n") == n0 + 1, "her istek günün bir sonraki kodunu verir (0.1.13 ajanı kodu bir kez kabul eder)")

    s, b = req("/api/audit/session/start", admin, {"target_pc": "HW-K1", "reason": "k", "is_mandatory": False})
    msgs = await collect(agent, 2)
    started = action(msgs, "start_vision_session")
    chk(started is not None and started.get("requested_by") == "kadmin", "Vision oturumu isteyeni taşıyor")
    if s == 200:
        req("/api/audit/session/end", admin, {"session_id": b.get("session_id"), "status": "Completed"})

    print("== agent_health")
    health = {
        "started_at": 1700000000,
        "last_policy_sync": 1700000100,
        "last_inventory_upload": None,
        "tray_connected": True,
        "vision_channel": "garip",
        "loop_errors_1h": 3,
        "last_error": "x" * 500,
        "extra": "yok sayılır",
    }
    await agent.send(json.dumps({"hw_id": "HW-K1", "hostname": "hw-k1", "status": "Online", "agent_health": health}))
    await asyncio.sleep(0.8)
    s, devices = req("/api/devices", admin)
    dev = next((d for d in devices if d.get("hw_id") == "HW-K1"), {}) if s == 200 else {}
    h = dev.get("agent_health") or {}
    chk(h.get("loop_errors_1h") == 3 and h.get("tray_connected") is True, "sağlık özeti saklandı")
    chk(
        len(h.get("last_error") or "") == 200 and h.get("vision_channel") is None,
        "metin kırpıldı, geçersiz değer atıldı",
    )
    chk("extra" not in h, "bilinmeyen alan saklanmadı")
    chk(dev.get("bypass_key") == "device", "cihaz listesinde bypass anahtarı onaylı görünüyor")
    await agent.send(json.dumps({"hw_id": "HW-K1", "hostname": "hw-k1", "status": "Online"}))
    await asyncio.sleep(0.8)
    s, devices = req("/api/devices", admin)
    dev = next((d for d in devices if d.get("hw_id") == "HW-K1"), {})
    chk(dev.get("agent_health") is None, "sağlık bildirmeyen heartbeat eski özeti siler")
    await agent.close()

    agent = await websockets.connect("%s/ws/agent/HW-K1" % WS, additional_headers=headers)
    await agent.send(dna("HW-K1"))
    chk(action(await collect(agent, 2), "set_bypass_secret") is None, "onaylı anahtar yeniden gönderilmedi")
    await agent.close()

    print("== eski ajan ve enroll bağlantısı")
    agent = await websockets.connect(
        "%s/ws/agent/HW-K2" % WS, additional_headers={"X-Agent-Secret": "k2-secret", "X-Agent-Version": "0.1.11-alpha"}
    )
    await agent.send(dna("HW-K2"))
    chk(action(await collect(agent, 2), "set_bypass_secret") is None, "0.1.11 ajana bypass anahtarı gönderilmedi")
    await agent.close()
    agent = await websockets.connect(
        "%s/ws/agent/HW-K3" % WS, additional_headers={"X-Enroll-Token": "K-ENROLL", "X-Agent-Version": "0.1.12-alpha"}
    )
    await agent.send(dna("HW-K3"))
    msgs = await collect(agent, 2)
    chk(action(msgs, "set_secret") is not None, "yeni cihaz kayıt oldu")
    chk(action(msgs, "set_bypass_secret") is None, "kayıt bağlantısında bypass anahtarı gönderilmedi")
    await agent.close()

    await cleanup(c)
    await c.close()
    if FAILS:
        print("\n%d KONTROL BAŞARISIZ" % len(FAILS))
        sys.exit(1)
    print("\nTUM CIHAZ ANAHTARI TESTLERI GECTI")


if __name__ == "__main__":
    asyncio.run(main())
