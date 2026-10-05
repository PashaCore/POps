<?php include 'includes/header.php'; ?>
<?php $tkCanAdmin = in_array($_SESSION['role'] ?? '', ['admin', 'superadmin'], true); ?>

<style>
    .tk-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
    .tk-bar .grow { flex: 1; }
    .tk-bar .search-field { flex: 0 1 260px; min-width: 180px; }
    .tk-list { padding: 4px 16px; }
    .tk-list .act { cursor: pointer; padding: 14px 6px; border-radius: 10px; }
    .tk-list .act:hover { background: var(--bg-surface-2); }
    .tk-list .act.is-focus { background: #f0f6ff; }
    .tk-prog { width: 150px; display: flex; flex-direction: column; gap: 5px; align-items: stretch; }
    .tk-prog .row { display: flex; justify-content: space-between; gap: 8px; }
    .tk-prog .cnt { font-size: var(--text-xs); color: var(--text-muted); font-variant-numeric: tabular-nums; }
    .tk-cmd { font-family: var(--font-mono); font-size: 12px; background: var(--bg-app); border-radius: 10px; padding: 10px 12px; white-space: pre-wrap; overflow-wrap: anywhere; max-height: 180px; overflow: auto; }
    .tk-actions { display: flex; gap: 8px; flex-wrap: wrap; }
    .tk-dev .act { padding: 10px 2px; }
    .tk-dev .act.has-out { cursor: pointer; }
    .tk-more { text-align: center; padding: 12px; }
    .sched-row { cursor: pointer; }
    .sched-days { gap: 6px; }
    .sched-days label.chip { cursor: pointer; }
    .sched-days label.chip input { display: none; }
    .sched-days label.chip:has(input:checked) { background: var(--text-primary); border-color: var(--text-primary); color: #fff; }
    .sched-devs { max-height: 220px; overflow-y: auto; border-radius: 10px; background: var(--bg-app); padding: 8px 12px; display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 4px 12px; }
    @media (max-width: 640px) { .tk-prog { width: 96px; } }
</style>

<div class="page-header">
    <div>
        <h1>İşlemler</h1>
        <div class="summary" id="tkSummary"></div>
    </div>
    <div class="page-header-actions">
        <?php if ($tkCanAdmin): ?>
        <button type="button" class="btn" id="schedNewBtn" hidden><?php echo pops_icon('plus', 'sm'); ?>Zamanlanmış görev</button>
        <button type="button" class="ibtn boxed" id="tkLimitBtn" data-tip="Aynı anda çalışacak görev sayısı" data-tip-pos="left" aria-label="Eşzamanlı görev sınırı"><?php echo pops_icon('sliders'); ?></button>
        <button type="button" class="ibtn boxed" id="tkMenuBtn" data-tip="Kuyruk işlemleri" data-tip-pos="left" aria-label="Kuyruk işlemleri" aria-haspopup="menu"><?php echo pops_icon('more'); ?></button>
        <?php endif; ?>
    </div>
</div>

<div class="tk-bar">
    <div class="segmented" id="tkTab" role="tablist" aria-label="Görünüm">
        <button type="button" data-tab="jobs" class="active" aria-selected="true">İşler</button>
        <button type="button" data-tab="sched" aria-selected="false">Zamanlanmış <span class="faint" id="schedCount"></span></button>
    </div>
    <span class="grow"></span>
    <div class="segmented" id="tkFilter" role="group" aria-label="Duruma göre süz">
        <button type="button" data-f="all" class="active" aria-pressed="true">Tümü</button>
        <button type="button" data-f="run" aria-pressed="false">Sürüyor</button>
        <button type="button" data-f="bad" aria-pressed="false">Sorunlu</button>
    </div>
    <div class="search-field">
        <?php echo pops_icon('search', 'sm'); ?>
        <input type="search" id="tkSearch" placeholder="İş, bilgisayar ya da kişi" aria-label="İşlerde ara">
    </div>
</div>

<div class="card" id="jobsCard"><div class="tk-list" id="jobList"></div></div>
<div class="card" id="schedCard" hidden><div class="card-body flush"><div id="schedList"></div></div></div>
<div class="set-note" id="tkNote"></div>

<?php if ($tkCanAdmin): ?>
<div id="schedModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="schedTitle">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="schedTitle">Yeni zamanlanmış görev</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <div class="form-grid">
                <div class="field"><label for="sfName">Ad</label><input id="sfName" maxlength="100" placeholder="Örn. Gece temizliği"></div>
                <div class="field"><label for="sfType">Zamanlama</label>
                    <select id="sfType"><option value="daily">Her gün</option><option value="weekly">Seçili günler</option><option value="once">Bir kez</option></select></div>
                <div class="field" id="sfTimeWrap"><label for="sfTime">Saat</label><input id="sfTime" type="time" value="03:00"></div>
                <div class="field hidden" id="sfAtWrap"><label for="sfAt">Tarih ve saat</label><input id="sfAt" type="datetime-local"></div>
            </div>
            <div class="field hidden" id="sfDaysWrap"><span class="field-label">Günler</span><div class="sched-days chip-row" id="sfDays"></div></div>
            <div class="field"><label for="sfCmd">Komut</label><textarea id="sfCmd" maxlength="4000" rows="3" class="text-mono" placeholder="Örn. cleanmgr /sagerun:1"></textarea>
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
            <button type="button" class="btn" id="sfSave">Kaydet</button>
        </div>
    </div>
</div>
<?php endif; ?>

<script>
(function () {
    const CAN_ADMIN = <?php echo $tkCanAdmin ? 'true' : 'false'; ?>;
    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const params = new URLSearchParams(location.search);
    const ui = { tab: CAN_ADMIN && params.get('tab') === 'sched' ? 'sched' : 'jobs', f: 'all', q: '', limit: 60, focus: null, tasks: [], jobs: [], key: null, loaded: false, names: {}, sched: [], schedLoaded: false };

    const nm = (pc) => ui.names[pc] || pc;
    const WAITING = ['Pending', 'Paused'];

    // ---- İşler: aynı istekte açılan görevler tek iş (batch_id); eski kayıtlarda zaman + kişi + komut
    function groupJobs(tasks) {
        const map = new Map();
        tasks.forEach(t => {
            const key = t.batch_id || ['legacy', t.created_at, t.created_by, t.script_path, t.retry_of ? 'r' : ''].join('|');
            let j = map.get(key);
            if (!j) { j = { key, tasks: [], title: dev.taskTitle(t), by: t.created_by, at: t.created_at, source: t.source, reason: t.reason, ip: t.client_ip, command: t.script_path, minId: t.id }; map.set(key, j); }
            j.tasks.push(t);
            if (t.id < j.minId) { j.minId = t.id; }
        });
        const jobs = [...map.values()].map(j => {
            const c = { ok: 0, bad: 0, run: 0, wait: 0, total: j.tasks.length };
            j.tasks.forEach(t => { const k = POps.taskState(t.status); c[k] += 1; if (WAITING.includes(t.status)) c.wait += 1; });
            j.c = c;
            j.state = c.run ? 'run' : c.bad ? 'bad' : 'ok';
            j.labs = [...new Set(j.tasks.map(t => t.target_lab).filter(l => l && l !== dev.UNASSIGNED))];
            return j;
        });
        jobs.sort((a, b) => b.minId - a.minId);
        return jobs;
    }
    function jobWord(j) {
        if (j.c.run) return j.c.wait === j.c.run && !j.tasks.some(t => t.status === 'Running') ? (j.tasks.some(t => t.status === 'Paused') ? 'Duraklatıldı' : 'Sırada') : 'Sürüyor';
        if (j.c.bad) return j.c.bad === j.c.total ? (j.c.total === 1 ? dev.statusWord(j.tasks[0].status) : 'Başarısız') : `${j.c.bad} başarısız`;
        return 'Tamamlandı';
    }
    function targetText(j) {
        if (j.c.total === 1) return nm(j.tasks[0].target_pc);
        return (j.labs.length === 1 ? j.labs[0] + ' · ' : j.labs.length > 1 ? j.labs.length + ' sınıf · ' : '') + j.c.total + ' bilgisayar';
    }
    function matches(j) {
        if (ui.f === 'run' && j.state !== 'run') return false;
        if (ui.f === 'bad' && !j.c.bad) return false;
        if (!ui.q) return true;
        const q = ui.q.toLocaleLowerCase('tr');
        return [j.title, j.command, j.by, j.reason, ...j.tasks.map(t => nm(t.target_pc)), ...j.labs].some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
    }
    function pbarHtml(c) {
        const segHtml = (cls, n) => n ? `<i class="${escapeHtml(cls)}" style="width:${(n / c.total * 100).toFixed(2)}%"></i>` : '';
        return `<div class="pbar">${segHtml('ok', c.ok)}${segHtml('bad', c.bad)}${segHtml('run', c.run - c.wait)}</div>`;
    }
    function jobRowHtml(j) {
        const icon = j.state === 'ok' ? 'check' : j.state === 'bad' ? 'x' : 'clock';
        const metaHtml = escapeHtml(j.by || 'sistem') + ' · ' + POps.timeHtml(j.at) + (j.source ? ' · ' + escapeHtml(dev.sourceText(j.source)) : '') + ' · ' + escapeHtml(targetText(j));
        const why = j.c.bad && j.c.total === 1 ? dev.failReason(j.tasks[0]) : '';
        return `<div class="act${ui.focus === j.key ? ' is-focus' : ''}" data-job="${escapeHtml(j.key)}" role="button" tabindex="0">
            <div class="res ${escapeHtml(j.state)}">${POps.iconHtml(icon)}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(j.title)}</div><div class="meta">${metaHtml}${j.reason ? ' · gerekçe: ' + escapeHtml(j.reason) : ''}</div>${why ? `<div class="why">${escapeHtml(why)}</div>` : ''}</div>
            <div class="side tk-prog"><div class="row"><span class="word ${escapeHtml(j.state)}">${escapeHtml(jobWord(j))}</span><span class="cnt">${j.c.bad && j.state === 'run' ? `<span class="word bad">${Number(j.c.bad)} hata</span> · ` : ''}${Number(j.c.ok + j.c.bad)}/${Number(j.c.total)}</span></div>${j.c.total > 1 ? pbarHtml(j.c) : ''}</div>
        </div>`;
    }
    function renderSummary() {
        const today = new Date().toDateString();
        const isToday = (j) => { const d = POps.toDate(j.at); return d && d.toDateString() === today; };
        const run = ui.jobs.filter(j => j.state === 'run').length;
        const badToday = ui.jobs.filter(j => j.c.bad && isToday(j)).length;
        const okToday = ui.jobs.filter(j => j.state === 'ok' && isToday(j)).length;
        $('tkSummary').innerHTML = `<span class="sum"><span class="dot run"></span><b>${Number(run)}</b> sürüyor</span>`
            + `<a href="#" class="sum" data-f="bad"><span class="dot bad"></span><b>${Number(badToday)}</b> sorunlu (bugün)</a>`
            + `<span class="sum"><span class="dot ok"></span><b>${Number(okToday)}</b> tamamlandı (bugün)</span>`;
    }
    function renderJobs() {
        renderSummary();
        const box = $('jobList');
        const list = ui.jobs.filter(matches);
        if (!list.length) {
            POps.setEmpty(box, ui.jobs.length
                ? { icon: 'filter', title: 'Süzgece uyan iş yok', text: 'Arama ya da süzgeci değiştirin.' }
                : { icon: 'check', kind: 'success', title: 'Henüz işlem yok', text: 'Sınıflar, Cihazlar, Uzak komut ya da Dağıtım sayfasından gönderilen her iş burada görünür.' });
            return;
        }
        const shown = list.slice(0, ui.limit);
        box.innerHTML = shown.map(jobRowHtml).join('') + (list.length > shown.length ? `<div class="tk-more"><button type="button" class="btn secondary sm" data-more="1">${Number(list.length - shown.length)} iş daha göster</button></div>` : '');
        $('tkNote').textContent = 'Son 1000 görev gösterilir.';
    }

    async function loadTasks() {
        try {
            const [devices, tasks] = await Promise.all([POps.get('/api/devices').catch(() => null), POps.get('/api/tasks?limit=1000')]);
            if (Array.isArray(devices)) devices.forEach(d => { ui.names[d.hostname] = POps.deviceName(d); });
            const key = JSON.stringify(tasks);
            if (key === ui.key) return;
            ui.key = key;
            ui.loaded = true;
            ui.tasks = Array.isArray(tasks) ? tasks : [];
            ui.jobs = groupJobs(ui.tasks);
            if (ui.tab === 'jobs') renderJobs();
            if (ui.focus && POps.drawer.isOpen('job:' + ui.focus)) renderDrawer(ui.focus, true);
            const want = params.get('job');
            if (want && !ui.opened) { ui.opened = true; if (ui.jobs.some(j => j.key === want)) openJob(want); }
        } catch (e) {
            if (!ui.loaded) POps.setError($('jobList'), e);
        }
    }

    // ---- İş ayrıntısı
    function taskRowHtml(t, open) {
        const k = POps.taskState(t.status);
        const why = k === 'bad' ? dev.failReason(t) : '';
        const exitHtml = t.exit_code != null && t.exit_code !== 0 ? ' · çıkış kodu ' + Number(t.exit_code) : '';
        const startedHtml = t.dispatched_at ? ' · başladı ' + POps.timeHtml(t.dispatched_at) : '';
        return `<div class="act${t.output ? ' has-out' : ''}" data-task="${Number(t.id)}">
            <div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(nm(t.target_pc))}</div><div class="meta">${escapeHtml(t.target_lab && t.target_lab !== dev.UNASSIGNED ? t.target_lab : 'Atanmamış')}${startedHtml}${exitHtml}${t.output ? ' · çıktıyı ' + (open ? 'gizle' : 'göster') : ''}</div>
                ${why ? `<div class="why">${escapeHtml(why)}</div>` : ''}${open && t.output ? `<div class="out">${escapeHtml(t.output)}</div>` : ''}</div>
            <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(dev.statusWord(t.status))}</span></div>
        </div>`;
    }
    const openOutputs = new Set();
    function renderDrawer(key, keepScroll) {
        const j = ui.jobs.find(x => x.key === key);
        const body = POps.drawer.body();
        if (!j) { body.innerHTML = '<div class="empty-state"><h3>İş bulunamadı</h3><p>Kayıtları silinmiş olabilir.</p></div>'; return; }
        const scroll = keepScroll ? body.scrollTop : 0;
        const tasks = j.tasks.slice().sort((a, b) => { const o = { bad: 0, run: 1, ok: 2 }; return o[POps.taskState(a.status)] - o[POps.taskState(b.status)] || nm(a.target_pc).localeCompare(nm(b.target_pc), 'tr', { numeric: true }); });
        const facts = [['Gönderen', j.by || 'sistem'], ['Kaynak', dev.sourceText(j.source)], ['IP', j.ip], ['Gerekçe', j.reason], ['Hedef', targetText(j)]];
        const factsHtml = facts.filter(f => f[1]).map(f => `<div class="grow"><span>${escapeHtml(f[0])}</span><span>${escapeHtml(f[1])}</span></div>`).join('')
            + `<div class="grow"><span>Zaman</span><span>${escapeHtml(POps.fullTime(j.at) || j.at || '—')}</span></div>`;
        const failed = j.tasks.filter(t => POps.taskState(t.status) === 'bad');
        const waiting = j.tasks.filter(t => WAITING.includes(t.status) || t.status === 'Running');
        const pending = j.tasks.filter(t => t.status === 'Pending');
        const paused = j.tasks.filter(t => t.status === 'Paused');
        const actionsHtml = CAN_ADMIN ? `<div class="tk-actions">
            ${failed.length ? `<button type="button" class="btn sm" data-op="RETRY" data-ids="${escapeHtml(failed.map(t => t.id).join(','))}">${POps.iconHtml('restart', 'sm')}Başarısızları yeniden dene (${Number(failed.length)})</button>` : ''}
            ${pending.length ? `<button type="button" class="btn secondary sm" data-op="PAUSE" data-ids="${escapeHtml(pending.map(t => t.id).join(','))}">${POps.iconHtml('pause', 'sm')}Duraklat</button>` : ''}
            ${paused.length ? `<button type="button" class="btn secondary sm" data-op="RESUME" data-ids="${escapeHtml(paused.map(t => t.id).join(','))}">${POps.iconHtml('play', 'sm')}Devam ettir</button>` : ''}
            ${waiting.length ? `<button type="button" class="btn danger-soft sm" data-op="CANCEL" data-ids="${escapeHtml(waiting.map(t => t.id).join(','))}">${POps.iconHtml('x', 'sm')}İptal et (${Number(waiting.length)})</button>` : ''}
        </div>` : '';
        body.innerHTML = `<div class="drawer-head">
                <div class="drawer-title"><span class="drawer-ico ${j.state === 'ok' ? 'on' : j.state === 'run' ? '' : 'idle'}">${POps.iconHtml('jobs', 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(j.title)}</h2><div class="sub"><span class="dot ${escapeHtml(j.state)}"></span>${escapeHtml(jobWord(j))} · ${Number(j.c.ok + j.c.bad)}/${Number(j.c.total)}</div></div></div>
                <button type="button" class="ibtn sm" data-close="1" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button>
            </div>
            ${j.c.total > 1 ? pbarHtml(j.c) : ''}
            ${actionsHtml}
            <div class="glist">${factsHtml}</div>
            ${j.command ? `<div><h3>Komut</h3><div class="tk-cmd">${escapeHtml(j.command)}</div></div>` : ''}
            <div class="tk-dev"><h3>Bilgisayarlar</h3>${tasks.map(t => taskRowHtml(t, openOutputs.has(t.id))).join('')}</div>`;
        body.scrollTop = scroll;
    }
    function openJob(key) {
        ui.focus = key;
        openOutputs.clear();
        POps.drawer.open('job:' + key, { onClose: () => { if (ui.focus === key) { ui.focus = null; renderJobs(); } } }).onclick = null;
        renderDrawer(key);
        renderJobs();
    }
    POps.drawer.body().addEventListener('click', async (e) => {
        if (!POps.drawer.isOpen('job:' + ui.focus)) return;
        if (e.target.closest('[data-close]')) { POps.drawer.close(); return; }
        const op = e.target.closest('[data-op]');
        if (op) {
            const ids = op.dataset.ids.split(',').filter(Boolean);
            const verb = { RETRY: 'yeniden denensin', PAUSE: 'duraklatılsın', RESUME: 'devam ettirilsin', CANCEL: 'iptal edilsin' }[op.dataset.op];
            if (op.dataset.op === 'CANCEL' || op.dataset.op === 'RETRY') {
                const ok = await POps.confirm({ title: `${ids.length} görev ${verb} mi?`, message: op.dataset.op === 'CANCEL' ? 'Çalışmakta olan komut da durdurulur.' : 'Her biri için yeni bir görev açılır; eski kayıtlar sonuçlarıyla kalır.', confirmText: op.dataset.op === 'CANCEL' ? `${ids.length} görevi iptal et` : `${ids.length} görevi yeniden dene`, danger: op.dataset.op === 'CANCEL' });
                if (!ok) return;
            }
            await POps.busy(op, async () => {
                const job = ui.jobs.find(x => x.key === ui.focus);
                const res = await Promise.allSettled(ids.map(id => POps.post('/api/tasks/action', { action: op.dataset.op, target_mode: 'TASK', target_id: String(id) }, op.dataset.op === 'RETRY' ? { jobTitle: (job ? job.title : 'Görev') + ' · yeniden' } : undefined)));
                const bad = res.filter(r => r.status === 'rejected');
                if (bad.length) POps.toast('error', `${bad.length} görev güncellenemedi: ${POps.errorMessage(bad[0].reason)}`);
                else POps.toast('success', `${ids.length} görev güncellendi.`);
            });
            ui.key = null;
            loadTasks();
            return;
        }
        const row = e.target.closest('.act.has-out[data-task]');
        if (row) {
            const id = Number(row.dataset.task);
            openOutputs.has(id) ? openOutputs.delete(id) : openOutputs.add(id);
            renderDrawer(ui.focus, true);
        }
    });

    $('jobList').addEventListener('click', (e) => {
        if (e.target.closest('[data-more]')) { ui.limit += 60; renderJobs(); return; }
        const row = e.target.closest('[data-job]');
        if (row && !e.target.closest('a, time')) openJob(row.dataset.job);
    });
    $('jobList').addEventListener('keydown', (e) => { const row = e.target.closest('[data-job]'); if (row && e.key === 'Enter') openJob(row.dataset.job); });
    $('tkFilter').addEventListener('click', (e) => { const b = e.target.closest('[data-f]'); if (b) setFilter(b.dataset.f); });
    $('tkSummary').addEventListener('click', (e) => { const a = e.target.closest('[data-f]'); if (a) { e.preventDefault(); setTab('jobs'); setFilter(a.dataset.f); } });
    function setFilter(f) {
        ui.f = f;
        $('tkFilter').querySelectorAll('button').forEach(b => { const on = b.dataset.f === f; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        renderJobs();
    }
    let qt = null;
    $('tkSearch').addEventListener('input', (e) => { clearTimeout(qt); qt = setTimeout(() => { ui.q = e.target.value.trim(); ui.tab === 'jobs' ? renderJobs() : renderSched(); }, 120); });

    // ---- Sekmeler
    function setTab(tab) {
        ui.tab = tab;
        $('tkTab').querySelectorAll('button').forEach(b => { const on = b.dataset.tab === tab; b.classList.toggle('active', on); b.setAttribute('aria-selected', on ? 'true' : 'false'); });
        $('jobsCard').hidden = tab !== 'jobs';
        $('schedCard').hidden = tab !== 'sched';
        $('tkFilter').hidden = tab !== 'jobs';
        $('tkNote').hidden = tab !== 'jobs';
        if ($('schedNewBtn')) $('schedNewBtn').hidden = tab !== 'sched';
        history.replaceState(null, '', tab === 'sched' ? 'tasks?tab=sched' : 'tasks');
        tab === 'jobs' ? renderJobs() : renderSched();
    }
    $('tkTab').addEventListener('click', (e) => { const b = e.target.closest('[data-tab]'); if (b) setTab(b.dataset.tab); });

    // ---- Zamanlanmış görevler
    const DAYS = ['Pzt', 'Sal', 'Çar', 'Per', 'Cum', 'Cmt', 'Paz'];
    const when = (t) => t.schedule_type === 'once' ? 'Bir kez · ' + (POps.fullTime(t.run_at) || '—') : t.schedule_type === 'daily' ? 'Her gün ' + (t.time_of_day || '') : (t.weekdays || []).map(x => DAYS[x - 1]).join(', ') + ' ' + (t.time_of_day || '');
    const target = (t) => t.target_mode === 'ALL' ? 'Bütün bilgisayarlar' : t.target_mode === 'LAB' ? (t.targets || []).join(', ') : (t.targets || []).length + ' bilgisayar';
    async function loadSched() {
        let d;
        try { d = await POps.get('/api/scheduled_tasks'); }
        catch (e) {
            if (e.status === 403 || e.status === 409) { ui.schedOff = true; $('tkTab').querySelector('[data-tab=sched]').hidden = true; return; }
            if (!ui.schedLoaded && ui.tab === 'sched') POps.setError($('schedList'), e);
            return;
        }
        ui.schedLoaded = true;
        ui.sched = d.items || [];
        ui.serverTime = d.server_time;
        $('schedCount').textContent = ui.sched.length ? String(ui.sched.length) : '';
        if (ui.tab === 'sched') renderSched();
    }
    function renderSched() {
        const box = $('schedList');
        if (!ui.schedLoaded) { POps.setLoading(box, 'Zamanlanmış görevler yükleniyor…'); return; }
        const q = ui.q.toLocaleLowerCase('tr');
        const items = ui.sched.filter(t => !q || [t.name, t.command, target(t)].some(v => String(v || '').toLocaleLowerCase('tr').includes(q)));
        if (!items.length) {
            POps.setEmpty(box, ui.sched.length ? { icon: 'filter', title: 'Süzgece uyan görev yok' } : { icon: 'calendar', title: 'Zamanlanmış görev yok', text: 'Belirli saatlerde tekrar eden komutlar için "Zamanlanmış görev" ile ekleyin.' });
            return;
        }
        box.innerHTML = `<table class="data-table"><thead><tr><th>Görev</th><th>Zaman</th><th>Hedef</th><th>Sıradaki</th><th>Son çalışma</th></tr></thead><tbody>${items.map(t => `
            <tr class="sched-row" data-sched="${Number(t.id)}">
                <td><div class="cell-title">${escapeHtml(t.name)}</div><div class="cell-sub mono">${escapeHtml(String(t.command || '').slice(0, 80))}</div></td>
                <td>${escapeHtml(when(t))}</td>
                <td>${escapeHtml(target(t))}</td>
                <td>${t.enabled && t.next_run ? POps.timeHtml(t.next_run) : '<span class="status-pill offline"><span class="dot off"></span>Durduruldu</span>'}</td>
                <td>${t.last_run ? POps.timeHtml(t.last_run) + (t.last_result ? ' · ' + escapeHtml(t.last_result) : '') : '<span class="faint">Henüz çalışmadı</span>'}</td>
            </tr>`).join('')}</tbody></table>`
            + `<div class="set-note" style="padding:10px 16px">Saatler sunucu saatine göredir${ui.serverTime ? ' (şu an ' + escapeHtml(POps.fullTime(ui.serverTime)) + ')' : ''}.</div>`;
    }
    $('schedList').addEventListener('click', (e) => {
        const row = e.target.closest('[data-sched]');
        if (!row) return;
        const t = ui.sched.find(x => String(x.id) === row.dataset.sched);
        if (!t) return;
        const body = POps.drawer.open('sched:' + t.id);
        body.innerHTML = `<div class="drawer-head"><div class="drawer-title"><span class="drawer-ico ${t.enabled ? 'on' : ''}">${POps.iconHtml('calendar', 'lg')}</span>
                <div style="min-width:0"><h2>${escapeHtml(t.name)}</h2><div class="sub"><span class="dot ${t.enabled ? 'on' : 'off'}"></span>${t.enabled ? 'Etkin' : 'Durduruldu'}</div></div></div>
                <button type="button" class="ibtn sm" data-close="1" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button></div>
            ${CAN_ADMIN ? `<div class="tk-actions">
                <button type="button" class="btn sm" data-sact="run">${POps.iconHtml('play', 'sm')}Şimdi çalıştır</button>
                <button type="button" class="btn secondary sm" data-sact="toggle">${t.enabled ? 'Durdur' : 'Başlat'}</button>
                <button type="button" class="btn danger-soft sm" data-sact="del">${POps.iconHtml('trash', 'sm')}Sil</button></div>` : ''}
            <div class="glist">
                <div class="grow"><span>Zaman</span><span>${escapeHtml(when(t))}</span></div>
                <div class="grow"><span>Hedef</span><span>${escapeHtml(target(t))}</span></div>
                <div class="grow"><span>Sıradaki</span><span>${t.enabled && t.next_run ? POps.timeHtml(t.next_run) : '—'}</span></div>
                <div class="grow"><span>Son çalışma</span><span>${t.last_run ? POps.timeHtml(t.last_run) : '—'}</span></div>
                ${t.last_result ? `<div class="grow"><span>Son sonuç</span><span>${escapeHtml(t.last_result)}</span></div>` : ''}
                ${t.created_by ? `<div class="grow"><span>Oluşturan</span><span>${escapeHtml(t.created_by)}</span></div>` : ''}
            </div>
            <div><h3>Komut</h3><div class="tk-cmd">${escapeHtml(t.command)}</div></div>`;
        body.onclick = async (ev) => {
            if (ev.target.closest('[data-close]')) { POps.drawer.close(); return; }
            const b = ev.target.closest('[data-sact]');
            if (!b) return;
            const id = encodeURIComponent(t.id);
            if (b.dataset.sact === 'run') {
                if (!await POps.confirm({ title: `"${t.name}" şimdi çalıştırılsın mı?`, message: 'Görev hedef bilgisayarlarda bir kez, hemen çalışır.', confirmText: 'Şimdi çalıştır' })) return;
                if (await POps.act(b, () => POps.post(`/api/scheduled_tasks/${id}/run`, {}, { jobTitle: t.name }), { success: (r) => `${(r && r.queued) || 0} bilgisayar için kuyruğa eklendi.` })) { ui.key = null; loadTasks(); }
            } else if (b.dataset.sact === 'toggle') {
                if (await POps.act(b, () => POps.post(`/api/scheduled_tasks/${id}/toggle`, { enabled: !t.enabled }), { success: t.enabled ? 'Görev durduruldu.' : 'Görev başlatıldı.' })) { POps.drawer.close(); loadSched(); }
            } else {
                if (!await POps.confirm({ title: `"${t.name}" silinsin mi?`, message: 'Kuyruğa daha önce eklenmiş görevler etkilenmez.', confirmText: 'Zamanlanmış görevi sil', danger: true })) return;
                if (await POps.act(b, () => POps.del(`/api/scheduled_tasks/${id}`), { success: 'Zamanlanmış görev silindi.' })) { POps.drawer.close(); loadSched(); }
            }
        };
    });

    // ---- Zamanlanmış görev formu
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
        const labs = [...new Set(devices.map(d => d.lab).filter(l => l && l !== dev.UNASSIGNED))].sort((a, b) => a.localeCompare(b, 'tr'));
        $('sfLab').replaceChildren(...(labs.length ? labs.map(l => POps.el('option', { value: l, text: l })) : [POps.el('option', { value: '', text: 'Sınıf yok' })]));
        $('sfDevs').replaceChildren(...(devices.length ? devices.map(d => POps.el('label', { className: 'check' }, [
            POps.el('input', { type: 'checkbox', value: d.hw_id }), document.createTextNode(' ' + POps.deviceName(d) + ' '),
            POps.el('span', { className: 'text-xs text-muted', text: d.lab && d.lab !== dev.UNASSIGNED ? d.lab : '' })
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
            loadSched();
        }
    }

    // ---- Kuyruk işlemleri (yönetici)
    if (CAN_ADMIN) {
        $('sfDays').replaceChildren(...DAYS.map((d, i) => POps.el('label', { className: 'chip' }, [POps.el('input', { type: 'checkbox', value: String(i + 1), checked: i < 5 }), document.createTextNode(d)])));
        ['sfType', 'sfMode'].forEach(id => $(id).addEventListener('change', syncForm));
        $('sfSave').addEventListener('click', (e) => saveSchedule(e.currentTarget));
        $('schedNewBtn').addEventListener('click', openSchedForm);
        $('tkLimitBtn').addEventListener('click', async (e) => {
            let cur = 5;
            try { cur = (await POps.get('/api/get_concurrent_limit')).limit; } catch (err) { /* varsayılan */ }
            const v = await POps.prompt({ title: 'Aynı anda kaç görev çalışsın?', message: 'Sınırın üstündeki görevler sırada bekler; büyük dağıtımlarda ağı korur.', label: 'Görev sayısı', inputType: 'number', inputMode: 'numeric', defaultValue: String(cur), confirmText: 'Kaydet',
                validate: (x) => { const n = parseInt(x, 10); return n >= 1 && n <= 500 ? '' : '1 ile 500 arasında bir sayı girin.'; } });
            if (v === null) return;
            POps.act(e.currentTarget, () => POps.post('/api/set_concurrent_limit', { limit: parseInt(v, 10) }), { success: `Aynı anda en çok ${parseInt(v, 10)} görev çalışacak.` });
        });
        $('tkMenuBtn').addEventListener('click', (e) => {
            const btn = e.currentTarget;
            const bulk = async (op, title, confirmText) => {
                if (!await POps.confirm({ title, message: 'Bütün sınıflardaki bekleyen görevlere uygulanır.', confirmText })) return;
                if (await POps.act(btn, () => POps.post('/api/tasks/action', { action: op, target_mode: 'ALL', target_id: 'GLOBAL' }), { success: (r) => r && r.changed === 0 ? 'Değişecek görev yoktu.' : `${(r && r.changed) || 0} görev güncellendi.` })) { ui.key = null; loadTasks(); }
            };
            POps.menu(btn, [
                { label: 'Bekleyenlerin hepsini duraklat', icon: 'pause', onClick: () => bulk('PAUSE', 'Bekleyen bütün görevler duraklatılsın mı?', 'Hepsini duraklat') },
                { label: 'Duraklatılanları devam ettir', icon: 'play', onClick: () => bulk('RESUME', 'Duraklatılan bütün görevler devam etsin mi?', 'Devam ettir') },
                '-',
                { label: 'Bütün görev kayıtlarını sil', icon: 'trash', danger: true, onClick: async () => {
                    if (!await POps.confirm({ title: 'Bütün görev kayıtları silinsin mi?', message: 'Bekleyen, çalışan ve biten bütün görevler sonuçlarıyla birlikte silinir. Bilgisayarda çalışmakta olan komut durmaz ama sonucu kaydedilmez. Silme işlemi denetim kaydına yazılır.', confirmText: 'Bütün kayıtları sil', danger: true })) return;
                    if (await POps.act(btn, () => POps.post('/api/flush_queue'), { success: 'Görev kayıtları silindi.' })) { ui.key = null; POps.drawer.close(); loadTasks(); }
                } }
            ]);
        });
    }

    POps.setLoading($('jobList'), 'İşler yükleniyor…');
    setTab(ui.tab);
    loadTasks();
    popsPoll(loadTasks, 4000);
    // Zamanlanmış görevler yalnızca yöneticiye açık: izleyici için istenmez (sunucu 403 döner), sekme gizlenir
    if (CAN_ADMIN) {
        loadSched();
        popsPoll(loadSched, 30000);
    } else {
        $('tkTab').querySelector('[data-tab=sched]').hidden = true;
    }
})();
</script>

<?php include 'includes/footer.php'; ?>
