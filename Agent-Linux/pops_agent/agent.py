"""Ajanın bağlantı döngüsü ve sunucu komutları (Windows Worker'ın Linux karşılığı, v1 kapsamı).

Bağlantı: wss://<sunucu>/ws/agent/<HW-kimlik>, başlıklar X-Agent-Version, X-Agent-Platform: linux ve varsa
X-Agent-Secret / X-Enroll-Token. İlk mesaj dna_payload'lı heartbeat'tir (sunucu kimliği ondan çözer), sonra 5 sn'de
bir heartbeat. Kopunca üstel geri çekilme + jitter (4401'de en az 60 sn, 4409'da en az 10 dk).

Desteklenen emirler: execute, cancel_task, get_hardware, set_capabilities, set_secret, set_identity,
set_bypass_secret, update_agent, wake_peer, server_info, result_ack, update_result_ack. Bu sürümde olmayanlar
(karantina, Vision/ekran, Windows Update) capability_denied ile "not_supported" olarak reddedilir: panel işlemi
"reddedildi" gösterir, beklemede kalmaz.
"""

import asyncio
import base64
import collections
import hashlib
import json
import logging
import os
import random
import re
import signal
import socket
import time
from typing import Dict, Optional, Set
from urllib.parse import quote, urlsplit

from pops_agent import (PLATFORM, __version__, audit, capabilities, commands, config, identity, inventory, net,
                        protocol, session, spool, store, update)
from pops_agent import paths as paths_mod

log = logging.getLogger("pops.agent")

HEARTBEAT_SECONDS = 5.0
MAX_COMMAND_MESSAGE = 8 * 1024 * 1024
POLICY_SECONDS = 60.0
SESSION_SECONDS = 15.0
SESSION_RETRY = 300.0
SOFTWARE_INTERVAL = 6 * 3600
SOFTWARE_RETRY = 15 * 60
SOFTWARE_RESEND = 24 * 3600
MAX_PENDING_RESULTS = 20
NOT_SUPPORTED = "not_supported"
UNASSIGNED_LAB = "Atanmamis_Cihazlar"
_BYPASS_RE = re.compile(r"^[A-Za-z0-9_-]{43}$")


class Health:
    """Heartbeat'teki agent_health (Backend/pops/agent_health.py alanları)."""

    def __init__(self):
        self.started_at = int(time.time())
        self.last_policy_sync = None
        self.last_inventory_upload = None
        self._errors = collections.deque()
        self.last_error = None

    def error(self, loop: str, message: str) -> None:
        now = time.monotonic()
        self._errors.append(now)
        self.last_error = ("%s: %s" % (loop, message))[:200]
        self._prune(now)

    def _prune(self, now: float) -> None:
        while self._errors and now - self._errors[0] > 3600:
            self._errors.popleft()

    def snapshot(self) -> Dict:
        self._prune(time.monotonic())
        return {"started_at": self.started_at, "last_policy_sync": self.last_policy_sync,
                "last_inventory_upload": self.last_inventory_upload, "vision_channel": "off",
                "loop_errors_1h": len(self._errors), "last_error": self.last_error}


