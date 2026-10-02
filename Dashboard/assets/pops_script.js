// =================================================================
// POps panel — ortak betik (bütün sayfalarda, <head> içinde yüklenir)
//   POps.api / get / post / del  : tek istek sarmalayıcı (JSON, 401 -> giriş, FastAPI 'detail' hatası)
//   POps.toast(tür, metin)       : sağ alttaki bildirim (success | error | warning | info)
//   POps.confirm / prompt / alert: tarayıcı penceresi yerine sayfa içi pencere (Promise döner)
//   POps.busy / act              : düğmeyi istek sürerken kilitler; hata/başarı bildirimini gösterir
//   openModal / closeModal       : sayfadaki .modal-overlay pencereleri (odak tuzağı, Esc)
//   POps.watchDevices            : cihaz listesini (state.devices) yalnızca isteyen sayfada yoklar
// Metinler her zaman textContent ile yazılır; sunucudan gelen değer HTML olarak yorumlanmaz.
// =================================================================

const API_HTTP = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '';

// Ortak durum (Laboratuvarlar, Dağıtım ve Terminal sayfaları cihaz listesini buradan okur)
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

// ============== İSTEK SARMALAYICI ==============
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
        if (typeof d === 'string' && d.trim()) return d.trim();
        if (Array.isArray(d) && d.length) {
            return d.map(x => {
                if (!x || typeof x !== 'object') return String(x);
                const loc = Array.isArray(x.loc) ? x.loc.filter(p => p !== 'body').join('.') : '';
                return (loc ? loc + ': ' : '') + (x.msg || '');
            }).join('; ');
        }
        if (d && typeof d === 'object' && typeof d.message === 'string') return d.message;
        if (typeof data.message === 'string' && data.message.trim()) return data.message.trim();
        if (typeof data.error === 'string' && data.error.trim()) return data.error.trim();
    } else if (typeof data === 'string') {
        const t = data.trim();
        if (t && t.length < 300 && !/<[a-z!]/i.test(t)) return t;
    }
    return STATUS_TEXT[status] || ('Sunucu hatası (HTTP ' + status + ').');
}

