<?php include 'includes/header.php'; ?>

<style>
    .lg-tabs { margin-bottom: 16px; }
    .lg-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
    .lg-bar .grow { flex: 1; }
    .lg-bar .search-field { flex: 0 1 260px; min-width: 180px; }
    .lg-bar select { width: auto; min-width: 150px; }
    .segmented .count { color: var(--text-muted); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }
    .lg-list { padding: 4px 16px 8px; }
    .lg-list .act { padding: 13px 6px; outline: none; }
    .lg-list .act:focus-visible { box-shadow: var(--focus-ring); }
    .lg-list .act > .facts { grid-column: 2 / -1; margin-top: -2px; cursor: text; }
    .lg-day { font-size: var(--text-xs); font-weight: var(--fw-semibold); color: var(--text-muted); padding: 18px 6px 2px; }
    .lg-day:first-child { padding-top: 12px; }
    .lg-links { grid-column: 1 / -1; display: flex; gap: 14px; flex-wrap: wrap; padding-top: 6px; }
    .lg-links button { color: var(--primary-500); font-size: var(--text-xs); font-weight: var(--fw-medium); }
    .lg-links button:hover { text-decoration: underline; }
    .lg-note { font-size: var(--text-xs); color: var(--text-muted); padding: 10px 4px 0; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .lg-note button { color: var(--primary-500); font-size: var(--text-xs); font-weight: var(--fw-medium); }
    .lg-note button:hover { text-decoration: underline; }
    .lg-fbtn .badge { min-width: 18px; justify-content: center; padding: 0 6px; background: var(--primary-500); color: #fff; }
    .lg-fpanel { position: fixed; z-index: 900; width: 340px; max-width: calc(100vw - 24px); background: var(--bg-surface); border-radius: 14px; box-shadow: var(--shadow-xl); padding: 14px 16px; display: flex; flex-direction: column; gap: 12px; animation: fadeIn 0.12s ease; }
    .lg-fpanel[hidden] { display: none; }
    .lg-fpanel label { display: flex; flex-direction: column; gap: 5px; margin: 0; font-size: var(--text-xs); font-weight: var(--fw-medium); color: var(--text-tertiary); }
    .lg-fpanel .two { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .lg-fpanel .foot { display: flex; justify-content: space-between; align-items: center; padding-top: 4px; border-top: 1px solid #f0f0f3; }
    .lg-chips { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin: -6px 0 12px; }
    .lg-chips:empty { display: none; }
    .lg-chips .chip { gap: 6px; padding-right: 5px; cursor: default; }
    .lg-chips .chip b { font-weight: 600; }
    .lg-chips .chip button { width: 20px; height: 20px; border-radius: 99px; display: inline-flex; align-items: center; justify-content: center; color: inherit; }
    .lg-chips .chip button:hover { background: var(--bg-hover); }
    .lg-chips .clear { font-size: var(--text-sm); color: var(--primary-500); padding: 0 4px; }
    .lg-chips .clear:hover { text-decoration: underline; }
    .lg-pager { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; padding: 12px 2px 0; font-size: var(--text-sm); color: var(--text-tertiary); }
    .lg-pager .pages { display: flex; align-items: center; gap: 2px; }
    .lg-pager .pages button { min-width: 32px; height: 32px; padding: 0 8px; border-radius: 8px; color: var(--text-primary); font-variant-numeric: tabular-nums; }
    .lg-pager .pages button:hover:not(:disabled) { background: var(--bg-hover); }
    .lg-pager .pages button.on { background: var(--text-primary); color: #fff; }
    .lg-pager .pages button:disabled { color: var(--text-muted); cursor: default; }
    .lg-pager .pages .gap { min-width: 24px; text-align: center; }
    .lg-pager .size { display: flex; align-items: center; gap: 8px; }
    .lg-pager .size select { width: auto; min-height: 32px; padding-top: 0; padding-bottom: 0; }
    .hw-table tbody tr { cursor: pointer; }
    .hw-table tbody tr.is-focus { background: #f0f6ff; }
    .hw-table .nm { font-weight: var(--fw-semibold); }
    .hw-table .sub { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .hw-table td.mono { font-family: var(--font-mono); font-size: 12px; color: var(--text-secondary); }
    .hw-table .wait { color: var(--text-muted); }
    .hw-table td:nth-child(3), .hw-table td:nth-last-child(-n+2) { white-space: nowrap; }
    .lg-dlinks { display: flex; gap: 8px; flex-wrap: wrap; }
    @media (max-width: 640px) {
        .lg-bar select { flex: 1 1 auto; }
                .lg-list { padding: 2px 10px 6px; }
        .lg-list .act > .facts { grid-column: 1 / -1; grid-template-columns: 96px minmax(0, 1fr); }
        .lg-bar .search-field { flex: 1 1 100%; }
    }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Kayıtlar'); ?></h1>
        <div class="summary" id="lgSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="lgExport" data-tip="<?php _e('Listeyi dışa aktar (CSV)'); ?>" data-tip-pos="left" aria-label="<?php _e('Listeyi dışa aktar'); ?>"><?php echo pops_icon('download'); ?></button>
        <button type="button" class="ibtn boxed" id="lgMenuBtn" data-tip="<?php _e('Diğer işlemler'); ?>" data-tip-pos="left" aria-label="<?php _e('Diğer işlemler'); ?>" aria-haspopup="menu" hidden><?php echo pops_icon('more'); ?></button>
    </div>
</div>

<div class="tabs lg-tabs" id="lgTabs" role="tablist" aria-label="<?php _e('Görünüm'); ?>">
    <button type="button" class="tab active" data-tab="log" role="tab" aria-selected="true"><?php _e('Olaylar'); ?></button>
    <button type="button" class="tab" data-tab="hw" role="tab" aria-selected="false"><?php _e('Donanım'); ?></button>
</div>

<section id="lgPaneLog">
    <div class="lg-bar">
        <div class="segmented" id="lgFilter" role="group" aria-label="<?php _e('Önem derecesine göre süz'); ?>">
            <button type="button" data-f="all" class="active" aria-pressed="true"><?php _e('Tümü'); ?> <span class="count" data-n="all"></span></button>
            <button type="button" data-f="warn" aria-pressed="false"><?php _e('Uyarılar'); ?> <span class="count" data-n="warn"></span></button>
            <button type="button" data-f="sec" aria-pressed="false"><?php _e('Güvenlik'); ?> <span class="count" data-n="sec"></span></button>
        </div>
        <button type="button" class="btn secondary lg-fbtn" id="lgFiltersBtn" aria-haspopup="dialog" aria-expanded="false" aria-controls="lgFPanel"><?php echo pops_icon('filter', 'sm'); ?><?php _e('Süzgeçler'); ?><span class="badge" id="lgFCount" hidden></span></button>
        <span class="grow"></span>
        <div class="search-field">
            <?php echo pops_icon('search', 'sm'); ?>
            <input type="search" id="lgSearch" placeholder="<?php _e('Olay, bilgisayar ya da kişi'); ?>" aria-label="<?php _e('Kayıtlarda ara'); ?>">
        </div>
    </div>
    <div class="lg-fpanel" id="lgFPanel" role="dialog" aria-label="<?php _e('Süzgeçler'); ?>" hidden>
        <label><?php _e('Tür'); ?><select id="lgKind"><option value=""><?php _e('Bütün türler'); ?></option></select></label>
        <label><?php _e('Sınıf'); ?><select id="lgLab"><option value=""><?php _e('Bütün sınıflar'); ?></option></select></label>
        <label><?php _e('Kişi'); ?><select id="lgWho"><option value=""><?php _e('Herkes'); ?></option></select></label>
        <div class="two">
            <label><?php _e('Başlangıç'); ?><input type="date" id="lgFrom"></label>
            <label><?php _e('Bitiş'); ?><input type="date" id="lgTo"></label>
        </div>
        <div class="foot"><button type="button" class="btn ghost sm" id="lgFClear"><?php _e('Süzgeçleri temizle'); ?></button><button type="button" class="btn sm" id="lgFDone"><?php _e('Tamam'); ?></button></div>
    </div>
    <div class="lg-chips" id="lgChips"></div>
    <div class="card"><div class="lg-list" id="lgList" aria-live="polite"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Kayıtlar yükleniyor…'); ?></div></div></div>
    <div class="lg-pager" id="lgPager" hidden>
        <span id="lgRange"></span>
        <div class="pages" id="lgPages" role="navigation" aria-label="<?php _e('Sayfalar'); ?>"></div>
        <label class="size"><?php _e('Sayfa başına'); ?> <select id="lgSize" aria-label="<?php _e('Sayfa başına kayıt'); ?>"><option>25</option><option>50</option><option>75</option><option>100</option></select></label>
    </div>
    <div class="lg-note" id="lgNote"></div>
</section>

<section id="lgPaneHw" hidden>
    <div class="lg-bar">
        <select id="hwLab" aria-label="<?php _e('Sınıfa göre süz'); ?>"><option value=""><?php _e('Bütün sınıflar'); ?></option></select>
        <span class="grow"></span>
        <div class="search-field">
            <?php echo pops_icon('search', 'sm'); ?>
            <input type="search" id="hwSearch" placeholder="<?php _e('Bilgisayar, işlemci, IP, MAC'); ?>" aria-label="<?php _e('Donanımda ara'); ?>">
        </div>
    </div>
    <div class="table-wrap">
        <table class="data-table hw-table wide">
            <thead><tr><th><?php _e('Bilgisayar'); ?></th><th><?php _e('İşlemci'); ?></th><th><?php _e('Bellek'); ?></th><th>Disk</th><th><?php _e('İşletim sistemi'); ?></th><th>IP</th><th><?php _e('Güncelleme'); ?></th></tr></thead>
            <tbody id="hwBody"><tr><td colspan="7"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Donanım bilgileri yükleniyor…'); ?></div></td></tr></tbody>
        </table>
    </div>
    <div class="lg-note"><?php _e('Donanım bilgisini ajan açılışta ve günde bir kez gönderir.'); ?></div>
</section>

<script>
(function () {
    const dev = POps.dev;
    const CAN_ADMIN = dev.canAdmin;
    const IS_SUPER = window.USER_ROLE === 'superadmin';
    const $ = (id) => document.getElementById(id);
    const params = new URLSearchParams(location.search);
    const LIMITS = [1000, 5000, 20000];
    const SIZES = [25, 50, 75, 100];
    const savedSize = (() => { try { return Number(localStorage.getItem('pops_log_size')) || 0; } catch (e) { return 0; } })();
    const ui = {
        tab: params.get('tab') === 'hw' ? 'hw' : 'log', f: ['warn', 'sec'].includes(params.get('f')) ? params.get('f') : 'all', kind: '', q: '', pc: params.get('pc') || '', from: '', to: '',
        lab: '', who: '', page: 1, size: SIZES.includes(savedSize) ? savedSize : 25,
        limitIx: 0, open: new Set(), rows: [], byId: new Map(), loaded: false, sig: '',
        inv: null, invAt: 0, hq: '', hlab: '', hfocus: null, hsig: ''
    };
    const list = $('lgList');

    // Olay sözlüğü ortak dosyada (assets/pops_devices.js, POps.logs): Kontrol merkezi de aynı dili kullanır
    const { SEV_WORD, KIND_LABEL, CAT_LABEL, RISK_LABEL, CAP, BY_TYPE, commandOf, EVENTS, parseMeta, isoOf, cleanMsg, cap1, riskSev, whoOf, sourceOf, norm } = POps.logs;

    const pcName = (pc) => pc ? dev.name(pc) : '';
    // Özet satırında sayı kalın: yer tutucuya HTML parçası (POps.tHtml'in üçüncü argümanı)
    const boldHtml = (n) => `<b>${Number(n)}</b>`;
    const labOf = (pc) => { const d = dev.find(pc); return d && d.lab && d.lab !== dev.UNASSIGNED ? d.lab : ''; };

    // ---------------------------------------------------------------- süzgeç
    function hay(e) {
        return [e.title, cleanMsg(e.r.message), e.who, e.r.actor_id, pcName(e.pc), e.pc, labOf(e.pc), e.r.reason, e.m.domain, e.r.event_type, SEV_WORD[e.sev]]
            .join(' ').toLocaleLowerCase('tr');
    }
    function pass(e, skip) {
        if (skip !== 'f' && ui.f === 'warn' && e.sev === 'info') return false;
        if (skip !== 'f' && ui.f === 'sec' && !e.security) return false;
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
        const linksHtml = e.pc ? `<div class="lg-links">${ui.pc !== e.pc ? `<button type="button" data-act="pc">${POps.tHtml('Yalnızca bu bilgisayarın kayıtları')}</button>` : ''}<button type="button" data-act="device">${POps.tHtml('Bilgisayarın ayrıntıları')}</button></div>` : '';
        return `<div class="facts">${rowsHtml}${linksHtml}</div>`;
    }
    function rowHtml(e) {
        const open = ui.open.has(e.id);
        const cls = e.sev === 'info' ? '' : e.sev;
        const where = pcName(e.pc);
        const lab = labOf(e.pc);
        const whereHtml = where ? escapeHtml(where) + (lab ? ' · ' + escapeHtml(lab) : '') + ' · ' : '';
        const metaHtml = whereHtml + escapeHtml(e.who) + ' · ' + POps.timeHtml(e.at);
        const whyHtml = e.why && e.sev !== 'info' ? `<div class="why${e.sev === 'warn' ? ' warn' : ''}">${escapeHtml(e.why)}</div>` : '';
        const noteHtml = e.why && e.sev === 'info' ? ' · ' + escapeHtml(e.why.toLocaleLowerCase(POps.locale)) : '';
        return `<div class="act clickable" data-id="${escapeHtml(e.id)}" role="button" tabindex="0" aria-expanded="${open ? 'true' : 'false'}">
            <div class="res ${escapeHtml(cls)}">${POps.iconHtml(e.icon)}</div>
            <div class="lg-body" style="min-width:0"><div class="what">${escapeHtml(e.title)}</div><div class="meta">${metaHtml}${noteHtml}</div>${whyHtml}</div>
            <div class="side"><span class="word ${escapeHtml(cls)}">${escapeHtml(SEV_WORD[e.sev])}</span></div>${open ? factsHtml(e) : ''}
        </div>`;
    }

    function renderCounts() {
        const base = ui.rows.filter(e => pass(e, 'f'));
        const n = { all: base.length, warn: base.filter(e => e.sev !== 'info').length, sec: base.filter(e => e.security).length };
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
    function renderSummary() {
        const box = $('lgSummary');
        if (ui.tab === 'hw') {
            const devs = state.devices || [];
            const inv = ui.inv || {};
            const have = devs.filter(d => inv[d.hostname] && inv[d.hostname].cpu && inv[d.hostname].cpu !== '-').length;
            box.innerHTML = `<span class="sum">${POps.tnHtml('{n} bilgisayar', devs.length, null, { n: boldHtml(devs.length) })}</span><span class="sum">${POps.tnHtml('{n} donanım bildirdi', have, null, { n: boldHtml(have) })}</span>`
                + (ui.inv && devs.length - have ? `<span class="sum"><span class="dot off"></span>${POps.tnHtml('{n} bekleniyor', devs.length - have, null, { n: boldHtml(devs.length - have) })}</span>` : '');
            return;
        }
        if (!ui.loaded) { box.textContent = ''; return; }
        const today = new Date();
        const todayKey = today.getFullYear() + '-' + String(today.getMonth() + 1).padStart(2, '0') + '-' + String(today.getDate()).padStart(2, '0');
        const warn = ui.rows.filter(e => e.sev === 'warn').length;
        const bad = ui.rows.filter(e => e.sev === 'bad').length;
        const todayN = ui.rows.filter(e => e.day === todayKey).length;
        box.innerHTML = `<span class="sum">${POps.tnHtml('{n} kayıt', ui.rows.length, null, { n: boldHtml(ui.rows.length) })}</span><span class="sum">${POps.tHtml('bugün {n}', null, { n: boldHtml(todayN) })}</span>`
            + (warn ? `<a href="#" class="sum" data-f="warn"><span class="dot warn"></span>${POps.tnHtml('{n} uyarı', warn, null, { n: boldHtml(warn) })}</a>` : '')
            + (bad ? `<a href="#" class="sum" data-f="warn"><span class="dot bad"></span>${POps.tnHtml('{n} kritik', bad, null, { n: boldHtml(bad) })}</a>` : '');
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
        const n = f.filter(x => x[0] !== 'pc').length;
        $('lgFCount').hidden = !n;
        $('lgFCount').textContent = String(n);
    }
    function render() {
        renderSummary();
        if (ui.tab !== 'log') return;
        renderCounts();
        renderPcChip();
        renderNote();
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
        const want = new Set([1, pages, ui.page - 1, ui.page, ui.page + 1].filter(n => n >= 1 && n <= pages));
        if (ui.page <= 3) [2, 3, 4].forEach(n => n <= pages && want.add(n));
        if (ui.page >= pages - 2) [pages - 3, pages - 2, pages - 1].forEach(n => n >= 1 && want.add(n));
        const nums = [...want].sort((a, b) => a - b);
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
        const u = new URL(location.href);
        if (ui.pc) u.searchParams.set('pc', ui.pc); else u.searchParams.delete('pc');
        history.replaceState(null, '', u.pathname + u.search);
        render();
    }
    list.addEventListener('click', (ev) => {
        const b = ev.target.closest('[data-act]');
        const row = ev.target.closest('.act[data-id]');
        if (b) {
            const e = row ? ui.byId.get(row.dataset.id) : null;
            if (b.dataset.act === 'pc' && e) { setPc(e.pc); window.scrollTo({ top: 0, behavior: 'smooth' }); }
            else if (b.dataset.act === 'device' && e) dev.open(e.pc, { source: 'logger' });
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
    $('lgSummary').addEventListener('click', (ev) => { const a = ev.target.closest('[data-f]'); if (a) { ev.preventDefault(); setTab('log'); ui.f = a.dataset.f; ui.page = 1; render(); } });
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
    const dateChanged = () => { ui.page = 1; ui.sig = ''; ui.limitIx = 0; render(); loadLogs().catch(() => {}); };
    $('lgKind').addEventListener('change', (ev) => { ui.kind = ev.target.value; ui.page = 1; render(); });
    $('lgLab').addEventListener('change', (ev) => { ui.lab = ev.target.value; ui.page = 1; render(); });
    $('lgWho').addEventListener('change', (ev) => { ui.who = ev.target.value; ui.page = 1; render(); });
    $('lgFrom').addEventListener('change', (ev) => { ui.from = ev.target.value; dateChanged(); });
    $('lgTo').addEventListener('change', (ev) => { ui.to = ev.target.value; dateChanged(); });
    function clearFilter(key) {
        if (key === 'pc' || key === 'all') { if (ui.pc) { setPc(''); } }
        if (key === 'kind' || key === 'all') { ui.kind = ''; $('lgKind').value = ''; }
        if (key === 'lab' || key === 'all') { ui.lab = ''; $('lgLab').value = ''; }
        if (key === 'who' || key === 'all') { ui.who = ''; $('lgWho').value = ''; }
        if ((key === 'date' || key === 'all') && (ui.from || ui.to)) { ui.from = ui.to = ''; $('lgFrom').value = $('lgTo').value = ''; dateChanged(); return; }
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
    function hwRows() {
        const q = ui.hq;
        return (state.devices || []).filter(d => {
            if (ui.hlab && (ui.hlab === dev.UNASSIGNED ? (d.lab && d.lab !== dev.UNASSIGNED) : d.lab !== ui.hlab)) return false;
            if (!q) return true;
            const hw = (ui.inv && ui.inv[d.hostname]) || {};
            return [POps.deviceName(d), d.hostname, d.lab, d.ip, hw.cpu, hw.ram, hw.gpu, hw.os_version, hw.ip_address, hw.mac_address, hw.motherboard]
                .some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
        }).sort((a, b) => labKey(a).localeCompare(labKey(b), 'tr') || POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
    }
    function renderHwLabs() {
        const sel = $('hwLab');
        const labs = dev.labs();
        if (sel.dataset.sig === labs.join('|')) return;
        sel.dataset.sig = labs.join('|');
        sel.replaceChildren(POps.el('option', { value: '', text: POps.t('Bütün sınıflar') }));
        labs.forEach(l => sel.append(POps.el('option', { value: l, text: l })));
        sel.append(POps.el('option', { value: dev.UNASSIGNED, text: POps.t('Atanmamış') }));
        sel.value = ui.hlab;
    }
    function hwRowHtml(d) {
        const hw = hwOf(d.hostname);
        const lab = labText(d);
        const cellHtml = (v) => val(v) ? escapeHtml(val(v)) : '—';
        const specsHtml = hw
            ? `<td>${cellHtml(hw.cpu)}</td><td>${cellHtml(hw.ram)}</td><td>${cellHtml(hw.disk_info)}</td><td>${cellHtml(hw.os_version)}</td>`
            : `<td colspan="4" class="wait">${POps.tHtml('Donanım bilgisi bekleniyor')}</td>`;
        return `<tr data-host="${escapeHtml(d.hostname)}" class="${ui.hfocus === d.hostname ? 'is-focus' : ''}">
            <td><div class="nm">${escapeHtml(POps.deviceName(d))}</div><div class="sub">${escapeHtml(lab)}</div></td>
            ${specsHtml}
            <td class="mono">${escapeHtml(val(hw && hw.ip_address) || d.ip || '—')}</td>
            <td>${hw ? POps.timeHtml(isoOf(hw.last_updated)) : '<span class="wait">—</span>'}</td>
        </tr>`;
    }
    function renderHw() {
        if (ui.tab !== 'hw') return;
        renderSummary();
        renderHwLabs();
        if (!state.devicesLoaded || !ui.inv) return;
        const rows = hwRows();
        const sig = JSON.stringify([ui.hq, ui.hlab, ui.hfocus, ui.invAt, rows.map(d => [d.hostname, d.display_name, d.lab, d.ip])]);
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
    function hwDrawerHtml(d) {
        const hw = hwOf(d.hostname);
        const st = dev.state(d);
        const lab = labText(d);
        const rowsHtml = hw
            ? HW_FIELDS.filter(f => val(hw[f[0]])).map(f => `<div class="grow"><span>${escapeHtml(f[1])}</span><span${f[0] === 'mac_address' || f[0] === 'ip_address' ? ' class="mono"' : ''}>${escapeHtml(val(hw[f[0]]))}</span></div>`).join('')
              + `<div class="grow"><span>${POps.tHtml('Kimlik')}</span><span class="mono">${escapeHtml(d.hostname)}</span></div>`
              + `<div class="grow"><span>${POps.tHtml('Son güncelleme')}</span><span>${POps.timeHtml(isoOf(hw.last_updated))}</span></div>`
            : `<div class="grow"><span>${POps.tHtml('Kimlik')}</span><span class="mono">${escapeHtml(d.hostname)}</span></div>`;
        return `<div class="drawer-head">
                <div class="drawer-title">
                    <span class="drawer-ico ${escapeHtml(st.cls)}">${POps.iconHtml('cpu', 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(POps.deviceName(d))}</h2><div class="sub"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)} · ${escapeHtml(lab)}</div></div>
                </div>
                <button type="button" class="ibtn sm" data-lg="close" data-tip="${escapeHtml(POps.t('Kapat (Esc)'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button>
            </div>
            ${hw ? '' : `<div class="issue lock">${POps.tHtml('Ajan donanım bilgisini henüz göndermedi; açılışta ve günde bir kez gönderir.')}</div>`}
            <div class="glist">${rowsHtml}</div>
            <div class="lg-dlinks">
                <button type="button" class="btn secondary sm" data-lg="logs">${POps.iconHtml('list', 'sm')}${POps.tHtml('Olay kayıtları')}</button>
                <button type="button" class="btn secondary sm" data-lg="device">${POps.iconHtml('monitor', 'sm')}${POps.tHtml('Bilgisayarın ayrıntıları')}</button>
            </div>`;
    }
    function openHw(h) {
        const d = dev.find(h);
        if (!d) return;
        ui.hfocus = h;
        renderHw();
        const body = POps.drawer.open('hw:' + h, { onClose: () => { if (ui.hfocus === h) { ui.hfocus = null; renderHw(); } } });
        body.innerHTML = hwDrawerHtml(d);
        if (!body.dataset.lgWired) {
            body.dataset.lgWired = '1';
            body.addEventListener('click', (ev) => {
                const b = ev.target.closest('[data-lg]');
                const k = POps.drawer.key() || '';
                if (!b || !k.startsWith('hw:')) return;
                const host = k.slice(3);
                if (b.dataset.lg === 'close') POps.drawer.close();
                else if (b.dataset.lg === 'device') dev.open(host, { source: 'logger' });
                else if (b.dataset.lg === 'logs') { POps.drawer.close(); setTab('log'); setPc(host); }
            });
        }
    }
    $('hwBody').addEventListener('click', (ev) => { const tr = ev.target.closest('tr[data-host]'); if (tr) openHw(tr.dataset.host); });
    $('hwLab').addEventListener('change', (ev) => { ui.hlab = ev.target.value; renderHw(); });
    let hqt = null;
    $('hwSearch').addEventListener('input', (ev) => { clearTimeout(hqt); hqt = setTimeout(() => { ui.hq = ev.target.value.trim().toLocaleLowerCase('tr'); renderHw(); }, 150); });

    // ---------------------------------------------------------------- sekmeler
    function setTab(tab) {
        ui.tab = tab;
        $('lgTabs').querySelectorAll('.tab').forEach(t => { const on = t.dataset.tab === tab; t.classList.toggle('active', on); t.setAttribute('aria-selected', on ? 'true' : 'false'); });
        $('lgPaneLog').hidden = tab !== 'log';
        $('lgPaneHw').hidden = tab !== 'hw';
        const u = new URL(location.href);
        if (tab === 'hw') u.searchParams.set('tab', 'hw'); else u.searchParams.delete('tab');
        history.replaceState(null, '', u.pathname + u.search);
        if (tab === 'hw') { ui.hsig = ''; loadInventory(false); renderHw(); } else render();
        renderSummary();
    }
    $('lgTabs').addEventListener('click', (ev) => { const t = ev.target.closest('.tab[data-tab]'); if (t && t.dataset.tab !== ui.tab) setTab(t.dataset.tab); });

    // ---------------------------------------------------------------- dışa aktarma ve diğer işlemler
    function csv(name, header, rows) {
        // Hücre =, +, -, @ ile başlarsa önüne ' (tablo programında formül olarak çalışmasın)
        const cell = (v) => { let s = String(v == null ? '' : v); if (/^[=+\-@\t\r]/.test(s)) s = "'" + s; return /[";\n\r]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
        const lines = [header.join(';')].concat(rows.map(r => r.map(cell).join(';')));
        const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = name + '-' + new Date().toISOString().slice(0, 10) + '.csv';
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    }
    $('lgExport').addEventListener('click', () => {
        if (ui.tab === 'hw') {
            const rows = hwRows();
            if (!rows.length) return POps.toast('info', POps.t('Dışa aktarılacak bilgisayar yok.'));
            // Sütun adları ve dosya adı arayüz dilinde; hücreler veri (ham zaman, ad, sürüm) olduğu gibi
            return csv(POps.t('pops-donanim'), [POps.t('Bilgisayar'), POps.t('Kimlik'), POps.t('Sınıf')].concat(HW_FIELDS.map(f => f[1]), [POps.t('Son güncelleme')]),
                rows.map(d => { const hw = hwOf(d.hostname) || {}; return [POps.deviceName(d), d.hostname, labText(d)].concat(HW_FIELDS.map(f => val(hw[f[0]])), [hw.last_updated || '']); }));
        }
        const rows = ui.rows.filter(e => pass(e));
        if (!rows.length) return POps.toast('info', POps.t('Dışa aktarılacak kayıt yok.'));
        csv(POps.t('pops-kayitlar'), ['Zaman', 'Olay', 'Önem', 'Bilgisayar', 'Kimlik', 'Sınıf', 'Kim', 'Gerekçe', 'Mesaj', 'Olay türü', 'Eylem', 'Kategori', 'Risk'].map(h => POps.t(h)),
            rows.map(e => [e.r.timestamp, e.title, SEV_WORD[e.sev], pcName(e.pc), e.pc, labOf(e.pc), e.who, e.r.reason, cleanMsg(e.r.message), e.r.event_type, e.r.action, e.r.category, e.r.risk_level]));
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
        if (ui.tab === 'hw') { renderHw(); if (ui.inv && Date.now() - ui.invAt > 60000) loadInventory(true); }
        else if (ui.loaded && list.querySelector('.act')) {
            // cihaz adları sonradan gelirse (ilk açılış) satırlar ad ile yeniden yazılır
            const sig = (state.devices || []).map(d => d.hostname + (d.display_name || '') + (d.lab || '')).join('|');
            if (sig !== ui.devSig) { ui.devSig = sig; render(); }
        }
    });
    POps.watchDevices();
    setTab(ui.tab);
    loadLogs().catch(() => {});
    popsPoll(loadLogs, 10000);
})();
</script>

<?php include 'includes/footer.php'; ?>
