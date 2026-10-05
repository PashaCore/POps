"""WebSocket bağlantı yöneticisi (ajan/panel/vision) ve süreli uzaktan-kontrol oturumu yetkileri."""

import asyncio
import collections
import json
import logging
import time
from typing import Dict, List, Optional

from fastapi import WebSocket

from pops import metrics, vision

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
# İkili (Vision v2) kareler parça parça gelir: bölge kareleri ancak tam karenin üzerine sırayla çizilirse doğru
# görüntü verir, bu yüzden "yalnız en son kare" burada işlemez. Panel başına, cihaz+monitör başına:
#   * tam kare bekleyenlerin hepsinin yerini alır (eskileri düşer, görüntü yine doğru);
#   * imleç konumu yalnız en son hâliyle bekler;
#   * bölge kareleri en çok _BIN_KEY_MAX_FRAMES adet / _BIN_KEY_MAX_BYTES bayt bekler. Taşarsa bölge düşer ve o
#     monitörün bölgeleri bir sonraki tam kareye kadar atılır; panele vision_resync gider, görüntüleyici tam kare
#     ister (select_monitor). Yavaş panel kareleri biriktirmez, düşürür.
_BIN_KEY_MAX_FRAMES = 32
_BIN_KEY_MAX_BYTES = 4 * 1024 * 1024
# Bir panelde bekleyen bütün ikili kareler (monitör baytını ajan seçer: 17 çıktı x 4 MB olmasın); aşan panel kapatılır
_BIN_PANEL_MAX_BYTES = 16 * 1024 * 1024
_ADMIN_ROLES = ("admin", "superadmin")


