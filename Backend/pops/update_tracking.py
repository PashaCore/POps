"""Ajan güncellemesinin izi (S20): kime güncelleme gönderildi, hangi sonuç zaten kaydedildi.

Eskiden ikisi de yalnızca bellekteydi: sunucu yeniden başlarsa "güncelleme gönderildi, sonuç gelmedi" uyarısı
kayboluyordu; ajan da sonucu gönderir göndermez silindiği için sunucu kaydı yazmadan çökerse sonuç kayboluyordu.
Şimdi gönderimler pending_updates tablosunda, kaydedilmiş sonuçlar update_results'ta durur. 0.1.14+ ajan sonucu
sunucu onaylayana (update_result_ack) kadar saklar ve yeniden gönderir; aynı result_id ikinci kez kaydedilmez.

Ara adımlar (update_progress, docs/api.md): ajan emri aldıktan sonra sonuç gelene kadar nerede olduğunu bildirir.
Son adım bellekte (manager.update_stages) ve gönderimin satırında durur; sonuç gelince ya da gönderim unutulunca
silinir, yeni gönderim sıfırlar. Adımı bildirmeyen eski ajanda (0.1.21 ve öncesi) hiçbir şey değişmez.
"""

import re
import time
import unicodedata
from typing import Iterable, Optional, Set

from pops.db import execute_query
from pops.manager import manager

_RESULT_ID = re.compile(r"^[A-Za-z0-9_-]{8,64}$")
_VERSION = re.compile(r"^[0-9A-Za-z][0-9A-Za-z.+_-]{0,63}$")

# Ajanın bildirebileceği adımlar. "rejected" saklanmaz: güncellemeyi sonuç olarak bitirir (bkz. routers/agents.py).
STAGES = (
    "received",            # update_agent alındı
    "downloaded",          # paket indirildi
    "verified",            # boyut ve SHA-256 imzalı manifest'le eşleşti
    "updater_started",     # POpsUpdater başlatıldı
    "waiting_installer",   # Windows Installer meşgul (msiexec 1618), yeniden denenecek: attempt / of
    "installing",          # msiexec çalışıyor
    "rejected",            # ajan uygulamadı; detail = sebep
    "ignored_busy",        # başka bir güncelleme sürerken gelen emir yok sayıldı
)
DETAIL_MAX = 300
# Aynı sürüm bu süre içinde gönderildiyse (ya da ajan bu süre içinde adım bildirdiyse) yeniden gönderilmez: ajan
# update.lock'u 15 dk taze sayar, o sürede gelen ikinci emri zaten yok sayar
RESEND_AFTER_SECONDS = 15 * 60


def clean_result_id(value) -> Optional[str]:
    return value if isinstance(value, str) and _RESULT_ID.match(value) else None


def _text(value, limit: int) -> Optional[str]:
    """Ajandan gelen metin: kontrol karakterleri boşluk, biçim karakterleri (ör. yön değiştiren U+202E) silinir,
    boşluklar teke iner, en çok limit karakter."""
    if not isinstance(value, str):
        return None
    out = []
    for ch in value:
        cat = unicodedata.category(ch)
        if cat == "Cc":
            out.append(" ")
        elif cat != "Cf":
            out.append(ch)
    text = " ".join("".join(out).split())
    return text[:limit] or None


def _count(value) -> Optional[int]:
    return value if isinstance(value, int) and not isinstance(value, bool) and 0 < value <= 100 else None


def clean_progress(payload) -> Optional[dict]:
    """update_progress mesajının saklanacak hali; adım bilinmiyorsa None (yok sayılır)."""
    if not isinstance(payload, dict) or payload.get("stage") not in STAGES:
        return None
    attempt, of = _count(payload.get("attempt")), _count(payload.get("of"))
    if attempt is not None and of is not None and attempt > of:
        attempt = of = None
    version = payload.get("to_version")
    return {
        "stage": payload["stage"],
        "to_version": version if isinstance(version, str) and _VERSION.match(version) else None,
        "attempt": attempt,
        "of": of,
        "detail": _text(payload.get("detail"), DETAIL_MAX),
    }


def same_version(a: Optional[str], b: Optional[str]) -> bool:
    def norm(v):
        return str(v or "").strip().lower().lstrip("v")
    return norm(a) == norm(b)


def pending_version(pc: str) -> Optional[str]:
    entry = manager.pending_updates.get(pc)
    return entry[0] if entry else None


