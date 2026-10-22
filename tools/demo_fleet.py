#!/usr/bin/env python3
"""POps demo filosu: herkese açık demo panel için sahte bir okulun bilgisayarları. Durdurulana kadar çalışır.

GÜVENLİK: Bu program HİÇBİR KOMUTU ÇALIŞTIRMAZ. Sunucudan gelen her "execute" isteğine önceden yazılmış, inandırıcı
bir çıktıyla yanıt verir (çıkış kodu 0); kabuk, alt süreç ya da komutun gösterdiği bir dosya işlemi yoktur. Diske
yazdığı tek şey kendi durum dosyasıdır (--state: cihaz anahtarları ve karantina/yetenek durumu).
(It never executes anything locally: every command gets a canned reply.)

Ne yapar (bkz. deploy/demo/README.md):
  - 4 laboratuvarda 48 bilgisayar ("Bilişim Lab 1", "Bilişim Lab 2", "Fen Lab", "Kütüphane"; BL1-PC01 gibi adlar)
    ve 2 öğretmen bilgisayarı. Donanım (işlemci, 8-16 GB bellek, SSD), Windows 10/11 sürümleri, ~40 okul
    programından oluşan yazılım envanteri ve Windows Update durumu (birkaç bilgisayarda bekleyen güncelleme).
  - Gerçek ajan gibi /ws/agent'a bağlanır: kayıt jetonuyla (DEMO_ENROLL_TOKEN) kaydolur, sunucunun verdiği cihaz
    anahtarını saklar ve sonraki bağlantılarda onu kullanır. Bağlantı koparsa geri çekilmeyle yeniden dener
    (gerçek ajanla aynı: tam rastgele, tavan 60 sn). SIGTERM'de bağlantıları düzgün kapatır.
  - Okul takvimi (yerel saat, TZ): ders saatlerinde öğrenci oturumları (çevrimiçi), teneffüste boşta, akşam ve gece
    bilgisayarların bir kısmı kapalı; gün içinde ara sıra kapanıp açılanlar.
  - Bir bilgisayar DNS kural ihlali eşiğinde kendini karantinaya alır; saatte 1-2 kural ihlali (DNS tespiti),
    ara sıra hatalı giriş ve tepsiden yardım masası talebi.
  - Panelden gelen istekler: donanım envanteri, Windows Update tarama/kurma, karantina, yetenek kapatma, bypass
    anahtarı onayı, uyandırma (wake_peer) ve "execute" (yukarıda: yalnızca hazır yanıt).

Ortam / seçenekler:
  DEMO_FLEET_URL       ws://backend:8000 (ajanlar gibi; demo compose'da iç ağdan doğrudan backend'e)
  DEMO_ENROLL_TOKEN    kayıt jetonu (deploy/demo/seed.py oluşturur)
  DEMO_FLEET_STATE     durum dosyası (varsayılan /state/demo-fleet.json; boş = yalnız bellekte)
  DEMO_FLEET_HB        heartbeat aralığı, sn (varsayılan 10)
  DEMO_FLEET_SCHEDULE  school (varsayılan) | always (hepsi her zaman açık, kesinti yok; test için)
  DEMO_FLEET_NIGHT_ON  gece ve hafta sonu açık kalan bilgisayar oranı (varsayılan 0.4)
  python demo_fleet.py --list    filoyu yazdırır, bağlanmaz

Python 3.10+; yalnızca websockets + standart kütüphane (backend imajında ikisi de var).
"""

import argparse
import asyncio
import base64
import datetime
import hashlib
import json
import os
import random
import signal
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from typing import Dict, List, Optional

# ─── Okul ─────────────────────────────────────────────────────────────────────────────────────────────────────────

# (sınıf, ad öneki, bilgisayar sayısı, alt ağ, donanım profili, işletim sistemi)
LABS = (
    ("Bilişim Lab 1", "BL1", 16, "10.20.1", "bl1", "win11edu"),
    ("Bilişim Lab 2", "BL2", 16, "10.20.2", "bl2", "win10pro"),
    ("Fen Lab", "FEN", 10, "10.20.3", "fen", "win11edu"),
    ("Kütüphane", "KUT", 6, "10.20.4", "kut", "win10pro"),
)
TEACHERS = (("Bilişim Lab 1", "BL1-OGRETMEN", "ogretmen.bilisim"), ("Fen Lab", "FEN-OGRETMEN", "ogretmen.fen"))
UNASSIGNED = "Atanmamis_Cihazlar"

HARDWARE = {
    "bl1": ("Intel(R) Core(TM) i5-12400", 16, "B660M DS3H DDR4", "Intel(R) UHD Graphics 730", 512),
    "bl2": ("Intel(R) Core(TM) i5-10400 CPU @ 2.90GHz", 8, "H410M-K", "Intel(R) UHD Graphics 630", 256),
    "fen": ("AMD Ryzen 5 5600G with Radeon Graphics", 16, "B550M-K", "AMD Radeon(TM) Graphics", 512),
    "kut": ("Intel(R) Core(TM) i3-10100 CPU @ 3.60GHz", 8, "H410M-E", "Intel(R) UHD Graphics 630", 256),
    "teacher": ("Intel(R) Core(TM) i7-12700", 16, "PRIME B660M-A D4", "NVIDIA GeForce GT 1030", 1024),
}
OS_NAMES = {
    "win11edu": "Microsoft Windows 11 Education 24H2 (26100.6584)",
    "win11pro": "Microsoft Windows 11 Pro 24H2 (26100.6584)",
    "win10pro": "Microsoft Windows 10 Pro 22H2 (19045.6332)",
}
MAC_PREFIX = {"bl1": "D8:BB:C1", "bl2": "70:85:C2", "fen": "A8:A1:59", "kut": "70:85:C2", "teacher": "04:7C:16"}

