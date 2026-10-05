"""Güç komutları ve kullanıcıya mesaj uçları (pops/power.py; ajan sözleşmesi docs/agent.md).

İkisi de görev açar (tasks.kind = 'power' | 'user_message', ayrıntı payload'da) ve kuyruk gönderir: sonuç İşlemler'de
ve cihazın Son işlemler'inde görünür. Yalnızca çevrimiçi hedeflere görev açılır; kapalı bilgisayar açılınca
saatler sonra kapanmasın ya da eski bir mesaj görmesin diye görev 15 dakika içinde gönderilemezse süresi dolar.
"""

import json
import uuid

from fastapi import APIRouter, Depends, HTTPException, Request

from pops import db, power, timeutil
from pops.audit import add_audit_log
from pops.db import execute_query
from pops.manager import manager
from pops.models import PowerCommandInput, UserMessageInput
from pops.security import require_admin, require_admin_session
from pops.taskqueue import process_queue, resolve_targets

router = APIRouter()

TASK_TTL_SECONDS = 15 * 60


async def _online_targets(target_mode: str, targets: list) -> tuple:
    if target_mode != "ALL" and not targets:
        raise HTTPException(status_code=422, detail="Hedef bilgisayar yok.")
    resolved = await resolve_targets(target_mode, targets)
    unknown = [t["pc"] for t in resolved if t.get("unknown")]
    if unknown:
        raise HTTPException(
            status_code=422,
            detail="Kayıtlı olmayan bilgisayar: %s" % ", ".join(unknown[:10]) + (" …" if len(unknown) > 10 else ""),
        )
    seen, online, offline = set(), [], []
    for t in resolved:
        if t["pc"] in seen:
            continue
        seen.add(t["pc"])
        (online if t["pc"] in manager.active_agents else offline).append(t)
    return online, [t["pc"] for t in offline]


async def _plans(kind: str, spec: dict, pcs: list) -> dict:
    """Hedeflerin bugünkü ajanına göre nasıl gideceği (native / fallback / unsupported). Panel bildirimi içindir;
    son kararı gönderim anında kuyruk verir (ajan arada güncellenmiş olabilir)."""
    rows = await execute_query(
        "SELECT c.pc_name, c.platform, av.features FROM clients c "
        "LEFT JOIN agent_versions av ON av.pc_name = c.pc_name WHERE c.pc_name = ANY($1::text[])",
        (pcs,),
        fetch=True,
    )
    out = {"native": [], "fallback": [], "unsupported": []}
    for r in rows or []:
        out[power.plan(kind, spec, r["features"], r["platform"])].append(r["pc_name"])
    return out


async def _queue(kind: str, spec: dict, title: str, data, auth: dict, request: Request) -> dict:
    online, offline = await _online_targets(data.target_mode, data.targets)
    creator = auth.get("sub")
    batch_id = uuid.uuid4().hex[:16]
    ids = []
    if online:
        now = timeutil.now()
        async with db.transaction() as conn:
            created = await conn.fetch(
                "INSERT INTO tasks (target_pc, target_lab, script_path, status, created_at, created_by, title, source, "
                "client_ip, batch_id, kind, payload, expires_at) "
                "SELECT t.pc, t.lab, $3, 'Pending', $4, $5, $6, $7, $8, $9, $10, $11::jsonb, "
                "NOW() + make_interval(secs => $12) "
                "FROM unnest($1::text[], $2::text[]) WITH ORDINALITY AS t(pc, lab, n) ORDER BY t.n RETURNING id",
                [t["pc"] for t in online], [t["lab"] for t in online], power.summary(kind, spec), now, creator,
                title[:200], data.source, request.client.host if request and request.client else None, batch_id,
                kind, json.dumps(spec, ensure_ascii=False), float(TASK_TTL_SECONDS),
            )
        ids = [r["id"] for r in created]
    plans = {"native": [], "fallback": [], "unsupported": []}
    if online:
        plans = await _plans(kind, spec, [t["pc"] for t in online])
    if kind == power.KIND_POWER:
        meta = {"op": spec["op"], "delay": spec["delay"], "note": power.preview(spec["message"])}
        label = "Güç komutu istendi: %s" % power.OP_TITLES[spec["op"]]
    else:
        meta = {"style": spec["style"], "requires_ack": spec["requires_ack"], "title": power.preview(spec["title"]),
                "text": power.preview(spec["text"])}
        label = "Kullanıcıya mesaj istendi"
    meta.update(requested_by=creator, targets=len(online), skipped_offline=len(offline), batch_id=batch_id,
                unsupported=len(plans["unsupported"]), fallback=len(plans["fallback"]))
    await add_audit_log("*", "power_command" if kind == power.KIND_POWER else "user_message",
                        "%s (%s): %d cihaz" % (label, creator, len(online)), meta)
    if ids:
        await process_queue()
    return {"status": "success", "created": len(ids), "task_ids": ids, "batch_id": batch_id,
            "skipped_offline": offline, **plans}


@router.post("/api/devices/power")
async def power_command(data: PowerCommandInput, request: Request, auth: dict = Depends(require_admin)):
    """Kapat, yeniden başlat, oturumu kapat ya da kilitle; isteğe bağlı gecikme (0-600 sn) ve kullanıcıya not.
    "power" duyurmayan ajana kapatma/yeniden başlatma eski komutla gider, diğerleri gönderilmeden reddedilir (-8).
    API jetonuyla da kullanılabilir (admin rolü)."""
    try:
        spec = power.power_payload(data.op, data.delay, data.message or "")
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    title = data.title or power.OP_TITLES[data.op]
    return await _queue(power.KIND_POWER, spec, title, data, auth, request)


@router.post("/api/devices/message")
async def user_message(data: UserMessageInput, request: Request, auth: dict = Depends(require_admin_session)):
    """Oturumdaki kullanıcıya tepside mesaj (başlık, metin, bilgi/uyarı, isteğe bağlı okundu onayı). Mesajı bir kişi
    yazar: API jetonu kullanılamaz. "message" duyurmayan ajanda görev gönderilmeden reddedilir (-8)."""
    try:
        spec = power.message_payload(data.title, data.text, data.style, data.requires_ack)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))
    return await _queue(power.KIND_MESSAGE, spec, power.MESSAGE_TITLE, data, auth, request)
