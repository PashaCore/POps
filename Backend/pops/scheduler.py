"""Zamanlayıcı: vakti gelen zamanlanmış görevleri normal görev kuyruğuna ekler ve güncelleme
gönderilip sonucu hiç gelmeyen ajanlar için bildirim üretir. Açılışta başlatılır, 30 sn'de bir döner.

Saatler sunucunun saat diliminde yorumlanır. Birden fazla backend süreci çalışsa bile aynı görev iki
kez eklenmez: tur, PostgreSQL advisory kilidiyle tek sürece verilir ve satırlar FOR UPDATE ile alınır.
"""

import asyncio
import datetime
import json
import logging
import os
import time
import uuid
from typing import Optional

from pops import db, health_alerts, modules, retention, update_tracking
from pops.audit import add_audit_log
from pops.manager import manager
from pops.notify import notify
from pops.taskqueue import process_queue, resolve_targets

log = logging.getLogger("pops.scheduler")

TICK_SECONDS = 30
# Zamanlanmış çalışmanın geçerliliği (dk, planlanan zamandan itibaren): cihaz bu sürede bağlanıp görevi almazsa görev
# başlatılmaz ("Expired"). Gece için konmuş bir "kapat" komutu, cihaz sabah açılınca çalışmasın.
SCHEDULE_VALID_MINUTES = int(os.environ.get("SCHEDULE_VALID_MINUTES", "60"))
# Sunucu kapalıyken kaçan çalışma: planlanan zamandan bu kadar sonra fark edilirse hiç çalıştırılmaz, "kaçırıldı"
# diye kaydedilir (eskiden saatler sonra koşulsuz çalışıyordu)
SCHEDULE_MISFIRE_MINUTES = int(os.environ.get("SCHEDULE_MISFIRE_MINUTES", "60"))
UPDATE_SILENCE_SECONDS = 20 * 60
_SCHEDULER_LOCK = 0x504F5053  # "POPS"


def _now() -> datetime.datetime:
    return datetime.datetime.now().astimezone()


def compute_next_run(
    schedule_type: str,
    run_at: Optional[datetime.datetime],
    time_of_day: Optional[str],
    weekdays: Optional[str],
    after: datetime.datetime,
) -> Optional[datetime.datetime]:
    """`after`dan sonraki ilk çalışma zamanı; tek seferlik görev geçmişte kaldıysa None."""
    if schedule_type == "once":
        return run_at if run_at and run_at > after else None
    try:
        hh, mm = (int(x) for x in (time_of_day or "").split(":"))
    except ValueError:
        return None
    days = {int(d) for d in (weekdays or "").split(",") if d.strip().isdigit()} if schedule_type == "weekly" else None
    base = after.astimezone()
    for add in range(0, 8):
        cand = (base + datetime.timedelta(days=add)).replace(hour=hh, minute=mm, second=0, microsecond=0)
        if cand <= after:
            continue
        if days is None or cand.isoweekday() in days:
            return cand
    return None


async def enqueue(row: dict, actor_suffix: str, conn=None, expires_at=None) -> int:
    """Zamanlanmış görevi hedef cihazlar için görev kuyruğuna ekler; eklenen görev sayısını döner. Bütün hedefler
    tek işlemde yazılır: yarıda kalan bir ekleme (100 hedefin 40'ı) bırakmaz. conn verilirse çağıranın işlemi.

    Aynı takvimin o cihaz için hâlâ bekleyen bir çalışması varsa yenisi eklenmez: çevrimdışı cihaz döndüğünde
    biriken onlarca çalışma arka arkaya çalışmasın. expires_at verilirse görev o andan sonra başlatılmaz."""
    if conn is None:
        async with db.transaction() as own:
            return await enqueue(row, actor_suffix, own, expires_at)
    targets = await resolve_targets(row["target_mode"], json.loads(row["targets"] or "[]"), conn)
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    creator = "%s (%s #%s)" % (row.get("created_by") or "?", actor_suffix, row["id"])
    if targets:
        # Zamanlanmış görevler modülü kapalı laboratuvarlardaki cihazlar atlanır (uzak komut ayrıca kuyrukta denetlenir)
        allowed, _closed = await modules.split_pcs("schedules", [t["pc"] for t in targets])
        allowed = set(allowed)
        targets = [t for t in targets if t["pc"] in allowed]
    if not targets:
        return 0
    created = await conn.fetch(
        "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by, schedule_id, "
        "expires_at, title, source, batch_id) SELECT t.pc, t.lab, $3, 'Pending', $4, $5, $6, $7, $8, 'schedule', $9 "
        "FROM unnest($1::text[], $2::text[]) AS t(pc, lab) "
        "WHERE NOT EXISTS (SELECT 1 FROM tasks p WHERE p.schedule_id = $6 AND p.target_pc = t.pc "
        "AND p.status IN ('Pending', 'Paused')) RETURNING id",
        [t["pc"] for t in targets],
        [t["lab"] for t in targets],
        row["command"],
        now,
        creator,
        row["id"],
        expires_at,
        (row.get("name") or None),
        uuid.uuid4().hex[:16],
    )
    return len(created)


