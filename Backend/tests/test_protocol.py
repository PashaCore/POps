"""Ajan ↔ sunucu protokolü (docs/protocol): şemalar, örnek mesajlar ve sunucu kodunun kurduğu / işlediği mesajlar.

Veritabanı ya da çalışan sunucu gerekmez:  python Backend/tests/test_protocol.py
Sunucunun gerçek uçları, kuyruğu ve WebSocket işleyicileri sahte veritabanı ve sahte soketlerle çalıştırılır; gönderilen
her mesaj şemasına uymalı ve yalnızca belgelenmiş alan taşımalı. jsonschema yalnızca bu test içindir (CI ayrıca kurar);
sunucunun çalışma bağımlılığı değildir.
"""

import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), os.pardir))
# Router modülleri yapılandırmayı içe aktarır; veritabanına bağlanılmaz
for _k in ("JWT_SECRET", "DB_USER", "DB_PASS", "DB_NAME"):
    os.environ.setdefault(_k, "unit-test")
import asyncio  # noqa: E402
import base64  # noqa: E402
import contextlib  # noqa: E402
import copy  # noqa: E402
import glob  # noqa: E402
import hashlib  # noqa: E402
import importlib.util  # noqa: E402
import json  # noqa: E402
import logging  # noqa: E402
import re  # noqa: E402
import tempfile  # noqa: E402
from types import SimpleNamespace  # noqa: E402

from jsonschema import Draft202012Validator  # noqa: E402
from jsonschema.exceptions import SchemaError  # noqa: E402
from starlette.websockets import WebSocketDisconnect, WebSocketState  # noqa: E402

BACKEND = os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir))
REPO = os.path.dirname(BACKEND)
PROTO = os.path.join(REPO, "docs", "protocol")
A2S, S2A = "agent-to-server", "server-to-agent"
CMD, VIS = "/ws/agent", "/ws/vision"
HW = "HW-3F9A1C7B2E4D"
SECRET = "EXAMPLE_device_secret_not_real_0123456789ab"
# Agent/POps.Tests/TestData/manifest.json(.sig) için tek kullanımlık test anahtarı (ReleaseVerifierTests.TestPublicKey)
TEST_PUBLIC_KEY = "9enOkSVQHBXaXoAurkfvUFBqSbfYbJQbMwL0zEIPTqQ="
TEST_MANIFEST = os.path.join(REPO, "Agent", "POps.Tests", "TestData", "manifest.json")

FAILS = []


def chk(cond, msg):
    print(("  OK   " if cond else "  FAIL ") + msg)
    if not cond:
        FAILS.append(msg)


def _load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def _read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


SCHEMAS = {
    d: {os.path.basename(p)[:-5]: _load(p) for p in sorted(glob.glob(os.path.join(PROTO, d, "*.json")))}
    for d in (A2S, S2A)
}
MANIFEST_SCHEMA = _load(os.path.join(PROTO, "release-manifest.json"))
README = _read(os.path.join(PROTO, "README.md"))


def examples(direction):
    return [
        (os.path.basename(p), _load(p))
        for p in sorted(glob.glob(os.path.join(PROTO, "examples", direction, "*.json")))
    ]


def example(direction, name):
    return _load(os.path.join(PROTO, "examples", direction, name + ".json"))


def message_name(direction, msg):
    """Mesajın şema adı: ajandan gelende type (yoksa status taşıyan heartbeat), sunucudan gidende action (remote_input
    istisnası type taşır)."""
    if not isinstance(msg, dict):
        return None
    if direction == A2S:
        return msg.get("type") or ("heartbeat" if "status" in msg else None)
    return "remote_input" if msg.get("type") == "remote_input" else msg.get("action")


def validation_errors(schema, msg):
    return sorted(e.message for e in Draft202012Validator(schema).iter_errors(msg))


def undocumented(schema, msg, prefix=""):
    """Şemanın properties'inde olmayan anahtarlar (iç içe nesnelerde de). Şemalar bilinçli olarak açıktır (alıcı
    bilmediği alanı yok sayar); gönderen ise yalnızca belgelenmiş alan gönderir."""
    props = schema.get("properties")
    if not isinstance(msg, dict) or props is None:
        return []
    out = []
    for key, value in msg.items():
        if key not in props:
            out.append(prefix + key)
        else:
            out += undocumented(props[key], value, prefix + key + ".")
    return out


def check_message(direction, msg, origin):
    name = message_name(direction, msg)
    schema = SCHEMAS[direction].get(name)
    if schema is None:
        chk(False, "%s: %s için şema var (%s)" % (origin, direction, name))
        return
    problems = validation_errors(schema, msg)[:3]
    extra = undocumented(schema, msg)
    if extra:
        problems.append("belgelenmemiş alan: %s" % ", ".join(extra))
    detail = (" -> " + "; ".join(problems)) if problems else ""
    chk(not problems, "%s: %s/%s.json%s" % (origin, direction, name, detail))


def walk_properties(schema, path=""):
    for key, sub in (schema.get("properties") or {}).items():
        yield path + key, sub
        yield from walk_properties(sub, path + key + ".")
        if isinstance(sub.get("items"), dict):
            yield from walk_properties(sub["items"], path + key + "[].")


# ------------------------------------------------------------------------------------------- şemalar ve örnekler