class _PanelSender:
    def __init__(self, websocket: WebSocket, on_dead):
        self.ws = websocket
        self.queue = collections.deque()
        self.frames = collections.OrderedDict()  # (tür, cihaz) -> en son kare
        self.bins = collections.OrderedDict()  # (cihaz, monitör | "cursor") -> bekleyen ikili kareler
        self.bin_bytes: Dict[tuple, int] = {}
        self.stale: set = set()  # bölgesi düşen (cihaz, monitör): tam kare gelene kadar bölgeler atılır
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

    def put_binary(self, key: tuple, kind: int, data: bytes) -> bool:
        """İkili kareyi sıraya koyar (kurallar yukarıda). Bir bölge düşüp monitör yeni bayatladıysa True döner:
        çağıran panele vision_resync gönderir."""
        pending = self.bins.get(key)
        if kind == vision.KIND_REGION:
            if key in self.stale:
                self._drop(1)
                return False
            if pending is None:
                pending = self.bins[key] = collections.deque()
            if len(pending) >= _BIN_KEY_MAX_FRAMES or self.bin_bytes.get(key, 0) + len(data) > _BIN_KEY_MAX_BYTES:
                self._drop(1)
                self.stale.add(key)
                if not pending:
                    del self.bins[key]
                return True
            pending.append(data)
            self.bin_bytes[key] = self.bin_bytes.get(key, 0) + len(data)
        else:
            # Tam kare (ya da imleç konumu) bekleyen eskilerin hepsini geçersiz kılar
            if pending:
                self._drop(len(pending))
            self.bins[key] = collections.deque((data,))
            self.bin_bytes[key] = len(data)
            if kind == vision.KIND_FULL:
                self.stale.discard(key)
        if sum(self.bin_bytes.values()) > _BIN_PANEL_MAX_BYTES:
            log.info("panelde bekleyen ikili kareler sınırı aştı, panel kapatılıyor")
            self._on_dead(self.ws)
            return False
        self.wake.set()
        return False

    def _drop(self, n: int) -> None:
        self.dropped_frames += n
        metrics.count("vision_frames_dropped_slow", n)

    def _next_binary(self) -> bytes:
        key, pending = next(iter(self.bins.items()))
        data = pending.popleft()
        if pending:
            self.bin_bytes[key] -= len(data)
            self.bins.move_to_end(key)  # monitörler sırayla: biri ötekini bekletmesin
        else:
            del self.bins[key]
            self.bin_bytes.pop(key, None)
        return data

    async def _run(self):
        try:
            while True:
                await self.wake.wait()
                self.wake.clear()
                while self.queue or self.frames or self.bins:
                    if self.queue or self.frames:
                        text = self.queue.popleft() if self.queue else self.frames.popitem(last=False)[1]
                        await asyncio.wait_for(self.ws.send_text(text), _PANEL_SEND_TIMEOUT)
                    else:
                        await asyncio.wait_for(self.ws.send_bytes(self._next_binary()), _PANEL_SEND_TIMEOUT)
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
        # Vision v2: ikili kare alabileceğini bildiren paneller (panel_hello), cihazın son monitör listesi, oturumların
        # türü ve panonun sahibi. Pano yalnızca kullanıcının onayladığı oturumda çalışır: tünel açıldığında cihazda
        # açık TEK oturum vardıysa ve o oturum "kullanıcıya sor" türündeyse, tüneli o oturumun rızası açmıştır; pano
        # yalnızca o oturumun sahibine açılır (bkz. vision_tunnel_opened).
        self.panel_binary: set = set()
        self.vision_monitors: Dict[str, list] = {}
        self.vision_session_modes: Dict[tuple, tuple] = {}  # (pc_name, kullanıcı) -> (açılış anı, zorunlu mu)
        self.vision_clipboard_owner: Dict[str, str] = {}  # pc_name -> rızası tüneli açan oturumun sahibi

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
    def add_vision_session(self, pc_name: str, username: str, mandatory: bool = False):
        self.vision_sessions.setdefault(pc_name, {})[username] = time.time() + _VISION_SESSION_TTL
        self.vision_session_modes[(pc_name, username)] = (time.time(), bool(mandatory))

    def touch_vision_session(self, pc_name: str, username: str):
        s = self.vision_sessions.get(pc_name)
        if s and username in s:
            s[username] = time.time() + _VISION_SESSION_TTL

    def remove_vision_session(self, pc_name: str, username: str):
        s = self.vision_sessions.get(pc_name)
        self.vision_session_modes.pop((pc_name, username), None)
        if self.vision_clipboard_owner.get(pc_name) == username:
            self.vision_clipboard_owner.pop(pc_name, None)
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
            self.vision_session_modes.pop((pc_name, u), None)
            if self.vision_clipboard_owner.get(pc_name) == u:
                self.vision_clipboard_owner.pop(pc_name, None)
        if not s:
            self.vision_sessions.pop(pc_name, None)
        return live

    def drop_user_sessions(self, username: Optional[str]):
        """Kullanıcının bütün cihazlardaki görüntü/kontrol yetkileri (oturumu iptal edildi ya da rolü düştü)."""
        for pc_name in list(self.vision_sessions):
            self.remove_vision_session(pc_name, username)

    def user_has_session(self, username: Optional[str], pc_name: str) -> bool:
        return bool(username) and username in self._live_session_users(pc_name)

    def clipboard_allowed(self, username: Optional[str], pc_name: str) -> bool:
        """Pano: açık tünel + o tüneli rızasıyla açan "kullanıcıya sor" oturumunun sahibi. Tünel açılırken cihazda
        birden çok oturum vardıysa hangisinin kabul edildiği bilinemez: pano kimseye açılmaz (fail-closed)."""
        if pc_name not in self.active_vision_ws or self.vision_clipboard_owner.get(pc_name) != username:
            return False
        mode = self.vision_session_modes.get((pc_name, username))
        return self.user_has_session(username, pc_name) and bool(mode) and not mode[1]

    def clipboard_users(self, pc_name: str) -> set:
        return {u for u in self._live_session_users(pc_name) if self.clipboard_allowed(u, pc_name)}

    def vision_tunnel_opened(self, pc_name: str, websocket: WebSocket):
        """Ajan tüneli yalnızca bir oturumun rızası (ya da zorunlu oturumun süresi) dolunca açar. O anda cihazda tek
        oturum varsa tüneli o açmıştır; "kullanıcıya sor" türündeyse pano onun sahibine açılır. Yeniden bağlanan
        tünel aynı kuralla yeniden değerlendirilir."""
        self.active_vision_ws[pc_name] = websocket
        self.vision_monitors.pop(pc_name, None)
        live = self._live_session_users(pc_name)
        owner = next(iter(live)) if len(live) == 1 else None
        mode = self.vision_session_modes.get((pc_name, owner)) if owner else None
        if mode and not mode[1]:
            self.vision_clipboard_owner[pc_name] = owner
        else:
            self.vision_clipboard_owner.pop(pc_name, None)

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
        self._forget_vision(pc_name)
        return removed

    def disconnect_panel(self, websocket: WebSocket):
        if websocket in self.active_panels:
            self.active_panels.remove(websocket)
        self.panel_users.pop(websocket, None)
        self.panel_roles.pop(websocket, None)
        self.panel_binary.discard(websocket)
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

    def send_to_panel(self, panel: WebSocket, message: dict):
        """Tek bir panele (o panelin sırasıyla) mesaj."""
        self._queue_to_panel(panel, json.dumps(message))

    def disconnect_vision(self, pc_name: str, websocket: Optional[WebSocket] = None):
        """websocket verilirse yalnızca kayıtlı tünel o ise silinir (bkz. disconnect_agent)."""
        if websocket is not None and self.active_vision_ws.get(pc_name) is not websocket:
            return
        self._forget_vision(pc_name)

    def _forget_vision(self, pc_name: str):
        self.active_vision_ws.pop(pc_name, None)
        self.vision_clipboard_owner.pop(pc_name, None)
        self.vision_monitors.pop(pc_name, None)

    def rename_agent(self, old_name: str, new_name: str):
        if old_name in self.active_agents:
            self.active_agents[new_name] = self.active_agents.pop(old_name)
        for table in (self.active_vision_ws, self.vision_clipboard_owner, self.vision_monitors):
            if old_name in table:
                table[new_name] = table.pop(old_name)

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

    def _session_panels(self, pc_name: str, users: Optional[set] = None) -> List[WebSocket]:
        """O cihaz için açık oturumu olan (users verilirse yalnız onlardan) admin kullanıcıların panelleri. Rol,
        panelin periyodik yeniden doğrulamasıyla güncel tutulur (bkz. control.websocket_panel)."""
        allowed = self._live_session_users(pc_name)
        if users is not None:
            allowed &= set(users)
        if not allowed:
            return []
        return [p for p in self.active_panels
                if self.panel_users.get(p) in allowed and self.panel_roles.get(p) in _ADMIN_ROLES]

    async def send_frame_to_viewers(self, message: dict, pc_name: str):
        """Canlı ekran karesi/önizlemesi YALNIZCA o cihaz için açık (süresi dolmamış) denetim oturumu
        olan admin panellerine gider (F12: tüm panellere yayınlama sızıntısı kapandı). Oturumu olan
        panel yoksa kare düşer."""
        panels = self._session_panels(pc_name)
        if not panels:
            return
        text = json.dumps(message)
        frame_key = (message.get("type"), pc_name)
        for panel in panels:
            self._queue_to_panel(panel, text, frame_key)

    def has_session_panels(self, pc_name: str, users: Optional[set] = None) -> bool:
        return bool(self._session_panels(pc_name, users))

    async def send_to_session_holders(self, message: dict, pc_name: str, users: Optional[set] = None) -> int:
        """Kare dışındaki Vision mesajları (monitors, clipboard) da yalnızca oturum sahiplerinin panellerine,
        sırayla gider. Mesaj kaç panele kondu, onu döner."""
        panels = self._session_panels(pc_name, users)
        text = json.dumps(message)
        for panel in panels:
            self._queue_to_panel(panel, text)
        return len(panels)

    async def send_binary_frame_to_viewers(self, pc_name: str, frame: vision.Frame, data: bytes) -> int:
        """İkili kare (Vision v2): F12 kuralı aynı, ayrıca yalnız ikili kare alabileceğini bildiren panellere.
        Öneki sunucu yazar (tünelin doğrulanmış kimliği); ajanın mesajı olduğu gibi arkasına eklenir."""
        panels = [p for p in self._session_panels(pc_name) if p in self.panel_binary]
        prefix = vision.panel_prefix(pc_name) if panels else None
        if prefix is None:
            return 0
        message = prefix + data
        key = (pc_name, "cursor" if frame.kind == vision.KIND_CURSOR else frame.monitor)
        for panel in panels:
            sender = self.panel_senders.get(panel)
            if sender is not None and sender.put_binary(key, frame.kind, message):
                self._queue_to_panel(panel, json.dumps({"type": "vision_resync", "hw_id": pc_name,
                                                        "monitor": frame.monitor}))
        return len(panels)

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
