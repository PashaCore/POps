"""Cihaz kimliği ve donanım DNA'sı (Backend/pops/dna.py ile karşılaştırılır).

DNA, Windows ajanıyla aynı alanlardır: uuid (SMBIOS sistem UUID'si), bios_sn (sistem seri numarası; okunamazsa
anakart seri numarası), disk_sn (kök dosya sisteminin diskinin seri numarası; okunamazsa dosya sistemi UUID'si,
disk_serial_real=false), mac (fiziksel ağ kartı) ve ram_sn (bellek modüllerinin seri numaraları, SMBIOS tip 17).
Okunamayan değer "NULL" gider; sunucu yer tutucuları ("To be filled by O.E.M.", tamamı 0/F UUID) kanıt saymaz.
UUID, Windows'taki gibi büyük harfle gönderilir. DMI dosyalarının çoğu yalnızca root'a açıktır.

Kimlik: /var/lib/pops-agent/identity ("HW-" + 12 onaltılık). İlk açılışta MD5(UUID + MAC)'ten türetilir (Windows
GenerateFallbackHash ile aynı biçim). Sunucu set_identity ile değiştirebilir.

Donanım bağı (hw.bind.json): cihaz anahtarı alındığında UUID ve sistem seri numarasının SHA-256'sı yazılır. Açılışta
okunan donanım bağla uyuşmuyorsa (kayıttan sonra disk imajı alınıp başka bilgisayara yüklenmiş) anahtar ve kimlik
clone-<zaman> klasörüne taşınır, kimlik yeniden türetilir; kayıt jetonu varsa yeni cihaz olarak kaydolunur.
"""

import glob
import hashlib
import logging
import os
import re
import shutil
import socket
import time
import uuid as uuidlib
from typing import Dict, List, Optional, Tuple

from pops_agent import store

log = logging.getLogger("pops.identity")

NULL = "NULL"
_PLACEHOLDERS = {
    "", "null", "none", "-", "0", "unknown", "n/a", "na", "default string", "to be filled by o.e.m.",
    "system serial number", "not specified", "not applicable", "not available", "chassis serial number",
    "base board serial number", "serialnum", "serial number", "00:00:00:00:00:00",
    "ffffffff-ffff-ffff-ffff-ffffffffffff", "00000000-0000-0000-0000-000000000000",
    "03000200-0400-0500-0006-000700080009", "123456789", "0123456789", "00000000", "ffffffff",
}
_VIRTUAL_IF = re.compile(r"^(lo|docker|veth|br-|virbr|vmnet|vboxnet|tun|tap|wg|zt|tailscale|lxc|lxd|flannel|cni|"
                         r"kube|cali|vxlan|bond|team|dummy|sit|ip6tnl|gre|nlmon)")


def known(value) -> bool:
    return isinstance(value, str) and value.strip().lower() not in _PLACEHOLDERS


def _read(path: str) -> Optional[str]:
    try:
        with open(path, "rb") as f:
            return f.read().decode("utf-8", errors="replace").strip("\x00 \t\r\n")
    except OSError:
        return None


def read_dmi(sys_root: str = "/sys") -> Dict[str, Optional[str]]:
    base = os.path.join(sys_root, "class", "dmi", "id")
    names = ("product_uuid", "product_serial", "board_serial", "board_name", "board_vendor", "product_name",
             "sys_vendor", "bios_version", "chassis_serial")
    return {n: _read(os.path.join(base, n)) for n in names}


def normalize_uuid(raw: Optional[str]) -> str:
    if not raw or not known(raw):
        return NULL
    return raw.strip().upper()


def normalize_bios_serial(raw: Optional[str]) -> str:
    if not raw or not known(raw) or "O.E.M" in raw.upper():
        return NULL
    return raw.strip()


def system_serial(dmi: Dict[str, Optional[str]]) -> str:
    serial = normalize_bios_serial(dmi.get("product_serial"))
    if serial == NULL:
        serial = normalize_bios_serial(dmi.get("board_serial"))
    return serial


# ── Ağ kartı ──
def physical_interfaces(sys_root: str = "/sys") -> List[str]:
    out = []
    for path in sorted(glob.glob(os.path.join(sys_root, "class", "net", "*"))):
        name = os.path.basename(path)
        if _VIRTUAL_IF.match(name):
            continue
        # Fiziksel kartın sürücü bağı vardır (device); sanal arayüzlerde yok
        if not os.path.exists(os.path.join(path, "device")):
            continue
        out.append(name)
    return out


def mac_address(sys_root: str = "/sys") -> str:
    """Önce bağlantısı açık fiziksel kart, yoksa ilk fiziksel kart. "AA:BB:CC:DD:EE:FF"; yoksa "-"."""
    candidates = []
    for name in physical_interfaces(sys_root):
        base = os.path.join(sys_root, "class", "net", name)
        mac = (_read(os.path.join(base, "address")) or "").upper()
        if not re.match(r"^([0-9A-F]{2}:){5}[0-9A-F]{2}$", mac) or not known(mac):
            continue
        up = (_read(os.path.join(base, "operstate")) or "") == "up"
        wired = not os.path.exists(os.path.join(base, "wireless"))
        candidates.append((0 if up else 1, 0 if wired else 1, name, mac))
    candidates.sort()
    return candidates[0][3] if candidates else "-"


