"""İşletim uçları: Prometheus /metrics, panel için sunucu sağlık özeti (/api/system/diagnostics) ve Genel bakış
grafiklerinin verisi (/api/system/overview).

/metrics yalnızca METRICS_TOKEN tanımlıysa açılır ve Bearer jeton ister; tanımlı değilse 404 döner (varsayılan
kapalı). Sağlık özeti superadmin içindir: okulda Prometheus olmasa da sunucunun durumu ve son hatalar panelden
görülür.
"""

import calendar
import hmac
import json
import os
import time

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import PlainTextResponse
from pydantic import Field

from pops import db, health_alerts, logs, metrics, retention, scheduler, server_metrics
from pops.config import DB_POOL_MAX, METRICS_TOKEN
from pops.db import execute_query
from pops.manager import manager
from pops.models import StrictInput
from pops.audit import add_audit_log
from pops.security import require_superadmin

router = APIRouter()

# pops-backup'ın (root) yazdığı son yedek sonucu; gizli bilgi içermez
BACKUP_STATUS_FILE = os.environ.get("POPS_BACKUP_STATUS", "/var/lib/pops-state/backup-status.json")
# 0.1.12 ve öncesi dosyayı backend'in yazabildiği /var/lib/pops'a yazıyordu
_LEGACY_BACKUP_STATUS_FILE = "/var/lib/pops/backup-status.json"


def _backup_status():
    data = None
    for path in (BACKUP_STATUS_FILE, _LEGACY_BACKUP_STATUS_FILE):
        try:
            with open(path, "r", encoding="utf-8") as f:
                data = json.load(f)
            break
        except (OSError, ValueError):
            continue
    if not isinstance(data, dict):
        return None
    return {k: data.get(k) for k in ("ok", "at", "message", "bytes", "verified")}


def _version():
    from system_routes import _read_version  # system_routes, pops paketine bağımlı değil; döngü yok

    return _read_version()


def _pool_stats():
    pool = db.db_pool
    if pool is None:
        return {"size": 0, "idle": 0, "max": DB_POOL_MAX}
    return {"size": pool.get_size(), "idle": pool.get_idle_size(), "max": DB_POOL_MAX}


async def _device_counts():
    rows = await execute_query(
        """SELECT count(*) AS total,
                  count(*) FILTER (WHERE status = 'Online') AS online,
                  count(*) FILTER (WHERE is_quarantined) AS quarantined
           FROM clients""",
        fetch=True,
    )
    r = rows[0] if rows else {}
    return {"total": r.get("total", 0) or 0, "online": r.get("online", 0) or 0,
            "quarantined": r.get("quarantined", 0) or 0}


@router.get("/metrics", include_in_schema=False)
async def prometheus_metrics(request: Request):
    if len(METRICS_TOKEN) < 16:
        raise HTTPException(status_code=404)
    auth = request.headers.get("authorization", "")
    if not hmac.compare_digest(auth.encode(), ("Bearer " + METRICS_TOKEN).encode()):
        raise HTTPException(status_code=401, detail="Geçersiz metrik jetonu")
    pool = _pool_stats()
    devices = await _device_counts()
    tick = scheduler.last_tick[0]
    gauges = [
        ("pops_agents_connected", "Bagli ajan WebSocket sayisi", [({}, len(manager.active_agents))]),
        ("pops_panels_connected", "Bagli panel WebSocket sayisi", [({}, len(manager.active_panels))]),
        ("pops_vision_sessions", "Acik uzaktan izleme oturumu olan cihaz", [({}, len(manager.vision_sessions))]),
        ("pops_pending_agent_updates", "Sonucu beklenen ajan guncellemesi", [({}, len(manager.pending_updates))]),
        ("pops_devices", "Kayitli cihazlar (durum)", [({"state": k}, v) for k, v in devices.items()]),
        ("pops_db_pool_connections", "Veritabani havuzu", [({"state": k}, v) for k, v in pool.items()]),
        ("pops_scheduler_last_tick_age_seconds", "Zamanlayicinin son turundan beri gecen sure",
         [({}, round(time.time() - tick, 1) if tick else -1)]),
        ("pops_process_resident_memory_mb", "Surecin bellek kullanimi (MB)", [({}, server_metrics.rss_mb() or 0)]),
    ]
    backup = _backup_status()
    if backup and backup.get("at"):
        try:
            at = calendar.timegm(time.strptime(backup["at"], "%Y-%m-%dT%H:%M:%SZ"))
            age = round(time.time() - at)
            gauges.append(("pops_backup_last_age_seconds", "Son yedekten beri gecen sure", [({}, age)]))
        except (TypeError, ValueError):
            pass
        gauges.append(("pops_backup_last_ok", "Son yedek basarili ve sinanmis mi (1/0)",
                       [({}, 1 if backup.get("ok") and backup.get("verified") else 0)]))
    return PlainTextResponse(
        metrics.render(gauges, logs.level_counts, _version()), media_type="text/plain; version=0.0.4"
    )


