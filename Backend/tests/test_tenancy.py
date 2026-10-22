"""Kurum birimleri (ilçe → okul) ve kapsamlı yetki — entegrasyon testi.

Bir ilçenin altında iki okul (A, B), her okulda bir sınıf ve bilgisayar; ayrıca birime bağlı olmayan bir sınıf ve
atanmamış bir cihaz. Denetlenen:
  * Süper admin birimleri, sınıf bağlarını ve kapsamları yönetir; admin ve jeton yönetemez. Süper admine kapsam
    verilemez, bilinmeyen birim reddedilir, birim kendi altına taşınamaz, alt birimi olan birim silinemez.
  * A okuluna sınırlı admin (ve aynı kapsamlı API jetonu ile izleyici) B'nin cihazını listede görmez ve doğrudan
    kimlikle de ulaşamaz (404): cihaz, sınıf, görev, kayıt, envanter, yazılım, yama, rapor ve CSV, lisans, talep,
    zamanlanmış görev, bildirim, uzak ekran, karantina, bypass kodu, dağıtım, uyandırma, silme ve taşıma. Kurum
    geneli ayarlar ona 403.
  * İlçe kapsamlı admin iki okulu da görür, atanmamışları görmez. Kapsamsız admin bugünkü gibi her şeyi görür.
  * Sonradan gelen özellikler: dosya aktarımı (gönderme, alma, liste, indirme), sınav modu (başlatma, bitirme,
    durum, geçmiş), winget dağıtımı (katalog ortak), Vision oturumu.
  * Güç komutu ve kullanıcıya mesaj: hedefler kapsamda çözülür (B'nin sınıfı 404, cihazı 422, ALL yalnızca A).
    Güncelleme ilerlemesi (eş önbelleği özeti) yalnızca kapsamdaki cihazlar. GLPI ve modül önizlemesi süper admine.
  * Dizin/OIDC: grup eşlemesi kapsam verebilir; kapsamı ayarlanmamış YENİ hesap en dar kapsamı alır (birim varken
    boş kapsam: hiçbir cihaz), "all" açıkça kapsamsız; var olan hesabın elle verilen kapsamı korunur, ayarlanan
    kapsam her girişte yazılır; silinmiş birim kapsamı genişletmez. Bilinmeyen birimli eşleme kaydedilemez.
  * Cihaz listesi sürümü: kapsamlı hesabın ETag'i yalnızca kendi satırlarından (B'nin değişikliği 304'ü bozmaz).
  * Panel soketi: B'nin cihazının yayını ve devices_changed bildirimi A'ya sınırlı panele gitmez, ilçe paneline
    gider; A'nın kendi değişikliği A'ya sürümsüz bildirilir.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET (sunucuyla aynı).
"""

import asyncio
import csv
import io
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import websockets  # noqa: E402

import server  # noqa: E402  (create_jwt)
from pops import db as pops_db  # noqa: E402
from pops import filestore, sso  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
WS = os.environ.get("POPS_TEST_WS", HTTP.replace("http", "ws", 1))
PA, PB, PU, PF = "HW-TNA1", "HW-TNB1", "HW-TNU1", "HW-TNF1"   # okul A, okul B, atanmamış, birimsiz sınıf
PCS = [PA, PB, PU, PF]
LA, LB, LF = "TN-A-Lab", "TN-B-Lab", "TN-Serbest"
LABS = [LA, LB, LF, "TN-A-Yeni", "TN-A-Yeni2"]
USERS = ("tnsuper", "tnadmin", "tnadminA", "tnadminD", "tnviewerA", "tnbad")
PASS = "Tn-Parola-12345"
BLOB_A, BLOB_B = "pull-tntenancyA.bin", "pull-tntenancyB.bin"
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, body=None, method=None, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            payload = resp.read()
            if raw:
                return resp.status, payload.decode("utf-8-sig")
            return resp.status, json.loads(payload or b"null")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"null")
        except ValueError:
            return e.code, None


def q(name):
    return urllib.parse.quote(name, safe="")


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    await c.execute("DELETE FROM api_tokens WHERE name LIKE 'tn-%'")
    await c.execute("DELETE FROM users WHERE username = ANY($1::text[]) OR username LIKE 'tnsso-%'", list(USERS))
    for table, col in (("tasks", "target_pc"), ("agent_logs_v2", "pc_name"), ("hw_inventory", "pc_name"),
                       ("device_software", "pc_name"), ("device_patch_status", "pc_name"),
                       ("notifications", "pc_name"), ("agent_versions", "pc_name"), ("clients", "pc_name"),
                       ("enterprise_audit_logs", "target_pc")):
        await c.execute("DELETE FROM %s WHERE %s = ANY($1::text[])" % (table, col), PCS)
    await c.execute("DELETE FROM ticket_messages WHERE ticket_id IN (SELECT id FROM tickets WHERE subject LIKE 'tn-%')")
    await c.execute("DELETE FROM tickets WHERE subject LIKE 'tn-%'")
    await c.execute("DELETE FROM licenses WHERE name LIKE 'tn-%'")
    await c.execute("DELETE FROM scheduled_tasks WHERE name LIKE 'tn-%'")
    await c.execute("DELETE FROM lab_settings WHERE lab_name = ANY($1::text[])", LABS)
    await c.execute("DELETE FROM custom_labs WHERE lab_name = ANY($1::text[])", LABS)
    await c.execute("DELETE FROM file_transfers WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM exam_devices WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM exam_sessions WHERE lab_name = ANY($1::text[])", LABS)
    for blob in (BLOB_A, BLOB_B):
        try:
            os.unlink(filestore.blob_path(blob))
        except OSError:
            pass
    await c.execute("DELETE FROM org_units WHERE parent_id IN (SELECT id FROM org_units WHERE name LIKE 'TN %')")
    await c.execute("DELETE FROM org_units WHERE name LIKE 'TN %'")


async def seed(c):
    await c.execute(
        "INSERT INTO clients (pc_name, hostname, lab_name, status, last_seen) VALUES "
        "($1, 'tn-a1', $5, 'Offline', now()), ($2, 'tn-b1', $6, 'Offline', now()), "
        "($3, 'tn-u1', 'Atanmamis_Cihazlar', 'Offline', now()), ($4, 'tn-f1', $7, 'Offline', now())",
        PA, PB, PU, PF, LA, LB, LF)
    await c.execute("INSERT INTO custom_labs (lab_name) VALUES ($1), ($2), ($3)", LA, LB, LF)
    for pc in PCS:
        await c.execute("INSERT INTO hw_inventory (pc_name, hostname, mac_address, last_updated) "
                        "VALUES ($1, $2, '00:11:22:33:44:55', now())", pc, pc.lower())
        await c.execute("INSERT INTO agent_logs_v2 (pc_name, event_type, risk_level, message, \"timestamp\") "
                        "VALUES ($1, 'policy.alert', 'high', $2, now())", pc, "tn-log-" + pc)
        await c.execute("INSERT INTO device_software (pc_name, name, version) VALUES ($1, 'TN Ofis', '1')", pc)
        await c.execute("INSERT INTO device_patch_status (pc_name, pending_count) VALUES ($1, 1)", pc)
        await c.execute("INSERT INTO notifications (event, severity, pc_name, title) VALUES ('test', 'high', $1, $2)",
                        pc, "tn-bildirim-" + pc)
    await c.execute("INSERT INTO notifications (event, severity, title) VALUES ('test', 'high', 'tn-bildirim-genel')")
    tasks = {}
    for pc, lab in ((PA, LA), (PB, LB), (PU, "Atanmamis_Cihazlar")):
        tasks[pc] = await c.fetchval(
            "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by) "
            "VALUES ($1, $2, 'echo tn', 'Pending', now(), 'tn') RETURNING id", pc, lab)
    tickets = {}
    for pc in (PA, PB):
        tickets[pc] = await c.fetchval(
            "INSERT INTO tickets (source, pc_name, reporter, subject) VALUES ('agent', $1, 'ogr', $2) RETURNING id",
            pc, "tn-talep-" + pc)
    return tasks, tickets


