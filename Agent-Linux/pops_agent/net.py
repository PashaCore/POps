"""Sunucuya giden her bağlantı (komut WebSocket'i, ajan HTTP uçları, /updates indirmesi) buradan geçer.

TLS: SERVER_CA_CERT verilmişse sunucu YALNIZCA o kurum sertifikasıyla doğrulanır (sistem deposundaki kökler geçerli
sayılmaz; Windows ServerTrust "custom"); verilmemişse işletim sisteminin güven deposu ("system"). Dosya verilmiş ama
okunamıyor, bozuk ya da root dışında birinin yazabildiği bir dosyaysa hiçbir bağlantı kurulmaz. En az TLS 1.2,
ana makine adı denetlenir. HTTP isteklerinde yönlendirme izlenmez (http.client izlemez): X-Agent-Secret başka bir
adrese taşınmaz.
"""

import asyncio
import http.client
import json
import logging
import ssl
from typing import Dict, Optional, Tuple
from urllib.parse import quote, urlsplit

from pops_agent.config import ca_file_problem

log = logging.getLogger("pops.net")

MODE_CUSTOM, MODE_SYSTEM = "custom", "system"


class TrustError(Exception):
    pass


def ssl_context(ca_file: Optional[str]) -> ssl.SSLContext:
    if ca_file:
        problem = ca_file_problem(ca_file)
        if problem:
            raise TrustError("[GÜVENLİK] Kurum sertifikası (%s) %s; sunucuya bağlanılmıyor." % (ca_file, problem))
        try:
            ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH, cafile=ca_file)
        except (ssl.SSLError, OSError, ValueError) as exc:
            raise TrustError("[GÜVENLİK] Kurum sertifikası (%s) okunamadı: %s; sunucuya bağlanılmıyor."
                             % (ca_file, exc))
    else:
        ctx = ssl.create_default_context(ssl.Purpose.SERVER_AUTH)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.check_hostname = True
    ctx.verify_mode = ssl.CERT_REQUIRED
    return ctx


def trust_mode(ca_file: Optional[str]) -> str:
    return MODE_CUSTOM if ca_file else MODE_SYSTEM


class HttpClient:
    """Küçük, eşzamanlı (iş parçacığında çağrılır) HTTPS istemcisi."""

    def __init__(self, base_url: str, ctx: Optional[ssl.SSLContext], headers: Dict[str, str], timeout: float = 30):
        parts = urlsplit(base_url)
        self.scheme = parts.scheme
        self.host = parts.hostname
        self.port = parts.port
        self.base_path = parts.path.rstrip("/")
        self.ctx = ctx
        self.headers = dict(headers)
        self.timeout = timeout

    def _conn(self, timeout: Optional[float] = None):
        t = timeout or self.timeout
        if self.scheme == "https":
            if self.ctx is None:   # sistem deposuna sessizce düşülmez (kurum sertifikası atlanırdı)
                raise TrustError("https için TLS bağlamı yok")
            return http.client.HTTPSConnection(self.host, self.port, timeout=t, context=self.ctx)
        return http.client.HTTPConnection(self.host, self.port, timeout=t)

    def request(self, method: str, path: str, body=None, headers: Optional[Dict[str, str]] = None,
                timeout: Optional[float] = None) -> Tuple[int, bytes]:
        hdrs = dict(self.headers)
        hdrs.update(headers or {})
        data = None
        if body is not None:
            data = json.dumps(body, ensure_ascii=False).encode("utf-8")
            hdrs["Content-Type"] = "application/json"
        conn = self._conn(timeout)
        try:
            conn.request(method, self.base_path + path, body=data, headers=hdrs)
            resp = conn.getresponse()
            return resp.status, resp.read(4 * 1024 * 1024)
        finally:
            conn.close()

    def download(self, path: str, out, max_bytes: int, timeout: float = 900):
        """Yanıt gövdesi out(bytes) ile parça parça verilir; max_bytes aşılınca okuma durur. Dönen: (durum, okunan)."""
        conn = self._conn(60)
        try:
            conn.request("GET", self.base_path + path, headers=dict(self.headers))
            resp = conn.getresponse()
            if resp.status != 200:
                return resp.status, 0
            total = 0
            if conn.sock is not None:
                conn.sock.settimeout(timeout)
            while True:
                chunk = resp.read(65536)
                if not chunk:
                    break
                total += len(chunk)
                if total > max_bytes:
                    break
                out(chunk)
            return resp.status, total
        finally:
            conn.close()


def device_path(prefix: str, hw_id: str) -> str:
    return prefix + quote(hw_id, safe="")


# ── WebSocket: python3-websockets 10.x (Debian 12 / Pardus 23, Ubuntu 24.04; eski "legacy" istemci) ve 14+ (yeni
# asyncio istemcisi). Ubuntu 22.04'ün 9.1 paketi kendi Python 3.10'uyla çalışmaz; paket >= 10 ister. ──
def _ws_major() -> int:
    import websockets

    try:
        return int(str(getattr(websockets, "__version__", getattr(websockets, "version", "0"))).split(".")[0])
    except (ValueError, AttributeError):
        return 0


async def ws_connect(uri: str, headers: Dict[str, str], ctx: Optional[ssl.SSLContext], max_size: int,
                     open_timeout: float = 30):
    import websockets

    major = _ws_major()
    kwargs = {"max_size": max_size, "ping_interval": 20, "ping_timeout": 20, "close_timeout": 5}
    if uri.startswith("wss://"):
        kwargs["ssl"] = ctx
    if major >= 14:
        kwargs["additional_headers"] = headers
    else:
        kwargs["extra_headers"] = headers
    if major >= 10:
        kwargs["open_timeout"] = open_timeout
    return await asyncio.wait_for(websockets.connect(uri, **kwargs), open_timeout + 5)


def close_code(ws, exc: Optional[BaseException] = None) -> Optional[int]:
    rcvd = getattr(exc, "rcvd", None) if exc is not None else None
    if rcvd is not None and getattr(rcvd, "code", None) is not None:
        return rcvd.code
    code = getattr(exc, "code", None) if exc is not None else None
    if isinstance(code, int) and code != 1006:
        return code
    if ws is not None:
        try:
            code = ws.close_code
        except AttributeError:
            code = None
        if isinstance(code, int):
            return code
    return None
