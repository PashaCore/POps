<?php include 'includes/header.php'; ?>

<style>
    /* İki dengeli sütun: solda etkinlik akışı (sağ sütunun boyunu alır, fazlası kendi içinde kayar), sağda kartlar */
    .home-grid { display: grid; grid-template-columns: minmax(0, 1.55fr) minmax(340px, 1fr); gap: 16px; align-items: start; margin-top: 18px; }
    .home-side { display: flex; flex-direction: column; gap: 16px; min-width: 0; }
    .home-side .card + .card, .home-grid .card + .card { margin-top: 0; }
    .home-card { padding: 12px 18px 6px; }
    .home-card .sect-row { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-height: 32px; margin-bottom: 2px; }
    .home-card h2 { font-size: 15px; font-weight: 600; letter-spacing: -0.01em; }
    .home-card .sect-row a, .home-card .sect-row .faint { font-size: var(--text-sm); }
    .home-card .sect-row .segmented > button { min-height: 26px; padding: 0.1875rem 0.625rem; font-size: var(--text-xs); }
    .kpi .pbar { margin-top: 12px; height: 5px; }

    .home-feed { display: flex; flex-direction: column; padding-bottom: 0; }
    .home-feed #homeActivity { flex: 1; min-height: 0; overflow-y: auto; margin: 0 -8px; padding: 0 8px; }
    .home-foot { display: flex; gap: 16px; justify-content: flex-end; align-items: center; min-height: 44px; font-size: var(--text-sm); border-top: 1px solid #f0f0f3; flex: none; }
    .home-card .act { grid-template-columns: 28px minmax(0, 1fr) auto; padding: 10px 2px; align-items: center; }
    .home-card .act .side { align-self: start; padding-top: 2px; }
    .home-card .act .why { grid-column: 2 / -1; margin-top: -4px; }
    .home-card .act .pbar { grid-column: 2 / -1; max-width: 320px; margin-top: -2px; }
    .home-card .act.clickable:hover { background: var(--bg-surface-2); }
    .act .res.neu { background: var(--bg-surface-3); color: var(--text-tertiary); }

    .rows > div { display: flex; align-items: center; justify-content: space-between; gap: 12px; min-height: 34px; padding: 4px 2px; border-bottom: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .rows > div:last-child { border-bottom: 0; }
    .rows > div > span:first-child { color: var(--text-tertiary); }
    .rows b { font-weight: 600; font-variant-numeric: tabular-nums; }
    .att { display: flex; align-items: center; gap: 12px; min-height: 46px; padding: 6px 2px; border-bottom: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .att:last-child { border-bottom: 0; }
    .att .grow { flex: 1; min-width: 0; }
    .att .t { font-weight: 500; }
    .att .d { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .att .btn { flex: none; }
    .lab-row { display: grid; grid-template-columns: minmax(0, 1fr) 120px 44px; gap: 12px; align-items: center; min-height: 34px; padding: 4px 2px; border-bottom: 1px solid #f0f0f3; color: inherit; font-size: var(--text-sm); }
    .lab-row:last-child { border-bottom: 0; }
    .lab-row:hover { color: inherit; background: var(--bg-surface-2); }
    .lab-row .n { text-align: right; color: var(--text-muted); font-size: var(--text-xs); font-variant-numeric: tabular-nums; }
    .vers .pbar { height: 8px; margin: 8px 0 10px; }
    .vers .leg { display: flex; flex-wrap: wrap; gap: 6px 14px; font-size: var(--text-xs); color: var(--text-tertiary); padding-bottom: 8px; }
    .vers .leg span { display: inline-flex; align-items: center; gap: 6px; }
    .vers .sw { width: 8px; height: 8px; border-radius: 2px; display: inline-block; }
    .home-card .set-note { padding: 0 2px 10px; }
    @media (max-width: 1100px) {
        .home-grid { grid-template-columns: minmax(0, 1fr); }
        .home-feed { height: auto !important; }
        .home-feed #homeActivity { overflow: visible; }
    }
</style>

<div class="page-header">
    <div>
        <h1>Kontrol merkezi</h1>
        <div class="summary" id="homeSummary"><span class="sum"><span class="spinner sm"></span> Bağlanıyor</span></div>
    </div>
</div>

<div class="kpis" id="homeKpis">
    <a class="kpi" href="devices?f=on" id="kOnline"><div class="l">Çevrimiçi</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
    <a class="kpi" href="devices?f=issue" id="kIssues"><div class="l">Sorunlu cihaz</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
    <a class="kpi" href="tasks" id="kJobs"><div class="l">Süren işlem</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
    <a class="kpi" href="devices" id="kAgents"><div class="l">Güncel ajan</div><div class="v">—</div><div class="f"><span>&nbsp;</span></div></a>
</div>

<div class="home-grid">
    <section class="card home-card home-feed" id="feedCard" aria-labelledby="actTitle">
        <div class="sect-row"><h2 id="actTitle">Son etkinlik</h2>
            <div class="segmented" id="actFilter" role="group" aria-label="Etkinliği süz">
                <button type="button" data-f="all" class="active" aria-pressed="true">Tümü</button>
                <button type="button" data-f="job" aria-pressed="false">İşlemler</button>
                <button type="button" data-f="log" aria-pressed="false">Olaylar</button>
            </div>
        </div>
        <div id="homeActivity"></div>
        <div class="home-foot"><a href="tasks">Bütün işlemler</a><a href="logger">Bütün kayıtlar</a></div>
    </section>
    <div class="home-side" id="homeSide">
        <section class="card home-card" aria-labelledby="attTitle">
            <div class="sect-row"><h2 id="attTitle">İlgilenmen gerekenler</h2></div>
            <div id="homeAttention"></div>
        </section>
        <section class="card home-card" aria-labelledby="todayTitle">
            <div class="sect-row"><h2 id="todayTitle">Bugün</h2><span class="faint" id="todayDate"></span></div>
            <div class="rows" id="homeToday"></div>
        </section>
        <section class="card home-card" aria-labelledby="labsTitle">
            <div class="sect-row"><h2 id="labsTitle">Sınıflar</h2><a href="labs">Sınıflara git</a></div>
            <div id="homeLabs"></div>
        </section>
        <section class="card home-card vers" aria-labelledby="verTitle">
            <div class="sect-row"><h2 id="verTitle">Ajan sürümleri</h2><a href="devices">Cihazlar</a></div>
            <div id="homeVersions"></div>
        </section>
        <section class="card home-card" aria-labelledby="srvTitle">
            <div class="sect-row"><h2 id="srvTitle">Sunucu</h2><a href="system" id="srvLink">Sistem</a></div>
            <div class="rows" id="homeServer"></div>
        </section>
    </div>
</div>

<script>
(function () {
    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const mem = { tasks: null, tasksKey: '', logs: null, logsKey: '', serverOk: null, lastOk: null, filter: 'all', health: null, version: null };
    const L = POps.logs;
    const IS_ADMIN = ['admin', 'superadmin'].includes(window.USER_ROLE);
    POps.setLoading($('homeActivity'), 'Etkinlik yükleniyor…');
    POps.setLoading($('homeAttention'));
    POps.setLoading($('homeLabs'));
    ['homeServer', 'homeVersions', 'homeToday'].forEach(id => POps.setLoading($(id)));

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
            $('kAgents').href = oldAgents.length && window.USER_ROLE === 'superadmin' ? 'system' : 'devices';
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
            renderActivity(jobs);
            renderToday(jobs);
        }
        if (loaded) renderVersions(devices, newest);
        renderServer();

        // İlgilenmen gerekenler
        if (loaded && mem.tasks) {
            const items = [];
            const failedJobs = jobs.filter(j => j.c.bad && !j.c.run).slice(0, 20);
            const failedToday = failedJobs.filter(j => (POps.toDate(j.first.created_at) || new Date(0)).toDateString() === new Date().toDateString());
            if (quarantined) items.push(['bad', `${quarantined} bilgisayar karantinada`, 'Kullanıcı ekranı kilitli; kaldırana kadar kullanılamaz.', 'devices?f=issue', 'Göster']);
            if (failedToday.length) items.push(['bad', `${failedToday.length} iş bugün sorunla bitti`, failedToday.slice(0, 2).map(j => dev.taskTitle(j.first)).join(', '), 'tasks', 'İncele']);
            if (oldAgents.length) items.push(['warn', `${oldAgents.length} bilgisayarda eski ajan`, `Güncel sürüm ${newest}. ` + oldAgents.slice(0, 3).map(d => POps.deviceName(d)).join(', ') + (oldAgents.length > 3 ? '…' : ''), window.USER_ROLE === 'superadmin' ? 'system' : 'devices?f=issue', 'Güncelle']);
            if (unassigned) items.push(['warn', `${unassigned} yeni bilgisayar sınıf bekliyor`, 'Atanmamış bilgisayarları bir sınıfa yerleştirin.', 'labs?lab=__atanmamis', 'Yerleştir']);
            const longOff = devices.filter(d => { const s = dev.state(d); const t = POps.toDate(s.since); return s.cls === 'off' && t && Date.now() - t > 7 * 864e5; });
            if (longOff.length) items.push(['neu', `${longOff.length} bilgisayar 7 günden uzun süredir kapalı`, longOff.slice(0, 3).map(d => POps.deviceName(d)).join(', '), 'devices?f=off', 'Göster']);
            $('homeAttention').innerHTML = items.length ? items.map(([k, t, d, href, label]) => `<div class="att"><span class="dot ${escapeHtml(k)}"></span><div class="grow"><div class="t">${escapeHtml(t)}</div><div class="d">${escapeHtml(d)}</div></div><button type="button" class="btn secondary sm" data-go="${escapeHtml(href)}">${escapeHtml(label)}</button></div>`).join('')
                : '<div class="empty-state compact">' + POps.iconHtml('check') + '<h3>Her şey yolunda</h3></div>';
        }

        // Sınıflar
        if (loaded) {
            const rowsHtml = labs.map(l => {
                const pcs = devices.filter(d => d.lab === l);
                const lOn = pcs.filter(d => dev.state(d).cls === 'on').length;
                const lIdle = pcs.filter(d => dev.state(d).cls === 'idle').length;
                return `<a class="lab-row" href="labs?lab=${encodeURIComponent(l)}"><span class="truncate">${escapeHtml(l)}</span><div class="pbar">${segHtml('ok', lOn, pcs.length)}${segHtml('warn', lIdle, pcs.length)}</div><span class="n">${Number(lOn + lIdle)}/${Number(pcs.length)}</span></a>`;
            }).join('');
            $('homeLabs').innerHTML = rowsHtml || '<div class="empty-state compact">' + POps.iconHtml('labs') + '<h3>Henüz sınıf yok</h3></div>';
        }
    }

    // ---- Son etkinlik: işler ve bilgisayarlardan gelen olaylar tek akışta (en yeni üstte)
    function jobRowHtml(j) {
        const t = j.first;
        const k = j.state;
        const where = j.c.total === 1 ? dev.name(t.target_pc) : ((t.target_lab && j.tasks.every(x => x.target_lab === t.target_lab) && t.target_lab !== dev.UNASSIGNED) ? t.target_lab + ' · ' : '') + j.c.total + ' bilgisayar';
        const badTask = j.tasks.find(x => POps.taskState(x.status) === 'bad');
        const why = badTask ? dev.failReason(badTask) : '';
        const badNames = j.tasks.filter(x => POps.taskState(x.status) === 'bad').map(x => dev.name(x.target_pc));
        const word = k === 'run' ? 'Sürüyor' : k === 'bad' ? (j.c.total === 1 ? dev.statusWord(t.status) : `${j.c.bad}/${j.c.total} başarısız`) : 'Tamamlandı';
        const metaHtml = escapeHtml(t.created_by || 'sistem') + ' · ' + POps.timeHtml(t.created_at) + (t.source ? ' · ' + escapeHtml(dev.sourceText(t.source)) : '') + ' · ' + escapeHtml(where);
        const whyHtml = why ? `<div class="why">${escapeHtml(why)}${j.c.total > 1 && badNames.length ? ' · ' + escapeHtml(badNames.slice(0, 4).join(', ')) + (badNames.length > 4 ? '…' : '') : ''}</div>` : '';
        return `<a class="act clickable" href="tasks?job=${encodeURIComponent(j.key)}" style="color:inherit">
            <div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(dev.taskTitle(t))}</div><div class="meta">${metaHtml}${t.reason ? ' · gerekçe: ' + escapeHtml(t.reason) : ''}</div></div>
            <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(word)}</span>${j.c.total > 1 ? `<span class="when">${Number(j.c.ok + j.c.bad)}/${Number(j.c.total)}</span>` : ''}</div>
            ${whyHtml}${j.c.total > 1 && k === 'run' ? `<div class="pbar">${segHtml('ok', j.c.ok, j.c.total)}${segHtml('bad', j.c.bad, j.c.total)}</div>` : ''}
        </a>`;
    }
    function logRowHtml(e) {
        const res = e.sev === 'bad' ? 'bad' : e.sev === 'warn' ? 'warn' : 'neu';
        const d = dev.find(e.pc);
        const lab = d && d.lab && d.lab !== dev.UNASSIGNED ? d.lab : '';
        const metaHtml = escapeHtml([e.pc ? dev.name(e.pc) : '', lab, e.who].filter(Boolean).join(' · ')) + ' · ' + POps.timeHtml(e.at);
        return `<a class="act clickable" href="logger${e.pc ? '?pc=' + encodeURIComponent(e.pc) : ''}" style="color:inherit">
            <div class="res ${escapeHtml(res)}">${POps.iconHtml(e.icon)}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(e.title)}</div><div class="meta">${metaHtml}</div></div>
            <div class="side"><span class="word ${e.sev === 'bad' ? 'bad' : e.sev === 'warn' ? 'warn' : ''}">${escapeHtml(L.SEV_WORD[e.sev] || 'Bilgi')}</span></div>
            ${e.why && e.sev !== 'info' ? `<div class="why${e.sev === 'warn' ? ' warn' : ''}">${escapeHtml(e.why)}</div>` : ''}
        </a>`;
    }
    function renderActivity(jobs) {
        const box = $('homeActivity');
        const items = [];
        if (mem.filter !== 'log') (jobs || []).forEach(j => items.push({ at: POps.toDate(j.first.created_at), html: jobRowHtml(j) }));
        // "Komut gönderildi" olayı iş satırıyla aynı şeyi anlatır: Tümü'nde yalnızca iş gösterilir
        if (mem.filter !== 'job') (mem.logs || []).map(L.norm).filter(e => mem.filter === 'log' || e.key !== 'execute_queue').forEach(e => items.push({ at: POps.toDate(e.at), html: logRowHtml(e) }));
        items.sort((a, b) => (b.at ? b.at.getTime() : 0) - (a.at ? a.at.getTime() : 0));
        if (!items.length) {
            POps.setEmpty(box, { icon: 'clock', title: mem.filter === 'log' ? 'Henüz olay yok' : 'Henüz etkinlik yok', text: 'Gönderilen komutlar, güç işlemleri, dağıtımlar ve bilgisayarlardan gelen olaylar burada görünür.', compact: true });
            return;
        }
        box.innerHTML = items.slice(0, 30).map(i => i.html).join('');
        syncFeed();
    }
    // Akış kartı sağ sütunun boyunu alır (en az 480 px); dar ekranda doğal boyunda kalır
    function syncFeed() {
        const card = $('feedCard'), side = $('homeSide');
        if (window.innerWidth <= 1100) { card.style.height = ''; return; }
        card.style.height = Math.max(side.offsetHeight, 480) + 'px';
    }
    if (window.ResizeObserver) new ResizeObserver(syncFeed).observe($('homeSide'));
    window.addEventListener('resize', syncFeed);

    // ---- Alt satır: sunucu, ajan sürümleri, bugün
    function renderServer() {
        const box = $('homeServer');
        const h = mem.health, v = mem.version;
        if (!h && mem.serverOk === null) return;
        const ok = mem.serverOk !== false && (!h || h.status === 'ok');
        const dbOk = !h || h.database;
        const rows = [
            ['Durum', `<span class="st"><span class="dot ${ok && dbOk ? 'on' : 'bad'}"></span> ${ok ? 'Çalışıyor' : 'Sorunlu'}${dbOk ? '' : ' · veritabanı yok'}</span>`],
            ['Sürüm', escapeHtml((v && v.running) || (h && h.version) || '—')]
        ];
        if (v) {
            rows.push(['Güncelleme', v.update_available && v.latest ? `<a href="system">${escapeHtml(v.latest)} kurulabilir</a>` : 'Güncel']);
            rows.push(['Cihaz anahtarı', `${Number(v.agents_enrolled)} / ${Number(v.agents_total)} bilgisayarda`]);
        }
        box.innerHTML = rows.map(([k, valHtml]) => `<div><span>${escapeHtml(k)}</span><span>${valHtml}</span></div>`).join('');
        $('srvLink').hidden = window.USER_ROLE !== 'superadmin';
    }
    const VERSION_COLORS = ['#34c759', '#0071e3', '#ff9f0a', '#af52de', '#ff3b30', '#8e8e93'];
    function renderVersions(devices, newest) {
        const box = $('homeVersions');
        const counts = {};
        devices.forEach(d => { const v = dev.version(d) || 'Bilinmiyor'; counts[v] = (counts[v] || 0) + 1; });
        const list = Object.entries(counts).sort((a, b) => dev.cmpVersion(b[0], a[0]) || b[1] - a[1]);
        if (!list.length) { POps.setEmpty(box, { icon: 'devices', title: 'Henüz cihaz yok', compact: true }); return; }
        const total = devices.length;
        const old = devices.filter(d => { const v = dev.version(d); return v && newest && dev.cmpVersion(v, newest) < 0; }).length;
        const color = (i) => escapeHtml(VERSION_COLORS[Math.min(i, VERSION_COLORS.length - 1)]);
        const barHtml = list.map(([v, n], i) => `<i style="width:${(n / total * 100).toFixed(2)}%;background:${color(i)}"></i>`).join('');
        const legHtml = list.map(([v, n], i) => `<span><span class="sw" style="background:${color(i)}"></span>${escapeHtml(v)} <b>${Number(n)}</b></span>`).join('');
        box.innerHTML = `<div class="rows"><div><span>En yeni</span><span>${escapeHtml(newest || '—')}</span></div>
            <div><span>Eski ajan</span><span>${old ? `<b>${Number(old)}</b> bilgisayar` : 'Yok'}</span></div></div>
            <div class="pbar">${barHtml}</div><div class="leg">${legHtml}</div>`;
    }
    function renderToday(jobs) {
        const box = $('homeToday');
        const today = new Date().toDateString();
        const isToday = (v) => { const d = POps.toDate(v); return d && d.toDateString() === today; };
        const tj = jobs.filter(j => isToday(j.first.created_at));
        const tasksToday = tj.reduce((a, j) => a + j.c.total, 0);
        const failed = tj.filter(j => j.c.bad).length;
        const evs = (mem.logs || []).map(L.norm).filter(e => isToday(e.at));
        const logins = evs.filter(e => e.key === 'login').length;
        const policy = evs.filter(e => e.kind === 'policy').length;
        const warn = evs.filter(e => e.sev !== 'info').length;
        $('todayDate').textContent = new Date().toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', weekday: 'long' });
        syncFeed();
        const rows = [
            ['Gönderilen iş', `<b>${Number(tj.length)}</b>${tasksToday > tj.length ? ` <span class="faint">(${Number(tasksToday)} görev)</span>` : ''}`],
            ['Sorunla biten', failed ? `<span class="word bad">${Number(failed)}</span>` : '<b>0</b>'],
            ['Oturum açma', `<b>${Number(logins)}</b>`],
            ['Kural ihlali', policy ? `<span class="word warn">${Number(policy)}</span>` : '<b>0</b>'],
            ['Uyarı ve kritik olay', warn ? `<span class="word warn">${Number(warn)}</span>` : '<b>0</b>']
        ];
        box.innerHTML = rows.map(([k, valHtml]) => `<div><span>${escapeHtml(k)}</span><span>${valHtml}</span></div>`).join('');
        box.title = mem.logs && mem.logs.length >= 60 ? 'Olay sayıları son 60 kayda göre' : '';
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
            const logs = await POps.get('/api/logs?limit=60');
            const key = JSON.stringify(logs);
            if (key !== mem.logsKey) { mem.logsKey = key; mem.logs = Array.isArray(logs) ? logs : []; render(); }
        } catch (e) {
            if (!mem.logs) { mem.logs = []; render(); }
        }
    }
    async function loadServer() {
        try { mem.health = await POps.get('/api/health'); } catch (e) { mem.health = { status: 'degraded', database: false }; }
        if (IS_ADMIN) { try { mem.version = await POps.get('/api/system/version'); } catch (e) { /* yetki ya da GitHub yok: satırlar gösterilmez */ } }
        renderServer();
    }

    $('homeAttention').addEventListener('click', (e) => { const b = e.target.closest('[data-go]'); if (b) location.href = b.dataset.go; });
    $('actFilter').addEventListener('click', (e) => {
        const b = e.target.closest('[data-f]');
        if (!b) return;
        mem.filter = b.dataset.f;
        $('actFilter').querySelectorAll('button').forEach(x => { const on = x === b; x.classList.toggle('active', on); x.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        render();
    });
    document.addEventListener('pops_data_updated', (e) => {
        if (e.detail && e.detail.error) mem.serverOk = false;
        render();
    });
    document.addEventListener('pops_jobs', () => { mem.tasksKey = ''; });
    POps.watchDevices({ inventory: false });
    loadTasks();
    loadLogs();
    loadServer();
    popsPoll(loadTasks, 6000);
    popsPoll(loadLogs, 15000);
    popsPoll(loadServer, 300000);
})();
</script>

<?php include 'includes/footer.php'; ?>
