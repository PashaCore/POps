// =================================================================
// POps panel — ortak betik (bütün sayfalarda, <head> içinde yüklenir)
//   POps.api / get / post / del  : tek istek sarmalayıcı (JSON, 401 -> giriş, FastAPI 'detail' hatası)
//   POps.toast(tür, metin)       : sağ alttaki bildirim (success | error | warning | info)
//   POps.confirm / prompt / alert: tarayıcı penceresi yerine sayfa içi pencere (Promise döner)
//   POps.busy / act              : düğmeyi istek sürerken kilitler; hata/başarı bildirimini gösterir
//   openModal / closeModal       : sayfadaki .modal-overlay pencereleri (odak tuzağı, Esc)
//   POps.watchDevices            : cihaz listesini (state.devices) yalnızca isteyen sayfada yoklar
//   POps.iconHtml / iconEl       : çizgi simge (assets/pops_icons.svg)
//   POps.menu(düğme, öğeler)     : açılır menü;  POps.drawer: sağdaki ayrıntı paneli
//   POps.pageTabs(çubuk)          : çok bölümlü sayfaların sekmeleri (?tab=)
//   POps.chart.line / bars       : bağımlılıksız SVG grafik (Sistem → Genel bakış)
//   POps.relTime / timeHtml      : "3 dk önce" (üstüne gelince tam tarih ve saat)
//   POps.jobs                    : süren işlemler (task_ids) — yan menünün altındaki işlem merkezi
//   POps.t / tn / tx / tHtml     : arayüz dili (Türkçe metin anahtardır; bkz. docs/i18n.md), POps.locale
// Metinler her zaman textContent ile yazılır; sunucudan gelen değer HTML olarak yorumlanmaz.
// =================================================================

const API_HTTP = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '';

// Ortak durum: cihaz listesi (POps.watchDevices çağıran sayfalar ve POps.dev buradan okur)
const state = {
    devices: [],
    devicesLoaded: false,
    devicesError: null,
    customLabs: [],
    mainPcs: {},
    labLayouts: {},
    labsStats: {},
    terminalHistory: []
};

const POps = window.POps = window.POps || {};

// ============== DİL ==============
// Türkçe metin anahtardır (gettext gibi). İngilizce arayüzde header.php ortak sözlükle sayfanın sözlüğünü
// (lang/en/common.json + lang/en/<sayfa>.json) window.POPS_I18N olarak basar; karşılığı olmayan metin Türkçe kalır.
//   POps.t('Cihazlar')                       düz metin (textContent; HTML'e escapeHtml(...) ile)
//   POps.t('{name} silinsin mi?', { name })  yer tutucular
//   POps.tn('{n} bilgisayar', n)             çoğul: İngilizce değer {"one": "{n} computer", "other": "{n} computers"} olabilir
//   POps.tx('Kapat', 'power')                aynı Türkçe metnin başka anlamı (sözlükte "Kapat|power"; yoksa Türkçe)
//   POps.tHtml(metin, params, html)          kaçırılmış HTML; html: { ad: '<b>…</b>' } yer tutucuya çağıranın kurduğu HTML
//   POps.tNodes('… {link} …', null, { link }) yer tutucuya DOM öğesi; dizi döner: el.append(...dizi)
// Sunucuya giden değerler (görev adı, gerekçe…) çevrilmez; yalnızca gösterilen metin çevrilir.
POps.lang = window.POPS_LANG === 'en' ? 'en' : 'tr';
POps.locale = POps.lang === 'en' ? 'en-GB' : 'tr-TR';
const I18N_DICT = POps.lang !== 'tr' && window.POPS_I18N && typeof window.POPS_I18N === 'object' ? window.POPS_I18N : {};
const i18nHas = (o, k) => Object.prototype.hasOwnProperty.call(o, k);
// Sözlükteki karşılık (çoğulda params.n'ye göre "one"/"other"); yoksa Türkçe anahtar
function i18nTemplate(key, fallback, params) {
    let v = i18nHas(I18N_DICT, key) ? I18N_DICT[key] : null;
    if (v && typeof v === 'object') v = (params && Math.abs(Number(params.n)) === 1 && v.one) || v.other;
    return typeof v === 'string' && v ? v : fallback;
}
// Metni yer tutucularına böler; her parça için fn(parça, ad|''); yer tutucu değerleri metne sonradan katılmaz
function i18nParts(template, fn) {
    return template.split(/(\{\w+\})/).filter(Boolean).map(part => fn(part, /^\{\w+\}$/.test(part) ? part.slice(1, -1) : ''));
}
const i18nValue = (params, k) => (k && params && i18nHas(params, k) && params[k] != null ? String(params[k]) : null);
const i18nFill = (template, params) => i18nParts(template, (part, k) => { const v = i18nValue(params, k); return v === null ? part : v; }).join('');
// Metin ve değerler kaçırılır; html'deki parçalar çağıranın kurduğu HTML'dir (çağrı yerinde denetlenir) ve olduğu gibi girer
function i18nFillHtml(template, params, html) {
    return template.split(/(\{\w+\})/).filter(Boolean).map(part => {
        const k = /^\{\w+\}$/.test(part) ? part.slice(1, -1) : '';
        if (k && html && i18nHas(html, k)) return String(html[k]);
        const v = i18nValue(params, k);
        return escapeHtml(v === null ? part : v);
    }).join('');
}
const i18nKey = (text) => String(text == null ? '' : text);
POps.t = (text, params) => i18nFill(i18nTemplate(i18nKey(text), i18nKey(text), params), params);
POps.tn = (text, n, params) => POps.t(text, Object.assign({}, params, { n }));
POps.tx = (text, context, params) => i18nFill(i18nTemplate(i18nKey(text) + '|' + context, i18nKey(text), params), params);
POps.tHtml = (text, params, html) => i18nFillHtml(i18nTemplate(i18nKey(text), i18nKey(text), params), params, html);
POps.tnHtml = (text, n, params, html) => POps.tHtml(text, Object.assign({}, params, { n }), html);
POps.tNodes = function (text, params, nodes) {
    return i18nParts(i18nTemplate(i18nKey(text), i18nKey(text), params), (part, k) => {
        if (k && nodes && i18nHas(nodes, k)) return nodes[k];
        const v = i18nValue(params, k);
        return v === null ? part : v;
    });
};
// Yüzde: Türkçede "%40", İngilizcede "40%"
POps.pct = (n) => POps.t('%{n}', { n });
// Dil seçimi bu tarayıcıda 1 yıl tutulur; sayfa yeni dille yeniden yüklenir
POps.setLang = function (lang) {
    const v = lang === 'en' ? 'en' : 'tr';
    document.cookie = 'pops_lang=' + v + '; Path=/; Max-Age=31536000; SameSite=Lax' + (location.protocol === 'https:' ? '; Secure' : '');
    location.reload();
};

// ============== İSTEK SARMALAYICI ==============
// Türkçe anahtarlar; gösterilirken POps.t ile çevrilir (sunucunun Türkçe "detail" metinleri de: common.json)
const STATUS_TEXT = {
    400: 'İstek geçersiz.',
    403: 'Bu işlem için yetkiniz yok.',
    404: 'Bulunamadı. Sunucu güncellemesi gerekebilir.',
    409: 'Çakışma: kayıt zaten var ya da işlem sürüyor.',
    413: 'Dosya çok büyük.',
    422: 'Gönderilen bilgiler eksik ya da hatalı.',
    429: 'Çok fazla istek. Biraz sonra yeniden deneyin.',
    500: 'Sunucu hatası.',
    502: 'Sunucuya ulaşılamadı.',
    503: 'Hizmet şu an kullanılamıyor.',
    504: 'Sunucu zamanında yanıt vermedi.'
};

