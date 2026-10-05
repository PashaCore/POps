import datetime
import json
import logging
import os
import stat

import pytest

from conftest import write
from pops_agent import audit, capabilities, config, logsetup


def test_parse_config(tmp_path):
    path = write(str(tmp_path / "agent.conf"), "# yorum\nSERVER_URL = 'https://pops.okul.local/'\n"
                                               "ENROLL_TOKEN=abcdefghijklmnop_-12\nSERVER_CA_CERT=/etc/x.pem\n")
    cfg = config.load(path)
    assert cfg.server_url == "https://pops.okul.local" and cfg.ws_base == "wss://pops.okul.local"
    assert cfg.enroll_token == "abcdefghijklmnop_-12" and cfg.ca_file == "/etc/x.pem" and cfg.secure
    write(path, "SERVER_URL=http://10.0.0.5\nENROLL_TOKEN=kısa\n")
    cfg = config.load(path)
    assert not cfg.secure and cfg.enroll_token is None   # biçimi bozuk jeton gönderilmez
    assert config.load(str(tmp_path / "yok")).server_url == ""


@pytest.mark.parametrize("url,ok", [
    ("https://pops.okul.local", True), ("https://10.0.0.5:8443/pops", True), ("http://127.0.0.1:8000", True),
    ("http://localhost", True), ("http://[::1]:8000", True), ("http://10.0.0.5", False),
    ("http://pops.okul.local", False), ("ftp://x", False), ("https://user:pw@x", False), ("", False),
    ("https://", False),
])
def test_tls_only_like_windows(url, ok):
    assert config.is_secure_server_url(url) is ok


def test_forget_enroll_token_keeps_rest(tmp_path):
    path = write(str(tmp_path / "agent.conf"), "SERVER_URL=https://x\n# not\nENROLL_TOKEN=abcdefghijklmnop\n")
    assert config.forget_enroll_token(path)
    text = open(path).read()
    assert "abcdefghijklmnop" not in text and "SERVER_URL=https://x" in text and "# not" in text
    assert stat.S_IMODE(os.stat(path).st_mode) == 0o600
    assert config.load(path).enroll_token is None


def test_write_config(tmp_path):
    path = str(tmp_path / "agent.conf")
    config.write_config(path, "https://pops.okul.local", "abcdefghijklmnop", None)
    config.write_config(path, None, None, "/etc/pops-agent/server-ca.pem")
    cfg = config.load(path)
    expected = ("https://pops.okul.local", "abcdefghijklmnop", "/etc/pops-agent/server-ca.pem")
    assert (cfg.server_url, cfg.enroll_token, cfg.ca_file) == expected
    assert stat.S_IMODE(os.stat(path).st_mode) == 0o600


def test_secure_file_tightens_mode(tmp_path):
    path = write(str(tmp_path / "agent.conf"), "SERVER_URL=x\n", 0o644)
    config.secure_file(path)
    assert stat.S_IMODE(os.stat(path).st_mode) == 0o600


def test_ca_file_must_not_be_writable_by_others(tmp_path):
    path = write(str(tmp_path / "ca.pem"), "x", 0o644)
    assert config.ca_file_problem(path) is None
    os.chmod(path, 0o666)
    assert "yazabiliyor" in config.ca_file_problem(path)
    assert "okunamadı" in config.ca_file_problem(str(tmp_path / "yok.pem"))


def caps(tmp_path, conf=None, state=None):
    if conf is not None:
        write(str(tmp_path / "capabilities.conf"), conf)
    if state is not None:
        write(str(tmp_path / "state.json"), state)
    audits = []
    c = capabilities.Capabilities(str(tmp_path / "capabilities.conf"), str(tmp_path / "state.json"),
                                  lambda e, **f: audits.append((e, f)))
    c.load()
    return c, audits


def test_capability_defaults_and_local_off(tmp_path):
    assert caps(tmp_path)[0].terminal_enabled   # dosya yok: kurulum varsayılanı açık
    c, _ = caps(tmp_path, "TERMINAL_ENABLED=0\n")
    assert not c.terminal_enabled and "yerel" in c.describe()
    c, _ = caps(tmp_path, "TERMINAL_ENABLED=belki\n")
    assert not c.terminal_enabled   # anlaşılmayan değer: kapalı
    assert not c.vision_enabled