def names(rows, key="hw_id"):
    """Bu testin cihazları (her biri bir kez), sıralı."""
    if not isinstance(rows, list):
        return rows
    return sorted({r[key] for r in rows if str(r.get(key, "")).startswith("HW-TN")})


async def main():
    c = await conn()
    await cleanup(c)
    await c.execute("INSERT INTO global_settings (key, value) VALUES ('enforce_agent_auth', '0') "
                    "ON CONFLICT (key) DO UPDATE SET value = '0'")
    await c.execute("INSERT INTO users (username, password_hash, role, permissions, token_version) "
                    "VALUES ('tnsuper', 'x', 'superadmin', '[]', 0), ('tnadmin', 'x', 'admin', '[]', 0)")
    sup = server.create_jwt("tnsuper", "superadmin", 0)
    glob = server.create_jwt("tnadmin", "admin", 0)
    try:
        tasks, tickets = await seed(c)
        units = await test_units(c, sup, glob)
        tokens = await test_users_and_tokens(c, sup, units)
        await test_school_a(c, tokens, units, tasks, tickets, sup)
        await test_district_and_global(c, tokens, glob, units)
        await test_features(c, tokens, sup)
        await test_device_list(c, tokens, sup)
        await test_panel_socket(tokens)
        await test_devices_changed(tokens, sup, glob)
        await test_power_and_progress(tokens, sup, glob)
        await test_sso_scope(c, tokens, sup, units)
        await test_unit_delete(c, sup, units)
    finally:
        await cleanup(c)
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM KURUM BIRIMI TESTLERI GECTI")


async def test_units(c, sup, glob):
    print("== birimler ve sınıf bağları (süper admin)")
    s, d = req("/api/org-units", sup, {"name": "TN İlçe"})
    chk(s == 200 and d.get("id"), "ilçe eklendi (%s %s)" % (s, d))
    district = d.get("id")
    s, a = req("/api/org-units", sup, {"name": "TN Okul A", "parent_id": district})
    s2, b = req("/api/org-units", sup, {"name": "TN Okul B", "parent_id": district})
    chk(s == 200 and s2 == 200, "iki okul ilçenin altında")
    ua, ub = a.get("id"), b.get("id")
    chk(req("/api/org-units", sup, {"name": "tn okul a", "parent_id": district})[0] == 409,
        "aynı üst birimde aynı ad (büyük/küçük harf duyarsız) 409")
    chk(req("/api/org-units", glob, {"name": "TN Yetkisiz"})[0] == 403, "admin birim ekleyemez")
    chk(req("/api/org-units/%d" % district, sup, {"parent_id": ua}, method="PATCH")[0] == 400,
        "birim kendi altına taşınamaz")
    chk(req("/api/org-units", sup, {"name": "TN Yok", "parent_id": 999999})[0] == 404, "olmayan üst birim 404")
    chk(req("/api/org-units/%d/labs" % ua, sup, {"labs": [LA]}, method="PUT")[0] == 200, "A'ya sınıf bağlandı")
    chk(req("/api/org-units/%d/labs" % ub, sup, {"labs": [LB]}, method="PUT")[0] == 200, "B'ye sınıf bağlandı")
    chk(req("/api/org-units/%d/labs" % ua, sup, {"labs": ["Atanmamis_Cihazlar"]}, method="PUT")[0] == 400,
        "atanmamışlar birime bağlanamaz")
    chk(req("/api/org-units/%d/labs" % ua, sup, {"labs": ["TN-Olmayan"]}, method="PUT")[0] == 404,
        "olmayan sınıf 404")
    chk(req("/api/org-units/%d/labs" % ua, glob, {"labs": [LA]}, method="PUT")[0] == 403, "admin bağlayamaz")
    s, tree = req("/api/org-units", sup)
    mine = {u["name"]: u for u in tree.get("units", []) if u["name"].startswith("TN ")} if s == 200 else {}
    chk(mine.get("TN Okul A", {}).get("labs") == [LA] and mine.get("TN Okul B", {}).get("parent_id") == district
        and tree.get("org_scope") is None, "ağaç: sınıflar ve üst birim, süper admin kapsamsız")
    return {"district": district, "a": ua, "b": ub}


async def test_users_and_tokens(c, sup, units):
    print("== kullanıcı ve jeton kapsamı")
    ua, district = units["a"], units["district"]
    bad = {"username": "tnbad", "password": PASS, "role": "admin", "permissions": "[]"}
    chk(req("/api/admin/users", sup, dict(bad, role="superadmin", org_scope=[ua]))[0] == 400,
        "süper admine kapsam verilemez")
    chk(req("/api/admin/users", sup, dict(bad, org_scope=[999999]))[0] == 400, "bilinmeyen birim 400")
    chk(req("/api/admin/users", sup, dict(bad, org_scope=[]))[0] == 400, "boş kapsam 400")
    for user, role, scope in (("tnadminA", "admin", [ua]), ("tnadminD", "admin", [district]),
                              ("tnviewerA", "viewer", [ua])):
        s, _ = req("/api/admin/users", sup, {"username": user, "password": PASS, "role": role,
                                             "permissions": '["devices"]', "org_scope": scope})
        chk(s == 200, "%s oluşturuldu" % user)
    uid = await c.fetchval("SELECT id FROM users WHERE username = 'tnadminA'")
    # Şifre sıfırlama gibi kapsamsız güncelleme kapsamı silmez
    reset = {"username": "tnadminA", "role": "admin", "permissions": '["devices"]', "password": PASS}
    chk(req("/api/admin/users/%d" % uid, sup, reset, method="PUT")[0] == 200
        and await c.fetchval("SELECT org_scope FROM users WHERE id = $1", uid) == [ua],
        "kapsam gönderilmeyen güncellemede kapsam korunur")
    s, login = req("/api/admin/login", None, {"username": "tnadminA", "password": PASS})
    chk(s == 200 and login.get("org_units") == [{"id": ua, "name": "TN Okul A"}] and login.get("org_scope") == [ua],
        "giriş yanıtında birim adı (%s)" % {k: login.get(k) for k in ("org_scope", "org_units")})
    tv = {r["username"]: r["token_version"] for r in await c.fetch(
        "SELECT username, token_version FROM users WHERE username LIKE 'tn%'")}
    out = {
        "A": server.create_jwt("tnadminA", "admin", tv["tnadminA"]),
        "D": server.create_jwt("tnadminD", "admin", tv["tnadminD"]),
        "VA": server.create_jwt("tnviewerA", "viewer", tv["tnviewerA"]),
    }
    s, users = req("/api/admin/users", out["A"])
    seen = sorted(u["username"] for u in users.get("users", []) if u["username"].startswith("tn")) if s == 200 else []
    chk(seen == ["tnadminA", "tnviewerA"], "kapsamlı admin yalnızca kendini ve kendi birimindekileri görür (%s)" % seen)
    s, tok = req("/api/tokens", sup, {"name": "tn-a", "role": "admin", "org_scope": [ua]})
    chk(s == 200 and tok.get("org_scope") == [ua] and tok.get("org_units") == [{"id": ua, "name": "TN Okul A"}],
        "kapsamlı API jetonu (%s)" % {k: tok.get(k) for k in ("org_scope", "org_units")})
    out["TA"] = tok.get("token")
    chk(req("/api/tokens", sup, {"name": "tn-bad", "role": "admin", "org_scope": [999999]})[0] == 400,
        "jetonda bilinmeyen birim 400")
    chk(req("/api/org-units", out["TA"], {"name": "TN Jeton"})[0] == 403, "jeton birim ekleyemez")
    return out


