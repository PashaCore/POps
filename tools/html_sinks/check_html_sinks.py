#!/usr/bin/env python3
"""Panelde HTML'e yazılan yerleri kaçırılmamış veri için tarar (CI: dashboard işi). Yalnızca standart
kütüphane, Python 3.9+.

Taranan: Dashboard/*.php, Dashboard/includes/*.php, Dashboard/assets/*.js (assets/vendor hariç);
PHP dosyalarında yalnızca <script> blokları JS olarak okunur.

Kurallar
  1. HTML'e yazılan ifadeler (innerHTML / outerHTML atamaları, insertAdjacentHTML, document.write ve
     izin listesinde "sink:" ile tanımlanan sarmalayıcılar) ile HTML etiketi içeren her şablon dizesi
     ya da dize birleştirmesindeki her parça güvenli olmalı: dize sabiti, sayı, escapeHtml(...),
     jsArg(...), encodeURIComponent(...), Number/parseInt(...), .length vb. Üçlü ifadede (a ? b : c)
     yalnızca dallar, a && b'de yalnızca b denetlenir.
  2. Yer önemlidir: on*="..." içinde escapeHtml yetmez (tarayıcı &#39;'yi JS çalışmadan önce geri
     çözer), jsArg gerekir; href/src değerinin başında yalnızca encodeURIComponent; etiketin içinde
     (nitelik adı yerine) ve tırnaksız nitelik değerinde yalnızca sabit ya da sayı kabul edilir.
  3. Adı html / ...Html / ...HTML olan değişkenler ve fonksiyonlar hazır (kaçırılmış) HTML sayılır;
     bu yüzden bu adlara yapılan her atama da aynı kurallarla denetlenir.
  4. PHP: <?= ?>, echo, print ve die/exit çıktısı htmlspecialchars, json_encode(..., JSON_HEX_TAG...),
     intval/(int) ya da sabit olmalı.
  5. Değişken ve fonksiyonlar ad bazında izlenir: bir adın bütün atamaları (fonksiyonun bütün return'leri)
     güvenliyse ad da güvenli sayılır. Parametreler, döngü değişkenleri ve aynı adın başka yerde böyle
     kullanıldığı durumlar bilinmez sayılır.

Sezgiseldir ve kod incelemesinin yerine geçmez: örneğin bir yardımcı fonksiyona argüman olarak giden veri,
o argüman "sink:" ile tanımlanmadıkça izlenmez.

İncelenmiş istisnalar html_sinks_allowlist.txt'dedir. Satır biçimi: "<dosya> <anahtar> <gerekçe>".
Anahtar, bulgunun bulunduğu satırın boşlukları sadeleştirilmiş metninin SHA-1'inin ilk 12 hanesidir:
satır kayınca bozulmaz, satırın kendisi değişince yeniden inceleme ister. "func:ad" o dosyada ad(...)
çağrısının güvenli HTML döndürdüğünü, "sink:ad/N" ad(...)'ın N. (0'dan) argümanını HTML olarak
yazdığını bildirir. Kullanılmayan izin satırı da hatadır.
"""
import argparse
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, '..', '..'))
DEFAULT_ALLOWLIST = os.path.join(HERE, 'html_sinks_allowlist.txt')

# ---------------------------------------------------------------- JS sözcükleri
PUNCT = sorted(['>>>=', '...', '===', '!==', '**=', '<<=', '>>=', '>>>', '&&=', '||=', '??=', '=>', '==',
                '!=', '<=', '>=', '&&', '||', '??', '?.', '++', '--', '+=', '-=', '*=', '/=', '%=', '&=',
                '|=', '^=', '**', '<<', '>>'], key=len, reverse=True)
ID_RE = re.compile(r'[A-Za-z_$\u00c0-\uffff][\w$\u00c0-\uffff]*')
NUM_RE = re.compile(r'0[xXbBoO][0-9a-fA-F_]+n?|(?:\d[\d_]*\.?\d*|\.\d+)(?:[eE][+-]?\d+)?n?')
REGEX_AFTER = {'return', 'typeof', 'case', 'do', 'else', 'in', 'of', 'new', 'delete', 'void', 'throw',
               'instanceof', 'yield', 'await'}
OPEN, CLOSE = {'(': ')', '[': ']', '{': '}'}, {')', ']', '}'}
# Satır sonunda bunlardan biri varsa ya da yeni satır bunlardan biriyle başlıyorsa ifade sürer (ASI)
CONT_AFTER = {'+', '-', '*', '/', '%', '?', ':', '=', '(', '[', '{', ',', '||', '&&', '??', '=>', '.', '?.',
              '+=', '<', '>', '<=', '>=', '===', '!==', '==', '!=', '|', '&', '^', '**'}
CONT_BEFORE = {'+', '-', '*', '/', '%', '?', ':', '.', '?.', '||', '&&', '??', '===', '!==', '==', '!=', '<',
               '>', '<=', '>=', '|', '&', '^', '**', '(', '['}
COMPARE = {'===', '!==', '==', '!=', '<', '>', '<=', '>='}
ARITH = {'-', '*', '/', '%', '**', '<<', '>>', '>>>', '&', '|', '^'}
SEG_SEP = {';', ',', '?', ':', '=>', '||', '&&', '??', '=', '+=', '-=', '*=', '/=', '%=', '||=', '&&=', '??='}
SEG_KW = {'return', 'throw', 'case', 'else', 'const', 'let', 'var', 'if', 'for', 'while', 'do'}

HTML_NAME = re.compile(r'^(?:html|\w*Html|\w*HTML)$')
HTML_TAG = re.compile(r'<[A-Za-z!/]')
# Çıktısı her bağlamda zararsız olanlar ve kaçırıcılar
ESCAPERS = {'escapeHtml': 'esc', 'jsArg': 'jsarg', 'encodeURIComponent': 'uri', 'Number': 'num',
            'parseInt': 'num', 'parseFloat': 'num', 'Boolean': 'lit', 'isNaN': 'lit'}