# (ad, sürüm, yayıncı, nerede): nerede = "*" her yerde, aksi hâlde profil listesi
SOFTWARE = (
    ("Google Chrome", "141.0.7390.66", "Google LLC", "*"),
    ("Microsoft Edge", "141.0.3537.71", "Microsoft Corporation", "*"),
    ("Microsoft Edge WebView2 Runtime", "141.0.3537.71", "Microsoft Corporation", "*"),
    ("Mozilla Firefox (x64 tr)", "143.0.4", "Mozilla", "bl1 bl2 kut teacher"),
    ("Microsoft Office LTSC Standard 2021 - tr-tr", "16.0.14332.21040", "Microsoft Corporation", "bl1 bl2 fen teacher"),
    ("LibreOffice 25.8.1.1", "25.8.1.1", "The Document Foundation", "bl2 kut"),
    ("Adobe Acrobat Reader (64-bit)", "25.001.20756", "Adobe", "*"),
    ("7-Zip 25.01 (x64)", "25.01", "Igor Pavlov", "*"),
    ("VLC media player", "3.0.21", "VideoLAN", "*"),
    ("Notepad++ (64-bit x64)", "8.8.5", "Notepad++ Team", "bl1 bl2 teacher"),
    ("Python 3.12.10 (64-bit)", "3.12.10150.0", "Python Software Foundation", "bl1 bl2"),
    ("Python Launcher", "3.12.10150.0", "Python Software Foundation", "bl1 bl2"),
    ("Thonny 4.1.7", "4.1.7", "Aivar Annamaa", "bl1 bl2"),
    ("Microsoft Visual Studio Code", "1.105.0", "Microsoft Corporation", "bl1 teacher"),
    ("Git", "2.51.0", "The Git Development Community", "bl1"),
    ("Scratch 3", "3.31.1", "Scratch Foundation", "bl1 bl2"),
    ("mBlock", "5.5.0", "Makeblock", "bl1 bl2"),
    ("Arduino IDE", "2.3.6", "Arduino SA", "bl1 fen"),
    ("Cisco Packet Tracer 8.2.2 64Bit", "8.2.2.0400", "Cisco Systems, Inc.", "bl2"),
    ("GIMP 3.0.4-2", "3.0.4-2", "The GIMP Team", "bl1 bl2"),
    ("Inkscape", "1.4.2", "Inkscape Project", "bl1"),
    ("Audacity 3.7.5", "3.7.5", "Audacity Team", "bl1 bl2 kut"),
    ("GeoGebra Classic", "6.0.904.0", "International GeoGebra Institute", "bl1 bl2 fen teacher"),
    ("Stellarium 25.2", "25.2.0", "Stellarium team", "fen"),
    ("Algodoo", "2.2.4", "Algoryx Simulation AB", "fen"),
    ("Avogadro", "1.2.0", "Avogadro Project", "fen"),
    ("SumatraPDF", "3.5.2", "Krzysztof Kowalczyk", "kut"),
    ("Zoom Workplace", "6.6.2", "Zoom Communications, Inc.", "teacher"),
    ("OBS Studio", "31.1.2", "OBS Project", "teacher"),
    ("ESET Endpoint Security", "12.1.2052.0", "ESET, spol. s r.o.", "*"),
    ("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", "14.44.35211.0",
     "Microsoft Corporation", "*"),
    ("Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0",
     "Microsoft Corporation", "*"),
    ("Microsoft .NET Runtime - 8.0.20 (x64)", "8.0.20", "Microsoft Corporation", "*"),
    ("Microsoft Windows Desktop Runtime - 8.0.20 (x64)", "8.0.20", "Microsoft Corporation", "*"),
    ("Microsoft OneDrive", "25.164.0826.0003", "Microsoft Corporation", "*"),
    ("Microsoft Update Health Tools", "5.72.0.0", "Microsoft Corporation", "*"),
    ("Intel(R) Graphics Driver", "31.0.101.5592", "Intel Corporation", "bl1 bl2 kut"),
    ("AMD Software", "25.9.1", "Advanced Micro Devices, Inc.", "fen"),
    ("NVIDIA Graphics Driver 581.42", "581.42", "NVIDIA Corporation", "teacher"),
    ("Realtek High Definition Audio Driver", "6.0.9733.1", "Realtek Semiconductor Corp.", "*"),
)
# Birkaç bilgisayarda eski Chrome (güncelleme raporunda ve yazılım aramasında farklı sürüm görünsün)
OLD_CHROME = "140.0.7339.208"

APPS = {
    "bl1": ("chrome", "Code", "python", "Scratch 3", "thonny", "WINWORD", "POWERPNT", "msedge", "gimp-3.0", "mBlock"),
    "bl2": ("chrome", "Scratch 3", "thonny", "WINWORD", "EXCEL", "PacketTracer", "msedge", "explorer", "mBlock"),
    "fen": ("GeoGebra", "chrome", "Stellarium", "Algodoo", "EXCEL", "msedge", "POWERPNT", "Avogadro"),
    "kut": ("chrome", "soffice.bin", "msedge", "SumatraPDF", "explorer", "Acrobat"),
    "teacher": ("POWERPNT", "chrome", "WINWORD", "Zoom", "msedge", "explorer", "obs64", "GeoGebra"),
}
# Ders saatleri (başlangıç, bitiş), dakika
LESSONS = ((510, 550), (560, 600), (610, 650), (660, 700), (710, 750), (800, 840), (850, 890), (900, 940))
LUNCH = (750, 800)

# Kural ihlali (DNS) — politika listesi boşsa kullanılır; seed bunları "okul_ozel" kategorisine yazar
DEFAULT_DNS = {"okul_ozel": ["poki.com", "crazygames.com", "friv.com", "y8.com", "roblox.com", "twitch.tv"]}
TICKETS = (
    ("yazici", "Yazıcı çıktı vermiyor", "Gönderdiğim belge kuyrukta bekliyor, yazıcıdan bir şey çıkmıyor."),
    ("donanim", "Fare çalışmıyor", "İmleç hareket etmiyor, kablosu takılı görünüyor."),
    ("donanim", "Kulaklıktan ses gelmiyor", "Kulaklık takılı ama ses yok."),
    ("yazilim", "Scratch açılmıyor", "Scratch 3 simgesine tıklayınca bir şey olmuyor."),
    ("ag", "İnternet çok yavaş", "Sayfalar çok geç açılıyor, video yüklenmiyor."),
    ("hesap", "Şifremi unuttum", "Okul hesabımın şifresini hatırlamıyorum."),
    ("yazilim", "GeoGebra dosyası açılmıyor", "Öğretmenin paylaştığı .ggb dosyası hata veriyor."),
    ("donanim", "Ekranda çizgiler var", "Monitörde yatay çizgiler çıkıyor."),
    ("yazilim", "Python komutu Microsoft Store'u açıyor", "Komut isteminde python yazınca Store açılıyor."),
    ("donanim", "Klavyede Ğ tuşu basmıyor", ""),
)
# Gerçek ajanın reddettiği komutun çıktı öneki (Agent CommandExecutionPolicy.DisabledMessage)
REFUSED = "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası); komut çalıştırılmadı."


