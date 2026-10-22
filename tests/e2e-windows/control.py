"""POps backend ile uçtan uca testin konuştuğu yardımcı (kısa ömürlü alt komutlar). Gerçek Windows ajanına karşı
çalışan backend'e HTTP ve veritabanından dokunur; her çağrı JSON basar ve çıkış koduyla sonucu bildirir. Orkestratör
(run.ps1) bunu adım adım çağırır. Jetonlar DB'deki users satırından server.create_jwt ile üretilir (bkz.
Backend/tests/test_exam.py / test_peer_cache.py).

    python control.py <komut> [--seçenekler]

Ortam: DB_HOST/PORT/USER/PASS/NAME, JWT_SECRET, POPS_IT_URL (https://localhost:<port>), POPS_IT_CA (test CA PEM).
"""

import argparse
import asyncio
import datetime
import hashlib
import json
import os
import ssl
import sys
import time
import urllib.request

import asyncpg
import jwt as jwtlib

# asyncpg Windows'ta Selector döngüsüyle sorunsuz çalışır (kısa ömürlü bağlantılar)
if sys.platform == "win32":
    asyncio.set_event_loop_policy(asyncio.WindowsSelectorEventLoopPolicy())

URL = os.environ.get("POPS_IT_URL", "https://localhost:8443").rstrip("/")
CA = os.environ.get("POPS_IT_CA")
SUPER, ADMIN = "e2esuper", "e2eadmin"


def _ctx():
    # Test CA ile doğrula (localhost sertifikası ona zincirli); yoksa (yalnızca yerel yedek) doğrulama kapalı
    if CA and os.path.isfile(CA):
        return ssl.create_default_context(cafile=CA)
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    return ctx


def _jwt(role):
    # server.create_jwt ile aynı yük (HS256, sub/role/tv/exp); server'ı import etmeye gerek yok
    exp = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=12)
    return jwtlib.encode({"sub": SUPER if role == "superadmin" else ADMIN, "role": role, "tv": 0, "exp": exp},
                         os.environ["JWT_SECRET"], algorithm="HS256")


def api(method, path, body=None, role="superadmin", timeout=40):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(URL + path, data=data, method=method)
    req.add_header("Authorization", "Bearer " + _jwt(role))
    if data is not None:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=timeout, context=_ctx()) as r:
            return r.status, json.loads(r.read() or b"{}")
    except urllib.error.HTTPError as e:
        try:
            return e.code, json.loads(e.read() or b"{}")
        except Exception:
            return e.code, {}
    except (urllib.error.URLError, OSError) as e:
        # Backend yeniden başlıyor ya da kapalı: bekleyen komutlar yeniden dener
        return 0, {"error": str(e)[:200]}


async def _conn():
    return await asyncpg.connect(host=os.environ.get("DB_HOST", "localhost"),
                                 port=int(os.environ.get("DB_PORT", "5432")), user=os.environ["DB_USER"],
                                 password=os.environ["DB_PASS"], database=os.environ["DB_NAME"])


async def with_db(fn):
    c = await _conn()
    try:
        return await fn(c)
    finally:
        await c.close()


# ── Komutlar ──────────────────────────────────────────────────────────────────
async def cmd_setup(args):
    """users (süper admin + admin), test sınıfı ve eş önbelleği ayarını başlangıç durumuna getirir."""
    async def go(c):
        for user, role in ((SUPER, "superadmin"), (ADMIN, "admin")):
            await c.execute("INSERT INTO users (username,password_hash,role,permissions,token_version) "
                            "VALUES ($1,'x',$2,'[]',0) ON CONFLICT (username) DO UPDATE SET role=$2, token_version=0",
                            user, role)
        await c.execute("INSERT INTO custom_labs (lab_name) VALUES ($1) ON CONFLICT DO NOTHING", args.lab)
        # Eş önbelleği varsayılan kapalı başlasın (testler açar)
        await c.execute("DELETE FROM global_settings WHERE key='update_peer_cache'")
    await with_db(go)
    print(json.dumps({"ok": True}))


def cmd_enroll_token(args):
    s, r = api("POST", "/api/system/enroll-token", {"lab_name": args.lab, "max_uses": 5, "ttl_hours": 24})
    print(json.dumps({**r, "http": s}))
    return 0 if s == 200 and r.get("token") else 1


def cmd_devices(args):
    s, r = api("GET", "/api/devices")
    print(json.dumps({"http": s, "devices": r if isinstance(r, list) else r.get("devices", [])}))
    return 0 if s == 200 else 1


