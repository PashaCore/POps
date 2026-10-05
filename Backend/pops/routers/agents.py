"""Ajan uçları: /ws/agent komut kanalı ve ajanın çağırdığı HTTP uçları (envanter, log, politika)."""

import datetime
import json
import logging
import re
import secrets
import time
from typing import Optional

from fastapi import APIRouter, Depends, Request, WebSocket, WebSocketDisconnect
from starlette.websockets import WebSocketState

from pops import db, exams, modules
from pops.db import execute_query
from pops.models import AgentPoliciesInput, AuthEventInput, HwInventoryInput, LogInput, PolicyAlertInput
from pops.security import require_admin, require_auth
from pops.agent_auth import (
    bind_agent,
    _hash_secret,
    agent_http_auth,
    enforce_agent_auth_enabled,
    valid_enroll_token,
    verify_agent_secret,
)
from pops.audit import add_audit_log, log_audit_event
from pops.manager import manager
from pops.taskqueue import process_queue
from pops.dna import check_known_device, reconcile_device, clean_payload as clean_dna_payload
from pops.notify import notify
from pops import agent_health, agent_version as agent_version_mod, bypass, devicelist, heartbeats, metrics
from pops import update_notice, winget
from pops import update_tracking
from pops.routers import files as file_transfer

log = logging.getLogger("pops.agents")
# Ajanın çalıştırmadığı komutun sonucu bu önekle başlar (Agent CommandExecutionPolicy.DisabledMessage). Çıkış kodu
# eski ajanlarda yoktur, 0.1.13+ ajanlarda -5 (reddedildi); winget_install'da -7 = bilgisayarda winget yok.
REFUSED_PREFIX = "[REDDEDİLDİ]"
REFUSED_EXIT_CODES = (None, winget.EXIT_DENIED, winget.EXIT_UNAVAILABLE)
router = APIRouter()


