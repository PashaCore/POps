#!/usr/bin/python3
"""POps Linux ajanı kurucusu (Windows POpsUpdater'ın karşılığı). Yalnızca standart kütüphane: ajan bu dosyayı
/var/lib/pops-agent/updater/'a kopyalar ve geçici bir systemd biriminde çalıştırır (systemd-run --unit
pops-agent-update), böylece paketin postinst'i servisi yeniden başlatınca kurulum yarıda kalmaz.

  pops-agent-updater.py --deb <paket> --sha256 <özet> --from <kurulu> --to <yeni> --state-dir <dizin>
                        [--rollback-deb <önceki paket>] [--log <dosya>]

Adımlar: kilit, özet, dpkg -i (yerel ayar dosyaları korunur), yeni sürümün health.json'unu bekleme (90 sn).
dpkg başarısız olur ya da yeni sürüm sağlıklı açılmazsa önceki paket kurulur ve onun sağlığı beklenir. Her durumda
ajanın kurulu ve çalışır olduğu denetlenir; yoksa son çare olarak elde kalan paket kurulur. Sonuç
update-result.json'a yazılır (Windows ile aynı şema: outcome, rollback, detail, agent_state, running_version).
"""

import argparse
import hashlib
import hmac
import json
import os
import subprocess
import sys
import time

SERVICE = "pops-agent"
PACKAGE = "pops-agent"
HEALTH_TIMEOUT = 90.0
ENV = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "DEBIAN_FRONTEND": "noninteractive"}


