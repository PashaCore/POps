"""Görev kuyruğu: eşzamanlılık sınırına göre bekleyen görevleri çevrimiçi ajanlara dağıtır."""

import asyncio
import datetime

from pops import metrics, modules, power, winget
from pops.db import execute_query
from pops.audit import add_audit_log, log_audit_event
from pops.manager import manager


async def resolve_targets(target_mode: str, targets, conn=None) -> list:
    """Görev hedefleri: ALL (tüm cihazlar), LAB (lab adları), PC (HW- kimlikleri) -> [{"pc", "lab"}].
    Hedef sayısından bağımsız tek sorgu (eskiden her lab/cihaz için ayrı sorgu gidiyordu). conn verilirse o bağlantı
    (ve işlemi) kullanılır."""

    async def fetch(query, *params):
        if conn is not None:
            return [dict(r) for r in await conn.fetch(query, *params)]
        return await execute_query(query, params, fetch=True) or []

    if target_mode == 'ALL':
        res = await fetch("SELECT pc_name, lab_name FROM clients")
        return [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in res]
    names = [str(t) for t in (targets or [])]
    if target_mode == 'LAB':
        res = await fetch(
            "SELECT pc_name, lab_name FROM clients WHERE lab_name = ANY($1::text[]) "
            "ORDER BY array_position($1::text[], lab_name), pc_name",
            names,
        )
        return [{"pc": r["pc_name"], "lab": r["lab_name"]} for r in res]
    res = await fetch("SELECT pc_name, lab_name FROM clients WHERE pc_name = ANY($1::text[])", names)
    labs = {r["pc_name"]: r["lab_name"] for r in res}
    # unknown: kayıtlı olmayan kimlik. Panelden gelen istekte reddedilir; zamanlanmış görevde o arada silinen cihazın
    # görevi eskisi gibi açılır ve geçerlilik süresi dolunca kapanır.
    return [{"pc": pc, "lab": labs.get(pc, "Bilinmeyen Lab"), "unknown": pc not in labs} for pc in names]


def _seconds_since(created_at) -> float:
    # created_at yerel saatte 'YYYY-MM-DD HH:MM:SS' metni
    try:
        created = datetime.datetime.strptime(str(created_at), "%Y-%m-%d %H:%M:%S")
        return (datetime.datetime.now() - created).total_seconds()
    except ValueError:
        return 0.0


# Eşzamanlı çağrılar birleştirilir: bir tur sürerken gelen çağrılar üst üste yığılmaz, tur bitince bir kez
# daha dönülür. Eskiden her ajan bağlantısı ayrı bir tur başlatıyor ve her tur çevrimiçi HER cihaz için ayrı sorgu
# atıyordu: 2000 ajan aynı anda bağlanınca ~2 milyon sorgu, sunucu dakikalarca meşgul (bkz. BENCHMARKS.md).
_queue_lock = asyncio.Lock()
_queue_again = False


async def process_queue():
    global _queue_again
    if _queue_lock.locked():
        _queue_again = True
        return
    async with _queue_lock:
        while True:
            _queue_again = False
            await _process_queue_once()
            if not _queue_again:
                break


