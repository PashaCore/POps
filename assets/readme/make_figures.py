"""README figures (SVG), light and dark, English and Turkish.

    python3 assets/readme/make_figures.py

Writes <name>.<lang>.svg and <name>.<lang>-dark.svg next to this file. The READMEs pick the theme with
<picture> + prefers-color-scheme. Edit the texts here, not the SVG files. Capacity numbers come from
docs/kapasite/olcum.json. The "veyon" figure belongs to docs/tr/veyon-ile-birlikte.md and exists in Turkish only.
"""

import json
import os
from xml.sax.saxutils import escape

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
FONT = "-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Helvetica,Arial,sans-serif"

PALETTES = {
    "light": dict(
        bg="#ffffff", border="#d0d7de", text="#1f2328", muted="#59636e", card="#f6f8fa", line="#8c959f",
        blue="#2563eb", green="#059669", amber="#b45309", violet="#7c3aed", red="#dc2626",
        blue_bg="#eff6ff", green_bg="#ecfdf5", amber_bg="#fffbeb", violet_bg="#f5f3ff", red_bg="#fef2f2",
    ),
    "dark": dict(
        bg="#0d1117", border="#30363d", text="#e6edf3", muted="#9198a1", card="#161b22", line="#6e7681",
        blue="#58a6ff", green="#3fb950", amber="#e3b341", violet="#bc8cff", red="#ff7b72",
        blue_bg="#0d2242", green_bg="#0f2a1c", amber_bg="#2b2108", violet_bg="#221a3d", red_bg="#33161a",
    ),
}


class Svg:
    def __init__(self, w, h, p, label):
        self.w, self.h, self.p = w, h, p
        self.out = [
            f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}" '
            f'role="img" aria-label="{escape(label)}">',
            "<defs>",
        ]
        for name in ("line", "blue", "green", "violet", "amber", "red"):
            self.out.append(
                f'<marker id="arr-{name}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" '
                f'orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="{p[name]}"/></marker>'
            )
        self.out.append("</defs>")
        self.rect(0, 0, w, h, fill=p["bg"], stroke=p["border"], rx=12)

    def rect(self, x, y, w, h, fill=None, stroke=None, rx=8, dash=None, sw=1):
        d = f' stroke-dasharray="{dash}"' if dash else ""
        self.out.append(
            f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="{rx}" fill="{fill or "none"}" '
            f'stroke="{stroke or "none"}" stroke-width="{sw}"{d}/>'
        )

    def text(self, x, y, s, size=13, color=None, weight=400, anchor="start", italic=False):
        st = ' font-style="italic"' if italic else ""
        self.out.append(
            f'<text x="{x}" y="{y}" font-family="{FONT}" font-size="{size}" font-weight="{weight}" '
            f'fill="{color or self.p["text"]}" text-anchor="{anchor}"{st}>{escape(s)}</text>'
        )

    def lines(self, x, y, items, size=12.5, color=None, gap=19, bullet=None, bcolor=None):
        for i, s in enumerate(items):
            yy = y + i * gap
            if bullet:
                dot = bcolor or self.p["muted"]
                self.out.append(f'<circle cx="{x + 3}" cy="{yy - size * 0.35}" r="2.6" fill="{dot}"/>')
                self.text(x + 12, yy, s, size, color)
            else:
                self.text(x, yy, s, size, color)

    def arrow(self, x1, y1, x2, y2, color="line", both=False, dash=None, sw=1.6):
        d = f' stroke-dasharray="{dash}"' if dash else ""
        start = f' marker-start="url(#arr-{color})"' if both else ""
        self.out.append(
            f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{self.p[color]}" stroke-width="{sw}"{d} '
            f'marker-end="url(#arr-{color})"{start}/>'
        )

    def path(self, d, color="line", sw=1.6, dash=None, end=True):
        da = f' stroke-dasharray="{dash}"' if dash else ""
        m = f' marker-end="url(#arr-{color})"' if end else ""
        self.out.append(f'<path d="{d}" fill="none" stroke="{self.p[color]}" stroke-width="{sw}"{da}{m}/>')

    def pill(self, x, y, s, color, size=11):
        w = int(len(s) * size * 0.6) + 16
        self.rect(x, y, w, 20, fill=self.p[color + "_bg"], stroke=self.p[color], rx=10)
        self.text(x + w / 2, y + 14, s, size, self.p[color], 600, "middle")
        return w

    def save(self, path):
        self.out.append("</svg>")
        with open(path, "w", encoding="utf-8") as f:
            f.write("\n".join(self.out) + "\n")


