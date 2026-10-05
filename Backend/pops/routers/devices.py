"""Cihaz/lab yönetimi, envanter/log okuma ve Wake-on-LAN uçları."""

import datetime
import json
import logging
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException, Request, Response

from pops.config import LOG_TABLE, USE_V2_SCHEMA
from pops import db, devicelist, exams, metrics, modules
from pops.db import execute_query
from pops.models import (
    AutoEnrollInput,
    CreateLabInput,
    DeleteLabInput,
    MovePcInput,
    MovePcsInput,
    RenameDeviceInput,
    RenameLabInput,
    SaveLabLayoutInput,
    SetMainPcInput,
)
from pops.security import require_admin, require_auth
from pops.audit import add_audit_log
from pops.manager import manager
from pops.wol import attempt_p2p_wol, send_wol_packet

log = logging.getLogger("pops.devices")
router = APIRouter()


@router.delete("/api/devices/{pc_name}")
async def delete_device(pc_name: str, auth: dict = Depends(require_admin)):
    # Hepsi tek işlemde (F17): yarıda kalırsa hiçbir şey silinmez. Görev geçmişi kalır; sıradaki ve çalışan
    # görevler kapatılır (yoksa "çalışıyor" görev eşzamanlı görev sınırını sonsuza dek tutardı).
    try:
        async with db.transaction() as conn:
            for table in (
                "clients", "hw_inventory", LOG_TABLE, "agent_versions", "agent_secrets", "agent_bypass_keys",
                "device_software", "device_patch_status", "pending_updates", "update_results",
            ):
                await conn.execute(f"DELETE FROM {table} WHERE pc_name = $1", pc_name)
            running = await conn.fetch(
                "UPDATE tasks SET status = 'Cancelled' WHERE target_pc = $1 "
                "AND status IN ('Pending', 'Paused', 'Running', 'Unknown') RETURNING id",
                pc_name,
            )
    except Exception as e:
        log.error("cihaz silinemedi", extra={"pc_name": pc_name, "error": repr(e)[:300]})
        return {"status": "error", "message": "Cihaz silinemedi; hiçbir kayıt değiştirilmedi. Sunucu günlüğüne bakın."}
    manager.pending_updates.pop(pc_name, None)
    manager.update_stages.pop(pc_name, None)
    await devicelist.sync([pc_name])
    await add_audit_log(pc_name, "device_deleted", "Cihaz silindi: %s" % auth.get("sub"), {"admin": auth.get("sub")})
    # Cihaz çevrimiçiyse çalışan komutu durdurması istenir, sonra bağlantı kapatılır
    agent_ws = manager.active_agents.get(pc_name)
    if agent_ws:
        for row in running:
            await manager.send_command({"action": "cancel_task", "task_id": row["id"]}, pc_name)
    manager.disconnect_agent(pc_name)
    if agent_ws:
        try:
            await agent_ws.close(code=4000, reason="Cihaz silindi")
        except Exception:
            pass
    return {"status": "success", "message": f"{pc_name} silindi."}


@router.post("/api/wake_pc/{pc_name}", deprecated=True)
async def wake_pc(pc_name: str, auth: dict = Depends(require_admin)):
    row = await execute_query(
        "SELECT c.lab_name, h.mac_address FROM clients c "
        "LEFT JOIN hw_inventory h ON c.pc_name = h.pc_name "
        "WHERE c.pc_name = $1",
        (pc_name,),
        fetch=True,
    )
    if row:
        await modules.check("wol", lab=row[0]["lab_name"])
    if not row or not row[0]["mac_address"] or row[0]["mac_address"] == "-":
        return {"status": "error", "message": "MAC adresi bulunamadı."}
    mac = row[0]["mac_address"]
    lab_name = row[0]["lab_name"]
    send_wol_packet(mac)
    if lab_name and lab_name != "Atanmamis_Cihazlar":
        await attempt_p2p_wol(mac, lab_name)
    return {"status": "success", "message": "WOL gönderildi."}


@router.post("/api/wake_lab/{lab_name}", deprecated=True)
async def wake_lab(lab_name: str, auth: dict = Depends(require_admin)):
    await modules.check("wol", lab=lab_name)
    rows = await execute_query(
        "SELECT hw_inventory.mac_address FROM hw_inventory "
        "JOIN clients ON hw_inventory.pc_name = clients.pc_name "
        "WHERE clients.lab_name = $1",
        (lab_name,),
        fetch=True,
    )
    count = 0
    for r in rows or []:
        mac = r["mac_address"]
        if mac and mac != "-":
            send_wol_packet(mac)
            await attempt_p2p_wol(mac, lab_name)
            count += 1
    return {"status": "success", "woken_pcs": count}


