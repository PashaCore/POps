"""winget ile paket dağıtımı (docs/decisions.md D-23, ajan sözleşmesi docs/agent.md "winget_install").

Dağıtım zincirinin WINGET adımı kurulacak paketi ({"id", "version"}) taşır, komut taşımaz. Görev kaydında
kind = 'winget', paket payload'dadır; kuyruk ajana "execute" yerine "winget_install" iletisi gönderir. Ajan winget'i
SYSTEM olarak, kabuk OLMADAN, aşağıdaki sabit argümanlarla çalıştırır; kimlik ve sürüm ayrı argümanlardır.

Kimlik ve sürüm burada (istekte ve gönderimden hemen önce yeniden) doğrulanır: izinli karakterler harf, rakam ve
". + _ -"dir; boşluk, tırnak, kabuk karakterleri ve satır sonu geçemez. Ajan aynı denetimi kendisi de yapar.

Eski ajan bilinmeyen iletiyi yok sayar (görev "Running"de kalırdı); bu yüzden görev yalnızca bağlanırken
X-Agent-Features ile "winget" duyuran ajana gönderilir, diğerlerinde gönderilmeden "Denied" olur.
"""

import json
import re
from typing import Dict, Iterable, List, Optional

from pops import winget_catalog

KIND = "winget"
FEATURE = "winget"

# fullmatch: "$" satır sonundan önce de eşleşirdi ("Mozilla.Firefox\n" geçmesin)
_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9.+_-]{1,127}")
_VERSION = re.compile(r"[0-9A-Za-z.+_-]{1,40}")

# Ajanın winget'e verdiği argümanlar (sıra ve yazım sözleşmenin parçasıdır; docs/agent.md)
BASE_ARGS = (
    "install", "--id", None, "-e", "--silent", "--scope", "machine",
    "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
)

# Çıkış kodları. -5: ajan politika gereği çalıştırmadı (execute ile aynı). -7: ajan, bilgisayarda winget yok.
# -8: sunucu, ajan winget'i desteklemiyor (görev gönderilmedi).
EXIT_DENIED = -5
EXIT_UNAVAILABLE = -7
EXIT_UNSUPPORTED = -8
UNSUPPORTED_OUTPUT = ("[REDDEDİLDİ] Bu bilgisayardaki ajan winget kurulumunu desteklemiyor; görev gönderilmedi. "
                      "Ajanı güncelleyin.")
MODULE_CLOSED_OUTPUT = ("[MODÜL KAPALI]: Dosya dağıtımı modülü bu cihazın laboratuvarında kapalı; winget kurulumu "
                        "başlatılmadı.")
INVALID_OUTPUT = "[HATA]: winget görevinin paket bilgisi geçersiz; ajana gönderilmedi."

# Sıfır olmasa da kurulumun yerinde olduğunu söyleyen winget kodları (HRESULT, ajan int32 olarak iletir):
# güncel sürüm zaten kurulu (UPDATE_NOT_APPLICABLE, PACKAGE_ALREADY_INSTALLED) ya da kurulum bitti ama yeniden
# başlatma istiyor (INSTALL_REBOOT_REQUIRED_TO_FINISH, INSTALL_REBOOT_INITIATED). Görev "Completed" olur.
OK_EXIT_CODES = (0, -1978335189, -1978335135, -1978334967, -1978334965)

CATEGORY_LABELS = {
    "browser": "Tarayıcılar",
    "office": "Ofis ve PDF",
    "education": "Eğitim",
    "programming": "Yazılım geliştirme",
    "graphics": "Grafik ve tasarım",
    "media": "Ses ve video",
    "communication": "İletişim",
    "utility": "Araçlar",
    "runtime": "Çalışma ortamları",
}


def clean_id(value) -> str:
    if not isinstance(value, str) or not _ID.fullmatch(value):
        raise ValueError("Geçersiz winget paket kimliği (harf, rakam ve . + _ - ; 2-128 karakter)")
    return value