def test_schemas():
    print("== şemalar")
    for direction, schemas in SCHEMAS.items():
        chk(len(schemas) > 0, "%s: şema dosyaları bulundu (%d)" % (direction, len(schemas)))
        for name, schema in schemas.items():
            where = "%s/%s.json" % (direction, name)
            try:
                Draft202012Validator.check_schema(schema)
                valid = schema.get("$schema") == "https://json-schema.org/draft/2020-12/schema"
            except SchemaError as e:
                valid = False
                print("        %s" % e.message)
            chk(valid, "%s: geçerli draft 2020-12 şeması" % where)
            props = schema.get("properties") or {}
            if direction == S2A and name != "remote_input":
                key = "action"
            else:
                key = "type"
            chk(props.get(key, {}).get("const") == name and schema.get("title") == name,
                "%s: %s sabiti ve başlık dosya adıyla aynı" % (where, key))
            chk(set(schema.get("required", [])) <= set(props), "%s: zorunlu alanların hepsi tanımlı" % where)
            missing = [p for p, sub in walk_properties(schema) if not sub.get("description")]
            chk(bool(schema.get("description")) and not missing, "%s: her alanın açıklaması var %s" % (where, missing))
            channels = schema.get("x-pops-channels") or []
            chk(bool(channels) and set(channels) <= {CMD, VIS}, "%s: kanal(lar) belirtilmiş: %s" % (where, channels))
            chk("[%s](%s/%s.json)" % (name, direction, name) in README, "%s: README tablosunda" % where)
    try:
        Draft202012Validator.check_schema(MANIFEST_SCHEMA)
        chk(True, "release-manifest.json: geçerli şema")
    except SchemaError as e:
        chk(False, "release-manifest.json: geçerli şema (%s)" % e.message)


def test_examples():
    print("== örnek mesajlar (ortak test vektörleri)")
    for direction in (A2S, S2A):
        seen = set()
        for fname, msg in examples(direction):
            prefix = fname.split(".")[0]
            seen.add(prefix)
            schema = SCHEMAS[direction].get(prefix)
            if schema is None:
                chk(False, "examples/%s/%s: '%s' şeması yok" % (direction, fname, prefix))
                continue
            chk(message_name(direction, msg) == prefix,
                "examples/%s/%s: mesaj adı dosya adıyla aynı" % (direction, fname))
            check_message(direction, msg, "examples/%s/%s" % (direction, fname))
            # Şema boş değil: zorunlu alanlardan biri eksikse mesaj geçersiz
            vacuous = [r for r in schema.get("required", [])
                       if not validation_errors(schema, {k: v for k, v in msg.items() if k != r})]
            chk(not vacuous, "examples/%s/%s: zorunlu alan eksilince geçersiz %s" % (direction, fname, vacuous))
            chk("examples/%s/%s" % (direction, fname) in README, "examples/%s/%s: README'de bağlı" % (direction, fname))
        chk(seen == set(SCHEMAS[direction]), "%s: her şemanın örneği var (eksik: %s)"
            % (direction, sorted(set(SCHEMAS[direction]) - seen)))
    for direction in (A2S, S2A):
        msg = _load(os.path.join(PROTO, "examples", "unknown", direction + ".json"))
        chk(message_name(direction, msg) not in SCHEMAS[direction],
            "examples/unknown/%s: hiçbir şemaya ait değil" % direction)

    # Tür hataları geçersiz (şemalar türü gerçekten denetliyor)
    result = example(A2S, "result.completed")
    chk(bool(validation_errors(SCHEMAS[A2S]["result"], dict(result, task_id="1042"))), "result: metin task_id geçersiz")
    hb = example(A2S, "heartbeat.minimal")
    chk(bool(validation_errors(SCHEMAS[A2S]["heartbeat"], dict(hb, type="result"))), "heartbeat: başka type geçersiz")
    chk(bool(validation_errors(SCHEMAS[S2A]["set_capabilities"], {"action": "set_capabilities"})),
        "set_capabilities: en az bir yetenek gerekir")

    print("== update_agent vektörü: test anahtarıyla imzalı manifest")
    from cryptography.exceptions import InvalidSignature
    from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PublicKey

    msg = example(S2A, "update_agent")
    raw = base64.b64decode(msg["manifest"])
    with open(TEST_MANIFEST, "rb") as f:
        chk(raw == f.read(), "manifest, Agent/POps.Tests/TestData/manifest.json ile bayt bayt aynı")
    check_message_manifest(raw, "update_agent örneği")
    key = Ed25519PublicKey.from_public_bytes(base64.b64decode(TEST_PUBLIC_KEY))
    try:
        key.verify(base64.b64decode(msg["manifest_sig"]), raw)
        chk(True, "imza test anahtarıyla doğrulanıyor")
    except InvalidSignature:
        chk(False, "imza test anahtarıyla doğrulanıyor")
    try:
        key.verify(base64.b64decode(msg["manifest_sig"]), raw.replace(b"0.1.3-alpha", b"0.1.4-alpha"))
        chk(False, "değiştirilmiş manifest reddediliyor")
    except InvalidSignature:
        chk(True, "değiştirilmiş manifest reddediliyor")
    verifier_tests = _read(os.path.join(REPO, "Agent", "POps.Tests", "Agent", "ReleaseVerifierTests.cs"))
    chk(TEST_PUBLIC_KEY in verifier_tests and TEST_PUBLIC_KEY in README,
        "test anahtarı ajan testleriyle ve README'yle aynı")

    from pops import bypass

    chk(bypass.fingerprint(example(S2A, "set_bypass_secret")["secret"])
        == example(A2S, "bypass_secret_ack")["fingerprint"],
        "bypass_secret_ack parmak izi set_bypass_secret anahtarının")


