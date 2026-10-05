#!/usr/bin/env python3
"""Belgelerdeki sürüm ve teknoloji iddialarını tek kaynaklarına karşı denetler (CI: version işi). Yalnızca standart
kütüphane, Python 3.10+.

Tek kaynaklar (belgeler bunlarla çelişmemeli)
  VERSION                        kök VERSION dosyası (ör. 0.1.22-alpha)
  Python alt sınırı              Backend/requirements.txt "# requires-python: >=X.Y" (install.sh ve pops-deploy-backend
                                 de bu satırı okur)
  PostgreSQL alt sınırı          .github/workflows/ci.yml, migrations işinin servis imajı (postgres:N): şema en eski
                                 desteklenen sürümde sınanır
  Ajanın .NET'i                  release.yml'in yayımladığı ajan projelerinin <TargetFramework>'ü (netN.0[-windows])
  self-contained                 release.yml'deki "dotnet publish ... --self-contained true"

Taranan: README.md, README.tr.md, ROADMAP.md, SECURITY.md, CONTRIBUTING.md, docs/**/*.md (docs/tr dahil),
.github/workflows/release.yml (sürüm notu metni), .github/ISSUE_TEMPLATE/*.

Kurallar (RULES tablosu; yeni kural = bir düzenli ifade + bir denetim fonksiyonu)
  1. current: "latest/current/newest release|version", "son/güncel/mevcut sürüm" ifadesinin hemen ardından gelen
     sürüm VERSION olmalı. README*.md ve ROADMAP.md hiç sürüm adı taşımaz (releases/latest bağlantısı verilir), orada
     sürüm doğru olsa da hatadır.
  2. next: "next/upcoming/in progress", "sırada/sıradaki/sonraki/gelecek" ifadesinin hemen ardından gelen sürüm
     VERSION'dan büyük olmalı (yayımlanmış bir sürüm "sıradaki" olamaz). README*/ROADMAP'te her zaman hata.
  3. dotnet: ".NET N (Desktop) Runtime", ".NET Runtime N", "netN.0-windows" N'si ajanın .NET ana sürümü olmalı. Ajan
     self-contained ise ".NET N Runtime" ya da ".NET Desktop Runtime" için "requires/needs/gerekir..." iddiası
     (olumsuz değilse) hatadır: çalışma zamanı ajanla gelir.
  4. python: "Python X.Y+", "Python X.Y or newer / ve üstü / ya da daha yenisi", "requires/at least/en az Python X.Y",
     "requires-python >=X.Y" alt sınırı tek kaynaktakiyle aynı olmalı (daha düşük ya da daha yüksek: hata).
  5. postgres: aynısı "PostgreSQL N+", "PostgreSQL N or later" vb. için.

İstisnalar (3-5): eşleşmenin cümlesinde yayımlanmış bir POps sürümü (<= VERSION) geçiyorsa cümle geçmişi anlatır ("up to
0.1.14 the .NET 8 Desktop Runtime was required", "0.1.15 öncesi") ve kural uygulanmaz; 4-5'te alt sınırı değiştirme
planı ("Move the backend to Python 3.12 or newer") da sayılmaz. ROADMAP'in Done listesindeki "(0.1.11)" gibi anmalar
1-2'ye takılmaz: onlar yalnızca "latest/next" ifadesinin hemen ardındaki sürüme bakar. Bilerek bırakılan tek satır için
satıra "docs-versions: ignore" yazılır (Markdown'da <!-- docs-versions: ignore -->).

Çıkış: 0 temiz, 1 bulgu var (dosya:satır: ileti), 2 tek kaynaklardan biri okunamadı.
"""
import argparse
import glob
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..'))

SCAN = ['README.md', 'README.tr.md', 'ROADMAP.md', 'SECURITY.md', 'CONTRIBUTING.md', 'docs/**/*.md',
        '.github/workflows/release.yml', '.github/ISSUE_TEMPLATE/*']
VERSION_FREE = re.compile(r'^(README(\.[a-z]{2})?|ROADMAP)\.md$')   # sürüm adı hiç taşımayan sayfalar
IGNORE = 'docs-versions: ignore'