def accepts(pc: str, progress: dict) -> bool:
    """Adım saklanır mı: cihaza sonucu beklenen bir güncelleme gönderilmiş olmalı ve adım (sürümü bildirildiyse) o
    gönderime ait olmalı. ignored_busy başka sürümün çalışmasını anlatabilir, sürümü karşılaştırılmaz."""
    if pc not in manager.pending_updates:
        return False
    if progress["stage"] == "ignored_busy" or not progress["to_version"]:
        return True
    return same_version(progress["to_version"], pending_version(pc))


async def set_stage(pc: str, progress: dict) -> bool:
    """Son adımı saklar; aynı adım (aynı ayrıntıyla) yeniden geldiyse hiçbir şey yazılmaz ve zamanı değişmez.
    Dönen: saklandı mı."""
    keys = ("stage", "detail", "attempt", "of")
    current = manager.update_stages.get(pc)
    if current and all(current.get(k) == progress.get(k) for k in keys):
        return False
    entry = {k: progress.get(k) for k in keys}
    entry["stage_at"] = time.time()
    manager.update_stages[pc] = entry
    await execute_query(
        "UPDATE pending_updates SET stage = $2, detail = $3, attempt = $4, attempt_of = $5, "
        "stage_at = to_timestamp($6) WHERE pc_name = $1",
        (pc, entry["stage"], entry["detail"], entry["attempt"], entry["of"], entry["stage_at"]),
    )
    return True


async def recently_sent(pcs: Iterable[str], version: str, seconds: int = RESEND_AFTER_SECONDS) -> Set[str]:
    """Bu sürümün son `seconds` içinde gönderildiği ya da ajanın o sürede adım bildirdiği cihazlar (yeniden
    gönderilmez)."""
    pcs = list(pcs)
    if not pcs:
        return set()
    rows = await execute_query(
        "SELECT pc_name, version FROM pending_updates WHERE pc_name = ANY($1::text[]) "
        "AND GREATEST(sent_at, COALESCE(stage_at, sent_at)) > NOW() - make_interval(secs => $2)",
        (pcs, float(seconds)),
        fetch=True,
    )
    return {r["pc_name"] for r in rows or [] if same_version(r["version"], version)}


async def mark_sent(pc: str, version: str) -> None:
    manager.pending_updates[pc] = (version, time.time())
    manager.update_stages.pop(pc, None)
    await execute_query(
        "INSERT INTO pending_updates (pc_name, version, sent_at) VALUES ($1, $2, NOW()) "
        "ON CONFLICT (pc_name) DO UPDATE SET version = $2, sent_at = NOW(), "
        "stage = NULL, detail = NULL, attempt = NULL, attempt_of = NULL, stage_at = NULL",
        (pc, version),
    )


async def forget(pc: str) -> None:
    manager.pending_updates.pop(pc, None)
    manager.update_stages.pop(pc, None)
    await execute_query("DELETE FROM pending_updates WHERE pc_name = $1", (pc,))


async def load() -> int:
    """Açılışta: yeniden başlatmadan önce gönderilmiş ve sonucu beklenen güncellemeler (son adımlarıyla) belleğe
    alınır."""
    rows = await execute_query(
        "SELECT pc_name, version, extract(epoch FROM sent_at) AS sent, stage, detail, attempt, attempt_of, "
        "extract(epoch FROM stage_at) AS stage_at FROM pending_updates",
        fetch=True,
    )
    for r in rows or []:
        manager.pending_updates[r["pc_name"]] = (r["version"], float(r["sent"]))
        if r["stage"]:
            manager.update_stages[r["pc_name"]] = {
                "stage": r["stage"],
                "detail": r["detail"],
                "attempt": r["attempt"],
                "of": r["attempt_of"],
                "stage_at": float(r["stage_at"]) if r["stage_at"] is not None else None,
            }
    return len(rows or [])


async def seen(pc: str, result_id: str) -> bool:
    rows = await execute_query(
        "SELECT 1 FROM update_results WHERE pc_name = $1 AND result_id = $2", (pc, result_id), fetch=True
    )
    return bool(rows)


async def remember(pc: str, result_id: str) -> None:
    await execute_query(
        "INSERT INTO update_results (pc_name, result_id) VALUES ($1, $2) ON CONFLICT DO NOTHING", (pc, result_id)
    )
