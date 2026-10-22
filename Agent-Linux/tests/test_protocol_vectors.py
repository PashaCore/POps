"""docs/protocol'deki ortak test vektörleri (docs/protocol/AGENT_TESTS.md): Windows ajanının testleriyle aynı dosyalar.

1. Sunucudan gelen her örnek mesaj hatasız işlenir ve belgelenen etkiyi yapar.
2. Ajanın kurduğu mesajlar ajan → sunucu şemalarına uyar ve yalnızca belgelenmiş alan taşır (python3-jsonschema).
3. İmzalı güncelleme vektörü test anahtarıyla doğrulanır, gömülü sürüm anahtarıyla reddedilir.
4. Ajanın gönderdiği mesajların üst düzey alanları örnekleriyle aynıdır.
"""

import asyncio
import base64
import glob
import json
import os

import pytest

from conftest import REPO, make_hw, write
from pops_agent import commands, release
from pops_agent import paths as paths_mod
from pops_agent.agent import Agent

PROTO = os.path.join(REPO, "docs", "protocol")
A2S, S2A = "agent-to-server", "server-to-agent"
TEST_PUBLIC_KEY = "9enOkSVQHBXaXoAurkfvUFBqSbfYbJQbMwL0zEIPTqQ="


def load(*parts):
    with open(os.path.join(PROTO, *parts), encoding="utf-8") as f:
        return json.load(f)


def server_vectors():
    return sorted(os.path.basename(p) for p in glob.glob(os.path.join(PROTO, "examples", S2A, "*.json")))


def message_name(msg):
    return msg.get("type") or ("heartbeat" if "status" in msg else None)


def undocumented(schema, msg, prefix=""):
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


def check_agent_message(msg):
    jsonschema = pytest.importorskip("jsonschema")
    schema = load(A2S, message_name(msg) + ".json")
    errors = sorted(e.message for e in jsonschema.Draft202012Validator(schema).iter_errors(msg))
    assert not errors, (message_name(msg), errors)
    assert not undocumented(schema, msg), (message_name(msg), undocumented(schema, msg))


def make_agent(tmp_path, terminal="1"):
    p = paths_mod.Paths(str(tmp_path / "etc"), str(tmp_path / "state"), str(tmp_path / "log"))
    os.makedirs(p.log_dir, exist_ok=True)
    write(p.config_file, "SERVER_URL=https://pops.okul.local\nENROLL_TOKEN=abcdefghijklmnop\n", 0o600)
    write(p.capabilities_file, "TERMINAL_ENABLED=%s\n" % terminal)
    make_hw(str(tmp_path / "hw"))
    agent = Agent(p, version="0.1.23-alpha", hw_root=str(tmp_path / "hw"))
    agent.init_core()
    sent = []

    async def capture(payload):
        sent.append(json.loads(json.dumps(payload)))
        return True

    agent.send = capture

    async def no_inventory():
        sent.append({"inventory": True})

    agent.send_inventory = no_inventory
    agent.on_update_agent = _no_update   # ağ ve iş parçacığı yok; doğrulama test 3'te
    return agent, sent


async def _no_update(msg):
    return None


def run(agent, coro_fn):
    async def go():
        agent.stopping = asyncio.Event()
        agent.send_lock = asyncio.Lock()
        result = await coro_fn()
        await asyncio.sleep(0)
        return result
    return asyncio.run(go())


