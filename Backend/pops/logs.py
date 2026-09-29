"""Yapılandırılmış log: her satır tek bir JSON nesnesi (ya da LOG_FORMAT=text ile okunur metin).

- request_id: her HTTP isteğine/WebSocket bağlantısına bir kimlik verilir (X-Request-ID); o istek sırasında
  yazılan her log satırında görünür, yanıt başlığında da döner. Kullanıcı hata bildirirken bu kimliği verirse
  ilgili satırlar tek aramayla bulunur.
- Son hatalar: ERROR ve üstü kayıtların son RECENT_MAX tanesi bellekte tutulur; panel (Sistem sayfası) ve
  /api/system/diagnostics bunları gösterir. Süreç yeniden başlayınca sıfırlanır; kalıcı kayıt journald'dadır.
- Seviye sayaçları: WARNING/ERROR/CRITICAL sayıları /metrics'e gider (sürekli hata basan bir bileşen görünür).

Ortam: LOG_LEVEL (varsayılan INFO), LOG_FORMAT (json | text, varsayılan json).
"""

import collections
import contextvars
import datetime
import json
import logging
import os
import sys

request_id_var = contextvars.ContextVar("pops_request_id", default=None)

RECENT_MAX = 100
recent_errors = collections.deque(maxlen=RECENT_MAX)
level_counts = collections.Counter()

# LogRecord'un kendi alanları; bunların dışındakiler (logger.info(..., extra={...})) JSON'a eklenir
_STD_ATTRS = set(vars(logging.LogRecord("", 0, "", 0, "", None, None))) | {"message", "asctime", "color_message"}

_configured = False


def _ts(record):
    return datetime.datetime.fromtimestamp(record.created, datetime.timezone.utc).isoformat(timespec="milliseconds")


_PLAIN = (str, int, float, bool, type(None), list, tuple, dict)


def _extras(record):
    # Yalnızca düz değerler: uvicorn bazı kayıtlara nesne ekler (ör. "connection open" satırında websocket
    # protokol nesnesi); onların repr'i log'a bir şey katmaz
    return {
        k: v for k, v in vars(record).items()
        if k not in _STD_ATTRS and not k.startswith("_") and isinstance(v, _PLAIN)
    }


class JsonFormatter(logging.Formatter):
    def format(self, record):
        out = {"ts": _ts(record), "level": record.levelname, "logger": record.name, "msg": record.getMessage()}
        rid = request_id_var.get()
        if rid:
            out["request_id"] = rid
        out.update(_extras(record))
        if record.exc_info:
            out["exc"] = self.formatException(record.exc_info)
        return json.dumps(out, ensure_ascii=False, default=str)


class TextFormatter(logging.Formatter):
    def format(self, record):
        rid = request_id_var.get()
        extras = _extras(record)
        line = "%s %-7s %s%s: %s" % (
            _ts(record), record.levelname, record.name, " [%s]" % rid if rid else "", record.getMessage(),
        )
        if extras:
            line += " " + json.dumps(extras, ensure_ascii=False, default=str)
        if record.exc_info:
            line += "\n" + self.formatException(record.exc_info)
        return line


class _StatsHandler(logging.Handler):
    """Seviye sayaçları + son hatalar (formatlamaz, dışarı yazmaz)."""

    def emit(self, record):
        try:
            if record.levelno >= logging.WARNING:
                level_counts[record.levelname] += 1
            if record.levelno >= logging.ERROR:
                exc = None
                if record.exc_info and record.exc_info[1] is not None:
                    exc = "%s: %s" % (type(record.exc_info[1]).__name__, record.exc_info[1])
                recent_errors.append({
                    "ts": _ts(record),
                    "level": record.levelname,
                    "logger": record.name,
                    "msg": record.getMessage()[:500],
                    "request_id": request_id_var.get(),
                    "exc": exc[:500] if exc else None,
                })
        except Exception:  # log altyapısı hiçbir zaman isteği düşürmemeli
            pass


class _DropDuplicateAsgiError(logging.Filter):
    # Yakalanmayan istisna, istek middleware'inde request_id ile zaten loglanır; uvicorn aynı hatayı
    # "Exception in ASGI application" diyerek bir kez daha yazar.
    def filter(self, record):
        return not record.getMessage().startswith("Exception in ASGI application")


def setup_logging():
    """Kök logger'ı ve uvicorn logger'larını tek biçime bağlar. Birden çok çağrı zararsızdır."""
    global _configured
    if _configured:
        return
    level = getattr(logging, os.environ.get("LOG_LEVEL", "INFO").strip().upper(), logging.INFO)
    fmt = os.environ.get("LOG_FORMAT", "json").strip().lower()

    stream = logging.StreamHandler(sys.stderr)
    stream.setFormatter(TextFormatter() if fmt == "text" else JsonFormatter())

    root = logging.getLogger()
    for h in list(root.handlers):
        root.removeHandler(h)
    root.addHandler(stream)
    root.addHandler(_StatsHandler())
    root.setLevel(level)

    # uvicorn kendi handler'larıyla gelir; onları kaldırıp kök logger'a yönlendir
    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        lg = logging.getLogger(name)
        for h in list(lg.handlers):
            lg.removeHandler(h)
        lg.propagate = True
    logging.getLogger("uvicorn.error").addFilter(_DropDuplicateAsgiError())
    _configured = True