NUM_METHODS = {'toFixed', 'getTime', 'indexOf', 'lastIndexOf', 'charCodeAt', 'getFullYear', 'getMonth',
               'getDate', 'getHours', 'getMinutes', 'getSeconds'}
DATE_TEXT = {'toLocaleDateString', 'toLocaleTimeString', 'toLocaleString', 'toISOString'}
CONSTS = {'true', 'false', 'null', 'undefined', 'NaN', 'Infinity'}
ALLOWED = {
    'text': {'lit', 'num', 'esc', 'jsarg', 'uri', 'html'},
    'attr': {'lit', 'num', 'esc', 'jsarg', 'uri'},
    'url': {'lit', 'num', 'uri'},
    'js': {'lit', 'num', 'jsarg'},
    'tag': {'lit', 'num'},
    'unquoted': {'lit', 'num'},
}
WHY = {
    'text': 'kaçırılmamış değer',
    'attr': 'nitelikte kaçırılmamış değer',
    'url': 'href/src başında encodeURIComponent yok',
    'js': 'on* niteliğinde jsArg yok (escapeHtml yetmez)',
    'tag': 'etiketin içinde serbest değer',
    'unquoted': 'tırnaksız nitelik değeri',
}
URL_ATTRS = {'href', 'src', 'action', 'formaction', 'xlink:href', 'poster', 'background'}


class Tok(object):
    __slots__ = ('kind', 'text', 'line', 'nl', 'parts')

    def __init__(self, kind, text, line, nl, parts=None):
        self.kind, self.text, self.line, self.nl, self.parts = kind, text, line, nl, parts

    def p(self, *texts):
        return self.kind == 'p' and (not texts or self.text in texts)


class Lexer(object):
    """Kaba ama yeterli bir JS sözcük çözücü: dize, şablon (iç içe ${}), regex, yorum ve noktalama."""

    def __init__(self, src, line):
        self.s, self.i, self.line = src, 0, line

    def tokens(self, until_brace=False):
        s, n, out, depth, nl = self.s, len(self.s), [], 0, False
        while self.i < n:
            c = s[self.i]
            if c == '\n':
                self.line += 1
                nl = True
                self.i += 1
                continue
            if c in ' \t\r\f\v﻿':
                self.i += 1
                continue
            if s.startswith('//', self.i):
                j = s.find('\n', self.i)
                self.i = n if j < 0 else j
                continue
            if s.startswith('/*', self.i):
                j = s.find('*/', self.i + 2)
                j = n if j < 0 else j + 2
                k = s.count('\n', self.i, j)
                self.line += k
                nl = nl or k > 0
                self.i = j
                continue
            line = self.line
            if c in '"\'':
                j = self.i + 1
                while j < n and s[j] != c and s[j] != '\n':
                    j += 2 if s[j] == '\\' else 1
                text = s[self.i:j + 1]
                self.line += text.count('\n')
                out.append(Tok('str', text, line, nl))
                self.i, nl = j + 1, False
                continue
            if c == '`':
                out.append(self.template(nl))
                nl = False
                continue
            if until_brace and c == '}' and depth == 0:
                self.i += 1
                return out
            if c == '/' and self.regex_ok(out):
                j = self.regex_end(self.i)
                if j:
                    out.append(Tok('re', s[self.i:j], line, nl))
                    self.i, nl = j, False
                    continue
            m = ID_RE.match(s, self.i)
            if m:
                out.append(Tok('id', m.group(0), line, nl))
                self.i, nl = m.end(), False
                continue
            if c.isdigit() or (c == '.' and s[self.i + 1:self.i + 2].isdigit()):
                m = NUM_RE.match(s, self.i)
                out.append(Tok('num', m.group(0), line, nl))
                self.i, nl = m.end(), False
                continue
            for p in PUNCT:
                if s.startswith(p, self.i):
                    break
            else:
                p = c
            if p in OPEN:
                depth += 1
            elif p in CLOSE:
                depth -= 1
            out.append(Tok('p', p, line, nl))
            self.i, nl = self.i + len(p), False
        return out

    def template(self, nl):
        s, n, line = self.s, len(self.s), self.line
        self.i += 1
        parts, buf = [], []
        while self.i < n:
            c = s[self.i]
            if c == '\\':
                buf.append(s[self.i:self.i + 2])
                self.line += s[self.i:self.i + 2].count('\n')
                self.i += 2
                continue
            if c == '`':
                self.i += 1
                break
            if s.startswith('${', self.i):
                parts.append(('s', ''.join(buf)))
                buf = []
                self.i += 2
                eline = self.line
                parts.append(('e', self.tokens(until_brace=True), eline))
                continue
            if c == '\n':
                self.line += 1
            buf.append(c)
            self.i += 1
        parts.append(('s', ''.join(buf)))
        return Tok('tpl', '`', line, nl, parts)

    @staticmethod
    def regex_ok(out):
        if not out:
            return True
        t = out[-1]
        if t.kind == 'id':
            return t.text in REGEX_AFTER
        if t.kind == 'p':
            return t.text not in (')', ']', '}', '++', '--')
        return False

    def regex_end(self, i):
        s, n, j, cls = self.s, len(self.s), i + 1, False
        while j < n:
            c = s[j]
            if c == '\n':
                return None
            if c == '\\':
                j += 2
                continue
            if cls:
                cls = c != ']'
            elif c == '[':
                cls = True
            elif c == '/':
                j += 1
                while j < n and s[j].isalpha():
                    j += 1
                return j
            j += 1
        return None


