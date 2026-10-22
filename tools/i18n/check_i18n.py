#!/usr/bin/env python3
"""Panelin çeviri sözlüklerini denetler (CI: dashboard işi). Yalnızca standart kütüphane, Python 3.9+.

Sözlükler Dashboard/lang/<dil>/<ad>.json: düz bir nesne, anahtar koddaki Türkçe metin, değer İngilizcesi ya da
çoğul için {"one": "...", "other": "..."}. Ad "common" (ortak kabuk ve ortak betikler) ya da bir sayfa
(Dashboard/<ad>.php) olmalı. Bağlamlı anahtar "Türkçe metin|bağlam" biçimindedir (POps.tx / __x).

Denetlenen
  1. Her dosya geçerli JSON, kökü nesne; aynı anahtar iki kez yok.
  2. Değer boş olmayan metin ya da yalnızca one/other alanları olan, other'ı bulunan bir nesne.
  3. Yer tutucular ({n}, {name} ...) anahtarla her değerde (one ve other dahil) aynı.
  4. Anahtarın Türkçe metni kaynakta (Dashboard/ ya da sunucu iletileri için Backend/) bir dize sabiti olarak birebir
     geçiyor: harfi harfine yazılmamış anahtar hiçbir zaman eşleşmez, sessizce Türkçe kalır.

--missing <ad>: o sayfanın (sayfa betiği assets/pages/<ad>.js dahil; common için includes/ ve assets/'in) koddaki
sabit anahtarlarından sözlükte karşılığı olmayanları JSON satırı olarak yazar (bilgi amaçlı; çıkış kodu 0).
Ayrıntı: docs/i18n.md
"""
import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
PLACEHOLDER = re.compile(r'\{(\w+)\}')
CONTEXT = re.compile(r'^(.+)\|([a-z_]+)$', re.S)

# Koddaki sabit anahtarlar: JS POps.t/tn/tx/tHtml/tnHtml/tNodes/taskName, PHP __/_e/__x/_ex
_STR = r"""('(?:[^'\\\n]|\\.)*'|"(?:[^"\\\n]|\\.)*")"""
JS_CALL = re.compile(r'POps\.(t|tn|tHtml|tnHtml|tNodes|tx|taskName)\(\s*' + _STR + r"(?:\s*,\s*'([a-z_]+)')?")
PHP_CALL = re.compile(r'(?<![\w$>])(__|_e|__x|_ex)\(\s*' + _STR + r"(?:\s*,\s*'([a-z_]+)')?")


def unquote(lit):
    body = lit[1:-1]
    return re.sub(r'\\(.)', lambda m: {'n': '\n', 't': '\t'}.get(m.group(1), m.group(1)), body)


def literal_keys(src):
    src = re.sub(r'^\s*(//|\*|#).*$', '', src, flags=re.M)  # yorum satırlarındaki örnekler sayılmaz
    keys = []
    for m in JS_CALL.finditer(src):
        text = unquote(m.group(2))
        if m.group(1) == 'taskName':
            keys.append(text + '|task')
        elif m.group(1) == 'tx':
            if m.group(3):
                keys.append(text + '|' + m.group(3))
        else:
            keys.append(text)
    for m in PHP_CALL.finditer(src):
        text = unquote(m.group(2))
        if m.group(1) in ('__x', '_ex'):
            if m.group(3):
                keys.append(text + '|' + m.group(3))
        else:
            keys.append(text)
    return keys


def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


def source_files(root, page=None):
    """page=None: bütün panel ve sunucu kaynakları; 'common': ortak kabuk; ad: o sayfa."""
    dash = os.path.join(root, 'Dashboard')
    if page == 'common':
        out = []
        for sub, ext in (('includes', '.php'), ('assets', '.js')):
            d = os.path.join(dash, sub)
            out += [os.path.join(d, n) for n in sorted(os.listdir(d)) if n.endswith(ext)]
        return out
    if page:
        # Sayfa ve varsa sayfa betiği (assets/pages/<ad>.js)
        js = os.path.join(dash, 'assets', 'pages', page + '.js')
        return [os.path.join(dash, page + '.php')] + ([js] if os.path.exists(js) else [])
    out = []
    for base, exts in ((dash, ('.php', '.js')), (os.path.join(root, 'Backend'), ('.py',))):
        for d, dirs, files in os.walk(base):
            dirs[:] = [x for x in dirs if x not in ('vendor', 'tests', '__pycache__', 'venv', '.venv')]
            out += [os.path.join(d, n) for n in sorted(files) if n.endswith(exts)]
    return out


def in_source(text, corpus):
    """Metin kaynakta bir dize sabiti olarak ('…', "…" ya da `…`) geçiyor mu."""
    body = text.replace('\\', '\\\\').replace('\n', '\\n')
    return any(q + body.replace(q, '\\' + q) + q in corpus for q in ('\'', '"', '`'))