def check_message_manifest(raw, origin):
    try:
        manifest = json.loads(raw.decode("utf-8"))
    except ValueError:
        chk(False, "%s: manifest JSON" % origin)
        return
    problems = validation_errors(MANIFEST_SCHEMA, manifest)
    chk(not problems, "%s: release-manifest.json'a uyuyor %s" % (origin, problems[:2]))


# --------------------------------------------------------------------------------------- kod ile şemalar eşleşiyor


def test_code_matches_schemas():
    print("== sunucu kodu ile şemalar")
    sources = sorted(glob.glob(os.path.join(BACKEND, "pops", "**", "*.py"), recursive=True))
    sources.append(os.path.join(BACKEND, "system_routes.py"))
    sent, sub_commands = set(), set()
    for path in sources:
        text = _read(path)
        sent |= set(re.findall(r'_patch_command\(data, auth, "([a-z_]+)"', text))
        for line in text.splitlines():
            found = set(re.findall(r'"action":\s*"([a-z_]+)"', line))
            if '"type": "remote_input"' in line:
                # Panel mesajı biçimi: action, remote_input'un alt komutu (get_thumbnail)
                sent.add("remote_input")
                sub_commands |= found
            else:
                sent |= found
    chk(sub_commands <= set(SCHEMAS[S2A]["remote_input"]["properties"]["action"]["enum"]),
        "remote_input alt komutları şemada: %s" % sorted(sub_commands))
    chk(sent <= set(SCHEMAS[S2A]), "sunucunun gönderdiği her action'ın şeması var (eksik: %s)"
        % sorted(sent - set(SCHEMAS[S2A])))
    unsent = {n for n, s in SCHEMAS[S2A].items() if not s.get("deprecated")} - sent
    chk(not unsent, "kullanımdan kalkmamış her sunucu mesajını kod gönderiyor (göndermeyen: %s)" % sorted(unsent))
    chk(all(SCHEMAS[S2A][n].get("deprecated") for n in set(SCHEMAS[S2A]) - sent),
        "kodun göndermediği şemalar deprecated işaretli")

    agents_src = _read(os.path.join(BACKEND, "pops", "routers", "agents.py"))
    control_src = _read(os.path.join(BACKEND, "pops", "routers", "control.py"))
    handled = set(re.findall(r'(?:payload|pld)\.get\("type"\) == "([a-z_]+)"', agents_src))
    for group in re.findall(r'payload\.get\("type"\) in \[([^\]]*)\]', control_src):
        handled |= set(re.findall(r'"([a-z_]+)"', group))
    handled.add("heartbeat")   # type'sız; status ile tanınır (agents.py handle_routine_payload)
    chk(handled == set(SCHEMAS[A2S]), "sunucunun işlediği her type'ın şeması var ve her şema işleniyor (fark: %s)"
        % sorted(handled ^ set(SCHEMAS[A2S])))

    from pops.routers import agents

    features = SCHEMAS[S2A]["server_info"]["properties"]["features"]["description"]
    for f in agents.SERVER_FEATURES:
        chk("| `%s` |" % f in README and f in features, "özellik '%s' README'de ve server_info şemasında" % f)
    chk("protocol %d" % agents.PROTOCOL_VERSION in README.lower(), "README protokol sürümünü söylüyor")


# ----------------------------------------------------------------------------------------- sahte veritabanı ve soket


_MISSING = object()


class Patches:
    """Modül ve nesne özniteliklerini geçici olarak değiştirir; restore() eskilerini geri koyar."""

    def __init__(self):
        self._saved = []

    def set(self, obj, name, value):
        own = name in vars(obj)
        self._saved.append((obj, name, vars(obj)[name] if own else _MISSING))
        setattr(obj, name, value)

    def restore(self):
        for obj, name, old in reversed(self._saved):
            if old is _MISSING:
                delattr(obj, name)
            else:
                setattr(obj, name, old)
        self._saved.clear()


class FakeDB:
    """execute_query yerine: sorgular kaydedilir; yanıt, sorgu metninde geçen ilk kuralın değeri (ya da params alan
    işlevin sonucu). Kural yoksa fetch=True için [], aksi hâlde True."""

    def __init__(self, rules=()):
        self.rules = list(rules)
        self.calls = []

    async def __call__(self, query, params=None, fetch=False):
        params = tuple(params or ())
        self.calls.append((query, params))
        for needle, value in self.rules:
            if needle in query:
                return value(params) if callable(value) else copy.deepcopy(value)
        return [] if fetch else True

    def params(self, needle):
        return [p for q, p in self.calls if needle in q]


class FakeWS:
    """Sunucu tarafındaki WebSocket. incoming: sırayla alınacak mesajlar (sözlük, metin ya da onları döndüren
    işlev); bitince WebSocketDisconnect(1000). Gönderilenler sent'te (JSON olarak çözülmüş)."""

    def __init__(self, incoming=(), headers=None, on_send=None):
        self.incoming = list(incoming)
        self.headers = dict(headers or {})
        self.client = SimpleNamespace(host="10.0.0.7")
        self.sent = []
        self.closed = None
        self.on_send = on_send
        self.application_state = WebSocketState.CONNECTED
        self.client_state = WebSocketState.CONNECTED

    async def accept(self):
        pass

    async def receive_text(self):
        if self.closed is not None or not self.incoming:
            raise WebSocketDisconnect(code=1000)
        item = self.incoming.pop(0)
        item = item() if callable(item) else item
        return item if isinstance(item, str) else json.dumps(item, ensure_ascii=False)

    async def send_text(self, text):
        msg = json.loads(text)
        self.sent.append(msg)
        if self.on_send:
            self.on_send(msg)

    async def close(self, code=1000, reason=None):
        self.closed = (code, reason)
        self.application_state = WebSocketState.DISCONNECTED