# --- ortak parçalar ---------------------------------------------------------------------------------------------
_VER = r'v?(?P<ver>\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?)'
_GAP = r'(?:[\s:=(*_`\[]|\bis\b|\bthe\b)*'   # "Latest release: **0.1.13-alpha**", "Next: the 0.1.14 round"
_OR_NEWER = (r'(?:\+|\s+or\s+(?:newer|later|above|higher)|\s+and\s+(?:newer|later|above|up)'
             r'|\s+ve\s+(?:üstü|üzeri|sonrası|daha\s+yeni\w*)'
             r'|\s+(?:ya\s+da|veya)\s+(?:daha\s+)?(?:yeni\w*|üstü|üzeri|sonrası))')
_AT_LEAST = r'(?:\b(?:requires|needs|at\s+least|minimum(?:\s+is)?|en\s+az)\s+|>=\s*)'
PRODUCT_VER = re.compile(r'(?<![\w.])v?(\d+)\.(\d+)\.(\d+)(?!\.?\d)')
SENTENCE_END = re.compile(r'[.!?](?=\s|$)|\n[ \t]*\n|\n[ \t]*(?:[-*+>]|\d+\.)\s|\n#|\|')
REQUIRES = re.compile(r'\b(?:requires?|required|needs?|needed|must\s+be\s+installed|install\s+the|gerekir|gerekli'
                      r'|gerekiyor|ister|kurulmalı|kurulu\s+olmalı|kurun)\b', re.I)
PLANNED = re.compile(r'\b(?:move|moving|raise|bump|upgrade|plan(?:ned)?)\b|taşı|yükselt|planlan', re.I)
NEGATED = re.compile(r'\b(?:no|not|nothing|without|never)\b|gerekmez|\byok\b|değil', re.I)


def first(m, *names):
    """Aynı anlamdaki adlı gruplardan (min, min2 ...) dolu olan ilki."""
    return next((m.group(n) for n in names if n in m.re.groupindex and m.group(n)), None)


def vtuple(v):
    return tuple(int(x) for x in re.match(r'v?(\d+)\.(\d+)\.(\d+)', v).groups())


def same_version(v, cur):
    """0.1.22 ve 0.1.22-alpha, VERSION 0.1.22-alpha ise aynıdır; 0.1.22-beta değildir."""
    base, _, suffix = v.partition('-')
    cbase, _, csuffix = cur.partition('-')
    return base == cbase and suffix in ('', csuffix)


def sentence(text, start, end):
    s = 0
    for m in SENTENCE_END.finditer(text, max(0, start - 800), start):
        s = m.end()
    m = SENTENCE_END.search(text, end)
    return text[s:m.start() if m else len(text)]


def exempt(ctx, m):
    """Geçmişe dair ya da planlanan bir değişikliği anlatan cümle (kural 4-5)."""
    return historical(ctx, m) or bool(PLANNED.search(sentence(ctx['text'], m.start(), m.end())))


def historical(ctx, m):
    """Cümlede yayımlanmış bir POps sürümü (<= VERSION) geçiyor mu: "up to 0.1.14 ... was required", "before 0.1.22"."""
    cur = vtuple(ctx['version'])
    for v in PRODUCT_VER.finditer(sentence(ctx['text'], m.start(), m.end())):
        t = tuple(int(x) for x in v.groups())
        if t <= cur:
            return True
    return False


# --- kurallar ---------------------------------------------------------------------------------------------------
def check_current(m, ctx):
    v = m.group('ver')
    if ctx['version_free']:
        return 'README/ROADMAP sürüm adı taşımaz ("%s %s"); releases/latest bağlantısı verin' % (
            m.group('phrase'), v)
    if not same_version(v, ctx['version']):
        return '"%s" %s diyor, VERSION %s' % (m.group('phrase'), v, ctx['version'])


def check_next(m, ctx):
    v = m.group('ver')
    if ctx['version_free']:
        return 'README/ROADMAP sürüm adı taşımaz ("%s %s"); planı sürümsüz yazın' % (m.group('phrase'), v)
    if vtuple(v) <= vtuple(ctx['version']):
        return '"%s" %s diyor, ama %s yayımlandı (VERSION %s)' % (m.group('phrase'), v, v, ctx['version'])


