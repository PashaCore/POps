"""Raporlar: filo özeti (cihaz, sürüm, kayıt, yama, yazılım, olaylar, güncellemeler) ve CSV dışa aktarma.

CSV'de hücreler formül olarak yorumlanmasın diye =, +, -, @ ile başlayan değerlerin önüne ' eklenir:
yazılım adları ve olay metinleri ajandan gelir (CSV/formül enjeksiyonu)."""

import csv
import datetime
import io
import json

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import Response

from pops import modules
from pops.db import execute_query
from pops.security import require_auth

router = APIRouter()


def _since(days: int) -> str:
    return (datetime.datetime.now() - datetime.timedelta(days=days)).strftime("%Y-%m-%d %H:%M:%S")


def _clamp_days(days: int) -> int:
    return max(1, min(int(days or 30), 365))


@router.get("/api/reports/summary", dependencies=[modules.require("reports")])
async def report_summary(days: int = 30, auth: dict = Depends(require_auth)):
    days = _clamp_days(days)
    since = _since(days)

    dev = (
        await execute_query(
            "SELECT count(*) AS total, count(*) FILTER (WHERE status = 'Online') AS online, "
            "count(*) FILTER (WHERE is_quarantined) AS quarantined, count(s.pc_name) AS enrolled "
            "FROM clients c LEFT JOIN agent_secrets s ON s.pc_name = c.pc_name",
            fetch=True,
        )
    )[0]
    labs = await execute_query(
        "SELECT coalesce(lab_name, '') AS lab, count(*) AS total, count(*) FILTER (WHERE status = 'Online') AS online "
        "FROM clients GROUP BY 1 ORDER BY 1",
        fetch=True,
    )
    versions = await execute_query(
        "SELECT coalesce(av.version, 'bilinmiyor') AS version, count(*) AS devices FROM clients c "
        "LEFT JOIN agent_versions av ON av.pc_name = c.pc_name GROUP BY 1 ORDER BY 2 DESC",
        fetch=True,
    )
    patch = (
        await execute_query(
            "SELECT count(p.pc_name) AS reporting, "
            "count(*) FILTER (WHERE p.pc_name IS NOT NULL AND p.pending_count = 0) AS up_to_date, "
            "count(*) FILTER (WHERE p.pending_security > 0) AS pending_security, "
            "count(*) FILTER (WHERE p.pending_critical > 0) AS pending_critical, "
            "count(*) FILTER (WHERE p.reboot_required) AS reboot_required "
            "FROM clients c LEFT JOIN device_patch_status p ON p.pc_name = c.pc_name",
            fetch=True,
        )
    )[0]
    sw = (
        await execute_query(
            "SELECT count(DISTINCT pc_name) AS reporting_devices, count(DISTINCT name) AS titles FROM device_software",
            fetch=True,
        )
    )[0]
    by_risk = await execute_query(
        "SELECT risk_level, count(*) AS n FROM agent_logs_v2 WHERE timestamp >= $1 GROUP BY 1", (since,), fetch=True
    )
    by_day = await execute_query(
        "SELECT substr(timestamp, 1, 10) AS day, "
        "count(*) FILTER (WHERE risk_level = 'critical') AS critical, "
        "count(*) FILTER (WHERE risk_level = 'high') AS high, "
        "count(*) FILTER (WHERE risk_level = 'medium') AS medium "
        "FROM agent_logs_v2 WHERE timestamp >= $1 GROUP BY 1 ORDER BY 1",
        (since,),
        fetch=True,
    )
    top_policy = await execute_query(
        "SELECT meta_data->>'domain' AS domain, meta_data->>'violation_category' AS category, count(*) AS n "
        "FROM agent_logs_v2 WHERE timestamp >= $1 AND action = 'dns_block' GROUP BY 1, 2 ORDER BY 3 DESC LIMIT 10",
        (since,),
        fetch=True,
    )
    top_devices = await execute_query(
        "SELECT l.pc_name, max(c.hostname) AS hostname, max(c.display_name) AS display_name, count(*) AS n "
        "FROM agent_logs_v2 l LEFT JOIN clients c ON c.pc_name = l.pc_name "
        "WHERE l.timestamp >= $1 AND l.risk_level IN ('critical', 'high') GROUP BY 1 ORDER BY 4 DESC LIMIT 10",
        (since,),
        fetch=True,
    )
    upd_rows = await execute_query(
        "SELECT changes FROM device_audit_logs WHERE action = 'update_result' AND timestamp >= $1", (since,), fetch=True
    )
    updates = {}
    for r in upd_rows or []:
        try:
            st = json.loads(r["changes"] or "{}").get("status") or "?"
        except ValueError:
            st = "?"
        updates[st] = updates.get(st, 0) + 1

    return {
        "days": days,
        "generated_at": datetime.datetime.now().astimezone().isoformat(),
        "devices": {**{k: int(v) for k, v in dev.items()}, "labs": [dict(r) for r in labs or []]},
        "versions": [dict(r) for r in versions or []],
        "patches": {k: int(v) for k, v in patch.items()},
        "software": {k: int(v) for k, v in sw.items()},
        "events": {
            "by_risk": {r["risk_level"] or "info": int(r["n"]) for r in by_risk or []},
            "by_day": [dict(r) for r in by_day or []],
            "top_policy": [dict(r) for r in top_policy or []],
            "top_devices": [dict(r) for r in top_devices or []],
        },
        "updates": updates,
    }