async def run_due() -> int:
    """Vakti gelen görevleri kuyruğa ekler. Eklenen toplam görev sayısını döner.

    F07: görevlerin eklenmesi ve takvimin ilerletilmesi (next_run, tek seferlikte enabled=false) AYNI işlemdedir.
    Eskiden takvim önce ilerletilip işlem kapatılıyordu; görevler ondan sonra yazıldığı için arada süreç ya da
    bağlantı ölürse iş hiç oluşmuyordu. Şimdi ya ikisi birden kalır ya hiçbiri: takvim bir sonraki turda yeniden
    dener. Hedef çözülemeyen (bozuk) bir takvim yine ilerletilir ve hatası kaydedilir, her turda tekrarlanmaz."""
    added = 0
    ran = []
    missed = []
    async with db.acquire() as conn:
        async with conn.transaction():
            if not await conn.fetchval("SELECT pg_try_advisory_xact_lock($1)", _SCHEDULER_LOCK):
                return 0
            rows = await conn.fetch(
                "SELECT * FROM scheduled_tasks WHERE enabled AND next_run IS NOT NULL AND next_run <= now() "
                "ORDER BY next_run FOR UPDATE SKIP LOCKED"
            )
            now = _now()
            for r in rows:
                r = dict(r)
                nxt = compute_next_run(r["schedule_type"], r["run_at"], r["time_of_day"], r["weekdays"], now)
                error = None
                late = (now - r["next_run"]).total_seconds() / 60
                if late > SCHEDULE_MISFIRE_MINUTES:
                    # Sunucu kapalıydı ya da zamanlayıcı durmuştu: planlanan saatten çok sonra çalıştırılmaz
                    result = "kaçırıldı: planlanan %s, %d dk geç fark edildi" % (
                        r["next_run"].astimezone().strftime("%Y-%m-%d %H:%M"), late)
                    missed.append(r)
                else:
                    try:
                        # Kayıt noktası: bir takvimin hatası diğerlerinin görevlerini geri almaz
                        expires = r["next_run"] + datetime.timedelta(minutes=SCHEDULE_VALID_MINUTES)
                        async with conn.transaction():
                            n = await enqueue(r, "zamanlanmış", conn, expires)
                        result = "%d cihaz için kuyruğa eklendi" % n
                        added += n
                    except (ValueError, TypeError) as exc:   # bozuk hedef listesi (JSON) gibi kalıcı hatalar
                        error = exc
                        result = "hata: %s" % exc
                await conn.execute(
                    "UPDATE scheduled_tasks SET last_run = now(), next_run = $1, enabled = $2, last_result = $3 "
                    "WHERE id = $4",
                    nxt,
                    bool(nxt) if r["schedule_type"] == "once" else r["enabled"],
                    result,
                    r["id"],
                )
                ran.append((r, result, error))
    # İşlem kapandıktan sonra: bildirim ve denetim kaydı (kendi bağlantılarıyla)
    for r in missed:
        await notify("schedule_missed", "medium", "Zamanlanmış görev kaçırıldı: %s" % r["name"],
                     "Planlanan saatte sunucu çalışmıyordu; görev geç saatte çalıştırılmadı.")
    for r, result, error in ran:
        if error is not None:
            await notify("schedule_failed", "high", "Zamanlanmış görev çalıştırılamadı: %s" % r["name"], str(error))
        await add_audit_log(
            "*",
            "schedule_run",
            "Zamanlanmış görev çalıştı: %s" % r["name"],
            {"schedule_id": r["id"], "result": result, "created_by": r.get("created_by")},
        )
    if added:
        await process_queue()
    return added


