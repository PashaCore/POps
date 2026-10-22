"""Wake-on-LAN (doğrudan yayın ve aynı labdaki çevrimiçi ajan üzerinden P2P)."""

import socket


from pops.config import WOL_BROADCAST_ADDR, WOL_PORT
from pops.db import execute_query
from pops.manager import manager


def send_wol_packet(mac_address: str):
    try:
        clean_mac = mac_address.replace(":", "").replace("-", "").replace(".", "")
        if len(clean_mac) != 12:
            return False
        data = bytes.fromhex('FFFFFFFFFFFF' + clean_mac * 16)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            s.sendto(data, (WOL_BROADCAST_ADDR, WOL_PORT))
        return True
    except Exception:
        return False


async def attempt_p2p_wol(mac_address: str, lab_name: str):
    # Veritabanı durumu 'Online' olarak yazılır; ayrıca soketi gerçekten açık olan bir eş seçilir
    peers = await execute_query(
        "SELECT pc_name FROM clients WHERE lab_name = $1 AND status = 'Online'", (lab_name,), fetch=True
    )
    for peer in peers or []:
        peer_name = peer["pc_name"]
        if await manager.is_online(peer_name):
            await manager.send_command({"action": "wake_peer", "mac": mac_address}, peer_name)
            return True
    return False
