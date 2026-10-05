"""WebSocket bağlantı yöneticisi (ajan/panel/vision) ve süreli uzaktan-kontrol oturumu yetkileri."""

import asyncio
import collections
import json
import logging
import time
from typing import Dict, List, Optional

from fastapi import WebSocket

from pops import metrics

log = logging.getLogger("pops.manager")

_VISION_SESSION_TTL = 1800  # denetim oturumu yetkisi: son etkinlikten 30 dk sonra kendiliğinden düşer (fail-closed)

# Panel yayını: her panelin kendi gönderim sırası ve tek yazıcı görevi var. Eskiden yayın panellere sırayla
# yazılıyordu; ağı yavaş tek bir panel (okul dışından, zayıf bağlantı) hem diğer panelleri hem de yayını yapan ajan
# işleyicisini bekletiyordu. Şimdi:
#   * ekran kareleri ve önizlemeler cihaz başına yalnızca EN SON hâliyle bekler (yetişemeyen panel eski kareyi atar);
#   * diğer mesajlar sırayla gider; sıra _PANEL_QUEUE_MAX'ı aşarsa ya da bir yazma _PANEL_SEND_TIMEOUT'tan uzun
#     sürerse panel kapatılır (tarayıcı yeniden bağlanıp güncel durumu yeniden yükler).
_PANEL_QUEUE_MAX = 500
_PANEL_SEND_TIMEOUT = 10.0
_FRAME_TYPES = ("stream_frame", "thumbnail")


class _PanelSender:
    def __init__(self, websocket: WebSocket, on_dead):
        self.ws = websocket
        self.queue = collections.deque()
        self.frames = collections.OrderedDict()  # (tür, cihaz) -> en son kare
        self.wake = asyncio.Event()
        self.dropped_frames = 0
        self._on_dead = on_dead
        self.task = asyncio.create_task(self._run())

    def put(self, text: str) -> bool:
        if len(self.queue) >= _PANEL_QUEUE_MAX:
            return False
        self.queue.append(text)
        self.wake.set()
        return True

    def put_frame(self, key: tuple, text: str) -> None:
        if key in self.frames:
            self.dropped_frames += 1
            del self.frames[key]
        self.frames[key] = text
        self.wake.set()

    async def _run(self):
        try:
            while True:
                await self.wake.wait()
                self.wake.clear()
                while self.queue or self.frames:
                    text = self.queue.popleft() if self.queue else self.frames.popitem(last=False)[1]
                    await asyncio.wait_for(self.ws.send_text(text), _PANEL_SEND_TIMEOUT)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            log.info("panel bağlantısı yavaş ya da kopuk, kapatılıyor", extra={"error": type(exc).__name__})
            self._on_dead(self.ws)


