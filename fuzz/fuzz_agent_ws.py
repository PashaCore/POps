#!/usr/bin/env python3
"""Fuzz hedefi: ajanın komut kanalı /ws/agent (docs/protocol/README.md).

Girdi, ajanın gönderdiği WebSocket metin çerçeveleridir (NUL baytıyla ayrılır). Sunucunun gerçek işleyicisi
(pops.routers.agents.websocket_agent) cihaz anahtarıyla doğrulanmış bir bağlantı gibi, sahte veritabanı ve sahte
soketle çalışır (Backend/tests/test_protocol.py'deki gibi). Her girdi iki bağlantıda denenir: çerçeveler bağlantının
ilk mesajlarıyken (kayıt yolu) ve geçerli bir ilk heartbeat'ten sonra (mesaj döngüsü); tek mesajlık tohumlar
(docs/protocol/examples) böylece iki yolu da çalıştırır. Her bağlantıda denetlenen:

  1. İşleyici dışarıya hata sızdırmaz.
  2. Bütün çerçeveler JSON nesnesiyse bağlantı kapanmaz ve hiçbiri "işlenemedi" sayılmaz: yanlış türdeki alan yok
     sayılır (README, "Rules for both sides"). Yalnızca JSON nesnesi olmayan çerçeve hatadır.
  3. Ajana giden her mesajın şeması var (docs/protocol/server-to-agent), zorunlu alanları taşır, belgelenmemiş alan
     taşımaz.
  4. Mesajdaki kimlikler yetki taşımaz: heartbeat, görev sonucu, yetenek, önizleme, dosya aktarımı sonucu, sınav
     durumu ve denetim kaydı yalnızca bağlantının cihazı adına.
  5. Veritabanına, denetim kaydına, bildirime ve toplu heartbeat yazımına giden her metin PostgreSQL'e yazılabilir
     (NUL yok, UTF-8). Toplu heartbeat yazımında tek bir bozuk satır bütün cihazların heartbeat'ini düşürür.
  6. İlk mesajdaki ad ve donanım bilgisi clients tablosuna sütununun türünde gider (yanlış türdeki alan yok sayılır;
     asyncpg yanlış türü reddeder ve el sıkışma düşerdi).

    python fuzz/fuzz_agent_ws.py -max_total_time=60 <yeni-girdiler-klasörü> fuzz/corpus/agent_ws \\
        docs/protocol/examples/agent-to-server
"""

import asyncio
import copy
import datetime
import glob
import json
import logging
import os
import re
import time
from types import SimpleNamespace

import common

with common.backend_imports():
    from pops import bypass, dna, exams, heartbeats, modules, update_tracking
    from pops.manager import manager
    from pops.routers import agents
    from pops.routers import files as file_router

from starlette.websockets import WebSocketDisconnect, WebSocketState  # noqa: E402

HW = "HW-3F9A1C7B2E4D"
SECRET = "fuzz-device-secret"
AGENT_VERSION = "0.1.22-alpha"
BYPASS_KEY = bypass.new_key()
MAX_FRAMES = 40
# Sunucunun kullandığı kapanış kodları (README, "Close codes")
CLOSE_CODES = {4401, 4409, 4000, 1011}

S2A = {}
for _path in glob.glob(os.path.join(common.REPO, "docs", "protocol", "server-to-agent", "*.json")):
    with open(_path, encoding="utf-8") as _f:
        S2A[os.path.basename(_path)[:-5]] = json.load(_f)

