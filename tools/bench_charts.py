#!/usr/bin/env python3
"""Kapasite ölçümlerinden (docs/kapasite/olcum.json) rapor grafiklerini SVG olarak üretir.

Bağımlılık yok. Her grafik açık ve koyu tema için iki dosya olarak yazılır (<ad>.svg, <ad>-koyu.svg); rapor
GitHub'ın <picture> etiketiyle temaya göre birini gösterir. Renkler dataviz varsayılan paletinden (doğrulanmış
kategorik 1. ve 2. yuva); tek eksen, ince çizgiler, seçici doğrudan etiketler. Python 3.10+ uyumlu.

Örnek:
    python3 tools/bench_charts.py docs/kapasite/olcum.json docs/kapasite/
"""

import json
import math
import os
import sys

THEMES = {
    "": {"surface": "#fcfcfb", "text": "#0b0b0b", "text2": "#52514e", "grid": "#e4e3df", "axis": "#b9b7b0",
         "s1": "#2a78d6", "s2": "#eb6834"},
    "-koyu": {"surface": "#1a1a19", "text": "#ffffff", "text2": "#c3c2b7", "grid": "#34332f", "axis": "#5a5954",
              "s1": "#3987e5", "s2": "#d95926"},
}
W, H = 720, 380
L, R, T, B = 72, 150, 56, 64   # sağda doğrudan etiketler için pay
FONT = "font-family=\"-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif\""


def esc(s):
    return str(s).replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def nice_max(v):
    if v <= 0:
        return 1
    e = 10 ** math.floor(math.log10(v))
    for m in (1, 1.2, 1.6, 2, 2.4, 3.2, 4, 4.8, 6, 8, 10):   # dörde tam bölünen tavanlar: tam sayı çizgiler
        if m * e >= v:
            return m * e
    return 10 * e


def fmt(v):
    if isinstance(v, float) and not v.is_integer():
        return ("%.1f" % v).replace(".", ",")
    v = int(round(v))
    return "{:,}".format(v).replace(",", ".")


def frame(t, title, subtitle, xlab, ylab, xs, ymax, xlabels=None):
    pw, ph = W - L - R, H - T - B
    out = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 %d %d" width="%d" height="%d" role="img" '
           'aria-label="%s">' % (W, H, W, H, esc(title)),
           '<rect width="%d" height="%d" rx="8" fill="%s"/>' % (W, H, t["surface"]),
           '<text x="%d" y="24" %s font-size="15" font-weight="600" fill="%s">%s</text>'
           % (L, FONT, t["text"], esc(title)),
           '<text x="%d" y="42" %s font-size="12" fill="%s">%s</text>' % (L, FONT, t["text2"], esc(subtitle))]
    for i in range(5):
        v = ymax * i / 4
        y = T + ph - ph * i / 4
        out.append('<line x1="%d" x2="%d" y1="%.1f" y2="%.1f" stroke="%s" stroke-width="1"/>'
                   % (L, L + pw, y, y, t["grid"] if i else t["axis"]))
        out.append('<text x="%d" y="%.1f" %s font-size="11" text-anchor="end" fill="%s">%s</text>'
                   % (L - 8, y + 4, FONT, t["text2"], fmt(v)))
    out.append('<text x="16" y="%d" %s font-size="11" fill="%s" transform="rotate(-90 16 %d)" '
               'text-anchor="middle">%s</text>' % (T + ph / 2, FONT, t["text2"], T + ph / 2, esc(ylab)))
    out.append('<text x="%d" y="%d" %s font-size="11" text-anchor="middle" fill="%s">%s</text>'
               % (L + pw / 2, H - 16, FONT, t["text2"], esc(xlab)))
    return out, pw, ph


def xpos(x, xs, pw):
    lo, hi = min(xs), max(xs)
    return L + (x - lo) / (hi - lo) * pw if hi > lo else L + pw / 2


