"""Yazılım envanteri ve Windows güncelleme (yama) durumu.

Ajan uçları yalnızca ANAHTARLI (secret) ajanları kabul eder, enforce_agent_auth kapalı olsa bile:
bu verileri yalnızca yeni ajanlar gönderir ve hepsi kayıtlıdır; eski uçlardaki "legacy kabul"
artık riski burada yoktur. Panel uçları: okuma require_auth, yama tarama/kurma emri require_admin."""

import datetime
import json
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException

from pops import db
from pops.agent_auth import agent_http_auth, bind_agent
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.manager import manager
from pops.models import PatchInstallInput, PatchStatusInput, SoftwareInventoryInput
from pops.security import require_admin, require_auth
from pops.taskqueue import resolve_targets

router = APIRouter()

MAX_SOFTWARE_ITEMS = 5000


async def _require_enrolled(agent_id: Optional[str], pc_name: str) -> None:
    if agent_id is None:
        raise HTTPException(status_code=401, detail="Bu uç yalnızca kayıtlı (anahtarlı) ajanları kabul eder.")
    await bind_agent(agent_id, pc_name)


def _parse_ts(v: Optional[str]) -> Optional[datetime.datetime]:
    if not v:
        return None
    try:
        ts = datetime.datetime.fromisoformat(v.replace("Z", "+00:00"))
        return ts if ts.tzinfo else ts.astimezone()
    except ValueError:
        return None


