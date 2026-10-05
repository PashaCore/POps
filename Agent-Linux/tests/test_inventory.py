import os
from collections import namedtuple

from conftest import FIXTURES, make_hw, write
from pops_agent import inventory, session

PROC = os.path.join(FIXTURES, "proc")


def read(*parts):
    with open(os.path.join(FIXTURES, *parts), encoding="utf-8") as f:
        return f.read()


def test_os_release_and_version():
    rel = inventory.os_release(os.path.join(FIXTURES, "etc"))
    assert rel["ID"] == "pardus" and rel["ID_LIKE"] == "debian"
    assert inventory.os_name(rel) == "Pardus 23.1"
    assert inventory.os_version(rel, "6.1.0-18-amd64") == "Pardus 23.1 (Linux 6.1.0-18-amd64)"
    debian = {"NAME": "Debian GNU/Linux", "VERSION": "12 (bookworm)"}
    assert inventory.os_name(debian) == "Debian GNU/Linux 12 (bookworm)"
    assert inventory.os_name({}) == "Linux"
    assert inventory.parse_os_release('PRETTY_NAME="A \\"B\\""\n# yorum\nX') == {"PRETTY_NAME": 'A "B"'}


def test_cpu_ram_uptime_load():
    assert inventory.cpu_model(read("proc", "cpuinfo")) == "Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz"
    assert inventory.cpu_model("Hardware\t: BCM2835\nModel\t: Raspberry Pi 4 Model B Rev 1.4\n") == \
        "Raspberry Pi 4 Model B Rev 1.4"
    mem = inventory.meminfo(read("proc", "meminfo"))
    assert mem["MemTotal"] == 16263044 and inventory.ram_text(mem) == "16 GB"
    assert inventory.ram_text({}) == "-"
    assert inventory.uptime_seconds(read("proc", "uptime")) == 3605 and inventory.uptime_seconds("") is None


def test_cpu_percent_from_two_samples():
    before = inventory.cpu_times(read("proc", "stat"))
    assert before == (3699 + 23, 4705 + 356 + 584 + 3699 + 23 + 23)
    after = (before[0] + 50, before[1] + 200)
    assert inventory.cpu_percent(before, after) == 75.0
    assert inventory.cpu_percent(None, after) is None and inventory.cpu_percent(after, after) is None


def test_system_sample_shape():
    sample = inventory.SystemSampler(PROC).sample()
    assert set(sample) == {"cpu_pct", "mem_pct", "disk_root_pct", "uptime_s", "load1"}
    assert sample["mem_pct"] == 25.0 and sample["uptime_s"] == 3605 and sample["load1"] == 0.42