class ApiError extends Error {
    constructor(message, status, data) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
        this.data = data;
    }
}
POps.ApiError = ApiError;

// FastAPI hatası: {"detail": "metin"} ya da doğrulama hatasında {"detail": [{loc, msg}, ...]};
// bazı uçlar 200 ile {"status": "error", "message": "..."} döner
function apiErrorText(data, status) {
    if (data && typeof data === 'object') {
        const d = data.detail;
        if (typeof d === 'string' && d.trim()) return POps.t(d.trim());
        if (Array.isArray(d) && d.length) {
            return d.map(x => {
                if (!x || typeof x !== 'object') return String(x);
                const loc = Array.isArray(x.loc) ? x.loc.filter(p => p !== 'body').join('.') : '';
                return (loc ? loc + ': ' : '') + (x.msg || '');
            }).join('; ');
        }
        if (d && typeof d === 'object' && typeof d.message === 'string') return POps.t(d.message);
        if (typeof data.message === 'string' && data.message.trim()) return POps.t(data.message.trim());
        if (typeof data.error === 'string' && data.error.trim()) return POps.t(data.error.trim());
    } else if (typeof data === 'string') {
        const t = data.trim();
        if (t && t.length < 300 && !/<[a-z!]/i.test(t)) return POps.t(t);
    }
    return STATUS_TEXT[status] ? POps.t(STATUS_TEXT[status]) : POps.t('Sunucu hatası (HTTP {status}).', { status });
}

// Hata metni; sunucunun ya da sayfanın Türkçe metni sözlükte varsa çevrilir
POps.errorMessage = function (err, fallback) {
    return POps.t((err && err.message) || fallback || 'Beklenmeyen hata.');
};

POps.api = async function (path, opts) {
    const o = Object.assign({ method: 'GET' }, opts || {});
    const headers = new Headers(o.headers || {});
    headers.set('X-Requested-With', 'XMLHttpRequest');
    let body = o.body;
    if (body != null && typeof body === 'object' && !(body instanceof FormData) && !(body instanceof Blob) && !(body instanceof URLSearchParams)) {
        body = JSON.stringify(body);
        headers.set('Content-Type', 'application/json');
    } else if (typeof body === 'string' && !headers.has('Content-Type')) {
        headers.set('Content-Type', 'application/json');
    }
    const url = /^https?:\/\//.test(path) ? path : API_HTTP + path;
    let res;
    try {
        res = await fetch(url, { method: o.method, headers, body, credentials: 'same-origin', signal: o.signal, keepalive: !!o.keepalive, cache: o.cache || 'no-store' });
    } catch (e) {
        if (e && e.name === 'AbortError') throw e;
        throw new ApiError(POps.t('Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.'), 0, null);
    }
    if (res.status === 401) {
        // Oturum bitti: giriş sayfasına; çağıranın devam etmesi beklenmez
        window.location.href = '/logout';
        return new Promise(() => {});
    }
    let data = null;
    if (res.status !== 204) {
        const ct = res.headers.get('content-type') || '';
        if (ct.includes('application/json')) data = await res.json().catch(() => null);
        else data = await res.text().catch(() => '');
    }
    if (!res.ok) throw new ApiError(apiErrorText(data, res.status), res.status, data);
    if (data && typeof data === 'object' && !Array.isArray(data) && data.status === 'error') {
        throw new ApiError(POps.t((typeof data.message === 'string' && data.message) || (typeof data.detail === 'string' && data.detail) || 'İşlem başarısız.'), res.status, data);
    }
    // Görev oluşturan istekler (task_ids döner) işlem merkezine kendiliğinden eklenir
    if (data && Array.isArray(data.task_ids) && data.task_ids.length && POps.jobs) {
        const seq = o.body && typeof o.body === 'object' && Array.isArray(o.body.taskSequence) ? o.body.taskSequence : [];
        POps.jobs.track(o.jobTitle || (seq[0] && seq[0].name ? POps.taskName(seq[0].name) : POps.t('İşlem')), data.task_ids);
    }
    return data;
};
POps.get = (path, opts) => POps.api(path, Object.assign({}, opts, { method: 'GET' }));
POps.post = (path, body, opts) => POps.api(path, Object.assign({}, opts, { method: 'POST', body: body === undefined ? {} : body }));
POps.del = (path, opts) => POps.api(path, Object.assign({}, opts, { method: 'DELETE' }));

// Eski çağrılar için (endpoint, {method, body: JSON metni})
async function apiRequest(endpoint, options) {
    return POps.api(endpoint, options || {});
}

// ============== YARDIMCILAR ==============
POps.escape = function (s) { return escapeHtml(s); };
POps.$ = (id) => document.getElementById(id);
POps.el = function (tag, attrs, children) {
    const el = document.createElement(tag);
    if (attrs) {
        for (const k of Object.keys(attrs)) {
            const v = attrs[k];
            if (v == null || v === false) continue;
            if (k === 'className') el.className = v;
            else if (k === 'text') el.textContent = v;
            else if (k === 'dataset') Object.assign(el.dataset, v);
            else el.setAttribute(k, v === true ? '' : v);
        }
    }
    (children || []).forEach(c => { if (c != null) el.append(c); });
    return el;
};
// Hazır durum blokları (yalnızca sabit metin; değişken değer textContent ile eklenir)
POps.setLoading = function (el, text) {
    if (!el) return;
    el.replaceChildren(POps.el('div', { className: 'loading-state', role: 'status' }, [
        POps.el('span', { className: 'spinner', 'aria-hidden': 'true' }), document.createTextNode(text || POps.t('Yükleniyor…'))
    ]));
};
POps.setEmpty = function (el, opts) {
    if (!el) return;
    const o = opts || {};
    const box = POps.el('div', { className: 'empty-state' + (o.compact ? ' compact' : '') + (o.kind ? ' ' + o.kind : '') }, [
        POps.iconEl(o.icon || 'inbox'),
        o.title ? POps.el('h3', { text: o.title }) : null,
        o.text ? POps.el('p', { text: o.text }) : null
    ]);
    if (o.tag === 'tr') {
        const td = POps.el('td', { colspan: String(o.colspan || 1) }, [box]);
        el.replaceChildren(POps.el('tr', null, [td]));
    } else {
        el.replaceChildren(box);
    }
};
POps.setError = function (el, err, opts) {
    POps.setEmpty(el, Object.assign({ icon: 'alert', kind: 'error', title: POps.t('Veriler alınamadı'), text: POps.errorMessage(err) }, opts || {}));
};

