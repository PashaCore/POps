<?php include 'includes/header.php'; ?>

<style>
    .dashboard-stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: var(--space-4); margin-bottom: var(--space-6); }

    .control-panel { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-4); margin-bottom: var(--space-5); display: flex; flex-wrap: wrap; gap: var(--space-3); align-items: center; }
    .control-group { display: flex; align-items: center; gap: 0.5rem; }

    .task-group-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); margin-bottom: var(--space-3); overflow: hidden; transition: box-shadow 0.15s, border-color 0.15s; }
    .task-group-card:hover { box-shadow: var(--shadow-sm); }
    .task-group-header { padding: var(--space-4) var(--space-5); display: flex; justify-content: space-between; align-items: center; cursor: pointer; background: var(--bg-surface); transition: background-color 0.15s; user-select: none; gap: var(--space-3); }
    .task-group-header:hover { background: var(--bg-surface-2); }
    .task-group-card.expanded .task-group-header { border-bottom: 1px solid var(--border-subtle); }
    .task-info-area { display: flex; flex-direction: column; gap: 0.375rem; flex: 1; min-width: 0; }
    .task-lab { font-weight: var(--fw-semibold); color: var(--primary-600); font-size: var(--text-sm); display: flex; align-items: center; gap: 0.5rem; }
    .task-cmd { font-family: var(--font-mono); font-size: var(--text-sm); color: var(--text-secondary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 600px; }

    .task-badges { display: flex; gap: 0.5rem; align-items: center; flex-shrink: 0; }
    .badge-box { padding: 0.375rem 0.625rem; border-radius: var(--radius-sm); font-size: 0.75rem; font-weight: var(--fw-semibold); display: inline-flex; align-items: center; gap: 0.375rem; border: 1px solid; }
    .badge-box.total { background: var(--bg-surface-2); color: var(--text-primary); border-color: var(--border-subtle); }
    .badge-box.success { background: var(--success-bg); color: var(--success-text); border-color: var(--success-border); }

    .toggle-icon { color: var(--text-tertiary); transition: transform 0.2s; }
    .task-group-card.expanded .toggle-icon { transform: rotate(180deg); color: var(--primary-500); }
    .task-details { display: none; background: var(--bg-surface-2); padding: var(--space-4); }
    .task-group-card.expanded .task-details { display: block; }

    .status-text { font-weight: var(--fw-semibold); }
    .status-text.success { color: var(--success-text); }
    .status-text.failed { color: var(--danger-text); }
    .status-text.pending { color: var(--warning-text); }
    .status-text.paused { color: var(--text-tertiary); font-style: italic; }
    .status-text.running { color: var(--info-text); }

    .btn-action { padding: 0.4375rem 0.75rem; border-radius: var(--radius-sm); font-weight: var(--fw-semibold); cursor: pointer; transition: all 0.15s; border: 1px solid; background: transparent; font-size: 0.75rem; display: inline-flex; align-items: center; gap: 0.375rem; }
    .btn-action.stop { color: var(--danger-text); border-color: var(--danger-border); background: var(--danger-bg); }
    .btn-action.stop:hover { background: var(--danger-solid); color: white; border-color: var(--danger-solid); }
    .btn-action.pause { color: var(--warning-text); border-color: var(--warning-border); background: var(--warning-bg); }
    .btn-action.pause:hover { background: var(--warning-solid); color: white; border-color: var(--warning-solid); }
    .btn-action.resume { color: var(--success-text); border-color: var(--success-border); background: var(--success-bg); }
    .btn-action.resume:hover { background: var(--success-solid); color: white; border-color: var(--success-solid); }
    .btn-action.retry { color: var(--info-text); border-color: var(--info-border); background: var(--info-bg); }
    .btn-action.retry:hover { background: var(--info-solid); color: white; border-color: var(--info-solid); }
    .btn-action.icon { padding: 0.375rem 0.5rem; }

    .log-toolbar { display: flex; gap: 0.5rem; padding: var(--space-4); background: var(--bg-surface-2); border-bottom: 1px solid var(--border-subtle); flex-wrap: wrap; }
    .log-toolbar select, .log-toolbar input { padding: 0.5rem 0.75rem; border-radius: var(--radius-md); font-size: var(--text-sm); }
    .log-toolbar input { flex: 1; min-width: 200px; }

    .agent-logs-container { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); overflow: hidden; box-shadow: var(--shadow-xs); }
    .log-list { max-height: 500px; overflow-y: auto; padding: var(--space-3); }
    .log-line { display: flex; gap: 0.75rem; padding: 0.625rem 0.875rem; border-radius: var(--radius-sm); margin-bottom: 0.25rem; background: var(--bg-surface-2); transition: background-color 0.1s; align-items: center; border-left: 3px solid transparent; font-family: var(--font-mono); font-size: 0.8125rem; }
    .log-line:hover { background: var(--bg-surface); }
    .log-time { color: var(--primary-500); min-width: 70px; font-size: 0.75rem; flex-shrink: 0; }
    .log-pc { color: var(--text-primary); font-weight: var(--fw-semibold); min-width: 140px; font-size: 0.8125rem; flex-shrink: 0; font-family: var(--font-sans); }
    .log-msg { color: var(--text-secondary); flex: 1; word-break: break-word; font-family: var(--font-sans); }
    .log-line.type-Error { border-left-color: var(--danger-solid); background: var(--danger-bg); }
    .log-line.type-Error .log-msg { color: var(--danger-text); }
    .log-line.type-System { border-left-color: var(--text-muted); }
    .log-line.type-Deploy { border-left-color: var(--info-solid); }
    .log-line.type-Network { border-left-color: var(--warning-solid); }

    .log-badge { padding: 0.25rem 0.5rem; border-radius: var(--radius-sm); font-size: 0.6875rem; font-weight: var(--fw-semibold); text-align: center; min-width: 80px; text-transform: uppercase; letter-spacing: 0.05em; }
    .log-badge.badge-System { background: rgba(107, 114, 128, 0.15); color: var(--text-tertiary); }
    .log-badge.badge-Deploy { background: var(--info-bg); color: var(--info-text); }
    .log-badge.badge-AppStart { background: var(--warning-bg); color: var(--warning-text); }
    .log-badge.badge-File { background: rgba(6, 182, 212, 0.15); color: #06b6d4; }
    .log-badge.badge-USB { background: rgba(139, 92, 246, 0.15); color: #8b5cf6; }
    .log-badge.badge-Network { background: var(--warning-bg); color: var(--warning-text); }
    .log-badge.badge-Error { background: var(--danger-bg); color: var(--danger-text); }

    .code-block { background: var(--bg-app); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); overflow: hidden; margin-bottom: var(--space-3); }
    .code-header { background: var(--bg-surface-2); padding: 0.5rem 0.875rem; font-size: 0.75rem; color: var(--text-tertiary); font-weight: var(--fw-semibold); border-bottom: 1px solid var(--border-subtle); display: flex; align-items: center; gap: 0.5rem; }
    .code-content { padding: 0.875rem; margin: 0; font-family: var(--font-mono); font-size: var(--text-sm); white-space: pre-wrap; word-wrap: break-word; color: var(--text-secondary); overflow-y: auto; max-height: 280px; }
    .code-content.output { color: var(--success-text); }
    .code-content.error { color: var(--danger-text); }

    .live-indicator { display: inline-flex; align-items: center; gap: 0.375rem; padding: 0.25rem 0.625rem; border-radius: var(--radius-full); font-size: 0.75rem; font-weight: var(--fw-semibold); background: var(--success-bg); color: var(--success-text); border: 1px solid var(--success-border); }
    .live-indicator .dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; animation: pulse 1.5s infinite; }
    /* Zamanlanmış görevler */
    .sched-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-5); margin-top: var(--space-6); }
    .sched-head { display: flex; justify-content: space-between; align-items: center; gap: 0.75rem; flex-wrap: wrap; }
    .sched-head h2 { font-size: var(--text-lg); margin: 0; display: flex; align-items: center; gap: 0.5rem; }
    .sched-head h2 i { color: var(--primary-500); }
    .sched-form { display: none; margin-top: var(--space-4); background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-4); }
    .sched-form.open { display: block; }
    .sched-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: var(--space-3); }
    .sched-form label.f { display: flex; flex-direction: column; gap: 0.3rem; font-size: 0.75rem; font-weight: var(--fw-semibold); color: var(--text-tertiary); text-transform: uppercase; letter-spacing: 0.04em; }
    .sched-form input, .sched-form select, .sched-form textarea { padding: 0.5rem 0.75rem; border: 1px solid var(--border-default); border-radius: var(--radius-md); background: var(--bg-surface); color: var(--text-primary); font-size: var(--text-sm); text-transform: none; letter-spacing: 0; font-weight: normal; }
    .sched-form input[type=checkbox] { width: auto; padding: 0; flex: none; margin: 0; }
    .sched-devs label { justify-content: flex-start; text-align: left; }
    .sched-form textarea { font-family: var(--font-mono); min-height: 70px; resize: vertical; }
    .sched-days { display: flex; gap: 0.375rem; flex-wrap: wrap; }
    .sched-days label { display: inline-flex; align-items: center; gap: 0.25rem; font-size: var(--text-sm); color: var(--text-secondary); background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-sm); padding: 0.25rem 0.5rem; }
    .sched-devs { max-height: 200px; overflow-y: auto; display: flex; flex-direction: column; gap: 0.25rem; background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.5rem; }
    .sched-devs label { font-size: var(--text-sm); color: var(--text-secondary); display: flex; gap: 0.5rem; align-items: center; }
    .sched-row { display: grid; grid-template-columns: 1.3fr 1fr 1fr auto; gap: var(--space-3); align-items: center; padding: var(--space-3) 0; border-bottom: 1px solid var(--border-subtle); font-size: var(--text-sm); }
    .sched-row:last-child { border-bottom: none; }
    .sched-row .n { font-weight: var(--fw-semibold); color: var(--text-primary); }
    .sched-row .c { font-family: var(--font-mono); font-size: 0.75rem; color: var(--text-tertiary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 360px; }
    .sched-row .s { color: var(--text-secondary); }
    .sched-row .sub { font-size: 0.75rem; color: var(--text-tertiary); }
    .sched-row.off { opacity: 0.55; }
    .sched-actions { display: flex; gap: 0.375rem; flex-wrap: wrap; justify-content: flex-end; }
    .sched-btn { padding: 0.35rem 0.6rem; border-radius: var(--radius-md); border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); font-size: 0.75rem; font-weight: var(--fw-semibold); cursor: pointer; }
    .sched-btn.primary { background: var(--primary-500); border-color: var(--primary-500); color: #fff; }
    .sched-btn.danger { color: var(--danger-text); }
    @media (max-width: 760px) { .sched-row { grid-template-columns: 1fr; } .sched-actions { justify-content: flex-start; } }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-tasks"></i> Operasyon ve Görev Yönetimi</h1>
        <p>Ağdaki komut dağıtımı ve ajan geri dönüşlerinin canlı takibi</p>
    </div>
    <div class="page-header-actions">
        <span class="live-indicator"><span class="dot"></span> CANLI AKIŞ</span>
    </div>
</div>

<div class="dashboard-stats" id="topStats"></div>

<div class="control-panel">
    <div class="control-group">
        <button class="btn-action pause" onclick="window.bulkControl('PAUSE')"><i class="fas fa-pause"></i> Tümünü Duraklat</button>
        <button class="btn-action resume" onclick="window.bulkControl('RESUME')"><i class="fas fa-play"></i> Devam Et</button>
        <button class="btn-action stop" onclick="window.bulkControl('CLEAR')"><i class="fas fa-trash"></i> Geçmişi Temizle</button>
    </div>
    <div style="margin-left:auto;color:var(--text-tertiary);font-size:var(--text-sm);" id="syncText">
        <i class="fas fa-arrows-rotate fa-spin" style="color:var(--primary-500);"></i> Eşitleniyor...
    </div>
</div>

<div id="groupedTasksContainer">
    <div style="text-align:center;padding:3rem;color:var(--text-tertiary);">
        <i class="fas fa-circle-notch fa-spin" style="font-size:1.5rem;"></i>
        <div style="margin-top:0.5rem;font-size:var(--text-sm);">Veriler toplanıyor...</div>
    </div>
</div>

<div class="sched-card" id="schedCard">
    <div class="sched-head">
        <h2><i class="fas fa-calendar-days"></i> Zamanlanmış görevler</h2>
        <button class="sched-btn primary" id="schedNewBtn"><i class="fas fa-plus"></i> Yeni zamanlanmış görev</button>
    </div>
    <div class="sched-form" id="schedForm">
        <div class="sched-grid">
            <label class="f">Ad<input id="sfName" maxlength="100" placeholder="ör. Gece temizliği"></label>
            <label class="f">Zamanlama
                <select id="sfType"><option value="daily">Her gün</option><option value="weekly">Seçili günler</option><option value="once">Bir kez</option></select>
            </label>
            <label class="f" id="sfTimeWrap">Saat<input id="sfTime" type="time" value="03:00"></label>
            <label class="f" id="sfAtWrap" style="display:none;">Tarih ve saat<input id="sfAt" type="datetime-local"></label>
        </div>
        <div id="sfDaysWrap" style="display:none;margin-top:var(--space-3);">
            <div class="sched-days" id="sfDays"></div>
        </div>
        <label class="f" style="margin-top:var(--space-3);">Komut (hedef cihazlarda SYSTEM olarak çalışır)<textarea id="sfCmd" maxlength="4000" placeholder="ör. cleanmgr /sagerun:1"></textarea></label>
        <div class="sched-grid" style="margin-top:var(--space-3);">
            <label class="f">Hedef
                <select id="sfMode"><option value="ALL">Tüm cihazlar</option><option value="LAB">Bir sınıf</option><option value="PC">Seçili cihazlar</option></select>
            </label>
            <label class="f" id="sfLabWrap" style="display:none;">Sınıf<select id="sfLab"></select></label>
        </div>
        <div class="sched-devs" id="sfDevs" style="display:none;margin-top:var(--space-3);"></div>
        <div style="display:flex;gap:0.5rem;margin-top:var(--space-4);align-items:center;flex-wrap:wrap;">
            <button class="sched-btn primary" id="sfSave"><i class="fas fa-floppy-disk"></i> Kaydet</button>
            <button class="sched-btn" id="sfCancel">Vazgeç</button>
            <span style="font-size:var(--text-xs);color:var(--text-tertiary);" id="sfTz"></span>
        </div>
    </div>
    <div id="schedList" style="margin-top:var(--space-3);"><div style="color:var(--text-tertiary);font-size:var(--text-sm);">Yükleniyor…</div></div>
</div>

<div style="margin-top:var(--space-8);">
    <div class="page-header" style="margin-bottom:var(--space-4);">
        <div><h2 style="font-size:var(--text-2xl);"><i class="fas fa-satellite-dish" style="color:var(--primary-500);"></i> Ajan Aktivite Radarı</h2></div>
    </div>

    <div class="agent-logs-container">
        <div class="log-toolbar">
            <select id="logPcFilter" onchange="window.renderLogs()"><option value="ALL">Tüm Ajanlar</option></select>
            <select id="logTypeFilter" onchange="window.renderLogs()">
                <option value="ALL">Tüm Log Tipleri</option>
                <option value="System">System</option>
                <option value="Deploy">Deploy</option>
                <option value="Network">Network</option>
                <option value="Error">Error</option>
            </select>
            <input type="text" id="logSearchInput" oninput="window.renderLogs()" placeholder="Log mesajı ara...">
        </div>
        <div class="log-list" id="agentLogsList">
            <div class="empty-state"><i class="fas fa-satellite-dish"></i><h3>Loglar bekleniyor</h3></div>
        </div>
    </div>
</div>

<div id="outputModal" class="modal-overlay" onclick="if(event.target===this) closeModal('outputModal')">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title"><i class="fas fa-microchip modal-title-icon"></i> Operasyon Raporu</div>
            <button class="modal-close" onclick="closeModal('outputModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <div style="display:flex;align-items:center;gap:0.875rem;background:var(--bg-surface-2);padding:var(--space-4);border-radius:var(--radius-md);border:1px solid var(--border-subtle);margin-bottom:var(--space-4);">
                <i class="fas fa-desktop" style="font-size:1.5rem;color:var(--primary-500);"></i>
                <div>
                    <div style="font-size:0.6875rem;color:var(--text-tertiary);text-transform:uppercase;font-weight:var(--fw-semibold);letter-spacing:0.05em;">Hedef Ajan</div>
                    <div id="modalPcName" style="font-size:var(--text-md);font-weight:var(--fw-semibold);color:var(--text-primary);">—</div>
                </div>
            </div>
            <div class="code-block">
                <div class="code-header"><i class="fas fa-code"></i> Gönderilen Komut</div>
                <pre id="modalCommand" class="code-content"></pre>
            </div>
            <div class="code-block">
                <div class="code-header"><i class="fas fa-terminal"></i> Ajan Yanıtı</div>
                <pre id="modalOutput" class="code-content output"></pre>
            </div>
        </div>
    </div>
</div>

<script>
window.POpsMemory = {
    devices: [], tasks: [], logs: [], deviceMap: {}, groupsMap: {},
    expandedGroups: new Set(), lastTasksHash: '', lastLogsHash: ''
};

window.taskAction = async function(action, targetMode, targetId) {
    const actionName = { CANCEL: 'İptal', RETRY: 'Yeniden Başlat', PAUSE: 'Duraklat', RESUME: 'Devam Ettir' }[action];
    if (!confirm(`[${actionName}] işlemi uygulanacak. Onaylıyor musunuz?`)) return;
    try {
        await apiRequest('/api/tasks/action', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ action, target_mode: targetMode, target_id: targetId.toString() }) });
        showToast('İşlem iletildi.', 'success');
        window.fetchAndRenderTasks();
    } catch (e) { showToast('API yanıt vermedi.', 'error'); }
};

