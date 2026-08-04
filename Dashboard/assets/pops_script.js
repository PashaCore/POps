// =================================================================
// POps V4.0 - GLOBAL STATE & API MANAGER
// =================================================================

const API_HTTP = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : "";
const API_WS   = (typeof OMYO_API !== 'undefined') ? OMYO_API.wsUrl('/ws/panel') : "";

// Global state
const state = {
    devices: [],
    taskQueue: [],
    selectedDeviceIds: new Set(),
    apiBaseUrl: API_HTTP,
    labsStats: {},
    customLabs: [],
    mainPcs: {},
    lastTaskId: 100,
    terminalHistory: []
};

// ============== TOAST SYSTEM ==============
function showToast(message, type = 'info', duration = 3500) {
    const container = document.getElementById('toastContainer');
    if (!container) { console.log(`[Toast ${type}]`, message); return; }
    const icons = { success: 'fa-circle-check', error: 'fa-circle-xmark', warning: 'fa-triangle-exclamation', info: 'fa-circle-info' };
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.innerHTML = `<i class="fas ${icons[type] || icons.info}"></i><span>${escapeHtml(message)}</span>`;
    container.appendChild(toast);
    setTimeout(() => { toast.style.opacity = '0'; toast.style.transform = 'translateX(20px)'; setTimeout(() => toast.remove(), 200); }, duration);
}

function escapeHtml(str) {
    if (str == null) return '';
    return String(str).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
}

// ============== API REQUEST ==============
async function apiRequest(endpoint, options = {}) {
    if (!state.apiBaseUrl) throw new Error('API URL tanımlı değil');
    const url = state.apiBaseUrl + endpoint;
    try {
        const opts = { ...options };
        // Her istekte Authorization header'ı ekle
        const authHeaders = (typeof OMYO_API !== 'undefined') ? OMYO_API.authHeader() : {};
        opts.headers = {
            ...authHeaders,
            ...(options.headers || {})
        };
        if (opts.method && opts.method.toUpperCase() !== 'GET') {
            opts.headers['Content-Type'] = 'application/json';
        }
        const response = await fetch(url, opts);
        // 401 gelirse oturumu sonlandır
        if (response.status === 401) {
            localStorage.removeItem('pops_jwt');
            window.location.href = '/login.php';
            return;
        }
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return await response.json();
    } catch (err) {
        console.warn(`API error: ${endpoint}`, err);
        throw err;
    }
}

// ============== DEVICE LOADER ==============
async function loadDevices() {
    try {
        const data = await apiRequest('/api/devices');
        if (Array.isArray(data)) {
            state.devices = data.map(d => ({
                id: d.hostname,
                hostname: d.hostname,
                real_hostname: d.real_hostname,
                lab: d.lab,
                status: d.status,
                last_seen: d.last_seen || '-',
                active_window: d.active_window || '-'
            }));
        }
        try { state.customLabs = await apiRequest('/api/custom_labs') || []; } catch (e) {}
        try { 
            const ls = await apiRequest('/api/lab_settings') || {};
            state.mainPcs = {};
            state.labLayouts = {};
            for (const lab in ls) {
                state.mainPcs[lab] = ls[lab].main_pc;
                state.labLayouts[lab] = ls[lab].layout_json;
            }
        } catch (e) {}
    } catch (e) {
        console.warn('Cihaz verisi çekilemedi.');
    }
    const stats = {};
    state.devices.forEach(d => { stats[d.lab] = (stats[d.lab] || 0) + 1; });
    state.labsStats = stats;
    document.dispatchEvent(new CustomEvent('pops_data_updated'));
}

// ============== WEBSOCKET ==============
let panelSocket = null;
function connectPanelWebSocket() {
    if (!API_WS) return;
    try {
        panelSocket = new WebSocket(API_WS);
        panelSocket.onopen = () => {
            if (window.location.pathname.includes('terminal.php')) showToast('Canlı bağlantı aktif', 'success');
        };
        panelSocket.onmessage = (event) => {
            try {
                const data = JSON.parse(event.data);
                if (data.type === 'terminal_output' && state.taskQueue) {
                    const targetId = data.id || data.pc_name;
                    const idx = state.taskQueue.findIndex(t => t.id === targetId && t.status === 'running');
                    if (idx !== -1) {
                        state.taskQueue[idx].status = 'completed';
                        state.taskQueue[idx].progress = 100;
                    }
                }
            } catch (e) {}
        };
        panelSocket.onclose = () => setTimeout(connectPanelWebSocket, 5000);
        panelSocket.onerror = () => { try { panelSocket.close(); } catch (e) {} };
    } catch (e) {
        setTimeout(connectPanelWebSocket, 5000);
    }
}

// ============== BOOT ==============
async function startRadar() {
    await loadDevices();
    setTimeout(startRadar, 3000);
}
function bootstrap() {
    connectPanelWebSocket();
    startRadar();
}
bootstrap();

// ============== POWER COMMANDS ==============
window.powerCommand = async function(targetType, action, targetName = null) {
    if (!API_HTTP) return;
    const cmd = action === 'shutdown' ? 'shutdown /s /f /t 5' : 'shutdown /r /f /t 5';
    const actionName = action === 'shutdown' ? 'KAPAT' : 'YENİDEN BAŞLAT';
    const label = targetType === 'ALL' ? 'TÜM AĞDAKİ' : (targetType === 'LAB' ? `${targetName} sınıfındaki` : targetName);
    let targets = [];
    if (targetType === 'ALL') targets = state.devices.filter(d => d.status.toLowerCase() !== 'offline').map(d => d.hostname);
    else if (targetType === 'LAB') targets = state.devices.filter(d => d.lab === targetName && d.status.toLowerCase() !== 'offline').map(d => d.hostname);
    else targets = [targetName];
    if (targets.length === 0) return showToast('Açık cihaz bulunamadı.', 'warning');
    if (!confirm(`${label} cihaz(lar)a [${actionName}] emri gönderilecek. Onaylıyor musunuz?`)) return;
    try {
        await apiRequest('/api/deploy_orchestration', {
            method: 'POST',
            body: JSON.stringify({ target_mode: 'PC', targets, taskSequence: [{ name: `Güç (${action})`, type: 'CMD', command: cmd }] })
        });
        showToast('Emir başarıyla gönderildi.', 'success');
    } catch (e) {
        showToast('Komut merkeze iletilemedi.', 'error');
    }
};

window.wakeUpCommand = async function(targetType, targetName = null) {
    if (!API_HTTP) return;
    try {
        if (targetType === 'ALL') {
            if (!confirm('Tüm ağdaki kapalı cihazları uyandırmak istediğinize emin misiniz?')) return;
            await apiRequest('/api/wake_all', { method: 'POST' });
            showToast('Tüm ağa WOL sinyali fırlatıldı.', 'success');
        } else if (targetType === 'LAB') {
            await apiRequest(`/api/wake_lab/${encodeURIComponent(targetName)}`, { method: 'POST' });
            showToast(`${targetName} sınıfına WOL sinyali gönderildi.`, 'success');
        } else if (targetType === 'PC') {
            await apiRequest(`/api/wake_pc/${encodeURIComponent(targetName)}`, { method: 'POST' });
            showToast(`${targetName} cihazına WOL sinyali gönderildi.`, 'success');
        } else {
            showToast('Toplu uyandırma için seçim yapın.', 'info');
        }
    } catch (e) {
        showToast('Sinyal gönderilemedi.', 'error');
    }
};