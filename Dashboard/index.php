<?php include 'includes/header.php'; ?>

<style>
    .home-grid { display: grid; grid-template-columns: minmax(0, 1.7fr) minmax(320px, 1fr); gap: 18px; align-items: start; margin-top: 18px; }
    .home-side { display: flex; flex-direction: column; gap: 18px; }
    .home-side .card + .card { margin-top: 0; }
    .home-card { padding: 16px 18px 8px; }
    .home-card .sect-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 4px; }
    .home-card h2 { font-size: 15px; font-weight: 600; letter-spacing: -0.01em; }
    .home-card .sect-row a { font-size: var(--text-sm); }
    .kpi .pbar { margin-top: 12px; height: 5px; }
    .att { display: flex; align-items: center; gap: 12px; padding: 11px 2px; border-bottom: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .att:last-child { border-bottom: 0; }
    .att .grow { flex: 1; min-width: 0; }
    .att .t { font-weight: 500; }
    .att .d { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .att a.btn { flex: none; }
    .lab-row { display: grid; grid-template-columns: minmax(0, 1fr) 110px 44px; gap: 12px; align-items: center; padding: 9px 2px; border-bottom: 1px solid #f0f0f3; color: inherit; font-size: var(--text-sm); }
    .lab-row:last-child { border-bottom: 0; }
    .lab-row:hover { color: inherit; background: var(--bg-surface-2); }
    .lab-row .n { text-align: right; color: var(--text-muted); font-size: var(--text-xs); font-variant-numeric: tabular-nums; }
    .sig { display: grid; grid-template-columns: 8px minmax(0, 1fr) auto; gap: 10px; align-items: start; padding: 10px 2px; border-bottom: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .sig:last-child { border-bottom: 0; }
    .sig .dot { margin-top: 6px; }
    .sig .m { overflow-wrap: anywhere; }
    .sig .w { font-size: var(--text-xs); color: var(--text-muted); white-space: nowrap; }
    .sig .s { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .home-card .act { padding: 12px 2px; }
    .home-card .act.clickable:hover { background: var(--bg-surface-2); }
    @media (max-width: 1100px) { .home-grid { grid-template-columns: minmax(0, 1fr); } }
</style>

<div class="page-header">
    <div>
        <h1>Kontrol merkezi</h1>
        <div class="summary" id="homeSummary"><span class="sum"><span class="spinner sm"></span> Bağlanıyor</span></div>
    </div>
</div>

<div class="kpis" id="homeKpis">
    <a class="kpi" href="devices.php?f=on" id="kOnline"><div class="l">Çevrimiçi</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
    <a class="kpi" href="devices.php?f=issue" id="kIssues"><div class="l">Sorunlu cihaz</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
    <a class="kpi" href="tasks.php" id="kJobs"><div class="l">Süren işlem</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
    <a class="kpi" href="devices.php" id="kAgents"><div class="l">Güncel ajan</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
</div>

<div class="home-grid">
    <section class="card home-card" aria-labelledby="actTitle">
        <div class="sect-row"><h2 id="actTitle">Son etkinlik</h2><a href="tasks.php">Bütün işlemler</a></div>
        <div id="homeActivity"></div>
    </section>
    <div class="home-side">
        <section class="card home-card" aria-labelledby="attTitle">
            <div class="sect-row"><h2 id="attTitle">İlgilenmen gerekenler</h2></div>
            <div id="homeAttention"></div>
        </section>
        <section class="card home-card" aria-labelledby="labsTitle">
            <div class="sect-row"><h2 id="labsTitle">Sınıflar</h2><a href="labs.php">Sınıflara git</a></div>
            <div id="homeLabs"></div>
        </section>
        <section class="card home-card" aria-labelledby="sigTitle">
            <div class="sect-row"><h2 id="sigTitle">Son sinyaller</h2><a href="logger.php">Kayıtlar</a></div>
            <div id="homeSignals"></div>
        </section>
    </div>
</div>

<script>
(function () {
    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const mem = { tasks: null, tasksKey: '', logs: null, logsKey: '', serverOk: null, lastOk: null };
    POps.setLoading($('homeActivity'), 'Etkinlik yükleniyor…');
    POps.setLoading($('homeAttention'));
    POps.setLoading($('homeLabs'));
    POps.setLoading($('homeSignals'));

    function kpi(id, valueHtml, footHtml, barHtml) {
        const el = $(id);
        el.querySelector('.v').innerHTML = valueHtml;
        el.querySelector('.f').innerHTML = footHtml;
        const old = el.querySelector('.pbar');
        if (old) old.remove();
        if (barHtml) el.insertAdjacentHTML('beforeend', barHtml);
    }
    const pctOf = (a, b) => b ? Math.round(a / b * 100) : 0;
    const segHtml = (cls, n, total) => n && total ? `<i class="${escapeHtml(cls)}" style="width:${(n / total * 100).toFixed(2)}%"></i>` : '';

    // ---- İşler (görevler istek bazında gruplanır; bkz. İşlemler sayfası)
    function groupJobs(tasks) {
        const map = new Map();
        tasks.forEach(t => {
            const key = t.batch_id || ['legacy', t.created_at, t.created_by, t.script_path].join('|');
            let j = map.get(key);
            if (!j) { j = { key, tasks: [], first: t, minId: t.id }; map.set(key, j); }
            j.tasks.push(t);
            if (t.id < j.minId) j.minId = t.id;
        });
        return [...map.values()].map(j => {
            const c = { ok: 0, bad: 0, run: 0, total: j.tasks.length };
            j.tasks.forEach(t => { c[POps.taskState(t.status)] += 1; });
            j.c = c;
            j.state = c.run ? 'run' : c.bad ? 'bad' : 'ok';
            return j;
        }).sort((a, b) => b.minId - a.minId);
    }

    function render() {
        const devices = state.devices || [];
        const loaded = state.devicesLoaded;
        const newest = dev.newestVersion();
        const on = devices.filter(d => dev.state(d).cls === 'on').length;
        const idle = devices.filter(d => dev.state(d).cls === 'idle').length;
        const total = devices.length;
        const issueDevs = devices.filter(d => dev.issues(d, newest).some(i => i.kind !== 'lock' || i.icon === 'lock'));
        const quarantined = devices.filter(d => d.is_quarantined).length;
        const oldAgents = devices.filter(d => { const v = dev.version(d); return v && newest && dev.cmpVersion(v, newest) < 0; });
        const unassigned = devices.filter(d => !d.lab || d.lab === dev.UNASSIGNED).length;
        const labs = dev.labs();

        // Özet satırı
        const serverHtml = mem.serverOk === false
            ? '<span class="sum"><span class="dot bad"></span>Sunucuya ulaşılamıyor</span>'
            : '<span class="sum"><span class="dot on"></span>Sunucu bağlı</span>';
        $('homeSummary').innerHTML = serverHtml
            + (loaded ? `<span class="sum"><b>${Number(total)}</b> cihaz</span><span class="sum"><b>${Number(labs.length)}</b> sınıf</span>` : '')
            + (mem.lastOk ? `<span class="sum faint">güncellendi ${escapeHtml(new Date(mem.lastOk).toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' }))}</span>` : '');

        if (loaded) {
            kpi('kOnline', `${Number(on + idle)}<small> / ${Number(total)}</small>`,
                `<span>%${pctOf(on + idle, total)} açık${idle ? ' · ' + Number(idle) + ' boşta' : ''}</span><span class="go">Cihazlar</span>`,
                `<div class="pbar">${segHtml('ok', on, total)}${segHtml('warn', idle, total)}</div>`);
            kpi('kIssues', `${Number(issueDevs.length)}`,
                issueDevs.length ? `<span>${quarantined ? Number(quarantined) + ' karantina · ' : ''}${Number(oldAgents.length)} eski ajan</span><span class="go">Göster</span>` : '<span>Sorun yok</span>', '');
            const agentOk = total - oldAgents.length;
            kpi('kAgents', `%${pctOf(agentOk, total)}`,
                `<span>${newest ? 'sürüm ' + escapeHtml(newest) : '—'}${oldAgents.length ? ' · ' + Number(oldAgents.length) + ' eski' : ''}</span><span class="go">${oldAgents.length ? 'Güncelle' : 'Cihazlar'}</span>`,
                `<div class="pbar">${segHtml('ok', agentOk, total)}${segHtml('run', oldAgents.length, total)}</div>`);
            $('kAgents').href = oldAgents.length && window.USER_ROLE === 'superadmin' ? 'system.php' : 'devices.php';
        }

        // İşler
        const jobs = mem.tasks ? groupJobs(mem.tasks) : [];
        if (mem.tasks) {
            const running = jobs.filter(j => j.state === 'run');
            const today = new Date().toDateString();
            const badToday = jobs.filter(j => j.c.bad && (POps.toDate(j.first.created_at) || new Date(0)).toDateString() === today);
            const runTasks = running.reduce((a, j) => a + j.c.total, 0);
            const runDone = running.reduce((a, j) => a + j.c.ok + j.c.bad, 0);
            kpi('kJobs', `${Number(running.length)}`,
                `<span>${running.length ? Number(runDone) + '/' + Number(runTasks) + ' görev bitti' : 'Şu an iş yok'}${badToday.length ? ' · ' + Number(badToday.length) + ' sorunlu' : ''}</span><span class="go">İşlemler</span>`,
                running.length ? `<div class="pbar">${segHtml('ok', runDone, runTasks)}${segHtml('run', runTasks - runDone, runTasks)}</div>` : '');
            renderActivity(jobs.slice(0, 8));
        }

        // İlgilenmen gerekenler
        if (loaded && mem.tasks) {
            const items = [];
            const failedJobs = jobs.filter(j => j.c.bad && !j.c.run).slice(0, 20);
            const failedToday = failedJobs.filter(j => (POps.toDate(j.first.created_at) || new Date(0)).toDateString() === new Date().toDateString());
            if (quarantined) items.push(['bad', `${quarantined} bilgisayar karantinada`, 'Kullanıcı ekranı kilitli; kaldırana kadar kullanılamaz.', 'devices.php?f=issue', 'Göster']);
            if (failedToday.length) items.push(['bad', `${failedToday.length} iş bugün sorunla bitti`, failedToday.slice(0, 2).map(j => dev.taskTitle(j.first)).join(', '), 'tasks.php', 'İncele']);
            if (oldAgents.length) items.push(['warn', `${oldAgents.length} bilgisayarda eski ajan`, `Güncel sürüm ${newest}. ` + oldAgents.slice(0, 3).map(d => POps.deviceName(d)).join(', ') + (oldAgents.length > 3 ? '…' : ''), window.USER_ROLE === 'superadmin' ? 'system.php' : 'devices.php?f=issue', 'Güncelle']);
            if (unassigned) items.push(['warn', `${unassigned} yeni bilgisayar sınıf bekliyor`, 'Atanmamış bilgisayarları bir sınıfa yerleştirin.', 'labs.php?lab=__atanmamis', 'Yerleştir']);
            const longOff = devices.filter(d => { const s = dev.state(d); const t = POps.toDate(s.since); return s.cls === 'off' && t && Date.now() - t > 7 * 864e5; });
            if (longOff.length) items.push(['neu', `${longOff.length} bilgisayar 7 günden uzun süredir kapalı`, longOff.slice(0, 3).map(d => POps.deviceName(d)).join(', '), 'devices.php?f=off', 'Göster']);
            $('homeAttention').innerHTML = items.length ? items.map(([k, t, d, href, label]) => `<div class="att"><span class="dot ${escapeHtml(k)}"></span><div class="grow"><div class="t">${escapeHtml(t)}</div><div class="d">${escapeHtml(d)}</div></div><button type="button" class="btn secondary sm" data-go="${escapeHtml(href)}">${escapeHtml(label)}</button></div>`).join('')
                : '<div class="empty-state compact"><i class="fas fa-check"></i><h3>Her şey yolunda</h3></div>';
        }

        // Sınıflar
        if (loaded) {
            const rowsHtml = labs.map(l => {
                const pcs = devices.filter(d => d.lab === l);
                const lOn = pcs.filter(d => dev.state(d).cls === 'on').length;
                const lIdle = pcs.filter(d => dev.state(d).cls === 'idle').length;
                return `<a class="lab-row" href="labs.php?lab=${encodeURIComponent(l)}"><span class="truncate">${escapeHtml(l)}</span><div class="pbar">${segHtml('ok', lOn, pcs.length)}${segHtml('warn', lIdle, pcs.length)}</div><span class="n">${Number(lOn + lIdle)}/${Number(pcs.length)}</span></a>`;
            }).join('');
            $('homeLabs').innerHTML = rowsHtml || '<div class="empty-state compact"><i class="fas fa-table-cells-large"></i><h3>Henüz sınıf yok</h3></div>';
        }
    }

    function renderActivity(jobs) {
        const box = $('homeActivity');
        if (!jobs.length) { POps.setEmpty(box, { icon: 'fa-clock', title: 'Henüz işlem yok', text: 'Gönderilen komutlar, güç işlemleri ve dağıtımlar burada görünür.', compact: true }); return; }
        box.innerHTML = jobs.map(j => {
            const t = j.first;
            const k = j.state;
            const where = j.c.total === 1 ? dev.name(t.target_pc) : ((t.target_lab && j.tasks.every(x => x.target_lab === t.target_lab) && t.target_lab !== dev.UNASSIGNED) ? t.target_lab + ' · ' : '') + j.c.total + ' bilgisayar';
            const badTask = j.tasks.find(x => POps.taskState(x.status) === 'bad');
            const why = badTask ? dev.failReason(badTask) : '';
            const badNames = j.tasks.filter(x => POps.taskState(x.status) === 'bad').map(x => dev.name(x.target_pc));
            const word = k === 'run' ? 'Sürüyor' : k === 'bad' ? (j.c.total === 1 ? dev.statusWord(t.status) : `${j.c.bad}/${j.c.total} başarısız`) : 'Tamamlandı';
            const metaHtml = escapeHtml(t.created_by || 'sistem') + ' · ' + POps.timeHtml(t.created_at) + (t.source ? ' · ' + escapeHtml(dev.sourceText(t.source)) : '') + ' · ' + escapeHtml(where);
            const whyHtml = why ? `<div class="why">${escapeHtml(why)}${j.c.total > 1 && badNames.length ? ' · ' + escapeHtml(badNames.slice(0, 4).join(', ')) + (badNames.length > 4 ? '…' : '') : ''}</div>` : '';
            return `<a class="act clickable" href="tasks.php?job=${encodeURIComponent(j.key)}" style="color:inherit">
                <div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div>
                <div style="min-width:0"><div class="what">${escapeHtml(dev.taskTitle(t))}</div><div class="meta">${metaHtml}${t.reason ? ' · gerekçe: ' + escapeHtml(t.reason) : ''}</div>${whyHtml}
                    ${j.c.total > 1 && k === 'run' ? `<div class="pbar" style="margin-top:8px;max-width:260px">${segHtml('ok', j.c.ok, j.c.total)}${segHtml('bad', j.c.bad, j.c.total)}</div>` : ''}</div>
                <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(word)}</span>${j.c.total > 1 ? `<span class="when">${Number(j.c.ok + j.c.bad)}/${Number(j.c.total)}</span>` : ''}</div>
            </a>`;
        }).join('');
    }

    // ---- Sinyaller: seviye dürüst gösterilir (oturum açma/kapama "Bilgi"dir, uyarı değil)
    const LEVEL = { critical: ['bad', 'Kritik'], high: ['bad', 'Yüksek'], medium: ['warn', 'Uyarı'], warning: ['warn', 'Uyarı'], low: ['off', 'Bilgi'], info: ['off', 'Bilgi'] };
    const INFO_EVENTS = /logon|logoff|login|logout|oturum|session|startup|shutdown|heartbeat|connect/i;
    const CATEGORY = { security: 'Güvenlik', system_maintenance: 'Bakım', restricted_content: 'Kural ihlali', user_activity: 'Kullanıcı', network: 'Ağ', deployment: 'Dağıtım', hardware: 'Donanım', auth: 'Oturum' };
    function levelOf(l) {
        const r = String(l.risk_level || 'info').toLowerCase();
        if ((r === 'medium' || r === 'low') && (INFO_EVENTS.test(l.event_type || '') || INFO_EVENTS.test(l.action || '') || l.category === 'auth' || l.category === 'user_activity')) return LEVEL.info;
        return LEVEL[r] || LEVEL.info;
    }
    function renderSignals() {
        const box = $('homeSignals');
        const logs = (mem.logs || []).slice(0, 6);
        if (!logs.length) { POps.setEmpty(box, { icon: 'fa-satellite-dish', title: 'Sinyal yok', compact: true }); return; }
        box.innerHTML = logs.map(l => {
            const [cls, word] = levelOf(l);
            const cat = CATEGORY[l.category] || '';
            return `<div class="sig"><span class="dot ${escapeHtml(cls)}" title="${escapeHtml(word)}"></span>
                <div style="min-width:0"><div class="m">${escapeHtml(l.message || l.action || l.event_type || '')}</div><div class="s">${escapeHtml(dev.name(l.pc_name))}${cat ? ' · ' + escapeHtml(cat) : ''}${l.actor_id ? ' · ' + escapeHtml(l.actor_id) : ''}</div></div>
                <div style="text-align:right"><div class="w">${POps.timeHtml(l.timestamp)}</div><div class="w">${escapeHtml(word)}</div></div></div>`;
        }).join('');
    }

    async function loadTasks() {
        try {
            const tasks = await POps.get('/api/tasks?limit=300');
            mem.serverOk = true; mem.lastOk = Date.now();
            const key = JSON.stringify(tasks);
            if (key !== mem.tasksKey) { mem.tasksKey = key; mem.tasks = Array.isArray(tasks) ? tasks : []; }
        } catch (e) {
            mem.serverOk = false;
            if (!mem.tasks) POps.setError($('homeActivity'), e);
        }
        render();
    }
    async function loadLogs() {
        try {
            const logs = await POps.get('/api/logs?limit=30');
            const key = JSON.stringify(logs);
            if (key !== mem.logsKey) { mem.logsKey = key; mem.logs = Array.isArray(logs) ? logs : []; renderSignals(); }
        } catch (e) {
            if (!mem.logs) POps.setError($('homeSignals'), e, { compact: true });
        }
    }

    $('homeAttention').addEventListener('click', (e) => { const b = e.target.closest('[data-go]'); if (b) location.href = b.dataset.go; });
    document.addEventListener('pops_data_updated', (e) => {
        if (e.detail && e.detail.error) mem.serverOk = false;
        render();
    });
    document.addEventListener('pops_jobs', () => { mem.tasksKey = ''; });
    POps.watchDevices({ inventory: false });
    loadTasks();
    loadLogs();
    popsPoll(loadTasks, 6000);
    popsPoll(loadLogs, 15000);
})();
</script>

<?php include 'includes/footer.php'; ?>