async def _process_queue_once():
    # En sık durum: bekleyen görev yok. Tek ucuz sorguyla çıkılır.
    if not await execute_query("SELECT 1 FROM tasks WHERE status = 'Pending' LIMIT 1", fetch=True):
        return
    limit_row = await execute_query("SELECT value FROM global_settings WHERE key = 'concurrent_limit'", fetch=True)
    try:
        limit = max(0, int(limit_row[0]["value"])) if limit_row else 5
    except ValueError:
        limit = 5
    online_pcs = list(manager.active_agents.keys())
    if not online_pcs:
        return
    # Eşzamanlılık kotasını yalnızca BAĞLI cihazlardaki çalışan görevler tutar: bağlantısı kopmuş cihazın "Running"
    # görevi (sonucu bekleniyor ya da zaman aşımına gidiyor) bütün filonun kuyruğunu bekletmesin
    # Güç komutu ve kullanıcıya mesaj (pops/power.py) kotayı tutmaz ve kota doluyken de gönderilir: kısa sürer, iş
    # yükü değildir. Okundu onayı bekleyen mesaj (30 dakikaya kadar "Running") cihazın kuyruğunu da bekletmez.
    light = list(power.KINDS)
    running_row = await execute_query(
        "SELECT COUNT(DISTINCT target_pc) as c FROM tasks WHERE status = 'Running' AND target_pc = ANY($1::text[]) "
        "AND (kind IS NULL OR kind <> ALL($2::text[]))",
        (online_pcs, light),
        fetch=True,
    )
    running_pcs_count = running_row[0]["c"] if running_row else 0
    available_slots = limit - running_pcs_count
    if not (available_slots > 0 or limit == 0) and not await execute_query(
        "SELECT 1 FROM tasks WHERE status = 'Pending' AND kind = ANY($2::text[]) AND target_pc = ANY($1::text[]) "
        "LIMIT 1",
        (online_pcs, light),
        fetch=True,
    ):
        return
    await _refuse_winget(online_pcs)
    await _refuse_power(online_pcs)
    # Çevrimiçi ve şu an görev çalıştırmayan her cihazın en eski bekleyen görevi, tek sorguda; en eski görev önce.
    # Ajanın duyurduğu özellikler ve platformu güç/mesaj görevinin nasıl gönderileceğini belirler (pops/power.py).
    tasks = await execute_query(
        """
        SELECT * FROM (
            SELECT DISTINCT ON (t.target_pc) t.*, av.features AS agent_features, c.platform AS agent_platform
            FROM tasks t
            LEFT JOIN agent_versions av ON av.pc_name = t.target_pc
            LEFT JOIN clients c ON c.pc_name = t.target_pc
            WHERE t.status = 'Pending' AND t.target_pc = ANY($1::text[])
              AND (t.expires_at IS NULL OR t.expires_at > NOW())
              AND NOT EXISTS (SELECT 1 FROM tasks r WHERE r.status = 'Running' AND r.target_pc = t.target_pc
                              AND r.kind IS DISTINCT FROM $2)
            ORDER BY t.target_pc, t.id ASC
        ) oldest ORDER BY id ASC
        """,
        (online_pcs, power.KIND_MESSAGE),
        fetch=True,
    )
    # Uzak komut modülü cihazın laboratuvarında kapalıysa görev gönderilmez: "Denied" olur (modül kapatılırken
    # bekleyenler zaten reddedilir; bu, arada kuyruğa girenler ve yeniden denemeler içindir). Güç komutu ve mesaj
    # komut değildir, bu modüle bağlı değildir (eski ajandaki execute karşılığı _refuse_power'da denetlenir).
    _allowed, closed = await modules.split_pcs("terminal", [t["target_pc"] for t in tasks or []])
    if closed:
        await execute_query(
            "UPDATE tasks SET status = 'Denied', output = COALESCE(NULLIF(output, ''), '') || '[MODÜL KAPALI]: Uzak "
            "komut modülü bu cihazın laboratuvarında kapalı; görev çalıştırılmadı.' "
            "WHERE status = 'Pending' AND target_pc = ANY($1::text[]) AND (kind IS NULL OR kind <> ALL($2::text[]))",
            (closed, light),
        )
    for task in tasks or []:
        pc = task["target_pc"]
        is_light = task.get("kind") in power.KINDS
        if limit > 0 and available_slots <= 0 and not is_light:
            continue
        if pc in closed and not is_light:
            continue
        if is_light:
            try:
                message = power.message(task, task.get("created_by") or "System/Queue", task.get("agent_features"),
                                        task.get("agent_platform"))
            except (ValueError, TypeError):
                # Arada ajan değişti ya da kayıt bozuk: bir sonraki turda _refuse_power karar verir
                continue
            if message["action"] == "execute" and pc in closed:
                continue
        # agent_started_at: o anki ajan sürecinin (heartbeat'teki) başlangıç değeri; yeniden bağlanınca değiştiyse ajan
        # yeniden başlamıştır (bkz. routers/agents.py _settle_running_tasks; saatler karşılaştırılmaz)
        await execute_query(
            "UPDATE tasks SET status = 'Running', dispatched_at = NOW(), agent_started_at = "
            "(SELECT CASE WHEN jsonb_typeof(agent_health->'started_at') = 'number' "
            "THEN (agent_health->>'started_at')::float8 END FROM clients WHERE pc_name = $2) WHERE id = $1",
            (task["id"], pc),
        )
        # F4(a): komutu KİMİN kuyrukladığını göster (eskiden 'System/Queue' idi, iz yoktu).
        actor = task.get("created_by") or "System/Queue"
        is_winget = task.get("kind") == winget.KIND
        if is_light:
            pass   # ileti yukarıda kuruldu
        elif is_winget:
            try:
                message = winget.message(task, actor)
            except (ValueError, TypeError):
                # Kayıt elle bozulmadıkça olmaz: paket bilgisi doğrulanamayan görev ajana hiç gitmez
                await execute_query(
                    "UPDATE tasks SET status = 'Error', output = $2 WHERE id = $1", (task["id"], winget.INVALID_OUTPUT)
                )
                continue
        else:
            # requested_by: ajan komutu kimin istediğini yerel denetim izine (Windows Olay Günlüğü) yazar (0.1.12+)
            message = {"action": "execute", "task_id": task["id"], "script_path": task["script_path"],
                       "requested_by": actor}
        sent = await manager.send_command(message, pc)
        if sent:
            metrics.observe_dispatch(_seconds_since(task.get("created_at")))
        else:
            # Bağlantı bu arada koptu: görev ajana ulaşmadı, sıraya geri döner (yeniden bağlanınca gönderilir)
            await execute_query(
                "UPDATE tasks SET status = 'Pending', dispatched_at = NULL WHERE id = $1 AND status = 'Running'",
                (task["id"],),
            )
            continue
        if is_light:
            await _audit_power_dispatch(pc, task, message, actor)
            continue
        meta = {"raw_command": task["script_path"], "created_by": task.get("created_by")}
        if is_winget:
            meta.update(winget_id=message["id"], winget_version=message["version"])
        await log_audit_event(
            pc,
            "Deploy",
            f"Görev: {task['script_path'][:50]}",
            actor_id=actor,
            event_type="deploy.execution",
            category="system_maintenance",
            action="winget_install" if is_winget else "execute_queue",
            risk_level="info",
            meta_data=meta,
        )
        # SYSTEM olarak komut çalıştırma (ya da paket kurma) yüksek-değerli olay → hash-zincirli,
        # ajanların yazamadığı loga da düş.
        if is_winget:
            await add_audit_log(
                pc,
                "winget_install",
                "winget paketi kurulumu gönderildi (kuyruk: %s)" % actor,
                {"task_id": task["id"], "created_by": task.get("created_by"), "id": message["id"],
                 "version": message["version"]},
            )
        else:
            await add_audit_log(
                pc,
                "execute",
                "SYSTEM komutu çalıştırıldı (kuyruk: %s)" % actor,
                {
                    "task_id": task["id"],
                    "created_by": task.get("created_by"),
                    "command": (task["script_path"] or "")[:200],
                },
            )
        available_slots -= 1


