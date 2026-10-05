#!/usr/bin/env python3
"""POps demo verisi: migration'lardan sonra, filo bağlanmadan ÖNCE bir kez çalışır (deploy/demo/reset.sh).

Yazdıkları:
  - salt okunur demo kullanıcısı (POPS_DEMO_LOGIN, ör. demo:demo; viewer, bütün izleyici sayfaları) ve yönetici
    hesabı yoksa rastgele şifreli bir superadmin (şifre yalnızca bu durumda bir kez yazılır);
  - filonun kayıt jetonu (DEMO_ENROLL_TOKEN; yalnızca özeti saklanır, filo boyu kadar kullanım, 2 gün geçerli);
  - kurum adı "POps Demo Okulu", DNS politikası, sınıflar, öğretmen bilgisayarları ve bilgisayar kayıtları (sınıfları
    belli olsun diye; filo bağlanınca aynı kayıtları kullanır);
  - son 2 haftanın geçmişi: oturumlar, kural ihlalleri, karantina, ajan güncellemesi, görevler ve çıktıları, uzak
    ekran oturumları, yardım masası talepleri, bildirimler; hash zincirli denetim kaydı zincire uygun yazılır;
  - birkaç lisans ve zamanlanmış görev.

Yeniden çalıştırılabilir: kullanıcı, jeton ve ayarlar güncellenir; geçmiş yalnızca ilk çalıştırmada yazılır
(global_settings.demo_seeded). Bilgisayar listesi tools/demo_fleet.py'den gelir (tek kaynak).

Ortam: DB_HOST, DB_PORT, DB_USER, DB_PASS, DB_NAME, POPS_DEMO_LOGIN, DEMO_ENROLL_TOKEN, PANEL_ADMIN_USER,
PANEL_ADMIN_PASS (isteğe bağlı).
"""

import asyncio
import datetime
import json
import os
import random
import secrets
import sys
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
for _p in (os.path.join(HERE, "..", "..", "tools"), "/opt/pops-tools", os.path.join(HERE, "..", "..", "Backend"),
           "/app"):
    if os.path.isdir(_p):
        sys.path.insert(0, _p)

import asyncpg  # noqa: E402
import bcrypt  # noqa: E402

import demo_fleet as fleet  # noqa: E402
from pops import auditchain  # noqa: E402  (bağımlılıksız: yalnızca hash zinciri)

TS = "%Y-%m-%d %H:%M:%S"
ORG_NAME = "POps Demo Okulu"
IT = "bt.sorumlusu"               # geçmişteki işlemleri yapan BT sorumlusu (panel kullanıcısı değil, yalnızca ad)
OFFICE_IP = "10.20.0.15"
VIEWER_PAGES = ["devices", "labs", "tasks", "vision", "policies", "logger", "reports", "helpdesk"]
AUDIT_CHAIN_LOCK = 0x504F6175     # pops/audit.py ile aynı kilit
FAIR_USE = ("Bu bilgisayar okulumuz tarafından POps ile yönetilmektedir. Bilgisayarın açık/kapalı durumu, donanım "
            "bilgileri ve belirlenen yasaklı alan adlarına erişim kaydedilir. Klavye, şifre ve sayfa içerikleri "
            "kaydedilmez. Yönetici ekranı yalnızca kayıtlı bir oturumda görüntüleyebilir; uzaktan kontrol için ekranda "
            "bildirim gösterilir. Ayrıntılı bilgi için okul yönetiminin KVKK aydınlatma metnine bakınız.")


def read_version() -> str:
    for path in ("/app/VERSION", os.path.join(HERE, "..", "..", "VERSION")):
        try:
            with open(path, encoding="utf-8") as f:
                return f.read().strip() or "0.1.0"
        except OSError:
            continue
    return "0.1.0"


def previous_version(v: str) -> str:
    base, _, rest = v.partition("-")
    parts = base.split(".")
    if len(parts) == 3 and parts[2].isdigit() and int(parts[2]) > 0:
        parts[2] = str(int(parts[2]) - 1)
    return ".".join(parts) + ("-" + rest if rest else "")


def aware(dt: datetime.datetime) -> datetime.datetime:
    return dt.astimezone()