def recorder(store, result=None):
    async def fake(*args, **kwargs):
        store.append((args, kwargs))
        return result

    return fake


class Captured(logging.Handler):
    def __init__(self):
        super().__init__(logging.WARNING)
        self.records = []

    def emit(self, record):
        self.records.append(record)


def channel_examples(channel):
    return [(f, m) for f, m in examples(A2S) if channel in SCHEMAS[A2S][f.split(".")[0]]["x-pops-channels"]]


# ------------------------------------------------------------------------------- /ws/agent: gerçek işleyici, uçtan uca


async def agent_session_with_secret():
    print("== /ws/agent: cihaz anahtarıyla bağlantı, bütün ajan örnekleri gerçek işleyiciden geçer")
    from pops import bypass, heartbeats, secretbox, update_tracking
    from pops.manager import manager
    from pops.routers import agents

    P = Patches()
    keys = {}

    def store_key(params):
        keys.setdefault("sealed", params[1])
        return True

    def confirm_key(params):
        return [{"pc_name": params[0]}] if params[1] == bypass.fingerprint(secretbox.unseal(keys["sealed"])) else []

    db = FakeDB([
        ("INSERT INTO agent_bypass_keys", store_key),
        ("SELECT secret, confirmed_at FROM agent_bypass_keys",
         lambda p: [{"secret": keys["sealed"], "confirmed_at": None}]),
        ("UPDATE agent_bypass_keys SET confirmed_at", confirm_key),
        ("SELECT is_quarantined, pending_quarantine_action",
         [{"is_quarantined": True, "act": "lock", "reason": "Sınav"}]),
        ("SELECT cap_terminal_disable_requested", [{"t": True, "v": False}]),
        ("UPDATE tasks SET output", lambda p: [{"id": p[1]}]),
    ])
    audits, events, notes, beats, panels, admin_panels, queue = [], [], [], [], [], [], []
    for mod in (agents, bypass, update_tracking):
        P.set(mod, "execute_query", db)

    async def secret_ok(pc, secret):
        return pc == HW and secret == SECRET

    async def no_token(token):
        return None

    async def enforced():
        return True

    async def known(*a):
        return False

    P.set(agents, "verify_agent_secret", secret_ok)
    P.set(agents, "valid_enroll_token", no_token)
    P.set(agents, "enforce_agent_auth_enabled", enforced)
    P.set(agents, "check_known_device", known)
    P.set(agents, "add_audit_log", recorder(audits))
    P.set(agents, "log_audit_event", recorder(events))
    P.set(agents, "notify", recorder(notes))
    P.set(agents, "process_queue", recorder(queue))
    P.set(heartbeats, "record", lambda *a: beats.append(a))
    P.set(manager, "broadcast_to_panels", recorder(panels))
    P.set(manager, "broadcast_to_admin_panels", recorder(admin_panels))
    agents._quarantine_resent.pop(HW, None)
    agents._isolation_warned.discard(HW)
    log = Captured()
    logging.getLogger("pops.agents").addHandler(log)

    cmd = channel_examples(CMD)
    first = example(A2S, "heartbeat.first")
    rest = [m for f, m in cmd if f != "heartbeat.first.json"]
    other_thumb = dict(example(A2S, "thumbnail"), hw_id="HW-000000000000")
    future_result = dict(example(A2S, "result.completed"), task_id=1099, future_field={"nested": [1, 2]})
    sent_key = {}

    def dynamic_ack():
        return {"type": "bypass_secret_ack", "fingerprint": bypass.fingerprint(sent_key["secret"])}

    def remember_key(msg):
        if msg.get("action") == "set_bypass_secret":
            sent_key["secret"] = msg["secret"]

    unknown = _load(os.path.join(PROTO, "examples", "unknown", A2S + ".json"))
    ws = FakeWS([first] + rest + [other_thumb, future_result, dynamic_ack, unknown],
                headers={"X-Agent-Secret": SECRET, "X-Agent-Version": "0.1.21-alpha"}, on_send=remember_key)
    try:
        await agents.websocket_agent(ws, HW)
    finally:
        logging.getLogger("pops.agents").removeHandler(log)
        P.restore()
        manager.active_agents.pop(HW, None)
        manager.pending_updates.pop(HW, None)

    chk(ws.closed is None and not ws.incoming, "bağlantı kapatılmadı, bütün mesajlar okundu")
    chk(not log.records, "hiçbir mesaj 'işlenemedi' uyarısı vermedi %s" % [r.getMessage() for r in log.records][:3])
    for m in ws.sent:
        check_message(S2A, m, "sunucu→ajan %s" % message_name(S2A, m))
    actions = [m.get("action") for m in ws.sent]
    chk(actions[:3] == ["server_info", "set_bypass_secret", "get_hardware"],
        "kayıttan sonra sırayla server_info, set_bypass_secret, get_hardware: %s" % actions[:3])
    info = ws.sent[0]
    chk(info.get("protocol") == agents.PROTOCOL_VERSION == 1 and info.get("features") == list(agents.SERVER_FEATURES),
        "server_info protokol 1 ve özellikleri duyuruyor")
    chk({"action": "lockdown", "reason": "Sınav"} in ws.sent, "bekleyen karantina heartbeat'e göre yeniden gönderildi")
    acks = sorted(m["task_id"] for m in ws.sent if m.get("action") == "result_ack")
    results = sorted(m["task_id"] for f, m in cmd if f.startswith("result."))
    chk(acks == sorted(results + [1099]), "her result (bilinmeyen alanlısı da) onaylandı: %s" % acks)
    with_id = sorted(m["result_id"] for f, m in cmd if f.startswith("update_result.") and "result_id" in m)
    uacks = sorted(m["result_id"] for m in ws.sent if m.get("action") == "update_result_ack")
    chk(uacks == with_id, "result_id'li update_result'lar onaylandı, result_id'siz onaylanmadı")
    chk({"action": "set_capabilities", "terminal_enabled": False} in ws.sent,
        "kapatılması istenen ama açık bildirilen yetenek için set_capabilities yeniden gönderildi")
    chk(len(ws.sent) == 3 + 1 + len(acks) + len(uacks) + 1, "başka mesaj gönderilmedi (bilinmeyen type yanıtsız)")

    hb_count = sum(1 for f, _ in cmd if f.startswith("heartbeat."))
    chk(len(beats) == hb_count, "her heartbeat (type'sız ve type'lı) kaydedildi: %d" % len(beats))
    chk(beats and beats[0][0] == HW and beats[0][2] == "Online" and beats[0][4] == "LAB1-PC07"
        and json.loads(beats[0][6]).get("started_at") == first["agent_health"]["started_at"],
        "ilk heartbeat: durum, ad ve agent_health saklandı")
    stored = {p[1]: p for p in db.params("UPDATE tasks SET output")}
    chk(stored.get(1044, (None,) * 5)[3] == -5 and stored.get(1045, (None,) * 5)[3] is None
        and stored.get(1042, (None,) * 5)[3] == 0, "result çıkış kodları olduğu gibi yazıldı (yoksa NULL)")
    chk(all(p[2] == HW for p in stored.values()), "sonuç yalnızca bağlantının cihazına yazıldı")
    caps = db.params("UPDATE clients SET cap_terminal_enabled")
    chk((True, True, HW, "system") in caps and (False, True, HW, "custom") in caps, "capabilities saklandı")
    chk((1044, HW) in db.params("UPDATE tasks SET status = 'Denied'"),
        "capability_denied task_id'li görevi Denied yaptı")
    audit_actions = [a[0][1] for a in audits]
    chk(audit_actions.count("update_result") == len([f for f, _ in cmd if f.startswith("update_result.")]),
        "her update_result denetim kaydına yazıldı")
    chk("bypass_key" in audit_actions and "bypass_key_mismatch" in audit_actions,
        "doğru parmak izi anahtarı onayladı, yanlışı (örnek) uyumsuzluk olarak kaydedildi")
    chk("quarantine_partial" in audit_actions, "yalıtımsız karantina bildirimi (heartbeat.typed) kaydedildi")
    denied = [e for e in events if e[1].get("event_type") == "agent.capability_denied"]
    chk(len(denied) == len([f for f, _ in cmd if f.startswith("capability_denied.")]),
        "her capability_denied denetlendi")
    thumbs = [a[0][0] for a in admin_panels if a[0][0].get("type") == "thumbnail"]
    chk(len(thumbs) == 2 and all(t["hw_id"] == HW for t in thumbs),
        "thumbnail yalnızca admin panellerine, hw_id bağlantının cihazıyla değiştirilerek")
    kinds = [a[0][0].get("type") for a in panels]
    chk({"terminal_output", "update_result", "capabilities", "capability_denied", "vision_rejected"} <= set(kinds),
        "sonuç, güncelleme, yetenek, ret ve vision_rejected panellere iletildi")


