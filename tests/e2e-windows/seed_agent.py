"""T7 (eş önbelleği) için simüle tohum ajan. Backend/tests/test_peer_cache.py'deki connect()/succeed() modeli:
gerçek bir ajanmış gibi /ws/agent'a bağlanır (X-Agent-Secret, X-Agent-Features: peer_cache, X-Agent-Peer-Cache),
update_agent emrini alınca yeni sürümle yeniden bağlanıp update_result success gönderir ve bağlı kalır (hazır eş
olarak önerilsin). Durumu bir log dosyasına yazar; dur dosyası görününce ya da süre dolunca çıkar.

Gerçek paketi SUNMAZ: paketi runner LAN IP:8818'deki sahte eş (peer_server.py) sunar; bu betik yalnızca sunucuya
tohumun hazır olduğunu söyler. Adres, port ve link başlıkta bildirilir (ip alt ağ dışı verilirse T7c varyantı).

    python seed_agent.py --hw HW-SEED --ip 10.1.0.10 --port 8818 --old 0.1.90-e2e --new 0.1.91-e2e \
        --log seed.log --stop seed.stop --timeout 180
"""

import argparse
import asyncio
import hashlib
import json
import os

import websockets

WS = os.environ.get("POPS_IT_WS") or os.environ.get("POPS_IT_URL", "https://localhost:8443") \
    .replace("https://", "wss://").replace("http://", "ws://")


def _log(path, msg):
    with open(path, "a", encoding="utf-8") as f:
        f.write(msg + "\n")
    print(msg, flush=True)


def _dna(hw):
    return json.dumps({
        "hw_id": hw,
        "dna_payload": {"hardware": {"uuid": hw + "-U", "bios_sn": hw + "-B", "disk_sn": hw + "-D", "mac": "-",
                                     "ram_sn": "-"}, "capabilities": {"ram_readable": True}},
        "hostname": hw.lower(),
        "status": "Online",
    })


async def _connect(hw, header, version):
    import ssl
    ca = os.environ.get("POPS_IT_CA")
    ctx = ssl.create_default_context(cafile=ca) if ca else ssl._create_unverified_context()
    headers = {"X-Agent-Secret": hw + "-s", "X-Agent-Version": version,
               "X-Agent-Features": "winget,peer_cache", "X-Agent-Peer-Cache": header}
    ws = await websockets.connect("%s/ws/agent/%s" % (WS.rstrip("/"), hw), additional_headers=headers, ssl=ctx)
    await ws.send(_dna(hw))
    return ws


async def main(a):
    header = "ip=%s; port=%d; link=wired" % (a.ip, a.port)
    ws = await _connect(a.hw, header, a.old)
    _log(a.log, "CONNECTED version=%s header=%s" % (a.old, header))
    loop = asyncio.get_event_loop()
    deadline = loop.time() + a.timeout
    updated = False
    while loop.time() < deadline:
        if os.path.exists(a.stop):
            _log(a.log, "STOP")
            break
        try:
            raw = await asyncio.wait_for(ws.recv(), timeout=1.0)
        except asyncio.TimeoutError:
            continue
        except websockets.exceptions.ConnectionClosed:
            _log(a.log, "CLOSED")
            break
        try:
            msg = json.loads(raw)
        except ValueError:
            continue
        action = msg.get("action")
        if action == "update_agent" and not updated:
            updated = True
            _log(a.log, "UPDATE_AGENT peers=%s peer_cache=%s" % (msg.get("peers"), msg.get("peer_cache")))
            # Yeni sürümle yeniden bağlan ve başarılı sonucu bildir (tohum paketi kurdu, artık hazır eş)
            await ws.close()
            ws = await _connect(a.hw, header, a.new)
            await asyncio.sleep(0.8)
            await ws.send(json.dumps({"type": "update_result", "status": "success", "from_version": a.old,
                                      "to_version": a.new, "running_version": a.new,
                                      "result_id": hashlib.sha256((a.hw + a.new).encode()).hexdigest()[:32]}))
            _log(a.log, "SEEDED version=%s" % a.new)
    await ws.close()
    _log(a.log, "EXIT updated=%s" % updated)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--hw", required=True)
    p.add_argument("--ip", required=True)
    p.add_argument("--port", type=int, default=8818)
    p.add_argument("--old", required=True)
    p.add_argument("--new", required=True)
    p.add_argument("--log", required=True)
    p.add_argument("--stop", required=True)
    p.add_argument("--timeout", type=int, default=180)
    asyncio.run(main(p.parse_args()))