def cmd_wait_device(args):
    """hw_id bilinmeyen tek gerçek ajanı bekler: verilenler dışındaki ilk online cihazın hw_id'sini basar."""
    known = set(args.exclude or [])
    deadline = time.time() + args.timeout
    while time.time() < deadline:
        s, r = api("GET", "/api/devices")
        if s == 200 and isinstance(r, list):
            for d in r:
                hw = d.get("hw_id")
                ver = str(d.get("running_version") or d.get("agent_version") or "").lstrip("v")
                if hw and hw not in known and (not args.online or d.get("status") == "Online") \
                        and (not args.version or ver == args.version):
                    print(json.dumps({"ok": True, "hw_id": hw, "status": d.get("status"),
                                      "version": ver}))
                    return 0
        time.sleep(1.5)
    print(json.dumps({"ok": False, "error": "cihaz görünmedi (ya da sürüm tutmadı)"}))
    return 1


def cmd_lab_of(args):
    async def go(c):
        await c.execute("UPDATE clients SET lab_name=$2 WHERE pc_name=$1", args.hw, args.lab)
    asyncio.run(with_db(go))
    print(json.dumps({"ok": True}))


def cmd_exam_start(args):
    body = {"duration_minutes": args.minutes, "reason": "e2e sınav saati testi",
            "message": "E2E sınav modu", "allow": args.allow or []}
    s, r = api("POST", "/api/labs/%s/exam" % args.lab, body, role="admin")
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_exam_stop(args):
    s, r = api("DELETE", "/api/labs/%s/exam" % args.lab, {"reason": "e2e bitti"}, role="admin")
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s in (200, 404) else 1


def cmd_power(args):
    body = {"target_mode": "PC", "targets": [args.hw], "op": args.op, "delay": args.delay}
    s, r = api("POST", "/api/devices/power", body, role="admin")
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_tasks(args):
    s, r = api("GET", "/api/tasks")
    rows = r if isinstance(r, list) else []
    if args.id is not None:
        rows = [t for t in rows if t.get("id") == args.id]
    elif args.hw:
        rows = [t for t in rows if t.get("target_pc") == args.hw]
    print(json.dumps({"http": s, "tasks": [{"id": t.get("id"), "status": t.get("status"),
                                              "output": t.get("output"), "exit_code": t.get("exit_code")}
                                             for t in rows]}))
    return 0 if s == 200 else 1


def cmd_lockdown(args):
    s, r = api("POST", "/api/security/lockdown", {"target_pc": args.hw, "reason": "e2e karantina"}, role="admin")
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_unlock(args):
    s, r = api("POST", "/api/security/unlock", {"target_pc": args.hw, "reason": "e2e kaldır"}, role="admin")
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_peer_cache(args):
    s, r = api("POST", "/api/system/update-peer-cache", {"enabled": args.on})
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_stage_release(args):
    """releases/<dir>'daki manifest.json + .sig + MSI'ı upload-release ile stage eder (çoklu parça)."""
    import io
    import uuid
    boundary = "----e2e%s" % uuid.uuid4().hex
    parts = io.BytesIO()
    for name in ("manifest.json", "manifest.json.sig", args.msi):
        path = os.path.join(args.dir, name)
        with open(path, "rb") as f:
            blob = f.read()
        parts.write(("--%s\r\n" % boundary).encode())
        parts.write(('Content-Disposition: form-data; name="files"; filename="%s"\r\n' % name).encode())
        parts.write(b"Content-Type: application/octet-stream\r\n\r\n")
        parts.write(blob)
        parts.write(b"\r\n")
    parts.write(("--%s--\r\n" % boundary).encode())
    body = parts.getvalue()
    req = urllib.request.Request(URL + "/api/system/upload-release", data=body, method="POST")
    req.add_header("Authorization", "Bearer " + _jwt("superadmin"))
    req.add_header("Content-Type", "multipart/form-data; boundary=%s" % boundary)
    try:
        with urllib.request.urlopen(req, timeout=60, context=_ctx()) as r:
            print(json.dumps({**json.loads(r.read() or b"{}"), "http": r.status}))
            return 0
    except urllib.error.HTTPError as e:
        print(json.dumps({"http": e.code, "error": (e.read() or b"").decode("utf-8", "replace")[:500]}))
        return 1


def cmd_deploy(args):
    s, r = api("POST", "/api/system/deploy-update", {"target_mode": "PC", "targets": args.targets})
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_progress(args):
    s, r = api("POST", "/api/system/update-progress",
               {"pcs": args.targets, "version": args.version, "since": time.time() - 900})
    print(json.dumps({**({} if not isinstance(r, dict) else r), "http": s}))
    return 0 if s == 200 else 1


