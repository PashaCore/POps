"""Ajan uçları: /ws/agent komut kanalı ve ajanın çağırdığı HTTP uçları (envanter, log, politika)."""

import datetime
import json
import secrets
from typing import Optional

from fastapi import APIRouter, Depends, WebSocket, WebSocketDisconnect

from pops.db import execute_query
from pops.models import AgentPoliciesInput, AuthEventInput, HwInventoryInput, LogInput, PolicyAlertInput
from pops.security import require_admin
from pops.agent_auth import (
    _bind_agent,
    _hash_secret,
    agent_http_auth,
    enforce_agent_auth_enabled,
    valid_enroll_token,
    verify_agent_secret,
)
from pops.audit import add_audit_log, log_audit_event
from pops.manager import manager
from pops.taskqueue import process_queue
from pops.dna import reconcile_device

router = APIRouter()


@router.post("/api/auth/login")
async def auth_login(data: AuthEventInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    _bind_agent(agent_id, data.hw_id)  # başka cihaz adına giriş kaydı yazılamaz
    await log_audit_event(
        data.hw_id,
        "Security",
        f"🟢 GİRİŞ: {data.student_id}",
        actor_id=data.student_id,
        event_type="auth.login",
        category="security",
        action="login",
        risk_level="info",
    )
    await execute_query("UPDATE clients SET logged_user=$1 WHERE pc_name=$2", (data.student_id, data.hw_id))
    return {"status": "success"}


@router.post("/api/auth/failed")
async def auth_failed(data: AuthEventInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    _bind_agent(agent_id, data.hw_id)
    await log_audit_event(
        data.hw_id,
        "Security",
        f"🔴 RED: {data.student_id} ({data.message})",
        actor_id=data.student_id,
        event_type="auth.failed",
        category="security",
        action="login_failed",
        risk_level="medium",
        reason=data.message,
    )
    return {"status": "success"}


@router.post("/api/auth/logout")
async def auth_logout(data: AuthEventInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    _bind_agent(agent_id, data.hw_id)
    await log_audit_event(
        data.hw_id,
        "Security",
        "⚪ OTURUM KAPATILDI",
        actor_id="System",
        event_type="auth.logout",
        category="security",
        action="logout",
        risk_level="info",
    )
    await execute_query("UPDATE clients SET logged_user='-' WHERE pc_name=$1", (data.hw_id,))
    return {"status": "success"}


@router.websocket("/ws/agent/{pc_name}")
async def websocket_agent(websocket: WebSocket, pc_name: str):
    await websocket.accept()
    forwarded = websocket.headers.get("X-Forwarded-For")
    client_ip = forwarded.split(",")[0] if forwarded else (websocket.client.host if websocket.client else "Bilinmiyor")
    active_hwid = pc_name
    agent_version = websocket.headers.get("X-Agent-Version", "unknown")

    # ── Faz 3 kimlik doğrulama (accept-both) ──
    # Secret ya da geçerli enroll token varsa kimlikli; hiçbiri yoksa "legacy".
    # enforce_agent_auth KAPALIYKEN legacy bağlantı KABUL edilir (mevcut ajan düşmez);
    # AÇIKKEN reddedilir. Değerlendirme reconcile'dan önce, URL pc_name'e karşı yapılır.
    auth_method = "none"
    pending_enroll = None
    if await verify_agent_secret(active_hwid, websocket.headers.get("X-Agent-Secret")):
        auth_method = "secret"
    else:
        pending_enroll = await valid_enroll_token(websocket.headers.get("X-Enroll-Token"))
        if pending_enroll:
            auth_method = "enroll"
    if auth_method == "none" and await enforce_agent_auth_enabled():
        # Kimliksiz: audit'i ajanların YAZAMADIĞI device_audit_logs'a düş, sonra reddet.
        await add_audit_log(
            active_hwid,
            "auth_reject",
            "Kimliksiz ajan bağlantısı reddedildi (enforce açık)",
            {"ip": client_ip, "agent_version": agent_version},
        )
        await websocket.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")
        return

    manager.active_agents[active_hwid] = websocket

    async def handle_routine_payload(pld):
        current_time = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        current_hostname = pld.get("hostname", active_hwid)
        if pld.get("type") == "result":
            # Ajan yalnızca kendisine atanmış görevin sonucunu yazabilir
            await execute_query(
                "UPDATE tasks SET status = 'Completed', output = $1 WHERE id = $2 AND target_pc = $3",
                (pld.get("output"), pld.get("task_id"), active_hwid),
            )
            await manager.broadcast_to_panels(
                {
                    "type": "terminal_output",
                    "id": active_hwid,
                    "pc_name": current_hostname,
                    "output": pld.get("output"),
                    "task_id": pld.get("task_id"),
                }
            )
            await process_queue()
            return
        if "status" in pld:
            await execute_query(
                "UPDATE clients SET last_seen=$1, status=$2, active_window=$3, hostname=$4, ip_address=$5 "
                "WHERE pc_name=$6",
                (
                    current_time,
                    pld.get("status"),
                    pld.get("active_window", "-"),
                    current_hostname,
                    client_ip,
                    active_hwid,
                ),
            )

    try:
        data = await websocket.receive_text()
        payload = json.loads(data)
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        if "dna_payload" in payload:
            verified_hwid = await reconcile_device(active_hwid, payload.get("dna_payload"), client_ip, websocket)
            if verified_hwid != active_hwid:
                manager.rename_agent(active_hwid, verified_hwid)
                # Enrolled secret'ı çözümlenen yeni kimliğe taşı (hedefte yoksa)
                await execute_query(
                    "UPDATE agent_secrets SET pc_name=$1 WHERE pc_name=$2 "
                    "AND NOT EXISTS (SELECT 1 FROM agent_secrets WHERE pc_name=$1)",
                    (verified_hwid, active_hwid),
                )
                active_hwid = verified_hwid
            hw = payload.get("dna_payload", {}).get("hardware", {})
            caps = payload.get("dna_payload", {}).get("capabilities", {})
            real_hostname = payload.get("hostname", active_hwid)

            await execute_query(
                "UPDATE tasks SET status = 'Completed (Rebooted)' WHERE target_pc = $1 AND status = 'Running'",
                (active_hwid,),
            )
            await execute_query(
                """
                INSERT INTO clients (pc_name, hostname, lab_name, last_seen, status, active_window, boot_count,
                    ip_address, dna_uuid, dna_bios, dna_disk, dna_mac, dna_ram, cap_ram_readable)
                VALUES ($1, $2, 'Atanmamis_Cihazlar', $3, 'Online', '-', 1, $4, $5, $6, $7, $8, $9, $10)
                ON CONFLICT (pc_name) DO UPDATE SET status='Online', last_seen=$3, ip_address=$4,
                    boot_count=clients.boot_count + 1, hostname=$2, dna_uuid=$5, dna_bios=$6, dna_disk=$7, dna_mac=$8,
                    dna_ram=$9, cap_ram_readable=$10
            """,
                (
                    active_hwid,
                    real_hostname,
                    now,
                    client_ip,
                    hw.get('uuid'),
                    hw.get('bios_sn'),
                    hw.get('disk_sn'),
                    hw.get('mac'),
                    hw.get('ram_sn'),
                    caps.get('ram_readable', True),
                ),
            )

            await execute_query(
                "INSERT INTO agent_versions (pc_name, version, last_update) "
                "VALUES ($1, $2, $3) ON CONFLICT (pc_name) DO UPDATE "
                "SET version=$2, last_update=$3",
                (active_hwid, agent_version, now),
            )

            # Enroll token ile bağlandıysa: tüket, kalıcı secret üret+sakla, ajana gönder, laba ata.
            if auth_method == "enroll" and pending_enroll:
                # F2: Zaten secret'ı OLAN bir cihaza düz enroll token'la yeniden-secret vermek
                # kimlik hırsızlığıdır (saldırgan geçerli token + hedef DNA'sıyla secret'ı ezip
                # ele geçirebilir). allow_reenroll açık DEĞİLSE reddet (Critical audit + 4401).
                existing_secret = await execute_query(
                    "SELECT 1 FROM agent_secrets WHERE pc_name=$1", (active_hwid,), fetch=True
                )
                if existing_secret:
                    rerow = await execute_query(
                        "SELECT allow_reenroll FROM clients WHERE pc_name=$1", (active_hwid,), fetch=True
                    )
                    if not (rerow and rerow[0].get("allow_reenroll")):
                        await add_audit_log(
                            active_hwid,
                            "enroll_denied",
                            "Zaten kayıtlı cihaza enroll token'la yeniden-secret REDDEDİLDİ (olası impersonation)",
                            {"ip": client_ip, "token_id": pending_enroll["id"], "agent_version": agent_version},
                        )
                        await log_audit_event(
                            active_hwid,
                            "Critical Security",
                            "🔴 Enroll ile secret ele geçirme girişimi reddedildi",
                            actor_id="System/Enroll",
                            event_type="agent.enroll_denied",
                            category="security",
                            action="enroll_denied",
                            risk_level="critical",
                            reason="already_enrolled",
                            meta_data={"ip": client_ip},
                        )
                        try:
                            await websocket.close(code=4401, reason="Cihaz zaten kayıtlı")
                        except Exception:
                            pass
                        return
                    # Meşru yeniden-kayıt (admin allow_reenroll açtı): izin ver, bayrağı tek-seferlik temizle.
                    await execute_query("UPDATE clients SET allow_reenroll=FALSE WHERE pc_name=$1", (active_hwid,))
                new_secret = secrets.token_urlsafe(32)
                await execute_query(
                    "INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2) "
                    "ON CONFLICT (pc_name) DO UPDATE SET secret_hash=$2, rotated_at=NOW()",
                    (active_hwid, _hash_secret(new_secret)),
                )
                await execute_query(
                    "UPDATE enroll_tokens SET use_count = use_count + 1, "
                    "is_used = (use_count + 1 >= max_uses), used_by=$1, used_at=NOW() "
                    "WHERE id=$2 AND NOT is_used AND use_count < max_uses",
                    (active_hwid, pending_enroll["id"]),
                )
                if pending_enroll.get("lab_name"):
                    await execute_query(
                        "UPDATE clients SET lab_name=$1 WHERE pc_name=$2", (pending_enroll["lab_name"], active_hwid)
                    )
                await add_audit_log(
                    active_hwid,
                    "enroll",
                    "Ajan enroll token ile kaydoldu",
                    {"lab": pending_enroll.get("lab_name"), "ip": client_ip},
                )
                try:
                    await websocket.send_text(json.dumps({"action": "set_secret", "secret": new_secret}))
                except Exception:
                    pass
                pending_enroll = None
                auth_method = "secret"

            hw_exists = await execute_query(
                "SELECT cpu FROM hw_inventory WHERE pc_name = $1", (active_hwid,), fetch=True
            )
            if not hw_exists or hw_exists[0]["cpu"] == "-":
                await manager.send_command({"action": "get_hardware"}, active_hwid)
            await process_queue()

        await handle_routine_payload(payload)

        while True:
            data = await websocket.receive_text()
            payload = json.loads(data)
            if payload.get("type") == "thumbnail":
                hwid = payload.get("hw_id")
                if hwid in manager.pending_thumbnails:
                    for fut in manager.pending_thumbnails[hwid]:
                        if not fut.done():
                            fut.set_result(payload.get("image", ""))
                    manager.pending_thumbnails[hwid] = []
                # F1 kalıntısı: ekran görüntüsü yalnızca admin panellerine (viewer'a SIZMAZ).
                await manager.broadcast_to_admin_panels(payload)
                continue
            if payload.get("type") == "vision_rejected":
                await manager.broadcast_to_panels(payload)
                continue
            if payload.get("type") == "update_result":
                # Ajanın güncelleme sonucu (POpsUpdater update-result.json'ından). Ajanların
                # yazamadığı device_audit_logs'a düşür + panele bildir.
                detail = {
                    k: payload.get(k)
                    for k in (
                        "status",
                        "from_version",
                        "to_version",
                        "detail",
                        "rollback",
                        "agent_state",
                        "msi_exit_code",
                        "reboot_required",
                        "running_version",
                    )
                }
                await add_audit_log(
                    active_hwid, "update_result", f"Ajan guncelleme sonucu: {payload.get('status', '?')}", detail
                )
                # Güncelleme/rollback sonrası GERÇEKTEN çalışan sürümü sakla (v0.1.3+ ajan gönderir).
                if payload.get("running_version"):
                    await execute_query(
                        "UPDATE clients SET running_version=$1 WHERE pc_name=$2",
                        (str(payload.get("running_version")), active_hwid),
                    )
                # Yalnızca GERÇEKTEN kötü durumlar kritik loglanır. NOT: v0.1.3'ten beri başarılı
                # geri dönüş "rolled_back" (kurtarıldı, kritik değil), "install_failed" ise kurulum
                # hiç başlamadı = makine değişmedi (iyi huylu) → ikisi de kritik SAYILMAZ.
                _astate = str(payload.get("agent_state") or "")
                _st = str(payload.get("status") or "")
                if _astate == "unmanaged" or _st in (
                    "rollback_failed",
                    "failed",
                    "reverted_by_freeze",
                    "error",
                    "rejected",
                ):
                    await log_audit_event(
                        active_hwid,
                        "Critical Security",
                        f"Ajan guncelleme sorunu: {_astate or _st}",
                        actor_id="System/Update",
                        event_type="agent.update",
                        category="system_maintenance",
                        action="update_problem",
                        risk_level="critical",
                        reason=(_astate or _st),
                        meta_data=detail,
                    )
                await manager.broadcast_to_panels({"type": "update_result", "pc_name": active_hwid, **detail})
                continue
            if payload.get("type") == "capabilities":
                # Ajan güncel yetenek durumunu bildirir (bağlantıda + her değişimde). Sakla + panele yay.
                t = payload.get("terminal_enabled")
                v = payload.get("vision_enabled")
                await execute_query(
                    "UPDATE clients SET cap_terminal_enabled=$1, cap_vision_enabled=$2 WHERE pc_name=$3",
                    (bool(t) if t is not None else None, bool(v) if v is not None else None, active_hwid),
                )
                # Yönetici daha önce kapatma istediyse ama ajan hâlâ AÇIK bildiriyorsa (ör. istek
                # çevrimdışıyken verildi) kapatmayı yeniden gönder. Fail-safe: yalnızca kapatırız.
                reqrow = await execute_query(
                    "SELECT cap_terminal_disable_requested AS t, cap_vision_disable_requested AS v "
                    "FROM clients WHERE pc_name=$1",
                    (active_hwid,),
                    fetch=True,
                )
                if reqrow:
                    resend = {}
                    if reqrow[0]["t"] and t:
                        resend["terminal_enabled"] = False
                    if reqrow[0]["v"] and v:
                        resend["vision_enabled"] = False
                    if resend:
                        await manager.send_command({"action": "set_capabilities", **resend}, active_hwid)
                await manager.broadcast_to_panels(
                    {"type": "capabilities", "pc_name": active_hwid, "terminal_enabled": t, "vision_enabled": v}
                )
                continue
            if payload.get("type") == "capability_denied":
                # Ajan, kapalı bir yetenek için gelen isteği reddettiğini bildirir. Denetime yaz + panele yay.
                _md = {k: payload.get(k) for k in ("capability", "action", "task_id")}
                await log_audit_event(
                    active_hwid,
                    "Security",
                    f"Yetenek reddedildi: {payload.get('capability')} ({payload.get('action')})",
                    actor_id="Agent",
                    event_type="agent.capability_denied",
                    category="security",
                    action="capability_denied",
                    risk_level="medium",
                    reason=str(payload.get("capability") or ""),
                    meta_data=_md,
                )
                await manager.broadcast_to_panels({"type": "capability_denied", "pc_name": active_hwid, **_md})
                continue
            await handle_routine_payload(payload)
    except WebSocketDisconnect:
        pass
    except Exception as e:
        # Bozuk mesaj veya beklenmeyen hata: soket kapansın ki cihaz yanlışlıkla Online görünmesin
        print(f"⚠️ /ws/agent/{active_hwid} hata: {e}")
        try:
            await websocket.close(code=1011)
        except Exception:
            pass
    finally:
        # Yeniden bağlanan ajanın yeni soketi kayıtlıysa ona dokunulmaz
        if manager.disconnect_agent(active_hwid, websocket):
            await execute_query("UPDATE clients SET status = 'Offline' WHERE pc_name = $1", (active_hwid,))


@router.post("/api/inventory/{pc_name}")
async def update_inventory(pc_name: str, data: HwInventoryInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    _bind_agent(agent_id, pc_name)  # başka cihaz adına envanter yazılamaz
    now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    await execute_query(
        "INSERT INTO hw_inventory (pc_name, hostname, cpu, ram, motherboard, gpu, os_version, ip_address, "
        "mac_address, disk_info, last_updated) "
        "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) "
        "ON CONFLICT (pc_name) DO UPDATE SET hostname=EXCLUDED.hostname, cpu=EXCLUDED.cpu, ram=EXCLUDED.ram, "
        "motherboard=EXCLUDED.motherboard, gpu=EXCLUDED.gpu, os_version=EXCLUDED.os_version, "
        "ip_address=EXCLUDED.ip_address, mac_address=EXCLUDED.mac_address, disk_info=EXCLUDED.disk_info, "
        "last_updated=EXCLUDED.last_updated",
        (
            pc_name,
            data.hostname,
            data.cpu,
            data.ram,
            data.motherboard,
            data.gpu,
            data.os_version,
            data.ip_address,
            data.mac_address,
            data.disk_info,
            now,
        ),
    )
    return {"status": "success"}


@router.post("/api/logs/{pc_name}")
async def add_log(pc_name: str, data: LogInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    _bind_agent(agent_id, pc_name)  # başka cihaz adına log yazılamaz
    await log_audit_event(
        pc_name=pc_name,
        log_type=data.log_type or "System",
        message=data.message or "",
        actor_id=data.actor_id or "Agent",
        event_type=data.event_type or "agent.log",
        category=data.category or "legacy",
        action=data.action or "unknown",
        risk_level=data.risk_level or "info",
        reason=data.reason or "",
        meta_data=data.meta_data or {},
    )
    return {"status": "success"}


@router.post("/api/agent_policies")
async def save_policies(data: AgentPoliciesInput, auth: dict = Depends(require_admin)):
    val = json.dumps(
        {
            "fair_use_text": data.fair_use_text,
            "dns_categories": data.dns_categories,
            "auto_quarantine": data.auto_quarantine,
            "quarantine_threshold": data.quarantine_threshold,
            "dns_domains": data.dns_domains or {},
        },
        ensure_ascii=False,
    )
    await execute_query(
        "INSERT INTO global_settings (key, value) "
        "VALUES ('agent_policies', $1) ON CONFLICT (key) DO UPDATE "
        "SET value = $1",
        (val,),
    )
    return {"status": "success"}


@router.get("/api/agent_policies")
async def get_policies():
    # Ajanlar JWT taşımaz; adil kullanım metni ve DNS kategorilerini okuyabilmeleri için bu uç
    # kimlik doğrulaması istemez. Politikayı değiştirmek (POST) admin JWT gerektirir.
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'agent_policies'", fetch=True)
    if row:
        pol = json.loads(row[0]["value"])
    else:
        pol = {
            "fair_use_text": "Bu cihaz POps platformu tarafından izlenmekte ve yönetilmektedir.",
            "dns_categories": ["yasadisi_bahis", "pornografi"],
            "auto_quarantine": False,
            "quarantine_threshold": 5,
        }
    # F8: ajan sözleşmesi — dns_domains her zaman bulunsun (yoksa {} => DNS tespiti kapalı, güvenli).
    pol.setdefault("dns_domains", {})
    return pol


@router.post("/api/policy_alert")
async def add_policy_alert(data: PolicyAlertInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    _bind_agent(agent_id, data.hw_id)  # başka cihaz adına ihlal uyarısı yazılamaz
    await log_audit_event(
        pc_name=data.hw_id,
        log_type="Security",
        message=f"🚨 KURAL İHLALİ: {data.domain} ({data.category})",
        actor_id=data.hw_id,
        event_type="policy.alert",
        category="restricted_content",
        action="dns_block",
        risk_level="high",
        reason="DNS Kural İhlali",
        meta_data={"domain": data.domain, "violation_category": data.category},
    )
    return {"status": "success"}
