"""Birden fazla backend süreci (HA): Redis pub/sub ile yönlendirme ve süreçler arası paylaşılan durum.

REDIS_URL boşsa (varsayılan) bu modül hiçbir şey yapmaz: enabled() False döner ve sunucu tek süreç çalışır. Tanımlıysa
birden fazla uvicorn worker'ı ya da sunucu (yük dengeleyicinin arkasında) aynı veritabanı ve aynı Redis'le çalışır;
herhangi bir worker herhangi bir ajan ya da panel soketini tutabilir (yapışkan oturum gerekmez). Bkz. docs/ha.md.

Kanallar (her worker'ın tek abonelik bağlantısı var):
  <önek>:ch:events        bütün worker'lar: panel ve admin yayınları (önizleme), oturum yetkisi ve Vision tüneli
                          değişiklikleri, cihaz listesi değişiklikleri (pops/devicelist.py)
  <önek>:ch:agent:<cihaz> ajanın soketini tutan worker: ajana komut, "evict" (cihaz başka worker'a bağlandı), "close"
  <önek>:ch:vision:<cihaz> Vision tünelini tutan worker: uzaktan girdi ve görüntüleyici komutları (monitör, pano...)
  <önek>:ch:user:<kullanıcı> o kullanıcının paneli açık olan worker'lar: ekran kareleri (metin ve ikili) ve Vision
                          iletileri (monitörler, pano); yalnızca oturum sahibine
Paylaşılan durum:
  <önek>:agents            cihaz -> soketini tutan worker (çevrimiçi ajan kaydı)
  <önek>:worker:<id>       worker'ın canlılık anahtarı (WORKER_TTL sn; BEAT_SECONDS'ta bir yenilenir) ve sayaçları
  <önek>:workers           bilinen worker'lar; canlılık anahtarı düşen worker'ın ajanları temizlenir ve Offline yazılır
  <önek>:vision            (cihaz, kullanıcı) -> uzaktan oturum yetkisi (bitiş, açılış, zorunlu mu); her worker bellekte
                          kopyasını tutar
  <önek>:tunnels           cihaz -> Vision tünelini tutan worker, pano sahibi ve monitör listesi
  <önek>:devlist:ver       cihaz listesinin ortak sürüm sayacı (pops/devicelist.py)
  <önek>:notify:*          bildirim tekrar süzgeci ve dışarıya gönderim sınırı (pops/notify.py)

Redis'e ulaşılamazsa worker kendi soketleriyle çalışmaya devam eder: yerel ajan ve panellere gönderim sürer, başka
worker'a gidecek mesajlar düşer ve günlüğe yazılır, bağlantı arka planda yeniden kurulur. Yeniden bağlanınca yerel
ajanlar kayda yeniden yazılır ve kesinti sırasında değişen oturum yetkileri Redis'e işlenir.
"""

import asyncio
import collections
import hashlib
import json
import logging
import os
import secrets
import socket
import time
from typing import Callable, Dict, Iterable, List, Optional

from pops.config import REDIS_PREFIX, REDIS_URL

log = logging.getLogger("pops.cluster")

WORKER_TTL = 15            # worker canlılık anahtarının ömrü (sn)
BEAT_SECONDS = 5           # canlılık yenileme ve ölü worker temizliği aralığı
VISION_SYNC_SECONDS = 10   # oturum yetkilerinin Redis'ten yeniden okunması (kaçan bir bildirim en geç bu kadar sürer)
SUBSCRIBE_WAIT = 2.0       # abonelik onayı için en uzun bekleme
_POLL = 0.05               # abonelik bağlantısında okuma aralığı (abonelik değişiklikleri arada uygulanır)

WORKER_ID = "%s:%d:%s" % (socket.gethostname(), os.getpid(), secrets.token_hex(3))

