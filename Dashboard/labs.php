<?php include 'includes/header.php'; ?>

<style>
    .lab-toolbar {
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

    .unassigned-zone {
        background: var(--bg-surface);
        border: 1px solid var(--border-default);
        border-radius: var(--radius-lg);
        padding: var(--space-5);
        margin-bottom: var(--space-5);
    }
    .unassigned-header {
        color: var(--danger-text);
        font-weight: var(--fw-semibold);
        font-size: var(--text-md);
        margin-bottom: var(--space-4);
        display: flex;
        align-items: center;
        gap: 0.5rem;
    }
    .master-cb-label {
        margin-left: auto;
        display: inline-flex;
        align-items: center;
        gap: 0.5rem;
        cursor: pointer;
        font-size: var(--text-sm);
        padding: 0.375rem 0.75rem;
        border-radius: var(--radius-md);
        border: 1px solid var(--danger-border);
        background: var(--danger-bg);
        color: var(--danger-text);
    }
    .unassigned-grid {
        display: grid;
        grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
        gap: 0.5rem;
        margin-bottom: var(--space-4);
        max-height: 240px;
        overflow-y: auto;
        padding-right: 0.5rem;
    }
    .unassigned-pc {
        background: var(--bg-surface-2);
        border: 1px solid var(--border-subtle);
        padding: 0.625rem 0.875rem;
        border-radius: var(--radius-md);
        display: flex;
        align-items: center;
        gap: 0.5rem;
        cursor: pointer;
        transition: border-color 0.15s, background-color 0.15s;
    }
    .unassigned-pc:hover { border-color: var(--primary-500); background: var(--bg-surface); }
    .unassigned-pc .pc-name { font-weight: var(--fw-semibold); font-size: var(--text-sm); color: var(--text-primary); }
    .unassigned-pc .pc-meta { font-size: 0.6875rem; color: var(--text-tertiary); font-family: var(--font-mono); }
    .unassigned-actions { display: flex; gap: 0.75rem; align-items: flex-end; flex-wrap: wrap; }

    .lab-wrapper {
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: var(--radius-lg);
        margin-bottom: var(--space-4);
        overflow: hidden;
        box-shadow: var(--shadow-xs);
        transition: box-shadow 0.15s;
    }
    .lab-wrapper:hover { box-shadow: var(--shadow-sm); }
    .lab-summary {
        padding: var(--space-4) var(--space-5);
        display: flex;
        justify-content: space-between;
        align-items: center;
        cursor: pointer;
        background: var(--bg-surface);
        transition: background-color 0.15s;
        user-select: none;
    }
    .lab-summary:hover { background: var(--bg-surface-2); }
    .lab-summary.expanded { border-bottom: 1px solid var(--border-subtle); }
    .lab-title-area { display: flex; align-items: center; gap: 0.875rem; }
    .lab-icon { font-size: 1.5rem; color: var(--primary-500); }
    .lab-name { font-size: var(--text-lg); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .lab-stats { color: var(--text-tertiary); font-size: var(--text-sm); display: flex; gap: 1rem; align-items: center; }
    .lab-stats .active-count { color: var(--success-text); font-weight: var(--fw-semibold); }
    .lab-toggle-icon { color: var(--text-tertiary); transition: transform 0.2s; }
    .lab-wrapper.expanded .lab-toggle-icon { transform: rotate(180deg); color: var(--primary-500); }

    .lab-content { display: none; flex-direction: column; background: var(--bg-surface-2); }
    .lab-wrapper.expanded .lab-content { display: flex; }
    .lab-inner-tabs {
        display: flex;
        border-bottom: 1px solid var(--border-subtle);
        background: var(--bg-surface);
        padding: 0 var(--space-4);
    }
    .inner-tab-btn {
        flex: 1;
        padding: 0.75rem 1rem;
        background: transparent;
        border: none;
        color: var(--text-tertiary);
        font-weight: var(--fw-semibold);
        cursor: pointer;
        font-size: var(--text-sm);
        border-bottom: 2px solid transparent;
        transition: color 0.15s, border-color 0.15s, background-color 0.15s;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 0.5rem;
        max-width: 280px;
    }
    .inner-tab-btn:hover { color: var(--text-primary); background: var(--bg-surface-2); }
    .inner-tab-btn.active { color: var(--primary-600); border-bottom-color: var(--primary-500); }

    .tab-content-map { padding: var(--space-6); display: flex; flex-direction: column; align-items: center; gap: var(--space-4); }
    .tab-content-manage { padding: var(--space-5); display: none; flex-direction: column; gap: var(--space-5); }
    .tab-content-manage.active { display: flex; }

    .power-controls { display: flex; gap: 0.5rem; background: var(--bg-surface); padding: 0.625rem 1rem; border-radius: var(--radius-md); border: 1px solid var(--border-subtle); align-items: center; flex-wrap: wrap; }
    .power-btn { padding: 0.4375rem 0.875rem; border-radius: var(--radius-sm); border: 1px solid; font-weight: var(--fw-semibold); cursor: pointer; font-size: var(--text-xs); display: inline-flex; align-items: center; gap: 0.375rem; transition: all 0.15s; background: transparent; }
    .power-btn.wake { color: var(--success-text); border-color: var(--success-border); }
    .power-btn.wake:hover { background: var(--success-solid); color: white; border-color: var(--success-solid); }
    .power-btn.shutdown { color: var(--danger-text); border-color: var(--danger-border); }
    .power-btn.shutdown:hover { background: var(--danger-solid); color: white; border-color: var(--danger-solid); }
    .power-btn.restart { color: var(--warning-text); border-color: var(--warning-border); }
    .power-btn.restart:hover { background: var(--warning-solid); color: white; border-color: var(--warning-solid); }

    .branches-wrapper {
        display: flex;
        width: 100%;
        max-width: 1100px;
        gap: var(--space-3);
        position: relative;
    }
    .pc-column {
        flex: 1;
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: var(--radius-md);
        padding: var(--space-3);
        display: flex;
        flex-direction: column;
        gap: 0.5rem;
        min-height: 200px;
    }
    .col-title { text-align: center; color: var(--text-tertiary); font-size: 0.6875rem; font-weight: var(--fw-semibold); text-transform: uppercase; letter-spacing: 0.06em; padding-bottom: 0.5rem; border-bottom: 1px solid var(--border-subtle); margin-bottom: 0.25rem; }

    .pc-desk {
        background: var(--bg-surface-2);
        border-radius: var(--radius-sm);
        padding: 0.5rem 0.75rem;
        border-left: 3px solid var(--text-muted);
        display: flex;
        justify-content: space-between;
        align-items: center;
        position: relative;
        overflow: hidden;
        transition: all 0.15s;
        min-height: 40px;
    }
    .pc-desk.online { border-left-color: var(--success-solid); }
    .pc-desk.offline { border-left-color: var(--danger-solid); opacity: 0.55; }
    .pc-desk.idle { border-left-color: var(--warning-solid); }
    .pc-desk.main-pc { background: var(--warning-bg); border-left: 3px solid var(--warning-solid); padding: 0.75rem; }
    .pc-desk.main-pc .pc-name { color: var(--text-primary); font-weight: var(--fw-bold); }
    .pc-name { font-weight: var(--fw-semibold); font-size: var(--text-sm); color: var(--text-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .pc-desk .pc-host { font-size: 0.6875rem; color: var(--text-tertiary); font-family: var(--font-mono); margin-top: 0.125rem; }
    .pc-desk .pc-icon-main { color: var(--text-tertiary); font-size: 0.875rem; transition: opacity 0.15s; }
    .pc-desk.online .pc-icon-main { color: var(--success-solid); }
    .pc-desk.idle .pc-icon-main { color: var(--warning-solid); }
    .pc-desk.offline .pc-icon-main { color: var(--danger-solid); }
    .pc-desk.main-pc .pc-icon-main { color: var(--warning-solid); font-size: 1.125rem; }

    .pc-desk-actions {
        position: absolute;
        right: 0; top: 0; bottom: 0;
        display: flex;
        background: rgba(15, 23, 42, 0.92);
        align-items: center;
        padding: 0 0.5rem;
        gap: 0.25rem;
        z-index: 5;
        transform: translateX(101%);
        transition: transform 0.2s ease;
    }
    .pc-desk:hover .pc-desk-actions { transform: translateX(0); }
    [data-theme="dark"] .pc-desk-actions { background: rgba(0, 0, 0, 0.92); }

    .desk-btn { width: 26px; height: 26px; border-radius: var(--radius-sm); border: none; color: white; font-size: 0.75rem; cursor: pointer; display: inline-flex; align-items: center; justify-content: center; transition: filter 0.15s; }
    .desk-btn:hover { filter: brightness(1.2); }
    .desk-btn.wake { background: var(--success-solid); }
    .desk-btn.res { background: var(--warning-solid); }
    .desk-btn.shut { background: var(--danger-solid); }

    .manage-section { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-5); }
    .manage-title { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin-bottom: var(--space-4); display: flex; align-items: center; gap: 0.5rem; }
    .manage-title i { color: var(--primary-500); }

    .action-btn { padding: 0.375rem 0.75rem; border-radius: var(--radius-sm); font-size: var(--text-xs); font-weight: var(--fw-semibold); cursor: pointer; border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); transition: all 0.15s; display: inline-flex; align-items: center; gap: 0.375rem; }
    .action-btn:hover { background: var(--bg-surface); border-color: var(--border-strong); }
    .action-btn.crown { color: var(--warning-text); border-color: var(--warning-border); background: var(--warning-bg); }
    .action-btn.crown:hover { background: var(--warning-solid); color: white; }
    .action-btn.eject { color: var(--danger-text); border-color: var(--danger-border); background: var(--danger-bg); }
    .action-btn.eject:hover { background: var(--danger-solid); color: white; }

    .skeleton-card { height: 80px; background: var(--bg-surface-2); border-radius: var(--radius-lg); border: 1px solid var(--border-subtle); margin-bottom: 0.75rem; position: relative; overflow: hidden; }
    .skeleton-card::after { content: ""; position: absolute; inset: 0; background: linear-gradient(90deg, transparent, rgba(255,255,255,0.05), transparent); animation: shimmer 1.5s infinite; transform: translateX(-100%); }
    @keyframes shimmer { 100% { transform: translateX(100%); } }

    .modal-label { font-size: var(--text-xs); color: var(--text-tertiary); margin-bottom: 0.375rem; display: block; font-weight: var(--fw-semibold); text-transform: uppercase; letter-spacing: 0.05em; }

    @media (max-width: 768px) {
        .branches-wrapper { flex-direction: column; }
        .lab-inner-tabs { padding: 0; }
        .inner-tab-btn { font-size: 0.75rem; padding: 0.625rem 0.5rem; }
    }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-sitemap"></i> Laboratuvar Yönetimi</h1>
        <p>Cihaz yerleşimi ve Güç Kontrol Merkezi</p>
    </div>
    <div class="page-header-actions">
        <button class="btn danger" onclick="window.powerCommand('ALL', 'shutdown')"><i class="fas fa-power-off"></i> Tümünü Kapat</button>
        <button class="btn warning" onclick="window.wakeUpCommand('ALL')"><i class="fas fa-bolt"></i> Tümünü Uyandır</button>
    </div>
</div>

<div class="lab-toolbar">
    <div style="flex:1;min-width:240px;position:relative;">
        <i class="fas fa-search" style="position:absolute;left:0.875rem;top:50%;transform:translateY(-50%);color:var(--text-tertiary);pointer-events:none;"></i>
        <input type="text" id="globalLabSearch" placeholder="Hostname, IP veya MAC Ara..." style="padding-left:2.25rem;">
    </div>
    <div style="display:flex;gap:0.5rem;flex-wrap:wrap;">
        <button class="btn secondary" onclick="window.expandAllLabs()"><i class="fas fa-up-right-and-down-left-from-center"></i> Tümünü Aç</button>
        <button class="btn secondary" onclick="window.collapseAllLabs()"><i class="fas fa-down-left-and-up-right-to-center"></i> Daralt</button>
        <button class="btn secondary" onclick="openModal('globalMoveModal')"><i class="fas fa-right-left"></i> Toplu Taşı</button>
        <button class="btn secondary" onclick="openModal('newLabModal')"><i class="fas fa-plus"></i> Lab Ekle</button>
        <button class="btn" onclick="openModal('autoEnrollModal')"><i class="fas fa-robot"></i> Oto-Kayıt</button>
    </div>
</div>

<div id="unassignedZoneContainer"></div>
<div id="advancedLabsContainer">
    <div class="skeleton-card"></div>
    <div class="skeleton-card"></div>
    <div class="skeleton-card"></div>
</div>

<div class="modal-overlay" id="newLabModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-plus" style="color:var(--primary-500);margin-right:0.5rem;"></i> Yeni Laboratuvar</div>
            <button class="modal-close" onclick="closeModal('newLabModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <label class="modal-label">Sınıf Adı</label>
            <input type="text" id="newLabName" placeholder="Örn: Yazilim_Lab_1">
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="closeModal('newLabModal')">İptal</button>
            <button class="btn success" onclick="window.createNewLab()"><i class="fas fa-save"></i> Kapsama Ekle</button>
        </div>
    </div>
</div>

<div class="modal-overlay" id="globalMoveModal">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-right-left" style="color:var(--primary-500);margin-right:0.5rem;"></i> Toplu Cihaz Taşıma</div>
            <button class="modal-close" onclick="closeModal('globalMoveModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <label class="modal-label">Filtrele</label>
            <select id="bulkMoveFilter" onchange="window.renderBulkMoveList()" style="margin-bottom:1rem;">
                <option value="ALL">Tüm Cihazları Göster</option>
            </select>
            <div style="max-height:300px;overflow-y:auto;background:var(--bg-surface-2);border:1px solid var(--border-subtle);border-radius:var(--radius-md);margin-bottom:1rem;">
                <table class="data-table" style="margin:0;">
                    <thead style="position:sticky;top:0;background:var(--bg-surface);z-index:1;">
                        <tr>
                            <th style="width:40px;text-align:center;"><input type="checkbox" id="bulkSelectAll" onchange="window.toggleBulkSelectAll()"></th>
                            <th>Hostname</th><th>Mevcut Lab</th>
                        </tr>
                    </thead>
                    <tbody id="bulkMovePcList"></tbody>
                </table>
            </div>
            <div style="display:flex;gap:0.75rem;align-items:flex-end;">
                <div style="flex:1;">
                    <label class="modal-label">Hedef Laboratuvar</label>
                    <select id="globalMoveLabSelect"></select>
                </div>
                <button class="btn" onclick="window.submitBulkMove()"><i class="fas fa-paper-plane"></i> Seçilileri Taşı</button>
            </div>
        </div>
    </div>
</div>

<div class="modal-overlay" id="autoEnrollModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-robot" style="color:var(--primary-500);margin-right:0.5rem;"></i> Toplu Oto-Kayıt</div>
            <button class="modal-close" onclick="closeModal('autoEnrollModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <p style="font-size:var(--text-sm);color:var(--text-tertiary);margin-bottom:1rem;">Belirtilen tarihe kadar ağa bağlanan yeni PC'ler otomatik olarak bu sınıfa kaydedilir.</p>
            <label class="modal-label">Hedef Sınıf</label>
            <select id="autoEnrollLabSelect" style="margin-bottom:1rem;"></select>
            <label class="modal-label">Bitiş Tarihi</label>
            <input type="date" id="autoEnrollDate">
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="closeModal('autoEnrollModal')">İptal</button>
            <button class="btn warning" onclick="window.saveAutoEnroll()"><i class="fas fa-bolt"></i> Kuralı Başlat</button>
        </div>
    </div>
</div>

<div class="modal-overlay" id="movePcModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-right-left" style="color:var(--primary-500);margin-right:0.5rem;"></i> Cihazı Taşı</div>
            <button class="modal-close" onclick="closeModal('movePcModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <p style="margin-bottom:1rem;color:var(--text-tertiary);">Seçili cihaz: <strong id="movePcName" style="color:var(--primary-600);font-size:var(--text-md);"></strong></p>
            <label class="modal-label">Hedef Sınıf</label>
            <select id="movePcLabSelect"></select>
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="closeModal('movePcModal')">İptal</button>
            <button class="btn" onclick="window.movePcSubmit()"><i class="fas fa-exchange-alt"></i> Taşı</button>
        </div>
    </div>
</div>

<script>
const apiUrl = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '';
var pageState = window.pageState || { selectedIds: new Set(), searchQuery: '', knownGoodNames: {} };
var isFirstLoad = typeof window.isFirstLoad !== 'undefined' ? window.isFirstLoad : true;
var expandedLabs = window.expandedLabs || new Set();
var activeLabTabs = window.activeLabTabs || {};
var pcToMove = null;

function extractPcNumber(hostname) {
    const m = hostname.match(/\d+$/);
    return m ? parseInt(m[0], 10) : 9999;
}

window.initLabsView = function(force = false) {
    if (typeof state === 'undefined' || !state.devices) return;
    if (isFirstLoad && state.devices.length > 0) {
        isFirstLoad = false; window.isFirstLoad = false;
        document.getElementById('advancedLabsContainer').innerHTML = '';
    }
    const isInputFocused = document.activeElement && (document.activeElement.tagName === 'INPUT' || document.activeElement.tagName === 'SELECT') && document.activeElement.id !== 'globalLabSearch';
    const isModalOpen = document.querySelectorAll('.modal-overlay.open').length > 0;
    if (!force && (isInputFocused || isModalOpen)) return;
    window.renderUnassignedZone();
    window.renderAdvancedLabsView();
};

window.renderUnassignedZone = function() {
    const container = document.getElementById('unassignedZoneContainer');
    if (!state.devices) return;
    const unassignedPcs = state.devices.filter(d => !d.lab || d.lab === 'Atanmamis_Cihazlar' || d.lab === '');
    if (unassignedPcs.length === 0) { container.innerHTML = ''; return; }
    const allLabs = [...new Set([...Object.keys(state.labsStats || {}), ...(state.customLabs || [])])].filter(l => l && l !== 'Atanmamis_Cihazlar');

    container.innerHTML = `
        <div class="unassigned-zone">
            <div class="unassigned-header">
                <i class="fas fa-exclamation-circle"></i> Bekleme Odası: ${unassignedPcs.length} cihaz atama bekliyor
                <label class="master-cb-label">
                    <input type="checkbox" id="unassignedMasterCb" onchange="window.toggleAllUnassigned(this.checked)"> Tümünü Seç
                </label>
            </div>
            <div class="unassigned-grid">
                ${unassignedPcs.map(pc => `
                    <label class="unassigned-pc">
                        <input type="checkbox" class="unassigned-cb" value="${pc.hostname}" onchange="window.checkUnassignedMaster()">
                        <div style="display:flex;flex-direction:column;overflow:hidden;flex:1;">
                            <span class="pc-name" style="display:flex;align-items:center;justify-content:space-between;width:100%;">
                                <span>${escapeHtml(pc.display_name || pc.real_hostname || pc.hostname)}</span>
                                <button class="mini-btn" style="padding:0;font-size:12px;background:transparent;border:none;color:var(--text-tertiary);" title="İsmi Değiştir" onclick="event.preventDefault(); event.stopPropagation(); window.renameDevice('${pc.hostname}', '${escapeHtml(pc.display_name || pc.real_hostname || pc.hostname)}')"><i class="fas fa-edit"></i></button>
                            </span>
                            <span class="pc-meta">${pc.ip || 'IP Yok'} • ${pc.status}</span>
                        </div>
                    </label>
                `).join('')}
            </div>
            <div class="unassigned-actions">
                <div style="flex:1;min-width:200px;">
                    <label class="modal-label">Mevcut Laba Taşı</label>
                    <select id="unassignedTargetLab">
                        <option value="">— Lab Seçin —</option>
                        ${allLabs.map(l => `<option value="${l}">${l}</option>`).join('')}
                    </select>
                </div>
                <span style="color:var(--text-tertiary);font-weight:var(--fw-semibold);">VEYA</span>
                <div style="flex:1;min-width:200px;">
                    <label class="modal-label">Yeni Lab Oluştur & Taşı</label>
                    <input type="text" id="unassignedNewLab" placeholder="Örn: Lab-6">
                </div>
                <button class="btn" onclick="window.assignUnassigned()"><i class="fas fa-paper-plane"></i> Ata</button>
            </div>
        </div>`;
};

window.renderAdvancedLabsView = function() {
    const container = document.getElementById('advancedLabsContainer');
    const query = (pageState.searchQuery || '').toLowerCase();
    const groupedByLab = {};
    state.devices.forEach(d => { const l = d.lab || 'Atanmamış'; if (!groupedByLab[l]) groupedByLab[l] = []; groupedByLab[l].push(d); });
    const allKnownLabs = [...new Set([...Object.keys(state.labsStats || {}), ...(state.customLabs || [])])].filter(l => l && l !== 'Atanmamis_Cihazlar');
    allKnownLabs.sort().forEach(labName => {
        const pcs = groupedByLab[labName] || [];
        if (query && pcs.length === 0) return;
        const wrapperId = `wrapper_${labName.replace(/\s+/g, '_')}`;
        let wrapper = document.getElementById(wrapperId);
        const onlineCount = pcs.filter(p => p.status.toLowerCase() === 'online').length;
        const idleCount = pcs.filter(p => p.status.toLowerCase() === 'idle').length;
        const activeCount = onlineCount + idleCount;
        const isExpanded = query.length > 0 || expandedLabs.has(labName);
        const activeTab = activeLabTabs[labName] || 'map';

        let teacherPc = null;
        if (state.mainPcs && state.mainPcs[labName]) teacherPc = pcs.find(d => d.hostname === state.mainPcs[labName]);
        if (!teacherPc) teacherPc = pcs.find(d => d.hostname.toUpperCase().includes('OGR') || d.hostname.toUpperCase().includes('ANA'));
        let studentPcs = pcs.filter(d => d !== teacherPc).sort((a, b) => extractPcNumber(a.display_name || a.display_name || a.real_hostname || a.hostname) - extractPcNumber(b.display_name || b.display_name || b.real_hostname || b.hostname));

        const dataHash = pcs.map(p => `${p.hostname}-${p.status}`).sort().join('|') + `|Exp:${isExpanded}|Tab:${activeTab}|Main:${teacherPc ? teacherPc.hostname : ''}`;

        if (!wrapper || wrapper.dataset.full !== dataHash) {
            if (!wrapper) { wrapper = document.createElement('div'); wrapper.id = wrapperId; container.appendChild(wrapper); }
            wrapper.className = `lab-wrapper ${isExpanded ? 'expanded' : ''}`;
            wrapper.dataset.full = dataHash;

            const buildPcHtml = (pc, isMain = false) => {
                if (!pc) return `<div class="pc-desk" style="opacity:0.4;"><span class="pc-name" style="color:var(--text-tertiary);">Yuva Boş</span></div>`;
                const statusClass = pc.status.toLowerCase();
                const dName = pc.display_name || pc.display_name || pc.real_hostname || pc.hostname;
                const iconClass = statusClass === 'online' ? 'fa-desktop' : (statusClass === 'idle' ? 'fa-moon' : 'fa-power-off');
                return `
                    <div class="pc-desk ${statusClass} ${isMain ? 'main-pc' : ''}" data-hostname="${pc.hostname}" title="IP: ${pc.ip || 'Yok'} | MAC: ${pc.mac || 'Yok'}">
                        <div style="overflow:hidden;flex:1;">
                            <div class="pc-name">${escapeHtml(dName)}</div>
                            <div class="pc-host">${pc.hostname}</div>
                        </div>
                        <i class="fas ${iconClass} pc-icon-main"></i>
                        <div class="pc-desk-actions">
                            <button class="desk-btn wake" title="Uyandır" onclick="window.wakeUpCommand('PC', '${pc.hostname}')"><i class="fas fa-bolt"></i></button>
                            <button class="desk-btn res" title="Yeniden Başlat" onclick="window.powerCommand('PC', 'restart', '${pc.hostname}')"><i class="fas fa-arrows-rotate"></i></button>
                            <button class="desk-btn shut" title="Kapat" onclick="window.powerCommand('PC', 'shutdown', '${pc.hostname}')"><i class="fas fa-power-off"></i></button>
                        </div>
                    </div>`;
            };

            let layoutData = { left: [], center: [], right: [] };
            try { if (state.labLayouts && state.labLayouts[labName]) layoutData = JSON.parse(state.labLayouts[labName]); } catch(e) {}

            let leftPcs = [], centerPcs = [], rightPcs = [];
            let unplacedPcs = [...studentPcs];

            ['left', 'center', 'right'].forEach(col => {
                if (layoutData[col]) {
                    layoutData[col].forEach(hostname => {
                        const idx = unplacedPcs.findIndex(p => p.hostname === hostname);
                        if (idx !== -1) {
                            if (col === 'left') leftPcs.push(unplacedPcs[idx]);
                            if (col === 'center') centerPcs.push(unplacedPcs[idx]);
                            if (col === 'right') rightPcs.push(unplacedPcs[idx]);
                            unplacedPcs.splice(idx, 1);
                        }
                    });
                }
            });

            unplacedPcs.forEach(p => {
                if (leftPcs.length < 12) leftPcs.push(p);
                else if (centerPcs.length < 12) centerPcs.push(p);
                else rightPcs.push(p);
            });

            let mapHtml = `<div class="tab-content-map" style="display:${activeTab === 'map' ? 'flex' : 'none'};">`;
            if (pcs.length > 0) {
                mapHtml += `
                <div class="power-controls">
                    <span style="color:var(--text-tertiary);font-size:var(--text-sm);margin-right:0.5rem;"><i class="fas fa-plug"></i> Sınıf Kontrolü:</span>
                    <button class="power-btn wake" onclick="window.wakeUpCommand('LAB', '${labName}')"><i class="fas fa-bolt"></i> Aç (WOL)</button>
                    <button class="power-btn restart" onclick="window.powerCommand('LAB', 'restart', '${labName}')"><i class="fas fa-arrows-rotate"></i> Yeniden Başlat</button>
                    <button class="power-btn shutdown" onclick="window.powerCommand('LAB', 'shutdown', '${labName}')"><i class="fas fa-power-off"></i> Kapat</button>
                </div>
                <div style="width:100%;max-width:400px;text-align:center;">
                    <div style="color:var(--warning-text);font-size:0.75rem;font-weight:var(--fw-semibold);margin-bottom:0.5rem;text-transform:uppercase;letter-spacing:0.06em;"><i class="fas fa-crown"></i> Yönetici (Ana) Bilgisayar</div>
                    ${teacherPc ? buildPcHtml(teacherPc, true) : `<div class="pc-desk main-pc" style="border-style:dashed;opacity:0.4;justify-content:center;"><span class="pc-name" style="color:var(--text-tertiary);">Ana PC Atanmadı</span></div>`}
                </div>
                <div class="branches-wrapper">
                    <div class="pc-column sortable-col" id="col_left_${labName}" data-col="left"><div class="col-title">Sol Sütun</div>${leftPcs.map(p => buildPcHtml(p)).join('')}</div>
                    <div class="pc-column sortable-col" id="col_center_${labName}" data-col="center"><div class="col-title">Orta Sütun</div>${centerPcs.map(p => buildPcHtml(p)).join('')}</div>
                    <div class="pc-column sortable-col" id="col_right_${labName}" data-col="right"><div class="col-title">Sağ Sütun</div>${rightPcs.map(p => buildPcHtml(p)).join('')}</div>
                </div>`;
            } else { mapHtml += `<div class="empty-state"><i class="fas fa-ghost"></i><h3>Laboratuvar boş</h3><p>Bu sınıfta kayıtlı cihaz yok.</p></div>`; }
            mapHtml += `</div>`;

            let manageHtml = `<div class="tab-content-manage ${activeTab === 'manage' ? 'active' : ''}">
                <div class="manage-section">
                    <div class="manage-title"><i class="fas fa-sliders"></i> Laboratuvar Kontrolleri</div>
                    <div style="display:flex;gap:0.5rem;flex-wrap:wrap;align-items:center;">
                        <input type="text" id="renameInput_${labName}" value="${labName}" style="flex:1;min-width:200px;">
                        <button class="btn" onclick="window.renameLab('${labName}')"><i class="fas fa-save"></i> Adı Güncelle</button>
                        <button class="btn danger" onclick="window.deleteLab('${labName}')"><i class="fas fa-trash"></i> Labı Sil</button>
                    </div>
                </div>
                <div class="manage-section">
                    <div class="manage-title"><i class="fas fa-list-ul"></i> Detaylı Cihaz Listesi</div>
                    <div style="overflow-x:auto;">
                        <table class="data-table">
                            <thead><tr><th>Kimlik / İsim</th><th>Ağ Bilgisi</th><th>Durum</th><th style="text-align:right;">İşlemler</th></tr></thead>
                            <tbody>
                                ${pcs.map(d => {
                                    const isMain = teacherPc && teacherPc.hostname === d.hostname;
                                    const dName = d.display_name || d.display_name || d.real_hostname || d.hostname;
                                    return `<tr>
                                        <td><div style="font-weight:var(--fw-semibold);">${escapeHtml(dName)} ${isMain ? '<i class="fas fa-crown" style="color:var(--warning-solid);margin-left:0.25rem;"></i>' : ''}</div><div style="font-size:0.6875rem;color:var(--text-tertiary);font-family:var(--font-mono);">${d.hostname}</div></td>
                                        <td><div style="color:var(--info-text);font-family:var(--font-mono);font-size:var(--text-xs);">${d.ip || '—'}</div><div style="color:var(--text-tertiary);font-size:0.6875rem;font-family:var(--font-mono);">${d.mac || '—'}</div></td>
                                        <td><span class="status-dot ${d.status.toLowerCase() === 'online' ? 'online' : (d.status.toLowerCase() === 'idle' ? 'idle' : 'offline')}"></span> <span style="font-size:var(--text-xs);">${d.status}</span></td>
                                        <td style="text-align:right;">
                                            <div style="display:inline-flex;gap:0.375rem;">
                                                <button class="action-btn crown" onclick="window.setMainPc('${labName}', '${d.hostname}')" title="Ana PC yap"><i class="fas fa-crown"></i></button>
                                                <button class="action-btn" onclick="window.openMovePcModal('${d.hostname}', '${labName}')" title="Taşı"><i class="fas fa-right-left"></i></button>
                                                <button class="action-btn eject" onclick="window.unassignPc('${d.hostname}')" title="Çıkar"><i class="fas fa-eject"></i></button>
                                            </div>
                                        </td>
                                    </tr>`;
                                }).join('')}
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>`;

            wrapper.innerHTML = `
                <div class="lab-summary ${isExpanded ? 'expanded' : ''}" onclick="window.toggleLab('${labName}')">
                    <div class="lab-title-area">
                        <i class="fas fa-network-wired lab-icon"></i>
                        <div>
                            <div class="lab-name">${escapeHtml(labName)}</div>
                            <div class="lab-stats">
                                <span class="active-count"><span class="status-dot online"></span> ${activeCount} aktif</span>
                                <span style="border-left:1px solid var(--border-subtle);padding-left:1rem;"><i class="fas fa-desktop"></i> ${pcs.length} toplam</span>
                            </div>
                        </div>
                    </div>
                    <i class="fas fa-chevron-down lab-toggle-icon"></i>
                </div>
                <div class="lab-content" style="${isExpanded ? 'display:flex;' : 'display:none;'}">
                    <div class="lab-inner-tabs">
                        <button class="inner-tab-btn ${activeTab === 'map' ? 'active' : ''}" onclick="window.switchLabTab('${labName}', 'map')"><i class="fas fa-sitemap"></i> Cihaz Haritası</button>
                        <button class="inner-tab-btn ${activeTab === 'manage' ? 'active' : ''}" onclick="window.switchLabTab('${labName}', 'manage')"><i class="fas fa-gear"></i> Lab Ayarları & Liste</button>
                    </div>
                    ${mapHtml}
                    ${manageHtml}
                </div>`;
        }
    });
    document.querySelectorAll('.lab-wrapper').forEach(w => {
        const labId = w.id.replace('wrapper_', '');
        if (!allKnownLabs.includes(labId)) w.style.display = 'none';
    });
    
    // SortableJS başlat
    if (typeof window.initSortableLabs === 'function') window.initSortableLabs();
};

window.toggleAllUnassigned = function(isChecked) { document.querySelectorAll('.unassigned-cb').forEach(cb => cb.checked = isChecked); };
window.checkUnassignedMaster = function() {
    const allCbs = document.querySelectorAll('.unassigned-cb');
    const checkedCbs = document.querySelectorAll('.unassigned-cb:checked');
    const masterCb = document.getElementById('unassignedMasterCb');
    if (masterCb) masterCb.checked = (allCbs.length > 0 && allCbs.length === checkedCbs.length);
};

window.assignUnassigned = async function() {
    const checkedBoxes = document.querySelectorAll('.unassigned-cb:checked');
    const pcNames = Array.from(checkedBoxes).map(cb => cb.value);
    if (pcNames.length === 0) return showToast('Lütfen taşınacak en az bir cihaz seçin!', 'error');
    const existingLab = document.getElementById('unassignedTargetLab').value;
    const newLab = document.getElementById('unassignedNewLab').value.trim();
    let targetLab = existingLab;
    if (newLab) {
        targetLab = newLab;
        try { await apiRequest('/api/create_lab', { method: 'POST', body: JSON.stringify({ lab_name: newLab }) }); } catch (e) {}
    }
    if (!targetLab) return showToast('Lütfen sınıf seçin veya oluşturun!', 'error');
    try {
        await apiRequest('/api/move_pcs', { method: 'POST', body: JSON.stringify({ pc_names: pcNames, new_lab: targetLab }) });
        showToast(`${pcNames.length} cihaz ${targetLab} sınıfına atandı!`, 'success');
        expandedLabs.add(targetLab);
        if (typeof loadDevices === 'function') loadDevices();
    } catch (e) { showToast('Sunucu hatası, taşıma başarısız!', 'error'); }
};

window.initSortableLabs = function() {
    if (typeof Sortable === 'undefined') return;
    document.querySelectorAll('.branches-wrapper').forEach(wrapper => {
        if (wrapper.dataset.sortableInit) return;
        wrapper.dataset.sortableInit = 'true';
        const cols = wrapper.querySelectorAll('.sortable-col');
        cols.forEach(col => {
            new Sortable(col, {
                group: 'shared',
                animation: 150,
                filter: '.col-title',
                onEnd: function (evt) {
                    const match = col.id.match(/col_[a-z]+_(.*)/);
                    if (!match) return;
                    const lName = match[1];
                    const layout = { left: [], center: [], right: [] };
                    ['left', 'center', 'right'].forEach(c => {
                        const cDiv = document.getElementById(`col_${c}_${lName}`);
                        if (cDiv) {
                            cDiv.querySelectorAll('.pc-desk').forEach(desk => {
                                if (desk.dataset.hostname) layout[c].push(desk.dataset.hostname);
                            });
                        }
                    });
                    apiRequest('/api/save_lab_layout', {
                        method: 'POST',
                        body: JSON.stringify({ lab_name: lName, layout_json: JSON.stringify(layout) })
                    }).then(() => {
                        if (state.labLayouts) state.labLayouts[lName] = JSON.stringify(layout);
                    });
                }
            });
        });
    });
};

window.toggleLab = function(labName) { expandedLabs.has(labName) ? expandedLabs.delete(labName) : expandedLabs.add(labName); window.initLabsView(true); };
window.expandAllLabs = function() { if (!state || !state.devices) return; const labs = [...new Set(state.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar'); labs.forEach(l => expandedLabs.add(l)); window.initLabsView(true); };
window.collapseAllLabs = function() { expandedLabs.clear(); window.initLabsView(true); };
window.switchLabTab = function(labName, tabName) { activeLabTabs[labName] = tabName; window.initLabsView(true); };

window.setMainPc = async function(labName, hostname) {
    try { 
        const res = await apiRequest('/api/set_main_pc', { method: 'POST', body: JSON.stringify({ lab_name: labName, pc_name: hostname }) }); 
        showToast(res.message || 'İşlem başarılı.', 'success'); 
        if (typeof loadDevices === 'function') loadDevices(); 
    } catch (e) {
        showToast('Ana bilgisayar ayarlanamadı.', 'error');
    }
};
window.unassignPc = async function(hostname) {
    if (!confirm(`${hostname} cihazını sınıftan çıkarmak istediğinize emin misiniz?`)) return;
    try { await apiRequest('/api/move_pc', { method: 'POST', body: JSON.stringify({ pc_name: hostname, new_lab: 'Atanmamis_Cihazlar' }) }); showToast(`${hostname} çıkarıldı.`, 'success'); if (typeof loadDevices === 'function') loadDevices(); } catch (e) {}
};
window.deleteLab = async function(labName) {
    if (!confirm(`${labName} laboratuvarını silmek istiyor musunuz?`)) return;
    try { await apiRequest('/api/delete_lab', { method: 'POST', body: JSON.stringify({ lab_name: labName }) }); expandedLabs.delete(labName); showToast(`${labName} silindi.`, 'success'); if (typeof loadDevices === 'function') loadDevices(); } catch (e) {}
};
window.renameLab = async function(oldName) {
    const newName = document.getElementById(`renameInput_${oldName}`).value.trim();
    if (!newName || newName === oldName) return;
    try { await apiRequest('/api/rename_lab', { method: 'POST', body: JSON.stringify({ old_name: oldName, new_name: newName }) }); if (expandedLabs.has(oldName)) { expandedLabs.delete(oldName); expandedLabs.add(newName); } showToast('Sınıf adı güncellendi.', 'success'); if (typeof loadDevices === 'function') loadDevices(); } catch (e) {}
};

window.openModal = function(id) {
    const el = document.getElementById(id);
    if (!el) return;
    el.classList.add('open');
    const allLabs = [...new Set([...Object.keys(state.labsStats || {}), ...(state.customLabs || [])])].filter(l => l && l !== 'Atanmamis_Cihazlar');
    if (id === 'autoEnrollModal' || id === 'movePcModal' || id === 'globalMoveModal') {
        const select = document.getElementById(id === 'autoEnrollModal' ? 'autoEnrollLabSelect' : (id === 'movePcModal' ? 'movePcLabSelect' : 'globalMoveLabSelect'));
        select.innerHTML = '';
        if (id === 'movePcModal' || id === 'globalMoveModal') select.innerHTML += `<option value="Atanmamis_Cihazlar" style="color:var(--danger-text);">❌ Bekleme Odası</option>`;
        allLabs.forEach(l => { select.innerHTML += `<option value="${l}">${l}</option>`; });
    }
    if (id === 'globalMoveModal') {
        const filterSelect = document.getElementById('bulkMoveFilter');
        filterSelect.innerHTML = `<option value="ALL">Tüm Cihazlar</option>`;
        allLabs.forEach(l => { filterSelect.innerHTML += `<option value="${l}">Sadece ${l}</option>`; });
        window.renderBulkMoveList();
    }
};

window.createNewLab = async function() {
    const val = document.getElementById('newLabName').value.trim();
    if (!val) return;
    try { await apiRequest('/api/create_lab', { method: 'POST', body: JSON.stringify({ lab_name: val }) }); showToast(`${val} oluşturuldu.`, 'success'); closeModal('newLabModal'); expandedLabs.add(val); document.getElementById('newLabName').value = ''; if (typeof loadDevices === 'function') loadDevices(); } catch (e) {}
};

window.openMovePcModal = function(hostname, currentLab) { pcToMove = hostname; document.getElementById('movePcName').innerText = hostname; openModal('movePcModal'); setTimeout(() => { document.getElementById('movePcLabSelect').value = currentLab; }, 50); };
window.movePcSubmit = async function() {
    const targetLab = document.getElementById('movePcLabSelect').value;
    if (!pcToMove || !targetLab) return;
    try { await apiRequest('/api/move_pc', { method: 'POST', body: JSON.stringify({ pc_name: pcToMove, new_lab: targetLab }) }); showToast(`${pcToMove} taşındı.`, 'success'); closeModal('movePcModal'); if (targetLab !== 'Atanmamis_Cihazlar') expandedLabs.add(targetLab); if (typeof loadDevices === 'function') loadDevices(); } catch (e) {}
};

window.renderBulkMoveList = function() {
    const filter = document.getElementById('bulkMoveFilter').value;
    const tbody = document.getElementById('bulkMovePcList');
    tbody.innerHTML = '';
    let filtered = state.devices || [];
    if (filter === 'ALL') filtered = filtered.filter(d => d.lab !== 'Atanmamis_Cihazlar');
    else filtered = filtered.filter(d => d.lab === filter);
    filtered.forEach(d => {
        const dName = d.display_name || d.display_name || d.real_hostname || d.hostname;
        const tr = document.createElement('tr');
        tr.innerHTML = `<td style="text-align:center;"><input type="checkbox" class="bulk-pc-cb" value="${d.hostname}"></td>
            <td><strong>${escapeHtml(dName)}</strong><br><span style="font-size:0.6875rem;color:var(--text-tertiary);font-family:var(--font-mono);">${d.hostname}</span></td>
            <td><span class="lab-pill">${d.lab}</span></td>`;
        tbody.appendChild(tr);
    });
    document.getElementById('bulkSelectAll').checked = false;
};
window.toggleBulkSelectAll = function() { const isChecked = document.getElementById('bulkSelectAll').checked; document.querySelectorAll('.bulk-pc-cb').forEach(cb => cb.checked = isChecked); };
window.submitBulkMove = async function() {
    const targetLab = document.getElementById('globalMoveLabSelect').value;
    const checkedBoxes = document.querySelectorAll('.bulk-pc-cb:checked');
    const pcNames = Array.from(checkedBoxes).map(cb => cb.value);
    if (pcNames.length === 0 || !targetLab) return;
    try { await apiRequest('/api/move_pcs', { method: 'POST', body: JSON.stringify({ pc_names: pcNames, new_lab: targetLab }) }); showToast(`${pcNames.length} cihaz taşındı.`, 'success'); closeModal('globalMoveModal'); if (targetLab !== 'Atanmamis_Cihazlar') expandedLabs.add(targetLab); if (typeof loadDevices === 'function') loadDevices(); } catch (e) {}
};
window.saveAutoEnroll = async function() {
    const targetLab = document.getElementById('autoEnrollLabSelect').value;
    const date = document.getElementById('autoEnrollDate').value;
    if (!targetLab || !date) return showToast('Eksik bilgi.', 'warning');
    try { await apiRequest('/api/set_auto_enroll', { method: 'POST', body: JSON.stringify({ target_lab: targetLab, expire_date: date }) }); showToast('Oto-kayıt aktif.', 'success'); closeModal('autoEnrollModal'); } catch (e) {}
};

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
            // Wait for WebSocket event or manually trigger a state refresh
        } else {
            showToast('İsim güncellenirken hata oluştu', 'danger');
        }
    } catch (e) {
        showToast('Bağlantı hatası', 'danger');
    }
}
document.addEventListener('pops_data_updated', () => {
    const searchEl = document.getElementById('globalLabSearch');
    if (searchEl) pageState.searchQuery = searchEl.value;
    window.initLabsView();
});
document.getElementById('globalLabSearch')?.addEventListener('input', (e) => { pageState.searchQuery = e.target.value; window.initLabsView(true); });
if (typeof state !== 'undefined' && state.devices && state.devices.length > 0) window.initLabsView();
</script>

<?php include 'includes/footer.php'; ?>