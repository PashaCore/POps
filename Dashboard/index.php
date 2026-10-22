<?php include 'includes/header.php'; ?>
<?php
// Hızlı işlemler yetkiye göre: sayfa yetkisi sidebar.php'deki can_view() ile (izleyiciye kapalı sayfalar dahil)
$hmRole = $_SESSION['role'] ?? 'admin';
$hmAdmin = in_array($hmRole, ['admin', 'superadmin'], true);
$hmSuper = $hmRole === 'superadmin';
$hmQuick = [
    'terminal' => can_view('terminal'),
    'deploy' => can_view('deploy'),
    'vision' => can_view('vision'),
    'exam' => $hmAdmin && can_view('labs'),
    'bypass' => $hmAdmin && (can_view('labs') || can_view('devices')),
    'token' => $hmSuper,
    'updates' => $hmSuper,
];
?>

<style>
    .home-quick { display: flex; flex-wrap: wrap; gap: 8px; margin: -6px 0 22px; }
    .home-quick .btn { gap: 7px; }
    /* Solda etkinlik akışı, sağda ilgilenilecekler; dar ekranda önce ilgilenilecekler */
    .home-grid { display: grid; grid-template-columns: minmax(0, 1.6fr) minmax(320px, 1fr); gap: 16px; align-items: start; }
    .home-grid .card + .card { margin-top: 0; }
    .home-att { grid-column: 2; grid-row: 1; }
    .home-feed { grid-column: 1; grid-row: 1; }
    .home-card { padding: 12px 18px 6px; min-width: 0; }
    .home-card .sect-row { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px 12px; min-height: 32px; margin-bottom: 2px; }
    .home-card h2 { font-size: 15px; font-weight: 600; letter-spacing: -0.01em; }
    .home-card .sect-row .segmented > button { min-height: 26px; padding: 0.1875rem 0.625rem; font-size: var(--text-xs); }

    .home-feed { padding-bottom: 0; }
    .home-foot { display: flex; gap: 16px; justify-content: flex-end; align-items: center; min-height: 44px; font-size: var(--text-sm); border-top: 1px solid #f0f0f3; }
    .home-card .act { grid-template-columns: 28px minmax(0, 1fr) auto; padding: 10px 2px; align-items: center; }
    .home-card .act .side { align-self: start; padding-top: 2px; }
    .home-card .act .why { grid-column: 2 / -1; margin-top: -4px; }
    .home-card .act .pbar { grid-column: 2 / -1; max-width: 320px; margin-top: -2px; }
    .home-card .act.clickable:hover { background: var(--bg-surface-2); }
    .home-card .act a.what { display: block; color: var(--text-primary); }
    .home-card .act .meta a, .home-card .act .why a { color: inherit; text-decoration: underline; text-decoration-color: rgba(0, 0, 0, 0.15); text-underline-offset: 2px; }
    .home-card .act .meta a:hover, .home-card .act .why a:hover { color: var(--primary-500); }
    .act .res.neu { background: var(--bg-surface-3); color: var(--text-tertiary); }

    .att { display: flex; align-items: flex-start; gap: 12px; padding: 12px 2px; border-bottom: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .att:last-child { border-bottom: 0; }
    .att > .dot { margin-top: 6px; }
    .att .grow { flex: 1; min-width: 0; }
    .att .t { font-weight: 500; }
    .att .d { font-size: var(--text-xs); color: var(--text-muted); margin-top: 2px; line-height: 1.5; overflow-wrap: anywhere; }
    .att .d a { color: var(--text-secondary); text-decoration: underline; text-decoration-color: rgba(0, 0, 0, 0.18); text-underline-offset: 2px; }
    .att .d a:hover { color: var(--primary-500); }
    .att .acts { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .att .acts .btn { gap: 6px; }
    @media (min-width: 1101px) { .home-att { position: sticky; top: 20px; } }
    @media (max-width: 1100px) {
        .home-grid { grid-template-columns: minmax(0, 1fr); }
        .home-att, .home-feed { grid-column: auto; grid-row: auto; }
    }
    @media (max-width: 640px) {
        .home-quick { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); }
        .home-quick .btn { justify-content: flex-start; min-width: 0; }
        .home-quick .btn span { white-space: normal; text-align: left; line-height: 1.2; }
        .home-card { padding: 10px 14px 4px; }
    }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Kontrol merkezi'); ?></h1>
        <div class="summary" id="homeSummary"><span class="sum"><span class="spinner sm"></span> <?php _e('Bağlanıyor'); ?></span></div>
    </div>
</div>

<?php if (in_array(true, $hmQuick, true)): ?>
<nav class="home-quick" aria-label="<?php _e('Hızlı işlemler'); ?>">
    <?php if ($hmQuick['terminal']): ?><a class="btn secondary" href="terminal"><?php echo pops_icon('terminal', 'sm'); ?><span><?php _e('Komut gönder'); ?></span></a><?php endif; ?>
    <?php if ($hmQuick['deploy']): ?><a class="btn secondary" href="deploy"><?php echo pops_icon('package', 'sm'); ?><span><?php _e('Paket dağıt'); ?></span></a><?php endif; ?>
    <?php if ($hmQuick['vision']): ?><a class="btn secondary" href="vision"><?php echo pops_icon('eye', 'sm'); ?><span><?php _e('Ekranları izle'); ?></span></a><?php endif; ?>
    <?php if ($hmQuick['exam']): ?><button type="button" class="btn secondary" id="qaExam" aria-haspopup="menu"><?php echo pops_icon('clock', 'sm'); ?><span><?php _e('Sınav başlat'); ?></span></button><?php endif; ?>
    <?php if ($hmQuick['bypass']): ?><button type="button" class="btn secondary" id="qaBypass"><?php echo pops_icon('key', 'sm'); ?><span><?php _e('Çevrimdışı açma kodu'); ?></span></button><?php endif; ?>
    <?php if ($hmQuick['token']): ?><a class="btn secondary" href="system?view=ayarlar&amp;do=token"><?php echo pops_icon('plus', 'sm'); ?><span><?php _e('Kayıt jetonu üret'); ?></span></a><?php endif; ?>
    <?php if ($hmQuick['updates']): ?><a class="btn secondary" href="system"><?php echo pops_icon('arrow-up', 'sm'); ?><span><?php _e('Güncellemeler'); ?></span></a><?php endif; ?>
</nav>
<?php endif; ?>

<div class="home-grid">
    <section class="card home-card home-att" aria-labelledby="attTitle">
        <div class="sect-row"><h2 id="attTitle"><?php _e('İlgilenmen gerekenler'); ?></h2></div>
        <div id="homeAttention"></div>
    </section>
    <section class="card home-card home-feed" aria-labelledby="actTitle">
        <div class="sect-row"><h2 id="actTitle"><?php _e('Son etkinlik'); ?></h2>
            <div class="segmented" id="actFilter" role="group" aria-label="<?php _e('Etkinliği süz'); ?>">
                <button type="button" data-f="all" class="active" aria-pressed="true"><?php _e('Tümü'); ?></button>
                <button type="button" data-f="job" aria-pressed="false"><?php _e('İşlemler'); ?></button>
                <button type="button" data-f="log" aria-pressed="false"><?php _e('Olaylar'); ?></button>
            </div>
        </div>
        <div id="homeActivity"></div>
        <div class="home-foot"><a href="tasks"><?php _e('Bütün işlemler'); ?></a><a href="logger"><?php _e('Bütün kayıtlar'); ?></a></div>
    </section>
</div>

<script>
(function () {
    const dev = POps.dev;
    const L = POps.logs;
    const $ = (id) => document.getElementById(id);
    const IS_ADMIN = dev.canAdmin;
    const IS_SUPER = window.USER_ROLE === 'superadmin';
    const FEED_MAX = 15;
    const mem = { tasks: null, tasksKey: '', logs: null, logsKey: '', serverOk: null, filter: 'all', health: null, version: null, sumHtml: null, feedHtml: null, attSig: null };
    POps.setLoading($('homeActivity'), POps.t('Etkinlik yükleniyor…'));
    POps.setLoading($('homeAttention'));

    const segHtml = (cls, n, total) => n && total ? `<i class="${escapeHtml(cls)}" style="width:${(n / total * 100).toFixed(2)}%"></i>` : '';
    const pcLinkHtml = (h) => `<a href="pc?id=${encodeURIComponent(h)}">${escapeHtml(dev.name(h))}</a>`;
    const isToday = (v) => { const d = POps.toDate(v); return !!d && d.toDateString() === new Date().toDateString(); };

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

    // ---- Özet satırı: her sayı ilgili listeye gider
    function renderSummary(devices, jobs) {
        const boldHtml = (n) => `<b>${Number(n)}</b>`;
        let html = mem.serverOk === false ? `<span class="sum"><span class="dot bad"></span>${POps.tHtml('Sunucuya ulaşılamıyor')}</span>` : '';
        if (state.devicesLoaded) {
            const newest = dev.newestVersion();
            const on = devices.filter(d => dev.state(d).cls !== 'off').length;
            const issues = devices.filter(d => dev.issues(d, newest).some(i => i.kind !== 'lock' || i.icon === 'lock')).length;
            html += `<a class="sum" href="labs">${POps.tnHtml('{n} bilgisayar', devices.length, null, { n: boldHtml(devices.length) })}</a>`
                + `<a class="sum" href="devices?f=on"><span class="dot on"></span>${POps.tHtml('{n} açık', null, { n: boldHtml(on) })}</a>`
                + (issues ? `<a class="sum" href="devices?f=issue"><span class="dot warn"></span>${POps.tHtml('{n} sorunlu', null, { n: boldHtml(issues) })}</a>` : '');
        } else if (mem.serverOk !== false) {
            html = `<span class="sum"><span class="spinner sm"></span> ${POps.tHtml('Bağlanıyor')}</span>`;
        }
        const running = jobs.filter(j => j.state === 'run').length;
        if (running && state.devicesLoaded) html += `<a class="sum" href="tasks?f=run"><span class="dot run"></span>${POps.tnHtml('{n} süren iş', running, null, { n: boldHtml(running) })}</a>`;
        if (html !== mem.sumHtml) { mem.sumHtml = html; $('homeSummary').innerHTML = html; }
    }

    // ---- İlgilenmen gerekenler: her madde ne olduğu, hangi bilgisayarlar ve yapılacak işlem
    function attentionItems(devices, jobs) {
        const items = [];
        const newest = dev.newestVersion();
        const hostsOf = (list) => list.map(d => d.hostname);
        const h = mem.health;
        if (h && mem.serverOk !== false && (h.status !== 'ok' || h.database === false)) {
            items.push({ k: 'bad', title: POps.t('Sunucu sorunlu'), text: h.database === false ? POps.t('Veritabanına ulaşılamıyor.') : '',
                acts: IS_SUPER ? [{ label: POps.t('Sağlık ve yedek'), icon: 'server', href: 'system?tab=health' }] : [] });
        }
        const quarantined = devices.filter(d => d.is_quarantined);
        if (quarantined.length) {
            const hosts = hostsOf(quarantined);
            const offline = quarantined.filter(d => POps.isOffline(d)).map(d => d.hostname);
            items.push({ k: 'bad', title: POps.tn('{n} bilgisayar karantinada', hosts.length), hosts, more: 'devices?f=issue',
                acts: IS_ADMIN ? [
                    { label: POps.t('Karantinayı kaldır'), icon: 'unlock', run: (b) => dev.unquarantine(hosts, { btn: b }) },
                    offline.length ? { label: POps.t('Çevrimdışı açma kodu'), icon: 'key', run: () => (offline.length === 1 ? dev.bypass(offline[0]) : pickBypass((d) => d.is_quarantined)) } : null
                ] : [] });
        }
        const failedToday = jobs.filter(j => j.c.bad && !j.c.run).slice(0, 20).filter(j => isToday(j.first.created_at));
        if (failedToday.length) {
            items.push({ k: 'bad', title: POps.tn('{n} iş bugün sorunla bitti', failedToday.length),
                links: failedToday.slice(0, 3).map(j => ({ href: 'tasks?job=' + encodeURIComponent(j.key), text: dev.taskTitle(j.first) })),
                acts: [{ label: POps.t('İncele'), icon: 'jobs', href: failedToday.length === 1 ? 'tasks?job=' + encodeURIComponent(failedToday[0].key) : 'tasks?f=bad' }] });
        }
        const v = mem.version;
        if (IS_SUPER && v && v.update_available && v.latest) {
            items.push({ k: 'run', title: POps.t('Sunucu güncellemesi var: {version}', { version: v.latest }), text: v.running ? POps.t('Kurulu sürüm {version}.', { version: v.running }) : '',
                acts: [{ label: POps.t('Güncellemelere git'), icon: 'arrow-up', href: 'system' }] });
        }
        const oldAgents = devices.filter(d => { const ver = dev.version(d); return ver && newest && dev.cmpVersion(ver, newest) < 0; });
        if (oldAgents.length) {
            items.push({ k: 'warn', title: POps.tn('{n} bilgisayarda eski ajan', oldAgents.length), text: POps.t('Güncel sürüm {version}.', { version: newest }), hosts: hostsOf(oldAgents), more: 'devices?f=issue',
                acts: [IS_SUPER ? { label: POps.t('Güncelle'), icon: 'arrow-up', href: 'system' } : { label: POps.t('Göster'), icon: 'list', href: 'devices?f=issue' }] });
        }
        const unassigned = devices.filter(d => !d.lab || d.lab === dev.UNASSIGNED);
        if (unassigned.length) {
            items.push({ k: 'warn', title: POps.tn('{n} yeni bilgisayar sınıf bekliyor', unassigned.length), hosts: hostsOf(unassigned), more: 'labs?lab=__atanmamis',
                acts: [{ label: POps.t('Yerleştir'), icon: 'move', href: 'labs?lab=__atanmamis' }] });
        }
        const longOff = devices.filter(d => { const s = dev.state(d); const t = POps.toDate(s.since); return s.cls === 'off' && t && Date.now() - t > 7 * 864e5; });
        if (longOff.length) {
            const hosts = hostsOf(longOff);
            items.push({ k: 'off', title: POps.tn('{n} bilgisayar 7 günden uzun süredir kapalı', hosts.length), hosts, more: 'devices?f=off',
                acts: IS_ADMIN ? [{ label: POps.t('Uyandır'), icon: 'zap', run: (b) => dev.power('wake', hosts, { btn: b }) }] : [] });
        }
        return items;
    }
    function renderAttention(items) {
        const box = $('homeAttention');
        const sig = JSON.stringify(items.map(i => [i.k, i.title, i.text, (i.hosts || []).map(h => [h, dev.name(h)]), i.links, (i.acts || []).filter(Boolean).map(a => [a.label, a.href])]));
        if (sig === mem.attSig) return;
        mem.attSig = sig;
        if (!items.length) { POps.setEmpty(box, { icon: 'check', kind: 'success', title: POps.t('Her şey yolunda'), compact: true }); return; }
        box.replaceChildren(...items.map(i => {
            const desc = [];
            if (i.text) desc.push(document.createTextNode(i.text + ((i.hosts || i.links) ? ' ' : '')));
            const links = (i.links || []).concat((i.hosts || []).slice(0, 4).map(h => ({ href: 'pc?id=' + encodeURIComponent(h), text: dev.name(h) })));
            links.forEach((l, ix) => { if (ix) desc.push(document.createTextNode(', ')); desc.push(POps.el('a', { href: l.href, text: l.text })); });
            const rest = (i.hosts || []).length - 4;
            if (rest > 0) desc.push(document.createTextNode(' '), i.more ? POps.el('a', { href: i.more, text: POps.tn('+{n} daha', rest) }) : document.createTextNode(POps.tn('+{n} daha', rest)));
            const acts = (i.acts || []).filter(Boolean).map(a => {
                const kids = [POps.iconEl(a.icon, 'sm'), POps.el('span', { text: a.label })];
                if (a.href) return POps.el('a', { className: 'btn secondary sm', href: a.href }, kids);
                const b = POps.el('button', { type: 'button', className: 'btn secondary sm' }, kids);
                b.addEventListener('click', () => a.run(b));
                return b;
            });
            return POps.el('div', { className: 'att' }, [
                POps.el('span', { className: 'dot ' + i.k }),
                POps.el('div', { className: 'grow' }, [
                    POps.el('div', { className: 't', text: i.title }),
                    desc.length ? POps.el('div', { className: 'd' }, desc) : null,
                    acts.length ? POps.el('div', { className: 'acts' }, acts) : null
                ])
            ]);
        }));
    }
    function pickBypass(filter) {
        dev.pickDevice(POps.t('Çevrimdışı açma kodu'), (h) => dev.bypass(h), { sub: POps.t('Sunucuya bağlanamayan karantinadaki bilgisayar için; kodu kullanıcı tepsi simgesinden girer.'), filter });
    }

    function render() {
        const devices = state.devices || [];
        const jobs = mem.tasks ? groupJobs(mem.tasks) : [];
        renderSummary(devices, jobs);
        if (mem.tasks) renderActivity(jobs);
        if (state.devicesLoaded && mem.tasks) renderAttention(attentionItems(devices, jobs));
    }

    // ---- Son etkinlik: işler ve bilgisayarlardan gelen olaylar tek akışta (en yeni üstte). Satır işi ya da
    // kaydı açar; içindeki bilgisayar adı bilgisayarın sayfasına gider.
    function jobRowHtml(j) {
        const t = j.first;
        const k = j.state;
        const labPrefix = (t.target_lab && j.tasks.every(x => x.target_lab === t.target_lab) && t.target_lab !== dev.UNASSIGNED) ? t.target_lab + ' · ' : '';
        const whereHtml = j.c.total === 1 ? pcLinkHtml(t.target_pc) : escapeHtml(labPrefix + POps.tn('{n} bilgisayar', j.c.total));
        const badTasks = j.tasks.filter(x => POps.taskState(x.status) === 'bad');
        const why = badTasks.length ? dev.failReason(badTasks[0]) || (j.c.total > 1 ? dev.statusWord(badTasks[0].status) : '') : '';
        const word = k === 'run' ? POps.t('Sürüyor') : k === 'bad' ? (j.c.total === 1 ? dev.statusWord(t.status) : POps.t('{bad}/{total} başarısız', { bad: j.c.bad, total: j.c.total })) : POps.t('Tamamlandı');
        const metaHtml = escapeHtml(POps.tPattern(t.created_by) || POps.t('sistem')) + ' · ' + POps.timeHtml(t.created_at) + (t.source ? ' · ' + escapeHtml(dev.sourceText(t.source)) : '') + ' · ' + whereHtml
            + (t.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: t.reason }) : '');
        // Nedenin yanında yalnızca aynı nedenle düşen bilgisayarlar
        const sameWhy = badTasks.filter(x => (dev.failReason(x) || dev.statusWord(x.status)) === why);
        const badNamesHtml = j.c.total > 1 ? sameWhy.slice(0, 4).map(x => pcLinkHtml(x.target_pc)).join(', ') + (sameWhy.length > 4 ? '…' : '') : '';
        const whyHtml = why ? `<div class="why">${escapeHtml(why)}${badNamesHtml ? ' · ' + badNamesHtml : ''}</div>` : '';
        return `<div class="act clickable">
            <div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div>
            <div style="min-width:0"><a class="what" href="tasks?job=${encodeURIComponent(j.key)}">${escapeHtml(dev.taskTitle(t))}</a><div class="meta">${metaHtml}</div></div>
            <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(word)}</span>${j.c.total > 1 && k === 'run' ? `<span class="when">${Number(j.c.ok + j.c.bad)}/${Number(j.c.total)}</span>` : ''}</div>
            ${whyHtml}${j.c.total > 1 && k === 'run' ? `<div class="pbar">${segHtml('ok', j.c.ok, j.c.total)}${segHtml('bad', j.c.bad, j.c.total)}</div>` : ''}
        </div>`;
    }
    function logRowHtml(e) {
        const res = e.sev === 'bad' ? 'bad' : e.sev === 'warn' ? 'warn' : 'neu';
        const d = dev.find(e.pc);
        const lab = d && d.lab && d.lab !== dev.UNASSIGNED ? d.lab : '';
        const who = [lab, e.who].filter(Boolean).join(' · ');
        const metaHtml = (e.pc ? pcLinkHtml(e.pc) + ' · ' : '') + (who ? escapeHtml(who) + ' · ' : '') + POps.timeHtml(e.at);
        return `<div class="act clickable">
            <div class="res ${escapeHtml(res)}">${POps.iconHtml(e.icon)}</div>
            <div style="min-width:0"><a class="what" href="logger${e.pc ? '?pc=' + encodeURIComponent(e.pc) : ''}">${escapeHtml(e.title)}</a><div class="meta">${metaHtml}</div></div>
            <div class="side"><span class="word ${e.sev === 'bad' ? 'bad' : e.sev === 'warn' ? 'warn' : ''}">${escapeHtml(L.SEV_WORD[e.sev] || POps.t('Bilgi'))}</span></div>
            ${e.why && e.sev !== 'info' ? `<div class="why${e.sev === 'warn' ? ' warn' : ''}">${escapeHtml(e.why)}</div>` : ''}
        </div>`;
    }
    function renderActivity(jobs) {
        const box = $('homeActivity');
        const items = [];
        if (mem.filter !== 'log') jobs.forEach(j => items.push({ at: POps.toDate(j.first.created_at), html: jobRowHtml(j) }));
        // "Komut gönderildi" olayı iş satırıyla aynı şeyi anlatır: Tümü'nde yalnızca iş gösterilir
        if (mem.filter !== 'job') (mem.logs || []).map(L.norm).filter(e => mem.filter === 'log' || e.key !== 'execute_queue').forEach(e => items.push({ at: POps.toDate(e.at), html: logRowHtml(e) }));
        items.sort((a, b) => (b.at ? b.at.getTime() : 0) - (a.at ? a.at.getTime() : 0));
        if (!items.length) {
            mem.feedHtml = null;
            POps.setEmpty(box, { icon: 'clock', title: mem.filter === 'log' ? POps.t('Henüz olay yok') : POps.t('Henüz etkinlik yok'), text: POps.t('Gönderilen komutlar, güç işlemleri, dağıtımlar ve bilgisayarlardan gelen olaylar burada görünür.'), compact: true });
            return;
        }
        const html = items.slice(0, FEED_MAX).map(i => i.html).join('');
        if (html !== mem.feedHtml) { mem.feedHtml = html; box.innerHTML = html; }
    }
    // Satırın boş yerine tıklamak başlıktaki bağlantıyı açar
    $('homeActivity').addEventListener('click', (e) => {
        if (e.target.closest('a, button') || String(window.getSelection() || '')) return;
        const a = e.target.closest('.act') && e.target.closest('.act').querySelector('a.what');
        if (a) location.href = a.href;
    });

    // ---- Hızlı işlemler
    const qaExam = $('qaExam');
    if (qaExam) qaExam.addEventListener('click', () => {
        const labs = dev.labs();
        POps.menu(qaExam, labs.length
            ? [{ header: POps.t('Sınav hangi sınıfta?') }, ...labs.map(l => ({ label: l, icon: 'labs', onClick: () => { location.href = 'labs?lab=' + encodeURIComponent(l) + '&do=exam'; } }))]
            : [{ label: POps.t('Henüz sınıf yok'), icon: 'labs', disabled: true }]);
    });
    const qaBypass = $('qaBypass');
    if (qaBypass) qaBypass.addEventListener('click', () => pickBypass(null));

    // ---- Yükleme
    async function loadTasks() {
        try {
            const tasks = await POps.get('/api/tasks?limit=300');
            mem.serverOk = true;
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
        if (IS_SUPER) { try { mem.version = await POps.get('/api/system/version'); } catch (e) { /* GitHub ya da yetki yok: madde gösterilmez */ } }
        render();
    }

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