class Agent:
    def __init__(self, paths, version: str = __version__, hw_root: Optional[str] = None, proc_root: str = "/proc",
                 etc_root: str = "/etc"):
        """hw_root: yalnızca testler için; DMI/ağ/disk bilgisi <hw_root>/sys, /run ve /dev altından okunur."""
        self.paths = paths
        self.version = version
        base = hw_root or "/"
        self.sys_root, self.run_root, self.dev_root = (os.path.join(base, d) for d in ("sys", "run", "dev"))
        self.proc_root, self.etc_root = proc_root, etc_root
        # Kurulu ajanın denetim izi "yalnızca ekleme" (chattr +a); testlerin geçici klasörlerinde değil
        self.audit = audit.LocalAudit(paths.audit_log, append_only=paths.log_dir == paths_mod.LOG_DIR)
        self.caps = capabilities.Capabilities(paths.capabilities_file, paths.capability_state, self.audit.write)
        self.handshake = protocol.ServerHandshake()
        self.update_results = protocol.UpdateResultReporter(self.handshake)
        self.results = spool.ResultSpool(paths.results)
        self.pending_results = collections.deque()
        self.runner = commands.CommandRunner()
        self.binding = identity.Binding(paths.binding)
        self.health = Health()
        self.update = update.UpdateManager(paths, version, lambda: self.http(auth=False), self.audit.write)
        self.sampler = inventory.SystemSampler(proc_root)
        self.config = config.AgentConfig()
        self.release = inventory.os_release(etc_root)
        self.hostname = identity.hostname()
        self.hw: Dict = {}
        self.dna: Dict = {}
        self.hw_id: Optional[str] = None
        self.secret: Optional[str] = None
        self.ssl_ctx = None
        self.ws = None
        self.connected_with_secret = False
        self.modules_closed: Optional[Set[str]] = None
        self.stopping: Optional[asyncio.Event] = None
        self.send_lock: Optional[asyncio.Lock] = None
        self.command_tasks: Set[asyncio.Future] = set()
        self.background: Set[asyncio.Future] = set()
        self._denials: Dict[str, float] = {}
        self._clone_audited = False

    # ── Açılış ──
    def init_core(self) -> Dict[str, bool]:
        checks = {"identity": False, "credentials": False, "capabilities": False, "loop": False}
        store.ensure_dir(self.paths.state_dir, 0o700)
        config.secure_file(self.paths.config_file)
        self.config = config.load(self.paths.config_file)
        self.hw = identity.hardware(self.sys_root, self.proc_root, self.run_root, self.dev_root)
        self.dna = identity.dna_payload(self.hw, inventory.os_name(self.release))
        verdict = self._check_binding()
        self.hw_id = identity.load_or_create(self.paths.identity, self.hw)
        checks["identity"] = identity.is_hw_id(self.hw_id)
        log.info("Kimlik: %s (%s %s, ajan %s).", self.hw_id, inventory.os_name(self.release), PLATFORM, self.version)
        self.secret = self.load_secret()
        checks["credentials"] = True
        self._apply_binding(verdict)
        self.caps.load()
        checks["capabilities"] = True
        return checks

    def _check_binding(self) -> str:
        try:
            verdict = self.binding.check(self.hw["uuid"], self.hw["bios_sn"])
        except OSError as exc:
            log.error("Donanım bağı denetlenemedi: %s", exc)
            return identity.UNREADABLE
        if verdict == identity.CLONE:
            previous = (store.read_text(self.paths.identity) or "").strip() or None
            dropped = self.results.discard()
            folder, moved = identity.move_clone_files(self.paths.state_dir)
            token = config.load(self.paths.config_file).enroll_token is not None
            log.error("[GÜVENLİK] Kopyalanmış kurulum: cihaz anahtarı bu donanıma ait değil. %s %s klasörüne taşındı "
                      "(onay bekleyen %d sonuç gönderilmeyecek). %s", ", ".join(moved), folder, dropped,
                      "Kayıt jetonuyla yeni cihaz olarak kaydolunacak." if token
                      else "Kayıt jetonu yok: cihaz kayıtsız kalacak, yönetici jeton vermeli.")
            self.audit.write("clone_detected", old_hw_id=previous, moved_to=folder, files=", ".join(moved),
                             enroll_token="var" if token else "yok")
        elif verdict == identity.PARTIAL:
            self.audit.write("hardware_partly_changed", changed=", ".join(self.binding.changed),
                             same=", ".join(self.binding.same))
            log.warning("Donanımın bir kısmı değişti (%s); kopya kararı verilmedi.", ", ".join(self.binding.changed))
        return verdict

    def _apply_binding(self, verdict: str) -> None:
        try:
            if verdict == identity.MATCH:
                bound = self.binding.bound_hw_id
                if identity.is_hw_id(bound) and bound != self.hw_id:
                    log.warning("Kimlik dosyası anahtarın verildiği kimlikten farklı (%s); %s geri yükleniyor.",
                                self.hw_id, bound)
                    identity.save(self.paths.identity, bound)
                    self.hw_id = bound
            elif verdict == identity.MISSING and self.secret:
                self.binding.bind(self.hw_id, self.hw["uuid"], self.hw["bios_sn"], "ilk kullanımda güven")
        except OSError as exc:
            log.error("Donanım bağı işlenemedi: %s", exc)

    def load_secret(self) -> Optional[str]:
        try:
            data = store.read_json(self.paths.secret)
        except (ValueError, OSError) as exc:
            log.error("Cihaz anahtarı okunamadı: %s", exc)
            return None
        secret = data.get("secret") if isinstance(data, dict) else None
        return secret if isinstance(secret, str) and config.TOKEN_RE.match(secret) else None

    # ── Ana döngü ──
    async def run(self) -> int:
        self.stopping = asyncio.Event()
        self.send_lock = asyncio.Lock()
        loop = asyncio.get_running_loop()
        for sig in (signal.SIGTERM, signal.SIGINT):
            try:
                loop.add_signal_handler(sig, self.stopping.set)
            except (NotImplementedError, RuntimeError):
                pass
        checks = self.init_core()
        checks["loop"] = True
        # Kurucu yeni sürümün sağlığını buradan anlar: yalnızca çekirdek açılış tamamsa yazılır (Windows ile aynı)
        if not self.update.startup_skip_health() and all(checks.values()):
            self.update.write_health(checks)
        update.prune_packages(self.paths.packages_dir, self.version)
        last = store.read_text(self.paths.update_result)
        if last:
            log.info("Son güncelleme sonucu: %s", last.strip()[:500])
        for coro in (self.policy_loop(), self.software_loop(), self.session_loop()):
            self._spawn(coro)
        try:
            await self.connection_loop()
        finally:
            await self.shutdown()
        return 0

    def _spawn(self, coro) -> asyncio.Future:
        task = asyncio.ensure_future(coro)
        self.background.add(task)
        task.add_done_callback(self.background.discard)
        return task

    async def shutdown(self) -> None:
        self.stopping.set()
        for t in list(self.background):
            t.cancel()
        if self.command_tasks:
            log.info("Servis duruyor: çalışan %d komut sonlandırılıyor.", len(self.command_tasks))
            await asyncio.wait(list(self.command_tasks), timeout=15)

    async def _sleep(self, seconds: float) -> bool:
        """Durdurulana kadar bekler; dönen: servis duruyor mu."""
        try:
            await asyncio.wait_for(self.stopping.wait(), max(0.0, seconds))
            return True
        except asyncio.TimeoutError:
            return False

    async def connection_loop(self) -> None:
        attempt = 0
        warned = 0.0
        while not self.stopping.is_set():
            self.config = config.load(self.paths.config_file)
            if not self.config.server_url or not self.config.secure:
                if time.monotonic() - warned > 600 or not warned:
                    warned = time.monotonic()
                    if not self.config.server_url:
                        log.error("SERVER_URL tanımlı değil (%s); sunucuya bağlanılmıyor. `pops-agent configure "
                                  "--server https://...` ile ayarlayın.", self.paths.config_file)
                    else:
                        log.error("[GÜVENLİK] SERVER_URL şifresiz http ve yerel değil (%s); cihaz anahtarı ve komutlar "
                                  "ağda açık gideceği için bağlanılmıyor. https:// bir adres verin.",
                                  self.config.server_url)
                if await self._sleep(30):
                    return
                continue
            healthy, code = False, None
            try:
                https = self.config.server_url.startswith("https")
                self.ssl_ctx = net.ssl_context(self.config.ca_file) if https else None
                healthy, code = await self.session()
            except net.TrustError as exc:
                self.health.error("tls", str(exc))
                log.error("%s", exc)
            except Exception as exc:   # noqa: BLE001 - bağlantı hatası yeniden denenir
                self.health.error("connection", repr(exc)[:200])
                log.warning("Sunucuyla bağlantı kurulamadı ya da koptu: %s", _short(exc))
            if self.stopping.is_set():
                return
            if healthy:
                attempt = 0
            kind = protocol.rejection(code)
            wait = protocol.delay(attempt, kind)
            attempt = protocol.next_attempt(attempt)
            if kind == "auth":
                self.audit.write("auth_rejected", channel="command")
                log.error("[GÜVENLİK] Sunucu ajan kimliğini reddetti (4401): geçerli bir kayıt jetonu gerekiyor. "
                          "%.0f sn sonra yeniden denenecek.", wait)
            elif kind == "clone":
                if not self._clone_audited:
                    self.audit.write("clone_rejected", channel="command")
                    self._clone_audited = True
                log.error("[GÜVENLİK] Sunucu bu cihaz kimliğinin (%s) başka bir bilgisayarda bağlı olduğunu bildirdi "
                          "(4409): bu kurulum kopyalanmış olabilir (bkz. pops-agent generalize). %.1f dk sonra yeniden "
                          "denenecek.", self.hw_id, wait / 60)
            else:
                log.info("Sunucuya %.1f sn sonra yeniden bağlanılacak%s.", wait,
                         " (kapanış kodu %s)" % code if code else "")
            if await self._sleep(wait):
                return

    def ws_headers(self) -> Dict[str, str]:
        headers = {"X-Agent-Version": self.version, "X-Agent-Platform": PLATFORM}
        if self.secret:
            headers["X-Agent-Secret"] = self.secret
        if self.config.enroll_token:
            headers["X-Enroll-Token"] = self.config.enroll_token
        return headers

    async def session(self):
        uri = "%s/ws/agent/%s" % (self.config.ws_base, quote(self.hw_id, safe=""))
        headers = self.ws_headers()
        mode = " + ".join(x for x in ("secret" if self.secret else "",
                                      "kayıt jetonu" if self.config.enroll_token else "") if x) or "yok"
        ws = await net.ws_connect(uri, headers, self.ssl_ctx, MAX_COMMAND_MESSAGE)
        log.info("Komut kanalı kuruldu (%s, kimlik: %s).", self.config.server_url, mode)
        self.connected_with_secret = self.secret is not None
        self.update_results.on_connected()
        self.results.on_connected()
        self.ws = ws
        receiver = asyncio.ensure_future(self.receive(ws))
        healthy = False
        capabilities_reported = False
        try:
            while not self.stopping.is_set() and not receiver.done():
                await self.send(self.heartbeat())
                if not capabilities_reported:
                    status = self.caps.status_message(net.trust_mode(self.config.ca_file))
                    capabilities_reported = await self.send(status)
                await self.report_update_result()
                await self.flush_pending_results()
                stop = asyncio.ensure_future(self.stopping.wait())
                try:
                    await asyncio.wait({receiver, stop}, timeout=HEARTBEAT_SECONDS, return_when=asyncio.FIRST_COMPLETED)
                finally:
                    stop.cancel()
                if not receiver.done():
                    healthy = True
        finally:
            self.ws = None
            if not receiver.done():
                try:
                    await asyncio.wait_for(ws.close(1001, "servis duruyor" if self.stopping.is_set() else ""), 6)
                except Exception:   # noqa: BLE001
                    pass
            try:
                exc = await asyncio.wait_for(receiver, 6)
            except Exception as e:   # noqa: BLE001
                exc = e
        return healthy, net.close_code(ws, exc if isinstance(exc, BaseException) else None)

    async def receive(self, ws):
        """Bağlantı kapanana kadar sunucu mesajları; dönen: kapanış istisnası (yoksa None)."""
        while True:
            try:
                raw = await ws.recv()
            except Exception as exc:   # noqa: BLE001 - ConnectionClosed ve ağ hataları
                return exc
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8", "replace")
            try:
                message = json.loads(raw)
            except ValueError:
                log.warning("Sunucu mesajı çözümlenemedi, yok sayıldı.")
                continue
            if not isinstance(message, dict):
                continue
            try:
                await self.handle(message)
            except Exception as exc:   # noqa: BLE001 - tek bir mesaj kanalı düşürmez
                self.health.error("message", repr(exc)[:200])
                log.exception("Sunucu mesajı işlenemedi: %s", exc)

    # ── Giden mesajlar ──
    async def send(self, payload: Dict) -> bool:
        """Dönen: mesaj o anki komut soketine yazıldı mı."""
        ws = self.ws
        if ws is None:
            return False
        async with self.send_lock:
            try:
                await ws.send(json.dumps(payload, ensure_ascii=False))
                return True
            except Exception as exc:   # noqa: BLE001
                log.debug("Sunucuya mesaj gönderilemedi: %s", exc)
                return False

    def heartbeat(self) -> Dict:
        return {"hw_id": self.hw_id, "hostname": self.hostname, "lab_name": UNASSIGNED_LAB, "status": "Online",
                "active_window": "-", "platform": PLATFORM, "dna_payload": self.dna,
                "agent_health": self.health.snapshot(), "system": self.sampler.sample()}

    async def send_result(self, task_id: int, result: Dict) -> None:
        """Onaylı sunucuda önce diske yazılır, onay gelene kadar kalır; onaysız sunucuda bellekte sırada bekler."""
        ack = self.handshake.supports(spool.ACK_FEATURE)
        durable = ack if ack is not None else (self.handshake.last_known(spool.ACK_FEATURE) or False)
        if durable:
            self.results.add(task_id, result)
            if ack is True and await self.send(result):
                self.results.mark_sent(task_id)
            return
        if not self.pending_results and await self.send(result):
            return
        self.pending_results.append((task_id, result))
        while len(self.pending_results) > MAX_PENDING_RESULTS:
            self.pending_results.popleft()
            log.warning("Gönderilemeyen görev sonuçları sınırı aşıldı; en eskisi atıldı.")

    async def flush_pending_results(self) -> None:
        ack = self.handshake.supports(spool.ACK_FEATURE)
        if ack is True:
            while self.pending_results:
                self.results.add(*self.pending_results.popleft())
            for task_id, result in self.results.unsent():
                if not await self.send(result):
                    return
                self.results.mark_sent(task_id)
            return
        while self.pending_results:
            if not await self.send(self.pending_results[0][1]):
                return
            self.pending_results.popleft()
        if ack is False:
            for task_id, result in self.results.all():
                if not await self.send(result):
                    return
                self.results.remove(task_id)

    async def report_update_result(self) -> None:
        message = self.update.pending_result_message()
        if message is None:
            return
        rid = message["result_id"]
        step = self.update_results.next(rid)
        if step in (protocol.NOTHING, protocol.WAIT):
            return
        if not await self.send(message):
            return
        if step == protocol.SEND_AND_MARK:
            self.update.mark_reported()
            log.info("Güncelleme sonucu sunucuya iletildi: %s.", message.get("status"))
        else:
            self.update_results.sent(rid)
            log.info("Güncelleme sonucu sunucuya iletildi, onay bekleniyor: %s (%s).", message.get("status"), rid)

    async def deny(self, capability: str, action: str, task_id: Optional[int] = None, reason: Optional[str] = None,
                   throttle: bool = False):
        """Kapalı ya da bu sürümde olmayan yetenek: loglanır ve capability_denied bildirilir. Sık gelen istekler
        (throttle: panelin önizleme ve uzaktan girdi iletileri) aynı yetenek/eylem/neden için en çok dakikada bir
        bildirilir; tek seferlik emirler (karantina, Vision oturumu) her seferinde yanıtlanır ki sunucu reddi görsün."""
        key = "%s/%s/%s" % (capability, action, reason)
        now = time.monotonic()
        if throttle and task_id is None and now - self._denials.get(key, -1e9) < 60:
            return
        self._denials[key] = now
        if reason == NOT_SUPPORTED:
            log.warning("%s reddedildi: %s bu ajan sürümünde (Linux) yok.", action, capability)
            self.audit.write("unsupported_refused", capability=capability, action=action)
        elif reason == commands.MODULE_DISABLED_REASON:
            log.warning("%s reddedildi: %s modülü bu bilgisayarın laboratuvarında kapalı.", action, capability)
        else:
            log.warning("[GÜVENLİK] %s reddedildi: %s bu cihazda kapalı (yetenek politikası).", action, capability)
        notice = {"type": "capability_denied", "capability": capability, "action": action}
        if task_id is not None:
            notice["task_id"] = task_id
        if reason:
            notice["reason"] = reason
        await self.send(notice)

    def module_enabled(self, module: str) -> bool:
        return not self.modules_closed or module not in self.modules_closed

    # ── Gelen emirler ──
    async def handle(self, msg: Dict) -> None:
        if msg.get("type") == "remote_input":
            if msg.get("device") in (None, self.hw_id):
                await self.deny("vision", str(msg.get("action") or "remote_input")[:40], reason=NOT_SUPPORTED,
                                throttle=True)
            return
        action = msg.get("action")
        if not isinstance(action, str):
            return
        handler = {
            "execute": self.on_execute,
            "cancel_task": self.on_cancel,
            "server_info": self.on_server_info,
            "result_ack": self.on_result_ack,
            "update_result_ack": self.on_update_result_ack,
            "get_hardware": self.on_get_hardware,
            "set_capabilities": self.on_set_capabilities,
            "set_secret": self.on_set_secret,
            "set_identity": self.on_set_identity,
            "set_bypass_secret": self.on_set_bypass_secret,
            "update_agent": self.on_update_agent,
            "wake_peer": self.on_wake_peer,
        }.get(action)
        if handler is not None:
            await handler(msg)
        elif action in ("lockdown", "unlock"):
            await self.deny("quarantine", action, reason=NOT_SUPPORTED)
        elif action in ("start_stream", "start_vision_session"):
            await self.deny("vision", action, reason=NOT_SUPPORTED)
        elif action in ("scan_updates", "install_updates"):
            await self.deny("patches", action, reason=NOT_SUPPORTED)
        elif action != "stop_stream":
            log.debug("Tanınmayan emir yok sayıldı: %s", action[:40])

    async def on_execute(self, msg: Dict) -> None:
        tid = msg.get("task_id")
        if not isinstance(tid, int) or isinstance(tid, bool):
            return
        command = msg.get("script_path") if isinstance(msg.get("script_path"), str) else ""
        requested_by = msg.get("requested_by") if isinstance(msg.get("requested_by"), str) else None
        perm = commands.permission(self.caps.terminal_enabled, self.module_enabled("terminal"))
        if not perm.allowed:
            # Görev "Running"de asılı kalmasın: ret sonuç olarak da bildirilir (çıkış kodu -5)
            await self.send_result(tid, {"type": "result", "pc_name": self.hw_id, "task_id": tid,
                                         "output": perm.rejection, "exit_code": commands.EXIT_DENIED})
            self.audit.write("command_refused", task_id=tid, requested_by=requested_by,
                             reason=perm.reason or "capability_off")
            await self.deny("terminal", "execute", tid, perm.reason)
            return
        if self.runner.is_running(tid):
            log.info("Uzaktan komut zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: %d).", tid)
            return
        log.info("Uzaktan komut çalıştırılıyor (TaskID: %d).", tid)
        self.audit.write("command_started", task_id=tid, command_sha256=audit.command_sha256(command),
                         command_length=len(command), requested_by=requested_by)
        task = asyncio.ensure_future(self._run_command(tid, command))
        self.command_tasks.add(task)
        task.add_done_callback(self.command_tasks.discard)

    async def _run_command(self, tid: int, command: str) -> None:
        result = await self.runner.run(tid, command, self.stopping)
        if result.exit_code == commands.EXIT_DUPLICATE:
            log.info("Uzaktan komut zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: %d).", tid)
            return
        self.audit.write("command_finished", task_id=tid, exit_code=result.exit_code,
                         duration_ms=int(result.duration * 1000))
        log.info("Uzaktan komut bitti (TaskID: %d, çıkış %d, %.1f sn).", tid, result.exit_code, result.duration)
        await self.send_result(tid, {"type": "result", "pc_name": self.hw_id, "output": result.output, "task_id": tid,
                                     "exit_code": result.exit_code})

    async def on_cancel(self, msg: Dict) -> None:
        tid = msg.get("task_id")
        if isinstance(tid, int) and not isinstance(tid, bool) and self.runner.cancel(tid):
            log.info("Uzaktan komut panelden iptal edildi; işlem sonlandırılıyor (TaskID: %d).", tid)

    async def on_server_info(self, msg: Dict) -> None:
        self.handshake.on_server_info(msg)

    async def on_result_ack(self, msg: Dict) -> None:
        tid = msg.get("task_id")
        if isinstance(tid, int) and not isinstance(tid, bool) and self.results.remove(tid):
            log.info("Sunucu görev sonucunu onayladı (TaskID: %d).", tid)

    async def on_update_result_ack(self, msg: Dict) -> None:
        pending = self.update.pending_result_id()
        if protocol.UpdateResultReporter.acknowledges(msg, pending):
            self.update.mark_reported()
            update.prune_packages(self.paths.packages_dir, self.version)
            log.info("Sunucu güncelleme sonucunu onayladı (%s).", pending)
        elif pending:
            log.info("Sunucunun onayı bekleyen güncelleme sonucuyla eşleşmiyor; sonuç saklanmaya devam ediyor.")

    async def on_get_hardware(self, msg: Dict) -> None:
        self._spawn(self.send_inventory())

    async def send_inventory(self) -> None:
        try:
            host = urlsplit(self.config.server_url).hostname
            inv = await asyncio.to_thread(inventory.hardware_inventory, self.hw_id, self.hostname, self.hw.get("mac"),
                                          host, self.release, self.sys_root, self.proc_root)
            status, _ = await asyncio.to_thread(self.http().request, "POST",
                                                net.device_path("/api/inventory/", self.hw_id), inv)
            if 200 <= status < 300:
                self.health.last_inventory_upload = int(time.time())
                log.info("Donanım envanteri sunucuya gönderildi.")
            else:
                log.warning("Donanım envanteri gönderilemedi: HTTP %d.", status)
        except Exception as exc:   # noqa: BLE001
            self.health.error("inventory", _short(exc))
            log.warning("Donanım envanteri gönderilemedi: %s", _short(exc))

    async def on_set_capabilities(self, msg: Dict) -> None:
        self.caps.apply_server_request(msg)
        await self.send(self.caps.status_message(net.trust_mode(self.config.ca_file)))

    async def on_set_secret(self, msg: Dict) -> None:
        secret = msg.get("secret")
        if not isinstance(secret, str) or not config.TOKEN_RE.match(secret):
            log.error("[GÜVENLİK] set_secret yok sayıldı: secret biçimi geçersiz.")
            return
        self.binding.bind(self.hw_id, self.hw.get("uuid"), self.hw.get("bios_sn"), "set_secret")
        store.delete(self.paths.software_gate)   # yeni kayıtta sunucunun yazılım listesi boş
        self.secret = secret
        try:
            store.write_json(self.paths.secret, {"secret": secret, "hw_id": self.hw_id, "saved_at": int(time.time())})
            if config.forget_enroll_token(self.paths.config_file):
                self.config.enroll_token = None
            log.info("Cihaz anahtarı alındı ve %s'e yazıldı; sonraki bağlantılar anahtarla doğrulanacak.",
                     self.paths.secret)
        except OSError as exc:
            log.error("Cihaz anahtarı alındı ama diske yazılamadı; servis yeniden başlayana kadar bellekte: %s", exc)
        self.audit.write("enrolled", hw_id=self.hw_id)

    async def on_set_identity(self, msg: Dict) -> None:
        new_id = msg.get("new_hw_id")
        if not identity.is_hw_id(new_id) or new_id == self.hw_id:
            return
        old = self.hw_id
        try:
            identity.save(self.paths.identity, new_id)
        except OSError as exc:
            log.error("Kimlik dosyası yazılamadı: %s", exc)
        self.hw_id = new_id
        self.binding.update_hw_id(new_id)
        self.audit.write("identity_changed", old_hw_id=old, new_hw_id=new_id)
        log.warning("Sunucu kimliği değiştirdi: %s -> %s.", old, new_id)

    async def on_set_bypass_secret(self, msg: Dict) -> None:
        """Çevrimdışı açma kodunun cihaz anahtarı. Karantina bu sürümde yok; anahtar saklanır ve onaylanır ki karantina
        geldiğinde hazır olsun ve sunucu her bağlanışta yeniden göndermesin."""
        secret = msg.get("secret")
        if not self.connected_with_secret:
            log.warning("[GÜVENLİK] set_bypass_secret yok sayıldı: komut bağlantısı cihaz anahtarıyla doğrulanmadı.")
            return
        key = _decode_bypass(secret)
        if key is None:
            log.warning("[GÜVENLİK] set_bypass_secret yok sayıldı: anahtar biçimi geçersiz.")
            return
        try:
            store.write_atomic(self.paths.bypass_key, secret + "\n", 0o600)
        except OSError as exc:
            log.error("Bypass cihaz anahtarı yazılamadı: %s", exc)
            return
        fingerprint = hashlib.sha256(key).hexdigest()[:16]
        self.audit.write("bypass_secret_received", fingerprint=fingerprint)
        await self.send({"type": "bypass_secret_ack", "fingerprint": fingerprint})

    async def on_update_agent(self, msg: Dict) -> None:
        loop = asyncio.get_running_loop()

        def progress(stage: str, to_version: Optional[str] = None, detail: Optional[str] = None) -> None:
            """update_progress yalnızca bunu duyuran sunucuya (server_info features); kurulum iş parçacığından."""
            if self.handshake.supports("update_progress") is not True:
                return
            message = {"type": "update_progress", "stage": stage}
            if to_version:
                message["to_version"] = to_version
            if detail:
                message["detail"] = detail
            asyncio.run_coroutine_threadsafe(self.send(message), loop)

        self._spawn(asyncio.to_thread(self.update.handle, dict(msg), progress))

    async def on_wake_peer(self, msg: Dict) -> None:
        if not self.module_enabled("wol"):
            await self.deny("wol", "wake_peer", reason=commands.MODULE_DISABLED_REASON)
            return
        mac = msg.get("mac") if isinstance(msg.get("mac"), str) else ""
        packet = magic_packet(mac)
        if packet is None:
            log.warning("WOL: geçersiz MAC adresi (%s).", mac[:40])
            return
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
                s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
                s.sendto(packet, ("255.255.255.255", 9))
            log.info("WOL sihirli paketi gönderildi: %s", mac)
        except OSError as exc:
            log.warning("WOL gönderilemedi (%s): %s", mac, exc)

    # ── HTTP ──
    def http(self, auth: bool = True) -> net.HttpClient:
        headers = {"X-Agent-Version": self.version, "X-Agent-Platform": PLATFORM,
                   "User-Agent": "pops-agent/" + self.version}
        if auth and self.secret:
            headers["X-Agent-Id"] = self.hw_id
            headers["X-Agent-Secret"] = self.secret
        if self.config.server_url.startswith("https") and self.ssl_ctx is None:
            self.ssl_ctx = net.ssl_context(self.config.ca_file)
        return net.HttpClient(self.config.server_url, self.ssl_ctx, headers)

    def _can_report(self) -> bool:
        return bool(self.secret and self.config.server_url and self.config.secure)

    async def policy_loop(self) -> None:
        """Dakikada bir /api/agent_policies: anahtarlı ajana laboratuvarının modül listesi gelir (kapalı modülün işi
        yapılmaz; ör. uzak komut modülü kapalıysa komut "[REDDEDİLDİ]" ile döner)."""
        while not self.stopping.is_set():
            if self.config.server_url and self.config.secure:
                try:
                    status, body = await asyncio.to_thread(self.http().request, "GET", "/api/agent_policies")
                    if not 200 <= status < 300:
                        raise OSError("HTTP %d" % status)
                    self.apply_policy(json.loads(body.decode("utf-8")))
                    self.health.last_policy_sync = int(time.time())
                except Exception as exc:   # noqa: BLE001
                    self.health.error("policy", _short(exc))
                    log.debug("Politika eşitleme başarısız: %s", _short(exc))
            if await self._sleep(POLICY_SECONDS):
                return

    def apply_policy(self, policy: Dict) -> None:
        modules = policy.get("modules") if isinstance(policy, dict) else None
        closed = {k for k, v in modules.items() if v is False} if isinstance(modules, dict) else set()
        before = self.modules_closed
        self.modules_closed = closed
        if before is None:
            if closed:
                log.info("Sunucu bu bilgisayarın laboratuvarında şu modülleri kapattı: %s.", ", ".join(sorted(closed)))
            return
        if closed != before:
            log.info("Sunucu modülleri değişti: kapatılan %s; açılan %s.", ", ".join(sorted(closed - before)) or "-",
                     ", ".join(sorted(before - closed)) or "-")
            if "software" in before - closed:
                store.delete(self.paths.software_gate)

    async def software_loop(self) -> None:
        if await self._sleep(60 + random.uniform(0, 300)):
            return
        while not self.stopping.is_set():
            wait = SOFTWARE_INTERVAL
            if self._can_report() and self.module_enabled("software"):
                try:
                    wait = await self.report_software()
                except Exception as exc:   # noqa: BLE001
                    self.health.error("software", _short(exc))
                    log.warning("Yazılım envanteri gönderilemedi: %s", _short(exc))
                    wait = SOFTWARE_RETRY
            elif not self.secret:
                wait = 60
            if await self._sleep(wait):
                return

    async def report_software(self) -> float:
        items = await asyncio.to_thread(inventory.installed_packages)
        digest = inventory.software_hash(items)
        try:
            gate = store.read_json(self.paths.software_gate) or {}
        except ValueError:
            gate = {}
        now = time.time()
        fresh = now - gate.get("sent_at", 0) < SOFTWARE_RESEND
        if gate.get("hash") == digest and gate.get("hw_id") == self.hw_id and fresh:
            return SOFTWARE_INTERVAL
        status, _ = await asyncio.to_thread(self.http().request, "POST",
                                            net.device_path("/api/software/", self.hw_id), {"items": items}, None, 120)
        if status in (404, 405):
            log.info("Sunucuda yazılım envanteri ucu yok (HTTP %d).", status)
            return SOFTWARE_INTERVAL
        if not 200 <= status < 300:
            raise OSError("HTTP %d" % status)
        store.write_json(self.paths.software_gate, {"hash": digest, "hw_id": self.hw_id, "sent_at": int(now)})
        self.health.last_inventory_upload = int(now)
        log.info("Yazılım envanteri gönderildi: %d paket.", len(items))
        return SOFTWARE_INTERVAL

    async def session_loop(self) -> None:
        retry_after = 0.0
        while not self.stopping.is_set():
            try:
                user, sid = await asyncio.to_thread(session.current)
                now = {"user": user, "session": sid, "boot": session.boot_id(self.proc_root)}
                if self._can_report() and time.monotonic() >= retry_after:
                    delivered = await self.report_session(now)
                    retry_after = 0.0 if delivered else time.monotonic() + SESSION_RETRY
            except Exception as exc:   # noqa: BLE001
                self.health.error("session", _short(exc))
                log.debug("Oturum durumu okunamadı: %s", _short(exc))
            if await self._sleep(SESSION_SECONDS):
                return

    async def report_session(self, now: Dict) -> bool:
        try:
            reported = store.read_json(self.paths.session)
        except ValueError:
            reported = None
        for action, user in session.diff(reported if isinstance(reported, dict) else None, now):
            body = {"hw_id": self.hw_id, "hostname": self.hostname, "student_id": user, "message": ""}
            status, _ = await asyncio.to_thread(self.http().request, "POST", "/api/auth/" + action, body)
            if not 200 <= status < 300:
                log.warning("Oturum %s bildirimi gönderilemedi: HTTP %d.", action, status)
                return False
            reported = now if action == "login" else {"user": None, "session": None, "boot": now.get("boot")}
            store.write_json(self.paths.session, reported)
            log.info("Oturum %s sunucuya bildirildi.", "açma" if action == "login" else "kapama")
        return True


def magic_packet(mac: str) -> Optional[bytes]:
    clean = (mac or "").replace(":", "").replace("-", "").replace(".", "").strip()
    if not re.match(r"^[0-9A-Fa-f]{12}$", clean):
        return None
    return b"\xff" * 6 + bytes.fromhex(clean) * 16


def _decode_bypass(value) -> Optional[bytes]:
    if not isinstance(value, str) or not _BYPASS_RE.match(value.strip()):
        return None
    v = value.strip()
    try:
        key = base64.urlsafe_b64decode(v + "=")
    except ValueError:
        return None
    if len(key) != 32 or base64.urlsafe_b64encode(key).decode().rstrip("=") != v:
        return None
    return key


def _short(exc: BaseException) -> str:
    text = str(exc) or exc.__class__.__name__
    return text[:200]
