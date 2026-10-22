"""Yapılandırılmış log: her satır tek bir JSON nesnesi (ya da LOG_FORMAT=text ile okunur metin).

- request_id: her HTTP isteğine/WebSocket bağlantısına bir kimlik verilir (X-Request-ID); o istek sırasında
  yazılan her log satırında görünür, yanıt başlığında da döner. Kullanıcı hata bildirirken bu kimliği verirse
  ilgili satırlar tek aramayla bulunur.
- Son hatalar: ERROR ve üstü kayıtların son RECENT_MAX tanesi bellekte tutulur; panel (Sistem sayfası) ve
  /api/system/diagnostics bunları gösterir. Süreç yeniden başlayınca sıfırlanır; kalıcı kayıt journald'dadır.
- Seviye sayaçları: WARNING/ERROR/CRITICAL sayıları /metrics'e gider (sürekli hata basan bir bileşen görünür).
- Satır olay döngüsünde biçimlenir (request_id o anki bağlantının), stderr'e AYRI bir iş parçacığı yazar. stderr bir
  dosya, Docker borusu ya da journald soketidir; disk takılınca yazım saniyelerce bekleyebilir (ölçüldü: dosyaya
  ekleme 5–30 sn). Yazım olay döngüsündeyken o sürede hiçbir bağlantı işlenmiyordu: ajanların el sıkışması zaman
  aşımına uğruyordu. Sıra dolarsa (yazıcı uzun süre takılı) yeni satırlar atılır, sayılır ve yazıcı yetişince tek
  satırla bildirilir; olay döngüsü beklemez.

Ortam: LOG_LEVEL (varsayılan INFO), LOG_FORMAT (json | text, varsayılan json).
"""

import atexit
import collections
import contextvars
import datetime
import json
import logging
import logging.handlers
import os
import queue
import sys
import threading

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


QUEUE_MAX = 10000  # yazılmayı bekleyen en çok satır (birkaç MB); fazlası atılır
_STOP_WAIT_SECONDS = 5.0  # süreç kapanırken kalan satırların yazılması için en çok bu kadar beklenir


class BackgroundStreamHandler(logging.handlers.QueueHandler):
    """Kaydı çağıran iş parçacığında biçimler, akışa yazmayı arka plandaki tek bir yazıcıya bırakır (sıra korunur).
    Çağıran hiçbir zaman beklemez: sıra doluysa satır atılır ve dropped artar."""

    def __init__(self, stream, formatter: logging.Formatter, maxsize: int = QUEUE_MAX):
        super().__init__(queue.Queue(maxsize))
        self.setFormatter(formatter)
        self.stream = stream
        self.dropped = 0
        self._reported = 0
        self._direct = False  # stop() sonrası: satırlar doğrudan yazılır
        self._thread = threading.Thread(target=self._write_loop, name="pops-log-writer", daemon=True)
        self._thread.start()

    def emit(self, record):
        if not self._direct:
            super().emit(record)
            return
        try:
            line = self.format(record)
        except Exception:
            self.handleError(record)
            return
        self._write(line)

    def enqueue(self, record):
        try:
            self.queue.put_nowait(record)
        except queue.Full:
            self.dropped += 1

    def _write(self, line: str) -> None:
        # Kilit tutulmaz: takılı bir yazım, kapanıştaki logging.shutdown'ı da bekletmesin
        try:
            self.stream.write(line + "\n")
            self.stream.flush()
        except Exception:  # log altyapısı hiçbir zaman isteği düşürmemeli
            pass

    def _report_drops(self) -> None:
        if self.dropped != self._reported:
            lost, self._reported = self.dropped - self._reported, self.dropped
            note = logging.LogRecord(
                "pops.logs", logging.WARNING, __file__, 0, "log yazımı yetişemedi, satırlar atıldı", None, None
            )
            note.dropped = lost
            self._write(self.format(note))

    def _write_loop(self):
        while True:
            record = self.queue.get()
            if record is None:
                return
            self._report_drops()
            self._write(record.getMessage())  # prepare() satırı biçimlemiş, msg odur

    def stop(self, timeout: float = _STOP_WAIT_SECONDS) -> None:
        """Sıradakiler yazılır, sonraki satırlar doğrudan (çağıranda) yazılır. uvicorn SIGTERM'le kapanınca sinyali
        yeniden gönderir ve süreç atexit çalışmadan biter; sıradaki son satırlar kaybolmasın diye sunucunun kapanış
        adımı bunu çağırır (atexit diğer çıkışlar için). Yazıcı takılıysa en çok timeout saniye beklenir."""
        if self._direct:
            return
        try:
            self.queue.put(None, timeout=timeout)
        except queue.Full:
            return
        self._thread.join(timeout)
        if self._thread.is_alive():
            return
        self._direct = True
        while True:  # sonlandırıcıdan sonra sıraya girenler (başka iş parçacıklarından)
            try:
                record = self.queue.get_nowait()
            except queue.Empty:
                break
            if record is not None:
                self._write(record.getMessage())
        self._report_drops()


_handler = None


def stop_background_writer() -> None:
    """Sıradaki satırları yazar, sonrakileri doğrudan yazdırır (bkz. BackgroundStreamHandler.stop)."""
    if _handler is not None:
        _handler.stop()


class _DropDuplicateAsgiError(logging.Filter):
    # Yakalanmayan istisna, istek middleware'inde request_id ile zaten loglanır; uvicorn aynı hatayı
    # "Exception in ASGI application" diyerek bir kez daha yazar.
    def filter(self, record):
        return not record.getMessage().startswith("Exception in ASGI application")


def setup_logging():
    """Kök logger'ı ve uvicorn logger'larını tek biçime bağlar. Birden çok çağrı zararsızdır."""
    global _configured, _handler
    if _configured:
        return
    level = getattr(logging, os.environ.get("LOG_LEVEL", "INFO").strip().upper(), logging.INFO)
    fmt = os.environ.get("LOG_FORMAT", "json").strip().lower()

    stream = _handler = BackgroundStreamHandler(sys.stderr, TextFormatter() if fmt == "text" else JsonFormatter())
    atexit.register(stream.stop)

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
