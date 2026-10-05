"""Backend testlerinin pytest'e bağlanması (docs/testing.md).

İki tür test dosyası var:

- **Yerel pytest dosyaları** (ör. test_units.py): pytest'i içe aktarır ya da giriş noktası olmadan test_*
  fonksiyonları tanımlar; her test_* fonksiyonu ayrı bir test olarak toplanır.
- **Betik testleri** (eski biçim, ör. test_security.py): ``if __name__ == "__main__"`` ya da modül düzeyinde
  ``asyncio.run(...)`` ile kendini çalıştırır, check()/chk() ile denetler ve çıkış koduyla sonuç bildirir. Bunlar içe
  AKTARILMAZ (içe aktarmak testi çalıştırırdı); her biri ayrı bir süreçte çalışan tek bir test olur. Çıkış kodu 0
  ise geçer; değilse düşen kontrollerin satırları ve çıktının sonu raporlanır.

Betik ``POPS_TEST_HTTP``'yi okuyorsa çalışan bir sunucu ister: ``integration`` işaretini alır ve POPS_TEST_HTTP
tanımlı değilse atlanır (sunucuyu tests/run_local.sh ya da CI başlatır). Betikler bilinen sırayla (CI'ın eski
sırası, SCRIPT_ORDER) koşar, listede olmayan yeni betikler ardından ada göre gelir; yeni bir betik için burada bir
şey değiştirmek gerekmez.

POPS_TEST_COVERAGE=1 iken sunucusuz betikler ``coverage run`` altında çalışır (CI'ın kapsam ölçümü; veriler
Backend/.coverage.* dosyalarına yazılır, ``coverage combine`` birleştirir).
"""

import ast
import os
import re
import subprocess
import sys
from pathlib import Path

import pytest

BACKEND_DIR = Path(__file__).resolve().parent.parent
# Betikler, yerel testlerin süreç içinde değiştirdiği ortamı değil (ör. test_units.py'nin DB_* varsayılanları)
# pytest başladığındaki ortamı görür
_BASE_ENV = dict(os.environ)

# CI'da betiklerin koştuğu sıra korunur: entegrasyon betikleri aynı sunucuyu ve veritabanını paylaşır
SCRIPT_ORDER = [
    "test_protocol.py",
    "test_security.py",
    "test_2fa.py",
    "test_agent_authz.py",
    "test_remote_authz.py",
    "test_vision_v2.py",
    "test_device_keys.py",
    "test_hardening.py",
    "test_p1.py",
    "test_review4.py",
    "test_modules.py",
    "test_jobs.py",
    "test_f4_accountability.py",
    "test_features.py",
    "test_helpdesk_licenses.py",
    "test_ops.py",
    "test_api_tokens.py",
    "test_demo.py",
    "test_files.py",
    "test_exam.py",
    "test_winget.py",
    "test_devices_delta.py",
    "test_power_message.py",
    "test_peer_cache.py",
    "test_sso.py",
    "test_strict_input.py",
    "test_glpi.py",
    "test_timestamps.py",
]

# Betiklerin kontrol satırları: chk() "  FAIL <mesaj>", check() "  ✘ <mesaj>" basar
_FAIL_LINE = re.compile(r"^\s*(FAIL\b|✘)")
_TAIL_LINES = 60


def _is_main_guard(node):
    return (isinstance(node, ast.If) and isinstance(node.test, ast.Compare)
            and isinstance(node.test.left, ast.Name) and node.test.left.id == "__name__")


def _imports_pytest(tree):
    for node in tree.body:
        if isinstance(node, ast.Import) and any(a.name.split(".")[0] == "pytest" for a in node.names):
            return True
        if isinstance(node, ast.ImportFrom) and (node.module or "").split(".")[0] == "pytest":
            return True
    return False


def _is_entry_call(node, defined):
    """Modül düzeyinde asyncio.run(...) ya da modülde tanımlı bir fonksiyonun çağrısı (ör. main()).

    sys.path.insert(...) gibi hazırlık çağrıları sayılmaz."""
    if not (isinstance(node, ast.Expr) and isinstance(node.value, ast.Call)):
        return False
    func = node.value.func
    if isinstance(func, ast.Name):
        return func.id in defined
    return (isinstance(func, ast.Attribute) and func.attr == "run"
            and isinstance(func.value, ast.Name) and func.value.id == "asyncio")