def bmatch(toks):
    """Parantez/köşeli/süslü eşleri: {açılış indeksi: kapanış indeksi}."""
    m, stack = {}, []
    for i, t in enumerate(toks):
        if t.kind != 'p':
            continue
        if t.text in OPEN:
            stack.append(i)
        elif t.text in CLOSE and stack:
            m[stack.pop()] = i
    return m


def top_level(toks, m):
    """Üst düzeydeki (parantez içinde olmayan) indeksler."""
    i, n = 0, len(toks)
    while i < n:
        yield i
        if toks[i].kind == 'p' and toks[i].text in OPEN and i in m:
            i = m[i] + 1
        else:
            i += 1


def split_top(toks, m, pred):
    parts, start = [], 0
    for i in top_level(toks, m):
        if pred(toks, i):
            parts.append(toks[start:i])
            start = i + 1
    parts.append(toks[start:])
    return parts


def strip_parens(toks):
    while len(toks) >= 2 and toks[0].p('(') and bmatch(toks).get(0) == len(toks) - 1:
        toks = toks[1:-1]
    return toks


def is_value_end(t):
    if t.kind in ('num', 'str', 'tpl', 're'):
        return True
    if t.kind == 'id':
        return t.text not in REGEX_AFTER
    return t.text in (')', ']', '}', '++', '--')


def binary_at(toks, i, ops):
    return toks[i].kind == 'p' and toks[i].text in ops and i > 0 and is_value_end(toks[i - 1])


def stmt_end(toks, start, m):
    """start'tan başlayan ifadenin bittiği indeks (;  ,  kapanan parantez ya da ASI)."""
    i, n = start, len(toks)
    while i < n:
        t = toks[i]
        if i > start and t.nl:
            prev = toks[i - 1]
            if not (prev.kind == 'p' and prev.text in CONT_AFTER) and not (t.kind == 'p' and t.text in CONT_BEFORE):
                return i
        if t.kind == 'p':
            if t.text in (';', ',') or t.text in CLOSE:
                return i
            if t.text in OPEN:
                if i not in m:
                    return n
                i = m[i] + 1
                continue
        i += 1
    return n


def split_args(toks):
    toks = list(toks)
    if not toks:
        return []
    return split_top(toks, bmatch(toks), lambda ts, i: ts[i].p(','))


def static_text(toks):
    """Birleştirmede bir parçanın HTML'e katkısı: sabitse metni, değilse yer tutucu."""
    toks = strip_parens(toks)
    if len(toks) == 1 and toks[0].kind == 'str':
        return toks[0].text[1:-1]
    if len(toks) == 1 and toks[0].kind == 'tpl':
        return ''.join(p[1] if p[0] == 's' else 'X' for p in toks[0].parts)
    return 'X'


def html_context(prefix):
    """Önündeki HTML metnine göre bir değerin yazıldığı yer."""
    lt = prefix.rfind('<')
    if lt < 0 or prefix.rfind('>') > lt:
        return 'text'
    s = prefix[lt + 1:]
    m = re.match(r'/?[A-Za-z][\w:-]*', s)
    if not m:
        return 'text'
    i, n = m.end(), len(s)
    while True:
        while i < n and s[i] in ' \t\r\n/':
            i += 1
        if i >= n:
            return 'tag'
        m = re.compile(r'[^\s/>=]+').match(s, i)
        if not m:
            return 'tag'
        name, i = m.group(0).lower(), m.end()
        while i < n and s[i] in ' \t\r\n':
            i += 1
        if i >= n:
            return 'tag'
        if s[i] != '=':
            continue
        i += 1
        while i < n and s[i] in ' \t\r\n':
            i += 1
        if i >= n:
            return 'unquoted'
        if s[i] in '"\'':
            j = s.find(s[i], i + 1)
            if j < 0:
                value = s[i + 1:]
                if name.startswith('on'):
                    return 'js'
                if name in URL_ATTRS and not value.strip():
                    return 'url'
                return 'attr'
            i = j + 1
        else:
            m = re.compile(r'[^\s>]*').match(s, i)
            i = m.end()
            if i >= n:
                return 'unquoted'


def parse_chain(toks):
    """a.b(c)[d]... zinciri: (taban, [(tür, değer)]); zincir değilse None."""
    i, n = 0, len(toks)
    if n and toks[0].kind == 'id' and toks[0].text == 'new':
        i = 1
    if i >= n:
        return None
    t, m = toks[i], bmatch(toks)
    if t.kind in ('id', 'str', 'num', 'tpl'):
        base, i = toks[i:i + 1], i + 1
    elif t.p('(', '[') and i in m:
        base, i = toks[i:m[i] + 1], m[i] + 1
    else:
        return None
    acc = []
    while i < n:
        t = toks[i]
        if t.p('.', '?.') and i + 1 < n and toks[i + 1].kind == 'id':
            acc.append(('prop', toks[i + 1].text))
            i += 2
        elif t.p('(') and i in m:
            acc.append(('call', toks[i + 1:m[i]]))
            i = m[i] + 1
        elif t.p('[') and i in m:
            acc.append(('index', toks[i + 1:m[i]]))
            i = m[i] + 1
        elif t.p('?.') and i + 1 < n and toks[i + 1].p('(', '['):
            i += 1
        else:
            return None
    return base, acc


class Finding(object):
    __slots__ = ('line', 'text', 'why')

    def __init__(self, line, text, why):
        self.line, self.text, self.why = line, text, why


def tok_text(toks):
    out = []
    for t in toks:
        if t.kind == 'tpl':
            out.append('`' + ''.join(p[1] if p[0] == 's' else '${' + tok_text(p[1]) + '}' for p in t.parts) + '`')
        else:
            out.append(t.text)
    text = ' '.join(out)
    text = re.sub(r' ?([.()\[\],]) ?', r'\1', text)
    return text if len(text) <= 90 else text[:87] + '...'


