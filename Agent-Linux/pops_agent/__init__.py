"""POps Linux ajanı (Pardus/Debian). Windows ajanıyla aynı /ws/agent protokolü; bkz. Agent-Linux/README.md.

Yalnızca standart kütüphane ile dağıtımın python3-websockets ve python3-cryptography paketleri kullanılır.
"""

import os

PLATFORM = "linux"


def _read_version() -> str:
    # Paket: build_deb.py kök VERSION dosyasından _version.py yazar. Kaynak ağacında doğrudan VERSION okunur.
    try:
        from pops_agent._version import VERSION  # noqa: F401  (yalnızca pakette var)

        return VERSION
    except ImportError:
        pass
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.pardir, os.pardir, "VERSION")
    try:
        with open(path, encoding="utf-8") as f:
            value = f.read().strip()
            if value:
                return value
    except OSError:
        pass
    return "0.0.0-dev"


__version__ = _read_version()
