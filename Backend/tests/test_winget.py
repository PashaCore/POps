"""winget dağıtımı — entegrasyon testi (CI 'security' job'ı).

- Katalog ucu: arama, kategori, /api/v1 karşılığı, oturumsuz 401, uzun sorgu 422.
- WINGET adımı deploy_orchestration ile kuyruğa girer: görev kaydında kind = 'winget', paket payload'da, script_path
  okunur komut satırı. Geçersiz kimlik/sürüm ya da komut taşıyan WINGET adımı 422.
- Kuyruk, X-Agent-Features ile "winget" duyuran ajana "winget_install" gönderir (execute DEĞİL, komut taşımaz);
  duyurmayan (eski) ajana hiçbir şey gitmez, görev "Denied" (-8) olur.
- Simüle ajanın sonucu: 0 ve "zaten kurulu" kodu "Completed", winget hatası "Failed", winget yok (-7, [REDDEDİLDİ])
  "Denied"; her sonuç result_ack ile onaylanır. Yeniden deneme winget görevi olarak kalır. Zincirde winget adımından
  sonra komut adımı execute ile gider.
- Dosya dağıtımı modülü laboratuvarda kapalıyken WINGET adımı 409, bekleyen winget görevi "Denied" ([MODÜL KAPALI]).
- server_info.features "winget" içerir; /api/devices ajanın özelliklerini, cihaz geçmişi görevin türünü döner.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
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

import server  # noqa: E402  (create_jwt)
from pops import winget  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
LAB = "WG-Lab"
NEW, OLD = "HW-WG1", "HW-WG2"
PCS = [NEW, OLD]
# Sürümler bu çalıştırmaya özgü: sunucu 5 sn içinde yinelenen isteğe önceki görevleri döner (art arda iki çalıştırma)
RUN = str(os.getpid())
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
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


async def collect(ws, seconds):
    out = []
    loop = asyncio.get_event_loop()
    end = loop.time() + seconds
    while loop.time() < end:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - loop.time()))
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            break
        try:
            out.append(json.loads(raw))
        except ValueError:
            pass
    return out


_INBOX = {}


async def wait_msg(ws, pred, seconds=8):
    """İlk uyan ileti ve bu çağrıda bakılan iletiler. Uymayanlar sonraki çağrılar için saklanır: aynı anda gelen
    iletilerden (ör. result_ack ve arkasından sıradaki görev) biri kaybolmasın."""
    box = _INBOX.setdefault(id(ws), [])
    seen = list(box)
    for i, m in enumerate(box):
        if pred(m):
            del box[i]
            return m, seen
    loop = asyncio.get_event_loop()
    end = loop.time() + seconds
    while loop.time() < end:
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - loop.time()))
        except (asyncio.TimeoutError, websockets.exceptions.ConnectionClosed):
            break
        try:
            m = json.loads(raw)
        except ValueError:
            continue
        seen.append(m)
        if pred(m):
            return m, seen
        box.append(m)
    return None, seen


async def wait_for(c, sql, *args, timeout=8):
    for _ in range(int(timeout / 0.2)):
        value = await c.fetchval(sql, *args)
        if value:
            return value
        await asyncio.sleep(0.2)
    return await c.fetchval(sql, *args)


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    for table, col in (("agent_secrets", "pc_name"), ("clients", "pc_name"), ("tasks", "target_pc"),
                       ("agent_versions", "pc_name"), ("hw_inventory", "pc_name")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), PCS)
    await c.execute("DELETE FROM module_settings WHERE scope_id = $1", LAB)


async def agent(pc, features=None):
    headers = {"X-Agent-Secret": pc + "-s", "X-Agent-Version": "0.1.23-alpha"}
    if features is not None:
        headers["X-Agent-Features"] = features
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc), additional_headers=headers)
    await ws.send(json.dumps({"hw_id": pc, "hostname": pc.lower(), "status": "Online", "dna_payload": {
        "hardware": {"uuid": pc + "-U", "bios_sn": pc + "-B", "disk_sn": "-", "mac": "-", "ram_sn": "-"},
        "capabilities": {}}}))
    return ws


def step(pkg_id, version=None, name=None):
    return {"name": name if name is not None else "winget: " + pkg_id, "type": "WINGET",
            "winget": {"id": pkg_id, "version": version}}


def deploy(token, targets, seq):
    return req("/api/deploy_orchestration", token, {"target_mode": "PC", "targets": targets, "taskSequence": seq,
                                                    "title": "wg-test", "source": "deploy", "reason": "wg"})


async def main():
    c = await conn()
    old_limit = await c.fetchval("SELECT value FROM global_settings WHERE key='concurrent_limit'")
    await cleanup(c)
    for pc in PCS:
        await c.execute("INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES ($1, $2, $3, 'Offline')",
                        pc, pc.lower(), LAB)
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2)",
                        pc, hashlib.sha256((pc + "-s").encode()).hexdigest())
        await c.execute("INSERT INTO hw_inventory (pc_name, cpu) VALUES ($1, 'test-cpu') ON CONFLICT DO NOTHING", pc)
    for u, role in (("wgadmin", "admin"), ("wgviewer", "viewer"), ("wgsuper", "superadmin")):
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0", u, role)
    roles = (("wgadmin", "admin"), ("wgviewer", "viewer"), ("wgsuper", "superadmin"))
    tokens = {u: server.create_jwt(u, r, 0) for u, r in roles}
    await c.execute("INSERT INTO global_settings (key,value) VALUES ('concurrent_limit','10') "
                    "ON CONFLICT (key) DO UPDATE SET value='10'")
    try:
        await run(c, tokens["wgadmin"], tokens["wgviewer"], tokens["wgsuper"])
    finally:
        req("/api/modules/deploy", tokens["wgsuper"], {"enabled": None, "lab": LAB})
        await cleanup(c)
        if old_limit is None:
            await c.execute("DELETE FROM global_settings WHERE key = 'concurrent_limit'")
        else:
            await c.execute("UPDATE global_settings SET value=$1 WHERE key='concurrent_limit'", old_limit)
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM WINGET TESTLERI GECTI")


async def run(c, admin, viewer, superadmin):
    print("== katalog")
    s, b = req("/api/deploy/winget/catalog?q=firefox", viewer)
    chk(s == 200 and [p["id"] for p in b.get("items", [])][:1] == ["Mozilla.Firefox"], "arama (izleyici de okur)")
    chk(b.get("total", 0) >= 60 and b.get("categories") and b.get("source", "").endswith("winget-pkgs"),
        "toplam, kategoriler, kaynak")
    s, b = req("/api/v1/deploy/winget/catalog?category=education&limit=500", admin)
    chk(s == 200 and b["items"] and all(p["category"] == "education" for p in b["items"]), "/api/v1 ve kategori")
    chk(req("/api/deploy/winget/catalog")[0] == 401, "oturumsuz 401")
    chk(req("/api/deploy/winget/catalog?q=" + "x" * 101, admin)[0] == 422, "uzun sorgu 422")

    print("== doğrulama")
    for seq, why in (
        ([step("Mozilla.Firefox & calc")], "kabuk karakterli kimlik"),
        ([step("Mozilla.Firefox", "1 2")], "boşluklu sürüm"),
        ([dict(step("Mozilla.Firefox"), command="calc.exe")], "komut taşıyan WINGET"),
        ([{"name": "x", "type": "WINGET"}], "paketsiz WINGET"),
        ([{"name": "x", "type": "CMD", "command": "echo", "winget": {"id": "Git.Git"}}], "winget alanlı CMD"),
    ):
        s, _ = deploy(admin, [NEW], seq)
        chk(s == 422, "%s 422 (%s)" % (why, s))
    chk(deploy(viewer, [NEW], [step("Git.Git")])[0] == 403, "izleyici dağıtamaz")
    chk(not await c.fetchval("SELECT count(*) FROM tasks WHERE target_pc = ANY($1::text[])", PCS),
        "reddedilen istek görev açmadı")

    print("== yeni ve eski ajan")
    new = await agent(NEW, "winget")
    old = await agent(OLD)
    info, _ = await wait_msg(new, lambda m: m.get("action") == "server_info")
    chk(info is not None and "winget" in info.get("features", []), "server_info.features winget içerir")
    await collect(old, 0.5)
    chk(await c.fetchval("SELECT features FROM agent_versions WHERE pc_name=$1", NEW) == ["winget"],
        "yeni ajanın özelliği saklandı")
    chk(await c.fetchval("SELECT features FROM agent_versions WHERE pc_name=$1", OLD) == [],
        "eski ajanda özellik yok")
    s, devs = req("/api/devices", admin)
    by = {d["hw_id"]: d for d in devs if isinstance(d, dict)}
    chk(by.get(NEW, {}).get("agent_features") == ["winget"] and by.get(OLD, {}).get("agent_features") == [],
        "/api/devices ajan özelliklerini döner")

    s, b = deploy(admin, [NEW, OLD], [step("Mozilla.Firefox", "124." + RUN)])
    chk(s == 200 and b.get("created") == 2, "iki bilgisayara winget görevi (%s %s)" % (s, b))
    ids = b.get("task_ids", [])
    rows = {r["target_pc"]: dict(r) for r in await c.fetch("SELECT * FROM tasks WHERE id = ANY($1::int[])", ids)}
    t_new, t_old = rows.get(NEW, {}), rows.get(OLD, {})
    chk(t_new.get("kind") == "winget" and json.loads(t_new.get("payload") or "{}") == {
        "id": "Mozilla.Firefox", "version": "124." + RUN}, "görev kaydı: tür ve paket")
    chk(t_new.get("script_path") == winget.command_line("Mozilla.Firefox", "124." + RUN)
        and t_new.get("title") == "winget: Mozilla.Firefox", "okunur komut ve başlık")
    msg, seen = await wait_msg(new, lambda m: m.get("task_id") == t_new.get("id") and "action" in m)
    chk(msg == {"action": "winget_install", "task_id": t_new.get("id"), "id": "Mozilla.Firefox",
                "version": "124." + RUN, "requested_by": "wgadmin"}, "ajana winget_install gitti: %s" % msg)
    chk(not [m for m in seen if m.get("action") == "execute"], "execute gönderilmedi")
    old_msgs = await collect(old, 1.5)
    chk(not [m for m in old_msgs if m.get("task_id") == t_old.get("id")], "eski ajana hiçbir şey gitmedi")
    chk(await wait_for(c, "SELECT status = 'Denied' FROM tasks WHERE id=$1", t_old.get("id")),
        "eski ajanın görevi 'Denied'")
    row = await c.fetchrow("SELECT exit_code, output FROM tasks WHERE id=$1", t_old.get("id"))
    chk(row["exit_code"] == winget.EXIT_UNSUPPORTED and row["output"] == winget.UNSUPPORTED_OUTPUT,
        "çıkış kodu -8 ve açıklama")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", t_new.get("id")) == "Running", "yeni ajanda Running")
    chk(await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE hw_id=$1 AND action='winget_install' "
                         "AND (changes::jsonb->>'task_id')::int = $2", NEW, t_new.get("id")) == 1,
        "denetim zincirine yazıldı")

    print("== sonuçlar")

    async def result(task_id, exit_code, output):
        await new.send(json.dumps({"type": "result", "pc_name": NEW, "task_id": task_id, "output": output,
                                   "exit_code": exit_code}))
        ack, _ = await wait_msg(new, lambda m: m.get("action") == "result_ack" and m.get("task_id") == task_id)
        return ack is not None

    chk(await result(t_new["id"], 0, "Successfully installed"), "sonuç onaylandı")
    chk(await wait_for(c, "SELECT status = 'Completed' FROM tasks WHERE id=$1", t_new["id"]), "0: Completed")

    async def one(pkg, version=None):
        # Aynı istek 5 sn içinde yinelenirse sunucu aynı görevleri döner: her deneme ayrı sürüm ister
        s, b = deploy(admin, [NEW], [step(pkg, version)])
        tid = (b.get("task_ids") or [None])[0]
        msg, _ = await wait_msg(new, lambda m: m.get("action") == "winget_install" and m.get("task_id") == tid)
        return tid, msg

    for n, (code, output, status) in enumerate((
        (-1978335135, "Found an existing package already installed.", "Completed"),
        (-1978335189, "No available upgrade found.", "Completed"),
        (-1978334967, "Restart your PC to finish installation.", "Completed"),
        (-1978335212, "No package found matching input criteria.", "Failed"),
        (winget.EXIT_UNAVAILABLE, "[REDDEDİLDİ] winget bu bilgisayarda yok", "Denied"),
        (winget.EXIT_DENIED, "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası).", "Denied"),
        (1, "[REDDEDİLDİ] ama sıfır olmayan başka bir kod", "Failed"),
    )):
        tid, msg = await one("7zip.7zip", "26.%d.%s" % (n, RUN))
        chk(msg is not None, "görev gönderildi (%s)" % code)
        await result(tid, code, output)
        chk(await wait_for(c, "SELECT status = $2 FROM tasks WHERE id=$1", tid, status),
            "%s -> %s (%s)" % (code, status, await c.fetchval("SELECT status FROM tasks WHERE id=$1", tid)))
        chk(await c.fetchval("SELECT exit_code FROM tasks WHERE id=$1", tid) == code, "çıkış kodu saklandı")
    s, tasks = req("/api/tasks?limit=50", admin)
    mine = [t for t in tasks if t.get("target_pc") == NEW]
    chk(mine and all(t.get("payload", {}).get("id") in ("Mozilla.Firefox", "7zip.7zip") for t in mine),
        "/api/tasks paketi nesne olarak döner")
    s, act = req("/api/devices/%s/activity?limit=5" % NEW, admin)
    chk(s == 200 and act["items"] and all(i.get("task_kind") == "winget" for i in act["items"]),
        "cihaz geçmişinde görevin türü")

    print("== yeniden deneme ve zincir")
    failed = await c.fetchval("SELECT id FROM tasks WHERE target_pc=$1 AND status='Failed' ORDER BY id LIMIT 1", NEW)
    s, b = req("/api/tasks/action", admin, {"action": "RETRY", "target_mode": "TASK", "target_id": str(failed)})
    retry = (b.get("task_ids") or [None])[0]
    chk(s == 200 and retry, "yeniden deneme açıldı")
    msg, seen = await wait_msg(new, lambda m: m.get("task_id") == retry and "action" in m)
    chk(msg is not None and msg.get("action") == "winget_install" and msg.get("id") == "7zip.7zip"
        and "script_path" not in msg, "yeniden deneme de winget_install (komut olarak gitmedi)")
    chk(await c.fetchval("SELECT kind FROM tasks WHERE id=$1", retry) == "winget", "yeniden denemenin türü winget")
    await result(retry, 0, "ok")
    s, b = deploy(admin, [NEW], [step("Git.Git", RUN), {"name": "sonra", "type": "CMD", "command": "echo wg-sonra"}])
    first_id, second_id = b.get("task_ids", [None, None])[:2]
    msg, _ = await wait_msg(new, lambda m: m.get("task_id") == first_id)
    chk(msg and msg.get("action") == "winget_install", "zincirin ilk adımı winget_install")
    await result(first_id, 0, "ok")
    msg, _ = await wait_msg(new, lambda m: m.get("task_id") == second_id)
    chk(msg and msg.get("action") == "execute" and msg.get("script_path") == "echo wg-sonra",
        "ikinci adım execute ile gitti")
    await result(second_id, 0, "wg-sonra")

    print("== dosya dağıtımı modülü")
    s, _ = req("/api/modules/deploy", superadmin, {"enabled": False, "lab": LAB})
    chk(s == 200, "laboratuvarda dosya dağıtımı kapatıldı")
    s, b = deploy(admin, [NEW], [step("Git.Git")])
    chk(s == 409, "kapalı laboratuvara WINGET adımı 409 (%s)" % s)
    late = await c.fetchval(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, kind, payload) VALUES "
        "($1, $2, $3, 'Pending', to_char(now(), 'YYYY-MM-DD HH24:MI:SS'), 'winget', $4::jsonb) RETURNING id",
        NEW, LAB, winget.command_line("Git.Git"), winget.payload("Git.Git"))
    s, b = deploy(admin, [NEW], [{"name": "komut", "type": "CMD", "command": "echo wg-modul " + RUN}])
    chk(s == 200, "uzak komut açık: komut kabul")
    cmd_id = (b.get("task_ids") or [None])[0]
    msg, seen = await wait_msg(new, lambda m: m.get("task_id") == cmd_id)
    chk(msg and msg.get("action") == "execute", "komut gönderildi")
    chk(not [m for m in seen if m.get("task_id") == late], "winget görevi gönderilmedi")
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", late) == "Denied"
        and "MODÜL KAPALI" in (await c.fetchval("SELECT output FROM tasks WHERE id=$1", late) or ""),
        "bekleyen winget görevi reddedildi")
    await result(cmd_id, 0, "wg-modul")
    paused = await c.fetchval(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, kind, payload) VALUES "
        "($1, $2, $3, 'Paused', to_char(now(), 'YYYY-MM-DD HH24:MI:SS'), 'winget', $4::jsonb) RETURNING id",
        OLD, LAB, winget.command_line("Git.Git"), winget.payload("Git.Git"))
    req("/api/modules/deploy", superadmin, {"enabled": None, "lab": LAB})
    s, b = req("/api/modules/deploy", superadmin, {"enabled": False, "lab": LAB})
    chk(s == 200 and b.get("tasks_denied", 0) >= 1, "modül kapanınca duraklatılmış winget görevi reddedildi (%s)" % b)
    chk(await c.fetchval("SELECT status FROM tasks WHERE id=$1", paused) == "Denied", "duraklatılmış görev 'Denied'")
    await new.close()
    await old.close()


if __name__ == "__main__":
    asyncio.run(main())
