#!/usr/bin/env python3
"""CI: Markdown dosyalarındaki göreceli bağlantıları ve başlık çapalarını doğrular.

Git tarafından takip edilen her Markdown dosyasında göreceli bağlantıları, görsel yollarını
ve satır içi HTML etiketlerini (href, src, srcset) bulur; hedef dosyanın varlığını ve
#çapa varsa hedef dosyadaki başlıkla (GitHub kurallarına göre) eşleştiğini denetler.
Python 3.10 uyumlu, yalnızca standart kütüphane.
"""
import os
import re
import subprocess
import sys
import urllib.parse
from collections import defaultdict

# Kod bloklarını ve satır içi kodları kaldırmak için desenler
RE_FENCED_CODE = re.compile(r"(```.*?```|~~~.*?~~~)", re.DOTALL)
RE_INLINE_CODE = re.compile(r"`[^`\n]+`")

# Markdown başlık deseni (# Başlık)
RE_HEADING = re.compile(r"^#{1,6}\s+(.*?)(?:\s+#+)?$")

# Markdown bağlantı ve görsel deseni: [metin](hedef) veya ![alt](hedef)
RE_MD_LINK = re.compile(r"!?(?:\[(?:[^\]\\]|\\.)*\])\(([^)\s]+)(?:\s+[\"'].*?[\"'])?\)")

# Satır içi HTML etiketleri: <a href="...">, <img src="...">, <source src="...">
RE_HTML_LINK = re.compile(r'<(?:a|img|source)\s+[^>]*(?:href|src)=["\']([^"\']+)["\']', re.IGNORECASE)

# HTML srcset özniteliği (<picture> ve <img> blokları)
RE_HTML_SRCSET = re.compile(r'<source\s+[^>]*srcset=["\']([^"\']+)["\']', re.IGNORECASE)

# Başlıktaki HTML çapa etiketleri (<a id="..." name="...">)
RE_HTML_ANCHOR = re.compile(r'<a\s+[^>]*(?:id|name)=["\']([^"\']+)["\']', re.IGNORECASE)


def get_tracked_md_files(repo_root: str) -> list[str]:
    """Git tarafından takip edilen tüm Markdown dosyalarının göreceli yollarını döndürür."""
    res = subprocess.run(
        ["git", "ls-files", "*.md"],
        cwd=repo_root,
        capture_output=True,
        text=True,
        check=True,
    )
    return [line.strip().replace("\\", "/") for line in res.stdout.splitlines() if line.strip()]


def slugify_heading(heading: str) -> str:
    """GitHub başlık çapa algoritması: küçük harfe çevir, biçimlendirmeyi ve noktalama işaretlerini sil."""
    # Satır içi kod bloklarının içeriğini koru, ters tırnakları sil
    h = re.sub(r"`([^`]+)`", r"\1", heading)
    # Bağlantı ve görsellerin metin kısmını koru
    h = re.sub(r"!\[([^\]]*)\]\([^)]+\)", r"\1", h)
    h = re.sub(r"\[([^\]]*)\]\([^)]+\)", r"\1", h)
    # HTML etiketlerini kaldır
    h = re.sub(r"<[^>]+>", "", h)
    # Kalın ve italik yıldızlarını kaldır
    h = re.sub(r"\*{1,3}([^*]+)\*{1,3}", r"\1", h)
    # Küçük harfe çevir
    h = h.strip().lower()
    # Harf, rakam, alt tire, boşluk ve tire harici işaretleri kaldır (Türkçe karakterler korunur)
    h = re.sub(r"[^\w\s-]", "", h)
    # Boşluk ve ardışık tireleri tek tireye indir
    h = re.sub(r"[-\s]+", "-", h).strip("-")
    return h


