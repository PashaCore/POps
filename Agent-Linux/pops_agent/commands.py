"""Uzaktan komut: /bin/sh -c ile root olarak, Windows ajanının CommandExecutionPolicy sınırlarıyla.

  * En çok 30 dakika; çıktı 512 K karakter (stderr 128 K). Sınırı aşan çıktı OKURKEN atılır ve sayılır: durmadan
    yazan bir komut ajanın belleğini tüketemez.
  * İşlem kendi süreç grubunda başlar; süre dolunca, panelden iptal edilince (cancel_task) ya da servis dururken
    bütün grup sonlandırılır. Çıkış kodu sonuçla birlikte gider.
  * Ortam temizdir (PATH, HOME=/root, LANG=C.UTF-8, DEBIAN_FRONTEND=noninteractive); stdin /dev/null.
  * Kapalı yetenekte komut çalıştırılmaz: sonuç "[REDDEDİLDİ] …" ve çıkış kodu -5 (sunucu bunu "Denied" yapar).
  * Panelin güç düğmeleri ve zamanlanmış görevler Windows komutu gönderir (shutdown /r|/s /f /t N): Linux'ta
    yalnızca bu iki kalıp N saniye sonra systemctl reboot|poweroff olarak çalışır.
"""

import asyncio
import codecs
import logging
import os
import re
import signal
import time
from typing import Dict, Optional

log = logging.getLogger("pops.command")

MAX_DURATION = 30 * 60
MAX_OUTPUT_CHARS = 512 * 1024
READ_CHUNK = 8192
STREAM_DRAIN_TIMEOUT = 10.0

DISABLED_MESSAGE = "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası); komut çalıştırılmadı."
MODULE_DISABLED_MESSAGE = ("[REDDEDİLDİ] Uzak komut modülü bu bilgisayarın laboratuvarında kapalı; komut "
                           "çalıştırılmadı.")
MODULE_DISABLED_REASON = "module_disabled"

EXIT_TIMEOUT = -1
EXIT_CANCELLED = -2
EXIT_AGENT_ERROR = -3
EXIT_SERVICE_STOPPING = -4
EXIT_DENIED = -5
EXIT_DUPLICATE = -6

SAFE_PATH = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"


class Permission:
    def __init__(self, allowed: bool, rejection: Optional[str] = None, reason: Optional[str] = None):
        self.allowed = allowed
        self.rejection = rejection
        self.reason = reason


def permission(terminal_enabled: bool, module_enabled: bool = True) -> Permission:
    """Yerel yetenek kilidi önce gelir; sunucuda modül kapalıysa ayrı neden."""
    if not terminal_enabled:
        return Permission(False, DISABLED_MESSAGE)
    if not module_enabled:
        return Permission(False, MODULE_DISABLED_MESSAGE, MODULE_DISABLED_REASON)
    return Permission(True)


def truncate_output(output: Optional[str]) -> str:
    output = output or ""
    if len(output) <= MAX_OUTPUT_CHARS:
        return output
    suffix = "\n[ÇIKTI KISALTILDI: toplam %d karakter]" % len(output)
    return output[: MAX_OUTPUT_CHARS - len(suffix)] + suffix


class BoundedOutput:
    def __init__(self, max_chars: int):
        self.max_chars = max(0, max_chars)
        self._parts = []
        self._len = 0
        self.dropped = 0

    def append(self, text: str) -> None:
        if not text:
            return
        room = self.max_chars - self._len
        take = min(max(room, 0), len(text))
        if take:
            self._parts.append(text[:take])
            self._len += take
        self.dropped += len(text) - take

    def text(self) -> str:
        body = "".join(self._parts)
        if self.dropped:
            return body.rstrip() + "\n[ÇIKTI KISALTILDI: %d karakter atıldı]" % self.dropped
        return body


class Result:
    def __init__(self, output: str, exit_code: int, duration: float):
        self.output = truncate_output(output)
        self.exit_code = exit_code
        self.duration = duration


_POWER = re.compile(r"^\s*shutdown\s+/([rs])\s+/f\s+/t\s+(\d{1,4})\s*$", re.IGNORECASE)


def translate_power_command(command: str) -> Optional[str]:
    """Panelin Windows güç komutu → Linux karşılığı (sonuç önce gider, bilgisayar N sn sonra kapanır); değilse None."""
    m = _POWER.match(command or "")
    if not m:
        return None
    verb = "reboot" if m.group(1).lower() == "r" else "poweroff"
    seconds = max(1, int(m.group(2)))
    return ("if command -v systemd-run >/dev/null 2>&1 && [ -d /run/systemd/system ]; then "
            "systemd-run --quiet --collect --on-active=%ds --timer-property=AccuracySec=1s systemctl %s "
            "&& echo 'Bilgisayar %d saniye sonra: systemctl %s'; "
            "else shutdown %s +0; fi" % (seconds, verb, seconds, verb, "-r" if verb == "reboot" else "-h"))


def _env() -> Dict[str, str]:
    env = {"PATH": SAFE_PATH, "HOME": "/root", "LANG": "C.UTF-8", "LC_ALL": "C.UTF-8", "SHELL": "/bin/sh",
           "USER": "root", "LOGNAME": "root", "DEBIAN_FRONTEND": "noninteractive", "TERM": "dumb"}
    if os.geteuid() != 0:
        # Testler ve geliştirme: root değilken kendi kullanıcısının ev klasörü
        env["HOME"] = os.environ.get("HOME", "/tmp")
        env["USER"] = env["LOGNAME"] = os.environ.get("USER", "")
    return env


