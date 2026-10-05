import json
import os
import sys

import pytest

from conftest import FIXTURES, REPO, make_hw, write
from pops_agent import identity

PROC = os.path.join(FIXTURES, "proc")


def test_hardware_from_dmi(tmp_path):
    sys_root = make_hw(str(tmp_path))
    hw = identity.hardware(sys_root, PROC, str(tmp_path / "run"), str(tmp_path / "dev"))
    assert hw["uuid"] == "4C4C4544-0042-3510-8051-B3C04F4D3732"   # Windows gibi büyük harf
    assert hw["bios_sn"] == "5CG1234XYZ"
    assert hw["disk_sn"] == "WD-WX12A3456789" and hw["_disk_serial_real"] is True
    assert hw["mac"] == "A4:BB:6D:12:34:56"
    assert hw["ram_sn"] == "1234ABCD"   # boş yuva ve "Unknown" atlanır
    dna = identity.dna_payload(hw, "Pardus 23.1")
    assert set(dna["hardware"]) == {"uuid", "bios_sn", "disk_sn", "mac", "ram_sn"}
    assert dna["capabilities"]["ram_readable"] is True and dna["os"] == "Pardus 23.1"
    json.dumps(dna)   # sunucuya JSON gider


def test_placeholders_become_null(tmp_path):
    sys_root = make_hw(str(tmp_path), uuid="FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF",
                       product_serial="To be filled by O.E.M.", board_serial="Default string",
                       ram=("Unknown", "00000000"), disk=("sda", None))
    hw = identity.hardware(sys_root, PROC, str(tmp_path / "run"), str(tmp_path / "dev"))
    assert hw["uuid"] == identity.NULL and hw["bios_sn"] == identity.NULL and hw["ram_sn"] == identity.NULL
    assert hw["disk_sn"] == identity.NULL and hw["_disk_serial_real"] is False
    assert identity.dna_payload(hw, "x")["capabilities"]["ram_readable"] is False


def test_board_serial_when_system_serial_missing(tmp_path):
    sys_root = make_hw(str(tmp_path), product_serial="System Serial Number")
    assert identity.system_serial(identity.read_dmi(sys_root)) == "/7ABC123/CNWS20012/"


def test_unreadable_dmi_as_non_root(tmp_path):
    """DMI dosyaları root'a açık; okunamazsa NULL (ajan root çalışır, testler değil)."""
    hw = identity.hardware(str(tmp_path / "empty"), PROC, str(tmp_path / "run"), str(tmp_path / "dev"))
    assert hw["uuid"] == identity.NULL and hw["_dmi_readable"] is False and hw["mac"] == "-"


def test_mac_prefers_up_wired_physical(tmp_path):
    sys_root = make_hw(str(tmp_path), macs=(
        ("docker0", "02:42:ac:11:00:01", "up", False),
        ("wlp2s0", "11:22:33:44:55:66", "up", True),
        ("enp3s0", "a4:bb:6d:12:34:56", "down", True),
        ("enp4s0", "a4:bb:6d:12:34:57", "up", True),
        ("veth1a2b", "aa:aa:aa:aa:aa:aa", "up", True),
    ))
    write(os.path.join(sys_root, "class", "net", "wlp2s0", "wireless", "x"), "")
    assert identity.mac_address(sys_root) == "A4:BB:6D:12:34:57"


def test_disk_serial_from_udev_and_fs_uuid_fallback(tmp_path):
    sys_root = make_hw(str(tmp_path), disk=("sda", None))
    run = tmp_path / "run"
    write(str(run / "udev" / "data" / "b8:0"), "S:disk/by-id/ata-X\nE:ID_SERIAL_SHORT=S3Z9NB0K123456\n")
    assert identity.disk_serial(sys_root, PROC, str(run), str(tmp_path / "dev")) == ("S3Z9NB0K123456", True)
    os.unlink(str(run / "udev" / "data" / "b8:0"))
    write(str(run / "udev" / "data" / "b8:2"), "E:ID_FS_UUID=0f3c6b9e-1111-2222-3333-444455556666\n")
    assert identity.disk_serial(sys_root, PROC, str(run), str(tmp_path / "dev")) == \
        ("0f3c6b9e-1111-2222-3333-444455556666", False)


