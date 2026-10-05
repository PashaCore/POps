"""Bildirim uçları: panel zili (liste, okundu), kanal ayarları ve test gönderimi.

Webhook adresi yalnızca superadmin tarafından, yalnızca http(s) olarak ayarlanabilir. SMTP gizli
bilgileri .env'dedir; panel yalnızca "yapılandırıldı mı" bilgisini görür."""

import re

from fastapi import APIRouter, Depends, HTTPException

from pops import notify as notify_mod, timeutil
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.models import NotificationsReadInput, NotifySettingsInput
from pops.security import require_admin, require_superadmin

router = APIRouter()

# Alan adı kısmı noktalarla ayrılmış etiketler; etiket noktayı içermediği için ifade belirsiz değil (doğrusal)
_EMAIL_RE = re.compile(r"^[^@\s,;]+@[^@\s,;.]+(?:\.[^@\s,;.]+)+$")


@router.get("/api/notifications")
async def list_notifications(limit: int = 30, auth: dict = Depends(require_admin)):
    limit = max(1, min(limit, 200))
    rows = await execute_query(
        "SELECT id, created_at, event, severity, pc_name, title, detail, channels, delivery_error, is_read "
        "FROM notifications ORDER BY id DESC LIMIT $1",
        (limit,),
        fetch=True,
    )
    unread = await execute_query("SELECT count(*) AS n FROM notifications WHERE NOT is_read", fetch=True)
    items = []
    for r in rows or []:
        r = dict(r)
        r["created_at"] = timeutil.iso(r["created_at"])
        items.append(r)
    return {"items": items, "unread": int(unread[0]["n"]) if unread else 0}


@router.post("/api/notifications/read")
async def mark_notifications_read(data: NotificationsReadInput, auth: dict = Depends(require_admin)):
    if data.ids:
        await execute_query("UPDATE notifications SET is_read = TRUE WHERE id = ANY($1::int[])", (data.ids,))
    else:
        await execute_query("UPDATE notifications SET is_read = TRUE WHERE NOT is_read")
    return {"ok": True}


@router.post("/api/notifications/clear")
async def clear_notifications(data: NotificationsReadInput, auth: dict = Depends(require_admin)):
    """Zili temizler: verilen kayıtları ya da (ids boşsa) okunmuş olanların hepsini siler. Bildirimler denetim kaydı
    değildir; olayların kendisi hash-zincirli device_audit_logs'ta ve olay günlüğünde kalır."""
    if data.ids:
        rows = await execute_query(
            "DELETE FROM notifications WHERE id = ANY($1::int[]) RETURNING id", (data.ids,), fetch=True
        )
    else:
        rows = await execute_query("DELETE FROM notifications WHERE is_read RETURNING id", fetch=True)
    return {"ok": True, "deleted": len(rows or [])}


def _settings_out(s: dict) -> dict:
    return {
        "enabled": s.get("notify_enabled") == "1",
        "min_severity": s.get("notify_min_severity") or "high",
        "email_to": s.get("notify_email_to") or "",
        "webhook_url": s.get("notify_webhook_url") or "",
        "smtp_configured": notify_mod.smtp_configured(),
    }


@router.get("/api/system/notify-settings")
async def get_notify_settings(auth: dict = Depends(require_superadmin)):
    return _settings_out(await notify_mod.get_settings())


def _validated(data: NotifySettingsInput) -> dict:
    if data.min_severity not in notify_mod.SEVERITIES:
        raise HTTPException(status_code=400, detail="Geçersiz önem seviyesi.")
    emails = [e.strip() for e in (data.email_to or "")[:5000].split(",") if e.strip()]
    # Uzunluk düzenli ifadeden ÖNCE sınırlanır (RFC 5321: adres en fazla 254 karakter); uzun girdi ifadeye gitmez
    bad = [e for e in emails if len(e) > 254 or not _EMAIL_RE.match(e[:254])]
    if bad or len(emails) > 20:
        raise HTTPException(status_code=400, detail="Geçersiz e-posta adresi: %s" % ", ".join(bad[:3]))
    url = (data.webhook_url or "").strip()
    if url and (not re.match(r"^https?://[^\s]+$", url) or len(url) > 500):
        raise HTTPException(status_code=400, detail="Webhook adresi http:// ya da https:// ile başlamalı.")
    if url:
        try:
            notify_mod.resolve_webhook(url)
        except ValueError as exc:
            raise HTTPException(status_code=400, detail="Webhook adresi kabul edilmedi: %s" % exc)
    return {
        "notify_enabled": "1" if data.enabled else "0",
        "notify_min_severity": data.min_severity,
        "notify_email_to": ", ".join(emails),
        "notify_webhook_url": url,
    }


@router.post("/api/system/notify-settings")
async def save_notify_settings(data: NotifySettingsInput, auth: dict = Depends(require_superadmin)):
    values = _validated(data)
    for k, v in values.items():
        await execute_query(
            "INSERT INTO global_settings (key, value) VALUES ($1, $2) ON CONFLICT (key) DO UPDATE SET value = $2",
            (k, v),
        )
    await add_audit_log(
        "*",
        "notify_settings",
        "Bildirim ayarları değişti (%s)" % auth.get("sub"),
        {
            "by": auth.get("sub"),
            "enabled": data.enabled,
            "min_severity": data.min_severity,
            "email_count": len([e for e in values["notify_email_to"].split(",") if e.strip()]),
            "webhook": bool(values["notify_webhook_url"]),
        },
    )
    return _settings_out(await notify_mod.get_settings())


@router.post("/api/system/notify-test")
async def test_notify(data: NotifySettingsInput, auth: dict = Depends(require_superadmin)):
    """Formdaki (henüz kaydedilmemiş olabilir) ayarlarla bir test bildirimi gönderir."""
    values = _validated(data)
    if not values["notify_email_to"] and not values["notify_webhook_url"]:
        raise HTTPException(status_code=400, detail="Önce bir e-posta adresi ya da webhook adresi girin.")
    if values["notify_email_to"] and not notify_mod.smtp_configured():
        if not values["notify_webhook_url"]:
            raise HTTPException(
                status_code=400, detail="E-posta için sunucuda SMTP ayarlı değil (.env: SMTP_HOST, SMTP_FROM)."
            )
    return await notify_mod.send_test(values)