window.bulkControl = async function(action) {
    if (action === 'CLEAR') {
        if (!confirm('Tüm kuyruğu temizlemek istediğinize emin misiniz?')) return;
        try { await apiRequest('/api/flush_queue', { method: 'POST' }); showToast('Kuyruk temizlendi.', 'success'); window.fetchAndRenderTasks(); } catch (e) { showToast('Hata.', 'error'); }
    } else { window.taskAction(action, 'ALL', 'GLOBAL'); }
};

window.openTaskDetail = function(taskId, groupId) {
    const group = window.POpsMemory.groupsMap[groupId];
    if (!group) return;
    const task = group.pcs.find(p => p.id.toString() === taskId.toString());
    if (!task) return;
    const dName = window.POpsMemory.deviceMap[task.target_pc] || task.target_pc;
    document.getElementById('modalPcName').innerText = dName;
    document.getElementById('modalCommand').textContent = group.command;
    const out = document.getElementById('modalOutput');
    out.textContent = task.output || 'Cihazdan henüz yanıt alınamadı.';
    out.scrollTop = 0;
    const isErr = (task.status || '').toLowerCase().includes('failed') || (task.status || '').toLowerCase().includes('error');
    out.classList.toggle('error', isErr);
    out.classList.toggle('output', !isErr);
    openModal('outputModal');
};