// ============== BİLDİRİM (TOAST) ==============
const TOAST_TYPES = { success: 'check-circle', error: 'x-circle', warning: 'alert', info: 'info' };
const TOAST_MS = { success: 4000, info: 4500, warning: 6500, error: 8000 };
function toastContainer() {
    let c = document.getElementById('toastContainer');
    if (!c) {
        c = document.createElement('div');
        c.id = 'toastContainer';
        c.className = 'toast-container';
        document.body.appendChild(c);
    }
    return c;
}
POps.toast = function (type, message, opts) {
    if (type === 'danger') type = 'error';
    if (!TOAST_TYPES[type]) type = 'info';
    const o = opts || {};
    if (!document.body) { document.addEventListener('DOMContentLoaded', () => POps.toast(type, message, opts), { once: true }); return; }
    const c = toastContainer();
    while (c.children.length >= 5) c.firstElementChild.remove();
    const t = document.createElement('div');
    t.className = 'toast ' + type;
    t.setAttribute('role', type === 'error' ? 'alert' : 'status');
    const ic = POps.iconEl(TOAST_TYPES[type], 'toast-icon');
    const msg = document.createElement('div');
    msg.className = 'toast-msg';
    msg.textContent = String(message == null ? '' : message);
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'toast-close';
    close.setAttribute('aria-label', POps.t('Kapat'));
    close.appendChild(POps.iconEl('x', 'sm'));
    t.append(ic, msg, close);
    c.appendChild(t);
    let timer = null;
    const dismiss = () => {
        if (t.classList.contains('leaving')) return;
        clearTimeout(timer);
        t.classList.add('leaving');
        setTimeout(() => t.remove(), 200);
    };
    const arm = () => { clearTimeout(timer); timer = setTimeout(dismiss, o.duration || TOAST_MS[type]); };
    close.addEventListener('click', dismiss);
    t.addEventListener('mouseenter', () => clearTimeout(timer));
    t.addEventListener('mouseleave', arm);
    t.addEventListener('focusin', () => clearTimeout(timer));
    t.addEventListener('focusout', arm);
    arm();
    return { close: dismiss };
};
// Eski imza: showToast(metin, tür, süre)
function showToast(message, type, duration) {
    if (TOAST_TYPES[message] && !TOAST_TYPES[type] && type !== 'danger') { const m = type; type = message; message = m; }
    return POps.toast(type || 'info', message, { duration });
}

// ============== KATMAN (modal / pencere) YÖNETİMİ ==============
const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
function visibleFocusables(root) {
    return [...root.querySelectorAll(FOCUSABLE)].filter(el => el.offsetParent !== null || el === document.activeElement);
}
function topLayer() {
    const dialogs = document.querySelectorAll('.pops-dialog-overlay');
    if (dialogs.length) return dialogs[dialogs.length - 1];
    const modals = document.querySelectorAll('.modal-overlay.open');
    return modals.length ? modals[modals.length - 1] : null;
}
function syncBodyLock() {
    document.body.classList.toggle('modal-open', !!topLayer());
}
function trapTab(e, root) {
    const items = visibleFocusables(root);
    if (!items.length) { e.preventDefault(); return; }
    const first = items[0], last = items[items.length - 1];
    if (e.shiftKey && (document.activeElement === first || !root.contains(document.activeElement))) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && (document.activeElement === last || !root.contains(document.activeElement))) { e.preventDefault(); first.focus(); }
}
function restoreFocus(el) {
    if (el && typeof el.focus === 'function' && document.contains(el)) { try { el.focus({ preventScroll: true }); } catch (e) { /* yok */ } }
}

window.openModal = function (id) {
    const el = typeof id === 'string' ? document.getElementById(id) : id;
    if (!el || el.classList.contains('open')) return;
    el._popsReturnFocus = document.activeElement;
    // Açık başka bir modalın üstünde açılan modal üstte görünsün
    document.body.appendChild(el);
    el.classList.add('open');
    const box = el.querySelector('.modal-box');
    if (box) {
        box.setAttribute('role', 'dialog');
        box.setAttribute('aria-modal', 'true');
        if (!box.hasAttribute('tabindex')) box.tabIndex = -1;
        const title = box.querySelector('.modal-title');
        if (title) {
            if (!title.id) title.id = (el.id || 'modal') + 'Title';
            box.setAttribute('aria-labelledby', title.id);
        }
    }
    syncBodyLock();
    setTimeout(() => {
        const target = el.querySelector('[autofocus]')
            || el.querySelector('.modal-body input:not([type=hidden]):not([type=checkbox]):not([disabled]), .modal-body select:not([disabled]), .modal-body textarea:not([disabled])')
            || box;
        if (target) { try { target.focus(); } catch (e) { /* yok */ } }
    }, 20);
    el.dispatchEvent(new CustomEvent('pops:modal-open'));
};
window.closeModal = function (id) {
    const el = typeof id === 'string' ? document.getElementById(id) : id;
    if (!el || !el.classList.contains('open')) return;
    el.classList.remove('open');
    syncBodyLock();
    restoreFocus(el._popsReturnFocus);
    el._popsReturnFocus = null;
    el.dispatchEvent(new CustomEvent('pops:modal-close'));
};

document.addEventListener('keydown', (e) => {
    const layer = topLayer();
    if (!layer || layer.classList.contains('pops-dialog-overlay')) return;  // pencere kendi tuşlarını yönetir
    if (e.key === 'Escape') { e.preventDefault(); window.closeModal(layer); }
    else if (e.key === 'Tab') trapTab(e, layer);
});
// Modalın dışına (karartılmış alana) tıklayınca kapanır
document.addEventListener('mousedown', (e) => { document._popsDownTarget = e.target; });
document.addEventListener('click', (e) => {
    const t = e.target;
    if (t && t.classList && t.classList.contains('modal-overlay') && t.classList.contains('open') && document._popsDownTarget === t) window.closeModal(t);
    const closer = t && t.closest && t.closest('[data-close-modal]');
    if (closer) { const ov = closer.closest('.modal-overlay'); if (ov) window.closeModal(ov); }
});

