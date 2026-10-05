"""Sunucu ölçüm geçmişi ve Sistem → Genel bakış grafiklerinin verisi.

Zamanlayıcı dakikada bir örnek alır (sample): bağlı ajan ve panel, işlemci, bellek ve disk doluluğu, veritabanı
boyutu, süreç belleği, o dakikadaki API isteği ve 5xx yanıtı. Örnekler server_metrics tablosuna yazılır, 30 günden
eskileri saatte bir silinir. Süreç açıldıktan sonraki ilk tur yalnızca başlangıç değerlerini alır: işlemci ve istek
sayısı iki ölçüm arasındaki farktan hesaplanır.

overview() bir zaman aralığı için bu örnekleri ve var olan kayıtlardan özetleri (görev sonuçları, olaylar, ajan
güncelleme sonuçları) döner.
"""

import asyncio
import datetime
import json
import os
import time

from pops import db, health_alerts, metrics, timeutil
from pops.config import LOG_TABLE
from pops.manager import manager

SAMPLE_SECONDS = 60
KEEP_DAYS = 30
_PRUNE_SECONDS = 3600

# Aralık -> (süre, ölçüm noktası aralığı, çubuk aralığı) saniye. Ölçümler UTC'ye, çubuklar sunucunun saat dilimindeki
# yerel saate hizalanır (pops/timeutil.py).
RANGES = {
    "24h": (86400, 900, 3600),
    "7d": (7 * 86400, 7200, 6 * 3600),
    "30d": (30 * 86400, 6 * 3600, 86400),
}

_state = {"cpu": None, "http": None, "at": 0.0, "pruned": 0.0}


def rss_mb():
    try:
        with open("/proc/self/statm") as f:
            pages = int(f.read().split()[1])
        return round(pages * os.sysconf("SC_PAGE_SIZE") / 1048576, 1)
    except (OSError, ValueError, IndexError):
        return None


def _cpu_times():
    """(toplam, boşta) jiffy; /proc/stat okunamazsa None. guest süreleri user'ın içindedir, toplanmaz."""
    try:
        with open("/proc/stat") as f:
            vals = [int(x) for x in f.readline().split()[1:9]]
        return sum(vals), vals[3] + vals[4]
    except (OSError, ValueError, IndexError):
        return None


def _mem_pct():
    try:
        info = {}
        with open("/proc/meminfo") as f:
            for line in f:
                key, _, rest = line.partition(":")
                info[key] = int(rest.split()[0])
        total, avail = info.get("MemTotal"), info.get("MemAvailable")
        if not total or avail is None:
            return None
        return round((total - avail) * 100.0 / total, 1)
    except (OSError, ValueError, IndexError):
        return None


def _disk_pct():
    rows = health_alerts.disk_status()
    return round(max(100.0 - r["free_percent"] for r in rows), 1) if rows else None


def _http_totals():
    total = errors = 0
    for (_method, _route, status), n in list(metrics.http_requests.items()):
        total += n
        if status >= 500:
            errors += n
    return total, errors


def cpu_pct_between(before, after):
    if not before or not after:
        return None
    total, idle = after[0] - before[0], after[1] - before[1]
    if total <= 0:
        return None
    return round(max(0.0, min(100.0, 100.0 * (1 - idle / total))), 1)


async def sample(force: bool = False) -> bool:
    """Dakikada bir ölçüm yazar; yazdıysa True. İlk çağrı yalnızca başlangıç değerlerini alır."""
    now = time.time()
    if not force and now - _state["at"] < SAMPLE_SECONDS:
        return False
    _state["at"] = now
    cpu = _cpu_times()
    http = _http_totals()
    before_cpu, before_http = _state["cpu"], _state["http"]
    _state["cpu"], _state["http"] = cpu, http
    if before_http is None:
        return False
    requests = errors = None
    if http[0] >= before_http[0]:
        requests, errors = http[0] - before_http[0], max(0, http[1] - before_http[1])
    disk = await asyncio.to_thread(_disk_pct)
    size = await db.execute_query("SELECT pg_database_size(current_database()) AS b", fetch=True)
    db_mb = round(size[0]["b"] / 1048576, 1) if size else None
    await db.execute_query(
        "INSERT INTO server_metrics (ts, agents, panels, cpu_pct, mem_pct, disk_pct, db_mb, rss_mb, requests, errors) "
        "VALUES (date_trunc('minute', NOW()), $1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT (ts) DO NOTHING",
        (len(manager.active_agents), len(manager.active_panels), cpu_pct_between(before_cpu, cpu), _mem_pct(),
         disk, db_mb, rss_mb(), requests, errors),
    )
    if now - _state["pruned"] >= _PRUNE_SECONDS:
        _state["pruned"] = now
        await db.execute_query("DELETE FROM server_metrics WHERE ts < NOW() - make_interval(days => $1)", (KEEP_DAYS,))
    return True


def bar_starts(now: datetime.datetime, seconds: int, bar: int):
    """Yerel saatle (saat dilimsiz duvar saati) çubuk başlangıçları, eskiden yeniye; sonuncusu şu anki çubuk (24 saatte
    24, 30 günde 30)."""
    if bar >= 86400:
        last = now.replace(hour=0, minute=0, second=0, microsecond=0)
    else:
        hours = bar // 3600
        last = now.replace(hour=now.hour - now.hour % hours, minute=0, second=0, microsecond=0)
    n = seconds // bar
    return [last - datetime.timedelta(seconds=bar * (n - 1 - i)) for i in range(n)]