def cmd_seed_future(args):
    """Simüle tohumun clients.last_seen'ini geleceğe çeker (deploy'dan önce deterministik tohum seçimi)."""
    import datetime
    async def go(c):
        late = datetime.datetime(2099, 1, 1, tzinfo=datetime.timezone.utc)
        await c.execute("UPDATE clients SET last_seen=$2 WHERE pc_name=$1", args.hw, late)
    asyncio.run(with_db(go))
    print(json.dumps({"ok": True}))


def cmd_clear_pending(args):
    async def go(c):
        for pc in args.targets:
            await c.execute("DELETE FROM pending_updates WHERE pc_name=$1", pc)
            await c.execute("DELETE FROM update_results WHERE pc_name=$1", pc)
    asyncio.run(with_db(go))
    print(json.dumps({"ok": True}))


def cmd_seed_secret(args):
    async def go(c):
        await c.execute("INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1,$2) "
                        "ON CONFLICT (pc_name) DO UPDATE SET secret_hash=$2",
                        args.hw, hashlib.sha256((args.hw + "-s").encode()).hexdigest())
    asyncio.run(with_db(go))
    print(json.dumps({"ok": True}))


def main():
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)

    s = sub.add_parser("setup"); s.add_argument("--lab", required=True)
    s = sub.add_parser("enroll-token"); s.add_argument("--lab", required=True)
    sub.add_parser("devices")
    s = sub.add_parser("wait-device"); s.add_argument("--timeout", type=int, default=120)
    s.add_argument("--online", action="store_true"); s.add_argument("--exclude", nargs="*")
    s.add_argument("--version", help="yalnızca bu sürümle bağlıysa (güncelleme bitti mi)")
    s = sub.add_parser("lab-of"); s.add_argument("--hw", required=True); s.add_argument("--lab", required=True)
    s = sub.add_parser("exam-start"); s.add_argument("--lab", required=True)
    s.add_argument("--minutes", type=int, default=3)
    s.add_argument("--allow", nargs="*")
    s = sub.add_parser("exam-stop"); s.add_argument("--lab", required=True)
    s = sub.add_parser("power"); s.add_argument("--hw", required=True)
    s.add_argument("--op", required=True); s.add_argument("--delay", type=int, default=0)
    s = sub.add_parser("tasks"); s.add_argument("--hw"); s.add_argument("--id", type=int)
    s = sub.add_parser("lockdown"); s.add_argument("--hw", required=True)
    s = sub.add_parser("unlock"); s.add_argument("--hw", required=True)
    s = sub.add_parser("peer-cache"); g = s.add_mutually_exclusive_group(required=True)
    g.add_argument("--on", dest="on", action="store_true"); g.add_argument("--off", dest="on", action="store_false")
    s = sub.add_parser("stage-release"); s.add_argument("--dir", required=True); s.add_argument("--msi", required=True)
    s = sub.add_parser("deploy"); s.add_argument("--targets", nargs="+", required=True)
    s = sub.add_parser("progress"); s.add_argument("--targets", nargs="+", required=True)
    s.add_argument("--version", required=True)
    s = sub.add_parser("seed-future"); s.add_argument("--hw", required=True)
    s = sub.add_parser("clear-pending"); s.add_argument("--targets", nargs="+", required=True)
    s = sub.add_parser("seed-secret"); s.add_argument("--hw", required=True)

    args = p.parse_args()
    handlers = {
        "setup": lambda a: asyncio.run(cmd_setup(a)),
        "enroll-token": cmd_enroll_token, "devices": cmd_devices, "wait-device": cmd_wait_device,
        "lab-of": cmd_lab_of, "exam-start": cmd_exam_start, "exam-stop": cmd_exam_stop, "power": cmd_power,
        "tasks": cmd_tasks, "lockdown": cmd_lockdown, "unlock": cmd_unlock, "peer-cache": cmd_peer_cache,
        "stage-release": cmd_stage_release, "deploy": cmd_deploy, "progress": cmd_progress,
        "seed-future": cmd_seed_future, "clear-pending": cmd_clear_pending, "seed-secret": cmd_seed_secret,
    }
    # Her zaman 0 ile çık: çağıran (run.ps1) sonucu basılan JSON'dan okur. Böylece PowerShell 7.4'ün
    # yerel-komut hata davranışı beklenen "bulunamadı/200 değil" durumlarında akışı kesmez. Yalnızca yakalanmamış
    # istisna (ör. DB'ye bağlanılamadı) sıfır dışı bırakır.
    handlers[args.cmd](args)
    sys.exit(0)


if __name__ == "__main__":
    main()
