"""Yalnızca root'un okuyabildiği durum dosyaları: atomik yazma (geçici dosya + rename), 0600 dosya, 0700 klasör."""

import json
import os
from typing import Any, Optional


def ensure_dir(path: str, mode: int = 0o700) -> None:
    os.makedirs(path, mode=mode, exist_ok=True)
    try:
        if (os.stat(path).st_mode & 0o7777) != mode:
            os.chmod(path, mode)
    except OSError:
        pass


def write_atomic(path: str, data, mode: int = 0o600) -> None:
    """Yarım yazılmış dosya kalmaz: önce aynı klasörde geçici dosya, fsync, sonra rename. Bağlantı izlenmez."""
    if isinstance(data, str):
        data = data.encode("utf-8")
    directory = os.path.dirname(path) or "."
    if not os.path.isdir(directory):
        ensure_dir(directory, 0o700)
    tmp = "%s.tmp.%d" % (path, os.getpid())
    fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | getattr(os, "O_NOFOLLOW", 0), mode)
    try:
        os.fchmod(fd, mode)
        view = memoryview(data)
        while view:
            written = os.write(fd, view)
            view = view[written:]
        os.fsync(fd)
    finally:
        os.close(fd)
    try:
        os.replace(tmp, path)
    except OSError:
        delete(tmp)
        raise
    try:
        dfd = os.open(directory, os.O_RDONLY)
        try:
            os.fsync(dfd)
        finally:
            os.close(dfd)
    except OSError:
        pass


def write_json(path: str, value: Any, mode: int = 0o600) -> None:
    write_atomic(path, json.dumps(value, ensure_ascii=False, sort_keys=True), mode)


def read_bytes(path: str) -> Optional[bytes]:
    try:
        with open(path, "rb") as f:
            return f.read()
    except FileNotFoundError:
        return None


def read_text(path: str) -> Optional[str]:
    raw = read_bytes(path)
    if raw is None:
        return None
    return raw.decode("utf-8", errors="replace")


def read_json(path: str) -> Any:
    """Dosya yoksa None; bozuksa ValueError (çağıran güvenli yöne düşer)."""
    text = read_text(path)
    if text is None:
        return None
    return json.loads(text)


def delete(path: str) -> bool:
    try:
        os.unlink(path)
        return True
    except FileNotFoundError:
        return False