def _h(*parts) -> float:
    """Parçalardan türeyen [0, 1) sayısı: aynı girdi her çalıştırmada aynı sonucu verir (yeniden başlatmada da)."""
    raw = "|".join(str(p) for p in parts).encode("utf-8")
    return int.from_bytes(hashlib.sha256(raw).digest()[:8], "big") / 2.0 ** 64


# ─── Cihazlar ─────────────────────────────────────────────────────────────────────────────────────────────────────


@dataclass
class Device:
    hostname: str
    lab: str
    profile: str
    os_key: str
    ip: str
    mac: str
    teacher_user: Optional[str] = None
    old_agent: bool = False
    flaky: bool = False                 # ajan son 1 saatte hata bildiriyor
    quarantine_story: bool = False      # kayıttan sonra DNS eşiğinde kendini karantinaya alır
    old_chrome: bool = False
    pending_updates: int = 0
    reboot_pending: bool = False
    ram_gb: int = 8
    disk_free: int = 100
    install_day: str = "20250901"
    # Çalışma zamanı
    secret: Optional[str] = None
    quarantined: bool = False
    story_done: bool = False
    terminal_enabled: bool = True
    vision_enabled: bool = True
    connected: bool = False
    user: Optional[str] = None
    started_at: int = 0
    forced_off_until: float = 0.0
    forced_on_until: float = 0.0
    last_policy_sync: int = 0
    last_inventory: int = 0
    next_software: float = 0.0
    next_patch: float = 0.0
    next_policy: float = 0.0
    failing: bool = False
    alerts: List[float] = field(default_factory=list)
    patch_state: Optional[dict] = None

    @property
    def hw_id(self) -> str:
        return "HW-" + hashlib.sha256(("pops-demo|" + self.hostname).encode()).hexdigest()[:12].upper()

    @property
    def is_teacher(self) -> bool:
        return self.teacher_user is not None

    def dna(self) -> dict:
        tag = hashlib.sha256(("dna|" + self.hostname).encode()).hexdigest().upper()
        return {
            "hardware": {
                "uuid": "%s-%s-%s-%s-%s" % (tag[:8], tag[8:12], tag[12:16], tag[16:20], tag[20:32]),
                "bios_sn": "SN" + tag[32:42],
                "disk_sn": "S6" + tag[42:52],
                "mac": self.mac,
                "ram_sn": tag[52:60],
            },
            "capabilities": {"ram_readable": True},
        }


def build_fleet() -> List[Device]:
    """Okulun bütün bilgisayarları; her çalıştırmada aynı (seed.py de bunu kullanır)."""
    devices = []
    for lab, prefix, count, net, profile, os_key in LABS:
        for n in range(1, count + 1):
            host = "%s-PC%02d" % (prefix, n)
            d = Device(host, lab, profile, os_key, "%s.%d" % (net, 20 + n), "", ram_gb=HARDWARE[profile][1])
            devices.append(d)
    for lab, host, user in TEACHERS:
        net = next(x[3] for x in LABS if x[0] == lab)
        devices.append(Device(host, lab, "teacher", "win11pro", net + ".10", "", teacher_user=user, ram_gb=16))
    for d in devices:
        tag = hashlib.sha256(("mac|" + d.hostname).encode()).hexdigest().upper()
        d.mac = "%s:%s:%s:%s" % (MAC_PREFIX[d.profile], tag[0:2], tag[2:4], tag[4:6])
        disk = HARDWARE[d.profile][4]
        d.disk_free = int(disk * (0.35 + 0.45 * _h(d.hostname, "disk")))
        d.install_day = "2025%02d%02d" % (8 + int(_h(d.hostname, "m") * 2), 1 + int(_h(d.hostname, "d") * 27))
        d.vision_enabled = not d.is_teacher      # öğretmen bilgisayarlarında uzak ekran kapalı (yetenek politikası)
    by_name = {d.hostname: d for d in devices}
    for name in ("BL2-PC03", "BL2-PC11", "KUT-PC05"):
        by_name[name].old_agent = True
    for name in ("BL2-PC02", "BL2-PC09", "KUT-PC03"):
        by_name[name].old_chrome = True
    by_name["BL2-PC14"].ram_gb = 16                    # sonradan bellek eklenmiş
    by_name["FEN-PC06"].flaky = True
    by_name["BL2-PC07"].quarantine_story = True
    for name, pending, reboot in (("BL2-PC04", 3, False), ("BL2-PC10", 2, False), ("BL2-PC15", 4, True),
                                  ("KUT-PC02", 2, False), ("KUT-PC06", 1, False), ("FEN-PC09", 0, True)):
        by_name[name].pending_updates = pending
        by_name[name].reboot_pending = reboot
    return devices


# ─── Yükler ───────────────────────────────────────────────────────────────────────────────────────────────────────


def inventory_payload(d: Device) -> dict:
    cpu, _ram, board, gpu, disk = HARDWARE[d.profile]
    total = {256: 237, 512: 476, 1024: 953}[disk]
    return {
        "hw_id": d.hw_id, "hostname": d.hostname, "cpu": cpu, "ram": "%d GB" % d.ram_gb, "motherboard": board,
        "gpu": gpu, "os_version": OS_NAMES[d.os_key], "ip_address": d.ip, "mac_address": d.mac,
        "disk_info": "C:\\ %dGB Boş / %dGB Toplam" % (min(d.disk_free, total - 20), total),
    }


def software_payload(d: Device, agent_version: str) -> dict:
    items = []
    for name, version, publisher, where in SOFTWARE:
        if where != "*" and d.profile not in where.split():
            continue
        if name == "Google Chrome" and d.old_chrome:
            version = OLD_CHROME
        items.append({"name": name, "version": version, "publisher": publisher, "install_date": d.install_day})
    items.append({"name": "POps Agent", "version": agent_version.split("-")[0], "publisher": "PashaCore",
                  "install_date": d.install_day})
    return {"items": items}


