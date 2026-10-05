"""Salt okunur demo hesabı (POPS_DEMO_USERS) — entegrasyon testi (CI 'security' job'ı).

- Demo hesabı şifreyle giriş yapar ve okuma uçlarını kullanır (cihazlar, kayıtlar, raporlar, görev durumu).
- Panel kimliği isteyen HER yazma ucu (POST/PUT/DELETE) demo hesabına 403 "Demo hesabında değiştirilemez" döner:
  kendi şifresi, 2FA kurulumu/açma/kapama dahil; uç listesi uygulamanın rota tablosundan çıkarılır, yeni bir uç
  eklendiğinde de denetlenir. Hiçbir şey değişmez (şifre özeti, 2FA anahtarı, token_version).
- Demo hesabı bir viewer'dır: yönetici uçları (GET dahil) zaten kapalıdır.
- Listede olmayan sıradan bir viewer kendi 2FA'sını yine kurabilir (kısıt yalnızca demo hesaplarına).

Sunucu POPS_DEMO_USERS ile başlatılmalıdır (CI ve tests/run_local.sh 'ci_demo' verir); test sürecinin ortamında da
aynı değer bulunur. Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET + POPS_DEMO_USERS.
"""

import asyncio
import json
import os
import re
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import asyncpg  # noqa: E402
import bcrypt  # noqa: E402

import server  # noqa: E402  (app, create_jwt)
from pops import security  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]
DEMO_USERS = [u.strip() for u in os.environ.get("POPS_DEMO_USERS", "").split(",") if u.strip()]
DEMO = DEMO_USERS[0] if DEMO_USERS else "ci_demo"
OTHER = "ci_demo_control_viewer"
PASSWORD = "demo-pass-123"
VIEWER_PAGES = '["devices", "labs", "tasks", "vision", "policies", "logger", "reports", "helpdesk"]'
FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def req(path, token=None, body=None, method=None, headers=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method or ("POST" if body is not None else "GET"))
    r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        with urllib.request.urlopen(r, timeout=30) as resp:
            return resp.status, json.loads(resp.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except ValueError:
            return e.code, {}


async def db():
    return await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"),
        port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"],
        password=os.environ["DB_PASS"],
        database=os.environ["DB_NAME"],
    )


async def q(sql, *args):
    c = await db()
    try:
        return await c.fetch(sql, *args)
    finally:
        await c.close()


def _uses_require_auth(dependant) -> bool:
    if dependant.call is security.require_auth:
        return True
    return any(_uses_require_auth(d) for d in dependant.dependencies)


def _flatten(routes):
    # FastAPI dahil edilen router'ları iç içe tutar (_IncludedRouter.original_router): uçlar düzleştirilir
    for route in routes:
        inner = getattr(route, "original_router", None)
        if inner is not None:
            yield from _flatten(inner.routes)
        else:
            yield route


def write_routes():
    """(metot, yol) — panel oturumu isteyen (require_auth'a bağlı) bütün yazma uçları, rota tablosundan."""
    out = []
    for route in _flatten(server.app.routes):
        methods = getattr(route, "methods", None) or set()
        dependant = getattr(route, "dependant", None)
        if dependant is None or not _uses_require_auth(dependant):
            continue
        for m in sorted(methods & {"POST", "PUT", "DELETE", "PATCH"}):
            out.append((m, re.sub(r"\{[^}]+\}", "1", route.path)))
    return out


