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

from pops import agent_health, bypass, logs, update_notice  # noqa: E402
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
         {"action": "set_capabilities", "timestamp": "2026-09-21 10:00:00",
          "changes": '{"vision_enabled": false, "by": "Pasha"}'}],
        [{"action": "install_updates", "timestamp": "2026-09-27 18:00:05",
          "changes": '{"by": "Pasha", "dispatched": ["HW-A"]}'},
         {"action": "install_updates", "timestamp": "2026-09-27 18:00:06",
          "changes": '{"by": "Pasha", "dispatched": ["HW-AB"]}'}],
        [{"created_at": "2026-09-26 19:44:40", "created_by": "Pasha", "status": "Completed"}],
    )
    chk([i["at"] for i in items] == sorted([i["at"] for i in items], reverse=True), "en yeni başta")
    chk(len(items) == 7, "başka cihaza gönderilen işlem dahil değil (%d kayıt)" % len(items))
    chk(items[0]["title"] == "0.1.8-alpha güncellemesi geri alındı, 0.1.7-alpha çalışıyor", "güncelleme başlığı")
    chk("GIZLI-SERI" not in json.dumps(items, ensure_ascii=False), "donanım seri numarası sızmıyor")
    kinds = {i["kind"] for i in items}
    chk(kinds == {"remote_session", "quarantine", "update", "enroll", "capability", "windows_update", "command"},
        "tüm türler: %s" % sorted(kinds))
    cap = [i for i in items if i["kind"] == "capability"][0]
    chk(cap["title"] == "Uzaktan izleme kapatıldı" and cap["actor"] == "Pasha", "yetenek değişikliği ve yapan")
    cmd = [i for i in items if i["kind"] == "command"][0]
    chk(cmd["detail"] == "Durum: Completed" and cmd["actor"] == "Pasha", "komut içeriği yok, yapan var")


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


def main():
    test_update_notice()
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
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("TUM BIRIM TESTLERI GECTI")


if __name__ == "__main__":
    main()