def _updates_for(d: Device) -> List[dict]:
    win10 = d.os_key == "win10pro"
    pool = [
        {"kb": "KB5066791", "severity": "Critical", "categories": ["Security Updates"], "is_security": True,
         "title": ("x64 tabanlı Sistemler için Windows 10 Version 22H2 2026-09 Toplu Güncelleştirmesi (KB5066791)"
                   if win10 else "x64 tabanlı Sistemler için Windows 11 Version 24H2 2026-09 Toplu Güncelleştirmesi "
                   "(KB5066835)")},
        {"kb": "KB5066131", "severity": "Important", "categories": ["Security Updates"], "is_security": True,
         "title": ".NET Framework 3.5 ve 4.8.1 için 2026-09 Toplu Güncelleştirme (KB5066131)"},
        {"kb": "KB890830", "severity": "Important", "categories": ["Update Rollups"], "is_security": False,
         "title": "Windows Kötü Amaçlı Yazılımları Temizleme Aracı x64 - v5.137 (KB890830)"},
        {"kb": "KB2267602", "severity": None, "categories": ["Definition Updates"], "is_security": False,
         "title": "Microsoft Defender Antivirus için Güvenlik Bilgileri Güncelleştirmesi - KB2267602 "
                  "(Sürüm 1.439.152.0)"},
    ]
    return pool[:d.pending_updates]


def patch_payload(d: Device, now: datetime.datetime) -> dict:
    if d.patch_state is None:
        last_install = (now - datetime.timedelta(days=3 + int(_h(d.hostname, "wu") * 9))).replace(hour=3, minute=12)
        d.patch_state = {"updates": _updates_for(d), "reboot_required": d.reboot_pending,
                         "last_install": last_install.astimezone().isoformat(),
                         "last_result": "2 güncelleme kuruldu" if d.reboot_pending else None}
    ups = d.patch_state["updates"]
    return {
        "pending_count": len(ups),
        "pending_security": sum(1 for u in ups if u["is_security"]),
        "pending_critical": sum(1 for u in ups if u["severity"] == "Critical"),
        "reboot_required": d.patch_state["reboot_required"],
        "last_search": now.astimezone().isoformat(),
        "last_install": d.patch_state["last_install"],
        "last_result": d.patch_state["last_result"],
        "updates": ups,
    }


# ─── Hazır komut yanıtları (hiçbir şey çalıştırılmaz) ──────────────────────────────────────────────────────────────