@router.post("/api/wake_all", dependencies=[modules.require("wol")], deprecated=True)
async def wake_all(auth: dict = Depends(require_admin)):
    rows = await execute_query(
        "SELECT c.lab_name, h.mac_address FROM hw_inventory h JOIN clients c ON h.pc_name = c.pc_name", fetch=True
    )
    count = 0
    lab_on = {}
    for r in rows or []:
        mac = r["mac_address"]
        lab = r["lab_name"]
        if lab not in lab_on:
            lab_on[lab] = await modules.enabled("wol", lab)   # uyandırma modülü kapalı laboratuvar atlanır
        if not lab_on[lab]:
            continue
        if mac and mac != "-":
            send_wol_packet(mac)
            if lab and lab != "Atanmamis_Cihazlar":
                await attempt_p2p_wol(mac, lab)
            count += 1
    return {"status": "success", "woken_pcs": count}


# Önbellek: tarayıcı saklayabilir ama her seferinde sunucuya sorar (ETag ile 304); paylaşılan önbellek saklamaz
_DEVICES_CACHE = "private, no-cache"


@router.get(
    "/api/devices",
    responses={304: {"description": "Not Modified: If-None-Match matches the current device list version (ETag)"}},
)
async def get_devices(
    request: Request, response: Response, since: Optional[int] = None, auth: dict = Depends(require_auth)
):
    """Cihaz listesi. Parametresiz: bütün cihazlar (dizi; biçim değişmez). Yanıtta zayıf ETag (W/"d<sürüm>") vardır;
    If-None-Match tutarsa 304. ?since=<sürüm>: {"version", "full": false, "changed", "removed", "seen"} ya da günlük
    yetmezse {"version", "full": true, "devices"} (bkz. pops/devicelist.py, docs/api.md)."""
    # Sürüm, veritabanı okunmadan ÖNCE alınır: dönen içerik en az bu sürüm kadar yenidir
    version = devicelist.S.version if devicelist.S.ready else None
    tag = devicelist.etag()
    if tag and devicelist.etag_matches(request.headers.get("if-none-match"), tag):
        metrics.count("device_list_304")
        return Response(status_code=304, headers={"ETag": tag, "Cache-Control": _DEVICES_CACHE})
    response.headers["Cache-Control"] = _DEVICES_CACHE
    if tag:
        response.headers["ETag"] = tag
    if since is not None:
        changes = devicelist.delta(since)
        if changes is not None:
            metrics.count("device_list_delta")
            return changes
    metrics.count("device_list_full")
    rows = await devicelist.fetch_rows()
    if since is None:
        return rows
    return {"version": version, "full": True, "devices": rows}


def _iso(v):
    if v is None:
        return None
    if isinstance(v, (datetime.datetime, datetime.date)):
        return v.isoformat()
    return str(v)


@router.get("/api/devices/{pc_name}/activity")
async def device_activity(pc_name: str, limit: int = 15, auth: dict = Depends(require_auth)):
    """Bir cihazın son işlemleri (görevler ve uzak ekran oturumları), yeniden eskiye. Panelin cihaz ayrıntı
    panelindeki "Son işlemler" listesi: ne, kim, ne zaman, nereden, sonuç ve gerekçe."""
    limit = max(1, min(int(limit), 50))
    tasks = await execute_query(
        "SELECT id, title, script_path, status, exit_code, created_at, created_by, source, reason, client_ip, "
        "dispatched_at, batch_id, kind FROM tasks WHERE target_pc = $1 ORDER BY id DESC LIMIT $2",
        (pc_name, limit),
        fetch=True,
    )
    sessions = await execute_query(
        "SELECT start_time, end_time, admin_name, reason, is_mandatory, status FROM enterprise_audit_logs "
        "WHERE target_pc = $1 ORDER BY start_time DESC NULLS LAST LIMIT $2",
        (pc_name, limit),
        fetch=True,
    )
    items = [
        {
            "kind": "task", "id": r["id"], "title": r["title"], "command": (r["script_path"] or "")[:300],
            "status": r["status"], "exit_code": r["exit_code"], "at": _iso(r["created_at"]),
            "by": r["created_by"], "source": r["source"], "reason": r["reason"], "ip": r["client_ip"],
            "started_at": _iso(r["dispatched_at"]), "batch_id": r["batch_id"],
            "task_kind": r["kind"],   # winget görevi: "winget" (bkz. pops/winget.py), komut: null
        }
        for r in tasks or []
    ] + [
        {
            "kind": "vision", "status": r["status"], "at": _iso(r["start_time"]), "ended_at": _iso(r["end_time"]),
            "by": r["admin_name"], "reason": r["reason"], "mandatory": bool(r["is_mandatory"]),
        }
        for r in sessions or []
    ]
    items.sort(key=lambda i: i["at"] or "", reverse=True)
    return {"items": items[:limit]}


