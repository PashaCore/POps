<?php include 'includes/header.php'; ?>

<style>
    .labs-bar { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 22px; }
    .labs-bar .search-field { flex: 0 1 260px; margin-left: auto; }
    .lab-map { display: flex; flex-direction: column; align-items: center; gap: 22px; min-height: 200px; }
    .lab-teacher { width: min(320px, 100%); display: flex; flex-direction: column; gap: 8px; align-items: stretch; }
    .lab-teacher .cap { display: none; font-size: var(--text-xs); color: #b25000; font-weight: 600; text-align: center; }
    .lab-cols { width: 100%; max-width: 1100px; display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 18px; align-items: start; }
    .lab-col { display: flex; flex-direction: column; gap: 8px; min-height: 40px; border-radius: 14px; }
    .lab-col .cap { display: none; font-size: var(--text-xs); color: var(--text-muted); text-align: center; padding: 2px 0 4px; }
    .lab-grid { width: 100%; display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; }
    .tile .crown { color: #ff9f0a; margin-right: 5px; vertical-align: -2px; }
    .no-select .tile .chk { display: none !important; }
    /* Yerleşim düzenleme */
    .is-editing .lab-col { background: rgba(0, 113, 227, 0.04); box-shadow: inset 0 0 0 1.5px rgba(0, 113, 227, 0.25); padding: 10px; min-height: 120px; }
    .is-editing .lab-col .cap, .is-editing .lab-teacher .cap { display: block; }
    .is-editing .lab-teacher { background: rgba(255, 159, 10, 0.06); box-shadow: inset 0 0 0 1.5px rgba(255, 159, 10, 0.35); border-radius: 14px; padding: 10px; }
    .is-editing .tile { cursor: grab; }
    .is-editing .tile.dragging { opacity: 0.4; }
    .is-editing .drop-over { box-shadow: inset 0 0 0 2px var(--primary-500); }
    .edit-note { display: flex; align-items: center; gap: 12px; margin-bottom: 18px; }
    .edit-note .alert { flex: 1; margin: 0; }
    @media (max-width: 900px) { .lab-cols { grid-template-columns: minmax(0, 1fr); } }
</style>

<div class="page-header">
    <div>
        <h1 id="labTitle">Sınıflar</h1>
        <div class="summary" id="labSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="editLayoutBtn" data-tip="Yerleşimi düzenle" aria-label="Yerleşimi düzenle" hidden><?php echo pops_icon('grid-edit'); ?></button>
        <button type="button" class="ibtn boxed" id="labMenuBtn" data-tip="Sınıf işlemleri" data-tip-pos="left" aria-label="Sınıf işlemleri" aria-haspopup="menu"><?php echo pops_icon('more'); ?></button>
    </div>
</div>

<div class="edit-note" id="editNote" hidden>
    <div class="alert info"><div>Bilgisayarları sürükleyip sütunlara ya da öğretmen yerine bırakın. Her bırakış hemen kaydedilir.</div></div>
    <button type="button" class="btn" id="editDoneBtn">Bitti</button>
</div>

<div class="labs-bar" id="labsBar" hidden>
    <div class="actionbar" id="labBar" role="toolbar" aria-label="Sınıf işlemleri"></div>
    <div class="search-field">
        <?php echo pops_icon('search', 'sm'); ?>
        <input type="search" id="labSearch" placeholder="Bilgisayar, kullanıcı ya da IP" aria-label="Bu sınıfta ara">
    </div>
</div>

<div id="labMap" class="lab-map"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>

<script>
(function () {
    const dev = POps.dev;
    const CAN_ADMIN = dev.canAdmin;   // izleyici yalnızca görür: işlem çubuğu, sınıf işlemleri ve yerleşim düzenleme yok
    const UNASSIGNED = dev.UNASSIGNED;
    const UNASSIGNED_KEY = '__atanmamis';
    const params = new URLSearchParams(location.search);
    let current = params.get('lab') || (function () { try { return localStorage.getItem('pops_lab') || ''; } catch (e) { return ''; } })();
    let selected = new Set();
    let focus = null;
    let editing = false;
    let query = '';
    let lastHash = '';

    const $ = (id) => document.getElementById(id);
    const map = $('labMap'), bar = $('labBar');

    function labNames() { return dev.labs(); }
    function pcsOf(lab) {
        if (lab === UNASSIGNED_KEY) return (state.devices || []).filter(d => !d.lab || d.lab === UNASSIGNED);
        return (state.devices || []).filter(d => d.lab === lab);
    }
    function pcNumber(d) { const m = POps.deviceName(d).match(/\d+$/); return m ? parseInt(m[0], 10) : 9999; }
    function teacherOf(lab, pcs) {
        const main = state.mainPcs && state.mainPcs[lab];
        return pcs.find(d => d.hostname === main) || pcs.find(d => /OGR|ANA/i.test(d.real_hostname || d.hostname)) || null;
    }
    // Kayıtlı yerleşim + yeni gelenler en kısa sütuna
    function columnsOf(lab, students) {
        let layout = {};
        try { layout = JSON.parse((state.labLayouts && state.labLayouts[lab]) || '{}') || {}; } catch (e) { layout = {}; }
        const cols = { left: [], center: [], right: [] };
        const rest = students.slice().sort((a, b) => pcNumber(a) - pcNumber(b));
        ['left', 'center', 'right'].forEach(c => (layout[c] || []).forEach(h => {
            const i = rest.findIndex(p => p.hostname === h);
            if (i !== -1) cols[c].push(rest.splice(i, 1)[0]);
        }));
        rest.forEach(p => {
            const c = ['left', 'center', 'right'].reduce((a, b) => cols[b].length < cols[a].length ? b : a, 'left');
            cols[c].push(p);
        });
        return cols;
    }
    function matches(d) {
        if (!query) return true;
        const q = query.toLocaleLowerCase('tr');
        return [POps.deviceName(d), d.hostname, d.ip, d.mac, dev.user(d)].some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
    }

    function tileHtml(d, isTeacher, newest) {
        const st = dev.state(d);
        const h = d.hostname;
        const stIcon = st.cls === 'on' ? 'monitor' : st.cls === 'idle' ? 'moon' : 'power';
        const cls = ['tile', st.cls === 'off' ? 'off' : '', isTeacher ? 'teacher' : '', selected.has(h) ? 'sel' : '', focus === h ? 'focus' : '', matches(d) ? '' : 'dim'].filter(Boolean).join(' ');
        return `<div class="${escapeHtml(cls)}" role="button" tabindex="0" data-host="${escapeHtml(h)}" draggable="${editing ? 'true' : 'false'}" aria-pressed="${selected.has(h) ? 'true' : 'false'}">
            <span class="chk" data-act="toggle" aria-hidden="true">${POps.iconHtml('check')}</span>
            <span class="tx"><span class="tn">${isTeacher ? POps.iconHtml('crown', 'sm crown') : ''}${escapeHtml(POps.deviceName(d))}</span><span class="ts">${escapeHtml(dev.subline(d))}</span></span>
            ${dev.markHtml(dev.mark(d, newest))}
            <span class="st ${escapeHtml(st.cls)}" data-tip="${escapeHtml(st.word)}" data-tip-pos="left">${POps.iconHtml(stIcon)}</span>
        </div>`;
    }

    function renderNav() {
        const box = $('navSub-labs');
        if (!box) return;
        const linksHtml = labNames().map(l => {
            const pcs = pcsOf(l);
            const on = pcs.filter(d => !POps.isOffline(d)).length;
            const warn = pcs.some(d => d.is_quarantined);
            return `<a href="labs?lab=${encodeURIComponent(l)}" data-lab="${escapeHtml(l)}" class="${l === current ? 'active' : ''}"><span>${escapeHtml(l)}</span>${warn ? '<span class="dot bad" title="Karantinada bilgisayar var"></span>' : ''}<span class="n">${Number(on)}/${pcs.length}</span></a>`;
        }).join('');
        const un = pcsOf(UNASSIGNED_KEY).length;
        const unHtml = un ? `<a href="labs?lab=${UNASSIGNED_KEY}" data-lab="${UNASSIGNED_KEY}" class="${current === UNASSIGNED_KEY ? 'active' : ''}"><span>Atanmamış</span><span class="n" style="color:#ff9f0a">${un}</span></a>` : '';
        box.innerHTML = linksHtml + unHtml;
    }

    function scopeHosts() {
        if (selected.size) return [...selected];
        return pcsOf(current).map(d => d.hostname);
    }
    function scopeLabel() {
        if (selected.size) return selected.size === 1 ? dev.name([...selected][0]) : `${selected.size} bilgisayar`;
        return current === UNASSIGNED_KEY ? 'atanmamışlar' : current;
    }

    function renderBar() {
        const n = selected.size;
        if (!CAN_ADMIN) { bar.innerHTML = `<span class="scope">${current === UNASSIGNED_KEY ? 'Atanmamış' : 'Sınıfta'} <b>${pcsOf(current).length}</b> bilgisayar</span>`; return; }
        const all = pcsOf(current).length;
        const tgt = n ? `${n} bilgisayar` : (current === UNASSIGNED_KEY ? 'atanmamışların hepsi' : 'tüm sınıf');
        const scopeHtml = n
            ? `<span class="scope sel">${n} seçili<button type="button" data-act="clear" aria-label="Seçimi temizle" data-tip="Seçimi temizle (Esc)">${POps.iconHtml('x', 'sm')}</button></span>`
            : `<span class="scope">${current === UNASSIGNED_KEY ? 'Hepsi' : 'Tüm sınıf'} <b>${all}</b></span>`;
        const moveHtml = current === UNASSIGNED_KEY ? `<span class="sep"></span><button type="button" class="btn sm" data-act="move" style="margin:0 4px">${POps.iconHtml('move', 'sm')}Sınıfa taşı</button>` : '';
        bar.innerHTML = scopeHtml + '<span class="sep"></span>'
            + `<button type="button" class="ibtn" data-act="wake" data-tip="Uyandır · ${escapeHtml(tgt)}" aria-label="Uyandır">${POps.iconHtml('zap')}</button>`
            + `<button type="button" class="ibtn" data-act="restart" data-tip="Yeniden başlat · ${escapeHtml(tgt)}" aria-label="Yeniden başlat">${POps.iconHtml('restart')}</button>`
            + `<button type="button" class="ibtn danger" data-act="shutdown" data-tip="Kapat · ${escapeHtml(tgt)}" aria-label="Kapat">${POps.iconHtml('power')}</button>`
            + '<span class="sep"></span>'
            + `<button type="button" class="ibtn" data-act="screen" data-tip="Ekranları izle · ${escapeHtml(tgt)}" aria-label="Ekranları izle">${POps.iconHtml('eye')}</button>`
            + `<button type="button" class="ibtn" data-act="command" data-tip="Komut gönder · ${escapeHtml(tgt)}" aria-label="Komut gönder">${POps.iconHtml('terminal')}</button>`
            + `<button type="button" class="ibtn" data-act="more" data-tip="Diğer işlemler" aria-label="Diğer işlemler" aria-haspopup="menu">${POps.iconHtml('more')}</button>`
            + moveHtml;
    }

    function renderSummary(pcs) {
        const on = pcs.filter(d => dev.state(d).cls === 'on').length;
        const idle = pcs.filter(d => dev.state(d).cls === 'idle').length;
        const off = pcs.length - on - idle;
        const quarantined = pcs.filter(d => d.is_quarantined).length;
        const old = pcs.filter(d => { const m = dev.mark(d); return m && m.kind === 'old'; }).length;
        const partsHtml = `<span class="sum"><span class="dot on"></span><b>${Number(on)}</b> açık</span>`
            + (idle ? `<span class="sum"><span class="dot idle"></span><b>${idle}</b> boşta</span>` : '')
            + `<span class="sum"><span class="dot off"></span><b>${off}</b> kapalı</span>`
            + (quarantined ? `<span class="sum"><span class="dot bad"></span><b>${Number(quarantined)}</b> karantinada</span>` : '')
            + (old ? `<span class="sum"><span class="dot run"></span><b>${Number(old)}</b> eski ajan</span>` : '');
        $('labSummary').innerHTML = current === UNASSIGNED_KEY
            ? `<span class="sum"><b>${pcs.length}</b> bilgisayar yeni bağlandı; bir sınıfa taşıyın.</span>`
            : partsHtml;
    }

    function render(force) {
        if (!state.devicesLoaded) return;
        const labs = labNames();
        const unCount = pcsOf(UNASSIGNED_KEY).length;
        if (current !== UNASSIGNED_KEY && !labs.includes(current)) current = labs.find(l => pcsOf(l).length) || labs[0] || (unCount ? UNASSIGNED_KEY : '');
        if (current === UNASSIGNED_KEY && !unCount) current = labs.find(l => pcsOf(l).length) || labs[0] || '';
        renderNav();
        const pcs = pcsOf(current);
        const newest = dev.newestVersion();
        const hash = JSON.stringify([current, editing, query, [...selected], focus, pcs.map(d => [d.hostname, d.status, d.display_name, d.current_user, d.is_quarantined, d.running_version, d.agent_version, d.cap_terminal_enabled, d.cap_vision_enabled]), state.mainPcs && state.mainPcs[current], state.labLayouts && state.labLayouts[current], newest]);
        if (!force && hash === lastHash) return;
        lastHash = hash;
        // seçimde artık bu sınıfta olmayanlar düşer
        selected = new Set([...selected].filter(h => pcs.some(d => d.hostname === h)));
        if (focus && !pcs.some(d => d.hostname === focus)) focus = null;

        if (!current) {
            $('labTitle').textContent = 'Sınıflar';
            $('labSummary').textContent = '';
            $('labsBar').hidden = true;
            $('editLayoutBtn').hidden = true;
            map.innerHTML = `<div class="empty-state">${POps.iconHtml('labs')}<h3>Henüz sınıf yok</h3><p>Bilgisayarları yerleştirmek için önce bir sınıf oluşturun.</p>${CAN_ADMIN ? '<button type="button" class="btn" id="firstLabBtn">Sınıf ekle</button>' : ''}</div>`;
            if (CAN_ADMIN) $('firstLabBtn').addEventListener('click', addLab);
            return;
        }
        $('labTitle').textContent = current === UNASSIGNED_KEY ? 'Atanmamış bilgisayarlar' : current;
        try { localStorage.setItem('pops_lab', current); } catch (e) { /* özel pencere */ }
        document.title = $('labTitle').textContent + ' · POps';
        $('labsBar').hidden = !pcs.length;
        $('editLayoutBtn').hidden = !CAN_ADMIN || current === UNASSIGNED_KEY || !pcs.length;
        renderSummary(pcs);
        renderBar();
        map.classList.toggle('is-selecting', selected.size > 0);
        map.classList.toggle('is-editing', editing);
        $('editNote').hidden = !editing;
        $('editLayoutBtn').setAttribute('aria-pressed', editing ? 'true' : 'false');

        if (!pcs.length) {
            map.innerHTML = current === UNASSIGNED_KEY
                ? '<div class="empty-state">' + POps.iconHtml('check') + '<h3>Atanmamış bilgisayar yok</h3></div>'
                : '<div class="empty-state">' + POps.iconHtml('devices') + '<h3>Bu sınıfta bilgisayar yok</h3><p>Atanmamış bilgisayarları ya da başka sınıftakileri buraya taşıyabilirsiniz.</p></div>';
            return;
        }
        if (current === UNASSIGNED_KEY) {
            map.innerHTML = `<div class="lab-grid">${pcs.slice().sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr')).map(d => tileHtml(d, false, newest)).join('')}</div>`;
            return;
        }
        const teacher = teacherOf(current, pcs);
        const cols = columnsOf(current, pcs.filter(d => d !== teacher));
        const teacherHtml = teacher ? tileHtml(teacher, true, newest) : (editing ? '<div class="tile empty">Öğretmen bilgisayarını buraya sürükleyin</div>' : '');
        const colHtml = (key, label) => `<div class="lab-col" data-col="${escapeHtml(key)}"><div class="cap">${escapeHtml(label)}</div>${cols[key].map(d => tileHtml(d, false, newest)).join('')}</div>`;
        map.innerHTML = (teacherHtml ? `<div class="lab-teacher" data-col="teacher"><div class="cap">Öğretmen</div>${teacherHtml}</div>` : '')
            + `<div class="lab-cols">${colHtml('left', 'Sol sütun')}${colHtml('center', 'Orta sütun')}${colHtml('right', 'Sağ sütun')}</div>`;
    }

    function select(h, mode) {
        if (mode === 'toggle') { selected.has(h) ? selected.delete(h) : selected.add(h); }
        else { selected = new Set([h]); }
        render(true);
    }
    function openPc(h) {
        focus = h;
        selected.add(h);
        render(true);
        dev.open(h, { source: 'labs', onClose: () => { if (focus === h) { focus = null; render(true); } } });
    }

    // ---- Etkileşim
    map.addEventListener('click', (e) => {
        if (editing) return;
        const t = e.target.closest('.tile[data-host]');
        if (!t) { if (e.target === map || e.target.classList.contains('lab-cols') || e.target.classList.contains('lab-col')) { selected.clear(); render(true); } return; }
        const h = t.dataset.host;
        if (CAN_ADMIN && (e.target.closest('[data-act="toggle"]') || e.ctrlKey || e.metaKey || e.shiftKey)) { select(h, 'toggle'); return; }
        selected = new Set([h]);
        openPc(h);
    });
    map.addEventListener('keydown', (e) => {
        const t = e.target.closest('.tile[data-host]');
        if (!t || editing) return;
        if (e.key === 'Enter') { e.preventDefault(); selected = new Set([t.dataset.host]); openPc(t.dataset.host); }
        else if (e.key === ' ') { e.preventDefault(); select(t.dataset.host, 'toggle'); t.focus(); }
    });
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && selected.size && !POps.drawer.isOpen() && !document.querySelector('.pops-dialog-overlay, .pops-menu, .palette-overlay')) { selected.clear(); render(true); }
        if ((e.ctrlKey || e.metaKey) && e.key === 'a' && !/INPUT|TEXTAREA|SELECT/.test(document.activeElement.tagName) && current) {
            e.preventDefault(); selected = new Set(pcsOf(current).filter(matches).map(d => d.hostname)); render(true);
        }
    });

    bar.addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        const hosts = scopeHosts();
        const o = { btn: b, source: 'labs', scopeLabel: scopeLabel(), lab: current !== UNASSIGNED_KEY ? current : null, wholeLab: !selected.size && current !== UNASSIGNED_KEY };
        const act = b.dataset.act;
        if (act === 'clear') { selected.clear(); render(true); }
        else if (act === 'wake' || act === 'restart' || act === 'shutdown') dev.power(act, hosts, o);
        else if (act === 'screen') { const on = hosts.filter(h => !POps.isOffline(dev.find(h))); if (!on.length) return POps.toast('warning', 'Açık bilgisayar yok.'); location.href = dev.screenUrl(on); }
        else if (act === 'command') { const on = hosts.filter(h => !POps.isOffline(dev.find(h))); if (!on.length) return POps.toast('warning', 'Açık bilgisayar yok.'); location.href = dev.commandUrl(on); }
        else if (act === 'move') dev.moveMenu(b, hosts, { currentLab: current === UNASSIGNED_KEY ? UNASSIGNED : current });
        else if (act === 'more') {
            const anyQ = hosts.some(h => (dev.find(h) || {}).is_quarantined);
            POps.menu(b, [
                { label: 'Mesaj gönder', icon: 'message', onClick: () => dev.message(hosts, o) },
                { label: 'Başka sınıfa taşı…', icon: 'move', onClick: () => dev.moveMenu(b, hosts, { currentLab: current === UNASSIGNED_KEY ? UNASSIGNED : current }) },
                selected.size ? null : { label: 'Tümünü seç', icon: 'check', hint: 'Ctrl A', onClick: () => { selected = new Set(pcsOf(current).filter(matches).map(d => d.hostname)); render(true); } },
                '-',
                { label: 'Karantinaya al', icon: 'lock', danger: true, onClick: () => dev.quarantine(hosts, o) },
                anyQ ? { label: 'Karantinayı kaldır', icon: 'unlock', onClick: () => dev.unquarantine(hosts.filter(h => (dev.find(h) || {}).is_quarantined), o) } : null
            ]);
        }
    });

    // ---- Sınıf işlemleri
    async function addLab() {
        const name = await POps.prompt({ title: 'Yeni sınıf', label: 'Sınıf adı', placeholder: 'Örn. Lab-4 Bilişim', maxLength: 60, confirmText: 'Oluştur' });
        if (!name || !name.trim()) return;
        if (await POps.act(null, () => POps.post('/api/create_lab', { lab_name: name.trim() }), { success: `${name.trim()} oluşturuldu.` })) {
            current = name.trim();
            await POps.loadDevices().catch(() => {});
            render(true);
        }
    }
    if (!CAN_ADMIN) { $('labMenuBtn').hidden = true; map.classList.add('no-select'); }
    $('labMenuBtn').addEventListener('click', (e) => {
        const real = current && current !== UNASSIGNED_KEY;
        POps.menu(e.currentTarget, [
            { label: 'Yeni sınıf…', icon: 'plus', onClick: addLab },
            real ? { label: 'Yeniden adlandır…', icon: 'edit', onClick: async () => {
                const name = await POps.prompt({ title: 'Sınıfı yeniden adlandır', label: 'Yeni ad', defaultValue: current, maxLength: 60, confirmText: 'Kaydet' });
                if (!name || !name.trim() || name.trim() === current) return;
                if (await POps.act(null, () => POps.post('/api/rename_lab', { old_name: current, new_name: name.trim() }), { success: 'Sınıf adı güncellendi.' })) { current = name.trim(); POps.loadDevices().catch(() => {}); }
            } } : null,
            real ? { label: 'Otomatik kayıt…', icon: 'calendar', onClick: async () => {
                const d = new Date(Date.now() + 7 * 864e5).toISOString().slice(0, 10);
                const date = await POps.prompt({ title: `Otomatik kayıt: ${current}`, message: 'Bu tarihe kadar ağa ilk kez bağlanan bilgisayarlar doğrudan bu sınıfa eklenir.', label: 'Bitiş tarihi', inputType: 'date', defaultValue: d, confirmText: 'Başlat', icon: 'calendar' });
                if (!date) return;
                POps.act(null, () => POps.post('/api/set_auto_enroll', { target_lab: current, expire_date: date }), { success: `Otomatik kayıt ${date} tarihine kadar açık.` });
            } } : null,
            real ? '-' : null,
            real ? { label: 'Sınıfı sil', icon: 'trash', danger: true, onClick: async () => {
                const n = pcsOf(current).length;
                const ok = await POps.confirm({ title: `${current} silinsin mi?`, message: n ? `İçindeki ${n} bilgisayar atanmamış bilgisayarlara taşınır.` : 'Sınıf boş.', confirmText: 'Sınıfı sil', danger: true, icon: 'trash' });
                if (!ok) return;
                if (await POps.act(null, () => POps.post('/api/delete_lab', { lab_name: current }), { success: `${current} silindi.` })) { current = ''; POps.loadDevices().catch(() => {}); }
            } } : null
        ]);
    });

    // ---- Yerleşim düzenleme (sürükle-bırak; her bırakış kaydedilir)
    function setEditing(on) { editing = on; if (on) { selected.clear(); POps.drawer.close(); } render(true); }
    $('editLayoutBtn').addEventListener('click', () => setEditing(!editing));
    $('editDoneBtn').addEventListener('click', () => setEditing(false));
    let dragHost = null;
    map.addEventListener('dragstart', (e) => {
        const t = e.target.closest('.tile[data-host]');
        if (!editing || !t) return;
        dragHost = t.dataset.host;
        t.classList.add('dragging');
        e.dataTransfer.effectAllowed = 'move';
        try { e.dataTransfer.setData('text/plain', dragHost); } catch (err) { /* eski tarayıcı */ }
    });
    map.addEventListener('dragend', () => { dragHost = null; map.querySelectorAll('.dragging, .drop-over').forEach(x => x.classList.remove('dragging', 'drop-over')); });
    map.addEventListener('dragover', (e) => {
        if (!editing || !dragHost) return;
        const zone = e.target.closest('[data-col]');
        if (!zone) return;
        e.preventDefault();
        map.querySelectorAll('.drop-over').forEach(x => { if (x !== zone) x.classList.remove('drop-over'); });
        zone.classList.add('drop-over');
    });
    map.addEventListener('drop', async (e) => {
        if (!editing || !dragHost) return;
        const zone = e.target.closest('[data-col]');
        if (!zone) return;
        e.preventDefault();
        const host = dragHost;
        dragHost = null;
        if (zone.dataset.col === 'teacher') { await dev.setTeacher(current, host); return; }
        // Bırakılan yerin sırası: imlecin altındaki ilk kutudan önce
        const tiles = [...zone.querySelectorAll('.tile[data-host]')].filter(t => t.dataset.host !== host);
        const before = tiles.find(t => { const r = t.getBoundingClientRect(); return e.clientY < r.top + r.height / 2; });
        const layout = { left: [], center: [], right: [] };
        map.querySelectorAll('.lab-col').forEach(c => { layout[c.dataset.col] = [...c.querySelectorAll('.tile[data-host]')].map(t => t.dataset.host).filter(h => h !== host); });
        const col = layout[zone.dataset.col];
        const idx = before ? col.indexOf(before.dataset.host) : col.length;
        col.splice(idx < 0 ? col.length : idx, 0, host);
        const wasTeacher = state.mainPcs && state.mainPcs[current] === host;
        state.labLayouts[current] = JSON.stringify(layout);
        if (wasTeacher) state.mainPcs[current] = null;
        render(true);
        try {
            await POps.post('/api/save_lab_layout', { lab_name: current, layout_json: JSON.stringify(layout) });
            if (wasTeacher) await POps.post('/api/set_main_pc', { lab_name: current, pc_name: host }).catch(() => {});  // aynı ad gönderilince kaldırılır
        } catch (err) { POps.toast('error', 'Yerleşim kaydedilemedi: ' + POps.errorMessage(err)); POps.loadDevices().catch(() => {}); }
    });

    // ---- Gezinme: sınıflar arasında sayfa yenilenmeden
    document.addEventListener('click', (e) => {
        const a = e.target.closest('#navSub-labs a[data-lab]');
        if (!a || e.ctrlKey || e.metaKey || e.shiftKey || e.button !== 0) return;
        e.preventDefault();
        switchLab(a.dataset.lab);
    });
    function switchLab(lab) {
        if (lab === current) return;
        current = lab;
        selected.clear(); focus = null; editing = false; query = ''; $('labSearch').value = '';
        POps.drawer.close();
        try { localStorage.setItem('pops_lab', lab); } catch (e) { /* özel pencere */ }
        history.replaceState(null, '', 'labs?lab=' + encodeURIComponent(lab));
        render(true);
    }
    window.addEventListener('popstate', () => { const l = new URLSearchParams(location.search).get('lab'); if (l) switchLab(l); });
    let qt = null;
    $('labSearch').addEventListener('input', (e) => { clearTimeout(qt); qt = setTimeout(() => { query = e.target.value.trim(); render(true); }, 120); });

    document.addEventListener('pops_data_updated', (e) => {
        if (e.detail && e.detail.error && !state.devicesLoaded) { POps.setError(map, e.detail.error); return; }
        render(false);
    });
    POps.watchDevices({ inventory: true });
    if (state.devicesLoaded) render(true);
})();
</script>

<?php include 'includes/footer.php'; ?>
