"""Ajan kimlik/yetki entegrasyon testi (pentest F2 + F3) — CI 'security' job'ında koşar.

F3: agent_http_auth doğrulanan kimliği hedef pc_name/hw_id'ye bağlar (cross-device sahtecilik
    engellenir); enforce açıkken secret şart, kapalıyken legacy kabul ama geçerli secret sunulursa
    yine hedefe bağlanır.
F2: /ws/agent enroll bloğu, zaten secret'ı olan cihaza düz enroll token'la yeniden-secret vermeyi
    reddeder (impersonation); allow_reenroll açıkken meşru yeniden-kayda izin verir + bayrağı temizler;
    secret'ı olmayan yeni cihaz normal enroll olur.

Ortam: POPS_TEST_HTTP + POPS_TEST_WS + DB_* + JWT_SECRET.
"""

import asyncio
import hashlib
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def http(path, method="POST", body=None, headers=None):
    data = json.dumps(body).encode() if body is not None else b"{}"
    r = urllib.request.Request(HTTP + path, data=data, method=method)
    r.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=10) as resp:
            return resp.status
    except urllib.error.HTTPError as e:
        return e.code


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


async def setup(c):
    await c.execute("DELETE FROM agent_secrets WHERE pc_name = ANY($1::text[])", ["HW-A", "HW-B", "HW-C", "HW-D"])
    await c.execute("DELETE FROM clients WHERE pc_name = ANY($1::text[])", ["HW-A", "HW-B", "HW-C", "HW-D"])
    await c.execute("DELETE FROM enroll_tokens WHERE token_hash=encode(sha256(convert_to('TEST-ENROLL','UTF8')),'hex')")
    # HW-A: HTTP F3 için secret'lı
    await c.execute("INSERT INTO clients (pc_name, status) VALUES ('HW-A','Offline')")
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-A',$1)", _sha("secretA"))
    # HW-B: enroll takeover hedefi — secret + dna + allow_reenroll FALSE
    await c.execute(
        "INSERT INTO clients (pc_name, status, dna_uuid, dna_bios, allow_reenroll) "
        "VALUES ('HW-B','Offline','UUID-B','BIOS-B',FALSE)"
    )
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-B',$1)", _sha("secretB"))
    # HW-D: meşru yeniden-kayıt — secret + dna + allow_reenroll TRUE
    await c.execute(
        "INSERT INTO clients (pc_name, status, dna_uuid, dna_bios, allow_reenroll) "
        "VALUES ('HW-D','Offline','UUID-D','BIOS-D',TRUE)"
    )
    await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ('HW-D',$1)", _sha("secretD"))
    # Çok-kullanımlık enroll token
    await c.execute(
        # Jetonlar yalnızca SHA-256 özetiyle saklanır (migration 0014)
        "INSERT INTO enroll_tokens (token_hash, token_hint, expires_at, max_uses, use_count, is_used) "
        "VALUES (encode(sha256(convert_to('TEST-ENROLL','UTF8')),'hex'), 'TEST-E', "
        "NOW() + interval '1 hour', 100, 0, FALSE)"
    )


async def set_enforce(c, on):
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth',$1) "
        "ON CONFLICT (key) DO UPDATE SET value=$1",
        "1" if on else "0",
    )


def dna_msg(uuid, bios):
    return json.dumps(
        {
            "dna_payload": {
                "hardware": {"uuid": uuid, "bios_sn": bios, "disk_sn": "-", "mac": "-", "ram_sn": "-"},
                "capabilities": {"ram_readable": True},
            },
            "hostname": "testpc",
            "status": "Online",
        }
    )


async def ws_enroll(pc, uuid, bios, token="TEST-ENROLL"):
    """/ws/agent'a enroll token'la bağlan, dna gönder; (closed_code, got_set_secret) döndür."""
    uri = "%s/ws/agent/%s" % (WS, pc)
    got_secret = False
    closed = None
    try:
        async with websockets.connect(
            uri, additional_headers={"X-Enroll-Token": token, "X-Agent-Version": "test"}
        ) as ws:
            await ws.send(dna_msg(uuid, bios))
            for _ in range(6):
                try:
                    raw = await asyncio.wait_for(ws.recv(), timeout=4)
                except asyncio.TimeoutError:
                    break
                try:
                    m = json.loads(raw)
                except Exception:
                    continue
                if m.get("action") == "set_secret":
                    got_secret = True
                    break
    except websockets.exceptions.ConnectionClosed as e:
        closed = e.code
    return closed, got_secret


