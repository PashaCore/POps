import base64
import hashlib
import json
import os
import subprocess
import types

import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey

from conftest import write
from pops_agent import paths as paths_mod
from pops_agent import release, update, updater

DEB = b"!<arch>\nsahte-deb-baytlari" * 50


@pytest.fixture
def signer(monkeypatch):
    key = Ed25519PrivateKey.generate()
    raw = key.public_key().public_bytes(serialization.Encoding.Raw, serialization.PublicFormat.Raw)
    real = release.verify_signature
    monkeypatch.setattr(release, "verify_signature",
                        lambda m, s, k=base64.b64encode(raw).decode(): real(m, s, k))
    return key


def command(key, version="0.1.22-alpha", artifacts=None, sig_key=None):
    if artifacts is None:
        artifacts = [{"name": "POps-Agent-%s-win-x64.msi" % version, "sha256": "a" * 64, "size": 1},
                     {"name": "pops-agent_%s_all.deb" % version, "sha256": hashlib.sha256(DEB).hexdigest(),
                      "size": len(DEB)}]
    body = (json.dumps({"schema": "pops-manifest/1", "version": version, "tag": "v" + version, "released_at": 1,
                        "artifacts": artifacts}, indent=2, sort_keys=True) + "\n").encode()
    sig = base64.b64encode((sig_key or key).sign(body)).decode()
    return {"action": "update_agent", "manifest": base64.b64encode(body).decode(), "manifest_sig": sig}


class FakeHttp:
    def __init__(self, data=DEB, status=200):
        self.data, self.status, self.paths = data, status, []

    def download(self, path, out, max_bytes, timeout=900):
        self.paths.append(path)
        if self.status != 200:
            return self.status, 0
        total = 0
        for i in range(0, len(self.data), 100):
            chunk = self.data[i:i + 100]
            total += len(chunk)
            if total > max_bytes:
                break
            out(chunk)
        return 200, total


class Runs:
    def __init__(self, rc=0):
        self.calls, self.rc = [], rc

    def __call__(self, args, **kw):
        self.calls.append(list(args))
        return types.SimpleNamespace(returncode=self.rc, stdout=b"", stderr=b"hata" if self.rc else b"")


def manager(tmp_path, http=None, runs=None, installed="0.1.21-alpha"):
    p = paths_mod.Paths(str(tmp_path / "etc"), str(tmp_path / "state"), str(tmp_path / "log"))
    os.makedirs(p.state_dir)
    os.makedirs(p.log_dir)
    audits = []
    m = update.UpdateManager(p, installed, lambda: http or FakeHttp(), lambda e, **f: audits.append((e, f)),
                             run=runs or Runs(), systemd_available=lambda: True)
    write(os.path.join(p.packages_dir, release.deb_name(installed)), b"onceki")   # geri dönüş paketi hazır
    return m, p, audits


def test_valid_update_launches_transient_unit(tmp_path, signer):
    runs = Runs()
    http = FakeHttp()
    m, p, audits = manager(tmp_path, http, runs)
    stages = []
    package = m.handle(command(signer), lambda stage, **f: stages.append((stage, f.get("to_version"))))
    assert stages == [("received", None), ("downloaded", "0.1.22-alpha"), ("verified", "0.1.22-alpha"),
                      ("updater_started", "0.1.22-alpha")]
    assert package == os.path.join(p.packages_dir, "pops-agent_0.1.22-alpha_all.deb")
    assert open(package, "rb").read() == DEB and http.paths == ["/updates/pops-agent_0.1.22-alpha_all.deb"]
    args = runs.calls[-1]
    assert args[:3] == ["systemd-run", "--unit", "pops-agent-update"] and "--collect" in args
    assert args[args.index("--rollback-deb") + 1].endswith("pops-agent_0.1.21-alpha_all.deb")
    assert args[args.index("--to") + 1] == "0.1.22-alpha" and args[args.index("--from") + 1] == "0.1.21-alpha"
    assert os.path.isfile(os.path.join(p.updater_dir, "pops-agent-updater.py"))
    lock = json.load(open(p.update_lock))
    assert lock["to_version"] == "0.1.22-alpha" and audits[-1][0] == "update_started"
    busy = []
    assert m.lock_fresh() and m.handle(command(signer), lambda s, **f: busy.append(s)) is None   # kilit varken
    assert busy == ["ignored_busy"]


