"""İşlem merkezi için uçlar — entegrasyon testi (CI 'security' job'ı).

- Görev oluşturan istek görev kimliklerini döner (yinelenen istek aynı kimlikleri), yeniden deneme de yeni kimlikleri.
- POST /api/tasks/status verilen görevlerin durumunu döner; silinmiş ya da olmayan görev listede yoktur.
- POST /api/system/update-progress ajan güncellemesinin cihaz cihaz durumunu döner: sürüm hedefte mi, bekleyen
  gönderim (ve ajanın bildirdiği son adım; adımların kendisi test_p1.py'de), gönderimden sonraki güncelleme
  sonucu; yalnızca yönetici.
- Görev bağlamı: adımın adı, kaynak sayfa, gerekçe, isteğin IP'si ve iş kimliği görevle saklanır; yeniden deneme
  adı ve gerekçeyi taşır. GET /api/devices/{pc}/activity cihazın son işlemlerini bu bağlamla döner.
- Ajan politikasını kimin, ne zaman değiştirdiği saklanır (GET /api/agent_policies/meta) ve denetim kaydına yazılır.
- GET /api/logs bilgisayara (pc) ve tarih aralığına (since / until, iki gün de dahil) göre süzer.

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (denetim kaydı).
Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import datetime
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import db, timeutil  # noqa: E402
from pops.audit import add_audit_log  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
PCS = ["HW-JB1", "HW-JB2"]
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


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


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    for table, col in (("clients", "pc_name"), ("tasks", "target_pc"), ("agent_versions", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), PCS)


async def main():
    c = await conn()
    await cleanup(c)
    await c.execute(
        "INSERT INTO clients (pc_name, hostname, lab_name, status, running_version) VALUES "
        "('HW-JB1', 'jb1', 'JB-Lab', 'Online', '0.1.15-alpha'), ('HW-JB2', 'jb2', 'JB-Lab', 'Offline', '0.1.14-alpha')"
    )
    for u, role in (("jbadmin", "admin"), ("jbviewer", "viewer")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0", u, role)
    admin = server.create_jwt("jbadmin", "admin", 0)
    viewer = server.create_jwt("jbviewer", "viewer", 0)
    db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=2,
    )
    try:
        await run(c, admin, viewer)
    finally:
        await cleanup(c)
        await db.db_pool.close()
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM ISLEM MERKEZI TESTLERI GECTI")


async def run(c, admin, viewer):
    print("== görev kimlikleri ve durumları")
    body = {"target_mode": "PC", "targets": PCS, "taskSequence": [
        {"name": "a", "type": "CMD", "command": "echo jb-1"}, {"name": "b", "type": "CMD", "command": "echo jb-2"}]}
    s, b = req("/api/deploy_orchestration", admin, body)
    ids = b.get("task_ids") or []
    chk(s == 200 and b.get("created") == 4 and len(ids) == 4 and ids == sorted(ids), "4 görev, 4 kimlik (%s)" % b)
    rows = await c.fetch("SELECT id, target_pc, script_path FROM tasks WHERE id = ANY($1::int[]) ORDER BY id", ids)
    chk([(r["target_pc"], r["script_path"]) for r in rows] == [("HW-JB1", "echo jb-1"), ("HW-JB1", "echo jb-2"),
                                                               ("HW-JB2", "echo jb-1"), ("HW-JB2", "echo jb-2")],
        "kimlikler hedef ve adım sırasıyla")
    s, d = req("/api/deploy_orchestration", admin, body)
    chk(s == 200 and d.get("duplicate") is True and d.get("task_ids") == ids, "yinelenen istek aynı kimlikleri döner")
    s, st = req("/api/tasks/status", viewer, {"ids": ids + [999999999]})
    items = {i["id"]: i for i in st.get("items", [])}
    chk(s == 200 and set(items) == set(ids) and all(i["status"] == "Pending" for i in items.values())
        and items[ids[0]]["target_pc"] == "HW-JB1", "durumlar döndü, olmayan kimlik yok")
    chk(req("/api/tasks/status", None, {"ids": ids})[0] == 401, "oturumsuz 401")
    chk(req("/api/tasks/status", viewer, {"ids": list(range(5001))})[0] == 422, "en çok 5000 kimlik")
    await c.execute("UPDATE tasks SET status = 'Failed' WHERE id = $1", ids[0])
    s, r = req("/api/tasks/action", admin, {"action": "RETRY", "target_mode": "TASK", "target_id": str(ids[0])})
    chk(s == 200 and r.get("changed") == 1 and len(r.get("task_ids") or []) == 1 and r["task_ids"][0] not in ids,
        "yeniden deneme yeni kimliği döner")

    print("== görev bağlamı ve cihazın son işlemleri")
    ctx = {"target_mode": "PC", "targets": ["HW-JB1"], "taskSequence": [{"name": "Yeniden başlat", "type": "CMD",
           "command": "echo jb-ctx"}], "title": "Yeniden başlat · JB-Lab", "source": "labs", "reason": "ders bitti"}
    s, b = req("/api/deploy_orchestration", admin, ctx)
    cid = (b.get("task_ids") or [None])[0]
    row = await c.fetchrow("SELECT title, source, reason, client_ip, batch_id FROM tasks WHERE id = $1", cid)
    chk(s == 200 and row and row["title"] == "Yeniden başlat" and row["source"] == "labs"
        and row["reason"] == "ders bitti" and row["client_ip"] == "127.0.0.1" and len(row["batch_id"] or "") == 16,
        "bağlam saklandı (%s)" % (dict(row) if row else None))
    batch = await c.fetch("SELECT DISTINCT batch_id FROM tasks WHERE id = ANY($1::int[])", ids)
    chk(len(batch) == 1 and batch[0]["batch_id"], "aynı istekteki görevler tek iş kimliğinde")
    chk(req("/api/deploy_orchestration", admin, dict(ctx, reason="x" * 501))[0] == 422, "gerekçe en çok 500 karakter")

    await c.execute("UPDATE tasks SET status = 'Denied', exit_code = -5 WHERE id = $1", cid)
    s, r = req("/api/tasks/action", admin, {"action": "RETRY", "target_mode": "TASK", "target_id": str(cid)})
    rid = (r.get("task_ids") or [None])[0]
    rrow = await c.fetchrow("SELECT title, source, reason, batch_id FROM tasks WHERE id = $1", rid)
    chk(rrow and rrow["title"] == "Yeniden başlat" and rrow["reason"] == "ders bitti" and rrow["source"] == "tasks"
        and rrow["batch_id"] != row["batch_id"], "yeniden deneme adı ve gerekçeyi taşır, yeni iş")
    s, a = req("/api/devices/HW-JB1/activity?limit=5", viewer)
    acts = a.get("items") or []
    first = acts[0] if acts else {}
    chk(s == 200 and len(acts) == 5 and first.get("id") == rid and first.get("by") == "jbadmin"
        and first.get("source") == "tasks" and first.get("title") == "Yeniden başlat", "son işlemler yeniden eskiye")
    denied = next((x for x in acts if x.get("id") == cid), {})
    chk(denied.get("status") == "Denied" and denied.get("exit_code") == -5 and denied.get("reason") == "ders bitti"
        and denied.get("ip") == "127.0.0.1", "reddedilen görev nedeniyle")
    chk(req("/api/devices/HW-JB1/activity", None)[0] == 401, "son işlemler: oturumsuz 401")

    print("== istek doğrulaması: hedef türü, bilinmeyen bilgisayar, tanınmayan alan")
    seq = [{"name": "v", "type": "CMD", "command": "echo jb-valid"}]
    s, b = req("/api/deploy_orchestration", admin, {"target_mode": "pc", "targets": ["HW-JB1"], "taskSequence": seq})
    chk(s == 200 and b.get("created") == 1, "küçük harfli hedef türü kabul edilir (%s)" % s)
    s, b = req("/api/deploy_orchestration", admin, {"target_mode": "lab", "targets": ["JB-Lab"], "taskSequence": seq})
    made = await c.fetch("SELECT target_pc FROM tasks WHERE id = ANY($1::int[])", (b or {}).get("task_ids") or [])
    chk(s == 200 and sorted(r["target_pc"] for r in made) == ["HW-JB1", "HW-JB2"],
        "\"lab\" sınıf olarak çözülür, bilgisayar adı sanılmaz")
    chk(req("/api/deploy_orchestration", admin, {"target_mode": "GROUP", "targets": ["x"], "taskSequence": seq})[0]
        == 422, "bilinmeyen hedef türü 422")
    unknown = {"target_mode": "PC", "targets": ["HW-JB1", "HW-JB-YOK"], "taskSequence": seq}
    s, b = req("/api/deploy_orchestration", admin, unknown)
    n = await c.fetchval("SELECT count(*) FROM tasks WHERE target_pc = 'HW-JB-YOK'")
    chk(s == 422 and n == 0, "kayıtlı olmayan bilgisayar 422, hiç görev açılmadı (%s)" % s)
    chk(req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": ["HW-JB1"], "taskSequence": seq,
                                                 "priority": 1})[0] == 422, "tanınmayan alan 422")
    chk(req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": ["HW-JB1"], "taskSequence": [
        dict(seq[0], timeout=5)]})[0] == 422, "adımda tanınmayan alan 422")

    print("== politika: son değiştiren")
    # CI'da aynı veritabanını kullanan sonraki testler politikayı değişmemiş bulsun: önce saklanır, sonra geri yazılır
    saved = await c.fetch(
        "SELECT key, value FROM global_settings WHERE key IN ('agent_policies', 'agent_policies_meta')")
    await c.execute("DELETE FROM global_settings WHERE key = 'agent_policies_meta'")
    chk(req("/api/agent_policies/meta", viewer)[1].get("updated_by") is None, "değişiklik yokken boş")
    policy = {"fair_use_text": "jb", "dns_categories": [], "auto_quarantine": False, "quarantine_threshold": 5}
    s, _ = req("/api/agent_policies", admin, policy)
    s2, m = req("/api/agent_policies/meta", viewer)
    chk(s == 200 and s2 == 200 and m.get("updated_by") == "jbadmin" and str(m.get("updated_at", "")).startswith("20"),
        "kim ve ne zaman saklandı (%s)" % m)
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE action = 'policy_update'") >= 1,
        "denetim kaydına yazıldı")
    chk(req("/api/agent_policies/meta", None)[0] == 401, "meta: oturumsuz 401")
    await c.execute("DELETE FROM global_settings WHERE key IN ('agent_policies', 'agent_policies_meta')")
    for r in saved:
        await c.execute("INSERT INTO global_settings (key, value) VALUES ($1, $2)", r["key"], r["value"])

    print("== olay kayıtları: bilgisayar ve tarih süzgeci")
    # Günler sunucunun saat diliminde (sunucu testle aynı ortamda: POPS_TZ ya da yerel saat dilimi)
    for pc, msg, ts in (("HW-JB1", "jb-log-1", "2026-01-10 09:00:00"), ("HW-JB1", "jb-log-2", "2026-01-11 23:59:59"),
                        ("HW-JB1", "jb-log-3", "2026-01-12 00:00:00"), ("HW-JB2", "jb-log-4", "2026-01-11 10:00:00")):
        at = datetime.datetime.strptime(ts, timeutil.TEXT_FORMAT).replace(tzinfo=timeutil.zone())
        await c.execute(
            "INSERT INTO agent_logs_v2 (pc_name, event_type, message, \"timestamp\") VALUES ($1, 'auth.login', $2, $3)",
            pc, msg, at)
    s, lg = req("/api/logs?pc=HW-JB1&since=2026-01-10&until=2026-01-11", viewer)
    got = [x.get("message") for x in lg] if isinstance(lg, list) else lg
    chk(s == 200 and got == ["jb-log-2", "jb-log-1"], "bilgisayar ve tarih aralığı, iki gün de dahil (%s)" % got)
    s, lg = req("/api/logs?since=2026-01-11&until=2026-01-11", viewer)
    chk(s == 200 and sorted(x["message"] for x in lg if x["message"].startswith("jb-log")) == ["jb-log-2", "jb-log-4"],
        "yalnızca tarih")
    chk(req("/api/logs?since=11.01.2026", viewer)[0] == 422, "bozuk tarih 422")
    s, lg = req("/api/logs?pc=HW-JB1&limit=1", viewer)
    chk(s == 200 and len(lg) == 1, "limit süzgeçle birlikte")
    await c.execute("DELETE FROM agent_logs_v2 WHERE message LIKE 'jb-log-%'")

    print("== ajan güncellemesi ilerlemesi")
    since = time.time() - 5
    await add_audit_log("HW-JB1", "update_result", "Ajan guncelleme sonucu: success",
                        {"status": "success", "to_version": "0.1.15-alpha", "rollback": "none"})
    s, p = req("/api/system/update-progress", admin,
               {"pcs": PCS + ["HW-YOK"], "version": "v0.1.15-alpha", "since": since})
    items = {i["pc"]: i for i in p.get("items", [])}
    chk(s == 200 and items["HW-JB1"]["on_target"] and items["HW-JB1"]["online"]
        and (items["HW-JB1"]["result"] or {}).get("status") == "success", "hedefte, sonuç başarılı")
    chk(not items["HW-JB2"]["on_target"] and not items["HW-JB2"]["online"] and items["HW-JB2"]["result"] is None,
        "eski sürümde, çevrimdışı, sonuç yok")
    chk(items["HW-YOK"]["known"] is False, "bilinmeyen cihaz işaretli")
    chk(all(items[pc]["pending"] is False and items[pc]["sent_at"] is None and items[pc]["stage"] is None
            and items[pc]["stage_at"] is None for pc in items) and abs(p.get("now", 0) - time.time()) < 60,
        "bekleyen gönderim yokken adım alanları boş, sunucu saati döner")
    s, p = req("/api/system/update-progress", admin, {"pcs": ["HW-JB1"], "version": "0.1.15-alpha",
                                                      "since": time.time() + 3600})
    chk(s == 200 and p["items"][0]["result"] is None, "gönderimden önceki sonuç sayılmaz")
    chk(req("/api/system/update-progress", viewer, {"pcs": PCS, "version": "x"})[0] == 403, "yalnız yönetici")


if __name__ == "__main__":
    asyncio.run(main())
