"""İmzalı .deb ile kendini güncelleme, ajan tarafı (Windows AgentUpdate ile aynı adımlar).

  1. Sunucu "update_agent" emrinde imzalı manifest'i gönderir: "manifest" (base64) ve "manifest_sig".
  2. İmza koddaki ed25519 anahtarla doğrulanır; sürüm kurulu sürümden büyük olmalıdır (sürüm düşürme yok).
  3. Paketin adı, boyutu ve SHA-256'sı yalnızca imzalı manifest'ten alınır (pops-agent_<sürüm>_all.deb, tek).
     Dosya yalnızca bu ajanın bağlı olduğu sunucunun /updates/ dizininden indirilir; boyut ya da özet tutmazsa
     uygulanmaz. Doğrulanan paket /var/lib/pops-agent/packages/'a konur.
  4. Geri dönüş paketi hazırlanır: kurulu sürümün .deb'i packages/'ta yoksa kurulu dosyalardan (dpkg --verify ile
     denetlenerek) dpkg-deb ile yeniden üretilir.
  5. Kurulumu ajan değil, geçici bir systemd birimi yapar (systemd-run --unit pops-agent-update): paketin postinst'i
     servisi yeniden başlatınca kurulum yarıda kalmaz. Kurucu (updater.py) yeni sürümün health.json'unu 90 sn bekler;
     gelmezse önceki paketi kurar. Sonuç update-result.json'a yazılır; yeni ajan onu "update_result" olarak gönderir
     ve sunucu onaylayana kadar saklar.
Doğrulamaların hepsi geçmeden hiçbir şey kurulmaz.
"""

import base64
import functools
import glob
import hashlib
import hmac
import json
import logging
import os
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from typing import Callable, Dict, Optional

from pops_agent import release, store

log = logging.getLogger("pops.update")

STALE_LOCK_SECONDS = 15 * 60
UPDATER_NAME = "pops-agent-updater.py"
UNIT_NAME = "pops-agent-update"
_ENV = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C"}


class UpdateRejected(Exception):
    pass


def result_id(raw: bytes) -> str:
    return hashlib.sha256(raw or b"").hexdigest()[:32]


