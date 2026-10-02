"""Yardım masası: öğrenci/personel tepsiden "Sorun bildir" ile talep açar, BT ekibi panelden yanıtlar.

Ajan uçları yalnızca ANAHTARLI ajanı kabul eder (kimliksiz istemci talep yağdıramasın) ve cihaz başına
sınırlıdır: en fazla 5 açık talep, saatte en fazla 10 yeni talep. Ajan kendi cihazının taleplerini ve
panelden verilen yanıtları (iç notlar hariç) okuyabilir. Panel uçları require_admin."""

import datetime
import time
from typing import Optional

from fastapi import APIRouter, Depends, HTTPException

from pops.agent_auth import agent_http_auth, bind_agent
from pops.db import execute_query
from pops.manager import manager
from pops.models import AgentTicketInput, PanelTicketInput, TicketMessageInput, TicketUpdateInput
from pops.notify import notify
from pops.security import require_admin

router = APIRouter()

CATEGORIES = ("donanim", "yazilim", "ag", "yazici", "hesap", "diger")
STATUSES = ("open", "in_progress", "waiting", "resolved", "closed")
PRIORITIES = ("low", "normal", "high")
STATUS_TR = {
    "open": "açık",
    "in_progress": "üzerinde çalışılıyor",
    "waiting": "yanıt bekleniyor",
    "resolved": "çözüldü",
    "closed": "kapatıldı",
}
MAX_OPEN_PER_PC = 5
MAX_NEW_PER_HOUR = 10
AGENT_MIN_INTERVAL = 5.0  # ajan uçları: cihaz başına en az 5 sn arayla (tepsi içinde çalışan koda karşı)
_agent_last = {}  # (uç, pc_name) -> son istek zamanı


def _throttle(kind: str, pc_name: str) -> None:
    now = time.monotonic()
    key = (kind, pc_name)
    if now - _agent_last.get(key, 0.0) < AGENT_MIN_INTERVAL:
        raise HTTPException(status_code=429, detail="Çok sık istek; birkaç saniye sonra tekrar deneyin.")
    _agent_last[key] = now
    if len(_agent_last) > 10000:  # bellek sınırı: eski kayıtları at
        for k in [k for k, t in _agent_last.items() if now - t > 60]:
            _agent_last.pop(k, None)


def _iso(r: dict) -> dict:
    r = dict(r)
    for k in ("created_at", "updated_at", "resolved_at"):
        if r.get(k):
            r[k] = r[k].astimezone().isoformat()
    return r


def _clean(subject: str, body: Optional[str], category: Optional[str]) -> tuple:
    subject = (subject or "").strip()[:200]
    if len(subject) < 3:
        raise HTTPException(status_code=400, detail="Konu en az 3 karakter olmalı.")
    category = category if category in CATEGORIES else "diger"
    return subject, (body or "").strip()[:5000], category


# ─── Ajan (tepsi → servis → sunucu) ───────────────────────────────────────────
@router.post("/api/tickets/agent/{pc_name}")
async def agent_create_ticket(pc_name: str, data: AgentTicketInput, agent_id: Optional[str] = Depends(agent_http_auth)):
    if agent_id is None:
        raise HTTPException(status_code=401, detail="Bu uç yalnızca kayıtlı (anahtarlı) ajanları kabul eder.")
    await bind_agent(agent_id, pc_name)
    _throttle("create", pc_name)
    subject, body, category = _clean(data.subject, data.body, data.category)
    counts = await execute_query(
        "SELECT count(*) FILTER (WHERE status IN ('open','in_progress','waiting')) AS open_n, "
        "count(*) FILTER (WHERE created_at > now() - interval '1 hour') AS hour_n FROM tickets WHERE pc_name = $1",
        (pc_name,),
        fetch=True,
    )
    if counts and (counts[0]["open_n"] >= MAX_OPEN_PER_PC or counts[0]["hour_n"] >= MAX_NEW_PER_HOUR):
        raise HTTPException(
            status_code=429, detail="Bu bilgisayardan çok fazla açık talep var; BT ekibinin yanıtını bekleyin."
        )
    reporter = (data.reporter or "").strip()[:100] or None
    rows = await execute_query(
        "INSERT INTO tickets (source, pc_name, reporter, category, subject, body) VALUES ('agent',$1,$2,$3,$4,$5) "
        "RETURNING id",
        (pc_name, reporter, category, subject, body),
        fetch=True,
    )
    tid = rows[0]["id"]
    await notify(
        "ticket_new",
        "medium",
        "Yeni destek talebi #%d: %s" % (tid, subject),
        ("%s · %s" % (reporter or "?", body[:300])) if body else (reporter or ""),
        pc_name,
    )
    await manager.broadcast_to_panels({"type": "ticket_new", "id": tid, "pc_name": pc_name, "subject": subject})
    return {"ok": True, "id": tid}