# Kayıtlı cihaz, bekleyen karantina, kapatılması istenen yetenekler, açık görevler: işleyicinin dalları çalışsın
_RULES = (
    ("SELECT * FROM clients WHERE pc_name", [{
        "pc_name": HW, "dna_uuid": "4C4C4544-0042-3510-8051-B4C04F4E3732", "dna_bios": "8Q5ZRB2",
        "dna_disk": "S4EVNF0M123456", "dna_mac": "00:1A:2B:3C:4D:5E", "dna_ram": "1A2B3C4D", "cap_ram_readable": True,
    }]),
    ("SELECT is_quarantined, pending_quarantine_action", [{"is_quarantined": True, "act": "lock", "reason": "Sınav"}]),
    ("SELECT cap_terminal_disable_requested", [{"t": True, "v": True}]),
    ("SELECT id, script_path, dispatched_at, agent_started_at FROM tasks", [
        {"id": 7, "script_path": "shutdown /r /t 0", "dispatched_at": datetime.datetime(2026, 10, 1, 8, 0),
         "agent_started_at": 1790000000.0},
        {"id": 8, "script_path": "ipconfig", "dispatched_at": datetime.datetime(2026, 10, 1, 8, 0),
         "agent_started_at": None},
    ]),
    ("UPDATE tasks SET output", lambda p: [{"id": p[1]}]),
    ("UPDATE agent_bypass_keys SET confirmed_at",
     lambda p: [{"pc_name": p[0]}] if p[1] == bypass.fingerprint(BYPASS_KEY) else []),
    ("SELECT cpu FROM hw_inventory", [{"cpu": "-"}]),
    # Açık dosya aktarımı ve cihazın sınıfında süren sınav (file_result, exam_state)
    ("UPDATE file_transfers SET status = $3", [{"direction": "push", "name": "Ödev föyü 3.pdf"}]),
    ("UPDATE file_transfers SET status = 'rejected'", [{"direction": "pull"}]),
    ("SELECT lab_name FROM clients WHERE pc_name = $1", [{"lab_name": "LAB-A"}]),
    ("FROM exam_sessions WHERE lab_name = $1 AND ended_at IS NULL", lambda p: [{
        "id": 31, "lab_name": "LAB-A", "allow_list": json.dumps(["okul.k12.tr"]), "message": "Sınav",
        "until_at": datetime.datetime.fromtimestamp(time.time() + 2400, datetime.timezone.utc),
        "block_apps": "[]", "reason": "Yazılı", "started_by": "admin",
        "started_at": datetime.datetime.fromtimestamp(time.time() - 600, datetime.timezone.utc),
        "ended_by": None, "ended_at": None, "end_reason": None}]),
    ("INSERT INTO exam_devices (exam_id, pc_name, reported_at", lambda p: [{
        "sent_at": datetime.datetime.fromtimestamp(time.time() - 300, datetime.timezone.utc), "denied_at": None,
        "since_sent": 300.0}]),
    ("UPDATE exam_devices SET left_at", [{"left_at": None}]),
)

# İlk mesajın clients satırı: sütun -> kabul edilen Python türleri (özellik 6)
_CLIENT_COLUMNS = {
    "pc_name": (str,), "hostname": (str,), "dna_uuid": (str, type(None)), "dna_bios": (str, type(None)),
    "dna_disk": (str, type(None)), "dna_mac": (str, type(None)), "dna_ram": (str, type(None)),
    "cap_ram_readable": (bool,),
}

# Sorgu metni -> cihaz kimliğinin parametre sırası (özellik 4)
_DEVICE_PARAM = {
    "UPDATE tasks SET output": 2,
    "UPDATE tasks SET status = 'Denied'": 1,
    "UPDATE clients SET cap_terminal_enabled": 2,
    "UPDATE clients SET running_version": 1,
    "UPDATE clients SET is_quarantined": 1,
    "UPDATE file_transfers SET status = $3": 1,
    "INSERT INTO exam_devices": 1,
    "UPDATE clients SET cap_files_enabled = FALSE": 0,
    "INSERT INTO agent_versions": 0,
}


def insert_params(query):
    """INSERT INTO t (a, b, ...) VALUES ($1, 'x', $3, ...): sütun adı -> parametre sırası."""
    m = re.search(r"INSERT INTO \w+ \(([^)]*)\)\s*VALUES \(([^)]*)\)", query)
    if not m:
        return {}
    cols = [c.strip() for c in m.group(1).split(",")]
    vals = [v.strip() for v in m.group(2).split(",")]
    return {c: int(v[1:]) - 1 for c, v in zip(cols, vals) if re.fullmatch(r"\$\d+", v)}


