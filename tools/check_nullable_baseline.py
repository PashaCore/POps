#!/usr/bin/env python3
"""Ajanın nullable tabanının büyümediğini denetler (CI: version işi). Yalnızca standart kütüphane, Python 3.9+.

Ajanın ürün projelerinde (POps.Tests dışında) <Nullable>enable</Nullable> açıktır. Nullable açısından temiz olmayan
eski dosyalar bir "#nullable disable" satırı taşır; bu dosyaların sabit listesi tools/nullable_baseline.txt'dedir ve
yalnızca küçülür (kural: Agent/Directory.Build.props'taki uyarı kapısı yorumu, CONTRIBUTING.md "Warnings are errors").

Taranan: Agent/ ve Installer/agent/CustomActions/ altındaki .cs dosyaları; Agent/POps.Tests, bin/ ve obj/ hariç.
Dosyanın projesi, içinde .csproj bulunan en yakın üst klasördür; Agent/ altına eklenen yeni bir proje de taranır.

Nullable'ı kapatan sayılan yönerge: satır başında (önünde yalnızca boşluk) "#nullable disable", "#nullable disable
warnings" ya da "#nullable disable annotations". Yalnızca uyarıları ya da yalnızca ek açıklamaları kapatmak da
temizlenmemiş koddur, o yüzden üçü de sayılır; "#nullable enable" ve "#nullable restore" sayılmaz. Dosya başındaki
UTF-8 BOM ve CRLF satır sonları sonucu değiştirmez.

Bulgular
  1. Listede olmayan bir dosya nullable'ı kapatıyor: yeni dosya nullable açısından temiz olmalı (satırı silin,
     uyarıları düzeltin). Yalnızca listedeki bir dosyadan taşınan kod (dosya adı değişikliği, Worker.cs'yi bölme
     adımları) yeni yoluyla listeye yazılabilir; liste değişikliği incelemede görünür.
  2. Listedeki dosya yok ya da artık nullable'ı kapatmıyor: satırını listeden silin (liste böyle küçülür).
  3. Liste biçimi bozuk: yol depo köküne göre ve ileri eğik çizgiyle (/) yazılır, liste sıralıdır (Python sorted(),
     yani LC_ALL=C sort sırası) ve her yol bir kez geçer.
  4. Bir ürün projesinin csproj'unda <Nullable>enable</Nullable> yok ya da başka bir değer var.

Kullanım: python tools/check_nullable_baseline.py [--root DEPO] [--baseline LISTE]
Çıkış: 0 temiz (proje başına dosya sayısı yazılır), 1 bulgu var (dosya:satır: ileti), 2 liste ya da taranan klasörler
okunamadı.
"""
import argparse
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..'))
DEFAULT_BASELINE = os.path.join(HERE, 'nullable_baseline.txt')

SCAN = ['Agent', 'Installer/agent/CustomActions']   # ajanın ürün projelerinin bulunduğu klasörler
SKIP_DIRS = {'bin', 'obj'}
SKIP_PATHS = {'Agent/POps.Tests'}                   # test projesi nullable kapısının dışında
# C# önişlemci yönergeleri büyük/küçük harfe duyarlıdır; "#" ile "nullable" arasında boşluk ve sonda yorum olabilir
DISABLE = re.compile(r'^[ \t]*#[ \t]*nullable[ \t]+disable\b', re.M)
NULLABLE_PROP = re.compile(r'<Nullable>\s*([^<]*?)\s*</Nullable>')


def read_text(path):
    """utf-8-sig: baştaki BOM yönergeyi gizlemesin; metin kipi CRLF'yi \\n'e çevirir."""
    with open(path, encoding='utf-8-sig', errors='replace') as f:
        return f.read()


def relpath(path, root):
    return os.path.relpath(path, root).replace(os.sep, '/')


def scan(root):
    """({yol: (proje, nullable'ı kapatan ilk satırın numarası ya da 0)}, {proje: csproj yolu})."""
    files, projects = {}, {}
    for top in SCAN:
        owner = {}   # klasör -> proje adı (en yakın .csproj)
        for d, dirs, names in os.walk(os.path.join(root, top)):
            here = relpath(d, root)
            dirs[:] = sorted(x for x in dirs if x not in SKIP_DIRS and '%s/%s' % (here, x) not in SKIP_PATHS)
            csprojs = sorted(n for n in names if n.endswith('.csproj'))
            if csprojs:
                project = os.path.splitext(csprojs[0])[0]
                for c in csprojs:
                    projects[os.path.splitext(c)[0]] = '%s/%s' % (here, c)
            else:
                project = owner.get(os.path.dirname(d), here)
            owner[d] = project
            for name in sorted(names):
                if name.endswith('.cs'):
                    text = read_text(os.path.join(d, name))
                    m = DISABLE.search(text)
                    files['%s/%s' % (here, name)] = (project, text.count('\n', 0, m.start()) + 1 if m else 0)
    return files, projects


