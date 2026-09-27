"""Donanım DNA'sı ile cihaz kimliği uzlaştırma (klon tespiti, kimlik kurtarma)."""

import datetime
import hashlib
import json

from fastapi import WebSocket

from pops.db import execute_query
from pops.audit import add_audit_log


def calculate_dna_score(incoming_hw, db_hw, incoming_caps, db_caps):
    if incoming_hw.get('uuid') in ["NULL", "-"] and incoming_hw.get('mac') in ["00:00:00:00:00:00", "-", "NULL"]:
        return 11, 11
    score = 0
    max_score = 11
    if incoming_hw.get('uuid') != "NULL" and incoming_hw.get('uuid') == db_hw.get('dna_uuid'):
        score += 4
    if incoming_hw.get('bios_sn') != "NULL" and incoming_hw.get('bios_sn') == db_hw.get('dna_bios'):
        score += 3
    if incoming_hw.get('disk_sn') != "NULL" and incoming_hw.get('disk_sn') == db_hw.get('dna_disk'):
        score += 2
    if incoming_hw.get('mac') != "NULL" and incoming_hw.get('mac') == db_hw.get('dna_mac'):
        score += 1
    db_ram_readable = db_caps.get('cap_ram_readable', True) if db_caps else True
    inc_ram_readable = incoming_caps.get('ram_readable', True)
    if not db_ram_readable:
        max_score = 10
    else:
        if (
            inc_ram_readable
            and incoming_hw.get('ram_sn') != "NULL"
            and incoming_hw.get('ram_sn') == db_hw.get('dna_ram')
        ):
            score += 1
    return score, max_score


async def reconcile_device(claimed_hwid: str, dna_payload: dict, client_ip: str, ws: WebSocket):
    hw = dna_payload.get("hardware", {})
    caps = dna_payload.get("capabilities", {})
    existing_pc = await execute_query("SELECT * FROM clients WHERE pc_name = $1", (claimed_hwid,), fetch=True)
    if existing_pc:
        db_record = existing_pc[0]
        score, max_score = calculate_dna_score(hw, db_record, caps, db_record)
        threshold = (
            5.5
            if hw.get('uuid') != "NULL"
            and hw.get('uuid') == db_record.get('dna_uuid')
            and hw.get('bios_sn') == db_record.get('dna_bios')
            else 6
        )
        if score >= threshold:
            await execute_query(
                "UPDATE clients SET dna_uuid=$1, dna_bios=$2, dna_disk=$3, dna_mac=$4, dna_ram=$5, "
                "cap_ram_readable=$6 WHERE pc_name=$7",
                (
                    hw.get('uuid'),
                    hw.get('bios_sn'),
                    hw.get('disk_sn'),
                    hw.get('mac'),
                    hw.get('ram_sn'),
                    caps.get('ram_readable', True),
                    claimed_hwid,
                ),
            )
            return claimed_hwid
        else:
            new_hwid = (
                "HW-"
                + hashlib.md5(
                    (hw.get('uuid', '') + hw.get('mac', '') + str(datetime.datetime.now().timestamp())).encode()
                )
                .hexdigest()[:12]
                .upper()
            )
            await add_audit_log(
                claimed_hwid,
                "CLONE_DETECTED",
                f"Skor: {score}/{max_score}",
                {"old_hw": db_record.get('dna_uuid'), "new_hw": hw.get('uuid')},
            )
            await ws.send_text(json.dumps({"action": "set_identity", "new_hw_id": new_hwid}))
            return new_hwid
    else:
        all_pcs = await execute_query("SELECT * FROM clients", fetch=True)
        best_match, best_score, best_max = None, 0, 11
        for pc in all_pcs:
            s, m = calculate_dna_score(hw, pc, caps, pc)
            if s > best_score:
                best_score, best_max, best_match = s, m, pc
        if best_match and best_score >= 6:
            real_hwid = best_match["pc_name"]
            await add_audit_log(
                real_hwid, "RECOVERED_IDENTITY", f"Kurtarıldı: {best_score}/{best_max}", {"temp_id": claimed_hwid}
            )
            await ws.send_text(json.dumps({"action": "set_identity", "new_hw_id": real_hwid}))
            return real_hwid
        else:
            await add_audit_log(claimed_hwid, "NEW_DEVICE", "Yeni Cihaz", hw)
            return claimed_hwid
