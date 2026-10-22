"""Güç komutları ve kullanıcıya mesaj — entegrasyon testi (CI 'security' job'ı).

- POST /api/devices/power ve /api/devices/message: doğrulama (op, gecikme, not/başlık/metin sınırları, kontrol
  karakterleri), izleyici 403, kayıtsız cihaz 422, /api/v1 karşılığı; API jetonu güç komutu verebilir, mesaj yazamaz.
- Simüle ajanlar: yeni (X-Agent-Features: power,message), eski Windows (başlık yok), Linux (platform = linux).
  Yeni ajana "power" / "user_message" gider; eski ajana kapatma/yeniden başlatma execute ile (Windows'ta güvenli
  notla, Linux'ta notsuz kalıpla), kilit/oturumu kapatma/mesaj hiç gitmez ve görev "Denied" (-8) olur.
- Sonuçlar: 0 -> Completed, -6 [REDDEDİLDİ] -> Denied, -5 + capability_denied -> Denied; okundu onayı bekleyen mesaj
  cihazın kuyruğunu bekletmez. Uzak komut modülü kapalı laboratuvarda yeni ajana güç komutu gider, eski ajanınki
  reddedilir. Kapalı bilgisayara görev açılmaz.
- Denetim kaydı: power_command / user_message kayıtları var, mesaj metninin tamamı hiçbir kayıtta yok.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET.
"""

import asyncio
import hashlib
import json
import os
import re
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import power  # noqa: E402
from pops.config import LOG_TABLE  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http://", "ws://").replace("https://", "wss://"))
LAB = "PW-Lab"
NEW, OLD, LIN, OFF = "HW-PW1", "HW-PW2", "HW-PW3", "HW-PW4"
PCS = [NEW, OLD, LIN, OFF]
RUN = str(os.getpid())
# Mesajın sonundaki işaret: denetim kaydına yalnızca ilk 60 karakter yazılır, bu hiçbir kayıtta görünmemeli
SECRET_TAIL = "GIZLI-SON-" + RUN
LONG_TEXT = ("Değerli öğrenciler, lütfen açık dosyalarınızı kaydedin ve oturumu kapatmaya hazırlanın. "
             "Sınav birazdan başlıyor. " + SECRET_TAIL)
LINUX_POWER = re.compile(r"^\s*shutdown\s+/([rs])\s+/f\s+/t\s+(\d{1,4})\s*$", re.IGNORECASE)   # Linux ajanının kalıbı
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


_INBOX = {}


async def wait_msg(ws, pred, seconds=8):
    """İlk uyan ileti ve bu çağrıda bakılan iletiler; uymayanlar sonraki çağrılar için saklanır."""
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


async def drain(ws, seconds):
    _m, seen = await wait_msg(ws, lambda m: False, seconds)
    _INBOX[id(ws)] = []
    return seen


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
    await c.execute("DELETE FROM api_tokens WHERE name = $1", "pw-token-" + RUN)


async def agent(pc, features=None, platform=None):
    headers = {"X-Agent-Secret": pc + "-s", "X-Agent-Version": "0.1.23-alpha"}
    if features is not None:
        headers["X-Agent-Features"] = features
    if platform:
        headers["X-Agent-Platform"] = platform
    ws = await websockets.connect("%s/ws/agent/%s" % (WS, pc), additional_headers=headers)
    await ws.send(json.dumps({"hw_id": pc, "hostname": pc.lower(), "status": "Online", "dna_payload": {
        "hardware": {"uuid": pc + "-U", "bios_sn": pc + "-B", "disk_sn": "-", "mac": "-", "ram_sn": "-"},
        "capabilities": {}}}))
    return ws


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
    roles = (("pwadmin", "admin"), ("pwviewer", "viewer"), ("pwsuper", "superadmin"))
    for u, role in roles:
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0) "
            "ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0", u, role)
    tokens = {u: server.create_jwt(u, r, 0) for u, r in roles}
    # Kota 1: güç komutu ve mesaj kotayı tutmaz, dolu kotada da gider
    await c.execute("INSERT INTO global_settings (key,value) VALUES ('concurrent_limit','1') "
                    "ON CONFLICT (key) DO UPDATE SET value='1'")
    try:
        await run(c, tokens["pwadmin"], tokens["pwviewer"], tokens["pwsuper"])
    finally:
        req("/api/modules/terminal", tokens["pwsuper"], {"enabled": None, "lab": LAB})
        await cleanup(c)
        if old_limit is None:
            await c.execute("DELETE FROM global_settings WHERE key = 'concurrent_limit'")
        else:
            await c.execute("UPDATE global_settings SET value=$1 WHERE key='concurrent_limit'", old_limit)
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM GUC/MESAJ TESTLERI GECTI")