# ---------------------------------------------------------------------------------------------------- texts
T = {
    "en": {
        "arch_title": "How POps fits together",
        "arch_sub": "Every managed PC connects out to the server. No inbound port is opened on a managed computer.",
        "admin": "Administrator", "admin_sub": ["Browser, any OS", "HTTPS only"],
        "github": "GitHub Releases", "github_sub": ["Agent MSI + server package", "ed25519-signed manifest"],
        "server": "POps server (Linux)",
        "web": "Web server: nginx or Apache, TLS", "panel": "Panel: PHP 8, plain JavaScript",
        "backend": "Backend: FastAPI, one process",
        "backend_items": ["REST API + WebSocket hub", "Task queue and scheduler", "Hash-chained audit log",
                          "Notifications, reports, alerts"],
        "db": "PostgreSQL", "db_sub": "devices, tasks, audit chain",
        "pc": "Windows 10 / 11 PC",
        "agent": "POpsAgent", "agent_sub": "Windows service (SYSTEM)",
        "agent_items": ["Commands, inventory, updates", "Quarantine and DNS policy"],
        "tray": "POpsTray", "tray_sub": "Signed-in user's session",
        "tray_items": ["Consent prompts, screen capture", "Lock screen, helpdesk, activity"],
        "watchdog": "POpsWatchdog", "watchdog_sub": "Keeps the service and tray running",
        "updater": "POpsUpdater", "updater_sub": "Installs the MSI, rolls back on failure",
        "e_https": "HTTPS", "e_proxy": "/api  /ws", "e_sql": "SQL",
        "e_cmd": ("WSS", "commands, heartbeats"), "e_vision": ("WSS", "Vision frames"),
        "e_out": "outbound 443", "e_release": "verified before staging", "e_pipe": "local pipe",
        "e_update": "verified again on the PC",

        "sec_title": "Security in layers",
        "sec_sub": "Each layer assumes the one before it may fail.",
        "sec_cols": [
            ("Panel", "blue", ["bcrypt passwords, rate-limited sign-in",
                               "Optional TOTP 2FA: each code works once, secrets encrypted",
                               "Roles: superadmin, admin, viewer",
                               "Open sessions re-checked every 10 seconds, revoked at once",
                               "Every value escaped on output, checked in CI"]),
            ("Server", "violet", ["Security audit log in a SHA-256 hash chain",
                                  "Enrollment in one transaction; tokens stored hashed",
                                  "A device's identity is bound to its key",
                                  "Signed links for deployment files, SHA-256 checked",
                                  "Self-update from signed release tags, automatic rollback"]),
            ("Connection", "green", ["TLS only: the agent refuses plain http",
                                     "Optional pinning to the school's own CA",
                                     "A per-device secret on every connection",
                                     "Vision accepts only the device's own key",
                                     "Screen frames go only to the admin who holds the session"]),
            ("PC", "amber", ["Terminal and Vision can be turned off per PC; the server cannot turn them on",
                             "Consent prompt, or a countdown with the recorded reason",
                             "Updates verified again on the PC; only newer versions",
                             "Remote actions written to the Windows event log",
                             "Quarantine with lock screen, isolation, per-device bypass codes"]),
        ],
        "sec_banner": "Even with the server taken over: no command on a PC with the terminal off, no unsigned agent, "
                      "no hidden screen view.",

        "upd_title": "Signed updates with automatic rollback",
        "upd_sub": "The signing key never reaches the server. The PC checks the signature itself.",
        "upd_steps": [
            ("1  Build and sign", ["CI signs the manifest", "(ed25519, key only in", "a GitHub secret)"]),
            ("2  Server verifies", ["checks the signature", "before staging the", "release"]),
            ("3  Dispatch", ["admin sends it to a", "lab or chosen PCs", "from the panel"]),
            ("4  PC verifies again", ["embedded public key,", "size, SHA-256,", "newer versions only"]),
            ("5  Install", ["POpsUpdater runs the", "MSI and waits until", "the agent is working"]),
        ],
        "upd_ok": "Working: success is reported and kept until the server confirms it",
        "upd_bad": "Not working: the previous version is restored automatically",

        "tl_title": "From first release to hardened alpha",
        "tl_sub": "Every release is in CHANGELOG.md with upgrade notes.",
        "tl": [
            ("0.1.0", "4 Aug", ["First public", "release"]),
            ("0.1.1–0.1.6", "25–27 Sep",
             ["Enrollment, 2FA,", "audit chain, signed", "updates, inventory,", "helpdesk"]),
            ("0.1.7–0.1.11", "29 Sep", ["Rollback drills,", "TLS installer,", "kiosk lock, 5,000", "agent test"]),
            ("0.1.12–0.1.14", "30 Sep–2 Oct", ["External reviews", "R-01 … R-20,", "F01 … F21;", "reliability"]),
            ("0.1.15", "2 Oct", [".NET 10 built in,", "modules per lab"]),
            ("0.1.16–0.1.21", "3 Oct", ["New panel: one", "layout, details,", "charts"]),
            ("0.1.22–0.1.23", "5 Oct", ["English panel,", "exam mode, Linux,", "SSO, schools, HA"]),
            ("next", "", ["Code signing,", "pilot schools"]),
        ],

        "cap_title": "All agents back after a server restart",
        "cap_sub": "0.1.11-alpha · one backend process · simulated agents, plain WebSocket · 0 failed attempts",
        "cap_y": "seconds",
        "cap_legend": "Agents connecting at the same moment · below: backend CPU as a share of one core",
    },
    "tr": {
        "arch_title": "POps nasıl çalışır",
        "arch_sub": "Yönetilen her bilgisayar sunucuya kendisi bağlanır. Bilgisayarlarda dışarıya açık port yoktur.",
        "admin": "Yönetici", "admin_sub": ["Tarayıcı, her işletim sistemi", "Yalnızca HTTPS"],
        "github": "GitHub sürümleri", "github_sub": ["Ajan MSI + sunucu paketi", "ed25519 imzalı manifest"],
        "server": "POps sunucusu (Linux)",
        "web": "Web sunucusu: nginx ya da Apache, TLS", "panel": "Panel: PHP 8, sade JavaScript",
        "backend": "Backend: FastAPI, tek süreç",
        "backend_items": ["REST API + WebSocket merkezi", "Görev kuyruğu ve zamanlayıcı", "Hash zincirli denetim kaydı",
                          "Bildirim, rapor, uyarılar"],
        "db": "PostgreSQL", "db_sub": "cihazlar, görevler, denetim zinciri",
        "pc": "Windows 10 / 11 bilgisayar",
        "agent": "POpsAgent", "agent_sub": "Windows servisi (SYSTEM)",
        "agent_items": ["Komut, envanter, güncelleme", "Karantina ve DNS politikası"],
        "tray": "POpsTray", "tray_sub": "Oturum açan kullanıcının oturumu",
        "tray_items": ["Onay istekleri, ekran yakalama", "Kilit ekranı, yardım masası, etkinlik"],
        "watchdog": "POpsWatchdog", "watchdog_sub": "Servisi ve tepsiyi ayakta tutar",
        "updater": "POpsUpdater", "updater_sub": "MSI'ı kurar, sorun olursa geri alır",
        "e_https": "HTTPS", "e_proxy": "/api  /ws", "e_sql": "SQL",
        "e_cmd": ("WSS", "komut, sinyal"), "e_vision": ("WSS", "Vision kareleri"),
        "e_out": "yalnızca dışa 443", "e_release": "hazırlanmadan doğrulanır", "e_pipe": "yerel pipe",
        "e_update": "bilgisayarda yeniden doğrulanır",

        "sec_title": "Katman katman güvenlik",
        "sec_sub": "Her katman, öncekinin aşılabileceğini varsayar.",
        "sec_cols": [
            ("Panel", "blue", ["bcrypt şifre, deneme sınırlı giriş",
                               "İsteğe bağlı 2FA (TOTP): her kod bir kez geçer, anahtar şifreli",
                               "Roller: superadmin, admin, viewer",
                               "Açık oturumlar 10 saniyede bir denetlenir, anında iptal",
                               "Her değer çıktıda kaçırılır, CI'da denetlenir"]),
            ("Sunucu", "violet", ["SHA-256 hash zincirli güvenlik denetim kaydı",
                                  "Kayıt tek işlemde; jetonlar yalnızca özetle saklanır",
                                  "Cihazın kimliği kendi anahtarına bağlı",
                                  "Dağıtım dosyalarına imzalı bağlantı, SHA-256 denetimi",
                                  "İmzalı sürüm etiketinden kendini günceller, geri alır"]),
            ("Bağlantı", "green", ["Yalnızca TLS: ajan düz http'yi reddeder",
                                   "İsteğe bağlı okulun kendi CA'sına sabitleme",
                                   "Her bağlantıda cihaza özel anahtar",
                                   "Vision yalnızca cihazın kendi anahtarıyla",
                                   "Ekran kareleri yalnızca oturumu açan yöneticiye gider"]),
            ("Bilgisayar", "amber", ["Terminal ve Vision cihazda kapatılabilir; sunucu geri açamaz",
                                     "Onay sorusu ya da kayıtlı gerekçeyle geri sayım",
                                     "Güncelleme cihazda yeniden doğrulanır; yalnızca yeni sürüm",
                                     "Uzaktan işlemler Windows olay günlüğüne yazılır",
                                     "Kilit ekranı, ağ yalıtımı, cihaza özel bypass kodlu karantina"]),
        ],
        "sec_banner": "Sunucu ele geçirilse bile: terminali kapalı bilgisayarda komut yok, imzasız ajan yok, "
                      "gizli ekran izleme yok.",

        "upd_title": "İmzalı güncelleme, kendiliğinden geri alma",
        "upd_sub": "İmza anahtarı sunucuya hiç gelmez. Bilgisayar imzayı kendisi denetler.",
        "upd_steps": [
            ("1  Derle ve imzala", ["CI manifesti imzalar", "(ed25519, anahtar yalnız", "GitHub secret'ında)"]),
            ("2  Sunucu doğrular", ["sürümü hazırlamadan", "önce imzayı", "denetler"]),
            ("3  Gönder", ["yönetici panelden", "bir laba ya da seçili", "cihazlara gönderir"]),
            ("4  Cihaz yine doğrular", ["gömülü açık anahtar,", "boyut, SHA-256,", "yalnızca yeni sürüm"]),
            ("5  Kur", ["POpsUpdater MSI'ı", "kurar, ajan çalışana", "kadar bekler"]),
        ],
        "upd_ok": "Çalışıyor: başarı bildirilir, sunucu onaylayana kadar saklanır",
        "upd_bad": "Çalışmıyor: önceki sürüm kendiliğinden geri yüklenir",

        "tl_title": "İlk sürümden sağlamlaştırılmış alfaya",
        "tl_sub": "Her sürüm yükseltme notlarıyla CHANGELOG.md'de.",
        "tl": [
            ("0.1.0", "4 Ağu", ["İlk açık", "sürüm"]),
            ("0.1.1–0.1.6", "25–27 Eyl", ["Kayıt, 2FA,", "denetim zinciri,", "imzalı güncelleme,", "envanter"]),
            ("0.1.7–0.1.11", "29 Eyl", ["Geri alma", "tatbikatı, TLS,", "kiosk kilidi,", "5.000 ajan testi"]),
            ("0.1.12–0.1.14", "30 Eyl–2 Eki", ["Dış incelemeler", "R-01 … R-20,", "F01 … F21;", "güvenilirlik"]),
            ("0.1.15", "2 Eki", [".NET 10 içinde,", "sınıf modülleri"]),
            ("0.1.16–0.1.21", "3 Eki", ["Yeni panel: tek", "düzen, ayrıntı,", "grafikler"]),
            ("0.1.22–0.1.23", "5 Eki", ["İngilizce panel,", "sınav modu, Linux,", "SSO, okullar, HA"]),
            ("sırada", "", ["Kod imzalama,", "pilot okullar"]),
        ],

        "cap_title": "Sunucu yeniden başladıktan sonra bütün ajanlar geri bağlandı",
        "cap_sub": "0.1.11-alpha · tek backend süreci · sanal ajanlar, düz WebSocket · 0 başarısız deneme",
        "cap_y": "saniye",
        "cap_legend": "Aynı anda bağlanan ajan sayısı · altında: backend işlemcisi, tek çekirdeğin yüzdesi",

        # docs/tr/veyon-ile-birlikte.md (yalnızca Türkçe)
        "vy_title": "Aynı bilgisayarda iki katman: Veyon dersi, POps laboratuvarı yönetir",
        "vy_sub": "Veyon öğretmenden öğrenci bilgisayarına bağlanır; POps ajanı sunucuya kendisi bağlanır. "
                  "Portlar çakışmaz.",
        "vy_teacher": "Öğretmen", "vy_teacher_sub": ["Veyon Master", "ders sırasında açık"],
        "vy_lab": "Laboratuvar bilgisayarları (Windows 10 / 11)",
        "vy_pc": "Öğrenci bilgisayarı",
        "vy_veyon": "Veyon Service", "vy_veyon_sub": "TCP 11100'ü dinler",
        "vy_pops": "POps ajanı", "vy_pops_sub": "port açmaz, dışa bağlanır",
        "vy_it": "BT sorumlusu", "vy_it_sub": ["Tarayıcı, POps paneli", "her zaman"],
        "vy_server": "POps sunucusu (okulda)",
        "vy_server_sub": ["envanter, dağıtım, güncelleme, denetim", "nginx 443 · backend yalnızca 127.0.0.1"],
        "vy_e_lesson": ("TCP 11100", "ders sırasında"), "vy_e_https": "HTTPS", "vy_e_out": "yalnızca dışa 443",
        "vy_note": "Karantinadaki bilgisayar yalnızca POps sunucusuna, DNS'e ve DHCP'ye açıktır; "
                   "o sırada Veyon da bağlanamaz.",
    },
}