LIT, NUM, UNKNOWN = frozenset(['lit']), frozenset(['num']), frozenset(['unknown'])


def opener_of(m, close_idx):
    for o, c in m.items():
        if c == close_idx:
            return o
    return None


def as_function(rhs):
    """Sağ taraf bir fonksiyonsa ('block', gövde) ya da ('expr', gövde); değilse None."""
    toks = list(rhs)
    if toks and toks[0].kind == 'id' and toks[0].text == 'async':
        toks = toks[1:]
    if not toks:
        return None
    m = bmatch(toks)
    if toks[0].kind == 'id' and toks[0].text == 'function':
        for i, t in enumerate(toks):
            if t.p('{') and i in m:
                return 'block', toks[i + 1:m[i]]
        return None
    if toks[0].p('(') and 0 in m:
        k = m[0] + 1
    elif toks[0].kind == 'id':
        k = 1
    else:
        return None
    if k < len(toks) and toks[k].p('=>'):
        body = toks[k + 1:]
        if body and body[0].p('{') and bmatch(body).get(0) == len(body) - 1:
            return 'block', body[1:-1]
        return 'expr', body
    return None


def returns(block):
    """Bir gövdedeki return ifadeleri (iç içe fonksiyonların return'leri hariç)."""
    m, out, i, n = bmatch(block), [], 0, len(block)
    while i < n:
        t = block[i]
        if t.p('=>'):
            if i + 1 < n and block[i + 1].p('{') and i + 1 in m:
                i = m[i + 1] + 1
            else:
                i = max(stmt_end(block, i + 1, m), i + 1)
            continue
        if t.kind == 'id' and t.text == 'function':
            j = i + 1
            while j < n and not (block[j].p('{') and j in m):
                j += 1
            i = m[j] + 1 if j < n else n
            continue
        if t.kind == 'id' and t.text == 'return':
            end = stmt_end(block, i + 1, m)
            out.append(block[i + 1:end])
            i = max(end, i + 1)
            continue
        i += 1
    return out


