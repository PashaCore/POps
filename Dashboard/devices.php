<?php include 'includes/header.php'; ?>
<?php
$devRole = $_SESSION['role'] ?? '';
$devCanAdmin = in_array($devRole, ['admin', 'superadmin'], true);
$devIsSuper = $devRole === 'superadmin';
?>

<style>
    .dev-name { display: flex; align-items: center; gap: 0.375rem; }
    .dev-name .btn.icon.sm { width: 24px; min-height: 24px; opacity: 0; transition: opacity 0.15s; }
    .data-table tbody tr:hover .dev-name .btn.icon.sm, .dev-name .btn.icon.sm:focus-visible { opacity: 1; }
    .net-cell { font-family: var(--font-mono); font-size: var(--text-xs); }
    .net-cell .ip { color: var(--info-text); }
    .net-cell .mac { color: var(--text-tertiary); font-size: 0.6875rem; }
    .hw-cell .hw-sub { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.125rem; }
    .row-actions .btn.icon.sm.wake:hover:not(:disabled) { background: var(--success-bg); color: var(--success-text); border-color: var(--success-border); }
    .row-actions .btn.icon.sm.reboot:hover:not(:disabled) { background: var(--warning-bg); color: var(--warning-text); border-color: var(--warning-border); }
    .row-actions .btn.icon.sm.power:hover:not(:disabled), .row-actions .btn.icon.sm.del:hover:not(:disabled) { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-border); }

    .lab-group { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); margin-bottom: 0.75rem; overflow: hidden; box-shadow: var(--shadow-xs); }
    .lab-group-head { width: 100%; padding: 0.75rem 1.125rem; display: flex; justify-content: space-between; align-items: center; gap: 0.75rem; background: transparent; border: none; cursor: pointer; text-align: left; font: inherit; color: var(--text-primary); }
    .lab-group-head:hover { background: var(--bg-surface-2); }
    .lab-group-head:focus-visible { outline: none; box-shadow: inset var(--focus-ring); }
    .lab-group.open .lab-group-head { border-bottom: 1px solid var(--border-subtle); background: var(--bg-surface-2); }
    .lab-group-title { display: flex; align-items: center; gap: 0.625rem; font-weight: var(--fw-semibold); font-size: var(--text-sm); }
    .lab-group-body { display: none; overflow-x: auto; }
    .lab-group.open .lab-group-body { display: block; }
    .lab-group .data-table { border-radius: 0; }
    .selection-bar { font-size: var(--text-sm); color: var(--text-secondary); }
    .selection-bar strong { color: var(--primary-600); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-server"></i> Cihaz Envanteri</h1>
        <p>Bilgisayarların durumu, ağ bilgileri ve güç işlemleri</p>
    </div>
    <?php if ($devCanAdmin): ?>
    <div class="page-header-actions">
        <button type="button" class="btn success-soft" data-action="wake-all"><i class="fas fa-bolt"></i> Ağı uyandır</button>
        <button type="button" class="btn danger-soft" data-action="shutdown-all"><i class="fas fa-power-off"></i> Ağı kapat</button>
    </div>
    <?php endif; ?>
</div>

<div class="stat-grid">
    <div class="stat-card"><div class="stat-icon"><i class="fas fa-desktop"></i></div><div><div class="stat-label">Toplam</div><div class="stat-value" id="statTotal">–</div></div></div>
    <div class="stat-card"><div class="stat-icon success"><i class="fas fa-wifi"></i></div><div><div class="stat-label">Çevrimiçi</div><div class="stat-value" id="statOnline">–</div></div></div>
    <div class="stat-card"><div class="stat-icon warning"><i class="fas fa-moon"></i></div><div><div class="stat-label">Boşta</div><div class="stat-value" id="statIdle">–</div></div></div>
    <div class="stat-card"><div class="stat-icon muted"><i class="fas fa-power-off"></i></div><div><div class="stat-label">Erişilemiyor</div><div class="stat-value" id="statOffline">–</div></div></div>
</div>

<div class="toolbar">
    <div class="search-field">
        <i class="fas fa-search" aria-hidden="true"></i>
        <input type="search" id="advancedSearch" placeholder="Ad, IP, MAC, sınıf ya da işlemci ile ara" aria-label="Cihaz ara">
    </div>
    <div class="segmented" role="group" aria-label="Görünüm">
        <button type="button" data-view="flat" class="active" aria-pressed="true"><i class="fas fa-list"></i> Düz liste</button>
        <button type="button" data-view="lab" aria-pressed="false"><i class="fas fa-layer-group"></i> Sınıfa göre</button>
    </div>
    <?php if ($devCanAdmin): ?>
    <div class="spacer"></div>
    <div class="toolbar-group">
        <span class="selection-bar" id="selectionBar">Seçili: <strong id="selectedCount">0</strong></span>
        <button type="button" class="btn secondary sm" data-action="bulk-wake"><i class="fas fa-bolt"></i> Uyandır</button>
        <button type="button" class="btn secondary sm" data-action="bulk-restart"><i class="fas fa-arrows-rotate"></i> Yeniden başlat</button>
        <button type="button" class="btn secondary sm" data-action="bulk-shutdown"><i class="fas fa-power-off"></i> Kapat</button>
        <button type="button" class="btn secondary sm" data-action="bulk-move"><i class="fas fa-right-left"></i> Sınıfa taşı</button>
    </div>
    <?php endif; ?>
</div>

<div id="dataContainer"></div>

<?php if ($devCanAdmin): ?>
<div id="moveLabModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="moveLabTitle">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="moveLabTitle"><i class="fas fa-right-left"></i> Cihazları başka sınıfa taşı</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <p class="text-secondary text-sm mb-2">Seçili <strong id="moveSelectedCount">0</strong> cihaz seçtiğiniz sınıfa taşınacak.</p>
            <div class="field">
                <label for="newLabNameInput">Hedef sınıf</label>
                <input type="text" id="newLabNameInput" placeholder="Örn: Yazilim_Lab" list="existingLabsList" autocomplete="off">
                <datalist id="existingLabsList"></datalist>
                <div class="field-hint">Listede olmayan bir ad yazarsanız yeni sınıf oluşur.</div>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="confirmMoveLabBtn"><i class="fas fa-check"></i> Taşı</button>
        </div>
    </div>
</div>
<?php endif; ?>

<script>
document.addEventListener('DOMContentLoaded', () => {
    const CAN_ADMIN = <?php echo $devCanAdmin ? 'true' : 'false'; ?>;
    const IS_SUPERADMIN = <?php echo $devIsSuper ? 'true' : 'false'; ?>;
    const page = { devices: [], inventory: {}, selected: new Set(), query: '', view: 'flat', expanded: new Set(), lastHtml: '', loaded: false };
    const container = document.getElementById('dataContainer');
    POps.setLoading(container, 'Cihazlar yükleniyor…');

    const nameOf = (d) => d.display_name || (d.real_hostname && !String(d.real_hostname).startsWith('HW-') ? d.real_hostname : '') || d.hostname;
    const statusOf = (d) => String(d.status || 'Offline').toLowerCase();

    async function fetchDevices() {
        try {
            const [devices, inv] = await Promise.all([
                POps.get('/api/devices'),
                POps.get('/api/inventory').catch(() => null)
            ]);
            if (Array.isArray(inv)) { page.inventory = {}; inv.forEach(r => { page.inventory[r.pc_name] = r; }); }
            page.devices = (Array.isArray(devices) ? devices : []).map(d => {
                const hw = page.inventory[d.hostname] || {};
                return Object.assign({}, d, { ip: hw.ip_address || d.ip_address || '', mac: hw.mac_address && hw.mac_address !== '-' ? hw.mac_address : '', cpu: hw.cpu, ram: hw.ram, os: hw.os_version });
            });
            // Silinen ya da kaybolan cihaz seçimde kalmasın
            const known = new Set(page.devices.map(d => d.hostname));
            page.selected.forEach(id => { if (!known.has(id)) page.selected.delete(id); });
            page.loaded = true;
            updateStats();
            render();
        } catch (e) {
            if (!page.loaded) POps.setError(container, e);
        }
    }

    function updateStats() {
        const total = page.devices.length;
        const online = page.devices.filter(d => statusOf(d) === 'online').length;
        const idle = page.devices.filter(d => statusOf(d) === 'idle').length;
        document.getElementById('statTotal').textContent = total;
        document.getElementById('statOnline').textContent = online;
        document.getElementById('statIdle').textContent = idle;
        document.getElementById('statOffline').textContent = total - online - idle;
        const datalist = document.getElementById('existingLabsList');
        if (datalist) {
            const labs = [...new Set(page.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar').sort();
            datalist.replaceChildren(...labs.map(l => POps.el('option', { value: l })));
        }
    }

    function filtered() {
        const q = page.query.trim().toLowerCase();
        return page.devices.filter(d => !q || [nameOf(d), d.hostname, d.ip, d.mac, d.lab, d.cpu].join(' ').toLowerCase().includes(q))
            .sort((a, b) => nameOf(a).localeCompare(nameOf(b), 'tr', { numeric: true, sensitivity: 'base' }));
    }

    function row(d, showLab) {
        const st = statusOf(d);
        const cls = st === 'online' ? 'online' : (st === 'idle' ? 'idle' : 'offline');
        const label = st === 'online' ? 'Çevrimiçi' : (st === 'idle' ? 'Boşta' : 'Çevrimdışı');
        const id = escapeHtml(d.hostname);
        const checked = page.selected.has(d.hostname);
        const actions = CAN_ADMIN ? `
            <button type="button" class="btn ghost icon sm wake" data-action="wake" data-id="${id}" title="Uyandır" aria-label="Uyandır"><i class="fas fa-bolt"></i></button>
            <button type="button" class="btn ghost icon sm reboot" data-action="restart" data-id="${id}" title="Yeniden başlat" aria-label="Yeniden başlat"><i class="fas fa-arrows-rotate"></i></button>
            <button type="button" class="btn ghost icon sm power" data-action="shutdown" data-id="${id}" title="Kapat" aria-label="Kapat"><i class="fas fa-power-off"></i></button>
            <button type="button" class="btn ghost icon sm" data-action="bypass" data-id="${id}" title="Çevrimdışı bypass kodu" aria-label="Çevrimdışı bypass kodu"><i class="fas fa-key"></i></button>
            ${IS_SUPERADMIN ? `<button type="button" class="btn ghost icon sm del" data-action="delete" data-id="${id}" title="Cihazı sil" aria-label="Cihazı sil"><i class="fas fa-trash"></i></button>` : ''}` : '';
        return `<tr class="${checked ? 'is-selected' : ''}">
            ${CAN_ADMIN ? `<td class="check-col"><input type="checkbox" class="dev-cb" data-id="${id}" ${checked ? 'checked' : ''} aria-label="Seç"></td>` : ''}
            <td><span class="status-pill ${cls}"><span class="status-dot ${cls}"></span>${label}</span></td>
            <td>
                <div class="dev-name"><span class="cell-title">${escapeHtml(nameOf(d))}</span>
                    ${CAN_ADMIN ? `<button type="button" class="btn ghost icon sm" data-action="rename" data-id="${id}" title="Adını değiştir" aria-label="Adını değiştir"><i class="fas fa-pen"></i></button>` : ''}</div>
                <div class="cell-sub mono">${id}</div>
            </td>
            ${showLab ? `<td><span class="badge muted">${escapeHtml(d.lab === 'Atanmamis_Cihazlar' ? 'Atanmamış' : (d.lab || 'Atanmamış'))}</span></td>` : ''}
            <td class="net-cell"><div class="ip">${escapeHtml(d.ip || 'Bilinmiyor')}</div><div class="mac">${escapeHtml(d.mac || '—')}</div></td>
            <td class="hw-cell"><div>${escapeHtml(d.cpu || '—')}</div><div class="hw-sub">${escapeHtml(d.ram || '—')} · ${escapeHtml(d.os || '—')}</div></td>
            <td class="actions"><div class="row-actions">${actions}</div></td>
        </tr>`;
    }

    function head(showLab, allChecked) {
        return `<thead><tr>
            ${CAN_ADMIN ? `<th class="check-col"><input type="checkbox" id="masterCb" ${allChecked ? 'checked' : ''} aria-label="Hepsini seç"></th>` : ''}
            <th>Durum</th><th>Cihaz</th>${showLab ? '<th>Sınıf</th>' : ''}<th>Ağ (IP / MAC)</th><th>Donanım</th><th class="actions">İşlem</th>
        </tr></thead>`;
    }

    function render() {
        const list = filtered();
        const sel = document.getElementById('selectedCount');
        if (sel) sel.textContent = page.selected.size;
        if (!list.length) {
            page.lastHtml = '';
            POps.setEmpty(container, page.devices.length
                ? { icon: 'fa-search', title: 'Eşleşen cihaz yok', text: 'Arama ifadesini değiştirin.' }
                : { icon: 'fa-desktop', title: 'Henüz cihaz yok', text: 'Ajan kurulan bilgisayarlar burada görünür.' });
            return;
        }
        let html;
        if (page.view === 'flat') {
            const all = list.every(d => page.selected.has(d.hostname));
            html = `<div class="table-wrap"><table class="data-table wide">${head(true, all)}<tbody>${list.map(d => row(d, true)).join('')}</tbody></table></div>`;
        } else {
            const groups = {};
            list.forEach(d => { const l = d.lab || 'Atanmamis_Cihazlar'; (groups[l] = groups[l] || []).push(d); });
            html = Object.keys(groups).sort((a, b) => a.localeCompare(b, 'tr')).map(lab => {
                const pcs = groups[lab];
                const open = page.query.trim() !== '' || page.expanded.has(lab);
                const online = pcs.filter(d => statusOf(d) === 'online').length;
                const all = pcs.every(d => page.selected.has(d.hostname));
                return `<div class="lab-group ${open ? 'open' : ''}">
                    <button type="button" class="lab-group-head" data-action="toggle-lab" data-lab="${escapeHtml(lab)}" aria-expanded="${open}">
                        <span class="lab-group-title"><i class="fas fa-network-wired text-muted"></i>${escapeHtml(lab === 'Atanmamis_Cihazlar' ? 'Atanmamış cihazlar' : lab)}
                            <span class="badge muted">${pcs.length} cihaz</span><span class="badge success">${online} açık</span></span>
                        <i class="fas fa-chevron-${open ? 'up' : 'down'} text-muted"></i>
                    </button>
                    <div class="lab-group-body">${CAN_ADMIN ? `<div class="toolbar" style="margin:0;border:0;border-radius:0;border-bottom:1px solid var(--border-subtle);box-shadow:none;">
                        <label class="check"><input type="checkbox" class="lab-cb" data-lab="${escapeHtml(lab)}" ${all ? 'checked' : ''}> Bu sınıftakileri seç</label></div>` : ''}
                        <table class="data-table wide">${head(false, all).replace('id="masterCb"', 'class="lab-master" data-lab="' + escapeHtml(lab) + '"')}<tbody>${pcs.map(d => row(d, false)).join('')}</tbody></table></div>
                </div>`;
            }).join('');
        }
        // Yoklamada değişmeyen tablo yeniden çizilmez (odak ve kaydırma korunur); değişince her zaman çizilir
        if (html !== page.lastHtml) {
            page.lastHtml = html;
            container.innerHTML = html;
        }
    }

    function selectIds(ids, on) {
        ids.forEach(id => on ? page.selected.add(id) : page.selected.delete(id));
        render();
    }

    async function runPower(targets, action, btn) {
        const shutdown = action === 'shutdown';
        const ok = await POps.confirm({
            title: shutdown ? 'Cihazlar kapatılsın mı?' : 'Cihazlar yeniden başlatılsın mı?',
            message: `${targets.length} cihaz 5 saniye içinde ${shutdown ? 'kapatılacak' : 'yeniden başlatılacak'}. Kaydedilmemiş işler kaybolabilir.`,
            confirmText: shutdown ? 'Kapat' : 'Yeniden başlat', danger: true, icon: 'fa-power-off'
        });
        if (!ok) return;
        await POps.act(btn, () => POps.post('/api/deploy_orchestration', {
            target_mode: 'PC', targets,
            taskSequence: [{ name: shutdown ? 'Güç: kapat' : 'Güç: yeniden başlat', type: 'CMD', command: shutdown ? 'shutdown /s /f /t 5' : 'shutdown /r /f /t 5' }]
        }), { success: (r) => `Komut kuyruğa eklendi (${(r && r.created) || 0} cihaz)` + (r && r.skipped_module_closed ? `; ${r.skipped_module_closed} cihazda uzak komut kapalı.` : '.') });
    }

    async function wakeMany(targets, btn) {
        await POps.busy(btn, async () => {
            const results = await Promise.allSettled(targets.map(pc => POps.post('/api/wake_pc/' + encodeURIComponent(pc))));
            const ok = results.filter(r => r.status === 'fulfilled').length;
            const firstErr = results.find(r => r.status === 'rejected');
            if (ok === targets.length) POps.toast('success', `${ok} cihaza uyandırma sinyali gönderildi.`);
            else POps.toast(ok ? 'warning' : 'error', `${ok}/${targets.length} cihaza gönderilebildi.` + (firstErr ? ' ' + POps.errorMessage(firstErr.reason) : ''));
        });
    }

    async function rename(id, btn) {
        const d = page.devices.find(x => x.hostname === id) || { hostname: id };
        const name = await POps.prompt({
            title: 'Cihazın adını değiştir', message: `${id} panelde bu adla görünür. Boş bırakırsanız bilgisayarın kendi adı kullanılır.`,
            label: 'Görünen ad', defaultValue: d.display_name || nameOf(d), maxLength: 100, confirmText: 'Kaydet'
        });
        if (name === null) return;
        if (await POps.act(btn, () => POps.post('/api/rename_device', { pc_name: id, display_name: name.trim() }), { success: 'Ad güncellendi.' })) fetchDevices();
    }

    async function remove(id, btn) {
        const ok = await POps.confirm({
            title: 'Cihaz silinsin mi?', danger: true, confirmText: 'Sil',
            message: `${id} bütün kayıtlarıyla (görevler, envanter, anahtar) silinecek. Bilgisayar yeniden bağlanırsa yeniden kayıt gerekir. Bu işlem geri alınamaz.`
        });
        if (!ok) return;
        if (await POps.act(btn, () => POps.del('/api/devices/' + encodeURIComponent(id)), { success: 'Cihaz silindi.' })) fetchDevices();
    }

    async function bypass(id, btn) {
        let data;
        try { data = await POps.busy(btn, () => POps.post('/api/security/bypass_token/' + encodeURIComponent(id))); }
        catch (e) { POps.toast('error', POps.errorMessage(e)); return; }
        if (!data) return;
        const codes = [data.token];
        if (data.fallback_token) codes.push(data.fallback_token);
        await POps.alert({
            title: 'Çevrimdışı bypass kodu', icon: 'fa-key', codes,
            message: `${id} · geçerli: ${data.valid_for}` + (data.n ? ` · bugünün ${data.n + 1}. kodu` : ''),
            note: (data.fallback_token ? 'İlk kod kabul edilmezse ikincisini deneyin (cihaz anahtarı henüz onaylanmadı). ' : '')
                + 'Kullanıcı kodu tepsi simgesi → "Yönetici Müdahalesi (Bypass)" menüsüne girer. Her kod bir kez geçerlidir; yeni kod için yeniden isteyin.'
        });
    }

    container.addEventListener('change', (e) => {
        const t = e.target;
        if (t.classList.contains('dev-cb')) selectIds([t.dataset.id], t.checked);
        else if (t.id === 'masterCb') selectIds(filtered().map(d => d.hostname), t.checked);
        else if (t.classList.contains('lab-cb') || t.classList.contains('lab-master')) selectIds(filtered().filter(d => (d.lab || 'Atanmamis_Cihazlar') === t.dataset.lab).map(d => d.hostname), t.checked);
    });

    document.addEventListener('click', (e) => {
        const b = e.target.closest('[data-action], [data-view]');
        if (!b || !document.querySelector('.app-content').contains(b)) return;
        if (b.dataset.view) {
            page.view = b.dataset.view;
            document.querySelectorAll('[data-view]').forEach(x => { const on = x.dataset.view === page.view; x.classList.toggle('active', on); x.setAttribute('aria-pressed', on); });
            render();
            return;
        }
        const id = b.dataset.id;
        const sel = Array.from(page.selected);
        const needSel = () => { if (!sel.length) { POps.toast('warning', 'Önce tablodan cihaz seçin.'); return false; } return true; };
        switch (b.dataset.action) {
            case 'toggle-lab':
                page.expanded.has(b.dataset.lab) ? page.expanded.delete(b.dataset.lab) : page.expanded.add(b.dataset.lab);
                render();
                break;
            case 'wake': window.wakeUpCommand('PC', id, b); break;
            case 'restart': runPower([id], 'restart', b); break;
            case 'shutdown': runPower([id], 'shutdown', b); break;
            case 'rename': rename(id, b); break;
            case 'delete': remove(id, b); break;
            case 'bypass': bypass(id, b); break;
            case 'wake-all': window.wakeUpCommand('ALL', null, b); break;
            case 'shutdown-all': window.powerCommand('ALL', 'shutdown', null, b); break;
            case 'bulk-wake': if (needSel()) wakeMany(sel, b); break;
            case 'bulk-restart': if (needSel()) runPower(sel, 'restart', b); break;
            case 'bulk-shutdown': if (needSel()) runPower(sel, 'shutdown', b); break;
            case 'bulk-move':
                if (!needSel()) break;
                document.getElementById('moveSelectedCount').textContent = sel.length;
                document.getElementById('newLabNameInput').value = '';
                openModal('moveLabModal');
                setTimeout(() => document.getElementById('newLabNameInput').focus(), 50);
                break;
        }
    });

    const moveBtn = document.getElementById('confirmMoveLabBtn');
    if (moveBtn) {
        const doMove = async () => {
            const lab = document.getElementById('newLabNameInput').value.trim();
            if (!lab) { POps.toast('warning', 'Hedef sınıfın adını yazın.'); return; }
            const ids = Array.from(page.selected);
            const ok = await POps.act(moveBtn, () => POps.post('/api/move_pcs', { pc_names: ids, new_lab: lab }), { success: `${ids.length} cihaz "${lab}" sınıfına taşındı.` });
            if (ok) { page.selected.clear(); closeModal('moveLabModal'); fetchDevices(); }
        };
        moveBtn.addEventListener('click', doMove);
        document.getElementById('newLabNameInput').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); doMove(); } });
    }

    document.getElementById('advancedSearch').addEventListener('input', (e) => { page.query = e.target.value; render(); });

    fetchDevices();
    popsPoll(fetchDevices, 5000);
});
</script>

<?php include 'includes/footer.php'; ?>