# ---------------------------------------------------------------------------------------------------- figures
def architecture(t, p):
    s = Svg(1000, 580, p, t["arch_title"])
    s.text(28, 38, t["arch_title"], 20, weight=700)
    s.text(28, 60, t["arch_sub"], 13, p["muted"])

    # Left column: administrator, GitHub
    s.rect(28, 132, 190, 86, fill=p["blue_bg"], stroke=p["blue"], rx=10)
    s.text(44, 160, t["admin"], 15, p["blue"], 700)
    s.lines(44, 182, t["admin_sub"], 12, p["text"], gap=18)
    s.rect(28, 404, 190, 86, fill=p["card"], stroke=p["border"], rx=10)
    s.text(44, 432, t["github"], 15, weight=700)
    s.lines(44, 454, t["github_sub"], 12, p["muted"], gap=18)

    # Middle: server
    sx, sw = 268, 340
    s.rect(sx, 92, sw, 458, fill=p["card"], stroke=p["border"], rx=12)
    s.text(sx + 18, 118, t["server"], 14, p["muted"], 700)
    s.rect(sx + 18, 132, sw - 36, 40, fill=p["bg"], stroke=p["border"], rx=8)
    s.text(sx + 34, 157, t["web"], 13, weight=600)
    s.rect(sx + 18, 182, sw - 36, 40, fill=p["bg"], stroke=p["border"], rx=8)
    s.text(sx + 34, 207, t["panel"], 13, weight=600)
    s.rect(sx + 18, 252, sw - 36, 156, fill=p["green_bg"], stroke=p["green"], rx=10)
    s.text(sx + 34, 280, t["backend"], 15, p["green"], 700)
    s.lines(sx + 36, 306, t["backend_items"], 12.5, gap=22, bullet=True, bcolor=p["green"])
    s.rect(sx + 18, 438, sw - 36, 92, fill=p["amber_bg"], stroke=p["amber"], rx=10)
    # Veritabanı simgesi: küçük silindir
    cx, cy = sx + 52, 470
    s.out.append(f'<ellipse cx="{cx}" cy="{cy}" rx="16" ry="5" fill="none" stroke="{p["amber"]}" stroke-width="1.5"/>')
    s.out.append(f'<path d="M{cx - 16},{cy} L{cx - 16},{cy + 22} A16,5 0 0 0 {cx + 16},{cy + 22} L{cx + 16},{cy}" '
                 f'fill="none" stroke="{p["amber"]}" stroke-width="1.5"/>')
    s.text(sx + 80, 478, t["db"], 15, p["amber"], 700)
    s.text(sx + 80, 498, t["db_sub"], 11.5, p["muted"])

    # Right: PC
    px, pw = 716, 256
    s.rect(px, 92, pw, 458, fill=p["card"], stroke=p["border"], rx=12)
    s.text(px + 16, 118, t["pc"], 14, p["muted"], 700)
    blocks = [
        (t["agent"], t["agent_sub"], t["agent_items"], 132),
        (t["tray"], t["tray_sub"], t["tray_items"], 284),
    ]
    for name, sub, items, y in blocks:
        s.rect(px + 14, y, pw - 28, 118, fill=p["violet_bg"], stroke=p["violet"], rx=10)
        s.text(px + 28, y + 24, name, 14, p["violet"], 700)
        s.text(px + 28, y + 42, sub, 11.5, p["muted"])
        s.lines(px + 30, y + 66, items, 11.5, gap=19, bullet=True, bcolor=p["violet"])
    for name, sub, y in ((t["watchdog"], t["watchdog_sub"], 416), (t["updater"], t["updater_sub"], 480)):
        s.rect(px + 14, y, pw - 28, 54, fill=p["bg"], stroke=p["border"], rx=10)
        s.text(px + 28, y + 22, name, 13.5, weight=700)
        s.text(px + 28, y + 41, sub, 11, p["muted"])

    # Edges
    s.arrow(218, 168, sx + 16, 154, "blue", both=True)
    s.text(243, 148, t["e_https"], 11.5, p["blue"], 600, "middle")
    s.arrow(sx + sw / 2, 224, sx + sw / 2, 250, "line", both=True)
    s.text(sx + sw / 2 + 10, 242, t["e_proxy"], 11, p["muted"])
    s.arrow(sx + sw / 2, 410, sx + sw / 2, 436, "amber", both=True)
    s.text(sx + sw / 2 + 10, 428, t["e_sql"], 11, p["muted"])
    # Ajanın iki kanalı (bilgisayardan sunucuya, dışa doğru)
    gx = (sx + sw + px) / 2
    s.arrow(px + 12, 168, sx + sw - 16, 300, "violet")
    s.arrow(px + 12, 222, sx + sw - 16, 362, "violet", dash="5 4")
    s.text(gx, 146, t["e_cmd"][0], 11.5, p["violet"], 700, "middle")
    s.text(gx, 161, t["e_cmd"][1], 11, p["violet"], 400, "middle")
    s.text(gx, 392, t["e_vision"][0], 11.5, p["violet"], 700, "middle")
    s.text(gx, 407, t["e_vision"][1], 11, p["violet"], 400, "middle")
    w = int(len(t["e_out"]) * 10.5 * 0.6) + 16
    s.pill(gx - w / 2, 424, t["e_out"], "violet", 10.5)
    # Tepsi kareleri ve kullanıcı yanıtlarını servise yerel bir pipe ile verir
    s.arrow(px + pw / 2, 252, px + pw / 2, 282, "violet", both=True)
    s.text(px + pw / 2 + 10, 271, t["e_pipe"], 11, p["muted"])
    # İmzalı sürüm yolu
    s.path(f"M218,446 C240,446 248,400 {sx + 16},392", "line", dash="4 4")
    s.text(122, 512, t["e_release"], 11, p["muted"], anchor="middle")
    return s