def test_disk_info_windows_format():
    mounts = inventory.real_mounts(read("proc", "self", "mounts"))
    assert mounts == ["/", "/home", "/media/ogrenci/USB Bellek"]   # kök önce; /boot, snap, bind tekrarı yok
    St = namedtuple("St", "f_blocks f_frsize f_bavail")
    gb = 1024 ** 3

    def fake(path):
        return {"/": St(256 * gb // 4096, 4096, 112 * gb // 4096), "/home": St(900 * gb // 4096, 4096, 400 * gb // 4096)
                }.get(path) or (_ for _ in ()).throw(OSError("yok"))

    assert inventory.disk_info(read("proc", "self", "mounts"), fake, lambda p: True) == \
        "/ 112GB Boş / 256GB Toplam | /home 400GB Boş / 900GB Toplam"
    assert inventory.disk_info("", fake) == "-"
    bind = "/dev/sda2 /etc/hostname ext4 rw 0 0\n"   # kapsayıcıda tek dosya bağlaması
    assert inventory.disk_info(bind, fake, lambda p: p != "/etc/hostname") == "-"


def test_gpu_name_from_sysfs(tmp_path):
    dev = tmp_path / "bus" / "pci" / "devices" / "0000:00:02.0"
    write(str(dev / "class"), "0x030000\n")
    write(str(dev / "vendor"), "0x8086\n")
    write(str(dev / "device"), "0x9bc8\n")
    other = tmp_path / "bus" / "pci" / "devices" / "0000:00:1f.3"
    write(str(other / "class"), "0x040300\n")
    ids = write(str(tmp_path / "pci.ids"), "# yorum\n8086  Intel Corporation\n"
                "\t9bc5  CometLake-S GT2 [UHD Graphics 630]\n\t9bc8  CometLake-S GT2 [UHD Graphics 630]\n"
                "10de  NVIDIA Corporation\n")
    assert inventory.gpu_name(str(tmp_path), (ids,)) == "Intel Corporation CometLake-S GT2 [UHD Graphics 630]"
    assert inventory.gpu_name(str(tmp_path), (str(tmp_path / "yok.ids"),)) == "Intel [8086:9bc8]"
    assert inventory.gpu_name(str(tmp_path / "yok")) == "-"


def test_hardware_inventory_shape(tmp_path):
    sys_root = make_hw(str(tmp_path))
    rel = inventory.os_release(os.path.join(FIXTURES, "etc"))
    inv = inventory.hardware_inventory("HW-1", "lab1-pc01", "A4:BB:6D:12:34:56", None, rel, sys_root, PROC)
    # Backend/pops/models.py HwInventoryInput alanları; hepsi metin (model null kabul etmez)
    assert set(inv) == {"hw_id", "hostname", "cpu", "ram", "motherboard", "gpu", "os_version", "ip_address",
                        "mac_address", "disk_info"}
    assert all(isinstance(v, str) and v for v in inv.values())
    assert inv["motherboard"] == "PRIME B460M-A" and inv["ram"] == "16 GB" and inv["os_version"].startswith("Pardus")


def test_dpkg_parse_software_shape():
    items = inventory.parse_dpkg(read("dpkg-query.txt"), lambda name: "20260105" if name == "vlc" else None)
    names = [i["name"] for i in items]
    # yalnızca kurulu (ii) ve kütüphane olmayan paketler; çok mimarili paket bir kez
    assert names == ["firefox-esr", "libreoffice-writer", "pardus-software", "python3-websockets", "wine32"]
    ff = items[0]
    assert ff == {"name": "firefox-esr", "version": "115.9.1esr-1~deb12u1", "publisher": "Debian Mozilla Team",
                  "install_date": None}
    assert items[2]["publisher"] == "Pardus Developers"
    assert inventory.software_hash(items) == inventory.software_hash(list(items))


def test_dpkg_install_date(tmp_path):
    write(str(tmp_path / "vlc.list"), "/.\n")
    write(str(tmp_path / "wine32:i386.list"), "/.\n")
    os.utime(str(tmp_path / "vlc.list"), (1767225600, 1767225600))   # 2026-01-01
    assert inventory.dpkg_install_date("vlc", str(tmp_path)) in ("20251231", "20260101")
    assert inventory.dpkg_install_date("wine32", str(tmp_path)) is not None
    assert inventory.dpkg_install_date("yok", str(tmp_path)) is None


def test_session_choice_and_diff():
    sessions = [
        {"Id": "c1", "Name": "lightdm", "Class": "greeter", "Active": "yes", "Type": "x11", "Seat": "seat0"},
        {"Id": "3", "Name": "uzak", "Class": "user", "Active": "yes", "Type": "tty", "Remote": "yes"},
        {"Id": "4", "Name": "admin", "Class": "user", "Active": "yes", "Type": "tty", "Remote": "no", "Seat": ""},
        {"Id": "5", "Name": "ogrenci", "Class": "user", "Active": "yes", "Type": "x11", "Remote": "no",
         "Seat": "seat0"},
        {"Id": "6", "Name": "eski", "Class": "user", "Active": "no", "Type": "x11", "Seat": "seat0"},
    ]
    assert session.choose(sessions)["Name"] == "ogrenci"
    assert session.choose(sessions[:2]) is None
    assert session.parse_show("Id=5\nName=ogrenci\n") == {"Id": "5", "Name": "ogrenci"}
    a = {"user": "ogrenci", "session": "5", "boot": "b1"}
    assert session.diff(None, a) == [("login", "ogrenci")]
    assert session.diff(a, dict(a)) == []
    assert session.diff(a, {"user": "ali", "session": "7", "boot": "b1"}) == [("logout", "ogrenci"), ("login", "ali")]
    assert session.diff(a, {"user": None, "session": None, "boot": "b1"}) == [("logout", "ogrenci")]
    assert session.diff(a, dict(a, boot="b2")) == [("logout", "ogrenci"), ("login", "ogrenci")]   # yeniden açılış