POps.errorMessage = function (err, fallback) {
    if (!err) return fallback || 'Beklenmeyen hata.';
    if (err instanceof ApiError) return err.message;
    return (err && err.message) || fallback || 'Beklenmeyen hata.';
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
        throw new ApiError('Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.', 0, null);
    }
    if (res.status === 401) {
        // Oturum bitti: giriş sayfasına; çağıranın devam etmesi beklenmez
        window.location.href = '/logout.php';
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
        throw new ApiError((typeof data.message === 'string' && data.message) || (typeof data.detail === 'string' && data.detail) || 'İşlem başarısız.', res.status, data);
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
function icon(name) {
    const i = document.createElement('i');
    i.className = 'fas ' + name;
    i.setAttribute('aria-hidden', 'true');
    return i;
}
POps.icon = icon;

// Hazır durum blokları (yalnızca sabit metin; değişken değer textContent ile eklenir)
POps.setLoading = function (el, text) {
    if (!el) return;
    el.replaceChildren(POps.el('div', { className: 'loading-state', role: 'status' }, [
        POps.el('span', { className: 'spinner', 'aria-hidden': 'true' }), document.createTextNode(text || 'Yükleniyor…')
    ]));
};
POps.setEmpty = function (el, opts) {
    if (!el) return;
    const o = opts || {};
    const box = POps.el('div', { className: 'empty-state' + (o.compact ? ' compact' : '') + (o.kind ? ' ' + o.kind : '') }, [
        icon(o.icon || 'fa-inbox'),
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
    POps.setEmpty(el, Object.assign({ icon: 'fa-triangle-exclamation', kind: 'error', title: 'Veriler alınamadı', text: POps.errorMessage(err) }, opts || {}));
};

// ============== BİLDİRİM (TOAST) ==============
const TOAST_TYPES = { success: 'fa-circle-check', error: 'fa-circle-xmark', warning: 'fa-triangle-exclamation', info: 'fa-circle-info' };
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
    const ic = icon(TOAST_TYPES[type]);
    ic.classList.add('toast-icon');
    const msg = document.createElement('div');
    msg.className = 'toast-msg';
    msg.textContent = String(message == null ? '' : message);
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'toast-close';
    close.setAttribute('aria-label', 'Kapat');
    close.appendChild(icon('fa-xmark'));
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
        ic.appendChild(icon(o.icon || (o.danger ? 'fa-triangle-exclamation' : kind === 'prompt' ? 'fa-pen' : kind === 'alert' ? 'fa-circle-info' : 'fa-circle-question')));
        const content = document.createElement('div');
        content.className = 'pops-dialog-content';
        const title = document.createElement('h2');
        title.className = 'pops-dialog-title';
        title.id = titleId;
        title.textContent = o.title || (kind === 'confirm' ? 'Emin misiniz?' : kind === 'prompt' ? 'Bilgi girin' : 'Bilgi');
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
            copy.append(icon('fa-copy'), document.createTextNode('Kopyala'));
            copy.addEventListener('click', async () => {
                try { await navigator.clipboard.writeText(String(code)); POps.toast('success', 'Kopyalandı.'); }
                catch (e) { POps.toast('warning', 'Kopyalanamadı; kodu seçip elle kopyalayın.'); }
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
            cancelBtn.textContent = o.cancelText || 'Vazgeç';
            footer.appendChild(cancelBtn);
        }
        const okBtn = document.createElement('button');
        okBtn.type = 'button';
        okBtn.className = 'btn' + (o.danger ? ' danger' : '');
        okBtn.textContent = o.confirmText || (kind === 'alert' ? 'Tamam' : kind === 'prompt' ? 'Kaydet' : 'Onayla');
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
            if (o.required !== false && !v) return showError(o.requiredText || 'Bu alan boş bırakılamaz.');
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
    if (!targets.length) return POps.toast('warning', 'Açık cihaz yok; komut gönderilmedi.');
    const verb = isShutdown ? 'kapatılacak' : 'yeniden başlatılacak';
    const dev = targetType === 'PC' ? (state.devices.find(d => d.hostname === targetName) || { hostname: targetName }) : null;
    const who = targetType === 'ALL' ? `Ağdaki açık ${targets.length} cihaz`
        : targetType === 'LAB' ? `${targetName} sınıfındaki açık ${targets.length} cihaz`
        : `${POps.deviceName(dev)} (${targetName})`;
    const ok = await POps.confirm({
        title: isShutdown ? 'Cihazlar kapatılsın mı?' : 'Cihazlar yeniden başlatılsın mı?',
        message: `${who} 5 saniye içinde ${verb}. Kaydedilmemiş işler kaybolabilir.`,
        confirmText: isShutdown ? 'Kapat' : 'Yeniden başlat',
        danger: true,
        icon: 'fa-power-off'
    });
    if (!ok) return;
    await POps.act(btn, () => POps.post('/api/deploy_orchestration', {
        target_mode: 'PC', targets, taskSequence: [{ name: isShutdown ? 'Güç: kapat' : 'Güç: yeniden başlat', type: 'CMD', command: cmd }]
    }), { success: (r) => `Komut kuyruğa eklendi (${(r && r.created) || targets.length} cihaz).` });
};

// wakeUpCommand('ALL'|'LAB'|'PC', ad, düğme)
window.wakeUpCommand = async function (targetType, targetName, btn) {
    if (targetType === 'ALL') {
        const ok = await POps.confirm({ title: 'Bütün ağ uyandırılsın mı?', message: 'MAC adresi bilinen bütün kapalı cihazlara uyandırma (WOL) sinyali gönderilecek.', confirmText: 'Uyandır', icon: 'fa-bolt' });
        if (!ok) return;
        await POps.act(btn, () => POps.post('/api/wake_all'), { success: (r) => `${(r && r.woken_pcs) || 0} cihaza uyandırma sinyali gönderildi.` });
    } else if (targetType === 'LAB') {
        await POps.act(btn, () => POps.post('/api/wake_lab/' + encodeURIComponent(targetName)), { success: (r) => `${targetName}: ${(r && r.woken_pcs) || 0} cihaza uyandırma sinyali gönderildi.` });
    } else if (targetType === 'PC') {
        await POps.act(btn, () => POps.post('/api/wake_pc/' + encodeURIComponent(targetName)), { success: 'Uyandırma sinyali gönderildi.' });
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
    box.innerHTML = '<i class="fas fa-shield-halved"></i><span>Hesabınızda iki adımlı doğrulama (2FA) kapalı. Önerilir: '
        + '<a href="settings.php#twofaCard">Ayarlar → İki Adımlı Doğrulama (2FA)</a> kartından açabilirsiniz.</span>'
        + '<button type="button" class="twofa-nudge-close" title="7 gün gösterme" aria-label="Kapat"><i class="fas fa-xmark"></i></button>';
    box.querySelector('button').addEventListener('click', () => {
        try { localStorage.setItem(TWOFA_NUDGE_KEY, String(Date.now() + 7 * 24 * 60 * 60 * 1000)); } catch (e) { /* özel pencere */ }
        box.remove();
    });
    header.insertAdjacentElement('afterend', box);
}
