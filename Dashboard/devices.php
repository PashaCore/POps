<?php include 'includes/header.php'; ?>

<style>
    .stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: var(--space-4); margin-bottom: var(--space-6); }

    .control-panel {
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: var(--radius-lg);
        padding: var(--space-4);
        margin-bottom: var(--space-5);
        display: flex;
        gap: var(--space-3);
        flex-wrap: wrap;
        align-items: center;
    }
    .search-box { flex: 1; min-width: 240px; position: relative; }
    .search-box i { position: absolute; left: 0.875rem; top: 50%; transform: translateY(-50%); color: var(--text-tertiary); pointer-events: none; }
    .search-box input { padding-left: 2.25rem; }

    .view-switcher { display: inline-flex; background: var(--bg-surface-2); border-radius: var(--radius-md); padding: 0.25rem; border: 1px solid var(--border-subtle); }
    .view-btn { padding: 0.4375rem 0.875rem; font-size: var(--text-xs); border: none; background: transparent; color: var(--text-tertiary); cursor: pointer; border-radius: var(--radius-sm); font-weight: var(--fw-semibold); display: inline-flex; align-items: center; gap: 0.375rem; transition: all 0.15s; }
    .view-btn:hover { color: var(--text-primary); }
    .view-btn.active { background: var(--bg-surface); color: var(--primary-600); box-shadow: var(--shadow-xs); }

    .table-wrapper { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); overflow: hidden; box-shadow: var(--shadow-xs); }
    .table-wrapper .data-table { font-size: var(--text-sm); }
    .table-wrapper .data-table th { background: var(--bg-surface-2); font-size: 0.6875rem; }
    .table-wrapper .data-table td { padding: 0.75rem 1rem; }

    .hw-info { font-size: var(--text-xs); color: var(--text-tertiary); display: block; margin-top: 0.25rem; }
    .device-name { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .device-host { color: var(--text-tertiary); font-size: 0.6875rem; font-family: var(--font-mono); }

    .lab-accordion { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); margin-bottom: 0.75rem; overflow: hidden; transition: border-color 0.15s, box-shadow 0.15s; }
    .lab-accordion:hover { box-shadow: var(--shadow-sm); }
    .lab-header { padding: 0.875rem 1.25rem; display: flex; justify-content: space-between; align-items: center; cursor: pointer; transition: background-color 0.15s; }
    .lab-header:hover { background: var(--bg-surface-2); }
    .lab-header.open { border-bottom: 1px solid var(--border-subtle); background: var(--bg-surface-2); }
    .lab-title { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); display: flex; align-items: center; gap: 0.625rem; }
    .lab-content { display: none; }
    .lab-content.open { display: block; }

    .action-row { display: inline-flex; gap: 0.25rem; }
    .mini-btn { width: 30px; height: 30px; padding: 0; border-radius: var(--radius-sm); border: 1px solid var(--border-subtle); background: var(--bg-surface); color: var(--text-secondary); cursor: pointer; display: inline-flex; align-items: center; justify-content: center; font-size: 0.75rem; transition: all 0.15s; }
    .mini-btn:hover { background: var(--bg-surface-2); color: var(--text-primary); border-color: var(--border-default); }
    .mini-btn.power:hover { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-border); }
    .mini-btn.reboot:hover { background: var(--warning-bg); color: var(--warning-text); border-color: var(--warning-border); }
    .mini-btn.wake:hover { background: var(--success-bg); color: var(--success-text); border-color: var(--success-border); }

    .lab-pill { display: inline-flex; align-items: center; gap: 0.25rem; padding: 0.1875rem 0.5rem; border-radius: var(--radius-sm); font-size: 0.75rem; background: var(--bg-surface-2); color: var(--text-secondary); border: 1px solid var(--border-subtle); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-server"></i> Cihaz Envanteri</h1>
        <p>Tüm donanım, ağ ve güç durumlarını profesyonel seviyede yönetin</p>
    </div>
    <div class="page-header-actions">
        <button class="btn danger" onclick="window.powerCommand('ALL', 'shutdown')"><i class="fas fa-power-off"></i> Ağı Komple Kapat</button>
        <button class="btn success" onclick="window.wakeUpCommand('ALL')"><i class="fas fa-bolt"></i> Ağı Uyandır (WOL)</button>
    </div>
</div>

<div class="stats-grid">
    <div class="stat-card">
        <div class="stat-icon"><i class="fas fa-desktop"></i></div>
        <div><div class="stat-label">Toplam Sistem</div><div class="stat-value" id="statTotal">0</div></div>
    </div>
    <div class="stat-card">
        <div class="stat-icon success"><i class="fas fa-wifi"></i></div>
        <div><div class="stat-label">Çevrimiçi</div><div class="stat-value" id="statOnline" style="color:var(--success-text);">0</div></div>
    </div>
    <div class="stat-card">
        <div class="stat-icon warning"><i class="fas fa-moon"></i></div>
        <div><div class="stat-label">Boşta</div><div class="stat-value" id="statIdle" style="color:var(--warning-text);">0</div></div>
    </div>
    <div class="stat-card">
        <div class="stat-icon danger"><i class="fas fa-power-off"></i></div>
        <div><div class="stat-label">Erişilemiyor</div><div class="stat-value" id="statOffline" style="color:var(--danger-text);">0</div></div>
    </div>
</div>

<div class="control-panel">
    <div class="search-box">
        <i class="fas fa-search"></i>
        <input type="text" id="advancedSearch" placeholder="Hostname, IP, MAC veya donanım ile ara...">
    </div>
    <div class="view-switcher">
        <button id="btnViewFlat" class="view-btn active" onclick="window.switchViewMode('flat')"><i class="fas fa-list"></i> Düz Liste</button>
        <button id="btnViewLab" class="view-btn" onclick="window.switchViewMode('lab')"><i class="fas fa-layer-group"></i> Sınıfa Göre</button>
    </div>
    <div style="display:flex;gap:0.5rem;flex-wrap:wrap;">
        <button class="btn secondary" onclick="window.bulkAction('wake')"><i class="fas fa-bolt"></i> Aç</button>
        <button class="btn secondary" onclick="window.bulkAction('restart')"><i class="fas fa-arrows-rotate"></i> Restart</button>
        <button class="btn secondary" onclick="window.bulkAction('shutdown')"><i class="fas fa-power-off"></i> Kapat</button>
        <button class="btn secondary" id="moveLabBtn"><i class="fas fa-right-left"></i> Taşı</button>
    </div>
</div>

<div id="dataContainer">
    <div style="text-align:center;padding:3rem;color:var(--text-tertiary);"><i class="fas fa-circle-notch fa-spin" style="font-size:1.5rem;"></i><div style="margin-top:0.5rem;font-size:var(--text-sm);">Sistem verileri senkronize ediliyor...</div></div>
</div>

<div id="moveLabModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-right-left" style="color:var(--primary-500);margin-right:0.5rem;"></i> Toplu Laboratuvar Transferi</div>
            <button class="modal-close" onclick="closeModal('moveLabModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <p style="color:var(--text-secondary);margin-bottom:1rem;font-size:var(--text-sm);">Seçilen <strong id="moveSelectedCount" style="color:var(--primary-600);">0</strong> cihazı yeni bir kapsama aktarın.</p>
            <label>Hedef Sınıf</label>
            <input type="text" id="newLabNameInput" placeholder="Örn: Yazilim_Lab" list="existingLabsList">
            <datalist id="existingLabsList"></datalist>
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="closeModal('moveLabModal')">İptal</button>
            <button class="btn" id="confirmMoveLabBtn"><i class="fas fa-paper-plane"></i> Transferi Başlat</button>
        </div>
    </div>
</div>

<script>
document.addEventListener('DOMContentLoaded', () => {
    let pageState = {
        devices: [],
        inventory: [],
        selectedIds: new Set(),
        searchQuery: '',
        knownGoodNames: {},
        expandedLabs: new Set(),
        viewMode: 'flat'
    };

    const container = document.getElementById('dataContainer');
    const searchInput = document.getElementById('advancedSearch');
    const apiUrl = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '';

    async function fetchDevices() {
        if (!apiUrl) return;
        try {
            const [devRes, invRes] = await Promise.all([
                fetch(apiUrl + '/api/devices'),
                fetch(apiUrl + '/api/inventory').catch(() => ({ ok: false }))
            ]);
            if (devRes.ok) {
                let rawData = await devRes.json();
                if (invRes && invRes.ok) pageState.inventory = await invRes.json();
                rawData.forEach(d => {
                    if (d.real_hostname && !d.real_hostname.startsWith('HW-')) {
                        pageState.knownGoodNames[d.hostname] = d.real_hostname;
                    }
                    const hw = pageState.inventory.find(i => i.pc_name === d.hostname);
                    if (hw) { d.ip = hw.ip_address; d.mac = hw.mac_address; d.cpu = hw.cpu; d.ram = hw.ram; d.os = hw.os_version; }
                });
                pageState.devices = rawData;
                updateTopStats();
                renderDataView();
            }
        } catch (e) { console.error('Ajan verisi alınamadı:', e); }
    }

    function updateTopStats() {
        const total = pageState.devices.length;
        const online = pageState.devices.filter(d => d.status.toLowerCase() === 'online').length;
        const idle = pageState.devices.filter(d => d.status.toLowerCase() === 'idle').length;
        const offline = total - online - idle;
        document.getElementById('statTotal').innerText = total;
        document.getElementById('statOnline').innerText = online;
        document.getElementById('statIdle').innerText = idle;
        document.getElementById('statOffline').innerText = offline;
        const uniqueLabs = [...new Set(pageState.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar');
        const datalist = document.getElementById('existingLabsList');
        if (datalist) datalist.innerHTML = uniqueLabs.map(l => `<option value="${l}">`).join('');
    }

    function renderDataView() {
        const query = pageState.searchQuery.toLowerCase();
        let filtered = pageState.devices.filter(d => {
            if (!query) return true;
            const s = `${d.hostname} ${pageState.knownGoodNames[d.hostname] || d.real_hostname} ${d.ip || ''} ${d.mac || ''} ${d.lab} ${d.cpu || ''}`.toLowerCase();
            return s.includes(query);
        });
        if (filtered.length === 0) {
            container.innerHTML = `<div class="empty-state"><i class="fas fa-search"></i><h3>Kayıt bulunamadı</h3><p>Arama kriterlerinize uygun cihaz yok.</p></div>`;
            return;
        }
        filtered.sort((a, b) => {
            const nA = pageState.knownGoodNames[a.hostname] || a.display_name || a.real_hostname || a.hostname;
            const nB = pageState.knownGoodNames[b.hostname] || b.display_name || b.real_hostname || b.hostname;
            return nA.localeCompare(nB, undefined, { numeric: true, sensitivity: 'base' });
        });
        let html = '';
        if (pageState.viewMode === 'flat') {
            const allSelected = filtered.every(d => pageState.selectedIds.has(d.hostname)) && filtered.length > 0;
            html += `<div class="table-wrapper"><table class="data-table">
                <thead><tr>
                    <th style="width:40px;text-align:center;"><input type="checkbox" ${allSelected ? 'checked' : ''} onchange="window.toggleMasterSelection(this.checked)"></th>
                    <th>Durum</th><th>Cihaz</th><th>Sınıf</th><th>Ağ (IP/MAC)</th><th>Donanım</th><th style="text-align:right;">İşlem</th>
                </tr></thead>
                <tbody>${filtered.map(d => generateTableRow(d, true)).join('')}</tbody>
            </table></div>`;
        } else {
            const grouped = {};
            filtered.forEach(d => { const l = d.lab || 'Atanmamış'; if (!grouped[l]) grouped[l] = []; grouped[l].push(d); });
            Object.keys(grouped).sort().forEach(labName => {
                const pcs = grouped[labName];
                const expanded = query.length > 0 || pageState.expandedLabs.has(labName);
                const labSelected = pcs.every(d => pageState.selectedIds.has(d.hostname)) && pcs.length > 0;
                html += `<div class="lab-accordion">
                    <div class="lab-header ${expanded ? 'open' : ''}" onclick="toggleLab('${labName}', event)">
                        <div class="lab-title">
                            <input type="checkbox" ${labSelected ? 'checked' : ''} onclick="event.stopPropagation()" onchange="toggleSelectLab('${labName}', this.checked)">
                            <i class="fas fa-network-wired" style="color:var(--text-tertiary);"></i> ${labName} <span class="lab-pill">${pcs.length} cihaz</span>
                        </div>
                        <i class="fas ${expanded ? 'fa-chevron-up' : 'fa-chevron-down'}" style="color:var(--text-tertiary);"></i>
                    </div>
                    <div class="lab-content ${expanded ? 'open' : ''}">
                        <table class="data-table" style="border-radius:0;">
                            <tbody>${pcs.map(d => generateTableRow(d, false)).join('')}</tbody>
                        </table>
                    </div>
                </div>`;
            });
        }
        if (Math.abs(container.innerHTML.length - html.length) > 50 || query !== '') container.innerHTML = html;
    }

    function generateTableRow(device, showLab) {
        const dName = device.display_name || pageState.knownGoodNames[device.hostname] || device.real_hostname || device.hostname;
        const isChecked = pageState.selectedIds.has(device.hostname) ? 'checked' : '';
        const statusLower = device.status.toLowerCase();
        const dotClass = statusLower === 'online' ? 'online' : (statusLower === 'idle' ? 'idle' : 'offline');
        const labCol = showLab ? `<td><span class="lab-pill">${device.lab || 'Belirsiz'}</span></td>` : '';
        const IS_SUPERADMIN = <?php echo (isset($_SESSION['role']) && $_SESSION['role'] === 'superadmin') ? 'true' : 'false'; ?>;
        const deleteBtn = IS_SUPERADMIN ? `<button class="mini-btn power" style="border-color:var(--danger-border); color:var(--danger-text);" title="Cihazı Sil" onclick="window.deleteDevice('${device.hostname}')"><i class="fas fa-trash"></i></button>` : '';
        
        return `<tr>
            <td style="text-align:center;"><input type="checkbox" class="dev-cb" data-id="${device.hostname}" ${isChecked} onchange="handleRowSelect(this)"></td>
            <td><span class="status-dot ${dotClass}"></span> <span style="font-size:0.6875rem;color:var(--text-tertiary);font-weight:600;text-transform:uppercase;letter-spacing:0.05em;">${device.status}</span></td>
            <td>
                <div class="device-name" style="display:flex;align-items:center;gap:5px;">
                    ${escapeHtml(dName)}
                    <button class="mini-btn" style="padding:2px 5px;font-size:10px;background:transparent;border:1px solid var(--border-subtle);color:var(--text-secondary);" title="İsmi Değiştir" onclick="window.renameDevice('${device.hostname}', '${escapeHtml(dName)}')"><i class="fas fa-edit"></i></button>
                </div>
                <div class="device-host">${device.hostname}</div>
            </td>
            ${labCol}
            <td style="font-family:var(--font-mono);font-size:var(--text-xs);">
                <div style="color:var(--info-text);">${device.ip || 'Bilinmiyor'}</div>
                <div style="color:var(--text-tertiary);font-size:0.6875rem;">${device.mac || '—'}</div>
            </td>
            <td>
                <div style="font-size:var(--text-sm);color:var(--text-primary);"><i class="fas fa-microchip" style="color:var(--text-tertiary);margin-right:0.25rem;"></i>${device.cpu || '—'}</div>
                <div class="hw-info"><i class="fas fa-memory"></i> ${device.ram || '—'} <span style="margin:0 0.25rem;color:var(--border-default);">|</span> <i class="fab fa-windows"></i> ${device.os || '—'}</div>
            </td>
            <td style="text-align:right;">
                <div class="action-row">
                    <button class="mini-btn wake" title="Uyandır" onclick="window.wakeUpCommand('PC', '${device.hostname}')"><i class="fas fa-bolt"></i></button>
                    <button class="mini-btn reboot" title="Yeniden Başlat" onclick="window.powerCommand('PC', 'restart', '${device.hostname}')"><i class="fas fa-arrows-rotate"></i></button>
                    <button class="mini-btn power" title="Kapat" onclick="window.powerCommand('PC', 'shutdown', '${device.hostname}')"><i class="fas fa-power-off"></i></button>
                    ${deleteBtn}
                </div>
            </td>
        </tr>`;
    }

    window.renameDevice = async function(hostname, currentName) {
        const newName = prompt(`${hostname} için yeni görünen isim:`, currentName);
        if (newName === null) return;
        try {
            const res = await fetch(`${apiUrl}/api/rename_device`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ pc_name: hostname, display_name: newName.trim() })
            });
            if (res.ok) {
                showToast('İsim güncellendi', 'success');
                fetchDevices();
            } else {
                showToast('İsim güncellenirken hata oluştu', 'danger');
            }
        } catch (e) {
            showToast('Bağlantı hatası', 'danger');
        }
    }
    window.deleteDevice = async function(hostname) {
        if (!confirm(`${hostname} cihazını sistemden tamamen silmek istediğinize emin misiniz?\nBu işlem geri alınamaz!`)) return;
        try {
            const res = await fetch(`${apiUrl}/api/devices/${hostname}`, { method: 'DELETE' });
            if (res.ok) {
                showToast('Cihaz başarıyla silindi.', 'success');
                fetchDevices();
            } else {
                showToast('Silme işlemi başarısız.', 'error');
            }
        } catch(e) { showToast('Sunucu hatası.', 'error'); }
    };

    window.switchViewMode = function(mode) {
        pageState.viewMode = mode;
        document.getElementById('btnViewFlat').classList.toggle('active', mode === 'flat');
        document.getElementById('btnViewLab').classList.toggle('active', mode === 'lab');
        renderDataView();
    };

    window.toggleMasterSelection = function(isChecked) {
        const query = pageState.searchQuery.toLowerCase();
        let filtered = pageState.devices.filter(d => {
            if (!query) return true;
            return `${d.hostname} ${pageState.knownGoodNames[d.hostname] || d.real_hostname} ${d.ip || ''} ${d.mac || ''} ${d.lab}`.toLowerCase().includes(query);
        });
        filtered.forEach(d => { isChecked ? pageState.selectedIds.add(d.hostname) : pageState.selectedIds.delete(d.hostname); });
        renderDataView();
    };

    window.toggleLab = function(labName, e) {
        if (e.target.tagName.toLowerCase() === 'input') return;
        if (pageState.expandedLabs.has(labName)) pageState.expandedLabs.delete(labName);
        else pageState.expandedLabs.add(labName);
        renderDataView();
    };

    window.toggleSelectLab = function(labName, isChecked) {
        document.querySelectorAll(`.dev-cb`).forEach(cb => {
            const row = cb.closest('tr');
            if (!row) return;
            if (cb.dataset.id && pageState.devices.find(d => d.hostname === cb.dataset.id && d.lab === labName)) {
                cb.checked = isChecked;
                isChecked ? pageState.selectedIds.add(cb.dataset.id) : pageState.selectedIds.delete(cb.dataset.id);
            }
        });
    };

    window.handleRowSelect = function(cb) {
        cb.checked ? pageState.selectedIds.add(cb.dataset.id) : pageState.selectedIds.delete(cb.dataset.id);
    };

    window.bulkAction = function(action) {
        if (pageState.selectedIds.size === 0) return showToast('Önce tablodan cihaz seçin.', 'warning');
        const cmd = action === 'shutdown' ? 'shutdown /s /f /t 5' : 'shutdown /r /f /t 5';
        const actionName = action === 'shutdown' ? 'KAPAT' : (action === 'wake' ? 'UYANDIR' : 'YENİDEN BAŞLAT');
        if (!confirm(`Seçili ${pageState.selectedIds.size} cihaza [${actionName}] emri gönderilecek. Onaylıyor musunuz?`)) return;
        const targets = Array.from(pageState.selectedIds);
        if (action === 'wake') {
            showToast('Uyandırma paketi ağa fırlatıldı.', 'info');
        } else {
            apiRequest('/api/deploy_orchestration', { method: 'POST', body: JSON.stringify({ target_mode: 'PC', targets, taskSequence: [{ name: `Toplu ${actionName}`, type: 'CMD', command: cmd }] }) });
            showToast('Operasyon başlatıldı.', 'success');
        }
    };

    searchInput.addEventListener('input', (e) => { pageState.searchQuery = e.target.value; renderDataView(); });

    document.getElementById('moveLabBtn').addEventListener('click', () => {
        if (pageState.selectedIds.size === 0) return showToast('Önce taşınacak cihazları seçin.', 'warning');
        document.getElementById('moveSelectedCount').innerText = pageState.selectedIds.size;
        document.getElementById('newLabNameInput').value = '';
        openModal('moveLabModal');
    });

    document.getElementById('confirmMoveLabBtn').addEventListener('click', async () => {
        const targetLab = document.getElementById('newLabNameInput').value.trim();
        if (!targetLab) return showToast('Hedef sınıf adı girin.', 'warning');
        const btn = document.getElementById('confirmMoveLabBtn');
        const origText = btn.innerHTML;
        btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> İşleniyor...'; btn.disabled = true;
        try {
            const res = await fetch(apiUrl + '/api/move_pcs', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pc_names: Array.from(pageState.selectedIds), new_lab: targetLab }) });
            if (res.ok) {
                showToast(`${pageState.selectedIds.size} cihaz ${targetLab} konumuna aktarıldı!`, 'success');
                pageState.selectedIds.clear();
                closeModal('moveLabModal');
                fetchDevices();
            } else { showToast('İşlem başarısız.', 'error'); }
        } catch (err) { showToast('Sunucu hatası.', 'error'); }
        finally { btn.innerHTML = origText; btn.disabled = false; }
    });

    fetchDevices();
    setInterval(fetchDevices, 3000);
});
</script>

<?php include 'includes/footer.php'; ?>