class Session:
    """Bir girdinin kayıtları: veritabanı çağrıları, denetim/bildirim çağrıları, heartbeat'ler, panel yayınları."""

    def __init__(self):
        self.db, self.audit, self.panels, self.admin_panels, self.beats, self.problems = [], [], [], [], [], []

    def reset(self):
        for store in (self.db, self.audit, self.panels, self.admin_panels, self.beats, self.problems):
            store.clear()

    def bad(self, value, where):
        text = common.bad_text(value)
        if text is not None:
            self.problems.append("%s: PostgreSQL'e yazılamayan metin %r" % (where, text[:80]))


S = Session()


async def fake_db(query, params=None, fetch=False):
    params = tuple(params or ())
    S.bad(params, "execute_query(%s)" % query[:50])
    S.db.append((query, params))
    for needle, value in _RULES:
        if needle in query:
            return value(params) if callable(value) else copy.deepcopy(value)
    return [] if fetch else True


def audit_recorder(name):
    async def fake(*args, **kwargs):
        S.bad((args, kwargs), name)
        S.audit.append((name, args, kwargs))

    return fake


def list_recorder(store):
    async def fake(message, *args):
        store.append(message)

    return fake


_real_record = heartbeats.record


def record_spy(pc_name, *args):
    _real_record(pc_name, *args)
    S.beats.append((pc_name, heartbeats._pending[pc_name]))


async def secret_ok(pc, secret):
    return pc == HW and secret == SECRET


async def no_token(token):
    return None


async def enforced():
    return True


async def fixed_bypass_key(pc_name):
    return BYPASS_KEY


for _mod in (agents, bypass, update_tracking, dna, file_router, exams, modules):
    _mod.execute_query = fake_db
agents.verify_agent_secret = secret_ok
agents.valid_enroll_token = no_token
agents.enforce_agent_auth_enabled = enforced
agents.add_audit_log = audit_recorder("add_audit_log")
agents.log_audit_event = audit_recorder("log_audit_event")
agents.notify = audit_recorder("notify")
agents.process_queue = audit_recorder("process_queue")
dna.add_audit_log = audit_recorder("dna.add_audit_log")
file_router.add_audit_log = audit_recorder("files.add_audit_log")
exams.add_audit_log = audit_recorder("exams.add_audit_log")
exams.notify = audit_recorder("notify")
bypass.key_to_send = fixed_bypass_key
heartbeats.record = record_spy
manager.broadcast_to_panels = list_recorder(S.panels)
manager.broadcast_to_admin_panels = list_recorder(S.admin_panels)


class Errors(logging.Handler):
    def __init__(self):
        super().__init__(logging.WARNING)
        self.records = []

    def emit(self, record):
        self.records.append("%s %s" % (record.getMessage(), getattr(record, "error", "")))


ERRORS = Errors()
logging.getLogger("pops.agents").addHandler(ERRORS)
logging.getLogger("pops.agents").setLevel(logging.WARNING)


class FakeWS:
    def __init__(self, frames):
        self.frames = list(frames)
        self.headers = {"X-Agent-Secret": SECRET, "X-Agent-Version": AGENT_VERSION}
        self.client = SimpleNamespace(host="10.0.0.7")
        self.sent = []
        self.closed = None
        self.application_state = WebSocketState.CONNECTED
        self.client_state = WebSocketState.CONNECTED

    async def accept(self):
        pass

    async def receive_text(self):
        if self.closed is not None or not self.frames:
            raise WebSocketDisconnect(code=1000)
        return self.frames.pop(0)

    async def send_text(self, text):
        self.sent.append(json.loads(text))

    async def close(self, code=1000, reason=None):
        self.closed = code
        self.application_state = WebSocketState.DISCONNECTED


def is_object(text):
    try:
        return isinstance(json.loads(text), dict)
    except (ValueError, RecursionError):
        return False