def test_server_can_only_switch_off(tmp_path):
    c, audits = caps(tmp_path, "TERMINAL_ENABLED=1\n")
    assert c.apply_server_request({"terminal_enabled": True}) == ([], [])
    assert c.apply_server_request({"terminal_enabled": False}) == (["terminal_enabled"], [])
    assert not c.terminal_enabled and audits[0][0] == "capability_changed"
    assert c.apply_server_request({"terminal_enabled": True, "vision_enabled": True}) == \
        ([], ["terminal_enabled", "vision_enabled"])
    again, _ = caps(tmp_path)   # yeniden başlatmada da kapalı
    assert not again.terminal_enabled and "sunucu" in again.describe()
    assert again.status_message("custom") == {"type": "capabilities", "terminal_enabled": False,
                                              "vision_enabled": False, "server_ca": "custom"}
    assert again.reset_server_state()   # yerel root: pops-agent capabilities --reset
    assert caps(tmp_path)[0].terminal_enabled


def test_unreadable_server_state_fails_safe(tmp_path):
    c, _ = caps(tmp_path, "TERMINAL_ENABLED=1\n", "{bozuk")
    assert not c.terminal_enabled


def test_audit_chain_and_tamper_detection(tmp_path):
    path = str(tmp_path / "audit.log")
    log = audit.LocalAudit(path, append_only=True)
    log.write("command_started", task_id=1, command_sha256=audit.command_sha256("ls"), command_length=2)
    log.write("command_finished", task_id=1, exit_code=0, duration_ms=5)
    audit.LocalAudit(path).write("enrolled", hw_id="HW-1")   # yeni süreç zinciri dosyadan sürdürür
    if audit.is_append_only(path):
        # root ve destekleyen dosya sistemi: satır silinemez, dosya kısaltılamaz
        with pytest.raises(PermissionError):
            open(path, "w").close()
        assert audit.clear_append_only(path) and not audit.is_append_only(path)
    ok, count, problems = audit.verify(path)
    assert ok and count == 3 and problems == []
    lines = open(path).read().splitlines()
    entry = json.loads(lines[0])
    assert entry["event_id"] == 1000 and "ls" not in lines[0]   # komut metni değil, özeti
    tampered = json.loads(lines[1])
    tampered["exit_code"] = 1
    with open(path, "w") as f:
        f.write("\n".join([lines[0], json.dumps(tampered), lines[2]]) + "\n")
    ok, _, problems = audit.verify(path)
    assert not ok and "içerik" in problems[0]
    with open(path, "w") as f:
        f.write("\n".join([lines[0], lines[2]]) + "\n")   # satır silindi
    ok, _, problems = audit.verify(path)
    assert not ok and "bağ kopuk" in problems[0]


def test_log_rotation_daily_size_and_retention(tmp_path):
    day = [datetime.date(2026, 10, 1)]
    path = str(tmp_path / "agent.log")
    h = logsetup.DailySizeRotatingHandler(path, max_bytes=200, keep_days=30, total_cap=10 ** 6, today=lambda: day[0])
    h.setFormatter(logging.Formatter("%(message)s"))

    def emit(msg):
        h.emit(logging.LogRecord("t", logging.INFO, "", 0, msg, None, None))

    emit("ilk")
    day[0] = datetime.date(2026, 10, 2)
    emit("ikinci gün")
    for i in range(10):
        emit("x" * 50)
    h.close()
    names = sorted(os.listdir(str(tmp_path)))
    assert "agent.log" in names and any(n.startswith("agent.log.20") for n in names)
    assert all(os.path.getsize(os.path.join(str(tmp_path), n)) <= 200 for n in names)
    old = write(str(tmp_path / "agent.log.2026-01-01"), "eski")
    os.utime(old, (0, 0))
    h.prune()
    assert not os.path.exists(old)
