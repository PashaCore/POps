"""Zamanlayıcı: vakti gelen zamanlanmış görevleri normal görev kuyruğuna ekler ve güncelleme
gönderilip sonucu hiç gelmeyen ajanlar için bildirim üretir. Açılışta başlatılır, 30 sn'de bir döner.

Saatler sunucunun saat diliminde yorumlanır. Birden fazla backend süreci çalışsa bile aynı görev iki
kez eklenmez: tur, PostgreSQL advisory kilidiyle tek sürece verilir ve satırlar FOR UPDATE ile alınır.
"""

import asyncio
import datetime
import json
import time
from typing import Optional

from pops import db
from pops.audit import add_audit_log
from pops.manager import manager
from pops.notify import notify
from pops.taskqueue import process_queue, resolve_targets

TICK_SECONDS = 30
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


async def enqueue(row: dict, actor_suffix: str) -> int:
    """Zamanlanmış görevi hedef cihazlar için görev kuyruğuna ekler; eklenen görev sayısını döner."""
    targets = await resolve_targets(row["target_mode"], json.loads(row["targets"] or "[]"))
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    creator = "%s (%s #%s)" % (row.get("created_by") or "?", actor_suffix, row["id"])
    async with db.db_pool.acquire() as conn:
        for t in targets:
            await conn.execute(
                "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by) "
                "VALUES ($1, $2, $3, 'Pending', $4, $5)",
                t["pc"],
                t["lab"],
                row["command"],
                now,
                creator,
            )
    return len(targets)


async def run_due() -> int:
    """Vakti gelen görevleri kuyruğa ekler. Eklenen toplam görev sayısını döner."""
    added = 0
    due = []
    async with db.db_pool.acquire() as conn:
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
                await conn.execute(
                    "UPDATE scheduled_tasks SET last_run = now(), next_run = $1, enabled = $2 WHERE id = $3",
                    nxt,
                    bool(nxt) if r["schedule_type"] == "once" else r["enabled"],
                    r["id"],
                )
                due.append(r)
    for r in due:
        try:
            n = await enqueue(r, "zamanlanmış")
            result = "%d cihaz için kuyruğa eklendi" % n
            added += n
        except Exception as exc:
            result = "hata: %s" % exc
            await notify("schedule_failed", "high", "Zamanlanmış görev çalıştırılamadı: %s" % r["name"], str(exc))
        await db.execute_query("UPDATE scheduled_tasks SET last_result = $1 WHERE id = $2", (result, r["id"]))
        await add_audit_log(
            "*",
            "schedule_run",
            "Zamanlanmış görev çalıştı: %s" % r["name"],
            {"schedule_id": r["id"], "result": result, "created_by": r.get("created_by")},
        )
    if added:
        await process_queue()
    return added


async def check_pending_updates() -> None:
    """Güncelleme gönderilen ajan uzun süre sonuç bildirmediyse (ölü/yönetilemez ajan sonuç gönderemez)."""
    now = time.time()
    for pc, (version, sent_at) in list(manager.pending_updates.items()):
        if now - sent_at < UPDATE_SILENCE_SECONDS:
            continue
        manager.pending_updates.pop(pc, None)
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


async def scheduler_loop() -> None:
    while True:
        try:
            await run_due()
            await check_pending_updates()
            await check_licenses_daily()
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            print("⚠️ zamanlayıcı hatası: %s" % exc)
        await asyncio.sleep(TICK_SECONDS)
