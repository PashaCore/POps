"""Donanım envanteri, anlık sistem durumu ve kurulu paketler.

Donanım envanteri sunucunun hw_inventory biçimindedir (POST /api/inventory/{hw_id}, Backend/pops/models.py
HwInventoryInput): cpu, ram, motherboard, gpu, os_version, ip_address, mac_address, disk_info. Her alanın Linux'ta
karşılığı vardır; okunamayan değer Windows ajanındaki gibi "-" gider (model null kabul etmez).

Kurulu yazılım dpkg'dan (POST /api/software/{hw_id}: {items: [{name, version, publisher, install_date}]}).
Kütüphane ve hata ayıklama paketleri (libs, oldlibs, libdevel, debug, introspection bölümleri) gönderilmez: Windows'un
"Programlar ve Özellikler" listesine karşılık gelen, insanın tanıyacağı paketler kalır. install_date, paketin
dpkg dosya listesinin değişme günüdür (YYYYMMDD, Windows kayıt defteriyle aynı biçim).
"""

import glob
import hashlib
import json
import logging
import os
import platform
import re
import socket
import subprocess
import time
from typing import Dict, List, Optional, Tuple

log = logging.getLogger("pops.inventory")


# ── İşletim sistemi ──
def parse_os_release(text: str) -> Dict[str, str]:
    out = {}
    for line in (text or "").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        v = v.strip()
        if len(v) >= 2 and v[0] == v[-1] and v[0] in "\"'":
            v = v[1:-1]
        out[k.strip()] = v.replace('\\"', '"').replace("\\$", "$").replace("\\\\", "\\")
    return out


def os_release(etc_root: str = "/etc") -> Dict[str, str]:
    for path in (os.path.join(etc_root, "os-release"), os.path.join(etc_root, "..", "usr", "lib", "os-release")):
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                return parse_os_release(f.read())
        except OSError:
            continue
    return {}


def os_name(release: Dict[str, str]) -> str:
    name = release.get("PRETTY_NAME") or " ".join(x for x in (release.get("NAME"), release.get("VERSION")) if x)
    return name or "Linux"


def os_version(release: Dict[str, str], kernel: Optional[str] = None) -> str:
    kernel = kernel if kernel is not None else platform.release()
    return "%s (Linux %s)" % (os_name(release), kernel) if kernel else os_name(release)


# ── İşlemci, bellek, disk ──
def cpu_model(cpuinfo: str) -> str:
    for key in ("model name", "Model", "Hardware", "cpu model", "Processor"):
        for line in (cpuinfo or "").splitlines():
            if ":" in line and line.split(":", 1)[0].strip() == key:
                value = re.sub(r"\s+", " ", line.split(":", 1)[1]).strip()
                if value:
                    return value
    return platform.processor() or "-"


def meminfo(text: str) -> Dict[str, int]:
    out = {}
    for line in (text or "").splitlines():
        m = re.match(r"^(\w+):\s+(\d+)\s*kB", line)
        if m:
            out[m.group(1)] = int(m.group(2))
    return out


def ram_text(mem: Dict[str, int]) -> str:
    total = mem.get("MemTotal")
    if not total:
        return "-"
    return "%d GB" % max(1, round(total / (1024 * 1024)))


_PSEUDO_FS = {"tmpfs", "devtmpfs", "proc", "sysfs", "cgroup", "cgroup2", "overlay", "squashfs", "iso9660", "devpts",
              "securityfs", "pstore", "efivarfs", "bpf", "tracefs", "debugfs", "configfs", "fusectl", "mqueue",
              "hugetlbfs", "autofs", "binfmt_misc", "nsfs", "ramfs", "rpc_pipefs", "fuse.gvfsd-fuse", "fuse.portal"}


def real_mounts(mounts_text: str) -> List[str]:
    """Fiziksel disk bölümlerinin bağlama noktaları (kök önce). /boot*, /snap ve loop aygıtları atlanır."""
    out, seen = [], set()
    for line in (mounts_text or "").splitlines():
        parts = line.split()
        if len(parts) < 3:
            continue
        dev, mnt, fstype = parts[0], parts[1].replace("\\040", " "), parts[2]
        if fstype in _PSEUDO_FS or not dev.startswith("/dev/") or dev.startswith("/dev/loop"):
            continue
        if mnt.startswith("/boot") or mnt.startswith("/snap") or mnt.startswith("/run") or dev in seen:
            continue
        seen.add(dev)
        out.append(mnt)
    out.sort(key=lambda m: (m != "/", m))
    return out