def _is_script(tree):
    """Kendini çalıştıran (içe aktarılamayan) eski biçim test mi?

    pytest'i içe aktaran dosya yerel testtir. Aktarmayan dosya, bir giriş noktası varsa ya da toplanacak hiçbir
    test_* fonksiyonu / Test* sınıfı yoksa betiktir."""
    if _imports_pytest(tree):
        return False
    defined = {n.name for n in tree.body if isinstance(n, (ast.FunctionDef, ast.AsyncFunctionDef))}
    has_tests = any(name.startswith("test") for name in defined) or any(
        isinstance(n, ast.ClassDef) and n.name.startswith("Test") for n in tree.body)
    return not has_tests or any(_is_main_guard(n) or _is_entry_call(n, defined) for n in tree.body)


def pytest_pycollect_makemodule(module_path, parent):
    """test_*.py için Module yerine, betikse ScriptFile kur (pytest betiği içe aktarmaz)."""
    source = module_path.read_text(encoding="utf-8")
    try:
        tree = ast.parse(source, filename=str(module_path))
    except SyntaxError:
        return None   # pytest'in kendi toplayıcısı sözdizimi hatasını raporlar
    if not _is_script(tree):
        return None
    return ScriptFile.from_parent(parent, path=module_path, needs_server="POPS_TEST_HTTP" in source)


class ScriptFile(pytest.File):
    def __init__(self, *, needs_server, **kwargs):
        super().__init__(**kwargs)
        self.needs_server = needs_server

    def collect(self):
        item = ScriptItem.from_parent(self, name=self.path.stem)
        if self.needs_server:
            item.add_marker(pytest.mark.integration)
        yield item


class ScriptFailed(Exception):
    pass


class ScriptItem(pytest.Item):
    def runtest(self):
        if self.parent.needs_server and not os.environ.get("POPS_TEST_HTTP"):
            pytest.skip("çalışan bir sunucu ister: POPS_TEST_HTTP tanımlı değil (tests/run_local.sh)")
        cmd = [sys.executable]
        if os.environ.get("POPS_TEST_COVERAGE") == "1" and not self.parent.needs_server:
            cmd += ["-m", "coverage", "run", "--rcfile=" + str(BACKEND_DIR / ".coveragerc")]
        cmd.append(str(self.path))
        proc = subprocess.run(cmd, cwd=BACKEND_DIR, env=_BASE_ENV, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              text=True, encoding="utf-8", errors="replace")
        self.add_report_section("call", "betik çıktısı", proc.stdout)
        if proc.returncode != 0:
            raise ScriptFailed(proc.returncode, proc.stdout)

    def repr_failure(self, excinfo):
        if not isinstance(excinfo.value, ScriptFailed):
            return super().repr_failure(excinfo)
        code, out = excinfo.value.args
        lines = out.splitlines()
        failed = [ln.strip() for ln in lines if _FAIL_LINE.match(ln)]
        parts = ["%s çıkış kodu %d ile bitti." % (self.path.name, code)]
        if failed:
            # Çıktının tamamı raporun "betik çıktısı" bölümünde
            parts.append("Düşen kontroller (%d):" % len(failed))
            parts += ["  " + ln for ln in failed]
        else:
            parts.append("Çıktının sonu:")
            parts += lines[-_TAIL_LINES:]
        return "\n".join(parts)

    def reportinfo(self):
        return self.path, None, "betik: %s" % self.path.name


def pytest_collection_modifyitems(config, items):
    """Betikleri bilinen sıraya koy; yerel testler önde, kendi sıralarında kalır."""
    rank = {name: i for i, name in enumerate(SCRIPT_ORDER)}

    def key(pair):
        pos, item = pair
        if not isinstance(item, ScriptItem):
            return (0, 0, "", pos)
        name = item.path.name
        return (1, rank.get(name, len(rank)), name, pos)

    items[:] = [it for _, it in sorted(enumerate(items), key=key)]
