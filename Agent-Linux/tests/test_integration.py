"""Uçtan uca: kaynak ağacındaki ajan, çalışan bir POps arka ucuna bağlanır (root gerekmez).

Ortam (yoksa test atlanır):
  POPS_IT_URL          arka uç, ör. https://127.0.0.1:8797 (ya da yerel http://127.0.0.1:8099)
  POPS_IT_CA           https için kurum CA'sı (ajana SERVER_CA_CERT olarak da verilir)
  POPS_IT_ADMIN_PASS   süper yönetici parolası (PANEL_ADMIN_PASS); POPS_IT_ADMIN_USER varsayılanı admin
  POPS_IT_AGENT_PYTHON ajanı çalıştıracak python3 (varsayılan: testi çalıştıran)

Adımlar: kayıt jetonu → ajan kaydolur (anahtar 0600, jeton ayardan silinir) → panel cihazı Linux ve çevrimiçi
gösterir → donanım envanteri gelir → komut çıkış koduyla döner → onaylanan sonuç diskten silinir → sunucu uzak komutu
kapatınca komut "Denied" (-5, "[REDDEDİLDİ] …") olur → sunucu Linux bilgisayarı karantinaya almaz (409) → ajan
SIGTERM ile temiz kapanır ve yerel denetim izi sağlamdır.
"""

import json
import os
import signal
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid

import pytest

from conftest import AGENT_DIR, make_hw, write

URL = os.environ.get("POPS_IT_URL")
CA = os.environ.get("POPS_IT_CA")
ADMIN = os.environ.get("POPS_IT_ADMIN_USER", "admin")
PASSWORD = os.environ.get("POPS_IT_ADMIN_PASS")

pytestmark = pytest.mark.skipif(not (URL and PASSWORD), reason="POPS_IT_URL ve POPS_IT_ADMIN_PASS gerekli")