def main():
    if not DEMO_USERS:
        print("  FAIL POPS_DEMO_USERS tanımlı değil: sunucuyu ve testi POPS_DEMO_USERS=ci_demo ile çalıştırın")
        sys.exit(1)
    pw_hash = bcrypt.hashpw(PASSWORD.encode(), bcrypt.gensalt()).decode()
    for name in (DEMO, OTHER):
        asyncio.run(q("DELETE FROM users WHERE username = $1", name))
        asyncio.run(q("INSERT INTO users (username, password_hash, role, permissions) VALUES ($1, $2, 'viewer', $3)",
                      name, pw_hash, VIEWER_PAGES))
    before = asyncio.run(q("SELECT id, password_hash, totp_secret, totp_enabled, token_version FROM users "
                           "WHERE username = $1", DEMO))[0]

    # Giriş (oturum açmak yazma sayılmaz). Giriş sınırı adres başına: bu testin kendi adresi
    s, b = req("/api/admin/login", body={"username": DEMO, "password": PASSWORD},
               headers={"X-Forwarded-For": "198.51.100.77"})
    chk(s == 200 and b.get("role") == "viewer" and b.get("token"), "demo hesabı şifreyle giriş yapar (viewer)")
    token = b.get("token") or server.create_jwt(DEMO, "viewer", before["token_version"])

    # Okuma uçları açık
    for path in ("/api/devices", "/api/logs?limit=5", "/api/tasks?limit=5", "/api/inventory", "/api/lab_settings",
                 "/api/reports/summary?days=14", "/api/admin/2fa/status", "/api/agent_policies/meta"):
        chk(req(path, token)[0] == 200, "demo okur: GET %s" % path)
    s, b = req("/api/tasks/status", token, {"ids": [1, 2]})
    chk(s == 200 and "items" in b, "salt okunur POST /api/tasks/status demo hesabına açık")

    # Kendi şifresi ve 2FA'sı
    me = before["id"]
    s, b = req("/api/admin/2fa/setup", token, {})
    chk(s == 403 and b.get("detail") == security.DEMO_DENIED, "2FA kurulumu reddedildi: %s %s" % (s, b))
    chk(req("/api/admin/2fa/enable", token, {"otp": "123456"})[0] == 403, "2FA açma reddedildi")
    chk(req("/api/admin/2fa/disable", token, {"otp": "123456"})[0] == 403, "2FA kapatma reddedildi")
    s, b = req("/api/admin/users/%d" % me, token, {"username": DEMO, "role": "viewer", "permissions": "[]",
                                                   "password": "yeni-sifre-1"}, method="PUT")
    chk(s == 403 and b.get("detail") == security.DEMO_DENIED, "kendi şifresini değiştiremez")

    # Çerezle gelen istek de (CSRF başlığıyla) aynı yanıtı alır
    s, b = req("/api/admin/2fa/setup", body={}, headers={"Cookie": "pops_jwt=" + token,
                                                         "X-Requested-With": "XMLHttpRequest"})
    chk(s == 403 and b.get("detail") == security.DEMO_DENIED, "çerezle 2FA kurulumu da reddedildi")

    # Rota tablosundaki bütün yazma uçları
    routes = write_routes()
    chk(len(routes) > 40, "rota tablosundan %d yazma ucu bulundu" % len(routes))
    leaked = []
    for method, path in routes:
        if path in security.DEMO_READ_ONLY_POSTS:
            continue
        s, b = req(path, token, {}, method=method)
        if s != 403 or b.get("detail") != security.DEMO_DENIED:
            leaked.append("%s %s -> %s %s" % (method, path, s, b))
    chk(not leaked, "her yazma ucu demo hesabına 403 döndü%s" % ("" if not leaked else ": " + "; ".join(leaked[:5])))

    after = asyncio.run(q("SELECT password_hash, totp_secret, totp_enabled, token_version FROM users "
                          "WHERE username = $1", DEMO))[0]
    chk(after["password_hash"] == before["password_hash"] and after["totp_secret"] is None
        and not after["totp_enabled"] and after["token_version"] == before["token_version"],
        "demo hesabında hiçbir şey değişmedi (şifre, 2FA, oturum sürümü)")

    # Viewer olduğu için yönetici uçları okumada da kapalı
    for path in ("/api/admin/users", "/api/notifications", "/api/tickets", "/api/system/version",
                 "/api/system/enroll-tokens", "/api/system/notify-settings"):
        s, b = req(path, token)
        chk(s == 403 and b.get("detail") != security.DEMO_DENIED, "viewer olarak kapalı: GET %s (%s)" % (path, s))

    # Kısıt yalnızca listedeki hesaplara: sıradan viewer kendi 2FA'sını kurabilir
    other = server.create_jwt(OTHER, "viewer", 0)
    s, b = req("/api/admin/2fa/setup", other, {})
    chk(s == 200 and b.get("secret"), "listede olmayan viewer kendi 2FA kurulumunu başlatabilir")

    for name in (DEMO, OTHER):
        asyncio.run(q("DELETE FROM users WHERE username = $1", name))
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM DEMO TESTLERI GECTI")


if __name__ == "__main__":
    main()