async def test_school_a(c, tok, units, tasks, tickets, sup):
    ua, ub = units["a"], units["b"]
    for who in ("A", "TA"):
        t = tok[who]
        label = "jeton" if who == "TA" else "admin"
        print("== okul A'ya sınırlı %s: listeler" % label)
        s, devs = req("/api/devices", t)
        chk(s == 200 and names(devs) == [PA], "cihazlar yalnızca A (%s)" % names(devs))
        s, labs = req("/api/custom_labs", t)
        chk(s == 200 and [x for x in labs if x.startswith("TN-")] == [LA], "sınıflar yalnızca A (%s)" % labs)
        s, labs = req("/api/v1/labs", t)
        chk(s == 200 and [x for x in labs if x.startswith("TN-")] == [LA], "/api/v1/labs yalnızca A")
        s, inv = req("/api/inventory", t)
        chk(s == 200 and names(inv, "pc_name") == [PA], "envanter yalnızca A")
        s, logs = req("/api/logs?limit=5000", t)
        chk(s == 200 and names(logs, "pc_name") == [PA], "kayıtlar yalnızca A")
        s, logs = req("/api/logs?pc=%s" % PB, t)
        chk(s == 200 and logs == [], "B'nin kayıtları cihaz süzgeciyle de gelmez")
        s, tl = req("/api/tasks", t)
        chk(s == 200 and names(tl, "target_pc") == [PA], "görevler yalnızca A")
        s, st = req("/api/tasks/status", t, {"ids": [tasks[PA], tasks[PB]]})
        chk(s == 200 and [i["id"] for i in st.get("items", [])] == [tasks[PA]], "görev durumu yalnızca A")
        s, sw = req("/api/software?q=TN%20Ofis", t)
        item = next((i for i in sw.get("items", []) if i["name"] == "TN Ofis"), {}) if s == 200 else {}
        chk(item.get("devices") == 1, "yazılım envanteri yalnızca A'yı sayar (%s)" % item)
        s, sd = req("/api/software/devices?name=TN%20Ofis", t)
        chk(s == 200 and names(sd, "pc_name") == [PA], "yazılımın cihazları yalnızca A")
        s, pt = req("/api/patches", t)
        chk(s == 200 and names(pt, "pc_name") == [PA], "yama durumu yalnızca A")
        s, rep = req("/api/reports/summary?days=7", t)
        chk(s == 200 and rep["devices"]["total"] == 1 and [x["lab"] for x in rep["devices"]["labs"]] == [LA],
            "rapor yalnızca A (%s)" % (rep or {}).get("devices"))
        for kind, col in (("devices", 0), ("software", 0), ("patches", 0), ("events", 1)):
            s, text = req("/api/reports/export?kind=%s&days=7" % kind, t, raw=True)
            rows = list(csv.reader(io.StringIO(text), delimiter=";"))[1:] if s == 200 else []
            got = sorted({r[col] for r in rows if len(r) > col and r[col].startswith("HW-TN")})
            chk(got == [PA], "CSV %s yalnızca A (%s)" % (kind, got))
        s, ls = req("/api/lab_settings", t)
        chk(s == 200 and not any(k in ls for k in (LB, LF)), "oturma planları yalnızca A")
        s, mods = req("/api/modules", t)
        chk(s == 200 and [x for x in mods.get("labs", []) if x.startswith("TN-")] == [LA], "modül sınıfları yalnızca A")
        s, ver = req("/api/system/version", t)
        chk(s == 200 and ver.get("agents_total") == 1, "sürüm ekranının cihaz sayısı yalnızca A")
        s, up = req("/api/system/update-progress", t, {"pcs": [PA, PB], "version": "0.1.0"})
        chk(s == 200 and [i["pc"] for i in up.get("items", [])] == [PA], "güncelleme ilerlemesi yalnızca A")
        s, tree = req("/api/org-units", t)
        chk(s == 200 and [u["id"] for u in tree.get("units", [])] == [ua] and tree["units"][0]["parent_id"] is None
            and [x["lab_name"] for x in tree.get("labs", [])] == [LA], "birim ağacı yalnızca A")

        print("== okul A'ya sınırlı %s: B'ye doğrudan kimlikle" % label)
        chk(req("/api/devices/%s/activity" % PB, t)[0] == 404, "B cihaz geçmişi 404")
        chk(req("/api/devices/%s/software" % PB, t)[0] == 404, "B yazılım listesi 404")
        chk(req("/api/tasks/action", t, {"action": "CANCEL", "target_mode": "TASK",
                                         "target_id": str(tasks[PB])})[0] == 404, "B görevini iptal 404")
        chk(req("/api/tasks/action", t, {"action": "CANCEL", "target_mode": "PC", "target_id": PB})[0] == 404,
            "B cihazının görevleri 404")
        chk(req("/api/tasks/action", t, {"action": "PAUSE", "target_mode": "LAB", "target_id": LB})[0] == 404,
            "B sınıfının görevleri 404")
        s, d = req("/api/tasks/action", t, {"action": "PAUSE", "target_mode": "ALL", "target_id": ""})
        chk(s == 200 and await c.fetchval("SELECT status FROM tasks WHERE id = $1", tasks[PB]) == "Pending"
            and await c.fetchval("SELECT status FROM tasks WHERE id = $1", tasks[PA]) == "Paused",
            "ALL yalnızca A'nın görevine uygulandı")
        await c.execute("UPDATE tasks SET status = 'Pending' WHERE id = $1", tasks[PA])
        body = {"target_mode": "PC", "targets": [PB], "task_sequence": [{"name": "x", "type": "CMD",
                                                                         "command": "echo %s" % who}]}
        chk(req("/api/deploy_orchestration", t, body)[0] == 422, "B'ye görev: kayıtlı olmayan bilgisayar (422)")
        chk(req("/api/v1/tasks", t, dict(body, target_mode="LAB", targets=[LB]))[0] == 404, "B sınıfına görev 404")
        s, d = req("/api/deploy_orchestration", t, dict(body, target_mode="ALL", targets=[]))
        made = await c.fetch("SELECT target_pc FROM tasks WHERE id = ANY($1::int[])", d.get("task_ids") or [])
        chk(s == 200 and [r["target_pc"] for r in made] == [PA], "ALL görevi yalnızca A'ya")
        if who == "A":
            chk(req("/api/audit/session/start", t, {"target_pc": PB, "reason": "x", "is_mandatory": False})[0] == 404,
                "B'ye uzak ekran oturumu 404")
            chk(req("/api/thumbnail/%s" % PB, t)[0] == 404, "B önizleme 404")
            chk(req("/api/remote_input", t, {"device": PB, "input_type": "mouse_move", "data": {"x": 1}})[0] == 404,
                "B uzaktan girdi 404")
            chk(req("/api/stream/stop", t, {"pc_name": PB})[0] == 404, "B akış durdurma 404")
        chk(req("/api/security/lockdown", t, {"target_pc": PB, "reason": "x"})[0] == 404, "B karantina 404")
        chk(req("/api/v1/devices/%s/quarantine" % PB, t, {"reason": "x"})[0] == 404, "B karantina (v1) 404")
        chk(req("/api/security/unlock", t, {"target_pc": PB, "reason": "x"})[0] == 404, "B karantina kaldırma 404")
        chk(req("/api/v1/devices/%s/bypass-code" % PB, t, {})[0] == 404, "B bypass kodu 404")
        chk(req("/api/wake_pc/%s" % PB, t, {})[0] == 404, "B uyandırma 404")
        chk(req("/api/v1/labs/%s/wake" % q(LB), t, {})[0] == 404, "B sınıfını uyandırma 404")
        chk(req("/api/v1/devices/%s" % PB, t, {"display_name": "x"}, method="PATCH")[0] == 404, "B ad değiştirme 404")
        chk(req("/api/v1/devices/move", t, {"pc_names": [PB], "new_lab": LA})[0] == 404, "B'yi taşıma 404")
        chk(req("/api/v1/devices/move", t, {"pc_names": [PA], "new_lab": LB})[0] == 404, "B sınıfına taşıma 404")
        chk(req("/api/devices/%s" % PB, t, method="DELETE")[0] == 404
            and await c.fetchval("SELECT count(*) FROM clients WHERE pc_name = $1", PB) == 1, "B silme 404")
        chk(req("/api/v1/labs/%s" % q(LB), t, {"new_name": "TN-X"}, method="PATCH")[0] == 404, "B sınıf adı 404")
        chk(req("/api/v1/labs/%s" % q(LB), t, method="DELETE")[0] == 404, "B sınıfını silme 404")
        chk(req("/api/v1/labs/%s/layout" % q(LB), t, {"layout_json": "{}"}, method="PUT")[0] == 404,
            "B oturma planı 404")
        chk(req("/api/v1/labs/%s/main-pc" % q(LB), t, {"pc_name": PB}, method="PUT")[0] == 404, "B ana bilgisayar 404")
        chk(req("/api/v1/labs", t, {"lab_name": LB})[0] == 409, "B'nin sınıf adıyla sınıf açılamaz (409)")
        chk(req("/api/patches/scan", t, {"target_mode": "PC", "targets": [PB], "scope": "security"})[0] == 404,
            "B'ye yama taraması 404")
        chk(req("/api/patches/install", t, {"target_mode": "LAB", "targets": [LB], "scope": "all"})[0] == 404,
            "B sınıfına yama kurulumu 404")
        for path, body, msg in (("/api/set_concurrent_limit", {"limit": 3}, "kuyruk sınırı"),
                                ("/api/set_auto_enroll", {"target_lab": LA, "expire_date": "2099-01-01"}, "oto-kayıt"),
                                ("/api/add_package", {"id": "tn", "name": "tn", "type": "script", "meta": "",
                                                      "command": "echo", "icon": "", "color": ""}, "paket"),
                                ("/api/agent_policies", {"fair_use_text": "", "dns_categories": [],
                                                         "auto_quarantine": False, "quarantine_threshold": 5},
                                 "ajan politikası")):
            chk(req(path, t, body)[0] == 403, "kurum geneli ayar 403: %s" % msg)

    print("== okul A: kendi cihazına aynı istekler geçer (karşılaştırma)")
    t = tok["A"]
    chk(req("/api/devices/%s/activity" % PA, t)[0] == 200, "A cihaz geçmişi 200")
    chk(req("/api/devices/%s/software" % PA, t)[0] == 200, "A yazılım listesi 200")
    chk(req("/api/thumbnail/%s" % PA, t)[0] == 200, "A önizleme 200 (çevrimdışı: görüntü yok)")
    chk(req("/api/v1/devices/%s" % PA, t, {"display_name": "tn"}, method="PATCH")[0] == 200, "A ad değiştirme 200")
    chk(req("/api/v1/labs/%s/layout" % q(LA), t, {"layout_json": "{}"}, method="PUT")[0] == 200, "A oturma planı 200")
    chk(req("/api/v1/labs/%s/main-pc" % q(LA), t, {"pc_name": PA}, method="PUT")[0] == 200, "A ana bilgisayar 200")
    chk(req("/api/patches/scan", t, {"target_mode": "PC", "targets": [PA], "scope": "security"})[0] == 200,
        "A'ya yama taraması 200")
    chk(req("/api/tasks/action", t, {"action": "PAUSE", "target_mode": "TASK",
                                     "target_id": str(tasks[PA])})[0] == 200, "A görevini duraklatma 200")
    chk(req("/api/tickets/%d" % tickets[PA], t)[0] == 200, "A talebi 200")

    print("== okul A: sınıf, lisans, talep, takvim, bildirim")
    chk(req("/api/v1/labs", t, {"lab_name": "TN-A-Yeni"})[0] == 200
        and await c.fetchval("SELECT org_unit_id FROM custom_labs WHERE lab_name = 'TN-A-Yeni'") == ua,
        "kapsamlı adminin açtığı sınıf kendi birimine bağlandı")
    s, labs = req("/api/custom_labs", t)
    chk("TN-A-Yeni" in (labs or []), "yeni sınıf hemen görünür")
    chk(req("/api/v1/labs", t, {"lab_name": "TN-A-Yeni2", "org_unit_id": ub})[0] == 404, "başka birime sınıf 404")
    lic = {"name": "tn-lisans-B", "match_pattern": "TN Ofis", "seats": 1, "org_unit_id": ub}
    s, lb = req("/api/licenses", sup, lic)
    s2, lg = req("/api/licenses", sup, dict(lic, name="tn-lisans-genel", org_unit_id=None))
    s3, la = req("/api/licenses", t, dict(lic, name="tn-lisans-A", org_unit_id=None))
    chk(s == 200 and s2 == 200 and s3 == 200
        and await c.fetchval("SELECT org_unit_id FROM licenses WHERE id = $1", la.get("id")) == ua,
        "kapsamlı adminin lisansı kendi biriminde")
    s, ll = req("/api/licenses", t)
    got = {i["name"]: i for i in ll.get("items", []) if i["name"].startswith("tn-")} if s == 200 else {}
    chk(sorted(got) == ["tn-lisans-A"] and got["tn-lisans-A"]["installed"] == 1,
        "lisanslar: yalnızca A'nınki, kurulum A'da sayılır (%s)" % {k: v.get("installed") for k, v in got.items()})
    s, sl = req("/api/licenses", sup)
    allg = {i["name"]: i["installed"] for i in sl.get("items", []) if i["name"].startswith("tn-")} if s == 200 else {}
    chk(allg.get("tn-lisans-B") == 1 and allg.get("tn-lisans-genel") == 4, "birim lisansı birimde, genel her yerde "
        "sayılır (%s)" % allg)
    chk(req("/api/licenses/%s/devices" % lb.get("id"), t)[0] == 404, "B lisansının cihazları 404")
    chk(req("/api/v1/licenses/%s" % lb.get("id"), t, dict(lic, org_unit_id=ua), method="PUT")[0] == 404,
        "B lisansını değiştirme 404")
    chk(req("/api/licenses/%s" % lb.get("id"), t, method="DELETE")[0] == 404, "B lisansını silme 404")
    s, d = req("/api/licenses/%s/devices" % la.get("id"), t)
    chk(s == 200 and names(d, "pc_name") == [PA], "A lisansının cihazları yalnızca A")

    s, tl = req("/api/tickets?status=all", t)
    mine = sorted(x["pc_name"] for x in tl.get("items", []) if (x.get("subject") or "").startswith("tn-")) \
        if s == 200 else []
    chk(mine == [PA], "talepler yalnızca A (%s)" % mine)
    chk(req("/api/tickets/%d" % tickets[PB], t)[0] == 404, "B talebi 404")
    chk(req("/api/v1/tickets/%d" % tickets[PB], t, {"status": "closed"}, method="PATCH")[0] == 404,
        "B talebini değiştirme 404")
    chk(req("/api/tickets/%d/messages" % tickets[PB], t, {"body": "x"})[0] == 404, "B talebine yanıt 404")
    chk(req("/api/tickets", t, {"subject": "tn-panel-B", "pc_name": PB})[0] == 404, "B cihazına talep 404")
    s, nt = req("/api/tickets", t, {"subject": "tn-panel-A"})
    chk(s == 200 and await c.fetchval("SELECT org_unit_id FROM tickets WHERE id = $1", nt.get("id")) == ua
        and req("/api/tickets/%d" % nt.get("id"), t)[0] == 200
        and req("/api/tickets/%d" % nt.get("id"), tok["D"])[0] == 200, "cihazsız talep A biriminde, ilçe de görür")

    sched = {"name": "tn-takvim", "command": "echo tn", "schedule_type": "daily", "time_of_day": "03:00"}
    chk(req("/api/scheduled_tasks", t, dict(sched, target_mode="PC", targets=[PB]))[0] == 404, "B'ye takvim 404")
    chk(req("/api/scheduled_tasks", t, dict(sched, target_mode="LAB", targets=[LB]))[0] == 404,
        "B sınıfına takvim 404")
    s, mine = req("/api/scheduled_tasks", t, dict(sched, target_mode="ALL", targets=[]))
    s2, glob_s = req("/api/scheduled_tasks", sup, dict(sched, name="tn-takvim-genel", target_mode="ALL", targets=[]))
    chk(s == 200 and mine.get("org_scope") == [ua] and s2 == 200 and glob_s.get("org_scope") is None,
        "takvim oluşturanın kapsamını taşır")
    s, lst = req("/api/scheduled_tasks", t)
    chk(s == 200 and [x["name"] for x in lst.get("items", []) if x["name"].startswith("tn-")] == ["tn-takvim"],
        "takvimler: kurum geneli A'ya görünmez")
    chk(req("/api/scheduled_tasks/%d/run" % glob_s["id"], t, {})[0] == 404, "kurum geneli takvimi çalıştırma 404")
    chk(req("/api/scheduled_tasks/%d" % glob_s["id"], t, method="DELETE")[0] == 404, "kurum geneli takvimi silme 404")
    s, run = req("/api/scheduled_tasks/%d/run" % mine["id"], sup, {})
    made = await c.fetch("SELECT target_pc FROM tasks WHERE schedule_id = $1", mine["id"])
    chk(s == 200 and [r["target_pc"] for r in made] == [PA], "A'nın takvimi süper admin çalıştırsa da yalnızca A'da")

    s, nl = req("/api/notifications?limit=200", t)
    got = sorted(x["title"] for x in nl.get("items", []) if x["title"].startswith("tn-")) if s == 200 else []
    chk(got == ["tn-bildirim-" + PA], "bildirimler yalnızca A (%s)" % got)
    req("/api/notifications/read", t, {"ids": []})
    chk(await c.fetchval("SELECT is_read FROM notifications WHERE title = $1", "tn-bildirim-" + PB) is False,
        "'tümü okundu' B'nin bildirimine dokunmaz")
    s, d = req("/api/notifications/clear", t, {"ids": [r["id"] for r in await c.fetch(
        "SELECT id FROM notifications WHERE title LIKE 'tn-bildirim-%'")]})
    chk(s == 200 and await c.fetchval("SELECT count(*) FROM notifications WHERE title LIKE 'tn-bildirim-%'") == 4,
        "B'nin ve genel bildirimler silinmez (%s)" % d)

    print("== okul A: izleyici")
    s, devs = req("/api/devices", tok["VA"])
    chk(s == 200 and names(devs) == [PA], "izleyici de yalnızca A'yı görür")
    chk(req("/api/devices/%s/activity" % PB, tok["VA"])[0] == 404, "izleyiciye B 404")