class Updater:
    def __init__(self, opt, run=subprocess.run, sleep=time.sleep, clock=time.time, health_timeout=HEALTH_TIMEOUT):
        self.opt = opt
        self.run = run
        self.sleep = sleep
        self.clock = clock
        self.health_timeout = health_timeout
        self.state = opt.state_dir

    def path(self, name):
        return os.path.join(self.state, name)

    def log(self, message, error=False):
        line = "%s %s updater: %s" % (time.strftime("%Y-%m-%d %H:%M:%S"), "ERROR" if error else "INFO", message)
        print(line, file=sys.stderr, flush=True)
        if self.opt.log:
            try:
                fd = os.open(self.opt.log, os.O_WRONLY | os.O_APPEND | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o640)
                with os.fdopen(fd, "a", encoding="utf-8") as f:
                    f.write(line + "\n")
            except OSError:
                pass

    def sh(self, args, timeout=900):
        try:
            proc = self.run(args, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout, check=False,
                            env=ENV, stdin=subprocess.DEVNULL)
        except (OSError, subprocess.TimeoutExpired) as exc:
            return 127, str(exc)
        return proc.returncode, (proc.stdout or b"").decode("utf-8", "replace")

    def write_json(self, path, value):
        tmp = path + ".tmp"
        fd = os.open(tmp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | getattr(os, "O_NOFOLLOW", 0), 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as f:
            json.dump(value, f, ensure_ascii=False, sort_keys=True)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, path)

    def hash_matches(self, path, expected):
        h = hashlib.sha256()
        try:
            with open(path, "rb") as f:
                for chunk in iter(lambda: f.read(1024 * 1024), b""):
                    h.update(chunk)
        except OSError:
            return False
        return hmac.compare_digest(h.hexdigest(), (expected or "").lower())

    def installed(self):
        """(Debian sürümü, durum kısaltması) ya da (None, None)."""
        rc, out = self.sh(["dpkg-query", "-W", "-f", "${Version}\t${db:Status-Abbrev}", PACKAGE], 60)
        if rc != 0 or "\t" not in out:
            return None, None
        version, abbrev = out.strip("\n").split("\t", 1)
        return version.strip(), abbrev

    @staticmethod
    def pops_version(deb_version):
        return (deb_version or "").replace("~", "-", 1)

    def dpkg_install(self, deb):
        rc, out = self.sh(["dpkg", "--force-confdef", "--force-confold", "-i", deb])
        tail = " | ".join(line.strip() for line in out.strip().splitlines()[-4:])
        self.log("dpkg -i %s -> %d %s" % (os.path.basename(deb), rc, tail), rc != 0)
        return rc, tail

    def read_health(self):
        try:
            with open(self.path("health.json"), encoding="utf-8") as f:
                data = json.load(f)
            return data if isinstance(data, dict) else None
        except (OSError, ValueError):
            return None

    def wait_health(self, version, since):
        deadline = self.clock() + self.health_timeout
        want = (version or "").strip().lstrip("vV")
        while self.clock() < deadline:
            h = self.read_health()
            if h and str(h.get("version", "")).strip().lstrip("vV") == want and h.get("phase") == "operational" \
                    and isinstance(h.get("ts"), (int, float)) and h["ts"] >= since - 2:
                return True
            self.sleep(2)
        return False

    def clear_drill(self, when):
        removed = 0
        for name in ("rollback-drill", "rollback-drill.consumed"):
            try:
                os.unlink(self.path(name))
                removed += 1
            except FileNotFoundError:
                pass
            except OSError as exc:
                self.log("%s silinemedi: %s" % (name, exc), True)
        if removed:
            self.log("[TATBİKAT] rollback-drill işaretleri silindi (%s)." % when)

    def rollback(self, start):
        opt = self.opt
        if not opt.rollback_deb or not os.path.isfile(opt.rollback_deb):
            return "rollback_failed", "none", "%s sağlıklı kurulamadı ve geri dönülecek önceki paket yok" % opt.to
        rc, tail = self.dpkg_install(opt.rollback_deb)
        if rc == 0 and self.wait_health(opt.from_version, start):
            return ("rolled_back", "deb",
                    "%s sağlıklı açılmadı; önceki paket (%s) geri kuruldu" % (opt.to, opt.from_version))
        if rc != 0:
            return "rollback_failed", "deb", "önceki paket kurulamadı: dpkg %d %s" % (rc, tail)
        return "rollback_failed", "deb", "önceki paket kuruldu ama %d sn içinde sağlıklı açılmadı" % self.health_timeout

    def ensure_agent_present(self):
        """Ajan kurulu ve çalışıyor mu; kurulu değilse son çare elde kalan paket kurulur."""
        version, abbrev = self.installed()
        if version and abbrev and abbrev.startswith("ii"):
            rc, out = self.sh(["systemctl", "is-active", SERVICE], 30)
            if out.strip() != "active":
                self.sh(["systemctl", "restart", SERVICE], 60)
                rc, out = self.sh(["systemctl", "is-active", SERVICE], 30)
            return "running" if out.strip() == "active" else "not_running"
        self.log("[KRİTİK] %s paketi kurulu değil ya da yarım (%s); son çare kurulum deneniyor."
                 % (PACKAGE, abbrev), True)
        for deb in (self.opt.rollback_deb, self.opt.deb):
            if deb and os.path.isfile(deb) and self.dpkg_install(deb)[0] == 0:
                self.log("[KRİTİK] Ajan son çare kurulumla geri getirildi (%s)." % os.path.basename(deb), True)
                return "reinstalled"
        self.log("[KRİTİK] Kurulabilecek paket yok; cihaz yönetimsiz kaldı, elle kurulum gerekiyor.", True)
        return "unmanaged"

    def main(self):
        opt = self.opt
        result = {"schema": "pops-update-result/1", "from_version": opt.from_version, "to_version": opt.to,
                  "started_at": int(self.clock()), "rollback": "none", "reboot_required": False}
        outcome = "error"
        try:
            self.log("Güncelleme başladı: %s -> %s (%s)" % (opt.from_version, opt.to, os.path.basename(opt.deb)))
            if os.path.exists(self.path("update.lock")):
                os.utime(self.path("update.lock"), None)   # kilit taze kalsın (15 dk kuralı)
            if not self.hash_matches(opt.deb, opt.sha256):
                outcome = "rejected"
                result["detail"] = "paketin SHA-256'sı ajanın doğruladığı özetle uyuşmuyor"
                return 1
            start = self.clock()
            rc, tail = self.dpkg_install(opt.deb)
            if rc != 0:
                version, abbrev = self.installed()
                if abbrev and abbrev.startswith("ii") and self.pops_version(version) == opt.from_version:
                    outcome = "install_failed"
                    result["detail"] = "dpkg %d: %s; kurulum başlamadı, kurulu sürüm (%s) değişmedi" % (
                        rc, tail, opt.from_version)
                    return 1
                self.log("Kurulum yarıda kaldı (%s %s); geri dönülüyor." % (version, abbrev), True)
                self.clear_drill("geri kurulumdan önce")
                outcome, result["rollback"], detail = self.rollback(start)
                result["detail"] = "dpkg %d: %s. %s" % (rc, tail, detail)
            elif self.wait_health(opt.to, start):
                outcome = "success"
            else:
                self.log("Yeni sürüm %d sn içinde sağlıklı açılmadı; geri dönülüyor." % self.health_timeout, True)
                self.clear_drill("geri kurulumdan önce")
                outcome, result["rollback"], result["detail"] = self.rollback(start)
            return 0 if outcome == "success" else 1
        except Exception as exc:   # noqa: BLE001 - sonuç her durumda yazılır
            result["detail"] = str(exc)[:300]
            self.log("Güncelleme hatası: %r" % exc, True)
            return 1
        finally:
            result["agent_state"] = self.ensure_agent_present()
            h = self.read_health()
            version, _ = self.installed()
            result["running_version"] = str(h.get("version")) if h and h.get("version") else self.pops_version(version)
            result["outcome"] = outcome
            self.clear_drill("güncelleme sonunda")
            result["finished_at"] = int(self.clock())
            try:
                self.write_json(self.path("update-result.json"), result)
            except OSError as exc:
                self.log("update-result.json yazılamadı: %s" % exc, True)
            self.log("Güncelleme bitti: %s %s" % (outcome, json.dumps(result, ensure_ascii=False)),
                     outcome != "success")
            try:
                os.unlink(self.path("update.lock"))
            except OSError:
                pass


def parse_args(argv):
    p = argparse.ArgumentParser(prog="pops-agent-updater")
    p.add_argument("--deb", required=True)
    p.add_argument("--sha256", required=True)
    p.add_argument("--from", dest="from_version", required=True)
    p.add_argument("--to", required=True)
    p.add_argument("--state-dir", required=True)
    p.add_argument("--rollback-deb")
    p.add_argument("--log")
    return p.parse_args(argv)


if __name__ == "__main__":
    sys.exit(Updater(parse_args(sys.argv[1:])).main())
