"""Günlük: journald (stderr; systemd zaman ve birim adını ekler) ve /var/log/pops-agent/agent.log.

Dosya her gün ve 10 MB'ı aşınca döner (agent.log.YYYY-MM-DD[.N]); 30 günden eski dosyalar ve toplamı 200 MB'ı
aşan en eski dosyalar silinir. Dosyaya yazma ayrı bir iş parçacığındadır (QueueListener): yavaş disk ajanın bağlantı
döngüsünü bekletmez. Gizli değerler (cihaz anahtarı, kayıt jetonu) ve komut metinleri hiçbir zaman loglanmaz.
"""

import datetime
import glob
import logging
import logging.handlers
import os
import queue
import sys
from typing import Optional

MAX_BYTES = 10 * 1024 * 1024
KEEP_DAYS = 30
TOTAL_CAP = 200 * 1024 * 1024


class DailySizeRotatingHandler(logging.Handler):
    def __init__(self, path: str, max_bytes: int = MAX_BYTES, keep_days: int = KEEP_DAYS, total_cap: int = TOTAL_CAP,
                 today=None):
        super().__init__()
        self.path = path
        self.max_bytes = max_bytes
        self.keep_days = keep_days
        self.total_cap = total_cap
        self._today = today or (lambda: datetime.date.today())
        self._stream = None
        self._day = None

    def _open(self):
        fd = os.open(self.path, os.O_WRONLY | os.O_APPEND | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o640)
        self._stream = os.fdopen(fd, "a", encoding="utf-8")
        try:
            mtime = datetime.date.fromtimestamp(os.fstat(fd).st_mtime)
        except OSError:
            mtime = self._today()
        self._day = mtime if os.fstat(fd).st_size else self._today()

    def _rotate(self):
        if self._stream:
            self._stream.close()
            self._stream = None
        base = "%s.%s" % (self.path, (self._day or self._today()).isoformat())
        target, n = base, 1
        while os.path.exists(target):
            target = "%s.%d" % (base, n)
            n += 1
        try:
            os.replace(self.path, target)
        except FileNotFoundError:
            pass
        self.prune()

    def prune(self):
        files = []
        for p in glob.glob(glob.escape(self.path) + ".*"):
            try:
                st = os.stat(p)
                files.append((st.st_mtime, st.st_size, p))
            except OSError:
                continue
        files.sort()
        cutoff = datetime.datetime.now().timestamp() - self.keep_days * 86400
        total = sum(f[1] for f in files)
        for mtime, size, p in files:
            if mtime < cutoff or total > self.total_cap:
                try:
                    os.unlink(p)
                    total -= size
                except OSError:
                    pass

    def emit(self, record):
        try:
            msg = self.format(record) + "\n"
            if self._stream is None:
                self._open()
            if self._day != self._today() or self._stream.tell() + len(msg) > self.max_bytes:
                if self._stream.tell() > 0:
                    self._rotate()
                    self._open()
                self._day = self._today()
            self._stream.write(msg)
            self._stream.flush()
        except Exception:
            self.handleError(record)

    def close(self):
        if self._stream:
            self._stream.close()
            self._stream = None
        super().close()


_listener: Optional[logging.handlers.QueueListener] = None


def setup(log_file: Optional[str], verbose: bool = False) -> None:
    global _listener
    root = logging.getLogger()
    root.setLevel(logging.DEBUG if verbose else logging.INFO)
    for h in list(root.handlers):
        root.removeHandler(h)
    # websockets'ın DEBUG kaydı el sıkışma başlıklarını (X-Agent-Secret, X-Enroll-Token) ve mesajların başını
    # (set_secret) yazar: --verbose'da da kapalı kalır
    logging.getLogger("websockets").setLevel(logging.WARNING)
    logging.getLogger("asyncio").setLevel(logging.WARNING)
    console = logging.StreamHandler(sys.stderr)
    # journald zamanı kendisi ekler; terminalde çalışırken de okunur kalsın diye kısa biçim
    console.setFormatter(logging.Formatter("%(levelname)s %(name)s: %(message)s"))
    root.addHandler(console)
    if log_file:
        try:
            os.makedirs(os.path.dirname(log_file), mode=0o750, exist_ok=True)
            fh = DailySizeRotatingHandler(log_file)
            fh.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(name)s: %(message)s"))
            q = queue.Queue(10000)
            root.addHandler(logging.handlers.QueueHandler(q))
            _listener = logging.handlers.QueueListener(q, fh, respect_handler_level=False)
            _listener.start()
        except OSError as exc:
            logging.getLogger("pops").error("Günlük dosyası açılamadı (%s): %s", log_file, exc)


def shutdown() -> None:
    global _listener
    if _listener:
        _listener.stop()
        _listener = None
    logging.shutdown()