class ConnectionManager:
    def __init__(self):
        self.active_agents: Dict[str, WebSocket] = {}
        self.active_panels: List[WebSocket] = []
        self.panel_users: Dict[WebSocket, str] = {}  # panel soketi -> giriş yapan kullanıcı adı
        self.panel_roles: Dict[WebSocket, str] = {}  # panel soketi -> rol (ekran görüntüsü yalnız admin'e)
        # pc_name -> {kullanıcı: bitiş_zamanı}; süreli (fail-closed)
        self.vision_sessions: Dict[str, Dict[str, float]] = {}
        self.pending_thumbnails: Dict[str, List[asyncio.Future]] = {}
        self.active_vision_ws: Dict[str, WebSocket] = {}
        # pc_name -> (sürüm, gönderim zamanı): güncelleme gönderildi, sonucu bekleniyor. Sonuç gelmeden
        # uzun süre geçerse zamanlayıcı "ajan geri dönmedi" bildirimi üretir (ölü ajan sonuç gönderemez).
        self.pending_updates: Dict[str, tuple] = {}
        # pc_name -> bekleyen güncellemenin ajanın bildirdiği son adımı (stage, detail, attempt, of, stage_at);
        # yalnızca pending_updates'te olan cihaz için tutulur (bkz. pops/update_tracking.py)
        self.update_stages: Dict[str, dict] = {}
        self.panel_senders: Dict[WebSocket, _PanelSender] = {}

    async def connect_agent(self, websocket: WebSocket, pc_name: str):
        self.active_agents[pc_name] = websocket

    async def connect_panel(self, websocket: WebSocket, username: Optional[str] = None, role: Optional[str] = None):
        await websocket.accept()
        self.active_panels.append(websocket)
        self.panel_senders[websocket] = _PanelSender(websocket, self._drop_slow_panel)
        if username:
            self.panel_users[websocket] = username
        if role:
            self.panel_roles[websocket] = role

    # ── Uzaktan kontrol/izleme oturumu (F1/F12): girdi ve canlı kare/önizleme, yalnızca o cihaz için
    # AÇIK bir denetim oturumu olan admin'e verilir. Oturum start/end_audit_session ile yönetilir; ayrıca
    # süreli — son etkinlikten _VISION_SESSION_TTL sonra kendiliğinden düşer (end çağrılmasa da fail-closed).
    def add_vision_session(self, pc_name: str, username: str):
        self.vision_sessions.setdefault(pc_name, {})[username] = time.time() + _VISION_SESSION_TTL

    def touch_vision_session(self, pc_name: str, username: str):
        s = self.vision_sessions.get(pc_name)
        if s and username in s:
            s[username] = time.time() + _VISION_SESSION_TTL

    def remove_vision_session(self, pc_name: str, username: str):
        s = self.vision_sessions.get(pc_name)
        if s:
            s.pop(username, None)
            if not s:
                self.vision_sessions.pop(pc_name, None)

    def _live_session_users(self, pc_name: str) -> set:
        s = self.vision_sessions.get(pc_name)
        if not s:
            return set()
        now = time.time()
        live = {u for u, exp in s.items() if exp > now}
        expired = set(s) - live
        for u in expired:  # süresi dolanları tembel temizle
            s.pop(u, None)
        if not s:
            self.vision_sessions.pop(pc_name, None)
        return live

    def drop_user_sessions(self, username: Optional[str]):
        """Kullanıcının bütün cihazlardaki görüntü/kontrol yetkileri (oturumu iptal edildi ya da rolü düştü)."""
        for pc_name in list(self.vision_sessions):
            self.remove_vision_session(pc_name, username)

    def user_has_session(self, username: Optional[str], pc_name: str) -> bool:
        return bool(username) and username in self._live_session_users(pc_name)

    async def connect_vision(self, websocket: WebSocket, pc_name: str):
        await websocket.accept()
        self.active_vision_ws[pc_name] = websocket

    def disconnect_agent(self, pc_name: str, websocket: Optional[WebSocket] = None) -> bool:
        """Ajan soketini kayıttan düşürür. websocket verilirse yalnızca kayıtlı soket o ise
        silinir; böylece geç kapanan eski bir bağlantı, yeniden bağlanan ajanın yeni soketini
        silmez. Kayıt gerçekten silindiyse True döner."""
        if websocket is not None and self.active_agents.get(pc_name) is not websocket:
            return False
        removed = self.active_agents.pop(pc_name, None) is not None
        self.active_vision_ws.pop(pc_name, None)
        return removed

    def disconnect_panel(self, websocket: WebSocket):
        if websocket in self.active_panels:
            self.active_panels.remove(websocket)
        self.panel_users.pop(websocket, None)
        self.panel_roles.pop(websocket, None)
        sender = self.panel_senders.pop(websocket, None)
        if sender is not None and sender.task is not asyncio.current_task():
            sender.task.cancel()

    def _drop_slow_panel(self, websocket: WebSocket):
        self.disconnect_panel(websocket)
        # Panel işleyicisinin receive döngüsü kapanışla biter; tarayıcı yeniden bağlanır
        task = asyncio.ensure_future(websocket.close(code=1013))
        task.add_done_callback(lambda t: t.exception() if not t.cancelled() else None)

    def _queue_to_panel(self, panel: WebSocket, text: str, frame_key: Optional[tuple] = None):
        sender = self.panel_senders.get(panel)
        if sender is None:
            return
        if frame_key is not None:
            sender.put_frame(frame_key, text)
        elif not sender.put(text):
            log.info("panel gönderim sırası doldu, panel kapatılıyor")
            self._drop_slow_panel(panel)

    def disconnect_vision(self, pc_name: str, websocket: Optional[WebSocket] = None):
        """websocket verilirse yalnızca kayıtlı tünel o ise silinir (bkz. disconnect_agent)."""
        if websocket is not None and self.active_vision_ws.get(pc_name) is not websocket:
            return
        self.active_vision_ws.pop(pc_name, None)

    def rename_agent(self, old_name: str, new_name: str):
        if old_name in self.active_agents:
            self.active_agents[new_name] = self.active_agents.pop(old_name)
        if old_name in self.active_vision_ws:
            self.active_vision_ws[new_name] = self.active_vision_ws.pop(old_name)

    async def send_command(self, message: dict, pc_name: str) -> bool:
        """Mesaj sokete yazıldıysa True. Hata olursa YALNIZCA bu soket kayıttan düşer: gönderim beklerken aynı
        cihazın yeni bağlantısı kaydedilmiş olabilir, o silinmemeli."""
        ws = self.active_agents.get(pc_name)
        if ws is None:
            return False
        try:
            started = time.perf_counter()
            await ws.send_text(json.dumps(message))
            metrics.observe_send(time.perf_counter() - started)
            return True
        except Exception:
            self.disconnect_agent(pc_name, ws)
            return False

    async def broadcast_to_panels(self, message: dict):
        text = json.dumps(message)
        for panel in list(self.active_panels):
            self._queue_to_panel(panel, text)

    async def broadcast_to_admin_panels(self, message: dict):
        """Yalnızca admin/superadmin rollü panellere gönderir. Ekran görüntüsü/thumbnail gibi hassas
        içerik salt-okur 'viewer' hesaplarına SIZMAMALI (F1). get_thumbnail yanıtı /ws/agent'tan gelir
        ve bu yolla tüm panellere yayınlanıyordu — artık viewer'a gitmez."""
        text = json.dumps(message)
        frame_key = (message.get("type"), message.get("hw_id")) if message.get("type") in _FRAME_TYPES else None
        for panel in list(self.active_panels):
            if self.panel_roles.get(panel) in ("admin", "superadmin"):
                self._queue_to_panel(panel, text, frame_key)

    async def send_frame_to_viewers(self, message: dict, pc_name: str):
        """Canlı ekran karesi/önizlemesi YALNIZCA o cihaz için açık (süresi dolmamış) denetim oturumu
        olan admin panellerine gider (F12: tüm panellere yayınlama sızıntısı kapandı). Oturumu olan
        panel yoksa kare düşer."""
        allowed = self._live_session_users(pc_name)
        if not allowed:
            return
        text = json.dumps(message)
        frame_key = (message.get("type"), pc_name)
        for panel in list(self.active_panels):
            # Rol, panelin periyodik yeniden doğrulamasıyla güncel tutulur (bkz. control.websocket_panel)
            if self.panel_users.get(panel) in allowed and self.panel_roles.get(panel) in ("admin", "superadmin"):
                self._queue_to_panel(panel, text, frame_key)

    async def send_remote_input_to_vision(self, message: dict, pc_name: str):
        ws = self.active_vision_ws.get(pc_name)
        if ws is None:
            return False
        try:
            await ws.send_text(json.dumps(message))
            return True
        except Exception:
            self.disconnect_vision(pc_name, ws)
            return False


manager = ConnectionManager()