async def agent_session_with_enroll_token():
    print("== /ws/agent: kayıt jetonuyla ilk bağlantı (set_secret)")
    from pops import db as dbmod
    from pops.manager import manager
    from pops.routers import agents

    P = Patches()
    db = FakeDB()
    conn_calls = []

    class Conn:
        async def execute(self, query, *args):
            conn_calls.append((query, args))

        async def fetchval(self, query, *args):
            conn_calls.append((query, args))
            return None

        async def fetchrow(self, query, *args):
            conn_calls.append((query, args))
            return {"lab_name": "LAB-A"}

    @contextlib.asynccontextmanager
    async def transaction():
        yield Conn()

    async def no_secret(pc, secret):
        return False

    async def token(value):
        return {"id": 7, "lab_name": "LAB-A"} if value == "enroll-token-0123" else None

    async def enforced():
        return True

    async def same_id(claimed, dna, ip, ws):
        return claimed

    P.set(agents, "execute_query", db)
    P.set(dbmod, "transaction", transaction)
    P.set(agents, "verify_agent_secret", no_secret)
    P.set(agents, "valid_enroll_token", token)
    P.set(agents, "enforce_agent_auth_enabled", enforced)
    P.set(agents, "reconcile_device", same_id)
    for name in ("add_audit_log", "log_audit_event", "notify", "process_queue"):
        P.set(agents, name, recorder([]))
    ws = FakeWS([example(A2S, "heartbeat.first")],
                headers={"X-Enroll-Token": "enroll-token-0123", "X-Agent-Version": "0.1.21-alpha"})
    try:
        await agents.websocket_agent(ws, HW)
    finally:
        P.restore()
        manager.active_agents.pop(HW, None)
    for m in ws.sent:
        check_message(S2A, m, "sunucu→ajan %s" % message_name(S2A, m))
    actions = [m.get("action") for m in ws.sent]
    chk(actions == ["set_secret", "server_info", "get_hardware"],
        "sırayla set_secret, server_info, get_hardware; set_bypass_secret yok (jetonlu bağlantı): %s" % actions)
    secret = ws.sent[0].get("secret", "")
    hashes = [a[1] for q, a in conn_calls if "INSERT INTO agent_secrets" in q]
    chk(bool(hashes) and hashes[0] == hashlib.sha256(secret.encode()).hexdigest(),
        "veritabanına anahtarın yalnızca SHA-256'sı yazıldı")
    chk(("LAB-A", HW) in db.params("UPDATE clients SET lab_name"), "cihaz jetonun sınıfına alındı")