async def _refuse_winget(online_pcs: list) -> None:
    """Çevrimiçi cihazların bekleyen winget görevlerinden gönderilemeyecek olanlar (kuyruğun seçiminden ÖNCE, cihazın
    sıradaki görevi beklemesin diye):
      - cihazın laboratuvarında dosya dağıtımı modülü kapalı: "Denied", [MODÜL KAPALI];
      - ajan bağlanırken "winget" özelliğini duyurmadı (X-Agent-Features; eski ajan iletiyi yok sayıp görevi
        "Running"de bırakırdı): gönderilmeden "Denied", çıkış kodu -8."""
    rows = await execute_query(
        "SELECT DISTINCT t.target_pc, av.features FROM tasks t LEFT JOIN agent_versions av ON av.pc_name = t.target_pc "
        "WHERE t.status = 'Pending' AND t.kind = $2 AND t.target_pc = ANY($1::text[])",
        (online_pcs, winget.KIND),
        fetch=True,
    )
    if not rows:
        return
    _allowed, closed = await modules.split_pcs("deploy", [r["target_pc"] for r in rows])
    if closed:
        await execute_query(
            "UPDATE tasks SET status = 'Denied', output = COALESCE(NULLIF(output, ''), '') || $3 "
            "WHERE status = 'Pending' AND kind = $2 AND target_pc = ANY($1::text[])",
            (closed, winget.KIND, winget.MODULE_CLOSED_OUTPUT),
        )
    old_agents = [r["target_pc"] for r in rows if r["target_pc"] not in closed and not winget.supports(r["features"])]
    if old_agents:
        await execute_query(
            "UPDATE tasks SET status = 'Denied', exit_code = $3, output = $4 "
            "WHERE status = 'Pending' AND kind = $2 AND target_pc = ANY($1::text[])",
            (old_agents, winget.KIND, winget.EXIT_UNSUPPORTED, winget.UNSUPPORTED_OUTPUT),
        )


