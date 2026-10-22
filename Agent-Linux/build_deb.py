#!/usr/bin/env python3
"""pops-agent .deb paketini dpkg olmadan üretir (ar + tar.xz, yalnızca standart kütüphane).

    python3 Agent-Linux/build_deb.py [--version 0.1.22-alpha] [--out dist]

Tekrarlanabilir derleme: aynı kaynak ve aynı SOURCE_DATE_EPOCH aynı baytları verir. Bütün dosya zamanları
SOURCE_DATE_EPOCH'tur (yoksa son commit'in zamanı); sahip root:root, sıra alfabetik, sıkıştırma ayarları sabit.
Çıktının adı POps sürümünü olduğu gibi taşır (pops-agent_0.1.22-alpha_all.deb); paketin Debian sürümü ön sürüm
doğru sıralansın diye "~" ile yazılır (0.1.22~alpha).

Paketin içeriği:
  /usr/lib/pops-agent/pops_agent/   ajan kodu (+ _version.py)
  /usr/bin/pops-agent               başlatıcı (python3 -I)
  /lib/systemd/system/pops-agent.service
  /etc/pops-agent/capabilities.conf (conffile)
  /usr/share/doc/pops-agent/copyright
"""

import argparse
import hashlib
import io
import lzma
import os
import subprocess
import sys
import tarfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
PKG = os.path.join(HERE, "pops_agent")
DEBIAN = os.path.join(HERE, "debian")

LAUNCHER = '''#!/usr/bin/python3 -I
"""pops-agent başlatıcısı (paket). -I: PYTHON* ortam değişkenleri ve kullanıcı site-packages'ı yok sayılır."""
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, "/usr/lib/pops-agent")

from pops_agent.cli import main  # noqa: E402

sys.exit(main())
'''


def deb_version(version: str) -> str:
    return version.strip().lstrip("vV").replace("-", "~", 1)


def source_date_epoch() -> int:
    env = os.environ.get("SOURCE_DATE_EPOCH")
    if env:
        return int(env)
    try:
        out = subprocess.run(["git", "-C", ROOT, "log", "-1", "--format=%ct"], stdout=subprocess.PIPE,
                             stderr=subprocess.DEVNULL, check=True)
        return int(out.stdout.strip())
    except (OSError, subprocess.CalledProcessError, ValueError):
        print("uyarı: SOURCE_DATE_EPOCH yok ve git okunamadı; zaman 0 alınıyor", file=sys.stderr)
        return 0


def package_files(version: str):
    """[(paket içindeki yol, baytlar, kip)] — yalnızca dosyalar, sıralı."""
    files = []
    for name in sorted(os.listdir(PKG)):
        if name.endswith(".py") and name != "_version.py":
            with open(os.path.join(PKG, name), "rb") as f:
                files.append(("usr/lib/pops-agent/pops_agent/" + name, f.read(), 0o644))
    files.append(("usr/lib/pops-agent/pops_agent/_version.py",
                  ('"""build_deb.py yazar."""\nVERSION = %r\n' % version).encode(), 0o644))
    files.append(("usr/bin/pops-agent", LAUNCHER.encode(), 0o755))
    with open(os.path.join(DEBIAN, "pops-agent.service"), "rb") as f:
        files.append(("lib/systemd/system/pops-agent.service", f.read(), 0o644))
    with open(os.path.join(DEBIAN, "capabilities.conf"), "rb") as f:
        files.append(("etc/pops-agent/capabilities.conf", f.read(), 0o644))
    with open(os.path.join(DEBIAN, "copyright"), "rb") as f:
        files.append(("usr/share/doc/pops-agent/copyright", f.read(), 0o644))
    files.sort(key=lambda x: x[0])
    return files


def _tar(entries, mtime: int) -> bytes:
    """entries: [(yol, baytlar ya da None=klasör, kip)]. GNU tar, root:root, sabit zaman."""
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for path, data, mode in entries:
            info = tarfile.TarInfo("./" + path + ("/" if data is None and path else ""))
            info.mtime = mtime
            info.uid = info.gid = 0
            info.uname = info.gname = "root"
            info.mode = mode
            if data is None:
                info.type = tarfile.DIRTYPE
                tar.addfile(info)
            else:
                info.size = len(data)
                tar.addfile(info, io.BytesIO(data))
    return buf.getvalue()