def line_chart(t, title, subtitle, xlab, ylab, xs, series, unit="", ymax=None, fit=None):
    """series: [(ad, [y...], renk-anahtarı)]; fit: (a, b, etiket) -> y = a + b·x kesikli çizgi."""
    top = max(max(v for v in s[1] if v is not None) for s in series)
    if fit:
        top = max(top, fit[0] + fit[1] * max(xs))
    ymax = ymax or nice_max(top * 1.1)
    out, pw, ph = frame(t, title, subtitle, xlab, ylab, xs, ymax)
    last = None
    for x in reversed(xs):   # sağdan: birbirine çok yakın etiketlerde soldaki atlanır (işaret ve ipucu kalır)
        px = xpos(x, xs, pw)
        if last is not None and last - px < 44:
            continue
        last = px
        out.append('<text x="%.1f" y="%d" %s font-size="11" text-anchor="middle" fill="%s">%s</text>'
                   % (px, T + ph + 18, FONT, t["text2"], fmt(x)))
    Y = lambda v: T + ph - ph * v / ymax  # noqa: E731
    if fit:
        a, b, lab = fit
        x0, x1 = min(xs), max(xs)
        out.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="1.5" '
                   'stroke-dasharray="5 4"/>'
                   % (xpos(x0, xs, pw), Y(a + b * x0), xpos(x1, xs, pw), Y(a + b * x1), t["text2"]))
        xm = x0 + (x1 - x0) * 0.62   # etiket çizginin altında, ortaya yakın (uç etiketleriyle çakışmasın)
        out.append('<text x="%.1f" y="%.1f" %s font-size="11" fill="%s">%s</text>'
                   % (xpos(xm, xs, pw) + 6, Y(a + b * xm) + 22, FONT, t["text2"], esc(lab)))
    for name, ys, key in series:
        pts = [(xpos(x, xs, pw), Y(y), y, x) for x, y in zip(xs, ys) if y is not None]
        out.append('<polyline fill="none" stroke="%s" stroke-width="2" stroke-linejoin="round" points="%s"/>'
                   % (t[key], " ".join("%.1f,%.1f" % (px, py) for px, py, _, _ in pts)))
        for px, py, y, x in pts:
            out.append('<circle cx="%.1f" cy="%.1f" r="4.5" fill="%s" stroke="%s" stroke-width="2">'
                       '<title>%s cihaz: %s%s</title></circle>' % (px, py, t[key], t["surface"], fmt(x), fmt(y), unit))
        lx, ly, ly_v, _ = pts[-1]
        out.append('<text x="%.1f" y="%.1f" %s font-size="12" font-weight="600" fill="%s">%s</text>'
                   % (lx + 10, ly - 6, FONT, t["text"], esc(name)))
        out.append('<text x="%.1f" y="%.1f" %s font-size="11" fill="%s">%s%s</text>'
                   % (lx + 10, ly + 9, FONT, t["text2"], fmt(ly_v), unit))
    out.append("</svg>")
    return "\n".join(out)


def bar_chart(t, title, subtitle, xlab, ylab, groups, series, unit=""):
    """Gruplanmış çubuk: groups = x etiketleri, series = [(ad, [y...], renk-anahtarı)]."""
    top = max(max(s[1]) for s in series)
    ymax = nice_max(top * 1.1)
    out, pw, ph = frame(t, title, subtitle, xlab, ylab, groups, ymax)
    gw = pw / len(groups)
    bw = min(34, (gw - 24) / len(series))
    Y = lambda v: T + ph - ph * v / ymax  # noqa: E731
    for gi, g in enumerate(groups):
        cx = L + gw * gi + gw / 2
        out.append('<text x="%.1f" y="%d" %s font-size="11" text-anchor="middle" fill="%s">%s</text>'
                   % (cx, T + ph + 18, FONT, t["text2"], fmt(g)))
        x0 = cx - (bw * len(series) + 2 * (len(series) - 1)) / 2
        for si, (name, ys, key) in enumerate(series):
            v = ys[gi]
            x = x0 + si * (bw + 2)
            h = max(ph * v / ymax, 1.5)
            r = min(4, h / 2)
            # yalnızca üst köşeler yuvarlak, taban düz (çubuk tabana oturur)
            out.append('<path d="M%.1f,%.1f v%.1f a%.1f,%.1f 0 0 1 %.1f,-%.1f h%.1f a%.1f,%.1f 0 0 1 %.1f,%.1f '
                       'v%.1f z" fill="%s"><title>%s, %s cihaz: %s%s</title></path>'
                       % (x, T + ph, -(h - r), r, r, r, r, bw - 2 * r, r, r, r, r, h - r, t[key],
                          esc(name), fmt(g), fmt(v), unit))
            out.append('<text x="%.1f" y="%.1f" %s font-size="11" text-anchor="middle" fill="%s">%s%s</text>'
                       % (x + bw / 2, Y(v) - 6, FONT, t["text"], fmt(v), unit))
    # açıklama (iki seri: renk tek başına kimlik taşımasın diye metinle)
    lx = L + pw + 16
    for si, (name, _, key) in enumerate(series):
        ly = T + 8 + si * 22
        out.append('<rect x="%d" y="%d" width="12" height="12" rx="3" fill="%s"/>' % (lx, ly, t[key]))
        out.append('<text x="%d" y="%d" %s font-size="12" fill="%s">%s</text>'
                   % (lx + 18, ly + 10, FONT, t["text"], esc(name)))
    out.append("</svg>")
    return "\n".join(out)


