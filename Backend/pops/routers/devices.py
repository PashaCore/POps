"""Cihaz/lab yönetimi, envanter/log okuma ve Wake-on-LAN uçları."""

from fastapi import APIRouter, Depends

from pops.config import LOG_TABLE, USE_V2_SCHEMA
from pops import db
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
from pops.manager import manager
from pops.wol import attempt_p2p_wol, send_wol_packet

router = APIRouter()


@router.delete("/api/devices/{pc_name}")
async def delete_device(pc_name: str, auth: dict = Depends(require_admin)):
    try:
        await execute_query("DELETE FROM clients WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM hw_inventory WHERE pc_name = $1", (pc_name,))
        await execute_query(f"DELETE FROM {LOG_TABLE} WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM agent_versions WHERE pc_name = $1", (pc_name,))
        await execute_query("DELETE FROM agent_secrets WHERE pc_name = $1", (pc_name,))
        # Cihaz çevrimiçiyse ajan bağlantısını da kapat
        agent_ws = manager.active_agents.get(pc_name)
        manager.disconnect_agent(pc_name)
        if agent_ws:
            try:
                await agent_ws.close(code=4000, reason="Cihaz silindi")
            except Exception:
                pass
        return {"status": "success", "message": f"{pc_name} silindi."}
    except Exception as e:
        return {"status": "error", "message": str(e)}


@router.post("/api/wake_pc/{pc_name}")
async def wake_pc(pc_name: str, auth: dict = Depends(require_admin)):
    row = await execute_query(
        "SELECT c.lab_name, h.mac_address FROM clients c "
        "LEFT JOIN hw_inventory h ON c.pc_name = h.pc_name "
        "WHERE c.pc_name = $1",
        (pc_name,),
        fetch=True,
    )
    if not row or not row[0]["mac_address"] or row[0]["mac_address"] == "-":
        return {"status": "error", "message": "MAC adresi bulunamadı."}
    mac = row[0]["mac_address"]
    lab_name = row[0]["lab_name"]
    send_wol_packet(mac)
    if lab_name and lab_name != "Atanmamis_Cihazlar":
        await attempt_p2p_wol(mac, lab_name)
    return {"status": "success", "message": "WOL gönderildi."}


@router.post("/api/wake_lab/{lab_name}")
async def wake_lab(lab_name: str, auth: dict = Depends(require_admin)):
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


@router.post("/api/wake_all")
async def wake_all(auth: dict = Depends(require_admin)):
    rows = await execute_query(
        "SELECT c.lab_name, h.mac_address FROM hw_inventory h JOIN clients c ON h.pc_name = c.pc_name", fetch=True
    )
    count = 0
    for r in rows or []:
        mac = r["mac_address"]
        lab = r["lab_name"]
        if mac and mac != "-":
            send_wol_packet(mac)
            if lab and lab != "Atanmamis_Cihazlar":
                await attempt_p2p_wol(mac, lab)
            count += 1
    return {"status": "success", "woken_pcs": count}


@router.get("/api/devices")
async def get_devices(auth: dict = Depends(require_auth)):
    query = """
    SELECT
        c.pc_name, c.hostname, c.display_name, c.lab_name, c.last_seen, c.status, c.active_window,
        c.boot_count, c.logged_user, c.ip_address, c.cap_ram_readable, c.is_quarantined,
        c.cap_terminal_enabled, c.cap_vision_enabled,
        c.cap_terminal_disable_requested, c.cap_vision_disable_requested, c.running_version,
        av.version AS agent_version
    FROM clients c
    LEFT JOIN agent_versions av ON c.pc_name = av.pc_name
    """
    rows = await execute_query(query, fetch=True)
    return [
        {
            "hostname": r["pc_name"],
            "real_hostname": r["hostname"] or r["pc_name"],
            "display_name": r["display_name"],
            "pc_name": r["hostname"] or r["pc_name"],
            "hw_id": r["pc_name"],
            "ip": r["ip_address"],
            "lab": r["lab_name"],
            "status": r["status"],
            "last_seen": r["last_seen"],
            "active_window": r["active_window"],
            "boot_count": r["boot_count"],
            "current_user": r.get("logged_user", "-"),
            "is_quarantined": r.get("is_quarantined", False),
            "agent_version": r.get("agent_version") or "Bilinmiyor",
            "running_version": r.get("running_version"),
            "cap_terminal_enabled": r.get("cap_terminal_enabled"),
            "cap_vision_enabled": r.get("cap_vision_enabled"),
            "cap_terminal_disable_requested": r.get("cap_terminal_disable_requested", False),
            "cap_vision_disable_requested": r.get("cap_vision_disable_requested", False),
        }
        for r in (rows or [])
    ]