def _cell(v) -> str:
    s = "" if v is None else str(v)
    return "'" + s if s[:1] in ("=", "+", "-", "@", "\t", "\r") else s


@router.get("/api/reports/export", dependencies=[modules.require("reports")])
async def report_export(kind: str, days: int = 30, auth: dict = Depends(require_auth)):
    days = _clamp_days(days)
    if kind == "devices":
        header = [
            "hw_id",
            "hostname",
            "display_name",
            "lab",
            "status",
            "last_seen",
            "agent_version",
            "enrolled",
            "quarantined",
            "os",
            "cpu",
            "ram",
            "ip",
            "pending_updates",
            "pending_security",
            "reboot_required",
        ]
        rows = await execute_query(
            "SELECT c.pc_name, c.hostname, c.display_name, c.lab_name, c.status, c.last_seen, av.version, "
            "(s.pc_name IS NOT NULL), c.is_quarantined, h.os_version, h.cpu, h.ram, c.ip_address, "
            "p.pending_count, p.pending_security, p.reboot_required "
            "FROM clients c LEFT JOIN agent_versions av ON av.pc_name = c.pc_name "
            "LEFT JOIN agent_secrets s ON s.pc_name = c.pc_name LEFT JOIN hw_inventory h ON h.pc_name = c.pc_name "
            "LEFT JOIN device_patch_status p ON p.pc_name = c.pc_name ORDER BY c.lab_name NULLS LAST, c.hostname",
            fetch=True,
        )
    elif kind == "software":
        header = ["hw_id", "hostname", "lab", "name", "version", "publisher", "install_date"]
        rows = await execute_query(
            "SELECT s.pc_name, c.hostname, c.lab_name, s.name, s.version, s.publisher, s.install_date "
            "FROM device_software s LEFT JOIN clients c ON c.pc_name = s.pc_name "
            "ORDER BY c.lab_name NULLS LAST, c.hostname, lower(s.name)",
            fetch=True,
        )
    elif kind == "patches":
        header = [
            "hw_id",
            "hostname",
            "lab",
            "pending",
            "security",
            "critical",
            "reboot_required",
            "last_search",
            "last_install",
            "last_result",
        ]
        rows = await execute_query(
            "SELECT c.pc_name, c.hostname, c.lab_name, p.pending_count, p.pending_security, p.pending_critical, "
            "p.reboot_required, p.last_search, p.last_install, p.last_result FROM clients c "
            "LEFT JOIN device_patch_status p ON p.pc_name = c.pc_name ORDER BY c.lab_name NULLS LAST, c.hostname",
            fetch=True,
        )
    elif kind == "licenses":
        from pops.routers.licenses import licenses_with_usage

        header = ["name", "type", "match", "publisher", "seats", "installed", "free", "expires", "state", "notes"]
        rows = [
            {
                k: lic.get(k)
                for k in (
                    "name",
                    "license_type",
                    "match_pattern",
                    "publisher",
                    "seats",
                    "installed",
                    "free",
                    "expires_at",
                    "state",
                    "notes",
                )
            }
            for lic in await licenses_with_usage()
        ]
    elif kind == "events":
        header = ["timestamp", "hw_id", "risk", "category", "action", "actor", "message"]
        rows = await execute_query(
            "SELECT timestamp, pc_name, risk_level, category, action, actor_id, message FROM agent_logs_v2 "
            "WHERE timestamp >= $1 ORDER BY timestamp DESC LIMIT 50000",
            (_since(days),),
            fetch=True,
        )
    else:
        raise HTTPException(status_code=400, detail="kind: devices | software | patches | licenses | events")

    buf = io.StringIO()
    w = csv.writer(buf, delimiter=";")
    w.writerow(header)
    for r in rows or []:
        w.writerow([_cell(v) for v in r.values()])
    name = "pops-%s-%s.csv" % (kind, datetime.date.today().isoformat())
    # BOM: Excel Türkçe karakterleri doğru açsın
    return Response(
        content="﻿" + buf.getvalue(),
        media_type="text/csv; charset=utf-8",
        headers={"Content-Disposition": 'attachment; filename="%s"' % name},
    )