def _bar_index(starts, text):
    """'YYYY-AA-GG SS…' metninin (sunucunun saat diliminde yerel saat) düştüğü çubuk; aralık dışıysa None."""
    try:
        at = datetime.datetime.strptime(text[:13], "%Y-%m-%d %H")
    except (TypeError, ValueError):
        return None
    for i in range(len(starts) - 1, -1, -1):
        if at >= starts[i]:
            return i
    return None


TASK_GROUPS = {"Completed": "ok", "Failed": "failed", "Error": "failed", "Denied": "denied"}
RISK_GROUPS = {"high": "high", "critical": "high", "medium": "medium"}


def update_group(status: str) -> str:
    """Ajan güncelleme sonucunun grafikteki grubu; yeniden başlatma bekleyen kurulum başarılı sayılır."""
    if status == "success" or "pending_reboot" in status:
        return "success"
    return "rolled_back" if status == "rolled_back" else "failed"


async def overview(span: str) -> dict:
    seconds, step, bar = RANGES[span]
    now_ts = time.time()

    # Ölçümler: aralığın tamamı için eşit aralıklı noktalar; ölçüm olmayan nokta null
    first = (int(now_ts - seconds) // step + 1) * step
    grid = list(range(first, int(now_ts) // step * step + 1, step))
    rows = await db.execute_query(
        "SELECT (floor(extract(epoch FROM ts) / $1) * $1)::bigint AS t, max(agents) AS agents, max(panels) AS panels, "
        "avg(cpu_pct) AS cpu, avg(mem_pct) AS mem, max(disk_pct) AS disk, max(db_mb) AS db_mb, avg(rss_mb) AS rss, "
        "sum(requests) AS requests, sum(errors) AS errors FROM server_metrics "
        "WHERE ts >= to_timestamp($2) GROUP BY 1",
        (step, first), fetch=True,
    )
    by_t = {int(r["t"]): r for r in rows or []}

    def num(v, digits=1):
        return None if v is None else round(float(v), digits)

    series = []
    for t in grid:
        r = by_t.get(t)
        series.append({
            "t": t,
            "agents": r and r["agents"], "panels": r and r["panels"],
            "cpu": num(r and r["cpu"]), "mem": num(r and r["mem"]), "disk": num(r and r["disk"]),
            "db_mb": num(r and r["db_mb"]), "rss_mb": num(r and r["rss"]),
            "requests": r and r["requests"], "errors": r and r["errors"],
        })
    latest = await db.execute_query(
        "SELECT extract(epoch FROM ts)::bigint AS t, agents, cpu_pct, mem_pct, disk_pct, db_mb, rss_mb "
        "FROM server_metrics ORDER BY ts DESC LIMIT 1", fetch=True,
    )

    # Görevler, olaylar ve ajan güncellemeleri: sunucunun saat dilimindeki yerel saatle çubuklar. Kayıtlar o dilimde
    # 'YYYY-AA-GG SS' saatine yuvarlanıp sayılır.
    tz = timeutil.zone()
    now = datetime.datetime.fromtimestamp(now_ts, tz).replace(tzinfo=None)
    starts = bar_starts(now, seconds, bar)
    since = starts[0].replace(tzinfo=tz)
    zone = timeutil.zone_name()
    tasks = [{"t": int(s.replace(tzinfo=tz).timestamp()), "ok": 0, "failed": 0, "denied": 0, "other": 0}
             for s in starts]
    events = [{"t": b["t"], "info": 0, "medium": 0, "high": 0} for b in tasks]
    rows = await db.execute_query(
        "SELECT to_char(created_at AT TIME ZONE $2, 'YYYY-MM-DD HH24') AS h, status, count(*) AS n FROM tasks "
        "WHERE created_at >= $1 GROUP BY 1, 2",
        (since, zone), fetch=True,
    )
    for r in rows or []:
        i = _bar_index(starts, r["h"])
        if i is not None:
            tasks[i][TASK_GROUPS.get(r["status"], "other")] += r["n"]
    rows = await db.execute_query(
        f'SELECT to_char("timestamp" AT TIME ZONE $2, \'YYYY-MM-DD HH24\') AS h, '
        f'lower(coalesce(risk_level, \'\')) AS risk, count(*) AS n FROM {LOG_TABLE} '
        'WHERE "timestamp" >= $1 GROUP BY 1, 2',
        (since, zone), fetch=True,
    )
    for r in rows or []:
        i = _bar_index(starts, r["h"])
        if i is not None:
            events[i][RISK_GROUPS.get(r["risk"], "info")] += r["n"]
    updates = {"success": 0, "rolled_back": 0, "failed": 0}
    rows = await db.execute_query(
        "SELECT changes FROM device_audit_logs WHERE action = 'update_result' AND \"timestamp\" >= $1", (since,),
        fetch=True,
    )
    for r in rows or []:
        try:
            status = str((json.loads(r["changes"] or "{}") or {}).get("status") or "")
        except (TypeError, ValueError, AttributeError):
            status = ""
        updates[update_group(status)] += 1

    return {
        "span": span,
        "seconds": seconds,
        "step": step,
        "bar": bar,
        "series": series,
        "latest": dict(latest[0]) if latest else None,
        "disk": health_alerts.last["disk"] or await asyncio.to_thread(health_alerts.disk_status),
        "tasks": tasks,
        "events": events,
        "updates": updates,
    }