async def identity_reassignment():
    print("== set_identity (pops/dna.py): kopya kurulum ve tanınan donanım")
    from pops import dna

    first = example(A2S, "heartbeat.first")["dna_payload"]
    other = {"pc_name": HW, "dna_uuid": "11111111-2222-3333-4444-555555555555", "dna_bios": "OTHERBIOS",
             "dna_disk": "OTHERDISK", "dna_mac": "00:11:22:33:44:55", "dna_ram": "OTHERRAM", "cap_ram_readable": True}
    hw = first["hardware"]
    same = {"pc_name": "HW-9B41D07E5A2C", "dna_uuid": hw["uuid"], "dna_bios": hw["bios_sn"], "dna_disk": hw["disk_sn"],
            "dna_mac": hw["mac"], "dna_ram": hw["ram_sn"], "cap_ram_readable": True}
    P = Patches()
    P.set(dna, "add_audit_log", recorder([]))
    try:
        P.set(dna, "execute_query", FakeDB([("SELECT * FROM clients WHERE pc_name = $1", [other])]))
        ws = FakeWS()
        new_id = await dna.reconcile_device(HW, first, "10.0.0.7", ws)
        chk(new_id != HW and ws.sent == [{"action": "set_identity", "new_hw_id": new_id}],
            "başka donanımdaki kimliğe yeni kimlik verildi")
        for m in ws.sent:
            check_message(S2A, m, "set_identity (kopya)")
        P.set(dna, "execute_query", FakeDB([("WHERE ($1::text IS NOT NULL", [same])]))
        ws = FakeWS()
        found = await dna.reconcile_device("HW-AAAAAAAAAAAA", first, "10.0.0.7", ws)
        chk(found == same["pc_name"] and ws.sent == [{"action": "set_identity", "new_hw_id": found}],
            "yeniden kurulan bilgisayar eski kimliğini geri aldı")
        for m in ws.sent:
            check_message(S2A, m, "set_identity (kurtarma)")
    finally:
        P.restore()


async def vision_channel():
    print("== /ws/vision: kareler oturum sahiplerine bu cihazın kimliğiyle iletilir")
    from pops.manager import manager
    from pops.routers import control

    P = Patches()
    forwarded = []

    async def secret_ok(pc, secret):
        return pc == HW and secret == SECRET

    async def to_viewers(message, pc):
        forwarded.append((message, pc))

    P.set(control, "verify_agent_secret", secret_ok)
    P.set(control, "add_audit_log", recorder([]))
    P.set(manager, "send_frame_to_viewers", to_viewers)
    vis = [m for _, m in channel_examples(VIS)]
    unknown = _load(os.path.join(PROTO, "examples", "unknown", A2S + ".json"))
    # Başka bir cihazın kimliğiyle gönderilen kare de bu tünelin cihazına yazılmalı
    spoofed = dict(vis[0], hw_id="HW-BASKACIHAZ1")
    ws = FakeWS(vis + [spoofed, unknown], headers={"X-Agent-Secret": SECRET})
    try:
        await control.websocket_vision(ws, HW)
    finally:
        P.restore()
        manager.active_vision_ws.pop(HW, None)
    expected = [dict(m, hw_id=HW) for m in vis]
    chk([m for m, _ in forwarded][:len(vis)] == expected and all(pc == HW for _, pc in forwarded),
        "stream_frame ve thumbnail bu cihazın kimliğiyle iletildi, bilinmeyen type atıldı")
    chk(len(forwarded) == len(vis) + 1 and forwarded[-1][0].get("hw_id") == HW,
        "başka cihaz kimliğiyle gelen kare bu tünelin cihazına yazıldı (sahte kutu yok)")
    chk(ws.closed is None and not ws.sent, "Vision kanalında sunucu yanıt göndermez")


# ------------------------------------------------------------------------- sunucunun kurduğu komutlar (gerçek kod)