class Checker(object):
    def __init__(self, funcs=None, sinks=None, allowed_lines=None):
        self.funcs = set(funcs or ())
        self.sinks = dict(sinks or {})
        # İzin listesindeki (incelenmiş) satırlardaki yapraklar bulgu sayılmaz ve çıkarımda sabit kabul edilir
        self.allowed_lines = set(allowed_lines or ())
        self.used_lines = set()
        self.findings = {}
        self.sink_count = 0
        self.var_kinds, self.fn_kinds, self.fn_defs = {}, {}, {}

    def add(self, items):
        for f in items:
            self.findings.setdefault((f.line, f.text, f.why), f)

    # ------------------------------------------------------------ çıkarım
    def collect(self, toks, assigns, bad, fns):
        """Ad bazlı bağlar: atamalar, fonksiyonlar ve çıkarım yapılamayacak adlar (parametre, döngü değişkeni...)."""
        m, n = bmatch(toks), len(toks)
        for i, t in enumerate(toks):
            prv = toks[i - 1] if i > 0 else None
            nxt = toks[i + 1] if i + 1 < n else None
            if t.kind == 'tpl':
                for p in t.parts:
                    if p[0] == 'e':
                        self.collect(p[1], assigns, bad, fns)
                continue
            if t.p('=>') and prv is not None:
                if prv.p(')'):
                    o = opener_of(m, i - 1)
                    if o is not None:
                        bad.update(x.text for x in toks[o + 1:i - 1] if x.kind == 'id')
                elif prv.kind == 'id':
                    bad.add(prv.text)
                continue
            if t.kind != 'id':
                continue
            if t.text in ('function', 'catch'):
                j, name = i + 1, None
                if j < n and toks[j].p('*'):
                    j += 1
                if t.text == 'function' and j < n and toks[j].kind == 'id':
                    name, j = toks[j].text, j + 1
                if j < n and toks[j].p('(') and j in m:
                    bad.update(x.text for x in toks[j + 1:m[j]] if x.kind == 'id')
                    k = m[j] + 1
                    if name and k < n and toks[k].p('{') and k in m and not (prv is not None and prv.p('=', ':')):
                        fns.setdefault(name, []).append(('block', toks[k + 1:m[k]]))
                continue
            if t.text in ('const', 'let', 'var') and nxt is not None and nxt.p('[', '{') and i + 1 in m:
                bad.update(x.text for x in toks[i + 2:m[i + 1]] if x.kind == 'id')
                continue
            if nxt is None or (prv is not None and prv.p('.', '?.')):
                continue
            if prv is not None and prv.kind == 'id' and prv.text in ('const', 'let', 'var') and nxt.kind == 'id' \
                    and nxt.text in ('of', 'in'):
                bad.add(t.text)
            elif nxt.p('=', '+=', '||=', '??=', '&&='):
                rhs = toks[i + 2:stmt_end(toks, i + 2, m)]
                fn = as_function(rhs)
                if fn is not None:
                    fns.setdefault(t.text, []).append(fn)
                else:
                    assigns.setdefault(t.text, []).append(rhs)

    def infer(self, toks):
        assigns, bad, fns = {}, set(), {}
        self.collect(toks, assigns, bad, fns)
        self.fn_defs = fns
        for _ in range(8):
            changed = False
            for name, rhss in assigns.items():
                if name in bad or name in fns:
                    continue
                ks = frozenset().union(*[self.kinds(r) for r in rhss])
                if self.var_kinds.get(name) != ks:
                    self.var_kinds[name], changed = ks, True
            for name, defs in fns.items():
                if name in bad or name in assigns or name in self.funcs:
                    continue
                ks = set()
                for kind, body in defs:
                    rs = [body] if kind == 'expr' else returns(body)
                    ks |= LIT if not rs else frozenset().union(*[self.kinds(r) for r in rs])
                ks = frozenset(ks)
                if self.fn_kinds.get(name) != ks:
                    self.fn_kinds[name], changed = ks, True
            if not changed:
                break

    # ------------------------------------------------------------ ifade ayrıştırma
    def walk(self, toks, prefix, cb):
        """İfadeyi çıktıya giden yapraklarına ayırır; her yaprak için cb(toks, önek, türler)."""
        toks = strip_parens(list(toks))
        if not toks:
            return
        m = bmatch(toks)
        tops = list(top_level(toks, m))
        for qi in tops:  # üçlü: yalnızca dallar çıktıya gider
            if toks[qi].p('?'):
                depth = 0
                for ci in tops:
                    if ci <= qi:
                        continue
                    if toks[ci].p('?'):
                        depth += 1
                    elif toks[ci].p(':'):
                        if depth == 0:
                            self.walk(toks[qi + 1:ci], prefix, cb)
                            self.walk(toks[ci + 1:], prefix, cb)
                            return
                        depth -= 1
                break
        if any(toks[i].p('=>') for i in tops):
            return cb(toks, prefix, UNKNOWN)
        parts = split_top(toks, m, lambda ts, i: ts[i].p('||', '??'))
        if len(parts) > 1:
            for p in parts:
                self.walk(p, prefix, cb)
            return
        parts = split_top(toks, m, lambda ts, i: ts[i].p('&&'))
        if len(parts) > 1:
            return self.walk(parts[-1], prefix, cb)  # a && b: a yanlışsa çıktı false/0/'' olur
        if any(binary_at(toks, i, COMPARE) or (toks[i].kind == 'id' and toks[i].text in ('instanceof', 'in'))
               for i in tops):
            return cb(toks, prefix, LIT)
        plus = [i for i in tops if binary_at(toks, i, ('+',))]
        if plus:
            pre, start = prefix, 0
            for i in plus + [len(toks)]:
                part = toks[start:i]
                self.walk(part, pre, cb)
                pre += static_text(part)
                start = i + 1
            return
        if any(binary_at(toks, i, ARITH) for i in tops):
            return cb(toks, prefix, NUM)
        first = toks[0]
        if first.p('-', '+', '~'):
            return cb(toks, prefix, NUM)
        if first.p('!') or (first.kind == 'id' and first.text in ('typeof', 'void')):
            return cb(toks, prefix, LIT)
        if len(toks) == 1 and first.kind == 'tpl':
            pre = prefix
            for part in first.parts:
                if part[0] == 's':
                    pre += part[1]
                else:
                    self.walk(part[1], pre, cb)
                    pre += 'X'
            return cb(toks, prefix, LIT)
        chain = parse_chain(toks)
        if chain:
            base, acc = chain
            # liste.map(x => `...`).join(''): geri çağrının döndürdüğü denetlenir
            if len(acc) >= 2 and acc[-1][0] == 'call' and acc[-2] == ('prop', 'join'):
                for k in range(len(acc) - 3, -1, -1):
                    if acc[k] == ('prop', 'map') and k + 1 < len(acc) and acc[k + 1][0] == 'call':
                        return self.walk_callback(acc[k + 1][1], prefix, cb, toks)
                if len(acc) == 2 and base[0].p('['):
                    for a in split_args(base[1:-1]):
                        self.walk(a, prefix, cb)
                    return
        cb(toks, prefix, self.leaf_kinds(toks, chain))

    def walk_callback(self, args, prefix, cb, whole):
        args = split_args(list(args))
        fn = args[0] if args else []
        if len(fn) == 1 and fn[0].kind == 'id':
            return cb(whole, prefix, self.call_kinds(fn[0].text))
        f = as_function(fn)
        if f is None:
            return cb(whole, prefix, UNKNOWN)
        for r in ([f[1]] if f[0] == 'expr' else returns(f[1])):
            self.walk(r, prefix, cb)

    def call_kinds(self, name):
        if name in ESCAPERS:
            return frozenset([ESCAPERS[name]])
        if name in self.funcs or HTML_NAME.match(name):
            return frozenset(['html'])
        return self.fn_kinds.get(name, UNKNOWN)

    def leaf_kinds(self, toks, chain):
        if len(toks) == 1:
            t = toks[0]
            if t.kind == 'str':
                return LIT
            if t.kind == 'num':
                return NUM
            if t.kind == 'id':
                if t.text in CONSTS:
                    return LIT
                if HTML_NAME.match(t.text):
                    return frozenset(['html'])
                return self.var_kinds.get(t.text, UNKNOWN)
            return UNKNOWN
        if not chain:
            return UNKNOWN
        base, acc = chain
        if not acc:
            return UNKNOWN
        b, last = base[0], acc[-1]
        if last[0] == 'prop':
            if last[1] in ('length', 'size'):
                return NUM
            if HTML_NAME.match(last[1]):
                return frozenset(['html'])  # el.innerHTML okuması: hazır HTML
            return UNKNOWN
        if last[0] != 'call':
            return UNKNOWN
        method = acc[-2][1] if len(acc) >= 2 and acc[-2][0] == 'prop' else None
        if method in NUM_METHODS:
            return NUM
        is_date = toks[0].text == 'new' and b.text == 'Date'
        if method in DATE_TEXT and b.kind == 'id' and (b.text == 'Number' or is_date):
            return LIT  # Number(x).toLocaleString() / new Date(x).toLocale...(): yalnızca rakam ve tarih metni
        if b.kind == 'id' and len(acc) == 1 and toks[0].text != 'new':
            return self.call_kinds(b.text)
        if b.kind == 'id' and b.text == 'Math' and len(acc) == 2:
            return NUM
        if b.kind == 'id' and b.text == 'Date' and acc[0] == ('prop', 'now'):
            return NUM
        return UNKNOWN

    def check(self, toks, prefix):
        out = []

        def cb(leaf, pre, ks):
            ctx = html_context(pre)
            if ks <= ALLOWED[ctx]:
                return
            if leaf[0].line in self.allowed_lines:
                self.used_lines.add(leaf[0].line)
                return
            out.append(Finding(leaf[0].line, tok_text(leaf), WHY[ctx]))
        self.walk(toks, prefix, cb)
        return out

    def kinds(self, toks):
        acc = set()

        def cb(leaf, pre, ks):
            acc.update(LIT if leaf[0].line in self.allowed_lines else ks)
        self.walk(toks, '', cb)
        return frozenset(acc) if acc else LIT

    def check_function(self, fn):
        kind, body = fn
        for r in ([body] if kind == 'expr' else returns(body)):
            self.add(self.check(r, ''))

    # ------------------------------------------------------------ tarama
    def scan(self, toks, top=True):
        if top:
            self.infer(toks)
            for name, defs in self.fn_defs.items():
                if HTML_NAME.match(name) and name not in ESCAPERS and name not in self.funcs:
                    for fn in defs:
                        self.check_function(fn)  # "hazır HTML" sayılan fonksiyonun döndürdüğü de denetlenir
        m = bmatch(toks)
        n = len(toks)
        for i, t in enumerate(toks):
            nxt = toks[i + 1] if i + 1 < n else None
            prv = toks[i - 1] if i > 0 else None
            if t.kind == 'tpl':
                if HTML_TAG.search(''.join(p[1] for p in t.parts if p[0] == 's')):
                    self.add(self.check([t], ''))
                for p in t.parts:
                    if p[0] == 'e':
                        self.scan(p[1], top=False)
                continue
            if t.kind != 'id' or nxt is None:
                continue
            dotted = prv is not None and prv.p('.', '?.')
            if dotted and t.text in ('innerHTML', 'outerHTML') and nxt.p('=', '+='):
                self.sink_count += 1
                self.add(self.check(toks[i + 2:stmt_end(toks, i + 2, m)], ''))
            elif dotted and t.text == 'insertAdjacentHTML' and nxt.p('(') and i + 1 in m:
                self.sink_count += 1
                args = split_args(toks[i + 2:m[i + 1]])
                if len(args) >= 2:
                    self.add(self.check(args[1], ''))
            elif (dotted and t.text in ('write', 'writeln') and i >= 2 and toks[i - 2].text == 'document'
                  and nxt.p('(') and i + 1 in m):
                self.sink_count += 1
                for a in split_args(toks[i + 2:m[i + 1]]):
                    self.add(self.check(a, ''))
            elif (t.text in self.sinks and nxt.p('(') and i + 1 in m and not dotted
                  and not (prv is not None and prv.kind == 'id' and prv.text == 'function')):
                self.sink_count += 1
                args = split_args(toks[i + 2:m[i + 1]])
                for k in sorted(self.sinks[t.text]):
                    if len(args) > k:
                        self.add(self.check(args[k], ''))
            elif HTML_NAME.match(t.text) and t.text not in ('innerHTML', 'outerHTML') and nxt.p('=', '+='):
                rhs = toks[i + 2:stmt_end(toks, i + 2, m)]
                fn = as_function(rhs)
                if fn is not None:
                    self.check_function(fn)
                else:
                    self.add(self.check(rhs, ''))
        self.scan_concat(toks, m)

    def scan_concat(self, toks, m):
        """HTML etiketi içeren dize sabitiyle yapılan + birleştirmeleri (her parantez düzeyinde)."""
        seg = []
        for i in top_level(toks, m):
            t = toks[i]
            if (t.kind == 'p' and t.text in SEG_SEP) or (t.kind == 'id' and t.text in SEG_KW):
                self.check_segment(seg)
                seg = []
                continue
            if seg and t.nl:
                prev = toks[i - 1]
                if not (prev.kind == 'p' and prev.text in CONT_AFTER) and not (t.kind == 'p' and t.text in CONT_BEFORE):
                    self.check_segment(seg)
                    seg = []
            if t.kind == 'p' and t.text in OPEN and i in m:
                seg.extend(toks[i:m[i] + 1])
                inner = toks[i + 1:m[i]]
                self.scan_concat(inner, bmatch(inner))
            else:
                seg.append(t)
        self.check_segment(seg)

    def check_segment(self, seg):
        if not seg:
            return
        m = bmatch(seg)
        tops = list(top_level(seg, m))
        if not any(binary_at(seg, i, ('+',)) for i in tops):
            return
        if any(seg[i].kind in ('str', 'tpl') and HTML_TAG.search(static_text([seg[i]])) for i in tops):
            self.add(self.check(seg, ''))


