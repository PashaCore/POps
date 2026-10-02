#!/usr/bin/env python3
"""check_html_sinks.py öz sınamaları (CI: dashboard işi). Yalnızca standart kütüphane."""
import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_html_sinks as chk  # noqa: E402

# Her satır tek başına yakalanmalı
BAD_JS = [
    "el.innerHTML = `<b>${name}</b>`;",
    "el.innerHTML = '<i>' + msg + '</i>';",
    "el.innerHTML += `<tr><td>${row.pc_name}</td></tr>`;",
    "box.insertAdjacentHTML('beforeend', `<p>${t.title}</p>`);",
    "document.write(data.msg);",
    "el.outerHTML = data;",
    # escapeHtml satır içi olay niteliğinde yetmez: &#39; JS'den önce ' olur
    "el.innerHTML = `<button onclick=\"go('${escapeHtml(id)}')\">x</button>`;",
    # href/src başında escapeHtml javascript: adresini durdurmaz
    "el.innerHTML = `<a href=\"${escapeHtml(url)}\">x</a>`;",
    # etiketin içinde (nitelik adı yerine) kaçırma işe yaramaz
    "el.innerHTML = `<div ${escapeHtml(attr)}>x</div>`;",
    "el.innerHTML = `<td title=${escapeHtml(t)}>x</td>`;",
    # sink dışında da: HTML etiketi içeren şablon ve html adlı değişken
    "const rows = items.map(i => `<td>${i.name}</td>`).join('');",
    "let html = 'Durum: ' + d.status;",
    "el.innerHTML = cond ? '<i>a</i>' : d.detail;",
    "el.innerHTML = list.map(x => x.name).join(', ');",
]

GOOD_JS = [
    "el.innerHTML = `<b>${escapeHtml(name)}</b>`;",
    "el.innerHTML = `<button onclick=\"go(${jsArg(id)})\">x</button>`;",
    "el.innerHTML = `<a href=\"/x/${encodeURIComponent(id)}\">${items.length}</a>`;",
    "el.innerHTML = cond ? '<i>a</i>' : `<b>${Number(n)}</b>`;",
    "el.innerHTML = ok && `<span class=\"${ok ? 'on' : 'off'}\">${escapeHtml(t)}</span>`;",
    "el.innerHTML = list.map(x => `<li>${escapeHtml(x)}</li>`).join('');",
    "el.innerHTML = list.map(x => { const y = 1; return `<li>${escapeHtml(x)} ${y}</li>`; }).join('');",
    "el.innerHTML = '';",
    "btn.innerHTML = oldHtml;",
    "el.textContent = d.name; if (!confirm(`${d.name} silinsin mi?`)) return;",
    "const cls = ok ? 'on' : 'off'; el.innerHTML = `<span class=\"${cls}\">x</span>`;",
    "const label = (v) => Number(v || 0).toLocaleString('tr-TR'); el.innerHTML = `<b>${label(x)}</b>`;",
    "showToast(`${name} kaydedildi`);",
    "const re = /[&<>\"']/g; el.innerHTML = `<i>${escapeHtml(s)}</i>`;",
]

BAD_PHP = [
    "<div><?= $_GET['q'] ?></div>",
    "<div><?php echo $row['name']; ?></div>",
    "<script>const x = <?= json_encode($x) ?>;</script>",
    "<a href=\"<?php echo $page; ?>.php\">x</a>",
]

GOOD_PHP = [
    "<div><?= htmlspecialchars($x, ENT_QUOTES, 'UTF-8') ?></div>",
    "<div><?php echo (int)$n; ?></div>",
    "<script>const x = <?= json_encode($x, JSON_HEX_TAG | JSON_HEX_AMP) ?>;</script>",
    "<input <?= $canEdit ? '' : 'disabled' ?>>",
    "<p>&copy; <?php echo date('Y'); ?></p>",
]


