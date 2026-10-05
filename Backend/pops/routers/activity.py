"""Cihazın etkinlik geçmişi: tepside "Etkinlik geçmişim" (şeffaflık).

Bilgisayarı kullanan kişi, BT yöneticilerinin bu cihazda son 30 günde ne yaptığını görür: uzaktan izleme
oturumları, çalıştırılan komutlar, karantina, yetenek değişiklikleri, ajan ve Windows güncellemeleri. Başka
kullanıcıların kişisel verisi (tarama geçmişi, politika uyarıları) burada YOKTUR: lab bilgisayarları ortak
kullanılır. Komutların içeriği gösterilmez (içinde yöneticinin gizli bilgisi olabilir); yalnızca kimin ne zaman
çalıştırdığı gösterilir.

Uç yalnızca ANAHTARLI ajanı kabul eder ve kendi cihazına bağlıdır (başka cihazın geçmişi okunamaz); cihaz başına
5 sn'de bir istek. Metinler burada Türkçe hazırlanır, tepsi olduğu gibi gösterir.
"""

import datetime
import json
import time
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException

from pops.agent_auth import agent_http_auth, bind_agent
from pops.db import execute_query

router = APIRouter()

DAYS = 30
MAX_ITEMS = 200
AGENT_MIN_INTERVAL = 5.0
_agent_last = {}

# Filo çapında ("*") yazılan ve "dispatched" listesiyle cihazları sayan kayıtlar
_FLEET_ACTIONS = ("deploy_update", "scan_updates", "install_updates")
_DEVICE_ACTIONS = ("lockdown", "unlock", "update_result", "set_capabilities", "enroll", "NEW_DEVICE")


def _throttle(pc_name: str) -> None:
    now = time.monotonic()
    if now - _agent_last.get(pc_name, 0.0) < AGENT_MIN_INTERVAL:
        raise HTTPException(status_code=429, detail="Çok sık istek; birkaç saniye sonra tekrar deneyin.")
    _agent_last[pc_name] = now
    if len(_agent_last) > 10000:
        for k in [k for k, t in _agent_last.items() if now - t > 60]:
            _agent_last.pop(k, None)


def _json(text):
    try:
        value = json.loads(text or "{}")
        return value if isinstance(value, dict) else {}
    except ValueError:
        return {}


def _item(at, kind, title, actor=None, detail=None):
    return {"at": at, "kind": kind, "title": title, "actor": actor or None, "detail": detail or None}


def _reason(text):
    text = (text or "").strip()
    return ("Gerekçe: " + text[:300]) if text else None


def _update_title(c):
    status = str(c.get("status") or "")
    to = c.get("to_version") or "?"
    running = c.get("running_version") or c.get("from_version") or "?"
    if status in ("success", "ok", "updated"):
        return "Ajan %s sürümüne güncellendi" % to
    if status == "rolled_back":
        return "%s güncellemesi geri alındı, %s çalışıyor" % (to, running)
    if status == "install_failed":
        return "%s güncellemesi başlatılamadı, cihaz değişmedi" % to
    if status in ("pending_reboot", "rollback_pending_reboot"):
        return "Ajan güncellemesi yeniden başlatmayı bekliyor"
    if status == "rejected":
        return "Ajan %s güncellemesini reddetti" % to
    return "Ajan güncellemesi sorunlu bitti (%s)" % (status or "?")


def _capability_title(c):
    parts = []
    for key, label in (("terminal_enabled", "Uzaktan terminal"), ("vision_enabled", "Uzaktan izleme")):
        if key in c:
            parts.append("%s %s" % (label, "açıldı" if c[key] else "kapatıldı"))
    return ", ".join(parts) or "Yetenek ayarı değişti"


