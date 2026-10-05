"""Fuzz hedeflerinin ortak kurulumu (docs/fuzzing.md): Backend içe aktarılabilir, zorunlu ortam değişkenleri sahte
değerle dolar (birim testlerindeki gibi), günlük çıktısı susar. Veritabanına ya da ağa bağlanılmaz.

Hedef dosyada atheris'ten ve bu modülden sonra, POps modülleri backend_imports() bloğunda içe aktarılır: yalnızca
onlar (pops, release_verify) kapsam için işaretlenir, üçüncü taraf paketler işaretlenmez (hızlı kalsın).
"""

import logging
import os
import sys

import atheris

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), os.pardir))
BACKEND = os.path.join(REPO, "Backend")
sys.path.insert(0, BACKEND)
for _k in ("JWT_SECRET", "DB_USER", "DB_PASS", "DB_NAME"):
    os.environ.setdefault(_k, "fuzz-only")
# Uyarılar stderr'e akmasın (hedefler kendi yakaladıklarını denetler); kök günlükçüde bir işleyici olunca Python'un
# son çare işleyicisi devreye girmez
logging.getLogger().addHandler(logging.NullHandler())

# Backend'in üçüncü taraf bağımlılıkları önce, işaretlenmeden yüklenir
import asyncpg  # noqa: E402,F401
import cryptography.hazmat.primitives.asymmetric.ed25519  # noqa: E402,F401
import fastapi  # noqa: E402,F401
import pydantic  # noqa: E402,F401
import starlette.websockets  # noqa: E402,F401


def backend_imports():
    """with backend_imports(): from pops import ...  — yalnızca POps kodu işaretlenir."""
    return atheris.instrument_imports(include=["pops", "release_verify", "system_routes"])


def run(test_one_input):
    atheris.Setup(sys.argv, test_one_input)
    atheris.Fuzz()


def bad_text(value):
    """PostgreSQL'e (metin ya da jsonb) yazılamayacak ilk metin, yoksa None: NUL (U+0000) ya da UTF-8'e çevrilemeyen
    (eşi olmayan vekil karakterli) metin. İkisi de sorguyu düşürür. Sözlük anahtarları ve iç içe yapılar da denetlenir
    (özyinelemesiz: JSON'un derinliği yığını aşmasın)."""
    stack = [value]
    while stack:
        item = stack.pop()
        if isinstance(item, str):
            if "\x00" in item:
                return item
            try:
                item.encode("utf-8")
            except UnicodeEncodeError:
                return item
        elif isinstance(item, dict):
            stack.extend(item.keys())
            stack.extend(item.values())
        elif isinstance(item, (list, tuple, set)):
            stack.extend(item)
    return None
