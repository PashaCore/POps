"""Donanım DNA'sı ile cihaz kimliği uzlaştırma (klon tespiti, kimlik kurtarma)."""

import datetime
import hashlib
import json
import time

from fastapi import WebSocket

from pops.db import execute_query
from pops.audit import add_audit_log


# Donanımın okunamadığı ya da üreticinin doldurmadığı değerler kanıt sayılmaz: iki cihazda da "boş" olması onları
# benzer yapmaz (F04). Karşılaştırma büyük/küçük harf ve boşluk duyarsızdır.
_PLACEHOLDERS = {
    "", "null", "none", "-", "0", "unknown", "n/a", "na", "default string", "to be filled by o.e.m.",
    "system serial number", "not specified", "00:00:00:00:00:00", "ffffffff-ffff-ffff-ffff-ffffffffffff",
    "00000000-0000-0000-0000-000000000000", "03000200-0400-0500-0006-000700080009",
}


def _known(value) -> bool:
    return isinstance(value, str) and value.strip().lower() not in _PLACEHOLDERS


def _same(a, b) -> bool:
    return _known(a) and _known(b) and a.strip().lower() == b.strip().lower()


def calculate_dna_score(incoming_hw, db_hw, incoming_caps, db_caps):
    """(puan, en yüksek puan). Yalnızca iki tarafta da bilinen değerler karşılaştırılır; hiçbir alan
    karşılaştırılamıyorsa (0, 0) döner: kanıt yok (benzerlik de farklılık da sayılmaz)."""
    weights = (("uuid", "dna_uuid", 4), ("bios_sn", "dna_bios", 3), ("disk_sn", "dna_disk", 2), ("mac", "dna_mac", 1))
    score = 0
    max_score = 0
    for inc_key, db_key, weight in weights:
        if _known(incoming_hw.get(inc_key)) and _known(db_hw.get(db_key)):
            max_score += weight
            if _same(incoming_hw.get(inc_key), db_hw.get(db_key)):
                score += weight
    db_ram_readable = db_caps.get('cap_ram_readable', True) if db_caps else True
    inc_ram_readable = incoming_caps.get('ram_readable', True)
    if db_ram_readable and inc_ram_readable and _known(incoming_hw.get('ram_sn')) and _known(db_hw.get('dna_ram')):
        max_score += 1
        if _same(incoming_hw.get('ram_sn'), db_hw.get('dna_ram')):
            score += 1
    return score, max_score


# Aynı cihaz için uyuşmazlık denetim satırı en fazla saatte bir (klonlar her bağlanışta zinciri büyütmesin)
_MISMATCH_AUDIT_SECONDS = 3600
_mismatch_seen = {}


async def check_known_device(hw_id: str, dna_payload: dict, client_ip: str) -> bool:
    """Cihaz anahtarıyla doğrulanmış bağlantı: kimlik HER ZAMAN URL'deki kimliktir; ne klon kimliği verilir ne de
    anahtar başka bir kayda taşınır (F04). Donanım bilgisi kayıtla belirgin biçimde uyuşmuyorsa (disk/anakart
    değişti ya da kopyalanmış imaj) yönetici için kaydedilir; karar yöneticinindir."""
    hw = (dna_payload or {}).get("hardware", {}) or {}
    caps = (dna_payload or {}).get("capabilities", {}) or {}
    rows = await execute_query("SELECT * FROM clients WHERE pc_name = $1", (hw_id,), fetch=True)
    if not rows:
        return False
    score, max_score = calculate_dna_score(hw, rows[0], caps, rows[0])
    if not (max_score >= 4 and score * 2 < max_score):
        return False
    now = time.monotonic()
    if now - _mismatch_seen.get(hw_id, -_MISMATCH_AUDIT_SECONDS) >= _MISMATCH_AUDIT_SECONDS:
        _mismatch_seen[hw_id] = now
        await add_audit_log(
            hw_id,
            "dna_mismatch",
            f"Donanım bilgisi kayıtla uyuşmuyor (skor {score}/{max_score}); cihaz anahtarı geçerli, kimlik korundu",
            {"ip": client_ip, "new_uuid": hw.get('uuid')},
        )
    return True


async def reconcile_device(claimed_hwid: str, dna_payload: dict, client_ip: str, ws: WebSocket):
    hw = dna_payload.get("hardware", {})
    caps = dna_payload.get("capabilities", {})
    existing_pc = await execute_query("SELECT * FROM clients WHERE pc_name = $1", (claimed_hwid,), fetch=True)
    if existing_pc:
        db_record = existing_pc[0]
        score, max_score = calculate_dna_score(hw, db_record, caps, db_record)
        threshold = (
            5.5
            if _same(hw.get('uuid'), db_record.get('dna_uuid')) and _same(hw.get('bios_sn'), db_record.get('dna_bios'))
            else 6
        )
        # Karşılaştırılabilir bilgi azsa (donanım okunamadı) kimlik korunur: klon için yeterli kanıt yok
        if max_score < 4 or score >= min(threshold, max_score):
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
            # Kimlik üretimi (güvenlik özeti değil); biçim aynı: HW- + 12 onaltılık
            new_hwid = (
                "HW-"
                + hashlib.sha256(
                    (str(hw.get('uuid') or '') + str(hw.get('mac') or '') + str(datetime.datetime.now().timestamp()))
                    .encode()
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
        # Kimlik kurtarma eşiği (6 puan) UUID ya da BIOS seri numarası eşleşmeden aşılamaz (en fazla disk 2 + MAC 1
        # + RAM 1 = 4). Bu yüzden yalnızca bu ikisinden biri tutan kayıtlar puanlanır; eskiden bilinmeyen her
        # bağlantıda bütün cihaz tablosu okunuyordu. İkisi de okunamadıysa kurtarılacak kayıt yoktur.
        uuid = hw.get('uuid').strip().lower() if _known(hw.get('uuid')) else None
        bios = hw.get('bios_sn').strip().lower() if _known(hw.get('bios_sn')) else None
        all_pcs = []
        if uuid or bios:
            all_pcs = await execute_query(
                "SELECT * FROM clients WHERE ($1::text IS NOT NULL AND lower(btrim(dna_uuid)) = $1) "
                "OR ($2::text IS NOT NULL AND lower(btrim(dna_bios)) = $2) LIMIT 50",
                (uuid, bios),
                fetch=True,
            )
        best_match, best_score, best_max = None, 0, 11
        for pc in all_pcs or []:
            sc, m = calculate_dna_score(hw, pc, caps, pc)
            if sc > best_score:
                best_score, best_max, best_match = sc, m, pc
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