@router.get("/api/system/diagnostics")
async def diagnostics(auth: dict = Depends(require_superadmin)):
    tick = scheduler.last_tick[0]
    slow = []
    for route, row in metrics.http_duration.items():
        count = row[len(metrics.BUCKETS)]
        if count:
            slow.append({"route": route, "count": count, "avg_ms": round(row[-1] / count * 1000, 1)})
    slow.sort(key=lambda r: r["avg_ms"], reverse=True)
    server_errors = sum(n for (_m, _r, s), n in metrics.http_requests.items() if s >= 500)
    return {
        "version": _version(),
        "uptime_seconds": round(time.time() - metrics.STARTED_AT),
        "pid": os.getpid(),
        "rss_mb": server_metrics.rss_mb(),
        "agents_connected": len(manager.active_agents),
        "panels_connected": len(manager.active_panels),
        "vision_sessions": len(manager.vision_sessions),
        "pending_updates": len(manager.pending_updates),
        "devices": await _device_counts(),
        "db_pool": _pool_stats(),
        "scheduler_last_tick_age": round(time.time() - tick, 1) if tick else None,
        "log_counts": {lvl: logs.level_counts.get(lvl, 0) for lvl in ("WARNING", "ERROR", "CRITICAL")},
        "http_5xx": server_errors,
        "unhandled_errors": metrics.unhandled_errors[0],
        "slowest_routes": slow[:5],
        "recent_errors": list(reversed(logs.recent_errors))[:50],
        "metrics_enabled": len(METRICS_TOKEN) >= 16,
        "backup": _backup_status(),
        "load": _load_summary(),
        # Son kontrol (disk saatte, sertifika günde bir; bkz. pops/health_alerts.py)
        "disk": health_alerts.last["disk"],
        "tls": health_alerts.last["tls"],
    }


def _load_summary():
    """Yük ölçümleri (süreç başladığından beri; saniyedeki yazma son 60 sn)."""
    c = metrics.counters
    beats = c.get("heartbeats", 0)

    def p95(row, buckets, scale=1):
        # Kova üst sınırı; en büyük kovayı aşan gözlemde ">sınır" (JSON sonsuzluk taşıyamaz)
        q = metrics.quantile(row, buckets, 0.95)
        if q is None:
            return None
        return ">%g" % (buckets[-1] * scale) if q == float("inf") else round(q * scale, 3)

    return {
        "heartbeats": beats,
        "queries_per_heartbeat": round(c.get("heartbeat_queries", 0) / beats, 2) if beats else None,
        "heartbeat_rows_written": c.get("heartbeat_rows_written", 0),
        "db_writes_per_second": metrics.writes_last_minute(),
        "db_reads": c.get("db_reads", 0),
        "db_writes": c.get("db_writes", 0),
        "task_dispatch_p95_seconds": p95(metrics.dispatch_latency, metrics.DISPATCH_BUCKETS),
        "command_send_p95_ms": p95(metrics.command_send, metrics.SEND_BUCKETS, 1000),
    }


@router.get("/api/system/overview")
async def overview(span: str = "24h", auth: dict = Depends(require_superadmin)):
    """Sistem → Genel bakış grafikleri: span (24h, 7d, 30d) boyunca dakikalık ölçümler (bkz. pops/server_metrics.py),
    görev sonuçları, olaylar ve ajan güncelleme sonuçları; şu anki cihaz ve bağlantı sayıları."""
    if span not in server_metrics.RANGES:
        raise HTTPException(status_code=422, detail="span 24h, 7d ya da 30d olmalı")
    data = await server_metrics.overview(span)
    data["devices"] = await _device_counts()
    data["agents_connected"] = len(manager.active_agents)
    return data


class RetentionInput(StrictInput):
    # Gün; 0 = süresiz sakla
    retention_days_logs: int = Field(ge=0, le=retention.MAX_DAYS)
    retention_days_tasks: int = Field(ge=0, le=retention.MAX_DAYS)
    retention_days_notifications: int = Field(ge=0, le=retention.MAX_DAYS)


@router.get("/api/system/retention")
async def get_retention(auth: dict = Depends(require_superadmin)):
    return await retention.settings()


@router.post("/api/system/retention")
async def set_retention(data: RetentionInput, auth: dict = Depends(require_superadmin)):
    before = await retention.settings()
    after = await retention.save(data.model_dump())
    await add_audit_log("*", "retention_settings", "Kayıt saklama süreleri değişti", {
        "before": before, "after": after, "by": auth.get("sub"),
    })
    return after
