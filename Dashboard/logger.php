<?php include 'includes/header.php'; ?>

<style>
    .logger-container { display: grid; grid-template-columns: 320px 1fr; gap: var(--space-4); min-height: calc(100vh - 160px); }
    .pc-list-panel, .detail-panel { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); display: flex; flex-direction: column; overflow: hidden; box-shadow: var(--shadow-xs); }

    .search-box { padding: var(--space-4); border-bottom: 1px solid var(--border-subtle); }
    .pc-list { flex: 1; overflow-y: auto; padding: var(--space-3); }
    .lab-group { margin-bottom: var(--space-3); }
    .lab-header { padding: 0.625rem 0.75rem; background: var(--bg-surface-2); border-radius: var(--radius-sm); font-weight: var(--fw-semibold); font-size: 0.75rem; color: var(--text-tertiary); text-transform: uppercase; letter-spacing: 0.05em; border-left: 3px solid var(--primary-500); cursor: pointer; display: flex; justify-content: space-between; align-items: center; transition: background-color 0.15s; }
    .lab-header:hover { background: var(--bg-surface); }
    .lab-header i { transition: transform 0.2s; }
    .lab-header.open i { transform: rotate(180deg); }
    .lab-pcs { display: none; padding-left: 0.5rem; margin-top: 0.25rem; }
    .lab-pcs.open { display: block; }

    .pc-item { padding: 0.5rem 0.625rem; border-radius: var(--radius-sm); cursor: pointer; transition: background-color 0.1s; border-left: 3px solid transparent; margin-bottom: 0.125rem; display: flex; justify-content: space-between; align-items: center; }
    .pc-item:hover { background: var(--bg-surface-2); }
    .pc-item.active { background: var(--primary-50); border-left-color: var(--primary-500); color: var(--primary-600); font-weight: var(--fw-semibold); }
    .pc-name { font-size: var(--text-sm); color: var(--text-primary); display: flex; align-items: center; gap: 0.5rem; }

    .mystic-btn { width: 100%; padding: 0.625rem; background: linear-gradient(135deg, var(--danger-bg), rgba(139, 92, 246, 0.10)); border: 1px solid var(--danger-border); color: var(--danger-text); border-radius: var(--radius-sm); cursor: pointer; font-weight: var(--fw-semibold); font-size: var(--text-sm); transition: all 0.15s; display: flex; align-items: center; justify-content: center; gap: 0.5rem; }
    .mystic-btn:hover { background: linear-gradient(135deg, var(--danger-border), rgba(139, 92, 246, 0.20)); }
    .mystic-container { padding: 0.75rem; border-bottom: 1px solid var(--border-subtle); }

    .detail-header { padding: var(--space-5); border-bottom: 1px solid var(--border-subtle); display: flex; justify-content: space-between; align-items: center; background: var(--bg-surface); gap: var(--space-4); flex-wrap: wrap; }
    .selected-pc-title { font-size: var(--text-xl); font-weight: var(--fw-semibold); display: flex; align-items: center; gap: 0.625rem; color: var(--text-primary); }
    .detail-tabs { display: flex; gap: 0.5rem; }
    .tab-btn { padding: 0.4375rem 0.75rem; background: var(--bg-surface-2); border: 1px solid var(--border-subtle); color: var(--text-tertiary); border-radius: var(--radius-sm); cursor: pointer; font-weight: var(--fw-semibold); transition: all 0.15s; font-size: var(--text-xs); display: inline-flex; align-items: center; gap: 0.375rem; }
    .tab-btn:hover { background: var(--bg-surface); color: var(--text-primary); }
    .tab-btn.active { background: var(--primary-500); color: white; border-color: var(--primary-500); }
    .tab-btn:disabled { opacity: 0.45; cursor: not-allowed; }
    .tab-btn:disabled:hover { background: var(--bg-surface-2); color: var(--text-tertiary); }

    .detail-body { flex: 1; overflow-y: auto; padding: var(--space-5); position: relative; }
    .empty-state { position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); text-align: center; color: var(--text-tertiary); }
    .empty-state i { font-size: 2.5rem; margin-bottom: 0.75rem; opacity: 0.4; }

    .hw-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: var(--space-3); }
    .hw-card { background: var(--bg-surface-2); padding: var(--space-4); border-radius: var(--radius-md); border: 1px solid var(--border-subtle); display: flex; align-items: center; gap: var(--space-3); transition: all 0.15s; }
    .hw-card:hover { border-color: var(--primary-500); transform: translateY(-1px); box-shadow: var(--shadow-sm); }
    .hw-icon { width: 40px; height: 40px; background: var(--primary-50); color: var(--primary-600); border-radius: var(--radius-sm); display: flex; align-items: center; justify-content: center; font-size: 1rem; flex-shrink: 0; }
    .hw-info { display: flex; flex-direction: column; flex: 1; min-width: 0; }
    .hw-label { font-size: 0.6875rem; color: var(--text-tertiary); text-transform: uppercase; font-weight: var(--fw-semibold); letter-spacing: 0.05em; margin-bottom: 0.125rem; }
    .hw-val { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); word-break: break-word; }

    .log-controls { display: flex; justify-content: space-between; align-items: center; margin-bottom: var(--space-3); background: var(--bg-surface-2); padding: 0.5rem; border-radius: var(--radius-md); border: 1px solid var(--border-subtle); gap: 0.75rem; flex-wrap: wrap; }
    .log-filters { display: flex; gap: 0.25rem; flex-wrap: wrap; }
    .log-filter-btn { padding: 0.3125rem 0.625rem; font-size: 0.75rem; border-radius: var(--radius-sm); border: 1px solid transparent; background: transparent; color: var(--text-tertiary); cursor: pointer; font-weight: var(--fw-semibold); }
    .log-filter-btn:hover { background: var(--bg-surface); }
    .log-filter-btn.active { background: var(--primary-50); border-color: var(--primary-500); color: var(--primary-600); }
    .log-search input { padding: 0.4375rem 0.75rem; font-size: var(--text-sm); width: 240px; }

    .log-table { width: 100%; border-collapse: collapse; }
    .log-table th, .log-table td { padding: 0.625rem 0.75rem; text-align: left; border-bottom: 1px solid var(--border-subtle); font-size: 0.8125rem; }
    .log-table th { color: var(--text-tertiary); text-transform: uppercase; font-weight: var(--fw-semibold); font-size: 0.6875rem; background: var(--bg-surface-2); position: sticky; top: 0; }
    .log-table tbody tr:hover { background: var(--bg-surface-2); }
    .log-badge { padding: 0.1875rem 0.5rem; border-radius: var(--radius-sm); font-size: 0.6875rem; font-weight: var(--fw-semibold); display: inline-block; text-align: center; min-width: 70px; }
    .badge-System { background: rgba(107, 114, 128, 0.15); color: var(--text-tertiary); }
    .badge-Deploy { background: var(--info-bg); color: var(--info-text); }
    .badge-AppStart { background: var(--warning-bg); color: var(--warning-text); }
    .badge-File { background: rgba(6, 182, 212, 0.15); color: #06b6d4; }
    .badge-USB { background: rgba(139, 92, 246, 0.15); color: #8b5cf6; }
    .badge-Network { background: var(--warning-bg); color: var(--warning-text); }
    .badge-Error { background: var(--danger-bg); color: var(--danger-text); }

    @media (max-width: 1024px) { .logger-container { grid-template-columns: 1fr; } .hw-grid { grid-template-columns: 1fr; } }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-clipboard-list"></i> Sistem Logları & Donanım Envanteri</h1>
        <p>Her cihazın aktivite geçmişini ve donanım bilgilerini merkezi panelden izleyin</p>
    </div>
</div>

<div class="logger-container">
    <div class="pc-list-panel">
        <div class="mystic-container">
            <button type="button" class="mystic-btn" onclick="window.wakeUpCommand('ALL', null, this)">
                <i class="fas fa-bolt"></i> Bütün cihazları uyandır
            </button>
        </div>
        <div class="search-box">
            <input type="text" id="searchPcInput" placeholder="Cihaz adı, lab veya IP ara..." oninput="renderPcList()">
        </div>
        <div class="pc-list" id="pcListContainer"></div>
    </div>

    <div class="detail-panel">
        <div class="detail-header" id="detailHeader">
            <div class="selected-pc-title">
                <i class="fas fa-desktop"></i> <span id="selectedPcName">Cihaz Seçiniz</span>
            </div>
            <div class="detail-tabs">
                <button type="button" class="tab-btn active" onclick="switchTab('logs')" id="tabBtn-logs" disabled title="Önce soldan bir cihaz seçin"><i class="fas fa-clock-rotate-left"></i> Etkinlik kayıtları</button>
                <button type="button" class="tab-btn" onclick="switchTab('hw')" id="tabBtn-hw" disabled title="Önce soldan bir cihaz seçin"><i class="fas fa-microchip"></i> Donanım</button>
            </div>
        </div>
        <div class="detail-body">
            <div class="empty-state" id="emptyState">
                <i class="fas fa-satellite-dish"></i>
                <h3>Veri Bekleniyor</h3>
                <p>Detayları görmek için sol taraftan bir cihaz seçin.</p>
            </div>

            <div id="hwTab" style="display:none;">
                <div class="hw-grid" id="hwGridContainer"></div>
            </div>

            <div id="logsTab" style="display:none;">
                <div class="log-controls">
                    <div class="log-filters">
                        <button type="button" class="log-filter-btn active" data-type="All" onclick="filterLogs('All')">Tümü</button>
                        <button type="button" class="log-filter-btn" data-type="risk" onclick="filterLogs('risk')">Yüksek risk</button>
                        <button type="button" class="log-filter-btn" data-type="security" onclick="filterLogs('security')">Güvenlik</button>
                        <button type="button" class="log-filter-btn" data-type="restricted_content" onclick="filterLogs('restricted_content')">Kural ihlali</button>
                        <button type="button" class="log-filter-btn" data-type="system_maintenance" onclick="filterLogs('system_maintenance')">Bakım</button>
                    </div>
                    <div class="log-search">
                        <input type="text" id="searchLogInput" placeholder="Loglarda kelime ara..." oninput="renderLogsTable()">
                    </div>
                </div>
                <div style="overflow-x:auto;">
                    <table class="log-table">
                        <thead><tr>
                            <th style="width:160px;">Tarih / Saat</th>
                            <th style="width:120px;">Risk / Kategori</th>
                            <th style="width:140px;">Eylem / Aktör</th>
                            <th>Mesaj / Neden</th>
                            <th style="width:60px;text-align:center;">İncele</th>
                        </tr></thead>
                        <tbody id="logTableBody"></tbody>
                    </table>
                </div>
            </div>
        </div>
    </div>
</div>


<div id="inspectModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="inspectTitle">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="inspectTitle"><i class="fas fa-magnifying-glass-plus"></i> Kayıt ayrıntısı</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <pre id="inspectJsonContent" class="text-mono" style="margin:0;font-size:0.8125rem;white-space:pre-wrap;word-break:break-all;background:var(--bg-app);border:1px solid var(--border-subtle);border-radius:var(--radius-md);padding:var(--space-4);max-height:60vh;overflow:auto;"></pre>
        </div>
        <div class="modal-footer"><button type="button" class="btn secondary" data-close-modal>Kapat</button></div>
    </div>
</div>

<script>
function showInspectModal(logId) {
    const log = globalLogs.find(l => l.id == logId);
    if (!log) return;
    const jsonStr = JSON.stringify(log, null, 4);
    document.getElementById('inspectJsonContent').textContent = jsonStr;
    openModal('inspectModal');
}

let globalDevices = [];
let globalInventory = [];
let globalLogs = [];
let selectedPc = null;
let currentTab = 'logs';
let currentLogFilter = 'All';
let openLabs = new Set();
let lastSidebarHash = '';
let lastHwHash = '';
let lastLogsHash = '';

let loggerLoaded = false;
async function fetchLoggerData() {
    try {
        const [newDevs, inv, logs] = await Promise.all([POps.get('/api/devices'), POps.get('/api/inventory').catch(() => null), POps.get('/api/logs').catch(() => null)]);
        loggerLoaded = true;
        if (Array.isArray(newDevs)) {
            globalDevices = newDevs.sort((a, b) => {
                const nA = a.display_name || a.real_hostname || a.hostname, nB = b.display_name || b.real_hostname || b.hostname;
                if (a.lab === b.lab) return nA.localeCompare(nB, undefined, { numeric: true });
                return (a.lab || '').localeCompare(b.lab || '');
            });
        }
        if (Array.isArray(inv)) globalInventory = inv;
        if (Array.isArray(logs)) globalLogs = logs.sort((a, b) => String(b.timestamp).localeCompare(String(a.timestamp)));
        renderPcList();
        if (selectedPc) { if (currentTab === 'hw') renderHardware(); if (currentTab === 'logs') renderLogsTable(); }
    } catch (e) {
        if (!loggerLoaded) POps.setError(document.getElementById('pcListContainer'), e, { compact: true });
    }
}

function renderPcList() {
    const container = document.getElementById('pcListContainer');
    const searchTerm = document.getElementById('searchPcInput').value.toLowerCase();
    let filtered = globalDevices.filter(d => {
        const hw = globalInventory.find(inv => inv.pc_name === d.hostname);
        const ip = (hw && hw.ip_address || '').toLowerCase();
        const real = (d.real_hostname || '').toLowerCase();
        return (d.hostname || '').toLowerCase().includes(searchTerm) || real.includes(searchTerm) || (d.lab || '').toLowerCase().includes(searchTerm) || ip.includes(searchTerm);
    });
    const visualState = filtered.map(d => `${d.hostname}:${d.real_hostname}:${d.lab}:${d.status}`).join('|');
    const currentHash = searchTerm + '|' + visualState;
    if (currentHash === lastSidebarHash) return;
    lastSidebarHash = currentHash;
    container.innerHTML = '';
    if (filtered.length === 0) { container.innerHTML = '<div style="text-align:center;color:var(--text-tertiary);padding:2rem;font-size:var(--text-sm);">Cihaz bulunamadı.</div>'; return; }

    const groupedByLab = {};
    filtered.forEach(pc => { if (!groupedByLab[pc.lab]) groupedByLab[pc.lab] = []; groupedByLab[pc.lab].push(pc); });
    for (let labName in groupedByLab) {
        const labGroup = document.createElement('div');
        labGroup.className = 'lab-group';
        const pcs = groupedByLab[labName];
        const isLabOpen = openLabs.has(labName) || searchTerm !== '';
        const header = document.createElement('div');
        header.className = `lab-header ${isLabOpen ? 'open' : ''}`;
        header.innerHTML = `<span><i class="fas fa-network-wired"></i> ${escapeHtml(labName)} <span style="color:var(--text-muted);font-size:0.6875rem;">(${pcs.length})</span></span><i class="fas fa-chevron-down"></i>`;
        const pcsContainer = document.createElement('div');
        pcsContainer.className = `lab-pcs ${isLabOpen ? 'open' : ''}`;
        header.onclick = () => {
            if (pcsContainer.classList.contains('open')) { pcsContainer.classList.remove('open'); header.classList.remove('open'); openLabs.delete(labName); }
            else { pcsContainer.classList.add('open'); header.classList.add('open'); openLabs.add(labName); }
        };
        pcs.forEach(pc => {
            const isOnline = (pc.status || '').toLowerCase() === 'online';
            const div = document.createElement('div');
            div.className = `pc-item ${selectedPc === pc.hostname ? 'active' : ''}`;
            div.onclick = () => selectPc(pc.hostname);
            const displayName = pc.display_name || pc.real_hostname || pc.hostname;
            div.innerHTML = `<div class="pc-name"><i class="fas fa-desktop" style="font-size:0.75rem;color:var(--text-tertiary);"></i> ${escapeHtml(displayName)}</div><span class="status-dot ${isOnline ? 'online' : 'offline'}"></span>`;
            pcsContainer.appendChild(div);
        });
        labGroup.appendChild(header); labGroup.appendChild(pcsContainer);
        container.appendChild(labGroup);
    }
}

function selectPc(hostname) {
    selectedPc = hostname;
    const pc = globalDevices.find(d => d.hostname === hostname);
    const displayName = pc ? (pc.display_name || pc.real_hostname || pc.hostname) : hostname;
    ['tabBtn-logs', 'tabBtn-hw'].forEach(id => { const b = document.getElementById(id); b.disabled = false; b.removeAttribute('title'); });
    document.getElementById('selectedPcName').textContent = displayName;
    document.getElementById('emptyState').style.display = 'none';
    lastSidebarHash = ''; lastHwHash = ''; lastLogsHash = '';
    renderPcList();
    document.getElementById('hwTab').style.display = currentTab === 'hw' ? 'block' : 'none';
    document.getElementById('logsTab').style.display = currentTab === 'logs' ? 'block' : 'none';
    if (currentTab === 'hw') renderHardware();
    if (currentTab === 'logs') renderLogsTable();
}

function switchTab(tab) {
    if (!selectedPc) return;
    currentTab = tab;
    lastHwHash = ''; lastLogsHash = '';
    document.getElementById('tabBtn-logs').classList.toggle('active', tab === 'logs');
    document.getElementById('tabBtn-hw').classList.toggle('active', tab === 'hw');
    document.getElementById('hwTab').style.display = tab === 'hw' ? 'block' : 'none';
    document.getElementById('logsTab').style.display = tab === 'logs' ? 'block' : 'none';
    if (tab === 'hw') renderHardware();
    if (tab === 'logs') renderLogsTable();
}

function renderHardware() {
    const container = document.getElementById('hwGridContainer');
    const hw = globalInventory.find(inv => inv.pc_name === selectedPc);
    const currentHash = hw ? JSON.stringify(hw) : 'empty';
    if (currentHash === lastHwHash) return;
    lastHwHash = currentHash;
    if (!hw || hw.cpu === '-') {
        const ip = hw ? hw.ip_address : 'Bilinmiyor';
        container.innerHTML = `<div style="grid-column:1/-1;text-align:center;padding:3rem;color:var(--text-tertiary);background:var(--bg-surface-2);border-radius:var(--radius-md);border:1px dashed var(--border-default);">
            <i class="fas fa-cog fa-spin fa-2x" style="margin-bottom:1rem;color:var(--primary-500);"></i>
            <h3 style="color:var(--text-primary);margin-bottom:0.5rem;">Envanter Bekleniyor</h3>
            <p>Bu cihazın (IP: <strong style="color:var(--text-primary);">${escapeHtml(ip)}</strong>) donanım bilgileri talep edildi.</p>
        </div>`;
        return;
    }
    const items = [
        { icon: 'fa-microchip', label: 'İşlemci (CPU)', val: hw.cpu },
        { icon: 'fa-memory', label: 'RAM Bellek', val: hw.ram },
        { icon: 'fa-chess-board', label: 'Anakart', val: hw.motherboard },
        { icon: 'fa-display', label: 'Ekran Kartı', val: hw.gpu },
        { icon: 'fa-hard-drive', label: 'Disk', val: hw.disk_info },
        { icon: 'fa-windows', label: 'İşletim Sistemi', val: hw.os_version },
        { icon: 'fa-network-wired', label: 'IP Adresi', val: hw.ip_address },
        { icon: 'fa-fingerprint', label: 'MAC Adresi', val: hw.mac_address }
    ];
    container.innerHTML = items.map(item => `<div class="hw-card"><div class="hw-icon"><i class="fas ${item.icon}"></i></div><div class="hw-info"><span class="hw-label">${item.label}</span><span class="hw-val">${escapeHtml(item.val || '-')}</span></div></div>`).join('');
    container.innerHTML += `<div style="grid-column:1/-1;text-align:right;font-size:var(--text-xs);color:var(--text-tertiary);margin-top:0.5rem;">Kimlik: <span style="font-family:var(--font-mono);">${escapeHtml(hw.pc_name)}</span> • Son güncelleme: <strong>${escapeHtml(hw.last_updated)}</strong></div>`;
}

function filterLogs(type) {
    currentLogFilter = type;
    lastLogsHash = '';
    document.querySelectorAll('.log-filter-btn').forEach(btn => btn.classList.toggle('active', btn.dataset.type === type));
    renderLogsTable();
}

function renderLogsTable() {
    const tbody = document.getElementById('logTableBody');
    const searchWord = document.getElementById('searchLogInput').value.toLowerCase();
    let pcLogs = globalLogs.filter(log => log.pc_name === selectedPc);
    // Kayıtlar kategori ve risk düzeyiyle gelir (eski şemada log_type)
    if (currentLogFilter === 'risk') pcLogs = pcLogs.filter(log => ['high', 'critical'].includes(String(log.risk_level || '').toLowerCase()));
    else if (currentLogFilter !== 'All') pcLogs = pcLogs.filter(log => (log.category || log.log_type) === currentLogFilter);
    if (searchWord) pcLogs = pcLogs.filter(log => (log.message || '').toLowerCase().includes(searchWord));
    const currentHash = searchWord + '|' + currentLogFilter + '|' + pcLogs.length + '|' + (pcLogs.length > 0 ? pcLogs[0].id : '');
    if (currentHash === lastLogsHash) return;
    lastLogsHash = currentHash;
    if (pcLogs.length === 0) {
        POps.setEmpty(tbody, { tag: 'tr', colspan: 5, icon: 'fa-magnifying-glass', title: 'Eşleşen kayıt yok', compact: true });
        return;
    }
    tbody.innerHTML = pcLogs.map(log => {
        let riskColor = 'var(--info-bg)';
        let riskTextColor = 'var(--info-text)';
        let r_level = (log.risk_level || 'info').toLowerCase();
        
        if (r_level === 'critical') { riskColor = 'rgba(239, 68, 68, 0.2)'; riskTextColor = '#ef4444'; }
        else if (r_level === 'high') { riskColor = 'rgba(249, 115, 22, 0.2)'; riskTextColor = '#f97316'; }
        else if (r_level === 'medium') { riskColor = 'var(--warning-bg)'; riskTextColor = 'var(--warning-text)'; }
        
        const cat = escapeHtml(log.category || log.log_type || 'Unknown');
        const action = escapeHtml(log.action || '-');
        const actor = escapeHtml(log.actor_id || 'System');
        const msg = escapeHtml(log.message || '');
        const reason = escapeHtml(log.reason || '');

        return `<tr>
            <td style="color:var(--text-tertiary);font-family:var(--font-mono);font-size:0.75rem;">${escapeHtml(log.timestamp)}</td>
            <td>
                <span class="log-badge" style="background:${riskColor};color:${riskTextColor};margin-bottom:0.25rem;display:inline-block;">${escapeHtml(r_level.toUpperCase())}</span><br>
                <span style="font-size:0.7rem;color:var(--text-tertiary);">${cat}</span>
            </td>
            <td>
                <div style="color:var(--text-primary);font-weight:600;font-size:0.8rem;">${action}</div>
                <div style="font-size:0.75rem;color:var(--text-muted);"><i class="fas fa-user-circle"></i> ${actor}</div>
            </td>
            <td>
                <div style="color:var(--text-primary);font-weight:500;">${msg}</div>
                ${reason ? `<div style="font-size:0.75rem;color:var(--primary-400);margin-top:0.25rem;"><i class="fas fa-info-circle"></i> Neden: ${reason}</div>` : ''}
            </td>
            <td style="text-align:center;">
                <button onclick="showInspectModal(${jsArg(log.id)})" style="background:var(--bg-surface-3);border:1px solid var(--border-default);color:var(--text-primary);padding:0.3rem 0.5rem;border-radius:4px;cursor:pointer;"><i class="fas fa-search-plus"></i></button>
            </td>
        </tr>`;
    }).join('');
}

document.addEventListener('DOMContentLoaded', () => {
    fetchLoggerData();
    popsPoll(fetchLoggerData, 5000);
});
</script>

<?php include 'includes/footer.php'; ?>