_P = REDIS_PREFIX + ":"
K_AGENTS = _P + "agents"
K_WORKERS = _P + "workers"
K_VISION = _P + "vision"
K_TUNNELS = _P + "tunnels"
K_DEVLIST_VER = _P + "devlist:ver"
K_NOTIFY_SENT = _P + "notify:sent"
CH_EVENTS = _P + "ch:events"


AGENT_CH = _P + "ch:agent:"
VISION_CH = _P + "ch:vision:"
USER_CH = _P + "ch:user:"


def worker_key(worker_id: str) -> str:
    return _P + "worker:" + worker_id


def agent_channel(pc_name: str) -> str:
    return AGENT_CH + pc_name


def vision_channel(pc_name: str) -> str:
    return VISION_CH + pc_name


def user_channel(username: str) -> str:
    return USER_CH + username


# Kayıt, yalnızca hâlâ bu worker'ı gösteriyorsa silinir. 1 = silindi, 2 = kayıt yoktu, 0 = başka worker'ın
_RELEASE = """
local v = redis.call('HGET', KEYS[1], ARGV[1])
if not v then return 2 end
if v == ARGV[2] then redis.call('HDEL', KEYS[1], ARGV[1]) return 1 end
return 0
"""
# Ölü worker'ın bütün kayıtları (silinen cihazlar döner)
_REAP = """
local out = {}
local all = redis.call('HGETALL', KEYS[1])
for i = 1, #all, 2 do
  if all[i + 1] == ARGV[1] then redis.call('HDEL', KEYS[1], all[i]) table.insert(out, all[i]) end
end
return out
"""
# Tünel kaydı yalnızca hâlâ bu worker'ınsa silinir
_TUNNEL_DEL = """
local v = redis.call('HGET', KEYS[1], ARGV[1])
if v and cjson.decode(v)['w'] == ARGV[2] then redis.call('HDEL', KEYS[1], ARGV[1]) return 1 end
return 0
"""
# Ortak sürüm sayacı: her zaman çağıranın bildiği sürümden büyük
_NEXT_VERSION = """
local v = redis.call('INCR', KEYS[1])
if v <= tonumber(ARGV[1]) then v = tonumber(ARGV[1]) + 1 redis.call('SET', KEYS[1], v) end
return v
"""
# Kayan pencerede gönderim sınırı: 1 = gönderilebilir (sayıldı), 0 = sınırda
_RATE = """
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', tonumber(ARGV[1]) - tonumber(ARGV[2]))
if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[3]) then return 0 end
redis.call('ZADD', KEYS[1], ARGV[1], ARGV[4])
redis.call('EXPIRE', KEYS[1], ARGV[2])
return 1
"""


def _vfield(pc_name: str, username: str) -> str:
    return json.dumps([pc_name, username], ensure_ascii=False)


