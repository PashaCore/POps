# Bundled fonts

The panel serves its own fonts so it works without internet access and the browser never contacts a font CDN.

| File | Source | Licence |
|---|---|---|
| `InterVariable.woff2` | Inter 4.1, `web/InterVariable.woff2` from [rsms/inter v4.1](https://github.com/rsms/inter/releases/tag/v4.1) (`Inter-4.1.zip`, sha256 `9883fdd4…6b11e`) | SIL OFL 1.1, `Inter-OFL.txt` |
| `JetBrainsMono-Variable.woff2` | JetBrains Mono 2.304, `fonts/variable/JetBrainsMono[wght].ttf` from [JetBrains/JetBrainsMono v2.304](https://github.com/JetBrains/JetBrainsMono/releases/tag/v2.304) (`JetBrainsMono-2.304.zip`, sha256 `6f6376c6…f7bbf`) | SIL OFL 1.1, `JetBrainsMono-OFL.txt` |

Both are variable fonts cut down to what the panel uses:

- weight axis limited to 400–700 (the panel's regular, medium, semibold and bold); Inter's optical-size axis is pinned
  to its text design (`opsz=14`);
- characters: Latin, Latin-1, Latin Extended-A/B (Turkish ğ ş ı İ ö ü ç), Latin Extended Additional, combining marks,
  general punctuation, currency signs, arrows and a few math signs;
- OpenType features: kerning, marks, `tnum` (tabular numbers), fractions, `case`, `zero` and the contextual ones.

## Rebuilding

Run in a directory that holds the two release zips. With the pinned tool versions and the fixed
`SOURCE_DATE_EPOCH` the output is byte-identical (sha256 below).

```sh
python3 -m venv fontvenv && fontvenv/bin/pip install fonttools==4.60.1 brotli==1.1.0
export SOURCE_DATE_EPOCH=1731715200
mkdir -p src out
unzip -j -d src Inter-4.1.zip web/InterVariable.woff2
unzip -j -d src JetBrainsMono-2.304.zip 'fonts/variable/JetBrainsMono[[]wght].ttf'

U="U+0000-024F,U+02B0-036F,U+1E00-1EFF,U+2000-206F,U+20A0-20C0,U+2113,U+2122,U+2190-2199,U+2212,U+2215,U+FEFF,U+FFFD"
F="kern,mark,mkmk,ccmp,locl,calt,liga,rlig,case,tnum,pnum,frac,numr,dnom,zero,sups,subs"

fontvenv/bin/fonttools varLib.instancer src/InterVariable.woff2 opsz=14 wght=400:700 -q -o src/inter.ttf
# Same smoothing setup as JetBrains Mono and the Google Fonts build of Inter (gasp + prep), so text is
# rasterised (and on Linux spaced) exactly as before
fontvenv/bin/python -c "
from fontTools.ttLib import TTFont, newTable
from fontTools.ttLib.tables import ttProgram
f = TTFont('src/inter.ttf')
f['gasp'] = newTable('gasp'); f['gasp'].gaspRange = {0xFFFF: 15}
p = ttProgram.Program(); p.fromAssembly(['PUSHW[]', '511', 'SCANCTRL[]', 'PUSHB[]', '4', 'SCANTYPE[]'])
f['prep'] = newTable('prep'); f['prep'].program = p
f.save('src/inter.ttf')"
fontvenv/bin/pyftsubset src/inter.ttf --unicodes="$U" --layout-features="$F" --flavor=woff2 \
    --output-file=out/InterVariable.woff2

fontvenv/bin/fonttools varLib.instancer 'src/JetBrainsMono[wght].ttf' wght=400:700 -q -o src/jbm.ttf
fontvenv/bin/pyftsubset src/jbm.ttf --unicodes="$U" --layout-features="$F" --flavor=woff2 \
    --output-file=out/JetBrainsMono-Variable.woff2

sha256sum out/*
# e7b51a0944f2cef1606c764d948554b9088184536cf20d7241c3df838518dcd6  out/InterVariable.woff2
# 0c9c5f23752fd60bc9c8f73d09b3d400bee35223dd657cfac5daee7a38b9aa3d  out/JetBrainsMono-Variable.woff2
```

The `@font-face` rules are at the top of `assets/pops_theme.css`.