@pytest.mark.parametrize("case,reason", [
    ("unsigned", "imzalı manifest yok"),
    ("bad_sig", "imzası geçersiz"),
    ("not_newer", "yeni değil"),
    ("same", "yeni değil"),
    ("no_deb", "0 bulundu"),
    ("two_debs", "2 bulundu"),
    ("bad_size", "boyut"),
    ("bad_hash", "uyuşmuyor"),
    ("http_404", "HTTP 404"),
    ("no_systemd", "systemd yok"),
])
def test_update_rejections(tmp_path, signer, case, reason):
    http = FakeHttp()
    cmd = command(signer)
    sysd = True
    if case == "unsigned":
        cmd = {"action": "update_agent", "manifest": cmd["manifest"]}
    elif case == "bad_sig":
        cmd = command(signer, sig_key=Ed25519PrivateKey.generate())
    elif case == "not_newer":
        cmd = command(signer, version="0.1.20")
    elif case == "same":
        cmd = command(signer, version="0.1.21-alpha")
    elif case == "no_deb":
        cmd = command(signer, artifacts=[{"name": "POps-Agent-0.1.22-alpha-win-x64.msi", "sha256": "a" * 64,
                                          "size": 1}])
    elif case == "two_debs":
        deb = {"name": "pops-agent_0.1.22-alpha_all.deb", "sha256": "a" * 64, "size": 1}
        cmd = command(signer, artifacts=[deb, dict(deb, name="pops-agent_0.1.23-alpha_all.deb")])
    elif case == "bad_size":
        http = FakeHttp(DEB + b"fazla")
    elif case == "bad_hash":
        http = FakeHttp(DEB[:-1] + b"X")
    elif case == "http_404":
        http = FakeHttp(status=404)
    elif case == "no_systemd":
        sysd = False
    runs = Runs()
    m, p, audits = manager(tmp_path, http, runs)
    m.systemd_available = lambda: sysd
    stages = []
    assert m.handle(cmd, lambda stage, **f: stages.append((stage, f))) is None
    assert audits and audits[-1][0] == "update_rejected" and reason in audits[-1][1]["reason"]
    # sunucu reddi update_progress "rejected" olarak alır ve gönderimi kapatır (panel beklemede kalmaz)
    assert stages[0][0] == "received" and stages[-1][0] == "rejected" and reason in stages[-1][1]["detail"]
    assert not any(c[0] == "systemd-run" for c in runs.calls)
    assert not os.path.exists(os.path.join(p.packages_dir, "pops-agent_0.1.22-alpha_all.deb"))
    assert not os.path.exists(p.update_lock)


def test_update_result_message_and_ack(tmp_path):
    m, p, audits = manager(tmp_path)
    assert m.pending_result_message() is None
    raw = json.dumps({"schema": "pops-update-result/1", "outcome": "rolled_back", "from_version": "0.1.21-alpha",
                      "to_version": "0.1.22-alpha", "rollback": "deb", "agent_state": "running",
                      "running_version": "0.1.21-alpha", "detail": "x", "reboot_required": False, "extra": 1})
    write(p.update_result, raw)
    msg = m.pending_result_message()
    assert msg == {"type": "update_result", "status": "rolled_back", "result_id": update.result_id(raw.encode()),
                   "from_version": "0.1.21-alpha", "to_version": "0.1.22-alpha", "running_version": "0.1.21-alpha",
                   "detail": "x", "rollback": "deb", "agent_state": "running", "reboot_required": False}
    assert len(msg["result_id"]) == 32 and m.pending_result_id() == msg["result_id"]
    m.pending_result_message()
    assert [a[0] for a in audits].count("update_result") == 1   # yerel denetim bir kez
    m.mark_reported()
    assert not os.path.exists(p.update_result) and os.path.exists(p.update_result_reported)


def test_repack_control_drops_database_fields():
    status = ("Package: pops-agent\nStatus: install ok installed\nPriority: optional\nVersion: 0.1.21~alpha\n"
              "Conffiles:\n /etc/pops-agent/capabilities.conf 0123abcd\nDescription: POps agent\n second line\n")
    out = update.control_for_repack(status)
    assert "Status" not in out and "Conffiles" not in out and "capabilities.conf" not in out
    assert out == ("Package: pops-agent\nPriority: optional\nVersion: 0.1.21~alpha\nDescription: POps agent\n"
                   " second line\n")


