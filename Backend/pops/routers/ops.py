"""İşletim uçları: Prometheus /metrics ve panel için sunucu sağlık özeti (/api/system/diagnostics).

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

from pops import db, logs, metrics, scheduler
from pops.config import DB_POOL_MAX, METRICS_TOKEN
from pops.db import execute_query
from pops.manager import manager
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


def _rss_mb():
    try:
        with open("/proc/self/statm") as f:
            pages = int(f.read().split()[1])
        return round(pages * os.sysconf("SC_PAGE_SIZE") / 1048576, 1)
    except (OSError, ValueError, IndexError):
        return None


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
        ("pops_process_resident_memory_mb", "Surecin bellek kullanimi (MB)", [({}, _rss_mb() or 0)]),
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
        "rss_mb": _rss_mb(),
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
    }
