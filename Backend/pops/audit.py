"""Denetim kayıtları: agent_logs_v2 olay günlüğü ve ajanların yazamadığı, hash-zincirli device_audit_logs."""

import datetime
import json


from pops.config import USE_V2_SCHEMA
from pops import auditchain, db
from pops.db import execute_query


async def log_audit_event(
    pc_name: str,
    log_type: str,
    message: str,
    actor_id: str = "System",
    event_type: str = "system",
    category: str = "legacy",
    action: str = "unknown",
    risk_level: str = "info",
    reason: str = "",
    meta_data: dict = None,
):
    if meta_data is None:
        meta_data = {}
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    if USE_V2_SCHEMA:
        cat = category if category != "legacy" else log_type.lower().replace(" ", "_")
        if risk_level == "info":
            if log_type in ["Error", "Critical Security"]:
                risk_level = "critical"
            elif log_type in ["Security", "Warning"]:
                risk_level = "medium"
        await execute_query(
            """
            INSERT INTO agent_logs_v2 (pc_name, actor_id, event_type, category, action, risk_level, reason,
                message, meta_data, timestamp)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
        """,
            (pc_name, actor_id, event_type, cat, action, risk_level, reason, message, json.dumps(meta_data), now),
        )
    else:
        await execute_query(
            "INSERT INTO agent_logs (pc_name, log_type, message, timestamp) VALUES ($1, $2, $3, $4)",
            (pc_name, log_type, message, now),
        )


_AUDIT_CHAIN_LOCK = 0x504F6175  # 'POau' — denetim zinciri eklemelerini serileştirir


_audit_entry_hash = auditchain.entry_hash  # geri uyum: eski içe aktarmalar


async def add_audit_log(hw_id, action, reason, changes):
    # Kurcalanamaz (tamper-evident) hash zinciri: her kayıt bir öncekinin hash'ini taşır.
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    payload = json.dumps(changes, ensure_ascii=False)
    async with db.acquire() as conn:
        async with conn.transaction():
            await conn.execute("SELECT pg_advisory_xact_lock($1)", _AUDIT_CHAIN_LOCK)
            row = await conn.fetchrow("SELECT entry_hash FROM device_audit_logs ORDER BY id DESC LIMIT 1")
            prev = row["entry_hash"] if row else None
            entry = _audit_entry_hash(prev, hw_id, action, reason, payload, now)
            await conn.execute(
                "INSERT INTO device_audit_logs (hw_id, action, reason, changes, timestamp, prev_hash, entry_hash) "
                "VALUES ($1, $2, $3, $4, $5, $6, $7)",
                hw_id,
                action,
                reason,
                payload,
                now,
                prev,
                entry,
            )