# ── Disk ──
def _root_block(proc_root: str = "/proc") -> Optional[Tuple[int, int, str]]:
    """Kök dosya sisteminin (major, minor, kaynak) değeri /proc/self/mountinfo'dan."""
    text = _read(os.path.join(proc_root, "self", "mountinfo")) or ""
    for line in text.splitlines():
        parts = line.split()
        if len(parts) < 10 or parts[4] != "/":
            continue
        try:
            major, minor = (int(x) for x in parts[2].split(":"))
        except ValueError:
            continue
        sep = parts.index("-") if "-" in parts else -1
        source = parts[sep + 2] if sep > 0 and len(parts) > sep + 2 else ""
        return major, minor, source
    return None


def _disk_of(sys_root: str, major: int, minor: int) -> Optional[str]:
    """Bölüm ya da LVM/dm aygıtının arkasındaki fiziksel disk adı (sda, nvme0n1)."""
    path = os.path.join(sys_root, "dev", "block", "%d:%d" % (major, minor))
    try:
        real = os.path.realpath(path)
    except OSError:
        return None
    seen = 0
    while seen < 8 and os.path.isdir(real):
        seen += 1
        name = os.path.basename(real)
        slaves = sorted(glob.glob(os.path.join(real, "slaves", "*")))
        if slaves:   # dm-crypt / LVM: alttaki aygıta in
            real = os.path.realpath(slaves[0])
            continue
        if os.path.exists(os.path.join(real, "partition")):
            real = os.path.dirname(real)
            continue
        return name
    return None


def disk_serial(sys_root: str = "/sys", proc_root: str = "/proc", run_root: str = "/run",
                dev_root: str = "/dev") -> Tuple[str, bool]:
    """(seri numarası, gerçek mi). Disk seri numarası okunamazsa kök dosya sisteminin UUID'si (Windows'ta birim
    kimliği) döner ve ikinci değer False olur."""
    root = _root_block(proc_root)
    if root:
        major, minor, _source = root
        disk = _disk_of(sys_root, major, minor)
        if disk:
            base = os.path.join(sys_root, "block", disk)
            for rel in ("device/serial", "serial", "device/vpd_pg80"):
                value = _read(os.path.join(base, rel))
                if value and rel.endswith("vpd_pg80"):
                    value = re.sub(r"[^\x20-\x7e]", "", value)
                if value and known(value.strip()):
                    return value.strip(), True
            dev = _read(os.path.join(base, "dev"))
            udev = _read(os.path.join(run_root, "udev", "data", "b" + dev)) if dev else None
            for line in (udev or "").splitlines():
                if line.startswith("E:ID_SERIAL_SHORT=") and known(line.split("=", 1)[1]):
                    return line.split("=", 1)[1].strip(), True
        fs_uuid = _fs_uuid(run_root, dev_root, major, minor)
        if fs_uuid:
            return fs_uuid, False
    return NULL, False


def _fs_uuid(run_root: str, dev_root: str, major: int, minor: int) -> Optional[str]:
    udev = _read(os.path.join(run_root, "udev", "data", "b%d:%d" % (major, minor))) or ""
    for line in udev.splitlines():
        if line.startswith("E:ID_FS_UUID="):
            return line.split("=", 1)[1].strip() or None
    for link in glob.glob(os.path.join(dev_root, "disk", "by-uuid", "*")):
        try:
            st = os.stat(link)
            if os.major(st.st_rdev) == major and os.minor(st.st_rdev) == minor:
                return os.path.basename(link)
        except OSError:
            continue
    return None


# ── Bellek modülleri (SMBIOS tip 17) ──
def parse_smbios_strings(raw: bytes) -> Tuple[bytes, List[str]]:
    """(biçimli alan, metinler). Metinler biçimli alanın ardında NUL ile ayrılır, çift NUL ile biter."""
    if len(raw) < 4:
        return b"", []
    length = raw[1]
    formatted = raw[:length]
    strings = []
    rest = raw[length:]
    for part in rest.split(b"\x00"):
        if not part:
            break
        strings.append(part.decode("latin-1").strip())
    return formatted, strings


def ram_serials(sys_root: str = "/sys") -> str:
    serials = []
    for entry in sorted(glob.glob(os.path.join(sys_root, "firmware", "dmi", "entries", "17-*"))):
        try:
            with open(os.path.join(entry, "raw"), "rb") as f:
                raw = f.read()
        except OSError:
            continue
        formatted, strings = parse_smbios_strings(raw)
        if len(formatted) <= 0x18 or formatted[0] != 17:
            continue
        size = int.from_bytes(formatted[0x0C:0x0E], "little") if len(formatted) >= 0x0E else 0
        if size == 0:   # yuva boş
            continue
        index = formatted[0x18]
        value = strings[index - 1] if 0 < index <= len(strings) else ""
        if value and known(value):
            serials.append(value)
    return ",".join(serials) if serials else NULL