class Workspace(object):
    """Geçici bir depo kökü: Dashboard/ altında verilen dosyalar ve izin listesi."""

    def __init__(self, files, allowlist=''):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = self.tmp.name
        os.makedirs(os.path.join(self.root, 'Dashboard', 'assets'))
        for rel, text in files.items():
            with open(os.path.join(self.root, rel), 'w', encoding='utf-8') as f:
                f.write(text)
        self.allow = os.path.join(self.root, 'allow.txt')
        with open(self.allow, 'w', encoding='utf-8') as f:
            f.write(allowlist)

    def run(self):
        out = io.StringIO()
        with redirect_stdout(out):
            code = chk.main(['--root', self.root, '--allowlist', self.allow])
        return code, out.getvalue()

    def close(self):
        self.tmp.cleanup()


def findings(path):
    result, _, _, _ = chk.scan_file(path)
    return [f for f, _, _ in result]


class CheckerTests(unittest.TestCase):
    def scan_text(self, text, ext):
        with tempfile.NamedTemporaryFile('w', suffix=ext, delete=False, encoding='utf-8') as f:
            f.write(text)
        try:
            return findings(f.name)
        finally:
            os.unlink(f.name)

    def test_bad_js_lines_are_flagged(self):
        for line in BAD_JS:
            with self.subTest(line=line):
                self.assertTrue(self.scan_text(line + '\n', '.js'), 'yakalanmadı')

    def test_good_js_lines_pass(self):
        for line in GOOD_JS:
            with self.subTest(line=line):
                self.assertEqual([(f.why, f.text) for f in self.scan_text(line + '\n', '.js')], [])

    def test_php_echo(self):
        for line in BAD_PHP:
            with self.subTest(line=line):
                self.assertTrue(self.scan_text(line + '\n', '.php'), 'yakalanmadı')
        for line in GOOD_PHP:
            with self.subTest(line=line):
                self.assertEqual([(f.why, f.text) for f in self.scan_text(line + '\n', '.php')], [])

    def test_script_block_in_php(self):
        page = "<p>x</p>\n<script>\nconst a = 1;\nel.innerHTML = `<b>${d.name}</b>`;\n</script>\n"
        got = self.scan_text(page, '.php')
        self.assertEqual([f.line for f in got], [4])

    def test_inference_follows_reassignment(self):
        js = "let cls = 'a';\ncls = d.status;\nel.innerHTML = `<i class=\"${cls}\"></i>`;\n"
        self.assertTrue(self.scan_text(js, '.js'))


class AllowlistTests(unittest.TestCase):
    PAGE = "<script>\nel.innerHTML = `<b>${d.count}</b>`;\n</script>\n"

    def key(self):
        return chk.line_key("el.innerHTML = `<b>${d.count}</b>`;")

    def test_unreviewed_finding_fails(self):
        ws = Workspace({'Dashboard/a.php': self.PAGE})
        try:
            code, out = ws.run()
            self.assertEqual(code, 1)
            self.assertIn(self.key(), out)
        finally:
            ws.close()

    def test_entry_survives_line_shift(self):
        ws = Workspace({'Dashboard/a.php': '\n\n\n' + self.PAGE},
                       'Dashboard/a.php  %s  sayaç\n' % self.key())
        try:
            self.assertEqual(ws.run()[0], 0)
        finally:
            ws.close()

    def test_stale_and_reasonless_entries_fail(self):
        ws = Workspace({'Dashboard/a.php': '<p>x</p>\n'}, 'Dashboard/a.php  %s  sayaç\n' % self.key())
        try:
            code, out = ws.run()
            self.assertEqual(code, 1)
            self.assertIn('kullanılmayan izin', out)
        finally:
            ws.close()
        ws = Workspace({'Dashboard/a.php': self.PAGE}, 'Dashboard/a.php  %s\n' % self.key())
        try:
            code, out = ws.run()
            self.assertEqual(code, 1)
            self.assertIn('gerekçe eksik', out)
        finally:
            ws.close()

    def test_sink_wrapper_arguments_are_checked(self):
        js = "function show(id, html) { $(id).innerHTML = html; }\nshow('a', d.msg);\nshow('b', escapeHtml(d.msg));\n"
        ws = Workspace({'Dashboard/assets/x.js': js}, 'Dashboard/assets/x.js  sink:show/1  durum satırı\n')
        try:
            code, out = ws.run()
            self.assertEqual(code, 1)
            self.assertIn('x.js:2:', out)
            self.assertNotIn('x.js:3:', out)
        finally:
            ws.close()


if __name__ == '__main__':
    unittest.main(verbosity=1)