@router.get("/api/tickets/agent/{pc_name}")
async def agent_list_tickets(pc_name: str, agent_id: Optional[str] = Depends(agent_http_auth)):
    """Tepsinin "Taleplerim" listesi: bu cihazın son 20 talebi ve iç not OLMAYAN yanıtlar."""
    if agent_id is None:
        raise HTTPException(status_code=401, detail="Bu uç yalnızca kayıtlı (anahtarlı) ajanları kabul eder.")
    await bind_agent(agent_id, pc_name)
    _throttle("list", pc_name)
    tickets = await execute_query(
        "SELECT id, created_at, updated_at, subject, status, reporter FROM tickets WHERE pc_name = $1 "
        "ORDER BY id DESC LIMIT 20",
        (pc_name,),
        fetch=True,
    )
    ids = [t["id"] for t in tickets or []]
    msgs = (
        await execute_query(
            "SELECT ticket_id, created_at, author, body FROM ticket_messages WHERE ticket_id = ANY($1::int[]) "
            "AND NOT internal ORDER BY id",
            (ids,),
            fetch=True,
        )
        if ids
        else []
    )
    by_ticket = {}
    for m in msgs or []:
        by_ticket.setdefault(m["ticket_id"], []).append(_iso({k: m[k] for k in ("created_at", "author", "body")}))
    out = []
    for t in tickets or []:
        item = _iso(t)
        item["status_text"] = STATUS_TR.get(t["status"], t["status"])
        item["replies"] = by_ticket.get(t["id"], [])
        out.append(item)
    return out


# ─── Panel ────────────────────────────────────────────────────────────────────
@router.get("/api/tickets")
async def list_tickets(status: str = "active", q: str = "", auth: dict = Depends(require_admin)):
    where, params = [], []
    if status == "active":
        where.append("t.status IN ('open','in_progress','waiting')")
    elif status in STATUSES:
        params.append(status)
        where.append("t.status = $%d" % len(params))
    q = (q or "").strip()[:100]
    if q:
        params.append(q)
        n = len(params)
        where.append(
            "(t.subject ILIKE '%%' || $%d || '%%' OR t.body ILIKE '%%' || $%d || '%%' OR "
            "t.reporter ILIKE '%%' || $%d || '%%' OR c.hostname ILIKE '%%' || $%d || '%%')" % (n, n, n, n)
        )
    sql = (
        "SELECT t.*, c.hostname, c.display_name, c.lab_name, "
        "(SELECT count(*) FROM ticket_messages m WHERE m.ticket_id = t.id) AS message_count "
        "FROM tickets t LEFT JOIN clients c ON c.pc_name = t.pc_name"
    )
    if where:
        sql += " WHERE " + " AND ".join(where)
    sql += " ORDER BY CASE t.priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END, t.updated_at DESC LIMIT 300"
    rows = await execute_query(sql, tuple(params), fetch=True)
    counts = await execute_query("SELECT status, count(*) AS n FROM tickets GROUP BY status", fetch=True)
    return {"items": [_iso(r) for r in rows or []], "counts": {r["status"]: int(r["n"]) for r in counts or []}}