window.toggleGroup = function(groupId) {
    const el = document.getElementById(groupId);
    if (!el) return;
    el.classList.toggle('expanded');
    if (el.classList.contains('expanded')) window.POpsMemory.expandedGroups.add(groupId);
    else window.POpsMemory.expandedGroups.delete(groupId);
};

window.fetchAndRenderTasks = async function() {
    try {
        const [devices, tasks] = await Promise.all([apiRequest('/api/devices').catch(() => []), apiRequest('/api/tasks?limit=500').catch(() => [])]);
        if (devices && devices.length > 0) {
            window.POpsMemory.devices = devices;
            devices.forEach(d => { if (d.real_hostname && !d.real_hostname.startsWith('HW-')) window.POpsMemory.deviceMap[d.hostname] = d.real_hostname; });
        }
        const currentHash = JSON.stringify(tasks);
        if (currentHash === window.POpsMemory.lastTasksHash) {
            document.getElementById('syncText').innerHTML = '<i class="fas fa-check" style="color:var(--success-text);"></i> Güncel: ' + new Date().toLocaleTimeString();
            return;
        }
        window.POpsMemory.lastTasksHash = currentHash;
        window.POpsMemory.tasks = tasks;

        if (!tasks || tasks.length === 0) {
            document.getElementById('groupedTasksContainer').innerHTML = '<div class="empty-state"><i class="fas fa-check-circle" style="color:var(--success-solid);"></i><h3>Sistem Stabil</h3><p>Şu an çalışan veya bekleyen bir görev yok.</p></div>';
            document.getElementById('topStats').innerHTML = statCardsHtml(0, 0, 0, 0);
            document.getElementById('syncText').innerHTML = '<i class="fas fa-check" style="color:var(--success-text);"></i> Güncellendi: ' + new Date().toLocaleTimeString();
            return;
        }

        const total = tasks.length;
        const success = tasks.filter(t => t.status === 'Completed' || t.status === 'Completed (Rebooted)').length;
        const running = tasks.filter(t => t.status === 'Running').length;
        const pending = tasks.filter(t => t.status === 'Pending').length;
        const paused = tasks.filter(t => t.status === 'Paused').length;
        const failed = total - success - running - pending - paused;

        document.getElementById('topStats').innerHTML = statCardsHtml(total, success, running, failed + pending + paused);

        window.POpsMemory.groupsMap = {};
        tasks.forEach(t => {
            const labName = t.target_lab || 'Atanmamış / Bireysel';
            const groupKey = labName + '|' + t.script_path;
            let h = 0; for (let i = 0; i < groupKey.length; i++) h = ((h << 5) - h) + groupKey.charCodeAt(i);
            const gId = 'group_' + Math.abs(h);
            if (!window.POpsMemory.groupsMap[gId]) window.POpsMemory.groupsMap[gId] = { id: gId, lab: labName, command: t.script_path, total: 0, success: 0, pcs: [] };
            const g = window.POpsMemory.groupsMap[gId];
            g.total++;
            if (t.status.includes('Completed')) g.success++;
            g.pcs.push(t);
        });

        let html = '';
        for (const [gId, group] of Object.entries(window.POpsMemory.groupsMap)) {
            const isExpanded = window.POpsMemory.expandedGroups.has(gId) ? 'expanded' : '';
            const trHtml = group.pcs.sort((a, b) => (window.POpsMemory.deviceMap[a.target_pc] || a.target_pc).localeCompare(window.POpsMemory.deviceMap[b.target_pc] || b.target_pc, undefined, { numeric: true })).map(pc => {
                let statusClass = 'pending', statusIcon = 'fa-clock', statusText = 'Sırada', isCancelable = true;
                if (pc.status.includes('Completed')) { statusClass = 'success'; statusIcon = 'fa-check'; statusText = 'Tamamlandı'; isCancelable = false; }
                else if (pc.status === 'Running') { statusClass = 'running'; statusIcon = 'fa-spinner fa-spin'; statusText = 'İşleniyor'; }
                else if (pc.status === 'Paused') { statusClass = 'paused'; statusIcon = 'fa-pause'; statusText = 'Durduruldu'; }
                else if (['Failed', 'Error', 'Cancelled'].includes(pc.status)) { statusClass = 'failed'; statusIcon = 'fa-xmark'; statusText = pc.status === 'Cancelled' ? 'İptal Edildi' : 'Hata Alındı'; isCancelable = false; }
                // Bağlantı koptuğunda sonucu bilinmeyen (Unknown) ya da ajan yeniden başladığı için yarıda kalan (Interrupted) görev
                else if (pc.status === 'Unknown') { statusClass = 'paused'; statusIcon = 'fa-circle-question'; statusText = 'Sonuç bilinmiyor (bağlantı koptu)'; }
                else if (pc.status === 'Interrupted') { statusClass = 'failed'; statusIcon = 'fa-circle-exclamation'; statusText = 'Yarıda kaldı (ajan yeniden başladı)'; isCancelable = false; }
                else if (pc.status === 'Denied') { statusClass = 'failed'; statusIcon = 'fa-ban'; statusText = 'Reddedildi (terminal bu cihazda kapalı)'; isCancelable = false; }
                // Gönderildikten 35 dk sonra hâlâ sonuç yok: ajan geri dönmedi (geç gelen sonuç yine kaydedilir)
                else if (pc.status === 'Timed Out') { statusClass = 'failed'; statusIcon = 'fa-hourglass-end'; statusText = 'Zaman aşımı (35 dk sonuç gelmedi)'; isCancelable = false; }
                // Zamanlanmış görev geçerlilik süresi içinde gönderilemedi (cihaz kapalıydı): geç çalıştırılmaz
                else if (pc.status === 'Expired') { statusClass = 'failed'; statusIcon = 'fa-calendar-xmark'; statusText = 'Süresi doldu (zamanında gönderilemedi)'; isCancelable = false; }
                const displayName = window.POpsMemory.deviceMap[pc.target_pc] || pc.target_pc;
                const showMac = displayName === pc.target_pc ? '' : `<br><span style="font-family:var(--font-mono);font-size:0.6875rem;color:var(--text-tertiary);">${escapeHtml(pc.target_pc)}</span>`;
                return `<tr>
                    <td><strong>${escapeHtml(displayName)}</strong>${showMac}</td>
                    <td class="status-text ${statusClass}"><i class="fas ${statusIcon}"></i> ${statusText}</td>
                    <td style="color:var(--text-tertiary);font-size:0.75rem;font-family:var(--font-mono);">${escapeHtml(pc.created_at || '-')}</td>
                    <td style="text-align:right;">
                        <div style="display:inline-flex;gap:0.375rem;">
                            <button class="btn-action retry icon" onclick="window.taskAction('RETRY', 'TASK', ${jsArg(pc.id)})" title="Yeniden Başlat"><i class="fas fa-rotate"></i></button>
                            <button class="btn secondary" style="padding:0.375rem 0.625rem;font-size:0.75rem;" onclick="window.openTaskDetail(${jsArg(pc.id)}, ${jsArg(gId)})"><i class="fas fa-magnifying-glass" style="color:var(--primary-500);"></i> Detay</button>
                            ${isCancelable ? `<button class="btn-action stop icon" onclick="window.taskAction('CANCEL', 'TASK', ${jsArg(pc.id)})" title="İptal"><i class="fas fa-xmark"></i></button>` : ''}
                        </div>
                    </td>
                </tr>`;
            }).join('');

            html += `<div class="task-group-card ${isExpanded}" id="${escapeHtml(group.id)}">
                <div class="task-group-header" onclick="window.toggleGroup(${jsArg(group.id)})">
                    <div class="task-info-area">
                        <span class="task-lab"><i class="fas fa-layer-group"></i> ${escapeHtml(group.lab)}</span>
                        <span class="task-cmd" title="${escapeHtml(group.command)}"><i class="fas fa-code" style="color:var(--text-tertiary);margin-right:0.375rem;"></i>${escapeHtml(group.command)}</span>
                    </div>
                    <div class="task-badges">
                        <div class="badge-box total"><i class="fas fa-desktop"></i> ${group.total}</div>
                        <div class="badge-box success">${group.success} başarılı</div>
                        <div style="display:inline-flex;gap:0.25rem;margin-left:0.5rem;">
                            <button class="btn-action pause icon" onclick="event.stopPropagation(); window.taskAction('PAUSE', 'LAB', ${jsArg(group.lab)})" title="Labı Duraklat"><i class="fas fa-pause"></i></button>
                            <button class="btn-action resume icon" onclick="event.stopPropagation(); window.taskAction('RESUME', 'LAB', ${jsArg(group.lab)})" title="Devam Ettir"><i class="fas fa-play"></i></button>
                            <button class="btn-action stop icon" onclick="event.stopPropagation(); window.taskAction('CANCEL', 'LAB', ${jsArg(group.lab)})" title="İptal"><i class="fas fa-xmark"></i></button>
                        </div>
                        <i class="fas fa-chevron-down toggle-icon" style="margin-left:0.5rem;"></i>
                    </div>
                </div>
                <div class="task-details">
                    <table class="data-table" style="margin:0;">
                        <thead><tr><th>Kayıtlı Cihaz</th><th>Durum</th><th>Zaman</th><th style="text-align:right;">İşlem</th></tr></thead>
                        <tbody>${trHtml}</tbody>
                    </table>
                </div>
            </div>`;
        }
        document.getElementById('groupedTasksContainer').innerHTML = html;
        document.getElementById('syncText').innerHTML = '<i class="fas fa-check" style="color:var(--success-text);"></i> Senkron: ' + new Date().toLocaleTimeString();
    } catch (error) {
        document.getElementById('groupedTasksContainer').innerHTML = '<div class="card" style="text-align:center;padding:3rem;color:var(--danger-text);"><i class="fas fa-wifi" style="font-size:2rem;margin-bottom:0.75rem;opacity:0.5;"></i><h3>Sunucu Bağlantısı Koptu</h3><p>Python API (Port 8000) yanıt vermiyor.</p></div>';
    }
};

