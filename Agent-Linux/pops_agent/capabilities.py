"""Yetenek politikası (docs/decisions.md D-08): bilgisayar karar verir, sunucu yalnızca KAPATABİLİR.

  * /etc/pops-agent/capabilities.conf (yerel yönetici, paketin conffile'ı): TERMINAL_ENABLED=1|0. Dosya yoksa açık
    (kurulum varsayılanı); değer anlaşılamıyorsa kapalı (güvenli yön).
  * Sunucunun "set_capabilities ... false" isteği /var/lib/pops-agent/capabilities.state.json'a yazılır ve yerel
    ayardan bağımsız olarak geçerli kalır. Sunucunun "aç" isteği yok sayılır; yeniden açmak yerel root'un işidir
    (`pops-agent capabilities --reset`). Durum dosyası okunamıyorsa terminal kapalı sayılır.
  * Vision (ekran, uzaktan fare/klavye) bu sürümde yok: her zaman kapalı bildirilir.
"""

import logging
import time
from typing import Callable, List, Optional, Tuple

from pops_agent import store
from pops_agent.config import parse_kv

log = logging.getLogger("pops.policy")

TERMINAL = "terminal_enabled"
VISION = "vision_enabled"
_TRUE = ("1", "true", "yes", "on", "evet", "acik", "açık")
_FALSE = ("0", "false", "no", "off", "hayir", "hayır", "kapali", "kapalı")


def parse_flag(value: Optional[str]) -> Optional[bool]:
    v = (value or "").strip().lower()
    if v in _TRUE:
        return True
    if v in _FALSE:
        return False
    return None


class Capabilities:
    def __init__(self, conf_path: str, state_path: str, audit: Optional[Callable] = None):
        self.conf_path = conf_path
        self.state_path = state_path
        self._audit = audit or (lambda *a, **k: None)
        self._local_terminal = True
        self._server_off = False

    def load(self) -> None:
        text = store.read_text(self.conf_path)
        if text is None:
            self._local_terminal = True
        else:
            raw = parse_kv(text).get("TERMINAL_ENABLED")
            flag = True if raw is None else parse_flag(raw)
            if flag is None:
                log.error("[GÜVENLİK] %s: TERMINAL_ENABLED=%r anlaşılamadı; terminal kapalı sayılıyor.",
                          self.conf_path, raw)
                flag = False
            self._local_terminal = flag
        try:
            state = store.read_json(self.state_path)
            self._server_off = bool(isinstance(state, dict) and state.get(TERMINAL) is False)
            if state is not None and not isinstance(state, dict):
                raise ValueError("nesne değil")
        except (ValueError, OSError) as exc:
            log.error("[GÜVENLİK] %s okunamadı (%s); terminal kapalı sayılıyor.", self.state_path, exc)
            self._server_off = True
        log.info("Yetenekler: %s.", self.describe())

    @property
    def terminal_enabled(self) -> bool:
        return self._local_terminal and not self._server_off

    @property
    def vision_enabled(self) -> bool:
        return False

    def apply_server_request(self, request: dict) -> Tuple[List[str], List[str]]:
        """Yalnızca false değerler uygulanır. Dönen: (kapatılanlar, yok sayılan açma istekleri)."""
        disabled, ignored = [], []
        value = request.get(TERMINAL)
        if value is False:
            if self.terminal_enabled:
                disabled.append(TERMINAL)
            if not self._server_off:
                self._server_off = True
                self._persist()
        elif value is True and not self.terminal_enabled:
            ignored.append(TERMINAL)
        if request.get(VISION) is True:
            ignored.append(VISION)
        if disabled:
            log.warning("[GÜVENLİK] Sunucu yetenek kapattı: %s. Yeniden açmak yalnızca yerel root ile mümkün "
                        "(pops-agent capabilities --reset). Yetenekler: %s.", ", ".join(disabled), self.describe())
            for name in disabled:
                self._audit("capability_changed", capability=name.replace("_enabled", ""), old=True, new=False,
                            source="server")
        if ignored:
            log.warning("[GÜVENLİK] Sunucunun açma isteği yok sayıldı: %s.", ", ".join(ignored))
        return disabled, ignored

    def reset_server_state(self) -> bool:
        return store.delete(self.state_path)

    def status_message(self, server_ca: str) -> dict:
        return {"type": "capabilities", TERMINAL: self.terminal_enabled, VISION: False, "server_ca": server_ca}

    def describe(self) -> str:
        why = ""
        if not self._local_terminal:
            why = " (yerel ayar)"
        elif self._server_off:
            why = " (sunucu kapattı)"
        return "terminal=%s%s, vision=yok" % ("açık" if self.terminal_enabled else "kapalı", why)

    def _persist(self) -> None:
        try:
            store.write_json(self.state_path, {TERMINAL: False, "source": "server", "updated_at": int(time.time())})
        except OSError as exc:
            log.error("%s yazılamadı; kapatma bu çalışmada geçerli, yeniden başlatmada kaybolabilir: %s",
                      self.state_path, exc)
