"""Oturum açan kullanıcı: systemd-logind'den (loginctl) etkin, yerel, grafik ya da konsol oturumu.

Windows SessionReporter ile aynı sözleşme: değişiklik /api/auth/login ve /api/auth/logout ile bildirilir (panelde
"oturum açan kullanıcı"); son bildirilen oturum /var/lib/pops-agent/session.json'da tutulur, ajan yeniden
başlayınca aynı oturum için yeniden giriş yazılmaz. Aynı oturum: aynı kullanıcı, aynı oturum numarası, aynı açılış
(boot_id).
"""

import logging
import subprocess
from typing import Dict, List, Optional, Tuple

log = logging.getLogger("pops.session")

_ENV = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "SYSTEMD_PAGER": "", "SYSTEMD_COLORS": "0"}


def _run(args: List[str]) -> Optional[str]:
    try:
        proc = subprocess.run(args, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=10, check=False,
                              env=_ENV)
    except (OSError, subprocess.TimeoutExpired):
        return None
    return proc.stdout.decode("utf-8", "replace") if proc.returncode == 0 else None


def parse_show(text: str) -> Dict[str, str]:
    out = {}
    for line in (text or "").splitlines():
        if "=" in line:
            k, v = line.split("=", 1)
            out[k.strip()] = v.strip()
    return out


def choose(sessions: List[Dict[str, str]]) -> Optional[Dict[str, str]]:
    """Etkin, uzak olmayan, kullanıcı sınıfı; grafik oturum ve seat0 öncelikli."""
    best = None
    for s in sessions:
        if s.get("Class") != "user" or s.get("Remote") == "yes" or s.get("Active") != "yes":
            continue
        if not s.get("Name") or s.get("State") == "closing":
            continue
        rank = (0 if s.get("Type") in ("x11", "wayland", "mir") else 1, 0 if s.get("Seat") == "seat0" else 1)
        if best is None or rank < best[0]:
            best = (rank, s)
    return best[1] if best else None


def current() -> Tuple[Optional[str], Optional[str]]:
    """(kullanıcı, oturum numarası); kimse yoksa ya da logind yoksa (None, None)."""
    listing = _run(["loginctl", "list-sessions", "--no-legend", "--no-pager"])
    if listing is None:
        return None, None
    sessions = []
    for line in listing.splitlines():
        parts = line.split()
        if not parts:
            continue
        show = _run(["loginctl", "show-session", parts[0], "-p", "Id", "-p", "Name", "-p", "Class", "-p", "Type",
                     "-p", "Active", "-p", "Remote", "-p", "Seat", "-p", "State"])
        if show:
            sessions.append(parse_show(show))
    chosen = choose(sessions)
    return (chosen.get("Name"), chosen.get("Id")) if chosen else (None, None)


def boot_id(proc_root: str = "/proc") -> str:
    try:
        with open(proc_root + "/sys/kernel/random/boot_id", encoding="ascii") as f:
            return f.read().strip()
    except OSError:
        return ""


def same_logon(a: Optional[dict], b: Optional[dict]) -> bool:
    return bool(a and b and a.get("user") and b.get("user") and a["user"] == b["user"]
                and a.get("session") == b.get("session") and a.get("boot") == b.get("boot"))


def diff(reported: Optional[dict], now: dict) -> List[Tuple[str, str]]:
    """Önce eski kullanıcının çıkışı, sonra yenisinin girişi."""
    if same_logon(reported, now):
        return []
    events = []
    if reported and reported.get("user"):
        events.append(("logout", reported["user"]))
    if now.get("user"):
        events.append(("login", now["user"]))
    return events