async def main():
    c = await conn()
    passed = 0

    def chk(cond, msg):
        nonlocal passed
        assert cond, "FAIL: " + msg
        passed += 1
        print("  ok:", msg)

    await setup(c)

    # ---- F3: HTTP kimlik-hedef bağlama (enforce AÇIK) ----
    await set_enforce(c, True)
    hA = {"X-Agent-Id": "HW-A", "X-Agent-Secret": "secretA"}
    chk(
        http("/api/inventory/HW-A", body={"hostname": "a"}, headers=hA) == 200,
        "F3: geçerli secret + kendi cihazı → 200",
    )
    chk(
        http("/api/inventory/HW-B", body={"hostname": "b"}, headers=hA) == 403,
        "F3: A'nın secret'ıyla B'ye envanter → 403 (binding)",
    )
    chk(http("/api/inventory/HW-A", body={"hostname": "a"}) == 401, "F3: secret yok + enforce açık → 401")
    chk(
        http("/api/policy_alert", body={"hw_id": "HW-B", "domain": "x", "category": "y"}, headers=hA) == 403,
        "F3: A'nın secret'ıyla B için policy_alert → 403",
    )
    chk(
        http("/api/auth/login", body={"hw_id": "HW-B", "hostname": "b", "student_id": "s"}, headers=hA) == 403,
        "F3: A'nın secret'ıyla B için auth/login → 403",
    )
    chk(
        http("/api/inventory/HW-A", body={"hostname": "a"}, headers={"X-Agent-Id": "HW-A", "X-Agent-Secret": "WRONG"})
        == 401,
        "F3: yanlış secret + enforce açık → 401",
    )

    # ---- F3: enforce KAPALI (anahtarı olmayan eski ajan kabul; anahtarı olan cihaz adına anahtarsız istek değil) ----
    await set_enforce(c, False)
    chk(
        http("/api/auth/login", body={"hw_id": "HW-C", "hostname": "c", "student_id": "s"}) == 200,
        "F3: anahtarı olmayan cihaz + enforce kapalı → 200 (legacy)",
    )
    chk(
        http("/api/inventory/HW-A", body={"hostname": "a"}) == 401,
        "F3: anahtarı olan cihaz adına anahtarsız istek, enforce kapalı olsa da → 401",
    )
    chk(
        http("/api/inventory/HW-B", body={"hostname": "b"}, headers=hA) == 403,
        "F3: enforce kapalı OLSA DA geçerli A secret'ıyla B'ye → 403 (binding)",
    )

    # ---- F2: enroll ile secret ele geçirme (enforce durumundan bağımsız) ----
    b_before = await c.fetchval("SELECT secret_hash FROM agent_secrets WHERE pc_name='HW-B'")
    closed, got = await ws_enroll("HW-B", "UUID-B", "BIOS-B")
    b_after = await c.fetchval("SELECT secret_hash FROM agent_secrets WHERE pc_name='HW-B'")
    chk(
        not got and b_after == b_before,
        "F2: zaten kayıtlı B'ye enroll ile yeniden-secret REDDEDİLDİ (secret değişmedi)",
    )
    chk(closed == 4401, "F2: takeover girişimi 4401 ile kapatıldı")

    # ---- F2: secret'ı olmayan yeni cihaz normal enroll olur ----
    closed, got = await ws_enroll("HW-C", "UUID-C", "BIOS-C")
    c_secret = await c.fetchval("SELECT secret_hash FROM agent_secrets WHERE pc_name='HW-C'")
    chk(got and c_secret is not None, "F2: yeni cihaz (secret yok) normal enroll → set_secret + secret kaydı")

    # ---- F2: allow_reenroll açık cihaz meşru yeniden-kayıt olur + bayrak temizlenir ----
    d_before = await c.fetchval("SELECT secret_hash FROM agent_secrets WHERE pc_name='HW-D'")
    closed, got = await ws_enroll("HW-D", "UUID-D", "BIOS-D")
    d_after = await c.fetchval("SELECT secret_hash FROM agent_secrets WHERE pc_name='HW-D'")
    d_flag = await c.fetchval("SELECT allow_reenroll FROM clients WHERE pc_name='HW-D'")
    chk(got and d_after != d_before, "F2: allow_reenroll açık → yeniden-secret verildi")
    chk(d_flag is False, "F2: başarılı yeniden-enroll sonrası allow_reenroll otomatik kapandı")

    await set_enforce(c, False)
    await c.close()
    print("\nTUM AJAN YETKI TESTLERI GECTI (%d kontrol)" % passed)


if __name__ == "__main__":
    asyncio.get_event_loop().run_until_complete(main())