class UpdateManager:
    def __init__(self, paths, installed_version: str, http_factory: Callable, audit: Callable,
                 run: Callable = subprocess.run, systemd_available: Optional[Callable[[], bool]] = None):
        self.paths = paths
        self.installed_version = installed_version
        self.http_factory = http_factory
        self.audit = audit
        self.run = run
        self.systemd_available = systemd_available or (lambda: os.path.isdir("/run/systemd/system")
                                                       and shutil.which("systemd-run") is not None)
        self._busy = threading.Lock()
        self._audited_result = None

    # ── update_agent ──
    def handle(self, command: dict, progress: Optional[Callable] = None) -> Optional[str]:
        """Dönen: başlatılan kurucunun paket yolu; reddedilirse None (neden loglanır ve denetime yazılır).
        progress(stage, to_version=None, detail=None): sunucuya update_progress adımı (Windows ajanıyla aynı adlar:
        received, downloaded, verified, updater_started, rejected, ignored_busy)."""
        step = progress or (lambda *a, **k: None)
        if not self._busy.acquire(blocking=False):
            log.info("Güncelleme zaten hazırlanıyor; yeni emir yok sayıldı.")
            step("ignored_busy")
            return None
        package = None
        launched = False
        to_version = None
        try:
            if self.lock_fresh():
                log.info("Başka bir güncelleme sürüyor (update.lock); emir yok sayıldı.")
                step("ignored_busy")
                return None
            step("received")
            manifest, deb = self.verify_command(command)
            to_version = manifest.version
            if not self.systemd_available():
                raise UpdateRejected("systemd yok: kurulum geçici bir systemd biriminde yapılır")
            log.warning("[GÜVENLİK] İmzalı manifest doğrulandı: %s -> %s (%s, %s).", self.installed_version,
                        manifest.version, deb.name, manifest.tag)
            store.ensure_dir(self.paths.packages_dir, 0o700)
            package = os.path.join(self.paths.packages_dir, deb.name)
            self.download_verified(deb, package, lambda: step("downloaded", to_version=to_version))
            step("verified", to_version=to_version)
            rollback = self.rollback_package()
            launched = self.launch(package, deb.sha256, manifest.version, rollback)
            if launched:
                self.audit("update_started", from_version=self.installed_version, to_version=manifest.version,
                           package=deb.name, rollback_package=os.path.basename(rollback) if rollback else None)
                step("updater_started", to_version=to_version)
            return package if launched else None
        except UpdateRejected as exc:
            log.error("[GÜVENLİK] Güncelleme reddedildi: %s.", exc)
            self.audit("update_rejected", reason=str(exc)[:300])
            step("rejected", to_version=to_version, detail=str(exc)[:300])
            return None
        except Exception as exc:   # noqa: BLE001 - beklenmeyen hata emri düşürür, ajanı değil
            log.exception("Güncelleme hazırlanamadı: %s", exc)
            return None
        finally:
            if not launched and package and os.path.basename(package) != release.deb_name(self.installed_version):
                store.delete(package)
            self._busy.release()

    def verify_command(self, command: dict):
        manifest_b64, sig = command.get("manifest"), command.get("manifest_sig")
        if not isinstance(manifest_b64, str) or not isinstance(sig, str):
            raise UpdateRejected("emirde imzalı manifest yok; imzasız paket uygulanmaz")
        try:
            raw = base64.b64decode(manifest_b64, validate=True)
        except ValueError:
            raise UpdateRejected("manifest base64 değil")
        if not release.verify_signature(raw, sig):
            raise UpdateRejected("manifest imzası geçersiz (kurcalanmış ya da başka anahtarla imzalanmış)")
        try:
            manifest = release.parse(raw)
            if release.compare_versions(manifest.version, self.installed_version) <= 0:
                raise UpdateRejected("manifest sürümü (%s) kurulu sürümden (%s) yeni değil"
                                     % (manifest.version, self.installed_version))
            deb = release.select_deb(manifest)
        except (release.ManifestError, ValueError) as exc:
            raise UpdateRejected(str(exc))
        return manifest, deb

    def download_verified(self, deb, path: str, downloaded: Optional[Callable] = None) -> None:
        partial = path + ".partial"
        store.delete(partial)
        digest = hashlib.sha256()
        fd = os.open(partial, os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0), 0o600)
        try:
            with os.fdopen(fd, "wb") as out:
                def sink(chunk):
                    digest.update(chunk)
                    out.write(chunk)
                client = self.http_factory()
                log.info("İndiriliyor: /updates/%s", deb.name)
                status, total = client.download("/updates/" + deb.name, sink, deb.size)
        except Exception:
            store.delete(partial)
            raise
        if status != 200:
            store.delete(partial)
            raise UpdateRejected("paket indirilemedi: HTTP %s" % status)
        if downloaded:
            downloaded()
        actual = digest.hexdigest()
        if total != deb.size or not hmac.compare_digest(actual, deb.sha256):
            store.delete(partial)
            raise UpdateRejected("indirilen paket manifest'le uyuşmuyor (boyut %d/%d, SHA-256 %s, beklenen %s)"
                                 % (total, deb.size, actual, deb.sha256))
        os.replace(partial, path)
        log.warning("[GÜVENLİK] Paket boyutu ve SHA-256'sı imzalı manifest'le eşleşti.")

    # ── Geri dönüş paketi ──
    def rollback_package(self) -> Optional[str]:
        """Kurulu sürümün .deb'i: packages/'ta varsa o, yoksa kurulu dosyalardan yeniden üretilir."""
        path = os.path.join(self.paths.packages_dir, release.deb_name(self.installed_version))
        if os.path.isfile(path):
            return path
        try:
            return self.repack_installed(path)
        except Exception as exc:   # noqa: BLE001
            log.error("Geri dönüş paketi hazırlanamadı (%s); güncelleme geri dönüşsüz yapılacak.", exc)
            return None

    def _q(self, *args) -> str:
        proc = self.run(list(args), stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=120, check=False,
                        env=_ENV)
        if proc.returncode != 0:
            raise OSError("%s: %s" % (" ".join(args[:2]), proc.stderr.decode("utf-8", "replace").strip()[:200]))
        return proc.stdout.decode("utf-8", "replace")

    def repack_installed(self, out_path: str) -> str:
        status = self._q("dpkg-query", "-W", "-f", "${Version}\t${db:Status-Abbrev}", "pops-agent").strip()
        version, _, abbrev = status.partition("\t")
        if not abbrev.startswith("ii") or release.from_deb_version(version) != self.installed_version:
            raise OSError("kurulu paket %r, çalışan sürüm %s" % (status, self.installed_version))
        verify = self.run(["dpkg", "--verify", "pops-agent"], stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                          timeout=120, check=False, env=_ENV)
        # Yerelde değiştirilmiş ayar dosyası (conffile, "c") sorun değildir; kod dosyaları paketle aynı olmalı
        changed = [line for line in verify.stdout.decode("utf-8", "replace").splitlines()
                   if line.strip() and " c " not in line]
        if changed:
            raise OSError("kurulu dosyalar paketle uyuşmuyor: %s" % "; ".join(changed[:3]))
        control = self._q("dpkg-query", "-s", "pops-agent")
        files = self._q("dpkg-query", "-L", "pops-agent").splitlines()
        tmp = tempfile.mkdtemp(prefix="repack-", dir=self.paths.packages_dir)
        try:
            debian = os.path.join(tmp, "DEBIAN")
            os.makedirs(debian, 0o755)
            with open(os.path.join(debian, "control"), "w", encoding="utf-8") as f:
                f.write(control_for_repack(control))
            for name in ("preinst", "postinst", "prerm", "postrm", "conffiles", "md5sums"):
                src = "/var/lib/dpkg/info/pops-agent.%s" % name
                if os.path.isfile(src):
                    shutil.copy2(src, os.path.join(debian, name))
            for path in files:
                if not path.startswith("/") or path == "/." or os.path.isdir(path) or not os.path.lexists(path):
                    continue
                dest = os.path.join(tmp, path.lstrip("/"))
                os.makedirs(os.path.dirname(dest), 0o755, exist_ok=True)
                shutil.copy2(path, dest, follow_symlinks=False)
            # Yarım kalan paket asıl adla kalmasın: önce geçici ada, sonra rename
            partial = out_path + ".partial"
            store.delete(partial)
            self._q("dpkg-deb", "--root-owner-group", "--build", tmp, partial)
            os.replace(partial, out_path)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)
        log.info("Geri dönüş paketi kurulu sürümden üretildi: %s", out_path)
        return out_path

    # ── Kurucu ──
    def launch(self, package: str, sha256: str, to_version: str, rollback: Optional[str]) -> bool:
        store.ensure_dir(self.paths.updater_dir, 0o700)
        updater = os.path.join(self.paths.updater_dir, UPDATER_NAME)
        src = os.path.join(os.path.dirname(os.path.abspath(__file__)), "updater.py")
        store.write_atomic(updater, store.read_bytes(src), 0o700)
        store.write_json(self.paths.update_lock, {"from_version": self.installed_version, "to_version": to_version,
                                                  "started_at": int(time.time())})
        args = ["systemd-run", "--unit", UNIT_NAME, "--collect", "--quiet",
                "--description", "POps agent update %s -> %s" % (self.installed_version, to_version),
                "--property", "TimeoutStopSec=300",
                sys.executable, "-I", "-B", updater, "--deb", package, "--sha256", sha256,
                "--from", self.installed_version, "--to", to_version, "--state-dir", self.paths.state_dir,
                "--log", self.paths.updater_log]
        if rollback:
            args += ["--rollback-deb", rollback]
        proc = self.run(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, check=False, env=_ENV)
        if proc.returncode != 0:
            store.delete(self.paths.update_lock)
            raise UpdateRejected("kurucu başlatılamadı: systemd-run %d: %s"
                                 % (proc.returncode, proc.stderr.decode("utf-8", "replace").strip()[:200]))
        log.info("Kurucu %s biriminde başlatıldı; servis kurulum sırasında yeni sürümle yeniden başlayacak.", UNIT_NAME)
        return True

    def lock_fresh(self) -> bool:
        try:
            return time.time() - os.stat(self.paths.update_lock).st_mtime < STALE_LOCK_SECONDS
        except OSError:
            return False

    # ── Sonuç (update-result.json) ──
    def pending_result_message(self) -> Optional[Dict]:
        raw = store.read_bytes(self.paths.update_result)
        if raw is None:
            return None
        try:
            result = json.loads(raw.decode("utf-8-sig"))
        except (ValueError, UnicodeDecodeError):
            return None
        if not isinstance(result, dict):
            return None
        msg = {"type": "update_result", "status": result.get("outcome") if isinstance(result.get("outcome"), str)
               else "unknown", "result_id": result_id(raw)}
        for key in ("from_version", "to_version", "running_version", "detail", "rollback", "agent_state"):
            if isinstance(result.get(key), str):
                msg[key] = result[key]
        if isinstance(result.get("reboot_required"), bool):
            msg["reboot_required"] = result["reboot_required"]
        key = json.dumps(msg, sort_keys=True)
        if key != self._audited_result:
            self._audited_result = key
            self.audit("update_result", from_version=msg.get("from_version"), to_version=msg.get("to_version"),
                       outcome=msg.get("status"), rollback=msg.get("rollback"))
        return msg

    def pending_result_id(self) -> Optional[str]:
        raw = store.read_bytes(self.paths.update_result)
        return result_id(raw) if raw is not None else None

    def mark_reported(self) -> None:
        try:
            os.replace(self.paths.update_result, self.paths.update_result_reported)
        except OSError as exc:
            log.error("update-result.json kenara alınamadı: %s", exc)

    # ── health.json: kurucu yeni sürümün açıldığını buradan anlar ──
    def startup_skip_health(self) -> bool:
        """Geri dönüş tatbikatı (Windows ApplyRollbackDrillOnStartup): yönetici rollback-drill dosyasını koyarsa,
        güncellemeyle kurulan yeni sürüm health.json yazmaz; kurucu onu sağlıksız sayıp önceki pakete döner."""
        try:
            run = store.read_json(self.paths.update_lock) if self.lock_fresh() else None
        except ValueError:
            run = None
        try:
            consumed = store.read_json(self.paths.rollback_drill_consumed)
        except ValueError:
            consumed = None
        marker = os.path.exists(self.paths.rollback_drill)
        decision = decide_drill(marker, consumed if isinstance(consumed, dict) else None,
                                run if isinstance(run, dict) else None, self.installed_version)
        if decision["discard_consumed"]:
            store.delete(self.paths.rollback_drill_consumed)
        if decision["consume"]:
            store.write_json(self.paths.rollback_drill_consumed, {"version": self.installed_version,
                                                                  "update_started_at": run.get("started_at")})
            store.delete(self.paths.rollback_drill)
            log.warning("[TATBİKAT] rollback-drill işareti tüketildi; bu sürüm sağlık bildirmeyecek.")
        return decision["skip_health"]

    def write_health(self, checks: Dict[str, bool]) -> None:
        try:
            store.write_json(self.paths.health, {"version": self.installed_version, "ts": int(time.time()),
                                                 "pid": os.getpid(), "phase": "operational", "checks": checks})
        except OSError as exc:
            log.error("health.json yazılamadı: %s", exc)