def load_baseline(path):
    """[(satır, yol)]; boş satırlar ve # ile başlayan yorumlar atlanır."""
    with open(path, encoding='utf-8-sig') as f:
        return [(n, s) for n, s in ((n, line.strip()) for n, line in enumerate(f, 1)) if s and not s.startswith('#')]


def check(root, baseline_path):
    """(bulgular [(dosya, satır, ileti)], files, projects)."""
    findings = []
    files, projects = scan(root)
    base_rel = relpath(baseline_path, root)

    for csproj in sorted(projects.values()):
        values = NULLABLE_PROP.findall(read_text(os.path.join(root, csproj)))
        if not values or any(v != 'enable' for v in values):
            findings.append((csproj, 0, '<Nullable>enable</Nullable> olmalı (bulunan: %s); nullable bir ürün '
                             'projesinde kapatılmaz' % (', '.join(values) or 'yok')))

    listed, prev = {}, None
    for n, path in load_baseline(baseline_path):
        if '\\' in path:
            findings.append((base_rel, n, '"%s": yolu ileri eğik çizgiyle (/) yazın' % path))
        if path in listed:
            findings.append((base_rel, n, '"%s" listede iki kez var (%d. satırda da)' % (path, listed[path])))
            continue
        if prev is not None and path < prev:
            findings.append((base_rel, n, 'liste sıralı olmalı: "%s", bir önceki satırdaki "%s" yolundan önce gelir'
                             % (path, prev)))
        listed[path] = n
        prev = path

    for path, (_, line) in sorted(files.items()):
        if line and path not in listed:
            findings.append((path, line, '"#nullable disable" listede (%s) olmayan bir dosyada: yeni dosya nullable '
                             'açısından temiz olmalı, satırı silip uyarıları düzeltin. Yalnızca listedeki bir dosyadan '
                             'taşınan kod (ad değişikliği, Worker.cs bölme adımı) yeni yoluyla listeye yazılabilir'
                             % base_rel))
    for path, n in sorted(listed.items(), key=lambda item: item[1]):
        if path not in files:
            findings.append((base_rel, n, '%s taranan ürün dosyaları arasında yok (silindi, taşındı ya da adı '
                             'değişti): satırı listeden silin' % path))
        elif not files[path][1]:
            findings.append((base_rel, n, '%s artık nullable\'ı kapatmıyor: satırı listeden silin (taban böyle '
                             'küçülür)' % path))
    return findings, files, projects


def main(argv=None):
    ap = argparse.ArgumentParser(description='Ajanın "#nullable disable" tabanının büyümediğini denetler.')
    ap.add_argument('--root', default=ROOT, help='depo kökü (varsayılan: bu betiğin bir üstü)')
    ap.add_argument('--baseline', default=DEFAULT_BASELINE, help='taban listesi (varsayılan: %(default)s)')
    args = ap.parse_args(argv)
    gh = os.environ.get('GITHUB_ACTIONS') == 'true'

    errors = ['%s okunamadı: klasör yok' % top for top in SCAN if not os.path.isdir(os.path.join(args.root, top))]
    if not os.path.isfile(args.baseline):
        errors.append('%s okunamadı: dosya yok' % args.baseline)
    if errors:
        for e in errors:
            if gh:
                print('::error::%s' % e)
            print(e)
        return 2

    findings, files, projects = check(args.root, args.baseline)
    for path, line, msg in findings:
        if gh:
            print('::error file=%s%s::%s' % (path, ',line=%d' % line if line else '', msg))
        print('%s%s: %s' % (path, ':%d' % line if line else '', msg))

    counts = {p: 0 for p in projects}
    for project, line in files.values():
        if line:
            counts[project] = counts.get(project, 0) + 1
    per_project = ', '.join('%s %d' % (p, c) for p, c in sorted(counts.items(), key=lambda pc: (-pc[1], pc[0])))
    print("%d projede %d .cs dosyası tarandı; nullable'ı kapatan %d dosya (%s); %d bulgu." % (
        len(projects), len(files), sum(counts.values()), per_project, len(findings)))
    return 1 if findings else 0


if __name__ == '__main__':
    sys.exit(main())
