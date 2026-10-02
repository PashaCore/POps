"""Görev kuyruğu: eşzamanlılık sınırına göre bekleyen görevleri çevrimiçi ajanlara dağıtır."""

import asyncio
import datetime

from pops import metrics
from pops.db import execute_query
from pops.audit import add_audit_log, log_audit_event
from pops.manager import manager


async def resolve_targets(target_mode: str, targets, conn=None) -> list:
    """Görev hedefleri: ALL (tüm cihazlar), LAB (lab adları), PC (HW- kimlikleri) -> [{"pc", "lab"}].
    Hedef sayısından bağımsız tek sorgu (eskiden her lab/cihaz için ayrı sorgu gidiyordu). conn verilirse o bağlantı
    (ve işlemi) kullanılır."""

    async def fetch(query, *params):
        if conn is not None:
            return [dict(r) for r in await conn.fetch(query, *params)]
        return await execute_query(query, params, fetch=True) or []

    if target_mode == 'ALL':
        res = await fetch("SELECT pc_name, lab_name FROM clients")
        return [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in res]
    names = [str(t) for t in (targets or [])]
    if target_mode == 'LAB':
        res = await fetch(
            "SELECT pc_name, lab_name FROM clients WHERE lab_name = ANY($1::text[]) "
            "ORDER BY array_position($1::text[], lab_name), pc_name",
            names,
        )
        return [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in res]
    res = await fetch("SELECT pc_name, lab_name FROM clients WHERE pc_name = ANY($1::text[])", names)
    labs = {r["pc_name"]: r["lab_name"] for r in res}
    return [{"pc": pc, "lab": labs[pc] if pc in labs else "Bilinmeyen Lab"} for pc in names]


def _seconds_since(created_at) -> float:
    # created_at yerel saatte 'YYYY-MM-DD HH:MM:SS' metni
    try:
        created = datetime.datetime.strptime(str(created_at), "%Y-%m-%d %H:%M:%S")
        return (datetime.datetime.now() - created).total_seconds()
    except ValueError:
        return 0.0


# Eşzamanlı çağrılar birleştirilir: bir tur sürerken gelen çağrılar üst üste yığılmaz, tur bitince bir kez
# daha dönülür. Eskiden her ajan bağlantısı ayrı bir tur başlatıyor ve her tur çevrimiçi HER cihaz için ayrı sorgu
# atıyordu: 2000 ajan aynı anda bağlanınca ~2 milyon sorgu, sunucu dakikalarca meşgul (bkz. BENCHMARKS.md).
_queue_lock = asyncio.Lock()
_queue_again = False


async def process_queue():
    global _queue_again
    if _queue_lock.locked():
        _queue_again = True
        return
    async with _queue_lock:
        while True:
            _queue_again = False
            await _process_queue_once()
            if not _queue_again:
                break


async def _process_queue_once():
    # En sık durum: bekleyen görev yok. Tek ucuz sorguyla çıkılır.
    if not await execute_query("SELECT 1 FROM tasks WHERE status = 'Pending' LIMIT 1", fetch=True):
        return
    limit_row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    try:
        limit = max(0, int(limit_row[0]["value"])) if limit_row else 5
    except ValueError:
        limit = 5
    online_pcs = list(manager.active_agents.keys())
    if not online_pcs:
        return
    # Eşzamanlılık kotasını yalnızca BAĞLI cihazlardaki çalışan görevler tutar: bağlantısı kopmuş cihazın "Running"
    # görevi (sonucu bekleniyor ya da zaman aşımına gidiyor) bütün filonun kuyruğunu bekletmesin
    running_row = await execute_query(
        "SELECT COUNT(DISTINCT target_pc) as c FROM tasks WHERE status = 'Running' AND target_pc = ANY($1::text[])",
        (online_pcs,),
        fetch=True,
    )
    running_pcs_count = running_row[0]["c"] if running_row else 0
    available_slots = limit - running_pcs_count
    if not (available_slots > 0 or limit == 0):
        return
    # Çevrimiçi ve şu an görev çalıştırmayan her cihazın en eski bekleyen görevi, tek sorguda; en eski görev önce
    tasks = await execute_query(
        """
        SELECT * FROM (
            SELECT DISTINCT ON (t.target_pc) t.* FROM tasks t
            WHERE t.status = 'Pending' AND t.target_pc = ANY($1::text[])
              AND (t.expires_at IS NULL OR t.expires_at > NOW())
              AND NOT EXISTS (SELECT 1 FROM tasks r WHERE r.status = 'Running' AND r.target_pc = t.target_pc)
            ORDER BY t.target_pc, t.id ASC
        ) oldest ORDER BY id ASC
        """,
        (online_pcs,),
        fetch=True,
    )
    for task in tasks or []:
        if limit > 0 and available_slots <= 0:
            break
        pc = task["target_pc"]
        # agent_started_at: o anki ajan sürecinin (heartbeat'teki) başlangıç değeri; yeniden bağlanınca değiştiyse ajan
        # yeniden başlamıştır (bkz. routers/agents.py _settle_running_tasks; saatler karşılaştırılmaz)
        await execute_query(
            "UPDATE tasks SET status = 'Running', dispatched_at = NOW(), agent_started_at = "
            "(SELECT CASE WHEN jsonb_typeof(agent_health->'started_at') = 'number' "
            "THEN (agent_health->>'started_at')::float8 END FROM clients WHERE pc_name = $2) WHERE id = $1",
            (task["id"], pc),
        )
        # F4(a): komutu KİMİN kuyrukladığını göster (eskiden 'System/Queue' idi, iz yoktu).
        actor = task.get("created_by") or "System/Queue"
        # requested_by: ajan komutu kimin istediğini yerel denetim izine (Windows Olay Günlüğü) yazar (0.1.12+)
        sent = await manager.send_command(
            {"action": "execute", "task_id": task["id"], "script_path": task["script_path"], "requested_by": actor}, pc
        )
        if sent:
            metrics.observe_dispatch(_seconds_since(task.get("created_at")))
        else:
            # Bağlantı bu arada koptu: görev ajana ulaşmadı, sıraya geri döner (yeniden bağlanınca gönderilir)
            await execute_query(
                "UPDATE tasks SET status = 'Pending', dispatched_at = NULL WHERE id = $1 AND status = 'Running'",
                (task["id"],),
            )
            continue
        await log_audit_event(
            pc,
            "Deploy",
            f"Görev: {task['script_path'][:50]}",
            actor_id=actor,
            event_type="deploy.execution",
            category="system_maintenance",
            action="execute_queue",
            risk_level="info",
            meta_data={"raw_command": task["script_path"], "created_by": task.get("created_by")},
        )
        # SYSTEM olarak komut çalıştırma yüksek-değerli olay → hash-zincirli,
        # ajanların yazamadığı loga da düş.
        await add_audit_log(
            pc,
            "execute",
            "SYSTEM komutu çalıştırıldı (kuyruk: %s)" % actor,
            {
                "task_id": task["id"],
                "created_by": task.get("created_by"),
                "command": (task["script_path"] or "")[:200],
            },
        )
        available_slots -= 1