def check_dotnet(m, ctx):
    if historical(ctx, m):
        return None
    n = first(m, 'n1', 'n2', 'n3')
    if n and int(n) != ctx['dotnet']:
        return '"%s": ajan .NET %d hedefler (csproj TargetFramework net%d.0)' % (
            m.group(0).strip(), ctx['dotnet'], ctx['dotnet'])
    if ctx['self_contained'] and not m.group('n3') and (n or m.group('desk')):
        s = sentence(ctx['text'], m.start(), m.end())
        if REQUIRES.search(s) and not NEGATED.search(s):
            return ('"%s" gerekiyor deniyor, ama ajan self-contained: .NET çalışma zamanı ajanla gelir, uç noktada '
                    'kurulum gerekmez (release.yml --self-contained true)' % m.group(0).strip())


def check_python(m, ctx):
    got = tuple(int(x) for x in first(m, 'min', 'min2', 'min3').split('.'))
    if got != ctx['python'] and not exempt(ctx, m):
        return '"%s": Backend en az Python %d.%d ister (Backend/requirements.txt requires-python)' % (
            m.group(0).strip(), *ctx['python'])


def check_postgres(m, ctx):
    if int(first(m, 'min', 'min2')) != ctx['postgres'] and not exempt(ctx, m):
        return '"%s": en düşük desteklenen PostgreSQL %d (CI migrations işi postgres:%d üzerinde sınar)' % (
            m.group(0).strip(), ctx['postgres'], ctx['postgres'])


RULES = [
    ('current', re.compile(
        r'(?P<phrase>\b(?:latest|current|newest|most\s+recent)\s+(?:stable\s+|public\s+)?(?:release|version)\b'
        r'|(?<!\w)(?:en\s+)?(?:son|güncel|mevcut|şimdiki|en\s+yeni)\s+(?:kararlı\s+)?(?:sürüm|versiyon)\w*)'
        + _GAP + _VER, re.I), check_current),
    ('next', re.compile(
        r'(?P<phrase>(?:\b(?:next|upcoming|in\s+progress)\b'
        r'|(?<!\w)(?:sırada(?:ki)?|sonraki|gelecek|yaklaşan)(?!\w))(?:\s+(?:release|version|sürüm)\w*)?)'
        + _GAP + _VER, re.I), check_next),
    ('dotnet', re.compile(
        r'\.NET\s+(?:Desktop\s+)?Runtime\s+(?P<n2>\d+)'
        r'|\.NET\s+(?:(?P<n1>\d+)(?:\.\d+|\.x)*\s+)?(?P<desk>Desktop\s+)?(?:Runtime|çalışma\s+zaman)'
        r'|\bnet(?P<n3>\d+)\.\d+-windows\b', re.I), check_dotnet),
    ('python', re.compile(
        r'\bPython\s+v?(?P<min>\d+\.\d+)(?:\.\d+)?' + _OR_NEWER
        + r'|' + _AT_LEAST + r'Python\s+v?(?P<min2>\d+\.\d+)'
        + r'|(?:requires-python|python_requires)\s*[:=]\s*["\']?\s*>=\s*(?P<min3>\d+\.\d+)', re.I), check_python),
    ('postgres', re.compile(
        r'\bPostgre(?:SQL|s)\s+v?(?P<min>\d+)(?:\.\d+)?' + _OR_NEWER
        + r'|' + _AT_LEAST + r'Postgre(?:SQL|s)\s+v?(?P<min2>\d+)', re.I), check_postgres),
]


# --- tek kaynaklar ----------------------------------------------------------------------------------------------
def read(path):
    with open(path, encoding='utf-8') as f:
        return f.read()


