"""device_audit_logs hash zinciri: kayıt özeti ve zincirin baştan doğrulanması.

Bağımlılıksızdır (yapılandırma ya da veritabanı modülü import etmez): panel ucu (/api/system/audit-verify),
kayıt ekleme (pops/audit.py) ve komut satırı doğrulaması (audit_verify.py, yedekten dönüş sonrası) aynı kodu
kullanır.
"""

import hashlib

SELECT_ROWS = (
    "SELECT id, hw_id, action, reason, changes, timestamp, prev_hash, entry_hash FROM device_audit_logs ORDER BY id ASC"
)


def entry_hash(prev, hw_id, action, reason, changes_json, ts):
    raw = "%s|%s|%s|%s|%s|%s" % (prev or "", hw_id, action, reason, changes_json, ts)
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()


def verify(rows):
    """Satırlar id sırasıyla verilir. Kurcalanmış ya da silinmiş bir kayıt varsa ilk kırık id döner."""
    prev = None
    checked = 0
    for r in rows or []:
        if r["entry_hash"] is None:
            continue  # 0004 öncesi eski satırlar zincire dahil değil
        expected = entry_hash(prev, r["hw_id"], r["action"], r["reason"], r["changes"], r["timestamp"])
        if expected != r["entry_hash"]:
            return {
                "ok": False,
                "first_broken_id": r["id"],
                "reason": "zincir kırık (kurcalanmış/silinmiş)",
                "checked": checked,
            }
        prev = r["entry_hash"]
        checked += 1
    return {"ok": True, "checked": checked, "total": len(rows or [])}