def security(t, p):
    s = Svg(1000, 516, p, t["sec_title"])
    s.text(28, 38, t["sec_title"], 20, weight=700)
    s.text(28, 60, t["sec_sub"], 13, p["muted"])
    w, h = 466, 164
    for i, (title, col, items) in enumerate(t["sec_cols"]):
        cx = 28 + (i % 2) * (w + 12)
        cy = 82 + (i // 2) * (h + 12)
        s.rect(cx, cy, w, h, fill=p[col + "_bg"], stroke=p[col], rx=12)
        s.text(cx + 18, cy + 30, title, 16, p[col], 700)
        y = cy + 58
        for it in items:
            s.out.append(f'<circle cx="{cx + 21}" cy="{y - 4}" r="2.8" fill="{p[col]}"/>')
            s.text(cx + 32, y, it, 12.5)
            y += 21
    s.rect(28, 446, 944, 46, fill=p["card"], stroke=p["border"], rx=10)
    s.text(500, 474, t["sec_banner"], 12.5, weight=600, anchor="middle")
    return s


def updates(t, p):
    s = Svg(1000, 370, p, t["upd_title"])
    s.text(28, 38, t["upd_title"], 20, weight=700)
    s.text(28, 60, t["upd_sub"], 13, p["muted"])
    cols = ["violet", "green", "blue", "amber", "green"]
    w, gap, x0 = 172, 21, 28
    for i, (title, items) in enumerate(t["upd_steps"]):
        x = x0 + i * (w + gap)
        col = cols[i]
        s.rect(x, 86, w, 132, fill=p[col + "_bg"], stroke=p[col], rx=10)
        s.text(x + 12, 112, title, 13, p[col], 700)
        s.lines(x + 12, 138, items, 12, gap=19)
        if i < len(t["upd_steps"]) - 1:
            s.arrow(x + w + 1, 152, x + w + gap - 1, 152, "line")
    lx = x0 + 4 * (w + gap) + w / 2
    right = 700
    s.rect(28, 262, right - 28, 34, fill=p["green_bg"], stroke=p["green"], rx=8)
    s.text(46, 284, "✓  " + t["upd_ok"], 12.5, p["green"], 600)
    s.rect(28, 310, right - 28, 34, fill=p["red_bg"], stroke=p["red"], rx=8)
    s.text(46, 332, "↺  " + t["upd_bad"], 12.5, p["red"], 600)
    s.path(f"M{lx},220 L{lx},279 L{right + 2},279", "green")
    s.path(f"M{lx},279 L{lx},327 L{right + 2},327", "red")
    return s


def timeline(t, p):
    s = Svg(1000, 262, p, t["tl_title"])
    s.text(28, 38, t["tl_title"], 20, weight=700)
    s.text(28, 60, t["tl_sub"], 13, p["muted"])
    items = t["tl"]
    n = len(items)
    x0, x1, y = 92, 908, 128
    s.out.append(f'<line x1="{x0 - 30}" y1="{y}" x2="{x1 + 30}" y2="{y}" stroke="{p["border"]}" stroke-width="3"/>')
    for i, (ver, date, desc) in enumerate(items):
        x = x0 + i * (x1 - x0) / (n - 1)
        last = i == n - 1
        col = "line" if last else ("green" if i >= n - 3 else "blue")
        fill = p["bg"] if last else p[col]
        dash = ' stroke-dasharray="3 3"' if last else ""
        s.out.append(f'<circle cx="{x}" cy="{y}" r="9" fill="{fill}" stroke="{p[col]}" stroke-width="2.5"{dash}/>')
        s.text(x, y - 34, ver, 13.5, p[col] if not last else p["muted"], 700, "middle")
        s.text(x, y - 17, date, 11.5, p["muted"], 400, "middle")
        for j, line in enumerate(desc):
            s.text(x, y + 34 + j * 17, line, 11.5, p["text"] if not last else p["muted"], 400, "middle",
                   italic=last)
    return s


def capacity(t, p):
    with open(os.path.join(ROOT, "docs", "kapasite", "olcum.json"), encoding="utf-8") as f:
        data = json.load(f)["olcumler"]
    tr = t is T["tr"]
    s = Svg(1000, 420, p, t["cap_title"])
    s.text(28, 38, t["cap_title"], 20, weight=700)
    s.text(28, 60, t["cap_sub"], 12.5, p["muted"])
    left, right, top, bottom = 72, 972, 100, 320
    ymax = 12
    for v in range(0, ymax + 1, 3):
        y = bottom - (bottom - top) * v / ymax
        s.out.append(f'<line x1="{left}" y1="{y}" x2="{right}" y2="{y}" stroke="{p["border"]}" stroke-width="1"/>')
        s.text(left - 10, y + 4, str(v), 11.5, p["muted"], anchor="end")
    s.text(left - 10, top - 16, t["cap_y"], 11.5, p["muted"], anchor="end")
    n = len(data)
    slot = (right - left) / n
    bw = slot * 0.5
    for i, o in enumerate(data):
        cx = left + slot * (i + 0.5)
        h = (bottom - top) * o["toparlanma_sn"] / ymax
        s.rect(cx - bw / 2, bottom - h, bw, h, fill=p["blue"], rx=4)
        lab = ("%.1f" % o["toparlanma_sn"]).replace(".", "," if tr else ".") + " s" if not tr else \
            ("%.1f" % o["toparlanma_sn"]).replace(".", ",") + " sn"
        s.text(cx, bottom - h - 8, lab, 12.5, weight=700, anchor="middle")
        agents = "{:,}".format(o["cihaz"]).replace(",", "." if tr else ",")
        s.text(cx, bottom + 22, agents, 13, weight=700, anchor="middle")
        s.text(cx, bottom + 40, ("%%%d" % o["backend_cpu"]) if tr else ("%d %%" % o["backend_cpu"]),
               11.5, p["muted"], anchor="middle")
    s.text(500, bottom + 76, t["cap_legend"], 12, p["muted"], anchor="middle")
    return s


def veyon(t, p):
    s = Svg(1000, 516, p, t["vy_title"])
    s.text(28, 38, t["vy_title"], 20, weight=700)
    s.text(28, 60, t["vy_sub"], 13, p["muted"])

    # Sol üst: öğretmen (Veyon Master)
    s.rect(28, 140, 180, 92, fill=p["amber_bg"], stroke=p["amber"], rx=10)
    s.text(44, 168, t["vy_teacher"], 15, p["amber"], 700)
    s.lines(44, 190, t["vy_teacher_sub"], 12, gap=18)

    # Orta: laboratuvar, üç öğrenci bilgisayarı; her birinde Veyon Service ve POps ajanı yan yana
    lx, lw = 300, 672
    s.rect(lx, 88, lw, 228, fill=p["card"], stroke=p["border"], rx=12)
    s.text(lx + 18, 114, t["vy_lab"], 14, p["muted"], 700)
    cw, gap = 200, 18
    centers = []
    for i in range(3):
        x = lx + 18 + i * (cw + gap)
        centers.append(x + cw / 2)
        s.rect(x, 128, cw, 172, fill=p["bg"], stroke=p["border"], rx=10)
        s.text(x + 14, 152, "%s %d" % (t["vy_pc"], i + 1), 13, weight=700)
        s.rect(x + 12, 166, cw - 24, 54, fill=p["amber_bg"], stroke=p["amber"], rx=8)
        s.text(x + 24, 188, t["vy_veyon"], 13, p["amber"], 700)
        s.text(x + 24, 207, t["vy_veyon_sub"], 11, p["muted"])
        s.rect(x + 12, 232, cw - 24, 54, fill=p["violet_bg"], stroke=p["violet"], rx=8)
        s.text(x + 24, 254, t["vy_pops"], 13, p["violet"], 700)
        s.text(x + 24, 273, t["vy_pops_sub"], 11, p["muted"])

    # Öğretmenden öğrenci bilgisayarlarına (gelen bağlantı, yalnızca ders sırasında)
    s.arrow(210, 193, lx - 2, 193, "amber")
    s.text(254, 180, t["vy_e_lesson"][0], 11.5, p["amber"], 700, "middle")
    s.text(254, 214, t["vy_e_lesson"][1], 11, p["amber"], 400, "middle")

    # Alt: BT sorumlusu ve POps sunucusu
    s.rect(28, 372, 180, 84, fill=p["blue_bg"], stroke=p["blue"], rx=10)
    s.text(44, 400, t["vy_it"], 15, p["blue"], 700)
    s.lines(44, 422, t["vy_it_sub"], 12, gap=18)
    sx, sw = 520, 300
    s.rect(sx, 372, sw, 84, fill=p["green_bg"], stroke=p["green"], rx=10)
    s.text(sx + 16, 400, t["vy_server"], 15, p["green"], 700)
    s.lines(sx + 16, 422, t["vy_server_sub"], 11.5, p["muted"], gap=18)
    s.arrow(210, 414, sx - 2, 414, "blue", both=True)
    s.text((210 + sx) / 2, 404, t["vy_e_https"], 11.5, p["blue"], 600, "middle")

    # Ajanlardan sunucuya: dışa doğru, sürekli
    targets = [sx + 40, sx + sw / 2, sx + sw - 40]
    for cx, tx in zip(centers, targets):
        s.arrow(cx, 302, tx, 370, "violet")
    s.pill(330, 326, t["vy_e_out"], "violet", 10.5)

    s.rect(28, 470, 944, 32, fill=p["card"], stroke=p["border"], rx=8)
    s.text(500, 491, t["vy_note"], 12, p["muted"], 600, "middle")
    return s


FIGURES = {"architecture": architecture, "security": security, "updates": updates, "timeline": timeline,
           "capacity": capacity, "veyon": veyon}
# Yalnızca bazı dillerde çizilen şekiller (öteki şekiller her dilde)
ONLY_LANGS = {"veyon": ("tr",)}


def main():
    for lang, texts in T.items():
        for theme, pal in PALETTES.items():
            for name, fn in FIGURES.items():
                if lang not in ONLY_LANGS.get(name, (lang,)):
                    continue
                suffix = "" if theme == "light" else "-dark"
                fn(texts, pal).save(os.path.join(HERE, "%s.%s%s.svg" % (name, lang, suffix)))
    print("ok")


if __name__ == "__main__":
    main()
