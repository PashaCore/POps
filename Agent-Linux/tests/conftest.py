"""Ortak test yardımcıları. Testler root gerektirmez; donanım bilgisi sahte /sys ağaçlarından okunur."""

import os
import struct
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
AGENT_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(AGENT_DIR)
FIXTURES = os.path.join(HERE, "fixtures")
sys.path.insert(0, AGENT_DIR)


def write(path, text, mode=None):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    data = text if isinstance(text, bytes) else text.encode("utf-8")
    with open(path, "wb") as f:
        f.write(data)
    if mode is not None:
        os.chmod(path, mode)
    return path


def smbios17(serial, size_mb=8192, extra_strings=("BANK 0", "ChannelA-DIMM0", "Samsung", "M378A1K43")):
    """SMBIOS tip 17 (Memory Device) ham kaydı: biçimli alan 0x28 bayt, seri numarası 0x18'deki metin indeksi."""
    length = 0x28
    body = bytearray(length)
    body[0] = 17
    body[1] = length
    struct.pack_into("<H", body, 2, 0x0040)
    struct.pack_into("<H", body, 0x0C, size_mb if size_mb is not None else 0)
    strings = list(extra_strings) + ([serial] if serial is not None else [])
    body[0x10] = 2   # device locator → ikinci metin (seri numarası testinde önemsiz)
    body[0x18] = len(strings) if serial is not None else 0
    tail = b"".join(s.encode("latin-1") + b"\x00" for s in strings) + b"\x00"
    return bytes(body) + tail


def make_hw(root, uuid="4c4c4544-0042-3510-8051-b3c04f4d3732", product_serial="5CG1234XYZ",
            board_serial="/7ABC123/CNWS20012/", macs=(("enp3s0", "a4:bb:6d:12:34:56", "up", True),),
            disk=("sda", "WD-WX12A3456789"), ram=("1234ABCD", None, "Unknown")):
    """Sahte /sys ağacı: DMI, ağ kartları, kök disk (8:2 → sda2 → sda) ve bellek modülleri."""
    sys_root = os.path.join(root, "sys")
    dmi = os.path.join(sys_root, "class", "dmi", "id")
    for name, value in (("product_uuid", uuid), ("product_serial", product_serial), ("board_serial", board_serial),
                        ("board_name", "PRIME B460M-A"), ("sys_vendor", "ASUS")):
        if value is not None:
            write(os.path.join(dmi, name), value + "\n")
    for name, mac, state, physical in macs:
        base = os.path.join(sys_root, "class", "net", name)
        write(os.path.join(base, "address"), mac + "\n")
        write(os.path.join(base, "operstate"), state + "\n")
        if physical:
            os.makedirs(os.path.join(base, "device"), exist_ok=True)
    if disk:
        name, serial = disk
        disk_dir = os.path.join(sys_root, "devices", "pci0000:00", "block", name)
        part = os.path.join(disk_dir, name + "2")
        write(os.path.join(part, "partition"), "2\n")
        write(os.path.join(disk_dir, "dev"), "8:0\n")
        if serial:
            write(os.path.join(disk_dir, "device", "serial"), serial + "\n")
        os.makedirs(os.path.join(sys_root, "dev", "block"), exist_ok=True)
        os.symlink(part, os.path.join(sys_root, "dev", "block", "8:2"))
        os.makedirs(os.path.join(sys_root, "block"), exist_ok=True)
        os.symlink(disk_dir, os.path.join(sys_root, "block", name))
    for i, serial in enumerate(ram or ()):
        entry = os.path.join(sys_root, "firmware", "dmi", "entries", "17-%d" % i)
        write(os.path.join(entry, "raw"), smbios17(serial, size_mb=None if serial is None else 8192))
    return sys_root


@pytest.fixture
def proc_root():
    return os.path.join(FIXTURES, "proc")
