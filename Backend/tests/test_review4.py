"""Dördüncü inceleme düzeltmeleri — entegrasyon testi (CI 'security' job'ı).

- Anahtarı olan cihaz adına anahtarsız bağlantı (WS) ve istek (HTTP), zorlama kapalıyken bile reddedilir; ret kaydı
  seyreltilir.
- Kayıttan sonra alınmış imajın klonu, asıl cihaz bağlıyken 4409 ile reddedilir; kayıtlı donanım bilgisi ezilmez.
- Bozuk tek bir mesaj ajan bağlantısını düşürmez; sürekli bozuk mesaj gönderen bağlantı kapatılır.
- Eşzamanlılık kotasını yalnızca bağlı cihazlardaki çalışan görevler tutar; gönderimde ajanın başlangıç zamanı
  saklanır ve yeniden bağlanınca "aynı süreç mi" buna göre karar verilir.
- Zamanlanmış görev: geçerlilik süresi dolan görev gönderilmez ve "Expired" olur; aynı takvimin bekleyen kopyası
  varsa ikincisi açılmaz; çok gecikmiş tur çalıştırılmaz, kaçırıldı diye bildirilir.
- Bypass anahtarı veritabanında şifreli; çözülemeyen anahtar yenilenir.
- Denetim zinciri doğrulaması ve dosya yükleme (özet, geçici dosya kalmaz).

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (zamanlayıcı, bypass).
Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import datetime
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
from pops import bypass, db, scheduler, secretbox  # noqa: E402
from pops.config import UPLOAD_DIR  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
PCS = ["HW-R4K", "HW-R4C", "HW-R4A", "HW-R4OFF", "HW-R4S", "HW-R4E", "HW-R4N", "HW-R4B"]
KEYED = ["HW-R4K", "HW-R4C", "HW-R4A", "HW-R4S", "HW-R4B"]
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _sha(s):
    return hashlib.sha256(s.encode()).hexdigest()


def req(path, token=None, body=None, method=None, headers=None, raw=None):
    data = raw if raw is not None else (json.dumps(body).encode() if body is not None else None)
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if data is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        return e.code, {}


def first(pc, uid=None, bios=None, disk=None, started_at=None):
    payload = {
        "hw_id": pc,
        "dna_payload": {
            "hardware": {"uuid": uid or pc + "-U", "bios_sn": bios or pc + "-B", "disk_sn": disk or pc + "-D",
                         "mac": "-", "ram_sn": "-"},
            "capabilities": {"ram_readable": True},
        },
        "hostname": pc.lower(),
        "status": "Online",
    }
    if started_at is not None:
        payload["agent_health"] = {"started_at": started_at}
    return json.dumps(payload)


def secret_heads(pc):
    return {"X-Agent-Secret": pc + "-s", "X-Agent-Version": "0.1.14-alpha"}


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


def closed_code(msgs):
    return next((m[1] for m in msgs if isinstance(m, tuple) and m[0] == "closed"), None)


def find(msgs, key, value):
    return next((m for m in msgs if isinstance(m, dict) and m.get(key) == value), None)


async def agent(pc, headers, first_msg):
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc), additional_headers=headers)
    try:
        await ws.send(first_msg)
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


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    for table, col in (("agent_secrets", "pc_name"), ("agent_bypass_keys", "pc_name"), ("clients", "pc_name"),
                       ("tasks", "target_pc"), ("agent_versions", "pc_name"), ("notifications", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), PCS)
    await c.execute("DELETE FROM scheduled_tasks WHERE name LIKE 'r4-test-%'")


async def main():
    c = await conn()
    old_enforce = await c.fetchval("SELECT value FROM global_settings WHERE key='enforce_agent_auth'")
    old_limit = await c.fetchval("SELECT value FROM global_settings WHERE key='concurrent_limit'")
    await cleanup(c)
    for pc in KEYED:
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2)", pc, _sha(pc + "-s"))
    for u, role in (("r4admin", "admin"), ("r4super", "superadmin")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
            u,
            role,
        )
    admin = server.create_jwt("r4admin", "admin", 0)
    superadmin = server.create_jwt("r4super", "superadmin", 0)
    db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=4,
    )
    try:
        await run(c, admin, superadmin)
    finally:
        await cleanup(c)
        await c.execute(
            "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth',$1) "
            "ON CONFLICT (key) DO UPDATE SET value=$1", old_enforce or "0")
        if old_limit is None:
            await c.execute("DELETE FROM global_settings WHERE key='concurrent_limit'")
        else:
            await c.execute("UPDATE global_settings SET value=$1 WHERE key='concurrent_limit'", old_limit)
        await db.db_pool.close()
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM REVIEW4 TESTLERI GECTI")


async def run(c, admin, superadmin):
    await c.execute(
        "INSERT INTO global_settings (key,value) VALUES ('enforce_agent_auth','0') "
        "ON CONFLICT (key) DO UPDATE SET value='0'"
    )

    print("== anahtarı olan cihaz adına anahtarsız bağlantı (zorlama kapalı)")
    for _ in range(2):
        ws = await agent("HW-R4K", {"X-Agent-Version": "0.1.14-alpha"}, first("HW-R4K"))
        msgs = await collect(ws, 2)
        chk(closed_code(msgs) == 4401, "anahtarsız WS 4401 ile kapandı (%s)" % closed_code(msgs))
    rejects = await c.fetchval(
        "SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-R4K' AND action='auth_reject'")
    chk(rejects == 1, "iki retten tek denetim kaydı (%s)" % rejects)
    login = {"hw_id": "HW-R4K", "hostname": "hw-r4k", "student_id": "r4"}
    chk(req("/api/auth/login", body=login)[0] == 401, "anahtarsız HTTP isteği 401")
    s, _ = req("/api/auth/login", body=login, headers={"X-Agent-Id": "HW-R4K", "X-Agent-Secret": "HW-R4K-s"})
    chk(s == 200, "anahtarlı HTTP isteği kabul")
    s, _ = req("/api/auth/login", body={"hw_id": "HW-R4N", "hostname": "hw-r4n", "student_id": "r4"})
    chk(s == 200, "anahtarı hiç olmayan eski cihaz (zorlama kapalı) hâlâ kabul")

    print("== kayıttan sonra alınmış imajın klonu")
    orig = await agent("HW-R4C", secret_heads("HW-R4C"), first("HW-R4C"))
    msgs = await collect(orig, 1.5)
    chk(closed_code(msgs) is None, "asıl cihaz bağlandı")
    chk(await wait_for(c, "SELECT dna_uuid = 'HW-R4C-U' FROM clients WHERE pc_name='HW-R4C'"), "donanım kaydı")
    clone_first = first("HW-R4C", uid="KLON-U", bios="KLON-B", disk="KLON-D")
    clone = await agent("HW-R4C", secret_heads("HW-R4C"), clone_first)
    msgs = await collect(clone, 2)
    chk(closed_code(msgs) == 4409, "asıl cihaz bağlıyken klon 4409 (%s)" % closed_code(msgs))
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-R4C' AND action='clone_rejected'")
        == 1, "klon denetim kaydı")
    chk(await wait_for(c, "SELECT count(*) FROM notifications WHERE event='clone_rejected' AND pc_name='HW-R4C'"),
        "klon bildirimi")
    msgs = await collect(orig, 0.5)
    chk(closed_code(msgs) is None, "asıl cihazın bağlantısı sürüyor")
    await orig.close()
    chk(await wait_for(c, "SELECT status = 'Offline' FROM clients WHERE pc_name='HW-R4C'"), "asıl cihaz çevrimdışı")
    clone = await agent("HW-R4C", secret_heads("HW-R4C"), clone_first)
    msgs = await collect(clone, 1.5)
    chk(closed_code(msgs) is None, "asıl cihaz yokken klon bağlanır (karar yöneticinin)")
    chk(await c.fetchval("SELECT dna_uuid FROM clients WHERE pc_name='HW-R4C'") == "HW-R4C-U",
        "kayıtlı donanım bilgisi klonla ezilmedi")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE hw_id='HW-R4C' AND action='dna_mismatch'")
        == 1, "uyuşmazlık kaydı saatte bir")
    await clone.close()

    print("== bozuk mesaj bağlantıyı düşürmez")
    ag = await agent("HW-R4A", secret_heads("HW-R4A"), first("HW-R4A", started_at=4242.0))
    await collect(ag, 1)
    await ag.send("bu JSON değil")
    await ag.send("[1, 2, 3]")
    await ag.send(json.dumps({"type": "result", "task_id": "yok", "output": 5}))
    await ag.send(json.dumps({"hw_id": "HW-R4A", "hostname": "hw-r4a", "status": "Online",
                              "active_window": "R4-ALIVE", "agent_health": {"started_at": 4242.0}}))
    msgs = await collect(ag, 0.5)
    chk(closed_code(msgs) is None, "bağlantı açık")
    chk(await wait_for(c, "SELECT active_window = 'R4-ALIVE' FROM clients WHERE pc_name='HW-R4A'"),
        "sonraki heartbeat işlendi")

    print("== eşzamanlılık kotası ve gönderimde ajan başlangıç zamanı")
    await c.execute(
        "INSERT INTO clients (pc_name, hostname, status) VALUES ('HW-R4OFF', 'hw-r4off', 'Offline')")
    offline_task = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at) VALUES "
        "('HW-R4OFF', 'r4 offline running', 'Running', '2000-01-01 00:00:00', NOW()) RETURNING id")
    queued = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at) VALUES "
        "('HW-R4A', 'echo r4', 'Pending', to_char(now(), 'YYYY-MM-DD HH24:MI:SS')) RETURNING id")
    expired = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, expires_at) VALUES "
        "('HW-R4B', 'echo r4 expired', 'Pending', to_char(now(), 'YYYY-MM-DD HH24:MI:SS'), NOW() - interval "
        "'1 minute') RETURNING id")
    # Sınır 1 ve tek "Running" görev bağlı olmayan cihazda: bağlı cihazın görevi yine de gönderilmeli
    chk(req("/api/set_concurrent_limit", admin, {"limit": 1})[0] == 200, "sınır 1")
    msgs = await collect(ag, 3)
    chk(find(msgs, "task_id", queued) is not None, "çevrimdışı cihazın 'Running' görevi kotayı tutmadı")
    chk(await c.fetchval("SELECT agent_started_at FROM tasks WHERE id=$1", queued) == 4242.0,
        "gönderimde ajanın başlangıç zamanı saklandı")

    print("== görev sonucu yalnızca kendi görevine yazılır ve panele öyle yayılır")
    panel = await websockets.connect(WS + "/ws/panel", additional_headers={"Cookie": "pops_jwt=" + admin})
    await collect(panel, 0.5)
    await ag.send(json.dumps({"type": "result", "task_id": offline_task, "output": "r4-yabanci", "exit_code": 0}))
    msgs = await collect(ag, 1.5)
    chk(find(msgs, "task_id", offline_task) is not None, "başka cihazın görev kimliğiyle gelen sonuç da onaylandı")
    pmsgs = await collect(panel, 1)
    chk(not [m for m in pmsgs if isinstance(m, dict) and m.get("output") == "r4-yabanci"],
        "eşleşmeyen sonuç panele yayılmadı")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", offline_task) == "Running",
        "başka cihazın görevi değişmedi")
    await ag.send(json.dumps({"type": "result", "task_id": queued, "output": "r4-kendi", "exit_code": 0}))
    pmsgs = await collect(panel, 2)
    chk([m for m in pmsgs if isinstance(m, dict) and m.get("output") == "r4-kendi"], "kendi sonucu panele yayıldı")
    chk(await wait_for(c, "SELECT status = 'Completed' FROM tasks WHERE id=$1", queued), "kendi görevi tamamlandı")
    refused = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at) VALUES "
        "('HW-R4A', 'ping r4', 'Running', to_char(now(), 'YYYY-MM-DD HH24:MI:SS'), NOW()) RETURNING id")
    await ag.send(json.dumps({"type": "result", "task_id": refused,
                              "output": "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı; komut çalıştırılmadı."}))
    chk(await wait_for(c, "SELECT status = 'Denied' AND exit_code = -5 FROM tasks WHERE id=$1", refused),
        "çıkış kodsuz ret sonucu 'Denied' oldu (capability_denied gelmese de)")
    await panel.close()
    other = await agent("HW-R4B", secret_heads("HW-R4B"), first("HW-R4B"))
    msgs = await collect(other, 2)
    chk(find(msgs, "task_id", expired) is None and await c.fetchval(
        "SELECT status FROM tasks WHERE id=$1", expired) == "Pending", "süresi dolmuş görev gönderilmedi")
    await scheduler.reap_stuck_tasks()
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", expired) == "Expired", "süresi dolan görev Expired")
    s, b = req("/api/tasks/action", admin, {"action": "RETRY", "target_mode": "TASK", "target_id": str(expired)})
    chk(s == 200 and b.get("changed") == 1, "Expired görev yeniden denenebilir")
    retry = await c.fetchrow("SELECT status, expires_at FROM tasks WHERE retry_of=$1", expired)
    chk(retry is not None and retry["expires_at"] is None, "yeniden deneme süresiz yeni görev")
    await other.close()
    await ag.close()

    print("== yeniden bağlanınca görev: aynı süreç mi, yeniden mi başladı")
    same = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at, agent_started_at) VALUES "
        "('HW-R4S', 'r4 same', 'Running', '2000-01-01 00:00:00', NOW(), 5000.0) RETURNING id")
    restarted = await c.fetchval(
        "INSERT INTO tasks (target_pc, script_path, status, created_at, dispatched_at, agent_started_at) VALUES "
        "('HW-R4S', 'r4 restart', 'Running', '2000-01-01 00:00:00', NOW(), 4000.0) RETURNING id")
    ws = await agent("HW-R4S", secret_heads("HW-R4S"), first("HW-R4S", started_at=5000.0))
    await collect(ws, 1.5)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", same) == "Running",
        "aynı ajan süreci: görev sürüyor (saatlere bakılmadı)")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", restarted) == "Interrupted",
        "ajan yeniden başlamış: görev yarıda kaldı")
    await ws.close()

    print("== bozuk mesaj seli bağlantıyı kapatır")
    ws = await agent("HW-R4S", secret_heads("HW-R4S"), first("HW-R4S", started_at=5000.0))
    await collect(ws, 1)
    for i in range(25):
        try:
            await ws.send("bozuk %d" % i)
        except websockets.exceptions.ConnectionClosed:
            break
    msgs = await collect(ws, 3)
    chk(closed_code(msgs) == 1011, "dakikada 20 hatadan sonra kapandı (%s)" % closed_code(msgs))

    print("== zamanlanmış görev: geçerlilik, tekrar ve kaçırılan tur")
    await c.execute("INSERT INTO clients (pc_name, hostname, status) VALUES ('HW-R4E', 'hw-r4e', 'Offline')")
    sid = await c.fetchval(
        "INSERT INTO scheduled_tasks (name, command, target_mode, targets, schedule_type, time_of_day, enabled, "
        "next_run, created_by) VALUES ('r4-test-daily', 'echo r4-sched', 'PC', '[\"HW-R4E\"]', 'daily', '08:00', "
        "TRUE, now() - interval '5 minutes', 'r4') RETURNING id")
    await scheduler.run_due()
    rows = await c.fetch("SELECT id, schedule_id, expires_at FROM tasks WHERE script_path='echo r4-sched'")
    chk(len(rows) == 1 and rows[0]["schedule_id"] == sid, "tur bir görev açtı, takvime bağlı")
    left = (rows[0]["expires_at"] - datetime.datetime.now(datetime.timezone.utc)).total_seconds() / 60 if rows else 0
    chk(50 < left < 56, "geçerlilik planlanan saatten 60 dk (%.1f dk kaldı)" % left)
    await c.execute("UPDATE scheduled_tasks SET next_run = now() - interval '1 minute' WHERE id=$1", sid)
    await scheduler.run_due()
    chk(await c.fetchval("SELECT count(*) FROM tasks WHERE script_path='echo r4-sched'") == 1,
        "bekleyen kopya varken ikinci görev açılmadı")
    await c.execute("UPDATE scheduled_tasks SET next_run = now() - interval '3 hours' WHERE id=$1", sid)
    await scheduler.run_due()
    row = await c.fetchrow("SELECT last_result, next_run, enabled FROM scheduled_tasks WHERE id=$1", sid)
    chk(await c.fetchval("SELECT count(*) FROM tasks WHERE script_path='echo r4-sched'") == 1
        and (row["last_result"] or "").startswith("kaçırıldı") and row["enabled"]
        and row["next_run"] > datetime.datetime.now(datetime.timezone.utc),
        "çok gecikmiş tur çalıştırılmadı, takvim ilerledi (%s)" % row["last_result"])
    chk(await c.fetchval("SELECT count(*) FROM notifications WHERE event='schedule_missed'") >= 1,
        "kaçırılan tur bildirildi")
    await c.execute("UPDATE tasks SET expires_at = now() - interval '1 second' WHERE script_path='echo r4-sched'")
    await scheduler.reap_stuck_tasks()
    chk(await c.fetchval("SELECT status FROM tasks WHERE script_path='echo r4-sched'") == "Expired",
        "süresi dolan zamanlanmış görev Expired")
    await c.execute("UPDATE scheduled_tasks SET next_run = now() - interval '1 minute' WHERE id=$1", sid)
    await scheduler.run_due()
    chk(await c.fetchval("SELECT count(*) FROM tasks WHERE script_path='echo r4-sched' AND status='Pending'") == 1,
        "süresi dolan kopya yenisini engellemez")

    print("== bypass anahtarı şifreli saklanır")
    plain = bypass.new_key()
    await c.execute("INSERT INTO agent_bypass_keys (pc_name, secret, fingerprint, confirmed_at) VALUES "
                    "('HW-R4K', $1, $2, NOW())", plain, bypass.fingerprint(plain))
    chk(await secretbox.reseal_bypass_keys(db.execute_query) >= 1, "düz metin anahtar açılışta şifrelendi")
    stored = await c.fetchval("SELECT secret FROM agent_bypass_keys WHERE pc_name='HW-R4K'")
    chk(stored.startswith("v1:") and plain not in stored, "veritabanında şifreli")
    s, b = req("/api/security/bypass_token/HW-R4K", admin, {})
    today = datetime.date.today()
    chk(s == 200 and b.get("token") in [bypass.device_code(plain, "HW-R4K", today, n) for n in range(10)],
        "şifreli anahtardan doğru kod")
    await c.execute("UPDATE agent_bypass_keys SET secret='v1:bozuk' WHERE pc_name='HW-R4K'")
    s, b = req("/api/security/bypass_token/HW-R4K", admin, {})
    chk(s == 200 and b.get("status") == "error" and "çözülemedi" in (b.get("message") or ""),
        "çözülemeyen anahtar açıkça bildirildi")
    fresh = await bypass.key_to_send("HW-R4K")
    row = await c.fetchrow("SELECT secret, fingerprint, confirmed_at FROM agent_bypass_keys WHERE pc_name='HW-R4K'")
    chk(fresh and row["secret"].startswith("v1:") and row["confirmed_at"] is None
        and row["fingerprint"] == bypass.fingerprint(fresh) and secretbox.unseal(row["secret"]) == fresh,
        "çözülemeyen anahtar yenilendi, onay bekliyor")
    chk(await bypass.key_to_send("HW-R4K") == fresh, "onaylanana kadar aynı anahtar yeniden gönderilir")

    print("== denetim zinciri ve yükleme")
    s, b = req("/api/system/audit-verify", superadmin)
    chk(s == 200 and b.get("ok") and b.get("checked", 0) > 0, "zincir sağlam (%s)" % b)
    content = os.urandom(300000)
    boundary = "r4boundary%d" % int(time.time())
    body = (("--%s\r\nContent-Disposition: form-data; name=\"file\"; filename=\"r4-test.bin\"\r\n"
             "Content-Type: application/octet-stream\r\n\r\n" % boundary).encode() + content
            + ("\r\n--%s--\r\n" % boundary).encode())
    s, up = req("/api/upload", admin, raw=body,
                headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
    chk(s == 200 and up.get("sha256") == hashlib.sha256(content).hexdigest(), "yükleme özeti doğru")
    if os.path.isdir(UPLOAD_DIR):
        chk(not [n for n in os.listdir(UPLOAD_DIR) if n.startswith(".upload-")], "geçici dosya kalmadı")
        try:
            os.unlink(os.path.join(UPLOAD_DIR, "r4-test.bin"))
        except OSError:
            pass


if __name__ == "__main__":
    started = time.time()
    asyncio.run(main())
    print("süre: %.1f sn" % (time.time() - started))