function statCardsHtml(total, success, running, failed) {
    return `
        <div class="stat-card"><div class="stat-icon"><i class="fas fa-layer-group"></i></div><div><div class="stat-label">Toplam İşlem</div><div class="stat-value">${total}</div></div></div>
        <div class="stat-card"><div class="stat-icon success"><i class="fas fa-check-double"></i></div><div><div class="stat-label">Başarılı</div><div class="stat-value" style="color:var(--success-text);">${success}</div></div></div>
        <div class="stat-card"><div class="stat-icon" style="background:var(--info-bg);color:var(--info-text);"><i class="fas fa-arrows-spin"></i></div><div><div class="stat-label">Aktif Akış</div><div class="stat-value" style="color:var(--info-text);">${running}</div></div></div>
        <div class="stat-card"><div class="stat-icon danger"><i class="fas fa-triangle-exclamation"></i></div><div><div class="stat-label">Hata/Bekleyen</div><div class="stat-value" style="color:var(--danger-text);">${failed}</div></div></div>`;
}

window.fetchAgentLogs = async function() {
    try {
        const logs = await apiRequest('/api/logs?limit=200');
        const currentHash = JSON.stringify(logs);
        if (currentHash === window.POpsMemory.lastLogsHash) return;
        window.POpsMemory.lastLogsHash = currentHash;
        window.POpsMemory.logs = logs;
        const pcSelect = document.getElementById('logPcFilter');
        if (pcSelect.options.length <= 1 && logs.length > 0) {
            const uniquePcs = [...new Set(logs.map(l => l.pc_name))];
            uniquePcs.forEach(pc => { const dName = window.POpsMemory.deviceMap[pc] || pc; pcSelect.innerHTML += `<option value="${escapeHtml(pc)}">${escapeHtml(dName)}</option>`; });
        }
        window.renderLogs();
    } catch (e) {}
};