// ============== ONAY / GİRİŞ PENCERESİ ==============
let dialogSeq = 0;
function openDialog(kind, opts) {
    const o = Object.assign({}, opts || {});
    return new Promise((resolve) => {
        const n = ++dialogSeq;
        const prevFocus = document.activeElement;
        const overlay = document.createElement('div');
        overlay.className = 'pops-dialog-overlay';
        const box = document.createElement('div');
        box.className = 'pops-dialog' + (o.danger ? ' danger' : (o.tone ? ' ' + o.tone : ''));
        box.setAttribute('role', kind === 'prompt' ? 'dialog' : 'alertdialog');
        box.setAttribute('aria-modal', 'true');
        box.tabIndex = -1;
        const titleId = 'popsDialogTitle' + n, msgId = 'popsDialogMsg' + n;
        box.setAttribute('aria-labelledby', titleId);

        const body = document.createElement('div');
        body.className = 'pops-dialog-body';
        const ic = document.createElement('div');
        ic.className = 'pops-dialog-icon';
        ic.appendChild(POps.iconEl(o.icon || (o.danger ? 'alert' : kind === 'prompt' ? 'edit' : kind === 'alert' ? 'info' : 'help')));
        const content = document.createElement('div');
        content.className = 'pops-dialog-content';
        const title = document.createElement('h2');
        title.className = 'pops-dialog-title';
        title.id = titleId;
        title.textContent = o.title || POps.t(kind === 'confirm' ? 'Emin misiniz?' : kind === 'prompt' ? 'Bilgi girin' : 'Bilgi');
        content.appendChild(title);
        if (o.message) {
            const p = document.createElement('p');
            p.className = 'pops-dialog-message';
            p.id = msgId;
            p.textContent = String(o.message);
            content.appendChild(p);
            box.setAttribute('aria-describedby', msgId);
        }
        // Kopyalanabilir kod(lar): alert({codes: ['123456']})
        (o.codes || (o.code ? [o.code] : [])).forEach(code => {
            const row = document.createElement('div');
            row.className = 'pops-dialog-code';
            const c = document.createElement('code');
            c.textContent = String(code);
            const copy = document.createElement('button');
            copy.type = 'button';
            copy.className = 'btn secondary sm';
            copy.append(POps.iconEl('copy', 'sm'), document.createTextNode(POps.t('Kopyala')));
            copy.addEventListener('click', async () => {
                try { await navigator.clipboard.writeText(String(code)); POps.toast('success', POps.t('Kopyalandı.')); }
                catch (e) { POps.toast('warning', POps.t('Kopyalanamadı; kodu seçip elle kopyalayın.')); }
            });
            row.append(c, copy);
            content.appendChild(row);
        });
        if (o.note) {
            const note = document.createElement('p');
            note.className = 'pops-dialog-note';
            note.textContent = String(o.note);
            content.appendChild(note);
        }
        let input = null, err = null;
        if (kind === 'prompt') {
            const field = document.createElement('div');
            field.className = 'pops-dialog-field field';
            const inputId = 'popsDialogInput' + n;
            if (o.label) {
                const lab = document.createElement('label');
                lab.setAttribute('for', inputId);
                lab.textContent = o.label;
                field.appendChild(lab);
            }
            input = document.createElement(o.multiline ? 'textarea' : 'input');
            input.id = inputId;
            if (!o.multiline) input.type = o.inputType || 'text';
            if (o.multiline) input.rows = 3;
            if (o.placeholder) input.placeholder = o.placeholder;
            if (o.maxLength) input.maxLength = o.maxLength;
            if (o.inputMode) input.inputMode = o.inputMode;
            if (o.pattern) input.pattern = o.pattern;
            input.autocomplete = o.autocomplete || 'off';
            input.spellcheck = false;
            input.value = o.defaultValue == null ? '' : String(o.defaultValue);
            if (!o.label) input.setAttribute('aria-labelledby', titleId);
            err = document.createElement('div');
            err.className = 'field-error';
            err.id = inputId + 'Err';
            err.setAttribute('aria-live', 'polite');
            input.setAttribute('aria-describedby', err.id);
            field.append(input, err);
            if (o.hint) {
                const h = document.createElement('div');
                h.className = 'field-hint';
                h.textContent = o.hint;
                field.appendChild(h);
            }
            content.appendChild(field);
        }
        body.append(ic, content);

        const footer = document.createElement('div');
        footer.className = 'pops-dialog-footer';
        let cancelBtn = null;
        if (kind !== 'alert') {
            cancelBtn = document.createElement('button');
            cancelBtn.type = 'button';
            cancelBtn.className = 'btn secondary';
            cancelBtn.textContent = o.cancelText || POps.t('Vazgeç');
            footer.appendChild(cancelBtn);
        }
        const okBtn = document.createElement('button');
        okBtn.type = 'button';
        okBtn.className = 'btn' + (o.danger ? ' danger' : '');
        okBtn.textContent = o.confirmText || POps.t(kind === 'alert' ? 'Tamam' : kind === 'prompt' ? 'Kaydet' : 'Onayla');
        footer.appendChild(okBtn);
        box.append(body, footer);
        overlay.appendChild(box);

        let done = false;
        function finish(result) {
            if (done) return;
            done = true;
            overlay.remove();
            syncBodyLock();
            restoreFocus(prevFocus);
            resolve(result);
        }
        function cancel() { finish(kind === 'prompt' ? null : false); }
        function showError(text) {
            err.textContent = text;
            input.closest('.field').classList.add('has-error');
            input.setAttribute('aria-invalid', 'true');
            input.focus();
        }
        function accept() {
            if (kind !== 'prompt') return finish(true);
            let v = input.value;
            if (o.trim !== false) v = v.trim();
            if (o.required !== false && !v) return showError(o.requiredText || POps.t('Bu alan boş bırakılamaz.'));
            if (typeof o.validate === 'function') {
                const msg = o.validate(v);
                if (msg) return showError(msg);
            }
            finish(v);
        }
        okBtn.addEventListener('click', accept);
        if (cancelBtn) cancelBtn.addEventListener('click', cancel);
        if (input) input.addEventListener('input', () => { input.closest('.field').classList.remove('has-error'); input.removeAttribute('aria-invalid'); });
        overlay.addEventListener('mousedown', (e) => { overlay._down = e.target; });
        overlay.addEventListener('click', (e) => { if (e.target === overlay && overlay._down === overlay) cancel(); });
        overlay.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancel(); }
            else if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
                const t = e.target;
                if (t && t.tagName === 'TEXTAREA') return;
                if (t && t.tagName === 'BUTTON' && t !== okBtn) return;   // Vazgeç / Kopyala kendi işini yapar
                e.preventDefault();
                e.stopPropagation();
                accept();
            } else if (e.key === 'Tab') { trapTab(e, box); e.stopPropagation(); }
        });
        document.body.appendChild(overlay);
        syncBodyLock();
        setTimeout(() => {
            if (input) { input.focus(); if (input.select) input.select(); }
            else okBtn.focus();
        }, 10);
    });
}
// confirm({title, message, confirmText, cancelText, danger, icon, note}) -> Promise<boolean>
POps.confirm = (opts) => openDialog('confirm', typeof opts === 'string' ? { message: opts } : opts);
// prompt({title, message, label, placeholder, defaultValue, required, validate, maxLength, multiline}) -> Promise<string|null>
POps.prompt = (opts) => openDialog('prompt', typeof opts === 'string' ? { message: opts } : opts);
// alert({title, message, code|codes, note}) -> Promise<true>
POps.alert = (opts) => openDialog('alert', typeof opts === 'string' ? { message: opts } : opts);

// ============== DÜĞME DURUMU ==============
// İstek sürerken düğmeyi kilitler ve dönen halka gösterir; ikinci tıklama yok sayılır
POps.busy = async function (btn, fn) {
    if (!btn) return fn();
    if (btn.dataset.busy === '1') return undefined;
    btn.dataset.busy = '1';
    const wasDisabled = btn.disabled;
    btn.disabled = true;
    btn.classList.add('is-loading');
    btn.setAttribute('aria-busy', 'true');
    try {
        return await fn();
    } finally {
        delete btn.dataset.busy;
        btn.disabled = wasDisabled;
        btn.classList.remove('is-loading');
        btn.removeAttribute('aria-busy');
    }
};
// busy + bildirim: başarıda success metni, hatada sunucunun açıklaması. Başarılıysa true döner.
POps.act = async function (btn, fn, opts) {
    const o = opts || {};
    try {
        const r = await POps.busy(btn, fn);
        if (o.success) POps.toast('success', typeof o.success === 'function' ? o.success(r) : o.success);
        return true;
    } catch (e) {
        if (e && e.name === 'AbortError') return false;
        POps.toast('error', (o.error ? o.error + ' ' : '') + POps.errorMessage(e));
        return false;
    }
};