def canned_output(cmd: str, d: Device):
    """(çıktı, yan etki): yan etki None | "reboot" | "shutdown". Komut ÇALIŞTIRILMAZ, yalnızca metne bakılır."""
    c = (cmd or "").strip()
    low = c.lower()
    gw = d.ip.rsplit(".", 1)[0] + ".1"
    if low.startswith("shutdown") and ("/r" in low or "-r" in low) or "restart-computer" in low:
        return "", "reboot"
    if low.startswith("shutdown") and ("/s" in low or "-s" in low) or "stop-computer" in low:
        return "", "shutdown"
    if low.startswith("shutdown") or low.startswith("msg ") or low.startswith("powercfg"):
        return "", None
    if "flushdns" in low:
        return "\r\nWindows IP Yapılandırması\r\n\r\nDNS Çözümleyici Önbelleği başarıyla temizlendi.\r\n", None
    if low.startswith("ipconfig"):
        return ("\r\nWindows IP Yapılandırması\r\n\r\n\r\nEthernet bağdaştırıcısı Ethernet:\r\n\r\n"
                "   Bağlantıya özgü DNS Soneki . . . : okul.local\r\n"
                "   IPv4 Adresi. . . . . . . . . . . : %s\r\n"
                "   Alt Ağ Maskesi . . . . . . . . . : 255.255.255.0\r\n"
                "   Varsayılan Ağ Geçidi . . . . . . : %s\r\n" % (d.ip, gw)), None
    if low == "hostname":
        return d.hostname + "\r\n", None
    if low.startswith("whoami"):
        return "nt authority\\system\r\n", None
    if low.startswith("systeminfo"):
        mem = "{:,}".format(d.ram_gb * 1024).replace(",", ".")
        return ("\r\nAna Bilgisayar Adı:          %s\r\nİşletim Sistemi Adı:         %s\r\n"
                "Sistem Üreticisi:            To Be Filled By O.E.M.\r\n"
                "İşlemci(ler):                1 İşlemci Yüklü.\r\n"
                "                             [01]: %s\r\nToplam Fiziksel Bellek:      %s MB\r\n"
                "Etki Alanı:                  okul.local\r\n"
                % (d.hostname, OS_NAMES[d.os_key].split(" (")[0], HARDWARE[d.profile][0], mem)), None
    if low.startswith("gpupdate"):
        return ("İlke güncelleştiriliyor...\r\n\r\nBilgisayar İlkesi güncelleştirmesi başarıyla tamamlandı.\r\n"
                "Kullanıcı İlkesi güncelleştirmesi başarıyla tamamlandı.\r\n"), None
    if low.startswith("w32tm"):
        return "Yeniden eşitleme komutu yerel bilgisayara gönderiliyor\r\nKomut başarıyla tamamlandı.\r\n", None
    if "winget" in low:
        pkg = c.split()[-1] if len(c.split()) > 2 else "paket"
        return ("Bulundu %s\r\nBu uygulamanın lisansı size sahibi tarafından verilmektedir.\r\n"
                "İndiriliyor ...\r\n  ██████████████████████████████  100%%\r\n"
                "Yükleyici karması başarıyla doğrulandı\r\nPaket yüklemesi başlatılıyor...\r\nBaşarıyla yüklendi\r\n"
                % pkg), None
    if "cleanmgr" in low or "temp" in low or "remove-item" in low or low.startswith("del "):
        mb = 180 + int(_h(d.hostname, c, int(time.time() // 3600)) * 1400)
        return "Geçici dosyalar temizlendi: %d dosya, %d MB boşaltıldı.\r\n" % (mb // 3, mb), None
    if low.startswith("tasklist"):
        return ("\r\nGörüntü Adı                    PID Oturum Adı        Oturum#    Bellek Kull.\r\n"
                "========================= ======== ================ =========== ============\r\n"
                "System Idle Process              0 Services                   0          8 K\r\n"
                "explorer.exe                  6120 Console                    1    142.380 K\r\n"
                "chrome.exe                    7344 Console                    1    296.112 K\r\n"
                "POps.Agent.exe                3312 Services                   0     61.204 K\r\n"), None
    if low.startswith("ping"):
        return ("\r\n%s [%s] 32 bayt veri ile ping ediliyor:\r\n" % (gw, gw)
                + "".join("%s yanıtı: bayt=32 süre=1ms TTL=64\r\n" % gw for _ in range(4))
                + "\r\n%s için Ping istatistiği:\r\n" % gw
                + "    Paket: Giden = 4, Gelen = 4, Kaybolan = 0 (%0 kayıp),\r\n"), None
    if "get-volume" in low or "logicaldisk" in low or "fsutil" in low:
        total = {256: 237, 512: 476, 1024: 953}[HARDWARE[d.profile][4]]
        return ("DriveLetter FileSystem SizeRemaining      Size\r\n----------- ---------- -------------      ----\r\n"
                "C           NTFS              %d GB    %d GB\r\n" % (d.disk_free, total)), None
    if low.startswith("echo "):
        return c[5:].strip().strip('"') + "\r\n", None
    if low.startswith("net user"):
        return ("\r\n\\\\%s için kullanıcı hesapları\r\n\r\n-----------------------------------------------\r\n"
                "Administrator            DefaultAccount           Guest\r\n"
                "ogrenci                  WDAGUtilityAccount\r\n"
                "Komut başarıyla tamamlandı.\r\n" % d.hostname), None
    return "Komut başarıyla tamamlandı.\r\n", None


# ─── Takvim ───────────────────────────────────────────────────────────────────────────────────────────────────────


def local_now() -> datetime.datetime:
    return datetime.datetime.now()


class Calendar:
    def __init__(self, mode: str, night_on: float):
        self.mode = mode
        self.night_on = night_on

    def school_day(self, now: datetime.datetime) -> bool:
        return self.mode == "always" or now.weekday() < 5

    def want_on(self, d: Device, now: datetime.datetime, wall: float) -> bool:
        if wall < d.forced_off_until:
            return False
        if wall < d.forced_on_until:
            return True
        minute = now.hour * 60 + now.minute
        block = now.strftime("%Y%m%d") + "-%d" % (minute // 15)
        churn = _h(d.hostname, "churn", block) < 0.04
        if self.mode == "always":
            return True
        if d.profile == "kut":
            start, end = 470, 1065
        elif d.is_teacher:
            start, end = 475, 990
        else:
            start, end = 480, 975
        start += int(_h(d.hostname, now.date(), "on") * 25)
        end += int(_h(d.hostname, now.date(), "off") * 40)
        if self.school_day(now) and start <= minute < end:
            return not churn
        # Akşam, gece ve hafta sonu: bir kısmı açık bırakılmış (boşta), sabaha kadar; saatlik küçük kesintiler olur
        evening = now.date() if minute >= 720 else now.date() - datetime.timedelta(days=1)
        owl = _h(d.hostname, evening, "owl")
        if owl >= self.night_on or d.is_teacher:
            return False
        return _h(d.hostname, "night", now.strftime("%Y%m%d%H")) >= 0.1

    def lesson(self, now: datetime.datetime) -> Optional[int]:
        minute = now.hour * 60 + now.minute
        for i, (a, b) in enumerate(LESSONS):
            if a <= minute < b:
                return i
        return None

    def user_for(self, d: Device, now: datetime.datetime) -> Optional[str]:
        """Bilgisayarda oturum açmış kullanıcı (yoksa None = boşta)."""
        if not self.school_day(now) or d.quarantined:
            return None
        minute = now.hour * 60 + now.minute
        day = now.strftime("%Y%m%d")
        if d.is_teacher:
            return d.teacher_user if 495 <= minute < 945 and not (LUNCH[0] <= minute < LUNCH[1]) else None
        if d.profile == "kut":
            if not (480 <= minute < 1050):
                return None
            slot = minute // 20
            if _h(d.hostname, day, slot, "kut") < 0.5:
                return "ogr%04d" % (1000 + int(_h(d.hostname, day, slot, "who") * 8999))
            return None
        idx = self.lesson(now)
        if idx is None or _h(d.lab, day, idx, "used") >= 0.7 or _h(d.hostname, day, idx, "seat") >= 0.9:
            return None
        return "ogr%04d" % (1000 + int(_h(d.hostname, day, idx, "who") * 8999))

    def busy_hours(self, now: datetime.datetime) -> bool:
        minute = now.hour * 60 + now.minute
        return self.school_day(now) and 500 <= minute < 950


# ─── Filo ─────────────────────────────────────────────────────────────────────────────────────────────────────────


def log(msg: str) -> None:
    print("%s demo-fleet: %s" % (datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S"), msg), flush=True)


def backoff(attempt: int) -> float:
    # POps.Shared.ReconnectBackoff ile aynı: tam rastgele, tavan 60 sn
    return random.random() * min(60.0, 2.0 * (2 ** min(attempt, 5)))


def bypass_fingerprint(key: str) -> str:
    raw = base64.urlsafe_b64decode(key + "=" * (-len(key) % 4))
    return hashlib.sha256(raw).hexdigest()[:16]


class Fleet:
    def __init__(self, args):
        self.args = args
        self.ws_base = args.url.rstrip("/")
        self.http_base = self.ws_base.replace("wss://", "https://").replace("ws://", "http://")
        self.cal = Calendar(args.schedule, args.night_on)
        self.devices = build_fleet()
        self.by_hw = {d.hw_id: d for d in self.devices}
        self.stop = asyncio.Event()
        self.pool = ThreadPoolExecutor(max_workers=8)
        self.version = "0.1.0"
        self.dns = dict(DEFAULT_DNS)
        self.sockets: Dict[str, object] = {}
        self.locks: Dict[str, asyncio.Lock] = {}
        self._load_state()

    # ---- durum dosyası
    def _load_state(self):
        if not self.args.state or not os.path.exists(self.args.state):
            return
        try:
            with open(self.args.state, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError) as e:
            log("durum dosyası okunamadı, baştan: %r" % e)
            return
        for hw, st in (data.get("devices") or {}).items():
            d = self.by_hw.get(hw)
            if d is None:
                continue
            d.secret = st.get("secret") or None
            d.quarantined = bool(st.get("quarantined"))
            d.story_done = bool(st.get("story_done"))
            d.terminal_enabled = st.get("terminal", True) is not False
            d.vision_enabled = st.get("vision", d.vision_enabled) is not False

    def save_state(self):
        if not self.args.state:
            return
        data = {"devices": {d.hw_id: {"secret": d.secret, "quarantined": d.quarantined, "story_done": d.story_done,
                                      "terminal": d.terminal_enabled, "vision": d.vision_enabled}
                            for d in self.devices}}
        tmp = self.args.state + ".tmp"
        try:
            fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(data, f)
            os.replace(tmp, self.args.state)
        except OSError as e:
            log("durum dosyası yazılamadı: %r" % e)

    # ---- HTTP (ajan uçları)
    def _http(self, method: str, path: str, body, headers: dict, timeout: float = 20):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.http_base + path, data=data, method=method,
                                     headers={"Content-Type": "application/json", **headers})
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read()
                return resp.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as e:
            return e.code, None
        except (OSError, ValueError):
            return 0, None

    async def call(self, d: Optional[Device], method: str, path: str, body=None):
        headers = {}
        if d is not None:
            headers["X-Forwarded-For"] = d.ip
            if d.secret:
                headers.update({"X-Agent-Id": d.hw_id, "X-Agent-Secret": d.secret})
        loop = asyncio.get_running_loop()
        return await loop.run_in_executor(self.pool, self._http, method, path, body, headers)

    async def post(self, d: Device, path: str, body) -> bool:
        if not d.secret:
            return False
        status, _ = await self.call(d, "POST", path, body)
        if status != 200:
            log("%s %s -> HTTP %s" % (d.hostname, path, status))
        return status == 200

    # ---- WebSocket
    async def send(self, d: Device, msg: dict) -> None:
        ws = self.sockets.get(d.hw_id)
        if ws is None:
            return
        async with self.locks.setdefault(d.hw_id, asyncio.Lock()):
            await ws.send(json.dumps(msg, ensure_ascii=False))

    def heartbeat(self, d: Device, now: datetime.datetime, first: bool = False) -> dict:
        user = d.user
        app = "-"
        if user:
            apps = APPS[d.profile]
            app = apps[int(_h(d.hostname, user, int(time.time() // 150)) * len(apps))]
        msg = {
            "hw_id": d.hw_id, "hostname": d.hostname, "lab_name": UNASSIGNED,
            "status": "Online" if user else "Idle", "active_window": app, "quarantined": d.quarantined,
            "agent_health": {
                "started_at": d.started_at, "last_policy_sync": d.last_policy_sync or None,
                "last_inventory_upload": d.last_inventory or None, "tray_connected": bool(user),
                "vision_channel": "idle" if d.vision_enabled else "off",
                "loop_errors_1h": 3 if d.flaky else 0,
                "last_error": "Windows Update taraması zaman aşımına uğradı (0x8024401C)" if d.flaky else None,
                "screen_locked": d.quarantined, "network_isolated": d.quarantined,
            },
        }
        if first:
            msg["dna_payload"] = d.dna()
        return msg

    async def update_user(self, d: Device, now: datetime.datetime) -> None:
        want = self.cal.user_for(d, now)
        if want == d.user or not d.secret:
            return
        if d.user:
            await self.post(d, "/api/auth/logout", {"hw_id": d.hw_id, "hostname": d.hostname, "student_id": d.user})
        if want:
            await self.post(d, "/api/auth/login", {"hw_id": d.hw_id, "hostname": d.hostname, "student_id": want})
        d.user = want

    async def uploads(self, d: Device, wall: float) -> None:
        """Yazılım envanteri (6 saatte bir), Windows Update durumu (günde bir) ve politika (10 dakikada bir)."""
        if not d.secret:
            return
        now = local_now()
        # Zaman damgası istekten önce ilerler: aynı anda çalışan ikinci çağrı aynı listeyi yeniden göndermesin
        if wall >= d.next_software:
            d.next_software = wall + 6 * 3600
            if await self.post(d, "/api/software/" + d.hw_id, software_payload(d, self.agent_version(d))):
                d.last_inventory = int(wall)
        if wall >= d.next_patch:
            d.next_patch = wall + 24 * 3600
            await self.post(d, "/api/patches/" + d.hw_id, patch_payload(d, now))
        if wall >= d.next_policy:
            d.next_policy = wall + 600
            status, pol = await self.call(d, "GET", "/api/agent_policies")
            if status == 200 and isinstance(pol, dict):
                d.last_policy_sync = int(wall)
                domains = {k: v for k, v in (pol.get("dns_domains") or {}).items() if v}
                if domains:
                    self.dns = domains

    def agent_version(self, d: Device) -> str:
        if not d.old_agent:
            return self.version
        base, _, rest = self.version.partition("-")
        parts = base.split(".")
        if len(parts) == 3 and parts[2].isdigit() and int(parts[2]) > 0:
            parts[2] = str(int(parts[2]) - 1)
        return ".".join(parts) + ("-" + rest if rest else "")

    async def handle(self, d: Device, msg: dict) -> None:
        action = msg.get("action")
        if action == "set_secret" and msg.get("secret"):
            d.secret = msg["secret"]
            self.save_state()
            log("%s kaydoldu (%s)" % (d.hostname, d.hw_id))
            asyncio.ensure_future(self.after_enroll(d))
        elif action == "get_hardware":
            if await self.post(d, "/api/inventory/" + d.hw_id, inventory_payload(d)):
                d.last_inventory = int(time.time())
        elif action == "execute":
            asyncio.ensure_future(self.execute(d, msg))
        elif action == "lockdown":
            d.quarantined = True
            self.save_state()
        elif action == "unlock":
            d.quarantined = False
            self.save_state()
        elif action == "set_capabilities":
            if msg.get("terminal_enabled") is False:
                d.terminal_enabled = False
            if msg.get("vision_enabled") is False:
                d.vision_enabled = False
            self.save_state()
            await self.send(d, self.capabilities(d))
        elif action == "set_bypass_secret" and msg.get("secret"):
            await self.send(d, {"type": "bypass_secret_ack", "fingerprint": bypass_fingerprint(msg["secret"])})
        elif action == "scan_updates":
            asyncio.ensure_future(self.patch_later(d, None))
        elif action == "install_updates":
            asyncio.ensure_future(self.patch_later(d, msg.get("scope") or "security"))
        elif action in ("start_vision_session", "start_stream", "get_thumbnail") and not d.vision_enabled:
            await self.send(d, {"type": "capability_denied", "capability": "vision", "action": action})
        elif action == "wake_peer":
            target = next((x for x in self.devices if x.mac.lower() == str(msg.get("mac") or "").lower()), None)
            if target is not None and not target.connected:
                target.forced_off_until = 0.0
                target.forced_on_until = time.time() + 2 * 3600 + random.uniform(15, 40)
        # server_info, result_ack, update_result_ack, stop_stream, cancel_task, update_agent: yanıt gerekmez

    def capabilities(self, d: Device) -> dict:
        return {"type": "capabilities", "terminal_enabled": d.terminal_enabled, "vision_enabled": d.vision_enabled,
                "server_ca": "system"}

    async def execute(self, d: Device, msg: dict) -> None:
        tid = msg.get("task_id")
        if not isinstance(tid, int):
            return
        if not d.terminal_enabled:
            # Yönetici bu bilgisayarda uzak komutu kapattıysa gerçek ajan gibi reddedilir
            await self.send(d, {"type": "result", "pc_name": d.hw_id, "task_id": tid, "output": REFUSED,
                                "exit_code": -5})
            await self.send(d, {"type": "capability_denied", "capability": "terminal", "action": "execute",
                                "task_id": tid})
            return
        output, effect = canned_output(str(msg.get("script_path") or ""), d)
        slow = "winget" in str(msg.get("script_path") or "").lower()
        await asyncio.sleep(random.uniform(8, 15) if slow else random.uniform(0.8, 3.5))
        try:
            await self.send(d, {"type": "result", "pc_name": d.hw_id, "task_id": tid, "output": output,
                                "exit_code": 0})
        except Exception:
            return
        if effect == "reboot":
            d.forced_off_until = time.time() + random.uniform(45, 90)
        elif effect == "shutdown":
            d.forced_on_until = 0.0
            d.forced_off_until = time.time() + random.uniform(20, 40) * 60

    async def patch_later(self, d: Device, scope: Optional[str]) -> None:
        await asyncio.sleep(random.uniform(30, 90) if scope else random.uniform(5, 20))
        now = local_now()
        payload = patch_payload(d, now)
        if scope:
            before = list(d.patch_state["updates"])
            keep = [u for u in before if scope == "security" and not u["is_security"]]
            installed = len(before) - len(keep)
            d.patch_state["updates"] = keep
            if installed:
                d.patch_state["reboot_required"] = True
                d.patch_state["last_install"] = now.astimezone().isoformat()
                d.patch_state["last_result"] = "%d güncelleme kuruldu; yeniden başlatma gerekiyor" % installed
            payload = patch_payload(d, now)
        await self.post(d, "/api/patches/" + d.hw_id, payload)

    async def after_enroll(self, d: Device) -> None:
        """Kayıttan hemen sonra: envanter ve (hikâyesi olan bilgisayarda) DNS eşiğinde kendini karantinaya alma."""
        await self.uploads(d, time.time())
        if not d.quarantine_story or d.story_done:
            return
        domains = [x for v in self.dns.values() for x in v] or DEFAULT_DNS["okul_ozel"]
        category = next(iter(self.dns), "okul_ozel")
        for dom in domains[:3]:
            await asyncio.sleep(8)
            await self.post(d, "/api/policy_alert", {"hw_id": d.hw_id, "domain": dom, "category": category})
        # Önce kendi durumu: araya giren heartbeat "kilitli değil" bildirip paneli geri çevirmesin
        d.quarantined = True
        d.story_done = True
        self.save_state()
        await self.post(d, "/api/logs/" + d.hw_id, {
            "log_type": "Security", "message": "Cihaz DNS kural ihlali eşiğinde kendini karantinaya aldı",
            "actor_id": "Agent", "event_type": "agent.auto_quarantine", "category": "security",
            "action": "auto_quarantine", "risk_level": "high", "reason": "3 ihlal / 1 saat (%s)" % category,
        })
        log("%s DNS eşiğinde karantinaya alındı" % d.hostname)

    # ---- bir bilgisayarın yaşamı
    async def run_device(self, d: Device, delay: float) -> None:
        await self.wait(delay)
        attempt = 0
        while not self.stop.is_set():
            wall = time.time()
            # Kaydı olmayan bilgisayar takvimden bağımsız bir kez bağlanır (kurulum günü), sonra takvime uyar
            if not d.secret or self.cal.want_on(d, local_now(), wall):
                ok = await self.session(d)
                if self.stop.is_set():
                    break
                attempt = 0 if ok else attempt + 1
                if not ok:
                    await self.wait(backoff(attempt))
            else:
                await self.wait(20 + random.uniform(0, 20))

    async def wait(self, seconds: float) -> None:
        try:
            await asyncio.wait_for(self.stop.wait(), timeout=max(0.0, seconds))
        except asyncio.TimeoutError:
            pass

    async def session(self, d: Device) -> bool:
        """Bir bağlantı ömrü. Dönen: bağlantı sağlam kuruldu ve düzgün bitti mi."""
        import websockets

        headers = {"X-Agent-Version": self.agent_version(d)}
        if self.args.fake_ips:
            headers["X-Forwarded-For"] = d.ip
        used_secret = bool(d.secret)
        if d.secret:
            headers["X-Agent-Secret"] = d.secret
        elif self.args.enroll_token:
            headers["X-Enroll-Token"] = self.args.enroll_token
        uri = "%s/ws/agent/%s" % (self.ws_base, d.hw_id)
        try:
            try:
                conn = websockets.connect(uri, open_timeout=20, close_timeout=3, additional_headers=headers)
            except TypeError:   # websockets < 14
                conn = websockets.connect(uri, open_timeout=20, close_timeout=3, extra_headers=headers)
            async with conn as ws:
                self.sockets[d.hw_id] = ws
                d.failing = False
                d.started_at = int(time.time())
                d.connected = True
                now = local_now()
                await self.send(d, self.heartbeat(d, now, first=True))
                await self.send(d, self.capabilities(d))
                receiver = asyncio.ensure_future(self.receive(d, ws))
                try:
                    stable = False
                    while not self.stop.is_set() and not receiver.done():
                        wall = time.time()
                        now = local_now()
                        if d.secret and not self.cal.want_on(d, now, wall):
                            break
                        await self.update_user(d, now)
                        await self.send(d, self.heartbeat(d, now))
                        await self.uploads(d, wall)
                        stable = True
                        await self.wait(self.args.hb * random.uniform(0.8, 1.2))
                    if not receiver.done():
                        # Kapanırken oturum kapanır; kopan bağlantıda kullanıcı bilinir kalır ve yeniden bağlanınca
                        # update_user sunucudaki kaydı düzeltir
                        if d.user:
                            await self.post(d, "/api/auth/logout",
                                            {"hw_id": d.hw_id, "hostname": d.hostname, "student_id": d.user})
                            d.user = None
                        await ws.close(code=1001, reason="Bilgisayar kapaniyor")
                        return True
                    code = receiver.result()
                    if code in (4401, 4403) and used_secret:
                        log("%s anahtarı reddedildi (%s); kayıt jetonuyla yeniden denenecek" % (d.hostname, code))
                        d.secret = None
                        self.save_state()
                    elif code in (4401, 4403, 4409):
                        log("%s bağlantısı reddedildi (%s)" % (d.hostname, code))
                        await self.wait(300)
                    return stable and code in (1000, 1001)
                finally:
                    receiver.cancel()
        except Exception as e:
            if not self.stop.is_set() and not d.failing:
                d.failing = True   # aynı bilgisayarın art arda hataları bir kez yazılır
                log("%s bağlanamadı: %s" % (d.hostname, (repr(e) or type(e).__name__)[:160]))
            return False
        finally:
            self.sockets.pop(d.hw_id, None)
            d.connected = False

    async def receive(self, d: Device, ws) -> int:
        import websockets

        try:
            async for raw in ws:
                try:
                    msg = json.loads(raw)
                except ValueError:
                    continue
                if isinstance(msg, dict):
                    try:
                        await self.handle(d, msg)
                    except Exception as e:   # tek bir mesaj bağlantıyı düşürmesin
                        log("%s mesajı işlenemedi: %r" % (d.hostname, e))
            return 1000
        except websockets.exceptions.ConnectionClosed as e:
            rcvd = getattr(e, "rcvd", None)
            return getattr(rcvd, "code", None) or getattr(e, "code", 1006) or 1006

    # ---- filo çapında olaylar
    async def events(self) -> None:
        """Ders saatinde: saatte ~1,5 kural ihlali, ~0,8 hatalı giriş, ~0,35 yardım masası talebi."""
        tick = 30.0
        while not self.stop.is_set():
            await self.wait(tick)
            now = local_now()
            if self.stop.is_set() or not self.cal.busy_hours(now):
                continue
            wall = time.time()
            students = [d for d in self.devices
                        if d.connected and d.secret and d.user and not d.is_teacher and not d.quarantined]
            if not students:
                continue
            if random.random() < 1.5 * tick / 3600:
                cands = [d for d in students if len([t for t in d.alerts if wall - t < 3600]) < 2]
                if cands:
                    d = random.choice(cands)
                    category = random.choice(list(self.dns))
                    dom = random.choice(self.dns[category])
                    if await self.post(d, "/api/policy_alert", {"hw_id": d.hw_id, "domain": dom, "category": category}):
                        d.alerts = [t for t in d.alerts if wall - t < 3600] + [wall]
                        log("%s kural ihlali: %s" % (d.hostname, dom))
            if random.random() < 0.8 * tick / 3600:
                d = random.choice(students)
                await self.post(d, "/api/auth/failed", {"hw_id": d.hw_id, "hostname": d.hostname,
                                                        "student_id": "ogr%04d" % random.randint(1000, 9999),
                                                        "message": "Kullanıcı adı veya parola yanlış"})
            if random.random() < 0.35 * tick / 3600:
                d = random.choice(students)
                category, subject, body = random.choice(TICKETS)
                await self.post(d, "/api/tickets/agent/" + d.hw_id,
                                {"subject": subject, "body": body, "category": category, "reporter": d.user})

    async def summary(self) -> None:
        while not self.stop.is_set():
            await self.wait(300)
            if self.stop.is_set():
                break
            on = [d for d in self.devices if d.connected]
            log("çevrimiçi %d (oturum açık %d, boşta %d), kapalı %d, kayıtlı %d / %d"
                % (len(on), sum(1 for d in on if d.user), sum(1 for d in on if not d.user),
                   len(self.devices) - len(on), sum(1 for d in self.devices if d.secret), len(self.devices)))

    async def run(self) -> None:
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGTERM, signal.SIGINT):
            loop.add_signal_handler(sig, self.stop.set)
        # Sunucu sürümü: ajanlar onunla aynı sürümü bildirir (üç bilgisayar bir önceki sürümde kalmış)
        while not self.stop.is_set():
            status, body = await self.call(None, "GET", "/api/health")
            if status == 200 and isinstance(body, dict) and body.get("database"):
                self.version = str(body.get("version") or self.version)
                break
            log("sunucu bekleniyor (%s)" % (status or "bağlantı yok"))
            await self.wait(5)
        if self.stop.is_set():
            return
        if not self.args.enroll_token and not all(d.secret for d in self.devices):
            log("UYARI: DEMO_ENROLL_TOKEN yok; anahtarı olmayan bilgisayarlar kaydolamaz")
        log("%d bilgisayar, sunucu %s, sürüm %s, takvim %s"
            % (len(self.devices), self.http_base, self.version, self.args.schedule))
        tasks = [asyncio.ensure_future(self.run_device(d, i * 0.4 + random.uniform(0, 0.4)))
                 for i, d in enumerate(self.devices)]
        tasks += [asyncio.ensure_future(self.events()), asyncio.ensure_future(self.summary())]
        await self.stop.wait()
        log("durduruluyor: bağlantılar kapatılıyor")
        await asyncio.wait(tasks, timeout=8)
        for t in tasks:
            t.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        self.save_state()
        self.pool.shutdown(wait=False)
        log("durdu")


def parse_args(argv=None):
    env = os.environ.get
    p = argparse.ArgumentParser(description="POps demo filosu (hiçbir komut çalıştırmaz)")
    p.add_argument("--url", default=env("DEMO_FLEET_URL", "ws://backend:8000"))
    p.add_argument("--enroll-token", default=env("DEMO_ENROLL_TOKEN", ""))
    p.add_argument("--state", default=env("DEMO_FLEET_STATE", "/state/demo-fleet.json"))
    p.add_argument("--hb", type=float, default=float(env("DEMO_FLEET_HB", "10")))
    p.add_argument("--schedule", choices=("school", "always"), default=env("DEMO_FLEET_SCHEDULE", "school"))
    p.add_argument("--night-on", type=float, default=float(env("DEMO_FLEET_NIGHT_ON", "0.4")))
    p.add_argument("--no-fake-ips", dest="fake_ips", action="store_false",
                   help="X-Forwarded-For ile okul ağı adresi gösterme (yalnızca güvenilen iç ağda işe yarar)")
    p.add_argument("--list", action="store_true", help="filoyu yazdır ve çık")
    return p.parse_args(argv)


def main(argv=None) -> int:
    args = parse_args(argv)
    if args.list:
        for d in build_fleet():
            flags = [f for f, on in (("öğretmen", d.is_teacher), ("eski ajan", d.old_agent),
                                     ("karantina", d.quarantine_story), ("güncelleme %d" % d.pending_updates,
                                                                         d.pending_updates)) if on]
            print("%-13s %-15s %-13s  %-14s %-17s %s" % (d.hostname, d.hw_id, d.lab, d.ip, d.mac, ", ".join(flags)))
        return 0
    asyncio.run(Fleet(args).run())
    return 0


if __name__ == "__main__":
    sys.exit(main())
