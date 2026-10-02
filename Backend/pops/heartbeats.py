"""Ajan heartbeat'lerinin toplu yazılması.

Her heartbeat eskiden clients tablosuna ayrı bir UPDATE idi: 2000 ajan dakikada iki kez bildirince saniyede ~70
yazma, her biri ayrı bir havuz bağlantısı ve işlem. Şimdi heartbeat bellekte cihaz başına EN SON hâliyle tutulur
ve FLUSH_SECONDS'ta bir tek UPDATE ... FROM unnest(...) ile yazılır. Panel ve raporlar en fazla bu kadar gecikmeli
görür. Yalnızca hâlâ bağlı cihazlar yazılır: kopan cihazın bekleyen heartbeat'i atılır, yoksa "Offline" kaydının
üzerine geç bir "Online" yazılırdı (bkz. routers/agents.py, bağlantı kapanışı).
"""

import asyncio
import logging
import os

from pops import metrics
from pops.db import execute_query
from pops.manager import manager

log = logging.getLogger("pops.heartbeats")

FLUSH_SECONDS = float(os.environ.get("HEARTBEAT_FLUSH_SECONDS", "2"))

_pending = {}  # pc_name -> (last_seen, status, active_window, hostname, ip, agent_health_json)


def record(pc_name: str, last_seen: str, status, active_window, hostname, ip, health_json) -> None:
    metrics.count("heartbeats")
    _pending[pc_name] = (
        last_seen,
        None if status is None else str(status),
        None if active_window is None else str(active_window),
        None if hostname is None else str(hostname),
        ip,
        health_json,
    )


def discard(pc_name: str) -> None:
    _pending.pop(pc_name, None)


async def flush() -> int:
    if not _pending:
        return 0
    batch = [(pc, row) for pc, row in _pending.items() if pc in manager.active_agents]
    _pending.clear()
    if not batch:
        return 0
    cols = list(zip(*[(pc,) + row for pc, row in batch]))
    await execute_query(
        "UPDATE clients AS c SET last_seen = v.last_seen, status = v.status, active_window = v.active_window, "
        "hostname = v.hostname, ip_address = v.ip, agent_health = v.health::jsonb "
        "FROM unnest($1::text[], $2::text[], $3::text[], $4::text[], $5::text[], $6::text[], $7::text[]) "
        "AS v(pc_name, last_seen, status, active_window, hostname, ip, health) "
        "WHERE c.pc_name = v.pc_name",
        tuple(list(c) for c in cols),
    )
    metrics.count("heartbeat_rows_written", len(batch))
    # Yazım sürerken kopan cihaz: bağlantı kapanışının "Offline" kaydı bu toplu yazımdan önce bitmiş olabilir, o
    # durumda biz "Online"ı üstüne yazdık. Yazım bittiğinde artık bağlı olmayanlar yeniden "Offline" yapılır (kapanış
    # bu andan sonra olursa kendi kaydı zaten sonra gelir).
    gone = [pc for pc, _row in batch if pc not in manager.active_agents]
    if gone:
        await execute_query(
            "UPDATE clients SET status = 'Offline' "
            "WHERE pc_name = ANY($1::text[]) AND status IS DISTINCT FROM 'Offline'",
            (gone,),
        )
    return len(batch)


async def flush_loop() -> None:
    while True:
        await asyncio.sleep(FLUSH_SECONDS)
        try:
            await flush()
        except asyncio.CancelledError:
            raise
        except Exception:
            # Bu turun heartbeat'leri kaybolur; ajanlar bir sonraki heartbeat'te yeniden bildirir
            log.warning("heartbeat'ler yazılamadı", exc_info=True)
