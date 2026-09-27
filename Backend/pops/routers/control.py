"""Uzaktan kontrol/izleme: denetim oturumu, karantina, bypass kodu, panel ve vision WebSocket'leri,
önizleme ve uzaktan girdi, audit zinciri doğrulaması."""

import asyncio
import datetime
import hashlib
import json
import secrets
import time

from fastapi import APIRouter, Depends, HTTPException, WebSocket, WebSocketDisconnect, status

from pops.config import BYPASS_SECRET, JWT_COOKIE_NAME
from pops.db import execute_query
from pops.models import EndAuditSessionInput, LockdownInput, RemoteInputData, StartAuditSessionInput
from pops.security import require_admin, require_auth, require_superadmin, verify_jwt, verify_session
from pops.agent_auth import enforce_agent_auth_enabled, valid_enroll_token, verify_agent_secret
from pops.audit import _audit_entry_hash, add_audit_log, log_audit_event
from pops.manager import manager

router = APIRouter()


@router.post("/api/audit/session/start")
async def start_audit_session(data: StartAuditSessionInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    session_id = f"SES-{secrets.token_hex(6).upper()}"
    # Rıza sorulmadan açılan (zorunlu) oturum için gerekçe şarttır
    if data.is_mandatory and not data.reason.strip():
        raise HTTPException(status_code=400, detail="Zorunlu oturum için gerekçe yazılmalıdır.")
    admin_name, admin_role = auth.get('sub'), auth.get('role')
    admin_row = await execute_query("SELECT id FROM users WHERE username = $1", (admin_name,), fetch=True)
    admin_id = admin_row[0]["id"] if admin_row else None

    await execute_query(
        """
        INSERT INTO enterprise_audit_logs
        (session_id, admin_id, admin_name, admin_role, target_pc, start_time, end_time, reason, is_notified,
            is_mandatory, status)
        VALUES ($1, $2, $3, $4, $5, $6, NULL, $7, TRUE, $8, 'Active')
    """,
        (session_id, admin_id, admin_name, admin_role, data.target_pc, now, data.reason, data.is_mandatory),
    )
    # F1/F12: bu oturum, uzaktan girdi ve canlı kare almanın ÖN KOŞULU. Oturumu aç (admin, cihaz).
    manager.add_vision_session(data.target_pc, admin_name)
    # Hesap verebilirlik (F4): kontrol oturumunu ajanların yazamadığı hash-zincirli loga META olarak
    # yaz (ham tuş/koordinat DEĞİL — sadece kim, hangi cihaz, gerekçe, zorunlu mu).
    await add_audit_log(
        data.target_pc,
        "remote_session_start",
        "Uzaktan denetim oturumu açıldı: %s → %s" % (admin_name, data.target_pc),
        {
            "session_id": session_id,
            "admin": admin_name,
            "role": admin_role,
            "reason": data.reason,
            "mandatory": bool(data.is_mandatory),
        },
    )
    # Karantina durumunu kontrol et
    rows = await execute_query("SELECT is_quarantined FROM clients WHERE pc_name = $1", (data.target_pc,), fetch=True)
    is_quarantined = False
    if rows and len(rows) > 0:
        is_quarantined = rows[0].get("is_quarantined", False)

    countdown = 5 if is_quarantined else 30
    if not data.is_mandatory:
        countdown = 0

    # Hedef PC'ye bağlantı komutunu (token ile birlikte) gönder
    payload = {
        "action": "start_vision_session",
        "session_id": session_id,
        "is_mandatory": data.is_mandatory,
        "admin_name": admin_name,
        "reason": data.reason,
        "countdown_seconds": countdown,
        "is_quarantined": is_quarantined,
    }
    await manager.send_command(payload, data.target_pc)

    return {"status": "success", "session_id": session_id, "countdown_seconds": countdown}


@router.post("/api/audit/session/end")
async def end_audit_session(data: EndAuditSessionInput, auth: dict = Depends(require_admin)):
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    # Oturumu kapatmadan önce hedef+admin'i öğren ki vision-session yetkisini geri alalım (F1/F12).
    srow = await execute_query(
        "SELECT target_pc, admin_name FROM enterprise_audit_logs WHERE session_id = $1", (data.session_id,), fetch=True
    )
    await execute_query(
        "UPDATE enterprise_audit_logs SET end_time = $1, status = $2 WHERE session_id = $3",
        (now, data.status, data.session_id),
    )
    if srow:
        manager.remove_vision_session(srow[0]["target_pc"], srow[0]["admin_name"])
    return {"status": "success"}


@router.post("/api/security/lockdown")
async def lockdown_pc(data: LockdownInput, auth: dict = Depends(require_admin)):

    # Karantina logunu yaz
    admin_name = auth.get('sub')
    await log_audit_event(
        data.target_pc,
        "Critical Security",
        f"🚨 KARANTİNA BAŞLATILDI by {admin_name} - Neden: {data.reason}",
        actor_id=admin_name,
        event_type="security.lockdown",
        category="security",
        action="lockdown",
        risk_level="critical",
        reason=data.reason,
    )
    # F4(c): hassas admin işlemi → ajanların yazamadığı, hash-zincirli device_audit_logs'a da düş.
    await add_audit_log(
        data.target_pc,
        "lockdown",
        "Karantina başlatıldı: %s" % admin_name,
        {"admin": admin_name, "reason": data.reason},
    )

    # Cihazı karantina moduna al
    await execute_query("UPDATE clients SET is_quarantined = TRUE WHERE pc_name = $1", (data.target_pc,))

    # Ajanı kilitleme emri gönder
    await manager.send_command({"action": "lockdown", "reason": data.reason}, data.target_pc)

    return {"status": "success", "message": "Karantina sinyali gönderildi."}


@router.post("/api/security/unlock")
async def unlock_pc(data: LockdownInput, auth: dict = Depends(require_admin)):

    # Karantina logunu yaz
    admin_name = auth.get('sub')
    await log_audit_event(
        data.target_pc,
        "Critical Security",
        f"✅ KARANTİNA KALDIRILDI by {admin_name} - Neden: {data.reason}",
        actor_id=admin_name,
        event_type="security.unlock",
        category="security",
        action="unlock",
        risk_level="info",
        reason=data.reason,
    )
    await add_audit_log(
        data.target_pc, "unlock", "Karantina kaldırıldı: %s" % admin_name, {"admin": admin_name, "reason": data.reason}
    )

    # Cihazı karantina modundan çıkar
    await execute_query("UPDATE clients SET is_quarantined = FALSE WHERE pc_name = $1", (data.target_pc,))

    # Ajanı kilit açma emri gönder
    await manager.send_command({"action": "unlock"}, data.target_pc)

    return {"status": "success", "message": "Karantina kaldırma sinyali gönderildi."}


def offline_bypass_code(hw_id: str, day: datetime.date) -> str:
    """Ajanın ağ bağlantısı olmadan doğruladığı günlük 6 haneli bypass kodu.

    Formül POpsAgent Worker.cs (UNLOCK_BYPASS) ile aynıdır: SHA-256(hw_id + BYPASS_SECRET + yyyy-MM-dd).
    Kod sunucunun yerel tarihine göre üretilir; sunucu ve ajanlar aynı saat diliminde olmalıdır.
    """
    raw = f"{hw_id}{BYPASS_SECRET}{day.strftime('%Y-%m-%d')}"
    return hashlib.sha256(raw.encode('utf-8')).hexdigest()[:6].upper()


@router.get("/api/security/bypass_token/{pc_name}")
async def get_bypass_token(pc_name: str, auth: dict = Depends(require_admin)):
    # Karantinadaki (çevrimdışı) cihaz için tepsi uygulamasına girilecek kod
    if not BYPASS_SECRET:
        return {"status": "error", "message": "BYPASS_SECRET tanımlı değil (bkz. .env.example)"}
    today = datetime.date.today()
    token = offline_bypass_code(pc_name, today)
    await log_audit_event(
        pc_name,
        "Security",
        "🔑 Çevrimdışı bypass kodu üretildi",
        actor_id=auth.get('sub', 'admin'),
        event_type="security.bypass_code",
        category="security",
        action="bypass_code",
        risk_level="medium",
    )
    await add_audit_log(
        pc_name,
        "bypass_code",
        "Çevrimdışı bypass kodu üretildi: %s" % auth.get('sub', 'admin'),
        {"admin": auth.get('sub')},
    )
    return {"status": "success", "token": token, "valid_for": today.isoformat()}


@router.get("/api/system/audit-verify")
async def audit_verify(auth: dict = Depends(require_superadmin)):
    """Denetim zincirini baştan yürütür; bir kayıt kurcalanmış/silinmişse ilk kırık id'yi döner."""
    rows = await execute_query(
        "SELECT id, hw_id, action, reason, changes, timestamp, prev_hash, entry_hash "
        "FROM device_audit_logs ORDER BY id ASC",
        fetch=True,
    )
    prev = None
    checked = 0
    for r in rows or []:
        if r["entry_hash"] is None:
            continue  # 0004 öncesi eski satırlar zincire dahil değil
        expected = _audit_entry_hash(prev, r["hw_id"], r["action"], r["reason"], r["changes"], r["timestamp"])
        if expected != r["entry_hash"]:
            return {
                "ok": False,
                "first_broken_id": r["id"],
                "reason": "zincir kırık (kurcalanmış/silinmiş)",
                "checked": checked,
            }
        prev = r["entry_hash"]
        checked += 1
    return {"ok": True, "checked": checked, "total": len(rows or [])}


@router.websocket("/ws/panel")
async def websocket_panel(websocket: WebSocket):
    session = await verify_session(verify_jwt(websocket.cookies.get(JWT_COOKIE_NAME) or ""))
    if not session:
        await websocket.accept()
        await websocket.close(code=4001, reason="Kimlik doğrulama hatası")
        return
    username = session.get("sub")
    role = session.get("role")  # DB'den (iptal/rol-düşürme anında geçerli)
    await manager.connect_panel(websocket, username, role)
    last_reverify = time.time()
    try:
        while True:
            data = await websocket.receive_text()
            try:
                msg = json.loads(data)
                if msg.get("type") == "remote_input":
                    target = msg.get("device")
                    # F4: açık soket için de iptal geçerli olsun — kontrol yolunda periyodik (≤10 sn)
                    # yeniden doğrula; kullanıcı silinmiş/rolü düşmüş/token_version artmışsa soketi kapat.
                    if time.time() - last_reverify > 10:
                        fresh = await verify_session(verify_jwt(websocket.cookies.get(JWT_COOKIE_NAME) or ""))
                        if not fresh:
                            await websocket.close(code=4001, reason="Oturum iptal edildi")
                            break
                        role = fresh.get("role")
                        manager.panel_roles[websocket] = role
                        last_reverify = time.time()
                    # F1: viewer HİÇBİR remote_input gönderemez.
                    if role not in ("admin", "superadmin"):
                        continue
                    # KONTROL (gerçek fare/klavye girdisi veya SYSTEM 'execute'): o cihaz için AÇIK
                    # denetim oturumu ŞART. Önizleme-tipi (get_thumbnail/set_fps) admin'e serbest.
                    is_control = bool(msg.get("input_type")) or msg.get("action") == "execute"
                    if is_control and not manager.user_has_session(username, target):
                        continue
                    if is_control:
                        # etkinlik oturum süresini uzatır (idle-timeout)
                        manager.touch_vision_session(target, username)
                    if target:
                        sent = await manager.send_remote_input_to_vision(msg, target)
                        if not sent:
                            await manager.send_command(msg, target)
                elif msg.get("type") == "ping":
                    await websocket.send_text(json.dumps({"type": "pong"}))
            except json.JSONDecodeError:
                pass
    except WebSocketDisconnect:
        pass
    finally:
        manager.disconnect_panel(websocket)


@router.websocket("/ws/vision/{pc_name}")
async def websocket_vision(websocket: WebSocket, pc_name: str):
    await websocket.accept()
    # Faz 3 accept-both: enforce açıkken kimliksiz vision tüneli reddedilir (sahte ekran engellenir)
    if await enforce_agent_auth_enabled() and not (
        await verify_agent_secret(pc_name, websocket.headers.get("X-Agent-Secret"))
        or await valid_enroll_token(websocket.headers.get("X-Enroll-Token"))
    ):
        await add_audit_log(pc_name, "auth_reject", "Kimliksiz vision baglantisi reddedildi (enforce acik)", {})
        await websocket.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")
        return
    manager.active_vision_ws[pc_name] = websocket
    try:
        while True:
            data = await websocket.receive_text()
            try:
                payload = json.loads(data)
                if payload.get("type") in ["stream_frame", "thumbnail"]:
                    # F12: kare yalnızca o cihaz için açık oturumu olan admin panellerine
                    await manager.send_frame_to_viewers(payload, pc_name)
            except json.JSONDecodeError:
                pass
    except WebSocketDisconnect:
        manager.disconnect_vision(pc_name)


# Ekran akışı yalnızca Vision oturumu (rıza/bildirim akışı) üzerinden başlatılır;
# rıza sormadan yakalama başlatan eski /api/stream/start ucu kaldırıldı.


@router.get("/api/stream/stop/{pc_name}")
async def stop_stream(pc_name: str, auth: dict = Depends(require_auth)):
    await manager.send_command({"action": "stop_stream"}, pc_name)
    return {"status": "stopped"}


@router.get("/api/thumbnail/{pc_name}")
async def get_thumbnail(pc_name: str, auth: dict = Depends(require_admin)):
    # F1: ekran önizlemesi salt-okur viewer'a kapalı (yalnız admin/superadmin)
    if pc_name not in manager.active_agents:
        return {"status": "error", "image": None}
    loop = asyncio.get_event_loop()
    fut = loop.create_future()
    if pc_name not in manager.pending_thumbnails:
        manager.pending_thumbnails[pc_name] = []
    manager.pending_thumbnails[pc_name].append(fut)
    await manager.send_command({"type": "remote_input", "device": pc_name, "action": "get_thumbnail"}, pc_name)
    try:
        image_data = await asyncio.wait_for(fut, timeout=5.0)
        return {"status": "success", "image": image_data}
    except Exception:
        return {"status": "timeout", "image": None}
    finally:
        if pc_name in manager.pending_thumbnails and fut in manager.pending_thumbnails[pc_name]:
            manager.pending_thumbnails[pc_name].remove(fut)


@router.post("/api/remote_input")
async def send_remote_input(data: RemoteInputData, auth: dict = Depends(require_admin)):
    target = data.device
    # F1: uzaktan girdi yalnızca admin + o cihaz için AÇIK denetim oturumu olan kullanıcıdan
    if not manager.user_has_session(auth.get("sub"), target):
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Uzaktan girdi için o cihazda açık bir denetim oturumu gerekir.",
        )
    sent = await manager.send_remote_input_to_vision(data.dict(), target)
    if not sent:
        if target in manager.active_agents:
            await manager.send_command(data.dict(), target)
            return {"status": "success"}
        return {"status": "error"}
    return {"status": "success"}