window.renderLogs = function() {
    const container = document.getElementById('agentLogsList');
    const pcFilter = document.getElementById('logPcFilter').value;
    const typeFilter = document.getElementById('logTypeFilter').value;
    const searchWord = document.getElementById('logSearchInput').value.toLowerCase().trim();
    let filtered = window.POpsMemory.logs || [];
    if (pcFilter !== 'ALL') filtered = filtered.filter(l => l.pc_name === pcFilter);
    if (typeFilter !== 'ALL') filtered = filtered.filter(l => l.log_type === typeFilter);
    if (searchWord) filtered = filtered.filter(l => l.message.toLowerCase().includes(searchWord));
    if (!filtered || filtered.length === 0) { container.innerHTML = '<div class="empty-state"><i class="fas fa-magnifying-glass"></i><h3>Log bulunamadı</h3><p>Kriterlere uygun kayıt yok.</p></div>'; return; }
    const icons = { System: 'fa-gear', Deploy: 'fa-terminal', AppStart: 'fa-rocket', File: 'fa-file-code', USB: 'fa-usb', Network: 'fa-globe', Error: 'fa-triangle-exclamation' };
    container.innerHTML = filtered.map(log => {
        const iconClass = icons[log.log_type] || 'fa-info-circle';
        const dName = window.POpsMemory.deviceMap[log.pc_name] || log.pc_name;
        const timeOnly = log.timestamp ? log.timestamp.split(' ')[1] : '';
        return `<div class="log-line type-${escapeHtml(log.log_type)}">
            <span class="log-time">${escapeHtml(timeOnly)}</span>
            <span class="log-badge badge-${escapeHtml(log.log_type)}"><i class="fas ${iconClass}"></i> ${escapeHtml(log.log_type)}</span>
            <span class="log-pc" title="${escapeHtml(log.pc_name)}">${escapeHtml(dName)}</span>
            <span class="log-msg">${escapeHtml(log.message)}</span>
        </div>`;
    }).join('');
};