def hardware(sys_root: str = "/sys", proc_root: str = "/proc", run_root: str = "/run", dev_root: str = "/dev") -> Dict:
    dmi = read_dmi(sys_root)
    disk, disk_real = disk_serial(sys_root, proc_root, run_root, dev_root)
    ram = ram_serials(sys_root)
    uuid = normalize_uuid(dmi.get("product_uuid"))
    return {
        "uuid": uuid,
        "bios_sn": system_serial(dmi),
        "disk_sn": disk,
        "mac": mac_address(sys_root),
        "ram_sn": ram,
        "_disk_serial_real": disk_real,
        "_dmi_readable": dmi.get("product_uuid") is not None,
    }


def dna_payload(hw: Dict, os_name: str) -> Dict:
    return {
        "os": os_name,
        "capabilities": {"ram_readable": hw["ram_sn"] != NULL, "disk_serial_real": bool(hw.get("_disk_serial_real")),
                         "dmi_readable": bool(hw.get("_dmi_readable"))},
        "hardware": {k: hw[k] for k in ("uuid", "bios_sn", "disk_sn", "mac", "ram_sn")},
    }


# ── Kimlik ──
_HW_ID = re.compile(r"^HW-[A-Za-z0-9_-]{1,64}$")


def is_hw_id(value: Optional[str]) -> bool:
    return bool(value and _HW_ID.match(value))


def generate_hw_id(uuid: str, mac: str) -> str:
    """Kimlik üretimi (güvenlik özeti değil): Windows ajanıyla aynı biçim, HW- + MD5(UUID + MAC)'in ilk 12 hanesi."""
    if known(uuid) or known(mac):
        raw = ((uuid or "") + (mac or "")).encode("ascii", errors="ignore")
        return "HW-" + hashlib.md5(raw, usedforsecurity=False).hexdigest()[:12].upper()
    return "HW-" + uuidlib.uuid4().hex[:12].upper()


def load_or_create(path: str, hw: Dict) -> str:
    saved = (store.read_text(path) or "").strip()
    if is_hw_id(saved):
        return saved
    new_id = generate_hw_id(hw.get("uuid"), hw.get("mac"))
    try:
        store.write_atomic(path, new_id + "\n")
    except OSError as exc:
        log.error("Kimlik dosyası yazılamadı (%s): %s", path, exc)
    return new_id


def save(path: str, hw_id: str) -> bool:
    if not is_hw_id(hw_id):
        return False
    store.write_atomic(path, hw_id + "\n")
    return True


# ── Donanım bağı ──
def _h(value: str) -> Optional[str]:
    return hashlib.sha256(value.strip().lower().encode("utf-8")).hexdigest() if known(value) else None


MISSING, MATCH, CLONE, PARTIAL, UNREADABLE = "missing", "match", "clone", "partial", "unreadable"


class Binding:
    def __init__(self, path: str):
        self.path = path
        self.changed: List[str] = []
        self.same: List[str] = []
        self.bound_hw_id: Optional[str] = None

    def bind(self, hw_id: str, uuid: str, bios: str, reason: str) -> None:
        try:
            store.write_json(self.path, {"hw_id": hw_id, "uuid_sha256": _h(uuid), "bios_sha256": _h(bios),
                                         "bound_at": int(time.time()), "reason": reason})
        except OSError as exc:
            log.error("Donanım bağı yazılamadı: %s", exc)

    def update_hw_id(self, hw_id: str) -> None:
        try:
            data = store.read_json(self.path)
        except ValueError:
            data = None
        if isinstance(data, dict):
            data["hw_id"] = hw_id
            store.write_json(self.path, data)

    def check(self, uuid: str, bios: str) -> str:
        self.changed, self.same = [], []
        try:
            saved = store.read_json(self.path)
        except ValueError:
            saved = None
        if not isinstance(saved, dict):
            return MISSING
        self.bound_hw_id = saved.get("hw_id")
        for name, value, key in (("uuid", uuid, "uuid_sha256"), ("bios_sn", bios, "bios_sha256")):
            current = _h(value)
            if current is None or not saved.get(key):
                continue
            (self.same if current == saved[key] else self.changed).append(name)
        if not self.changed and not self.same:
            return UNREADABLE
        if self.changed and not self.same:
            return CLONE
        if self.changed:
            return PARTIAL
        return MATCH


# Sunucunun kapattığı yetenekler (capabilities.state.json) kopyada da kapalı kalır: taşınmaz
CLONE_FILES = ("identity", "secret.json", "hw.bind.json", "pending-results.json", "bypass.key", "session.json",
               "software.json")


def move_clone_files(state_dir: str) -> Tuple[str, List[str]]:
    folder = os.path.join(state_dir, "clone-" + time.strftime("%Y%m%dT%H%M%S"))
    store.ensure_dir(folder, 0o700)
    moved = []
    for name in CLONE_FILES:
        src = os.path.join(state_dir, name)
        if os.path.exists(src):
            shutil.move(src, os.path.join(folder, name))
            moved.append(name)
    return folder, moved


def hostname() -> str:
    return socket.gethostname()