def disk_info(mounts_text: str, statvfs=os.statvfs, isdir=os.path.isdir) -> str:
    """Windows biçimi: "C:\\ 112GB Boş / 256GB Toplam | …" → "/ 112GB Boş / 256GB Toplam | /home …".
    Klasör olmayan bağlama noktaları (kapsayıcıdaki /etc/hostname gibi tek dosya bağlamaları) atlanır."""
    parts = []
    gb = 1024 ** 3
    for mnt in real_mounts(mounts_text):
        if not isdir(mnt):
            continue
        try:
            st = statvfs(mnt)
        except OSError:
            continue
        total = st.f_blocks * st.f_frsize
        free = st.f_bavail * st.f_frsize
        if total <= 0:
            continue
        parts.append("%s %dGB Boş / %dGB Toplam" % (mnt, free // gb, total // gb))
    return " | ".join(parts) or "-"


def uptime_seconds(text: str) -> Optional[int]:
    try:
        return int(float((text or "").split()[0]))
    except (IndexError, ValueError):
        return None


def cpu_times(stat_text: str) -> Optional[Tuple[int, int]]:
    """(boşta, toplam) jiffies; /proc/stat'ın ilk "cpu" satırı."""
    for line in (stat_text or "").splitlines():
        if line.startswith("cpu "):
            values = [int(x) for x in line.split()[1:] if x.isdigit()]
            if len(values) < 4:
                return None
            idle = values[3] + (values[4] if len(values) > 4 else 0)
            return idle, sum(values[:8])
    return None


def cpu_percent(before: Optional[Tuple[int, int]], after: Optional[Tuple[int, int]]) -> Optional[float]:
    if not before or not after or after[1] <= before[1]:
        return None
    busy = (after[1] - before[1]) - (after[0] - before[0])
    return round(100.0 * max(0, busy) / (after[1] - before[1]), 1)


def _read(path: str) -> str:
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError:
        return ""


class SystemSampler:
    """Heartbeat'teki "system" bloğu: işlemci (iki örnek arası), bellek, kök disk doluluğu, açık kalma süresi, yük.
    Sunucu bugün saklamıyor (bkz. Agent-Linux/README.md); ileride panel için."""

    def __init__(self, proc_root: str = "/proc"):
        self.proc_root = proc_root
        self._prev = cpu_times(_read(os.path.join(proc_root, "stat")))

    def sample(self) -> Dict:
        now = cpu_times(_read(os.path.join(self.proc_root, "stat")))
        cpu = cpu_percent(self._prev, now)
        self._prev = now or self._prev
        mem = meminfo(_read(os.path.join(self.proc_root, "meminfo")))
        mem_pct = None
        if mem.get("MemTotal") and "MemAvailable" in mem:
            mem_pct = round(100.0 * (1 - mem["MemAvailable"] / mem["MemTotal"]), 1)
        disk_pct = None
        try:
            st = os.statvfs("/")
            if st.f_blocks:
                disk_pct = round(100.0 * (1 - st.f_bavail / st.f_blocks), 1)
        except OSError:
            pass
        load = _read(os.path.join(self.proc_root, "loadavg")).split()
        return {"cpu_pct": cpu, "mem_pct": mem_pct, "disk_root_pct": disk_pct,
                "uptime_s": uptime_seconds(_read(os.path.join(self.proc_root, "uptime"))),
                "load1": float(load[0]) if load else None}


# ── Ekran kartı ──
_VENDORS = {"8086": "Intel", "10de": "NVIDIA", "1002": "AMD", "1af4": "Red Hat (virtio)", "15ad": "VMware",
            "1234": "QEMU", "80ee": "VirtualBox", "1414": "Microsoft (Hyper-V)", "1013": "Cirrus Logic",
            "102b": "Matrox", "1a03": "ASPEED"}


PCI_IDS = ("/usr/share/misc/pci.ids", "/usr/share/hwdata/pci.ids")


def _pci_names(vendor: str, device: str, ids_paths=PCI_IDS) -> Tuple[Optional[str], Optional[str]]:
    for path in ids_paths:
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                vname = None
                for line in f:
                    if vname is None:
                        if line.startswith(vendor + "  "):
                            vname = line[len(vendor) + 2:].strip()
                        continue
                    if line and not line[0].isspace() and not line.startswith("#"):
                        return vname, None
                    if line.startswith("\t" + device + "  "):
                        return vname, line[len(device) + 3:].strip()
                if vname:
                    return vname, None
        except OSError:
            continue
    return None, None


def gpu_name(sys_root: str = "/sys", ids_paths=PCI_IDS) -> str:
    names = []
    for dev in sorted(glob.glob(os.path.join(sys_root, "bus", "pci", "devices", "*"))):
        cls = _read(os.path.join(dev, "class")).strip()
        if not cls.startswith("0x03"):
            continue
        vendor = _read(os.path.join(dev, "vendor")).strip().lower().replace("0x", "")
        device = _read(os.path.join(dev, "device")).strip().lower().replace("0x", "")
        vname, dname = _pci_names(vendor, device, ids_paths)
        vname = vname or _VENDORS.get(vendor, vendor)
        names.append("%s %s" % (vname, dname) if dname else "%s [%s:%s]" % (vname, vendor, device))
    return " + ".join(names) or "-"


# ── Ağ ──
def primary_ipv4(server_host: Optional[str]) -> str:
    """Sunucuya giden yolun kaynak adresi (UDP connect paket göndermez). Debian'da hostname 127.0.1.1'e çözüldüğü
    için Windows'taki gibi ad çözümlemesi kullanılmaz."""
    targets = [server_host] if server_host else []
    targets.append("192.0.2.1")   # belge adresi; yalnızca yönlendirme tablosu için
    for host in targets:
        try:
            infos = socket.getaddrinfo(host, 9, socket.AF_INET, socket.SOCK_DGRAM)
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
                s.connect(infos[0][4])
                ip = s.getsockname()[0]
                if ip and not ip.startswith("127."):
                    return ip
        except OSError:
            continue
    return "-"


def hardware_inventory(hw_id: str, host: str, mac: str, server_host: Optional[str], release: Dict[str, str],
                       sys_root: str = "/sys", proc_root: str = "/proc") -> Dict[str, str]:
    dmi_board = _read(os.path.join(sys_root, "class", "dmi", "id", "board_name")).strip()
    return {
        "hw_id": hw_id,
        "hostname": host,
        "cpu": cpu_model(_read(os.path.join(proc_root, "cpuinfo"))),
        "ram": ram_text(meminfo(_read(os.path.join(proc_root, "meminfo")))),
        "motherboard": dmi_board or "-",
        "gpu": gpu_name(sys_root),
        "os_version": os_version(release),
        "ip_address": primary_ipv4(server_host),
        "mac_address": mac or "-",
        "disk_info": disk_info(_read(os.path.join(proc_root, "self", "mounts"))),
    }


# ── Kurulu paketler (dpkg) ──
DPKG_FORMAT = "${Package}\\t${Version}\\t${Maintainer}\\t${Section}\\t${db:Status-Abbrev}\\n"
_SKIP_SECTIONS = {"libs", "oldlibs", "libdevel", "debug", "introspection"}
MAX_NAME, MAX_VERSION, MAX_PUBLISHER = 300, 100, 200


def parse_dpkg(output: str, install_date=None) -> List[Dict[str, Optional[str]]]:
    items, seen = [], set()
    for line in (output or "").splitlines():
        parts = line.split("\t")
        if len(parts) < 5:
            continue
        name, version, maintainer, section, status = (p.strip() for p in parts[:5])
        if not name or not status.startswith("ii"):
            continue
        if section.split("/")[-1] in _SKIP_SECTIONS:
            continue
        if (name, version) in seen:   # çok mimarili paket (amd64 + i386) bir kez
            continue
        seen.add((name, version))
        publisher = re.sub(r"\s*<[^>]*>\s*$", "", maintainer).strip() or None
        items.append({
            "name": name[:MAX_NAME],
            "version": version[:MAX_VERSION],
            "publisher": publisher[:MAX_PUBLISHER] if publisher else None,
            "install_date": install_date(name) if install_date else None,
        })
    items.sort(key=lambda i: (i["name"], i["version"]))
    return items


def dpkg_install_date(name: str, info_dir: str = "/var/lib/dpkg/info") -> Optional[str]:
    arch_lists = glob.glob(os.path.join(info_dir, glob.escape(name) + ":*.list"))
    for path in [os.path.join(info_dir, name + ".list")] + sorted(arch_lists):
        try:
            return time.strftime("%Y%m%d", time.localtime(os.stat(path).st_mtime))
        except OSError:
            continue
    return None


def installed_packages(timeout: float = 60) -> List[Dict[str, Optional[str]]]:
    proc = subprocess.run(["dpkg-query", "-W", "-f", DPKG_FORMAT], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=timeout, check=False, env={"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C"})
    if proc.returncode != 0:
        raise OSError("dpkg-query %d: %s" % (proc.returncode, proc.stderr.decode("utf-8", "replace").strip()[:200]))
    return parse_dpkg(proc.stdout.decode("utf-8", "replace"), dpkg_install_date)


def software_hash(items: List[Dict]) -> str:
    return hashlib.sha256(json.dumps({"items": items}, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()