@router.get("/api/inventory")
async def get_all_inventory(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT * FROM hw_inventory", fetch=True)
    return rows if rows else []


@router.get("/api/logs")
async def get_all_logs(limit: int = 1000, auth: dict = Depends(require_auth)):
    if USE_V2_SCHEMA:
        rows = await execute_query("SELECT * FROM agent_logs_v2 ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    else:
        rows = await execute_query("SELECT * FROM agent_logs ORDER BY id DESC LIMIT $1", (limit,), fetch=True)
    return rows if rows else []


@router.post("/api/create_lab")
async def create_lab(data: CreateLabInput, auth: dict = Depends(require_admin)):
    await execute_query("INSERT INTO custom_labs (lab_name) VALUES ($1) ON CONFLICT DO NOTHING", (data.lab_name,))
    return {"status": "success"}


@router.get("/api/custom_labs")
async def get_custom_labs(auth: dict = Depends(require_auth)):
    rows = await execute_query("SELECT lab_name FROM custom_labs", fetch=True)
    return [row["lab_name"] for row in (rows or [])]


@router.post("/api/rename_lab")
async def rename_lab(data: RenameLabInput, auth: dict = Depends(require_admin)):
    # Oturma planı (lab_settings) ve görev kayıtları da yeni ada taşınır; hepsi tek işlemde
    async with db.db_pool.acquire() as conn:
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
    return {"status": "success"}


@router.post("/api/rename_device")
async def rename_device(data: RenameDeviceInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET display_name = $1 WHERE pc_name = $2", (data.display_name, data.pc_name))
    return {"status": "success"}


@router.post("/api/delete_lab")
async def delete_lab(data: DeleteLabInput, auth: dict = Depends(require_admin)):
    async with db.db_pool.acquire() as conn:
        async with conn.transaction():
            await conn.execute("DELETE FROM custom_labs WHERE lab_name = $1", data.lab_name)
            await conn.execute("UPDATE clients SET lab_name = 'Atanmamis_Cihazlar' WHERE lab_name = $1", data.lab_name)
            await conn.execute("DELETE FROM lab_settings WHERE lab_name = $1", data.lab_name)
    return {"status": "success"}


@router.post("/api/move_pc")
async def move_pc(data: MovePcInput, auth: dict = Depends(require_admin)):
    await execute_query("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", (data.new_lab, data.pc_name))
    return {"status": "success"}


@router.post("/api/move_pcs")
async def move_pcs(data: MovePcsInput, auth: dict = Depends(require_admin)):
    for pc in data.pc_names:
        await execute_query("UPDATE clients SET lab_name = $1 WHERE pc_name = $2", (data.new_lab, pc))
    return {"status": "success"}


@router.post("/api/set_main_pc")
async def set_main_pc(data: SetMainPcInput, auth: dict = Depends(require_admin)):
    current = await execute_query("SELECT main_pc FROM lab_settings WHERE lab_name = $1", (data.lab_name,), fetch=True)
    if current and current[0]["main_pc"] == data.pc_name:
        await execute_query("UPDATE lab_settings SET main_pc = NULL WHERE lab_name = $1", (data.lab_name,))
        return {"status": "success", "message": f"{data.pc_name} ana bilgisayar yetkisi kaldırıldı."}

    await execute_query(
        "INSERT INTO lab_settings (lab_name, main_pc) "
        "VALUES ($1, $2) ON CONFLICT (lab_name) DO UPDATE "
        "SET main_pc=EXCLUDED.main_pc",
        (data.lab_name, data.pc_name),
    )
    return {"status": "success", "message": f"{data.pc_name} ana bilgisayar yapıldı."}


@router.post("/api/save_lab_layout")
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


@router.post("/api/set_auto_enroll")
async def set_auto_enroll(data: AutoEnrollInput, auth: dict = Depends(require_admin)):
    await execute_query(
        "INSERT INTO global_settings (key, value) "
        "VALUES ('auto_enroll_lab', $1) ON CONFLICT (key) DO UPDATE "
        "SET value=EXCLUDED.value",
        (data.target_lab,),
    )
    return {"status": "success"}