# dpkg-query -s'nin yalnızca kurulu paket veritabanına ait alanları (dpkg-repack gibi atılır; devam satırlarıyla)
_STATUS_FIELDS = ("Status", "Conffiles", "Config-Version")


def control_for_repack(status_text: str) -> str:
    out, skip = [], False
    for line in status_text.splitlines():
        if line[:1] in (" ", "\t"):
            if not skip:
                out.append(line)
            continue
        skip = line.split(":", 1)[0] in _STATUS_FIELDS
        if not skip and line.strip():
            out.append(line)
    return "\n".join(out).strip() + "\n"


def decide_drill(marker: bool, consumed: Optional[dict], run: Optional[dict], own_version: str) -> Dict[str, bool]:
    decision = {"skip_health": False, "consume": False, "discard_consumed": False}
    updating_to_me = bool(run) and release.same_version(run.get("to_version"), own_version)
    if consumed is not None:
        if updating_to_me and release.same_version(consumed.get("version"), own_version) \
                and consumed.get("update_started_at") == run.get("started_at"):
            decision["skip_health"] = True
            return decision
        decision["discard_consumed"] = True
    if marker and updating_to_me:
        decision["consume"] = True
        decision["skip_health"] = True
    return decision


def prune_packages(packages_dir: str, installed: str) -> None:
    """packages/'ta kurulu sürümün paketi ve ondan eski en yeni paket (bir sonraki geri dönüş için) kalır. Kurulu
    sürümden yeni paketler (geri alınmış, sağlıksız bir güncelleme) ve daha eskiler silinir."""
    older = []
    for path in glob.glob(os.path.join(packages_dir, "pops-agent_*_all.deb")):
        version = os.path.basename(path)[len("pops-agent_"):-len("_all.deb")]
        if not release.parse_semver(version) or release.parse_semver(installed) is None:
            continue
        order = release.compare_versions(version, installed)
        if order < 0:
            older.append((version, path))
        elif order > 0:
            store.delete(path)
    older.sort(key=functools.cmp_to_key(lambda a, b: release.compare_versions(a[0], b[0])), reverse=True)
    for _version, path in older[1:]:
        store.delete(path)