def no_duplicates(pairs):
    seen = {}
    for k, v in pairs:
        if k in seen:
            raise ValueError('aynı anahtar iki kez: %r' % k)
        seen[k] = v
    return seen


def check_file(path, corpus, pages):
    errors = []
    name = os.path.splitext(os.path.basename(path))[0].split('.')[0]   # settings.tokens.json -> settings
    if name != 'common' and name not in pages:
        errors.append('%s: Dashboard/%s.php yok (ad "common" ya da bir sayfa olmalı)' % (path, name))
    try:
        data = json.loads(read(path), object_pairs_hook=no_duplicates)
    except ValueError as e:
        return errors + ['%s: geçersiz JSON: %s' % (path, e)], 0
    if not isinstance(data, dict):
        return errors + ['%s: kök bir nesne olmalı' % path], 0
    for key, value in data.items():
        where = '%s: %r' % (path, key)
        if not key.strip() or key != key.strip():
            errors.append('%s: anahtar boş ya da başında/sonunda boşluk var' % where)
        if isinstance(value, str):
            forms = {'': value}
        elif isinstance(value, dict) and 'other' in value and set(value) <= {'one', 'other'}:
            forms = value
        else:
            errors.append('%s: değer metin ya da {"one": ..., "other": ...} olmalı' % where)
            continue
        m = CONTEXT.match(key)
        text = m.group(1) if m else key
        want = sorted(set(PLACEHOLDER.findall(text)))
        for form, v in forms.items():
            label = ' (%s)' % form if form else ''
            if not isinstance(v, str) or not v.strip():
                errors.append('%s%s: çeviri boş' % (where, label))
                continue
            got = sorted(set(PLACEHOLDER.findall(v)))
            if got != want:
                errors.append('%s%s: yer tutucular uyuşmuyor: anahtarda %s, çeviride %s' % (where, label, want, got))
        if not in_source(text, corpus):
            errors.append('%s: Türkçe metin kaynakta dize olarak yok (koddakiyle harfi harfine aynı olmalı)' % where)
    return errors, len(data)


def lang_files(root):
    base = os.path.join(root, 'Dashboard', 'lang')
    out = []
    if os.path.isdir(base):
        for lang in sorted(os.listdir(base)):
            d = os.path.join(base, lang)
            if os.path.isdir(d):
                out += [os.path.join(d, n) for n in sorted(os.listdir(d)) if n.endswith('.json')]
    return out


def missing(root, page, lang):
    d = os.path.join(root, 'Dashboard', 'lang', lang)

    def load(name):
        # <ad>.json ve parça dosyaları <ad>.<parça>.json (panel ikisini de okur: includes/i18n.php)
        out = {}
        for n in sorted(os.listdir(d)) if os.path.isdir(d) else []:
            if n.startswith(name + '.') and n.endswith('.json') and n != name + '.json':
                out.update(json.loads(read(os.path.join(d, n))))
        p = os.path.join(d, name + '.json')
        out.update(json.loads(read(p)) if os.path.exists(p) else {})
        return out
    have = load('common')
    if page != 'common':
        have.update(load(page))
    seen, out = set(), []
    for path in source_files(root, page):
        for key in literal_keys(read(path)):
            if key not in have and key not in seen:
                seen.add(key)
                out.append(key)
    for key in out:
        print('    %s: ""' % json.dumps(key, ensure_ascii=False))
    print('%d anahtarın karşılığı yok (%s).' % (len(out), page), file=sys.stderr)
    return 0


def main(argv=None):
    ap = argparse.ArgumentParser(description='Panel çeviri sözlüklerini denetler.')
    ap.add_argument('--root', default=ROOT, help='depo kökü (varsayılan: bu betiğin iki üstü)')
    ap.add_argument('--missing', metavar='AD', help='bu sayfanın (ya da common) çevrilmemiş anahtarlarını yaz')
    ap.add_argument('--lang', default='en')
    args = ap.parse_args(argv)
    if args.missing:
        return missing(args.root, args.missing, args.lang)

    corpus = '\n'.join(read(p) for p in source_files(args.root))
    pages = {os.path.splitext(n)[0] for n in os.listdir(os.path.join(args.root, 'Dashboard')) if n.endswith('.php')}
    gh = os.environ.get('GITHUB_ACTIONS') == 'true'
    files = lang_files(args.root)
    errors, entries = [], 0
    for path in files:
        errs, n = check_file(path, corpus, pages)
        entries += n
        errors += [e.replace(args.root + os.sep, '') for e in errs]
    for e in errors:
        if gh:
            print('::error::%s' % e)
        print(e)
    print('%d sözlük, %d girdi denetlendi; %d hata.' % (len(files), entries, len(errors)))
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