// ============== CİHAZ LİSTESİ ==============
// Yalnızca POps.watchDevices() çağıran sayfa yoklar (eskiden her sayfa 3 sn'de bir üç istek atıyordu)
let inventoryAt = 0;
let inventoryMap = {};
async function loadDevices() {
    const wantInventory = !!state.withInventory;
    const [devices, labs, settings, inv] = await Promise.all([
        POps.get('/api/devices'),
        POps.get('/api/custom_labs').catch(() => state.customLabs),
        POps.get('/api/lab_settings').catch(() => null),
        wantInventory && Date.now() - inventoryAt > 60000 ? POps.get('/api/inventory').catch(() => null) : Promise.resolve(null)
    ]).catch(e => { state.devicesError = e; document.dispatchEvent(new CustomEvent('pops_data_updated', { detail: { error: e } })); throw e; });
    if (Array.isArray(inv)) {
        inventoryAt = Date.now();
        inventoryMap = {};
        inv.forEach(r => { inventoryMap[r.pc_name] = r; });
    }
    state.devices = (Array.isArray(devices) ? devices : []).map(d => {
        const hw = inventoryMap[d.hostname] || {};
        return Object.assign({}, d, {
            id: d.hostname,
            status: d.status || 'Offline',
            ip: d.ip || hw.ip_address || '',
            mac: hw.mac_address && hw.mac_address !== '-' ? hw.mac_address : '',
            last_seen: d.last_seen || '-',
            active_window: d.active_window || '-'
        });
    });
    state.customLabs = Array.isArray(labs) ? labs : [];
    if (settings && typeof settings === 'object') {
        state.mainPcs = {};
        state.labLayouts = {};
        for (const lab in settings) {
            state.mainPcs[lab] = settings[lab].main_pc;
            state.labLayouts[lab] = settings[lab].layout_json;
        }
    }
    const stats = {};
    state.devices.forEach(d => { stats[d.lab] = (stats[d.lab] || 0) + 1; });
    state.labsStats = stats;
    state.devicesLoaded = true;
    state.devicesError = null;
    document.dispatchEvent(new CustomEvent('pops_data_updated', { detail: {} }));
    return state.devices;
}
POps.loadDevices = loadDevices;
let devicesPoller = null;
POps.watchDevices = function (opts) {
    const o = opts || {};
    if (o.inventory) state.withInventory = true;
    if (!devicesPoller) {
        loadDevices().catch(() => {});
        devicesPoller = window.popsPoll(loadDevices, o.interval || 5000);
    }
    return devicesPoller;
};
POps.isOnline = (d) => String((d && d.status) || '').toLowerCase() === 'online';
POps.isOffline = (d) => String((d && d.status) || '').toLowerCase() === 'offline' || !(d && d.status);
POps.deviceName = (d) => (d && (d.display_name || d.real_hostname || d.hostname || d.hw_id)) || '';

// ============== GÜÇ KOMUTLARI ==============
// powerCommand('ALL'|'LAB'|'PC', 'shutdown'|'restart', ad, düğme)
window.powerCommand = async function (targetType, action, targetName, btn) {
    const isShutdown = action === 'shutdown';
    const cmd = isShutdown ? 'shutdown /s /f /t 5' : 'shutdown /r /f /t 5';
    let targets;
    if (targetType === 'PC') {
        targets = [targetName];
    } else {
        if (!state.devicesLoaded) { try { await loadDevices(); } catch (e) { POps.toast('error', POps.errorMessage(e)); return; } }
        const pool = targetType === 'LAB' ? state.devices.filter(d => d.lab === targetName) : state.devices;
        targets = pool.filter(d => !POps.isOffline(d)).map(d => d.hostname);
    }
    if (!targets.length) return POps.toast('warning', POps.t('Açık cihaz yok; komut gönderilmedi.'));
    const dev = targetType === 'PC' ? (state.devices.find(d => d.hostname === targetName) || { hostname: targetName }) : null;
    const who = targetType === 'ALL' ? POps.tn('Ağdaki açık {n} cihaz', targets.length)
        : targetType === 'LAB' ? POps.tn('{lab} sınıfındaki açık {n} cihaz', targets.length, { lab: targetName })
        : `${POps.deviceName(dev)} (${targetName})`;
    const ok = await POps.confirm({
        title: isShutdown ? POps.t('Cihazlar kapatılsın mı?') : POps.t('Cihazlar yeniden başlatılsın mı?'),
        message: isShutdown ? POps.t('{who} 5 saniye içinde kapatılacak. Kaydedilmemiş işler kaybolabilir.', { who })
            : POps.t('{who} 5 saniye içinde yeniden başlatılacak. Kaydedilmemiş işler kaybolabilir.', { who }),
        confirmText: isShutdown ? POps.tx('Kapat', 'power') : POps.t('Yeniden başlat'),
        danger: true,
        icon: 'power'
    });
    if (!ok) return;
    // Görev adı sunucuya Türkçe gider (veri); panelde POps.taskName ile çevrilir
    await POps.act(btn, () => POps.post('/api/deploy_orchestration', {
        target_mode: 'PC', targets, taskSequence: [{ name: isShutdown ? 'Güç: kapat' : 'Güç: yeniden başlat', type: 'CMD', command: cmd }]
    }), { success: (r) => POps.tn('Komut kuyruğa eklendi ({n} cihaz).', (r && r.created) || targets.length) });
};

// wakeUpCommand('ALL'|'LAB'|'PC', ad, düğme)
window.wakeUpCommand = async function (targetType, targetName, btn) {
    if (targetType === 'ALL') {
        const ok = await POps.confirm({ title: POps.t('Bütün ağ uyandırılsın mı?'), message: POps.t('MAC adresi bilinen bütün kapalı cihazlara uyandırma (WOL) sinyali gönderilecek.'), confirmText: POps.t('Uyandır'), icon: 'zap' });
        if (!ok) return;
        await POps.act(btn, () => POps.post('/api/wake_all'), { success: (r) => POps.tn('{n} cihaza uyandırma sinyali gönderildi.', (r && r.woken_pcs) || 0) });
    } else if (targetType === 'LAB') {
        await POps.act(btn, () => POps.post('/api/wake_lab/' + encodeURIComponent(targetName)), { success: (r) => POps.tn('{lab}: {n} cihaza uyandırma sinyali gönderildi.', (r && r.woken_pcs) || 0, { lab: targetName }) });
    } else if (targetType === 'PC') {
        await POps.act(btn, () => POps.post('/api/wake_pc/' + encodeURIComponent(targetName)), { success: POps.t('Uyandırma sinyali gönderildi.') });
    }
};

// ============== 2FA ÖNERİSİ ==============
// 2FA isteğe bağlıdır (önerilir, zorunlu değil). Kendi 2FA'sı kapalı admin/superadmin'e Ayarlar ve Sistem
// sayfalarında başlığın altında kapatılabilir bir not gösterilir; kapatılınca bu tarayıcıda 7 gün görünmez.
// enabled verilmezse durum /api/admin/2fa/status'tan okunur.
const TWOFA_NUDGE_KEY = 'pops_2fa_nudge_hidden_until';
async function popsTwofaNudge(enabled) {
    if (!['admin', 'superadmin'].includes(window.USER_ROLE)) return;
    if (enabled === undefined) {
        try { enabled = !!(await POps.get('/api/admin/2fa/status')).enabled; } catch (e) { return; }
    }
    let box = document.getElementById('twofaNudge');
    let hiddenUntil = 0;
    try { hiddenUntil = Number(localStorage.getItem(TWOFA_NUDGE_KEY)) || 0; } catch (e) { /* özel pencere */ }
    if (enabled || hiddenUntil > Date.now()) { if (box) box.remove(); return; }
    const header = document.querySelector('.app-content .page-header');
    if (box || !header) return;
    box = document.createElement('div');
    box.id = 'twofaNudge';
    box.className = 'twofa-nudge';
    box.setAttribute('role', 'status');
    const link = POps.el('a', { href: 'settings#twofaCard', text: POps.t('Ayarlar → İki adımlı doğrulama') });
    const close = POps.el('button', { type: 'button', className: 'twofa-nudge-close', title: POps.t('7 gün gösterme'), 'aria-label': POps.t('Kapat') }, [POps.iconEl('x', 'sm')]);
    box.append(POps.iconEl('shield', 'sm'), POps.el('span', null, POps.tNodes('Hesabınızda iki adımlı doğrulama (2FA) kapalı. Önerilir: {link} bölümünden açabilirsiniz.', null, { link })), close);
    close.addEventListener('click', () => {
        try { localStorage.setItem(TWOFA_NUDGE_KEY, String(Date.now() + 7 * 24 * 60 * 60 * 1000)); } catch (e) { /* özel pencere */ }
        box.remove();
    });
    header.insertAdjacentElement('afterend', box);
}