async def test_district_and_global(c, tok, glob, units):
    print("== ilçe kapsamlı admin ve kapsamsız admin")
    s, devs = req("/api/devices", tok["D"])
    chk(s == 200 and names(devs) == [PA, PB], "ilçe iki okulu görür, atanmamışı görmez (%s)" % names(devs))
    chk(req("/api/devices/%s/activity" % PB, tok["D"])[0] == 200, "ilçe B'ye ulaşır")
    s, labs = req("/api/custom_labs", tok["D"])
    chk(sorted(x for x in labs if x.startswith("TN-")) == [LA, "TN-A-Yeni", LB], "ilçe iki okulun sınıflarını görür")
    s, tree = req("/api/org-units", tok["D"])
    chk(s == 200 and sorted(u["id"] for u in tree["units"]) == sorted(units.values()), "ilçe bütün alt birimleri görür")
    s, devs = req("/api/devices", glob)
    chk(s == 200 and names(devs) == sorted(PCS), "kapsamsız admin her şeyi görür (bugünkü davranış)")
    s, labs = req("/api/custom_labs", glob)
    chk(LF in labs, "kapsamsız admin birimsiz sınıfı görür")
    chk(req("/api/devices/%s/activity" % PU, glob)[0] == 200, "kapsamsız admin atanmamış cihaza ulaşır")
    s, cur = req("/api/get_concurrent_limit", glob)
    chk(req("/api/set_concurrent_limit", glob, {"limit": cur.get("limit", 5)})[0] == 200,
        "kapsamsız admin kurum ayarını değiştirir")


