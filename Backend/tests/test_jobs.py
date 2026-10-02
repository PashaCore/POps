"""İşlem merkezi için uçlar — entegrasyon testi (CI 'security' job'ı).

- Görev oluşturan istek görev kimliklerini döner (yinelenen istek aynı kimlikleri), yeniden deneme de yeni kimlikleri.
- POST /api/tasks/status verilen görevlerin durumunu döner; silinmiş ya da olmayan görev listede yoktur.
- POST /api/system/update-progress ajan güncellemesinin cihaz cihaz durumunu döner: sürüm hedefte mi, bekleyen
  gönderim, gönderimden sonraki güncelleme sonucu; yalnızca yönetici.

Bazı adımlar sunucunun modüllerini bu süreçte, aynı veritabanına bağlanarak çağırır (denetim kaydı).
Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import db  # noqa: E402
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
    s, p = req("/api/system/update-progress", admin, {"pcs": ["HW-JB1"], "version": "0.1.15-alpha",
                                                      "since": time.time() + 3600})
    chk(s == 200 and p["items"][0]["result"] is None, "gönderimden önceki sonuç sayılmaz")
    chk(req("/api/system/update-progress", viewer, {"pcs": PCS, "version": "x"})[0] == 403, "yalnız yönetici")


if __name__ == "__main__":
    asyncio.run(main())
