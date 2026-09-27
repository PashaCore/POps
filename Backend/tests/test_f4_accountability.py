"""JWT iptali (token_version) + hesap-verebilirlik testi (pentest F4).

- Geçerli jeton çalışır; token_version uyuşmayan (bayat) jeton reddedilir; tv iddiası OLMAYAN
  eski jeton geçiş uyumu için kabul edilir (tv=0 sayılır).
- Kullanıcı silinince jetonu anında geçersiz (satır yok).
- update_user (rol/şifre) token_version'ı artırır → o kullanıcının eldeki jetonu anında 401.
- Rol DB'den okunur (JWT rol iddiasına güvenilmez).
- flush_queue silmeden önce hash-zincirli device_audit_logs'a kayıt yazar.

Ortam: POPS_TEST_HTTP + DB_* + JWT_SECRET (uvicorn ile aynı).
"""
import asyncio
import datetime
import json
import os
import sys
import urllib.error
import urllib.request

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
import server      # noqa: E402  (create_jwt, JWT_SECRET, JWT_ALGO)
import asyncpg     # noqa: E402
import bcrypt      # noqa: E402
import jwt as pyjwt  # noqa: E402

HTTP = os.environ["POPS_TEST_HTTP"]


def req(path, method="GET", body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    r = urllib.request.Request(HTTP + path, data=data, method=method)
    if data is not None:
        r.add_header("Content-Type", "application/json")
    if token:
        r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, timeout=10) as resp:
            return resp.status
    except urllib.error.HTTPError as e:
        return e.code


def old_token_no_tv(username, role):
    exp = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)
    return pyjwt.encode({"sub": username, "role": role, "exp": exp}, server.JWT_SECRET, algorithm=server.JWT_ALGO)


async def main():
    c = await asyncpg.connect(
        host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
        user=os.environ["DB_USER"], password=os.environ["DB_PASS"], database=os.environ["DB_NAME"])
    h = bcrypt.hashpw(b"pw12345", bcrypt.gensalt()).decode()
    for u in ("f4admin", "f4target", "f4del"):
        await c.execute("DELETE FROM users WHERE username=$1", u)
    await c.execute("INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ('f4admin',$1,'superadmin','[]',0)", h)
    await c.execute("INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ('f4target',$1,'admin','[]',0)", h)
    await c.execute("INSERT INTO users (username,password_hash,role,permissions,token_version) VALUES ('f4del',$1,'admin','[]',0)", h)
    tgt_id = await c.fetchval("SELECT id FROM users WHERE username='f4target'")
    del_id = await c.fetchval("SELECT id FROM users WHERE username='f4del'")
    passed = 0

    def chk(cond, msg):
        nonlocal passed
        assert cond, "FAIL: " + msg
        passed += 1
        print("  ok:", msg)

    admin_tok = server.create_jwt("f4admin", "superadmin", 0)
    chk(req("/api/devices", token=admin_tok) == 200, "geçerli jeton (tv=0) çalışıyor")
    chk(req("/api/devices", token=server.create_jwt("f4admin", "superadmin", 99)) == 401, "bayat token_version → 401")
    chk(req("/api/devices", token=old_token_no_tv("f4admin", "superadmin")) == 200, "tv'siz eski jeton kabul (geçiş uyumu)")
    chk(req("/api/devices", token=server.create_jwt("ghost", "superadmin", 0)) == 401, "var olmayan kullanıcı → 401")

    # Rol DB'den: f4target admin jetonuyla admin ucu 200
    tgt_tok = server.create_jwt("f4target", "admin", 0)
    chk(req("/api/admin/users", token=tgt_tok) == 200, "f4target admin jetonu admin ucunda 200")
    # f4admin, f4target'ı viewer'a düşürür → token_version artar
    chk(req("/api/admin/users/%d" % tgt_id, method="PUT",
            body={"username": "f4target", "role": "viewer", "permissions": "[]"}, token=admin_tok) == 200,
        "f4target viewer'a düşürüldü")
    chk(req("/api/admin/users", token=tgt_tok) == 401, "rol düşürme sonrası f4target'ın eski jetonu → 401 (iptal)")

    # Silinen kullanıcının jetonu geçersiz
    del_tok = server.create_jwt("f4del", "admin", 0)
    chk(req("/api/devices", token=del_tok) == 200, "f4del jetonu silmeden önce 200")
    chk(req("/api/admin/users/%d" % del_id, method="DELETE", token=admin_tok) == 200, "f4del silindi")
    chk(req("/api/devices", token=del_tok) == 401, "silinen kullanıcının jetonu → 401")

    # F4(b): flush_queue hash-zincirli audit yazar
    before = await c.fetchval("SELECT COUNT(*) FROM device_audit_logs WHERE action='flush_queue'")
    chk(req("/api/flush_queue", method="POST", token=admin_tok) == 200, "flush_queue 200")
    after = await c.fetchval("SELECT COUNT(*) FROM device_audit_logs WHERE action='flush_queue'")
    chk(after == before + 1, "F4(b): flush_queue device_audit_logs'a (hash-zincirli) kayıt yazdı")

    for u in ("f4admin", "f4target", "f4del"):
        await c.execute("DELETE FROM users WHERE username=$1", u)
    await c.close()
    print("\nTUM F4 TESTLERI GECTI (%d kontrol)" % passed)


if __name__ == "__main__":
    asyncio.get_event_loop().run_until_complete(main())