def multipart(fields, files):
    boundary = "----tntest" + os.urandom(6).hex()
    out = bytearray()
    for k, v in fields:
        out += ("--%s\r\nContent-Disposition: form-data; name=\"%s\"\r\n\r\n%s\r\n" % (boundary, k, v)).encode()
    for k, fname, content in files:
        out += ("--%s\r\nContent-Disposition: form-data; name=\"%s\"; filename=\"%s\"\r\n"
                "Content-Type: application/octet-stream\r\n\r\n" % (boundary, k, fname)).encode() + content + b"\r\n"
    out += ("--%s--\r\n" % boundary).encode()
    return bytes(out), "multipart/form-data; boundary=" + boundary


def raw_req(path, token, data=None, method="GET", headers=None):
    """(durum, başlıklar (küçük harfli adlar), gövde baytları)."""
    r = urllib.request.Request(HTTP + path, data=data, method=method)
    r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            return resp.status, {k.lower(): v for k, v in resp.headers.items()}, resp.read()
    except urllib.error.HTTPError as e:
        return e.code, {k.lower(): v for k, v in e.headers.items()}, e.read()


async def test_features(c, tok, sup):
    """Sonradan gelen özellikler: dosya aktarımı, sınav modu, winget dağıtımı, Vision oturumu."""
    print("== okul A: dosya aktarımı, sınav modu, winget, Vision")
    a, d = tok["A"], tok["D"]
    body, ctype = multipart([("pcs", PB), ("dest", "inbox"), ("reason", "tn deneme")], [("file", "tn.txt", b"tn")])
    s, _h, raw = raw_req("/api/files/push", a, body, "POST", {"Content-Type": ctype})
    chk(s == 409 and "kayıtlı değil" in raw.decode("utf-8", "replace"),
        "B'ye dosya gönderme: bilgisayar kayıtlı değil (%s)" % s)
    s, out = req("/api/files/pull", a, {"pc": PB, "path": "C:\\Users\\Public\\Desktop\\a.txt", "max_size": 1024,
                                        "reason": "tn deneme"})
    chk(s == 409 and "kayıtlı değil" in str(out), "B'den dosya alma: kayıtlı değil (%s %s)" % (s, out))
    for pc, blob in ((PA, BLOB_A), (PB, BLOB_B)):
        with open(filestore.blob_path(blob), "wb") as f:
            f.write(b"tn-" + pc.encode())
        await c.execute(
            "INSERT INTO file_transfers (transfer_id, direction, pc_name, name, size, reason, status, storage_path) "
            "VALUES ($1, 'pull', $2, 'tn.txt', 9, 'tn', 'done', $3)", "tn" + pc.lower(), pc, blob)
    s, lst = req("/api/files", a)
    got = sorted({i["pc_name"] for i in lst.get("items", []) if i["pc_name"].startswith("HW-TN")}) if s == 200 else []
    chk(got == [PA], "aktarım listesi yalnızca A (%s)" % got)
    s, lst = req("/api/files?pc=%s" % PB, a)
    chk(s == 200 and lst.get("items") == [], "B'nin aktarımları ?pc= ile de gelmez")
    chk(raw_req("/api/files/tn%s/content" % PB.lower(), a)[0] == 404, "B'den alınan dosyayı indirme 404")
    chk(raw_req("/api/files/tn%s/content" % PA.lower(), a)[0] == 200, "A'nın dosyası iner")
    chk(raw_req("/api/files/tn%s/content" % PB.lower(), d)[0] == 200, "ilçe B'nin dosyasını indirir")

    exam = {"allow": ["sinav.meb.gov.tr"], "duration_minutes": 30, "reason": "tn yazılı"}
    chk(req("/api/labs/%s/exam" % q(LB), a, exam)[0] == 404, "B sınıfında sınav başlatma 404")
    chk(req("/api/labs/%s/exam" % q(LB), a)[0] == 404, "B sınıfının sınav durumu 404")
    s, _ = req("/api/labs/%s/exam" % q(LB), d, exam)
    chk(s == 200, "ilçe B'de sınav başlatır")
    chk(req("/api/labs/%s/exam" % q(LB), a, method="DELETE")[0] == 404, "B'nin sınavını bitirme 404")
    s, ex = req("/api/exams?active=true", a)
    chk(s == 200 and not [i for i in ex.get("items", []) if i.get("lab") == LB], "sınav listesi B'yi göstermez")
    s, ex = req("/api/exams?lab=%s" % q(LB), a)
    chk(s == 200 and ex.get("items") == [], "B süzgeciyle de boş")
    s, ex = req("/api/exams?active=true", d)
    chk(s == 200 and [i for i in ex.get("items", []) if i.get("lab") == LB], "ilçe B'nin sınavını görür")
    req("/api/labs/%s/exam" % q(LB), d, method="DELETE")

    step = {"target_mode": "PC", "targets": [PB],
            "task_sequence": [{"name": "w", "type": "WINGET", "winget": {"id": "Mozilla.Firefox"}}]}
    chk(req("/api/deploy_orchestration", a, step)[0] == 422, "B'ye winget: kayıtlı olmayan bilgisayar (422)")
    chk(req("/api/deploy_orchestration", a, dict(step, target_mode="LAB", targets=[LB]))[0] == 404,
        "B sınıfına winget 404")
    chk(req("/api/deploy/winget/catalog", a)[0] == 200, "winget kataloğu ortak (kurum geneli)")

    s, _ = req("/api/audit/session/start", a, {"target_pc": PA, "reason": "tn", "is_mandatory": False})
    chk(s == 200, "A'da Vision oturumu açılır")
    chk(req("/api/remote_input", a, {"device": PA, "input_type": "mouse_move", "data": {"x": 1}})[0] == 200,
        "oturumla uzaktan girdi gider (A)")


