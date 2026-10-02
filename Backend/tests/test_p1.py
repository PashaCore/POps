"""0.1.14 güvenilirlik — entegrasyon testi (CI 'security' job'ı).

- Eşzamanlı kayıt: tek kullanımlık jetonla 20 eşzamanlı bağlantıdan yalnızca biri, 5 kullanımlık jetonla 20 farklı
  cihazdan yalnızca 5'i anahtar alır (gerçek PostgreSQL üzerinde).
- Sürüm uyumluluğu: 0.1.11, 0.1.12, 0.1.13, 0.1.14 ve sürümsüz ajan bağlanır, heartbeat ve sonuç gönderir; her
  birine yalnızca anladığı mesajlar gider (cihaz bypass anahtarı 0.1.12+); server_info herkese gider.
- Güncelleme sonucu onayı (S20): result_id'li sonuç kaydedilip onaylanır, ikinci gönderim yeni kayıt açmaz.
- Heartbeat'ler toplu yazılır; kopan cihazın "Offline" kaydı geç heartbeat'le ezilmez; kopma sebebi saklanır.
- Yayın durdurma POST + admin (R-10); çift istek tek görev; eksi eşzamanlılık sınırı reddedilir (F21).
- Zamanlanmış görev ya hep ya hiç yazılır (F07); takılı görev zaman aşımına düşer, geç sonuç yine kaydedilir.
- Saklama süresi dolan kayıtlar silinir, denetim kaydına dokunulmaz; tanınmayan cihaz UUID/BIOS ile kurtarılır.

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (zamanlayıcı, saklama).
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
from pops import db, retention, scheduler, update_tracking  # noqa: E402
from pops.manager import manager as local_manager  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
CONC = ["HW-PC%02d" % i for i in range(20)]
MULTI = ["HW-PM%02d" % i for i in range(20)]
COMPAT = {"HW-PV11": "0.1.11-alpha", "HW-PV12": "0.1.12-alpha", "HW-PV13": "0.1.13-alpha",
          "HW-PV14": "0.1.14-alpha", "HW-PVXX": None}
OTHER = ["HW-PHB", "HW-PDUP", "HW-PS1", "HW-PS2", "HW-PR1", "HW-PRTMP"]
PCS = CONC + MULTI + list(COMPAT) + OTHER
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token=None, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def dna(pc, uid=None, bios=None, disk=None):
    return json.dumps({
        "hw_id": pc,
        "dna_payload": {
            "hardware": {"uuid": uid or pc + "-U", "bios_sn": bios or pc + "-B", "disk_sn": disk or pc + "-D",
                         "mac": "-", "ram_sn": "-"},
            "capabilities": {"ram_readable": True},
        },
        "hostname": pc.lower(),
        "status": "Online",
    })


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
        pass
    return ws


async def wait_for(c, sql, *args, timeout=8):
    for _ in range(int(timeout / 0.2)):
        value = await c.fetchval(sql, *args)
        if value:
            return value
        await asyncio.sleep(0.2)
    return await c.fetchval(sql, *args)


async def cleanup(c):
    renamed = await c.fetch("SELECT pc_name FROM clients WHERE dna_uuid = ANY($1::text[])", [p + "-U" for p in PCS])
    names = PCS + [r["pc_name"] for r in renamed]
    for table, col in (("agent_secrets", "pc_name"), ("agent_bypass_keys", "pc_name"), ("clients", "pc_name"),
                       ("tasks", "target_pc"), ("agent_versions", "pc_name"), ("pending_updates", "pc_name"),
                       ("update_results", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), names)
    await c.execute("DELETE FROM scheduled_tasks WHERE name LIKE 'p1-test-%'")
    await c.execute("DELETE FROM agent_logs_v2 WHERE message LIKE 'p1-test-%'")


async def main():
    c = await conn()
    old_enforce = await c.fetchval("SELECT value FROM global_settings WHERE key='enforce_agent_auth'")
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','1') "
        "ON CONFLICT (key) DO UPDATE SET value='1'"
    )
    await cleanup(c)
    for pc in COMPAT:
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc, _sha(pc + "-s"))
    for pc in ("HW-PHB", "HW-PDUP"):
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc, _sha(pc + "-s"))
    for u, role in (("p1admin", "admin"), ("p1super", "superadmin"), ("p1viewer", "viewer")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            u,
            role,
        )
    admin = server.create_jwt("p1admin", "admin", 0)
    superadmin = server.create_jwt("p1super", "superadmin", 0)
    viewer = server.create_jwt("p1viewer", "viewer", 0)
    db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=4,
    )
    try:
        await run(c, admin, superadmin, viewer)
    finally:
        await cleanup(c)
        await c.execute("UPDATE global_settings SET value=$1 WHERE key='enforce_agent_auth'", old_enforce or "0")
        await db.db_pool.close()
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM 0.1.14 TESTLERI GECTI")


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


async def run(c, admin, superadmin, viewer):
    print("== eşzamanlı kayıt: tek kullanımlık jeton, 20 bağlantı")
    s, created = req("/api/system/enroll-token", superadmin, {"max_uses": 1, "ttl_hours": 1})
    token = created["token"]
    heads = {"X-Enroll-Token": token, "X-Agent-Version": "0.1.14-alpha"}
    sockets = await asyncio.gather(*[agent(pc, heads, dna(pc)) for pc in CONC], return_exceptions=True)
    results = await asyncio.gather(*[collect(ws, 4) for ws in sockets if not isinstance(ws, Exception)])
    granted = sum(1 for msgs in results if find(msgs, "action", "set_secret"))
    stored = await c.fetchval("SELECT count(*) FROM agent_secrets WHERE pc_name = ANY($1::text[])", CONC)
    used = await c.fetchval("SELECT use_count FROM enroll_tokens WHERE token_hash=$1", _sha(token))
    chk(granted == 1 and stored == 1 and used == 1,
        "yalnızca bir anahtar (%d/%d, kullanım %s)" % (granted, stored, used))
    for ws in sockets:
        if not isinstance(ws, Exception):
            await ws.close()

    print("== eşzamanlı kayıt: 5 kullanımlık jeton, 20 farklı cihaz")
    s, created = req("/api/system/enroll-token", superadmin, {"max_uses": 5, "ttl_hours": 1})
    token = created["token"]
    heads = {"X-Enroll-Token": token, "X-Agent-Version": "0.1.14-alpha"}
    sockets = await asyncio.gather(*[agent(pc, heads, dna(pc)) for pc in MULTI], return_exceptions=True)
    results = await asyncio.gather(*[collect(ws, 4) for ws in sockets if not isinstance(ws, Exception)])
    granted = sum(1 for msgs in results if find(msgs, "action", "set_secret"))
    stored = await c.fetchval("SELECT count(*) FROM agent_secrets WHERE pc_name = ANY($1::text[])", MULTI)
    row = await c.fetchrow("SELECT use_count, is_used FROM enroll_tokens WHERE token_hash=$1", _sha(token))
    chk(granted == 5 and stored == 5, "tam 5 cihaz anahtar aldı (%d/%d)" % (granted, stored))
    chk(row["use_count"] == 5 and row["is_used"], "jeton sınırında kapandı")
    for ws in sockets:
        if not isinstance(ws, Exception):
            await ws.close()

    print("== sürüm uyumluluğu")
    agents = {}
    for pc, ver in COMPAT.items():
        heads = {"X-Agent-Secret": pc + "-s"}
        if ver:
            heads["X-Agent-Version"] = ver
        agents[pc] = await agent(pc, heads, dna(pc))
    first = {pc: await collect(ws, 1.5) for pc, ws in agents.items()}
    for pc, ver in COMPAT.items():
        msgs = first[pc]
        info = find(msgs, "action", "server_info")
        chk(info is not None and "update_result_ack" in info.get("features", []),
            "%s: server_info aldı" % (ver or "sürümsüz"))
        wants_key = ver is not None and ver >= "0.1.12"
        chk(bool(find(msgs, "action", "set_bypass_secret")) == wants_key,
            "%s: cihaz bypass anahtarı %s" % (ver or "sürümsüz", "gönderildi" if wants_key else "gönderilmedi"))
        chk(not any(m == ("closed", 4401) for m in msgs), "%s: bağlı kaldı" % (ver or "sürümsüz"))
    tids = {}
    for pc in COMPAT:
        tids[pc] = await c.fetchval(
            "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at) "
            "VALUES ($1, 'exit 3', 'Running', to_char(now(), 'YYYY-MM-DD HH24:MI:SS'), NOW()) RETURNING id", pc)
    for pc, ver in COMPAT.items():
        result = {"type": "result", "task_id": tids[pc], "output": "çıktı"}
        if ver and ver >= "0.1.13":
            result["exit_code"] = 3
        await agents[pc].send(json.dumps(result))
    for pc, ver in COMPAT.items():
        expect = "Failed" if ver and ver >= "0.1.13" else "Completed"
        got = await wait_for(c, "SELECT status FROM tasks WHERE id=$1 AND status <> 'Running'", tids[pc])
        chk(got == expect, "%s: sonuç işlendi (%s)" % (ver or "sürümsüz", got))

    print("== güncelleme sonucu onayı (S20)")
    v13, v14 = agents["HW-PV13"], agents["HW-PV14"]
    await v13.send(json.dumps({"type": "update_result", "status": "success", "to_version": "0.1.13-alpha"}))
    msgs = await collect(v13, 1.5)
    chk(not find(msgs, "action", "update_result_ack"), "result_id'siz (eski) sonuç onaylanmaz")
    chk(await wait_for(c, "SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-PV13' AND action='update_result'")
        == 1, "eski sonuç kaydedildi")
    rid = "0123456789abcdef0123456789abcdef"
    for attempt in range(2):
        await v14.send(json.dumps({"type": "update_result", "status": "success", "to_version": "0.1.14-alpha",
                                   "result_id": rid}))
        ack = find(await collect(v14, 1.5), "action", "update_result_ack")
        chk(ack is not None and ack.get("result_id") == rid, "gönderim %d onaylandı" % (attempt + 1))
    n = await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-PV14' AND action='update_result'")
    chk(n == 1, "aynı sonuç ikinci kez kaydedilmedi (%d)" % n)
    await v14.send(json.dumps({"type": "update_result", "status": "success", "result_id": "kısa!"}))
    chk(not find(await collect(v14, 1), "action", "update_result_ack"), "geçersiz result_id onaylanmaz")
    for ws in agents.values():
        await ws.close()

    print("== heartbeat'ler toplu yazılır; kopma sebebi")
    hb = await agent("HW-PHB", {"X-Agent-Secret": "HW-PHB-s", "X-Agent-Version": "0.1.14-alpha"}, dna("HW-PHB"))
    await collect(hb, 1)
    await hb.send(json.dumps({"hw_id": "HW-PHB", "hostname": "hw-phb", "status": "Online", "active_window": "P1-W1"}))
    chk(await wait_for(c, "SELECT active_window = 'P1-W1' FROM clients WHERE pc_name='HW-PHB'"),
        "heartbeat birkaç saniye içinde yazıldı")
    await hb.send(json.dumps({"hw_id": "HW-PHB", "hostname": "hw-phb", "status": "Online", "active_window": "P1-W2"}))
    await hb.close()
    await asyncio.sleep(3.5)   # toplu yazma aralığından (2 sn) uzun
    row = await c.fetchrow("SELECT status, last_disconnect_reason, last_disconnect_at FROM clients "
                           "WHERE pc_name='HW-PHB'")
    chk(row["status"] == "Offline", "kopan cihazın Offline kaydı geç heartbeat'le ezilmedi")
    chk((row["last_disconnect_reason"] or "").startswith("ajan kapattı") and row["last_disconnect_at"] is not None,
        "kopma sebebi saklandı: %s" % row["last_disconnect_reason"])
    s, devices = req("/api/devices", admin)
    dev = next((d for d in devices if d.get("hw_id") == "HW-PHB"), {})
    chk(dev.get("last_disconnect_reason") == row["last_disconnect_reason"], "cihaz listesinde görünüyor")

    print("== yayın durdurma POST + admin (R-10)")
    chk(req("/api/stream/stop/HW-PHB", admin)[0] in (404, 405), "eski GET adresi yok")
    chk(req("/api/stream/stop", viewer, {"pc_name": "HW-PHB"})[0] == 403, "viewer durduramaz")
    chk(req("/api/stream/stop", admin, {"pc_name": "HW-PHB"})[0] == 200, "admin POST ile durdurur")

    print("== çift istek tek görev; eksi sınır")
    body = {"target_mode": "PC", "targets": ["HW-PDUP"],
            "taskSequence": [{"name": "p1", "type": "CMD", "command": "echo p1-dup"}]}
    s1, b1 = req("/api/deploy_orchestration", admin, body)
    s2, b2 = req("/api/deploy_orchestration", admin, body)
    n = await c.fetchval("SELECT count(*) FROM tasks WHERE target_pc='HW-PDUP' AND script_path='echo p1-dup'")
    chk(s1 == 200 and b1.get("created") == 1 and b2.get("duplicate") is True and n == 1, "ikinci istek görev açmadı")
    other = dict(body, taskSequence=[{"name": "p1", "type": "CMD", "command": "echo p1-other"}])
    chk(req("/api/deploy_orchestration", admin, other)[1].get("created") == 1, "farklı komut yeni görev açar")
    chk(req("/api/set_concurrent_limit", admin, {"limit": -1})[0] == 422, "eksi eşzamanlılık sınırı reddedildi")

    print("== zamanlanmış görev: ya hep ya hiç (F07)")
    sid = await c.fetchval(
        "INSERT INTO scheduled_tasks (name, command, target_mode, targets, schedule_type, run_at, enabled, next_run, "
        "created_by) VALUES ('p1-test-once', 'echo p1-sched', 'PC', $1, 'once', now() - interval '1 minute', FALSE, "
        "now() - interval '1 minute', 'p1') RETURNING id",
        json.dumps(["HW-PS1", "HW-PS2"]),
    )
    real_enqueue = scheduler.enqueue

    async def crashing(row, suffix, conn=None):
        await conn.execute("INSERT INTO tasks (target_pc, script_path, status) VALUES ('HW-PS1', 'echo p1-sched', "
                           "'Pending')")
        raise RuntimeError("süreç burada öldü")

    scheduler.enqueue = crashing
    try:
        await c.execute("UPDATE scheduled_tasks SET enabled = TRUE WHERE id=$1", sid)
        try:
            await scheduler.run_due()
        except RuntimeError:
            pass
    finally:
        scheduler.enqueue = real_enqueue
    n = await c.fetchval("SELECT count(*) FROM tasks WHERE script_path='echo p1-sched'")
    row = await c.fetchrow("SELECT enabled, next_run FROM scheduled_tasks WHERE id=$1", sid)
    chk(n == 0 and row["enabled"] and row["next_run"] is not None,
        "yarıda kalan tur hiçbir şey yazmadı, takvim duruyor")
    await scheduler.run_due()
    n = await c.fetchval("SELECT count(*) FROM tasks WHERE script_path='echo p1-sched'")
    row = await c.fetchrow("SELECT enabled, last_result FROM scheduled_tasks WHERE id=$1", sid)
    chk(n == 2 and not row["enabled"] and "2 cihaz" in (row["last_result"] or ""),
        "sonraki turda iki görev + takvim kapandı")
    bad = await c.fetchval(
        "INSERT INTO scheduled_tasks (name, command, target_mode, targets, schedule_type, run_at, enabled, next_run, "
        "created_by) VALUES ('p1-test-bad', 'echo x', 'PC', '{bozuk', 'once', now() - interval '1 minute', TRUE, "
        "now() - interval '1 minute', 'p1') RETURNING id")
    await scheduler.run_due()
    row = await c.fetchrow("SELECT enabled, last_result FROM scheduled_tasks WHERE id=$1", bad)
    chk(not row["enabled"] and (row["last_result"] or "").startswith("hata"), "bozuk takvim hatasıyla ilerletildi")

    print("== takılı görev zaman aşımı")
    stuck = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at) VALUES ('HW-PHB', 'p1 stuck', "
        "'Running', '2000-01-01 00:00:00', NOW() - interval '40 minutes') RETURNING id")
    fresh = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at) VALUES ('HW-PHB', 'p1 fresh', "
        "'Running', '2000-01-01 00:00:00', NOW() - interval '5 minutes') RETURNING id")
    await scheduler.reap_stuck_tasks()
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", stuck) == "Timed Out",
        "35 dk'yı geçen görev Timed Out")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", fresh) == "Running", "yeni görev sürüyor")
    hb = await agent("HW-PHB", {"X-Agent-Secret": "HW-PHB-s", "X-Agent-Version": "0.1.14-alpha"}, dna("HW-PHB"))
    await collect(hb, 1)
    await hb.send(json.dumps({"type": "result", "task_id": stuck, "output": "geç", "exit_code": 0}))
    chk(await wait_for(c, "SELECT status = 'Completed' FROM tasks WHERE id=$1", stuck), "geç gelen sonuç kaydedildi")
    await hb.close()

    print("== saklama süresi")
    await c.execute("INSERT INTO agent_logs_v2 (pc_name, message, timestamp) VALUES ('HW-PHB', 'p1-test-old', "
                    "'2001-01-01 00:00:00'), ('HW-PHB', 'p1-test-new', to_char(now(), 'YYYY-MM-DD HH24:MI:SS'))")
    old_done = await c.fetchval("INSERT INTO tasks (target_pc, script_path, status, created_at) VALUES "
                                "('HW-PHB', 'p1 old', 'Completed', '2001-01-01 00:00:00') RETURNING id")
    old_wait = await c.fetchval("INSERT INTO tasks (target_pc, script_path, status, created_at) VALUES "
                                "('HW-PHB', 'p1 old', 'Pending', '2001-01-01 00:00:00') RETURNING id")
    audits = await c.fetchval("SELECT count(*) FROM device_audit_logs")
    s, cfg = req("/api/system/retention", superadmin)
    chk(s == 200 and cfg.get("retention_days_logs") == 365, "varsayılan süreler")
    chk(req("/api/system/retention", admin)[0] == 403, "yalnız superadmin")
    chk(req("/api/system/retention", superadmin, {"retention_days_logs": -1, "retention_days_tasks": 1,
                                                  "retention_days_notifications": 1})[0] == 422, "eksi süre reddedildi")
    await retention.apply()
    msgs = {r["message"] for r in await c.fetch("SELECT message FROM agent_logs_v2 WHERE message LIKE 'p1-test-%'")}
    chk(msgs == {"p1-test-new"}, "eski log silindi, yenisi duruyor")
    chk(await c.fetchval("SELECT count(*) FROM tasks WHERE id=$1", old_done) == 0, "eski sonuçlanmış görev silindi")
    chk(await c.fetchval("SELECT count(*) FROM tasks WHERE id=$1", old_wait) == 1, "bekleyen görev silinmedi")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs") >= audits, "denetim kaydına dokunulmadı")

    print("== güncelleme izi yeniden başlatmada kaybolmaz")
    await update_tracking.mark_sent("HW-PHB", "9.9.9")
    local_manager.pending_updates.clear()
    await update_tracking.load()
    chk(local_manager.pending_updates.get("HW-PHB", ("",))[0] == "9.9.9", "bekleyen güncelleme tablodan yüklendi")
    await update_tracking.forget("HW-PHB")

    print("== tanınmayan cihaz UUID/BIOS ile kurtarılır")
    await c.execute("UPDATE global_settings SET value='0' WHERE key='enforce_agent_auth'")
    await c.execute(
        "INSERT INTO clients (pc_name, hostname, status, dna_uuid, dna_bios, dna_disk) "
        "VALUES ('HW-PR1', 'hw-pr1', 'Offline', 'HW-PR1-U', ' hw-pr1-b ', 'HW-PR1-D')")
    tmp = await agent("HW-PRTMP", {"X-Agent-Version": "0.1.14-alpha"},
                      dna("HW-PRTMP", uid="hw-pr1-u", bios="HW-PR1-B", disk="HW-PR1-D"))
    msgs = await collect(tmp, 2)
    ident = find(msgs, "action", "set_identity")
    chk(ident is not None and ident.get("new_hw_id") == "HW-PR1", "kimlik kurtarıldı (büyük/küçük harf, boşluk farkı)")
    await tmp.close()
    await c.execute("UPDATE global_settings SET value='1' WHERE key='enforce_agent_auth'")


if __name__ == "__main__":
    started = time.time()
    asyncio.get_event_loop().run_until_complete(main())
    print("(%.1f sn)" % (time.time() - started))
