<?php include 'includes/header.php'; ?>

<style>
    .dev-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
    .dev-bar .grow { flex: 1; }
    .dev-bar .search-field { flex: 0 1 240px; min-width: 180px; }
    .dev-bar select { width: auto; min-width: 150px; }
    .dev-table td { padding-top: 10px; padding-bottom: 10px; }
    .dev-table tbody tr { cursor: pointer; }
    .dev-table tbody tr.is-focus { background: #f0f6ff; }
    .dev-table .nm { display: flex; align-items: center; gap: 8px; font-weight: var(--fw-semibold); }
    .dev-table .sub { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .dev-table th.sortable { cursor: pointer; user-select: none; }
    .dev-table th.sortable:hover { color: var(--text-primary); }
    .dev-table th .arr { opacity: 0.6; margin-left: 3px; }
    .dev-table .ver { display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; }
    .dev-table td.mono { font-family: var(--font-mono); font-size: 12px; color: var(--text-secondary); }
</style>

<div class="page-header">
    <div>
        <h1>Cihazlar</h1>
        <div class="summary" id="devSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="exportBtn" data-tip="Listeyi dışa aktar (CSV)" data-tip-pos="left" aria-label="Listeyi dışa aktar"><?php echo pops_icon('download'); ?></button>
    </div>
</div>

<div class="dev-bar">
    <div class="actionbar" id="devBar" role="toolbar" aria-label="Cihaz işlemleri"></div>
    <span class="grow"></span>
    <div class="segmented" id="devFilter" role="group" aria-label="Duruma göre süz">
        <button type="button" data-f="all" class="active" aria-pressed="true">Tümü</button>
        <button type="button" data-f="on" aria-pressed="false">Açık</button>
        <button type="button" data-f="off" aria-pressed="false">Kapalı</button>
        <button type="button" data-f="issue" aria-pressed="false">Sorunlu</button>
    </div>
    <select id="devLab" aria-label="Sınıfa göre süz"><option value="">Bütün sınıflar</option></select>
    <div class="search-field">
        <?php echo pops_icon('search', 'sm'); ?>
        <input type="search" id="devSearch" placeholder="Ad, kullanıcı, IP, MAC" aria-label="Cihaz ara">
    </div>
</div>

<div class="table-wrap">
    <table class="data-table dev-table wide">
        <thead id="devHead"></thead>
        <tbody id="devBody"><tr><td colspan="7"><div class="loading-state" role="status"><span class="spinner"></span>Cihazlar yükleniyor…</div></td></tr></tbody>
    </table>
</div>

<script>
(function () {
    const dev = POps.dev;
    const CAN_ADMIN = ['admin', 'superadmin'].includes(window.USER_ROLE);
    const params = new URLSearchParams(location.search);
    const ui = { f: params.get('f') || 'all', lab: params.get('lab') || '', q: '', sort: 'name', dir: 1, selected: new Set(), focus: null, hash: '' };
    const $ = (id) => document.getElementById(id);
    const body = $('devBody'), bar = $('devBar');

    function issuesOf(d, newest) { return dev.issues(d, newest).filter(i => i.kind !== 'lock' || i.icon === 'lock'); }
    function list() {
        const newest = dev.newestVersion();
        const q = ui.q.toLocaleLowerCase('tr');
        let rows = (state.devices || []).filter(d => {
            const st = dev.state(d).cls;
            if (ui.f === 'on' && st === 'off') return false;
            if (ui.f === 'off' && st !== 'off') return false;
            if (ui.f === 'issue' && !issuesOf(d, newest).length) return false;
            if (ui.lab && (ui.lab === dev.UNASSIGNED ? (d.lab && d.lab !== dev.UNASSIGNED) : d.lab !== ui.lab)) return false;
            if (q && ![POps.deviceName(d), d.hostname, d.ip, d.mac, dev.user(d), d.lab].some(v => String(v || '').toLocaleLowerCase('tr').includes(q))) return false;
            return true;
        });
        const key = {
            name: (d) => POps.deviceName(d).toLocaleLowerCase('tr'),
            lab: (d) => String(d.lab || '').toLocaleLowerCase('tr'),
            status: (d) => ({ on: 0, idle: 1, off: 2 })[dev.state(d).cls],
            version: (d) => (dev.parseVersion(dev.version(d)) || [0, 0, 0]).map(n => String(n).padStart(4, '0')).join('.'),
            ip: (d) => String(d.ip || '').split('.').map(n => n.padStart(3, '0')).join('.')
        }[ui.sort];
        rows.sort((a, b) => { const x = key(a), y = key(b); return (x < y ? -1 : x > y ? 1 : 0) * ui.dir || POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }); });
        return { rows, newest };
    }

    const COLS = [['name', 'Bilgisayar'], ['lab', 'Sınıf'], ['status', 'Durum'], ['version', 'Ajan'], ['ip', 'IP']];
    function renderHead(rows) {
        const all = rows.length && rows.every(d => ui.selected.has(d.hostname));
        $('devHead').innerHTML = '<tr>' + (CAN_ADMIN ? `<th class="check-col"><input type="checkbox" id="devAll" ${all ? 'checked' : ''} aria-label="Listedekilerin hepsini seç"></th>` : '')
            + COLS.map(([k, label]) => `<th class="sortable" data-sort="${escapeHtml(k)}" aria-sort="${ui.sort === k ? (ui.dir > 0 ? 'ascending' : 'descending') : 'none'}">${escapeHtml(label)}${ui.sort === k ? `<span class="arr">${ui.dir > 0 ? '↑' : '↓'}</span>` : ''}</th>`).join('')
            + '</tr>';
    }
    function rowHtml(d, newest) {
        const st = dev.state(d);
        const h = d.hostname;
        const marksHtml = issuesOf(d, newest).slice(0, 2).map(i => dev.markHtml(i)).join('');
        const statusHtml = `<span class="status-pill ${st.cls === 'on' ? 'online' : st.cls === 'idle' ? 'idle' : 'offline'}"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}${st.cls === 'off' && st.since ? ' · ' + POps.timeHtml(st.since) : ''}</span>`;
        const v = dev.version(d);
        const old = v && newest && dev.cmpVersion(v, newest) < 0;
        return `<tr data-host="${escapeHtml(h)}" class="${ui.selected.has(h) ? 'is-selected' : ''}${ui.focus === h ? ' is-focus' : ''}">
            ${CAN_ADMIN ? `<td class="check-col"><input type="checkbox" class="dev-cb" data-host="${escapeHtml(h)}" ${ui.selected.has(h) ? 'checked' : ''} aria-label="${escapeHtml(POps.deviceName(d))} seç"></td>` : ''}
            <td><div class="nm">${escapeHtml(POps.deviceName(d))}${marksHtml}</div><div class="sub">${escapeHtml(dev.subline(d))}</div></td>
            <td>${d.lab && d.lab !== dev.UNASSIGNED ? escapeHtml(d.lab) : '<span class="faint">Atanmamış</span>'}</td>
            <td>${statusHtml}</td>
            <td><span class="ver">${escapeHtml(v || '—')}${old ? `<span class="mark old" data-tip="${escapeHtml('Eski sürüm; güncel ' + newest)}" aria-label="Eski sürüm">↑</span>` : ''}</span></td>
            <td class="mono">${escapeHtml(d.ip || '—')}</td>
        </tr>`;
    }
    function renderSummary() {
        const all = state.devices || [];
        const newest = dev.newestVersion();
        const on = all.filter(d => dev.state(d).cls === 'on').length;
        const idle = all.filter(d => dev.state(d).cls === 'idle').length;
        const issues = all.filter(d => issuesOf(d, newest).length).length;
        const sumHtml = `<span class="sum"><b>${all.length}</b> cihaz</span>`
            + `<span class="sum"><span class="dot on"></span><b>${Number(on)}</b> açık</span>`
            + (idle ? `<span class="sum"><span class="dot idle"></span><b>${idle}</b> boşta</span>` : '')
            + `<span class="sum"><span class="dot off"></span><b>${all.length - on - idle}</b> kapalı</span>`
            + (issues ? `<a href="#" class="sum" data-f="issue"><span class="dot warn"></span><b>${issues}</b> sorunlu</a>` : '');
        $('devSummary').innerHTML = sumHtml;
    }
    function renderLabs() {
        const sel = $('devLab');
        const cur = ui.lab;
        const labs = dev.labs();
        const optsHtml = '<option value="">Bütün sınıflar</option>' + labs.map(l => `<option value="${escapeHtml(l)}">${escapeHtml(l)}</option>`).join('') + `<option value="${escapeHtml(dev.UNASSIGNED)}">Atanmamış</option>`;
        if (sel.dataset.sig !== labs.join('|')) { sel.innerHTML = optsHtml; sel.dataset.sig = labs.join('|'); }
        sel.value = cur;
    }
    function scope(rows) { return ui.selected.size ? [...ui.selected] : rows.map(d => d.hostname); }
    function renderBar(rows) {
        const n = ui.selected.size;
        const tgt = n ? `${n} bilgisayar` : `listedeki ${rows.length} bilgisayar`;
        if (!CAN_ADMIN) { bar.innerHTML = `<span class="scope">${rows.length} bilgisayar</span>`; return; }
        bar.innerHTML = (n
            ? `<span class="scope sel">${Number(n)} seçili<button type="button" data-act="clear" aria-label="Seçimi temizle" data-tip="Seçimi temizle (Esc)">${POps.iconHtml('x', 'sm')}</button></span>`
            : `<span class="scope">Listedeki <b>${rows.length}</b></span>`)
            + '<span class="sep"></span>'
            + `<button type="button" class="ibtn" data-act="wake" data-tip="Uyandır · ${escapeHtml(tgt)}" aria-label="Uyandır">${POps.iconHtml('zap')}</button>`
            + `<button type="button" class="ibtn" data-act="restart" data-tip="Yeniden başlat · ${escapeHtml(tgt)}" aria-label="Yeniden başlat">${POps.iconHtml('restart')}</button>`
            + `<button type="button" class="ibtn danger" data-act="shutdown" data-tip="Kapat · ${escapeHtml(tgt)}" aria-label="Kapat">${POps.iconHtml('power')}</button>`
            + '<span class="sep"></span>'
            + `<button type="button" class="ibtn" data-act="screen" data-tip="Ekranları izle" aria-label="Ekranları izle">${POps.iconHtml('eye')}</button>`
            + `<button type="button" class="ibtn" data-act="command" data-tip="Komut gönder" aria-label="Komut gönder">${POps.iconHtml('terminal')}</button>`
            + `<button type="button" class="ibtn" data-act="more" data-tip="Diğer işlemler" aria-label="Diğer işlemler" aria-haspopup="menu">${POps.iconHtml('more')}</button>`;
    }
    function render(force) {
        if (!state.devicesLoaded) return;
        const { rows, newest } = list();
        const sig = JSON.stringify([ui, [...ui.selected], rows.map(d => [d.hostname, d.status, d.display_name, d.current_user, d.lab, d.ip, d.is_quarantined, d.running_version, d.agent_version, d.last_seen && dev.state(d).cls === 'off' ? d.last_seen : '']), newest]);
        renderSummary();
        renderLabs();
        $('devFilter').querySelectorAll('button').forEach(b => { const on = b.dataset.f === ui.f; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        if (!force && sig === ui.hash) return;
        ui.hash = sig;
        ui.selected = new Set([...ui.selected].filter(h => (state.devices || []).some(d => d.hostname === h)));
        renderHead(rows);
        renderBar(rows);
        if (!rows.length) {
            const filteredOut = (state.devices || []).length > 0;
            POps.setEmpty(body, filteredOut
                ? { tag: 'tr', colspan: 7, icon: 'filter', title: 'Süzgece uyan cihaz yok', text: 'Arama ya da süzgeçleri değiştirin.' }
                : { tag: 'tr', colspan: 7, icon: 'devices', title: 'Henüz cihaz yok', text: 'Ajan kurulan bilgisayarlar bağlandıkça burada görünür.' });
            return;
        }
        body.innerHTML = rows.map(d => rowHtml(d, newest)).join('');
    }

    function openPc(h) {
        ui.focus = h;
        render(true);
        dev.open(h, { source: 'devices', onClose: () => { if (ui.focus === h) { ui.focus = null; render(true); } } });
    }

    body.addEventListener('click', (e) => {
        const tr = e.target.closest('tr[data-host]');
        if (!tr || e.target.closest('input, a, .mark')) return;
        if (CAN_ADMIN && (e.ctrlKey || e.metaKey)) { toggle(tr.dataset.host); return; }
        openPc(tr.dataset.host);
    });
    function toggle(h, on) {
        const want = on === undefined ? !ui.selected.has(h) : on;
        want ? ui.selected.add(h) : ui.selected.delete(h);
        render(true);
    }
    body.addEventListener('change', (e) => { if (e.target.classList.contains('dev-cb')) toggle(e.target.dataset.host, e.target.checked); });
    $('devHead').addEventListener('change', (e) => { if (e.target.id === 'devAll') { list().rows.forEach(d => e.target.checked ? ui.selected.add(d.hostname) : ui.selected.delete(d.hostname)); render(true); } });
    $('devHead').addEventListener('click', (e) => {
        const th = e.target.closest('th[data-sort]');
        if (!th) return;
        if (ui.sort === th.dataset.sort) ui.dir = -ui.dir; else { ui.sort = th.dataset.sort; ui.dir = 1; }
        render(true);
    });
    $('devFilter').addEventListener('click', (e) => { const b = e.target.closest('button[data-f]'); if (b) { ui.f = b.dataset.f; render(true); } });
    $('devSummary').addEventListener('click', (e) => { const a = e.target.closest('[data-f]'); if (a) { e.preventDefault(); ui.f = a.dataset.f; render(true); } });
    $('devLab').addEventListener('change', (e) => { ui.lab = e.target.value; render(true); });
    let qt = null;
    $('devSearch').addEventListener('input', (e) => { clearTimeout(qt); qt = setTimeout(() => { ui.q = e.target.value.trim(); render(true); }, 120); });
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && ui.selected.size && !POps.drawer.isOpen() && !document.querySelector('.pops-dialog-overlay, .pops-menu, .palette-overlay')) { ui.selected.clear(); render(true); }
    });

    bar.addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        const rows = list().rows;
        const hosts = scope(rows);
        const label = ui.selected.size ? (ui.selected.size === 1 ? dev.name(hosts[0]) : `${hosts.length} bilgisayar`) : `${hosts.length} bilgisayar`;
        const o = { btn: b, source: 'devices', scopeLabel: label };
        const onHosts = () => hosts.filter(h => !POps.isOffline(dev.find(h)));
        switch (b.dataset.act) {
            case 'clear': ui.selected.clear(); render(true); break;
            case 'wake': case 'restart': case 'shutdown': dev.power(b.dataset.act, hosts, o); break;
            case 'screen': { const on = onHosts(); if (!on.length) return POps.toast('warning', 'Açık bilgisayar yok.'); location.href = dev.screenUrl(on.slice(0, 60)); break; }
            case 'command': { const on = onHosts(); if (!on.length) return POps.toast('warning', 'Açık bilgisayar yok.'); location.href = dev.commandUrl(on.slice(0, 200)); break; }
            case 'more': {
                const anyQ = hosts.some(h => (dev.find(h) || {}).is_quarantined);
                POps.menu(b, [
                    { label: 'Mesaj gönder', icon: 'message', onClick: () => dev.message(hosts, o) },
                    { label: 'Başka sınıfa taşı…', icon: 'move', onClick: () => dev.moveMenu(b, hosts, {}) },
                    '-',
                    { label: 'Karantinaya al', icon: 'lock', danger: true, onClick: () => dev.quarantine(hosts, o) },
                    anyQ ? { label: 'Karantinayı kaldır', icon: 'unlock', onClick: () => dev.unquarantine(hosts.filter(h => (dev.find(h) || {}).is_quarantined), o) } : null
                ]);
                break;
            }
        }
    });

    $('exportBtn').addEventListener('click', () => {
        const { rows } = list();
        const cell = (v) => { const s = String(v == null ? '' : v); return /[",;\n]/.test(s) ? '"' + s.replace(/"/g, '""') + '"' : s; };
        const lines = [['Ad', 'Kimlik', 'Sınıf', 'Durum', 'Kullanıcı', 'IP', 'MAC', 'Ajan', 'Son görülme'].join(';')]
            .concat(rows.map(d => [POps.deviceName(d), d.hostname, d.lab, dev.state(d).word, dev.user(d), d.ip, d.mac, dev.version(d), d.last_seen].map(cell).join(';')));
        const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
        const a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = 'pops-cihazlar-' + new Date().toISOString().slice(0, 10) + '.csv';
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    });

    let opened = false;
    document.addEventListener('pops_data_updated', (e) => {
        if (e.detail && e.detail.error && !state.devicesLoaded) { POps.setError(body, e.detail.error, { tag: 'tr', colspan: 7 }); return; }
        render(false);
        const want = params.get('pc');
        if (want && !opened && state.devicesLoaded) { opened = true; openPc(want); }
    });
    POps.watchDevices({ inventory: true });
    if (state.devicesLoaded) render(true);
})();
</script>

<?php include 'includes/footer.php'; ?>
