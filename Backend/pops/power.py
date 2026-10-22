"""Güç komutları ve kullanıcıya mesaj (ajan sözleşmesi docs/agent.md "power" ve "user_message").

Panel eskiden kapatma/yeniden başlatmayı "execute" ile bir komut olarak gönderiyordu (shutdown /s /f /t 5) ve mesajı
"msg *" komutuyla. Artık görev kaydında kind = 'power' ya da 'user_message', ayrıntı payload'dadır; kuyruk ajana komut
değil ayrı bir ileti gönderir. Metin kabuktan hiç geçmez, JSON alanı olarak gider.

Ajan bağlanırken X-Agent-Features ile "power" ve "message" duyurur. Duyurmayan (eski ya da Linux) ajanda:
  - kapatma / yeniden başlatma eski "execute" komutuyla gider (shutdown /s|/r /f /t N; Linux ajanı bu kalıbı
    systemctl poweroff|reboot'a çevirir). Notu yalnızca güvenle tırnaklanabilen karakterlerle Windows'un
    shutdown /c açıklamasına konur; Linux'ta kalıp değişmesin diye not hiç eklenmez;
  - oturumu kapatma, kilitleme ve mesajın güvenli bir karşılığı yok: görev gönderilmeden "Denied" olur (-8).
"""

import json
import re
import unicodedata
from typing import Iterable, Optional

KIND_POWER = "power"
KIND_MESSAGE = "user_message"
KINDS = (KIND_POWER, KIND_MESSAGE)
FEATURE_POWER = "power"
FEATURE_MESSAGE = "message"

OPS = ("shutdown", "restart", "logoff", "lock")
FALLBACK_OPS = ("shutdown", "restart")   # eski ajanda execute ile yapılabilenler
STYLES = ("info", "warning")
MAX_DELAY = 600
MAX_NOTE = 200      # güç komutunun kullanıcıya notu
MAX_TITLE = 80
MAX_TEXT = 1000
FALLBACK_MIN_DELAY = 5   # eski komutun gecikmesi: panelin bugüne kadar gönderdiği değer

# Görev kaydının başlığı (Türkçe veri; panel POps.taskName ile çevirir)
OP_TITLES = {"shutdown": "Kapat", "restart": "Yeniden başlat", "logoff": "Oturumu kapat", "lock": "Kilitle"}
MESSAGE_TITLE = "Mesaj"

# Çıkış kodları. -5: ajan yerel yetenek kapalı olduğu için yapmadı (+ capability_denied). -6: oturum açmış kullanıcı
# yok (oturumu kapatma, kilitleme, mesaj). -8: sunucu, ajan bu iletiyi desteklemiyor (görev gönderilmedi).
EXIT_DENIED = -5
EXIT_NO_SESSION = -6
EXIT_UNSUPPORTED = -8
UNSUPPORTED_OUTPUT = "[REDDEDİLDİ] Bu bilgisayardaki ajan bunu desteklemiyor; görev gönderilmedi. Ajanı güncelleyin."
MODULE_CLOSED_OUTPUT = ("[MODÜL KAPALI]: Bu bilgisayardaki ajan eski; güç komutu ona uzak komut olarak gider ve uzak "
                        "komut modülü bu cihazın laboratuvarında kapalı. Görev gönderilmedi.")
INVALID_OUTPUT = "[HATA]: Görevin ayrıntısı geçersiz; ajana gönderilmedi."

# Satır sonu ve sekme dışındaki kontrol karakterleri, yön değiştiren biçim karakterleri (metni tersine gösterip
# okuyanı yanıltabilir), sıfır genişlikli karakterler ve BOM silinir
_STRIP = re.compile("[​-‏‪-‮⁠-⁩﻿]")
# Eski Windows ajanının .bat dosyasında tırnak içinde güvenle duran karakterler (harf ve rakam ayrıca): % ! " ^ & | <
# > ve satır sonu yok
_NOTE_SAFE = set(" .,:;?'()-")


def clean_text(value, limit: int, multiline: bool = False) -> str:
    """Kontrol karakterleri atılmış, baştan/sondan boşluğu kırpılmış metin. multiline'da satır sonları (\\n) kalır
    (\\r\\n ve \\r -> \\n, en çok iki boş satır art arda); değilse her satır sonu ve sekme bir boşluk olur. Sınırı
    aşan metin kesilmez, reddedilir (ValueError): kullanıcı göndereceğini görsün."""
    if value is None:
        return ""
    if not isinstance(value, str):
        raise ValueError("Metin bekleniyordu")
    text = unicodedata.normalize("NFC", value).replace("\r\n", "\n").replace("\r", "\n")
    text = _STRIP.sub("", text)
    out = []
    for ch in text:
        if ch == "\n" and multiline:
            out.append(ch)
        elif ch in "\n\t":
            out.append(" ")
        elif unicodedata.category(ch) == "Cc":
            continue
        else:
            out.append(ch)
    text = "".join(out)
    if multiline:
        text = re.sub(r"[ ]+\n", "\n", text)
        text = re.sub(r"\n{3,}", "\n\n", text)
    text = text.strip()
    if len(text) > limit:
        raise ValueError("Metin en çok %d karakter olabilir" % limit)
    return text