# ---------------------------------------------------------------- PHP
PHP_OPEN = re.compile(r'<\?(?:php\b|=)', re.I)
PHP_SAFE_FUNCS = {'htmlspecialchars', 'htmlentities', 'intval', 'floatval', 'boolval', 'count', 'time', 'date',
                  'number_format', 'urlencode', 'rawurlencode', 'strlen'}


def php_skip(src, i):
    """i'deki dize/yorumun sonrasına atlar; değilse i."""
    c = src[i]
    if c in '"\'':
        j = i + 1
        while j < len(src) and src[j] != c:
            j += 2 if src[j] == '\\' else 1
        return j + 1
    if src.startswith('/*', i):
        j = src.find('*/', i + 2)
        return len(src) if j < 0 else j + 2
    if c == '#' or src.startswith('//', i):
        j = i
        while j < len(src) and src[j] != '\n' and not src.startswith('?>', j):
            j += 1
        return j
    return i


def php_blocks(src):
    pos = 0
    while True:
        m = PHP_OPEN.search(src, pos)
        if not m:
            return
        i = m.end()
        while i < len(src) and not src.startswith('?>', i):
            j = php_skip(src, i)
            i = j if j != i else i + 1
        yield m.group(0) == '<?=', m.end(), i
        pos = i + 2


