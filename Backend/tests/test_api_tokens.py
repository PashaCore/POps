"""API jetonları, /api/v1 ve REST adları — entegrasyon testi (CI 'security' job'ı).

- Jetonu yalnızca süper admin oluşturur, listeler ve iptal eder (panel oturumuyla); admin ve izleyici yönetemez, jeton
  da jeton yönetemez. Jeton bir kez döner, veritabanında yalnızca SHA-256 özeti ve ilk 8 karakteri durur.
- Görüntüleyici jetonu yalnızca GET yapar; yönetici jetonu admin işlerini yapar (görev, karantina), süper admin
  uçlarına, kullanıcı listesine, 2FA'ya ve uzak ekrana ulaşamaz. Süresi dolan ve iptal edilen jeton anında 401.
- last_used_at dakikada en çok bir kez yazılır. Jetonla yapılan iş görev ve denetim kayıtlarına "token:<ad>" yazılır.
- /api/v1 ve REST adları eski yollarla aynı sonucu verir; eski yollar çalışmaya devam eder (eğik çizgili sınıf adı
  dahil). task_sequence ve taskSequence kabul edilir. Çerezle gelen değişiklik X-Requested-With'siz 403 (CSRF).

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET (sunucuyla aynı); METRICS_TOKEN varsa metrik etiketleri de denetlenir.
"""

import asyncio
import hashlib
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402

import server  # noqa: E402  (create_jwt)

HTTP = os.environ["POPS_TEST_HTTP"]
PCS = ["HW-TK1", "HW-TK2"]
LABS = ["TK-Lab", "TK-Lab-2", "TK 9/A", "TK 9/B", "TK-Old", "TK-Cookie"]
USERS = (("tksuper", "superadmin"), ("tkadmin", "admin"), ("tkviewer", "viewer"))
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, body=None, method=None, headers=None):
    """(durum, JSON gövde). token: Bearer (JWT ya da pops_ jetonu)."""
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    if body is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=40) as resp:
            raw = resp.read()
            ctype = resp.headers.get("Content-Type", "")
            return resp.status, (json.loads(raw or b"null") if "json" in ctype else raw.decode("utf-8", "replace"))
    except urllib.error.HTTPError as e:
        raw = e.read()
        try:
            return e.code, json.loads(raw or b"null")
        except ValueError:
            return e.code, None


def lab_path(name):
    return urllib.parse.quote(name, safe="")


async def conn():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"],
    )


async def cleanup(c):
    await c.execute("DELETE FROM api_tokens WHERE name LIKE 'tk-%'")
    await c.execute("DELETE FROM users WHERE username = ANY($1::text[])", [u for u, _ in USERS])
    await c.execute("DELETE FROM tasks WHERE target_pc = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM clients WHERE pc_name = ANY($1::text[])", PCS)
    await c.execute("DELETE FROM custom_labs WHERE lab_name = ANY($1::text[])", LABS + ["TK-Lab-renamed"])
    await c.execute("DELETE FROM lab_settings WHERE lab_name = ANY($1::text[])", LABS + ["TK-Lab-renamed"])


async def audit(c, action, key, value):
    rows = await c.fetch(
        "SELECT changes FROM device_audit_logs WHERE action = $1 AND changes::jsonb ->> $2 = $3 ORDER BY id DESC",
        action, key, value,
    )
    return [json.loads(r["changes"]) for r in rows]