@router.get("/api/inventory")
async def get_all_inventory(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM hw_inventory", fetch=True)
    return rows if rows else []


def _log_day(value: Optional[str], name: str) -> Optional[datetime.date]:
    if not value:
        return None
    try:
        return datetime.date.fromisoformat(value)
    except ValueError:
        raise HTTPException(status_code=422, detail=f"{name} YYYY-AA-GG biçiminde olmalı")


@router.get("/api/logs")
async def get_all_logs(
    limit: int = 1000,
    pc: Optional[str] = None,
    since: Optional[str] = None,
    until: Optional[str] = None,
    auth: dict = Depends(require_auth),
):
    """Olay kayıtları, yeniden eskiye. İsteğe bağlı süzgeçler: pc (cihaz kimliği), since / until (YYYY-AA-GG, iki
    gün de dahil); zaman sütunu sunucunun yerel "YYYY-AA-GG SS:DD:ss" metnidir, metin karşılaştırması sırayı korur."""
    limit = max(1, min(int(limit), 20000))
    start, end = _log_day(since, "since"), _log_day(until, "until")
    where, args = [], []
    if pc:
        args.append(pc)
        where.append(f"pc_name = ${len(args)}")
    if start:
        args.append(start.isoformat())
        where.append(f'"timestamp" >= ${len(args)}')
    if end:
        args.append((end + datetime.timedelta(days=1)).isoformat())
        where.append(f'"timestamp" < ${len(args)}')
    args.append(limit)
    table = "agent_logs_v2" if USE_V2_SCHEMA else "agent_logs"
    sql = f"SELECT * FROM {table}" + (" WHERE " + " AND ".join(where) if where else "")
    sql += f" ORDER BY id DESC LIMIT ${len(args)}"
    rows = await execute_query(sql, tuple(args), fetch=True)
    return rows if rows else []


@router.post("/api/create_lab", deprecated=True)
async def create_lab(data: CreateLabInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO custom_labs (lab_name) VALUES ($1) ON CONFLICT DO NOTHING", (data.lab_name,))
    return {"status": "success"}


@router.get("/api/custom_labs", deprecated=True)
async def get_custom_labs(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT lab_name FROM custom_labs", fetch=True)
    return [row["lab_name"] for row in (rows or [])]


@router.post("/api/rename_lab", deprecated=True)
async def rename_lab(data: RenameLabInput, auth: dict = Depends(require_admin)):
    # Oturma planı (lab_settings) ve görev kayıtları da yeni ada taşınır; hepsi tek işlemde
    async with db.acquire() as conn:
        async with conn.transaction():
            await conn.execute("UPDATE clients SET lab_name = $1 WHERE lab_name = $2", data.new_name, data.old_name)
            await conn.execute("UPDATE custom_labs SET lab_name = $1 WHERE lab_name = $2", data.new_name, data.old_name)
            await conn.execute(
                """UPDATE lab_settings SET lab_name = $1 WHERE lab_name = $2
                                  AND NOT EXISTS (SELECT 1 FROM lab_settings WHERE lab_name = $1)""",
                data.new_name,
                data.old_name,
            )
            await conn.execute("UPDATE tasks SET target_lab = $1 WHERE target_lab = $2", data.new_name, data.old_name)
            # Süren sınav sınıfla birlikte taşınır (yeni adda süren bir sınav yoksa; geçmiş eski adla kalır)
            await conn.execute(
                "UPDATE exam_sessions SET lab_name = $1 WHERE lab_name = $2 AND ended_at IS NULL "
                "AND NOT EXISTS (SELECT 1 FROM exam_sessions WHERE lab_name = $1 AND ended_at IS NULL)",
                data.new_name,
                data.old_name,
            )
    await devicelist.sync()
    return {"status": "success"}


@router.post("/api/rename_device", deprecated=True)
async def rename_device(data: RenameDeviceInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET display_name = $1 WHERE pc_name = $2", (data.display_name, data.pc_name))
    await devicelist.sync([data.pc_name])
    return {"status": "success"}


@router.post("/api/delete_lab", deprecated=True)
async def delete_lab(data: DeleteLabInput, auth: dict = Depends(require_admin)):
    async with db.acquire() as conn:
        async with conn.transaction():
            await conn.execute("DELETE FROM custom_labs WHERE lab_name = $1", data.lab_name)
            await conn.execute("UPDATE clients SET lab_name = 'Atanmamis_Cihazlar' WHERE lab_name = $1", data.lab_name)
            await conn.execute("DELETE FROM lab_settings WHERE lab_name = $1", data.lab_name)
    # Süren sınav biter; bilgisayarları (artık atanmamış) enabled:false alır
    await exams.end(data.lab_name, auth.get("sub"), "lab_deleted")
    await devicelist.sync()
    return {"status": "success"}


@router.post("/api/move_pc", deprecated=True)
async def move_pc(data: MovePcInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", (data.new_lab, data.pc_name))
    await exams.sync_pcs([data.pc_name], data.new_lab)
    await devicelist.sync([data.pc_name])
    return {"status": "success"}


@router.post("/api/move_pcs", deprecated=True)
async def move_pcs(data: MovePcsInput, auth: dict = Depends(require_admin)):
    for pc in data.pc_names:
        await execute_query("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", (data.new_lab, pc))
    # Sınav sürerken sınıfa taşınan bağlı bilgisayar sınavı hemen alır; sınavdaki sınıftan çıkan enabled:false alır
    await exams.sync_pcs(data.pc_names, data.new_lab)
    await devicelist.sync(data.pc_names)
    return {"status": "success"}


async def put_main_pc(lab_name: str, pc_name: str) -> dict:
    await execute_query(
        "INSERT INTO lab_settings (lab_name, main_pc) "
        "VALUES ($1, $2) ON CONFLICT (lab_name) DO UPDATE "
        "SET main_pc=EXCLUDED.main_pc",
        (lab_name, pc_name),
    )
    return {"status": "success", "message": f"{pc_name} ana bilgisayar yapıldı."}


async def clear_main_pc(lab_name: str) -> dict:
    await execute_query("UPDATE lab_settings SET main_pc = NULL WHERE lab_name = $1", (lab_name,))
    return {"status": "success", "message": f"{lab_name} sınıfının ana bilgisayarı kaldırıldı."}


@router.post("/api/set_main_pc", deprecated=True)
async def set_main_pc(data: SetMainPcInput, auth: dict = Depends(require_admin)):
    """Aynı bilgisayar yeniden seçilirse ana bilgisayar kaldırılır (aç/kapa). REST karşılığı aç/kapa yapmaz:
    PUT ve DELETE /api/v1/labs/{lab_name}/main-pc (bkz. routers/rest.py)."""
    current = await execute_query("SELECT main_pc FROM lab_settings WHERE lab_name = $1", (data.lab_name,), fetch=True)
    if current and current[0]["main_pc"] == data.pc_name:
        await clear_main_pc(data.lab_name)
        return {"status": "success", "message": f"{data.pc_name} ana bilgisayar yetkisi kaldırıldı."}
    return await put_main_pc(data.lab_name, data.pc_name)


@router.post("/api/save_lab_layout", deprecated=True)
async def save_lab_layout(data: SaveLabLayoutInput, auth: dict = Depends(require_admin)):
    await execute_query(
        "INSERT INTO lab_settings (lab_name, layout_json) "
        "VALUES ($1, $2) ON CONFLICT (lab_name) DO UPDATE "
        "SET layout_json=EXCLUDED.layout_json",
        (data.lab_name, data.layout_json),
    )
    return {"status": "success"}


@router.get("/api/lab_settings")
async def get_lab_settings(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT lab_name, main_pc, layout_json FROM lab_settings", fetch=True)
    return {
        row["lab_name"]: {"main_pc": row["main_pc"], "layout_json": row["layout_json"] or "{}"} for row in (rows or [])
    }


@router.post("/api/set_auto_enroll", deprecated=True)
async def set_auto_enroll(data: AutoEnrollInput, auth: dict = Depends(require_admin)):
    """Bitiş tarihine kadar (o gün dahil) İLK kez bağlanan cihazlar bu sınıfa atanır (bkz. agents.py).
    Sınıfı belirtilmiş bir enroll jetonu varsa o önceliklidir."""
    try:
        until = datetime.date.fromisoformat((data.expire_date or "").strip())
    except ValueError:
        raise HTTPException(status_code=400, detail="Bitiş tarihi YYYY-AA-GG olmalı.")
    lab = (data.target_lab or "").strip()
    if not lab:
        raise HTTPException(status_code=400, detail="Sınıf seçin.")
    await execute_query(
        "INSERT INTO global_settings (key, value) "
        "VALUES ('auto_enroll_lab', $1) ON CONFLICT (key) DO UPDATE "
        "SET value=EXCLUDED.value",
        (json.dumps({"lab": lab, "until": until.isoformat()}),),
    )
    await add_audit_log(
        "*",
        "auto_enroll",
        "Oto-kayıt: %s (%s tarihine kadar)" % (lab, until.isoformat()),
        {"by": auth.get("sub"), "lab": lab, "until": until.isoformat()},
    )
    return {"status": "success"}