class Cluster:
    def __init__(self):
        self.redis = None
        self.manager = None
        self.healthy = False
        self.started_at = time.time()
        self._started = False
        self._tasks: List[asyncio.Task] = []
        self._wanted = {CH_EVENTS}
        self._subscribed = set()   # abonelik bağlantısında şu an abone olunan kanallar (dinleyici yazar)
        self._dirty = asyncio.Event()
        self._waiters: Dict[str, List[asyncio.Future]] = {}
        self._connected_once = False
        self._beaten = False
        self._down_logged_at = 0.0
        # Redis'e yazılamayan oturum yetkisi değişiklikleri; bağlantı geri gelince sırayla işlenir
        self._vision_journal: List[tuple] = []
        self._vision_gen = 0
        self._last_vision_sync = 0.0
        self._scripts = {}
        # Sırayla yapılacak Redis işleri (eşzamanlı çağrılardan: oturum yetkileri, tünel bilgisi, evict)
        self._outbox: collections.deque = collections.deque()
        self._outbox_wake = asyncio.Event()
        # Başka modüllerin iletileri (events kanalında "k" -> işleyici) ve yeniden bağlanınca çağrılacaklar
        self.handlers: Dict[str, Callable] = {}
        self.reconnect_hooks: List[Callable] = []

    # ── Yaşam döngüsü

    def enabled(self) -> bool:
        return self._started

    async def start(self, manager) -> None:
        """REDIS_URL tanımlıysa bağlanır ve arka plan görevlerini başlatır. Redis o an yoksa açılış durmaz: worker kendi
        soketleriyle çalışır, bağlantı arka planda kurulur."""
        if not REDIS_URL or self._started:
            return
        import redis.asyncio as aioredis
        from redis.asyncio.retry import Retry
        from redis.backoff import NoBackoff

        self.manager = manager
        self.redis = aioredis.from_url(
            REDIS_URL, decode_responses=True, socket_connect_timeout=1.0, socket_timeout=2.0,
            retry=Retry(NoBackoff(), 0), health_check_interval=0,
        )
        for name, body in (("release", _RELEASE), ("reap", _REAP), ("rate", _RATE), ("tunnel_del", _TUNNEL_DEL),
                           ("next_version", _NEXT_VERSION)):
            self._scripts[name] = self.redis.register_script(body)
        self._started = True
        self._tasks = [asyncio.create_task(self._listen()), asyncio.create_task(self._beat_loop()),
                       asyncio.create_task(self._outbox_loop())]
        for _ in range(int(SUBSCRIBE_WAIT / 0.05)):
            if self.healthy:
                break
            await asyncio.sleep(0.05)
        log.info("çoklu süreç (Redis) açık", extra={"worker": WORKER_ID, "redis_ok": self.healthy})

    async def stop(self) -> None:
        if not self._started:
            return
        for t in self._tasks:
            t.cancel()
        for t in self._tasks:
            try:
                await t
            except (asyncio.CancelledError, Exception):
                pass
        if self.healthy:
            try:
                local = list(self.manager.active_agents) if self.manager else []
                for pc in local:
                    await self._scripts["release"](keys=[K_AGENTS], args=[pc, WORKER_ID])
                await self.redis.delete(worker_key(WORKER_ID))
                await self.redis.srem(K_WORKERS, WORKER_ID)
            except Exception:
                log.warning("kapanışta Redis kaydı silinemedi", exc_info=True)
        try:
            await self.redis.aclose()
        except Exception:
            pass
        self._started = False
        self.healthy = False

    def _down(self, exc: Optional[BaseException], what: str) -> None:
        """Redis hatası: bağlantı kopmuş sayılır (dinleyici yeniden bağlanır); günlüğe en çok 30 sn'de bir yazılır."""
        if self.healthy:
            log.warning("Redis'e ulaşılamıyor; yalnız bu sürecin soketleri çalışıyor",
                        extra={"worker": WORKER_ID, "op": what, "error": repr(exc)[:200]})
            self._down_logged_at = time.monotonic()
        elif time.monotonic() - self._down_logged_at > 30:
            log.warning("Redis'e hâlâ ulaşılamıyor", extra={"worker": WORKER_ID, "op": what})
            self._down_logged_at = time.monotonic()
        self.healthy = False
        self._dirty.set()

    # ── Abonelikler

    def want(self, channel: str) -> bool:
        """Kanala abone olunacak (beklemeden); zaten aboneyse True."""
        self._wanted.add(channel)
        if channel in self._subscribed:
            return True
        self._dirty.set()
        return False

    async def subscribe(self, channel: str, wait: bool = True) -> bool:
        """Kanala abone olur; wait ise Redis onaylayana kadar (en çok SUBSCRIBE_WAIT) bekler. Onaylandıysa True."""
        if self.want(channel):
            return True
        if not wait or not self.healthy:
            return False
        fut = asyncio.get_running_loop().create_future()
        self._waiters.setdefault(channel, []).append(fut)
        try:
            return await asyncio.wait_for(fut, SUBSCRIBE_WAIT)
        except asyncio.TimeoutError:
            return False
        finally:
            waiters = self._waiters.get(channel)
            if waiters and fut in waiters:
                waiters.remove(fut)
                if not waiters:
                    self._waiters.pop(channel, None)

    def unsubscribe(self, channel: str) -> None:
        if channel in self._wanted and channel != CH_EVENTS:
            self._wanted.discard(channel)
            self._dirty.set()

    async def _sync_subscriptions(self, pubsub) -> None:
        self._dirty.clear()
        add = self._wanted - self._subscribed
        remove = self._subscribed - self._wanted
        # Kayıt komutlardan önce güncellenir: beklerken gelen abone ol / bırak istekleri bir sonraki turda uygulanır
        self._subscribed = (self._subscribed | add) - remove
        if add:
            await pubsub.subscribe(*add)
        if remove:
            await pubsub.unsubscribe(*remove)

    async def _listen(self) -> None:
        backoff = 0.5
        while True:
            pubsub = None
            try:
                if self._connected_once:
                    # Kesintiden kalan (karşı tarafı kapanmış) boştaki bağlantılar atılır; ilk komut hata vermesin
                    await self.redis.connection_pool.disconnect(inuse_connections=False)
                pubsub = self.redis.pubsub()
                self._subscribed = set()
                await self._sync_subscriptions(pubsub)
                reconnected = self._connected_once
                self._connected_once = True
                if not self.healthy:
                    if reconnected:
                        log.info("Redis bağlantısı geri geldi", extra={"worker": WORKER_ID})
                    self.healthy = True
                if reconnected:
                    await self._after_reconnect()
                else:
                    await self._beat()   # canlılık kaydı hemen: diğer worker'lar bu süreci açılışta görsün
                backoff = 0.5
                while True:
                    if self._dirty.is_set():
                        if not self.healthy:
                            raise ConnectionError("Redis işlemi başarısız oldu, abonelik yenileniyor")
                        await self._sync_subscriptions(pubsub)
                    msg = await pubsub.get_message(timeout=_POLL)
                    while msg is not None:
                        self._on_message(msg)
                        msg = await pubsub.get_message(timeout=0)
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                self._down(exc, "listen")
                self._subscribed = set()
                for waiters in self._waiters.values():
                    for fut in waiters:
                        if not fut.done():
                            fut.set_result(False)
            finally:
                if pubsub is not None:
                    try:
                        await pubsub.aclose()
                    except Exception:
                        pass
            await asyncio.sleep(backoff)
            backoff = min(backoff * 2, 5.0)

    def _on_message(self, msg: dict) -> None:
        kind = msg.get("type")
        channel = msg.get("channel")
        if kind == "subscribe":
            for fut in self._waiters.pop(channel, []):
                if not fut.done():
                    fut.set_result(True)
            return
        if kind != "message":
            return
        try:
            data = json.loads(msg.get("data") or "{}")
            if data.get("o") == WORKER_ID:
                return   # bu worker'ın kendi yayını: yerel teslim zaten yapıldı
            handler = self.handlers.get(data.get("k")) if channel == CH_EVENTS else None
            if handler is not None:
                handler(data)
            else:
                self.manager.on_cluster_message(channel, data)
        except Exception:
            log.warning("Redis mesajı işlenemedi", exc_info=True, extra={"channel": str(channel)[:80]})

    # ── Yayın

    async def publish(self, channel: str, payload: dict) -> Optional[int]:
        """Kanala yayın; dönen: mesajı alan abone sayısı (Redis yoksa None, mesaj düşer)."""
        if not self.healthy:
            return None
        payload["o"] = WORKER_ID
        text = json.dumps(payload, ensure_ascii=False, default=str)   # kodlama hatası Redis kesintisi sayılmasın
        try:
            return await self.redis.publish(channel, text)
        except Exception as exc:
            self._down(exc, "publish")
            return None

    # ── Sıralı arka plan işleri

    def defer(self, fn, *args) -> None:
        """fn(*args) arka planda, sırayla çalışır (eşzamanlı yerlerden çağrılabilir; sıra korunur)."""
        self._outbox.append((fn, args))
        self._outbox_wake.set()

    async def _outbox_loop(self) -> None:
        while True:
            await self._outbox_wake.wait()
            self._outbox_wake.clear()
            while self._outbox:
                fn, args = self._outbox.popleft()
                try:
                    await fn(*args)
                except asyncio.CancelledError:
                    raise
                except Exception as exc:
                    self._down(exc, getattr(fn, "__name__", "defer"))

    async def claim(self, name: str, seconds: int) -> Optional[bool]:
        """Adlı işi bu süre içinde yalnızca bir worker yapsın: ilk alan True; Redis yoksa None."""
        if not self.healthy:
            return None
        try:
            return bool(await self.redis.set(_P + "claim:" + name, WORKER_ID, nx=True, ex=seconds))
        except Exception as exc:
            self._down(exc, "claim")
            return None

    async def next_version(self, floor: int) -> Optional[int]:
        """Cihaz listesinin ortak sürüm sayacından yeni sürüm (floor'dan büyük); Redis yoksa None."""
        if not self.healthy:
            return None
        try:
            return int(await self._scripts["next_version"](keys=[K_DEVLIST_VER], args=[int(floor)]))
        except Exception as exc:
            self._down(exc, "next_version")
            return None

    # ── Çevrimiçi ajan kaydı

    async def claim_agent(self, pc_name: str) -> None:
        """Cihazın soketi artık bu worker'da: kanalına abone olunur, kayıt bu worker'ı gösterir ve önceki worker
        kaydı bırakır (evict; eski soketi tek süreçteki gibi kapatılmaz, kapanınca cihazı Offline yazmaz)."""
        await self.subscribe(agent_channel(pc_name))
        if not self.healthy:
            return
        try:
            async with self.redis.pipeline(transaction=True) as pipe:
                pipe.hget(K_AGENTS, pc_name)
                pipe.hset(K_AGENTS, pc_name, WORKER_ID)
                prev, _n = await pipe.execute()
        except Exception as exc:
            self._down(exc, "claim")
            return
        if prev and prev != WORKER_ID:
            await self.publish(agent_channel(pc_name), {"k": "evict"})

    async def holds_agent(self, pc_name: str) -> Optional[bool]:
        """Kayıt bu worker'ı mı gösteriyor (Redis yoksa None)."""
        if not self.healthy:
            return None
        try:
            return await self.redis.hget(K_AGENTS, pc_name) == WORKER_ID
        except Exception as exc:
            self._down(exc, "holds")
            return None

    async def release_agent(self, pc_name: str) -> bool:
        """Kayıt bu worker'ı gösteriyorsa silinir. False: cihaz bu arada başka worker'a bağlanmış (bu soketin kapanması
        cihazın kopması değildir)."""
        self.unsubscribe(agent_channel(pc_name))
        if not self.healthy:
            return True
        try:
            res = await self._scripts["release"](keys=[K_AGENTS], args=[pc_name, WORKER_ID])
        except Exception as exc:
            self._down(exc, "release")
            return True
        return int(res) != 0

    async def _live_workers(self) -> Optional[set]:
        members = await self.redis.smembers(K_WORKERS)
        members = list(members or [])
        if not members:
            return set()
        async with self.redis.pipeline(transaction=False) as pipe:
            for w in members:
                pipe.exists(worker_key(w))
            alive = await pipe.execute()
        return {w for w, a in zip(members, alive) if a} | {WORKER_ID}

    async def holders(self, pcs: Iterable[str]) -> Optional[Dict[str, str]]:
        """Cihaz -> soketini tutan (canlı) worker; Redis yoksa None."""
        pcs = list(pcs)
        if not self.healthy:
            return None
        try:
            if pcs:
                values = await self.redis.hmget(K_AGENTS, pcs)
            else:
                values = []
            live = await self._live_workers()
        except Exception as exc:
            self._down(exc, "holders")
            return None
        return {pc: w for pc, w in zip(pcs, values) if w and w in live}

    async def online_agents(self) -> Optional[List[str]]:
        """Bütün worker'lardaki çevrimiçi ajanlar; Redis yoksa None."""
        if not self.healthy:
            return None
        try:
            entries = await self.redis.hgetall(K_AGENTS)
            live = await self._live_workers()
        except Exception as exc:
            self._down(exc, "online_agents")
            return None
        return [pc for pc, w in (entries or {}).items() if w in live]

    async def workers(self) -> Optional[List[dict]]:
        """Canlı worker'lar ve sayaçları (tanı ekranı, metrikler); Redis yoksa None."""
        if not self.healthy:
            return None
        try:
            live = sorted(await self._live_workers())
            async with self.redis.pipeline(transaction=False) as pipe:
                for w in live:
                    pipe.hgetall(worker_key(w))
                infos = await pipe.execute()
        except Exception as exc:
            self._down(exc, "workers")
            return None
        out = []
        for w, info in zip(live, infos):
            info = info or {}
            out.append({"id": w, "agents": int(info.get("agents") or 0), "panels": int(info.get("panels") or 0),
                        "host": info.get("host"), "pid": int(info.get("pid") or 0) or None,
                        "started_at": float(info.get("started") or 0) or None, "self": w == WORKER_ID})
        return out

    # ── Canlılık ve ölü worker temizliği

    async def _beat_loop(self) -> None:
        while True:
            try:
                await self._beat()
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                self._down(exc, "beat")
            await asyncio.sleep(BEAT_SECONDS)

    async def _beat(self) -> None:
        if not self.healthy:
            return
        m = self.manager
        info = {"agents": len(m.active_agents), "panels": len(m.active_panels), "host": socket.gethostname(),
                "pid": os.getpid(), "started": self.started_at, "at": time.time()}
        async with self.redis.pipeline(transaction=True) as pipe:
            pipe.exists(worker_key(WORKER_ID))
            pipe.hset(worker_key(WORKER_ID), mapping=info)
            pipe.expire(worker_key(WORKER_ID), WORKER_TTL)
            pipe.sadd(K_WORKERS, WORKER_ID)
            existed = (await pipe.execute())[0]
        if not existed and self._beaten:
            # Canlılık anahtarı düşmüş (bu süreç bir süre yanıt veremedi): ajanlarını başka worker kayıttan silmiş
            # olabilir
            log.warning("bu sürecin canlılık kaydı düşmüştü, ajanları yeniden yazılıyor", extra={"worker": WORKER_ID})
            await self._reassert_agents()
        self._beaten = True
        await self._reap_dead_workers()
        if time.monotonic() - self._last_vision_sync >= VISION_SYNC_SECONDS:
            await self.vision_resync()

    async def _reap_dead_workers(self) -> None:
        members = list(await self.redis.smembers(K_WORKERS) or [])
        dead = []
        for w in members:
            if w != WORKER_ID and not await self.redis.exists(worker_key(w)):
                dead.append(w)
        for w in dead:
            # Aynı ölü worker'ı iki worker birden temizlemesin
            if not await self.redis.set(_P + "reap:" + w, WORKER_ID, nx=True, ex=60):
                continue
            pcs = list(await self._scripts["reap"](keys=[K_AGENTS], args=[w]) or [])
            await self.redis.srem(K_WORKERS, w)
            log.warning("yanıt vermeyen backend süreci kayıttan düşürüldü",
                        extra={"worker": w, "agents": len(pcs)})
            if pcs:
                await self._mark_offline(pcs, "backend süreci yanıt vermiyor (%s)" % w)

    @staticmethod
    async def _mark_offline(pcs: List[str], reason: str) -> None:
        from pops.db import execute_query  # db havuzu açılışta kurulur; döngü olmasın diye burada

        # Bu arada başka worker'a bağlanmış cihaz kısa süre Offline görünebilir; bir sonraki heartbeat düzeltir
        await execute_query(
            "UPDATE clients SET status = 'Offline', last_disconnect_at = NOW(), last_disconnect_reason = $2 "
            "WHERE pc_name = ANY($1::text[]) AND status IS DISTINCT FROM 'Offline'",
            (pcs, reason[:200]),
        )

    async def _reassert_agents(self) -> None:
        """Kayıt bu worker'daki soketlerle eşitlenir: yerel ajanlar yazılır, artık burada olmayanların (kesintide kopan)
        kaydı silinir."""
        local = set(self.manager.active_agents)
        if local:
            await self.redis.hset(K_AGENTS, mapping={pc: WORKER_ID for pc in local})
        entries = await self.redis.hgetall(K_AGENTS)
        for pc, w in (entries or {}).items():
            if w == WORKER_ID and pc not in self.manager.active_agents:
                await self._scripts["release"](keys=[K_AGENTS], args=[pc, WORKER_ID])

    async def _after_reconnect(self) -> None:
        """Kesintiden sonra: kayıt yerel soketlerle eşitlenir, kesintide değişen oturum yetkileri işlenir."""
        try:
            await self._reassert_agents()
            journal, self._vision_journal = self._vision_journal, []
            for op in journal:
                await self._vision_write(*op)
            for pc, value in (await self.redis.hgetall(K_TUNNELS) or {}).items():
                if pc not in self.manager.active_vision_ws and json.loads(value).get("w") == WORKER_ID:
                    await self._tunnel_apply(pc, None)   # kesintide kapanan tünel
            for pc in list(self.manager.active_vision_ws):
                self.manager._share_tunnel(pc)
            await self._beat()
        except Exception as exc:
            self._down(exc, "reconnect")
            return
        for hook in self.reconnect_hooks:
            try:
                hook()
            except Exception:
                log.warning("yeniden bağlanma işi başarısız", exc_info=True)

    # ── Uzaktan oturum yetkileri (F1/F12; bellekteki kopya manager.vision_sessions)

    async def _vision_write(self, kind: str, pc_name: Optional[str], username: Optional[str], rec: Optional[dict]):
        if kind == "set":
            await self.redis.hset(K_VISION, _vfield(pc_name, username), json.dumps(rec))
        elif kind == "del":
            await self.redis.hdel(K_VISION, _vfield(pc_name, username))
        else:
            fields = await self.redis.hkeys(K_VISION)
            drop = []
            for f in fields or []:
                try:
                    pc, user = json.loads(f)
                except ValueError:
                    drop.append(f)
                    continue
                if (kind == "drop_user" and user == username) or (kind == "drop_pc" and pc == pc_name):
                    drop.append(f)
            if drop:
                await self.redis.hdel(K_VISION, *drop)
        await self.publish(CH_EVENTS, {"k": "vision", "op": kind, "pc": pc_name, "u": username, "r": rec})

    def vision_change(self, kind: str, pc_name: Optional[str], username: Optional[str], rec: Optional[dict] = None):
        """Yetki değişikliği sırayla Redis'e yazılır ve diğer worker'lara bildirilir; Redis yoksa sıraya alınır ve
        bağlantı geri gelince yazılır. Bellekteki kopyayı çağıran zaten değiştirmiştir."""
        self._vision_gen += 1
        self.defer(self._vision_apply, kind, pc_name, username, rec)

    async def _vision_apply(self, kind, pc_name, username, rec):
        if not self.healthy:
            self._vision_journal.append((kind, pc_name, username, rec))
            return
        try:
            await self._vision_write(kind, pc_name, username, rec)
        except Exception as exc:
            self._vision_journal.append((kind, pc_name, username, rec))
            self._down(exc, "vision")

    def tunnel_change(self, pc_name: str, info: Optional[dict]) -> None:
        """Bu worker'daki Vision tüneli açıldı / değişti (info) ya da kapandı (None): diğer worker'lar pano sahibini
        ve monitörleri buradan bilir."""
        self.defer(self._tunnel_apply, pc_name, info)

    async def _tunnel_apply(self, pc_name: str, info: Optional[dict]):
        if not self.healthy:
            return   # yeniden bağlanınca bu worker'ın tünelleri yeniden yazılır
        if info is None:
            if int(await self._scripts["tunnel_del"](keys=[K_TUNNELS], args=[pc_name, WORKER_ID])):
                await self.publish(CH_EVENTS, {"k": "tunnel", "pc": pc_name, "info": None})
            return
        await self.redis.hset(K_TUNNELS, pc_name, json.dumps(dict(info, w=WORKER_ID)))
        await self.publish(CH_EVENTS, {"k": "tunnel", "pc": pc_name, "info": info})

    async def vision_resync(self) -> None:
        """Bütün yetkiler ve tüneller Redis'ten okunur ve bellekteki kopyanın yerine geçer (kaçan bildirimleri
        düzeltir). Okuma sürerken bu worker'da yetki değiştiyse ya da yazılmayı bekleyen değişiklik varsa bu tur
        atlanır."""
        if not self.healthy or self._vision_journal or self._outbox:
            return
        gen = self._vision_gen
        raw = await self.redis.hgetall(K_VISION)
        tunnels_raw = await self.redis.hgetall(K_TUNNELS)
        live_workers = await self._live_workers()
        now = time.time()
        sessions, modes, expired = {}, {}, []
        for f, value in (raw or {}).items():
            try:
                pc, user = json.loads(f)
                rec = json.loads(value)
                exp = float(rec["e"])
            except (ValueError, TypeError, KeyError):
                expired.append(f)
                continue
            if exp > now:
                sessions.setdefault(pc, {})[user] = exp
                modes[(pc, user)] = (float(rec.get("o") or 0), bool(rec.get("m")))
            else:
                expired.append(f)
        if expired:
            await self.redis.hdel(K_VISION, *expired)
        tunnels, dead = {}, []
        for pc, value in (tunnels_raw or {}).items():
            try:
                info = json.loads(value)
            except ValueError:
                dead.append(pc)
                continue
            if info.get("w") not in live_workers:
                dead.append(pc)
            elif info.get("w") != WORKER_ID:
                tunnels[pc] = info
        if dead:
            await self.redis.hdel(K_TUNNELS, *dead)
        self._last_vision_sync = time.monotonic()
        if gen == self._vision_gen and not self._outbox:
            self.manager.replace_vision_sessions(sessions, modes)
            self.manager.replace_tunnels(tunnels)

    # ── Bildirim sayaçları (pops/notify.py)

    async def claim_once(self, key: tuple, seconds: int) -> Optional[bool]:
        """Olay bu süre içinde ilk kez mi (bütün worker'larda)? Redis yoksa None."""
        if not self.healthy:
            return None
        digest = hashlib.sha256(json.dumps(key, ensure_ascii=False, default=str).encode("utf-8")).hexdigest()
        try:
            return bool(await self.redis.set(_P + "notify:dedupe:" + digest, "1", nx=True, ex=seconds))
        except Exception as exc:
            self._down(exc, "dedupe")
            return None

    async def forget_once(self, key: tuple) -> None:
        if not self.healthy:
            return
        digest = hashlib.sha256(json.dumps(key, ensure_ascii=False, default=str).encode("utf-8")).hexdigest()
        try:
            await self.redis.delete(_P + "notify:dedupe:" + digest)
        except Exception as exc:
            self._down(exc, "dedupe")

    async def rate_ok(self, window: int, limit: int) -> Optional[bool]:
        """Dışarıya gönderim sınırı (bütün worker'larda); Redis yoksa None."""
        if not self.healthy:
            return None
        try:
            res = await self._scripts["rate"](keys=[K_NOTIFY_SENT],
                                              args=[time.time(), window, limit, secrets.token_hex(8)])
        except Exception as exc:
            self._down(exc, "rate")
            return None
        return bool(int(res))


cluster = Cluster()