async def main():
    c = await conn()
    await cleanup(c)
    for u, role in USERS:
        await c.execute(
            "INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ($1,'x',$2,'[]',0)",
            u, role)
    await c.execute(
        "INSERT INTO clients (pc_name, hostname, lab_name, status) VALUES "
        "('HW-TK1', 'tk1', 'TK-Lab', 'Offline'), ('HW-TK2', 'tk2', 'TK-Lab', 'Offline')"
    )
    sa = server.create_jwt("tksuper", "superadmin", 0)
    admin = server.create_jwt("tkadmin", "admin", 0)
    viewer = server.create_jwt("tkviewer", "viewer", 0)
    try:
        await run(c, sa, admin, viewer)
    finally:
        await cleanup(c)
        await c.close()
    if FAILS:
        print("\nBASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("\nTUM API JETONU VE /api/v1 TESTLERI GECTI")


async def run(c, sa, admin, viewer):
    print("== jeton yönetimi yalnızca süper admin")
    s, made = req("/api/v1/tokens", sa, {"name": "tk-admin", "role": "admin", "expires_days": 30})
    tok_admin = made.get("token", "") if isinstance(made, dict) else ""
    chk(s == 200 and tok_admin.startswith("pops_") and len(tok_admin) >= 48 and made.get("role") == "admin"
        and made.get("state") == "active" and made.get("expires_at"), "süper admin yönetici jetonu oluşturdu")
    chk(made.get("token_prefix") == tok_admin[5:13] and made.get("created_by") == "tksuper",
        "önek ilk 8 karakter, oluşturan kaydedildi")
    s, made_v = req("/api/v1/tokens", sa, {"name": "tk-viewer", "role": "viewer"})
    tok_viewer = made_v.get("token", "")
    chk(s == 200 and made_v.get("expires_at") is None and tok_viewer != tok_admin, "süresiz görüntüleyici jetonu")
    s, lst = req("/api/v1/tokens", sa)
    names = {t["name"]: t for t in lst} if s == 200 else {}
    chk(s == 200 and {"tk-admin", "tk-viewer"} <= set(names) and not any("token" in t for t in lst)
        and names["tk-admin"]["role"] == "admin", "liste jetonun kendisini içermez")
    chk(req("/api/tokens", sa)[1] == lst, "/api/tokens ile /api/v1/tokens aynı")
    chk(req("/api/v1/tokens", sa, {"name": "tk-admin", "role": "viewer"})[0] == 409, "aynı ad 409")
    chk(req("/api/v1/tokens", sa, {"name": "tk-<x>", "role": "viewer"})[0] == 400, "geçersiz karakterli ad 400")
    chk(req("/api/v1/tokens", sa, {"name": "tk-super", "role": "superadmin"})[0] == 422, "superadmin rolü 422")
    chk(req("/api/v1/tokens", sa, {"name": "tk-x", "role": "viewer", "expires_days": 0})[0] == 422, "0 gün 422")
    chk(req("/api/v1/tokens", sa, {"name": "tk-x", "role": "viewer", "scope": "all"})[0] == 422, "bilinmeyen alan 422")

    for who, t in (("admin", admin), ("izleyici", viewer), ("yönetici jetonu", tok_admin),
                   ("görüntüleyici jetonu", tok_viewer)):
        codes = [req("/api/v1/tokens", t)[0], req("/api/v1/tokens", t, {"name": "tk-y", "role": "viewer"})[0],
                 req("/api/v1/tokens/%d" % made["id"], t, method="DELETE")[0]]
        chk(codes == [403, 403, 403], "%s jeton yönetemez (%s)" % (who, codes))
    chk(not await c.fetch("SELECT 1 FROM api_tokens WHERE name = 'tk-y'"), "yetkisiz istek jeton oluşturmadı")

    print("== saklama: jeton açık metin olarak durmaz")
    row = await c.fetchrow("SELECT * FROM api_tokens WHERE id = $1", made["id"])
    secret = tok_admin[len("pops_"):]
    leaked = await c.fetchval(
        "SELECT count(*) FROM api_tokens WHERE strpos(row_to_json(api_tokens)::text, $1) > 0", secret[8:])
    chk(row["token_hash"] == hashlib.sha256(tok_admin.encode()).hexdigest() and row["token_prefix"] == secret[:8]
        and leaked == 0, "yalnızca SHA-256 özeti ve 8 karakterlik önek")
    created = await audit(c, "api_token_created", "id", str(made["id"]))
    chk(created and created[0].get("by") == "tksuper" and created[0].get("role") == "admin"
        and secret not in json.dumps(created[0]), "oluşturma denetim kaydında, jetonsuz")

    print("== görüntüleyici jetonu: yalnızca okuma")
    chk(req("/api/v1/devices", tok_viewer)[0] == 200, "GET /api/v1/devices 200")
    chk(req("/api/devices", tok_viewer)[0] == 200, "düz /api de çalışır")
    chk(req("/api/v1/labs", tok_viewer, {"lab_name": "TK-Lab-2"})[0] == 403, "POST 403")
    chk(req("/api/v1/tasks/status", tok_viewer, {"ids": [1]})[0] == 403, "okuyan POST da 403 (yalnızca GET)")
    chk(req("/api/v1/labs/TK-Lab", tok_viewer, method="DELETE")[0] == 403, "DELETE 403")
    chk(req("/api/v1/thumbnail/HW-TK1", tok_viewer)[0] == 403, "admin GET'i (önizleme) 403")

    print("== yönetici jetonu: admin işleri, süper admin ve panel oturumu uçları kapalı")
    seq = [{"name": "tk", "type": "CMD", "command": "echo tk-token"}]
    body = {"target_mode": "PC", "targets": ["HW-TK1"], "task_sequence": seq, "source": "api"}
    s, d = req("/api/v1/deploy_orchestration", tok_admin, body)
    ids = d.get("task_ids") or [] if isinstance(d, dict) else []
    trow = await c.fetchrow("SELECT created_by, source FROM tasks WHERE id = $1", ids[0]) if ids else None
    chk(s == 200 and len(ids) == 1 and trow and trow["created_by"] == "token:tk-admin",
        "/api/v1/deploy_orchestration görev açtı, sahibi token:tk-admin (%s %s)" % (s, dict(trow) if trow else d))
    s, d = req("/api/v1/tasks", tok_admin, {"target_mode": "pc", "targets": ["HW-TK2"], "taskSequence": seq})
    chk(s == 200 and d.get("created") == 1, "POST /api/v1/tasks (REST adı) taskSequence ile de çalışır")
    s, d = req("/api/deploy_orchestration", tok_admin, {"target_mode": "PC", "targets": ["HW-TK2"],
                                                        "task_sequence": [dict(seq[0], command="echo tk-2")]})
    chk(s == 200 and d.get("created") == 1, "eski yol task_sequence ile")
    chk(req("/api/v1/tasks", tok_admin, {"target_mode": "PC", "targets": ["HW-TK2"], "task_sequence": seq,
                                         "taskSequence": seq})[0] == 422, "ikisi birden 422")

    s, d = req("/api/v1/devices/HW-TK1/quarantine", tok_admin, {"reason": "tk test"})
    q = await c.fetchrow("SELECT is_quarantined FROM clients WHERE pc_name = 'HW-TK1'")
    locked = await audit(c, "lockdown", "admin", "token:tk-admin")
    chk(s == 200 and q["is_quarantined"] and locked and locked[0].get("reason") == "tk test",
        "karantina REST adıyla; denetim kaydında token:tk-admin")
    s, d = req("/api/v1/devices/HW-TK1/quarantine", tok_admin, {"reason": "tk bitti"}, method="DELETE")
    q = await c.fetchrow("SELECT is_quarantined FROM clients WHERE pc_name = 'HW-TK1'")
    chk(s == 200 and not q["is_quarantined"] and await audit(c, "unlock", "admin", "token:tk-admin"),
        "karantina kaldırıldı (DELETE), denetimde token:tk-admin")
    actor = await c.fetchval("SELECT actor_id FROM agent_logs_v2 WHERE pc_name = 'HW-TK1' AND action = 'lockdown' "
                             "ORDER BY id DESC LIMIT 1")
    chk(actor == "token:tk-admin", "olay kaydında da token:tk-admin")

    refused = {
        "GET /api/v1/system/enroll-tokens": req("/api/v1/system/enroll-tokens", tok_admin)[0],
        "POST /api/v1/system/enforce-auth": req("/api/v1/system/enforce-auth", tok_admin, {"enabled": False})[0],
        "GET /api/v1/system/audit-verify": req("/api/v1/system/audit-verify", tok_admin)[0],
        "POST /api/v1/admin/users": req("/api/v1/admin/users", tok_admin, {"username": "tk-evil", "password": "x",
                                                                           "role": "superadmin",
                                                                           "permissions": "[]"})[0],
        "GET /api/v1/admin/users": req("/api/v1/admin/users", tok_admin)[0],
        "GET /api/v1/admin/2fa/status": req("/api/v1/admin/2fa/status", tok_admin)[0],
        "POST /api/v1/audit/session/start": req("/api/v1/audit/session/start", tok_admin, {
            "target_pc": "HW-TK1", "reason": "x", "is_mandatory": False})[0],
        "GET /api/v1/thumbnail/HW-TK1": req("/api/v1/thumbnail/HW-TK1", tok_admin)[0],
        "POST /api/v1/remote_input": req("/api/v1/remote_input", tok_admin, {"device": "HW-TK1",
                                                                             "input_type": "keyboard"})[0],
    }
    chk(all(v == 403 for v in refused.values()), "süper admin, kullanıcı, 2FA ve uzak ekran uçları 403 (%s)" % refused)
    chk(not await c.fetch("SELECT 1 FROM users WHERE username = 'tk-evil'"), "jeton kullanıcı oluşturamadı")
    chk(req("/api/v1/admin/users", admin)[0] == 200 and req("/api/v1/admin/2fa/status", viewer)[0] == 200,
        "panel oturumu (JWT) aynı uçları kullanabilir")

    print("== son kullanım dakikada en çok bir kez yazılır")
    first = await c.fetchval("SELECT last_used_at FROM api_tokens WHERE id = $1", made["id"])
    req("/api/v1/devices", tok_admin)
    again = await c.fetchval("SELECT last_used_at FROM api_tokens WHERE id = $1", made["id"])
    chk(first is not None and again == first, "bir dakika içinde yeniden yazılmadı")
    await c.execute("UPDATE api_tokens SET last_used_at = NOW() - interval '2 minutes' WHERE id = $1", made["id"])
    old = await c.fetchval("SELECT last_used_at FROM api_tokens WHERE id = $1", made["id"])
    req("/api/v1/devices", tok_admin)
    newer = await c.fetchval("SELECT last_used_at FROM api_tokens WHERE id = $1", made["id"])
    chk(newer > old, "bir dakikadan eskiyse güncellendi")

    print("== /api/v1 ve REST adları eski yollarla aynı sonucu verir")
    for old_path, new_path in (
        ("/api/devices", "/api/v1/devices"), ("/api/custom_labs", "/api/v1/labs"),
        ("/api/get_concurrent_limit", "/api/v1/settings/task-concurrency"),
        ("/api/lab_settings", "/api/v1/lab_settings"), ("/api/tasks?limit=5", "/api/v1/tasks?limit=5"),
    ):
        a, b = req(old_path, tok_viewer), req(new_path, tok_viewer)
        chk(a[0] == 200 and a == b, "%s == %s" % (old_path, new_path))

    s, _ = req("/api/v1/labs", tok_admin, {"lab_name": "TK-Lab-2"})
    chk(s == 200 and "TK-Lab-2" in req("/api/custom_labs", admin)[1], "POST /api/v1/labs eski listede görünür")
    s, _ = req("/api/create_lab", admin, {"lab_name": "TK-Old"})
    chk(s == 200 and "TK-Old" in req("/api/v1/labs", tok_viewer)[1], "eski /api/create_lab çalışır, yenide görünür")
    s, _ = req("/api/v1/labs/" + lab_path("TK-Lab-2"), tok_admin, {"new_name": "TK-Lab-renamed"}, method="PATCH")
    labs = req("/api/v1/labs", tok_viewer)[1]
    chk(s == 200 and "TK-Lab-renamed" in labs and "TK-Lab-2" not in labs, "PATCH /api/v1/labs/{ad} yeniden adlandırdı")
    s, _ = req("/api/v1/labs/" + lab_path("TK-Lab-renamed"), tok_admin, method="DELETE")
    chk(s == 200 and "TK-Lab-renamed" not in req("/api/custom_labs", admin)[1], "DELETE /api/v1/labs/{ad}")

    # Eğik çizgili sınıf adı (Türk okullarında "9/A" gibi): yolda eğik çizgi olduğu gibi (önerilen; Apache %2F'yi
    # reddeder) ya da %2F (doğrudan arka uç ve nginx)
    s, _ = req("/api/v1/labs", tok_admin, {"lab_name": "TK 9/A"})
    s2, _ = req("/api/v1/devices/move", tok_admin, {"pc_names": ["HW-TK1", "HW-TK2"], "new_lab": "TK 9/A"})
    moved = {r["pc_name"]: r["lab_name"] for r in await c.fetch(
        "SELECT pc_name, lab_name FROM clients WHERE pc_name = ANY($1::text[])", PCS)}
    chk(s == 200 and s2 == 200 and set(moved.values()) == {"TK 9/A"}, "POST /api/v1/devices/move (birden çok)")
    s, _ = req("/api/v1/labs/TK%209/A/main-pc", tok_admin, {"pc_name": "HW-TK1"}, method="PUT")
    s2, _ = req("/api/v1/labs/%s/main-pc" % lab_path("TK 9/A"), tok_admin, {"pc_name": "HW-TK1"}, method="PUT")
    settings = req("/api/lab_settings", admin)[1].get("TK 9/A", {})
    chk(s == 200 and s2 == 200 and settings.get("main_pc") == "HW-TK1",
        "PUT main-pc (düz eğik çizgi ve %2F) aç/kapa yapmaz (iki kez = aynı)")
    s, _ = req("/api/v1/labs/%s/layout" % lab_path("TK 9/A"), tok_admin, {"layout_json": "{\"HW-TK1\": [1, 2]}"},
               method="PUT")
    settings = req("/api/v1/lab_settings", tok_viewer)[1].get("TK 9/A", {})
    chk(s == 200 and json.loads(settings.get("layout_json") or "{}") == {"HW-TK1": [1, 2]}, "PUT layout")
    s, _ = req("/api/v1/labs/%s/main-pc" % lab_path("TK 9/A"), tok_admin, method="DELETE")
    settings = req("/api/lab_settings", admin)[1].get("TK 9/A", {})
    labs = req("/api/custom_labs", admin)[1]
    chk(s == 200 and settings.get("main_pc") is None and "TK 9/A" in labs, "DELETE main-pc sınıfı silmez")
    s, _ = req("/api/v1/labs/TK%209/A", tok_admin, {"new_name": "TK 9/B"}, method="PATCH")
    moved = {r["lab_name"] for r in await c.fetch("SELECT lab_name FROM clients WHERE pc_name = ANY($1::text[])", PCS)}
    chk(s == 200 and moved == {"TK 9/B"} and "TK 9/B" in req("/api/v1/lab_settings", tok_viewer)[1],
        "eğik çizgili sınıf yeniden adlandırıldı; cihazlar ve plan taşındı")
    s, _ = req("/api/move_pc", admin, {"pc_name": "HW-TK2", "new_lab": "TK-Lab"})
    chk(s == 200 and await c.fetchval("SELECT lab_name FROM clients WHERE pc_name = 'HW-TK2'") == "TK-Lab",
        "eski /api/move_pc çalışır")
    s, _ = req("/api/v1/devices/HW-TK2", tok_admin, {"display_name": "TK İki"}, method="PATCH")
    dev = [d for d in req("/api/devices", admin)[1] if d["hw_id"] == "HW-TK2"]
    chk(s == 200 and dev and dev[0]["display_name"] == "TK İki", "PATCH /api/v1/devices/{pc} görünen adı")
    s, _ = req("/api/v1/labs/TK%209/B", tok_admin, method="DELETE")
    chk(s == 200 and await c.fetchval("SELECT lab_name FROM clients WHERE pc_name = 'HW-TK1'") == "Atanmamis_Cihazlar",
        "eğik çizgili sınıf silindi, cihazı atanmamışlara döndü")

    before = req("/api/get_concurrent_limit", admin)[1]["limit"]
    s, _ = req("/api/v1/settings/task-concurrency", tok_admin, {"limit": before + 1}, method="PUT")
    chk(s == 200 and req("/api/get_concurrent_limit", admin)[1]["limit"] == before + 1,
        "PUT /api/v1/settings/task-concurrency eski GET'te görünür")
    chk(req("/api/set_concurrent_limit", admin, {"limit": before})[0] == 200
        and req("/api/v1/settings/task-concurrency", tok_viewer)[1] == {"limit": before}, "eski POST yenide görünür")
    chk(req("/api/v1/settings/task-concurrency", tok_admin, {"limit": -1}, method="PUT")[0] == 422,
        "doğrulama aynı (eksi sınır 422)")

    print("== çerez oturumu: değişiklik X-Requested-With ister")
    cookie = {"Cookie": "pops_jwt=" + admin}
    chk(req("/api/v1/labs", None, {"lab_name": "TK-Cookie"}, headers=cookie)[0] == 403, "/api/v1 çerezle başlıksız 403")
    chk(req("/api/create_lab", None, {"lab_name": "TK-Cookie"}, headers=cookie)[0] == 403, "/api çerezle başlıksız 403")
    chk(req("/api/v1/labs", None, {"lab_name": "TK-Cookie"},
            headers=dict(cookie, **{"X-Requested-With": "XMLHttpRequest"}))[0] == 200, "başlıkla 200")
    chk(req("/api/v1/devices", None, headers=cookie)[0] == 200, "çerezle GET başlık istemez")
    chk(req("/api/v1/devices", None, headers={"Cookie": "pops_jwt=" + tok_admin})[0] == 401,
        "API jetonu çerezde kabul edilmez")

    print("== süre ve iptal anında geçerli")
    s, exp = req("/api/v1/tokens", sa, {"name": "tk-expiring", "role": "viewer", "expires_days": 1})
    chk(req("/api/v1/devices", exp["token"])[0] == 200, "süresi dolmamış jeton çalışır")
    await c.execute("UPDATE api_tokens SET expires_at = NOW() - interval '1 second' WHERE id = $1", exp["id"])
    chk(req("/api/v1/devices", exp["token"])[0] == 401, "süresi dolan jeton 401")
    states = {t["name"]: t["state"] for t in req("/api/v1/tokens", sa)[1]}
    chk(states.get("tk-expiring") == "expired", "listede süresi dolmuş görünür")
    s, r = req("/api/v1/tokens/%d" % made["id"], sa, method="DELETE")
    chk(s == 200 and r.get("already_revoked") is False, "iptal edildi")
    chk(req("/api/v1/devices", tok_admin)[0] == 401 and req("/api/devices", tok_admin)[0] == 401,
        "iptal edilen jeton anında 401")
    s, r = req("/api/v1/tokens/%d" % made["id"], sa, method="DELETE")
    chk(s == 200 and r.get("already_revoked") is True, "ikinci iptal zararsız")
    chk(req("/api/v1/tokens/999999999", sa, method="DELETE")[0] == 404, "olmayan jeton 404")
    # Denetim kaydı silinmez: önceki çalıştırmaların kayıtları karışmasın diye jeton kimliğiyle aranır
    revoked = await audit(c, "api_token_revoked", "id", str(made["id"]))
    chk(len(revoked) == 1 and revoked[0].get("name") == "tk-admin" and revoked[0].get("by") == "tksuper",
        "iptal denetim kaydında (bir kez)")
    chk({t["name"]: t["state"] for t in req("/api/v1/tokens", sa)[1]}.get("tk-admin") == "revoked",
        "iptal edilen jeton listede kalır")
    chk(req("/api/v1/devices", "pops_" + "A" * 43)[0] == 401 and req("/api/v1/devices", "pops_")[0] == 401,
        "bilinmeyen jeton 401")

    metrics_token = os.environ.get("METRICS_TOKEN", "")
    if len(metrics_token) >= 16:
        s, text = req("/metrics", metrics_token)
        chk(s == 200 and 'route="/api/v1' not in text and "HW-TK1" not in text
            and 'route="/api/devices/{pc_name}/quarantine"' in text,
            "metrikler rota şablonuyla, /api/v1 ayrı seri açmaz")


if __name__ == "__main__":
    asyncio.run(main())