def test_rollback_drill_decision():
    run = {"to_version": "0.1.22-alpha", "started_at": 100}
    expected = {"skip_health": True, "consume": True, "discard_consumed": False}
    assert update.decide_drill(True, None, run, "0.1.22-alpha") == expected
    assert update.decide_drill(True, None, run, "0.1.21-alpha")["skip_health"] is False   # geri kurulan sürüm
    assert update.decide_drill(True, None, None, "0.1.22-alpha")["consume"] is False      # güncelleme yokken
    same = {"version": "0.1.22-alpha", "update_started_at": 100}
    assert update.decide_drill(False, same, run, "0.1.22-alpha")["skip_health"] is True   # aynı çalışmada yeniden
    assert update.decide_drill(False, same, None, "0.1.22-alpha")["discard_consumed"] is True


def test_startup_drill_consumes_marker(tmp_path):
    m, p, _ = manager(tmp_path, installed="0.1.22-alpha")
    write(p.update_lock, json.dumps({"to_version": "0.1.22-alpha", "started_at": 5}))
    write(p.rollback_drill, "")
    assert m.startup_skip_health() is True
    assert not os.path.exists(p.rollback_drill) and os.path.exists(p.rollback_drill_consumed)
    assert m.startup_skip_health() is True   # servis aynı güncellemede yeniden başladı
    os.unlink(p.update_lock)
    assert m.startup_skip_health() is False and not os.path.exists(p.rollback_drill_consumed)
    m.write_health({"identity": True})
    health = json.load(open(p.health))
    assert health["version"] == "0.1.22-alpha" and health["phase"] == "operational"


def test_prune_packages_keeps_installed_and_previous(tmp_path):
    for v in ("0.1.9", "0.1.10", "0.1.21-alpha", "0.1.22-alpha", "0.1.23-alpha"):
        write(str(tmp_path / release.deb_name(v)), b"x")
    write(str(tmp_path / "pops-agent_bozuk_all.deb"), b"x")
    # 0.1.23 kuruldu, sağlıklı açılmadı, 0.1.22'ye dönüldü: 0.1.23 ve eskiler silinir, bir önceki (0.1.21) kalır
    update.prune_packages(str(tmp_path), "0.1.22-alpha")
    assert sorted(os.listdir(str(tmp_path))) == ["pops-agent_0.1.21-alpha_all.deb", "pops-agent_0.1.22-alpha_all.deb",
                                                 "pops-agent_bozuk_all.deb"]


def test_repack_requires_matching_installed_package(tmp_path):
    def fake(args, **kw):
        out = b"0.1.20~alpha\tii " if args[0] == "dpkg-query" else b""
        return types.SimpleNamespace(returncode=0, stdout=out, stderr=b"")

    m, p, _ = manager(tmp_path, runs=fake)
    os.unlink(os.path.join(p.packages_dir, "pops-agent_0.1.21-alpha_all.deb"))
    assert m.rollback_package() is None   # kurulu paket çalışan sürüm değil: üretilmez


# ── Kurucu (updater.py): dpkg ve systemctl sahte ──
class FakeSystem:
    """dpkg -i, dpkg-query ve systemctl taklidi; yeni sürüm açılınca health.json yazar."""

    def __init__(self, state_dir, new_ok=True, new_healthy=True, old_ok=True, unchanged_on_fail=False):
        self.state_dir, self.installed = state_dir, ("0.1.21~alpha", "ii ")
        self.new_ok, self.new_healthy, self.old_ok, self.unchanged = new_ok, new_healthy, old_ok, unchanged_on_fail
        self.calls = []

    def health(self, version):
        write(os.path.join(self.state_dir, "health.json"),
              json.dumps({"version": version, "ts": 10 ** 10, "phase": "operational"}))

    def __call__(self, args, **kw):
        self.calls.append(list(args))
        out, rc = b"", 0
        if args[0] == "dpkg" and "-i" in args:
            deb = args[-1]
            new = "0.1.22" in deb
            if new and not self.new_ok:
                rc, out = 1, b"dpkg: dependency problems"
                if not self.unchanged:
                    self.installed = ("0.1.22~alpha", "iU ")
            elif not new and not self.old_ok:
                rc, out = 2, b"dpkg: error"
                self.installed = ("0.1.21~alpha", "iF ")
            else:
                self.installed = ("0.1.22~alpha" if new else "0.1.21~alpha", "ii ")
                if not new or self.new_healthy:
                    self.health("0.1.22-alpha" if new else "0.1.21-alpha")
        elif args[0] == "dpkg-query":
            out = ("%s\t%s" % self.installed).encode()
        elif args[0] == "systemctl":
            out = b"active\n"
        return types.SimpleNamespace(returncode=rc, stdout=out, stderr=b"")


