"""Ajanın protokolü, sahte bir POps sunucusuna karşı (websockets sunucusu, yerel http): el sıkışma başlıkları, ilk
mesajın DNA'sı, kayıt, komut ve sonuç onayı, desteklenmeyen emir, yetenek kapatma ve 4401 reddi. Gerçek arka uçla
uçtan uca test: test_integration.py."""

import asyncio
import json
import os

from conftest import make_hw, write
from pops_agent import paths as paths_mod
from pops_agent.agent import Agent, magic_packet

SECRET = "s3cr3t-" + "x" * 30


async def serve(handler):
    import websockets

    async def entry(ws, *args):
        headers = getattr(ws, "request_headers", None)
        if headers is None:
            headers = ws.request.headers
        path = args[0] if args else (getattr(ws, "path", None) or ws.request.path)   # eski / yeni websockets
        await handler(ws, headers, path)

    server = await websockets.serve(entry, "127.0.0.1", 0)
    port = list(server.sockets)[0].getsockname()[1]
    return server, port


def make_agent(tmp_path, port, token="abcdefghijklmnop", terminal="1"):
    p = paths_mod.Paths(str(tmp_path / "etc"), str(tmp_path / "state"), str(tmp_path / "log"))
    os.makedirs(p.log_dir, exist_ok=True)
    write(p.config_file, "SERVER_URL=http://127.0.0.1:%d\nENROLL_TOKEN=%s\n" % (port, token), 0o600)
    write(p.capabilities_file, "TERMINAL_ENABLED=%s\n" % terminal)
    make_hw(str(tmp_path / "hw"))
    return Agent(p, version="0.1.22-alpha", hw_root=str(tmp_path / "hw")), p


async def next_message(queue, kind, timeout=15):
    """Heartbeat'ler atlanır; kind: "type" değeri ya da ("type", alan, değer)."""
    loop = asyncio.get_running_loop()
    end = loop.time() + timeout
    while True:
        msg = await asyncio.wait_for(queue.get(), max(0.1, end - loop.time()))
        if msg.get("type") == kind:
            return msg


def test_enroll_command_ack_and_refusals(tmp_path):
    async def scenario():
        queue = asyncio.Queue()
        seen = {}

        async def handler(ws, headers, path):
            seen["headers"] = dict(headers)
            seen["path"] = path
            first = json.loads(await ws.recv())
            seen["first"] = first
            await ws.send(json.dumps({"action": "set_secret", "secret": SECRET}))
            await ws.send(json.dumps({"action": "server_info", "version": "x",
                                      "features": ["update_result_ack", "result_ack"]}))
            await ws.send(json.dumps({"action": "execute", "task_id": 41, "script_path": "echo merhaba; exit 2",
                                      "requested_by": "admin"}))
            try:
                async for raw in ws:
                    msg = json.loads(raw)
                    await queue.put(msg)
                    if msg.get("type") == "result" and msg.get("task_id") == 41:
                        await ws.send(json.dumps({"action": "result_ack", "task_id": 41}))
                        await ws.send(json.dumps({"action": "lockdown", "reason": "test"}))
                        await ws.send(json.dumps({"action": "lockdown", "reason": "ikinci kez"}))
                        await ws.send(json.dumps({"action": "set_capabilities", "terminal_enabled": False}))
                        await ws.send(json.dumps({"action": "execute", "task_id": 42, "script_path": "id"}))
            except Exception:
                pass

        server, port = await serve(handler)
        agent, p = make_agent(tmp_path, port)
        runner = asyncio.ensure_future(agent.run())
        try:
            result = await next_message(queue, "result")
            denied_q = await next_message(queue, "capability_denied")
            denied_q2 = await next_message(queue, "capability_denied")   # tek seferlik emir: her seferinde yanıt
            caps = None
            while caps is None or caps.get("terminal_enabled") is not False:
                caps = await next_message(queue, "capabilities")
            refused = await next_message(queue, "result")
            denied_t = await next_message(queue, "capability_denied")
            await asyncio.sleep(0.3)
            spool_left = os.path.exists(p.results) and json.load(open(p.results))
        finally:
            agent.stopping.set()
            await asyncio.wait_for(runner, 30)
            server.close()
        return seen, result, denied_q, denied_q2, refused, denied_t, spool_left, p

    seen, result, denied_q, denied_q2, refused, denied_t, spool_left, p = asyncio.run(scenario())
    h = {k.lower(): v for k, v in seen["headers"].items()}
    assert h["x-agent-version"] == "0.1.22-alpha" and h["x-agent-platform"] == "linux"
    assert h["x-enroll-token"] == "abcdefghijklmnop" and "x-agent-secret" not in h
    hw_id = open(p.identity).read().strip()
    assert seen["path"] == "/ws/agent/" + hw_id
    first = seen["first"]
    assert first["dna_payload"]["hardware"]["uuid"] == "4C4C4544-0042-3510-8051-B3C04F4D3732"
    assert first["platform"] == "linux" and first["status"] == "Online" and "quarantined" not in first
    assert set(first["agent_health"]) >= {"started_at", "loop_errors_1h", "vision_channel"}
    # kayıt: anahtar 0600 dosyada, jeton ayar dosyasından silindi
    assert json.load(open(p.secret))["secret"] == SECRET and oct(os.stat(p.secret).st_mode & 0o777) == "0o600"
    assert "abcdefghijklmnop" not in open(p.config_file).read()
    # komut: çıkış kodu ve Windows biçimi
    assert result["task_id"] == 41 and result["exit_code"] == 2 and result["pc_name"] == hw_id
    assert result["output"] == "merhaba"
    # 41 onaylandı ve diskten silindi; onaylanmayan 42 (ret sonucu) onay gelene kadar diskte kalır
    assert [e["task_id"] for e in spool_left] == [42]
    assert denied_q == {"type": "capability_denied", "capability": "quarantine", "action": "lockdown",
                        "reason": "not_supported"} == denied_q2
    assert refused["task_id"] == 42 and refused["exit_code"] == -5 and refused["output"].startswith("[REDDEDİLDİ]")
    assert denied_t["capability"] == "terminal" and denied_t["task_id"] == 42
    events = [json.loads(line)["event"] for line in open(p.audit_log)]
    for e in ("enrolled", "command_started", "command_finished", "unsupported_refused", "capability_changed",
              "command_refused"):
        assert e in events
    health = json.load(open(p.health))
    assert health["version"] == "0.1.22-alpha" and health["checks"]["loop"] is True


def test_auth_rejection_is_audited_and_backs_off(tmp_path):
    async def scenario():
        connections = []

        async def handler(ws, headers, path):
            connections.append(path)
            await ws.recv()
            await ws.close(code=4401, reason="Ajan kimlik dogrulamasi gerekli")

        server, port = await serve(handler)
        agent, p = make_agent(tmp_path, port)
        runner = asyncio.ensure_future(agent.run())
        await asyncio.sleep(3)
        agent.stopping.set()
        await asyncio.wait_for(runner, 30)
        server.close()
        return connections, p

    connections, p = asyncio.run(scenario())
    assert len(connections) == 1   # 4401'den sonra en az 60 sn beklenir
    events = [json.loads(line)["event"] for line in open(p.audit_log)]
    assert "auth_rejected" in events


def test_magic_packet():
    pkt = magic_packet("a4:bb:6d:12:34:56")
    assert len(pkt) == 102 and pkt[:6] == b"\xff" * 6 and pkt[6:12] == bytes.fromhex("a4bb6d123456")
    assert magic_packet("zz") is None and magic_packet("a4-bb-6d-12-34-56") == pkt