async def _refuse_power(online_pcs: list) -> None:
    """Çevrimiçi cihazların bekleyen güç ve mesaj görevlerinden gönderilemeyecek olanlar (kuyruğun seçiminden ÖNCE):
      - ajan "power"/"message" duyurmadı ve eski komutla karşılığı yok (oturumu kapat, kilitle, mesaj; Linux'ta da):
        gönderilmeden "Denied", çıkış kodu -8 (eski ajan bilinmeyen iletiyi yok sayıp görevi "Running"de bırakırdı);
      - eski komutla gidecek kapatma/yeniden başlatma, uzak komut modülü kapalı bir laboratuvarda: "Denied";
      - ayrıntısı doğrulanamayan kayıt (elle bozulmadıkça olmaz): "Error"."""
    rows = await execute_query(
        "SELECT t.id, t.kind, t.payload, t.target_pc, av.features, c.platform FROM tasks t "
        "LEFT JOIN agent_versions av ON av.pc_name = t.target_pc LEFT JOIN clients c ON c.pc_name = t.target_pc "
        "WHERE t.status = 'Pending' AND t.kind = ANY($2::text[]) AND t.target_pc = ANY($1::text[])",
        (online_pcs, list(power.KINDS)),
        fetch=True,
    )
    if not rows:
        return
    unsupported, invalid, fallback = [], [], []
    for r in rows:
        try:
            spec = power.read_payload(r["kind"], r["payload"])
        except (ValueError, TypeError):
            invalid.append(r["id"])
            continue
        how = power.plan(r["kind"], spec, r["features"], r["platform"])
        if how == "unsupported":
            unsupported.append(r["id"])
        elif how == "fallback":
            fallback.append(r)
    if fallback:
        _allowed, closed = await modules.split_pcs("terminal", sorted({r["target_pc"] for r in fallback}))
        closed_ids = [r["id"] for r in fallback if r["target_pc"] in closed]
        if closed_ids:
            await execute_query(
                "UPDATE tasks SET status = 'Denied', output = $2 WHERE id = ANY($1::int[]) AND status = 'Pending'",
                (closed_ids, power.MODULE_CLOSED_OUTPUT),
            )
    if unsupported:
        await execute_query(
            "UPDATE tasks SET status = 'Denied', exit_code = $2, output = $3 WHERE id = ANY($1::int[]) "
            "AND status = 'Pending'",
            (unsupported, power.EXIT_UNSUPPORTED, power.UNSUPPORTED_OUTPUT),
        )
    if invalid:
        await execute_query(
            "UPDATE tasks SET status = 'Error', output = $2 WHERE id = ANY($1::int[]) AND status = 'Pending'",
            (invalid, power.INVALID_OUTPUT),
        )


async def _audit_power_dispatch(pc: str, task: dict, message: dict, actor: str) -> None:
    """Gönderilen güç komutu / mesaj: cihazın Kayıtlar'ına ve hash zincirli denetim kaydına. Mesaj metni ve not
    yazılmaz, uzunluğu ve ilk 60 karakteri yazılır."""
    spec = power.read_payload(task["kind"], task.get("payload"))
    is_power = task["kind"] == power.KIND_POWER
    via = "fallback" if message["action"] == "execute" else "native"
    if is_power:
        meta = {"task_id": task["id"], "created_by": task.get("created_by"), "op": spec["op"], "delay": spec["delay"],
                "via": via, "note": power.preview(spec["message"])}
        if via == "fallback":
            meta["raw_command"] = power.fallback_command(dict(spec, message=None), task.get("agent_platform"))
        label = "Güç komutu gönderildi: %s" % power.OP_TITLES[spec["op"]]
    else:
        meta = {"task_id": task["id"], "created_by": task.get("created_by"), "style": spec["style"],
                "requires_ack": spec["requires_ack"], "title": power.preview(spec["title"]),
                "text": power.preview(spec["text"])}
        label = "Kullanıcıya mesaj gönderildi"
    action = "power_command" if is_power else "user_message"
    await log_audit_event(
        pc,
        "Power" if is_power else "Message",
        label,
        actor_id=actor,
        event_type="device.power" if is_power else "device.message",
        category="system_maintenance",
        action=action,
        risk_level="info",
        meta_data=meta,
    )
    await add_audit_log(pc, action, "%s (kuyruk: %s)" % (label, actor), meta)