def api(path, body=None, token=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(URL.rstrip("/") + path, data=data, method=method or ("POST" if data else "GET"))
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    ctx = ssl.create_default_context(cafile=CA) if URL.startswith("https") else None
    try:
        with urllib.request.urlopen(req, timeout=20, context=ctx) as resp:
            raw = resp.read()
            return resp.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read().decode("utf-8", "replace")


def wait_for(what, fn, timeout=60, every=0.5):
    end = time.monotonic() + timeout
    last = None
    while time.monotonic() < end:
        last = fn()
        if last:
            return last
        time.sleep(every)
    raise AssertionError("beklenen olmadı: %s (son: %r)" % (what, last))


@pytest.fixture(scope="module")
def token():
    status, body = api("/api/admin/login", {"username": ADMIN, "password": PASSWORD})
    assert status == 200 and body.get("token"), body
    return body["token"]


def device(token, hw_id):
    status, devices = api("/api/devices", token=token)
    assert status == 200
    return next((d for d in devices if d["hostname"] == hw_id), None)


def task(token, task_id):
    status, tasks = api("/api/tasks?limit=200", token=token)
    assert status == 200
    rows = tasks if isinstance(tasks, list) else tasks.get("items", [])
    return next((t for t in rows if t.get("id") == task_id), None)


def run_command(token, hw_id, command, final=None):
    """final: beklenen son durum. Ret sonucu önce "Failed" (-5) yazılır, ardından gelen capability_denied görevi
    "Denied" yapar (Windows ajanıyla aynı sıra)."""
    status, body = api("/api/deploy_orchestration", {
        "target_mode": "PC", "targets": [hw_id], "title": "Komut", "source": "terminal",
        "taskSequence": [{"name": "Komut", "type": "CMD", "command": command}]}, token)
    assert status == 200 and body["created"] == 1, body
    tid = body["task_ids"][0]

    def done():
        t = task(token, tid)
        if not t or t["status"] in ("Pending", "Running") or (final and t["status"] != final):
            return None
        return t
    return wait_for("görev %d bitsin" % tid, done)


def test_linux_agent_against_backend(tmp_path, token):
    status, created = api("/api/system/enroll-token", {"lab_name": "Linux-IT", "ttl_hours": 1, "max_uses": 1}, token)
    assert status == 200, created
    conf, state, logs = tmp_path / "etc", tmp_path / "state", tmp_path / "log"
    hw_id = "HW-IT" + uuid.uuid4().hex[:10].upper()
    # Benzersiz sahte donanım: gerçek bilgisayarın DNA'sı başka bir test cihazıyla eşleşmesin
    make_hw(str(tmp_path / "hw"), uuid=str(uuid.uuid4()), product_serial="IT-" + uuid.uuid4().hex[:8],
            macs=(("enp0s31f6", "02:00:00:%02x:%02x:%02x" % tuple(os.urandom(3)), "up", True),),
            disk=("sda", "IT-DISK-" + uuid.uuid4().hex[:6]), ram=("IT-RAM-1",))
    write(str(state / "identity"), hw_id + "\n", 0o600)
    lines = ["SERVER_URL=" + URL, "ENROLL_TOKEN=" + created["token"]]
    if CA:
        lines.append("SERVER_CA_CERT=" + CA)
    write(str(conf / "agent.conf"), "\n".join(lines) + "\n", 0o600)
    write(str(conf / "capabilities.conf"), "TERMINAL_ENABLED=1\n")
    os.chmod(str(state), 0o700)
    python = os.environ.get("POPS_IT_AGENT_PYTHON", sys.executable)
    out = open(str(tmp_path / "agent.out"), "wb")
    agent = subprocess.Popen([python, os.path.join(AGENT_DIR, "pops-agent"), "--config-dir", str(conf),
                              "--state-dir", str(state), "--log-dir", str(logs), "run", "--verbose",
                              "--hw-root", str(tmp_path / "hw")], stdout=out, stderr=subprocess.STDOUT)
    try:
        # Kayıt: anahtar root'a ait 0600 dosyada, jeton ayar dosyasından silindi
        wait_for("cihaz anahtarı", lambda: (state / "secret.json").exists(), 60)
        assert oct((state / "secret.json").stat().st_mode & 0o777) == "0o600"
        wait_for("jeton silinsin", lambda: created["token"] not in (conf / "agent.conf").read_text(), 10)

        # Panel: Linux, çevrimiçi, jetonun sınıfında, yetenekler bildirildi
        # (heartbeat'ler toplu yazılır: sağlık özeti ilk yazımdan sonra görünür)
        def ready():
            x = device(token, hw_id)
            ok = x and x["status"] == "Online" and x.get("agent_health") and x.get("cap_terminal_enabled")
            return x if ok else None
        d = wait_for("cihaz panelde", ready)
        assert d["platform"] == "linux" and d["lab"] == "Linux-IT" and d["agent_version"] != "Bilinmiyor"
        assert d["cap_vision_enabled"] is False and d["cap_server_ca"] == ("custom" if CA else "system")
        assert isinstance(d["agent_health"], dict) and d["agent_health"]["started_at"]

        # Donanım envanteri (sunucu bağlanınca get_hardware ister)
        def inventory():
            status, rows = api("/api/inventory", token=token)
            return next((r for r in rows or [] if r["pc_name"] == hw_id), None)
        inv = wait_for("donanım envanteri", inventory, 60)
        assert "Linux" in inv["os_version"] and inv["mac_address"].startswith("02:00:00")

        # Komut: çıkış kodu ve çıktı
        ok = run_command(token, hw_id, "uname -s; echo pops-it-$((6*7))")
        assert ok["status"] == "Completed" and ok["exit_code"] == 0
        assert ok["output"].splitlines() == ["Linux", "pops-it-42"]
        failed = run_command(token, hw_id, "echo yarim; echo bozuk >&2; exit 3")
        assert failed["status"] == "Failed" and failed["exit_code"] == 3
        assert failed["output"] == "[ÇIKIŞ KODU: 3]\n[HATA]:\nbozuk\n[ÇIKTI]:\nyarim"
        # Onaylanan sonuç diskten silindi
        wait_for("onay bekleyen sonuç kalmasın", lambda: not (state / "pending-results.json").exists(), 15)

        # Karantina bu sürümde yok: sunucu Linux bilgisayarı kilitlemez (409), panel kilitli göstermez
        status, body = api("/api/security/lockdown", {"target_pc": hw_id, "reason": "entegrasyon testi"}, token)
        assert status == 409 and "Linux" in str(body), (status, body)
        assert device(token, hw_id)["is_quarantined"] is False

        # Sunucu uzak komutu kapatır: ajan kalıcı kaydeder, bildirir; komut Denied, -5, [REDDEDİLDİ]
        status, _ = api("/api/system/set-capabilities", {"pc_name": hw_id, "terminal_enabled": False}, token)
        assert status == 200
        wait_for("uzak komut kapalı görünsün", lambda: device(token, hw_id)["cap_terminal_enabled"] is False, 20)
        assert json.loads((state / "capabilities.state.json").read_text())["terminal_enabled"] is False
        refused = run_command(token, hw_id, "id", final="Denied")
        assert refused["status"] == "Denied" and refused["exit_code"] == -5
        assert refused["output"].startswith("[REDDEDİLDİ]")
    finally:
        agent.send_signal(signal.SIGTERM)
        try:
            code = agent.wait(30)
        except subprocess.TimeoutExpired:
            agent.kill()
            code = None
        out.close()
        api("/api/devices/" + hw_id, token=token, method="DELETE")
    assert code == 0, (tmp_path / "agent.out").read_text()[-3000:]
    # --verbose'da bile cihaz anahtarı ve kayıt jetonu günlüğe yazılmaz
    secret = json.loads((state / "secret.json").read_text())["secret"]
    for log_text in ((tmp_path / "agent.out").read_text(), (logs / "agent.log").read_text()):
        assert secret not in log_text and created["token"] not in log_text and secret[:10] not in log_text
    verify = subprocess.run([python, os.path.join(AGENT_DIR, "pops-agent"), "--log-dir", str(logs), "--config-dir",
                             str(conf), "--state-dir", str(state), "audit-verify"], stdout=subprocess.PIPE)
    assert verify.returncode == 0, verify.stdout