def clean_version(value) -> Optional[str]:
    if value is None or value == "":
        return None
    if not isinstance(value, str) or not _VERSION.fullmatch(value):
        raise ValueError("Geçersiz sürüm (harf, rakam ve . + _ - ; en çok 40 karakter)")
    return value


def arguments(package_id: str, version: Optional[str] = None) -> List[str]:
    args = [clean_id(package_id) if a is None else a for a in BASE_ARGS]
    version = clean_version(version)
    return args + ["--version", version] if version else args


def command_line(package_id: str, version: Optional[str] = None) -> str:
    """Görevin script_path'i: ajanın çalıştıracağı komutun okunur hâli (panel, denetim kaydı). Ajana gitmez."""
    return "winget " + " ".join(arguments(package_id, version))


def payload(package_id: str, version: Optional[str] = None) -> str:
    return json.dumps({"id": clean_id(package_id), "version": clean_version(version)})


def read_payload(value) -> dict:
    """Görev kaydındaki payload (asyncpg JSONB'yi metin döndürür) -> {"id", "version"}; bozuksa ValueError."""
    data = json.loads(value) if isinstance(value, str) else value
    if not isinstance(data, dict):
        raise ValueError("payload nesne değil")
    return {"id": clean_id(data.get("id")), "version": clean_version(data.get("version"))}


def message(task: dict, requested_by: str) -> dict:
    """Ajana giden iletinin kendisi (docs/agent.md). Paket bilgisi gönderimden hemen önce yeniden doğrulanır."""
    spec = read_payload(task.get("payload"))
    return {"action": "winget_install", "task_id": task["id"], "id": spec["id"], "version": spec["version"],
            "requested_by": requested_by}


def supports(features: Optional[Iterable[str]]) -> bool:
    return FEATURE in (features or ())


# ── Katalog ────────────────────────────────────────────────────────────────────────────────────────────────────
_FOLD = str.maketrans({"ı": "i", "ş": "s", "ğ": "g", "ü": "u", "ö": "o", "ç": "c", "â": "a", "î": "i", "û": "u"})


def _fold(text: str) -> str:
    """Arama için: Türkçe büyük/küçük harf ve aksan farkı yok sayılır ("TARAYICI" = "tarayici" = "tarayıcı")."""
    return str(text or "").replace("I", "ı").replace("İ", "i").lower().translate(_FOLD)


_INDEX = [
    (p, _fold(" ".join([p["id"], p["name"], p["publisher"], p["category"], CATEGORY_LABELS.get(p["category"], ""),
                        p["description"], p.get("description_en", "")])))
    for p in winget_catalog.PACKAGES
]


def search(q: str = "", category: Optional[str] = None, limit: int = 200) -> Dict:
    """Katalogda arama: sözcüklerin hepsi kimlik, ad, yayıncı, kategori ya da açıklamada geçmeli. Sıra: kimliği
    ya da adı aramayla başlayanlar önce, sonra katalog sırası (kategori, ad)."""
    words = _fold(q).split()[:8]
    first = words[0] if words else ""
    hits = []
    for order, (p, text) in enumerate(_INDEX):
        if category and p["category"] != category:
            continue
        if not all(w in text for w in words):
            continue
        name, pid = _fold(p["name"]), _fold(p["id"])
        rank = 0 if first and first == pid else 1 if first and (name.startswith(first) or pid.startswith(first)) else 2
        hits.append((rank if words else 2, order, p))
    hits.sort(key=lambda h: (h[0], h[1]))
    counts = {}
    for p in winget_catalog.PACKAGES:
        counts[p["category"]] = counts.get(p["category"], 0) + 1
    return {
        "items": [dict(h[2]) for h in hits[:max(1, min(int(limit), 500))]],
        "matched": len(hits),
        "total": len(winget_catalog.PACKAGES),
        "categories": [{"id": k, "label": v, "count": counts.get(k, 0)} for k, v in CATEGORY_LABELS.items()
                       if counts.get(k)],
        "updated": winget_catalog.UPDATED,
        "source": winget_catalog.SOURCE,
    }