// ============== SİMGELER ==============
POps.iconHtml = function (name, cls) {
    return `<svg class="ico${cls ? ' ' + escapeHtml(cls) : ''}" aria-hidden="true"><use href="${escapeHtml((window.POPS_ICONS || 'assets/pops_icons.svg') + '#i-' + name)}"></use></svg>`;
};
POps.iconEl = function (name, cls) {
    const NS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('class', 'ico' + (cls ? ' ' + cls : ''));
    svg.setAttribute('aria-hidden', 'true');
    const use = document.createElementNS(NS, 'use');
    use.setAttribute('href', (window.POPS_ICONS || 'assets/pops_icons.svg') + '#i-' + name);
    svg.append(use);
    return svg;
};

// ============== ZAMAN ==============
// Göreli zaman ("şimdi", "5 dk önce", "dün 14:02", "12 Eki 14:02"); tam hali title'da. Tarih biçimi POps.locale'e göre.
function popsDate(v) {
    if (v == null || v === '' || v === '-') return null;
    const d = v instanceof Date ? v : new Date(typeof v === 'number' && v < 1e12 ? v * 1000 : v);
    return isNaN(d.getTime()) ? null : d;
}
POps.toDate = popsDate;
POps.fullTime = function (v) {
    const d = popsDate(v);
    return d ? d.toLocaleString(POps.locale, { day: 'numeric', month: 'long', year: 'numeric', weekday: 'long', hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
};
POps.relTime = function (v) {
    const d = popsDate(v);
    if (!d) return '—';
    const now = new Date();
    const sec = Math.round((now - d) / 1000);
    const hm = d.toLocaleTimeString(POps.locale, { hour: '2-digit', minute: '2-digit' });
    if (sec < 0) return sec > -120 ? POps.t('şimdi') : d.toLocaleDateString(POps.locale, { day: 'numeric', month: 'short' }) + ' ' + hm;
    if (sec < 45) return POps.t('şimdi');
    if (sec < 3600) return POps.tn('{n} dk önce', Math.max(1, Math.round(sec / 60)));
    const sameDay = d.toDateString() === now.toDateString();
    if (sameDay && sec < 6 * 3600) return POps.tn('{n} sa önce', Math.round(sec / 3600));
    if (sameDay) return POps.t('bugün {time}', { time: hm });
    const y = new Date(now); y.setDate(now.getDate() - 1);
    if (d.toDateString() === y.toDateString()) return POps.t('dün {time}', { time: hm });
    const opts = { day: 'numeric', month: 'short' };
    if (d.getFullYear() !== now.getFullYear()) opts.year = 'numeric';
    return d.toLocaleDateString(POps.locale, opts) + ' ' + hm;
};
POps.timeHtml = function (v) {
    const d = popsDate(v);
    if (!d) return '—';
    return `<time datetime="${escapeHtml(d.toISOString())}" title="${escapeHtml(POps.fullTime(d))}">${escapeHtml(POps.relTime(d))}</time>`;
};
POps.duration = function (sec) {
    sec = Math.max(0, Math.round(Number(sec) || 0));
    const s = (n) => POps.tn('{n} sn', n), m = (n) => POps.tn('{n} dk', n), h = (n) => POps.tn('{n} sa', n);
    if (sec < 60) return s(sec);
    if (sec < 3600) return m(Math.floor(sec / 60)) + (sec % 60 && sec < 600 ? ' ' + s(sec % 60) : '');
    if (sec < 86400) return h(Math.floor(sec / 3600)) + (Math.floor(sec / 60) % 60 ? ' ' + m(Math.floor(sec / 60) % 60) : '');
    return POps.tn('{n} gün', Math.floor(sec / 86400));
};

// ============== İPUCU ==============
// data-tip="metin" taşıyan her öğe: üstüne gelince (300 ms; bir ipucu açıkken beklemeden) ya da klavyeyle odaklanınca, ekranın içinde kalacak
// biçimde öğenin altında (data-tip-pos="up": üstünde, "left": sağa hizalı) gösterilir. Açık menüsü olan ya da
// devre dışı öğede gösterilmez.
(function () {
    let tipEl = null, cur = null, timer = null;
    function hide() {
        clearTimeout(timer);
        timer = null;
        cur = null;
        if (tipEl) tipEl.hidden = true;
    }
    function show(el) {
        const text = el.getAttribute('data-tip');
        if (!text || el.disabled || el.getAttribute('aria-expanded') === 'true' || !el.isConnected) return;
        if (!tipEl) {
            tipEl = POps.el('div', { className: 'pops-tip', role: 'tooltip' });
            tipEl.hidden = true;
            document.body.append(tipEl);
        }
        tipEl.textContent = text;
        tipEl.hidden = false;
        const r = el.getBoundingClientRect();
        const w = tipEl.offsetWidth, h = tipEl.offsetHeight, gap = 8, pad = 6;
        const pos = el.getAttribute('data-tip-pos');
        let top = pos === 'up' ? r.top - h - gap : r.bottom + gap;
        if (top + h > window.innerHeight - pad) top = r.top - h - gap;
        if (top < pad) top = r.bottom + gap;
        let left = pos === 'left' ? r.right - w : r.left + r.width / 2 - w / 2;
        left = Math.max(pad, Math.min(left, window.innerWidth - w - pad));
        tipEl.style.top = Math.round(top) + 'px';
        tipEl.style.left = Math.round(left) + 'px';
    }
    POps.hideTip = hide;
    document.addEventListener('mouseover', (e) => {
        const el = e.target.closest ? e.target.closest('[data-tip]') : null;
        if (el === cur) return;
        const warm = tipEl && !tipEl.hidden;
        hide();
        if (!el) return;
        cur = el;
        timer = setTimeout(() => { if (cur === el) show(el); }, warm ? 0 : 300);
    });
    document.addEventListener('mouseout', (e) => { if (!e.relatedTarget) hide(); });
    document.addEventListener('focusin', (e) => {
        const el = e.target.closest ? e.target.closest('[data-tip]') : null;
        if (!el || el === cur) return;
        hide();
        let kb = false;
        try { kb = el.matches(':focus-visible'); } catch (err) { kb = false; }
        if (kb) { cur = el; show(el); }
    });
    document.addEventListener('focusout', hide);
    document.addEventListener('mousedown', hide, true);
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hide(); }, true);
    window.addEventListener('scroll', hide, true);
    window.addEventListener('resize', hide);
})();