def php_split(expr, seps):
    """Üst düzeyde (dize ve parantez dışında) ayırıcılara göre böler."""
    parts, depth, start, i = [], 0, 0, 0
    while i < len(expr):
        j = php_skip(expr, i)
        if j != i:
            i = j
            continue
        c = expr[i]
        if c in '([{':
            depth += 1
        elif c in ')]}':
            depth -= 1
        elif depth == 0:
            for s in seps:
                if expr.startswith(s, i):
                    parts.append(expr[start:i])
                    start = i = i + len(s)
                    break
            else:
                i += 1
            continue
        i += 1
    parts.append(expr[start:])
    return parts


def php_wrapped(e):
    """e baştan sona tek bir parantez çifti içinde mi: (a) evet, (a) . (b) hayır."""
    if not (e.startswith('(') and e.endswith(')')):
        return False
    depth, i = 0, 0
    while i < len(e):
        j = php_skip(e, i)
        if j != i:
            i = j
            continue
        depth += {'(': 1, ')': -1}.get(e[i], 0)
        if depth == 0 and i < len(e) - 1:
            return False
        i += 1
    return True


def php_unsafe(expr):
    """Güvensiz çıktı parçaları (boş liste = güvenli)."""
    e = expr.strip().rstrip(';').strip()
    while php_wrapped(e):
        e = e[1:-1].strip()
    if not e:
        return []
    # üçlü: yalnızca dallar (a ?: b'de a da) çıktıya gider; ?? ve ?-> ayırıcı sayılmaz (uzunluk korunur)
    q = php_split(e.replace('??', '\x00\x00').replace('?->', '\x00\x00\x00'), ['?'])
    if len(q) > 1:
        rest = e[len(q[0]) + 1:]
        if rest.lstrip().startswith(':'):
            return php_unsafe(q[0]) + php_unsafe(rest.lstrip()[1:])
        branches = php_split(rest.replace('??', '\x00\x00').replace('::', '\x01\x01'), [':'])
        if len(branches) == 2:
            return php_unsafe(rest[:len(branches[0])]) + php_unsafe(rest[len(branches[0]) + 1:])
    parts = [p for p in php_split(e, ['.']) if p.strip()]
    if len(parts) > 1 and not re.search(r'\d\.\d', e):
        return [u for p in parts for u in php_unsafe(p)]
    if re.fullmatch(r"'(?:[^'\\]|\\.)*'", e) or re.fullmatch(r'"[^"$]*"', e):
        return []
    if e.lower() in ('true', 'false', 'null') or re.fullmatch(r'-?\d+(?:\.\d+)?|[A-Z_][A-Z0-9_]*', e):
        return []
    if re.match(r'\((?:int|integer|float|bool)\)', e):
        return []
    m = re.match(r'([A-Za-z_]\w*)\s*\(', e)
    if m and e.endswith(')'):
        name = m.group(1).lower()
        if name in PHP_SAFE_FUNCS:
            return []
        if name == 'json_encode' and 'JSON_HEX_TAG' in e:
            return []
    return [e]


def php_findings(src):
    out = []
    for short, start, end in php_blocks(src):
        code = src[start:end]
        base_line = src.count('\n', 0, start) + 1
        if short:
            for u in php_unsafe(code):
                out.append(Finding(base_line, u[:90], 'PHP çıktısı kaçırılmamış'))
            continue
        i = 0
        while i < len(code):
            j = php_skip(code, i)
            if j != i:
                i = j
                continue
            m = re.compile(r'\b(echo|print|die|exit)\b', re.I).match(code, i)
            if m and (i == 0 or not re.match(r'[\w$>]', code[i - 1])):
                k = m.end()
                depth = 0
                while k < len(code):
                    jj = php_skip(code, k)
                    if jj != k:
                        k = jj
                        continue
                    if code[k] in '([':
                        depth += 1
                    elif code[k] in ')]':
                        depth -= 1
                    elif code[k] == ';' and depth <= 0:
                        break
                    k += 1
                expr = code[m.end():k]
                line = base_line + code.count('\n', 0, i)
                for arg in php_split(expr.strip(), [',']) if m.group(1).lower() == 'echo' else [expr]:
                    for u in php_unsafe(arg):
                        out.append(Finding(line, u[:90], 'PHP çıktısı kaçırılmamış'))
                i = k
                continue
            i += 1
    return out


# ---------------------------------------------------------------- dosyalar
# Kapanış etiketi tarayıcının kabul ettiği biçimlerin hepsi: </script>, </SCRIPT >, </script\t\n foo>
SCRIPT_RE = re.compile(r'<script\b([^>]*)>(.*?)</script\b[^>]*>', re.S | re.I)
PHP_IN_JS = re.compile(r'<\?(?:php\b|=).*?\?>', re.S | re.I)


def js_regions(path, src):
    if path.endswith('.js'):
        yield 1, src
        return
    for m in SCRIPT_RE.finditer(src):
        if re.search(r'\bsrc\s*=', m.group(1), re.I):
            continue
        body = PHP_IN_JS.sub(lambda p: '__PHP__' + '\n' * p.group(0).count('\n'), m.group(2))
        yield src.count('\n', 0, m.start(2)) + 1, body


def line_key(line_text):
    norm = ' '.join(line_text.split())
    return hashlib.sha1(norm.encode('utf-8')).hexdigest()[:12]


