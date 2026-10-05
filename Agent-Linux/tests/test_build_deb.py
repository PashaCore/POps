import hashlib
import io
import lzma
import os
import sys
import tarfile

from conftest import AGENT_DIR

sys.path.insert(0, AGENT_DIR)
import build_deb  # noqa: E402


def build(tmp, version="0.1.22-alpha", epoch=1759600000):
    return build_deb.build(version, str(tmp), epoch)


def members(path):
    with open(path, "rb") as f:
        return build_deb.read_ar(f.read())


def tar_of(data):
    return tarfile.open(fileobj=io.BytesIO(lzma.decompress(data)))


def test_reproducible(tmp_path):
    a = build(tmp_path / "a")
    b = build(tmp_path / "b")
    assert os.path.basename(a) == "pops-agent_0.1.22-alpha_all.deb"
    assert open(a, "rb").read() == open(b, "rb").read()
    c = build(tmp_path / "c", epoch=1759600001)
    assert open(a, "rb").read() != open(c, "rb").read()


def test_deb_layout(tmp_path):
    m = members(build(tmp_path))
    assert list(m) == ["debian-binary", "control.tar.xz", "data.tar.xz"] and m["debian-binary"] == b"2.0\n"
    ctrl = tar_of(m["control.tar.xz"])
    control = ctrl.extractfile("./control").read().decode()
    assert "Version: 0.1.22~alpha\n" in control and "Architecture: all\n" in control
    assert "python3-websockets" in control and "python3-cryptography" in control
    assert ctrl.getmember("./postinst").mode == 0o755
    assert ctrl.extractfile("./conffiles").read() == b"/etc/pops-agent/capabilities.conf\n"
    data = tar_of(m["data.tar.xz"])
    names = data.getnames()
    for expected in ("./usr/lib/pops-agent/pops_agent/agent.py", "./usr/lib/pops-agent/pops_agent/updater.py",
                     "./usr/lib/pops-agent/pops_agent/_version.py", "./usr/bin/pops-agent",
                     "./lib/systemd/system/pops-agent.service", "./etc/pops-agent/capabilities.conf"):
        assert expected in names
    assert not any("__pycache__" in n or "/tests/" in n for n in names)
    assert all(i.uid == 0 and i.gid == 0 and i.uname == "root" and i.mtime == 1759600000 for i in data.getmembers())
    assert data.getmember("./usr/bin/pops-agent").mode == 0o755
    assert data.getmember("./etc/pops-agent").mode == 0o700
    launcher = data.extractfile("./usr/bin/pops-agent").read().decode()
    assert launcher.startswith("#!/usr/bin/python3 -I\n") and '"/usr/lib/pops-agent"' in launcher
    version = data.extractfile("./usr/lib/pops-agent/pops_agent/_version.py").read().decode()
    assert "VERSION = '0.1.22-alpha'" in version
    md5 = ctrl.extractfile("./md5sums").read().decode().splitlines()
    sums = dict(reversed(line.split("  ", 1)) for line in md5)
    agent_py = data.extractfile("./usr/lib/pops-agent/pops_agent/agent.py").read()
    assert sums["usr/lib/pops-agent/pops_agent/agent.py"] == hashlib.md5(agent_py).hexdigest()
    assert "etc/pops-agent/capabilities.conf" not in sums   # conffile md5sums'ta değil


def test_unit_file_restarts_and_documents_hardening():
    unit = open(os.path.join(AGENT_DIR, "debian", "pops-agent.service"), encoding="utf-8").read()
    assert "Restart=always" in unit and "ExecStart=/usr/bin/pops-agent run" in unit
    assert "StateDirectoryMode=0700" in unit and "LimitCORE=0" in unit
    assert "NoNewPrivileges=yes" not in unit and "ProtectSystem=" not in unit.replace("(ProtectSystem", "")


def test_version_mapping():
    assert build_deb.deb_version("0.1.22-alpha") == "0.1.22~alpha"
    assert build_deb.deb_version("0.1.22") == "0.1.22"