def main():
    data = json.load(open(sys.argv[1], encoding="utf-8"))
    outdir = sys.argv[2]
    os.makedirs(outdir, exist_ok=True)
    rows = data["olcumler"]
    xs = [r["cihaz"] for r in rows]
    # doğrusal model: bellek = a + b·cihaz (en küçük kareler)
    n = len(xs)
    mx = sum(xs) / n
    my = sum(r["backend_mb"] for r in rows) / n
    b = sum((x - mx) * (r["backend_mb"] - my) for x, r in zip(xs, rows)) / sum((x - mx) ** 2 for x in xs)
    a = my - b * mx
    data["bellek_modeli"] = {"taban_mb": round(a, 1), "cihaz_basi_mb": round(b, 3)}
    before = data["duzeltme_oncesi"]
    for suffix, t in THEMES.items():
        charts = {
            "toparlanma": line_chart(
                t, "Sunucu yeniden başladıktan sonra tüm cihazların geri bağlanma süresi",
                "Hepsi aynı anda bağlanmaya çalışıyor; ajanın gerçek rastgele geri çekilmesiyle. Başarısız deneme: 0.",
                "Cihaz sayısı", "Saniye", xs, [("Geri bağlanma", [r["toparlanma_sn"] for r in rows], "s1")], " sn"),
            "islemci": line_chart(
                t, "Normal çalışmada işlemci kullanımı (bir çekirdeğin yüzdesi)",
                "Her cihaz 5 sn'de bir sinyal gönderiyor. %100 = tek çekirdeğin tamamı; sunucuda 8 çekirdek var.",
                "Cihaz sayısı", "Tek çekirdeğin %'si", xs,
                [("POps sunucusu", [r["backend_cpu"] for r in rows], "s1"),
                 ("PostgreSQL", [r["pg_cpu"] for r in rows], "s2")], " %"),
            "duzeltme": bar_chart(
                t, "Görev kuyruğu düzeltmesinin etkisi: sunucu işlemcisi",
                "Aynı test, aynı makine. Önce: her bağlantıda çevrimiçi her cihaz için ayrı sorgu (N²/2).",
                "Cihaz sayısı", "Tek çekirdeğin %'si", [g["cihaz"] for g in before],
                [("Düzeltme öncesi", [g["once"] for g in before], "s2"),
                 ("Düzeltme sonrası", [g["sonra"] for g in before], "s1")], " %"),
            "bellek": line_chart(
                t, "POps sunucusunun bellek kullanımı",
                "Ölçülen değerler ve doğrusal model (kesikli): taban + cihaz başına sabit pay.",
                "Cihaz sayısı", "MB", xs, [("Ölçülen", [r["backend_mb"] for r in rows], "s1")], " MB",
                fit=(a, b, "model: %s MB + %s MB × cihaz" % (fmt(round(a)), ("%.2f" % b).replace(".", ",")))),
            "panel": line_chart(
                t, "Yük altında panelde 5.000 cihazlık listenin açılma süresi",
                "Veritabanında 5.000 kayıtlı cihaz; panel listeyi her saniye çekiyor (/api/devices).",
                "Bağlı cihaz sayısı", "Milisaniye", xs,
                [("p95 (en yavaş %5)", [r["panel_p95_ms"] for r in rows], "s2"),
                 ("p50 (tipik)", [r["panel_p50_ms"] for r in rows], "s1")], " ms"),
        }
        for name, svg in charts.items():
            with open(os.path.join(outdir, name + suffix + ".svg"), "w", encoding="utf-8") as f:
                f.write(svg)
    json.dump(data, open(sys.argv[1], "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("bellek modeli:", data["bellek_modeli"])


if __name__ == "__main__":
    main()