async def test_device_list(c, tok, sup):
    """Cihaz listesi sürümü: kapsamlı hesabın ETag'i yalnızca kendi satırlarından; başka okulun değişikliği 304'ü
    bozmaz, kendi değişikliği bozar. since her zaman tam liste."""
    print("== cihaz listesi: kapsamlı sürüm ve ETag")
    a = tok["A"]
    s, h, raw = raw_req("/api/devices?since=0", a)
    body = json.loads(raw or b"{}") if s == 200 else {}
    tag = h.get("etag") or ""
    got = sorted(d["hw_id"] for d in body.get("devices", []) if d["hw_id"].startswith("HW-TN"))
    chk(s == 200 and body.get("full") is True and got == [PA] and tag == 'W/"d%d"' % body.get("version"),
        "since: tam liste, yalnızca A, sürüm ETag'le aynı (%s %s)" % (got, tag))
    await asyncio.sleep(1.2)
    s, _ = req("/api/v1/devices/%s" % PB, sup, {"display_name": "tn-b-yeni"}, method="PATCH")
    chk(s == 200, "süper admin B'nin adını değiştirdi")
    s, h, _raw = raw_req("/api/devices?since=%d" % body.get("version", 0), a, headers={"If-None-Match": tag})
    chk(s == 304, "B'nin değişikliği A'nın ETag'ini bozmaz (304) (%s)" % s)
    req("/api/v1/devices/%s" % PA, sup, {"display_name": "tn-a-yeni"}, method="PATCH")
    s, h, raw = raw_req("/api/devices?since=%d" % body.get("version", 0), a, headers={"If-None-Match": tag})
    new = json.loads(raw or b"{}") if s == 200 else {}
    names_now = {d["hw_id"]: d.get("display_name") for d in new.get("devices", [])}
    chk(s == 200 and h.get("etag") != tag and names_now.get(PA) == "tn-a-yeni" and PB not in names_now,
        "A'nın değişikliği yeni sürüm ve satır getirir, B yine yok")


