"""agent.conf: SERVER_URL, ENROLL_TOKEN ve isteğe bağlı SERVER_CA_CERT (KEY=VALUE satırları, # yorum).

Dosya yalnızca root'a açıktır (0600). Ajan her açılışta izni denetler ve gerekirse düzeltir (Windows ajanının
appsettings.json için yaptığı gibi). Kayıt jetonu, cihaz anahtarı alınınca dosyadan silinir: bir sınıfa verilmiş
çok kullanımlık jeton diskte kalmasın.
"""

import ipaddress
import logging
import os
import re
import time
from typing import Dict, Optional
from urllib.parse import urlsplit

from pops_agent import store

log = logging.getLogger("pops.config")

KEYS = ("SERVER_URL", "ENROLL_TOKEN", "SERVER_CA_CERT")
# Sunucunun verdiği jetonlar ve cihaz anahtarı (Windows AgentCredentials.TokenRegex ile aynı)
TOKEN_RE = re.compile(r"^[A-Za-z0-9_-]{16,256}$")

TEMPLATE = """# POps ajanı ayarları (yalnızca root okuyabilir: 0600).
# SERVER_URL: panelin adresi, https:// ile (şifresiz http yalnızca aynı bilgisayardaki sunucu için kabul edilir).
# ENROLL_TOKEN: panelden alınan kayıt jetonu; cihaz anahtarı alınınca ajan bu satırı siler.
# SERVER_CA_CERT: kurum sertifikası (özel CA) kullanılıyorsa PEM dosyasının yolu; verilirse sunucu YALNIZCA
#                 bununla doğrulanır.
SERVER_URL=
ENROLL_TOKEN=
#SERVER_CA_CERT=/etc/pops-agent/server-ca.pem
"""


class AgentConfig:
    def __init__(self, server_url: str = "", enroll_token: Optional[str] = None, ca_file: Optional[str] = None):
        url = (server_url or "").strip().rstrip("/")
        scheme, sep, rest = url.partition("://")
        # "HTTPS://" de geçerli bir adres: şema küçük harfe çevrilir (TLS ve kurum sertifikası kararı şemaya bakar)
        self.server_url = scheme.lower() + sep + rest if sep else url
        self.enroll_token = enroll_token or None
        self.ca_file = ca_file or None

    @property
    def secure(self) -> bool:
        return is_secure_server_url(self.server_url)

    @property
    def ws_base(self) -> str:
        url = self.server_url
        if url.startswith("https://"):
            return "wss://" + url[len("https://"):]
        if url.startswith("http://"):
            return "ws://" + url[len("http://"):]
        return url


def parse_kv(text: str) -> Dict[str, str]:
    out = {}
    for raw in (text or "").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip().upper()
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ("'", '"'):
            value = value[1:-1]
        out[key] = value
    return out


def is_secure_server_url(url: str) -> bool:
    """https, ya da yalnızca bu bilgisayarın kendisine (loopback) giden http. Windows ajanının
    POpsHelpers.IsSecureServerUrl kuralıyla aynı: cihaz anahtarı ve komutlar ağda şifresiz gitmez."""
    try:
        parts = urlsplit(url or "")
    except ValueError:
        return False
    host = parts.hostname
    if not host or parts.username or parts.password:
        return False
    if parts.scheme == "https":
        return True
    if parts.scheme != "http":
        return False
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def secure_file(path: str, mode: int = 0o600) -> None:
    """Başkalarının okuyabildiği ayar dosyası sıkılaştırılır (root olarak çalışırken)."""
    try:
        st = os.stat(path)
    except FileNotFoundError:
        return
    if os.geteuid() == 0 and st.st_uid != 0:
        log.warning("[GÜVENLİK] %s root'a ait değil (uid %d); sahibi root yapılıyor.", path, st.st_uid)
        try:
            os.chown(path, 0, 0)
        except OSError as exc:
            log.error("%s sahibi değiştirilemedi: %s", path, exc)
    if st.st_mode & 0o077:
        try:
            os.chmod(path, mode)
            log.warning("[GÜVENLİK] %s başkalarına açıktı (%o); izin %o yapıldı.", path, st.st_mode & 0o777, mode)
        except OSError as exc:
            log.error("%s izni düzeltilemedi: %s", path, exc)


def load(path: str) -> AgentConfig:
    text = store.read_text(path)
    if text is None:
        return AgentConfig()
    values = parse_kv(text)
    token = values.get("ENROLL_TOKEN") or None
    if token and not TOKEN_RE.match(token):
        log.error("Kayıt jetonunun biçimi geçersiz; gönderilmiyor.")
        token = None
    return AgentConfig(values.get("SERVER_URL", ""), token, values.get("SERVER_CA_CERT") or None)


def forget_enroll_token(path: str) -> bool:
    """Cihaz anahtarı alındı: ENROLL_TOKEN satırı yorum satırıyla değiştirilir (dosyanın geri kalanı korunur)."""
    text = store.read_text(path)
    if text is None:
        return False
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    changed = False
    lines = []
    for raw in text.splitlines():
        key = raw.split("=", 1)[0].strip().upper() if "=" in raw and not raw.strip().startswith("#") else ""
        if key == "ENROLL_TOKEN":
            changed = True
            lines.append("# ENROLL_TOKEN cihaz anahtarı alındığı için silindi (%s)" % stamp)
            lines.append("ENROLL_TOKEN=")
        else:
            lines.append(raw)
    if changed:
        store.write_atomic(path, "\n".join(lines) + "\n", 0o600)
    return changed


def write_config(path: str, server_url: str, token: Optional[str], ca_file: Optional[str]) -> None:
    """`pops-agent configure`: verilen değerler yazılır, verilmeyenler korunur."""
    current = parse_kv(store.read_text(path) or "")
    if server_url is not None:
        current["SERVER_URL"] = server_url.strip()
    if token is not None:
        current["ENROLL_TOKEN"] = token.strip()
    if ca_file is not None:
        current["SERVER_CA_CERT"] = ca_file.strip()
    body = TEMPLATE.split("SERVER_URL=")[0]
    for key in KEYS:
        if key in current or key != "SERVER_CA_CERT":
            body += "%s=%s\n" % (key, current.get(key, ""))
    store.write_atomic(path, body, 0o600)


def ca_file_problem(path: str) -> Optional[str]:
    """Kurum sertifikası dosyasını root dışında biri değiştirebiliyorsa sunucu taklit edilebilir: reddedilir."""
    try:
        st = os.stat(path)
    except OSError as exc:
        return "okunamadı (%s)" % exc.strerror
    if os.geteuid() == 0 and st.st_uid != 0:
        return "sahibi root değil"
    if st.st_mode & 0o022:
        return "root dışındakiler yazabiliyor (%o)" % (st.st_mode & 0o777)
    return None