class CommandRunner:
    def __init__(self, shell: str = "/bin/sh", max_duration: float = MAX_DURATION, max_chars: int = MAX_OUTPUT_CHARS):
        self.shell = shell
        self.max_duration = max_duration
        self.max_chars = max_chars
        self._running: Dict[int, asyncio.Event] = {}

    def is_running(self, task_id: int) -> bool:
        return task_id in self._running

    @property
    def running_count(self) -> int:
        return len(self._running)

    def cancel(self, task_id: int) -> bool:
        ev = self._running.get(task_id)
        if ev is None:
            return False
        ev.set()
        return True

    async def run(self, task_id: int, command: str, stopping: Optional[asyncio.Event] = None) -> Result:
        """Görev kimliği çağrı anında ayrılır: aynı kimlik zaten çalışıyorsa ikinci işlem başlatılmaz."""
        if task_id in self._running:
            return Result("[YİNELENEN]: Görev %d zaten çalışıyor; ikinci kez başlatılmadı." % task_id,
                          EXIT_DUPLICATE, 0)
        cancel = asyncio.Event()
        self._running[task_id] = cancel
        try:
            return await self._run(task_id, command, cancel, stopping or asyncio.Event())
        finally:
            if self._running.get(task_id) is cancel:
                del self._running[task_id]

    async def _run(self, task_id: int, command: str, cancel: asyncio.Event, stopping: asyncio.Event) -> Result:
        started = time.monotonic()
        actual = translate_power_command(command) or command
        try:
            proc = await asyncio.create_subprocess_exec(
                self.shell, "-c", actual,
                stdin=asyncio.subprocess.DEVNULL, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                env=_env(), cwd="/", start_new_session=True, close_fds=True,
            )
        except Exception as exc:   # noqa: BLE001 - her başlatma hatası görev sonucu olur
            return Result("Ajan Hatası: %s" % exc, EXIT_AGENT_ERROR, time.monotonic() - started)
        stdout = BoundedOutput(self.max_chars)
        stderr = BoundedOutput(self.max_chars // 4)
        pumps = asyncio.ensure_future(asyncio.gather(_pump(proc.stdout, stdout), _pump(proc.stderr, stderr)))
        waiter = asyncio.ensure_future(proc.wait())
        cancel_w = asyncio.ensure_future(cancel.wait())
        stop_w = asyncio.ensure_future(stopping.wait())
        try:
            done, _ = await asyncio.wait({waiter, cancel_w, stop_w}, timeout=self.max_duration,
                                         return_when=asyncio.FIRST_COMPLETED)
        finally:
            for f in (cancel_w, stop_w):
                f.cancel()
        if waiter not in done:
            _kill_group(proc)
            try:
                await asyncio.wait_for(waiter, 10)
            except asyncio.TimeoutError:
                pass
            await _drain(pumps, task_id)
            if cancel.is_set():
                reason, code = "[İPTAL EDİLDİ]: Görev panelden iptal edildi; işlem sonlandırıldı.", EXIT_CANCELLED
            elif stopping.is_set():
                reason = "[DURDURULDU]: POps Agent servisi durduğu için işlem sonlandırıldı."
                code = EXIT_SERVICE_STOPPING
            else:
                minutes = round(self.max_duration / 60)
                reason = "[HATA]: İşlem %d dakikadan uzun sürdüğü için zorla sonlandırıldı." % minutes
                code = EXIT_TIMEOUT
            partial = stdout.text().strip()
            return Result(reason + ("\n[ÇIKTI]:\n" + partial if partial else ""), code, time.monotonic() - started)
        await _drain(pumps, task_id)
        rc = proc.returncode
        exit_code = 128 - rc if rc is not None and rc < 0 else (rc if rc is not None else EXIT_AGENT_ERROR)
        out = stdout.text().strip()
        err = stderr.text().strip()
        if exit_code != 0 and err:
            output = "[ÇIKIŞ KODU: %d]\n[HATA]:\n%s\n[ÇIKTI]:\n%s" % (exit_code, err, out)
        elif not out:
            output = "Komut çalıştı (Çıkış: %d) ancak çıktı üretilmedi." % exit_code
        else:
            output = out
        return Result(output, exit_code, time.monotonic() - started)


def _kill_group(proc) -> None:
    try:
        os.killpg(proc.pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        try:
            proc.kill()
        except ProcessLookupError:
            pass


async def _pump(stream, sink: BoundedOutput) -> None:
    decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
    try:
        while True:
            chunk = await stream.read(READ_CHUNK)
            if not chunk:
                break
            sink.append(decoder.decode(chunk).replace("\r\n", "\n"))
        sink.append(decoder.decode(b"", final=True))
    except (OSError, ValueError):
        pass


async def _drain(pumps, task_id: int) -> None:
    """İşlem bitti: akışın kapanması beklenir, ama akışı açık tutan bir alt süreç sonucu bekletemez."""
    try:
        await asyncio.wait_for(asyncio.shield(pumps), STREAM_DRAIN_TIMEOUT)
    except asyncio.TimeoutError:
        log.warning("Görev %d: işlem bitti ama çıktı akışı %d sn içinde kapanmadı (açık kalan bir alt süreç); okunan "
                    "çıktıyla devam ediliyor.", task_id, STREAM_DRAIN_TIMEOUT)
        pumps.cancel()