def scan_file(path, funcs=None, sinks=None, allowed_keys=()):
    """(izinsiz bulgular, kullanılan izin anahtarları, HTML yazımı sayısı, kaynak)"""
    with open(path, encoding='utf-8') as f:
        src = f.read()
    lines = src.split('\n')
    keys = [line_key(t) for t in lines]
    allowed = {n + 1 for n, k in enumerate(keys) if k in allowed_keys}
    chk = Checker(funcs, sinks, allowed)
    for line, body in js_regions(path, src):
        chk.scan(Lexer(body, line).tokens())
    found = list(chk.findings.values())
    if path.endswith('.php'):
        for f in php_findings(src):
            if f.line in allowed:
                chk.used_lines.add(f.line)
            else:
                found.append(f)
    result = []
    for f in sorted(found, key=lambda x: (x.line, x.text)):
        ok = 0 < f.line <= len(lines)
        result.append((f, keys[f.line - 1] if ok else '', lines[f.line - 1].strip() if ok else ''))
    used = {keys[n - 1] for n in chk.used_lines if 0 < n <= len(keys)}
    return result, used, chk.sink_count, src


def collect_files(root):
    dash = os.path.join(root, 'Dashboard')
    files = []
    for sub, exts in (('', ('.php',)), ('includes', ('.php',)), ('assets', ('.js',))):
        d = os.path.join(dash, sub)
        if not os.path.isdir(d):
            continue
        for name in sorted(os.listdir(d)):
            p = os.path.join(d, name)
            if os.path.isfile(p) and name.endswith(exts):
                files.append(p)
    return files


def load_allowlist(path):
    entries, funcs, sinks, errors = {}, {}, {}, []
    if not os.path.exists(path):
        return entries, funcs, sinks, errors
    with open(path, encoding='utf-8') as f:
        for n, raw in enumerate(f, 1):
            line = raw.strip()
            if not line or line.startswith('#'):
                continue
            parts = line.split(None, 2)
            if len(parts) < 3 or not parts[2].strip():
                errors.append('%s:%d: gerekçe eksik: %s' % (path, n, line))
                continue
            rel, key, reason = parts
            if key.startswith('func:'):
                funcs.setdefault(rel, {})[key[5:]] = n
            elif key.startswith('sink:'):
                name, _, idx = key[5:].partition('/')
                if not idx.isdigit():
                    errors.append('%s:%d: sink biçimi sink:ad/argüman_sırası olmalı' % (path, n))
                    continue
                sinks.setdefault(rel, {}).setdefault(name, [set(), n])[0].add(int(idx))
            elif re.fullmatch(r'[0-9a-f]{12}', key):
                entries[(rel, key)] = [reason, n, False]
            else:
                errors.append('%s:%d: anahtar 12 haneli onaltılık özet, func:ad ya da sink:ad/N olmalı' % (path, n))
    return entries, funcs, sinks, errors


def main(argv=None):
    ap = argparse.ArgumentParser(description='Panel HTML yazımlarını (XSS) denetler.')
    ap.add_argument('--root', default=ROOT, help='depo kökü (varsayılan: bu betiğin iki üstü)')
    ap.add_argument('--allowlist', default=DEFAULT_ALLOWLIST)
    ap.add_argument('--list', action='store_true', help='izinli olanlar dahil bütün bulguları yaz')
    args = ap.parse_args(argv)
    gh = os.environ.get('GITHUB_ACTIONS') == 'true'

    entries, funcs, sinks, errors = load_allowlist(args.allowlist)
    files = collect_files(args.root)
    bad, total_sinks, allowed = [], 0, 0
    for path in files:
        rel = os.path.relpath(path, args.root).replace(os.sep, '/')
        f_funcs = funcs.get(rel, {})
        f_sinks = {k: v[0] for k, v in sinks.get(rel, {}).items()}  # ad -> HTML yazılan argüman sıraları
        f_keys = {k for (r, k) in entries if r == rel}
        result, used, count, src = scan_file(path, f_funcs, f_sinks, f_keys)
        total_sinks += count
        for name, n in list(f_funcs.items()) + [(k, v[1]) for k, v in sinks.get(rel, {}).items()]:
            if not re.search(r'\b%s\s*\(' % re.escape(name), src):
                errors.append('%s:%d: kullanılmayan izin (%s içinde %s yok)' % (args.allowlist, n, rel, name))
        for key in used:
            entries[(rel, key)][2] = True
            allowed += 1
            if args.list:
                print('%s: [izinli] %s  (%s)' % (rel, key, entries[(rel, key)][0]))
        for f, key, text in result:
            bad.append((rel, f, key, text))
    known = {os.path.relpath(p, args.root).replace(os.sep, '/') for p in files}
    for (rel, key), (reason, n, used) in sorted(entries.items(), key=lambda kv: kv[1][1]):
        if not used:
            why = 'dosya yok' if rel not in known else 'eşleşen satır yok (satır değişti ya da düzeltildi; kaldırın)'
            errors.append('%s:%d: kullanılmayan izin %s %s: %s' % (args.allowlist, n, rel, key, why))

    for rel, f, key, text in bad:
        msg = '%s: %s' % (f.why, f.text)
        if gh:
            print('::error file=%s,line=%d::%s' % (rel, f.line, msg))
        print('%s:%d: %s' % (rel, f.line, msg))
        print('    satır: %s' % text[:160])
        print('    güvenliyse izin satırı: %s  %s  <gerekçe>' % (rel, key))
    for e in errors:
        if gh:
            print('::error::%s' % e)
        print(e)
    print('%d dosya, %d HTML yazımı tarandı; %d incelenmiş satır, %d yeni bulgu, %d izin listesi hatası.'
          % (len(files), total_sinks, allowed, len(bad), len(errors)))
    return 1 if bad or errors else 0


if __name__ == '__main__':
    sys.exit(main())