def build_items(device, sessions, device_audits, fleet_audits, tasks):
    """Veritabanı satırlarından tepsinin göstereceği liste (en yeni başta, en çok MAX_ITEMS)."""
    items = []
    for s in sessions or []:
        detail = _reason(s.get("reason"))
        if s.get("end_time"):
            detail = (detail + " · " if detail else "") + "Bitiş: %s" % s["end_time"]
        title = "Uzaktan izleme oturumu" + (" (zorunlu)" if s.get("is_mandatory") else "")
        items.append(_item(s.get("start_time"), "remote_session", title, s.get("admin_name"), detail))

    for a in device_audits or []:
        c = _json(a.get("changes"))
        action = a.get("action")
        at = a.get("timestamp")
        if action == "lockdown":
            items.append(_item(at, "quarantine", "Cihaz karantinaya alındı", c.get("admin"), _reason(c.get("reason"))))
        elif action == "unlock":
            items.append(_item(at, "quarantine", "Karantina kaldırıldı", c.get("admin"), _reason(c.get("reason"))))
        elif action == "update_result":
            # Reddin sebebi (imza, sürüm, indirme...) ayrıntıda
            why = str(c.get("detail") or "")[:300] if c.get("status") == "rejected" else None
            items.append(_item(at, "update", _update_title(c), None, why))
        elif action == "set_capabilities":
            items.append(_item(at, "capability", _capability_title(c), c.get("by")))
        elif action in ("enroll", "NEW_DEVICE"):
            # NEW_DEVICE'ın ayrıntısında donanım seri numaraları var: gösterilmez
            title = "Cihaz POps'a kaydedildi" if action == "enroll" else "Cihaz sunucuya ilk kez bağlandı"
            items.append(_item(at, "enroll", title))

    for a in fleet_audits or []:
        c = _json(a.get("changes"))
        if device not in (c.get("dispatched") or []):
            continue
        action = a.get("action")
        at = a.get("timestamp")
        if action == "deploy_update":
            title = "Ajan güncellemesi gönderildi (%s)" % (c.get("version") or "?")
            items.append(_item(at, "update", title, c.get("by")))
        elif action == "scan_updates":
            items.append(_item(at, "windows_update", "Windows güncellemeleri tarandı", c.get("by")))
        elif action == "install_updates":
            items.append(_item(at, "windows_update", "Windows güncellemelerinin kurulması başlatıldı", c.get("by")))

    for t in tasks or []:
        detail = ("Durum: %s" % t["status"]) if t.get("status") else None
        items.append(_item(t.get("created_at"), "command", "Uzaktan komut çalıştırıldı", t.get("created_by"), detail))

    items = [i for i in items if i["at"]]
    items.sort(key=lambda i: str(i["at"]), reverse=True)
    return items[:MAX_ITEMS]


@router.get("/api/activity/agent/{pc_name}")
async def agent_activity(pc_name: str, agent_id: Optional[str] = Depends(agent_http_auth)):
    if agent_id is None:
        raise HTTPException(status_code=401, detail="Bu uç yalnızca kayıtlı (anahtarlı) ajanları kabul eder.")
    await bind_agent(agent_id, pc_name)
    _throttle(pc_name)
    # Zaman damgaları yerel saatte 'YYYY-MM-DD HH:MM:SS' metni: metin karşılaştırması tarih sırasıyla aynıdır
    since = (datetime.datetime.now() - datetime.timedelta(days=DAYS)).strftime("%Y-%m-%d %H:%M:%S")
    sessions = await execute_query(
        "SELECT start_time, end_time, admin_name, reason, is_mandatory FROM enterprise_audit_logs "
        "WHERE target_pc = $1 AND start_time >= $2 ORDER BY start_time DESC LIMIT $3",
        (pc_name, since, MAX_ITEMS),
        fetch=True,
    )
    device_audits = await execute_query(
        "SELECT action, changes, timestamp FROM device_audit_logs "
        "WHERE hw_id = $1 AND action = ANY($2::text[]) AND timestamp >= $3 ORDER BY id DESC LIMIT $4",
        (pc_name, list(_DEVICE_ACTIONS), since, MAX_ITEMS),
        fetch=True,
    )
    fleet_audits = await execute_query(
        "SELECT action, changes, timestamp FROM device_audit_logs "
        "WHERE hw_id = '*' AND action = ANY($1::text[]) AND timestamp >= $2 AND position($3 in changes) > 0 "
        "ORDER BY id DESC LIMIT $4",
        (list(_FLEET_ACTIONS), since, '"%s"' % pc_name, MAX_ITEMS),
        fetch=True,
    )
    tasks = await execute_query(
        "SELECT created_at, created_by, status FROM tasks WHERE target_pc = $1 AND created_at >= $2 "
        "ORDER BY id DESC LIMIT $3",
        (pc_name, since, MAX_ITEMS),
        fetch=True,
    )
    rows = [[dict(r) for r in (x or [])] for x in (sessions, device_audits, fleet_audits, tasks)]
    return {"device": pc_name, "days": DAYS, "items": build_items(pc_name, *rows)}
