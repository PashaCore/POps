<?php include 'includes/header.php'; ?>
<?php $tkCanAdmin = in_array($_SESSION['role'] ?? '', ['admin', 'superadmin'], true); ?>

<style>
    .task-group { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); margin-bottom: var(--space-3); overflow: hidden; box-shadow: var(--shadow-xs); transition: box-shadow 0.15s, border-color 0.15s; }
    .task-group:hover { border-color: var(--border-default); }
    .task-group-head { display: flex; justify-content: space-between; align-items: center; gap: var(--space-3); padding: var(--space-4) var(--space-5); cursor: pointer; }
    .task-group-head:hover { background: var(--bg-surface-2); }
    .task-group.open .task-group-head { border-bottom: 1px solid var(--border-subtle); background: var(--bg-surface-2); }
    .task-group-info { display: flex; flex-direction: column; gap: 0.25rem; min-width: 0; flex: 1; }
    .task-lab { font-weight: var(--fw-semibold); color: var(--primary-600); font-size: var(--text-sm); display: flex; align-items: center; gap: 0.5rem; }
    .task-cmd { font-family: var(--font-mono); font-size: var(--text-sm); color: var(--text-secondary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .task-group-side { display: flex; align-items: center; gap: 0.5rem; flex-shrink: 0; flex-wrap: wrap; justify-content: flex-end; }
    .task-group .chev { color: var(--text-tertiary); transition: transform 0.2s; }
    .task-group.open .chev { transform: rotate(180deg); color: var(--primary-500); }
    .task-group-body { display: none; overflow-x: auto; }
    .task-group.open .task-group-body { display: block; }
    .task-status { font-weight: var(--fw-semibold); font-size: var(--text-sm); display: inline-flex; align-items: center; gap: 0.375rem; }
    .task-status.success { color: var(--success-text); }
    .task-status.failed { color: var(--danger-text); }
    .task-status.pending { color: var(--warning-text); }
    .task-status.paused { color: var(--text-tertiary); }
    .task-status.running { color: var(--info-text); }
    .live-indicator { display: inline-flex; align-items: center; gap: 0.375rem; padding: 0.25rem 0.625rem; border-radius: var(--radius-full); font-size: 0.75rem; font-weight: var(--fw-semibold); background: var(--success-bg); color: var(--success-text); border: 1px solid var(--success-border); }
    .live-indicator .dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; animation: pulse 1.5s infinite; }

    .log-list { max-height: 520px; overflow-y: auto; padding: var(--space-3); }
    .log-line { display: flex; gap: 0.75rem; padding: 0.5rem 0.75rem; border-radius: var(--radius-sm); margin-bottom: 0.25rem; background: var(--bg-surface-2); align-items: center; border-left: 3px solid transparent; font-size: var(--text-sm); }
    .log-line:hover { background: var(--bg-hover); }
    .log-time { color: var(--primary-500); min-width: 64px; font-size: 0.75rem; font-family: var(--font-mono); flex-shrink: 0; }
    .log-pc { color: var(--text-primary); font-weight: var(--fw-semibold); min-width: 140px; flex-shrink: 0; }
    .log-msg { color: var(--text-secondary); flex: 1; word-break: break-word; }
    .log-line.risk-high, .log-line.risk-critical { border-left-color: var(--danger-solid); background: var(--danger-bg); }
    .log-line.risk-high .log-msg, .log-line.risk-critical .log-msg { color: var(--danger-text); }
    .log-line.risk-medium { border-left-color: var(--warning-solid); }
    .log-type { min-width: 96px; justify-content: center; }
    .card-header .toolbar-group select { width: auto; min-width: 170px; }
    .card-header .toolbar-group .search-field { flex: 0 1 240px; }

    .code-block { background: var(--bg-app); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); overflow: hidden; margin-bottom: var(--space-3); }
    .code-header { background: var(--bg-surface-2); padding: 0.5rem 0.875rem; font-size: 0.75rem; color: var(--text-tertiary); font-weight: var(--fw-semibold); border-bottom: 1px solid var(--border-subtle); display: flex; align-items: center; gap: 0.5rem; }
    .code-content { padding: 0.875rem; margin: 0; font-family: var(--font-mono); font-size: var(--text-sm); white-space: pre-wrap; word-wrap: break-word; color: var(--text-secondary); overflow-y: auto; max-height: 300px; }
    .code-content.output { color: var(--success-text); }
    .code-content.error { color: var(--danger-text); }

    .sched-row { display: grid; grid-template-columns: 1.4fr 1fr 1.2fr auto; gap: var(--space-3); align-items: center; padding: var(--space-3) var(--space-5); border-bottom: 1px solid var(--border-subtle); font-size: var(--text-sm); }
    .sched-row:hover { background: var(--bg-surface-2); }
    .sched-row:last-child { border-bottom: none; }
    .sched-row .n { font-weight: var(--fw-semibold); color: var(--text-primary); }
    .sched-row .c { font-family: var(--font-mono); font-size: 0.75rem; color: var(--text-tertiary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 380px; }
    .sched-row .sub { font-size: 0.75rem; color: var(--text-tertiary); margin-top: 0.125rem; }
    .sched-row.off { opacity: 0.6; }
    .sched-days { display: flex; gap: 0.375rem; flex-wrap: wrap; }
    .sched-devs { max-height: 220px; overflow-y: auto; display: flex; flex-direction: column; gap: 0.25rem; background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.5rem; }
    @media (max-width: 860px) { .sched-row { grid-template-columns: 1fr; } }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-list-check"></i> Görev Kuyruğu</h1>
        <p>Bilgisayarlara gönderilen komutlar, sonuçları ve zamanlanmış görevler</p>
    </div>
    <div class="page-header-actions">
        <span class="live-indicator"><span class="dot"></span> Canlı</span>
    </div>
</div>

<div class="stat-grid" id="topStats"></div>

<div class="toolbar">
    <?php if ($tkCanAdmin): ?>
    <div class="toolbar-group">
        <button type="button" class="btn warning-soft sm" data-action="bulk" data-op="PAUSE"><i class="fas fa-pause"></i> Bekleyenleri duraklat</button>
        <button type="button" class="btn success-soft sm" data-action="bulk" data-op="RESUME"><i class="fas fa-play"></i> Devam ettir</button>
        <button type="button" class="btn danger-soft sm" data-action="clear"><i class="fas fa-trash"></i> Görev geçmişini sil</button>
    </div>
    <?php endif; ?>
    <div class="spacer"></div>
    <span class="text-sm text-muted" id="syncText" role="status">Eşitleniyor…</span>
</div>

<div id="groupedTasksContainer"></div>

<div class="card" id="schedCard" style="margin-top:var(--space-6);">
    <div class="card-header">
        <div>
            <h2 class="card-title"><i class="fas fa-calendar-days"></i> Zamanlanmış görevler</h2>
            <div class="card-subtitle" id="sfTz"></div>
        </div>
        <?php if ($tkCanAdmin): ?>
        <button type="button" class="btn sm" data-action="sched-new"><i class="fas fa-plus"></i> Yeni zamanlanmış görev</button>
        <?php endif; ?>
    </div>
    <div class="card-body flush" id="schedList"></div>
</div>

<div class="card" style="margin-top:var(--space-6);">
    <div class="card-header">
        <h2 class="card-title"><i class="fas fa-satellite-dish"></i> Ajan etkinliği</h2>
        <div class="toolbar-group">
            <select id="logPcFilter" aria-label="Bilgisayar"><option value="ALL">Bütün bilgisayarlar</option></select>
            <select id="logTypeFilter" aria-label="Kayıt türü"><option value="ALL">Bütün türler</option></select>
            <div class="search-field" style="min-width:220px;"><i class="fas fa-search" aria-hidden="true"></i><input type="search" id="logSearchInput" placeholder="Mesajda ara" aria-label="Mesajda ara"></div>
        </div>
    </div>
    <div class="log-list" id="agentLogsList"></div>
</div>

<div id="outputModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="outputTitle">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="outputTitle"><i class="fas fa-file-lines"></i> Görev sonucu</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <div class="field"><span class="field-label">Bilgisayar</span><div class="cell-title" id="modalPcName">—</div></div>
            <div class="code-block"><div class="code-header"><i class="fas fa-code"></i> Gönderilen komut</div><pre id="modalCommand" class="code-content"></pre></div>
            <div class="code-block"><div class="code-header"><i class="fas fa-terminal"></i> Ajanın yanıtı</div><pre id="modalOutput" class="code-content output"></pre></div>
        </div>
    </div>
</div>

<?php if ($tkCanAdmin): ?>
<div id="schedModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="schedTitle">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="schedTitle"><i class="fas fa-calendar-plus"></i> Yeni zamanlanmış görev</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <div class="form-grid">
                <div class="field"><label for="sfName">Ad</label><input id="sfName" maxlength="100" placeholder="ör. Gece temizliği"></div>
                <div class="field"><label for="sfType">Zamanlama</label>
                    <select id="sfType"><option value="daily">Her gün</option><option value="weekly">Seçili günler</option><option value="once">Bir kez</option></select></div>
                <div class="field" id="sfTimeWrap"><label for="sfTime">Saat</label><input id="sfTime" type="time" value="03:00"></div>
                <div class="field hidden" id="sfAtWrap"><label for="sfAt">Tarih ve saat</label><input id="sfAt" type="datetime-local"></div>
            </div>
            <div class="field hidden" id="sfDaysWrap"><span class="field-label">Günler</span><div class="sched-days chip-row" id="sfDays"></div></div>
            <div class="field"><label for="sfCmd">Komut</label><textarea id="sfCmd" maxlength="4000" rows="3" class="text-mono" placeholder="ör. cleanmgr /sagerun:1"></textarea>
                <div class="field-hint">Hedef bilgisayarlarda SYSTEM hesabıyla, cmd.exe ile çalışır.</div></div>
            <div class="form-grid">
                <div class="field"><label for="sfMode">Hedef</label>
                    <select id="sfMode"><option value="ALL">Bütün bilgisayarlar</option><option value="LAB">Bir sınıf</option><option value="PC">Seçili bilgisayarlar</option></select></div>
                <div class="field hidden" id="sfLabWrap"><label for="sfLab">Sınıf</label><select id="sfLab"></select></div>
            </div>
            <div class="field hidden" id="sfDevsWrap"><span class="field-label">Bilgisayarlar</span><div class="sched-devs" id="sfDevs"></div></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="sfSave"><i class="fas fa-floppy-disk"></i> Kaydet</button>
        </div>
    </div>
</div>
<?php endif; ?>

<script>
document.addEventListener('DOMContentLoaded', () => {
    const CAN_ADMIN = <?php echo $tkCanAdmin ? 'true' : 'false'; ?>;
    const $ = (id) => document.getElementById(id);
    const mem = { names: {}, groups: {}, expanded: new Set(), tasksKey: null, logs: [], logsKey: null, loaded: false };
    const FINISHED = ['Completed', 'Completed (Rebooted)', 'Failed', 'Error', 'Cancelled', 'Unknown', 'Interrupted', 'Timed Out', 'Denied', 'Expired'];
    const ACTION_TEXT = { CANCEL: 'iptal edilecek', RETRY: 'yeniden çalıştırılacak', PAUSE: 'duraklatılacak', RESUME: 'devam ettirilecek' };
    POps.setLoading($('groupedTasksContainer'), 'Görevler yükleniyor…');
    POps.setLoading($('agentLogsList'), 'Kayıtlar yükleniyor…');

    function statusInfo(s) {
        if (String(s).includes('Completed')) return ['success', 'fa-check', s === 'Completed (Rebooted)' ? 'Tamamlandı (yeniden başladı)' : 'Tamamlandı'];
        switch (s) {
            case 'Running': return ['running', 'fa-spinner fa-spin', 'Çalışıyor'];
            case 'Pending': return ['pending', 'fa-clock', 'Sırada'];
            case 'Paused': return ['paused', 'fa-pause', 'Duraklatıldı'];
            case 'Failed': case 'Error': return ['failed', 'fa-xmark', 'Hata'];
            case 'Cancelled': return ['paused', 'fa-ban', 'İptal edildi'];
            case 'Unknown': return ['paused', 'fa-circle-question', 'Sonuç bilinmiyor (bağlantı koptu)'];
            case 'Interrupted': return ['failed', 'fa-circle-exclamation', 'Yarıda kaldı (ajan yeniden başladı)'];
            case 'Denied': return ['failed', 'fa-ban', 'Reddedildi (uzak komut bu cihazda kapalı)'];
            case 'Timed Out': return ['failed', 'fa-hourglass-end', 'Zaman aşımı (35 dk sonuç gelmedi)'];
            case 'Expired': return ['failed', 'fa-calendar-xmark', 'Süresi doldu (zamanında gönderilemedi)'];
            default: return ['paused', 'fa-circle-question', String(s || '?')];
        }
    }

    function stats(tasks) {
        const c = (f) => tasks.filter(f).length;
        const done = c(t => String(t.status).includes('Completed'));
        const running = c(t => t.status === 'Running');
        const waiting = c(t => t.status === 'Pending' || t.status === 'Paused');
        const bad = tasks.length - done - running - waiting;
        const card = (icon, cls, label, val) => `<div class="stat-card"><div class="stat-icon ${cls}"><i class="fas ${icon}"></i></div><div><div class="stat-label">${label}</div><div class="stat-value">${val}</div></div></div>`;
        $('topStats').innerHTML = card('fa-layer-group', '', 'Toplam', tasks.length) + card('fa-check-double', 'success', 'Tamamlanan', done)
            + card('fa-arrows-spin', 'info', 'Çalışan', running) + card('fa-clock', 'warning', 'Bekleyen', waiting) + card('fa-triangle-exclamation', 'danger', 'Sorunlu', bad);
    }

    async function loadTasks() {
        try {
            const [devices, tasks] = await Promise.all([POps.get('/api/devices').catch(() => null), POps.get('/api/tasks?limit=500')]);
            if (Array.isArray(devices)) devices.forEach(d => { mem.names[d.hostname] = POps.deviceName(d); });
            const key = JSON.stringify(tasks);
            $('syncText').textContent = 'Güncel · ' + new Date().toLocaleTimeString('tr-TR');
            if (key === mem.tasksKey) return;
            mem.tasksKey = key;
            mem.loaded = true;
            renderTasks(Array.isArray(tasks) ? tasks : []);
        } catch (e) {
            $('syncText').textContent = 'Sunucuya ulaşılamadı';
            if (!mem.loaded) POps.setError($('groupedTasksContainer'), e);
        }
    }

    function renderTasks(tasks) {
        stats(tasks);
        const box = $('groupedTasksContainer');
        if (!tasks.length) { POps.setEmpty(box, { icon: 'fa-circle-check', kind: 'success', title: 'Kuyruk boş', text: 'Çalışan ya da bekleyen görev yok.' }); return; }
        mem.groups = {};
        tasks.forEach(t => {
            const lab = t.target_lab || 'Tek cihaz';
            const key = lab + '|' + t.script_path;
            let h = 0; for (let i = 0; i < key.length; i++) h = ((h << 5) - h + key.charCodeAt(i)) | 0;
            const id = 'g' + Math.abs(h);
            const g = mem.groups[id] = mem.groups[id] || { id, lab, command: t.script_path, done: 0, pcs: [] };
            if (String(t.status).includes('Completed')) g.done++;
            g.pcs.push(t);
        });
        const nm = (pc) => mem.names[pc] || pc;
        box.innerHTML = Object.values(mem.groups).map(g => {
            const open = mem.expanded.has(g.id);
            const rows = g.pcs.sort((a, b) => nm(a.target_pc).localeCompare(nm(b.target_pc), 'tr', { numeric: true })).map(t => {
                const [cls, icon, text] = statusInfo(t.status);
                const canCancel = !FINISHED.includes(t.status);
                const name = nm(t.target_pc);
                return `<tr>
                    <td><div class="cell-title">${escapeHtml(name)}</div>${name !== t.target_pc ? `<div class="cell-sub mono">${escapeHtml(t.target_pc)}</div>` : ''}</td>
                    <td><span class="task-status ${cls}"><i class="fas ${icon}"></i> ${escapeHtml(text)}</span></td>
                    <td class="mono text-xs text-muted">${escapeHtml(t.created_at || '-')}</td>
                    <td class="actions"><div class="row-actions">
                        <button type="button" class="btn secondary sm" data-action="detail" data-task="${escapeHtml(t.id)}" data-group="${g.id}"><i class="fas fa-file-lines"></i> Sonuç</button>
                        ${CAN_ADMIN && FINISHED.includes(t.status) ? `<button type="button" class="btn ghost icon sm" data-action="task" data-op="RETRY" data-mode="TASK" data-target="${escapeHtml(t.id)}" title="Yeniden çalıştır" aria-label="Yeniden çalıştır"><i class="fas fa-rotate-right"></i></button>` : ''}
                        ${CAN_ADMIN && canCancel ? `<button type="button" class="btn ghost icon sm" data-action="task" data-op="CANCEL" data-mode="TASK" data-target="${escapeHtml(t.id)}" title="İptal et" aria-label="İptal et"><i class="fas fa-xmark"></i></button>` : ''}
                    </div></td>
                </tr>`;
            }).join('');
            const labActions = CAN_ADMIN && g.lab !== 'Tek cihaz' ? `
                <button type="button" class="btn ghost icon sm" data-action="task" data-op="PAUSE" data-mode="LAB" data-target="${escapeHtml(g.lab)}" title="Bu sınıfta bekleyenleri duraklat" aria-label="Duraklat"><i class="fas fa-pause"></i></button>
                <button type="button" class="btn ghost icon sm" data-action="task" data-op="RESUME" data-mode="LAB" data-target="${escapeHtml(g.lab)}" title="Devam ettir" aria-label="Devam ettir"><i class="fas fa-play"></i></button>
                <button type="button" class="btn ghost icon sm" data-action="task" data-op="CANCEL" data-mode="LAB" data-target="${escapeHtml(g.lab)}" title="Bu sınıftakileri iptal et" aria-label="İptal et"><i class="fas fa-xmark"></i></button>` : '';
            return `<div class="task-group ${open ? 'open' : ''}" data-group-id="${g.id}">
                <div class="task-group-head" data-action="toggle" data-group="${g.id}" role="button" tabindex="0" aria-expanded="${open}">
                    <div class="task-group-info">
                        <span class="task-lab"><i class="fas fa-layer-group"></i> ${escapeHtml(g.lab)}</span>
                        <span class="task-cmd" title="${escapeHtml(g.command)}">${escapeHtml(g.command)}</span>
                    </div>
                    <div class="task-group-side">
                        <span class="badge muted"><i class="fas fa-desktop"></i> ${g.pcs.length}</span>
                        <span class="badge success">${g.done} tamamlandı</span>
                        ${labActions}
                        <i class="fas fa-chevron-down chev"></i>
                    </div>
                </div>
                <div class="task-group-body"><table class="data-table"><thead><tr><th>Bilgisayar</th><th>Durum</th><th>Eklenme</th><th class="actions">İşlem</th></tr></thead><tbody>${rows}</tbody></table></div>
            </div>`;
        }).join('');
    }

    async function taskAction(btn, op, mode, target) {
        const scope = mode === 'TASK' ? 'Bu görev' : mode === 'LAB' ? `"${target}" sınıfındaki görevler` : 'Kuyruktaki görevler';
        const ok = await POps.confirm({ title: 'Emin misiniz?', message: `${scope} ${ACTION_TEXT[op]}.`, confirmText: 'Uygula', danger: op === 'CANCEL' });
        if (!ok) return;
        const done = await POps.act(btn, () => POps.post('/api/tasks/action', { action: op, target_mode: mode, target_id: String(target) }),
            { success: (r) => r && r.changed === 0 ? 'Değişecek görev yoktu.' : `${(r && r.changed) || 0} görev güncellendi.` });
        if (done) { mem.tasksKey = null; loadTasks(); }
    }

    function openDetail(taskId, groupId) {
        const g = mem.groups[groupId];
        const t = g && g.pcs.find(p => String(p.id) === String(taskId));
        if (!t) return;
        $('modalPcName').textContent = mem.names[t.target_pc] || t.target_pc;
        $('modalCommand').textContent = g.command;
        const out = $('modalOutput');
        out.textContent = t.output || 'Bilgisayardan henüz yanıt gelmedi.';
        const bad = ['Failed', 'Error', 'Denied', 'Interrupted', 'Timed Out', 'Expired'].includes(t.status);
        out.classList.toggle('error', bad);
        out.classList.toggle('output', !bad);
        openModal('outputModal');
        out.scrollTop = 0;
    }

    // ---- Ajan etkinliği ----
    async function loadLogs() {
        try {
            const logs = await POps.get('/api/logs?limit=200');
            const key = JSON.stringify(logs);
            if (key === mem.logsKey) return;
            mem.logsKey = key;
            mem.logs = Array.isArray(logs) ? logs : [];
            const addOptions = (sel, values, label) => {
                const have = new Set([...sel.options].map(o => o.value));
                values.filter(v => v && !have.has(v)).forEach(v => sel.append(POps.el('option', { value: v, text: label(v) })));
            };
            addOptions($('logPcFilter'), [...new Set(mem.logs.map(l => l.pc_name))], pc => mem.names[pc] || pc);
            addOptions($('logTypeFilter'), [...new Set(mem.logs.map(logCategory))], c => CATEGORY[c] || c);
            renderLogs();
        } catch (e) {
            if (!mem.logs.length) POps.setError($('agentLogsList'), e);
        }
    }
    // Kayıtlar (agent_logs_v2) kategori ve risk düzeyiyle gelir; eski şemada log_type vardı
    const CATEGORY = { security: 'Güvenlik', system_maintenance: 'Bakım', restricted_content: 'Kural ihlali', user_activity: 'Kullanıcı', network: 'Ağ', deployment: 'Dağıtım', hardware: 'Donanım', auth: 'Giriş' };
    const CATEGORY_ICON = { security: 'fa-shield-halved', system_maintenance: 'fa-screwdriver-wrench', restricted_content: 'fa-ban', user_activity: 'fa-user', network: 'fa-globe', deployment: 'fa-terminal', hardware: 'fa-microchip', auth: 'fa-right-to-bracket' };
    const RISK_TONE = { critical: 'danger', high: 'danger', medium: 'warning', low: 'info', info: 'muted' };
    const logCategory = (l) => l.category || l.log_type || 'diger';
    function renderLogs() {
        const box = $('agentLogsList');
        const pc = $('logPcFilter').value, type = $('logTypeFilter').value, q = $('logSearchInput').value.toLowerCase().trim();
        const list = mem.logs.filter(l => (pc === 'ALL' || l.pc_name === pc) && (type === 'ALL' || logCategory(l) === type) && (!q || String(l.message || '').toLowerCase().includes(q)));
        if (!list.length) { POps.setEmpty(box, { icon: 'fa-magnifying-glass', title: mem.logs.length ? 'Eşleşen kayıt yok' : 'Henüz kayıt yok', compact: true }); return; }
        box.innerHTML = list.map(l => `<div class="log-line risk-${escapeHtml(l.risk_level || 'info')}">
            <span class="log-time">${escapeHtml(l.timestamp ? String(l.timestamp).split(' ')[1] || '' : '')}</span>
            <span class="badge ${RISK_TONE[l.risk_level] || 'muted'} log-type"><i class="fas ${CATEGORY_ICON[logCategory(l)] || 'fa-circle-info'}"></i> ${escapeHtml(CATEGORY[logCategory(l)] || logCategory(l))}</span>
            <span class="log-pc" title="${escapeHtml(l.pc_name)}">${escapeHtml(mem.names[l.pc_name] || l.pc_name)}</span>
            <span class="log-msg">${escapeHtml(l.message)}</span>
        </div>`).join('');
    }
    ['logPcFilter', 'logTypeFilter'].forEach(id => $(id).addEventListener('change', renderLogs));
    $('logSearchInput').addEventListener('input', renderLogs);

    // ---- Zamanlanmış görevler ----
    const DAYS = ['Pzt', 'Sal', 'Çar', 'Per', 'Cum', 'Cmt', 'Paz'];
    const fmt = (iso) => { if (!iso) return '—'; try { return new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }); } catch (e) { return String(iso); } };
    let schedLoaded = false;
    async function loadSchedules() {
        let d;
        try { d = await POps.get('/api/scheduled_tasks'); }
        catch (e) {
            if (e.status === 403 || e.status === 409) { $('schedCard').classList.add('hidden'); return; }   // yetki yok ya da modül kapalı
            if (!schedLoaded) POps.setError($('schedList'), e);
            return;
        }
        schedLoaded = true;
        $('schedCard').classList.remove('hidden');
        $('sfTz').textContent = 'Saatler sunucu saatine göredir (şu an ' + fmt(d.server_time) + ').';
        const items = d.items || [];
        if (!items.length) { POps.setEmpty($('schedList'), { icon: 'fa-calendar', title: 'Zamanlanmış görev yok', text: 'Belirli saatlerde tekrar eden komutlar için yeni görev ekleyin.', compact: true }); return; }
        const when = (t) => t.schedule_type === 'once' ? 'Bir kez · ' + fmt(t.run_at) : t.schedule_type === 'daily' ? 'Her gün ' + (t.time_of_day || '') : (t.weekdays || []).map(x => DAYS[x - 1]).join(', ') + ' ' + (t.time_of_day || '');
        const target = (t) => t.target_mode === 'ALL' ? 'Bütün bilgisayarlar' : t.target_mode === 'LAB' ? 'Sınıf: ' + (t.targets || []).join(', ') : (t.targets || []).length + ' bilgisayar';
        $('schedList').innerHTML = items.map(t => `<div class="sched-row${t.enabled ? '' : ' off'}">
            <div><div class="n">${escapeHtml(t.name)}</div><div class="c" title="${escapeHtml(t.command)}">${escapeHtml(t.command)}</div></div>
            <div><div>${escapeHtml(when(t))}</div><div class="sub">${escapeHtml(target(t))}</div></div>
            <div><div>${t.enabled && t.next_run ? 'Sıradaki: ' + escapeHtml(fmt(t.next_run)) : 'Durduruldu'}</div>
                <div class="sub">${t.last_run ? 'Son: ' + escapeHtml(fmt(t.last_run)) + (t.last_result ? ' · ' + escapeHtml(t.last_result) : '') : 'Henüz çalışmadı'}</div></div>
            <div class="row-actions">${CAN_ADMIN ? `
                <button type="button" class="btn secondary sm" data-action="sched-run" data-id="${escapeHtml(t.id)}"><i class="fas fa-play"></i> Şimdi çalıştır</button>
                <button type="button" class="btn secondary sm" data-action="sched-toggle" data-id="${escapeHtml(t.id)}" data-on="${t.enabled ? 1 : 0}">${t.enabled ? '<i class="fas fa-pause"></i> Durdur' : '<i class="fas fa-play"></i> Başlat'}</button>
                <button type="button" class="btn ghost icon sm" data-action="sched-del" data-id="${escapeHtml(t.id)}" title="Sil" aria-label="Sil"><i class="fas fa-trash"></i></button>` : ''}
            </div>
        </div>`).join('');
    }

    async function schedAction(btn) {
        const id = encodeURIComponent(btn.dataset.id);
        if (btn.dataset.action === 'sched-run') {
            if (!await POps.confirm({ title: 'Şimdi çalıştırılsın mı?', message: 'Görev hedef bilgisayarlarda bir kez, hemen çalıştırılacak.', confirmText: 'Çalıştır' })) return;
            if (await POps.act(btn, () => POps.post(`/api/scheduled_tasks/${id}/run`), { success: (r) => `${(r && r.queued) || 0} bilgisayar için kuyruğa eklendi.` })) { mem.tasksKey = null; loadTasks(); }
        } else if (btn.dataset.action === 'sched-toggle') {
            const on = btn.dataset.on !== '1';
            if (await POps.act(btn, () => POps.post(`/api/scheduled_tasks/${id}/toggle`, { enabled: on }), { success: on ? 'Görev başlatıldı.' : 'Görev durduruldu.' })) loadSchedules();
        } else {
            if (!await POps.confirm({ title: 'Zamanlanmış görev silinsin mi?', message: 'Kuyruğa daha önce eklenmiş görevler etkilenmez.', confirmText: 'Sil', danger: true })) return;
            if (await POps.act(btn, () => POps.del(`/api/scheduled_tasks/${id}`), { success: 'Zamanlanmış görev silindi.' })) loadSchedules();
        }
    }

    function syncForm() {
        const t = $('sfType').value, m = $('sfMode').value;
        $('sfTimeWrap').classList.toggle('hidden', t === 'once');
        $('sfAtWrap').classList.toggle('hidden', t !== 'once');
        $('sfDaysWrap').classList.toggle('hidden', t !== 'weekly');
        $('sfLabWrap').classList.toggle('hidden', m !== 'LAB');
        $('sfDevsWrap').classList.toggle('hidden', m !== 'PC');
    }
    async function openSchedForm() {
        syncForm();
        openModal('schedModal');
        setTimeout(() => $('sfName').focus(), 50);
        let devices = [];
        try { devices = await POps.get('/api/devices'); } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        const labs = [...new Set(devices.map(d => d.lab).filter(Boolean))].sort((a, b) => a.localeCompare(b, 'tr'));
        $('sfLab').replaceChildren(...(labs.length ? labs.map(l => POps.el('option', { value: l, text: l })) : [POps.el('option', { value: '', text: 'Sınıf yok' })]));
        $('sfDevs').replaceChildren(...(devices.length ? devices.map(d => POps.el('label', { className: 'check' }, [
            POps.el('input', { type: 'checkbox', value: d.hw_id }), document.createTextNode(' ' + POps.deviceName(d) + ' '),
            POps.el('span', { className: 'text-xs text-muted', text: (d.lab || '') + ' · ' + d.hw_id })
        ])) : [POps.el('span', { className: 'text-muted text-sm', text: 'Cihaz yok' })]));
    }
    async function saveSchedule(btn) {
        const mode = $('sfMode').value;
        const body = {
            name: $('sfName').value.trim(), command: $('sfCmd').value.trim(), target_mode: mode,
            targets: mode === 'LAB' ? [$('sfLab').value].filter(Boolean) : mode === 'PC' ? [...$('sfDevs').querySelectorAll('input:checked')].map(i => i.value) : [],
            schedule_type: $('sfType').value, time_of_day: $('sfTime').value, run_at: $('sfAt').value || null,
            weekdays: [...$('sfDays').querySelectorAll('input:checked')].map(i => parseInt(i.value, 10)),
            enabled: true
        };
        if (!body.name) { POps.toast('warning', 'Göreve bir ad verin.'); $('sfName').focus(); return; }
        if (!body.command) { POps.toast('warning', 'Çalıştırılacak komutu yazın.'); $('sfCmd').focus(); return; }
        if (mode !== 'ALL' && !body.targets.length) { POps.toast('warning', mode === 'LAB' ? 'Bir sınıf seçin.' : 'En az bir bilgisayar seçin.'); return; }
        if (body.schedule_type === 'weekly' && !body.weekdays.length) { POps.toast('warning', 'En az bir gün seçin.'); return; }
        const ok = await POps.confirm({ title: 'Görev zamanlansın mı?', message: 'Komut hedef bilgisayarlarda SYSTEM hesabıyla çalışacak:', note: body.command.slice(0, 300), confirmText: 'Zamanla' });
        if (!ok) return;
        if (await POps.act(btn, () => POps.post('/api/scheduled_tasks', body), { success: 'Zamanlanmış görev kaydedildi.' })) {
            closeModal('schedModal');
            $('sfName').value = ''; $('sfCmd').value = '';
            loadSchedules();
        }
    }
    if (CAN_ADMIN) {
        $('sfDays').replaceChildren(...DAYS.map((d, i) => POps.el('label', { className: 'chip' }, [POps.el('input', { type: 'checkbox', value: String(i + 1), checked: i < 5 }), document.createTextNode(' ' + d)])));
        ['sfType', 'sfMode'].forEach(id => $(id).addEventListener('change', syncForm));
        $('sfSave').addEventListener('click', (e) => saveSchedule(e.currentTarget));
    }

    // ---- Tıklamalar ----
    document.querySelector('.app-content').addEventListener('click', async (e) => {
        const b = e.target.closest('[data-action]');
        if (!b) return;
        switch (b.dataset.action) {
            case 'toggle': {
                if (e.target.closest('button')) return;   // başlıktaki düğmeler grubu açıp kapatmaz
                const el = b.closest('.task-group');
                const open = el.classList.toggle('open');
                b.setAttribute('aria-expanded', open);
                open ? mem.expanded.add(b.dataset.group) : mem.expanded.delete(b.dataset.group);
                break;
            }
            case 'detail': openDetail(b.dataset.task, b.dataset.group); break;
            case 'task': taskAction(b, b.dataset.op, b.dataset.mode, b.dataset.target); break;
            case 'bulk': taskAction(b, b.dataset.op, 'ALL', 'GLOBAL'); break;
            case 'clear':
                if (!await POps.confirm({ title: 'Bütün görev kayıtları silinsin mi?', message: 'Bekleyen, çalışan ve biten bütün görevler sonuçlarıyla birlikte silinecek. Bilgisayarda çalışmakta olan komut durmaz ama sonucu kaydedilmez. Silme işlemi denetim kaydına yazılır.', confirmText: 'Hepsini sil', danger: true })) return;
                if (await POps.act(b, () => POps.post('/api/flush_queue'), { success: 'Görev kayıtları silindi.' })) { mem.tasksKey = null; loadTasks(); }
                break;
            case 'sched-new': openSchedForm(); break;
            case 'sched-run': case 'sched-toggle': case 'sched-del': schedAction(b); break;
        }
    });
    document.querySelector('.app-content').addEventListener('keydown', (e) => {
        const head = e.target.closest && e.target.closest('.task-group-head');
        if (head && (e.key === 'Enter' || e.key === ' ') && e.target === head) { e.preventDefault(); head.click(); }
    });

    loadTasks();
    loadLogs();
    loadSchedules();
    popsPoll(() => Promise.all([loadTasks(), loadLogs()]), 5000);
    popsPoll(loadSchedules, 30000);
});
</script>

<?php include 'includes/footer.php'; ?>