def test_smbios_strings():
    from conftest import smbios17

    formatted, strings = identity.parse_smbios_strings(smbios17("ABC"))
    assert formatted[0] == 17 and strings[-1] == "ABC"


def test_identity_file(tmp_path):
    path = str(tmp_path / "identity")
    hw = {"uuid": "4C4C4544-0042-3510-8051-B3C04F4D3732", "mac": "A4:BB:6D:12:34:56"}
    first = identity.load_or_create(path, hw)
    assert identity.is_hw_id(first) and len(first) == 15 and first == first.upper()
    assert identity.load_or_create(path, {"uuid": "other", "mac": "x"}) == first   # kalıcı
    assert identity.save(path, "HW-ABCDEF123456") and identity.load_or_create(path, hw) == "HW-ABCDEF123456"
    assert not identity.save(path, "../../etc/passwd")
    assert identity.generate_hw_id("NULL", "-").startswith("HW-")   # okunamayan donanımda rastgele


def test_binding_verdicts(tmp_path):
    b = identity.Binding(str(tmp_path / "hw.bind.json"))
    assert b.check("U1", "B1") == identity.MISSING
    b.bind("HW-1", "U1", "B1", "set_secret")
    assert b.check("u1", "B1") == identity.MATCH          # büyük/küçük harf duyarsız
    assert b.check("U2", "B2") == identity.CLONE
    assert b.check("U1", "B2") == identity.PARTIAL and b.changed == ["bios_sn"]
    assert b.check("NULL", "NULL") == identity.UNREADABLE
    b.update_hw_id("HW-2")
    b.check("U1", "B1")
    assert b.bound_hw_id == "HW-2"


def test_move_clone_files_keeps_server_switch_off(tmp_path):
    state = str(tmp_path)
    for name in ("identity", "secret.json", "capabilities.state.json"):
        write(os.path.join(state, name), "x")
    folder, moved = identity.move_clone_files(state)
    assert set(moved) == {"identity", "secret.json"}
    assert os.path.exists(os.path.join(state, "capabilities.state.json"))
    assert os.path.exists(os.path.join(folder, "secret.json"))


def test_server_dna_score_accepts_agent_payload(tmp_path):
    """Sunucunun DNA puanlaması (Backend/pops/dna.py) ajanın gönderdiğini Windows ajanınınki gibi değerlendirir."""
    pytest.importorskip("fastapi")
    pytest.importorskip("asyncpg")
    sys.path.insert(0, os.path.join(REPO, "Backend"))
    for k in ("JWT_SECRET", "DB_USER", "DB_PASS", "DB_NAME"):
        os.environ.setdefault(k, "unit-test")
    from pops import dna

    hw = identity.hardware(make_hw(str(tmp_path)), PROC, str(tmp_path / "run"), str(tmp_path / "dev"))
    payload = identity.dna_payload(hw, "Pardus")
    db_row = {"dna_uuid": hw["uuid"].lower(), "dna_bios": hw["bios_sn"], "dna_disk": hw["disk_sn"],
              "dna_mac": hw["mac"], "dna_ram": hw["ram_sn"], "cap_ram_readable": True}
    assert dna.calculate_dna_score(payload["hardware"], db_row, payload["capabilities"], db_row) == (11, 11)
    nulls = {k: identity.NULL for k in ("uuid", "bios_sn", "disk_sn", "ram_sn")}
    nulls["mac"] = "-"
    assert dna.calculate_dna_score(nulls, db_row, {"ram_readable": False}, db_row) == (0, 0)