@router.get("/api/tickets/{ticket_id}")
async def get_ticket(ticket_id: int, auth: dict = Depends(require_admin)):
    rows = await execute_query(
        "SELECT t.*, c.hostname, c.display_name, c.lab_name, c.status AS device_status, c.logged_user, "
        "c.active_window FROM tickets t LEFT JOIN clients c ON c.pc_name = t.pc_name WHERE t.id = $1",
        (ticket_id,),
        fetch=True,
    )
    if not rows:
        raise HTTPException(status_code=404, detail="Talep bulunamadı.")
    msgs = await execute_query(
        "SELECT * FROM ticket_messages WHERE ticket_id = $1 ORDER BY id", (ticket_id,), fetch=True
    )
    return {**_iso(rows[0]), "messages": [_iso(m) for m in msgs or []]}


@router.post("/api/tickets")
async def create_ticket(data: PanelTicketInput, auth: dict = Depends(require_admin)):
    subject, body, category = _clean(data.subject, data.body, data.category)
    priority = data.priority if data.priority in PRIORITIES else "normal"
    pc = (data.pc_name or "").strip()[:100] or None
    reporter = (data.reporter or "").strip()[:100] or auth.get("sub")
    rows = await execute_query(
        "INSERT INTO tickets (source, pc_name, reporter, category, subject, body, priority) "
        "VALUES ('panel',$1,$2,$3,$4,$5,$6) RETURNING id",
        (pc, reporter, category, subject, body, priority),
        fetch=True,
    )
    return {"ok": True, "id": rows[0]["id"]}


@router.post("/api/tickets/{ticket_id}/update")
async def update_ticket(ticket_id: int, data: TicketUpdateInput, auth: dict = Depends(require_admin)):
    rows = await execute_query("SELECT status, priority, assignee FROM tickets WHERE id = $1", (ticket_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Talep bulunamadı.")
    cur = rows[0]
    changes = []
    status, priority, assignee = cur["status"], cur["priority"], cur["assignee"]
    if data.status is not None and data.status != status:
        if data.status not in STATUSES:
            raise HTTPException(status_code=400, detail="Geçersiz durum.")
        status = data.status
        changes.append("durum: %s" % STATUS_TR[status])
    if data.priority is not None and data.priority != priority:
        if data.priority not in PRIORITIES:
            raise HTTPException(status_code=400, detail="Geçersiz öncelik.")
        priority = data.priority
        changes.append("öncelik: %s" % priority)
    if data.assignee is not None and (data.assignee.strip() or None) != assignee:
        assignee = data.assignee.strip()[:100] or None
        changes.append("atanan: %s" % (assignee or "yok"))
    if not changes:
        return {"ok": True, "changed": False}
    await execute_query(
        "UPDATE tickets SET status=$1, priority=$2, assignee=$3, updated_at=now(), "
        "resolved_at = CASE WHEN $1 IN ('resolved','closed') THEN coalesce(resolved_at, now()) ELSE NULL END "
        "WHERE id=$4",
        (status, priority, assignee, ticket_id),
    )
    await execute_query(
        "INSERT INTO ticket_messages (ticket_id, author, body, internal) VALUES ($1,$2,$3,TRUE)",
        (ticket_id, auth.get("sub") or "?", "; ".join(changes)),
    )
    return {"ok": True, "changed": True}


@router.post("/api/tickets/{ticket_id}/messages")
async def add_ticket_message(ticket_id: int, data: TicketMessageInput, auth: dict = Depends(require_admin)):
    body = (data.body or "").strip()[:5000]
    if not body:
        raise HTTPException(status_code=400, detail="Mesaj boş olamaz.")
    rows = await execute_query("SELECT status FROM tickets WHERE id = $1", (ticket_id,), fetch=True)
    if not rows:
        raise HTTPException(status_code=404, detail="Talep bulunamadı.")
    await execute_query(
        "INSERT INTO ticket_messages (ticket_id, author, body, internal) VALUES ($1,$2,$3,$4)",
        (ticket_id, auth.get("sub") or "?", body, bool(data.internal)),
    )
    # Kullanıcıya yanıt verildiyse ve talep açıksa "yanıt bekleniyor"a çek; iç not durumu değiştirmez
    new_status = "waiting" if (not data.internal and rows[0]["status"] == "open") else rows[0]["status"]
    await execute_query("UPDATE tickets SET updated_at = now(), status = $1 WHERE id = $2", (new_status, ticket_id))
    return {"ok": True, "at": datetime.datetime.now().astimezone().isoformat()}
