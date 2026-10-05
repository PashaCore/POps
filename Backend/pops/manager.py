"""WebSocket bağlantı yöneticisi (ajan/panel/vision) ve süreli uzaktan-kontrol oturumu yetkileri.

Birden fazla backend süreci çalışıyorsa (REDIS_URL, bkz. pops/cluster.py ve docs/ha.md) bu sürecin soketleri burada,
diğer süreçlere gidenler Redis kanallarından geçer: başka süreçteki ajana komut, bütün panellere yayın, oturum sahibine
ekran karesi ve Vision iletileri. Buradaki durumun hangisinin süreçte kaldığı, hangisinin paylaşıldığı docs/ha.md'de
("Process-local state"). REDIS_URL yoksa her şey eskisi gibi bu süreçtedir.
"""

import asyncio
import base64
import collections
import json
import logging
import time
from typing import Dict, Iterable, List, Optional

from fastapi import WebSocket

from pops import metrics, vision
from pops.cluster import AGENT_CH, CH_EVENTS, USER_CH, VISION_CH, agent_channel, cluster, user_channel, vision_channel

log = logging.getLogger("pops.manager")

_VISION_SESSION_TTL = 1800  # denetim oturumu yetkisi: son etkinlikten 30 dk sonra kendiliğinden düşer (fail-closed)
# Birden fazla süreçte: oturum süresini uzatan etkinlik (fare/klavye) diğer süreçlere en çok bu sıklıkta bildirilir
_VISION_TOUCH_SHARE = 60

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
# Konu süzgeci: /ws/panel?topics=devices ile açılan soket YALNIZCA bu konudaki mesajları alır (panelin cihaz listesi
# soketi; görev çıktıları ve ekran görüntüleri gitmez). Konusuz soket eskisi gibi her şeyi alır; yalnızca isteğe bağlı
# türler (devices_changed) ona gitmez: mevcut istemciler beklemedikleri yeni bir mesaj görmesin.
PANEL_TOPICS = {"devices": ("devices_changed",)}
_OPT_IN_TYPES = frozenset(t for types in PANEL_TOPICS.values() for t in types)


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
        # pc_name -> {kullanıcı: bitiş_zamanı}; süreli (fail-closed). Birden fazla süreçte bütün süreçlerin yetkileri
        # (Redis'teki asıl kaydın bellekteki kopyası)
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
        # panel soketi -> alacağı mesaj türleri (?topics=); kaydı olmayan soket isteğe bağlılar dışında hepsini alır
        self.panel_topics: Dict[WebSocket, frozenset] = {}
        # panel soketi -> kapsamdaki laboratuvarlar (None: kapsamsız, her şey). Cihaza ait yayın yalnızca cihazın
        # laboratuvarı kapsamda olan panellere gider (bkz. pops/tenancy.py); cihazın laboratuvarı lab_resolver'la
        # (pc_name -> lab) bulunur, yalnızca kapsamlı bir panel bağlıyken sorulur. Birden fazla süreçte süzgeç
        # paneli tutan süreçte uygulanır (yayın cihazıyla birlikte gelir).
        self.panel_scopes: Dict[WebSocket, Optional[frozenset]] = {}
        self.lab_resolver = None
        # Birden fazla süreçte: başka süreçteki Vision tünelleri (cihaz -> {"w": süreç, "owner", "monitors"})
        self.remote_tunnels: Dict[str, dict] = {}
        self._tasks = set()   # Redis'ten gelen teslimatlar (GC toplamasın)
        self._chains: Dict[tuple, asyncio.Task] = {}   # (tür, cihaz) -> son teslimat: aynı sokete sırayla yazılır

    async def connect_agent(self, websocket: WebSocket, pc_name: str):
        await self.register_agent(pc_name, websocket)

    async def register_agent(self, pc_name: str, websocket: WebSocket):
        """Ajan soketi kaydedilir; bundan sonra komut alır. Birden fazla süreçte cihazın kanalına abone olunur ve
        çevrimiçi kaydı bu süreci gösterir."""
        self.active_agents[pc_name] = websocket
        if cluster.enabled():
            await cluster.claim_agent(pc_name)

    async def connect_panel(
        self, websocket: WebSocket, username: Optional[str] = None, role: Optional[str] = None, topics=None
    ):
        await websocket.accept()
        self.active_panels.append(websocket)
        if topics:
            self.panel_topics[websocket] = frozenset(t for topic in topics for t in PANEL_TOPICS.get(topic, ()))
        self.panel_senders[websocket] = _PanelSender(websocket, self._drop_slow_panel)
        if username:
            self.panel_users[websocket] = username
            if cluster.enabled():
                await cluster.subscribe(user_channel(username), wait=False)
        if role:
            self.panel_roles[websocket] = role

    # ── Uzaktan kontrol/izleme oturumu (F1/F12): girdi ve canlı kare/önizleme, yalnızca o cihaz için
    # AÇIK bir denetim oturumu olan admin'e verilir. Oturum start/end_audit_session ile yönetilir; ayrıca
    # süreli — son etkinlikten _VISION_SESSION_TTL sonra kendiliğinden düşer (end çağrılmasa da fail-closed).
    # Birden fazla süreçte değişiklik sırayla Redis'e yazılır ve diğer süreçlere bildirilir (pops/cluster.py).
    def add_vision_session(self, pc_name: str, username: str, mandatory: bool = False):
        expires, opened = time.time() + _VISION_SESSION_TTL, time.time()
        self.vision_sessions.setdefault(pc_name, {})[username] = expires
        self.vision_session_modes[(pc_name, username)] = (opened, bool(mandatory))
        if cluster.enabled():
            cluster.vision_change("set", pc_name, username, {"e": expires, "o": opened, "m": bool(mandatory)})

    def touch_vision_session(self, pc_name: str, username: str):
        s = self.vision_sessions.get(pc_name)
        if s and username in s:
            before, s[username] = s[username], time.time() + _VISION_SESSION_TTL
            if cluster.enabled() and s[username] - before >= _VISION_TOUCH_SHARE:
                mode = self.vision_session_modes.get((pc_name, username)) or (time.time(), False)
                cluster.vision_change("set", pc_name, username, {"e": s[username], "o": mode[0], "m": mode[1]})

    def _remove_local_session(self, pc_name: str, username: Optional[str]):
        s = self.vision_sessions.get(pc_name)
        self.vision_session_modes.pop((pc_name, username), None)
        if self.vision_clipboard_owner.get(pc_name) == username:
            self.vision_clipboard_owner.pop(pc_name, None)
        if s:
            s.pop(username, None)
            if not s:
                self.vision_sessions.pop(pc_name, None)

    def remove_vision_session(self, pc_name: str, username: str):
        self._remove_local_session(pc_name, username)
        if cluster.enabled():
            cluster.vision_change("del", pc_name, username)

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
            self._remove_local_session(pc_name, username)
        if cluster.enabled():
            cluster.vision_change("drop_user", None, username)

    def drop_device_sessions(self, pc_name: str) -> bool:
        """Cihazın bütün oturum yetkileri (ör. Vision modülü kapandı). Yetki vardıysa True."""
        had = bool(self.vision_sessions.pop(pc_name, None))
        if cluster.enabled():
            cluster.vision_change("drop_pc", pc_name, None)
        return had

    def replace_vision_sessions(self, sessions: Dict[str, Dict[str, float]], modes: Dict[tuple, tuple]):
        """Birden fazla süreçte: Redis'ten okunan bütün yetkiler (pops/cluster.py vision_resync)."""
        self.vision_sessions.clear()
        self.vision_sessions.update(sessions)
        self.vision_session_modes.clear()
        self.vision_session_modes.update(modes)

    def user_has_session(self, username: Optional[str], pc_name: str) -> bool:
        return bool(username) and username in self._live_session_users(pc_name)

    def vision_tunnel_open(self, pc_name: str) -> bool:
        """Cihazın Vision tüneli açık mı (birden fazla süreçte herhangi bir süreçte)."""
        return pc_name in self.active_vision_ws or pc_name in self.remote_tunnels

    def monitors_of(self, pc_name: str) -> Optional[list]:
        """Tünelin bildirdiği son monitör listesi (tünel başka süreçteyse oranın bildirdiği)."""
        if pc_name in self.vision_monitors:
            return self.vision_monitors[pc_name]
        return (self.remote_tunnels.get(pc_name) or {}).get("monitors")

    def set_monitors(self, pc_name: str, monitors: list):
        self.vision_monitors[pc_name] = monitors
        if cluster.enabled():
            self._share_tunnel(pc_name)

    def clipboard_allowed(self, username: Optional[str], pc_name: str) -> bool:
        """Pano: açık tünel + o tüneli rızasıyla açan "kullanıcıya sor" oturumunun sahibi. Tünel açılırken cihazda
        birden çok oturum vardıysa hangisinin kabul edildiği bilinemez: pano kimseye açılmaz (fail-closed)."""
        if pc_name in self.active_vision_ws:
            owner = self.vision_clipboard_owner.get(pc_name)
        elif pc_name in self.remote_tunnels:
            owner = self.remote_tunnels[pc_name].get("owner")
        else:
            return False
        if owner != username:
            return False
        mode = self.vision_session_modes.get((pc_name, username))
        return self.user_has_session(username, pc_name) and bool(mode) and not mode[1]

    def clipboard_users(self, pc_name: str) -> set:
        return {u for u in self._live_session_users(pc_name) if self.clipboard_allowed(u, pc_name)}

    def vision_tunnel_opened(self, pc_name: str, websocket: WebSocket):
        """Ajan tüneli yalnızca bir oturumun rızası (ya da zorunlu oturumun süresi) dolunca açar. O anda cihazda tek
        oturum varsa tüneli o açmıştır; "kullanıcıya sor" türündeyse pano onun sahibine açılır. Yeniden bağlanan
        tünel aynı kuralla yeniden değerlendirilir. Birden fazla süreçte tünelin kanalına abone olunur, başka
        süreçteki eski tünel kaydı bırakılır ve tünelin bilgisi (pano sahibi, monitörler) paylaşılır."""
        self.active_vision_ws[pc_name] = websocket
        self.vision_monitors.pop(pc_name, None)
        live = self._live_session_users(pc_name)
        owner = next(iter(live)) if len(live) == 1 else None
        mode = self.vision_session_modes.get((pc_name, owner)) if owner else None
        if mode and not mode[1]:
            self.vision_clipboard_owner[pc_name] = owner
        else:
            self.vision_clipboard_owner.pop(pc_name, None)
        if cluster.enabled():
            self.remote_tunnels.pop(pc_name, None)
            cluster.want(vision_channel(pc_name))
            cluster.defer(cluster.publish, vision_channel(pc_name), {"k": "evict"})
            self._share_tunnel(pc_name)

    def _share_tunnel(self, pc_name: str):
        cluster.tunnel_change(pc_name, {"owner": self.vision_clipboard_owner.get(pc_name),
                                        "monitors": self.vision_monitors.get(pc_name)})

    async def connect_vision(self, websocket: WebSocket, pc_name: str):
        await websocket.accept()
        self.vision_tunnel_opened(pc_name, websocket)

    def disconnect_agent(self, pc_name: str, websocket: Optional[WebSocket] = None) -> bool:
        """Ajan soketini kayıttan düşürür. websocket verilirse yalnızca kayıtlı soket o ise
        silinir; böylece geç kapanan eski bir bağlantı, yeniden bağlanan ajanın yeni soketini
        silmez. Kayıt gerçekten silindiyse True döner."""
        if websocket is not None and self.active_agents.get(pc_name) is not websocket:
            return False
        removed = self.active_agents.pop(pc_name, None) is not None
        self._forget_vision(pc_name)
        return removed

    async def release_agent(self, pc_name: str, websocket: WebSocket) -> bool:
        """Bağlantı kapandı: kayıt bu soketse düşürülür. True ise cihaz gerçekten koptu (Offline yazılır). Birden fazla
        süreçte cihaz bu arada başka sürece bağlandıysa False (bu eski soketin kapanması kopuş değildir)."""
        removed = self.disconnect_agent(pc_name, websocket)
        if removed and cluster.enabled():
            return await cluster.release_agent(pc_name)
        return removed

    async def kick_agent(self, pc_name: str, code: int, reason: str):
        """Ajan bağlantısı kayıttan düşer ve kapatılır (ör. cihaz silindi); soket başka süreçteyse o süreç kapatır."""
        ws = self.active_agents.get(pc_name)
        self.disconnect_agent(pc_name)
        if cluster.enabled():
            if ws is None:
                await cluster.publish(agent_channel(pc_name), {"k": "close", "code": code, "reason": reason})
            await cluster.release_agent(pc_name)
        if ws is not None:
            try:
                await ws.close(code=code, reason=reason)
            except Exception:
                pass

    async def is_online(self, pc_name: str) -> bool:
        """Ajanın soketi açık mı (birden fazla süreçte herhangi bir süreçte)."""
        if pc_name in self.active_agents:
            return True
        if cluster.enabled():
            holders = await cluster.holders([pc_name])
            return bool(holders and pc_name in holders)
        return False

    async def online_among(self, pcs: Iterable[str]) -> set:
        """Verilen cihazlardan soketi açık olanlar."""
        pcs = set(pcs)
        online = {p for p in pcs if p in self.active_agents}
        if cluster.enabled() and pcs - online:
            holders = await cluster.holders(sorted(pcs - online))
            online |= set(holders or {})
        return online

    async def online_agents(self) -> list:
        """Soketi açık bütün ajanlar (birden fazla süreçte bütün süreçlerinki; Redis yoksa yalnızca bu sürecinkiler)."""
        local = list(self.active_agents.keys())
        if not cluster.enabled():
            return local
        remote = await cluster.online_agents()
        return local if remote is None else sorted(set(local) | set(remote))

    async def connection_counts(self) -> tuple:
        """(bağlı ajan, bağlı panel): birden fazla süreçte bütün süreçlerin toplamı."""
        if cluster.enabled():
            workers = await cluster.workers()
            if workers:
                others = [w for w in workers if not w["self"]]
                return (len(self.active_agents) + sum(w["agents"] for w in others),
                        len(self.active_panels) + sum(w["panels"] for w in others))
        return len(self.active_agents), len(self.active_panels)

    def disconnect_panel(self, websocket: WebSocket):
        if websocket in self.active_panels:
            self.active_panels.remove(websocket)
        username = self.panel_users.pop(websocket, None)
        self.panel_roles.pop(websocket, None)
        self.panel_binary.discard(websocket)
        self.panel_topics.pop(websocket, None)
        self.panel_scopes.pop(websocket, None)
        sender = self.panel_senders.pop(websocket, None)
        if sender is not None and sender.task is not asyncio.current_task():
            sender.task.cancel()
        if username and cluster.enabled() and username not in self.panel_users.values():
            cluster.unsubscribe(user_channel(username))

    def _drop_slow_panel(self, websocket: WebSocket):
        self.disconnect_panel(websocket)
        # Panel işleyicisinin receive döngüsü kapanışla biter; tarayıcı yeniden bağlanır
        task = asyncio.ensure_future(websocket.close(code=1013))
        task.add_done_callback(lambda t: t.exception() if not t.cancelled() else None)

    def _wants(self, panel: WebSocket, message_type) -> bool:
        topics = self.panel_topics.get(panel)
        return message_type not in _OPT_IN_TYPES if topics is None else message_type in topics

    def _queue_to_panel(self, panel: WebSocket, text: str, frame_key: Optional[tuple] = None, mtype=None):
        sender = self.panel_senders.get(panel)
        if sender is None or not self._wants(panel, mtype):
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
        had = self.active_vision_ws.pop(pc_name, None) is not None
        self.vision_clipboard_owner.pop(pc_name, None)
        self.vision_monitors.pop(pc_name, None)
        if had and cluster.enabled():
            cluster.unsubscribe(vision_channel(pc_name))
            cluster.tunnel_change(pc_name, None)

    def rename_agent(self, old_name: str, new_name: str):
        if old_name in self.active_agents:
            self.active_agents[new_name] = self.active_agents.pop(old_name)
        for table in (self.active_vision_ws, self.vision_clipboard_owner, self.vision_monitors):
            if old_name in table:
                table[new_name] = table.pop(old_name)

    async def send_command(self, message: dict, pc_name: str) -> bool:
        """Mesaj sokete yazıldıysa True. Hata olursa YALNIZCA bu soket kayıttan düşer: gönderim beklerken aynı
        cihazın yeni bağlantısı kaydedilmiş olabilir, o silinmemeli. Birden fazla süreçte soket başka süreçteyse
        mesaj cihazın kanalına yayılır; o süreç aldıysa True."""
        ws = self.active_agents.get(pc_name)
        if ws is None:
            if cluster.enabled():
                return bool(await cluster.publish(agent_channel(pc_name), {"k": "cmd", "m": message}))
            return False
        return await self._send_to_agent(ws, message, pc_name)

    async def _send_to_agent(self, ws: WebSocket, message: dict, pc_name: str) -> bool:
        try:
            started = time.perf_counter()
            await ws.send_text(json.dumps(message))
            metrics.observe_send(time.perf_counter() - started)
            return True
        except Exception:
            self.disconnect_agent(pc_name, ws)
            return False

    def has_scoped_panels(self) -> bool:
        return any(self.panel_scopes.get(p) is not None for p in self.active_panels)

    async def _in_scope(self, device: Optional[str]):
        """Panel -> bu cihazın yayınını alabilir mi. Kapsamlı panel yoksa sorgu yapılmaz; cihazı belirsiz yayın
        kapsamlı panele gitmez."""
        if not self.has_scoped_panels():
            return lambda _panel: True
        lab = None
        if device and self.lab_resolver is not None:
            try:
                lab = await self.lab_resolver(device)
            except Exception:
                log.warning("yayın için cihazın laboratuvarı okunamadı", exc_info=True)

        def allowed(panel) -> bool:
            labs = self.panel_scopes.get(panel)
            return labs is None or (lab is not None and lab in labs)

        return allowed

    async def _broadcast_local(self, text: str, mtype=None, device: Optional[str] = None):
        allowed = await self._in_scope(device)
        for panel in list(self.active_panels):
            if allowed(panel):
                self._queue_to_panel(panel, text, mtype=mtype)

    async def _broadcast_local_admins(self, text: str, frame_key: Optional[tuple], mtype=None,
                                      device: Optional[str] = None):
        allowed = await self._in_scope(device)
        for panel in list(self.active_panels):
            if self.panel_roles.get(panel) in _ADMIN_ROLES and allowed(panel):
                self._queue_to_panel(panel, text, frame_key, mtype)

    async def broadcast_to_panels(self, message: dict, device: Optional[str] = None):
        """device: yayının ait olduğu cihaz (kapsamlı paneller yalnızca kendi cihazlarınınkini alır; cihazsız yayın
        kapsamlı panellere gitmez). Birden fazla süreçte yayın cihazıyla birlikte diğer süreçlere gider; kapsam
        süzgecini paneli tutan süreç uygular."""
        text = json.dumps(message)
        await self._broadcast_local(text, message.get("type"), device)
        if cluster.enabled():
            await cluster.publish(CH_EVENTS, {"k": "panels", "t": text, "y": message.get("type"), "d": device})

    async def broadcast_devices_changed(self, message: dict, labs) -> None:
        """Cihaz listesi değişti (pops/devicelist.py): kapsamsız panellere sürümle; kapsamlı panele yalnızca kendi
        sınıflarından biri değiştiyse ve sürümsüz (sürüm bütün kurumundur, kapsamlı panelin sürümü başkadır). Yalnızca
        bu sürecin panellerine: her süreç değişikliği kendi döngüsünde bildirir (bkz. pops/devicelist.py)."""
        text = json.dumps(message)
        bare = json.dumps({"type": message.get("type")})
        labs = set(labs or ())
        for panel in list(self.active_panels):
            scope = self.panel_scopes.get(panel)
            if scope is None:
                self._queue_to_panel(panel, text, mtype=message.get("type"))
            elif labs & scope:
                self._queue_to_panel(panel, bare, mtype=message.get("type"))

    async def broadcast_to_admin_panels(self, message: dict, device: Optional[str] = None):
        """Yalnızca admin/superadmin rollü panellere gönderir. Ekran görüntüsü/thumbnail gibi hassas
        içerik salt-okur 'viewer' hesaplarına SIZMAMALI (F1). get_thumbnail yanıtı /ws/agent'tan gelir
        ve bu yolla tüm panellere yayınlanıyordu — artık viewer'a gitmez."""
        text = json.dumps(message)
        frame_key = (message.get("type"), message.get("hw_id")) if message.get("type") in _FRAME_TYPES else None
        device = device or message.get("hw_id")
        await self._broadcast_local_admins(text, frame_key, message.get("type"), device)
        if cluster.enabled():
            await cluster.publish(CH_EVENTS, {"k": "admins", "t": text, "y": message.get("type"), "d": device,
                                              "f": list(frame_key) if frame_key else None})

    def _session_panels(self, pc_name: str, users: Optional[set] = None) -> List[WebSocket]:
        """O cihaz için açık oturumu olan (users verilirse yalnız onlardan) admin kullanıcıların panelleri. Rol,
        panelin periyodik yeniden doğrulamasıyla güncel tutulur (bkz. control.websocket_panel). Kurum birimi
        kapsamı: oturum yalnızca kapsamdaki cihaza açılır (start_audit_session) ve kapsam değişince ya da cihaz
        kapsam dışına çıkınca panelin yeniden doğrulaması oturumu düşürür; kareler bu yüzden ayrıca sorgulanmaz."""
        allowed = self._live_session_users(pc_name)
        if users is not None:
            allowed &= set(users)
        if not allowed:
            return []
        return [p for p in self.active_panels
                if self.panel_users.get(p) in allowed and self.panel_roles.get(p) in _ADMIN_ROLES]

    async def _to_holder_channels(self, pc_name: str, users: Optional[set], payload: dict) -> int:
        """Birden fazla süreçte: oturum sahiplerinin kanallarına (paneli başka süreçte olanlar için). Dönen: iletiyi
        alan süreç sayısı."""
        allowed = self._live_session_users(pc_name)
        if users is not None:
            allowed &= set(users)
        received = 0
        for username in allowed:
            received += await cluster.publish(user_channel(username), dict(payload, pc=pc_name)) or 0
        return received

    async def send_frame_to_viewers(self, message: dict, pc_name: str):
        """Canlı ekran karesi/önizlemesi YALNIZCA o cihaz için açık (süresi dolmamış) denetim oturumu
        olan admin panellerine gider (F12: tüm panellere yayınlama sızıntısı kapandı). Oturumu olan
        panel yoksa kare düşer. Birden fazla süreçte kare yalnızca oturum sahiplerinin kanalına yayılır (paneli başka
        süreçteyse o süreç alır); hiçbir zaman bütün süreçlere yayılmaz."""
        panels = self._session_panels(pc_name)
        text = json.dumps(message)
        frame_key = (message.get("type"), pc_name)
        for panel in panels:
            self._queue_to_panel(panel, text, frame_key, message.get("type"))
        if cluster.enabled():
            await self._to_holder_channels(pc_name, None, {"k": "frame", "t": text, "f": list(frame_key),
                                                           "y": message.get("type")})

    def has_session_panels(self, pc_name: str, users: Optional[set] = None) -> bool:
        """Oturum sahiplerinin paneli var mı. Birden fazla süreçte paneller başka süreçte olabilir: oturum sahibi varsa
        True (ileti yalnız onların kanallarına gider)."""
        if cluster.enabled():
            allowed = self._live_session_users(pc_name)
            return bool(allowed & set(users) if users is not None else allowed)
        return bool(self._session_panels(pc_name, users))

    async def send_to_session_holders(self, message: dict, pc_name: str, users: Optional[set] = None) -> int:
        """Kare dışındaki Vision mesajları (monitors, clipboard) da yalnızca oturum sahiplerinin panellerine,
        sırayla gider. Mesaj kaç panele kondu, onu döner (birden fazla süreçte iletiyi alan diğer süreçler de
        sayılır)."""
        panels = self._session_panels(pc_name, users)
        text = json.dumps(message)
        for panel in panels:
            self._queue_to_panel(panel, text)
        if cluster.enabled():
            return len(panels) + await self._to_holder_channels(
                pc_name, users, {"k": "holder", "t": text, "y": message.get("type")})
        return len(panels)

    def _binary_to_local(self, pc_name: str, kind: int, monitor: int, message: bytes, users: Optional[set] = None):
        panels = [p for p in self._session_panels(pc_name, users) if p in self.panel_binary]
        key = (pc_name, "cursor" if kind == vision.KIND_CURSOR else monitor)
        for panel in panels:
            sender = self.panel_senders.get(panel)
            if sender is not None and sender.put_binary(key, kind, message):
                self._queue_to_panel(panel, json.dumps({"type": "vision_resync", "hw_id": pc_name,
                                                        "monitor": monitor}))
        return len(panels)

    async def send_binary_frame_to_viewers(self, pc_name: str, frame: vision.Frame, data: bytes) -> int:
        """İkili kare (Vision v2): F12 kuralı aynı, ayrıca yalnız ikili kare alabileceğini bildiren panellere.
        Öneki sunucu yazar (tünelin doğrulanmış kimliği); ajanın mesajı olduğu gibi arkasına eklenir. Birden fazla
        süreçte kare oturum sahiplerinin kanallarına da gider (base64; Redis kanalı metin taşır)."""
        prefix = vision.panel_prefix(pc_name)
        if prefix is None:
            return 0
        n = 0
        panels = [p for p in self._session_panels(pc_name) if p in self.panel_binary]
        if panels:
            n = self._binary_to_local(pc_name, frame.kind, frame.monitor, prefix + data)
        if cluster.enabled():
            n += await self._to_holder_channels(pc_name, None, {
                "k": "bin", "kind": frame.kind, "mon": frame.monitor, "b": base64.b64encode(data).decode("ascii")})
        return n

    async def send_remote_input_to_vision(self, message: dict, pc_name: str):
        ws = self.active_vision_ws.get(pc_name)
        if ws is None:
            if cluster.enabled():
                return bool(await cluster.publish(vision_channel(pc_name), {"k": "input", "m": message}))
            return False
        return await self._send_to_vision(ws, message, pc_name)

    async def _send_to_vision(self, ws: WebSocket, message: dict, pc_name: str) -> bool:
        try:
            await ws.send_text(json.dumps(message))
            return True
        except Exception:
            self.disconnect_vision(pc_name, ws)
            return False

    # ── Başka süreçten gelenler (pops/cluster.py; yalnızca REDIS_URL tanımlıyken)

    def on_cluster_message(self, channel: str, data: dict):
        kind = data.get("k")
        if channel == CH_EVENTS:
            if kind == "panels":
                # Kapsam süzgeci cihazın laboratuvarını sorabilir: yayınlar geliş sırasıyla, ayrı görevde
                self._in_order(("panels",), self._broadcast_local, data["t"], data.get("y"), data.get("d"))
            elif kind == "admins":
                self._in_order(("panels",), self._remote_admin_broadcast, data)
            elif kind == "vision":
                self._apply_vision_change(data)
            elif kind == "tunnel":
                self._apply_tunnel_change(data)
        elif channel.startswith(AGENT_CH):
            pc_name = channel[len(AGENT_CH):]
            ws = self.active_agents.get(pc_name)
            if kind == "cmd" and ws is not None:
                # Yalnızca bu süreçteki sokete; soket bu arada kapandıysa komut düşer (yeniden yayılmaz)
                self._in_order(("agent", pc_name), self._send_to_agent, ws, data.get("m") or {}, pc_name)
            elif kind == "evict":
                self._spawn(self._evict_agent(pc_name))
            elif kind == "close":
                self._spawn(self._close_local_agent(pc_name, int(data.get("code") or 1000), data.get("reason") or ""))
        elif channel.startswith(VISION_CH):
            pc_name = channel[len(VISION_CH):]
            ws = self.active_vision_ws.get(pc_name)
            if kind == "input" and ws is not None:
                self._in_order(("vision", pc_name), self._send_to_vision, ws, data.get("m") or {}, pc_name)
            elif kind == "evict" and ws is not None:
                # Tünel başka bir sürece taşındı: buradaki kayıt düşer (eski soket tek süreçteki gibi kapatılmaz)
                self.active_vision_ws.pop(pc_name, None)
                self.vision_clipboard_owner.pop(pc_name, None)
                self.vision_monitors.pop(pc_name, None)
                cluster.unsubscribe(vision_channel(pc_name))
        elif channel.startswith(USER_CH):
            self._remote_for_user(channel[len(USER_CH):], kind, data)

    def _remote_for_user(self, username: str, kind, data: dict):
        """Oturum sahibinin kanalından: kare, ikili kare ya da Vision iletisi. Gönderen süreç oturumu denetledi;
        burada da yetki ve admin rolü yeniden denetlenir (fail-closed)."""
        pc_name = data.get("pc")
        if not isinstance(pc_name, str) or not self.user_has_session(username, pc_name):
            return
        if kind == "frame":
            frame_key = tuple(data.get("f") or ())
            if len(frame_key) == 2:
                for panel in self._session_panels(pc_name, {username}):
                    self._queue_to_panel(panel, data["t"], frame_key, data.get("y"))
        elif kind == "holder":
            for panel in self._session_panels(pc_name, {username}):
                self._queue_to_panel(panel, data["t"])
        elif kind == "bin":
            prefix = vision.panel_prefix(pc_name)
            if prefix is not None:
                raw = base64.b64decode(data.get("b") or "")
                self._binary_to_local(pc_name, int(data.get("kind") or 0), int(data.get("mon") or 0), prefix + raw,
                                      {username})

    def _spawn(self, coro):
        task = asyncio.ensure_future(coro)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    def _in_order(self, key: tuple, send, *args):
        """Aynı sokete giden teslimatlar geliş sırasıyla yazılır (ör. tuşa basma ve bırakma); bir soketin yavaşlığı
        diğerlerini bekletmez."""
        previous = self._chains.get(key)

        async def run():
            if previous is not None:
                await asyncio.wait([previous])
            await send(*args)

        task = asyncio.ensure_future(run())
        self._chains[key] = task
        self._tasks.add(task)

        def done(t):
            self._tasks.discard(t)
            if self._chains.get(key) is t:
                del self._chains[key]
        task.add_done_callback(done)

    async def _remote_admin_broadcast(self, data: dict):
        frame_key = tuple(data["f"]) if data.get("f") else None
        # Başka süreçteki ajandan gelen önizleme: bu süreçte bekleyen get_thumbnail istekleri de yanıtlanır
        if frame_key and frame_key[0] == "thumbnail" and self.pending_thumbnails.get(frame_key[1]):
            image = json.loads(data["t"]).get("image", "")
            for fut in self.pending_thumbnails[frame_key[1]]:
                if not fut.done():
                    fut.set_result(image)
            self.pending_thumbnails[frame_key[1]] = []
        await self._broadcast_local_admins(data["t"], frame_key, data.get("y"), data.get("d"))

    def _apply_vision_change(self, data: dict):
        op, pc_name, username, rec = data.get("op"), data.get("pc"), data.get("u"), data.get("r") or {}
        if op == "set":
            self.vision_sessions.setdefault(pc_name, {})[username] = float(rec.get("e") or 0)
            self.vision_session_modes[(pc_name, username)] = (float(rec.get("o") or 0), bool(rec.get("m")))
        elif op == "del":
            self._remove_local_session(pc_name, username)
        elif op == "drop_user":
            for pc in list(self.vision_sessions):
                self._remove_local_session(pc, username)
        elif op == "drop_pc":
            self.vision_sessions.pop(pc_name, None)

    def _apply_tunnel_change(self, data: dict):
        pc_name, info = data.get("pc"), data.get("info")
        if not isinstance(pc_name, str):
            return
        if info is None:
            if (self.remote_tunnels.get(pc_name) or {}).get("w") == data.get("o"):
                self.remote_tunnels.pop(pc_name, None)
        elif pc_name not in self.active_vision_ws:
            self.remote_tunnels[pc_name] = dict(info, w=data.get("o"))

    def replace_tunnels(self, tunnels: Dict[str, dict]):
        """Birden fazla süreçte: Redis'ten okunan başka süreçlerdeki tüneller (pops/cluster.py vision_resync)."""
        self.remote_tunnels = {pc: t for pc, t in tunnels.items() if pc not in self.active_vision_ws}

    async def _evict_agent(self, pc_name: str):
        """Cihaz başka bir sürece bağlandı: buradaki kayıt düşer (eski soket kapatılmaz; kapanınca cihazı Offline
        yazmaz, tek süreçteki yeniden bağlanma gibi). Cihaz bu arada yeniden buraya bağlandıysa (kayıt yine bu süreci
        gösteriyor) bildirim eskidir, yok sayılır."""
        if pc_name not in self.active_agents or await cluster.holds_agent(pc_name):
            return
        if self.active_agents.pop(pc_name, None) is not None:
            log.info("ajan başka bir backend sürecine bağlandı", extra={"pc_name": pc_name})
        cluster.unsubscribe(agent_channel(pc_name))

    async def _close_local_agent(self, pc_name: str, code: int, reason: str):
        ws = self.active_agents.get(pc_name)
        if ws is not None:
            await self.kick_agent(pc_name, code, reason)


manager = ConnectionManager()
