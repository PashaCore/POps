"""Görev sonuçları, sunucu veritabanına yazdığını onaylayana kadar diskte (Windows ResultSpool ile aynı sözleşme).

  * Sunucu server_info features'ta "result_ack" duyurur ve her result'ı yazınca {"action":"result_ack","task_id":N}
    gönderir; aynı sonucun ikinci kez gelmesi sunucuda zararsızdır.
  * Onaylı sunucuda her sonuç /var/lib/pops-agent/pending-results.json'a yazılır (0600, atomik), result_ack ile
    silinir; yeniden bağlanınca onaysızlar yeniden gönderilir. Ajan yeniden başlayınca dosya okunur.
  * En çok 20 sonuç: sınır dolunca en eskisi atılır ve loglanır (her sonucun çıktısı zaten sınırlı).
"""

import logging
import threading
from typing import List, Optional, Tuple

from pops_agent import store

log = logging.getLogger("pops.spool")

ACK_FEATURE = "result_ack"
MAX_RESULTS = 20


class ResultSpool:
    def __init__(self, path: Optional[str]):
        self.path = path
        self._lock = threading.Lock()
        self._entries: List[Tuple[int, dict]] = self._load()
        self._sent = set()

    def _load(self) -> List[Tuple[int, dict]]:
        if not self.path:
            return []
        try:
            raw = store.read_json(self.path)
        except (ValueError, OSError) as exc:
            log.error("%s okunamadı (%s); onay bekleyen sonuçlar yok sayıldı.", self.path, exc)
            return []
        entries = []
        for item in raw or []:
            if isinstance(item, dict) and isinstance(item.get("task_id"), int) and isinstance(item.get("result"), dict):
                entries.append((item["task_id"], item["result"]))
        if entries:
            log.info("Önceki çalışmadan onay bekleyen %d görev sonucu bulundu; sunucuya yeniden gönderilecek.",
                     len(entries))
        return entries[-MAX_RESULTS:]

    def _save(self) -> None:
        if not self.path:
            return
        try:
            if not self._entries:
                store.delete(self.path)
            else:
                store.write_json(self.path, [{"task_id": t, "result": r} for t, r in self._entries])
        except OSError as exc:
            log.error("%s yazılamadı; onay bekleyen sonuçlar yalnızca bellekte: %s", self.path, exc)

    @property
    def count(self) -> int:
        with self._lock:
            return len(self._entries)

    def task_ids(self) -> List[int]:
        with self._lock:
            return [t for t, _ in self._entries]

    def add(self, task_id: int, result: dict) -> None:
        with self._lock:
            self._entries = [(t, r) for t, r in self._entries if t != task_id]
            self._sent.discard(task_id)
            self._entries.append((task_id, dict(result)))
            while len(self._entries) > MAX_RESULTS:
                dropped, _ = self._entries.pop(0)
                self._sent.discard(dropped)
                log.warning("Onay bekleyen görev sonuçları sınırı (%d) aşıldı; en eskisi atıldı (görev %d).",
                            MAX_RESULTS, dropped)
            self._save()

    def remove(self, task_id: int) -> bool:
        with self._lock:
            self._sent.discard(task_id)
            before = len(self._entries)
            self._entries = [(t, r) for t, r in self._entries if t != task_id]
            if len(self._entries) == before:
                return False
            self._save()
            return True

    def unsent(self) -> List[Tuple[int, dict]]:
        with self._lock:
            return [(t, r) for t, r in self._entries if t not in self._sent]

    def all(self) -> List[Tuple[int, dict]]:
        with self._lock:
            return list(self._entries)

    def mark_sent(self, task_id: int) -> None:
        with self._lock:
            self._sent.add(task_id)

    def on_connected(self) -> None:
        with self._lock:
            self._sent.clear()

    def discard(self) -> int:
        """Dosya kenara alındı (kopyalanmış kurulum): bellekteki sonuçlar da bırakılır."""
        with self._lock:
            n = len(self._entries)
            self._entries = []
            self._sent.clear()
            return n