def extract_headings(content: str) -> set[str]:
    """Markdown içeriğindeki tüm başlıkların GitHub çapalarını ve satır içi HTML id/name değerlerini çıkarır."""
    c = RE_FENCED_CODE.sub("", content)
    slugs = set()
    seen = defaultdict(int)

    for line in c.splitlines():
        m = RE_HEADING.match(line)
        if m:
            s = slugify_heading(m.group(1))
            if s:
                if s in seen:
                    seen[s] += 1
                    slugs.add(f"{s}-{seen[s]}")
                else:
                    seen[s] = 0
                    slugs.add(s)
        # Satır içindeki özel <a id="..." veya <a name="..."> etiketleri
        for am in RE_HTML_ANCHOR.finditer(line):
            slugs.add(am.group(1).lower())

    return slugs


def extract_links(content: str) -> list[str]:
    """Kod blokları dışındaki Markdown ve HTML bağlantılarını çıkarır."""
    c = RE_FENCED_CODE.sub("", content)
    c = RE_INLINE_CODE.sub("", c)

    links = []
    # [metin](link) ve ![alt](link)
    for m in RE_MD_LINK.finditer(c):
        links.append(m.group(1))

    # <a href="...">, <img src="...">
    for m in RE_HTML_LINK.finditer(c):
        links.append(m.group(1))

    # <source srcset="...">
    for m in RE_HTML_SRCSET.finditer(c):
        for part in m.group(1).split(","):
            url = part.strip().split()[0] if part.strip() else ""
            if url:
                links.append(url)

    return links


def main() -> None:
    repo_root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    md_files = get_tracked_md_files(repo_root)

    headings_cache: dict[str, set[str]] = {}
    for f in md_files:
        full_path = os.path.join(repo_root, f)
        if os.path.isfile(full_path):
            with open(full_path, "r", encoding="utf-8", errors="replace") as fp:
                headings_cache[f] = extract_headings(fp.read())

    errors: list[str] = []

    for f in md_files:
        full_path = os.path.join(repo_root, f)
        if not os.path.isfile(full_path):
            continue

        with open(full_path, "r", encoding="utf-8", errors="replace") as fp:
            content = fp.read()

        for raw_link in extract_links(content):
            # Dış URL'leri (http, https, mailto vb.) atla
            if re.match(r"^[a-zA-Z][a-zA-Z0-9+.-]*:", raw_link):
                continue
            # Boş veya tek '#' işaretini atla
            if not raw_link or raw_link == "#":
                continue

            parsed = urllib.parse.urlparse(raw_link)
            path_part = urllib.parse.unquote(parsed.path)
            fragment = urllib.parse.unquote(parsed.fragment)

            # Hedef dosya yolu
            if not path_part:
                target_rel = f
            else:
                target_rel = os.path.normpath(os.path.join(os.path.dirname(f), path_part)).replace("\\", "/")

            target_full = os.path.join(repo_root, target_rel)
            if not os.path.exists(target_full):
                errors.append(f"{f} -> {raw_link}: hedef dosya bulunamadi ({target_rel})")
                continue

            # Markdown dosyasıysa #çapa kontrolü
            if fragment and target_rel.endswith(".md"):
                target_headings = headings_cache.get(target_rel)
                if target_headings is None and os.path.isfile(target_full):
                    with open(target_full, "r", encoding="utf-8", errors="replace") as fp:
                        target_headings = extract_headings(fp.read())
                    headings_cache[target_rel] = target_headings

                if target_headings is not None:
                    frag_slug = fragment.lower().strip("-")
                    if frag_slug not in target_headings:
                        errors.append(f"{f} -> {raw_link}: #{fragment} capasi {target_rel} icinde bulunamadi")

    if errors:
        print(f"HATA: {len(errors)} kirik baglanti tespit edildi:")
        for err in errors:
            print(f"  {err}")
        sys.exit(1)

    print(f"TAMAM: {len(md_files)} Markdown dosyasindaki tum goreceli baglantilar ve capalar dogrulandi.")


if __name__ == "__main__":
    main()