@router.post("/api/auth/login")
async def auth_login(data: AuthEventInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    await bind_agent(agent_id, data.hw_id)  # başka cihaz adına giriş kaydı yazılamaz
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
    devicelist.touch([data.hw_id])
    return {"status": "success"}


@router.post("/api/auth/failed")
async def auth_failed(data: AuthEventInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    await bind_agent(agent_id, data.hw_id)
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
    await bind_agent(agent_id, data.hw_id)
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
    devicelist.touch([data.hw_id])
    return {"status": "success"}


QUARANTINE_RESEND_SECONDS = 300
_quarantine_resent = {}  # pc_name -> son yeniden gönderim zamanı


_isolation_warned = set()  # ağ yalıtımı uygulanamadığı bildirilmiş cihazlar (uyarı bir kez)


async def _warn_isolation(pc_name: str, network_isolated: Optional[bool], reported: bool, error: str) -> None:
    """Kilit ekranı açık ama ağ yalıtımı uygulanamamışsa (0.1.13+ ajan bildirir) yönetici bir kez uyarılır."""
    partial = reported and network_isolated is False
    if partial and pc_name not in _isolation_warned:
        _isolation_warned.add(pc_name)
        await add_audit_log(
            pc_name,
            "quarantine_partial",
            "Karantina: kilit ekranı açık ama ağ yalıtımı uygulanamadı",
            {"error": error or None},
        )
        await notify(
            "quarantine_partial",
            "high",
            "Karantina ağ yalıtımı uygulanamadı (yalnız kilit ekranı açık)",
            error or "",
            pc_name,
        )
    elif not partial:
        _isolation_warned.discard(pc_name)


async def reconcile_quarantine(
    pc_name: str, reported: bool, network_isolated: Optional[bool] = None, isolation_error: str = ""
) -> None:
    """Ajanın heartbeat'te bildirdiği kilit durumu (0.1.5+, yalnız anahtarlı bağlantı) ile panel durumunu eşitler.
    Bekleyen yönetici işlemi varsa: ajan istenen durumdaysa işlem tamamlanır, değilse komut yeniden gönderilir
    (en fazla 5 dakikada bir). Bekleyen işlem yoksa panel ajanın gerçek durumunu gösterir (ör. kendini karantinaya
    aldığını bildiren istek kaybolduysa). network_isolated (0.1.13+): kilit istenmiş ve ekran kilitli ama ağ
    yalıtılamamışsa işlem tamamlanmış sayılmaz; kilit komutu yeniden gönderilir (ajan yalıtımı yeniden dener)."""
    await _warn_isolation(pc_name, network_isolated, reported, isolation_error)
    rows = await execute_query(
        "SELECT is_quarantined, pending_quarantine_action AS act, pending_quarantine_reason AS reason "
        "FROM clients WHERE pc_name = $1",
        (pc_name,),
        fetch=True,
    )
    if not rows:
        return
    row = rows[0]
    if row["act"] in ("lock", "unlock"):
        want = row["act"] == "lock"
        if reported == want and not (want and network_isolated is False):
            await execute_query(
                "UPDATE clients SET is_quarantined = $1, pending_quarantine_action = NULL, "
                "pending_quarantine_reason = NULL WHERE pc_name = $2",
                (reported, pc_name),
            )
            devicelist.touch([pc_name])
            _quarantine_resent.pop(pc_name, None)
            return
        now = time.time()
        if now - _quarantine_resent.get(pc_name, 0) >= QUARANTINE_RESEND_SECONDS:
            _quarantine_resent[pc_name] = now
            cmd = {"action": "lockdown", "reason": row["reason"] or ""} if want else {"action": "unlock"}
            await manager.send_command(cmd, pc_name)
        return
    if bool(row["is_quarantined"]) != reported:
        await execute_query("UPDATE clients SET is_quarantined = $1 WHERE pc_name = $2", (reported, pc_name))
        devicelist.touch([pc_name])
        await add_audit_log(
            pc_name,
            "quarantine_state",
            "Panel karantina durumu ajanın bildirdiğine eşitlendi: %s" % ("kilitli" if reported else "açık"),
            {"reported": reported},
        )


# Sunucunun desteklediği, ajanın davranışını değiştiren özellikler (0.1.14+ ajan okur; eskiler bilinmeyen action'ı
# yok sayar). update_result_ack: güncelleme sonucu kaydedilince onaylanır, ajan onaya kadar sonucu saklar.
# update_progress: güncellemenin ara adımları okunur (eski sunucu bilinmeyen mesajı zaten yok sayar; ajan isterse
# yalnızca bunu duyuran sunucuya gönderir). file_transfer: file_push / file_pull komutları ve file_result iletisi
# (bkz. routers/files.py); ajan dosya aktarımını yalnızca bunu duyuran sunucudan kabul edebilir. exam_mode: sınav
# modu gönderilir ve exam_state okunur (pops/exams.py).
# winget: sunucu winget görevlerini "winget_install" ile gönderir ve ajanın bağlanırken X-Agent-Features ile duyurduğu
# özellikleri okur (bkz. pops/winget.py); görev yalnızca "winget" duyuran ajana gider.
# vision_binary: /ws/vision ikili kareleri ve monitors/select_monitor/set_quality'yi bilir (eski sunucu ikili mesajda
# tüneli düşürürdü). vision_clipboard: pano metni aktarılır (bkz. docs/vision.md).
SERVER_FEATURES = (
    "update_result_ack", "result_ack", "update_progress", "file_transfer", "exam_mode", "winget", "vision_binary",
    "vision_clipboard",
)
# Ajan protokolünün sürümü (docs/protocol/README.md): yalnızca uyumsuz bir değişiklikte artar. Yeni alan ya da yeni
# mesaj sürümü değiştirmez; sunucunun yeni davranışları SERVER_FEATURES ile duyurulur.
PROTOCOL_VERSION = 1


def _server_version() -> str:
    import system_routes  # sürüm tek yerden okunur (VERSION / POPS_VERSION); döngüsel import olmasın diye burada

    return system_routes._read_version()


def server_info_message() -> dict:
    return {
        "action": "server_info",
        "version": _server_version(),
        "protocol": PROTOCOL_VERSION,
        "features": list(SERVER_FEATURES),
    }


async def _send_server_info(websocket: WebSocket) -> None:
    try:
        await websocket.send_text(json.dumps(server_info_message()))
    except Exception:
        pass


async def _sync_exam(pc_name: str) -> None:
    """Bağlanan ajanı sınıfının sınav durumuna getirir. Hata bağlantıyı düşürmez: ajan bir sonraki bağlanışında ya da
    exam_state bildirdiğinde yeniden eşitlenir."""
    try:
        await exams.sync_pc(pc_name)
    except Exception as exc:
        log.warning("sınav durumu eşitlenemedi", extra={"pc_name": pc_name, "error": repr(exc)[:300]})


async def _ack_update_result(pc_name: str, result_id: str) -> None:
    await manager.send_command({"action": "update_result_ack", "result_id": result_id}, pc_name)


_RESULT_FIELDS = (
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


async def _store_update_result(pc_name: str, payload: dict) -> None:
    """Güncelleme sonucunun kaydı: ajanların yazamadığı device_audit_logs, çalışan sürüm, kritikse güvenlik kaydı,
    sonucu beklenen gönderimin (ve son adımının) kapanması, bildirim ve panele yayın. update_result ve ajanın
    "rejected" adımı (sonuç dosyası yazılmadan biten ret) aynı yoldan geçer."""
    detail = {k: payload.get(k) for k in _RESULT_FIELDS}
    await add_audit_log(pc_name, "update_result", f"Ajan guncelleme sonucu: {payload.get('status', '?')}", detail)
    # Güncelleme/rollback sonrası GERÇEKTEN çalışan sürümü sakla (v0.1.3+ ajan gönderir).
    if payload.get("running_version"):
        await execute_query(
            "UPDATE clients SET running_version=$1 WHERE pc_name=$2", (str(payload.get("running_version")), pc_name)
        )
        devicelist.touch([pc_name])
    # Yalnızca GERÇEKTEN kötü durumlar kritik loglanır (bkz. pops/update_notice.py)
    notice = update_notice.describe(payload)
    if update_notice.is_critical(payload):
        _reason = str(payload.get("agent_state") or "") or str(payload.get("status") or "")
        await log_audit_event(
            pc_name,
            "Critical Security",
            f"Ajan guncelleme sorunu: {_reason}",
            actor_id="System/Update",
            event_type="agent.update",
            category="system_maintenance",
            action="update_problem",
            risk_level="critical",
            reason=_reason,
            meta_data=detail,
        )
    await update_tracking.forget(pc_name)
    if notice:
        await notify(notice[0], notice[1], notice[2], str(payload.get("detail") or ""), pc_name)
    await manager.broadcast_to_panels({"type": "update_result", "pc_name": pc_name, **detail})


async def _update_progress(pc_name: str, payload: dict, agent_version: Optional[str]) -> None:
    """Ajanın güncelleme adımı (update_progress, bkz. docs/api.md ve pops/update_tracking.py). Bilinmeyen adım,
    sonucu beklenmeyen cihazın ya da başka bir gönderimin adımı yok sayılır; bağlantı sürer. "rejected" güncellemeyi
    bitirir: ajan sonuç dosyası yazmadan durduğu için sebep "rejected" sonucu olarak kaydedilir."""
    progress = update_tracking.clean_progress(payload)
    if progress is None:
        log.info("tanınmayan güncelleme adımı yok sayıldı",
                 extra={"pc_name": pc_name, "stage": str(payload.get("stage"))[:40]})
        return
    if not update_tracking.accepts(pc_name, progress):
        log.info("beklenmeyen güncelleme adımı yok sayıldı", extra={"pc_name": pc_name, "stage": progress["stage"]})
        return
    if progress["stage"] == "rejected":
        await _store_update_result(pc_name, {
            "status": "rejected",
            "from_version": agent_version[:64] if agent_version and agent_version != "unknown" else None,
            "to_version": progress["to_version"] or update_tracking.pending_version(pc_name),
            "detail": progress["detail"] or "ajan sebep bildirmedi",
        })
        return
    await update_tracking.set_stage(pc_name, progress)


# Ajanın işletim sistemi ailesi (X-Agent-Platform; Linux ajanı gönderir). Başlık yoksa Windows ajanıdır.
PLATFORMS = ("windows", "linux")


def agent_platform(header: Optional[str], payload_value=None) -> str:
    for value in (header, payload_value):
        value = str(value or "").strip().lower()
        if value in PLATFORMS:
            return value
    return "windows"


# WebSocket kapanış kodları (RFC 6455) → panelde ve günlükte okunur sebep
_CLOSE_CODES = {
    1000: "ajan kapattı (normal)",
    1001: "ajan kapattı (servis duruyor ya da yeniden başlıyor)",
    1006: "bağlantı koptu (ağ ya da sunucu yanıtsız; kapanış mesajı gelmedi)",
    1011: "ajan tarafında hata",
    1012: "ajan yeniden başlıyor",
}


def _close_reason(code, reason) -> str:
    text = _CLOSE_CODES.get(code, "kapanış kodu %s" % code)
    reason = str(reason or "").strip()
    return "%s: %s" % (text, reason[:120]) if reason else text


# JSON'da NUL ve eşi olmayan vekil karakter yalnızca \u kaçışıyla gelebilir (ham denetim karakteri JSON'u bozar)
_UNSTORABLE_ESCAPE = re.compile(r"\\u(?:0000|[dD][89a-fA-F][0-9a-fA-F]{2})")
_SURROGATE = re.compile("[\ud800-\udfff]")


def _storable_text(text: str) -> str:
    return _SURROGATE.sub("\ufffd", text.replace("\x00", ""))


def _parse_agent_message(data: str):
    """Ajanın bir çerçevesi -> JSON değeri, içindeki metinler PostgreSQL'e yazılabilir hâlde: NUL (U+0000) silinir, eşi
    olmayan vekil karakter (ör. \\udfff) U+FFFD olur. PostgreSQL ikisini de metinde ve jsonb'de reddeder: böyle bir
    görev çıktısı hiç saklanamaz (ajan onaysız sonucu yeniden gönderip durur), toplu heartbeat yazımında tek bir satır
    bütün cihazların heartbeat'ini düşürürdü (fuzz/fuzz_agent_ws.py). Kaçış yoksa yapı gezilmez."""
    payload = json.loads(data)
    if not _UNSTORABLE_ESCAPE.search(data):
        return payload
    if isinstance(payload, str):
        return _storable_text(payload)
    stack = [payload]   # özyinelemesiz: derin iç içe JSON yığını aşmasın
    while stack:
        item = stack.pop()
        if isinstance(item, dict):
            for key in list(item):
                value = item.pop(key)
                if isinstance(value, str):
                    value = _storable_text(value)
                else:
                    stack.append(value)
                item[_storable_text(key)] = value
        elif isinstance(item, list):
            for i, value in enumerate(item):
                if isinstance(value, str):
                    item[i] = _storable_text(value)
                else:
                    stack.append(value)
    return payload


def _is_reboot_command(script: Optional[str]) -> bool:
    """Komut cihazı yeniden başlatıyor mu (shutdown /r, Restart-Computer)? Düzenli ifade kullanılmaz."""
    text = (script or "")[:20000].lower()
    if "restart-computer" in text:
        return True
    return "shutdown" in text and any(tok in ("/r", "-r") for tok in text.replace('"', " ").split())


async def _settle_running_tasks(pc_name: str, health, agent_version: str) -> None:
    """Ajan yeniden bağlandığında hâlâ "Running" görünen görevler (F05):
      - ajan, görev gönderildikten sonra yeniden başlamışsa: yeniden başlatma komutu ise "Completed (Rebooted)",
        değilse "Interrupted" (işlem ajanla birlikte kesilmiş olabilir);
      - aynı ajan süreci (yalnızca bağlantı koptu) ve ajan sonucu yeni bağlantıdan gönderebiliyorsa (0.1.13+):
        görev "Running" kalır, sonuç gelince kapanır;
      - aksi hâlde "Unknown": ne olduğu bilinmiyor (eskiden yanlışlıkla "tamamlandı" sayılıyordu)."""
    rows = await execute_query(
        "SELECT id, script_path, dispatched_at, agent_started_at FROM tasks "
        "WHERE target_pc = $1 AND status = 'Running'",
        (pc_name,),
        fetch=True,
    )
    if not rows:
        return
    started = agent_health.unix_time(health.get("started_at")) if isinstance(health, dict) else None
    resends = agent_version_mod.at_least(agent_version, (0, 1, 13))
    for r in rows:
        recorded = r.get("agent_started_at")
        dispatched = r.get("dispatched_at")
        if started is not None and recorded is not None:
            # Gönderim anındaki ajan süreci ile şimdiki karşılaştırılır; ajan ve sunucu saatleri karşılaştırılmaz
            # (saat kaymış bir bilgisayarda görev yanlışlıkla "yarıda kaldı" ya da "sürüyor" sayılırdı)
            restarted = abs(started - recorded) > 1.0
        elif started is not None and dispatched is not None:
            restarted = started >= dispatched.timestamp()   # bu sürümden önce gönderilmiş görev
        else:
            restarted = None
        if restarted:
            status = "Completed (Rebooted)" if _is_reboot_command(r.get("script_path")) else "Interrupted"
        elif restarted is False and resends:
            continue
        else:
            status = "Unknown"
        await execute_query("UPDATE tasks SET status = $1 WHERE id = $2 AND status = 'Running'", (status, r["id"]))


async def _reenroll_allowed(pc_name: str) -> bool:
    """Kayıt jetonuyla bu kimliğe anahtar verilebilir mi: cihazın anahtarı yoksa ya da yönetici yeniden kayda izin
    verdiyse. Kesin karar _enroll'daki işlemde (yarışa karşı) yeniden verilir."""
    has_secret = await execute_query("SELECT 1 FROM agent_secrets WHERE pc_name=$1", (pc_name,), fetch=True)
    if not has_secret:
        return True
    row = await execute_query("SELECT allow_reenroll FROM clients WHERE pc_name=$1", (pc_name,), fetch=True)
    return bool(row and row[0].get("allow_reenroll"))


async def _deny_reenroll(websocket: WebSocket, pc_name: str, client_ip: str, agent_version: str, pending_enroll):
    """F2: Zaten anahtarı olan cihaza kayıt jetonuyla yeni anahtar vermek kimlik hırsızlığıdır (saldırgan geçerli bir
    jetonla anahtarı ezip cihazın yerine geçebilir). Kritik denetim kaydı, bildirim ve 4401."""
    await add_audit_log(
        pc_name,
        "enroll_denied",
        "Zaten kayıtlı cihaza enroll token'la yeniden-secret REDDEDİLDİ (olası impersonation)",
        {"ip": client_ip, "token_id": (pending_enroll or {}).get("id"), "agent_version": agent_version},
    )
    await log_audit_event(
        pc_name,
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
    await notify(
        "enroll_denied",
        "critical",
        "Kayıtlı cihazın kimliğini ele geçirme girişimi reddedildi",
        "Kaynak IP: %s" % client_ip,
        pc_name,
    )
    try:
        await websocket.close(code=4401, reason="Cihaz zaten kayitli")
    except Exception:
        pass


async def _has_secret(pc_name: str) -> bool:
    return bool(await execute_query("SELECT 1 FROM agent_secrets WHERE pc_name = $1", (pc_name,), fetch=True))


# Bir dakikada bu kadar mesaj işlenemezse ajan bağlantısı kapatılır (tek hatada kapatılmaz)
_MSG_ERROR_LIMIT = 20


# Kimliksiz ret kayıtları: aynı cihaz ve adres için en fazla 10 dakikada bir denetim satırı (arada kalanlar sayılır)
_REJECT_AUDIT_SECONDS = 600
_reject_seen = {}


async def _audit_rejected(pc_name: str, client_ip: str, agent_version: str) -> None:
    key = (pc_name, client_ip)
    now = time.monotonic()
    last, suppressed = _reject_seen.get(key, (None, 0))
    if last is not None and now - last < _REJECT_AUDIT_SECONDS:
        _reject_seen[key] = (last, suppressed + 1)
        return
    if len(_reject_seen) > 10000:
        _reject_seen.clear()
    _reject_seen[key] = (now, 0)
    await add_audit_log(
        pc_name,
        "auth_reject",
        "Kimliksiz ajan bağlantısı reddedildi",
        {"ip": client_ip, "agent_version": agent_version, "suppressed_since_last": suppressed},
    )


async def _reject_clone(websocket: WebSocket, pc_name: str, client_ip: str, agent_version: str) -> None:
    key = ("clone:" + pc_name, client_ip)
    now = time.monotonic()
    last, suppressed = _reject_seen.get(key, (None, 0))
    if last is None or now - last >= _REJECT_AUDIT_SECONDS:
        _reject_seen[key] = (now, 0)
        await add_audit_log(
            pc_name,
            "clone_rejected",
            "Aynı cihaz kimliği ve anahtarıyla başka bir bilgisayar bağlanmaya çalıştı (kayıttan sonra imaj alınmış "
            "olabilir); bağlı cihaz korundu",
            {"ip": client_ip, "agent_version": agent_version, "suppressed_since_last": suppressed},
        )
        await notify(
            "clone_rejected",
            "high",
            "Klon bilgisayar reddedildi: aynı cihaz kimliği başka bir donanımdan",
            "Ajan kaydedildikten sonra disk imajı alınıp çoğaltılmış olabilir. Klonların kimliği ve anahtarı "
            "silinip yeniden kaydedilmeli (bkz. belgeler: imaj öncesi hazırlık). Kaynak IP: %s" % client_ip,
            pc_name,
        )
    else:
        _reject_seen[key] = (last, suppressed + 1)
    try:
        await websocket.close(code=4409, reason="Bu cihaz kimligi baska bir bilgisayarda bagli")
    except Exception:
        pass


class _EnrollRejected(Exception):
    pass


async def _enroll(pc_name: str, token_id: int):
    """Kayıt: jetonun bir kullanım hakkı, (varsa) yeniden kayıt izni ve cihaz anahtarı TEK işlemde (F02). Jeton
    bu arada tükendiyse, süresi dolduysa ya da izin yoksa hiçbir şey yazılmaz. Dönen: (anahtar, sınıf) ya da
    (None, None)."""
    new_secret = secrets.token_urlsafe(32)
    try:
        async with db.transaction() as conn:
            # Cihaz kimliği üzerinde kilit: yeni bir cihazın henüz anahtar satırı yoktur, FOR UPDATE bir şey kilitlemez.
            # Aynı yeni kimlikle eşzamanlı iki kayıttan ikincisi burada bekler, sonra anahtarı görür ve yeniden kayıt
            # kuralına takılır; ilkinin verdiği anahtarı ezemez.
            await conn.execute("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", "enroll:" + pc_name)
            has_secret = await conn.fetchval("SELECT 1 FROM agent_secrets WHERE pc_name=$1 FOR UPDATE", pc_name)
            if has_secret:
                allowed = await conn.fetchval(
                    "UPDATE clients SET allow_reenroll=FALSE WHERE pc_name=$1 AND allow_reenroll RETURNING 1", pc_name
                )
                if not allowed:
                    raise _EnrollRejected()
            token = await conn.fetchrow(
                "UPDATE enroll_tokens SET use_count = use_count + 1, is_used = (use_count + 1 >= max_uses), "
                "used_by=$1, used_at=NOW() "
                "WHERE id=$2 AND NOT is_used AND expires_at > NOW() AND use_count < max_uses RETURNING lab_name",
                pc_name,
                token_id,
            )
            if token is None:
                raise _EnrollRejected()
            await conn.execute(
                "INSERT INTO agent_secrets (pc_name, secret_hash) VALUES ($1, $2) "
                "ON CONFLICT (pc_name) DO UPDATE SET secret_hash=$2, rotated_at=NOW()",
                pc_name,
                _hash_secret(new_secret),
            )
    except _EnrollRejected:
        return None, None
    return new_secret, token["lab_name"]


@router.websocket("/ws/agent/{pc_name}")
async def websocket_agent(websocket: WebSocket, pc_name: str):
    await websocket.accept()
    # uvicorn, güvenilen ters vekilin (127.0.0.1) X-Forwarded-For başlığını zaten çözer; başlığın ilk öğesi istemcinin
    # kendisinin yazabildiği değerdir, kullanılmaz
    client_ip = websocket.client.host if websocket.client else "Bilinmiyor"
    active_hwid = pc_name
    agent_version = websocket.headers.get("X-Agent-Version", "unknown")
    platform_header = websocket.headers.get("X-Agent-Platform")
    platform = agent_platform(platform_header)
    agent_features = agent_version_mod.features(websocket.headers.get("X-Agent-Features"))
    connected_at = time.monotonic()
    close_reason = "bilinmiyor"

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
    if auth_method == "none" and (await enforce_agent_auth_enabled() or await _has_secret(active_hwid)):
        # Kimliksiz bağlantı reddedilir: zorlama açıksa her cihaz için, kapalıysa da ANAHTARI OLAN bir cihaz adına
        # (anahtarı olan cihaz hiçbir zaman anahtarsız bağlanmaz; bu, kanalını ele geçirme denemesidir). Denetim
        # kaydı cihaz ve adres başına seyreltilir (kimliksiz biri zincire sınırsız satır yazdıramasın).
        await _audit_rejected(active_hwid, client_ip, agent_version)
        await websocket.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")
        return

    # Kayıt jetonuyla, anahtarı olan bir cihaz adına bağlanılıyorsa (yeniden kayıt izni yoksa) HİÇBİR ŞEY
    # değiştirilmeden reddedilir: bağlantı kaydı, cihaz ve görev durumu olduğu gibi kalır (F03).
    if auth_method == "enroll" and not await _reenroll_allowed(active_hwid):
        await _deny_reenroll(websocket, active_hwid, client_ip, agent_version, pending_enroll)
        return

    # Bu bağlantı cihazın kalıcı anahtarıyla mı açıldı (aynı bağlantıda enroll ile alınan anahtar sayılmaz;
    # ajan da o bağlantıda set_bypass_secret'ı kabul etmez)
    connected_with_secret = auth_method == "secret"
    # Bağlantı, ilk mesaj işlenip yetki ve kayıt tamamlanınca kaydedilir (manager.active_agents); öncesinde
    # komut alamaz ve başka bir bağlantının yerini almaz.

    async def handle_routine_payload(pld):
        current_time = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
        current_hostname = pld.get("hostname", active_hwid)
        if pld.get("type") == "result":
            # Ajan yalnızca kendisine atanmış görevin sonucunu yazabilir. İptal edilmiş görevin durumu değişmez (çıktı
            # saklanır); çıkış kodu (0.1.13+) sıfır değilse görev Failed olur.
            exit_code = pld.get("exit_code")
            exit_code = exit_code if isinstance(exit_code, int) and not isinstance(exit_code, bool) else None
            task_id = pld.get("task_id")
            if not isinstance(task_id, int) or isinstance(task_id, bool):
                return
            # Ajanın ret sonucu ("[REDDEDİLDİ] …", eski ajanlarda çıkış kodsuz, yenilerde -5; winget yoksa -7) görevi
            # "Completed" ya da "Failed" yapmasın: ayrıca gelen capability_denied iletisi kaybolsa ya da sunucu eskiyse
            # de görev "Denied" olur.
            output = pld.get("output")
            refused = (exit_code in REFUSED_EXIT_CODES and isinstance(output, str)
                       and output.startswith(REFUSED_PREFIX))
            if refused and exit_code is None:
                exit_code = -5
            # winget görevinde "zaten kurulu" ve "kuruldu, yeniden başlatma bekliyor" kodları da başarıdır
            stored = await execute_query(
                "UPDATE tasks SET output = $1, exit_code = $4, status = CASE "
                "WHEN status IN ('Running', 'Unknown', 'Interrupted', 'Timed Out') THEN "
                "(CASE WHEN $5 THEN 'Denied' WHEN $4::int IS NULL OR $4::int = 0 THEN 'Completed' "
                "WHEN kind = $6 AND $4::int = ANY($7::int[]) THEN 'Completed' ELSE 'Failed' END) "
                "ELSE status END "
                "WHERE id = $2 AND target_pc = $3 "
                "AND status IN ('Running', 'Unknown', 'Interrupted', 'Timed Out', 'Cancelled') RETURNING id",
                (output, task_id, active_hwid, exit_code, refused, winget.KIND, list(winget.OK_EXIT_CODES)),
                fetch=True,
            )
            # Sonuç veritabanına yazıldı: 0.1.14+ ajan sonucu bu onaya kadar saklar ve yeniden gönderir (aynı sonucun
            # ikinci kez gelmesi zararsızdır, yalnızca çıktı yeniden yazılır). Bu cihaza ait olmayan ya da artık var
            # olmayan görevin sonucu da onaylanır (ajan saklamayı bıraksın) ama panele yayılmaz: başka cihazın görev
            # kimliğiyle gelen çıktı (ör. kopyalanmış kurulumun eski sonuçları) o görevin çıktısı gibi görünmesin.
            await manager.send_command({"action": "result_ack", "task_id": task_id}, active_hwid)
            if not stored:
                log.info("görev sonucu eşleşmedi, yayılmadı", extra={"pc_name": active_hwid, "task_id": task_id})
                return
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
            # Toplu yazılır (bkz. pops/heartbeats.py). agent_health (0.1.12+): her heartbeat'te üzerine yazılır;
            # bildirmeyen ajanda NULL kalır
            heartbeats.record(
                active_hwid,
                current_time,
                pld.get("status"),
                pld.get("active_window", "-"),
                current_hostname,
                client_ip,
                agent_health.clean(pld.get("agent_health")),
            )
            scope = metrics.query_scope.set([0])
            try:
                if auth_method == "secret" and isinstance(pld.get("quarantined"), bool):
                    health = pld.get("agent_health") if isinstance(pld.get("agent_health"), dict) else {}
                    isolated = health.get("network_isolated")
                    await reconcile_quarantine(
                        active_hwid, pld["quarantined"], isolated if isinstance(isolated, bool) else None,
                        str(health.get("isolation_error") or "")[:200],
                    )
            finally:
                metrics.count("heartbeat_queries", metrics.query_scope.get()[0])
                metrics.query_scope.reset(scope)

    try:
        data = await websocket.receive_text()
        payload = _parse_agent_message(data)
        now = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")

        if "dna_payload" in payload:
            dna_payload = clean_dna_payload(payload.get("dna_payload"))
            keep_dna = False
            if auth_method == "secret":
                # Anahtarla doğrulanan bağlantının kimliği değişmez (F04): donanım bilgisi başka bir cihaza
                # benzese de anahtar ve kimlik taşınmaz; uyuşmazlık yönetici için kaydedilir.
                mismatch = await check_known_device(active_hwid, dna_payload, client_ip)
                if mismatch and manager.active_agents.get(active_hwid) is not None:
                    # Aynı kimlik ve anahtar, başka bir donanımdan, asıl cihaz bağlıyken: kayıttan SONRA alınmış bir
                    # imajın klonu. Bağlı cihazın yerini almaz, yönetici uyarılır (yoksa 40 klon tek cihaz görünür).
                    await _reject_clone(websocket, active_hwid, client_ip, agent_version)
                    return
                # Uyuşmayan donanımın bilgisi kayıtlı donanımın üzerine yazılmaz (kayıt asıl cihazı göstermeye devam
                # eder; bir sonraki klon da yine uyuşmaz)
                keep_dna = mismatch
            else:
                verified_hwid = await reconcile_device(active_hwid, dna_payload, client_ip, websocket)
                if verified_hwid != active_hwid and auth_method == "none" and await _has_secret(verified_hwid):
                    # Kimliksiz bağlantı, donanım benzerliğiyle anahtarı olan bir cihazın kimliğine taşınamaz
                    await _audit_rejected(verified_hwid, client_ip, agent_version)
                    await websocket.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")
                    return
                if verified_hwid != active_hwid:
                    if auth_method == "enroll" and not await _reenroll_allowed(verified_hwid):
                        await _deny_reenroll(websocket, verified_hwid, client_ip, agent_version, pending_enroll)
                        return
                    # Kimliği çözümlenen cihaza, varsa eski kimliğin anahtarı taşınır (hedefte yoksa)
                    await execute_query(
                        "UPDATE agent_secrets SET pc_name=$1 WHERE pc_name=$2 "
                        "AND NOT EXISTS (SELECT 1 FROM agent_secrets WHERE pc_name=$1)",
                        (verified_hwid, active_hwid),
                    )
                    await bypass.move(active_hwid, verified_hwid)
                    devicelist.touch([active_hwid])
                    active_hwid = verified_hwid

            # Kayıt jetonu: jetonun tüketimi, yeniden kayıt izninin tüketimi ve anahtar tek işlemde (F02). Jeton bu
            # sırada tükendiyse ya da süresi dolduysa hiçbir şey yazılmadan reddedilir.
            new_secret, enroll_lab = None, None
            if auth_method == "enroll":
                new_secret, enroll_lab = await _enroll(active_hwid, pending_enroll["id"])
                if new_secret is None:
                    await add_audit_log(
                        active_hwid,
                        "enroll_denied",
                        "Kayıt reddedildi: jeton tükendi, süresi doldu ya da cihazın yeniden kayıt izni yok",
                        {"ip": client_ip, "token_id": pending_enroll["id"], "agent_version": agent_version},
                    )
                    try:
                        await websocket.close(code=4401, reason="Kayit jetonu kullanilamaz")
                    except Exception:
                        pass
                    return

            hw = dna_payload.get("hardware", {})
            caps = dna_payload.get("capabilities", {})
            real_hostname = payload.get("hostname")
            real_hostname = real_hostname if isinstance(real_hostname, str) else active_hwid
            platform = agent_platform(platform_header, payload.get("platform"))

            # Bağlantı koptuğunda "çalışıyor" kalan görevlerin akıbeti (F05)
            await _settle_running_tasks(active_hwid, payload.get("agent_health"), agent_version)
            # Oto-kayıt: bitiş tarihine kadar İLK kez bağlanan cihaz o sınıfa (yalnız yeni satırda; mevcut
            # cihazın sınıfı değişmez). Eski biçimdeki (tarihsiz) kayıt etkisizdir.
            new_lab = "Atanmamis_Cihazlar"
            ae = await execute_query("SELECT value FROM global_settings WHERE key = 'auto_enroll_lab'", fetch=True)
            if ae:
                try:
                    rule = json.loads(ae[0]["value"] or "")
                    if rule.get("lab") and datetime.date.today().isoformat() <= str(rule.get("until") or ""):
                        new_lab = rule["lab"]
                except (ValueError, AttributeError):
                    pass
            await execute_query(
                """
                INSERT INTO clients (pc_name, hostname, lab_name, last_seen, status, active_window, boot_count,
                    ip_address, dna_uuid, dna_bios, dna_disk, dna_mac, dna_ram, cap_ram_readable, platform)
                VALUES ($1, $2, $11, $3, 'Online', '-', 1, $4, $5, $6, $7, $8, $9, $10, $13)
                ON CONFLICT (pc_name) DO UPDATE SET status='Online', last_seen=$3, ip_address=$4,
                    boot_count=clients.boot_count + 1, hostname=$2, platform=$13,
                    dna_uuid=CASE WHEN $12 THEN clients.dna_uuid ELSE $5 END,
                    dna_bios=CASE WHEN $12 THEN clients.dna_bios ELSE $6 END,
                    dna_disk=CASE WHEN $12 THEN clients.dna_disk ELSE $7 END,
                    dna_mac=CASE WHEN $12 THEN clients.dna_mac ELSE $8 END,
                    dna_ram=CASE WHEN $12 THEN clients.dna_ram ELSE $9 END,
                    cap_ram_readable=$10
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
                    new_lab,
                    keep_dna,
                    platform,
                ),
            )

            # Sürüm ve ajanın duyurduğu özellikler (X-Agent-Features, ör. winget) her bağlantıda yazılır: kuyruk winget
            # görevini yalnızca o özelliği duyuran ajana gönderir. Başlığı göndermeyen ajanda boş liste.
            await execute_query(
                "INSERT INTO agent_versions (pc_name, version, last_update, features) "
                "VALUES ($1, $2, $3, $4) ON CONFLICT (pc_name) DO UPDATE "
                "SET version=$2, last_update=$3, features=$4",
                (active_hwid, agent_version, now, agent_features),
            )

            if new_secret:
                if enroll_lab:
                    await execute_query("UPDATE clients SET lab_name=$1 WHERE pc_name=$2", (enroll_lab, active_hwid))
                await add_audit_log(
                    active_hwid, "enroll", "Ajan enroll token ile kaydoldu", {"lab": enroll_lab, "ip": client_ip}
                )
                try:
                    await websocket.send_text(json.dumps({"action": "set_secret", "secret": new_secret}))
                except Exception:
                    pass
                pending_enroll = None
                auth_method = "secret"

            manager.active_agents[active_hwid] = websocket
            await _send_server_info(websocket)

            # Cihaz başına çevrimdışı bypass anahtarı (0.1.12+, bkz. pops/bypass.py). Ajan parmak iziyle onaylar
            # (bypass_secret_ack); onaylanana kadar her bağlanışta aynı anahtar yeniden gönderilir.
            if connected_with_secret and bypass.supports_device_key(agent_version):
                bypass_key = await bypass.key_to_send(active_hwid)
                if bypass_key:
                    try:
                        await websocket.send_text(json.dumps({"action": "set_bypass_secret", "secret": bypass_key}))
                    except Exception:
                        pass

            # Cihaz satırı (durum, sürüm, sınıf, bypass anahtarı) değişti: panel listesi bir sonraki turda okur
            devicelist.touch([active_hwid])
            hw_exists = await execute_query(
                "SELECT cpu FROM hw_inventory WHERE pc_name = $1", (active_hwid,), fetch=True
            )
            if not hw_exists or hw_exists[0]["cpu"] == "-":
                await manager.send_command({"action": "get_hardware"}, active_hwid)
            await process_queue()
            # Sınıfın süren sınavı (yeniden bağlanan ya da sınav sürerken sınıfa taşınmış bilgisayar); sınav bu
            # bilgisayarda bitmeden kapandıysa enabled:false (bkz. pops/exams.py, docs/protocol/README.md sırası)
            await _sync_exam(active_hwid)
        else:
            if auth_method == "enroll":
                # Kayıt, donanım bilgisini taşıyan ilk mesajla yapılır
                try:
                    await websocket.close(code=4401, reason="Kayit icin donanim bilgisi gerekli")
                except Exception:
                    pass
                return
            await execute_query(
                "UPDATE agent_versions SET features = $2 WHERE pc_name = $1", (active_hwid, agent_features)
            )
            manager.active_agents[active_hwid] = websocket
            await _send_server_info(websocket)
            await _sync_exam(active_hwid)

        await handle_routine_payload(payload)

        msg_errors = []
        while True:
            data = await websocket.receive_text()
            mtype = None
            try:
                payload = _parse_agent_message(data)
                if not isinstance(payload, dict):
                    raise ValueError("mesaj JSON nesnesi değil")
                mtype = str(payload.get("type") or "")[:40]
                if payload.get("type") == "thumbnail":
                    # Görüntü yalnızca bu bağlantının cihazına ait olabilir (F16): gövdedeki kimlik yetki taşımaz
                    hwid = active_hwid
                    payload["hw_id"] = active_hwid
                    if hwid in manager.pending_thumbnails:
                        for fut in manager.pending_thumbnails[hwid]:
                            if not fut.done():
                                fut.set_result(payload.get("image", ""))
                        manager.pending_thumbnails[hwid] = []
                    # F1 kalıntısı: ekran görüntüsü yalnızca admin panellerine (viewer'a SIZMAZ).
                    await manager.broadcast_to_admin_panels(payload)
                    continue
                if payload.get("type") == "vision_rejected":
                    # Cihaz bağlantıdan (önizlemedeki gibi): gövdedeki hw_id yetki taşımaz. Panel bu mesajda cihazı
                    # yoksa ya da kendi açık oturumunun cihazıysa uzaktan bağlantıyı kapatır; başka bir ajan böylece
                    # yöneticinin oturumunu kapattırabiliyordu (fuzz/fuzz_agent_ws.py)
                    await manager.broadcast_to_panels(
                        {"type": "vision_rejected", "session_id": payload.get("session_id"), "hw_id": active_hwid}
                    )
                    continue
                if payload.get("type") == "update_result":
                    # Ajanın güncelleme sonucu (POpsUpdater update-result.json'ından). 0.1.14+ ajan result_id gönderir
                    # ve sonucu onay (update_result_ack) gelene kadar saklayıp yeniden gönderir: aynı sonuç ikinci kez
                    # kaydedilmez, yalnızca onaylanır. Onay kayıt yazıldıktan SONRA gider (S20).
                    result_id = update_tracking.clean_result_id(payload.get("result_id"))
                    if result_id and await update_tracking.seen(active_hwid, result_id):
                        await _ack_update_result(active_hwid, result_id)
                        continue
                    await _store_update_result(active_hwid, payload)
                    # Onay en sonda: arada sunucu çökerse ajan sonucu yeniden gönderir ve kayıt/bildirim tekrarlanır (en
                    # az bir kez). Önce onaylanırsa çökmede bildirim hiç oluşmazdı.
                    if result_id:
                        await update_tracking.remember(active_hwid, result_id)
                        await _ack_update_result(active_hwid, result_id)
                    continue
                if payload.get("type") == "update_progress":
                    await _update_progress(active_hwid, payload, agent_version)
                    continue
                if payload.get("type") == "file_result":
                    # Dosya aktarımının sonucu (gönderilen dosya yazıldı/reddedildi; istenen dosya bulunamadı...)
                    await file_transfer.handle_result(active_hwid, payload)
                    continue
                if payload.get("type") == "exam_state":
                    # Ajanın sınav modu durumu: sınav yokken "sınavda" ise kapatma yeniden gider, sınav sürerken
                    # "sınavda değil" ise erken çıkış bildirilir (pops/exams.py)
                    await exams.on_agent_state(active_hwid, payload)
                    continue
                if payload.get("type") == "capabilities":
                    # Ajan güncel yetenek durumunu bildirir (bağlantıda + her değişimde). Sakla + panele yay.
                    t = payload.get("terminal_enabled")
                    v = payload.get("vision_enabled")
                    # files_enabled: dosya aktarımı (bilmeyen eski ajanda alan yok -> NULL, sunucu dosya göndermez)
                    f = payload.get("files_enabled")
                    # server_ca (0.1.10+): custom = cihazdaki kurum CA'sı, system = Windows kök deposu; yoksa eskisi
                    # kalır
                    sc = payload.get("server_ca")
                    sc = sc if sc in ("custom", "system") else None
                    await execute_query(
                        "UPDATE clients SET cap_terminal_enabled=$1, cap_vision_enabled=$2, "
                        "cap_server_ca=COALESCE($4, cap_server_ca), cap_files_enabled=$5 WHERE pc_name=$3",
                        (bool(t) if t is not None else None, bool(v) if v is not None else None, active_hwid, sc,
                         bool(f) if f is not None else None),
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
                    devicelist.touch([active_hwid])
                    await manager.broadcast_to_panels(
                        {"type": "capabilities", "pc_name": active_hwid, "terminal_enabled": t, "vision_enabled": v,
                         "files_enabled": f}
                    )
                    continue
                if payload.get("type") == "bypass_secret_ack":
                    fp = str(payload.get("fingerprint") or "")[:64]
                    if connected_with_secret and await bypass.confirm(active_hwid, fp):
                        devicelist.touch([active_hwid])
                        await add_audit_log(
                            active_hwid, "bypass_key", "Cihaza özel bypass anahtarı ajana ulaştı", {"fingerprint": fp}
                        )
                    else:
                        await add_audit_log(
                            active_hwid,
                            "bypass_key_mismatch",
                            "Bypass anahtarı onayı saklanan anahtarla eşleşmedi",
                            {"fingerprint": fp},
                        )
                    continue
                if payload.get("type") == "capability_denied":
                    # Ajan, kapalı bir yetenek için gelen isteği reddettiğini bildirir. Denetime yaz + panele yay.
                    # reason (0.1.12+): ör. not_enrolled = cihaz anahtarı olmadığı için Vision tüneli açılmadı
                    _md = {k: payload.get(k) for k in ("capability", "action", "task_id", "transfer_id", "reason")}
                    denied_task = payload.get("task_id")
                    if isinstance(denied_task, int) and not isinstance(denied_task, bool):
                        # Komut çalıştırılmadı. Ajanın ret sonucu (0.1.13 ve öncesi çıkış kodsuz) görevi "Completed"
                        # yapmış olabilir; görev "Denied" olur.
                        await execute_query(
                            "UPDATE tasks SET status = 'Denied', exit_code = COALESCE(exit_code, -5) "
                            "WHERE id = $1 AND target_pc = $2 "
                            "AND status IN ('Running', 'Completed', 'Failed', 'Unknown', 'Interrupted', 'Timed Out')",
                            (denied_task, active_hwid),
                        )
                    if payload.get("capability") == "files":
                        # Dosya aktarımı bilgisayarda kapalı: yetenek kapalı yazılır, aktarım "rejected"
                        await file_transfer.handle_denied(active_hwid, payload)
                    elif (payload.get("capability") == "quarantine" and payload.get("reason") == "not_supported"
                            and auth_method == "secret" and platform == "linux"):
                        # Bu ajanda karantina yok (Linux ajanı): kilit isteği "bekleyen" kalmasın ve panel cihazı
                        # kilitli göstermesin; yönetici reddi bildirimden görür. Yalnızca anahtarla doğrulanmış Linux
                        # ajanından: başka bir bağlantı bu iletiyle karantina durumunu silemesin.
                        await execute_query(
                            "UPDATE clients SET is_quarantined = FALSE, pending_quarantine_action = NULL, "
                            "pending_quarantine_reason = NULL WHERE pc_name = $1",
                            (active_hwid,),
                        )
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
                    await notify(
                        "capability_denied",
                        "medium",
                        "Vision tüneli açılmadı: cihaz kayıtlı değil (anahtarı yok)"
                        if payload.get("reason") == "not_enrolled"
                        else "Ajan bu işlemi desteklemiyor: %s" % (payload.get("capability") or "?")
                        if payload.get("reason") == "not_supported"
                        else "Kapalı yetenek istendi, ajan reddetti: %s" % (payload.get("capability") or "?"),
                        str(payload.get("action") or ""),
                        active_hwid,
                    )
                    if payload.get("capability") == "exam":
                        await exams.on_denied(active_hwid)
                    await manager.broadcast_to_panels({"type": "capability_denied", "pc_name": active_hwid, **_md})
                    continue
                await handle_routine_payload(payload)
            except WebSocketDisconnect:
                raise
            except Exception as e:
                if WebSocketState.DISCONNECTED in (websocket.application_state, websocket.client_state):
                    raise
                # Tek bir bozuk ya da işlenemeyen mesaj (ör. anlık DB hatası) bağlantıyı düşürmez (B9). Dakikada
                # _MSG_ERROR_LIMIT hatayı aşan ajan kapatılır ki bozuk bir istemci döngüye girmesin.
                now_m = time.monotonic()
                msg_errors = [t for t in msg_errors if now_m - t < 60.0] + [now_m]
                log.warning(
                    "ajan mesajı işlenemedi",
                    extra={"pc_name": active_hwid, "msg_type": mtype, "error": repr(e)[:300]},
                )
                if len(msg_errors) >= _MSG_ERROR_LIMIT:
                    raise
    except WebSocketDisconnect as e:
        close_reason = _close_reason(e.code, e.reason)
    except Exception as e:
        if websocket.application_state == WebSocketState.DISCONNECTED:
            # Soketi sunucu kapattı (ör. cihaz silindi); bekleyen receive bu yüzden hata verdi, sorun değil
            close_reason = "sunucu kapattı"
        else:
            # Sürekli işlenemeyen mesajlar ya da el sıkışmada hata: soket kapansın ki cihaz yanlışlıkla Online
            # görünmesin
            close_reason = "sunucu hatası: %s" % type(e).__name__
            log.warning("ajan bağlantısı hatayla kapandı", extra={"pc_name": active_hwid, "error": repr(e)[:300]})
        try:
            await websocket.close(code=1011)
        except Exception:
            pass
    finally:
        # Yeniden bağlanan ajanın yeni soketi kayıtlıysa ona dokunulmaz (o durumda bu eski bağlantının kapanması
        # cihazın kopması değildir)
        if manager.disconnect_agent(active_hwid, websocket):
            heartbeats.discard(active_hwid)
            await execute_query(
                "UPDATE clients SET status = 'Offline', last_disconnect_at = NOW(), last_disconnect_reason = $2 "
                "WHERE pc_name = $1",
                (active_hwid, close_reason[:200]),
            )
            devicelist.touch([active_hwid])
            log.info(
                "ajan bağlantısı kapandı",
                extra={
                    "pc_name": active_hwid,
                    "reason": close_reason,
                    "seconds": round(time.monotonic() - connected_at),
                    "agent_version": agent_version,
                },
            )


@router.post("/api/inventory/{pc_name}")
async def update_inventory(pc_name: str, data: HwInventoryInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    await bind_agent(agent_id, pc_name)  # başka cihaz adına envanter yazılamaz
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
    await bind_agent(agent_id, pc_name)  # başka cihaz adına log yazılamaz
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
    # Ajanın kendi karantina durumunu değiştiren olaylar panel durumunu da günceller; YALNIZ anahtarı
    # doğrulanmış ajandan (kimliksiz istemci başka cihazın karantina bayrağını değiştiremesin).
    if agent_id is not None and data.event_type in ("agent.auto_quarantine", "agent.offline_bypass"):
        quarantined = data.event_type == "agent.auto_quarantine"
        await execute_query("UPDATE clients SET is_quarantined = $1 WHERE pc_name = $2", (quarantined, pc_name))
        devicelist.touch([pc_name])
        await add_audit_log(
            pc_name,
            "auto_quarantine" if quarantined else "offline_bypass",
            (data.message or "")[:300],
            {"event_type": data.event_type, "reason": (data.reason or "")[:200]},
        )
        if quarantined:
            await notify(
                "auto_quarantine",
                "high",
                "Cihaz kural ihlali eşiğinde kendini karantinaya aldı",
                (data.reason or "")[:300],
                pc_name,
            )
        else:
            await notify("offline_bypass", "medium", "Çevrimdışı bypass kodu kullanıldı, karantina kalktı", "", pc_name)
    elif agent_id is not None and data.event_type == "agent.unlock_failed":
        # Karantina kaldırılmak istendi ama ajan ağ yalıtımını kaldıramadı: kilit sürüyor. Panel bunu göstersin;
        # kilit açma "bekleyen" kalır ve ajan heartbeat'te kilitli bildirdikçe (5 dk'da bir) yeniden denenir.
        await execute_query(
            "UPDATE clients SET is_quarantined = TRUE, pending_quarantine_action = 'unlock' WHERE pc_name = $1",
            (pc_name,),
        )
        devicelist.touch([pc_name])
        await notify(
            "unlock_failed", "high", "Karantina kaldırılamadı, ağ yalıtımı sürüyor", (data.reason or "")[:300], pc_name
        )
    return {"status": "success"}


def _clean_dns_domains(raw: dict) -> dict:
    """kategori -> tam alan adları. Küçük harf, baştaki '*.' / '.' ve sondaki '.' atılır, http(s):// ve yol
    temizlenir, tekrarlar birleşir. Kategori başına en fazla 5000 alan adı. Boşluk, denetim ya da biçim karakteri
    (sekme, NUL, U+202E...) içeren ad ve denetim karakterli kategori atılır: ajanda hiçbir zaman eşleşmez, NUL'u
    PostgreSQL saklayamaz (fuzz/fuzz_request_models.py)."""
    out = {}
    for cat, domains in (raw or {}).items():
        cat = str(cat).strip()[:60]
        if not cat or not cat.isprintable() or not isinstance(domains, list):
            continue
        seen = []
        for d in domains[:5000]:
            d = str(d).strip().lower()
            d = d.split("://", 1)[-1].split("/", 1)[0].lstrip("*.").rstrip(".")
            if d and " " not in d and d.isprintable() and len(d) <= 253 and d not in seen:
                seen.append(d)
        out[cat] = seen
    return out


@router.post("/api/agent_policies", deprecated=True)
async def save_policies(data: AgentPoliciesInput, auth: dict = Depends(require_admin)):
    if data.dns_domains is None:
        # Alan adı listesini göndermeyen istemci onu silmesin: kayıtlı listeyi koru
        row = await execute_query("SELECT value FROM global_settings WHERE key = 'agent_policies'", fetch=True)
        dns_domains = (json.loads(row[0]["value"]).get("dns_domains") or {}) if row else {}
    else:
        dns_domains = _clean_dns_domains(data.dns_domains)
    val = json.dumps(
        {
            "fair_use_text": data.fair_use_text,
            "dns_categories": data.dns_categories,
            "auto_quarantine": data.auto_quarantine,
            "quarantine_threshold": data.quarantine_threshold,
            "dns_domains": dns_domains,
        },
        ensure_ascii=False,
    )
    await execute_query(
        "INSERT INTO global_settings (key, value) "
        "VALUES ('agent_policies', $1) ON CONFLICT (key) DO UPDATE "
        "SET value = $1",
        (val,),
    )
    # Kim, ne zaman: panel "Son değişiklik" satırını buradan okur (GET /api/agent_policies/meta); politika
    # değişikliği hash-zincirli denetim kaydına da yazılır.
    who = auth.get("sub")
    meta = json.dumps({"updated_by": who, "updated_at": datetime.datetime.now(datetime.timezone.utc).isoformat()})
    await execute_query(
        "INSERT INTO global_settings (key, value) VALUES ('agent_policies_meta', $1) "
        "ON CONFLICT (key) DO UPDATE SET value = $1",
        (meta,),
    )
    await add_audit_log(
        "*", "policy_update", "Ajan politikası değiştirildi: %s" % who,
        {"admin": who, "dns_categories": data.dns_categories, "auto_quarantine": data.auto_quarantine},
    )
    return {"status": "success"}


@router.get("/api/agent_policies/meta")
async def policies_meta(auth: dict = Depends(require_auth)):
    """Politikayı en son kimin, ne zaman değiştirdiği (panel için; ajanlar okumaz)."""
    row = await execute_query("SELECT value FROM global_settings WHERE key = 'agent_policies_meta'", fetch=True)
    try:
        meta = json.loads(row[0]["value"]) if row else {}
    except (TypeError, ValueError):
        meta = {}
    return {"updated_by": meta.get("updated_by"), "updated_at": meta.get("updated_at")}


@router.get("/api/agent_policies")
async def get_policies(request: Request):
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
    # Modüller: anahtarını gönderen ajana (X-Agent-Id + X-Agent-Secret) kendi laboratuvarının ayarı ve modül listesi
    # gider. Kimliksiz istekte (bugünkü ajanlar) kurum geneli ayar geçerlidir; laboratuvar istisnaları DNS politikası
    # için ajan bu başlıkları gönderince işler. DNS politikası kapalıysa liste boş gider (ajan izlemez); karantina
    # modülü kapalıysa ajan eşikte kendini karantinaya almaz.
    hwid = request.headers.get("X-Agent-Id")
    secret = request.headers.get("X-Agent-Secret")
    lab = None
    if hwid and secret and await verify_agent_secret(hwid, secret):
        lab = await modules.lab_of(hwid)
        pol["modules"] = {m.id: await modules.enabled(m.id, lab) for m in modules.MODULES}
    if not await modules.enabled("dns_policy", lab):
        pol["dns_domains"] = {}
        pol["dns_categories"] = []
        pol["auto_quarantine"] = False
    elif not await modules.enabled("quarantine", lab):
        pol["auto_quarantine"] = False
    return pol


@router.post("/api/policy_alert")
async def add_policy_alert(data: PolicyAlertInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    await bind_agent(agent_id, data.hw_id)  # başka cihaz adına ihlal uyarısı yazılamaz
    if not await modules.enabled("dns_policy", await modules.lab_of(data.hw_id)):
        # DNS politikası bu laboratuvarda kapalı (eski ajan listeyi kurum ayarına göre almış olabilir): kayıt ve
        # bildirim yok. Hata dönülmez, ajan yeniden denemesin.
        return {"status": "ignored", "reason": "module_disabled"}
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
    # Bildirim yalnız anahtarı doğrulanmış ajandan: enforce kapalıyken kimliksiz istemci de bu uca
    # yazabilir (accept-both) ve sahte ihlallerle bildirim yağdırmamalı. Başlık kategori bazlı: aynı
    # cihaz + kategori 10 dakikada bir bildirir, alan adı değiştirerek süzgeç aşılamaz.
    if agent_id is not None:
        await notify(
            "policy_alert", "high", "Kural ihlali: %s" % data.category, "Alan adı: %s" % data.domain, data.hw_id
        )
    return {"status": "success"}
