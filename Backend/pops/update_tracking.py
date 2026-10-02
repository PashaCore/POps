"""Ajan güncellemesinin izi (S20): kime güncelleme gönderildi, hangi sonuç zaten kaydedildi.

Eskiden ikisi de yalnızca bellekteydi: sunucu yeniden başlarsa "güncelleme gönderildi, sonuç gelmedi" uyarısı
kayboluyordu; ajan da sonucu gönderir göndermez silindiği için sunucu kaydı yazmadan çökerse sonuç kayboluyordu.
Şimdi gönderimler pending_updates tablosunda, kaydedilmiş sonuçlar update_results'ta durur. 0.1.14+ ajan sonucu
sunucu onaylayana (update_result_ack) kadar saklar ve yeniden gönderir; aynı result_id ikinci kez kaydedilmez.
"""

import re
import time
from typing import Optional

from pops.db import execute_query
from pops.manager import manager

_RESULT_ID = re.compile(r"^[A-Za-z0-9_-]{8,64}$")


def clean_result_id(value) -> Optional[str]:
    return value if isinstance(value, str) and _RESULT_ID.match(value) else None


async def mark_sent(pc: str, version: str) -> None:
    manager.pending_updates[pc] = (version, time.time())
    await execute_query(
        "INSERT INTO pending_updates (pc_name, version, sent_at) VALUES ($1, $2, NOW()) "
        "ON CONFLICT (pc_name) DO UPDATE SET version = $2, sent_at = NOW()",
        (pc, version),
    )


async def forget(pc: str) -> None:
    manager.pending_updates.pop(pc, None)
    await execute_query("DELETE FROM pending_updates WHERE pc_name = $1", (pc,))


async def load() -> int:
    """Açılışta: yeniden başlatmadan önce gönderilmiş ve sonucu beklenen güncellemeler belleğe alınır."""
    rows = await execute_query(
        "SELECT pc_name, version, extract(epoch FROM sent_at) AS sent FROM pending_updates", fetch=True
    )
    for r in rows or []:
        manager.pending_updates[r["pc_name"]] = (r["version"], float(r["sent"]))
    return len(rows or [])


async def seen(pc: str, result_id: str) -> bool:
    rows = await execute_query(
        "SELECT 1 FROM update_results WHERE pc_name = $1 AND result_id = $2", (pc, result_id), fetch=True
    )
    return bool(rows)


async def remember(pc: str, result_id: str) -> None:
    await execute_query(
        "INSERT INTO update_results (pc_name, result_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", (pc, result_id)
    )