def _xz(data: bytes) -> bytes:
    return lzma.compress(data, format=lzma.FORMAT_XZ, check=lzma.CHECK_CRC64, preset=6)


def data_tar(files, mtime: int) -> bytes:
    dirs = set()
    for path, _data, _mode in files:
        parts = path.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            dirs.add("/".join(parts[:i]))
    entries = [("", None, 0o755)]
    for path in sorted(dirs | {p for p, _d, _m in files}):
        if path in dirs:
            entries.append((path, None, 0o700 if path == "etc/pops-agent" else 0o755))
        else:
            data, mode = next((d, m) for p, d, m in files if p == path)
            entries.append((path, data, mode))
    return _tar(entries, mtime)


def control_tar(version: str, files, mtime: int) -> bytes:
    conffiles = open(os.path.join(DEBIAN, "conffiles"), encoding="utf-8").read()
    conf_set = {line.strip().lstrip("/") for line in conffiles.splitlines() if line.strip()}
    installed_kib = sum((len(d) + 1023) // 1024 for _p, d, _m in files)
    control = open(os.path.join(DEBIAN, "control.in"), encoding="utf-8").read()
    control = control.replace("@DEB_VERSION@", deb_version(version)).replace("@INSTALLED_SIZE@", str(installed_kib))
    md5sums = "".join("%s  %s\n" % (hashlib.md5(d, usedforsecurity=False).hexdigest(), p)
                      for p, d, _m in files if p not in conf_set)
    entries = [("", None, 0o755), ("conffiles", conffiles.encode(), 0o644), ("control", control.encode(), 0o644),
               ("md5sums", md5sums.encode(), 0o644)]
    for script in ("postinst", "postrm", "prerm"):
        with open(os.path.join(DEBIAN, script), "rb") as f:
            entries.append((script, f.read(), 0o755))
    return _tar(entries, mtime)


def _ar_member(name: str, data: bytes, mtime: int) -> bytes:
    header = "%-16s%-12d%-6d%-6d%-8s%-10d`\n" % (name, mtime, 0, 0, "100644", len(data))
    assert len(header) == 60
    return header.encode("ascii") + data + (b"\n" if len(data) % 2 else b"")


def build(version: str, out_dir: str, mtime: int) -> str:
    files = package_files(version)
    ctrl = _xz(control_tar(version, files, mtime))
    data = _xz(data_tar(files, mtime))
    deb = b"!<arch>\n" + _ar_member("debian-binary", b"2.0\n", mtime) + _ar_member("control.tar.xz", ctrl, mtime) \
        + _ar_member("data.tar.xz", data, mtime)
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, "pops-agent_%s_all.deb" % version.strip().lstrip("vV"))
    with open(path, "wb") as f:
        f.write(deb)
    return path


def read_ar(raw: bytes):
    """Testler için: {üye adı: baytlar}."""
    if not raw.startswith(b"!<arch>\n"):
        raise ValueError("ar arşivi değil")
    pos, out = 8, {}
    while pos < len(raw):
        header = raw[pos:pos + 60]
        name = header[:16].decode().strip().rstrip("/")
        size = int(header[48:58].decode().strip())
        out[name] = raw[pos + 60:pos + 60 + size]
        pos += 60 + size + (size % 2)
    return out


def main(argv=None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--version", help="varsayılan: kök VERSION dosyası")
    p.add_argument("--out", default=os.path.join(ROOT, "dist"))
    args = p.parse_args(argv)
    version = args.version or open(os.path.join(ROOT, "VERSION"), encoding="utf-8").read().strip()
    path = build(version, args.out, source_date_epoch())
    with open(path, "rb") as f:
        digest = hashlib.sha256(f.read()).hexdigest()
    print("%s  %s (Debian sürümü %s)" % (digest, path, deb_version(version)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
