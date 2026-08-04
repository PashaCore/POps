<?php include 'includes/header.php'; ?>

<style>
    .top-search-container {
        background: var(--bg-surface);
        padding: var(--space-5);
        border-radius: var(--radius-lg);
        border: 1px solid var(--border-subtle);
        margin-bottom: var(--space-5);
        box-shadow: var(--shadow-xs);
    }
    .mega-search { position: relative; }
    .mega-search i { position: absolute; left: 1rem; top: 50%; transform: translateY(-50%); color: var(--primary-500); pointer-events: none; }
    .mega-search input { padding-left: 2.75rem; height: 44px; font-size: var(--text-md); }

    #searchResults { display: none; margin-top: var(--space-3); border-top: 1px solid var(--border-subtle); padding-top: var(--space-3); max-height: 280px; overflow-y: auto; }
    .search-result-item {
        background: var(--bg-surface-2);
        border: 1px solid var(--border-subtle);
        padding: 0.75rem 1rem;
        border-radius: var(--radius-md);
        display: flex;
        justify-content: space-between;
        align-items: center;
        cursor: pointer;
        margin-bottom: 0.5rem;
        transition: border-color 0.15s, background-color 0.15s;
    }
    .search-result-item:hover { border-color: var(--primary-500); background: var(--bg-surface); }
    .sr-pc-name { color: var(--text-primary); font-weight: var(--fw-semibold); font-size: var(--text-sm); }
    .sr-pc-network { color: var(--text-tertiary); font-family: var(--font-mono); font-size: 0.75rem; margin-top: 0.125rem; }
    .sr-lab { color: var(--primary-600); font-size: var(--text-xs); font-weight: var(--fw-semibold); }

    .lab-cards-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: var(--space-4); }
    .lab-card {
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: var(--radius-lg);
        padding: var(--space-5);
        cursor: pointer;
        transition: all 0.15s;
        position: relative;
        overflow: hidden;
        box-shadow: var(--shadow-xs);
    }
    .lab-card::before { content: ''; position: absolute; top: 0; left: 0; width: 3px; height: 100%; background: var(--border-default); transition: background-color 0.15s; }
    .lab-card:hover { transform: translateY(-2px); box-shadow: var(--shadow-md); border-color: var(--primary-500); }
    .lab-card:hover::before { background: var(--primary-500); }
    .lab-card-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: var(--space-4); }
    .lab-card-title { font-size: var(--text-lg); font-weight: var(--fw-semibold); color: var(--text-primary); margin: 0; }
    .lab-card-icon { color: var(--text-tertiary); font-size: 1.25rem; }
    .lab-card-stats { display: flex; gap: 0.5rem; }
    .stat-pill { flex: 1; text-align: center; padding: 0.625rem; background: var(--bg-surface-2); border-radius: var(--radius-md); border: 1px solid var(--border-subtle); }
    .stat-pill.online { border-color: var(--success-border); background: var(--success-bg); color: var(--success-text); }
    .stat-pill.offline { color: var(--danger-text); }
    .stat-val { display: block; font-size: var(--text-2xl); font-weight: var(--fw-bold); line-height: 1; }
    .stat-label { display: block; font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; margin-top: 0.25rem; color: var(--text-tertiary); }

    .wall-header { display: flex; justify-content: space-between; align-items: center; background: var(--bg-surface); padding: var(--space-4) var(--space-5); border-radius: var(--radius-lg); border: 1px solid var(--border-subtle); margin-bottom: var(--space-4); flex-wrap: wrap; gap: var(--space-3); }
    .wall-title { color: var(--primary-600); font-weight: var(--fw-semibold); font-size: var(--text-md); }

    .wall-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: var(--space-4); }
    .screen-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); overflow: hidden; cursor: pointer; transition: all 0.15s; display: flex; flex-direction: column; user-select: none; box-shadow: var(--shadow-xs); }
    .screen-card:hover { transform: translateY(-2px); border-color: var(--primary-500); box-shadow: var(--shadow-md); }
    .screen-card.offline { opacity: 0.5; filter: grayscale(100%); cursor: not-allowed; }
    .screen-card.offline:hover { transform: none; border-color: var(--border-subtle); }

    .thumb-wrapper { width: 100%; aspect-ratio: 16 / 9; background: #000; position: relative; display: flex; align-items: center; justify-content: center; border-bottom: 1px solid var(--border-subtle); pointer-events: none; }
    .thumb-img { width: 100%; height: 100%; object-fit: cover; display: none; }
    .thumb-placeholder { color: var(--text-tertiary); font-size: var(--text-sm); display: flex; flex-direction: column; align-items: center; gap: 0.5rem; opacity: 0.5; }

    .card-footer { padding: 0.625rem 0.875rem; display: flex; justify-content: space-between; align-items: center; background: var(--bg-surface); pointer-events: none; }
    .card-pc-name { font-weight: var(--fw-semibold); color: var(--text-primary); font-size: var(--text-sm); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .card-ip { font-size: 0.6875rem; color: var(--text-tertiary); font-family: var(--font-mono); margin-top: 0.125rem; }
    .card-status { width: 8px; height: 8px; border-radius: 50%; }
    .card-status.online { background: var(--success-solid); box-shadow: 0 0 6px var(--success-solid); }
    .card-status.offline { background: var(--text-muted); }

    #viewFocus { display: none; flex-direction: column; background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); overflow: hidden; box-shadow: var(--shadow-md); height: 75vh; margin-top: var(--space-4); }
    #viewFocus.fullscreen-mode { position: fixed; inset: 0; height: 100vh; width: 100vw; z-index: var(--z-modal); margin: 0; border-radius: 0; }

    .focus-header { padding: var(--space-4) var(--space-5); background: var(--bg-surface); border-bottom: 1px solid var(--border-subtle); display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: var(--space-3); }
    .live-stream-area { flex: 1; background: #000; position: relative; display: flex; align-items: center; justify-content: center; overflow: hidden; }
    #liveDisplay { width: 100%; height: 100%; object-fit: contain; display: block; z-index: 5; user-select: none; pointer-events: none; }
    #inputLayer { position: absolute; inset: 0; outline: none; z-index: 15; display: none; cursor: crosshair; }

    .hud-overlay { position: absolute; inset: var(--space-4); pointer-events: none; }
    .hud-badge { position: absolute; top: var(--space-3); right: var(--space-4); font-weight: var(--fw-semibold); font-size: 0.75rem; letter-spacing: 0.05em; padding: 0.375rem 0.75rem; border-radius: var(--radius-sm); text-transform: uppercase; }
    .hud-rec { color: var(--danger-text); background: var(--danger-bg); border: 1px solid var(--danger-border); }
    .hud-ctrl { color: var(--warning-text); background: var(--warning-bg); border: 1px solid var(--warning-border); }

    .btn-vision { padding: 0.5rem 0.875rem; border-radius: var(--radius-md); font-weight: var(--fw-semibold); cursor: pointer; font-size: var(--text-xs); border: 1px solid transparent; display: inline-flex; align-items: center; gap: 0.5rem; transition: all 0.15s; }
    .btn-vision.back { background: transparent; color: var(--text-secondary); border-color: var(--border-default); }
    .btn-vision.back:hover { background: var(--bg-surface-2); color: var(--text-primary); }
    .btn-vision.refresh { background: var(--info-bg); color: var(--info-text); border-color: var(--info-border); }
    .btn-vision.refresh:hover { background: var(--info-solid); color: white; border-color: var(--info-solid); }
    .btn-vision.diag { background: rgba(139, 92, 246, 0.10); color: #8b5cf6; border-color: rgba(139, 92, 246, 0.30); }
    .btn-vision.diag:hover { background: #8b5cf6; color: white; }
    .btn-vision.danger { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-border); }
    .btn-vision.danger:hover { background: var(--danger-solid); color: white; border-color: var(--danger-solid); }

    .control-switch { display: inline-flex; align-items: center; gap: 0.5rem; background: var(--bg-surface-2); padding: 0.4375rem 0.75rem; border-radius: var(--radius-md); border: 1px solid var(--border-subtle); font-size: 0.75rem; font-weight: var(--fw-semibold); }
    .control-switch.disabled { opacity: 0.5; pointer-events: none; }
    .switch { position: relative; display: inline-block; width: 36px; height: 20px; flex-shrink: 0; }
    .switch input { opacity: 0; width: 0; height: 0; }
    .switch-slider { position: absolute; cursor: pointer; inset: 0; background-color: var(--border-default); transition: 0.2s; border-radius: 20px; }
    .switch-slider::before { position: absolute; content: ""; height: 14px; width: 14px; left: 3px; bottom: 3px; background-color: white; transition: 0.2s; border-radius: 50%; box-shadow: var(--shadow-sm); }
    .switch input:checked + .switch-slider { background-color: var(--success-solid); }
    .switch input:checked + .switch-slider::before { transform: translateX(16px); }
    .switch.ctrl input:checked + .switch-slider { background-color: var(--warning-solid); }

    .stream-waiting { color: rgba(255,255,255,0.6); font-size: var(--text-md); font-weight: var(--fw-semibold); position: absolute; z-index: 2; text-align: center; }
    .stream-waiting i { font-size: 2.5rem; margin-bottom: 0.75rem; color: rgba(255,255,255,0.3); display: block; }

    .modal-title-icon { color: var(--primary-500); margin-right: 0.5rem; }
    .modal-title-danger { color: var(--danger-text); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-th-large"></i> POpsVision <span style="color:var(--text-tertiary);font-size:var(--text-md);font-weight:var(--fw-regular);margin-left:0.5rem;">| Laboratuvar İzleme Motoru</span></h1>
    </div>
    <div class="page-header-actions">
        <button class="btn-vision diag" onclick="App.fixOfflineCameras()"><i class="fas fa-wrench"></i> Görüntüleri Onar</button>
        <button class="btn-vision danger" onclick="App.powerCommand('ALL', 'shutdown')"><i class="fas fa-power-off"></i> Tümünü Kapat</button>
        <button class="btn-vision refresh" onclick="App.wakeUpCommand('ALL')"><i class="fas fa-bolt"></i> Tümünü Uyandır</button>
    </div>
</div>

<div class="top-search-container" id="topSearchContainer">
    <div class="mega-search">
        <i class="fas fa-search"></i>
        <input type="text" id="searchInput" placeholder="Cihaz adı, IP veya MAC adresi ile ara..." autocomplete="off">
    </div>
    <div id="searchResults"></div>
</div>

<div id="viewDashboard">
    <div class="lab-cards-grid" id="dashboardGrid">
        <div style="grid-column:1/-1;text-align:center;padding:3rem;color:var(--text-tertiary);">
            <i class="fas fa-circle-notch fa-spin" style="font-size:1.5rem;"></i>
            <div style="margin-top:0.5rem;font-size:var(--text-sm);">Ağ taranıyor...</div>
        </div>
    </div>
</div>

<div id="viewWall" style="display:none;">
    <div class="wall-header">
        <div style="display:flex;align-items:center;gap:0.75rem;">
            <button class="btn-vision back" onclick="App.switchView('dashboard')"><i class="fas fa-arrow-left"></i> Sınıflara Dön</button>
            <span class="wall-title" id="wallLabName"></span>
        </div>
        <div style="display:flex;gap:0.5rem;flex-wrap:wrap;">
            <button class="btn-vision refresh" onclick="App.wakeUpCommand('LAB')"><i class="fas fa-bolt"></i> Sınıfı Aç</button>
            <button class="btn-vision danger" onclick="App.powerCommand('LAB', 'shutdown')"><i class="fas fa-power-off"></i> Kapat</button>
            <button class="btn-vision refresh" onclick="App.fetchWallThumbnails()" id="btnRefreshGrid"><i class="fas fa-camera"></i> Ekranları Tazele</button>
        </div>
    </div>
    <div class="wall-grid" id="wallGridContainer"></div>
</div>

<div id="viewFocus" style="display:none;">
    <div class="focus-header">
        <div style="display:flex;align-items:center;gap:0.75rem;">
            <button class="btn-vision back" onclick="App.closeStream()"><i class="fas fa-arrow-left"></i> Geri</button>
            <span id="focusPcName" style="font-weight:var(--fw-semibold);color:var(--primary-600);"></span>
        </div>
        <div style="display:flex;gap:0.75rem;align-items:center;flex-wrap:wrap;">
            <select id="fpsSelector" class="fps-select" onchange="App.changeFPS()" style="padding:0.4375rem 0.625rem;border-radius:var(--radius-md);background:var(--bg-surface-2);color:var(--text-primary);border:1px solid var(--border-subtle);font-size:var(--text-xs);font-weight:var(--fw-semibold);">
                <option value="15">15 FPS (Ekonomik)</option>
                <option value="30" selected>30 FPS (Standart)</option>
                <option value="45">45 FPS (Akıcı)</option>
                <option value="60">60 FPS (Ultra)</option>
            </select>
            <button class="btn-vision refresh" onclick="App.requestSingleSnapshot()" id="btnSnapShot"><i class="fas fa-arrows-rotate"></i> Tazele</button>
            <button class="btn-vision diag" onclick="App.openDiag()"><i class="fas fa-stethoscope"></i> Teşhis</button>
            <?php if(isset($_SESSION['role']) && $_SESSION['role'] === 'superadmin'): ?>
            <button class="btn-vision danger" id="btnQuarantine" onclick="App.handleQuarantineAction()"><i class="fas fa-biohazard"></i> Karantinaya Al</button>
            <?php endif; ?>
            <button class="btn-vision back" onclick="App.toggleFullscreen()"><i class="fas fa-expand"></i> Tam Ekran</button>
            <div class="control-switch">
                <label class="switch">
                    <input type="checkbox" id="streamModeToggle" onchange="App.toggleStreamAction()">
                    <span class="switch-slider"></span>
                </label>
                <span style="color:var(--success-text);"><i class="fas fa-satellite-dish"></i> Canlı Yayın</span>
            </div>
            <div class="control-switch disabled" id="controlToggleWrapper">
                <label class="switch ctrl">
                    <input type="checkbox" id="controlModeToggle" disabled onchange="App.toggleControlMode()">
                    <span class="switch-slider"></span>
                </label>
                <span style="color:var(--warning-text);"><i class="fas fa-gamepad"></i> Kontrol</span>
            </div>
        </div>
    </div>
    <div class="live-stream-area">
        <div class="hud-overlay"><div id="hudRecBadge" class="hud-badge" style="background:transparent;border-color:rgba(255,255,255,0.3);color:rgba(255,255,255,0.6);">📷 BEKLEMEDE</div></div>
        <div class="stream-waiting" id="streamWaiting">
            <i class="fas fa-desktop"></i>
            Görüntü Bekleniyor...
        </div>
        <img id="liveDisplay" src="" draggable="false" oncontextmenu="return false;" alt=""/>
        <div id="inputLayer" tabindex="0" oncontextmenu="return false;"></div>
    </div>
</div>

<div id="diagModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-stethoscope modal-title-icon"></i> Uç Nokta Teşhisi</div>
            <button class="modal-close" onclick="App.closeDiag()"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <div id="diagConsole" style="background:var(--bg-surface-2);color:var(--success-text);font-family:var(--font-mono);padding:var(--space-4);border-radius:var(--radius-md);height:240px;overflow-y:auto;font-size:var(--text-sm);border:1px solid var(--border-subtle);margin-bottom:var(--space-4);white-space:pre-wrap;word-break:break-all;">POps Teşhis Birimi Hazır...</div>
            <?php if(($_SESSION['role'] ?? '') !== 'viewer'): ?>
            <div style="display:grid;grid-template-columns:1fr 1fr;gap:0.5rem;">
                <button class="action-btn" onclick="App.diagCommand('restart_agent')">Ajanı Yeniden Başlat</button>
                <button class="action-btn" onclick="App.diagCommand('kill_vision')">Vision Sürecini Öldür</button>
                <button class="action-btn" onclick="App.diagCommand('sync_time')">Zamanı Eşitle</button>
                <button class="action-btn eject" onclick="App.diagCommand('reboot_pc')">PC'yi Yeniden Başlat</button>
                <button class="action-btn" onclick="App.runDiagCommand('check_logs')"><i class="fas fa-file-lines"></i> Son Logları Oku</button>
                <button class="action-btn" onclick="App.runDiagCommand('restart_vision')"><i class="fas fa-eye-slash"></i> Vision'ı Yeniden Başlat</button>
            </div>
            <?php endif; ?>
        </div>
    </div>
</div>

<div id="auditModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-shield-halved modal-title-icon"></i> Kurumsal Bağlantı Oturumu</div>
            <button class="modal-close" onclick="App.closeAuditModal()"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <label>Bağlantı Türü</label>
            <select id="auditType" class="setting-input" style="background:var(--bg-surface-2);">
                <option value="routine">Rutin Uzaktan Destek (Kullanıcı Onayı İster)</option>
                <?php if(($_SESSION['role'] ?? '') !== 'viewer'): ?>
                <option value="mandatory">Zorunlu Müdahale (Anında Bağlan)</option>
                <?php endif; ?>
            </select>
            <label style="margin-top:1rem;">Bağlantı Gerekçesi</label>
            <input type="text" id="auditReason" placeholder="Örn: Ağ bağlantı sorunu çözümü...">
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="App.closeAuditModal()">İptal</button>
            <button class="btn" onclick="App.submitAuditSession()">Oturumu Başlat</button>
        </div>
    </div>
</div>

<div id="lockdownModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-biohazard modal-title-icon modal-title-danger"></i> Cihazı Karantinaya Al</div>
            <button class="modal-close" onclick="App.closeLockdownPrompt()"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <div style="color:var(--text-tertiary);margin-bottom:1rem;font-size:var(--text-sm);">Bu işlem kullanıcının oturumunu anında kilitleyecek ve cihazı ağda izole edecektir.</div>
            <label>Karantina Gerekçesi</label>
            <input type="text" id="lockdownReason" placeholder="Örn: Yasadışı aktivite tespiti...">
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="App.closeLockdownPrompt()">İptal</button>
            <button class="btn danger" onclick="App.submitLockdown()">Karantinaya Al</button>
        </div>
    </div>
</div>

<script>
const SESSION_ADMIN_ID = <?php echo isset($_SESSION['admin_id']) ? intval($_SESSION['admin_id']) : 1; ?>;
const SESSION_ADMIN_NAME = "<?php echo isset($_SESSION['username']) ? addslashes($_SESSION['username']) : 'Admin'; ?>";
const SESSION_ADMIN_ROLE = "<?php echo isset($_SESSION['role']) ? addslashes($_SESSION['role']) : 'superadmin'; ?>";

const App = {
    apiUrl: (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '',
    wsUrl: (typeof OMYO_API !== 'undefined') ? OMYO_API.wsUrl('/ws/panel') : '',
    devices: [],
    ws: null,
    wsQueue: [],
    currentView: 'dashboard',
    currentLab: null,
    currentPc: null,
    isFullscreen: false,
    isStreamActive: false,
    isControlMode: false,
    currentAuditSessionId: null,
    lastFrameTime: {},
    imageCache: {},

    init: function() {
        this.apiUrl = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '';
        this.wsUrl = (typeof OMYO_API !== 'undefined') ? OMYO_API.wsUrl('/ws/panel') : '';
        this.connectWS();
        this.syncData();
        this.setupSearch();
        this.setupInputLayer();
        setInterval(() => { this.syncData(); this.autoRecoveryCheck(); }, 5000);
        
        window.addEventListener('beforeunload', () => {
            if (this.isStreamActive && this.currentPc) {
                if (this.currentAuditSessionId) {
                    fetch(`${this.apiUrl}/api/audit/session/end`, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ session_id: this.currentAuditSessionId, status: 'Ended' }),
                        keepalive: true
                    });
                }
                fetch(`${this.apiUrl}/api/stream/stop/${this.currentPc}`, { keepalive: true });
            }
        });
    },

    setupInputLayer: function() {
        const layer = document.getElementById('inputLayer');
        if (!layer) return;
        layer.addEventListener('mousemove', e => { if (this.isControlMode) this.sendInput('mouse', 'move', e); });
        layer.addEventListener('mousedown', e => { if (this.isControlMode) this.sendInput('mouse', 'down', e); });
        layer.addEventListener('mouseup', e => { if (this.isControlMode) this.sendInput('mouse', 'up', e); });
        layer.addEventListener('wheel', e => { if (this.isControlMode) this.sendScrollInput(e); });
        layer.addEventListener('keydown', e => { if (this.isControlMode) { e.preventDefault(); this.sendInput('keyboard', 'down', e); } });
        layer.addEventListener('keyup', e => { if (this.isControlMode) { e.preventDefault(); this.sendInput('keyboard', 'up', e); } });
    },

    setupSearch: function() {
        const input = document.getElementById('searchInput');
        if (input) input.addEventListener('input', (e) => this.handleSearch(e.target.value));
    },

    connectWS: function() {
        if (!this.wsUrl) return;
        try {
            this.ws = new WebSocket(this.wsUrl);
            this.ws.onopen = () => { while (this.wsQueue.length > 0 && this.ws.readyState === WebSocket.OPEN) this.ws.send(this.wsQueue.shift()); };
            this.ws.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    if (data.type === 'result' && document.getElementById('diagModal').classList.contains('open')) {
                        const c = document.getElementById('diagConsole');
                        c.innerText += `\n[SİSTEM ÇIKTISI]:\n${data.output}\n`;
                        c.scrollTop = c.scrollHeight;
                    }
                    if (data.type === 'vision_rejected') {
                        showToast('Karşı taraf bağlantıyı reddetti.', 'error');
                        document.getElementById('streamModeToggle').checked = false;
                        if (this.currentView === 'live') this.stopLiveStream();
                    }
                    if (data.type === 'thumbnail' || data.type === 'stream_frame') {
                        this.lastFrameTime[data.hw_id] = Date.now();
                        this.imageCache[data.hw_id] = data.image;
                        if (this.currentView === 'wall') {
                            const img = document.getElementById(`thumb_${data.hw_id}`);
                            const plc = document.getElementById(`place_${data.hw_id}`);
                            if (img) { img.src = 'data:image/jpeg;base64,' + data.image; img.style.display = 'block'; }
                            if (plc) plc.style.display = 'none';
                        }
                        if (this.currentView === 'focus' && this.currentPc === data.hw_id) {
                            if (data.type === 'stream_frame') {
                                this.isStreamActive = true;
                                document.getElementById('streamWaiting').style.display = 'none';
                                const img = document.getElementById('liveDisplay');
                                if (img) { img.style.display = 'block'; img.src = 'data:image/jpeg;base64,' + data.image; }
                                
                                const badge = document.getElementById('hudRecBadge');
                                if (badge && !this.isControlMode) {
                                    badge.className = 'hud-badge hud-rec';
                                    badge.innerHTML = '🔴 CANLI YAYIN';
                                }
                            } else if (data.type === 'thumbnail' && !this.isStreamActive) {
                                const img = document.getElementById('liveDisplay');
                                if (img) { img.style.display = 'block'; img.src = 'data:image/jpeg;base64,' + data.image; }
                            }
                        }
                    }
                } catch (e) {}
            };
            this.ws.onclose = () => setTimeout(() => this.connectWS(), 2000);
        } catch (e) { setTimeout(() => this.connectWS(), 2000); }
    },

    safeWsSend: function(payload) {
        if (!this.ws || this.ws.readyState !== WebSocket.OPEN) { this.wsQueue.push(payload); return false; }
        this.ws.send(payload);
        return true;
    },

    autoRecoveryCheck: function() {
        if (this.currentView !== 'wall') return;
        const now = Date.now();
        this.devices.filter(d => d.lab === this.currentLab && d.status.toLowerCase() === 'online').forEach(pc => {
            const lastSeen = this.lastFrameTime[pc.hostname] || 0;
            if (lastSeen > 0 && (now - lastSeen) > 25000) {
                this.lastFrameTime[pc.hostname] = now;
                const fix = 'taskkill /F /IM POpsWatchdog.exe & taskkill /F /IM POpsVision.exe & schtasks /run /tn "POpsWatchdogLauncher"';
                this.safeWsSend(JSON.stringify({ type: 'remote_input', device: pc.hostname, action: 'execute', script_path: fix, task_id: 999 }));
            }
        });
    },

    fixOfflineCameras: function() {
        if (!confirm('Ağdaki tüm açık cihazlarda kamera motoru yeniden başlatılacak. Emin misiniz?')) return;
        const onlinePcs = this.devices.filter(d => d.status.toLowerCase() === 'online');
        if (onlinePcs.length === 0) return showToast('Ağda açık cihaz yok.', 'warning');
        const fix = 'taskkill /F /IM POpsWatchdog.exe & taskkill /F /IM POpsVision.exe & schtasks /run /tn "POpsWatchdogLauncher"';
        onlinePcs.forEach(pc => this.safeWsSend(JSON.stringify({ type: 'remote_input', device: pc.hostname, action: 'execute', script_path: fix, task_id: 888 })));
        showToast('Onarma sinyali gönderildi. 10-15 saniye bekleyin.', 'info');
    },

    openDiag: function() { if (!this.currentPc) return; document.getElementById('diagConsole').innerText = 'Konsol hazır...'; openModal('diagModal'); },
    closeDiag: function() { closeModal('diagModal'); },

    runDiagCommand: function(cmdType) {
        let script = '';
        const taskId = Math.floor(Math.random() * 10000);
        document.getElementById('diagConsole').innerText = '[İşlem Başlatıldı] Görev bekleniyor...\n';
        if (cmdType === 'check_process') script = 'tasklist | findstr /I "POps"';
        else if (cmdType === 'check_logs') script = `powershell.exe -ExecutionPolicy Bypass -Command "$log = Get-ChildItem 'C:\\POpsLogs\\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1; if($log){ Get-Content $log.FullName -Tail 10 }else{ Write-Host 'Log bulunamadi.' }"`;
        else if (cmdType === 'restart_vision') script = 'taskkill /F /IM POpsVision.exe';
        else if (cmdType === 'restart_watchdog') script = 'taskkill /F /IM POpsWatchdog.exe & schtasks /run /tn "POpsWatchdogLauncher"';
        this.safeWsSend(JSON.stringify({ type: 'remote_input', device: this.currentPc, action: 'execute', script_path: script, task_id: taskId }));
    },

    diagCommand: function(cmd) {
        const map = { restart_agent: 'taskkill /F /IM POpsAgent.exe & schtasks /run /tn "POpsLauncher"', kill_vision: 'taskkill /F /IM POpsVision.exe', sync_time: 'w32tm /resync', reboot_pc: 'shutdown /r /t 5' };
        const script = map[cmd] || '';
        if (!script) return;
        const taskId = Math.floor(Math.random() * 10000);
        this.safeWsSend(JSON.stringify({ type: 'remote_input', device: this.currentPc, action: 'execute', script_path: script, task_id: taskId }));
        showToast('Teşhis komutu gönderildi.', 'info');
    },

    requestSingleSnapshot: function() {
        if (!this.currentPc) return;
        const btn = document.getElementById('btnSnapShot');
        if (btn) btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Bekleniyor...';
        this.safeWsSend(JSON.stringify({ type: 'remote_input', device: this.currentPc, action: 'get_thumbnail' }));
        setTimeout(() => { if (btn) btn.innerHTML = '<i class="fas fa-arrows-rotate"></i> Tazele'; }, 2000);
    },

    fetchWallThumbnails: function() {
        if (!this.currentLab) return;
        this.devices.filter(d => d.lab === this.currentLab && d.status.toLowerCase() === 'online').forEach(pc => {
            this.safeWsSend(JSON.stringify({ type: 'remote_input', device: pc.hostname, action: 'get_thumbnail' }));
        });
    },

    openStream: function(hostname, displayName) {
        this.currentPc = hostname;
        this.switchView('focus');
        document.getElementById('focusPcName').innerText = `${displayName} (${hostname})`;
        if (this.imageCache[hostname]) {
            document.getElementById('streamWaiting').style.display = 'none';
            const img = document.getElementById('liveDisplay');
            img.src = 'data:image/jpeg;base64,' + this.imageCache[hostname];
            img.style.display = 'block';
        } else {
            document.getElementById('liveDisplay').style.display = 'none';
            document.getElementById('streamWaiting').innerHTML = '<i class="fas fa-desktop"></i>Görüntü Bekleniyor...';
            document.getElementById('streamWaiting').style.display = 'block';
        }
        document.getElementById('streamModeToggle').checked = false;
        this.isStreamActive = false;
        const ctrlToggle = document.getElementById('controlModeToggle');
        if (ctrlToggle) {
            ctrlToggle.checked = false;
            ctrlToggle.disabled = true;
        }
        document.getElementById('controlToggleWrapper').classList.add('disabled');
        
        const qBtn = document.getElementById('btnQuarantine');
        if (qBtn) {
            const pcData = this.devices.find(d => d.hostname === hostname);
            if (pcData && pcData.is_quarantined) {
                qBtn.innerHTML = '<i class="fas fa-unlock"></i> Karantinayı Kaldır';
                qBtn.className = 'btn-vision success';
            } else {
                qBtn.innerHTML = '<i class="fas fa-biohazard"></i> Karantinaya Al';
                qBtn.className = 'btn-vision danger';
            }
        }
    },

    toggleStreamAction: function() {
        const toggle = document.getElementById('streamModeToggle');
        if (toggle.checked) {
            toggle.checked = false;
            openModal('auditModal');
            document.getElementById('auditReason').value = '';
        } else { this.stopLiveStream(); }
    },

    closeAuditModal: function() { closeModal('auditModal'); document.getElementById('streamModeToggle').checked = false; },

    submitAuditSession: async function() {
        const reason = document.getElementById('auditReason').value.trim();
        const type = document.getElementById('auditType').value;
        if (!reason) return showToast('Gerekçe girin.', 'warning');
        closeModal('auditModal');
        document.getElementById('streamModeToggle').checked = true;
        try {
            const res = await fetch(`${this.apiUrl}/api/audit/session/start`, {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ admin_id: SESSION_ADMIN_ID, admin_name: SESSION_ADMIN_NAME, admin_role: SESSION_ADMIN_ROLE, target_pc: this.currentPc, reason, is_mandatory: type === 'mandatory' })
            });
            const data = await res.json();
            if (data.status === 'success') { this.currentAuditSessionId = data.session_id; this.startLiveStream(data.countdown_seconds); }
        } catch (e) { showToast('Bağlantı başlatılamadı.', 'error'); document.getElementById('streamModeToggle').checked = false; }
    },

    openLockdownPrompt: function() { openModal('lockdownModal'); document.getElementById('lockdownReason').value = ''; },
    closeLockdownPrompt: function() { closeModal('lockdownModal'); },

    handleQuarantineAction: async function() {
        if (!this.currentPc) return;
        const pcData = this.devices.find(d => d.hostname === this.currentPc);
        if (pcData && pcData.is_quarantined) {
            if (!confirm('Karantina kaldırılsın mı?')) return;
            try {
                await fetch(`${this.apiUrl}/api/security/unlock`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ admin_id: SESSION_ADMIN_ID, admin_name: SESSION_ADMIN_NAME, target_pc: this.currentPc, reason: 'Karantina Kaldırıldı' }) });
                showToast('Kilit açma sinyali gönderildi!', 'success');
                // Optimistic UI update
                pcData.is_quarantined = false;
                const qBtn = document.getElementById('btnQuarantine');
                if (qBtn) {
                    qBtn.innerHTML = '<i class="fas fa-biohazard"></i> Karantinaya Al';
                    qBtn.className = 'btn-vision danger';
                }
            } catch (e) { showToast('Hata oluştu', 'error'); }
        } else {
            this.openLockdownPrompt();
        }
    },

    submitLockdown: async function() {
        const reason = document.getElementById('lockdownReason').value.trim();
        if (!reason) return showToast('Gerekçe girmek zorunlu.', 'warning');
        closeModal('lockdownModal');
        try {
            await fetch(`${this.apiUrl}/api/security/lockdown`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ admin_id: SESSION_ADMIN_ID, admin_name: SESSION_ADMIN_NAME, target_pc: this.currentPc, reason }) });
            showToast('Karantina sinyali gönderildi!', 'warning');
            const pcData = this.devices.find(d => d.hostname === this.currentPc);
            if (pcData) pcData.is_quarantined = true;
            const qBtn = document.getElementById('btnQuarantine');
            if (qBtn) {
                qBtn.innerHTML = '<i class="fas fa-unlock"></i> Karantinayı Kaldır';
                qBtn.className = 'btn-vision success';
            }
        } catch (e) {}
    },

    startLiveStream: function(countdown = 0) {
        if (!this.currentPc) return;
        this.isStreamActive = true;
        
        if (countdown > 0) {
            document.getElementById('streamWaiting').innerHTML = `<i class="fas fa-clock fa-spin" style="margin-bottom:0.75rem;font-size:2.5rem;display:block;"></i>Zorunlu müdahale başlatıldı.<br><span style="font-size:var(--text-sm);font-weight:normal;color:var(--text-tertiary);">Kullanıcıya ${countdown} saniye süre verildi...</span>`;
            document.getElementById('streamWaiting').style.display = 'block';
            let c = countdown;
            const timer = setInterval(() => {
                c--;
                if (c <= 0 || !this.isStreamActive) {
                    clearInterval(timer);
                    if (this.isStreamActive) document.getElementById('streamWaiting').innerHTML = '<i class="fas fa-satellite-dish fa-spin" style="margin-bottom:0.75rem;font-size:2.5rem;display:block;"></i>Görüntü aktarımı başlıyor...';
                } else {
                    document.getElementById('streamWaiting').innerHTML = `<i class="fas fa-clock fa-spin" style="margin-bottom:0.75rem;font-size:2.5rem;display:block;"></i>Zorunlu müdahale başlatıldı.<br><span style="font-size:var(--text-sm);font-weight:normal;color:var(--text-tertiary);">Kullanıcıya ${c} saniye süre verildi...</span>`;
                }
            }, 1000);
        } else {
            document.getElementById('streamWaiting').innerHTML = '<i class="fas fa-satellite-dish fa-spin" style="margin-bottom:0.75rem;font-size:2.5rem;display:block;"></i>Kullanıcı onayı bekleniyor...';
        }
        
        const badge = document.getElementById('hudRecBadge');
        badge.className = 'hud-badge hud-rec'; badge.innerHTML = '● CANLI YAYIN';
        document.getElementById('controlToggleWrapper').classList.remove('disabled');
        const ctrlToggle = document.getElementById('controlModeToggle');
        if (ctrlToggle) ctrlToggle.disabled = false;
    },

    stopLiveStream: async function() {
        if (!this.currentPc) return;
        this.isStreamActive = false;
        document.getElementById('streamModeToggle').checked = false;
        const ctrlToggle = document.getElementById('controlModeToggle');
        if (ctrlToggle) {
            if (ctrlToggle.checked) { ctrlToggle.checked = false; this.toggleControlMode(); }
            ctrlToggle.disabled = true;
        }
        document.getElementById('controlToggleWrapper').classList.add('disabled');
        const badge = document.getElementById('hudRecBadge');
        badge.className = 'hud-badge'; badge.style.background = 'transparent'; badge.style.color = 'rgba(255,255,255,0.6)'; badge.style.borderColor = 'rgba(255,255,255,0.3)'; badge.innerHTML = '📷 BEKLEMEDE';
        try { await fetch(`${this.apiUrl}/api/stream/stop/${this.currentPc}`); } catch (e) {}
        if (this.currentAuditSessionId) {
            try { await fetch(`${this.apiUrl}/api/audit/session/end`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ session_id: this.currentAuditSessionId, status: 'Ended' }) }); this.currentAuditSessionId = null; } catch (e) {}
        }
    },

    closeStream: function() {
        if (this.currentPc) {
            this.stopLiveStream().then(() => { this.currentPc = null; if (this.isFullscreen) this.toggleFullscreen(); this.switchView('wall'); });
        } else { this.switchView('dashboard'); }
    },

    toggleControlMode: function() {
        const toggle = document.getElementById('controlModeToggle');
        this.isControlMode = toggle.checked;
        const badge = document.getElementById('hudRecBadge');
        const inputLayer = document.getElementById('inputLayer');
        if (this.isControlMode) { badge.className = 'hud-badge hud-ctrl'; badge.innerHTML = '🎮 KONTROL AKTİF'; inputLayer.style.display = 'block'; inputLayer.focus(); }
        else { badge.className = this.isStreamActive ? 'hud-badge hud-rec' : 'hud-badge'; badge.innerHTML = this.isStreamActive ? '● CANLI YAYIN' : '📷 BEKLEMEDE'; inputLayer.style.display = 'none'; }
    },

    toggleFullscreen: function() {
        const view = document.getElementById('viewFocus');
        if (!this.isFullscreen) { if (view.requestFullscreen) view.requestFullscreen(); view.classList.add('fullscreen-mode'); this.isFullscreen = true; }
        else { if (document.exitFullscreen) document.exitFullscreen(); view.classList.remove('fullscreen-mode'); this.isFullscreen = false; }
    },

    changeFPS: function() {
        if (!this.currentPc || !this.isStreamActive) return;
        const fps = document.getElementById('fpsSelector').value;
        this.safeWsSend(JSON.stringify({ type: 'remote_input', device: this.currentPc, action: 'set_fps', fps: parseInt(fps) }));
    },

    sendInput: function(type, action, e) {
        if (this.currentView !== 'focus' || !this.isControlMode || !this.ws || this.ws.readyState !== WebSocket.OPEN) return;
        const display = document.getElementById('liveDisplay');
        if (!display) return;
        if (type === 'mouse') {
            const rect = display.getBoundingClientRect();
            const nw = display.naturalWidth;
            const nh = display.naturalHeight;
            if (!nw || !nh) return;
            
            const wRatio = rect.width / nw;
            const hRatio = rect.height / nh;
            const fitRatio = Math.min(wRatio, hRatio);
            
            const renderW = nw * fitRatio;
            const renderH = nh * fitRatio;
            const offsetX = (rect.width - renderW) / 2;
            const offsetY = (rect.height - renderH) / 2;
            
            const clickX = e.clientX - rect.left - offsetX;
            const clickY = e.clientY - rect.top - offsetY;
            
            let xN = Math.max(0, Math.min(1, clickX / renderW));
            let yN = Math.max(0, Math.min(1, clickY / renderH));
            
            let x = Math.round(xN * (nw / 0.75));
            let y = Math.round(yN * (nh / 0.75));
            
            if (action === 'down' || action === 'move') {
                this.ws.send(JSON.stringify({ type: 'remote_input', device: this.currentPc, input_type: 'mouse_move', x, y, relative: false }));
            }
            if (action === 'down' || action === 'up') {
                let btn = 'left'; if (e.button === 1) btn = 'middle'; if (e.button === 2) btn = 'right';
                this.ws.send(JSON.stringify({ type: 'remote_input', device: this.currentPc, input_type: 'mouse_click', button: btn, is_down: (action === 'down'), double: false }));
            }
        } else if (type === 'keyboard') {
            this.ws.send(JSON.stringify({ type: 'remote_input', device: this.currentPc, input_type: 'keyboard', key: e.key, is_down: (action === 'down') }));
        }
    },

    sendScrollInput: function(e) {
        if (this.currentView !== 'focus' || !this.isControlMode || !this.ws || this.ws.readyState !== WebSocket.OPEN) return;
        const delta = e.deltaY > 0 ? -120 : 120;
        this.ws.send(JSON.stringify({ type: 'remote_input', device: this.currentPc, input_type: 'mouse_wheel', delta, horizontal: false }));
    },

    syncData: async function() {
        if (!this.apiUrl) return;
        try {
            const [devRes, invRes] = await Promise.all([fetch(`${this.apiUrl}/api/devices`), fetch(`${this.apiUrl}/api/inventory`).catch(() => ({ ok: false }))]);
            if (!devRes.ok) return;
            const rawDevs = await devRes.json();
            let inv = [];
            if (invRes && invRes.ok) inv = await invRes.json();
            this.devices = rawDevs.map(d => { const hw = inv.find(i => i.pc_name === d.hostname); return { ...d, ip: hw ? hw.ip_address : '', mac: hw ? hw.mac_address : '' }; });
            this.renderUI();
        } catch (e) {}
    },

    switchView: function(view) {
        this.currentView = view;
        document.getElementById('viewDashboard').style.display = view === 'dashboard' ? 'block' : 'none';
        document.getElementById('viewWall').style.display = view === 'wall' ? 'block' : 'none';
        document.getElementById('viewFocus').style.display = view === 'focus' ? 'flex' : 'none';
        document.getElementById('topSearchContainer').style.display = view === 'focus' ? 'none' : 'block';
        if (view === 'dashboard') this.currentLab = null;
        this.renderUI();
    },

    renderUI: function() {
        if (this.currentView === 'dashboard') this.renderDashboard();
        if (this.currentView === 'wall') this.renderWall();
    },

    renderDashboard: function() {
        const grid = document.getElementById('dashboardGrid');
        const uniqueLabs = [...new Set(this.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar').sort();
        if (uniqueLabs.length === 0) { grid.innerHTML = '<div style="grid-column:1/-1;text-align:center;padding:3rem;color:var(--text-tertiary);">Sistemde laboratuvar bulunamadı.</div>'; return; }
        let html = '';
        uniqueLabs.forEach(lab => {
            const pcs = this.devices.filter(d => d.lab === lab);
            const online = pcs.filter(d => d.status.toLowerCase() === 'online').length;
            html += `
                <div class="lab-card" onclick="App.openLab('${escapeHtml(lab)}')">
                    <div class="lab-card-header">
                        <h3 class="lab-card-title">${escapeHtml(lab)}</h3>
                        <i class="fas fa-network-wired lab-card-icon"></i>
                    </div>
                    <div class="lab-card-stats">
                        <div class="stat-pill online"><span class="stat-val">${online}</span><span class="stat-label">Online</span></div>
                        <div class="stat-pill offline"><span class="stat-val">${pcs.length - online}</span><span class="stat-label">Kapalı</span></div>
                    </div>
                </div>`;
        });
        if (grid.innerHTML !== html) grid.innerHTML = html;
    },

    openLab: function(labName) {
        this.currentLab = labName;
        document.getElementById('wallLabName').innerHTML = `${escapeHtml(labName)} <span style="color:var(--text-tertiary);font-size:var(--text-sm);margin-left:0.5rem;">(${this.devices.filter(d => d.lab === labName).length} cihaz)</span>`;
        this.switchView('wall');
        setTimeout(() => this.fetchWallThumbnails(), 1000);
    },

    renderWall: function() {
        if (!this.currentLab) return;
        const container = document.getElementById('wallGridContainer');
        const pcs = this.devices.filter(d => d.lab === this.currentLab);
        pcs.sort((a, b) => (a.display_name || a.real_hostname || a.hostname).localeCompare(b.display_name || b.real_hostname || b.hostname, undefined, { numeric: true }));
        let html = '';
        pcs.forEach(pc => {
            const isOnline = pc.status.toLowerCase() === 'online';
            const dName = pc.display_name || pc.real_hostname || pc.hostname;
            const cachedImg = this.imageCache[pc.hostname];
            const imgSrc = cachedImg ? ('data:image/jpeg;base64,' + cachedImg) : '';
            const imgStyle = cachedImg ? 'display:block;' : 'display:none;';
            const placeStyle = cachedImg ? 'display:none;' : 'display:flex;';
            
            html += `
                <div class="screen-card ${isOnline ? '' : 'offline'}" id="vcard_${pc.hostname}" ${isOnline ? `ondblclick="App.openStream('${pc.hostname}', '${escapeHtml(dName)}')"` : ''}>
                    <div class="thumb-wrapper">
                        <img id="thumb_${pc.hostname}" class="thumb-img" src="${imgSrc}" style="${imgStyle}" alt="">
                        <div class="thumb-placeholder" id="place_${pc.hostname}" style="${placeStyle}">
                            <i class="fas ${isOnline ? 'fa-camera' : 'fa-power-off'}" style="font-size:1.5rem;"></i>
                            <span>${isOnline ? 'Bekleniyor...' : 'Kapalı'}</span>
                        </div>
                    </div>
                    <div class="card-footer">
                        <div style="overflow:hidden;">
                            <div class="card-pc-name">${escapeHtml(dName)}</div>
                            <div class="card-ip">${pc.ip || 'IP Yok'}</div>
                        </div>
                        <div class="card-status ${isOnline ? 'online' : 'offline'}"></div>
                    </div>
                </div>`;
        });
        container.innerHTML = html;
    },

    handleSearch: function(query) {
        const resDiv = document.getElementById('searchResults');
        query = query.toLowerCase().trim();
        if (!query) { resDiv.style.display = 'none'; return; }
        const matches = this.devices.filter(d => {
            if (!d.lab || d.lab === 'Atanmamis_Cihazlar') return false;
            return `${d.hostname} ${d.real_hostname || ''} ${d.ip || ''} ${d.mac || ''}`.toLowerCase().includes(query);
        });
        if (matches.length === 0) { resDiv.innerHTML = '<div style="text-align:center;padding:var(--space-3);color:var(--text-tertiary);">Sonuç bulunamadı.</div>'; }
        else {
            let html = '';
            matches.slice(0, 5).forEach(m => {
                html += `<div class="search-result-item" onclick="App.jumpToSearch('${escapeHtml(m.lab)}', '${m.hostname}')">
                    <div>
                        <div class="sr-pc-name"><i class="fas fa-desktop" style="margin-right:0.375rem;color:var(--text-tertiary);"></i> ${escapeHtml(m.real_hostname || m.hostname)}</div>
                        <div class="sr-pc-network">IP: ${m.ip || '-'} • ${m.hostname}</div>
                    </div>
                    <span class="sr-lab"><i class="fas fa-sitemap"></i> ${escapeHtml(m.lab)}</span>
                </div>`;
            });
            resDiv.innerHTML = html;
        }
        resDiv.style.display = 'block';
    },

    jumpToSearch: function(lab, pc) {
        document.getElementById('searchInput').value = '';
        document.getElementById('searchResults').style.display = 'none';
        this.openLab(lab);
        setTimeout(() => {
            const card = document.getElementById(`vcard_${pc}`);
            if (card) { card.scrollIntoView({ behavior: 'smooth', block: 'center' }); card.style.boxShadow = '0 0 20px var(--primary-500)'; card.style.borderColor = 'var(--primary-500)'; setTimeout(() => { card.style.boxShadow = ''; card.style.borderColor = ''; }, 2000); }
        }, 300);
    },

    powerCommand: async function(targetType, action) {
        if (!this.apiUrl) return;
        const cmd = action === 'shutdown' ? 'shutdown /s /f /t 5' : 'shutdown /r /f /t 5';
        const label = targetType === 'ALL' ? 'tüm sistemdeki' : `${this.currentLab} sınıfındaki`;
        if (!confirm(`${label} açık cihazlara [KAPAT] emri fırlatılacak. Emin misiniz?`)) return;
        const targets = targetType === 'ALL'
            ? this.devices.filter(d => d.status.toLowerCase() !== 'offline').map(d => d.hostname)
            : this.devices.filter(d => d.lab === this.currentLab && d.status.toLowerCase() !== 'offline').map(d => d.hostname);
        if (targets.length === 0) return showToast('Açık cihaz yok.', 'warning');
        try {
            await fetch(`${this.apiUrl}/api/deploy_orchestration`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ target_mode: 'PC', targets, taskSequence: [{ name: 'Güç Kapat', type: 'CMD', command: cmd }] }) });
            showToast('Emir gönderildi.', 'success');
        } catch (e) { showToast('Hata oluştu.', 'error'); }
    },

    wakeUpCommand: async function(targetType) {
        if (!this.apiUrl) return;
        try {
            if (targetType === 'ALL') {
                if (!confirm('Tüm ağı uyandırmak istediğinize emin misiniz?')) return;
                await fetch(`${this.apiUrl}/api/wake_all`, { method: 'POST' });
            } else {
                await fetch(`${this.apiUrl}/api/wake_lab/${encodeURIComponent(this.currentLab)}`, { method: 'POST' });
            }
            showToast('WOL sinyali gönderildi.', 'success');
        } catch (e) { showToast('Hata oluştu.', 'error'); }
    }
};

function escapeHtml(str) { return String(str || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

document.addEventListener('DOMContentLoaded', () => { App.init(); });
</script>

<?php include 'includes/footer.php'; ?>