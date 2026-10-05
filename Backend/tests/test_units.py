"""Sunucu gerektirmeyen birim testleri (CI 'backend' job'ı).

Veritabanı ya da çalışan sunucu gerekmez: python Backend/tests/test_units.py
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
# Router modülleri yapılandırmayı içe aktarır; veritabanına bağlanılmaz, değerler yalnızca doğrulamayı geçer
for _k in ("JWT_SECRET", "DB_USER", "DB_PASS", "DB_NAME"):
    os.environ.setdefault(_k, "unit-test")
import asyncio  # noqa: E402
import json  # noqa: E402
import logging  # noqa: E402
import threading  # noqa: E402
import time  # noqa: E402

import datetime  # noqa: E402

from pops import agent_health, bypass, logs, update_notice, update_tracking  # noqa: E402
from pops.routers import activity  # noqa: E402

FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def test_update_notice():
    print("== update_notice")
    # 27 Eylül tatbikatlarının gerçek sonucu: geri kurulum servisi bırakmadı, son çare kurtardı
    real = {
        "status": "rollback_failed", "from_version": "0.1.5-alpha", "to_version": "0.1.6-alpha",
        "rollback": "msi", "agent_state": "reinstalled", "msi_exit_code": 0, "running_version": "0.1.5-alpha",
    }
    ev, sev, title = update_notice.describe(real)
    chk(ev == "update_problem" and sev == "critical", "son çareyle kurtarılan geri dönüş hâlâ kritik")
    chk("son çare" in title and "0.1.5-alpha" in title, "başlık son çareyi ve çalışan sürümü söylüyor: %s" % title)
    chk("reinstalled" not in title, "başlıkta ajan terimi yok")
    chk(update_notice.is_critical(real), "denetim kaydı kritik")

    ev, sev, title = update_notice.describe(dict(real, agent_state="unmanaged", running_version=None))
    chk(sev == "critical" and "elle kurulum" in title, "yönetimsiz cihaz elle kurulum ister")

    ok = {"status": "rolled_back", "from_version": "0.1.7-alpha", "to_version": "0.1.8-alpha", "rollback": "msi",
          "agent_state": "running", "running_version": "0.1.7-alpha"}
    ev, sev, title = update_notice.describe(ok)
    chk(ev == "update_rolled_back" and sev == "high", "başarılı geri dönüş kritik değil")
    chk("0.1.7-alpha" in title and "onarım" not in title, "başlık çalışan sürümü söylüyor: %s" % title)
    chk(not update_notice.is_critical(ok), "başarılı geri dönüş kritik denetim kaydı üretmez")
    _, _, title = update_notice.describe(dict(ok, rollback="msi_repair"))
    chk("onarımla" in title, "onarımla geri dönüş ayrı yazılıyor")

    chk(update_notice.describe({"status": "install_failed"})[0] == "update_not_started", "install_failed")
    chk(update_notice.describe({"status": "pending_reboot"})[1] == "medium", "pending_reboot orta")
    chk(update_notice.describe({"status": "rollback_pending_reboot", "to_version": "0.1.8"})[1] == "high",
        "rollback_pending_reboot artık bildiriliyor")
    chk(update_notice.describe({"status": "success", "to_version": "0.1.8", "running_version": "0.1.8"})
        == ("update_ok", "info", "Ajan güncellendi: 0.1.8"), "başarı")
    chk(update_notice.describe({"status": "something-new"}) is None, "bilinmeyen durum bildirim üretmez")
    rejected = {"status": "rejected", "to_version": "0.1.22-alpha", "detail": "manifest imzası geçersiz"}
    chk(update_notice.describe(rejected) == ("update_problem", "critical", "Ajan 0.1.22-alpha güncellemesini reddetti")
        and update_notice.is_critical(rejected), "ret kritik, başlık sürümü söylüyor")


def test_update_progress():
    print("== güncelleme adımı (update_progress)")
    clean = update_tracking.clean_progress
    for stage in update_tracking.STAGES:
        chk((clean({"type": "update_progress", "stage": stage}) or {}).get("stage") == stage, "adım: %s" % stage)
    chk(clean({"stage": "teleporting"}) is None and clean({"stage": ["received"]}) is None and clean({}) is None
        and clean("received") is None, "bilinmeyen ya da bozuk adım yok sayılır")
    got = clean({"stage": "waiting_installer", "to_version": "0.1.22-alpha", "attempt": 2, "of": 5})
    chk(got == {"stage": "waiting_installer", "to_version": "0.1.22-alpha", "attempt": 2, "of": 5, "detail": None},
        "deneme sayısı ve sürüm saklanır: %s" % got)
    got = clean({"stage": "waiting_installer", "attempt": True, "of": 5})
    chk(got["attempt"] is None and got["of"] == 5, "bool deneme sayısı değildir")
    got = clean({"stage": "waiting_installer", "attempt": 6, "of": 5})
    chk(got["attempt"] is None and got["of"] is None, "deneme sınırı aşılamaz")
    chk(clean({"stage": "installing", "attempt": 0, "of": 101})["of"] is None, "sayılar 1–100")
    got = clean({"stage": "rejected", "to_version": "<b>x</b>", "detail": "imza\x00 geçersiz\r\n\u202esahte\t "})
    chk(got["to_version"] is None and got["detail"] == "imza geçersiz sahte",
        "kontrol ve yön karakterleri silinir, bozuk sürüm atılır: %r" % got["detail"])
    chk(len(clean({"stage": "rejected", "detail": "ç" * 1000})["detail"]) == update_tracking.DETAIL_MAX == 300,
        "ayrıntı en çok 300 karakter")
    chk(clean({"stage": "installing", "detail": 42})["detail"] is None
        and clean({"stage": "installing", "detail": " \x01 "})["detail"] is None, "metin olmayan ya da boş ayrıntı yok")
    chk(update_tracking.same_version("v0.1.22-Alpha", "0.1.22-alpha")
        and not update_tracking.same_version("0.1.2", "0.1.22"),
        "sürüm karşılaştırması baştaki v'yi ve büyük harfi saymaz")


def test_log_format():
    print("== log biçimi")
    rec = logging.LogRecord("uvicorn.error", logging.INFO, __file__, 1, "connection open", None, None)
    rec.websocket = object()
    rec.pc_name = "HW-1"
    out = json.loads(logs.JsonFormatter().format(rec))
    chk(out["msg"] == "connection open" and out["level"] == "INFO", "temel alanlar")
    chk(out.get("pc_name") == "HW-1", "düz ek alan yazılır")
    chk("websocket" not in out, "nesne ek alanı yazılmaz")


class _StuckStream:
    """Diski takılmış bir log hedefi gibi: kapı açılana kadar write bekler."""

    def __init__(self):
        self.gate = threading.Event()
        self.lines = []

    def write(self, text):
        self.gate.wait()
        self.lines.append(text)

    def flush(self):
        pass


def test_log_writer():
    print("== log yazımı olay döngüsünü bekletmez")
    out = _StuckStream()
    handler = logs.BackgroundStreamHandler(out, logs.JsonFormatter(), maxsize=3)
    lg = logging.getLogger("pops.test.writer")
    lg.propagate = False
    lg.setLevel(logging.INFO)
    lg.addHandler(handler)

    async def run():
        ticks = [0]

        async def ticker():
            while True:
                await asyncio.sleep(0.01)
                ticks[0] += 1

        tick_task = asyncio.create_task(ticker())
        token = logs.request_id_var.set("rid-unit-1")
        started = time.monotonic()
        for i in range(6):
            lg.info("satır %d", i, extra={"pc_name": "HW-1"})
            await asyncio.sleep(0.02)
        elapsed = time.monotonic() - started
        logs.request_id_var.reset(token)
        tick_task.cancel()
        return elapsed, ticks[0]

    # asyncio.run değil: 3.9'da ana iş parçacığının döngüsünü kaldırır, sonraki testlerin içe aktardığı modüller
    # (asyncio.Lock()) hata verir
    loop = asyncio.new_event_loop()
    try:
        elapsed, ticks = loop.run_until_complete(run())
    finally:
        loop.close()
    chk(elapsed < 1.0 and ticks >= 5, "hedef takılıyken log çağrısı beklemedi, döngü işledi (%.2f sn)" % elapsed)
    chk(handler.dropped >= 1 and not out.lines, "sıra dolunca satır atıldı, takılı hedefe yazılmadı")
    out.gate.set()
    handler.stop()
    lg.info("kapanışta")  # stop() sonrası doğrudan yazılır (uvicorn SIGTERM'de süreci atexit'siz bitirir)
    lg.removeHandler(handler)
    rows = [json.loads(line) for line in out.lines]
    chk(rows[-1]["msg"] == "kapanışta", "durdurulunca sıradakiler yazıldı, sonraki satır doğrudan yazıldı")
    rows = rows[:-1]
    kept = [r for r in rows if r["logger"] == "pops.test.writer"]
    note = next((r for r in rows if r["logger"] == "pops.logs"), None)
    chk(all(line.endswith("\n") for line in out.lines) and len(kept) + handler.dropped == 6, "kalan satırlar yazıldı")
    chk([r["msg"] for r in kept] == sorted(r["msg"] for r in kept) and kept[0]["msg"] == "satır 0", "sıra korundu")
    chk(all(r.get("request_id") == "rid-unit-1" and r.get("pc_name") == "HW-1" for r in kept),
        "request_id ve ek alanlar çağıranın bağlamından")
    chk(note is not None and note.get("dropped") == handler.dropped, "atılan satır sayısı bildirildi")

    stuck = _StuckStream()
    late = logs.BackgroundStreamHandler(stuck, logs.JsonFormatter())
    late.handle(logging.LogRecord("x", logging.INFO, __file__, 1, "takılı", None, None))
    started = time.monotonic()
    late.stop(timeout=0.2)
    chk(time.monotonic() - started < 1.0, "takılı yazıcı kapanışı asılı bırakmaz")
    stuck.gate.set()


def test_activity():
    print("== etkinlik geçmişi")
    dev = "HW-A"
    items = activity.build_items(
        dev,
        [{"start_time": "2026-09-27 19:22:38", "end_time": "2026-09-27 19:22:42", "admin_name": "Pasha",
          "reason": "sınav", "is_mandatory": False}],
        [{"action": "lockdown", "timestamp": "2026-09-27 20:08:48", "changes": '{"admin": "Pasha", "reason": "s"}'},
         {"action": "update_result", "timestamp": "2026-09-29 20:31:29",
          "changes": '{"status": "rolled_back", "to_version": "0.1.8-alpha", "running_version": "0.1.7-alpha"}'},
         {"action": "NEW_DEVICE", "timestamp": "2026-09-20 10:00:00", "changes": '{"bios_sn": "GIZLI-SERI"}'},
         {"action": "update_result", "timestamp": "2026-09-19 10:00:00",
          "changes": '{"status": "rejected", "to_version": "0.1.22-alpha", "detail": "imza geçersiz"}'},
         {"action": "set_capabilities", "timestamp": "2026-09-21 10:00:00",
          "changes": '{"vision_enabled": false, "by": "Pasha"}'}],
        [{"action": "install_updates", "timestamp": "2026-09-27 18:00:05",
          "changes": '{"by": "Pasha", "dispatched": ["HW-A"]}'},
         {"action": "install_updates", "timestamp": "2026-09-27 18:00:06",
          "changes": '{"by": "Pasha", "dispatched": ["HW-AB"]}'}],
        [{"created_at": "2026-09-26 19:44:40", "created_by": "Pasha", "status": "Completed"}],
    )
    chk([i["at"] for i in items] == sorted([i["at"] for i in items], reverse=True), "en yeni başta")
    chk(len(items) == 8, "başka cihaza gönderilen işlem dahil değil (%d kayıt)" % len(items))
    chk(items[0]["title"] == "0.1.8-alpha güncellemesi geri alındı, 0.1.7-alpha çalışıyor", "güncelleme başlığı")
    chk("GIZLI-SERI" not in json.dumps(items, ensure_ascii=False), "donanım seri numarası sızmıyor")
    kinds = {i["kind"] for i in items}
    chk(kinds == {"remote_session", "quarantine", "update", "enroll", "capability", "windows_update", "command"},
        "tüm türler: %s" % sorted(kinds))
    cap = [i for i in items if i["kind"] == "capability"][0]
    chk(cap["title"] == "Uzaktan izleme kapatıldı" and cap["actor"] == "Pasha", "yetenek değişikliği ve yapan")
    cmd = [i for i in items if i["kind"] == "command"][0]
    chk(cmd["detail"] == "Durum: Completed" and cmd["actor"] == "Pasha", "komut içeriği yok, yapan var")
    rej = items[-1]
    chk(rej["title"] == "Ajan 0.1.22-alpha güncellemesini reddetti" and rej["detail"] == "imza geçersiz",
        "reddedilen güncelleme sebebiyle")


def test_bypass():
    print("== bypass")
    # Ajanla ortak vektör (POps.Tests OfflineBypassTests): anahtar 32 x 0x01, HW-TEST, 2026-10-01
    import base64
    key = base64.urlsafe_b64encode(bytes([1] * 32)).rstrip(b"=").decode()
    chk(bypass.device_code(key, "HW-TEST", datetime.date(2026, 10, 1)) == "BA258E", "cihaz kodu test vektörü")
    chk(bypass.fingerprint(key) == "72cd6e8422c407fb", "parmak izi test vektörü")
    # Günün sonraki kodları (n=1..9): "HW-TEST|2026-10-01|n"; ajan testleriyle ortak
    chk(bypass.device_code(key, "HW-TEST", datetime.date(2026, 10, 1), 1) == "D31B3C", "günün 2. kodu (n=1)")
    chk(bypass.device_code(key, "HW-TEST", datetime.date(2026, 10, 1), 9) == "ACA17F", "günün 10. kodu (n=9)")
    fresh = bypass.new_key()
    chk(len(fresh) == 43 and "=" not in fresh and len(base64.urlsafe_b64decode(fresh + "=")) == 32,
        "yeni anahtar 32 bayt base64url (dolgusuz)")
    chk(bypass.supports_device_key("0.1.12-alpha") and bypass.supports_device_key("v0.2.0"), "0.1.12+ destekler")
    chk(not bypass.supports_device_key("0.1.11-alpha") and not bypass.supports_device_key("test")
        and not bypass.supports_device_key(None), "eski/bilinmeyen sürüme gönderilmez")


def test_hardening():
    print("== 0.1.13 sağlamlaştırma")
    import asyncio
    from pops import agent_auth, agent_version, dna
    from pops.routers import agents as agents_router
    # Donanım kimliği: okunamayan/üretici varsayılanı değerler puan kazandırmaz (F04)
    blank = {"uuid": "NULL", "bios_sn": "Default string", "disk_sn": "", "mac": "00:00:00:00:00:00", "ram_sn": "NULL"}
    rec = {"dna_uuid": "NULL", "dna_bios": "Default string", "dna_disk": "", "dna_mac": "00:00:00:00:00:00",
           "dna_ram": "NULL"}
    chk(dna.calculate_dna_score(blank, rec, {}, {}) == (0, 0), "boş/varsayılan donanım değerleri kanıt sayılmaz")
    real = {"uuid": "U-1", "bios_sn": "B-1", "disk_sn": "D-1", "mac": "AA:BB:CC:DD:EE:01", "ram_sn": "R-1"}
    same = {"dna_uuid": "u-1", "dna_bios": "B-1", "dna_disk": "D-1", "dna_mac": "aa:bb:cc:dd:ee:01", "dna_ram": "R-1"}
    chk(dna.calculate_dna_score(real, same, {}, {}) == (11, 11), "aynı donanım tam puan (büyük/küçük harf duyarsız)")
    # Yeniden başlatma komutu tanıma (görev akıbeti)
    chk(agents_router._is_reboot_command("shutdown /r /t 15") and agents_router._is_reboot_command("Restart-Computer"),
        "yeniden başlatma komutu tanınır")
    chk(not agents_router._is_reboot_command("shutdown /s") and not agents_router._is_reboot_command("echo /r"),
        "kapatma ya da ilgisiz komut yeniden başlatma sayılmaz")
    chk(agent_version.at_least("0.1.13-alpha", (0, 1, 13)) and not agent_version.at_least("0.1.12", (0, 1, 13))
        and agent_version.parse("x" * 10000) is None, "sürüm karşılaştırma (uzun girdide de)")
    # Kimlik zorlaması ayarı okunamazsa kapı kapalı (R-03)

    async def broken(*a, **k):
        raise RuntimeError("db down")
    original = agent_auth.execute_query
    agent_auth.execute_query = broken
    try:
        chk(asyncio.run(agent_auth.enforce_agent_auth_enabled()) is True, "ayar okunamazsa zorlama açık sayılır")
    finally:
        agent_auth.execute_query = original
    chk(agent_auth.hash_enroll_token("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
        "kayıt jetonu SHA-256 ile saklanır")


def test_agent_health():
    print("== agent_health")
    chk(agent_health.clean(None) is None and agent_health.clean("x") is None, "blok yoksa NULL")
    out = json.loads(agent_health.clean({
        "started_at": 1700000000, "last_policy_sync": -5, "last_inventory_upload": True, "tray_connected": "evet",
        "vision_channel": "connected", "loop_errors_1h": 2, "last_error": "ğ" * 300, "extra": 1,
        "screen_locked": True, "network_isolated": False, "isolation_error": "x" * 300}))
    chk(out["screen_locked"] is True and out["network_isolated"] is False and len(out["isolation_error"]) == 200,
        "karantina kilit/yalıtım durumu ayrı saklanır")
    chk(out["started_at"] == 1700000000 and out["last_policy_sync"] is None and out["last_inventory_upload"] is None,
        "zaman alanları: negatif ve bool atıldı")
    chk(out["tray_connected"] is None and out["vision_channel"] == "connected", "tür denetimi")
    chk(len(out["last_error"]) == 200 and "extra" not in out, "metin kırpıldı, bilinmeyen alan yok")
    chk(agent_health.parse('{"a": 1}') == {"a": 1} and agent_health.parse("bozuk") is None, "okuma")


def test_p1():
    """0.1.14: veritabanı ya da sunucu gerektirmeyen parçalar."""
    import asyncio
    import time

    from cryptography import x509
    from cryptography.fernet import Fernet
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID

    from pops import health_alerts, manager as manager_mod, metrics, notify, secretbox, security
    from pops.models import RemoteInputData
    from pops.routers import agents, control

    print("== secretbox (R-12)")
    sealed = secretbox.seal("JBSWY3DPEHPK3PXP")
    chk(sealed.startswith("v1:") and "JBSWY3DP" not in sealed, "şifreli saklanır")
    chk(secretbox.unseal(sealed) == "JBSWY3DPEHPK3PXP", "geri açılır")
    chk(secretbox.unseal("JBSWY3DPEHPK3PXP") == "JBSWY3DPEHPK3PXP", "eski düz metin okunur")
    chk(secretbox.needs_reseal("JBSWY3DPEHPK3PXP") and not secretbox.needs_reseal(sealed), "düz metin yeniden yazılır")
    chk(secretbox.unseal("v1:bozuk") is None, "çözülemeyen değer None")
    # Sonradan TOTP_ENCRYPTION_KEY eklenince eski (türetilmiş) anahtarla şifreli değer okunur ve yenilenir
    old_primary, old_box = secretbox._primary, secretbox._box
    os.environ["TOTP_ENCRYPTION_KEY"] = Fernet.generate_key().decode()
    try:
        secretbox._primary, secretbox._box = secretbox._build()
        chk(secretbox.unseal(sealed) == "JBSWY3DPEHPK3PXP", "yeni anahtar eklenince eski değer okunur")
        chk(secretbox.needs_reseal(sealed), "eski anahtarla şifreli değer yeniden şifrelenir")
        os.environ["TOTP_ENCRYPTION_KEY"] = "kisa"
        try:
            secretbox._build()
            chk(False, "geçersiz anahtar reddedilir")
        except RuntimeError:
            chk(True, "geçersiz anahtar reddedilir")
    finally:
        os.environ.pop("TOTP_ENCRYPTION_KEY", None)
        secretbox._primary, secretbox._box = old_primary, old_box

    print("== TOTP adımı")
    secret = security._totp_new_secret()
    now = int(time.time() // 30)
    chk(security.totp_step(secret, security._totp_code(secret, now - 1)) == now - 1, "eşleşen adım döner")
    chk(security.totp_step(secret, security._totp_code(secret, now + 3)) is None, "pencere dışı kod reddedilir")
    chk(security.totp_step(secret, "12345") is None and security.totp_step(None, "123456") is None, "biçim")
    ch = security.create_totp_challenge("ali", 7)
    chk(security.verify_totp_challenge(ch) == ("ali", 7), "challenge oturum sürümünü taşır")
    chk(security.verify_jwt(ch) is None, "challenge oturum jetonu değil")

    print("== uzaktan girdi (F13) ve kopma sebebi")
    flat = control._flat_remote_input(RemoteInputData(device="HW-1", input_type="keyboard", key="ş", is_down=True,
                                                      action="execute", code="KeyS"))
    chk(flat.get("key") == "ş" and flat.get("code") == "KeyS" and flat.get("is_down") is True, "düz alanlar geçer")
    chk("action" not in flat and flat["type"] == "remote_input" and flat["device"] == "HW-1", "başka alan geçmez")
    legacy = control._flat_remote_input(RemoteInputData(device="HW-1", input_type="mouse_move", data={"x": 5, "y": 6}))
    chk(legacy.get("x") == 5 and legacy.get("y") == 6 and "data" not in legacy, "eski 'data' biçimi düzleşir")
    chk(agents._close_reason(1006, "").startswith("bağlantı koptu"), "1006 = bağlantı koptu")
    chk(agents._close_reason(4000, "x" * 500).endswith("x" * 120), "bilinmeyen kod + kırpılmış sebep")

    print("== ölçümler")
    row = [0] * (len(metrics.SEND_BUCKETS) + 1) + [0.0]
    for v in (0.0005, 0.0005, 0.002, 0.2):
        metrics._observe_into(row, metrics.SEND_BUCKETS, v)
    chk(metrics.quantile(row, metrics.SEND_BUCKETS, 0.5) == 0.001, "medyan kovası")
    chk(metrics.quantile(row, metrics.SEND_BUCKETS, 0.95) == 0.5, "p95 kovası")
    metrics._observe_into(row, metrics.SEND_BUCKETS, 99)
    chk(metrics.quantile(row, metrics.SEND_BUCKETS, 0.99) == float("inf"), "en büyük kovayı aşan gözlem")

    print("== disk ve sertifika uyarısı")
    real_usage = health_alerts.shutil.disk_usage
    try:
        Usage = type("U", (), {})

        def fake(total, free):
            u = Usage()
            u.total, u.free, u.used = total, free, total - free
            return lambda _p: u

        health_alerts.shutil.disk_usage = fake(100 * 1024**3, 50 * 1024**3)
        chk(all(d["level"] == "ok" for d in health_alerts.disk_status()), "yarısı boş: sorun yok")
        health_alerts.shutil.disk_usage = fake(100 * 1024**3, 8 * 1024**3)
        chk(health_alerts.disk_status()[0]["level"] == "high", "%8 boş: uyarı")
        health_alerts.shutil.disk_usage = fake(100 * 1024**3, int(0.5 * 1024**3))
        chk(health_alerts.disk_status()[0]["level"] == "critical", "yarım GB: kritik")
    finally:
        health_alerts.shutil.disk_usage = real_usage
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "pops-test")])
    now_dt = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(1).not_valid_before(now_dt - datetime.timedelta(days=1))
            .not_valid_after(now_dt + datetime.timedelta(days=5)).sign(key, hashes.SHA256()))
    import tempfile
    with tempfile.NamedTemporaryFile("wb", suffix=".pem", delete=False) as f:
        f.write(cert.public_bytes(serialization.Encoding.PEM))
    os.environ["TLS_CERT_FILES"] = f.name
    os.environ["TLS_CHECK_URL"] = "http://yok.example"   # https değil: bağlanılmaz
    try:
        status = health_alerts.tls_status()
        chk(len(status) == 1 and status[0]["level"] == "critical" and 4 < status[0]["days_left"] < 5.1,
            "5 gün kalan sertifika kritik")
    finally:
        os.unlink(f.name)
        os.environ.pop("TLS_CERT_FILES", None)
        os.environ.pop("TLS_CHECK_URL", None)

    async def panel_queue():
        class SlowWs:
            def __init__(self):
                self.sent = []
                self.gate = asyncio.Event()

            async def send_text(self, text):
                await self.gate.wait()
                self.sent.append(text)

            async def close(self, code=1000):
                pass

        m = manager_mod.ConnectionManager()
        ws = SlowWs()
        m.active_panels.append(ws)
        m.panel_roles[ws] = "admin"
        m.panel_senders[ws] = manager_mod._PanelSender(ws, m._drop_slow_panel)
        await m.broadcast_to_panels({"type": "a"})
        await asyncio.sleep(0.01)   # yazıcı ilk mesajda bekliyor
        for i in range(5):
            await m.broadcast_to_admin_panels({"type": "thumbnail", "hw_id": "HW-1", "n": i})
        await m.broadcast_to_panels({"type": "b"})
        ws.gate.set()
        await asyncio.sleep(0.05)
        kinds = [json.loads(t).get("type") + str(json.loads(t).get("n", "")) for t in ws.sent]
        chk(kinds == ["a", "b", "thumbnail4"], "yavaş panelde yalnızca en son kare kalır: %s" % kinds)
        ws.gate.clear()
        for i in range(manager_mod._PANEL_QUEUE_MAX + 2):
            await m.broadcast_to_panels({"type": "x"})
        chk(ws not in m.active_panels, "sırası taşan panel kapatılır")
        await asyncio.sleep(0)

    async def notify_retry():
        calls = []
        real_exec, real_sched = notify.execute_query, notify._schedule_retry

        async def broken(*a, **k):
            raise ConnectionError("db yok")

        notify.execute_query = broken
        notify._schedule_retry = lambda attempt, args: calls.append((attempt, args[0]))
        try:
            await notify.notify("unit_evt", "high", "Başlık-retry")
            chk(("unit_evt", None, "Başlık-retry") not in notify._recent, "kaydedilemeyen olay süzgeçte kalmaz (F15)")
            chk(calls == [(0, "unit_evt")], "yeniden deneme planlandı")
        finally:
            notify.execute_query, notify._schedule_retry = real_exec, real_sched

    async def heartbeat_race():
        from pops import heartbeats

        calls = []
        real_exec, real_agents = heartbeats.execute_query, dict(heartbeats.manager.active_agents)
        heartbeats.manager.active_agents.clear()
        heartbeats.manager.active_agents["HW-RACE"] = object()

        async def fake(query, params=(), fetch=False):
            calls.append((query, params))
            if len(calls) == 1:   # toplu yazım sürerken cihaz kopar
                heartbeats.manager.active_agents.pop("HW-RACE", None)
            return True

        heartbeats.execute_query = fake
        try:
            heartbeats.record("HW-RACE", "2026-10-02 12:00:00", "Online", "-", "h", "1.2.3.4", None)
            await heartbeats.flush()
            chk(len(calls) == 2 and "Offline" in calls[1][0] and calls[1][1] == (["HW-RACE"],),
                "yazım sürerken kopan cihaz yeniden Offline yapıldı")
        finally:
            heartbeats.execute_query = real_exec
            heartbeats.manager.active_agents.clear()
            heartbeats.manager.active_agents.update(real_agents)

    async def dedupe_waits_for_outcome():
        from fastapi import HTTPException
        from pops.models import OrchestrationInput
        from pops.routers import tasks as tasks_router

        real_resolve = tasks_router.resolve_targets
        gate = asyncio.Event()

        async def slow_fail(mode, targets):
            await gate.wait()
            raise RuntimeError("veritabanı yok")

        tasks_router.resolve_targets = slow_fail
        data = OrchestrationInput(target_mode="PC", targets=["HW-X"],
                                  taskSequence=[{"name": "n", "type": "CMD", "command": "echo dedupe"}])
        try:
            first = asyncio.ensure_future(tasks_router.deploy_orchestration(data, {"sub": "u"}))
            await asyncio.sleep(0)
            second = asyncio.ensure_future(tasks_router.deploy_orchestration(data, {"sub": "u"}))
            await asyncio.sleep(0)
            gate.set()
            r1, r2 = await asyncio.gather(first, second, return_exceptions=True)
            chk(isinstance(r1, RuntimeError), "ilk istek başarısız")
            chk(isinstance(r2, HTTPException) and r2.status_code == 409, "ikinci istek yanlışlıkla 'başarılı' dönmedi")
        finally:
            tasks_router.resolve_targets = real_resolve
            tasks_router._recent_orchestrations.clear()

    async def retention_date_after_success():
        from pops import retention

        written = []
        real_exec, real_apply = retention.execute_query, retention.apply

        async def fake_exec(query, params=(), fetch=False):
            if "retention_run_date" in query and query.lstrip().startswith("INSERT"):
                written.append(params)
            return [] if fetch else True

        async def failing_apply():
            raise RuntimeError("silme yarıda kaldı")

        retention.execute_query, retention.apply = fake_exec, failing_apply
        retention._last_attempt[0] = -3600.0
        try:
            try:
                await retention.apply_daily()
            except RuntimeError:
                pass
            chk(written == [], "başarısız tur günün tarihini yazmadı (aynı gün yeniden denenir)")
        finally:
            retention.execute_query, retention.apply = real_exec, real_apply
            retention._last_attempt[0] = -3600.0

    loop = asyncio.new_event_loop()
    try:
        loop.run_until_complete(panel_queue())
        loop.run_until_complete(notify_retry())
        loop.run_until_complete(heartbeat_race())
        loop.run_until_complete(dedupe_waits_for_outcome())
        loop.run_until_complete(retention_date_after_success())
    finally:
        loop.close()


def test_review4():
    """Denetim zinciri parça parça doğrulanır (B6); yükleme tek geçişte yazılır ve özetlenir (B10)."""
    import asyncio
    import hashlib
    import io
    import tempfile

    from pops import auditchain
    from pops.routers import tasks as tasks_router

    rows, prev = [], None
    for i in range(1, 12):
        r = {"id": i * 2, "hw_id": "HW-U", "action": "a%d" % i, "reason": "r", "changes": "{}", "timestamp": "t%d" % i,
             "prev_hash": prev}
        r["entry_hash"] = (
            None if i == 3 else auditchain.entry_hash(prev, "HW-U", r["action"], "r", "{}", r["timestamp"])
        )
        prev = r["entry_hash"] or prev
        rows.append(r)

    def fetcher(data, calls):
        async def fetch(sql, last_id, limit):
            calls.append((last_id, limit))
            return [r for r in data if r["id"] > last_id][:limit]
        return fetch

    calls = []
    got = asyncio.run(auditchain.verify_batched(fetcher(rows, calls), batch_size=4))
    chk(got == auditchain.verify(rows) and got["ok"] and got["checked"] == 10 and got["total"] == 11,
        "parça parça doğrulama tek seferlikle aynı (%s)" % got)
    chk([c[0] for c in calls] == [0, 8, 16], "id'ye göre ilerledi (%s)" % calls)
    tampered = [dict(r) for r in rows]
    tampered[6]["reason"] = "değişti"
    got = asyncio.run(auditchain.verify_batched(fetcher(tampered, []), batch_size=4))
    chk(not got["ok"] and got["first_broken_id"] == 14, "kurcalanan kayıt bulundu (%s)" % got)
    chk(asyncio.run(auditchain.verify_batched(fetcher([], []), batch_size=4)) == {"ok": True, "checked": 0, "total": 0},
        "boş tablo")

    with tempfile.TemporaryDirectory() as d:
        data = os.urandom(3 * 1024 * 1024 + 5)
        dest = os.path.join(d, "paket.bin")
        digest = tasks_router._store_upload(io.BytesIO(data), dest)
        with open(dest, "rb") as f:
            chk(f.read() == data and digest == hashlib.sha256(data).hexdigest(), "yükleme yazıldı ve özetlendi")
        chk(os.listdir(d) == ["paket.bin"], "geçici dosya kalmadı")

        class Broken(io.BytesIO):
            def read(self, *a):
                raise OSError("bağlantı koptu")

        try:
            tasks_router._store_upload(Broken(), os.path.join(d, "yarim.bin"))
            chk(False, "yarıda kalan yükleme hata verdi")
        except OSError:
            chk(os.listdir(d) == ["paket.bin"], "yarıda kalan yükleme iz bırakmadı")


def test_modules():
    """Modül kararı: laboratuvar istisnası > kurum ayarı > açık; bağımlılıklar; profiller tam."""
    import asyncio
    import time

    from pops import modules

    def setting(org, lab):
        modules._cache.update(at=time.monotonic() + 3600, org=org, lab=lab)

    async def run():
        setting({}, {})
        chk(all([await modules.enabled(m.id) for m in modules.MODULES]), "ayar yoksa hepsi açık (mevcut kurulum)")
        chk(await modules.enabled("cihazlar"), "modül olmayan özellik (çekirdek) her zaman açık")
        setting({"vision": False}, {("vision", "Lab-1"): True})
        chk(not await modules.enabled("vision") and await modules.enabled("vision", "Lab-1")
            and not await modules.enabled("vision", "Lab-2"), "laboratuvar istisnası kurum ayarını ezer")
        chk(await modules.enabled_anywhere("vision"), "bir laboratuvarda açıksa 'herhangi bir yerde açık'")
        setting({"vision": True}, {("vision", "Lab-2"): False})
        chk(await modules.enabled("vision", "Lab-1") and not await modules.enabled("vision", "Lab-2"),
            "kurumda açık, tek laboratuvarda kapalı")
        setting({"terminal": False}, {})
        chk(not await modules.enabled("deploy") and not await modules.enabled("schedules"),
            "uzak komut kapalıyken dağıtım (depends) ve zamanlanmış görevler (depends_any) de kapalı")
        chk(not await modules.enabled_anywhere("deploy"), "hiçbir yerde açık değil")
        setting({"terminal": False}, {("terminal", "Lab-1"): True})
        chk(await modules.enabled("deploy", "Lab-1") and await modules.enabled_anywhere("deploy"),
            "bağımlılık laboratuvarda açılınca bağımlı modül de orada açık")
        setting({"software": False}, {})
        chk(not await modules.enabled("licenses"), "lisanslar yazılım envanteri olmadan kapalı")
        try:
            setting({"vision": False}, {})
            await modules.check("vision", lab="Lab-9")
            chk(False, "kapalı modül 409")
        except Exception as exc:
            chk(getattr(exc, "status_code", None) == 409 and exc.headers.get("X-POps-Module") == "vision",
                "kapalı modül 409 + X-POps-Module")
        modules.invalidate()

    asyncio.run(run())
    ids = {m.id for m in modules.MODULES}
    chk(all(set(v) == ids for v in modules.PROFILES.values()), "her profil her modüle karar veriyor")
    chk(all(d in ids for m in modules.MODULES for d in m.depends + m.depends_any), "bağımlılıklar tanımlı modüller")
    chk(not modules.PROFILES["org"]["vision"] and modules.PROFILES["school"]["vision"]
        and modules.PROFILES["org"]["helpdesk"] and not modules.PROFILES["school"]["helpdesk"],
        "profil varsayılanları tasarımdaki gibi")


def test_release_compare():
    """Yalnızca daha yeni sürüm "güncelleme var" sayılır (GitHub'da henüz yayımlanmamış sürüm çalışırken eskisi
    önerilmesin)."""
    import system_routes

    newer = system_routes._newer
    chk(newer("v0.1.15-alpha", "0.1.14-alpha") and not newer("v0.1.14-alpha", "0.1.15-alpha"), "yeni / eski")
    chk(not newer("v0.1.15-alpha", "0.1.15-alpha") and not newer(None, "0.1.15-alpha"), "aynı sürüm ya da sürüm yok")
    chk(newer("v0.1.10-alpha", "0.1.9-alpha"), "sayısal karşılaştırma (0.1.10 > 0.1.9)")
    chk(newer("v0.1.15-alpha", None), "çalışan sürüm bilinmiyorsa öneri var")

    # Önbellekteki "son sürüm" çalışan sürümden eskiyse (GitHub yeni sürümü geç gösterdi) yeniden sorulur
    calls = []
    real_fetch, real_read = system_routes._fetch_github_latest_tag, system_routes._read_version
    real_cache = dict(system_routes._latest_cache)
    try:
        system_routes._read_version = lambda: "0.1.19-alpha"
        system_routes._fetch_github_latest_tag = lambda: calls.append(1) or "v0.1.19-alpha"
        now = time.time()
        system_routes._latest_cache.update({"tag": "v0.1.18-alpha", "at": now - 300, "checked": True})
        got = asyncio.run(system_routes._github_latest())
        chk(got == "v0.1.19-alpha" and len(calls) == 1, "eski önbellek yenilendi (%s)" % got)
        system_routes._latest_cache.update({"tag": "v0.1.18-alpha", "at": time.time() - 30, "checked": True})
        got = asyncio.run(system_routes._github_latest())
        chk(got == "v0.1.18-alpha" and len(calls) == 1, "2 dakikadan sık sorulmaz")
        system_routes._latest_cache.update({"tag": "v0.1.19-alpha", "at": time.time() - 300, "checked": True})
        got = asyncio.run(system_routes._github_latest())
        chk(got == "v0.1.19-alpha" and len(calls) == 1, "güncel önbellek saatlik kalır")
    finally:
        system_routes._fetch_github_latest_tag, system_routes._read_version = real_fetch, real_read
        system_routes._latest_cache.clear()
        system_routes._latest_cache.update(real_cache)


def test_server_metrics():
    """Genel bakış grafikleri: çubuk başlangıçları yerel saate hizalı, kayıt metni doğru çubuğa düşer, işlemci yüzdesi
    iki ölçümün farkından, güncelleme sonucu gruplanır."""
    from pops import server_metrics as sm

    now = datetime.datetime(2026, 10, 3, 22, 41, 7)
    hours = sm.bar_starts(now, 86400, 3600)
    chk(len(hours) == 24 and hours[-1] == datetime.datetime(2026, 10, 3, 22)
        and hours[0] == datetime.datetime(2026, 10, 2, 23), "24 saat: 24 saatlik çubuk, sonuncusu şu anki saat")
    six = sm.bar_starts(now, 7 * 86400, 6 * 3600)
    chk(len(six) == 28 and six[-1] == datetime.datetime(2026, 10, 3, 18),
        "7 gün: 6 saatlik 28 çubuk, 00/06/12/18'e hizalı")
    days = sm.bar_starts(now, 30 * 86400, 86400)
    chk(len(days) == 30 and days[-1] == datetime.datetime(2026, 10, 3) and days[0] == datetime.datetime(2026, 9, 4),
        "30 gün: 30 günlük çubuk, gece yarısına hizalı")
    chk(sm._bar_index(six, "2026-10-03 17:59:59") == 26 and sm._bar_index(six, "2026-10-03 18:00:00") == 27,
        "kayıt zamanı doğru çubukta")
    chk(sm._bar_index(six, "2026-09-26 05:00:00") is None and sm._bar_index(six, "bozuk") is None,
        "aralık dışı ve bozuk zaman sayılmaz")
    chk(sm.cpu_pct_between((1000, 800), (1200, 900)) == 50.0, "işlemci: 200 jiffy'nin 100'ü boşta = %50")
    chk(sm.cpu_pct_between(None, (1, 1)) is None and sm.cpu_pct_between((5, 1), (5, 1)) is None,
        "ilk ölçümde ya da fark yokken işlemci boş")
    chk(sm.update_group("success") == "success" and sm.update_group("success_pending_reboot") == "success"
        and sm.update_group("rolled_back") == "rolled_back" and sm.update_group("rollback_failed") == "failed",
        "güncelleme sonuçları gruplanır")


async def _asgi(app, method, path, headers=(), body=b"{}"):
    """Uygulamayı sunucusuz çağırır (lifespan yok, veritabanı yok). Yönlendirmenin seçtiği uç kapsamda kalır."""
    scope = {
        "type": "http", "asgi": {"version": "3.0"}, "http_version": "1.1", "method": method, "scheme": "http",
        "path": path, "raw_path": path.encode(), "query_string": b"", "root_path": "",
        "headers": [(k.lower().encode(), v.encode()) for k, v in headers],
        "client": ("127.0.0.1", 50000), "server": ("unit", 80),
    }
    sent = []

    async def receive():
        return {"type": "http.request", "body": body, "more_body": False}

    async def send(message):
        sent.append(message)

    await app(scope, receive, send)
    status = next(m["status"] for m in sent if m["type"] == "http.response.start")
    data = b"".join(m.get("body", b"") for m in sent if m["type"] == "http.response.body")
    return status, json.loads(data or b"null"), scope


def test_api_v1():
    """/api/v1 aynı uçlara gider (metrik etiketi rota şablonu, istek sınırı ortak), REST adları doğru işleyiciye
    yönlenir, task_sequence ve taskSequence kabul edilir, API jetonu CSRF başlığı istemez ama çerez ister."""
    print("== /api/v1, REST adları, API jetonu")
    from fastapi import FastAPI, Request
    from pydantic import ValidationError
    from slowapi import _rate_limit_exceeded_handler
    from slowapi.errors import RateLimitExceeded

    from pops import apiversion, metrics, security
    from pops.models import ApiTokenCreateInput, OrchestrationInput

    chk(apiversion.unversioned("/api/v1/devices/HW-1") == "/api/devices/HW-1", "/api/v1/x -> /api/x")
    chk(apiversion.unversioned("/api/v10/x") == "/api/v10/x" and apiversion.unversioned("/ws/panel") == "/ws/panel",
        "başka yollar değişmez")

    seen = []

    async def inner(scope, receive, send):
        seen.append(scope)

    mw = apiversion.ApiVersionMiddleware(inner)
    original = {"type": "http", "path": "/api/v1/labs/9/A", "raw_path": b"/api/v1/labs/9%2FA"}
    asyncio.run(mw(original, None, None))
    chk(seen[0]["path"] == "/api/labs/9/A" and seen[0]["raw_path"] == b"/api/labs/9%2FA"
        and original["path"] == "/api/v1/labs/9/A", "yol ve ham yol çevrildi, gelen kapsam değişmedi")
    asyncio.run(mw({"type": "websocket", "path": "/api/v1/x"}, None, None))
    chk(seen[1]["path"] == "/api/v1/x", "WebSocket yolu sürümlenmez")

    import server
    from pops.routers import agents, control, devices, rest, tasks, tokens

    routed = {
        ("DELETE", "/api/labs/9/A/main-pc"): rest.delete_lab_main_pc,   # eğik çizgili sınıf adı (9/A)
        ("PUT", "/api/labs/9/A/main-pc"): rest.put_lab_main_pc,
        ("PUT", "/api/labs/9/A/layout"): rest.put_lab_layout,
        ("POST", "/api/labs/9/A/wake"): devices.wake_lab,
        ("DELETE", "/api/labs/9/A"): rest.delete_lab,
        ("PATCH", "/api/labs/Lab 1"): rest.rename_lab,
        ("GET", "/api/labs"): devices.get_custom_labs,
        ("POST", "/api/labs"): devices.create_lab,
        ("POST", "/api/devices/move"): devices.move_pcs,
        ("PATCH", "/api/devices/HW-1"): rest.update_device,
        ("DELETE", "/api/devices/HW-1"): devices.delete_device,
        ("POST", "/api/devices/HW-1/quarantine"): rest.quarantine_device,
        ("DELETE", "/api/devices/HW-1/quarantine"): rest.release_device,
        ("POST", "/api/devices/HW-1/bypass-code"): control.get_bypass_token,
        ("GET", "/api/settings/task-concurrency"): tasks.get_concurrent_limit,
        ("PUT", "/api/settings/task-concurrency"): tasks.set_concurrent_limit,
        ("POST", "/api/tasks"): tasks.deploy_orchestration,
        ("DELETE", "/api/tasks"): tasks.flush_queue,
        ("PUT", "/api/agent_policies"): agents.save_policies,
        ("POST", "/api/create_lab"): devices.create_lab,
        ("POST", "/api/move_pc"): devices.move_pc,
        ("GET", "/api/tokens"): tokens.list_tokens,
        ("DELETE", "/api/tokens/3"): tokens.revoke_token,
    }
    wrong = []
    for (method, path), endpoint in routed.items():
        status, _, scope = asyncio.run(_asgi(server.app, method, path))
        if status != 401 or scope.get("endpoint") is not endpoint:
            wrong.append("%s %s -> %s %s" % (method, path, status, getattr(scope.get("endpoint"), "__name__", None)))
    chk(not wrong, "REST adları ve eski yollar doğru işleyicide, oturumsuz 401 (%s)" % wrong)
    chk(len(rest.ALIASES) == len({(m, p) for m, p, _, _ in rest.ALIASES}), "her REST adı bir kez")

    metrics.http_requests.clear()
    status, _, _ = asyncio.run(_asgi(server.app, "GET", "/api/v1/devices/HW-UNIT-METRIC/activity"))
    labels = [route for _, route, _ in metrics.http_requests]
    chk(status == 401 and labels == ["/api/devices/{pc_name}/activity"],
        "/api/v1 isteği rota şablonuyla sayılır, cihaz adı ve v1 etikete girmez (%s)" % labels)

    status, body, _ = asyncio.run(_asgi(server.app, "POST", "/api/v1/labs", [("Cookie", "pops_jwt=x")]))
    chk(status == 403 and "CSRF" in body["detail"], "çerezle X-Requested-With'siz değişiklik 403 (CSRF)")
    status, body, _ = asyncio.run(_asgi(server.app, "POST", "/api/v1/labs", [("Authorization", "Bearer pops_x")]))
    chk(status == 401 and "API jetonu" in body["detail"], "Bearer API jetonu CSRF başlığı istemez (geçersiz jeton 401)")
    status, body, _ = asyncio.run(_asgi(server.app, "POST", "/api/v1/labs", [("Cookie", "pops_jwt=pops_x"),
                                                                             ("X-Requested-With", "XMLHttpRequest")]))
    chk(status == 401 and "API jetonu" not in body["detail"], "çerezdeki API jetonu jeton sayılmaz")

    # İstek sınırı işleyiciye göre sayılır: /api/v1 ayrı kota açmaz
    mini = FastAPI()
    mini.state.limiter = security.limiter
    mini.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)
    mini.add_middleware(apiversion.ApiVersionMiddleware)

    @mini.post("/api/unit-limited")
    @security.limiter.limit("2/minute")
    async def unit_limited(request: Request):
        return {"ok": True}

    paths = ("/api/unit-limited", "/api/v1/unit-limited", "/api/v1/unit-limited")
    codes = [asyncio.run(_asgi(mini, "POST", p))[0] for p in paths]
    chk(codes == [200, 200, 429], "istek sınırı /api ve /api/v1 arasında ortak (%s)" % codes)

    seq = [{"name": "n", "type": "CMD", "command": "echo"}]
    a = OrchestrationInput.model_validate({"target_mode": "pc", "targets": ["HW-1"], "task_sequence": seq})
    b = OrchestrationInput.model_validate({"target_mode": "PC", "targets": ["HW-1"], "taskSequence": seq})
    chk(a.task_sequence == b.task_sequence and a.target_mode == "PC", "task_sequence ve taskSequence kabul edilir")
    for bad, why in (
        ({"task_sequence": seq, "taskSequence": seq}, "ikisi birden"),
        ({"task_sequence": seq, "x": 1}, "tanınmayan alan"),
    ):
        try:
            OrchestrationInput.model_validate(dict({"target_mode": "PC", "targets": []}, **bad))
            chk(False, "%s reddedilmeli" % why)
        except ValidationError:
            chk(True, "%s reddedildi" % why)
    try:
        ApiTokenCreateInput(name="x", role="superadmin")
        chk(False, "superadmin jetonu reddedilmeli")
    except ValidationError:
        chk(True, "jeton rolü superadmin olamaz")

    tok = security.new_api_token()
    chk(tok.startswith("pops_") and len(tok) >= 5 + 43 and security.api_token_hash(tok) == __import__(
        "hashlib").sha256(tok.encode()).hexdigest(), "jeton biçimi pops_ + 32 bayt, özet SHA-256")
    chk(security.is_api_token({"api_token_id": 1}) and not security.is_api_token({"sub": "admin"}),
        "jeton kimliği ayırt edilir")


def test_files():
    print("== dosya aktarımı")
    import io
    import tempfile

    from pops import filestore

    chk(filestore.clean_name("Ödev 1.pdf") == "Ödev 1.pdf", "Türkçe ad korunur")
    chk(filestore.clean_name("..\\..\\Windows\\evil\u202egnp.exe") == "evilgnp.exe", "yol ve yön karakteri atılır")
    chk(filestore.clean_name("a<b>c:d|e?.txt") == "abcde.txt", "Windows'ta geçersiz karakterler atılır")
    chk(filestore.clean_name("CON.txt") == "_CON.txt" and filestore.clean_name("nul") == "_nul", "ayrılmış adlar")
    chk(filestore.clean_name("  rapor.pdf.  ") == "rapor.pdf", "sondaki nokta ve boşluk atılır")
    chk(filestore.clean_name("..") is None and filestore.clean_name("") is None and filestore.clean_name("/") is None,
        "kullanılamayan ad: None")
    long_name = filestore.clean_name("a" * 300 + ".docx")
    chk(len(long_name) == 200 and long_name.endswith(".docx"), "uzun ad kısalır, uzantı kalır")
    chk(filestore.needs_exec("x.LNK") and filestore.needs_exec("a.url") and filestore.needs_exec("s.scr")
        and not filestore.needs_exec("a.exe"), "allow_exec isteyen türler (sözleşme)")
    chk(filestore.check_pull_path("c:/Users/Public/a.txt") == "c:\\Users\\Public\\a.txt", "/ -> \\")
    for bad in ("\\\\srv\\share\\a", "a.txt", "C:\\a\\..\\b", "C:\\a.txt:ads", "C:\\d\\", "C:\\a?.txt",
                "C:\\a\\\\b.txt", "C:\\a\x07.txt", "C:\\" + "a" * 1100):
        try:
            filestore.check_pull_path(bad)
            chk(False, "geçersiz yol kabul edildi: %r" % bad)
        except ValueError:
            pass
    chk(filestore.name_of_path("C:\\Users\\Public\\rapor.pdf") == "rapor.pdf", "yoldan dosya adı")
    chk(filestore.valid_id("abcDEF12_-x") and not filestore.valid_id("../x") and not filestore.valid_id("a" * 65)
        and not filestore.valid_id(None), "kimlik biçimi")
    chk(filestore.blob_path("../etc/passwd") is None and filestore.blob_path("push-abc.bin") is None
        and filestore.blob_path("pull-abcdefgh.bin") is not None, "depo adı yalnız sunucunun ürettiği biçimde")
    real_dir = filestore.FILES_DIR
    with tempfile.TemporaryDirectory() as tmp:
        filestore.FILES_DIR = os.path.realpath(tmp)
        try:
            size, sha = filestore.store_file(io.BytesIO(b"x" * 10), "push-abcdefgh.bin", 10)
            chk(size == 10 and len(sha) == 64 and os.path.exists(os.path.join(tmp, "push-abcdefgh.bin")),
                "sınırda dosya yazıldı")
            try:
                filestore.store_file(io.BytesIO(b"x" * 11), "push-abcdefgx.bin", 10)
                chk(False, "sınır aşıldı ama yazıldı")
            except filestore.TooLarge:
                chk(sorted(os.listdir(tmp)) == ["push-abcdefgh.bin"], "sınır aşılınca geçici dosya kalmaz")

            async def chunks():
                for part in (b"a" * 700000, b"b" * 700000):
                    yield part
            size, _sha = asyncio.run(filestore.store_stream(chunks(), "pull-abcdefgh.bin", 2000000))
            chk(size == 1400000 and os.path.getsize(os.path.join(tmp, "pull-abcdefgh.bin")) == 1400000,
                "akış parça parça yazıldı")
            try:
                asyncio.run(filestore.store_stream(chunks(), "pull-abcdefgx.bin", 1000000))
                chk(False, "akış sınırı aşıldı ama yazıldı")
            except filestore.TooLarge:
                chk(not any(n.startswith(".upload-") or n == "pull-abcdefgx.bin" for n in os.listdir(tmp)),
                    "akış sınırı aşılınca geçici dosya kalmaz")
            old = os.path.join(tmp, "pull-orphan01.bin")
            open(old, "wb").close()
            os.utime(old, (time.time() - 2 * 86400, time.time() - 2 * 86400))
            removed = filestore._orphans({"push-abcdefgh.bin"})
            chk(removed == 1 and not os.path.exists(old) and os.path.exists(os.path.join(tmp, "pull-abcdefgh.bin")),
                "sahipsiz eski dosya silinir, yenisi ve kayıtlı olan kalır")
        finally:
            filestore.FILES_DIR = real_dir
    items = activity.build_items(
        "HW-F", [],
        [{"action": "file_push", "timestamp": "2026-10-01 10:00:00",
          "changes": '{"name": "Ödev.pdf", "admin": "Pasha", "reason": "ders"}'},
         {"action": "file_pull", "timestamp": "2026-10-01 11:00:00",
          "changes": '{"path": "C:\\\\Users\\\\ali\\\\gizli.docx", "admin": "Pasha", "reason": "inceleme"}'}],
        [], [],
    )
    chk([i["title"] for i in items] == ["Bu bilgisayardan dosya istendi", "Bilgisayara dosya gönderildi: Ödev.pdf"],
        "tepsi geçmişinde dosya aktarımları")
    chk("gizli.docx" not in json.dumps(items, ensure_ascii=False) and items[0]["actor"] == "Pasha",
        "istenen dosyanın yolu tepside görünmez, yapan görünür")


def test_exam():
    """Sınav modu: izin listesi (alan adı, IP, CIDR) ve program listesi doğrulaması, bitiş zamanı, bilgisayar durumu,
    ajan mesajının biçimi ve eğik çizgili sınıf adıyla yönlendirme."""
    print("== sınav modu")
    from pops import exams

    good = {
        "sinav.meb.gov.tr": "sinav.meb.gov.tr", "https://Sinav.MEB.gov.tr/giris?x=1": "sinav.meb.gov.tr",
        "10.0.0.5": "10.0.0.5", "10.1.0.7/24": "10.1.0.0/24", "10.0.0.5/32": "10.0.0.5", "2001:db8::1": "2001:db8::1",
        "http://[2001:db8::1]:8080/a": "2001:db8::1", "example.com.": "example.com", "meb.gov.tr/a/b": "meb.gov.tr",
        "sınav.meb.gov.tr": "xn--snav-lza.meb.gov.tr",
    }
    got = {raw: exams.clean_allow_entry(raw) for raw in good}
    chk(got == good, "izin girişleri normalleşir (%s)" % {k: v for k, v in got.items() if good[k] != v})
    bad = ["", "*.meb.gov.tr", "localhost", "0.0.0.0/0", "::/8", "10.0.0", "foo bar", "a..b", "-a.com", "10.0.0.5:80",
           "1.2.3.4/abc", "x" * 301]
    passed = []
    for raw in bad:
        try:
            passed.append((raw, exams.clean_allow_entry(raw)))
        except ValueError:
            pass
    chk(not passed, "geçersiz girişler reddedilir (%s)" % passed)
    chk(exams.clean_allow(["a.com", "A.com", "10.0.0.1"]) == ["a.com", "10.0.0.1"], "tekrarlar birleşir, sıra korunur")
    try:
        exams.clean_allow(["n%d.example.com" % i for i in range(51)])
        chk(False, "51 giriş reddedilmeli")
    except ValueError as exc:
        chk("50" in str(exc), "en fazla 50 giriş")
    try:
        exams.clean_allow(["ok.com", "kötü giriş"])
        chk(False, "geçersiz giriş reddedilmeli")
    except ValueError as exc:
        chk("kötü giriş" in str(exc), "hata mesajı geçersiz girişi adıyla yazar")

    chk(exams.clean_apps(["cmd", "C:\\Windows\\System32\\CMD.EXE", "PowerShell.exe"]) == ["cmd.exe", "powershell.exe"],
        "program adı: yol atılır, küçük harf, .exe eklenir, tekrarsız")
    for apps in (["explorer.exe"], ["POpsAgent.exe"], ["bad*.exe"], ["n%d.exe" % i for i in range(51)]):
        try:
            exams.clean_apps(apps)
            chk(False, "reddedilmeli: %s" % apps[:1])
        except ValueError:
            chk(True, "reddedildi: %s" % apps[0])

    now = 1_800_000_000.0
    chk(exams.resolve_until(None, 40, now) == int(now) + 2400, "süre (dakika) bitişe çevrilir")
    chk(exams.resolve_until(now + 3600, None, now) == int(now) + 3600, "bitiş zamanı olduğu gibi")
    late = now + 8 * 3600 + 1
    for until, minutes in ((None, None), (now + 600, 10), (now - 5, None), (now + 30, None), (late, None)):
        try:
            exams.resolve_until(until, minutes, now)
            chk(False, "bitiş reddedilmeli: %s %s" % (until, minutes))
        except ValueError:
            chk(True, "bitiş reddedildi: until=%s duration=%s" % (until and until - now, minutes))

    t = datetime.datetime.fromtimestamp
    utc = datetime.timezone.utc
    sent = t(now - 60, utc)

    def st(online, row, at=now):
        return exams.device_state(online, row, at)

    chk(st(False, {"sent_at": sent, "reported_at": sent, "enabled": True}) == "unreachable", "çevrimdışı: ulaşılamıyor")
    chk(st(True, None) == "pending", "gönderilmemiş: bekleniyor")
    chk(st(True, {"sent_at": t(now - 5, utc)}) == "pending", "yeni gönderildi, yanıt yok: bekleniyor")
    chk(st(True, {"sent_at": sent}) == "unsupported", "yanıt yok (eski ajan): desteklemiyor")
    chk(st(True, {"sent_at": sent, "reported_at": t(now - 50, utc), "enabled": True}) == "in_exam", "sınavda")
    chk(st(True, {"sent_at": sent, "reported_at": t(now - 10, utc), "enabled": False}) == "left", "ayrıldı")
    chk(st(True, {"sent_at": t(now - 3, utc), "reported_at": t(now - 2, utc), "enabled": False}) == "pending",
        "gönderimden hemen sonraki 'sınavda değil' henüz ayrılma sayılmaz")
    chk(st(True, {"sent_at": sent, "reported_at": t(now - 50, utc), "enabled": False,
                  "denied_at": t(now - 40, utc)}) == "denied", "yerel yetenek kapalı: reddetti")
    chk(st(True, {"sent_at": sent, "reported_at": t(now - 30, utc), "enabled": True,
                  "denied_at": t(now - 40, utc)}) == "in_exam", "retten sonra sınava giren: sınavda")
    chk(exams.counts([{"state": "in_exam"}, {"state": "left"}, {"state": "in_exam"}])
        == {"in_exam": 2, "left": 1, "unreachable": 0, "unsupported": 0, "pending": 0, "denied": 0}, "durum sayıları")

    row = {"id": 7, "lab_name": "9/A", "allow_list": '["sinav.meb.gov.tr"]', "until_at": t(now + 600, utc),
           "message": "m", "block_apps": "[]", "reason": "r", "started_by": "admin", "started_at": t(now, utc),
           "ended_by": None, "ended_at": None, "end_reason": None}
    exam = exams.public(row, now)
    chk(exam["active"] and exam["remaining_seconds"] == 600 and exam["until"] == int(now) + 600
        and exam["allow"] == ["sinav.meb.gov.tr"], "kaydın API biçimi")
    msg = exams.agent_message(exam)
    chk(msg == {"action": "exam_mode", "enabled": True, "allow": ["sinav.meb.gov.tr"], "until": int(now) + 600,
                "message": "m", "block_apps": []}, "ajan mesajı sözleşmedeki biçimde")
    chk(exams.DISABLE == {"action": "exam_mode", "enabled": False}, "kapatma mesajı")
    chk(exams._ts(True) is None and exams._ts("1") is None and exams._ts(1) is None
        and exams._ts(now).timestamp() == now, "ajanın zamanı yalnızca makul unix sayısı")

    import server
    from pops.routers import agents, devices, exams as exams_router, rest

    routed = {
        ("POST", "/api/labs/9/A/exam"): exams_router.start_exam,
        ("DELETE", "/api/labs/9/A/exam"): exams_router.end_exam,
        ("GET", "/api/labs/Lab 1/exam"): exams_router.get_lab_exam,
        ("GET", "/api/exams"): exams_router.list_exams,
        ("DELETE", "/api/labs/9/A"): rest.delete_lab,
        ("POST", "/api/labs/9/A/wake"): devices.wake_lab,
    }
    wrong = []
    for (method, path), endpoint in routed.items():
        status, _, scope = asyncio.run(_asgi(server.app, method, path))
        if status != 401 or scope.get("endpoint") is not endpoint:
            wrong.append("%s %s -> %s %s" % (method, path, status, getattr(scope.get("endpoint"), "__name__", None)))
    chk(not wrong, "sınav yolları (eğik çizgili sınıf adıyla) doğru işleyicide, oturumsuz 401 (%s)" % wrong)
    chk("exam_mode" in agents.SERVER_FEATURES, "server_info exam_mode'u duyurur")


def test_agent_platform():
    """Linux ajanı (migration 0026): platform başlıktan, yoksa ilk mesajdan; bildirmeyen ajan Windows."""
    print("== agent_platform")
    from pops.routers import agents as agents_router

    chk(agents_router.agent_platform("linux") == "linux" and agents_router.agent_platform(" Linux ") == "linux",
        "X-Agent-Platform: linux")
    chk(agents_router.agent_platform(None) == "windows" and agents_router.agent_platform("") == "windows",
        "başlık yoksa (Windows ajanı) windows")
    chk(agents_router.agent_platform(None, "linux") == "linux", "başlık yoksa ilk mesajdaki platform")
    chk(agents_router.agent_platform("beos", "haiku") == "windows", "bilinmeyen değer windows sayılır")


def test_agent_packages():
    """Ajan güncellemesi platform başına tek paket seçer; Windows ajanının MSI adı .deb ile karışmaz."""
    print("== agent_packages")
    import system_routes

    msi = {"name": "POps-Agent-0.1.22-alpha-win-x64.msi", "sha256": "a" * 64, "size": 1}
    deb = {"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "b" * 64, "size": 1}
    zipped = {"name": "POps-Agent-0.1.22-alpha-win-x64.zip", "sha256": "c" * 64, "size": 1}
    server = {"name": "pops-server-0.1.22-alpha.tar.gz", "sha256": "d" * 64, "size": 1}
    both = {"artifacts": [deb, msi, zipped, server]}
    chk(system_routes._agent_packages(both) == {"windows": msi["name"], "linux": deb["name"]},
        "manifest'te MSI ve .deb: platform başına bir paket")
    chk(system_routes._agent_msis(both) == [msi["name"]], ".deb, Windows'un MSI listesine girmez")
    chk(system_routes._agent_packages({"artifacts": [msi, zipped]}) == {"windows": msi["name"]},
        "eski release (yalnız MSI)")
    chk(system_routes._agent_packages({"artifacts": [deb]}) == {"linux": deb["name"]}, "yalnız .deb")
    try:
        system_routes._agent_packages({"artifacts": [deb, dict(deb, name="pops-agent_0.1.23-alpha_all.deb")]})
        chk(False, "iki .deb reddedilir")
    except ValueError:
        chk(True, "iki .deb reddedilir")
    chk(system_routes._agent_debs({"artifacts": [{"name": "pops-agent_x_all.deb.sig"}, {"name": "../a_all.deb"}]})
        == [], "benzer ama geçersiz adlar paket sayılmaz")


def test_winget():
    """winget adımı: kimlik ve sürüm doğrulaması (kabuk karakteri ve satır sonu geçmez), sabit argüman listesi, adım
    modeli (WINGET paket ister, komut taşımaz), ajana giden ileti, X-Agent-Features ve katalog araması."""
    print("== winget")
    from pydantic import ValidationError

    from pops import agent_version, winget, winget_catalog
    from pops.models import OrchestrationInput, TaskSequenceItem

    for good in ("Mozilla.Firefox", "Notepad++.Notepad++", "Microsoft.VCRedist.2015+.x64", "7zip.7zip",
                 "Adobe.Acrobat.Reader.64-bit", "a_" + "b" * 126):
        chk(winget.clean_id(good) == good, "geçerli kimlik: %s" % good[:40])
    for bad in ("", "x", ".Mozilla", "-Firefox", "Mozilla Firefox", "Mozilla.Firefox\n", "Mozilla.Firefox\r",
                "Mozilla.Firefox & calc", "a;b", "a|b", "a\"b", "a'b", "a`b", "$(x)", "a>b", "a%PATH%",
                "a/b", "a\\b", "a" * 129, "Mozilla.Firefox\x00", "Mözilla.Firefox", None, 5, ["x"]):
        try:
            winget.clean_id(bad)
            chk(False, "geçersiz kimlik reddedilmeli: %r" % (bad,))
        except ValueError:
            chk(True, "geçersiz kimlik reddedildi: %r" % (str(bad)[:30],))
    chk(winget.clean_version(None) is None and winget.clean_version("") is None, "sürüm yok: en son sürüm")
    for good in ("1.2", "124.0.1", "3.12.10", "1.0-beta+2", "x" * 40):
        chk(winget.clean_version(good) == good, "geçerli sürüm: %s" % good[:20])
    for bad in ("1.2 ", "1.2\n", "1;2", "1 2", "x" * 41, "--force", 1.2):
        try:
            v = winget.clean_version(bad)
            # "--force" yalnızca izinli karakterlerden oluşur ama ayrı argüman olarak --version'ın değeridir
            chk(bad == "--force" and v == "--force", "sürüm %r" % (bad,))
        except ValueError:
            chk(True, "geçersiz sürüm reddedildi: %r" % (str(bad)[:20],))

    args = winget.arguments("Mozilla.Firefox", "124.0")
    chk(args == ["install", "--id", "Mozilla.Firefox", "-e", "--silent", "--scope", "machine",
                 "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
                 "--version", "124.0"], "argüman listesi sözleşmedeki gibi (%s)" % args)
    chk(winget.arguments("7zip.7zip")[-1] == "--disable-interactivity", "sürümsüz: --version yok")
    chk(winget.command_line("7zip.7zip").startswith("winget install --id 7zip.7zip -e --silent"), "okunur komut")

    task = {"id": 7, "payload": winget.payload("Mozilla.Firefox", None)}
    chk(winget.message(task, "admin") == {"action": "winget_install", "task_id": 7, "id": "Mozilla.Firefox",
                                          "version": None, "requested_by": "admin"}, "ajana giden ileti")
    for broken in ('{"id": "a b"}', '{"id": "Mozilla.Firefox", "version": "1 2"}', "[]", "null", None, "{"):
        try:
            winget.message({"id": 8, "payload": broken}, "admin")
            chk(False, "bozuk paket bilgisi gönderilmemeli: %r" % (broken,))
        except (ValueError, TypeError):
            chk(True, "bozuk paket bilgisi gönderilmedi: %r" % (broken,))

    ok = TaskSequenceItem(name="Firefox", type="WINGET", winget={"id": "Mozilla.Firefox", "version": None})
    chk(ok.is_winget and ok.winget.id == "Mozilla.Firefox" and ok.command is None, "WINGET adımı")
    chk(TaskSequenceItem(name="x", type="winget", winget={"id": "7zip.7zip"}).is_winget,
        "tür büyük/küçük harf duyarsız")
    for bad, why in (
        ({"type": "WINGET"}, "paketsiz WINGET"),
        ({"type": "WINGET", "winget": {"id": "Mozilla.Firefox"}, "command": "calc"}, "komutlu WINGET"),
        ({"type": "WINGET", "winget": {"id": "Mozilla.Firefox & calc"}}, "kabuk karakterli kimlik"),
        ({"type": "WINGET", "winget": {"id": "Mozilla.Firefox", "version": "1.0 & calc"}}, "kabuk karakterli sürüm"),
        ({"type": "WINGET", "winget": {"id": "Mozilla.Firefox", "args": "--force"}}, "tanınmayan paket alanı"),
        ({"type": "CMD", "command": "echo", "winget": {"id": "Mozilla.Firefox"}}, "winget alanlı CMD"),
        ({"type": "CMD"}, "komutsuz CMD"),
    ):
        try:
            TaskSequenceItem.model_validate(dict({"name": "t"}, **bad))
            chk(False, "%s reddedilmeli" % why)
        except ValidationError:
            chk(True, "%s reddedildi" % why)
    chk(TaskSequenceItem(name="t", type="CMD", command="").command == "", "boş komutlu CMD eskisi gibi geçer")
    chk(len(OrchestrationInput.model_validate({"target_mode": "pc", "targets": ["HW-1"], "taskSequence": [
        {"name": "a", "type": "package", "command": "x"}, {"name": "b", "type": "WINGET", "winget": {"id": "Git.Git"}},
    ]}).task_sequence) == 2, "karışık zincir")

    chk(agent_version.features("winget") == ["winget"], "X-Agent-Features: winget")
    chk(agent_version.features(" Winget , foo_bar,winget,,bad name,x;y,") == ["foo_bar", "winget"],
        "özellik listesi temizlenir")
    chk(agent_version.features(None) == [] and agent_version.features("") == [], "başlık yoksa boş")
    chk(len(agent_version.features(",".join("f%d" % i for i in range(100)))) == 32, "en çok 32 özellik")
    chk(winget.supports(["winget"]) and not winget.supports(None) and not winget.supports([]), "özellik denetimi")

    ids = [p["id"] for p in winget_catalog.PACKAGES]
    chk(len(ids) == len(set(ids)) and len(ids) >= 60, "katalog: %d tekil paket" % len(ids))
    bad_entries = []
    for p in winget_catalog.PACKAGES:
        try:
            winget.clean_id(p["id"])
        except ValueError:
            bad_entries.append(p["id"])
        if p["category"] not in winget.CATEGORY_LABELS or not all(p.get(k) for k in (
                "name", "publisher", "description", "description_en")):
            bad_entries.append(p["id"])
    chk(not bad_entries, "katalog kayıtları eksiksiz ve kimlikleri geçerli (%s)" % bad_entries)
    r = winget.search("TARAYICI")
    chk(r["matched"] >= 4 and all(p["category"] == "browser" for p in r["items"]),
        "Türkçe büyük harf / ı farkı yok sayılır (%d)" % r["matched"])
    chk([p["id"] for p in winget.search("firefox")["items"]][:2] == ["Mozilla.Firefox", "Mozilla.Firefox.tr"],
        "adı aramayla başlayan önce")
    chk(winget.search("mozilla.firefox")["items"][0]["id"] == "Mozilla.Firefox", "tam kimlik en üstte")
    chk(winget.search("python dil")["matched"] == 2, "sözcüklerin hepsi geçmeli")
    r = winget.search("", "education")
    chk(r["items"] and all(p["category"] == "education" for p in r["items"]) and r["total"] == len(ids),
        "kategori süzgeci")
    chk(sum(c["count"] for c in r["categories"]) == len(ids), "kategori sayıları")
    chk(winget.search("yok-boyle-bir-paket")["items"] == [], "eşleşme yoksa boş")
    chk(len(winget.search("", limit=3)["items"]) == 3, "limit")
    chk(-1978335135 in winget.OK_EXIT_CODES and 0 in winget.OK_EXIT_CODES, "zaten kurulu = başarı")


def test_vision_v2():
    """Vision v2: ikili kare başlığı, panel öneki, görüntüleyici komutları, yavaş panelde kare düşürme ve kare/pano
    yetkisi (F12: yalnızca oturum sahibi admin paneli, yalnızca ikili kare bildiren panel)."""
    print("== vision v2")
    from pops import vision as v
    from pops import manager as mgr

    jpeg = b"\xff\xd8\xff\xe0" + b"x" * 60 + b"\xff\xd9"
    full = v.pack_frame(v.KIND_FULL, 0, 7, 0, 0, 1920, 1080, 1920, 1080, jpeg)
    f, why = v.parse_frame(full)
    chk(why is None and f == v.Frame(1, 0, 7, 0, 0, 1920, 1080, 1920, 1080),
        "tam kare başlığı okunur (18 bayt, BE)")
    chk(v.HEADER_SIZE == 18 and full[:2] == b"\x01\x00" and full[2:6] == b"\x00\x00\x00\x07"
        and full[14:16] == (1920).to_bytes(2, "big"), "alan sırası: tür, monitör, sıra, x, y, w, h, tam w, tam h")
    region = v.pack_frame(v.KIND_REGION, 1, 0xFFFFFFFF, 100, 50, 200, 80, 1280, 1024, jpeg)
    f, why = v.parse_frame(region)
    chk(why is None and f.kind == 2 and f.monitor == 1 and f.seq == 0xFFFFFFFF
        and (f.x, f.y, f.w, f.h) == (100, 50, 200, 80), "bölge karesi okunur (u32 sıra sınırda)")
    cursor = v.pack_frame(v.KIND_CURSOR, 0, 8, 640, 360, 0, 0, 1920, 1080)
    f, why = v.parse_frame(cursor)
    chk(why is None and f.kind == 3 and (f.x, f.y) == (640, 360), "imleç konumu: görüntüsüz 18 bayt")
    every = v.pack_frame(v.KIND_FULL, v.ALL_MONITORS, 9, 0, 0, 3200, 1080, 3200, 1080, jpeg)
    chk(v.parse_frame(every)[0].monitor == 0xFF, "monitör 0xFF: bütün ekranlar yan yana tek görüntüde")
    big = v.pack_frame(v.KIND_FULL, 0, 1, 0, 0, 10, 10, 10, 10, jpeg + b"\x00" * v.MAX_FRAME_BYTES)
    bad = {
        "short": full[:17],
        "kind": b"\x07" + full[1:],
        "monitor": v.pack_frame(v.KIND_FULL, 16, 1, 0, 0, 10, 10, 10, 10, jpeg),
        "geometry": v.pack_frame(v.KIND_REGION, 0, 1, 1900, 0, 40, 10, 1920, 1080, jpeg),
        "jpeg": v.pack_frame(v.KIND_FULL, 0, 1, 0, 0, 10, 10, 10, 10, b"\x89PNG...."),
        "cursor_payload": v.pack_frame(v.KIND_CURSOR, 0, 1, 1, 1, 0, 0, 10, 10, jpeg),
        "oversize": big,
    }
    chk(all(v.parse_frame(data) == (None, reason) for reason, data in bad.items()),
        "bozuk kareler nedeniyle reddedilir: " + ", ".join(bad))
    geometry = (
        v.pack_frame(v.KIND_FULL, 0, 1, 5, 0, 10, 10, 10, 10, jpeg),        # tam kare (0,0)'dan
        v.pack_frame(v.KIND_FULL, 0, 1, 0, 0, 5, 10, 10, 10, jpeg),         # tam kare bütün çıktı
        v.pack_frame(v.KIND_FULL, 0, 1, 0, 0, 0, 0, 0, 10, jpeg),           # çıktı boyutu sıfır olamaz
        v.pack_frame(v.KIND_REGION, 0, 1, 0, 0, 0, 10, 10, 10, jpeg),       # bölge boyutu sıfır olamaz
        v.pack_frame(v.KIND_CURSOR, 0, 1, 10, 0, 0, 0, 10, 10),             # imleç çıktının içinde
        v.pack_frame(v.KIND_CURSOR, 0, 1, 1, 1, 4, 4, 10, 10),              # imleçte w = h = 0
    )
    chk(all(v.parse_frame(data)[1] == "geometry" for data in geometry),
        "geometri ajanın kurallarıyla aynı: tam kare bütün çıktı, bölge içinde, imleç w = h = 0")
    chk(v.parse_frame(v.pack_frame(v.KIND_FULL, 0, 1, 0, 0, 10, 10, 10, 10, b"\xff\xd8\xff"))[1] == "jpeg",
        "JPEG imzası ve en az 4 bayt")
    exact = v.pack_frame(v.KIND_FULL, 0, 1, 0, 0, 10, 10, 10, 10, jpeg)
    chk(v.parse_frame(exact + b"\x00" * (v.MAX_FRAME_BYTES - len(exact)))[1] is None, "tam 2 MB kabul edilir")
    chk(v.panel_prefix("HW-ABC") == b"\x01\x06HW-ABC" and v.panel_prefix("") is None
        and v.panel_prefix("x" * 256) is None and v.panel_prefix("ÇÖ") == b"\x01\x04" + "ÇÖ".encode(),
        "panel öneki: 0x01, kimliğin bayt uzunluğu, UTF-8 kimlik")

    vc = v.viewer_command
    chk(vc({"action": "select_monitor", "index": 1, "device": "HW-1", "x": 1})
        == {"action": "select_monitor", "index": 1}
        and vc({"action": "select_monitor", "index": "all"}) == {"action": "select_monitor", "index": "all"},
        "select_monitor: yalnız sözleşmedeki alanlar")
    chk(vc({"action": "select_monitor", "index": True}) is None
        and vc({"action": "select_monitor", "index": 16}) is None
        and vc({"action": "select_monitor", "index": "1"}) is None, "select_monitor: geçersiz dizin atılır")
    chk(vc({"action": "set_quality", "quality": 50, "scale": 0.75, "fps": 5})
        == {"action": "set_quality", "quality": 50, "scale": 0.75, "fps": 5}
        and vc({"action": "set_quality", "quality": 75, "scale": 1, "fps": 10})["scale"] == 1.0,
        "set_quality: sınırlar içinde iletilir")
    chk(all(vc(dict({"action": "set_quality", "quality": 50, "scale": 0.75, "fps": 5}, **bad)) is None for bad in (
        {"quality": 29}, {"quality": 76}, {"scale": 0.4}, {"scale": 1.01}, {"fps": 0}, {"fps": 11},
        {"fps": 2.5}, {"quality": True}, {"scale": float("nan")}, {"scale": "1"})), "set_quality: sınır dışı atılır")
    chk(vc({"action": "clipboard", "text": "merhaba"}) == {"action": "clipboard", "text": "merhaba"}
        and vc({"action": "clipboard", "text": "ğ" * 32768}) is not None
        and vc({"action": "clipboard", "text": "ğ" * 32769}) is None
        and vc({"action": "clipboard", "text": ""}) is None
        and vc({"action": "clipboard", "text": "\ud800"}) is None and vc({"action": "execute"}) is None,
        "pano: en çok 64 KB UTF-8, boş ve bozuk metin atılır; bilinmeyen komut atılır")
    ml = v.monitors_list
    chk(ml({"list": [{"index": 0, "width": 1920, "height": 1080, "primary": True, "name": "x"}]})
        == [{"index": 0, "width": 1920, "height": 1080, "primary": True}], "monitors: bilinen alanlar")
    chk(all(ml(p) is None for p in ({"list": []}, {"list": "x"}, {"list": [{"index": 0, "width": 0, "height": 1}]},
            {"list": [{"index": 0, "width": 9, "height": 9}, {"index": 0, "width": 9, "height": 9}]},
            {"list": [{"index": 0, "width": 9, "height": 9, "primary": 1}]})), "monitors: bozuk liste atılır")

    class SlowWS:
        def __init__(self):
            self.sent, self.gate = [], asyncio.Event()

        async def send_text(self, t):
            await self.gate.wait()
            self.sent.append(t)

        async def send_bytes(self, b):
            await self.gate.wait()
            self.sent.append(b)

    async def slow_panel():
        dead = []
        ws = SlowWS()
        sender = mgr._PanelSender(ws, dead.append)
        k0, k1, kc = ("HW-1", 0), ("HW-1", 1), ("HW-1", "cursor")
        sender.put_binary(k0, v.KIND_FULL, b"F0")
        await asyncio.sleep(0)  # F0 yazılıyor, panel yavaş: gerisi bekler
        resync = [sender.put_binary(k0, v.KIND_REGION, b"R%d" % i) for i in range(mgr._BIN_KEY_MAX_FRAMES + 2)]
        chk(resync.count(True) == 1 and resync[mgr._BIN_KEY_MAX_FRAMES] is True and k0 in sender.stale
            and len(sender.bins[k0]) == mgr._BIN_KEY_MAX_FRAMES,
            "yavaş panel: %d bölgeden sonrası düşer, monitör bayatlar (bir kez vision_resync)"
            % mgr._BIN_KEY_MAX_FRAMES)
        sender.put_binary(k1, v.KIND_REGION, b"S1")
        chk(list(sender.bins[k1]) == [b"S1"], "başka monitörün bölgeleri etkilenmez")
        sender.put_binary(k0, v.KIND_FULL, b"F1")
        chk(list(sender.bins[k0]) == [b"F1"] and k0 not in sender.stale,
            "tam kare bekleyenlerin yerini alır, bayatlık biter")
        sender.put_binary(k0, v.KIND_REGION, b"R99")
        sender.put_binary(kc, v.KIND_CURSOR, b"C1")
        sender.put_binary(kc, v.KIND_CURSOR, b"C2")
        chk(sender.dropped_frames == 2 + mgr._BIN_KEY_MAX_FRAMES + 1, "düşen kareler sayılır")
        ws.gate.set()
        for _ in range(20):
            await asyncio.sleep(0)
        mine = [b for b in ws.sent if b in (b"F0", b"F1", b"R99")]
        chk(mine == [b"F0", b"F1", b"R99"] and b"C2" in ws.sent and b"C1" not in ws.sent and b"S1" in ws.sent
            and not sender.bins and not dead, "panel yetişince sıra korunarak gönderilir, imleçte yalnız sonuncu")
        ws.gate.clear()
        sender.put_binary(k0, v.KIND_FULL, b"G0")
        await asyncio.sleep(0)
        mb = b"x" * (1024 * 1024)
        res = [sender.put_binary(k0, v.KIND_REGION, mb) for _ in range(5)]
        chk(res == [False] * 4 + [True], "bayt sınırı: bekleyen bölgeler 4 MB'ı geçemez")
        sender.task.cancel()

        # Yönetici: ikili kare yalnız oturum sahibi admin'in ikili kare bildiren paneline, önek tünelin cihazı
        m = mgr.ConnectionManager()

        class FastWS:
            def __init__(self):
                self.sent = []

            async def send_text(self, t):
                self.sent.append(t)

            async def send_bytes(self, b):
                self.sent.append(b)

        panels = {}
        for name, user, role, binary in (("own", "ali", "admin", True), ("own_text", "ali", "admin", False),
                                         ("other", "veli", "admin", True), ("viewer", "ali", "viewer", True)):
            ws = FastWS()
            panels[name] = ws
            m.active_panels.append(ws)
            m.panel_users[ws], m.panel_roles[ws] = user, role
            m.panel_senders[ws] = mgr._PanelSender(ws, lambda w: None)
            if binary:
                m.panel_binary.add(ws)
        m.add_vision_session("HW-1", "ali")
        m.add_vision_session("HW-2", "veli")
        frame, _ = v.parse_frame(full)
        n = await m.send_binary_frame_to_viewers("HW-1", frame, full)
        await asyncio.sleep(0)
        chk(n == 1 and panels["own"].sent == [b"\x01\x04HW-1" + full] and not panels["own_text"].sent
            and not panels["other"].sent and not panels["viewer"].sent,
            "F12: ikili kare yalnız oturum sahibinin ikili panelinde; önek tünelin cihazı")
        await m.send_to_session_holders({"type": "monitors", "hw_id": "HW-1", "list": []}, "HW-1")
        await asyncio.sleep(0)
        chk(len(panels["own_text"].sent) == 1 and not panels["other"].sent and not panels["viewer"].sent,
            "monitors yalnız oturum sahibinin panellerine")
        chk(not m.clipboard_allowed("ali", "HW-1"), "pano: tünel açılmadan kapalı")
        m.vision_tunnel_opened("HW-1", FastWS())
        chk(m.clipboard_allowed("ali", "HW-1") and m.clipboard_users("HW-1") == {"ali"},
            "pano: tüneli tek 'kullanıcıya sor' oturumunun rızası açtı → o oturumun sahibine açık")
        m.add_vision_session("HW-1", "veli")
        chk(m.clipboard_users("HW-1") == {"ali"}, "pano: tünel açıkken başlayan ikinci oturuma kapalı")
        m.vision_tunnel_opened("HW-1", FastWS())
        chk(not m.clipboard_users("HW-1"),
            "pano: tünel açılırken iki oturum varsa (hangisi kabul edildi bilinmez) kimseye açılmaz")
        m.remove_vision_session("HW-1", "veli")
        m.add_vision_session("HW-1", "ali", mandatory=True)
        m.vision_tunnel_opened("HW-1", FastWS())
        chk(not m.clipboard_allowed("ali", "HW-1"), "pano: zorunlu oturumda kapalı")
        m.add_vision_session("HW-1", "ali")
        m.vision_tunnel_opened("HW-1", FastWS())
        chk(m.clipboard_allowed("ali", "HW-1"), "pano: yeni 'kullanıcıya sor' oturumu tüneli yeniden açınca açık")
        m.add_vision_session("HW-1", "ali", mandatory=True)
        chk(not m.clipboard_allowed("ali", "HW-1"), "pano: oturum zorunluya dönerse kapanır")
        m.remove_vision_session("HW-1", "ali")
        chk(not m.clipboard_allowed("ali", "HW-1") and ("HW-1", "ali") not in m.vision_session_modes
            and "HW-1" not in m.vision_clipboard_owner, "oturum bitince pano, oturum türü ve pano sahibi düşer")
        m.vision_tunnel_opened("HW-2", FastWS())
        chk(m.vision_clipboard_owner.get("HW-2") == "veli", "B cihazında tek oturum: pano sahibi veli")
        m.disconnect_vision("HW-2")
        chk("HW-2" not in m.vision_clipboard_owner and not m.clipboard_allowed("veli", "HW-2"),
            "tünel kapanınca pano sahibi düşer")

        # Yavaş panelde bekleyen ikili kareler toplam sınırı aşarsa panel kapatılır (monitör baytını ajan seçer)
        dead = []
        ws = SlowWS()
        sender = mgr._PanelSender(ws, dead.append)
        mb2 = b"x" * (2 * 1024 * 1024)
        sender.put_binary(("HW-9", 0), v.KIND_FULL, b"F")
        await asyncio.sleep(0)
        for mon in range(9):
            sender.put_binary(("HW-9", mon), v.KIND_FULL, mb2)
        chk(dead == [ws], "bekleyen ikili kareler 16 MB'ı aşınca panel kapatıldı")
        sender.task.cancel()
        for s in m.panel_senders.values():
            s.task.cancel()

    asyncio.run(slow_panel())
    from pops.routers import agents
    chk({"vision_binary", "vision_clipboard"} <= set(agents.SERVER_FEATURES),
        "server_info vision_binary ve vision_clipboard'u duyurur")


def main():
    test_update_notice()
    test_update_progress()
    test_log_format()
    test_log_writer()
    test_activity()
    test_bypass()
    test_hardening()
    test_agent_health()
    test_p1()
    test_review4()
    test_modules()
    test_release_compare()
    test_server_metrics()
    test_api_v1()
    test_files()
    test_exam()
    test_agent_platform()
    test_agent_packages()
    test_winget()
    test_vision_v2()
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM BIRIM TESTLERI GECTI")


if __name__ == "__main__":
    main()