async def test_panel_socket(tok):
    print("== panel soketi: yayın kapsamı")
    cookie = lambda t: {"Cookie": "pops_jwt=%s" % t}  # noqa: E731
    pa = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(tok["A"]))
    pd = await websockets.connect(WS + "/ws/panel", additional_headers=cookie(tok["D"]))
    agent = await websockets.connect(WS + "/ws/agent/" + PB, additional_headers={"X-Agent-Version": "0.1.14-alpha"})
    try:
        await agent.send(json.dumps({"status": "Online", "hostname": "tn-b1"}))
        await asyncio.sleep(0.5)
        await agent.send(json.dumps({"type": "capabilities", "terminal_enabled": True, "vision_enabled": True}))

        async def got_caps(ws):
            end = asyncio.get_running_loop().time() + 3
            while asyncio.get_running_loop().time() < end:
                try:
                    msg = json.loads(await asyncio.wait_for(ws.recv(), 0.5))
                except (asyncio.TimeoutError, ValueError):
                    continue
                if msg.get("type") == "capabilities" and msg.get("pc_name") == PB:
                    return True
            return False

        d_got, a_got = await asyncio.gather(got_caps(pd), got_caps(pa))
        chk(d_got, "B cihazının yayını ilçe paneline gitti")
        chk(not a_got, "B cihazının yayını A'ya sınırlı panele gitmedi")
    finally:
        for ws in (agent, pa, pd):
            await ws.close()


async def test_devices_changed(tok, sup, glob):
    print("== panel soketi: devices_changed kapsamı")
    cookie = lambda t: {"Cookie": "pops_jwt=%s" % t}  # noqa: E731
    pa = await websockets.connect(WS + "/ws/panel?topics=devices", additional_headers=cookie(tok["A"]))
    pd = await websockets.connect(WS + "/ws/panel?topics=devices", additional_headers=cookie(tok["D"]))
    pg = await websockets.connect(WS + "/ws/panel?topics=devices", additional_headers=cookie(glob))

    async def changes(ws, seconds=3.0):
        out = []
        end = asyncio.get_running_loop().time() + seconds
        while asyncio.get_running_loop().time() < end:
            try:
                msg = json.loads(await asyncio.wait_for(ws.recv(), 0.5))
            except (asyncio.TimeoutError, ValueError):
                continue
            if msg.get("type") == "devices_changed":
                out.append(msg)
        return out

    try:
        await asyncio.sleep(1.5)
        await asyncio.gather(changes(pa, 0.5), changes(pd, 0.5), changes(pg, 0.5))
        req("/api/v1/devices/%s" % PB, sup, {"display_name": "tn-b-soket"}, method="PATCH")
        d_msgs, a_msgs, g_msgs = await asyncio.gather(changes(pd), changes(pa), changes(pg))
        chk(g_msgs and isinstance(g_msgs[-1].get("version"), int), "kapsamsız panele sürümle bildirildi")
        chk(d_msgs and "version" not in d_msgs[-1], "ilçe paneline sürümsüz bildirildi (%s)" % d_msgs)
        chk(not a_msgs, "B'nin değişikliği A'ya bildirilmedi (%s)" % a_msgs)
        req("/api/v1/devices/%s" % PA, sup, {"display_name": "tn-a-soket"}, method="PATCH")
        a_msgs = await changes(pa)
        chk(a_msgs and "version" not in a_msgs[-1], "A'nın değişikliği A'ya sürümsüz bildirildi (%s)" % a_msgs)
    finally:
        for ws in (pa, pd, pg):
            await ws.close()


def tn_pcs(items):
    return sorted(p for p in items or [] if str(p).startswith("HW-TN"))


async def test_power_and_progress(tok, sup, glob):
    """Güç komutu, kullanıcıya mesaj, güncelleme ilerlemesi; GLPI ve modül önizlemesi kurum geneli (süper admin)."""
    print("== güç komutu, mesaj, güncelleme ilerlemesi, GLPI")
    a, d, ta = tok["A"], tok["D"], tok["TA"]
    off = {"op": "lock", "delay": 0}
    chk(req("/api/devices/power", a, dict(off, target_mode="PC", targets=[PB]))[0] == 422,
        "B'ye güç komutu: kayıtlı olmayan bilgisayar (422)")
    chk(req("/api/devices/power", ta, dict(off, target_mode="LAB", targets=[LB]))[0] == 404,
        "jetonla B sınıfına güç komutu 404")
    s, out = req("/api/devices/power", a, dict(off, target_mode="ALL"))
    chk(s == 200 and out.get("created") == 0 and tn_pcs(out.get("skipped_offline")) == [PA],
        "ALL: yalnızca A'nın bilgisayarı hedef (%s %s)" % (s, tn_pcs(out.get("skipped_offline"))))
    s, out = req("/api/devices/power", d, dict(off, target_mode="ALL"))
    chk(s == 200 and tn_pcs(out.get("skipped_offline")) == [PA, PB], "ilçe: iki okul, atanmamış yok")
    s, out = req("/api/devices/power", glob, dict(off, target_mode="ALL"))
    chk(s == 200 and tn_pcs(out.get("skipped_offline")) == sorted(PCS), "kapsamsız admin: hepsi")
    msg = {"title": "tn", "text": "tn mesaj"}
    chk(req("/api/devices/message", a, dict(msg, target_mode="LAB", targets=[LB]))[0] == 404,
        "B sınıfına mesaj 404")
    chk(req("/api/devices/message", a, dict(msg, target_mode="PC", targets=[PB]))[0] == 422,
        "B'nin bilgisayarına mesaj 422")
    s, out = req("/api/devices/message", a, dict(msg, target_mode="LAB", targets=[LA]))
    chk(s == 200 and tn_pcs(out.get("skipped_offline")) == [PA], "A sınıfına mesaj (çevrimdışı)")

    s, prog = req("/api/system/update-progress", a, {"pcs": [PA, PB, PU], "version": "9.9.9", "since": 0})
    chk(s == 200 and [i["pc"] for i in prog.get("items", [])] == [PA] and prog.get("peer_labs") == [],
        "güncelleme ilerlemesi ve eş önbelleği özeti yalnızca A (%s)" % prog.get("items"))
    s, prog = req("/api/system/update-progress", d, {"pcs": [PA, PB, PU], "version": "9.9.9", "since": 0})
    chk(s == 200 and [i["pc"] for i in prog.get("items", [])] == [PA, PB], "ilçe: iki okul")

    chk(req("/api/system/glpi", a)[0] == 403 and req("/api/system/glpi", ta)[0] == 403,
        "GLPI ayarı kapsamlı hesaba kapalı (süper admin, kurum geneli)")
    chk(req("/api/system/glpi/sync", a, {})[0] == 403, "GLPI eşitlemesi kapsamlı hesaba kapalı")
    chk(req("/api/system/glpi", sup)[0] == 200, "GLPI ayarını süper admin okur")
    chk(req("/api/modules/terminal/preview?enabled=false", a)[0] == 403,
        "modül önizlemesi (kurum geneli sayılar) yalnızca süper admin")