def preview(text: Optional[str]) -> dict:
    """Denetim kaydı için: metnin kendisi yazılmaz, uzunluğu ve ilk 60 karakteri."""
    text = text or ""
    return {"length": len(text), "preview": text[:60] + ("…" if len(text) > 60 else "")}


def power_payload(op: str, delay: int, note: str = "") -> dict:
    if op not in OPS:
        raise ValueError("op shutdown, restart, logoff ya da lock olmalı")
    if isinstance(delay, bool) or not isinstance(delay, int) or not 0 <= delay <= MAX_DELAY:
        raise ValueError("Gecikme 0-%d saniye olmalı" % MAX_DELAY)
    return {"op": op, "delay": delay, "message": clean_text(note, MAX_NOTE) or None}


def message_payload(title: str, text: str, style: str = "info", requires_ack: bool = False) -> dict:
    title = clean_text(title, MAX_TITLE)
    text = clean_text(text, MAX_TEXT, multiline=True)
    if not title:
        raise ValueError("Başlık boş olamaz")
    if not text:
        raise ValueError("Metin boş olamaz")
    if style not in STYLES:
        raise ValueError("style info ya da warning olmalı")
    return {"title": title, "text": text, "style": style, "requires_ack": bool(requires_ack)}


def summary(kind: str, spec: dict) -> str:
    """Görevin script_path'i: panelde ve denetimde görünen okunur özet. Ajana gitmez; mesaj metnini taşımaz."""
    if kind == KIND_POWER:
        return "power %s delay=%d" % (spec["op"], spec["delay"])
    return "user_message %s%s" % (spec["style"], " ack" if spec["requires_ack"] else "")


def read_payload(kind: str, value) -> dict:
    """Görev kaydındaki payload (asyncpg JSONB'yi metin döndürür) yeniden doğrulanır; bozuksa ValueError."""
    data = json.loads(value) if isinstance(value, str) else value
    if not isinstance(data, dict):
        raise ValueError("payload nesne değil")
    if kind == KIND_POWER:
        return power_payload(data.get("op"), data.get("delay"), data.get("message") or "")
    if kind == KIND_MESSAGE:
        return message_payload(data.get("title"), data.get("text"), data.get("style"), data.get("requires_ack"))
    raise ValueError("bilinmeyen görev türü")


def feature_for(kind: str) -> str:
    return FEATURE_POWER if kind == KIND_POWER else FEATURE_MESSAGE


def supports(kind: str, features: Optional[Iterable[str]]) -> bool:
    return feature_for(kind) in (features or ())


def can_fallback(kind: str, spec: dict) -> bool:
    return kind == KIND_POWER and spec.get("op") in FALLBACK_OPS


def plan(kind: str, spec: dict, features: Optional[Iterable[str]], platform: Optional[str]) -> str:
    """Bu ajana nasıl gidecek: "native" (yeni ileti), "fallback" (eski execute komutu) ya da "unsupported"."""
    if supports(kind, features):
        return "native"
    return "fallback" if can_fallback(kind, spec) else "unsupported"


def safe_note(note: Optional[str]) -> str:
    """Eski Windows ajanının shutdown /c açıklaması: yalnızca harf, rakam, boşluk ve . , : ; ? ' ( ) - kalır."""
    kept = "".join(ch if (ch.isalnum() or ch in _NOTE_SAFE) else " " for ch in (note or ""))
    return re.sub(r" {2,}", " ", kept).strip()[:MAX_NOTE]


def fallback_command(spec: dict, platform: Optional[str]) -> str:
    """Eski ajana giden komut. Linux ajanı yalnızca "shutdown /r|/s /f /t N" kalıbını tanır: not eklenmez."""
    if not can_fallback(KIND_POWER, spec):
        raise ValueError("Bu işlemin eski ajanda karşılığı yok")
    flag = "/s" if spec["op"] == "shutdown" else "/r"
    command = "shutdown %s /f /t %d" % (flag, max(int(spec["delay"]), FALLBACK_MIN_DELAY))
    note = safe_note(spec.get("message")) if (platform or "windows") == "windows" else ""
    return command + (' /c "%s"' % note if note else "")


def message(task: dict, requested_by: str, features: Optional[Iterable[str]], platform: Optional[str]) -> dict:
    """Ajana giden ileti (docs/agent.md). Ayrıntı gönderimden hemen önce yeniden doğrulanır. Ajan desteklemiyorsa ve
    karşılığı yoksa ValueError (kuyruk görevi zaten önceden "Denied" yapar)."""
    kind = task.get("kind")
    spec = read_payload(kind, task.get("payload"))
    how = plan(kind, spec, features, platform)
    if how == "native" and kind == KIND_POWER:
        return {"action": "power", "task_id": task["id"], "op": spec["op"], "delay": spec["delay"],
                "message": spec["message"], "requested_by": requested_by}
    if how == "native":
        return {"action": "user_message", "task_id": task["id"], "title": spec["title"], "text": spec["text"],
                "style": spec["style"], "requires_ack": spec["requires_ack"], "requested_by": requested_by}
    if how == "fallback":
        return {"action": "execute", "task_id": task["id"], "script_path": fallback_command(spec, platform),
                "requested_by": requested_by}
    raise ValueError("Bu bilgisayardaki ajan bunu desteklemiyor")
