"""Ajanın dosyaları. Kurulu pakette sabit yollar; testler ve geliştirme için klasörler komut satırından verilir.

  /usr/lib/pops-agent/         kod (paket)
  /etc/pops-agent/             agent.conf (0600: SERVER_URL, ENROLL_TOKEN, SERVER_CA_CERT), capabilities.conf
  /var/lib/pops-agent/         kimlik, cihaz anahtarı, onay bekleyen sonuçlar, güncelleme durumu (0700)
  /var/log/pops-agent/         agent.log (günlük döner) ve audit.log (yerel denetim izi)
"""

import os

LIB_DIR = "/usr/lib/pops-agent"
CONFIG_DIR = "/etc/pops-agent"
STATE_DIR = "/var/lib/pops-agent"
LOG_DIR = "/var/log/pops-agent"


class Paths:
    def __init__(self, config_dir: str = CONFIG_DIR, state_dir: str = STATE_DIR, log_dir: str = LOG_DIR):
        self.config_dir = config_dir
        self.state_dir = state_dir
        self.log_dir = log_dir

    def _s(self, name: str) -> str:
        return os.path.join(self.state_dir, name)

    @property
    def config_file(self) -> str:
        return os.path.join(self.config_dir, "agent.conf")

    @property
    def capabilities_file(self) -> str:
        return os.path.join(self.config_dir, "capabilities.conf")

    # ── durum (0700) ──
    @property
    def identity(self) -> str:
        return self._s("identity")

    @property
    def secret(self) -> str:
        return self._s("secret.json")

    @property
    def binding(self) -> str:
        return self._s("hw.bind.json")

    @property
    def results(self) -> str:
        return self._s("pending-results.json")

    @property
    def capability_state(self) -> str:
        return self._s("capabilities.state.json")

    @property
    def bypass_key(self) -> str:
        return self._s("bypass.key")

    @property
    def session(self) -> str:
        return self._s("session.json")

    @property
    def software_gate(self) -> str:
        return self._s("software.json")

    @property
    def health(self) -> str:
        return self._s("health.json")

    @property
    def update_lock(self) -> str:
        return self._s("update.lock")

    @property
    def update_result(self) -> str:
        return self._s("update-result.json")

    @property
    def update_result_reported(self) -> str:
        return self._s("update-result.reported.json")

    @property
    def rollback_drill(self) -> str:
        return self._s("rollback-drill")

    @property
    def rollback_drill_consumed(self) -> str:
        return self._s("rollback-drill.consumed")

    @property
    def packages_dir(self) -> str:
        return self._s("packages")

    @property
    def updater_dir(self) -> str:
        return self._s("updater")

    # ── günlükler ──
    @property
    def agent_log(self) -> str:
        return os.path.join(self.log_dir, "agent.log")

    @property
    def audit_log(self) -> str:
        return os.path.join(self.log_dir, "audit.log")

    @property
    def updater_log(self) -> str:
        return os.path.join(self.log_dir, "updater.log")