# ─── Ajan → sunucu ────────────────────────────────────────────────────────────
@router.post("/api/software/{pc_name}")
async def put_software(pc_name: str, data: SoftwareInventoryInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    """Cihazın kurulu yazılım listesinin TAMAMI; önceki liste bununla değiştirilir."""
    await _require_enrolled(agent_id, pc_name)
    if len(data.items) > MAX_SOFTWARE_ITEMS:
        raise HTTPException(status_code=413, detail="En fazla %d kayıt gönderilebilir." % MAX_SOFTWARE_ITEMS)
    seen = {}
    for it in data.items:
        name = (it.name or "").strip()[:300]
        if not name:
            continue
        version = (it.version or "").strip()[:100]
        seen[(name, version)] = (
            (it.publisher or "").strip()[:200] or None,
            (it.install_date or "").strip()[:20] or None,
        )
    async with db.acquire() as conn:
        async with conn.transaction():
            await conn.execute("DELETE FROM device_software WHERE pc_name = $1", pc_name)
            if seen:
                await conn.executemany(
                    "INSERT INTO device_software (pc_name, name, version, publisher, install_date) "
                    "VALUES ($1,$2,$3,$4,$5)",
                    [(pc_name, n, v, p, d) for (n, v), (p, d) in seen.items()],
                )
    return {"status": "success", "count": len(seen)}


# Panel emirleri ajanın /api/patches/{pc_name} ucundan ÖNCE tanımlanır: FastAPI rotaları sırayla eşler,
# yoksa POST /api/patches/install o uca düşerdi.
async def _patch_command(data: PatchInstallInput, auth: dict, action: str, label: str) -> dict:
    if data.target_mode not in ("ALL", "LAB", "PC"):
        raise HTTPException(status_code=400, detail="Geçersiz hedef türü.")
    if data.scope not in ("security", "all"):
        raise HTTPException(status_code=400, detail="Kapsam 'security' ya da 'all' olmalı.")
    targets = [t["pc"] for t in await resolve_targets(data.target_mode, data.targets)]
    online = sorted(t for t in set(targets) if t in manager.active_agents)
    offline = sorted(t for t in set(targets) if t not in manager.active_agents)
    msg = {"action": action, "scope": data.scope}
    for pc in online:
        await manager.send_command(msg, pc)
    await add_audit_log(
        "*",
        action,
        "%s (%s): %d cihaz" % (label, auth.get("sub"), len(online)),
        {"by": auth.get("sub"), "scope": data.scope, "dispatched": online, "offline": offline},
    )
    return {"ok": True, "dispatched": online, "skipped_offline": offline}


@router.post("/api/patches/scan")
async def scan_patches(data: PatchInstallInput, auth: dict = Depends(require_admin)):
    """Ajanlardan Windows Update taraması ister (sonuç /api/patches/{pc} ile gelir)."""
    return await _patch_command(data, auth, "scan_updates", "Windows Update taraması istendi")


@router.post("/api/patches/install")
async def install_patches(data: PatchInstallInput, auth: dict = Depends(require_admin)):
    """Ajanlara bekleyen Windows güncellemelerini kurdurur (security: yalnız güvenlik/kritik, all: hepsi).
    Ajan yeniden başlatmaz; gerekiyorsa reboot_required bildirir."""
    return await _patch_command(data, auth, "install_updates", "Windows güncellemeleri kurulumu istendi")


@router.post("/api/patches/{pc_name}")
async def put_patch_status(pc_name: str, data: PatchStatusInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    """Cihazın son Windows Update taraması (bekleyen güncellemeler, yeniden başlatma gereksinimi)."""
    await _require_enrolled(agent_id, pc_name)
    updates = [
        {
            "kb": (u.kb or "")[:20] or None,
            "title": (u.title or "")[:300],
            "severity": (u.severity or "")[:20] or None,
            "categories": [c[:60] for c in u.categories[:10]],
            "is_security": bool(u.is_security),
        }
        for u in data.updates[:500]
    ]
    await execute_query(
        "INSERT INTO device_patch_status (pc_name, pending_count, pending_security, pending_critical, reboot_required, "
        "last_search, last_install, updates, last_result, updated_at) VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9, now()) "
        "ON CONFLICT (pc_name) DO UPDATE SET pending_count=EXCLUDED.pending_count, "
        "pending_security=EXCLUDED.pending_security, pending_critical=EXCLUDED.pending_critical, "
        "reboot_required=EXCLUDED.reboot_required, last_search=EXCLUDED.last_search, "
        "last_install=COALESCE(EXCLUDED.last_install, device_patch_status.last_install), updates=EXCLUDED.updates, "
        "last_result=COALESCE(EXCLUDED.last_result, device_patch_status.last_result), updated_at=now()",
        (
            pc_name,
            max(0, data.pending_count),
            max(0, data.pending_security),
            max(0, data.pending_critical),
            data.reboot_required,
            _parse_ts(data.last_search),
            _parse_ts(data.last_install),
            json.dumps(updates),
            (data.last_result or "")[:500] or None,
        ),
    )
    return {"status": "success"}


# ─── Panel ────────────────────────────────────────────────────────────────────
@router.get("/api/software")
async def search_software(q: str = "", limit: int = 300, auth: dict = Depends(require_auth)):
    """Filodaki yazılımlar: ad, yayıncı, sürümler ve kaç cihazda kurulu olduğu."""
    limit = max(1, min(limit, 2000))
    rows = await execute_query(
        "SELECT name, max(publisher) AS publisher, count(DISTINCT pc_name) AS devices, "
        "array_agg(DISTINCT version) AS versions FROM device_software "
        "WHERE $1 = '' OR name ILIKE '%' || $1 || '%' OR publisher ILIKE '%' || $1 || '%' "
        "GROUP BY name ORDER BY count(DISTINCT pc_name) DESC, name LIMIT $2",
        ((q or "").strip()[:100], limit),
        fetch=True,
    )
    total = await execute_query("SELECT count(DISTINCT pc_name) AS n FROM device_software", fetch=True)
    return {"items": [dict(r) for r in rows or []], "reporting_devices": int(total[0]["n"]) if total else 0}


@router.get("/api/software/devices")
async def software_devices(name: str, auth: dict = Depends(require_auth)):
    """Belirli bir yazılımın kurulu olduğu cihazlar ve sürümleri."""
    rows = await execute_query(
        "SELECT s.pc_name, s.version, s.install_date, c.hostname, c.display_name, c.lab_name, c.status "
        "FROM device_software s LEFT JOIN clients c ON c.pc_name = s.pc_name WHERE s.name = $1 "
        "ORDER BY c.lab_name NULLS LAST, c.hostname",
        (name,),
        fetch=True,
    )
    return [dict(r) for r in rows or []]


@router.get("/api/devices/{pc_name}/software")
async def device_software(pc_name: str, auth: dict = Depends(require_auth)):
    rows = await execute_query(
        "SELECT name, version, publisher, install_date, updated_at FROM device_software WHERE pc_name = $1 "
        "ORDER BY lower(name)",
        (pc_name,),
        fetch=True,
    )
    out = []
    for r in rows or []:
        r = dict(r)
        r["updated_at"] = r["updated_at"].astimezone().isoformat()
        out.append(r)
    return out


@router.get("/api/patches")
async def list_patch_status(auth: dict = Depends(require_auth)):
    """Cihaz başına Windows Update durumu (hiç bildirmeyen cihazlar da listelenir)."""
    rows = await execute_query(
        "SELECT c.pc_name, c.hostname, c.display_name, c.lab_name, c.status, av.version AS agent_version, "
        "p.pending_count, p.pending_security, p.pending_critical, p.reboot_required, p.last_search, "
        "p.last_install, p.updates, p.last_result, p.updated_at "
        "FROM clients c LEFT JOIN device_patch_status p ON p.pc_name = c.pc_name "
        "LEFT JOIN agent_versions av ON av.pc_name = c.pc_name ORDER BY c.lab_name NULLS LAST, c.hostname",
        fetch=True,
    )
    out = []
    for r in rows or []:
        r = dict(r)
        for k in ("last_search", "last_install", "updated_at"):
            if r.get(k):
                r[k] = r[k].astimezone().isoformat()
        r["updates"] = json.loads(r["updates"]) if r.get("updates") else []
        r["reported"] = r.get("updated_at") is not None
        out.append(r)
    return out