// ============== GRAFİKLER ==============
// Bağımlılıksız SVG grafikler (Sistem → Genel bakış). Noktalar eşit aralıklıdır: [{ t, <anahtar>: sayı | null }];
// null "ölçüm yok" demektir (çizgi orada kesilir, çubuk çizilmez). Her noktanın üstüne gelince tip(nokta) metni
// ipucu olarak görünür. Renkler seri sınıfıyla verilir (.ch-c1 … .ch-c5, bkz. pops_theme.css).
//   POps.chart.line(kutu, noktalar, { series: [{ key, cls }], max, area, tip })
//   POps.chart.bars(kutu, noktalar, { series: [{ key, cls }], tip })
// Dönen: ölçeğin üst değeri (en az 1; line'da max verildiyse o).
POps.chart = (function () {
    const NS = 'http://www.w3.org/2000/svg';
    const W = 1000, H = 100;
    function svgEl(tag, attrs) {
        const e = document.createElementNS(NS, tag);
        Object.keys(attrs || {}).forEach(k => e.setAttribute(k, attrs[k]));
        return e;
    }
    function niceMax(v) {
        if (!(v > 0)) return 1;
        const p = Math.pow(10, Math.floor(Math.log10(v)));
        for (const m of [1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v) return m * p;
        return 10 * p;
    }
    const val = (p, k) => (p && typeof p[k] === 'number' && isFinite(p[k]) ? p[k] : null);
    function frame(box, points, o) {
        const svg = svgEl('svg', { viewBox: `0 0 ${W} ${H}`, preserveAspectRatio: 'none', class: 'ch-svg', 'aria-hidden': 'true' });
        svg.append(svgEl('line', { x1: 0, x2: W, y1: H / 2, y2: H / 2, class: 'ch-rule' }));
        svg.append(svgEl('line', { x1: 0, x2: W, y1: 0.5, y2: 0.5, class: 'ch-rule' }));
        box.replaceChildren(svg);
        return svg;
    }
    function hits(svg, points, o) {
        const w = W / points.length;
        points.forEach((p, i) => {
            const r = svgEl('rect', { x: (i * w).toFixed(2), y: 0, width: w.toFixed(2), height: H, class: 'ch-hit' });
            if (o.tip) { r.setAttribute('data-tip', o.tip(p)); r.setAttribute('data-tip-pos', 'up'); }
            svg.append(r);
        });
    }
    function line(box, points, o) {
        const n = points.length || 1, w = W / n;
        const top = o.max || niceMax(Math.max(0, ...points.flatMap(p => o.series.map(s => val(p, s.key) || 0))));
        const svg = frame(box, points, o);
        const y = (v) => (H - 1 - Math.min(v, top) / top * (H - 3)).toFixed(2);
        o.series.forEach((s, si) => {
            const segs = [];
            let cur = null;
            points.forEach((p, i) => {
                const v = val(p, s.key);
                if (v === null) { cur = null; return; }
                if (!cur) { cur = []; segs.push(cur); }
                cur.push([i * w + w / 2, y(v)]);
            });
            segs.forEach(seg => {
                if (seg.length === 1) seg = [[seg[0][0] - w / 3, seg[0][1]], [seg[0][0] + w / 3, seg[0][1]]];
                const d = seg.map((pt, i) => (i ? 'L' : 'M') + pt[0].toFixed(2) + ' ' + pt[1]).join(' ');
                if (o.area && si === 0) {
                    svg.append(svgEl('path', { d: `${d} L${seg[seg.length - 1][0].toFixed(2)} ${H} L${seg[0][0].toFixed(2)} ${H} Z`, class: 'ch-area ' + s.cls }));
                }
                svg.append(svgEl('path', { d, class: 'ch-line ' + s.cls, 'vector-effect': 'non-scaling-stroke' }));
            });
        });
        hits(svg, points, o);
        return top;
    }
    function bars(box, points, o) {
        const n = points.length || 1, w = W / n;
        const sum = (p) => o.series.reduce((a, s) => a + (val(p, s.key) || 0), 0);
        const top = niceMax(Math.max(0, ...points.map(sum)));
        const svg = frame(box, points, o);
        const bw = Math.max(w * 0.62, Math.min(w, 2)), off = (w - bw) / 2;
        points.forEach((p, i) => {
            if (o.series.every(s => val(p, s.key) === null)) return;
            const x = (i * w + off).toFixed(2);
            if (!sum(p)) { svg.append(svgEl('rect', { x, y: H - 1, width: bw.toFixed(2), height: 1, class: 'ch-zero' })); return; }
            let base = H;
            o.series.forEach(s => {
                const v = val(p, s.key) || 0;
                if (!v) return;
                const h = Math.max(1.2, v / top * (H - 2));
                base -= h;
                svg.append(svgEl('rect', { x, y: base.toFixed(2), width: bw.toFixed(2), height: h.toFixed(2), class: 'ch-bar ' + s.cls }));
            });
        });
        hits(svg, points, o);
        return top;
    }
    return { line, bars, niceMax };
})();

// ============== SAYFA SEKMELERİ ==============
// Çok bölümlü sayfalar (Sistem, Ayarlar, Politikalar…): <div class="tabs" id="…"><button class="tab" data-tab="x">
// ve paneller [data-pane="x"]. Seçili sekme adreste (?tab=x) tutulur; ilk sekme varsayılandır. Ok tuşlarıyla gezilir.
POps.pageTabs = function (bar, opts) {
    const o = opts || {};
    const param = o.param || 'tab';
    const tabs = [...bar.querySelectorAll('[data-tab]')];
    const names = tabs.map(t => t.dataset.tab);
    const def = o.def || names[0];
    let current = null;
    bar.setAttribute('role', 'tablist');
    tabs.forEach(t => t.setAttribute('role', 'tab'));
    function set(name, opt) {
        if (!names.includes(name)) name = def;
        current = name;
        tabs.forEach(t => {
            const on = t.dataset.tab === name;
            t.classList.toggle('active', on);
            t.setAttribute('aria-selected', on ? 'true' : 'false');
            t.tabIndex = on ? 0 : -1;
        });
        document.querySelectorAll(o.panes || '[data-pane]').forEach(p => { p.hidden = p.dataset.pane !== name; });
        if (!(opt && opt.silent)) {
            const u = new URL(location.href);
            if (name === def) u.searchParams.delete(param); else u.searchParams.set(param, name);
            history.replaceState(null, '', u.pathname + u.search + u.hash);
            if (opt && opt.scroll) window.scrollTo({ top: 0, behavior: 'smooth' });
        }
        if (o.onChange) o.onChange(name);
        return name;
    }
    bar.addEventListener('click', (e) => { const t = e.target.closest('[data-tab]'); if (t) set(t.dataset.tab); });
    bar.addEventListener('keydown', (e) => {
        if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
        e.preventDefault();
        const i = names.indexOf(current);
        const n = names[(i + (e.key === 'ArrowRight' ? 1 : names.length - 1)) % names.length];
        set(n);
        bar.querySelector(`[data-tab="${n}"]`).focus();
    });
    set(new URLSearchParams(location.search).get(param) || def, { silent: true });
    return { set, current: () => current };
};

// ============== AÇILIR MENÜ ==============
// POps.menu(düğme, [{ label, icon, danger, disabled, hint, onClick } | '-' | { header }])
let openMenuEl = null;
function closeMenu(focusBack) {
    if (!openMenuEl) return;
    const m = openMenuEl;
    openMenuEl = null;
    m.remove();
    if (m._anchor) {
        m._anchor.setAttribute('aria-expanded', 'false');
        if (focusBack) m._anchor.focus();
    }
}
POps.closeMenu = closeMenu;
POps.menu = function (anchor, items) {
    const again = openMenuEl && openMenuEl._anchor === anchor;
    closeMenu(false);
    if (again) return;
    const m = POps.el('div', { className: 'pops-menu', role: 'menu' });
    m._anchor = anchor;
    (items || []).forEach(it => {
        if (!it) return;
        if (it === '-') { m.append(POps.el('div', { className: 'msep', role: 'separator' })); return; }
        if (it.header) { m.append(POps.el('div', { className: 'mh', text: it.header })); return; }
        const b = POps.el('button', { type: 'button', className: 'mi' + (it.danger ? ' danger' : ''), role: 'menuitem' });
        if (it.disabled) b.disabled = true;
        if (it.title) b.title = it.title;
        if (it.icon) b.append(POps.iconEl(it.icon));
        b.append(POps.el('span', { text: it.label }));
        if (it.hint) b.append(POps.el('span', { className: 'k', text: it.hint }));
        b.addEventListener('click', (e) => { e.stopPropagation(); closeMenu(false); if (it.onClick) it.onClick(anchor); });
        m.append(b);
    });
    document.body.append(m);
    const r = anchor.getBoundingClientRect();
    const w = m.offsetWidth, h = m.offsetHeight;
    let left = r.left, top = r.bottom + 6;
    if (left + w > window.innerWidth - 8) left = Math.max(8, r.right - w);
    if (top + h > window.innerHeight - 8) top = Math.max(8, r.top - h - 6);
    m.style.left = left + 'px';
    m.style.top = top + 'px';
    anchor.setAttribute('aria-expanded', 'true');
    openMenuEl = m;
    const first = m.querySelector('.mi:not(:disabled)');
    if (first) first.focus();
};
document.addEventListener('click', (e) => { if (openMenuEl && !openMenuEl.contains(e.target) && !(openMenuEl._anchor && openMenuEl._anchor.contains(e.target))) closeMenu(false); });
document.addEventListener('keydown', (e) => {
    if (!openMenuEl) return;
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeMenu(true); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault();
        const list = [...openMenuEl.querySelectorAll('.mi:not(:disabled)')];
        if (!list.length) return;
        const i = list.indexOf(document.activeElement);
        list[(i + (e.key === 'ArrowDown' ? 1 : list.length - 1)) % list.length].focus();
    }
}, true);
window.addEventListener('resize', () => closeMenu(false));
window.addEventListener('scroll', () => closeMenu(false), true);

