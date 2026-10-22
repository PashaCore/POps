// =================================================================
// Kayıtlar sayfasının betiği (logger.php). Ortak betiklerden (POps.*, POps.dev, POps.logs, state) sonra defer
// ile yüklenir. Sayfanın PHP'den aldığı değer yok; rol header.php'nin window.USER_ROLE'ünden okunur.
// Adresten gelen süzgeçler: tab=hw, f=warn|bad|sec, pc, kind, from, to (YYYY-AA-GG), risk=high, q.
// En üstteki saf yardımcılar DOM'a dokunmaz; tests/unit-js/pages.test.mjs sınar (bkz. docs/dashboard.md).
// =================================================================
(function () {
    // ---------------------------------------------------------------- saf yardımcılar
    // Yerel gün anahtarı: 2026-10-05 (kayıtların e.day alanıyla aynı biçim)
    function dayKey(t) { return t.getFullYear() + '-' + String(t.getMonth() + 1).padStart(2, '0') + '-' + String(t.getDate()).padStart(2, '0'); }
    // CSV hücresi: =, +, -, @ (ya da sekme/satır başı) ile başlarsa önüne ' (tablo programında formül olarak
    // çalışmasın); ; " ya da satır sonu varsa tırnak içinde, tırnaklar ikilenir
    function csvCell(v) { let s = String(v == null ? '' : v); if (/^[=+\-@\t\r]/.test(s)) s = "'" + s; return /[";\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; }
    // Sayfa düğmeleri: ilk, son, bulunulan sayfanın iki yanı; baştaki ve sondaki üç sayfada ilk/son dört sayfa
    function pageNumbers(page, pages) {
        const want = new Set([1, pages, page - 1, page, page + 1].filter(n => n >= 1 && n <= pages));
        if (page <= 3) [2, 3, 4].forEach(n => n <= pages && want.add(n));
        if (page >= pages - 2) [pages - 3, pages - 2, pages - 1].forEach(n => n >= 1 && want.add(n));
        return [...want].sort((a, b) => a - b);
    }
    // Node testleri dosyayı bir module nesnesiyle yükler: yalnızca yardımcılar verilir, sayfa kurulmaz
    if (typeof module === 'object' && module && module.exports) { module.exports = { dayKey, csvCell, pageNumbers }; return; }

    const dev = POps.dev;
    const CAN_ADMIN = dev.canAdmin;
    const IS_SUPER = window.USER_ROLE === 'superadmin';
    const $ = (id) => document.getElementById(id);
    const params = new URLSearchParams(location.search);
    const LIMITS = [1000, 5000, 20000];
    const SIZES = [25, 50, 75, 100];
    const savedSize = (() => { try { return Number(localStorage.getItem('pops_log_size')) || 0; } catch (e) { return 0; } })();
    const isDay = (v) => /^\d{4}-\d{2}-\d{2}$/.test(v || '');

    // Olay sözlüğü ortak dosyada (assets/pops_devices.js, POps.logs): Kontrol merkezi de aynı dili kullanır
    const { SEV_WORD, KIND_LABEL, CAT_LABEL, RISK_LABEL, commandOf, isoOf, cleanMsg, cap1, sourceOf, norm } = POps.logs;

    const ui = {
        f: ['warn', 'bad', 'sec'].includes(params.get('f')) ? params.get('f') : 'all',
        kind: KIND_LABEL[params.get('kind')] ? params.get('kind') : '',
        q: (params.get('q') || '').trim().toLocaleLowerCase('tr'),
        pc: params.get('pc') || '',
        from: isDay(params.get('from')) ? params.get('from') : '',
        to: isDay(params.get('to')) ? params.get('to') : '',
        risk: params.get('risk') === 'high' ? 'high' : '',
        lab: '', who: '', page: 1, size: SIZES.includes(savedSize) ? savedSize : 25,
        limitIx: 0, open: new Set(), rows: [], byId: new Map(), loaded: false, sig: '',
        inv: null, invAt: 0, hq: '', hlab: '', hf: 'all', hsig: ''
    };
    const list = $('lgList');
    if (params.get('q')) $('lgSearch').value = params.get('q');
    $('lgFrom').value = ui.from;
    $('lgTo').value = ui.to;

    const pcName = (pc) => pc ? dev.name(pc) : '';
    const pcHref = (pc) => 'pc?id=' + encodeURIComponent(pc);
    const labOf = (pc) => { const d = dev.find(pc); return d && d.lab && d.lab !== dev.UNASSIGNED ? d.lab : ''; };
    const highRisk = (e) => ['high', 'critical'].includes(String(e.r.risk_level || '').toLowerCase());

    // ---------------------------------------------------------------- süzgeç
    function hay(e) {
        return [e.title, cleanMsg(e.r.message), e.who, e.r.actor_id, pcName(e.pc), e.pc, labOf(e.pc), e.r.reason, e.m.domain, e.r.event_type, SEV_WORD[e.sev]]
            .join(' ').toLocaleLowerCase('tr');
    }
    function pass(e, skip) {
        if (skip !== 'f' && ui.f === 'warn' && e.sev === 'info') return false;
        if (skip !== 'f' && ui.f === 'bad' && e.sev !== 'bad') return false;
        if (skip !== 'f' && ui.f === 'sec' && !e.security) return false;
        if (ui.risk && !highRisk(e)) return false;
        if (ui.kind && e.kind !== ui.kind) return false;
        if (ui.pc && e.pc !== ui.pc) return false;
        if (ui.lab && (ui.lab === dev.UNASSIGNED ? !!labOf(e.pc) : labOf(e.pc) !== ui.lab)) return false;
        if (ui.who && e.who !== ui.who) return false;
        if (ui.from && e.day < ui.from) return false;
        if (ui.to && e.day > ui.to) return false;
        if (ui.q && !hay(e).includes(ui.q)) return false;
        return true;
    }

    // ---------------------------------------------------------------- görünüm
    function dayLabel(day) {
        const d = POps.toDate(day + 'T12:00:00');
        if (!d) return day;
        const now = new Date();
        const y = new Date(now); y.setDate(now.getDate() - 1);
        if (d.toDateString() === now.toDateString()) return POps.t('Bugün');
        if (d.toDateString() === y.toDateString()) return POps.t('Dün');
        const o = { day: 'numeric', month: 'long', weekday: 'long' };
        if (d.getFullYear() !== now.getFullYear()) o.year = 'numeric';
        return d.toLocaleDateString(POps.locale, o);
    }
    // Yasaklı sitenin kategorisi: ek alandan ya da ajanın iletisinden ("KURAL İHLALİ: alan (kategori) kişi")
    const siteCat = (e) => e.m.violation_category || (cleanMsg(e.r.message).match(/\(([^)]{1,60})\)/) || [])[1] || '';
    // Uyarının nedeni: gerekçe; yoksa ajanın iletisi (başlığı tekrar etmiyorsa). Yasaklı sitede kategori meta satırında.
    function whyOf(e) {
        if (e.why) return e.why;
        const msg = cleanMsg(e.r.message);
        if (!msg || e.key === 'dns_block') return '';
        const low = msg.toLocaleLowerCase('tr'), title = String(e.title).toLocaleLowerCase('tr');
        if (low === title) return '';
        const rest = low.startsWith(title) ? msg.slice(title.length).replace(/^[\s:·,-]+/, '') : msg;
        return rest ? cap1(rest.slice(0, 300)) : '';
    }
    function factsHtml(e) {
        const r = e.r, m = e.m;
        const lab = labOf(e.pc);
        const facts = [
            [POps.t('Bilgisayar'), e.pc ? pcName(e.pc) + (pcName(e.pc) !== e.pc ? ' (' + e.pc + ')' : '') : ''],
            [POps.t('Sınıf'), lab], [POps.t('Kim'), e.who + (r.actor_id && r.actor_id !== e.who && r.actor_id !== 'Agent' && r.actor_id !== e.pc ? ' (' + r.actor_id + ')' : '')], [POps.t('Kaynak'), sourceOf(r)], ['IP', m.ip || m.client_ip || ''],
            [POps.t('Gerekçe'), r.reason || ''], [POps.t('Alan adı'), m.domain || ''], [POps.t('Site kategorisi'), m.violation_category || ''],
            [POps.t('Komut'), e.key === 'execute_queue' ? commandOf(r, m) : ''], [POps.t('Mesaj'), cleanMsg(r.message)],
            [POps.t('Olay türü'), [r.event_type, r.action].filter(x => x && x !== 'unknown').join(' · ')],
            [POps.t('Kategori'), CAT_LABEL[String(r.category || '').toLowerCase()] || r.category || r.log_type || ''],
            [POps.t('Risk düzeyi'), RISK_LABEL[String(r.risk_level || '').toLowerCase()] || r.risk_level || ''],
            [POps.t('Zaman'), POps.fullTime(e.at)], [POps.t('Kayıt no'), '#' + e.id]
        ];
        // Bilinmeyen ek alanlar (zincir özeti dahil) olduğu gibi, düz metin (ad da veri: çevrilmez)
        const known = ['ip', 'client_ip', 'domain', 'violation_category', 'raw_command', 'created_by'];
        Object.keys(m).filter(k => !known.includes(k)).slice(0, 12).forEach(k => {
            const v = m[k];
            facts.push([k, v != null && typeof v === 'object' ? JSON.stringify(v) : String(v == null ? '' : v)]);
        });
        const rowsHtml = facts.filter(f => f[1]).map(f => `<span>${escapeHtml(f[0])}</span><span>${escapeHtml(String(f[1]).slice(0, 600))}</span>`).join('');
        const linksHtml = e.pc ? `<div class="lg-links">${ui.pc !== e.pc ? `<button type="button" data-act="pc">${POps.tHtml('Yalnızca bu bilgisayarın kayıtları')}</button>` : ''}<a href="${pcHref(e.pc)}">${POps.tHtml('Bilgisayarı aç')}</a></div>` : '';
        return `<div class="facts">${rowsHtml}${linksHtml}</div>`;
    }
    function rowHtml(e) {
        const open = ui.open.has(e.id);
        const cls = e.sev === 'info' ? '' : e.sev;
        const lab = labOf(e.pc);
        const whereHtml = e.pc ? `<a href="${pcHref(e.pc)}">${escapeHtml(pcName(e.pc))}</a>` + (lab ? ' · ' + escapeHtml(lab) : '') + ' · ' : '';
        const metaHtml = whereHtml + escapeHtml(e.who) + ' · ' + POps.timeHtml(e.at);
        const why = e.sev !== 'info' ? whyOf(e) : '';
        const whyHtml = why ? `<div class="why${e.sev === 'warn' ? ' warn' : ''}">${escapeHtml(why)}</div>` : '';
        const cat = e.key === 'dns_block' ? siteCat(e) : '';
        const noteHtml = (e.why && e.sev === 'info' ? ' · ' + escapeHtml(e.why.toLocaleLowerCase(POps.locale)) : '')
            + (cat ? ' · ' + POps.tHtml('Site kategorisi: {category}', { category: cat }) : '');
        return `<div class="act clickable" data-id="${escapeHtml(e.id)}" role="button" tabindex="0" aria-expanded="${open ? 'true' : 'false'}">
            <div class="res ${escapeHtml(cls)}">${POps.iconHtml(e.icon)}</div>
            <div class="lg-body" style="min-width:0"><div class="what">${escapeHtml(e.title)}</div><div class="meta">${metaHtml}${noteHtml}</div>${whyHtml}</div>
            <div class="side"><span class="word ${escapeHtml(cls)}">${escapeHtml(SEV_WORD[e.sev])}</span></div>${open ? factsHtml(e) : ''}
        </div>`;
    }

    function renderCounts() {
        const base = ui.rows.filter(e => pass(e, 'f'));
        const n = { all: base.length, warn: base.filter(e => e.sev !== 'info').length, bad: base.filter(e => e.sev === 'bad').length, sec: base.filter(e => e.security).length };
        $('lgFilter').querySelectorAll('button[data-f]').forEach(b => {
            const on = b.dataset.f === ui.f;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
            b.querySelector('.count').textContent = ui.loaded ? String(n[b.dataset.f]) : '';
        });
        const sel = $('lgKind');
        const byKind = {};
        ui.rows.forEach(e => { byKind[e.kind] = (byKind[e.kind] || 0) + 1; });
        const sig = JSON.stringify(byKind) + ui.kind;
        if (sel.dataset.sig !== sig) {
            sel.dataset.sig = sig;
            sel.replaceChildren(POps.el('option', { value: '', text: POps.t('Bütün türler') }));
            Object.keys(KIND_LABEL).forEach(k => {
                if (!byKind[k] && ui.kind !== k) return;
                sel.append(POps.el('option', { value: k, text: `${KIND_LABEL[k]} (${byKind[k] || 0})` }));
            });
            sel.value = ui.kind;
        }
        const labSel = $('lgLab');
        const labs = dev.labs();
        const labSig = labs.join('|') + '|' + ui.lab;
        if (labSel.dataset.sig !== labSig) {
            labSel.dataset.sig = labSig;
            labSel.replaceChildren(POps.el('option', { value: '', text: POps.t('Bütün sınıflar') }), ...labs.map(l => POps.el('option', { value: l, text: l })), POps.el('option', { value: dev.UNASSIGNED, text: POps.t('Atanmamış') }));
            labSel.value = ui.lab;
        }
        const whoSel = $('lgWho');
        const whos = {};
        ui.rows.forEach(e => { whos[e.who] = (whos[e.who] || 0) + 1; });
        const whoSig = JSON.stringify(whos) + ui.who;
        if (whoSel.dataset.sig !== whoSig) {
            whoSel.dataset.sig = whoSig;
            whoSel.replaceChildren(POps.el('option', { value: '', text: POps.t('Herkes') }), ...Object.keys(whos).sort((a, b) => a.localeCompare(b, 'tr')).map(w => POps.el('option', { value: w, text: `${w} (${whos[w]})` })));
            whoSel.value = ui.who;
        }
    }
    // Başlığın altındaki tek satır: bugünkü olaylar (tıklayınca bugünün süzgeci). Sayılar süzgeç düğmelerinde.
    function renderSummary() {
        const box = $('lgSummary');
        if (!ui.loaded) { box.replaceChildren(); return; }
        const today = dayKey(new Date());
        if ((ui.to && ui.to < today) || (ui.from && ui.from > today)) { box.replaceChildren(); return; }   // aralık bugünü kapsamıyor
        const n = ui.rows.filter(e => e.day === today).length;
        const on = ui.from === today && ui.to === today;
        // Bugünün süzgeci açıksa ikinci tıklama kaldırır
        const a = POps.el('a', { href: '#', className: 'sum', 'data-day': on && tabs.current() === 'log' ? '' : today });
        a.append(...POps.tNodes('Bugün {n} olay', { n }, { n: POps.el('b', { text: n.toLocaleString(POps.locale) }) }));
        box.replaceChildren(a);
    }
    function renderNote() {
        const note = $('lgNote');
        note.replaceChildren();
        if (!ui.loaded) return;
        const ranged = !!(ui.from || ui.to || ui.pc);
        const limit = ranged ? LIMITS[LIMITS.length - 1] : LIMITS[ui.limitIx];
        const full = ui.rows.length >= limit;
        // n çoğul biçimini seçer, {count} biçimlenmiş sayıdır (1.000 / 1,000)
        const cnt = (n) => ({ n, count: n.toLocaleString(POps.locale) });
        note.append(POps.el('span', { text: ranged
            ? (full ? POps.t('Seçilen bilgisayar ya da tarih aralığındaki son {count} kayıt sunucudan alındı.', cnt(limit)) : POps.t('Seçilen bilgisayar ya da tarih aralığındaki bütün kayıtlar sunucudan alındı.'))
            : full ? POps.t('Son {count} kayıt yüklendi; tarih aralığı seçince o aralığın tamamı sunucudan alınır.', cnt(limit)) : POps.t('Sunucudaki {count} kaydın hepsi yüklendi.', cnt(ui.rows.length)) }));
        if (!ranged && full && ui.limitIx < LIMITS.length - 1) {
            const b = POps.el('button', { type: 'button', text: POps.t('Daha eski kayıtları yükle') });
            b.addEventListener('click', () => { ui.limitIx += 1; ui.sig = ''; POps.busy(b, loadLogs).catch(e => POps.toast('error', POps.errorMessage(e))); });
            note.append(b);
        }
    }
    const fmtDay = (d) => { const x = POps.toDate(d + 'T12:00:00'); return x ? x.toLocaleDateString(POps.locale, { day: 'numeric', month: 'short' }) : d; };
    function activeFilters() {
        const out = [];
        if (ui.pc) out.push(['pc', POps.t('Bilgisayar'), pcName(ui.pc)]);
        if (ui.risk) out.push(['risk', POps.t('Risk düzeyi'), POps.t('yüksek ve kritik')]);
        if (ui.kind) out.push(['kind', POps.t('Tür'), KIND_LABEL[ui.kind] || ui.kind]);
        if (ui.lab) out.push(['lab', POps.t('Sınıf'), ui.lab === dev.UNASSIGNED ? POps.t('Atanmamış') : ui.lab]);
        if (ui.who) out.push(['who', POps.t('Kişi'), ui.who]);
        if (ui.from || ui.to) out.push(['date', POps.t('Tarih'), (ui.from ? fmtDay(ui.from) : '…') + ' – ' + (ui.to ? fmtDay(ui.to) : POps.t('bugün'))]);
        return out;
    }
    function renderPcChip() {
        const f = activeFilters();
        const box = $('lgChips');
        box.replaceChildren(...f.map(([key, label, value]) => {
            const x = POps.el('button', { type: 'button', 'aria-label': POps.t('{label} süzgecini kaldır', { label }), 'data-clear': key });
            x.append(POps.iconEl('x', 'sm'));
            return POps.el('span', { className: 'chip' }, [document.createTextNode(label + ': '), POps.el('b', { text: value }), x]);
        }), ...(f.length > 1 ? [POps.el('button', { type: 'button', className: 'clear', 'data-clear': 'all', text: POps.t('Hepsini temizle') })] : []));
        const n = f.filter(x => x[0] !== 'pc' && x[0] !== 'risk').length;
        $('lgFCount').hidden = !n;
        $('lgFCount').textContent = String(n);
    }
    // Adres süzgeçleri izler: sayfa yenilenince ya da bağlantı paylaşılınca aynı liste açılır
    function syncUrl() {
        const u = new URL(location.href);
        const set = (k, v) => { if (v) u.searchParams.set(k, v); else u.searchParams.delete(k); };
        set('f', ui.f === 'all' ? '' : ui.f); set('pc', ui.pc); set('kind', ui.kind); set('from', ui.from); set('to', ui.to); set('risk', ui.risk);
        u.searchParams.delete('q');
        const next = u.pathname + u.search + u.hash;
        if (next !== location.pathname + location.search + location.hash) history.replaceState(null, '', next);
    }
    function render() {
        renderSummary();
        if (tabs.current() !== 'log') return;
        renderCounts();
        renderPcChip();
        renderNote();
        syncUrl();
        if (!ui.loaded) return;
        const rows = ui.rows.filter(e => pass(e));
        if (!rows.length) {
            $('lgPager').hidden = true;
            const filtered = ui.rows.length > 0;
            POps.setEmpty(list, filtered
                ? { icon: 'filter', title: POps.t('Süzgece uyan kayıt yok'), text: POps.t('Arama, tarih ya da süzgeçleri değiştirin.') }
                : { icon: 'list', title: POps.t('Henüz kayıt yok'), text: POps.t('Bilgisayarlar olay bildirdikçe burada görünür.') });
            return;
        }
        const pages = Math.max(1, Math.ceil(rows.length / ui.size));
        if (ui.page > pages) ui.page = pages;
        const startIx = (ui.page - 1) * ui.size;
        const shown = rows.slice(startIx, startIx + ui.size);
        renderPager(rows.length, startIx, shown.length, pages);
        let lastDay = null;
        let bodyHtml = '';
        shown.forEach(e => {
            if (e.day !== lastDay) { lastDay = e.day; bodyHtml += `<div class="lg-day">${escapeHtml(dayLabel(e.day))}</div>`; }
            bodyHtml += rowHtml(e);
        });
        const focusId = document.activeElement && list.contains(document.activeElement) ? (document.activeElement.closest('.act[data-id]') || {}).dataset : null;
        list.innerHTML = bodyHtml;
        if (focusId && focusId.id) { const el = list.querySelector(`.act[data-id="${CSS.escape(focusId.id)}"]`); if (el) el.focus({ preventScroll: true }); }
    }

    // Sayfa düğmeleri: ilk, son, bulunulan sayfanın iki yanı; aralar "…"
    function renderPager(total, startIx, count, pages) {
        $('lgPager').hidden = false;
        const fmt = (n) => n.toLocaleString(POps.locale);
        $('lgRange').textContent = POps.tn('{from}–{to} / {total} kayıt', total, { from: fmt(startIx + 1), to: fmt(startIx + count), total: fmt(total) });
        $('lgSize').value = String(ui.size);
        const nums = pageNumbers(ui.page, pages);
        const box = $('lgPages');
        const btn = (label, page, opts) => {
            const b = POps.el('button', { type: 'button', 'data-page': String(page), 'aria-label': (opts && opts.aria) || POps.t('Sayfa {n}', { n: page }) });
            if (opts && opts.icon) b.append(POps.iconEl(opts.icon, 'sm')); else b.textContent = label;
            if (opts && opts.on) { b.className = 'on'; b.setAttribute('aria-current', 'page'); }
            if (opts && opts.disabled) b.disabled = true;
            return b;
        };
        const items = [btn('', ui.page - 1, { icon: 'left', aria: POps.t('Önceki sayfa'), disabled: ui.page <= 1 })];
        nums.forEach((n, i) => {
            if (i && n - nums[i - 1] > 1) items.push(POps.el('span', { className: 'gap', text: '…' }));
            items.push(btn(String(n), n, { on: n === ui.page }));
        });
        items.push(btn('', ui.page + 1, { icon: 'right', aria: POps.t('Sonraki sayfa'), disabled: ui.page >= pages }));
        box.replaceChildren(...items);
    }
    function goPage(n) {
        ui.page = n;
        render();
        const top = $('lgList').getBoundingClientRect().top + window.scrollY - 90;
        if (window.scrollY > top) window.scrollTo({ top, behavior: 'smooth' });
    }

    // ---------------------------------------------------------------- veri
    async function loadLogs() {
        let rows;
        const ranged = !!(ui.from || ui.to || ui.pc);
        const q = new URLSearchParams({ limit: String(ranged ? LIMITS[LIMITS.length - 1] : LIMITS[ui.limitIx]) });
        if (ui.pc) q.set('pc', ui.pc);
        if (ui.from) q.set('since', ui.from);
        if (ui.to) q.set('until', ui.to);
        const reqKey = q.toString();
        ui.reqKey = reqKey;
        try { rows = await POps.get('/api/logs?' + reqKey); }
        catch (e) { if (!ui.loaded) POps.setError(list, e); throw e; }
        if (ui.reqKey !== reqKey) return;   // bu sırada süzgeç değişti; yeni istek sonucu yazar
        rows = Array.isArray(rows) ? rows : [];
        const sig = reqKey + ':' + rows.length + ':' + (rows[0] && rows[0].id) + ':' + (rows[rows.length - 1] && rows[rows.length - 1].id);
        if (sig === ui.sig && ui.loaded) return;
        ui.sig = sig;
        const byId = new Map();
        ui.rows = rows.map(r => { const k = String(r.id); const e = ui.byId.get(k) || norm(r); byId.set(k, e); return e; })
            .sort((a, b) => String(b.r.timestamp).localeCompare(String(a.r.timestamp)) || Number(b.id) - Number(a.id));
        ui.byId = byId;
        ui.loaded = true;
        render();
    }

    // ---------------------------------------------------------------- etkileşim (olaylar)
    function toggleRow(row) {
        const e = ui.byId.get(row.dataset.id);
        if (!e) return;
        if (ui.open.has(e.id)) {
            ui.open.delete(e.id);
            const f = row.querySelector(':scope > .facts');
            if (f) f.remove();
            row.setAttribute('aria-expanded', 'false');
        } else {
            ui.open.add(e.id);
            row.insertAdjacentHTML('beforeend', factsHtml(e));
            row.setAttribute('aria-expanded', 'true');
        }
    }
    function setPc(pc) {
        ui.pc = pc || '';
        ui.page = 1;
        ui.sig = '';
        loadLogs().catch(() => {});
        render();
    }
    list.addEventListener('click', (ev) => {
        if (ev.target.closest('a')) return;   // bilgisayar bağlantısı: sayfa değişir
        const b = ev.target.closest('[data-act]');
        const row = ev.target.closest('.act[data-id]');
        if (b) {
            const e = row ? ui.byId.get(row.dataset.id) : null;
            if (b.dataset.act === 'pc' && e) { setPc(e.pc); window.scrollTo({ top: 0, behavior: 'smooth' }); }
            return;
        }
        if (!row || ev.target.closest('.facts') || String(window.getSelection() || '')) return;
        toggleRow(row);
    });
    list.addEventListener('keydown', (ev) => {
        const row = ev.target.closest('.act[data-id]');
        if (!row || ev.target !== row) return;
        if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); toggleRow(row); }
    });
    $('lgFilter').addEventListener('click', (ev) => { const b = ev.target.closest('button[data-f]'); if (b) { ui.f = b.dataset.f; ui.page = 1; render(); } });
    function setDates(from, to) {
        ui.from = from; ui.to = to;
        $('lgFrom').value = from; $('lgTo').value = to;
        dateChanged();
    }
    $('lgSummary').addEventListener('click', (ev) => {
        const a = ev.target.closest('[data-day]');
        if (!a) return;
        ev.preventDefault();
        if (tabs.current() !== 'log') tabs.set('log');
        setDates(a.dataset.day, a.dataset.day);
    });
    // Süzgeç paneli
    const fpanel = $('lgFPanel'), fbtn = $('lgFiltersBtn');
    function openPanel(open) {
        fpanel.hidden = !open;
        fbtn.setAttribute('aria-expanded', open ? 'true' : 'false');
        if (!open) return;
        const r = fbtn.getBoundingClientRect();
        fpanel.style.top = Math.round(r.bottom + 6) + 'px';
        fpanel.style.left = Math.round(Math.max(12, Math.min(r.left, window.innerWidth - fpanel.offsetWidth - 12))) + 'px';
        $('lgKind').focus();
    }
    fbtn.addEventListener('click', (ev) => { ev.stopPropagation(); openPanel(fpanel.hidden); });
    $('lgFDone').addEventListener('click', () => { openPanel(false); fbtn.focus(); });
    document.addEventListener('mousedown', (ev) => { if (!fpanel.hidden && !fpanel.contains(ev.target) && !fbtn.contains(ev.target)) openPanel(false); });
    document.addEventListener('keydown', (ev) => { if (ev.key === 'Escape' && !fpanel.hidden) { ev.preventDefault(); openPanel(false); fbtn.focus(); } }, true);
    window.addEventListener('resize', () => openPanel(false));
    function dateChanged() { ui.page = 1; ui.sig = ''; ui.limitIx = 0; render(); loadLogs().catch(() => {}); }
    $('lgKind').addEventListener('change', (ev) => { ui.kind = ev.target.value; ui.page = 1; render(); });
    $('lgLab').addEventListener('change', (ev) => { ui.lab = ev.target.value; ui.page = 1; render(); });
    $('lgWho').addEventListener('change', (ev) => { ui.who = ev.target.value; ui.page = 1; render(); });
    $('lgFrom').addEventListener('change', (ev) => { ui.from = ev.target.value; dateChanged(); });
    $('lgTo').addEventListener('change', (ev) => { ui.to = ev.target.value; dateChanged(); });
    function clearFilter(key) {
        if (key === 'pc' || key === 'all') { if (ui.pc) { setPc(''); } }
        if (key === 'risk' || key === 'all') ui.risk = '';
        if (key === 'kind' || key === 'all') { ui.kind = ''; $('lgKind').value = ''; }
        if (key === 'lab' || key === 'all') { ui.lab = ''; $('lgLab').value = ''; }
        if (key === 'who' || key === 'all') { ui.who = ''; $('lgWho').value = ''; }
        if ((key === 'date' || key === 'all') && (ui.from || ui.to)) { setDates('', ''); return; }
        ui.page = 1;
        render();
    }
    $('lgFClear').addEventListener('click', () => clearFilter('all'));
    $('lgChips').addEventListener('click', (ev) => { const b = ev.target.closest('[data-clear]'); if (b) clearFilter(b.dataset.clear); });
    // Sayfalama
    $('lgPages').addEventListener('click', (ev) => { const b = ev.target.closest('button[data-page]'); if (b && !b.disabled) goPage(Number(b.dataset.page)); });
    $('lgSize').addEventListener('change', (ev) => {
        const first = (ui.page - 1) * ui.size;
        ui.size = Number(ev.target.value) || 25;
        try { localStorage.setItem('pops_log_size', String(ui.size)); } catch (e) { /* özel pencere */ }
        ui.page = Math.floor(first / ui.size) + 1;
        render();
    });
    let qt = null;
    $('lgSearch').addEventListener('input', (ev) => { clearTimeout(qt); qt = setTimeout(() => { ui.q = ev.target.value.trim().toLocaleLowerCase('tr'); ui.page = 1; render(); }, 150); });

    // ---------------------------------------------------------------- donanım
    const HW_FIELDS = [['cpu', POps.t('İşlemci')], ['ram', POps.t('Bellek')], ['motherboard', POps.t('Anakart')], ['gpu', POps.t('Ekran kartı')], ['disk_info', 'Disk'], ['os_version', POps.t('İşletim sistemi')], ['ip_address', 'IP'], ['mac_address', 'MAC']];
    const hwOf = (h) => { const x = ui.inv && ui.inv[h]; return x && x.cpu && x.cpu !== '-' ? x : null; };
    const val = (v) => v && v !== '-' ? String(v) : '';
    async function loadInventory(force) {
        if (!force && ui.inv && Date.now() - ui.invAt < 60000) return;
        try {
            const rows = await POps.get('/api/inventory');
            const map = {};
            (Array.isArray(rows) ? rows : []).forEach(r => { map[r.pc_name] = r; });
            ui.inv = map;
            ui.invAt = Date.now();
            ui.hsig = '';
            renderHw();
        } catch (e) {
            if (!ui.inv) POps.setError($('hwBody'), e, { tag: 'tr', colspan: 7 });
        }
    }
    // Atanmamışlar en sonda
    const labKey = (d) => d.lab && d.lab !== dev.UNASSIGNED ? '0' + d.lab : '1';
    const labText = (d) => d.lab && d.lab !== dev.UNASSIGNED ? d.lab : POps.t('Atanmamış');
    function hwRows(skipF) {
        const q = ui.hq;
        return (state.devices || []).filter(d => {
            if (!skipF && ui.hf === 'wait' && hwOf(d.hostname)) return false;
            if (ui.hlab && (ui.hlab === dev.UNASSIGNED ? (d.lab && d.lab !== dev.UNASSIGNED) : d.lab !== ui.hlab)) return false;
            if (!q) return true;
            const hw = (ui.inv && ui.inv[d.hostname]) || {};
            return [POps.deviceName(d), d.hostname, d.lab, d.ip, hw.cpu, hw.ram, hw.gpu, hw.os_version, hw.ip_address, hw.mac_address, hw.motherboard]
                .some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
        }).sort((a, b) => labKey(a).localeCompare(labKey(b), 'tr') || POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
    }
    function renderHwBar() {
        const sel = $('hwLab');
        const labs = dev.labs();
        if (sel.dataset.sig !== labs.join('|')) {
            sel.dataset.sig = labs.join('|');
            sel.replaceChildren(POps.el('option', { value: '', text: POps.t('Bütün sınıflar') }));
            labs.forEach(l => sel.append(POps.el('option', { value: l, text: l })));
            sel.append(POps.el('option', { value: dev.UNASSIGNED, text: POps.t('Atanmamış') }));
            sel.value = ui.hlab;
        }
        const base = hwRows(true);
        const n = { all: base.length, wait: base.filter(d => !hwOf(d.hostname)).length };
        $('hwFilter').querySelectorAll('button[data-hf]').forEach(b => {
            const on = b.dataset.hf === ui.hf;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
            b.querySelector('.count').textContent = ui.inv ? String(n[b.dataset.hf]) : '';
        });
    }
    const hwHref = (h) => pcHref(h) + '&tab=hardware';
    function hwRowHtml(d) {
        const hw = hwOf(d.hostname);
        const lab = labText(d);
        const cellHtml = (v) => val(v) ? escapeHtml(val(v)) : '—';
        const specsHtml = hw
            ? `<td>${cellHtml(hw.cpu)}</td><td>${cellHtml(hw.ram)}</td><td>${cellHtml(hw.disk_info)}</td><td>${cellHtml(hw.os_version)}</td>`
            : `<td colspan="4" class="wait" data-tip="${escapeHtml(POps.t('Donanım bilgisini ajan açılışta ve günde bir kez gönderir.'))}">${POps.tHtml('Donanım bilgisi bekleniyor')}</td>`;
        return `<tr data-host="${escapeHtml(d.hostname)}">
            <td><div class="nm"><a href="${hwHref(d.hostname)}">${escapeHtml(POps.deviceName(d))}</a></div><div class="sub">${escapeHtml(lab)}</div></td>
            ${specsHtml}
            <td class="mono">${escapeHtml(val(hw && hw.ip_address) || d.ip || '—')}</td>
            <td>${hw ? POps.timeHtml(isoOf(hw.last_updated)) : '<span class="wait">—</span>'}</td>
        </tr>`;
    }
    function renderHw() {
        if (tabs.current() !== 'hw') return;
        renderSummary();
        if (!state.devicesLoaded || !ui.inv) return;
        renderHwBar();
        const rows = hwRows();
        const sig = JSON.stringify([ui.hq, ui.hlab, ui.hf, ui.invAt, rows.map(d => [d.hostname, d.display_name, d.lab, d.ip])]);
        if (sig === ui.hsig) return;
        ui.hsig = sig;
        const body = $('hwBody');
        if (!rows.length) {
            POps.setEmpty(body, (state.devices || []).length
                ? { tag: 'tr', colspan: 7, icon: 'filter', title: POps.t('Süzgece uyan bilgisayar yok'), text: POps.t('Arama ya da sınıf süzgecini değiştirin.') }
                : { tag: 'tr', colspan: 7, icon: 'devices', title: POps.t('Henüz bilgisayar yok'), text: POps.t('Ajan kurulan bilgisayarlar bağlandıkça burada görünür.') });
            return;
        }
        body.innerHTML = rows.map(hwRowHtml).join('');
    }
    // Satır bilgisayar sayfasını (Donanım bölümü) açar
    $('hwBody').addEventListener('click', (ev) => {
        const tr = ev.target.closest('tr[data-host]');
        if (!tr || ev.target.closest('a')) return;
        location.href = hwHref(tr.dataset.host);
    });
    $('hwFilter').addEventListener('click', (ev) => { const b = ev.target.closest('button[data-hf]'); if (b) { ui.hf = b.dataset.hf; renderHw(); } });
    $('hwLab').addEventListener('change', (ev) => { ui.hlab = ev.target.value; renderHw(); });
    let hqt = null;
    $('hwSearch').addEventListener('input', (ev) => { clearTimeout(hqt); hqt = setTimeout(() => { ui.hq = ev.target.value.trim().toLocaleLowerCase('tr'); renderHw(); }, 150); });

    // ---------------------------------------------------------------- sekmeler (?tab=hw)
    let tabsReady = false;   // pageTabs kurulurken onChange'i bir kez çağırır; ilk çizim aşağıda
    const tabs = POps.pageTabs($('lgTabs'), {
        def: 'log',
        onChange: (tab) => {
            if (!tabsReady) return;
            if (tab === 'hw') { ui.hsig = ''; loadInventory(false); renderHw(); } else render();
            renderSummary();
        }
    });

    // ---------------------------------------------------------------- dışa aktarma ve diğer işlemler
    function csv(name, header, rows) {
        const lines = [header.join(';')].concat(rows.map(r => r.map(csvCell).join(';')));
        const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = name + '-' + new Date().toISOString().slice(0, 10) + '.csv';
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    }
    $('lgExport').addEventListener('click', () => {
        if (tabs.current() === 'hw') {
            const rows = hwRows();
            if (!rows.length) return POps.toast('info', POps.t('Dışa aktarılacak bilgisayar yok.'));
            // Sütun adları ve dosya adı arayüz dilinde; hücreler veri (ham zaman, ad, sürüm) olduğu gibi
            return csv(POps.t('pops-donanim'), [POps.t('Bilgisayar'), POps.t('Kimlik'), POps.t('Sınıf')].concat(HW_FIELDS.map(f => f[1]), [POps.t('Son güncelleme')]),
                rows.map(d => { const hw = hwOf(d.hostname) || {}; return [POps.deviceName(d), d.hostname, labText(d)].concat(HW_FIELDS.map(f => val(hw[f[0]])), [POps.csvTime(hw.last_updated)]); }));
        }
        const rows = ui.rows.filter(e => pass(e));
        if (!rows.length) return POps.toast('info', POps.t('Dışa aktarılacak kayıt yok.'));
        csv(POps.t('pops-kayitlar'), ['Zaman', 'Olay', 'Önem', 'Bilgisayar', 'Kimlik', 'Sınıf', 'Kim', 'Gerekçe', 'Mesaj', 'Olay türü', 'Eylem', 'Kategori', 'Risk'].map(h => POps.t(h)),
            rows.map(e => [POps.csvTime(e.r.timestamp), e.title, SEV_WORD[e.sev], pcName(e.pc), e.pc, labOf(e.pc), e.who, e.r.reason, cleanMsg(e.r.message), e.r.event_type, e.r.action, e.r.category, e.r.risk_level]));
    });

    async function wakeAll(btn) {
        const off = (state.devices || []).filter(d => POps.isOffline(d)).length;
        if (!off) return POps.toast('info', POps.t('Bütün bilgisayarlar zaten açık.'));
        const ok = await POps.confirm({
            title: POps.tn('Kapalı {n} bilgisayar uyandırılsın mı?', off),
            message: POps.t('MAC adresi bilinen bütün kapalı bilgisayarlara uyandırma (Wake-on-LAN) sinyali gönderilir.'),
            confirmText: POps.tn('{n} bilgisayarı uyandır', off), icon: 'zap'
        });
        if (!ok) return;
        await POps.act(btn, () => POps.post('/api/wake_all'), { success: (r) => POps.tn('{n} bilgisayara uyandırma sinyali gönderildi.', (r && r.woken_pcs) || 0) });
    }
    async function verifyChain(btn) {
        let r;
        try { r = await POps.busy(btn, () => POps.get('/api/system/audit-verify')); }
        catch (e) { POps.toast('error', POps.errorMessage(e)); return; }
        if (!r) return;
        const checked = Number(r.checked || 0);
        const cnt = { n: checked, count: checked.toLocaleString(POps.locale) };
        const note = POps.t('Denetim zinciri yönetici işlemlerini (karantina, açma kodu, güncelleme, lisans) tutar; her kayıt bir öncekinin özetini taşır, araya giren değişiklik zinciri kırar.');
        await POps.alert(r.ok
            ? { title: POps.t('Denetim zinciri sağlam'), tone: 'success', icon: 'shield', confirmText: POps.t('Kapat'), note,
                message: checked ? POps.t('{count} kayıt doğrulandı; hiçbiri değiştirilmemiş ya da silinmemiş.', cnt) : POps.t('Zincirde henüz doğrulanacak kayıt yok.') }
            : { title: POps.t('Denetim zinciri kırık'), tone: 'warning', icon: 'alert', confirmText: POps.t('Kapat'), note,
                message: POps.t('#{id} numaralı kayıt değiştirilmiş ya da silinmiş görünüyor. Ondan önceki {count} kayıt sorunsuz.', Object.assign({ id: r.first_broken_id }, cnt)) });
    }
    const menuItems = () => [
        CAN_ADMIN ? { label: POps.t('Bütün bilgisayarları uyandır'), icon: 'zap', onClick: (a) => wakeAll(a) } : null,
        IS_SUPER ? { label: POps.t('Denetim zincirini doğrula'), icon: 'shield', onClick: (a) => verifyChain(a) } : null
    ].filter(Boolean);
    $('lgMenuBtn').hidden = !menuItems().length;
    $('lgMenuBtn').addEventListener('click', (ev) => POps.menu(ev.currentTarget, menuItems()));

    // ---------------------------------------------------------------- başlat
    document.addEventListener('pops_data_updated', () => {
        if (tabs.current() === 'hw') { renderHw(); if (ui.inv && Date.now() - ui.invAt > 60000) loadInventory(true); }
        else if (ui.loaded && list.querySelector('.act')) {
            // cihaz adları sonradan gelirse (ilk açılış) satırlar ad ile yeniden yazılır
            const sig = (state.devices || []).map(d => d.hostname + (d.display_name || '') + (d.lab || '')).join('|');
            if (sig !== ui.devSig) { ui.devSig = sig; render(); }
        }
    });
    POps.watchDevices();
    tabsReady = true;
    if (tabs.current() === 'hw') loadInventory(false);
    render();
    loadLogs().catch(() => {});
    popsPoll(loadLogs, 10000);
})();