def load_facts(root):
    """Tek kaynakları okur; okunamayanı açıklayan bir hata listesiyle döner."""
    facts, errors = {}, []

    def src(rel):
        try:
            return read(os.path.join(root, rel))
        except OSError as e:
            errors.append('%s okunamadı: %s' % (rel, e.strerror))
            return ''

    facts['version'] = src('VERSION').strip()
    if facts['version'] and not re.match(r'^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$', facts['version']):
        errors.append('VERSION a.b.c[-etiket] biçiminde değil: %r' % facts['version'])

    m = re.search(r'^#\s*requires-python:\s*>=\s*(\d+)\.(\d+)', src('Backend/requirements.txt'), re.M)
    if m:
        facts['python'] = (int(m.group(1)), int(m.group(2)))
    else:
        errors.append('Backend/requirements.txt: "# requires-python: >=X.Y" satırı yok')

    ci = src('.github/workflows/ci.yml')
    job = re.search(r'^  migrations:\s*\n((?:[ \t]*\n|    .*\n)*)', ci, re.M)
    m = job and re.search(r'^\s*image:\s*["\']?postgres:(\d+)', job.group(1), re.M)
    if m:
        facts['postgres'] = int(m.group(1))
    else:
        errors.append('.github/workflows/ci.yml: migrations işinde "image: postgres:N" yok')

    release = src('.github/workflows/release.yml')
    sc = re.search(r'dotnet\s+publish\b[^\n]*?(?:--self-contained|-p:SelfContained=)[\s=]*(true|false)?', release, re.I)
    facts['self_contained'] = bool(sc) and (sc.group(1) or 'true').lower() == 'true'
    projects = re.findall(r'"(Agent/[^"\s]+\.csproj)"', release)   # yayımlanan ajan bileşenleri
    majors = {}
    for proj in projects:
        tfm = re.search(r'<TargetFramework>\s*net(\d+)\.\d+', src(proj))
        if tfm:
            majors[proj] = int(tfm.group(1))
        else:
            errors.append('%s: <TargetFramework>netN.0...</TargetFramework> yok' % proj)
    if not projects:
        errors.append('.github/workflows/release.yml: yayımlanan ajan projeleri ("Agent/....csproj") bulunamadı')
    elif len(set(majors.values())) > 1:
        errors.append('ajan projeleri farklı .NET sürümleri hedefliyor: %s' % majors)
    elif majors:
        facts['dotnet'] = next(iter(majors.values()))
    return facts, errors


def scan_files(root):
    out = []
    for pattern in SCAN:
        for p in sorted(glob.glob(os.path.join(root, pattern), recursive=True)):
            if os.path.isfile(p) and p not in out:
                out.append(p)
    return out


def check_text(rel, text, facts):
    """(satır, kural, ileti) listesi."""
    ctx = dict(facts, text=text, version_free=bool(VERSION_FREE.match(rel)))
    lines = text.split('\n')
    found = []
    for name, rx, check in RULES:
        for m in rx.finditer(text):
            line = text.count('\n', 0, m.start()) + 1
            if IGNORE in lines[line - 1]:
                continue
            msg = check(m, ctx)
            if msg and (line, name, msg) not in found:
                found.append((line, name, msg))
    return sorted(found)


def main(argv=None):
    ap = argparse.ArgumentParser(description='Belgelerdeki sürüm ve teknoloji iddialarını denetler.')
    ap.add_argument('--root', default=ROOT, help='depo kökü (varsayılan: bu betiğin bir üstü)')
    args = ap.parse_args(argv)
    gh = os.environ.get('GITHUB_ACTIONS') == 'true'

    facts, errors = load_facts(args.root)
    if errors:
        for e in errors:
            if gh:
                print('::error::%s' % e)
            print(e)
        return 2

    files = scan_files(args.root)
    count = 0
    for path in files:
        rel = os.path.relpath(path, args.root).replace(os.sep, '/')
        for line, name, msg in check_text(rel, read(path), facts):
            count += 1
            if gh:
                print('::error file=%s,line=%d::%s' % (rel, line, msg))
            print('%s:%d: [%s] %s' % (rel, line, name, msg))
    print('%d dosya tarandı (VERSION %s, Python >=%d.%d, PostgreSQL >=%d, ajan .NET %d%s); %d bulgu.' % (
        len(files), facts['version'], facts['python'][0], facts['python'][1], facts['postgres'], facts['dotnet'],
        ', self-contained' if facts['self_contained'] else '', count))
    return 1 if count else 0


if __name__ == '__main__':
    sys.exit(main())
