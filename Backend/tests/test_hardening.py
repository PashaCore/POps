"""0.1.13 sağlamlaştırması — entegrasyon testi (CI 'security' job'ı).

- Reddedilen kayıt denemesi hiçbir şeyi değiştirmez; meşru bağlantı kayıtlı kalır (F03).
- Kayıt jetonu eşzamanlı denemelerde kullanım sınırını aşmaz (F02); jeton yalnızca özetle saklanır.
- Anahtarla bağlanan cihazın kimliği donanım bilgisine göre başka cihaza kaymaz (F04).
- Görevin akıbeti: bağlantı kopunca, ajan yeniden başlayınca, çıkış kodu, iptal (F05).
- Yetkisi alınan panel oturumu kendiliğinden kapanır (F06).
- Bir cihaz başka cihazın ekran görüntüsü yerine geçemez (F16).
- /download yalnızca imzalı adresle (dağıtım paketleri).
- Cihaz silme tek işlemde; görevleri kapanır (F17).
- Karantinada ağ yalıtımı uygulanamazsa kilit işlemi tamamlanmış sayılmaz.

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
import uuid

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
PCS = ["HW-H1", "HW-H2", "HW-H3", "HW-H4"] + ["HW-HE%d" % i for i in range(8)]
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token=None, body=None, method=None, raw=False, headers=None):
    data = json.dumps(body).encode() if isinstance(body, (dict, list)) else body
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if isinstance(body, (dict, list)):
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            payload = resp.read()
            return resp.status, (payload if raw else json.loads(payload or b"{}"))
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


def dna(pc, uid=None, health=None):
    msg = {
        "hw_id": pc,
        "dna_payload": {
            "hardware": {"uuid": uid or pc + "-U", "bios_sn": pc + "-B", "disk_sn": pc + "-D", "mac": "-",
                         "ram_sn": "-"},
            "capabilities": {"ram_readable": True},
        },
        "hostname": pc.lower(),
        "status": "Online",
    }
    if health is not None:
        msg["agent_health"] = health
    return json.dumps(msg)


async def collect(ws, seconds):
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


def find(msgs, key, value):
    return next((m for m in msgs if isinstance(m, dict) and m.get(key) == value), None)


async def agent(pc, headers, first):
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc), additional_headers=headers)
    try:
        await ws.send(first)
    except websockets.exceptions.ConnectionClosed:
        pass  # sunucu ilk mesajdan önce reddetti; collect kapanış kodunu görür
    return ws


async def cleanup(c):
    renamed = await c.fetch("SELECT pc_name FROM clients WHERE dna_uuid = ANY($1::text[])", [p + "-U" for p in PCS])
    names = PCS + [r["pc_name"] for r in renamed]
    for table, col in (("agent_secrets", "pc_name"), ("agent_bypass_keys", "pc_name"), ("clients", "pc_name"),
                       ("tasks", "target_pc"), ("agent_versions", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), names)


async def wait_for(c, sql, *args, timeout=8):
    for _ in range(int(timeout / 0.2)):
        value = await c.fetchval(sql, *args)
        if value:
            return value
        await asyncio.sleep(0.2)
    return await c.fetchval(sql, *args)


async def main():
    c = await conn()
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','1') "
        "ON CONFLICT (key) DO UPDATE SET value='1'"
    )
    await cleanup(c)
    for pc, sec in (("HW-H1", "h1-secret"), ("HW-H2", "h2-secret"), ("HW-H3", "h3-secret")):
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc, _sha(sec))
    for u, role in (("hadmin", "admin"), ("hsuper", "superadmin")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            u,
            role,
        )
    admin = server.create_jwt("hadmin", "admin", 0)
    superadmin = server.create_jwt("hsuper", "superadmin", 0)
    v13 = "0.1.13-alpha"

    print("== kayıt jetonu yalnızca özetle saklanır")
    s, created = req("/api/system/enroll-token", superadmin, {"max_uses": 1, "ttl_hours": 1})
    token = created.get("token")
    made_tokens = [token]
    row = await c.fetchrow("SELECT token, token_hash, token_hint FROM enroll_tokens WHERE token_hash=$1",
                           _sha(token or ""))
    chk(s == 200 and row is not None and row["token"] is None, "veritabanında jetonun kendisi yok, özeti var")
    s, listed = req("/api/system/enroll-tokens", superadmin)
    mine = next((r for r in listed if r.get("token_hint") == token[:6]), None) if s == 200 else None
    chk(mine is not None and "token" not in mine, "listede yalnızca ilk 6 karakter")

    print("== F02: tek kullanımlık jeton, 8 eşzamanlı kayıt denemesi")
    heads = {"X-Enroll-Token": token, "X-Agent-Version": v13}
    sockets = await asyncio.gather(*[agent("HW-HE%d" % i, heads, dna("HW-HE%d" % i)) for i in range(8)],
                                   return_exceptions=True)
    results = await asyncio.gather(*[collect(ws, 3) for ws in sockets if not isinstance(ws, Exception)])
    granted = sum(1 for msgs in results if find(msgs, "action", "set_secret"))
    chk(granted == 1, "yalnızca bir cihaz anahtar aldı (%d)" % granted)
    use_count = await c.fetchval("SELECT use_count FROM enroll_tokens WHERE token_hash=$1", _sha(token))
    secrets_made = await c.fetchval(
        "SELECT count(*) FROM agent_secrets WHERE pc_name = ANY($1::text[])", ["HW-HE%d" % i for i in range(8)]
    )
    chk(use_count == 1 and secrets_made == 1, "jeton bir kez tüketildi, bir anahtar yazıldı")
    for ws in sockets:
        if not isinstance(ws, Exception):
            await ws.close()

    print("== F03: reddedilen kayıt denemesi meşru bağlantıyı bozmaz")
    legit = await agent("HW-H1", {"X-Agent-Secret": "h1-secret", "X-Agent-Version": v13}, dna("HW-H1"))
    await collect(legit, 1.5)
    boot_before = await c.fetchval("SELECT boot_count FROM clients WHERE pc_name='HW-H1'")
    s, created = req("/api/system/enroll-token", superadmin, {"max_uses": 5, "ttl_hours": 1})
    made_tokens.append(created["token"])
    attacker = await agent("HW-H1", {"X-Enroll-Token": created["token"], "X-Agent-Version": v13}, dna("HW-H1"))
    att_msgs = await collect(attacker, 2)
    chk(("closed", 4401) in att_msgs and not find(att_msgs, "action", "set_secret"), "saldırgan 4401 aldı")
    chk(await c.fetchval("SELECT boot_count FROM clients WHERE pc_name='HW-H1'") == boot_before,
        "cihaz kaydı değişmedi")
    used = await c.fetchval("SELECT use_count FROM enroll_tokens WHERE token_hash=$1", _sha(created["token"]))
    chk(used == 0, "jeton tüketilmedi")
    # Meşru bağlantı hâlâ kayıtlı: sunucunun istediği önizleme ona ulaşır
    thumb = asyncio.get_event_loop().run_in_executor(None, lambda: req("/api/thumbnail/HW-H1", admin))
    msgs = await collect(legit, 2)
    asked = find(msgs, "action", "get_thumbnail")
    if asked:
        await legit.send(json.dumps({"type": "thumbnail", "hw_id": "HW-H1", "image": "LEGIT"}))
    s, b = await thumb
    chk(asked is not None and b.get("image") == "LEGIT", "meşru ajan kayıtlı kaldı (önizleme ona gitti)")

    print("== F16: başka cihazın ekran görüntüsü yerine geçilemez")
    other = await agent("HW-H2", {"X-Agent-Secret": "h2-secret", "X-Agent-Version": v13}, dna("HW-H2"))
    await collect(other, 1.5)
    thumb = asyncio.get_event_loop().run_in_executor(None, lambda: req("/api/thumbnail/HW-H1", admin))
    await asyncio.sleep(0.5)
    await other.send(json.dumps({"type": "thumbnail", "hw_id": "HW-H1", "image": "FAKE"}))
    await collect(legit, 1)
    s, b = await thumb
    chk(b.get("image") != "FAKE", "H2'nin gönderdiği görüntü H1'in isteğini karşılamadı")

    print("== F04: anahtarla bağlanan cihazın kimliği kaymaz")
    await other.close()
    drift = await agent("HW-H2", {"X-Agent-Secret": "h2-secret", "X-Agent-Version": v13},
                        dna("HW-H2", uid="HW-H1-U"))
    msgs = await collect(drift, 2)
    chk(not find(msgs, "action", "set_identity"), "kimlik değiştirme komutu gönderilmedi")
    chk(await c.fetchval("SELECT count(*) FROM agent_secrets WHERE pc_name='HW-H2'") == 1, "anahtar yerinde")
    await drift.close()

    print("== F05: görevin akıbeti")

    async def new_task(script):
        return await c.fetchval(
            "INSERT INTO tasks (target_pc, script_path, status, created_at, created_by) "
            "VALUES ('HW-H3', $1, 'Pending', NOW()::text, 'hadmin') RETURNING id",
            script,
        )

    started = int(time.time()) - 600
    health = {"started_at": started}
    h3 = await agent("HW-H3", {"X-Agent-Secret": "h3-secret", "X-Agent-Version": v13}, dna("HW-H3", health=health))
    await collect(h3, 1)
    t1 = await new_task("echo bir")
    req("/api/tasks/action", admin, {"action": "RESUME", "target_mode": "ALL", "target_id": ""})
    msgs = await collect(h3, 2)
    ex = find(msgs, "action", "execute")
    chk(ex is not None and ex.get("task_id") == t1, "görev ajana gönderildi")
    chk(await c.fetchval("SELECT dispatched_at FROM tasks WHERE id=$1", t1) is not None, "gönderim anı kaydedildi")
    # Bağlantı koptu, aynı ajan süreci geri geldi: görev çalışıyor kalır, sonuç yeni bağlantıdan gelir
    await h3.close()
    await asyncio.sleep(0.3)
    h3 = await agent("HW-H3", {"X-Agent-Secret": "h3-secret", "X-Agent-Version": v13}, dna("HW-H3", health=health))
    await collect(h3, 1)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t1) == "Running", "aynı süreç: görev sürüyor")
    await h3.send(json.dumps({"type": "result", "pc_name": "HW-H3", "task_id": t1, "output": "x", "exit_code": 3}))
    status = await wait_for(c, "SELECT status FROM tasks WHERE id=$1 AND status <> 'Running'", t1)
    chk(status == "Failed" and await c.fetchval("SELECT exit_code FROM tasks WHERE id=$1", t1) == 3,
        "çıkış kodu 3: Failed, kod saklandı")
    # Ajan yeniden başladı (started_at gönderimden sonra): kesildi; yeniden başlatma komutuysa tamamlandı
    t2 = await new_task("echo iki")
    t3 = await new_task("shutdown /r /t 0")
    req("/api/tasks/action", admin, {"action": "RESUME", "target_mode": "ALL", "target_id": ""})
    await collect(h3, 1.5)
    await c.execute("UPDATE tasks SET status='Running', dispatched_at=NOW() - interval '1 minute' "
                    "WHERE id = ANY($1::int[])", [t2, t3])
    await h3.close()
    await asyncio.sleep(0.3)
    h3 = await agent("HW-H3", {"X-Agent-Secret": "h3-secret", "X-Agent-Version": v13},
                     dna("HW-H3", health={"started_at": int(time.time())}))
    await collect(h3, 1.5)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t2) == "Interrupted",
        "ajan yeniden başladı: Interrupted")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t3) == "Completed (Rebooted)",
        "yeniden başlatma komutu: Completed (Rebooted)")
    # Eski ajan (sonucu yeni bağlantıdan göndermez): Unknown
    t4 = await new_task("echo dort")
    await c.execute("UPDATE tasks SET status='Running', dispatched_at=NOW() WHERE id=$1", t4)
    await h3.close()
    await asyncio.sleep(0.3)
    h3 = await agent("HW-H3", {"X-Agent-Secret": "h3-secret", "X-Agent-Version": "0.1.12-alpha"},
                     dna("HW-H3", health=health))
    await collect(h3, 1.5)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t4) == "Unknown", "eski ajan: Unknown")
    await h3.close()
    # İptal: çalışan görev ajana bildirilir; sonradan gelen sonuç durumu değiştirmez
    h3 = await agent("HW-H3", {"X-Agent-Secret": "h3-secret", "X-Agent-Version": v13}, dna("HW-H3", health=health))
    await collect(h3, 1)
    t5 = await new_task("ping -n 100 127.0.0.1")
    req("/api/tasks/action", admin, {"action": "RESUME", "target_mode": "ALL", "target_id": ""})
    await collect(h3, 1.5)
    s, b = req("/api/tasks/action", admin, {"action": "PAUSE", "target_mode": "TASK", "target_id": str(t5)})
    chk(b.get("changed") == 0, "çalışan görev duraklatılamaz")
    s, b = req("/api/tasks/action", admin, {"action": "RETRY", "target_mode": "TASK", "target_id": str(t5)})
    chk(b.get("changed") == 0, "çalışan görev yeniden başlatılamaz (çift çalışma yok)")
    req("/api/tasks/action", admin, {"action": "CANCEL", "target_mode": "TASK", "target_id": str(t5)})
    msgs = await collect(h3, 2)
    cancel = find(msgs, "action", "cancel_task")
    chk(cancel is not None and cancel.get("task_id") == t5, "iptal ajana bildirildi")
    await h3.send(json.dumps({"type": "result", "pc_name": "HW-H3", "task_id": t5, "output": "[İPTAL]",
                              "exit_code": -2}))
    stored = await wait_for(c, "SELECT output FROM tasks WHERE id=$1 AND output IS NOT NULL", t5)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t5) == "Cancelled", "iptal edilen görev iptal kaldı")
    chk(stored == "[İPTAL]", "çıktısı yine saklandı")

    print("== karantina: ağ yalıtımı uygulanamazsa kilit tamamlanmış sayılmaz")
    s, b = req("/api/security/lockdown", admin, {"target_pc": "HW-H3", "reason": "test"})
    await collect(h3, 1)
    await h3.send(json.dumps({"hw_id": "HW-H3", "hostname": "hw-h3", "status": "Online", "quarantined": True,
                              "agent_health": {"screen_locked": True, "network_isolated": False,
                                               "isolation_error": "gpo"}}))
    await wait_for(c, "SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-H3' AND action='quarantine_partial'")
    act = await c.fetchval("SELECT pending_quarantine_action FROM clients WHERE pc_name='HW-H3'")
    chk(act == "lock", "yalıtım yok: kilit işlemi bekliyor (ajan yeniden deneyecek)")
    partial = await c.fetchval(
        "SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-H3' AND action='quarantine_partial'"
    )
    chk(partial >= 1, "yöneticiye bildirildi")
    await h3.send(json.dumps({"hw_id": "HW-H3", "hostname": "hw-h3", "status": "Online", "quarantined": True,
                              "agent_health": {"screen_locked": True, "network_isolated": True}}))
    done = await wait_for(c, "SELECT pending_quarantine_action IS NULL FROM clients WHERE pc_name='HW-H3'")
    chk(done is True, "yalıtım uygulanınca kilit tamamlandı")
    await h3.close()

    print("== /download yalnızca imzalı adresle")
    boundary = uuid.uuid4().hex
    body = (
        "--%s\r\nContent-Disposition: form-data; name=\"file\"; filename=\"h-test.txt\"\r\n"
        "Content-Type: text/plain\r\n\r\nmerhaba\r\n--%s--\r\n" % (boundary, boundary)
    ).encode()
    s, up = req("/api/upload", admin, body, headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
    chk(s == 200 and up.get("sha256") == hashlib.sha256(b"merhaba").hexdigest(), "yükleme özeti döndü")
    chk(req("/download/h-test.txt", raw=True)[0] == 404, "imzasız indirme: 404")
    chk(req("/download/h-test.txt?sig=AAAA", raw=True)[0] == 404, "yanlış imza: 404")
    s, content = req("/download/h-test.txt?sig=" + up.get("sig", ""), raw=True)
    chk(s == 200 and content == b"merhaba", "imzalı adresle indi")

    print("== F06: yetkisi alınan panel oturumu kapanır")
    panel = await websockets.connect(WS + "/ws/panel", additional_headers={"Cookie": "pops_jwt=" + admin})
    await c.execute("UPDATE users SET token_version = token_version + 1 WHERE username='hadmin'")
    closed = None
    deadline = time.time() + 16
    while closed is None and time.time() < deadline:
        try:
            await asyncio.wait_for(panel.recv(), timeout=max(0.1, deadline - time.time()))
        except websockets.exceptions.ConnectionClosed as e:
            closed = e.code
        except asyncio.TimeoutError:
            break
    chk(closed == 4001, "oturum iptal edilince panel soketi kapandı (%s)" % closed)
    await c.execute("UPDATE users SET token_version = 0 WHERE username='hadmin'")

    print("== F17: cihaz silme tek işlemde, görevleri kapanır")
    t6 = await new_task("echo alti")
    s, b = req("/api/devices/HW-H3", admin, method="DELETE")
    chk(b.get("status") == "success", "cihaz silindi")
    chk(await c.fetchval("SELECT count(*) FROM clients WHERE pc_name='HW-H3'") == 0
        and await c.fetchval("SELECT count(*) FROM agent_secrets WHERE pc_name='HW-H3'") == 0, "kayıtları gitti")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t6) == "Cancelled", "bekleyen görevi iptal edildi")

    await legit.close()
    await cleanup(c)
    await c.execute("DELETE FROM enroll_tokens WHERE token_hash = ANY($1::text[])", [_sha(t) for t in made_tokens])
    # Sonraki testler zorlamanın kapalı olduğunu varsayar (test_device_keys de böyle bırakır)
    await c.execute("UPDATE global_settings SET value='0' WHERE key='enforce_agent_auth'")
    await c.close()
    if FAILS:
        print("\n%d KONTROL BAŞARISIZ" % len(FAILS))
        sys.exit(1)
    print("\nTUM SAGLAMLASTIRMA TESTLERI GECTI")


if __name__ == "__main__":
    asyncio.run(main())