async def task_ids(c, b):
    rows = await c.fetch("SELECT id, target_pc FROM tasks WHERE id = ANY($1::int[])", b.get("task_ids", []))
    return {r["target_pc"]: r["id"] for r in rows}


def pw(token, targets, op, delay=0, message=None, mode="PC", path="/api/devices/power"):
    body = {"target_mode": mode, "targets": targets, "op": op, "delay": delay, "source": "devices"}
    if message is not None:
        body["message"] = message
    return req(path, token, body)


def msg(token, targets, title, text, style="info", ack=False, path="/api/devices/message"):
    return req(path, token, {"target_mode": "PC", "targets": targets, "title": title, "text": text, "style": style,
                             "requires_ack": ack, "source": "devices"})


async def run(c, admin, viewer, superadmin):
    print("== doğrulama ve yetki")
    for body, why in (
        ({"targets": [NEW], "op": "hibernate"}, "bilinmeyen op"),
        ({"targets": [NEW], "op": "shutdown", "delay": 601}, "gecikme 600'ü aşıyor"),
        ({"targets": [NEW], "op": "shutdown", "delay": -1}, "eksi gecikme"),
        ({"targets": [NEW], "op": "shutdown", "message": "x" * 201}, "not 200 karakteri aşıyor"),
        ({"targets": [NEW], "op": "shutdown", "command": "calc"}, "tanınmayan alan"),
        ({"targets": [], "op": "lock"}, "hedefsiz"),
        ({"targets": ["HW-YOK-" + RUN], "op": "lock"}, "kayıtsız cihaz"),
    ):
        s, _ = req("/api/devices/power", admin, body)
        chk(s == 422, "güç: %s 422 (%s)" % (why, s))
    for title, text, why in (("", "metin", "boş başlık"), ("\u0007\u0008", "metin", "yalnızca kontrol karakteri"),
                             ("Başlık", "  \n ", "boş metin"), ("x" * 81, "metin", "uzun başlık"),
                             ("Başlık", "y" * 1001, "uzun metin")):
        s, _ = msg(admin, [NEW], title, text)
        chk(s == 422, "mesaj: %s 422 (%s)" % (why, s))
    s, _ = req("/api/devices/message", admin, {"targets": [NEW], "title": "a", "text": "b", "style": "danger"})
    chk(s == 422, "mesaj: bilinmeyen style 422")
    chk(pw(viewer, [NEW], "lock")[0] == 403, "izleyici güç komutu veremez")
    chk(msg(viewer, [NEW], "a", "b")[0] == 403, "izleyici mesaj gönderemez")
    chk(req("/api/devices/power", None, {"targets": [NEW], "op": "lock"})[0] == 401, "oturumsuz 401")
    chk(not await c.fetchval("SELECT count(*) FROM tasks WHERE target_pc = ANY($1::text[])", PCS),
        "reddedilen istek görev açmadı")

    print("== ajanlar")
    new = await agent(NEW, "winget, power,message")
    old = await agent(OLD)
    lin = await agent(LIN, None, "linux")
    info, _ = await wait_msg(new, lambda m: m.get("action") == "server_info")
    chk(info is not None and {"power", "message"} <= set(info.get("features", [])),
        "server_info.features power ve message içerir")
    await drain(old, 0.5)
    await drain(lin, 0.2)
    # PR #108'den önce sunucu X-Agent-Platform'u yazmaz: test platformu kendisi yazar (sonra da aynı değer)
    await c.execute("UPDATE clients SET platform = 'linux' WHERE pc_name = $1", LIN)
    chk(await c.fetchval("SELECT features FROM agent_versions WHERE pc_name=$1", NEW) == ["message", "power", "winget"],
        "yeni ajanın özellikleri saklandı")

    async def result(ws, pc, task_id, exit_code, output):
        await ws.send(json.dumps({"type": "result", "pc_name": pc, "task_id": task_id, "output": output,
                                  "exit_code": exit_code}))
        ack, _ = await wait_msg(ws, lambda m: m.get("action") == "result_ack" and m.get("task_id") == task_id)
        return ack is not None

    async def status(task_id, expected):
        ok = await wait_for(c, "SELECT status = $2 FROM tasks WHERE id=$1", task_id, expected)
        return bool(ok), await c.fetchval("SELECT status FROM tasks WHERE id=$1", task_id)

    print("== kapat: yeni, eski Windows, Linux, kapalı")
    note = "Ders bitti; kaydedin. %PATH% & \"x\"\t‮satır\nsonu " + RUN
    s, b = pw(admin, [NEW, OLD, LIN, OFF], "shutdown", 60, note)
    chk(s == 200 and b.get("created") == 3 and b.get("skipped_offline") == [OFF], "kapalıya görev açılmadı (%s)" % b)
    chk(b.get("native") == [NEW] and sorted(b.get("fallback", [])) == [OLD, LIN] and b.get("unsupported") == [],
        "yanıt: yeni / eski komut / desteklenmeyen")
    rows = {r["target_pc"]: dict(r)
            for r in await c.fetch("SELECT * FROM tasks WHERE id = ANY($1::int[])", b.get("task_ids", []))}
    clean_note = "Ders bitti; kaydedin. %PATH% & \"x\" satır sonu " + RUN
    t = rows.get(NEW, {})
    chk(t.get("kind") == "power" and json.loads(t.get("payload") or "{}") == {
        "op": "shutdown", "delay": 60, "message": clean_note}, "görev kaydı: tür ve temizlenmiş not")
    chk(t.get("title") == "Kapat" and t.get("script_path") == "power shutdown delay=60" and t.get("expires_at"),
        "başlık, okunur özet, geçerlilik süresi")
    m, seen = await wait_msg(new, lambda m: m.get("task_id") == t.get("id") and "action" in m)
    chk(m == {"action": "power", "task_id": t.get("id"), "op": "shutdown", "delay": 60, "message": clean_note,
              "requested_by": "pwadmin"}, "yeni ajana power gitti: %s" % m)
    m, _ = await wait_msg(old, lambda m: m.get("task_id") == rows[OLD]["id"])
    expected_old = 'shutdown /s /f /t 60 /c "Ders bitti; kaydedin. PATH x satır sonu %s"' % RUN
    chk(m is not None and m.get("action") == "execute" and m.get("script_path") == expected_old,
        "eski Windows ajanına execute, tırnaklanabilen notla: %s" % (m or {}).get("script_path"))
    m, _ = await wait_msg(lin, lambda m: m.get("task_id") == rows[LIN]["id"])
    chk(m is not None and m.get("action") == "execute" and m.get("script_path") == "shutdown /s /f /t 60"
        and LINUX_POWER.match(m.get("script_path", "")),
        "Linux ajanına notsuz kalıp: %s" % (m or {}).get("script_path"))
    chk(await result(new, NEW, t["id"], 0, "[TAMAM] 60 saniye sonra kapanıyor"), "sonuç onaylandı")
    chk((await status(t["id"], "Completed"))[0], "yeni ajan: Completed")
    chk(await result(old, OLD, rows[OLD]["id"], 0, ""), "eski ajanın sonucu")
    chk((await status(rows[OLD]["id"], "Completed"))[0], "eski ajan: Completed")
    chk(await result(lin, LIN, rows[LIN]["id"], 0, "Bilgisayar 60 saniye sonra: systemctl poweroff"), "Linux sonucu")

    print("== kilit ve oturumu kapatma: eski ajanlarda karşılığı yok")
    s, b = pw(admin, [NEW, OLD, LIN], "lock")
    chk(s == 200 and b.get("native") == [NEW] and sorted(b.get("unsupported", [])) == [OLD, LIN], "yanıt: %s" % b)
    ids = await task_ids(c, b)
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == ids.get(NEW))
    chk(m == {"action": "power", "task_id": ids.get(NEW), "op": "lock", "delay": 0, "message": None,
              "requested_by": "pwadmin"}, "yeni ajana power lock")
    for pc, ws in ((OLD, old), (LIN, lin)):
        chk((await status(ids[pc], "Denied"))[0], "%s: Denied" % pc)
        row = await c.fetchrow("SELECT exit_code, output FROM tasks WHERE id=$1", ids[pc])
        chk(row["exit_code"] == -8 and row["output"] == power.UNSUPPORTED_OUTPUT, "%s: -8 ve açıklama" % pc)
    for pc, ws in ((OLD, old), (LIN, lin)):
        seen = await drain(ws, 0.8)
        chk(not [x for x in seen if x.get("task_id") == ids[pc]], "%s: hiçbir şey gönderilmedi" % pc)
    chk(await result(new, NEW, ids[NEW], -6, "[REDDEDİLDİ] oturum açık kullanıcı yok"), "-6 sonucu onaylandı")
    ok, st = await status(ids[NEW], "Denied")
    chk(ok and await c.fetchval("SELECT exit_code FROM tasks WHERE id=$1", ids[NEW]) == -6,
        "-6 [REDDEDİLDİ] -> Denied (%s)" % st)

    print("== yeniden başlat: eski ajanda en az 5 sn")
    s, b = pw(admin, [OLD], "restart", 0, "")
    tid = (b.get("task_ids") or [None])[0]
    m, _ = await wait_msg(old, lambda m: m.get("task_id") == tid)
    chk(m is not None and m.get("script_path") == "shutdown /r /f /t 5", "execute: shutdown /r /f /t 5")
    await result(old, OLD, tid, 0, "")
    s, b = pw(admin, [LAB], "logoff", 30, mode="LAB")
    chk(s == 200 and b.get("created") == 3 and b.get("skipped_offline") == [OFF], "sınıf hedefi: çevrimiçi üç cihaz")
    tid = [r["id"] for r in await c.fetch("SELECT id FROM tasks WHERE id = ANY($1::int[]) AND target_pc=$2",
                                          b.get("task_ids", []), NEW)][0]
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == tid)
    chk(m is not None and m.get("op") == "logoff" and m.get("delay") == 30, "LAB: yeni ajana logoff")
    await result(new, NEW, tid, 0, "[TAMAM] oturum kapatılıyor")

    print("== kullanıcıya mesaj")
    s, b = msg(admin, [NEW, OLD, LIN], "Sınav\u0000 başlıyor\n", LONG_TEXT + "\r\n\r\n\r\n\u0007Son satır", "warning",
               True)
    chk(s == 200 and b.get("native") == [NEW] and sorted(b.get("unsupported", [])) == [OLD, LIN], "yanıt: %s" % b)
    ids = await task_ids(c, b)
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == ids.get(NEW))
    chk(m == {"action": "user_message", "task_id": ids.get(NEW), "title": "Sınav başlıyor",
              "text": LONG_TEXT + "\n\nSon satır", "style": "warning", "requires_ack": True,
              "requested_by": "pwadmin"}, "yeni ajana user_message (temizlenmiş): %s" % m)
    for pc in (OLD, LIN):
        chk((await status(ids[pc], "Denied"))[0]
            and await c.fetchval("SELECT exit_code FROM tasks WHERE id=$1", ids[pc]) == -8, "%s: mesaj -8" % pc)
    chk(await c.fetchval("SELECT script_path FROM tasks WHERE id=$1", ids[NEW]) == "user_message warning ack",
        "script_path mesaj metnini taşımaz")
    # Onay beklerken ("Running") cihazın sıradaki görevi bekletilmez
    s, b = req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": [NEW], "taskSequence": [
        {"name": "komut", "type": "CMD", "command": "echo pw-" + RUN}]})
    cmd_id = (b.get("task_ids") or [None])[0]
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == cmd_id)
    chk(m is not None and m.get("action") == "execute", "okundu onayı beklenirken komut da gönderildi")
    # Kota (1) başka bir cihazdaki komutla dolu: güç komutu yine gider (aynı cihazda süren komut bitince gider)
    await result(new, NEW, cmd_id, 0, "pw")
    s, b = req("/api/deploy_orchestration", admin, {"target_mode": "PC", "targets": [OLD], "taskSequence": [
        {"name": "komut", "type": "CMD", "command": "echo pw-kota-" + RUN}]})
    busy_id = (b.get("task_ids") or [None])[0]
    m, _ = await wait_msg(old, lambda m: m.get("task_id") == busy_id)
    chk(m is not None, "eski ajandaki komut kotayı doldurdu")
    s, b = pw(admin, [NEW], "lock", 0, "kota " + RUN)
    lock_id = (b.get("task_ids") or [None])[0]
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == lock_id)
    chk(m is not None and m.get("action") == "power", "dolu kotada güç komutu gönderildi")
    await result(old, OLD, busy_id, 0, "pw")
    await result(new, NEW, lock_id, 0, "[TAMAM] kilitlendi")
    chk(await result(new, NEW, ids[NEW], 0, "[TAMAM] okundu"), "okundu sonucu onaylandı")
    chk((await status(ids[NEW], "Completed"))[0], "mesaj: Completed")

    print("== yerel yetenek kapalı (-5 + capability_denied)")
    s, b = msg(admin, [NEW], "Bilgi", "kısa", "info", False)
    tid = (b.get("task_ids") or [None])[0]
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == tid)
    chk(m is not None and m.get("requires_ack") is False and m.get("style") == "info", "bilgi mesajı gitti")
    await new.send(json.dumps({"type": "capability_denied", "capability": "message", "action": "user_message",
                               "task_id": tid}))
    await result(new, NEW, tid, -5, "[REDDEDİLDİ] Bu cihazda kullanıcıya mesaj kapalı (yetenek politikası).")
    ok, st = await status(tid, "Denied")
    chk(ok and await c.fetchval("SELECT exit_code FROM tasks WHERE id=$1", tid) == -5, "-5 -> Denied (%s)" % st)

    print("== API jetonu, /api/v1")
    s, b = req("/api/tokens", superadmin, {"name": "pw-token-" + RUN, "role": "admin"})
    api = b.get("token")
    chk(s == 200 and api, "admin API jetonu")
    s, b = pw(api, [NEW], "lock", path="/api/v1/devices/power")
    chk(s == 200 and b.get("created") == 1, "API jetonu güç komutu verir (/api/v1)")
    tid = (b.get("task_ids") or [None])[0]
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == tid)
    chk(m is not None and m.get("requested_by") == "token:pw-token-" + RUN, "isteyen: jeton")
    await result(new, NEW, tid, 0, "[TAMAM] kilitlendi")
    s, b = msg(api, [NEW], "Jeton", "jetondan mesaj")
    chk(s == 403, "API jetonu mesaj yazamaz (%s)" % s)
    s, b = msg(admin, [NEW], "v1", "v1 mesajı", path="/api/v1/devices/message")
    chk(s == 200 and b.get("created") == 1, "/api/v1/devices/message (oturumla)")
    tid = (b.get("task_ids") or [None])[0]
    await wait_msg(new, lambda m: m.get("task_id") == tid)
    await result(new, NEW, tid, 0, "[TAMAM] gösterildi")

    print("== uzak komut modülü kapalı")
    s, _ = req("/api/modules/terminal", superadmin, {"enabled": False, "lab": LAB})
    chk(s == 200, "laboratuvarda uzak komut kapatıldı")
    s, b = pw(admin, [NEW, OLD], "restart", 120)
    ids = await task_ids(c, b)
    m, _ = await wait_msg(new, lambda m: m.get("task_id") == ids.get(NEW))
    chk(m is not None and m.get("action") == "power", "yeni ajana güç komutu gider (komut değil)")
    ok, st = await status(ids[OLD], "Denied")
    chk(ok and "MODÜL KAPALI" in (await c.fetchval("SELECT output FROM tasks WHERE id=$1", ids[OLD]) or ""),
        "eski ajanın execute karşılığı reddedildi (%s)" % st)
    seen = await drain(old, 0.6)
    chk(not [x for x in seen if x.get("task_id") == ids[OLD]], "eski ajana gönderilmedi")
    await result(new, NEW, ids[NEW], 0, "[TAMAM]")
    req("/api/modules/terminal", superadmin, {"enabled": None, "lab": LAB})

    print("== denetim kaydı")
    audit = await c.fetch("SELECT action, changes FROM device_audit_logs WHERE action = ANY($1::text[]) "
                          "AND changes LIKE $2", ["power_command", "user_message"], "%" + "pwadmin" + "%")
    actions = {r["action"] for r in audit}
    chk({"power_command", "user_message"} <= actions, "power_command ve user_message kayıtları var")
    req_rows = [json.loads(r["changes"]) for r in audit if '"requested_by"' in r["changes"]]
    first = next((r for r in req_rows if r.get("op") == "shutdown" and r.get("delay") == 60), None)
    chk(first is not None and first.get("targets") == 3 and first.get("requested_by") == "pwadmin"
        and first.get("note", {}).get("length") == len(clean_note), "istek kaydı: op, gecikme, hedef sayısı, isteyen")
    full = await c.fetchval("SELECT count(*) FROM device_audit_logs WHERE changes LIKE $1", "%" + SECRET_TAIL + "%")
    logs = await c.fetchval("SELECT count(*) FROM %s WHERE meta_data::text LIKE $1 OR message LIKE $1" % LOG_TABLE,
                            "%" + SECRET_TAIL + "%")
    chk(full == 0 and logs == 0, "mesaj metninin tamamı hiçbir denetim kaydında yok (%s, %s)" % (full, logs))
    s, act = req("/api/devices/%s/activity?limit=20" % NEW, admin)
    kinds = {i.get("task_kind") for i in act.get("items", [])}
    chk(s == 200 and {"power", "user_message"} <= kinds, "cihaz geçmişinde görev türleri")
    for ws in (new, old, lin):
        await ws.close()


if __name__ == "__main__":
    asyncio.run(main())
