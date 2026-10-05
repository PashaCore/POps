"""device_audit_logs hash zinciri: kayıt özeti ve zincirin baştan doğrulanması.

Bağımlılıksızdır (yapılandırma ya da veritabanı modülü import etmez): panel ucu (/api/system/audit-verify),
kayıt ekleme (pops/audit.py) ve komut satırı doğrulaması (audit_verify.py, yedekten dönüş sonrası) aynı kodu
kullanır.

Zaman özete eski metin biçiminde ('YYYY-AA-GG SS:DD:ss') girer: 0031'e kadar sütun bu metni tutuyordu. Sütun artık
TIMESTAMPTZ; metin saklanan değerden denetim saat diliminde (timeutil.audit_zone(), veritabanında sabit) üretilir.
Böylece migration'dan önce ve sonra yazılan kayıtlar aynı kuralla doğrulanır.
"""

import asyncio
import datetime
import hashlib

from pops import timeutil

_COLUMNS = "id, hw_id, action, reason, changes, timestamp, prev_hash, entry_hash"
# Zincir id sırasıyla parça parça okunur (B6): tablo büyüse de bellekte en çok BATCH_SIZE satır tutulur
SELECT_BATCH = "SELECT %s FROM device_audit_logs WHERE id > $1 ORDER BY id ASC LIMIT $2" % _COLUMNS
BATCH_SIZE = 5000


def ts_text(ts, tz=None):
    """Özete giren zaman metni: zaman damgası denetim diliminde 'YYYY-AA-GG SS:DD:ss'; metin olduğu gibi."""
    if isinstance(ts, datetime.datetime):
        return timeutil.local_text(ts, tz or timeutil.audit_zone())
    return ts


def entry_hash(prev, hw_id, action, reason, changes_json, ts, tz=None):
    raw = "%s|%s|%s|%s|%s|%s" % (prev or "", hw_id, action, reason, changes_json, ts_text(ts, tz))
    return hashlib.sha256(raw.encode("utf-8")).hexdigest()


class _Walker:
    def __init__(self, tz=None):
        self.tz = tz
        self.prev = None
        self.checked = 0
        self.total = 0
        self.broken = None

    def feed(self, rows) -> bool:
        """Sıradaki satırları zincire ekler; kırık bulunursa False."""
        for r in rows:
            self.total += 1
            if r["entry_hash"] is None:
                continue  # 0004 öncesi eski satırlar zincire dahil değil
            expected = entry_hash(
                self.prev, r["hw_id"], r["action"], r["reason"], r["changes"], r["timestamp"], self.tz
            )
            if expected != r["entry_hash"]:
                self.broken = r["id"]
                return False
            self.prev = r["entry_hash"]
            self.checked += 1
        return True

    def result(self):
        if self.broken is not None:
            return {
                "ok": False,
                "first_broken_id": self.broken,
                "reason": "zincir kırık (kurcalanmış/silinmiş)",
                "checked": self.checked,
            }
        return {"ok": True, "checked": self.checked, "total": self.total}


def verify(rows, tz=None):
    """Satırlar id sırasıyla verilir. Kurcalanmış ya da silinmiş bir kayıt varsa ilk kırık id döner. tz: denetim
    saat dilimi (verilmezse timeutil.audit_zone())."""
    w = _Walker(tz)
    w.feed(rows or [])
    return w.result()


async def verify_batched(fetch, batch_size: int = BATCH_SIZE, tz=None):
    """verify ile aynı sonuç, ama satırlar SELECT_BATCH ile parça parça okunur ve özetler iş parçacığında
    hesaplanır (olay döngüsü tıkanmaz). fetch(sql, son_id, limit) satırları döndüren bir eşzamansız çağrıdır."""
    w = _Walker(tz)
    last_id = 0
    while True:
        rows = await fetch(SELECT_BATCH, last_id, batch_size)
        if not rows:
            break
        if not await asyncio.to_thread(w.feed, rows):
            break
        if len(rows) < batch_size:
            break
        last_id = rows[-1]["id"]
    return w.result()