def run_updater(tmp_path, system, rollback=True, sha=None):
    state = str(tmp_path / "state")
    os.makedirs(state, exist_ok=True)
    deb = write(str(tmp_path / "pops-agent_0.1.22-alpha_all.deb"), DEB)
    old = write(str(tmp_path / "pops-agent_0.1.21-alpha_all.deb"), b"old")
    write(os.path.join(state, "update.lock"), "{}")
    write(os.path.join(state, "rollback-drill"), "")
    argv = ["--deb", deb, "--sha256", sha or hashlib.sha256(DEB).hexdigest(), "--from", "0.1.21-alpha",
            "--to", "0.1.22-alpha", "--state-dir", state, "--log", str(tmp_path / "updater.log")]
    if rollback:
        argv += ["--rollback-deb", old]
    clock = iter(range(0, 10 ** 6, 3))
    u = updater.Updater(updater.parse_args(argv), run=system, sleep=lambda s: None, clock=lambda: next(clock),
                        health_timeout=30)
    rc = u.main()
    result = json.load(open(os.path.join(state, "update-result.json")))
    assert not os.path.exists(os.path.join(state, "update.lock"))
    assert not os.path.exists(os.path.join(state, "rollback-drill"))
    return rc, result


def test_updater_success(tmp_path):
    system = FakeSystem(str(tmp_path / "state"))
    rc, result = run_updater(tmp_path, system)
    assert rc == 0 and result["outcome"] == "success" and result["rollback"] == "none"
    assert result["running_version"] == "0.1.22-alpha" and result["agent_state"] == "running"
    dpkg = [c for c in system.calls if c[0] == "dpkg"][0]
    assert "--force-confold" in dpkg and "--force-confdef" in dpkg


def test_updater_rolls_back_unhealthy_version(tmp_path):
    rc, result = run_updater(tmp_path, FakeSystem(str(tmp_path / "state"), new_healthy=False))
    assert rc == 1 and result["outcome"] == "rolled_back" and result["rollback"] == "deb"
    assert result["running_version"] == "0.1.21-alpha"


def test_updater_rolls_back_half_installed_package(tmp_path):
    rc, result = run_updater(tmp_path, FakeSystem(str(tmp_path / "state"), new_ok=False))
    assert result["outcome"] == "rolled_back" and result["detail"].startswith("dpkg 1:")


def test_updater_install_failed_leaves_machine_unchanged(tmp_path):
    rc, result = run_updater(tmp_path, FakeSystem(str(tmp_path / "state"), new_ok=False, unchanged_on_fail=True))
    assert result["outcome"] == "install_failed" and result["rollback"] == "none"


def test_updater_without_rollback_package(tmp_path):
    rc, result = run_updater(tmp_path, FakeSystem(str(tmp_path / "state"), new_healthy=False), rollback=False)
    assert result["outcome"] == "rollback_failed" and "önceki paket yok" in result["detail"]


def test_updater_rollback_fails_then_last_resort(tmp_path):
    system = FakeSystem(str(tmp_path / "state"), new_healthy=False, old_ok=False)
    rc, result = run_updater(tmp_path, system)
    assert result["outcome"] == "rollback_failed" and result["agent_state"] in ("reinstalled", "unmanaged")


def test_updater_rejects_changed_package(tmp_path):
    system = FakeSystem(str(tmp_path / "state"))
    rc, result = run_updater(tmp_path, system, sha="0" * 64)
    assert result["outcome"] == "rejected" and not any(c[0] == "dpkg" and "-i" in c for c in system.calls)


def test_updater_is_standalone():
    """Kurucu /var/lib/pops-agent/updater'a kopyalanıp paket dışından çalışır: pops_agent'ı içe aktarmamalı."""
    src = open(updater.__file__, encoding="utf-8").read()
    assert "pops_agent" not in src.replace("pops_agent.updater", "")
    out = subprocess.run(["python3", "-I", updater.__file__, "--help"], stdout=subprocess.PIPE, check=False)
    assert out.returncode == 0 and b"--rollback-deb" in out.stdout