async def server_builders():
    print("== sunucunun kurduğu komutlar")
    import system_routes
    from pops import modules, taskqueue, wol
    from pops.manager import manager
    from pops.models import LockdownInput, PatchInstallInput, RemoteInputData, StartAuditSessionInput, StreamStopInput
    from pops.models import TaskActionInput
    from pops.routers import agents, control, inventory, tasks as tasks_router
    from pops.routers import modules as modules_router

    P = Patches()
    agent_ws, vision_ws = FakeWS(), FakeWS()
    auth = {"sub": "admin", "role": "admin"}

    async def all_open(module_id, pcs):
        return list(pcs), []

    async def module_on(*a, **k):
        return None

    P.set(modules, "split_pcs", all_open)
    P.set(modules, "check", module_on)
    manager.active_agents[HW] = agent_ws
    built = []   # (kaynak, mesaj)

    def take(ws, origin):
        built.extend((origin, m) for m in ws.sent)
        ws.sent.clear()

    try:
        built.append(("agents.server_info_message", agents.server_info_message()))

        P.set(taskqueue, "execute_query", FakeDB([
            ("SELECT 1 FROM tasks WHERE status = 'Pending' LIMIT 1", [{"x": 1}]),
            ("COUNT(DISTINCT target_pc)", [{"c": 0}]),
            ("SELECT * FROM (", [{"id": 1042, "target_pc": HW, "script_path": "ipconfig /all", "created_by": "admin",
                                  "created_at": "2026-10-05 10:00:00"}]),
        ]))
        P.set(taskqueue, "log_audit_event", recorder([]))
        P.set(taskqueue, "add_audit_log", recorder([]))
        await taskqueue._process_queue_once()
        chk([m.get("action") for m in agent_ws.sent] == ["execute"] and agent_ws.sent[0]["requested_by"] == "admin",
            "taskqueue: execute, isteyen kullanıcıyla")
        take(agent_ws, "taskqueue._process_queue_once")

        P.set(tasks_router, "execute_query", FakeDB([("WITH target AS", [
            {"id": 1042, "target_pc": HW, "old_status": "Running"}])]))
        await tasks_router.handle_task_action(TaskActionInput(action="CANCEL", target_mode="TASK", target_id="1042"),
                                              auth, None)
        chk(agent_ws.sent == [{"action": "cancel_task", "task_id": 1042}], "görev iptali: cancel_task")
        take(agent_ws, "tasks.handle_task_action")

        P.set(control, "execute_query", FakeDB([
            ("SELECT id FROM users", [{"id": 1}]),
            ("SELECT is_quarantined FROM clients", [{"is_quarantined": False}]),
        ]))
        for name in ("log_audit_event", "add_audit_log", "notify"):
            P.set(control, name, recorder([]))
        await control.lockdown_pc(LockdownInput(target_pc=HW, reason="Sınav sırasında yasaklı site"), auth)
        await control.unlock_pc(LockdownInput(target_pc=HW, reason="Sınav bitti"), auth)
        chk([m.get("action") for m in agent_ws.sent] == ["lockdown", "unlock"], "karantina: lockdown ve unlock")
        take(agent_ws, "control.lockdown_pc / unlock_pc")
        for mandatory in (False, True):
            await control.start_audit_session(
                StartAuditSessionInput(target_pc=HW, reason="Güvenlik incelemesi", is_mandatory=mandatory), auth)
        chk([m.get("countdown_seconds") for m in agent_ws.sent] == [0, 30], "uzaktan oturum: rızalı 0, zorunlu 30 sn")
        take(agent_ws, "control.start_audit_session")
        await control.stop_stream(StreamStopInput(pc_name=HW), auth)
        take(agent_ws, "control.stop_stream")

        def answer_preview(msg):
            for fut in manager.pending_thumbnails.get(HW, []):
                if not fut.done():
                    fut.set_result("IMG")

        agent_ws.on_send = answer_preview
        got = await control.get_thumbnail(HW, auth)
        agent_ws.on_send = None
        chk(got.get("status") == "success", "önizleme isteği yanıtlandı")
        take(agent_ws, "control.get_thumbnail")
        manager.active_vision_ws[HW] = vision_ws
        for fields in (
            {"input_type": "mouse_move", "x": 960, "y": 540, "relative": False},
            {"input_type": "mouse_click", "button": "left", "is_down": True, "double": False},
            {"input_type": "mouse_wheel", "delta": -120, "horizontal": False},
            {"input_type": "keyboard", "key": "ş", "code": "Semicolon", "ctrl": False, "alt": False, "shift": False,
             "meta": False, "altgr": False, "is_down": True, "action": "execute"},
        ):
            await control.send_remote_input(RemoteInputData(device=HW, **fields), auth)
        chk(len(vision_ws.sent) == 4 and not agent_ws.sent and all("action" not in m for m in vision_ws.sent),
            "uzaktan girdi açık Vision kanalından, yalnızca belgelenmiş alanlarla")
        take(vision_ws, "control.send_remote_input")
        manager.active_vision_ws.pop(HW, None)

        P.set(modules_router, "execute_query", FakeDB([("WHERE lab_name = $1", [{"pc_name": HW}])]))
        await modules_router._close_effects({("vision", "LAB-A"): True}, {("vision", "LAB-A"): False})
        chk(agent_ws.sent == [{"action": "stop_stream"}], "Vision modülü kapanınca stop_stream")
        take(agent_ws, "modules._close_effects")

        async def targets(mode, names):
            return [{"pc": HW, "lab": "LAB-A"}]

        P.set(inventory, "resolve_targets", targets)
        P.set(inventory, "add_audit_log", recorder([]))
        await inventory.scan_patches(PatchInstallInput(target_mode="PC", targets=[HW], scope="security"), auth)
        await inventory.install_patches(PatchInstallInput(target_mode="PC", targets=[HW], scope="all"), auth)
        chk([m.get("action") for m in agent_ws.sent] == ["scan_updates", "install_updates"], "Windows Update komutları")
        take(agent_ws, "inventory.scan_patches / install_patches")

        P.set(wol, "execute_query", FakeDB([("SELECT pc_name FROM clients WHERE lab_name", [{"pc_name": HW}])]))
        chk(await wol.attempt_p2p_wol("A4:BB:6D:12:34:57", "LAB-A"), "Wake-on-LAN eş üzerinden")
        take(agent_ws, "wol.attempt_p2p_wol")

        # Bekleyen karantina işlemi yoksa panel ajanın bildirdiği durumu alır, ajana komut gitmez
        P.set(agents, "execute_query", FakeDB([("pending_quarantine_action AS act", [
            {"is_quarantined": True, "act": None, "reason": None}])]))
        P.set(agents, "add_audit_log", recorder([]))
        P.set(agents, "notify", recorder([]))
        await agents.reconcile_quarantine(HW, False)
        chk(not agent_ws.sent, "bekleyen karantina işlemi yoksa komut gönderilmez")

        with tempfile.TemporaryDirectory() as tmp:
            with open(TEST_MANIFEST, "rb") as f:
                manifest_bytes = f.read()
            manifest = json.loads(manifest_bytes)
            reldir = os.path.join(tmp, "releases", manifest["version"])
            os.makedirs(reldir)
            with open(os.path.join(reldir, "manifest.json"), "wb") as f:
                f.write(manifest_bytes)
            with open(TEST_MANIFEST + ".sig", "rb") as src, open(os.path.join(reldir, "manifest.json.sig"), "wb") as f:
                f.write(src.read())
            msi = system_routes._agent_msis(manifest)[0]
            with open(os.path.join(reldir, msi), "wb") as f:
                f.write(b"MSI")
            P.set(system_routes, "RELEASES_DIR", os.path.join(tmp, "releases"))
            # Gönderim kaydı (pending_updates) veritabanına yazılmaz; yeniden gönderim denetimi boş döner
            from pops import update_tracking

            async def none_recent(pcs, version, seconds=0):
                return set()

            async def mark_sent(pc, version):
                manager.pending_updates[pc] = (version, 0.0)
            P.set(update_tracking, "recently_sent", none_recent)
            P.set(update_tracking, "mark_sent", mark_sent)
            sysdb = FakeDB([("verified_release_manifest", [{"value": manifest_bytes.decode()}])])
            router = system_routes.build_router(lambda: None, lambda: None, sysdb, manager,
                                                os.path.join(tmp, "updates"), recorder([]))
            endpoints = {r.path: r.endpoint for r in router.routes}
            await endpoints["/api/system/deploy-update"](system_routes.DeployUpdateInput(target_mode="PC",
                                                                                         targets=[HW]), auth)
            manager.pending_updates.pop(HW, None)
            sent = agent_ws.sent[0] if agent_ws.sent else {}
            chk(base64.b64decode(sent.get("manifest", "")) == manifest_bytes,
                "update_agent manifest'i baytı bozmadan taşır")
            check_message_manifest(base64.b64decode(sent.get("manifest", "")), "deploy-update")
            take(agent_ws, "system_routes.deploy_update")
            for fields in ({"terminal_enabled": False}, {"terminal_enabled": False, "vision_enabled": False}):
                capability = system_routes.CapabilityInput(pc_name=HW, **fields)
                await endpoints["/api/system/set-capabilities"](capability, auth)
            chk([m.get("vision_enabled") for m in agent_ws.sent] == [None, False],
                "set_capabilities yalnızca verilen alanlar")
            take(agent_ws, "system_routes.set_capabilities")
    finally:
        P.restore()
        manager.active_agents.pop(HW, None)
        manager.active_vision_ws.pop(HW, None)
        manager.vision_sessions.pop(HW, None)
        manager.pending_thumbnails.pop(HW, None)

    for origin, msg in built:
        check_message(S2A, msg, origin)
    produced = {message_name(S2A, m) for _, m in built}
    chk({"execute", "cancel_task", "lockdown", "unlock", "start_vision_session", "stop_stream", "remote_input",
         "scan_updates", "install_updates", "wake_peer", "update_agent", "set_capabilities", "server_info"} <= produced,
        "uçlardaki bütün komutlar kuruldu ve denetlendi")


def agent_simulator_messages():
    print("== tools/agent_simulator.py")
    spec = importlib.util.spec_from_file_location("agent_simulator", os.path.join(REPO, "tools", "agent_simulator.py"))
    sim = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(sim)
    first = json.loads(sim.dna(3))
    chk("dna_payload" in first, "simülatörün ilk mesajı dna_payload taşıyor")
    check_message(A2S, first, "agent_simulator.dna")


def main():
    test_schemas()
    test_examples()
    test_code_matches_schemas()
    loop = asyncio.new_event_loop()
    try:
        loop.run_until_complete(agent_session_with_secret())
        loop.run_until_complete(agent_session_with_enroll_token())
        loop.run_until_complete(identity_reassignment())
        loop.run_until_complete(vision_channel())
        loop.run_until_complete(server_builders())
    finally:
        loop.close()
    agent_simulator_messages()
    if FAILS:
        print("BASARISIZ: %d kontrol" % len(FAILS))
        sys.exit(1)
    print("PROTOKOL TESTLERI GECTI")


if __name__ == "__main__":
    main()