function escapeHtml(s) { return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

// ================= Zamanlanmış görevler =================
(function () {
    const $ = (id) => document.getElementById(id);
    const DAYS = ['Pzt', 'Sal', 'Çar', 'Per', 'Cum', 'Cmt', 'Paz'];
    let devices = [];
    const fmt = (iso) => { if (!iso) return '—'; try { return new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }); } catch (e) { return iso || ''; } };
    async function api(path, opts) {
        const r = await fetch(path, opts);
        const d = await r.json().catch(() => ({}));
        if (!r.ok) { const e = new Error(d.detail || ('HTTP ' + r.status)); e.status = r.status; throw e; }
        return d;
    }
    const post = (path, body) => api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

    $('sfDays').innerHTML = DAYS.map((d, i) => `<label><input type="checkbox" value="${i + 1}" ${i < 5 ? 'checked' : ''}> ${d}</label>`).join('');
    function syncForm() {
        const t = $('sfType').value, m = $('sfMode').value;
        $('sfTimeWrap').style.display = t === 'once' ? 'none' : '';
        $('sfAtWrap').style.display = t === 'once' ? '' : 'none';
        $('sfDaysWrap').style.display = t === 'weekly' ? '' : 'none';
        $('sfLabWrap').style.display = m === 'LAB' ? '' : 'none';
        $('sfDevs').style.display = m === 'PC' ? 'flex' : 'none';
    }
    ['sfType', 'sfMode'].forEach(id => $(id).addEventListener('change', syncForm));

    async function loadDevices() {
        try { devices = await api('/api/devices'); } catch (e) { devices = []; }
        const labs = [...new Set(devices.map(d => d.lab).filter(Boolean))].sort();
        $('sfLab').innerHTML = labs.map(l => `<option value="${escapeHtml(l)}">${escapeHtml(l)}</option>`).join('') || '<option value="">Sınıf yok</option>';
        $('sfDevs').innerHTML = devices.map(d => `<label><input type="checkbox" value="${escapeHtml(d.hw_id)}"> ${escapeHtml(d.display_name || d.real_hostname || d.hw_id)} <span style="color:var(--text-tertiary);font-size:0.75rem;">${escapeHtml(d.lab || '')} · ${escapeHtml(d.hw_id)}</span></label>`).join('') || '<span>Cihaz yok</span>';
    }

    function when(t) {
        if (t.schedule_type === 'once') return 'Bir kez · ' + escapeHtml(fmt(t.run_at));
        if (t.schedule_type === 'daily') return 'Her gün ' + escapeHtml(t.time_of_day || '');
        return escapeHtml((t.weekdays || []).map(d => DAYS[d - 1]).join(', ')) + ' ' + escapeHtml(t.time_of_day || '');
    }
    function target(t) {
        if (t.target_mode === 'ALL') return 'Tüm cihazlar';
        if (t.target_mode === 'LAB') return 'Sınıf: ' + escapeHtml((t.targets || []).join(', '));
        return (t.targets || []).length + ' cihaz';
    }
    async function loadList() {
        let d;
        try { d = await api('/api/scheduled_tasks'); }
        catch (e) {
            if (e.status === 403) { $('schedCard').style.display = 'none'; return; }
            $('schedList').innerHTML = `<div style="color:var(--text-tertiary);font-size:var(--text-sm);">Zamanlanmış görevler alınamadı${e.status === 404 ? ' (sunucu güncellemesi gerekli)' : ''}.</div>`;
            return;
        }
        $('sfTz').textContent = 'Saatler sunucu saatine göredir (şu an: ' + fmt(d.server_time) + ').';
        const items = d.items || [];
        $('schedList').innerHTML = items.length ? items.map(t => `
            <div class="sched-row${t.enabled ? '' : ' off'}">
                <div><div class="n">${escapeHtml(t.name)}</div><div class="c" title="${escapeHtml(t.command)}">${escapeHtml(t.command)}</div></div>
                <div><div class="s">${when(t)}</div><div class="sub">${target(t)}</div></div>
                <div><div class="s">${t.enabled && t.next_run ? 'Sıradaki: ' + escapeHtml(fmt(t.next_run)) : 'Durduruldu'}</div>
                     <div class="sub">${t.last_run ? 'Son: ' + escapeHtml(fmt(t.last_run)) + (t.last_result ? ' · ' + escapeHtml(t.last_result) : '') : 'Henüz çalışmadı'}</div></div>
                <div class="sched-actions">
                    <button class="sched-btn" data-act="run" data-id="${escapeHtml(t.id)}" title="Şimdi bir kez çalıştır"><i class="fas fa-play"></i> Şimdi</button>
                    <button class="sched-btn" data-act="toggle" data-id="${escapeHtml(t.id)}" data-on="${t.enabled ? 1 : 0}">${t.enabled ? '<i class="fas fa-pause"></i> Durdur' : '<i class="fas fa-play"></i> Başlat'}</button>
                    <button class="sched-btn danger" data-act="del" data-id="${escapeHtml(t.id)}"><i class="fas fa-trash"></i></button>
                </div>
            </div>`).join('') : '<div style="color:var(--text-tertiary);font-size:var(--text-sm);padding:0.5rem 0;">Zamanlanmış görev yok.</div>';
        $('schedList').querySelectorAll('button[data-act]').forEach(b => b.addEventListener('click', () => act(b)));
    }
    async function act(b) {
        const id = b.dataset.id;
        try {
            if (b.dataset.act === 'run') {
                if (!confirm('Bu görev şimdi bir kez hedef cihazlarda çalıştırılsın mı?')) return;
                const r = await post(`/api/scheduled_tasks/${encodeURIComponent(id)}/run`, {});
                showToast(`${r.queued} cihaz için kuyruğa eklendi.`, 'success');
                window.fetchAndRenderTasks && window.fetchAndRenderTasks();
            } else if (b.dataset.act === 'toggle') {
                await post(`/api/scheduled_tasks/${encodeURIComponent(id)}/toggle`, { enabled: b.dataset.on !== '1' });
            } else {
                if (!confirm('Zamanlanmış görev silinsin mi?')) return;
                await api(`/api/scheduled_tasks/${encodeURIComponent(id)}`, { method: 'DELETE' });
            }
            loadList();
        } catch (e) { showToast(e.message, 'error'); }
    }
    $('schedNewBtn').addEventListener('click', () => { $('schedForm').classList.add('open'); syncForm(); loadDevices(); });
    $('sfCancel').addEventListener('click', () => $('schedForm').classList.remove('open'));
    $('sfSave').addEventListener('click', async () => {
        const mode = $('sfMode').value;
        const body = {
            name: $('sfName').value.trim(), command: $('sfCmd').value.trim(), target_mode: mode,
            targets: mode === 'LAB' ? [$('sfLab').value].filter(Boolean) : mode === 'PC' ? [...$('sfDevs').querySelectorAll('input:checked')].map(i => i.value) : [],
            schedule_type: $('sfType').value, time_of_day: $('sfTime').value, run_at: $('sfAt').value || null,
            weekdays: [...$('sfDays').querySelectorAll('input:checked')].map(i => parseInt(i.value)),
        };
        if (!confirm(`"${body.command.slice(0, 80)}" komutu zamanlanacak ve hedef cihazlarda SYSTEM olarak çalışacak. Onaylıyor musunuz?`)) return;
        try {
            await post('/api/scheduled_tasks', body);
            showToast('Zamanlanmış görev kaydedildi.', 'success');
            $('schedForm').classList.remove('open');
            $('sfName').value = ''; $('sfCmd').value = '';
            loadList();
        } catch (e) { showToast(e.message, 'error'); }
    });
    syncForm();
    loadList();
    popsPoll(loadList, 30000);
})();

document.addEventListener('DOMContentLoaded', () => {
    if (typeof window.showToast !== 'function') window.showToast = function(msg) { console.log(msg); };
    window.fetchAndRenderTasks();
    window.fetchAgentLogs();
    popsPoll(() => Promise.all([window.fetchAndRenderTasks(), window.fetchAgentLogs()]), 5000);
});
</script>

<?php include 'includes/footer.php'; ?>