@pytest.mark.parametrize("name", server_vectors())
def test_every_server_vector_is_accepted(tmp_path, name):
    msg = load("examples", S2A, name)
    agent, sent = make_agent(tmp_path, terminal="0")
    agent.modules_closed = {"wol"}
    if name == "set_bypass_secret.json":
        agent.connected_with_secret = True
    if msg.get("device"):
        agent.hw_id = msg["device"]   # remote_input yalnızca hedef cihazdaysa işlenir (Windows ajanı gibi)
    run(agent, lambda: agent.handle(msg))
    kind = name.split(".")[0]
    if kind == "server_info":
        assert agent.handshake.supports("result_ack") is True and agent.handshake.supports("update_result_ack") is True
    elif kind == "set_secret":
        assert agent.secret == msg["secret"] and agent.load_secret() == msg["secret"]
    elif kind == "set_identity":
        assert agent.hw_id == "HW-9B41D07E5A2C"
    elif kind == "set_bypass_secret":
        assert sent == [load("examples", A2S, "bypass_secret_ack.json")]
    elif kind == "execute":
        result, denied = sent
        assert result["exit_code"] == commands.EXIT_DENIED and result["output"].startswith("[REDDEDİLDİ]")
        assert denied["type"] == "capability_denied" and denied["task_id"] == msg["task_id"] == result["task_id"]
    elif kind in ("lockdown", "unlock", "start_stream", "start_vision_session", "scan_updates", "install_updates"):
        assert len(sent) == 1 and sent[0]["reason"] == "not_supported"
    elif kind == "remote_input":
        assert len(sent) == 1 and sent[0]["capability"] == "vision"
    elif kind == "wake_peer":
        assert sent == [{"type": "capability_denied", "capability": "wol", "action": "wake_peer",
                         "reason": "module_disabled"}]
    elif kind == "set_capabilities":
        assert not agent.caps.terminal_enabled and sent[-1]["type"] == "capabilities"
    elif kind == "get_hardware":
        assert sent == [] or sent == [{"inventory": True}]
    else:   # cancel_task, result_ack, update_result_ack, stop_stream, update_agent (test 3)
        assert sent == []
    for m in sent:
        if "type" in m:
            check_agent_message(m)


def test_unknown_server_message_is_ignored(tmp_path):
    agent, sent = make_agent(tmp_path)
    run(agent, lambda: agent.handle(load("examples", "unknown", S2A + ".json")))
    assert sent == []


def test_signed_update_vector(tmp_path):
    msg = load("examples", S2A, "update_agent.json")
    raw = base64.b64decode(msg["manifest"])
    assert release.verify_signature(raw, msg["manifest_sig"], TEST_PUBLIC_KEY)
    assert not release.verify_signature(raw, msg["manifest_sig"])   # sürüm anahtarı değil
    with open(os.path.join(REPO, "Agent", "POps.Tests", "TestData", "manifest.json"), "rb") as f:
        assert f.read() == raw
    manifest = release.parse(raw)
    assert manifest.version == "0.1.3-alpha"
    with pytest.raises(release.ManifestError):   # yalnızca Windows paketi var
        release.select_deb(manifest)


def test_agent_messages_match_schemas_and_examples(tmp_path):
    agent, sent = make_agent(tmp_path, terminal="0")
    hb = agent.heartbeat()
    check_agent_message(hb)
    assert set(hb) == set(load("examples", A2S, "heartbeat.linux.json"))
    caps = agent.caps.status_message("custom")
    check_agent_message(caps)
    assert set(caps) == set(load("examples", A2S, "capabilities.default.json"))
    run(agent, lambda: agent.handle({"action": "execute", "task_id": 7, "script_path": "id"}))
    run(agent, lambda: agent.handle({"action": "lockdown", "reason": "x"}))
    for m in sent:
        check_agent_message(m)
    assert set(sent[0]) == set(load("examples", A2S, "result.denied.json"))
    assert set(sent[-1]) == set(load("examples", A2S, "capability_denied.not_supported.json"))
    write(agent.paths.update_result, json.dumps({"schema": "pops-update-result/1", "outcome": "rolled_back",
                                                 "from_version": "0.1.22-alpha", "to_version": "0.1.23-alpha",
                                                 "rollback": "deb", "detail": "x", "agent_state": "running",
                                                 "running_version": "0.1.22-alpha", "reboot_required": False}))
    check_agent_message(agent.update.pending_result_message())
    for stage in ({"type": "update_progress", "stage": "received"},
                  {"type": "update_progress", "stage": "rejected", "to_version": "0.1.23-alpha", "detail": "x"}):
        check_agent_message(stage)