class History:
    """Geçmiş satırları; zaman sırasına dizilip yazılır (kimlik sırası = zaman sırası)."""

    def __init__(self, devices, admin):
        self.rng = random.Random("pops-demo-" + datetime.date.today().isoformat())
        self.devices = devices
        self.by_name = {d.hostname: d for d in devices}
        self.admin = admin
        self.cal = fleet.Calendar("school", 0.4)
        self.events, self.audits, self.tasks, self.notes, self.sessions, self.tickets = [], [], [], [], [], []
        self.alerts = {}   # cihaz -> son kural ihlali zamanları (saatte en fazla 2; eşik 3)

    # ---- yardımcılar
    def on(self, d, at):
        return self.cal.want_on(d, at, 0.0)

    def event(self, at, pc, log_type, message, actor, event_type, category, action, risk, reason="", meta=None):
        # pops/audit.py log_audit_event ile aynı önem eşlemesi
        if risk == "info":
            if log_type in ("Error", "Critical Security"):
                risk = "critical"
            elif log_type in ("Security", "Warning"):
                risk = "medium"
        self.events.append((at, pc, actor, event_type, category, action, risk, reason, message, meta or {}))

    def audit(self, at, hw, action, reason, changes):
        self.audits.append((at, hw, action, reason, changes))

    def note(self, at, event, severity, pc, title, detail="", read=True):
        self.notes.append((at, event, severity, pc, title, detail, read))

    def school_days(self, today):
        return [today - datetime.timedelta(days=k) for k in range(14, 0, -1)
                if (today - datetime.timedelta(days=k)).weekday() < 5]

    @staticmethod
    def at(day, minute, second=0):
        return datetime.datetime.combine(day, datetime.time()) + datetime.timedelta(minutes=minute, seconds=second)

    # ---- oturumlar, hatalı girişler, kural ihlalleri
    def sessions_for(self, day):
        out = []   # (cihaz, kullanıcı, giriş, çıkış)
        for d in self.devices:
            if d.is_teacher:
                slots = ((495, 750), (800, 945))
            elif d.profile == "kut":
                slots = tuple((m, m + 20) for m in range(480, 1050, 20))
            else:
                slots = fleet.LESSONS
            for a, b in slots:
                start = self.at(day, a + 1)
                user = self.cal.user_for(d, start)
                if not user or not self.on(d, start):
                    continue
                login = self.at(day, a, self.rng.randint(20, 240))
                logout = self.at(day, b - 1, 0) - datetime.timedelta(seconds=self.rng.randint(0, 150))
                out.append((d, user, login, logout))
        return out

    def daily_activity(self, day):
        sessions = self.sessions_for(day)
        for d, user, login, logout in sessions:
            self.event(login, d.hw_id, "Security", "🟢 GİRİŞ: %s" % user, user, "auth.login", "security", "login",
                       "info")
            self.event(logout, d.hw_id, "Security", "⚪ OTURUM KAPATILDI", "System", "auth.logout", "security",
                       "logout", "info")
        students = [s for s in sessions if not s[0].is_teacher]
        if not students:
            return
        for _ in range(self.rng.randint(2, 5)):
            d, _user, login, logout = self.rng.choice(students)
            at = login - datetime.timedelta(seconds=self.rng.randint(20, 90))
            who = "ogr%04d" % self.rng.randint(1000, 9999)
            self.event(at, d.hw_id, "Security", "🔴 RED: %s (Kullanıcı adı veya parola yanlış)" % who, who,
                       "auth.failed", "security", "login_failed", "medium", "Kullanıcı adı veya parola yanlış")
        for _ in range(self.rng.randint(5, 12)):
            d, _user, login, logout = self.rng.choice(students)
            span = max(60, int((logout - login).total_seconds()) - 60)
            at = login + datetime.timedelta(seconds=self.rng.randint(60, span))
            recent = [t for t in self.alerts.get(d.hw_id, []) if abs((at - t).total_seconds()) < 3600]
            if len(recent) >= 2:
                continue
            self.alerts.setdefault(d.hw_id, []).append(at)
            self.policy_alert(at, d, self.rng.choice(fleet.DEFAULT_DNS["okul_ozel"]))

    def policy_alert(self, at, d, domain, category="okul_ozel"):
        self.event(at, d.hw_id, "Security", "🚨 KURAL İHLALİ: %s (%s)" % (domain, category), d.hw_id, "policy.alert",
                   "restricted_content", "dns_block", "high", "DNS Kural İhlali",
                   {"domain": domain, "violation_category": category})
        self.note(at, "policy_alert", "high", d.hw_id, "Kural ihlali: %s" % category, "Alan adı: %s" % domain)

    # ---- görevler
    def job(self, at, targets, command, title, source, by=IT, reason=None, fail=None, schedule_id=None):
        batch = uuid.uuid4().hex[:16]
        for i, d in enumerate(targets):
            created = at
            # Zamanlanmış görev bir saat geçerlidir: bilgisayar o saat içinde açılırsa o zaman çalışır
            waits = (0, 15, 30, 45) if schedule_id is not None else (0,)
            start = next((m for m in waits if self.on(d, at + datetime.timedelta(minutes=m, seconds=30))), None)
            if schedule_id is not None and start is None:
                status, out, code, dispatched = "Expired", None, None, None
            elif start is None:
                continue   # panel kapalı bilgisayarı atlar
            else:
                out, _effect = fleet.canned_output(command, d)
                status, code = "Completed", 0
                if fail and d.hostname in fail:
                    status, code, out = "Failed", 1, fail[d.hostname]
                dispatched = created + datetime.timedelta(minutes=start, seconds=1 + i // 5)
            self.tasks.append({
                "target_pc": d.hw_id, "target_lab": d.lab, "script_path": command, "status": status,
                "created_at": created, "output": out, "created_by": by, "exit_code": code,
                "dispatched_at": dispatched, "title": title, "source": source, "reason": reason,
                "client_ip": None if schedule_id else OFFICE_IP, "batch_id": batch, "schedule_id": schedule_id,
                "expires_at": created + datetime.timedelta(minutes=60) if schedule_id else None,
            })

    def daily_jobs(self, day, idx):
        labs = {lab: [d for d in self.devices if d.lab == lab] for lab in {d.lab for d in self.devices}}
        bl = labs["Bilişim Lab 1"] + labs["Bilişim Lab 2"]
        self.job(self.at(day, 755), bl, 'cmd /c del /q /f /s "%TEMP%\\*"', "Geçici dosyaları temizle", "schedule",
                 by="%s (zamanlanmış #1)" % IT, schedule_id=1)
        if day.weekday() == 0:
            self.job(self.at(day, 525), self.devices, "gpupdate /force", "Grup ilkesini güncelle", "schedule",
                     by="%s (zamanlanmış #2)" % IT, schedule_id=2)
        r = self.rng
        lab = r.choice(["Bilişim Lab 1", "Bilişim Lab 2", "Fen Lab"])
        lesson = r.choice(fleet.LESSONS)
        self.job(self.at(day, lesson[1] - 6), labs[lab], 'msg * /TIME:120 "Ders bitiyor, çalışmalarınızı kaydedin."',
                 "Mesaj", "labs")
        pcs = r.sample([d for d in self.devices if not d.is_teacher], 2)
        self.job(self.at(day, r.randint(560, 900)), pcs, "shutdown /r /f /t 5", "Yeniden başlat", "devices")
        extra = idx % 4
        if extra == 0:
            self.job(self.at(day, 970), labs["Bilişim Lab 2"], "shutdown /s /f /t 5", "Kapat · Bilişim Lab 2", "labs",
                     reason="Ders sonu")
        elif extra == 1:
            self.job(self.at(day, 760), labs["Bilişim Lab 2"],
                     "winget upgrade --id Google.Chrome --silent --accept-source-agreements", "Chrome güncellemesi",
                     "deploy", fail={"BL2-PC09": "Yükleyici başarısız oldu, çıkış kodu: 1603\r\n"})
        elif extra == 2:
            self.job(self.at(day, 765), labs["Fen Lab"],
                     "winget install --id GeoGebra.Classic --silent --accept-package-agreements",
                     "GeoGebra Classic", "deploy", by=self.admin)
        else:
            pc = r.choice(labs["Kütüphane"])
            self.job(self.at(day, r.randint(600, 880)), [pc], "ipconfig /flushdns", "Komut", "terminal")
            self.job(self.at(day, r.randint(600, 880)), [pc],
                     'powershell -NoProfile -Command "Get-Volume -DriveLetter C"', "Komut", "terminal")

    # ---- olay hikâyeleri
    def stories(self, days, version):
        old = previous_version(version)
        if len(days) < 9:
            return
        # Sınav sırasında yönetici karantinası
        d9, d = days[-8], self.by_name["BL2-PC05"]
        lock, unlock = self.at(d9, 665), self.at(d9, 700)
        reason = "Sınav sırasında izinsiz site kullanımı"
        self.event(lock, d.hw_id, "Critical Security", "🚨 KARANTİNA BAŞLATILDI by %s - Neden: %s" % (IT, reason), IT,
                   "security.lockdown", "security", "lockdown", "critical", reason)
        self.audit(lock, d.hw_id, "lockdown", "Karantina başlatıldı: %s" % IT, {"admin": IT, "reason": reason})
        self.event(unlock, d.hw_id, "Critical Security", "✅ KARANTİNA KALDIRILDI by %s - Neden: Sınav bitti" % IT, IT,
                   "security.unlock", "security", "unlock", "info", "Sınav bitti")
        self.audit(unlock, d.hw_id, "unlock", "Karantina kaldırıldı: %s" % IT, {"admin": IT, "reason": "Sınav bitti"})

        # DNS eşiğinde kendiliğinden karantina, BT sorumlusu kaldırır
        d5, d = days[-4], self.by_name["BL1-PC09"]
        for k, dom in enumerate(("poki.com", "crazygames.com", "friv.com")):
            self.policy_alert(self.at(d5, 615 + 7 * k, 12), d, dom)
        q = self.at(d5, 629, 40)
        msg = "Cihaz DNS kural ihlali eşiğinde kendini karantinaya aldı"
        self.event(q, d.hw_id, "Security", msg, "Agent", "agent.auto_quarantine", "security", "auto_quarantine", "high",
                   "3 ihlal / 1 saat (okul_ozel)")
        self.audit(q, d.hw_id, "auto_quarantine", msg, {"event_type": "agent.auto_quarantine",
                                                        "reason": "3 ihlal / 1 saat (okul_ozel)"})
        self.note(q, "auto_quarantine", "high", d.hw_id, "Cihaz kural ihlali eşiğinde kendini karantinaya aldı",
                  "3 ihlal / 1 saat (okul_ozel)")
        u = self.at(d5, 652)
        why = "Öğrenciyle görüşüldü, uyarıldı"
        self.event(u, d.hw_id, "Critical Security", "✅ KARANTİNA KALDIRILDI by %s - Neden: %s" % (IT, why), IT,
                   "security.unlock", "security", "unlock", "info", why)
        self.audit(u, d.hw_id, "unlock", "Karantina kaldırıldı: %s" % IT, {"admin": IT, "reason": why})

        # Ajan güncellemesi: iki bilgisayar kapalıydı, biri geri döndü (eski sürümde kaldılar)
        d6 = days[-5]
        t0 = self.at(d6, 945)
        skipped = {"BL2-PC03", "BL2-PC11"}
        sent = [d for d in self.devices if d.hostname not in skipped]
        self.audit(t0, "*", "deploy_update", "İmzalı güncelleme dağıtıldı: %s" % version,
                   {"version": version, "msi": "POpsAgent-%s.msi" % version, "dispatched": [d.hw_id for d in sent],
                    "offline": [self.by_name[n].hw_id for n in sorted(skipped)], "by": self.admin})
        for i, d in enumerate(sent):
            at = t0 + datetime.timedelta(seconds=90 + i * 7)
            if d.hostname == "KUT-PC05":
                detail = {"status": "rolled_back", "from_version": old, "to_version": version,
                          "detail": "Yeni sürüm açılışta hizmet olarak başlamadı; önceki sürüme dönüldü",
                          "rollback": True, "agent_state": "rolled_back", "msi_exit_code": 0,
                          "reboot_required": False, "running_version": old}
                self.event(at, d.hw_id, "Critical Security", "Ajan guncelleme sorunu: rolled_back", "System/Update",
                           "agent.update", "system_maintenance", "update_problem", "critical", "rolled_back", detail)
                self.note(at, "update_rolled_back", "high", d.hw_id, "Ajan güncellemesi geri alındı",
                          detail["detail"])
            else:
                detail = {"status": "success", "from_version": old, "to_version": version, "detail": None,
                          "rollback": False, "agent_state": "running", "msi_exit_code": 0, "reboot_required": False,
                          "running_version": version}
            self.audit(at, d.hw_id, "update_result", "Ajan guncelleme sonucu: %s" % detail["status"], detail)

        # Öğretmen bilgisayarında uzak ekran kapalı: istek reddedildi
        d3, d = days[-3], self.by_name["FEN-OGRETMEN"]
        at = self.at(d3, 620)
        meta = {"capability": "vision", "action": "start_vision_session", "task_id": None, "reason": None}
        self.event(at, d.hw_id, "Security", "Yetenek reddedildi: vision (start_vision_session)", "Agent",
                   "agent.capability_denied", "security", "capability_denied", "medium", "vision", meta)
        self.note(at, "capability_denied", "medium", d.hw_id, "Kapalı yetenek istendi, ajan reddetti: vision",
                  "start_vision_session")

        # Uzak ekran oturumları
        for k, (host, reason, mandatory) in enumerate((
                ("BL1-PC04", "Öğrenci yardım istedi: Scratch projesi açılmıyor", False),
                ("KUT-PC01", "Uzaktan destek: yazıcı ayarı", False),
                ("BL2-PC08", "Sınav sırasında ekran kontrolü (öğretmen talebi)", True),
                ("FEN-PC03", "GeoGebra kurulumu sonrası kontrol", False))):
            day = days[-(2 + 2 * k)]
            d = self.by_name[host]
            start = self.at(day, 590 + 37 * k)
            if not self.on(d, start):
                continue
            sid = "SES-%s" % uuid.uuid4().hex[:12].upper()
            self.sessions.append((sid, host, d.hw_id, start, start + datetime.timedelta(minutes=6 + k), reason,
                                  mandatory))
            self.audit(start, d.hw_id, "remote_session_start",
                       "Uzaktan denetim oturumu açıldı: %s → %s" % (IT, d.hw_id),
                       {"session_id": sid, "admin": IT, "role": "admin", "reason": reason, "mandatory": mandatory})

    def tickets_history(self, today):
        rows = (
            (12, "BL2-PC06", "donanim", "Fare çalışmıyor", "İmleç hareket etmiyor.", "resolved",
             [(IT, "Fare değiştirildi.", False)]),
            (10, "KUT-PC02", "yazici", "Yazıcı çıktı vermiyor", "Belge kuyrukta bekliyor.", "closed",
             [(IT, "Yazdırma kuyruğu temizlendi, sürücü yeniden kuruldu.", False)]),
            (8, "FEN-PC04", "yazilim", "GeoGebra dosyası açılmıyor", "Paylaşılan .ggb dosyası hata veriyor.",
             "resolved", [(IT, "GeoGebra Classic güncellendi, dosya açılıyor.", False)]),
            (7, "BL1-PC05", "donanim", "Kulaklıktan ses gelmiyor", "", "resolved",
             [(IT, "Ses çıkışı kulaklığa alındı.", False)]),
            (4, "BL2-PC12", "ag", "İnternet çok yavaş", "Sayfalar geç açılıyor.", "in_progress",
             [(IT, "Kat anahtarında port hatası olabilir, kontrol edilecek.", True)]),
            (2, "KUT-PC04", "hesap", "Şifremi unuttum", "Okul hesabımın şifresini hatırlamıyorum.", "waiting",
             [(IT, "Müdür yardımcısına başvurun; şifre sıfırlama formu dolduruluyor.", False)]),
            (1, "FEN-OGRETMEN", "donanim", "Akıllı tahta bağlantısı kopuyor", "Ders sırasında iki kez koptu.", "open",
             []),
            (1, "BL1-PC14", "yazilim", "Scratch açılmıyor", "Simgeye tıklayınca bir şey olmuyor.", "open", []),
        )
        for ago, host, cat, subject, body, status, msgs in rows:
            d = self.by_name[host]
            created = self.at(today - datetime.timedelta(days=ago), 600 + 13 * ago)
            reporter = d.teacher_user or "ogr%04d" % (1000 + int(fleet._h(host, subject) * 8999))
            self.tickets.append(("agent", d.hw_id, reporter, cat, subject, body, status, "normal", created, msgs))
            if status == "open":
                self.note(created, "ticket_new", "medium", d.hw_id, "Yeni destek talebi: %s" % subject,
                          "%s · %s" % (reporter, body), read=False)
        self.tickets.append(("panel", None, "Müdür yardımcısı", "diger", "Kütüphaneye 2 yeni bilgisayar kurulacak",
                             "Yeni gelen iki bilgisayar kütüphaneye kurulup POps'a kaydedilecek.", "open", "low",
                             self.at(today - datetime.timedelta(days=3), 845), []))


async def write_history(c, h: History):
    # Önce görevler (denetim kaydı görev kimliğini taşır); sonra bütün olaylar tek seferde, zaman sırasıyla: panel
    # kayıtları kimlik sırasıyla gösterir
    h.tasks.sort(key=lambda t: t["created_at"])
    for t in h.tasks:
        tid = await c.fetchval(
            "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, output, created_by, exit_code, "
            "dispatched_at, title, source, reason, client_ip, batch_id, schedule_id, expires_at) "
            "VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16) RETURNING id",
            t["target_pc"], t["target_lab"], t["script_path"], t["status"], t["created_at"].strftime(TS), t["output"],
            t["created_by"], t["exit_code"], aware(t["dispatched_at"]) if t["dispatched_at"] else None, t["title"],
            t["source"], t["reason"], t["client_ip"], t["batch_id"], t["schedule_id"],
            aware(t["expires_at"]) if t["expires_at"] else None,
        )
        if t["dispatched_at"]:
            # Görev kuyruğunun yazdıkları (pops/taskqueue.py): olay günlüğü ve hash zincirli denetim kaydı
            h.event(t["dispatched_at"], t["target_pc"], "Deploy", "Görev: %s" % t["script_path"][:50],
                    t["created_by"], "deploy.execution", "system_maintenance", "execute_queue", "info", "",
                    {"raw_command": t["script_path"], "created_by": t["created_by"]})
            h.audit(t["dispatched_at"], t["target_pc"], "execute", "SYSTEM komutu çalıştırıldı (kuyruk: %s)"
                    % t["created_by"], {"task_id": tid, "created_by": t["created_by"],
                                        "command": t["script_path"][:200]})
    h.events.sort(key=lambda r: r[0])
    await c.executemany(
        "INSERT INTO agent_logs_v2 (pc_name, actor_id, event_type, category, action, risk_level, reason, message, "
        "meta_data, timestamp) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
        [(pc, actor, et, cat, act, risk, reason, msg, json.dumps(meta, ensure_ascii=False), at.strftime(TS))
         for at, pc, actor, et, cat, act, risk, reason, msg, meta in h.events],
    )
    # Hash zinciri: pops/audit.py ile aynı kilit ve özet; zincirin o anki ucundan devam eder
    h.audits.sort(key=lambda r: r[0])
    async with c.transaction():
        await c.execute("SELECT pg_advisory_xact_lock($1)", AUDIT_CHAIN_LOCK)
        prev = await c.fetchval("SELECT entry_hash FROM device_audit_logs ORDER BY id DESC LIMIT 1")
        rows = []
        for at, hw, action, reason, changes in h.audits:
            payload, ts = json.dumps(changes, ensure_ascii=False), at.strftime(TS)
            entry = auditchain.entry_hash(prev, hw, action, reason, payload, ts)
            rows.append((hw, action, reason, payload, ts, prev, entry))
            prev = entry
        await c.executemany(
            "INSERT INTO device_audit_logs (hw_id, action, reason, changes, timestamp, prev_hash, entry_hash) "
            "VALUES ($1,$2,$3,$4,$5,$6,$7)", rows)
    await c.executemany(
        "INSERT INTO notifications (created_at, event, severity, pc_name, title, detail, is_read) "
        "VALUES ($1,$2,$3,$4,$5,$6,$7)",
        [(aware(at), ev, sev, pc, title, detail, read)
         for at, ev, sev, pc, title, detail, read in sorted(h.notes, key=lambda n: n[0])],
    )
    for sid, host, hw, start, end, reason, mandatory in h.sessions:
        await c.execute(
            "INSERT INTO enterprise_audit_logs (session_id, admin_id, admin_name, admin_role, target_pc, start_time, "
            "end_time, reason, is_notified, is_mandatory, status) VALUES ($1,NULL,$2,'admin',$3,$4,$5,$6,TRUE,$7,"
            "'Completed')", sid, IT, hw, start.strftime(TS), end.strftime(TS), reason, mandatory)
    for source, pc, reporter, cat, subject, body, status, prio, created, msgs in h.tickets:
        tid = await c.fetchval(
            "INSERT INTO tickets (created_at, updated_at, source, pc_name, reporter, category, subject, body, status, "
            "priority, assignee, resolved_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12) RETURNING id",
            aware(created), aware(created + datetime.timedelta(hours=2 if msgs else 0)), source, pc, reporter, cat,
            subject, body, status, prio, IT if msgs else None,
            aware(created + datetime.timedelta(hours=2)) if status in ("resolved", "closed") else None)
        for k, (author, text, internal) in enumerate(msgs):
            await c.execute("INSERT INTO ticket_messages (ticket_id, created_at, author, body, internal) "
                            "VALUES ($1,$2,$3,$4,$5)", tid, aware(created + datetime.timedelta(minutes=40 + 30 * k)),
                            author, text, internal)


def next_weekly(time_of_day: str, weekdays: str) -> datetime.datetime:
    hh, mm = (int(x) for x in time_of_day.split(":"))
    days = {int(x) for x in weekdays.split(",")}
    now = datetime.datetime.now().astimezone()
    for add in range(0, 8):
        cand = (now + datetime.timedelta(days=add)).replace(hour=hh, minute=mm, second=0, microsecond=0)
        if cand > now and cand.isoweekday() in days:
            return cand
    return now + datetime.timedelta(days=1)


async def seed_settings(c, devices, admin, token):
    """Her çalıştırmada: kullanıcılar, kayıt jetonu, kurum, politika, sınıflar, bilgisayar kayıtları."""
    login = os.environ.get("POPS_DEMO_LOGIN", "demo:demo")
    user, _, password = login.partition(":")
    if not user or not password:
        raise SystemExit("POPS_DEMO_LOGIN 'kullanıcı:şifre' olmalı")
    pw = bcrypt.hashpw(password.encode(), bcrypt.gensalt()).decode()
    await c.execute(
        "INSERT INTO users (username, password_hash, role, permissions) VALUES ($1, $2, 'viewer', $3) "
        "ON CONFLICT (username) DO UPDATE SET password_hash = $2, role = 'viewer', permissions = $3, "
        "totp_enabled = FALSE, totp_secret = NULL, token_version = users.token_version + 1",
        user, pw, json.dumps(VIEWER_PAGES))
    print("demo kullanıcısı: %s (viewer, salt okunur)" % user)

    if not await c.fetchval("SELECT 1 FROM users WHERE username = $1", admin):
        admin_pw = os.environ.get("PANEL_ADMIN_PASS") or secrets.token_urlsafe(18)
        await c.execute("INSERT INTO users (username, password_hash, role) VALUES ($1, $2, 'superadmin')",
                        admin, bcrypt.hashpw(admin_pw.encode(), bcrypt.gensalt()).decode())
        if os.environ.get("PANEL_ADMIN_PASS"):
            print("yönetici hesabı oluşturuldu: %s (şifre .env.demo'daki PANEL_ADMIN_PASS)" % admin)
        else:
            print("yönetici hesabı oluşturuldu: %s  şifre: %s  (yalnızca bu kez gösterilir)" % (admin, admin_pw))

    if token:
        await c.execute(
            "INSERT INTO enroll_tokens (token_hash, token_hint, lab_name, note, expires_at, max_uses) "
            "VALUES (encode(sha256(convert_to($1, 'UTF8')), 'hex'), left($1, 6), NULL, 'Demo filosu', "
            "NOW() + interval '2 days', $2) ON CONFLICT (token_hash) DO UPDATE SET "
            "expires_at = NOW() + interval '2 days', is_used = FALSE, max_uses = enroll_tokens.use_count + $2",
            token, len(devices))
    else:
        print("UYARI: DEMO_ENROLL_TOKEN yok; filo kaydolamaz")

    policies = {"fair_use_text": FAIR_USE, "dns_categories": ["okul_ozel"], "auto_quarantine": True,
                "quarantine_threshold": 3, "dns_domains": fleet.DEFAULT_DNS}
    meta = {"updated_by": IT, "updated_at": aware(datetime.datetime.now() - datetime.timedelta(days=12)).isoformat()}
    for key, value in (("branding_org_name", ORG_NAME), ("agent_policies", json.dumps(policies, ensure_ascii=False)),
                       ("agent_policies_meta", json.dumps(meta)), ("concurrent_limit", "10")):
        await c.execute("INSERT INTO global_settings (key, value) VALUES ($1, $2) "
                        "ON CONFLICT (key) DO UPDATE SET value = $2", key, value)

    for lab in sorted({d.lab for d in devices}):
        await c.execute("INSERT INTO custom_labs (lab_name) VALUES ($1) ON CONFLICT DO NOTHING", lab)
    for d in devices:
        if d.is_teacher:
            await c.execute("INSERT INTO lab_settings (lab_name, main_pc) VALUES ($1, $2) "
                            "ON CONFLICT (lab_name) DO UPDATE SET main_pc = $2", d.lab, d.hw_id)
    # Bilgisayar kayıtları önceden: sınıfları belli olur (tek kayıt jetonu sınıf taşımaz). Filo bağlanınca donanım
    # bilgisi aynı olduğu için aynı kayıtları kullanır.
    yesterday = datetime.datetime.now() - datetime.timedelta(days=1)
    for d in devices:
        hw = d.dna()["hardware"]
        last = yesterday.replace(hour=16, minute=30) + datetime.timedelta(minutes=int(fleet._h(d.hostname, "l") * 40))
        await c.execute(
            "INSERT INTO clients (pc_name, hostname, lab_name, last_seen, status, active_window, boot_count, "
            "ip_address, dna_uuid, dna_bios, dna_disk, dna_mac, dna_ram, cap_ram_readable, last_disconnect_at, "
            "last_disconnect_reason) VALUES ($1,$2,$3,$4,'Offline','-',$5,$6,$7,$8,$9,$10,$11,TRUE,$12,$13) "
            "ON CONFLICT (pc_name) DO UPDATE SET lab_name = $3",
            d.hw_id, d.hostname, d.lab, last.strftime(TS), 40 + int(fleet._h(d.hostname, "boot") * 260), d.ip,
            hw["uuid"], hw["bios_sn"], hw["disk_sn"], hw["mac"], hw["ram_sn"], aware(last),
            "ajan kapattı (servis duruyor ya da yeniden başlıyor)")


async def seed_static(c, devices):
    today = datetime.date.today()
    for name, pattern, publisher, seats, kind, expires, notes in (
            ("Microsoft Office LTSC Standard 2021", "Microsoft Office LTSC", "Microsoft", 40, "per_device", None,
             "2024 alımı, 40 cihaz. Fen Lab'a kurulumla aşıldı; ek lisans istendi."),
            ("ESET Endpoint Security", "ESET Endpoint Security", "ESET", 60, "subscription",
             today + datetime.timedelta(days=24), "Yıllık abonelik; yenileme teklifi bekleniyor."),
            ("Zoom Workplace (öğretmen)", "Zoom Workplace", "Zoom", 2, "subscription",
             today + datetime.timedelta(days=210), "Öğretmen bilgisayarları")):
        await c.execute("INSERT INTO licenses (name, match_pattern, publisher, seats, license_type, expires_at, notes, "
                        "created_by) VALUES ($1,$2,$3,$4,$5,$6,$7,$8)",
                        name, pattern, publisher, seats, kind, expires, notes, IT)
    for sid, name, command, mode, targets, tod, days, enabled, last in (
            (1, "Geçici dosyaları temizle", 'cmd /c del /q /f /s "%TEMP%\\*"', "LAB",
             ["Bilişim Lab 1", "Bilişim Lab 2"], "12:35", "1,2,3,4,5", True, "33 cihaz için kuyruğa eklendi"),
            (2, "Grup ilkesini güncelle", "gpupdate /force", "ALL", [], "08:45", "1", True,
             "%d cihaz için kuyruğa eklendi" % len(devices)),
            (3, "Haftalık yeniden başlatma", "shutdown /r /f /t 60", "LAB",
             ["Bilişim Lab 1", "Bilişim Lab 2", "Fen Lab"], "15:50", "5", False, None)):
        await c.execute(
            "INSERT INTO scheduled_tasks (id, name, command, target_mode, targets, schedule_type, time_of_day, "
            "weekdays, enabled, next_run, last_run, last_result, created_by, created_at) "
            "VALUES ($1,$2,$3,$4,$5,'weekly',$6,$7,$8,$9,$10,$11,$12,$13)",
            sid, name, command, mode, json.dumps(targets, ensure_ascii=False), tod, days, enabled,
            next_weekly(tod, days) if enabled else None,
            next_weekly(tod, days) - datetime.timedelta(days=7) if last else None, last, IT,
            aware(datetime.datetime.now() - datetime.timedelta(days=20)))
    await c.execute("SELECT setval(pg_get_serial_sequence('scheduled_tasks', 'id'), 3)")


async def main():
    devices = fleet.build_fleet()
    admin = os.environ.get("PANEL_ADMIN_USER") or "admin"
    c = await asyncpg.connect(host=os.environ.get("DB_HOST", "localhost"), port=int(os.environ.get("DB_PORT", "5432")),
                              user=os.environ["DB_USER"], password=os.environ["DB_PASS"],
                              database=os.environ["DB_NAME"])
    try:
        await seed_settings(c, devices, admin, os.environ.get("DEMO_ENROLL_TOKEN", "").strip())
        if await c.fetchval("SELECT 1 FROM global_settings WHERE key = 'demo_seeded'"):
            print("geçmiş zaten yazılmış; atlandı")
            return
        await seed_static(c, devices)
        h = History(devices, admin)
        today = datetime.date.today()
        days = h.school_days(today)
        for i, day in enumerate(days):
            h.daily_activity(day)
            h.daily_jobs(day, i)
        h.stories(days, read_version())
        h.tickets_history(today)
        await write_history(c, h)
        await c.execute("INSERT INTO global_settings (key, value) VALUES ('demo_seeded', $1)",
                        datetime.datetime.now().isoformat(timespec="seconds"))
        print("geçmiş yazıldı: %d olay, %d görev, %d denetim kaydı, %d bildirim, %d talep (%d okul günü)"
              % (len(h.events), len(h.tasks), len(h.audits), len(h.notes), len(h.tickets), len(days)))
    finally:
        await c.close()


if __name__ == "__main__":
    asyncio.run(main())
