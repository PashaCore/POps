"""Yerel denetim izi: bu bilgisayarda yapılan yönetici işlemleri (uzaktan komut, yetenek kapatma, kayıt, güncelleme),
sunucudan bağımsız. Windows ajanının Olay Günlüğü kayıtlarının (LocalAudit, olay 1000–1072) karşılığı.

/var/log/pops-agent/audit.log: her satır bir JSON nesnesi; "prev" önceki satırın "hash"i, "hash" bu satırın
(hash alanı hariç) SHA-256'sı. Satır silinir ya da değiştirilirse `pops-agent audit-verify` zincirin koptuğu yeri
gösterir. Dosyaya ext4/xfs'in "yalnızca ekleme" (chattr +a) özniteliği konur: root bile özniteliği kaldırmadan
satır silemez ya da dosyayı kısaltamaz. Komut metni yazılmaz (kişisel veri ya da parola içerebilir): SHA-256'sı ve
uzunluğu yazılır. Her kayıt journald'a da düşer.
"""

import fcntl
import hashlib
import json
import logging
import os
import struct
import threading
import time
from typing import List, Optional, Tuple

log = logging.getLogger("pops.audit")

# linux/fs.h
_FS_IOC_GETFLAGS = 0x80086601
_FS_IOC_SETFLAGS = 0x40086602
_FS_APPEND_FL = 0x00000020

GENESIS = "0" * 64

EVENT_IDS = {
    "command_started": 1000,
    "command_finished": 1001,
    "command_refused": 1002,
    "update_started": 1029,
    "update_result": 1030,
    "update_rejected": 1031,
    "capability_changed": 1040,
    "auth_rejected": 1050,
    "bypass_secret_received": 1060,
    "enrolled": 1061,
    "identity_changed": 1062,
    "clone_detected": 1070,
    "clone_rejected": 1071,
    "hardware_partly_changed": 1072,
    "unsupported_refused": 1080,
}


def _digest(entry: dict) -> str:
    body = {k: v for k, v in entry.items() if k != "hash"}
    return hashlib.sha256(json.dumps(body, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()


def command_sha256(command: str) -> str:
    return hashlib.sha256((command or "").encode("utf-8")).hexdigest()


class LocalAudit:
    def __init__(self, path: str, append_only: bool = False):
        """append_only: dosyaya chattr +a konur (kurulu ajanın /var/log/pops-agent/audit.log'u)."""
        self.path = path
        self._lock = threading.Lock()
        self._last = None
        self._append_only_tried = not append_only

    def _last_hash(self) -> str:
        if self._last is not None:
            return self._last
        last = GENESIS
        try:
            with open(self.path, "rb") as f:
                f.seek(0, os.SEEK_END)
                size = f.tell()
                f.seek(max(0, size - 65536))
                tail = f.read().splitlines()
            for line in reversed(tail):
                try:
                    last = json.loads(line.decode("utf-8"))["hash"]
                    break
                except (ValueError, KeyError, TypeError):
                    continue
        except FileNotFoundError:
            pass
        self._last = last
        return last

    def write(self, event: str, **fields) -> Optional[dict]:
        """Başarısız olursa ajanı durdurmaz (loglanır)."""
        with self._lock:
            try:
                entry = {"ts": time.strftime("%Y-%m-%dT%H:%M:%S%z"), "event_id": EVENT_IDS.get(event, 0),
                         "event": event, "prev": self._last_hash()}
                entry.update({k: v for k, v in fields.items() if v is not None})
                entry["hash"] = _digest(entry)
                line = (json.dumps(entry, ensure_ascii=False, sort_keys=True) + "\n").encode("utf-8")
                fd = os.open(self.path, os.O_WRONLY | os.O_APPEND | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o640)
                try:
                    os.write(fd, line)
                    if not self._append_only_tried:
                        self._append_only_tried = True
                        _set_append_only(fd)
                finally:
                    os.close(fd)
                self._last = entry["hash"]
                shown = {k: v for k, v in fields.items() if v is not None}
                log.info("Denetim: %s %s", event, json.dumps(shown, ensure_ascii=False))
                return entry
            except OSError as exc:
                log.error("Yerel denetim kaydı yazılamadı (%s): %s", event, exc)
                return None


def _flags(fd: int) -> int:
    return struct.unpack("l", fcntl.ioctl(fd, _FS_IOC_GETFLAGS, struct.pack("l", 0)))[0]


def is_append_only(path: str) -> bool:
    try:
        fd = os.open(path, os.O_RDONLY)
    except OSError:
        return False
    try:
        return bool(_flags(fd) & _FS_APPEND_FL)
    except OSError:
        return False
    finally:
        os.close(fd)


def clear_append_only(path: str) -> bool:
    """chattr -a (testler ve paket kaldırma)."""
    try:
        fd = os.open(path, os.O_RDONLY)
    except OSError:
        return False
    try:
        fcntl.ioctl(fd, _FS_IOC_SETFLAGS, struct.pack("l", _flags(fd) & ~_FS_APPEND_FL))
        return True
    except OSError:
        return False
    finally:
        os.close(fd)


def _set_append_only(fd: int) -> bool:
    """chattr +a (CAP_LINUX_IMMUTABLE gerekir; root'ta var, kapsayıcıda ya da desteklemeyen dosya sisteminde yok)."""
    try:
        flags = _flags(fd)
        if flags & _FS_APPEND_FL:
            return True
        fcntl.ioctl(fd, _FS_IOC_SETFLAGS, struct.pack("l", flags | _FS_APPEND_FL))
        return True
    except OSError:
        return False


def verify(path: str) -> Tuple[bool, int, List[str]]:
    """(zincir sağlam mı, satır sayısı, sorunlar)."""
    problems = []
    prev = GENESIS
    count = 0
    try:
        with open(path, "rb") as f:
            for number, raw in enumerate(f, 1):
                count = number
                try:
                    entry = json.loads(raw.decode("utf-8"))
                except ValueError:
                    problems.append("satır %d: JSON değil" % number)
                    prev = None
                    continue
                if prev is not None and entry.get("prev") != prev:
                    problems.append("satır %d: önceki satırla bağ kopuk (silinmiş ya da değiştirilmiş satır)" % number)
                if _digest(entry) != entry.get("hash"):
                    problems.append("satır %d: içerik değiştirilmiş" % number)
                prev = entry.get("hash")
    except FileNotFoundError:
        return True, 0, []
    return not problems, count, problems