async def test_sso_scope(c, tok, sup, units):
    """Dizin/OIDC hesabının kapsamı: eşlemeden ya da en dar varsayılan; hiçbir zaman kazara kapsamsız değil."""
    print("== dizin/OIDC hesabının kapsamı")
    ua, ub = units["a"], units["b"]
    saved = await c.fetchrow("SELECT enabled, config, secret, updated_by FROM sso_providers WHERE kind = 'oidc'")
    try:
        bad = {"enabled": False, "group_map": [{"group": "tn-x", "role": "admin", "org_scope": [999999]}]}
        s, out = req("/api/sso/settings/oidc", sup, bad, method="PUT")
        chk(s == 400 and "999999" in str(out), "eşlemede bilinmeyen birim 400 (%s)" % s)
        chk(req("/api/sso/settings/oidc", sup, {"enabled": False, "default_org_scope": "everything"},
                method="PUT")[0] == 422, "kapsamda yalnızca birimler ya da \"all\"")
        good = {"enabled": False, "group_map": [
            {"group": "tn-okul-a", "role": "admin", "pages": ["devices"], "org_scope": [ua]},
            {"group": "tn-ilce", "role": "viewer", "org_scope": "all"},
            {"group": "tn-kok", "role": "superadmin", "org_scope": [ua]}]}
        s, out = req("/api/sso/settings/oidc", sup, good, method="PUT")
        gm = out.get("group_map") or [] if s == 200 else []
        chk(s == 200 and [m.get("org_scope") for m in gm] == [[ua], "all", None],
            "eşleme kapsamı kaydedilir ve döner; süper adminde tutulmaz (%s)" % [m.get("org_scope") for m in gm])
        chk(req("/api/sso/settings/oidc", tok["A"], good, method="PUT")[0] == 403, "kapsamlı admin ayarlayamaz")
    finally:
        if saved:
            await c.execute("UPDATE sso_providers SET enabled = $1, config = $2, secret = $3, updated_by = $4 "
                            "WHERE kind = 'oidc'", saved["enabled"], saved["config"], saved["secret"],
                            saved["updated_by"])
        else:
            await c.execute("DELETE FROM sso_providers WHERE kind = 'oidc'")

    pops_db.db_pool = await asyncpg.create_pool(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
        min_size=1, max_size=2)
    try:
        u = await sso.link_user("oidc", "tn|1", "tnsso-yeni", "admin", ["devices"], None)
        chk(u["org_scope"] == [], "kapsamı ayarlanmamış yeni hesap: boş kapsam, kapsamsız değil (%r)" % u["org_scope"])
        t = server.create_jwt(u["username"], u["role"], u["token_version"])
        s, devs = req("/api/devices", t)
        chk(s == 200 and names(devs.get("devices", []) if isinstance(devs, dict) else devs) == [],
            "boş kapsamlı hesap hiçbir cihazı görmez")
        chk(req("/api/v1/devices/%s" % PA, t, {"display_name": "tn"}, method="PATCH")[0] == 404,
            "boş kapsamlı hesap A'nın bilgisayarına da ulaşamaz (404)")
        u = await sso.link_user("oidc", "tn|2", "tnsso-okul", "admin", ["devices"], [ua])
        chk(u["org_scope"] == [ua], "eşlemedeki birim yeni hesaba yazılır")
        t = server.create_jwt(u["username"], u["role"], u["token_version"])
        s, devs = req("/api/devices", t)
        chk(s == 200 and names(devs.get("devices", []) if isinstance(devs, dict) else devs) == [PA],
            "eşlemeyle A'ya sınırlı hesap yalnızca A'yı görür")
        u = await sso.link_user("oidc", "tn|3", "tnsso-ilce", "viewer", [], "all")
        chk(u["org_scope"] is None, "açıkça \"all\": kapsamsız")
        # Süper admin elle kapsam verdi; eşleme kapsam vermiyor: korunur
        await c.execute("UPDATE users SET org_scope = ARRAY[$1::int] WHERE username = 'tnsso-yeni'", ub)
        tv = await c.fetchval("SELECT token_version FROM users WHERE username = 'tnsso-yeni'")
        u = await sso.link_user("oidc", "tn|1", "tnsso-yeni", "admin", ["devices"], None)
        chk(u["org_scope"] == [ub] and u["token_version"] == tv, "elle verilen kapsam ayarlanmamış eşlemede korunur")
        u = await sso.link_user("oidc", "tn|1", "tnsso-yeni", "admin", ["devices"], [ua])
        chk(u["org_scope"] == [ua] and u["token_version"] == tv + 1,
            "ayarlanan kapsam girişte yazılır, oturumlar yenilenir")
        u = await sso.link_user("oidc", "tn|4", "tnsso-kok", "superadmin", [], [ua])
        chk(u["org_scope"] is None, "süper admin kapsamsız")
        u = await sso.link_user("oidc", "tn|4", "tnsso-kok", "admin", [], None)
        chk(u["org_scope"] == [], "süper adminlikten düşen hesap kapsamsız kalmaz (en dar kapsam)")
        u = await sso.link_user("oidc", "tn|5", "tnsso-silik", "viewer", [], [999999])
        chk(u["org_scope"] == [], "silinmiş birimli eşleme boş kapsam verir, kapsamsız değil")
        chk(sso.map_role(["tn-okul-a", "tn-ilce"], good["group_map"], dn=False)[2] == "all"
            and sso.map_role(["tn-okul-a"], [dict(m, pages=m.get("pages", [])) for m in good["group_map"]],
                             dn=False)[2] == [ua], "eşleme kapsamı map_role'den gelir")
    finally:
        await pops_db.db_pool.close()
        pops_db.db_pool = None


async def test_unit_delete(c, sup, units):
    print("== birim silme")
    chk(req("/api/org-units/%d" % units["district"], sup, method="DELETE")[0] == 409, "alt birimi olan silinemez")
    s, _ = req("/api/org-units/%d" % units["b"], sup, method="DELETE")
    chk(s == 200 and await c.fetchval("SELECT org_unit_id FROM custom_labs WHERE lab_name = $1", LB) is None
        and await c.fetchval("SELECT org_unit_id FROM licenses WHERE name = 'tn-lisans-B'") is None,
        "silinen birimin sınıfı ve lisansı birimsiz kaldı")
    s, made = req("/api/org-units", sup, {"name": "TN Geçici"})
    tmp = made["id"]
    await c.execute("UPDATE users SET org_scope = ARRAY[$1::int, $2::int] WHERE username = 'tnviewerA'",
                    units["a"], tmp)
    chk(req("/api/org-units/%d" % tmp, sup, method="DELETE")[0] == 200
        and await c.fetchval("SELECT org_scope FROM users WHERE username = 'tnviewerA'") == [units["a"]],
        "silinen birim kullanıcı kapsamından çıkarıldı")


if __name__ == "__main__":
    asyncio.run(main())
