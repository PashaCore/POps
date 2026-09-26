"""Panel 2FA (TOTP) entegrasyon testi — CI 'security' job'ıyla aynı ortamda koşar.

Doğrular: 2FA kapalıyken şifreyle giriş; opt-in kurulum ONAYLANANA kadar zorunlu
DEĞİL (kilitlenme yok); yanlış kod enable/disable'ı reddeder; açıkken giriş
challenge + kod ister; iki adımlı ve tek-atış (inline otp) giriş; geçersiz/süresi
dolmuş challenge reddedilir. TOTP kodları server modülünden (_totp_code) üretilir.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET (run_local.sh / CI export eder).
"""
import asyncio
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import server  # noqa: E402  (_totp_code, _totp_new_secret)
import asyncpg  # noqa: E402
import bcrypt   # noqa: E402

BASE = os.environ["POPS_TEST_HTTP"]
USER = "twofa-test"
PW = "twofa-pass-123"


def req(path, method="GET", body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=10) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read().decode())
        except Exception:
            return e.code, {}


async def _seed():
    conn = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"])
    h = bcrypt.hashpw(PW.encode(), bcrypt.gensalt()).decode()
    await conn.execute("DELETE FROM users WHERE username=$1", USER)
    await conn.execute(
        "INSERT INTO users (username,password_hash,role,permissions) VALUES ($1,$2,'superadmin','[]')", USER, h)
    await conn.close()


def _code(secret):
    return server._totp_code(secret, int(time.time() // 30))


def main():
    asyncio.get_event_loop().run_until_complete(_seed())
    passed = 0

    def check(cond, msg):
        nonlocal passed
        assert cond, "FAIL: " + msg
        passed += 1
        print("  ok:", msg)

    s, b = req("/api/admin/login", "POST", {"username": USER, "password": PW})
    check(s == 200 and b.get("status") == "success" and b.get("token"), "2FA kapalı → şifreyle giriş")
    token = b["token"]

    s, b = req("/api/admin/2fa/status", "GET", token=token)
    check(s == 200 and b.get("enabled") is False, "status: kapalı")

    s, b = req("/api/admin/2fa/setup", "POST", token=token)
    check(s == 200 and b.get("secret") and b.get("otpauth_uri", "").startswith("otpauth://totp/"),
          "setup: secret + otpauth uri")
    secret = b["secret"]

    s, b = req("/api/admin/login", "POST", {"username": USER, "password": PW})
    check(s == 200 and b.get("status") == "success", "kurulum onaylanmadan giriş hâlâ şifreyle (kilitlenme yok)")

    s, b = req("/api/admin/2fa/enable", "POST", {"otp": "000000"}, token=token)
    check(s == 400, "yanlış kodla enable reddedildi")

    s, b = req("/api/admin/2fa/enable", "POST", {"otp": _code(secret)}, token=token)
    check(s == 200 and b.get("enabled") is True, "doğru kodla enable → aktif")

    s, b = req("/api/admin/login", "POST", {"username": USER, "password": PW})
    check(s == 200 and b.get("status") == "totp_required" and b.get("challenge"),
          "2FA açık → totp_required + challenge")
    challenge = b["challenge"]

    # GÜVENLİK: challenge jetonu bir OTURUM jetonu değildir → require_auth ucunda 401 olmalı
    # (aksi halde şifre-sonrası/OTP-öncesi jetonla 2FA atlatılabilirdi).
    s, b = req("/api/admin/2fa/status", "GET", token=challenge)
    check(s == 401, "challenge jetonu oturum olarak kullanılamaz (2FA atlatma engeli)")

    s, b = req("/api/admin/login", "POST", {"username": USER, "password": "WRONG"})
    check(s == 401, "yanlış şifre → 401")

    s, b = req("/api/admin/login/totp", "POST", {"challenge": challenge, "otp": _code(secret)})
    check(s == 200 and b.get("status") == "success" and b.get("token"), "challenge + kod → giriş")

    _, b2 = req("/api/admin/login", "POST", {"username": USER, "password": PW})
    s, b = req("/api/admin/login/totp", "POST", {"challenge": b2["challenge"], "otp": "000000"})
    check(s == 401, "challenge + yanlış kod → 401")

    s, b = req("/api/admin/login", "POST", {"username": USER, "password": PW, "otp": _code(secret)})
    check(s == 200 and b.get("status") == "success", "inline otp → tek adımda giriş")

    s, b = req("/api/admin/login/totp", "POST", {"challenge": "bogus.token.x", "otp": _code(secret)})
    check(s == 401, "geçersiz challenge → 401")

    s, b = req("/api/admin/2fa/disable", "POST", {"otp": "000000"}, token=token)
    check(s == 400, "yanlış kodla disable reddedildi")

    s, b = req("/api/admin/2fa/disable", "POST", {"otp": _code(secret)}, token=token)
    check(s == 200 and b.get("enabled") is False, "doğru kodla disable → kapalı")

    s, b = req("/api/admin/login", "POST", {"username": USER, "password": PW})
    check(s == 200 and b.get("status") == "success", "disable sonrası şifreyle giriş")

    print("\nTUM 2FA TESTLERI GECTI (%d kontrol)" % passed)


if __name__ == "__main__":
    main()