# Ajanın kendi süre sınırı 30 dakika (CommandExecutionPolicy). Bundan 5 dakika sonra hâlâ sonucu gelmemiş
# "Running" görev, ajan hiç geri dönmediği için takılı kalmıştır: "Timed Out" olur ve cihazın kuyruğu açılır.
# Geç gelen sonuç yine kaydedilir (bkz. routers/agents.py, sonuç işleyici).
TASK_STUCK_SECONDS = 35 * 60


async def reap_stuck_tasks() -> int:
    rows = await db.execute_query(
        "UPDATE tasks SET status = 'Timed Out', "
        "output = COALESCE(NULLIF(output, ''), '[ZAMAN AŞIMI]: 35 dakika içinde ajandan sonuç gelmedi.') "
        "WHERE status = 'Running' AND dispatched_at IS NOT NULL "
        "AND dispatched_at < NOW() - make_interval(secs => $1) RETURNING id, target_pc",
        (TASK_STUCK_SECONDS,),
        fetch=True,
    )
    for r in rows or []:
        log.warning("takılı görev zaman aşımına uğradı", extra={"task_id": r["id"], "pc": r["target_pc"]})
    # Geçerlilik süresi dolan bekleyen görev (çoğunlukla çevrimdışı cihazın zamanlanmış görevi) başlatılmaz
    expired = await db.execute_query(
        "UPDATE tasks SET status = 'Expired', output = COALESCE(NULLIF(output, ''), "
        "'[SÜRESİ DOLDU]: Cihaz görevin geçerlilik süresi içinde bağlanmadı; görev çalıştırılmadı.') "
        "WHERE status IN ('Pending', 'Paused') AND expires_at IS NOT NULL AND expires_at < NOW() RETURNING id",
        fetch=True,
    )
    if rows:
        await process_queue()
    return len(rows or []) + len(expired or [])


async def check_pending_updates() -> None:
    """Güncelleme gönderilen ajan uzun süre sonuç bildirmediyse (ölü/yönetilemez ajan sonuç gönderemez)."""
    now = time.time()
    for pc, (version, sent_at) in list(manager.pending_updates.items()):
        if now - sent_at < UPDATE_SILENCE_SECONDS:
            continue
        await update_tracking.forget(pc)
        online = pc in manager.active_agents
        await notify(
            "update_silent",
            "high",
            "Güncelleme (%s) gönderildi, ajan 20 dakikadır sonuç bildirmedi" % version,
            "Ajan şu an %s." % ("bağlı" if online else "bağlı değil"),
            pc,
        )


async def check_licenses_daily() -> None:
    """Günde bir kez: koltuk aşımı, süresi dolmuş ve 30 gün içinde bitecek lisanslar için bildirim."""
    from pops.routers.licenses import licenses_with_usage  # döngüsel import olmasın diye burada

    if not await modules.enabled("licenses"):
        return   # lisanslar modülü kapalı: aşım/süre bildirimi yok

    today = datetime.date.today().isoformat()
    rows = await db.execute_query("SELECT value FROM global_settings WHERE key = 'license_check_date'", fetch=True)
    if rows and rows[0]["value"] == today:
        return
    await db.execute_query(
        "INSERT INTO global_settings (key, value) VALUES ('license_check_date', $1) "
        "ON CONFLICT (key) DO UPDATE SET value = $1",
        (today,),
    )
    for lic in await licenses_with_usage():
        if lic["state"] == "over":
            await notify(
                "license_over",
                "high",
                "Lisans aşımı: %s (%d kurulu / %d izinli)" % (lic["name"], lic["installed"], lic["seats"]),
                "",
            )
        elif lic["state"] == "expired":
            await notify(
                "license_expired", "high", "Lisansın süresi doldu: %s (%s)" % (lic["name"], lic["expires_at"]), ""
            )
        elif lic["state"] == "expiring":
            await notify(
                "license_expiring",
                "medium",
                "Lisans 30 gün içinde bitiyor: %s (%s)" % (lic["name"], lic["expires_at"]),
                "",
            )


# Son tamamlanan turun zamanı (epoch); /api/system/diagnostics zamanlayıcının durup durmadığını gösterir
last_tick = [0.0]


async def scheduler_loop() -> None:
    while True:
        try:
            await run_due()
            await reap_stuck_tasks()
            await check_pending_updates()
            await check_licenses_daily()
            await retention.apply_daily()
            await health_alerts.check()
            last_tick[0] = time.time()
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("zamanlayıcı turu başarısız")
        await asyncio.sleep(TICK_SECONDS)
