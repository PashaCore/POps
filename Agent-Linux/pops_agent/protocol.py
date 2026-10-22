"""Bağlantı kuralları (Windows ajanıyla aynı): yeniden bağlanma beklemesi, server_info el sıkışması ve güncelleme
sonucunun onaya kadar saklanması. Saf mantık; ağa ve diske dokunmaz."""

import random
import threading
import time
from typing import Callable, Optional

# ── Yeniden bağlanma: üstel geri çekilme + full jitter (POps.Shared.ReconnectBackoff) ──
#   bekleme = rastgele(0, min(60 sn, 2 sn × 2^deneme)); 4401 (kimlik reddi) en az 60 sn, 4409 (kopya) en az 10 dk
BASE = 2.0
CAP = 60.0
AUTH_REJECTED_MIN = 60.0
CLONE_REJECTED_MIN = 600.0
MAX_ATTEMPT = 5
AUTH_REJECTED_CODE = 4401
CLONE_REJECTED_CODE = 4409


def ceiling(attempt: int) -> float:
    exponent = min(max(attempt, 0), MAX_ATTEMPT)
    return min(CAP, BASE * (1 << exponent))


def rejection(close_code: Optional[int]) -> Optional[str]:
    if close_code == AUTH_REJECTED_CODE:
        return "auth"
    if close_code == CLONE_REJECTED_CODE:
        return "clone"
    return None


def delay(attempt: int, kind: Optional[str] = None, rnd: Callable[[], float] = random.random) -> float:
    jitter = rnd() * ceiling(attempt)
    if kind == "auth":
        return AUTH_REJECTED_MIN + jitter
    if kind == "clone":
        return CLONE_REJECTED_MIN + jitter
    return jitter


def next_attempt(attempt: int) -> int:
    return min(max(attempt, 0), MAX_ATTEMPT - 1) + 1


# ── server_info (ServerHandshake): bağlantı başına; 15 sn içinde gelmezse sunucu eski sayılır ──
SERVER_INFO_WAIT = 15.0


class ServerHandshake:
    def __init__(self, clock: Callable[[], float] = time.monotonic):
        self.clock = clock
        self._lock = threading.Lock()
        self._connected = float("-inf")
        self._seen = False
        self._features = set()
        self._last_known = None   # bağlantılar arası ipucu; None: hiç öğrenilmedi

    def on_connected(self) -> None:
        with self._lock:
            self._connected = self.clock()
            self._seen = False
            self._features = set()

    def on_server_info(self, message: dict) -> None:
        feats = message.get("features") if isinstance(message, dict) else None
        features = {f for f in feats if isinstance(f, str)} if isinstance(feats, list) else set()
        with self._lock:
            self._seen = True
            self._features = features
            self._last_known = set(features)

    def supports(self, feature: str) -> Optional[bool]:
        """None: henüz bilinmiyor; False: duyurulmadı ya da eski sunucu."""
        with self._lock:
            if self._seen:
                return feature in self._features
            if self.clock() - self._connected < SERVER_INFO_WAIT:
                return None
            self._last_known = set()
            return False

    def last_known(self, feature: str) -> Optional[bool]:
        with self._lock:
            return None if self._last_known is None else feature in self._last_known


# ── update_result onayı (UpdateResultReporter) ──
UPDATE_ACK_FEATURE = "update_result_ack"
RESEND_INTERVAL = 60.0

NOTHING, WAIT, SEND_AND_MARK, SEND_AND_KEEP = "nothing", "wait", "send_and_mark_reported", "send_and_keep"


class UpdateResultReporter:
    def __init__(self, handshake: ServerHandshake):
        self.handshake = handshake
        self._lock = threading.Lock()
        self._sent_id = None
        self._sent_at = 0.0

    def on_connected(self) -> None:
        self.handshake.on_connected()
        with self._lock:
            self._sent_id = None

    def next(self, result_id: Optional[str]) -> str:
        if not result_id:
            return NOTHING
        supported = self.handshake.supports(UPDATE_ACK_FEATURE)
        if supported is None:
            return WAIT
        if supported is False:
            return SEND_AND_MARK
        with self._lock:
            if self._sent_id == result_id and self.handshake.clock() - self._sent_at < RESEND_INTERVAL:
                return NOTHING
            return SEND_AND_KEEP

    def sent(self, result_id: str) -> None:
        with self._lock:
            self._sent_id = result_id
            self._sent_at = self.handshake.clock()

    @staticmethod
    def acknowledges(message: dict, pending_id: Optional[str]) -> bool:
        return bool(pending_id) and isinstance(message, dict) and message.get("result_id") == pending_id