// ============== AYRINTI PANELİ ==============
// const body = POps.drawer.open('pc:LAB1-PC07', { onClose }) -> sayfa body.innerHTML'i doldurur
POps.drawer = (function () {
    let el = null, body = null, key = null, onClose = null, returnFocus = null;
    function ensure() {
        if (el) return;
        el = POps.el('aside', { className: 'drawer', id: 'popsDrawer', 'aria-label': POps.t('Ayrıntılar'), tabindex: '-1' });
        body = POps.el('div', { className: 'drawer-body' });
        el.append(body);
        document.body.append(el);
        // Yakalama evresinde: üstteki pencere (modal, onay, menü, arama) Esc ile kapanırken paneli de kapatmasın
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && !e.defaultPrevented && key !== null && !document.querySelector('.pops-dialog-overlay, .modal-overlay.open, .palette-overlay') && !openMenuEl) api.close();
        }, true);
    }
    const api = {
        open(k, opts) {
            ensure();
            const o = opts || {};
            if (key !== null && key !== k && onClose) { const f = onClose; onClose = null; f(); }
            if (key === null) returnFocus = document.activeElement;
            key = k;
            onClose = o.onClose || null;
            el.classList.add('open');
            document.body.classList.add('drawer-open');
            return body;
        },
        close() {
            if (!el || key === null) return;
            key = null;
            el.classList.remove('open');
            document.body.classList.remove('drawer-open');
            const f = onClose; onClose = null;
            if (f) f();
            if (returnFocus && document.contains(returnFocus)) returnFocus.focus();
        },
        isOpen(k) { return k === undefined ? key !== null : key === k; },
        key() { return key; },
        body() { ensure(); return body; }
    };
    return api;
})();

// ============== İŞLEM MERKEZİ ==============
// Görev oluşturan her istek (task_ids) burada izlenir; durum /api/tasks/status'tan okunur.
// Liste sekme boyunca sessionStorage'da tutulur; biten işler 15 dk sonra düşer.
const JOB_FINAL_OK = ['Completed', 'Completed (Rebooted)'];
const JOB_FINAL_BAD = ['Failed', 'Error', 'Cancelled', 'Interrupted', 'Timed Out', 'Denied', 'Unknown', 'Expired'];
POps.taskState = function (status) {
    if (JOB_FINAL_OK.includes(status)) return 'ok';
    if (JOB_FINAL_BAD.includes(status)) return 'bad';
    return 'run';
};
// Panelin sunucuya Türkçe yazdığı görev adlarının (veri) görünen hali: "Kapat · LAB1" -> "Shut down · LAB1".
// Yalnızca ilk parça ve yalnızca sözlükteki "…|task" girdileri çevrilir; kullanıcının yazdığı ad olduğu gibi kalır.
POps.taskName = function (title) {
    const t = String(title == null ? '' : title);
    const i = t.indexOf(' · ');
    return i < 0 ? POps.tx(t, 'task') : POps.tx(t.slice(0, i), 'task') + t.slice(i);
};
POps.jobs = (function () {
    const KEY = 'pops_jobs_v1';
    let jobs = [];
    let timer = null;
    try { jobs = JSON.parse(sessionStorage.getItem(KEY) || '[]') || []; } catch (e) { jobs = []; }
    function save() { try { sessionStorage.setItem(KEY, JSON.stringify(jobs)); } catch (e) { /* özel pencere */ } }
    function counts(j) {
        const c = { ok: 0, bad: 0, run: 0, total: j.ids.length };
        j.ids.forEach(id => { const st = j.items[id]; c[st ? POps.taskState(st.status) : 'run'] += 1; });
        return c;
    }
    function emit() { document.dispatchEvent(new CustomEvent('pops_jobs')); }
    async function poll() {
        timer = null;
        const now = Date.now();
        jobs = jobs.filter(j => !j.doneAt || now - j.doneAt < 15 * 60 * 1000);
        const live = jobs.filter(j => !j.doneAt);
        if (live.length && !document.hidden) {
            const ids = [...new Set(live.flatMap(j => j.ids))].slice(0, 5000);
            try {
                const r = await POps.post('/api/tasks/status', { ids });
                const map = {};
                (r.items || []).forEach(t => { map[t.id] = t; });
                live.forEach(j => {
                    j.ids.forEach(id => { if (map[id]) j.items[id] = { status: map[id].status, pc: map[id].target_pc, exit: map[id].exit_code }; else if (!j.items[id]) j.items[id] = { status: 'Cancelled', pc: '' }; });
                    const c = counts(j);
                    if (!c.run) {
                        j.doneAt = Date.now();
                        POps.toast(c.bad ? 'warning' : 'success', c.bad ? POps.t('{title}: {ok} başarılı, {bad} başarısız.', { title: j.title, ok: c.ok, bad: c.bad })
                            : POps.tn('{title}: {n} cihazda tamamlandı.', c.total, { title: j.title }));
                    }
                });
            } catch (e) { /* bir sonraki turda yeniden denenir */ }
        }
        save();
        emit();
        if (jobs.some(j => !j.doneAt)) timer = setTimeout(poll, document.hidden ? 15000 : 3000);
    }
    function kick() { if (timer) clearTimeout(timer); timer = setTimeout(poll, 1200); }
    document.addEventListener('visibilitychange', () => { if (!document.hidden && jobs.some(j => !j.doneAt)) kick(); });
    return {
        track(title, ids) {
            const clean = [...new Set((ids || []).map(Number).filter(n => n > 0))];
            if (!clean.length) return;
            jobs.unshift({ id: Date.now() + '-' + Math.random().toString(36).slice(2, 6), title: String(title || POps.t('İşlem')), ids: clean, items: {}, at: Date.now(), doneAt: null });
            jobs = jobs.slice(0, 20);
            save(); emit(); kick();
        },
        list() { return jobs.slice(); },
        counts,
        clearDone() { jobs = jobs.filter(j => !j.doneAt); save(); emit(); },
        start() { emit(); if (jobs.some(j => !j.doneAt)) kick(); }
    };
})();
