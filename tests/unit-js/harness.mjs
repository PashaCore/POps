// Panel betiklerini tarayıcısız yükler (node:vm): sahte, boş bir window/document ile önce header.php'deki
// escapeHtml/jsArg, sonra Dashboard/assets/pops_script.js. Yalnızca DOM'a dokunmayan yardımcılar sınanır.
// Bağımlılık yok: node --test tests/unit-js/*.test.mjs (bkz. docs/dashboard.md "Page scripts").
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

export const ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (rel) => fs.readFileSync(path.join(ROOT, rel), 'utf8');

// Başka bir vm bağlamından gelen dizi/nesne bu bağlamın prototiplerine çevrilir (deepStrictEqual için)
export const plain = (v) => JSON.parse(JSON.stringify(v));

// header.php'deki satır içi escapeHtml ve jsArg: bütün sayfalarda ortak betiklerden önce tanımlanır
function headerHelpers() {
    const src = read('Dashboard/includes/header.php');
    return ['escapeHtml', 'jsArg'].map((name) => {
        const m = src.match(new RegExp('function ' + name + '\\([^)]*\\) \\{[\\s\\S]*?\\n    \\}'));
        if (!m) throw new Error(`header.php içinde function ${name} bulunamadı`);
        return m[0];
    }).join('\n');
}

// İngilizce sözlük: header.php'nin POPS_I18N olarak bastığı gibi ortak sözlük + sayfanın sözlükleri
export function dictFor(page) {
    const dir = path.join(ROOT, 'Dashboard', 'lang', 'en');
    const dict = JSON.parse(fs.readFileSync(path.join(dir, 'common.json'), 'utf8'));
    if (page) {
        for (const n of fs.readdirSync(dir).sort()) {
            if (n === page + '.json' || (n.startsWith(page + '.') && n.endsWith('.json'))) {
                Object.assign(dict, JSON.parse(fs.readFileSync(path.join(dir, n), 'utf8')));
            }
        }
    }
    return dict;
}

function memoryStorage() {
    const m = new Map();
    return {
        getItem: (k) => (m.has(k) ? m.get(k) : null),
        setItem: (k, v) => { m.set(k, String(v)); },
        removeItem: (k) => { m.delete(k); },
    };
}

// Ortak betik yüklenmiş bir bağlam: { POps, ctx }. lang 'en' ise dict window.POPS_I18N olur.
export function loadPanel({ lang = 'tr', dict = {} } = {}) {
    const noop = () => {};
    const document = {
        addEventListener: noop, removeEventListener: noop, dispatchEvent: noop,
        getElementById: () => null, querySelector: () => null, querySelectorAll: () => [],
        body: null, hidden: false, cookie: '',
    };
    const ctx = vm.createContext({
        document, console, setTimeout, clearTimeout,
        sessionStorage: memoryStorage(), localStorage: memoryStorage(),
        addEventListener: noop, removeEventListener: noop,
        location: { protocol: 'http:', href: 'http://panel.test/', search: '' },
        POPS_LANG: lang, POPS_I18N: lang === 'tr' ? undefined : dict, USER_ROLE: 'admin',
    });
    ctx.window = ctx;
    vm.runInContext(headerHelpers(), ctx, { filename: 'header.php (escapeHtml, jsArg)' });
    vm.runInContext(read('Dashboard/assets/pops_script.js'), ctx, { filename: 'Dashboard/assets/pops_script.js' });
    return { POps: ctx.POps, ctx };
}

// Sayfa betiğinin (Dashboard/assets/pages/<ad>.js) saf yardımcıları: betik bir module nesnesi görünce yalnızca
// yardımcıları verir, sayfayı kurmaz
export function loadPageHelpers(page, opts) {
    const panel = loadPanel(opts);
    panel.ctx.module = { exports: {} };
    vm.runInContext(read(`Dashboard/assets/pages/${page}.js`), panel.ctx, { filename: `Dashboard/assets/pages/${page}.js` });
    const helpers = panel.ctx.module.exports;
    delete panel.ctx.module;
    return { ...panel, helpers };
}