def check_sent(msg):
    name = "remote_input" if msg.get("type") == "remote_input" else msg.get("action")
    schema = S2A.get(name)
    assert schema is not None, "ajana belgelenmemiş mesaj gitti: %r" % msg
    missing = set(schema.get("required", [])) - set(msg)
    extra = set(msg) - set(schema.get("properties", {}))
    assert not missing and not extra, "%s: eksik %s, belgelenmemiş %s" % (name, sorted(missing), sorted(extra))


LOOP = asyncio.new_event_loop()


with open(os.path.join(common.REPO, "docs", "protocol", "examples", "agent-to-server", "heartbeat.first.json"),
          encoding="utf-8") as _f:
    FIRST = json.dumps(json.load(_f))


def TestOneInput(data):
    frames = [f.decode("utf-8", "replace") for f in data.split(b"\x00")[:MAX_FRAMES]]
    session(frames)
    session([FIRST] + frames)


def session(frames):
    S.reset()
    ERRORS.records.clear()
    for store in (manager.active_agents, manager.update_stages, manager.pending_thumbnails, agents._quarantine_resent,
                  dna._mismatch_seen):
        store.pop(HW, None)
    agents._isolation_warned.discard(HW)
    exams._resent.pop(HW, None)
    modules.invalidate()
    heartbeats._pending.clear()
    # Cihaza güncelleme gönderilmiş: update_progress adımları kabul edilir
    manager.pending_updates[HW] = ("0.1.23-alpha", time.time())
    ws = FakeWS(frames)

    LOOP.run_until_complete(agents.websocket_agent(ws, HW))   # 1: hata sızarsa atheris yakalar

    if all(is_object(f) for f in frames):
        assert ws.closed is None and not ERRORS.records, (
            "JSON nesnesi olan çerçeve hata sayıldı (kapanış %s): %s" % (ws.closed, ERRORS.records[:3]))
    assert ws.closed is None or ws.closed in CLOSE_CODES, "belgelenmemiş kapanış kodu %s" % ws.closed
    for msg in ws.sent:
        check_sent(msg)
    for pc, row in S.beats:
        assert pc == HW, "heartbeat başka cihaz adına: %s" % pc
        S.bad(row, "heartbeat")
    for query, params in S.db:
        if "INSERT INTO clients" in query:
            columns = insert_params(query)
            assert set(_CLIENT_COLUMNS) <= set(columns), "clients sütunları bulunamadı: %s" % sorted(columns)
            for col, types in _CLIENT_COLUMNS.items():
                assert isinstance(params[columns[col]], types), "clients.%s: %r" % (col, params[columns[col]])
        for needle, pos in _DEVICE_PARAM.items():
            if needle in query:
                # Tek cihaz ya da cihaz listesi (ör. sınavın toplu gönderimi)
                devices = params[pos] if isinstance(params[pos], (list, tuple)) else [params[pos]]
                assert devices and all(d == HW for d in devices), "%s başka cihaz adına: %r" % (needle, params[pos])
    for name, args, _kwargs in S.audit:
        if name != "process_queue":
            device = args[4] if name == "notify" else args[0]
            assert device == HW, "%s başka cihaz adına: %r" % (name, device)
    for msg in S.admin_panels:
        assert msg.get("type") != "thumbnail" or msg.get("hw_id") == HW, "önizleme başka cihaz adına"
        assert msg.get("pc_name", HW) == HW, "panele başka cihaz adına %s" % msg.get("type")
    for msg in S.panels:
        # Panelin cihazı okuduğu alan (Dashboard: vision.php, pops_script.js); vision_rejected'da cihaz yoksa panel
        # açık uzaktan bağlantıyı kapatır
        if msg.get("type") == "terminal_output":
            device = msg.get("id")
        elif msg.get("type") == "vision_rejected":
            device = msg.get("hw_id") or msg.get("pc_name") or msg.get("device")
        else:
            device = msg.get("pc_name", HW)
        assert device == HW, "panele başka cihaz adına %s: %r" % (msg.get("type"), device)
    assert not S.problems, S.problems[:3]


if __name__ == "__main__":
    common.run(TestOneInput)
