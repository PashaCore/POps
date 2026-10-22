<?php include 'includes/header.php'; ?>

<style>
    /* Bilgisayar sayfası: bir bilgisayarın her şeyi tek sayfada (Bilgisayarlar → sınıf → bilgisayar) */
    .crumbs { display: flex; align-items: center; gap: 6px; font-size: var(--text-sm); color: var(--text-tertiary); margin-bottom: 10px; min-width: 0; }
    .crumbs a { color: var(--text-tertiary); }
    .crumbs a:hover { color: var(--primary-500); }
    .crumbs .sep { color: var(--text-muted); }
    .pc-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; flex-wrap: wrap; margin-bottom: 20px; }
    .pc-title { display: flex; align-items: center; gap: 14px; min-width: 0; }
    .pc-ico { width: 48px; height: 48px; border-radius: 14px; display: inline-flex; align-items: center; justify-content: center; background: var(--bg-surface); box-shadow: 0 0 0 1px var(--border-subtle); color: var(--text-tertiary); flex: none; }
    .pc-ico.on { color: #1a8f3c; }
    .pc-ico.idle { color: #b25e00; }
    .pc-title h1 { font-size: 26px; line-height: 1.15; overflow-wrap: anywhere; }
    .pc-sub { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 14px; margin-top: 4px; font-size: var(--text-sm); color: var(--text-secondary); }
    .pc-sub .st { display: inline-flex; align-items: center; gap: 6px; font-weight: var(--fw-medium); color: var(--text-primary); }
    .pc-acts { display: flex; flex-wrap: wrap; gap: 8px; }
    .pc-acts .btn { gap: 7px; }

    .pc-issues { margin-bottom: 20px; background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle); }
    .pc-issue { display: grid; grid-template-columns: 28px minmax(0, 1fr); gap: 2px 12px; padding: 12px 16px; border-top: 1px solid var(--border-subtle); }
    .pc-issue:first-child { border-top: 0; }
    .pc-issue .ii { width: 28px; height: 28px; border-radius: 8px; display: inline-flex; align-items: center; justify-content: center; font-weight: 700; grid-row: span 3; }
    .pc-issue.err .ii { background: #fff1f0; color: #c4001a; }
    .pc-issue.lock .ii { background: #fff4e5; color: #b25e00; }
    .pc-issue.old .ii { background: #f0f6ff; color: #0058cc; }
    .pc-issue.cap .ii { background: var(--bg-app); color: var(--text-secondary); }
    .pc-issue .it { font-weight: var(--fw-semibold); }
    .pc-issue .id { font-size: var(--text-sm); color: var(--text-secondary); line-height: 1.5; }
    .pc-issue .id code { font-family: var(--font-mono); font-size: 12.5px; background: var(--bg-app); padding: 1px 6px; border-radius: 6px; overflow-wrap: anywhere; }
    .pc-issue .ia { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }

    .pc-tabs { margin-bottom: 18px; }
    .pc-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 16px; align-items: start; }
    .pc-card { background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle); padding: 6px 0; min-width: 0; }
    .pc-card h2 { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-secondary); padding: 10px 16px 6px; }
    .kv { display: flex; justify-content: space-between; gap: 16px; padding: 9px 16px; border-top: 1px solid var(--border-subtle); font-size: var(--text-sm); }
    .kv:first-of-type { border-top: 0; }
    .kv > span:first-child { color: var(--text-tertiary); white-space: nowrap; }
    .kv > span:last-child { text-align: right; min-width: 0; overflow-wrap: anywhere; }
    .kv .mono, .mono { font-family: var(--font-mono); font-size: 12.5px; }
    .kv .no { color: #c4001a; }
    .kv .yes { color: #1a8f3c; }
    .pc-note { font-size: var(--text-xs); color: var(--text-muted); padding: 8px 16px 6px; line-height: 1.5; }

    .pane-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 12px; }
    .pane-bar .grow { flex: 1; }
    .pane-bar .faint { font-size: var(--text-sm); }
    .pc-list { background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle); padding: 2px 16px; }
    .pc-list .act:last-child { border-bottom: 0; }
    .pc-table { width: 100%; border-collapse: collapse; font-size: var(--text-sm); }
    .pc-table th { text-align: left; font-weight: var(--fw-medium); color: var(--text-tertiary); font-size: var(--text-xs); padding: 10px 16px; border-bottom: 1px solid var(--border-subtle); }
    .pc-table td { padding: 10px 16px; border-bottom: 1px solid var(--border-subtle); vertical-align: top; }
    .pc-table tr:last-child td { border-bottom: 0; }
    .pc-table .sub { font-size: var(--text-xs); color: var(--text-muted); }
    .pc-wrap { background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle); overflow-x: auto; }
    @media (max-width: 720px) { .pc-title h1 { font-size: 22px; } .pc-acts { width: 100%; } }
</style>

<nav class="crumbs" aria-label="<?php _e('Konum'); ?>">
    <a href="labs"><?php _e('Bilgisayarlar'); ?></a>
    <span class="sep" aria-hidden="true">/</span>
    <a id="pcLabLink" href="labs"></a>
</nav>

<div class="page-header pc-head">
    <div class="pc-title">
        <span class="pc-ico" id="pcIco"><?php echo pops_icon('monitor', 'lg'); ?></span>
        <div style="min-width:0">
            <h1 id="pcName"><?php _e('Bilgisayar'); ?></h1>
            <div class="pc-sub" id="pcSub"><span class="spinner sm"></span></div>
        </div>
    </div>
    <div class="pc-acts" id="pcActs"></div>
</div>

<section class="pc-issues" id="pcIssues" hidden aria-label="<?php _e('Dikkat edilmesi gerekenler'); ?>"></section>

<div class="tabs pc-tabs" id="pcTabs" aria-label="<?php _e('Bilgisayar bölümleri'); ?>">
    <button type="button" class="tab" data-tab="general"><?php _e('Genel'); ?></button>
    <button type="button" class="tab" data-tab="hardware"><?php _e('Donanım'); ?></button>
    <button type="button" class="tab" data-tab="software"><?php _e('Yazılım'); ?></button>
    <button type="button" class="tab" data-tab="patches"><?php _e('Windows güncellemeleri'); ?></button>
    <button type="button" class="tab" data-tab="history"><?php _e('Geçmiş'); ?></button>
    <button type="button" class="tab" data-tab="events"><?php _e('Olaylar'); ?></button>
    <button type="button" class="tab" data-tab="files"><?php _e('Dosyalar'); ?></button>
</div>

<div data-pane="general" id="paneGeneral"></div>
<div data-pane="hardware" id="paneHardware" hidden></div>
<div data-pane="software" id="paneSoftware" hidden></div>
<div data-pane="patches" id="panePatches" hidden></div>
<div data-pane="history" id="paneHistory" hidden></div>
<div data-pane="events" id="paneEvents" hidden></div>
<div data-pane="files" id="paneFiles" hidden></div>

<script>
(function () {
    const dev = POps.dev;
    const L = POps.logs;
    const $ = (id) => document.getElementById(id);
    const CAN_ADMIN = dev.canAdmin;
    const IS_SUPER = window.USER_ROLE === 'superadmin';
    const host = new URLSearchParams(location.search).get('id') || '';
    const UNASSIGNED = dev.UNASSIGNED;
    let headSig = '', generalSig = '';
    // MAC adresi cihaz listesinde yok, donanım envanterinden gelir (bkz. loadInvMac)
    let invMac = null;
    let swQuery = '', swItems = null;
    // Sekme ?tab=files ile açılınca loadFiles aşağıdaki pageTabs çağrısında hemen çalışır: sayaç ondan önce tanımlı olmalı
    let filesTimer = null;

    const kvHtml = (label, valueHtml) => `<div class="kv"><span>${escapeHtml(label)}</span><span>${valueHtml}</span></div>`;
    const unixDate = (v) => (Number(v) > 0 ? new Date(Number(v) * 1000) : null);
    const yesNoHtml = (on, yes, no) => (on ? `<span class="yes">${escapeHtml(yes)}</span>` : `<span class="no">${escapeHtml(no)}</span>`);

    if (!host) {
        $('pcName').textContent = POps.t('Bilgisayar seçilmedi');
        $('pcSub').textContent = '';
        $('pcTabs').hidden = true;
        POps.setEmpty($('paneGeneral'), { icon: 'devices', title: POps.t('Bilgisayar seçilmedi'), text: POps.t('Bilgisayarlar sayfasından bir bilgisayar açın.') });
        return;
    }

    const tabs = POps.pageTabs($('pcTabs'), { def: 'general', onChange: (name) => loadPane(name) });

    // ---------------------------------------------------------------- Üst kısım: ad, durum, işlemler
    function renderHead(d) {
        const st = dev.state(d);
        const sig = JSON.stringify([d.status, d.last_seen, d.display_name, d.lab, d.current_user, d.ip, dev.version(d), d.is_quarantined, d.cap_vision_enabled, d.cap_terminal_enabled, d.cap_files_enabled, d.agent_features, d.platform]);
        if (sig === headSig) return;
        headSig = sig;
        const name = POps.deviceName(d);
        $('pcName').textContent = name;
        document.title = name + ' · POps';
        const lab = d.lab && d.lab !== UNASSIGNED ? d.lab : '';
        const labLink = $('pcLabLink');
        labLink.textContent = lab || POps.t('Atanmamış bilgisayarlar');
        labLink.href = 'labs?lab=' + encodeURIComponent(lab || '__atanmamis');
        $('pcIco').className = 'pc-ico ' + st.cls;
        const since = st.cls === 'off' && st.since ? ' · ' + POps.timeHtml(st.since) : '';
        const user = dev.user(d);
        $('pcSub').innerHTML = `<span class="st"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}${since}</span>`
            + (user && st.cls !== 'off' ? `<span>${POps.tHtml('Kullanıcı: {user}', { user })}</span>` : '')
            + `<span>${escapeHtml(dev.platform(d))}</span>`
            + (dev.version(d) ? `<span>${POps.tHtml('Ajan {version}', { version: dev.version(d) })}</span>` : '')
            + (d.ip ? `<span class="mono">${escapeHtml(d.ip)}</span>` : '');
        renderActions(d);
    }

    function renderActions(d) {
        const box = $('pcActs');
        box.replaceChildren();
        const off = POps.isOffline(d);
        const btn = (label, icon, opts) => {
            const o = opts || {};
            const b = POps.el(o.href ? 'a' : 'button', Object.assign({ className: 'btn ' + (o.primary ? '' : 'secondary') }, o.href ? { href: o.href } : { type: 'button' }), [POps.iconEl(icon, 'sm'), POps.el('span', { text: label })]);
            if (o.disabled) { if (o.href) { b.removeAttribute('href'); b.setAttribute('aria-disabled', 'true'); b.classList.add('disabled'); } else b.disabled = true; }
            if (o.title) b.title = o.title;
            if (o.onClick) b.addEventListener('click', (e) => o.onClick(b, e));
            box.append(b);
            return b;
        };
        // İzleyici yalnızca bilgileri görür: işlem düğmesi yok (Uzak ekran da yönetici sayfasıdır)
        if (!CAN_ADMIN) return;
        const visionOff = dev.isLinux(d) || d.cap_vision_enabled === false;
        btn(POps.t('Ekranı aç'), 'eye', { href: dev.screenUrl([host]), disabled: off || visionOff, title: off ? POps.t('Bilgisayar kapalı.') : visionOff ? POps.t('Uzak ekran bu bilgisayarda kapalı.') : '' });
        btn(POps.t('Komut gönder'), 'terminal', { href: dev.commandUrl([host]), disabled: d.cap_terminal_enabled === false, title: d.cap_terminal_enabled === false ? POps.t('Uzak komut bu bilgisayarda kapalı.') : '' });
        btn(POps.t('Güç'), 'power', {
            onClick: (b) => POps.menu(b, [
                { label: POps.t('Uyandır'), icon: 'zap', disabled: !POps.isOffline(d), onClick: () => dev.power('wake', [host], { source: 'devices' }) },
                { label: POps.t('Yeniden başlat'), icon: 'restart', disabled: POps.isOffline(d), onClick: () => dev.power('restart', [host], { source: 'devices' }) },
                { label: POps.tx('Kapat', 'power'), icon: 'power', danger: true, disabled: POps.isOffline(d), onClick: () => dev.power('shutdown', [host], { source: 'devices' }) },
                '-',
                ...dev.sessionItems([host], { source: 'devices' })
            ])
        });
        const msg = dev.messageItem([host], { source: 'devices' });
        btn(msg.label, 'message', { disabled: msg.disabled, title: msg.title || (off ? POps.t('Bilgisayar kapalı.') : ''), onClick: () => msg.onClick() });
        btn(POps.tx('Diğer', 'menu'), 'more', {
            onClick: (b) => {
                const cur = dev.find(host) || d;
                const filesOk = dev.filesState(cur) === 'ok';
                const lab = cur.lab && cur.lab !== UNASSIGNED ? cur.lab : '';
                POps.menu(b, [
                    { label: POps.t('Dosya gönder'), icon: 'upload', disabled: !filesOk, title: dev.filesBlockText(cur) || null, onClick: () => dev.sendFile([host]) },
                    { label: POps.t('Dosya al'), icon: 'download', disabled: !filesOk, title: dev.filesBlockText(cur) || null, onClick: () => dev.pullFile(host) },
                    '-',
                    { label: POps.t('Yeniden adlandır'), icon: 'edit', onClick: () => dev.rename(host) },
                    { label: POps.t('Başka sınıfa taşı…'), icon: 'move', onClick: () => dev.moveMenu(b, [host], { currentLab: cur.lab }) },
                    lab ? { label: (state.mainPcs || {})[lab] === host ? POps.t('Öğretmen bilgisayarı olmaktan çıkar') : POps.t('Öğretmen bilgisayarı yap'), icon: 'crown', onClick: () => dev.setTeacher(lab, host) } : null,
                    '-',
                    cur.is_quarantined
                        ? { label: POps.t('Karantinayı kaldır'), icon: 'unlock', onClick: () => dev.unquarantine([host]) }
                        : { label: POps.t('Karantinaya al'), icon: 'lock', danger: true, onClick: () => dev.quarantine([host]) },
                    { label: POps.t('Çevrimdışı açma kodu (bypass)'), icon: 'key', onClick: () => dev.bypass(host) },
                    IS_SUPER ? '-' : null,
                    IS_SUPER ? { label: POps.t('Bilgisayarı sil'), icon: 'trash', danger: true, onClick: async () => { if (await dev.remove(host) !== false && !dev.find(host)) location.href = 'labs'; } } : null
                ].filter(Boolean));
            }
        });
    }

    // ---------------------------------------------------------------- Dikkat edilmesi gerekenler
    // Her sorun: ne olduğu, neden ve yapılacak işlem (yalnızca sayaç ya da işaret değil)
    function issues(d) {
        const out = [];
        const h = d.agent_health || {};
        const logsBtn = { label: POps.t('Olayları göster'), icon: 'list', tab: 'events' };
        if (d.is_quarantined) {
            out.push({
                kind: 'lock', glyph: POps.iconHtml('lock', 'sm'), title: POps.t('Karantinada'),
                text: POps.t('Kullanıcının ekranı kilitli; ağ yalnızca POps sunucusuna açık. Kaldırınca bilgisayar normale döner. Bilgisayar sunucuya bağlanamıyorsa çevrimdışı açma kodu üretip kullanıcıya söyleyin (tepsi simgesi → "Yönetici Müdahalesi (Bypass)").'),
                acts: CAN_ADMIN ? [{ label: POps.t('Karantinayı kaldır'), icon: 'unlock', run: () => dev.unquarantine([host]) }, { label: POps.t('Çevrimdışı açma kodu'), icon: 'key', run: () => dev.bypass(host) }] : []
            });
            if (h.isolation_error) {
                out.push({
                    kind: 'err', glyph: '!', title: POps.t('Ağ yalıtımı uygulanamadı'),
                    text: POps.t('Ekran kilitli ama güvenlik duvarı kuralları uygulanamadığı için bilgisayar ağa erişebiliyor.'),
                    code: h.isolation_error, acts: [logsBtn]
                });
            }
        }
        if (Number(h.loop_errors_1h) > 0) {
            out.push({
                kind: 'err', glyph: '!', title: POps.tn('Ajan son 1 saatte {n} hata bildirdi', Number(h.loop_errors_1h)),
                text: (h.last_error ? '' : POps.t('Ajan hata metni göndermedi (eski ajan).') + ' ') + POps.t('Bunlar ajanın arka plan işlerindeki hatalardır: sunucuya bağlanma, politika eşitleme, envanter gönderme. Ajan çalışmaya devam eder; bir saat yeni hata olmazsa sayaç sıfırlanır. Ayrıntı bilgisayardaki ajan günlüğündedir:') + ' ' + (dev.isLinux(d) ? '/var/log/pops-agent/agent.log' : 'C:\\POpsLogs'),
                code: h.last_error || '', acts: [logsBtn]
            });
        }
        const v = dev.version(d), top = dev.newestVersion();
        if (v && top && dev.cmpVersion(v, top) < 0) {
            out.push({
                kind: 'old', glyph: '↑', title: POps.t('Ajan eski: {version}', { version: v }),
                text: POps.t('Güncel sürüm {latest}. Eski ajanda sınav modu, dosya aktarımı, güç işlemleri ve mesaj gibi yeni özellikler çalışmayabilir.', { latest: top }),
                acts: IS_SUPER ? [{ label: POps.t('Güncellemelere git'), icon: 'arrow-up', href: 'system' }] : []
            });
        }
        return out;
    }
    let issueActs = [];
    function renderIssues(d) {
        const list = issues(d);
        const box = $('pcIssues');
        box.hidden = !list.length;
        issueActs = [];
        box.innerHTML = list.map(i => {
            const actsHtml = (i.acts || []).map(a => {
                issueActs.push(a);
                const ix = issueActs.length - 1;
                return `<button type="button" class="btn secondary sm" data-issue-act="${Number(ix)}">${POps.iconHtml(a.icon, 'sm')}${escapeHtml(a.label)}</button>`;
            }).join('');
            return `<div class="pc-issue ${escapeHtml(i.kind)}"><span class="ii" aria-hidden="true">${i.glyph === '!' ? '!' : i.glyph === '↑' ? '↑' : POps.iconHtml('lock', 'sm')}</span>`
                + `<div class="it">${escapeHtml(i.title)}</div><div class="id">${escapeHtml(i.text)}${i.code ? ` <code>${escapeHtml(i.code)}</code>` : ''}</div>`
                + (actsHtml ? `<div class="ia">${actsHtml}</div>` : '') + '</div>';
        }).join('');
    }
    $('pcIssues').addEventListener('click', (e) => {
        const b = e.target.closest('[data-issue-act]');
        if (!b) return;
        const a = issueActs[Number(b.dataset.issueAct)];
        if (!a) return;
        if (a.tab) tabs.set(a.tab, { scroll: false });
        else if (a.href) location.href = a.href;
        else if (a.run) a.run();
    });

    // ---------------------------------------------------------------- Genel
    const FEATURE_NAMES = { exam: 'Sınav modu', files: 'Dosya aktarımı', winget: 'winget kurulumu', power: 'Güç işlemleri', message: 'Mesaj', peer_cache: 'Sınıf içi paket paylaşımı' };
    function renderGeneral(d) {
        const h = d.agent_health || {};
        const sig = JSON.stringify([d, (state.mainPcs || {})[d.lab], invMac]);
        if (sig === generalSig) return;
        generalSig = sig;
        const off = POps.isOffline(d);
        const st = dev.state(d);
        const lab = d.lab && d.lab !== UNASSIGNED ? d.lab : '';
        const teacher = lab && (state.mainPcs || {})[lab] === host;
        const nowHtml = kvHtml(POps.t('Durum'), `<span class="dot ${escapeHtml(st.cls)}"></span> ${escapeHtml(st.word)}`)
            + (off ? kvHtml(POps.t('Son görülme'), POps.timeHtml(d.last_seen)) : '')
            + (off && d.last_disconnect_reason ? kvHtml(POps.t('Son kopuş'), escapeHtml(d.last_disconnect_reason)) : '')
            + (!off ? kvHtml(POps.t('Oturumdaki kullanıcı'), escapeHtml(dev.user(d) || POps.t('oturum yok'))) : '')
            + (!off && dev.app(d) ? kvHtml(POps.t('Öndeki uygulama'), escapeHtml(dev.app(d))) : '')
            + kvHtml(POps.t('Sınıf'), lab ? `<a href="labs?lab=${encodeURIComponent(lab)}">${escapeHtml(lab)}</a>` + (teacher ? ' · ' + POps.tHtml('öğretmen bilgisayarı') : '') : POps.tHtml('Atanmamış'));
        const netHtml = kvHtml(POps.t('IP adresi'), d.ip ? `<span class="mono">${escapeHtml(d.ip)}</span>` : '—')
            + kvHtml(POps.t('MAC adresi'), invMac ? `<span class="mono">${escapeHtml(invMac)}</span>` : '—')
            + kvHtml(POps.t('Sistem'), escapeHtml(dev.platform(d)))
            + kvHtml(POps.t('Donanım kimliği'), `<span class="mono">${escapeHtml(d.hostname)}</span>`);
        const tray = typeof h.tray_connected === 'boolean' ? h.tray_connected : null;
        const agentHtml = kvHtml(POps.t('Sürüm'), escapeHtml(dev.version(d) || POps.t('Bilinmiyor')))
            + (unixDate(h.started_at) && !off ? kvHtml(POps.t('Ajan açılışı'), POps.timeHtml(unixDate(h.started_at))) : '')
            + (unixDate(h.last_policy_sync) ? kvHtml(POps.t('Son politika eşitleme'), POps.timeHtml(unixDate(h.last_policy_sync))) : '')
            + (unixDate(h.last_inventory_upload) ? kvHtml(POps.t('Son envanter'), POps.timeHtml(unixDate(h.last_inventory_upload))) : '')
            + (tray !== null && !off ? kvHtml(POps.t('Tepsi uygulaması'), yesNoHtml(tray, POps.t('bağlı'), POps.t('bağlı değil'))) : '')
            + kvHtml(POps.t('Son 1 saatteki hata'), Number(h.loop_errors_1h) > 0 ? `<span class="no">${Number(h.loop_errors_1h)}</span>` : '0');
        const trayNote = tray === false && !off ? `<div class="pc-note">${POps.tHtml('Tepsi uygulaması kullanıcının oturumunda çalışır: oturum yoksa bağlı değildir. Mesaj, ekran izni ve kilitleme tepsiyle yapılır.')}</div>` : '';
        const capRow = (label, on) => kvHtml(label, on === false ? `<span class="no">${POps.tHtml('kapalı')}</span>` : `<span class="yes">${POps.tHtml('açık')}</span>`);
        const feats = Array.isArray(d.agent_features) ? d.agent_features.filter(f => FEATURE_NAMES[f]).map(f => POps.t(FEATURE_NAMES[f])) : [];
        const capsHtml = capRow(POps.t('Uzak komut'), d.cap_terminal_enabled)
            + capRow(POps.t('Uzak ekran'), dev.isLinux(d) ? false : d.cap_vision_enabled)
            + capRow(POps.t('Dosya aktarımı'), d.cap_files_enabled)
            + kvHtml(POps.t('Ajanın desteklediği'), feats.length ? escapeHtml(feats.join(', ')) : POps.tHtml('temel özellikler'));
        const capNote = [d.cap_terminal_enabled, d.cap_vision_enabled, d.cap_files_enabled].includes(false)
            ? `<div class="pc-note">${POps.tHtml('Kapalı özellik bilgisayarın kendisinde kapatılmıştır (kurulumda ya da yerel ayarla); panelden açılamaz. Böylece sunucu ele geçirilse bile o bilgisayarda çalışmaz.')}</div>` : '';
        $('paneGeneral').innerHTML = `<div class="pc-grid">`
            + `<section class="pc-card"><h2>${POps.tHtml('Şu an')}</h2>${nowHtml}</section>`
            + `<section class="pc-card"><h2>${POps.tHtml('Ağ ve kimlik')}</h2>${netHtml}</section>`
            + `<section class="pc-card"><h2>${POps.tHtml('Ajan')}</h2>${agentHtml}${trayNote}</section>`
            + `<section class="pc-card"><h2>${POps.tHtml('Özellikler')}</h2>${capsHtml}${capNote}</section>`
            + `<section class="pc-card" id="pcExam" hidden></section>`
            + `</div>`;
        renderExam(d);
    }
    async function renderExam(d) {
        const lab = d.lab && d.lab !== UNASSIGNED ? d.lab : '';
        if (!lab) return;
        try { await dev.exam.load(lab, 15000); } catch (e) { return; }
        const box = $('pcExam');
        const hit = dev.exam.byLab[lab];
        const data = hit && hit.data;
        if (!box || !data || !data.active || !data.exam) return;
        const ex = data.exam;
        const row = data.byPc[d.hostname];
        const es = dev.exam.state(row ? row.state : 'pending');
        box.hidden = false;
        box.innerHTML = `<h2>${POps.tHtml('Sınav modu')}</h2>`
            + kvHtml(POps.t('Bu bilgisayarda'), `<span class="badge ${escapeHtml(es.cls)}">${escapeHtml(es.word)}</span>`)
            + kvHtml(POps.t('Kalan süre'), escapeHtml(dev.exam.leftText(ex)))
            + kvHtml(POps.t('İzin verilen'), escapeHtml((ex.allow || []).length ? ex.allow.join(', ') : POps.t('yalnızca POps sunucusu')))
            + kvHtml(POps.t('Başlatan'), escapeHtml(ex.started_by || '?') + ' · ' + POps.timeHtml(ex.started_at));
    }

    // ---------------------------------------------------------------- Bölümler (açılınca yüklenir, her açılışta tazelenir)
    async function loadPane(name) {
        if (name === 'hardware') return loadHardware();
        if (name === 'software') return loadSoftware();
        if (name === 'patches') return loadPatches();
        if (name === 'history') return loadHistory();
        if (name === 'events') return loadEvents();
        if (name === 'files') return loadFiles();
        return null;
    }
    function paneLoading(el) { if (!el.childElementCount) POps.setLoading(el); }

    async function loadHardware() {
        const el = $('paneHardware');
        paneLoading(el);
        let rows;
        try { rows = await POps.get('/api/inventory'); } catch (e) { POps.setError(el, e); return; }
        const r = (rows || []).find(x => x.pc_name === host);
        if (!r) { POps.setEmpty(el, { icon: 'cpu', title: POps.t('Donanım bilgisi yok'), text: POps.t('Ajan donanım envanterini ilk bağlantıda gönderir.') }); return; }
        const val = (v) => (v ? escapeHtml(v) : '—');
        el.innerHTML = `<div class="pc-grid"><section class="pc-card"><h2>${POps.tHtml('Donanım')}</h2>`
            + kvHtml(POps.t('İşlemci'), val(r.cpu)) + kvHtml(POps.t('Bellek'), val(r.ram)) + kvHtml(POps.t('Anakart'), val(r.motherboard))
            + kvHtml(POps.t('Ekran kartı'), val(r.gpu)) + kvHtml(POps.t('Diskler'), val(r.disk_info)) + `</section>`
            + `<section class="pc-card"><h2>${POps.tHtml('Sistem')}</h2>`
            + kvHtml(POps.t('İşletim sistemi'), val(r.os_version)) + kvHtml(POps.t('IP adresi'), r.ip_address ? `<span class="mono">${escapeHtml(r.ip_address)}</span>` : '—')
            + kvHtml(POps.t('MAC adresi'), r.mac_address ? `<span class="mono">${escapeHtml(r.mac_address)}</span>` : '—')
            + kvHtml(POps.t('Envanter tarihi'), r.last_updated ? POps.timeHtml(r.last_updated) : '—') + `</section></div>`;
    }

    function renderSoftware() {
        const el = $('paneSoftware');
        if (!swItems) return;
        const q = swQuery.toLocaleLowerCase('tr');
        const rows = swItems.filter(s => !q || [s.name, s.publisher, s.version].some(v => v && String(v).toLocaleLowerCase('tr').includes(q)));
        const tbody = el.querySelector('tbody');
        el.querySelector('[data-sw-count]').textContent = POps.tn('{n} program', rows.length);
        if (!rows.length) { tbody.innerHTML = `<tr><td colspan="3" class="faint">${POps.tHtml('Eşleşen program yok.')}</td></tr>`; return; }
        tbody.innerHTML = rows.slice(0, 1000).map(s => `<tr><td>${escapeHtml(s.name)}<div class="sub">${escapeHtml(s.publisher || '')}</div></td>`
            + `<td class="mono">${escapeHtml(s.version || '—')}</td><td>${s.install_date ? escapeHtml(s.install_date) : '—'}</td></tr>`).join('');
    }
    async function loadSoftware() {
        const el = $('paneSoftware');
        if (!el.childElementCount) {
            el.innerHTML = `<div class="pane-bar"><div class="search-field">${POps.iconHtml('search', 'sm')}<input type="search" data-sw-q placeholder="${escapeHtml(POps.t('Program ya da yayıncı'))}" aria-label="${escapeHtml(POps.t('Programlarda ara'))}"></div><span class="grow"></span><span class="faint" data-sw-count></span></div>`
                + `<div class="pc-wrap"><table class="pc-table"><thead><tr><th>${POps.tHtml('Program')}</th><th>${POps.tHtml('Sürüm')}</th><th>${POps.tHtml('Kurulum tarihi')}</th></tr></thead><tbody></tbody></table></div>`;
            el.querySelector('[data-sw-q]').addEventListener('input', (e) => { swQuery = e.target.value.trim(); renderSoftware(); });
            POps.setLoading(el.querySelector('tbody'));
        }
        try { swItems = await POps.get('/api/devices/' + encodeURIComponent(host) + '/software') || []; } catch (e) { POps.setError(el.querySelector('tbody'), e, { tag: 'tr', colspan: 3 }); return; }
        if (!swItems.length) { POps.setEmpty(el, { icon: 'package', title: POps.t('Program listesi yok'), text: POps.t('Kurulu programlar 0.1.5 ve sonraki ajanlardan gelir.') }); return; }
        renderSoftware();
    }

    async function loadPatches() {
        const el = $('panePatches');
        paneLoading(el);
        let rows;
        try { rows = await POps.get('/api/patches'); } catch (e) { POps.setError(el, e); return; }
        const r = (rows || []).find(x => x.pc_name === host);
        const actsHtml = CAN_ADMIN ? `<button type="button" class="btn secondary" data-patch="scan">${POps.iconHtml('refresh', 'sm')}${POps.tHtml('Güncellemeleri tara')}</button>`
            + `<button type="button" class="btn secondary" data-patch="security">${POps.iconHtml('shield', 'sm')}${POps.tHtml('Güvenlik güncellemelerini kur')}</button>`
            + `<button type="button" class="btn secondary" data-patch="all">${POps.iconHtml('download', 'sm')}${POps.tHtml('Bütün güncellemeleri kur')}</button>` : '';
        if (!r || !r.reported) {
            el.innerHTML = `<div class="pane-bar"><span class="grow"></span>${actsHtml}</div>`;
            el.append(POps.el('div'));
            POps.setEmpty(el.lastChild, { icon: 'shield', title: POps.t('Henüz güncelleme bilgisi yok'), text: POps.t('"Güncellemeleri tara" ile bilgisayardan Windows Update durumunu isteyin.') });
            return;
        }
        const ups = r.updates || [];
        el.innerHTML = `<div class="pane-bar"><span class="faint">${POps.tHtml('Son tarama')}: ${r.last_search ? POps.timeHtml(r.last_search) : '—'}</span><span class="grow"></span>${actsHtml}</div>`
            + `<div class="pc-grid" style="margin-bottom:16px"><section class="pc-card"><h2>${POps.tHtml('Durum')}</h2>`
            + kvHtml(POps.t('Bekleyen güncelleme'), escapeHtml(String(r.pending_count ?? 0)))
            + kvHtml(POps.t('Güvenlik'), escapeHtml(String(r.pending_security ?? 0))) + kvHtml(POps.t('Kritik'), escapeHtml(String(r.pending_critical ?? 0)))
            + kvHtml(POps.t('Yeniden başlatma'), r.reboot_required ? `<span class="no">${POps.tHtml('gerekiyor')}</span>` : POps.tHtml('gerekmiyor'))
            + kvHtml(POps.t('Son kurulum'), r.last_install ? POps.timeHtml(r.last_install) : '—') + `</section></div>`
            + (ups.length ? `<div class="pc-wrap"><table class="pc-table"><thead><tr><th>${POps.tHtml('Güncelleme')}</th><th>${POps.tHtml('Tür')}</th></tr></thead><tbody>`
                + ups.map(u => `<tr><td>${escapeHtml(u.title || u.kb || '')}${u.kb && u.title ? `<div class="sub">${escapeHtml(u.kb)}</div>` : ''}</td><td>${escapeHtml(u.severity || u.classification || u.category || '—')}</td></tr>`).join('')
                + `</tbody></table></div>` : '');
    }
    $('panePatches').addEventListener('click', async (e) => {
        const b = e.target.closest('[data-patch]');
        if (!b || b.disabled) return;
        const kind = b.dataset.patch;
        const body = { target_mode: 'PC', targets: [host] };
        if (kind === 'scan') {
            await POps.act(b, () => POps.post('/api/patches/scan', body), { success: POps.t('Tarama isteği gönderildi; sonuç birkaç dakika içinde gelir.') });
        } else {
            const ok = await POps.confirm({ title: kind === 'all' ? POps.t('Bütün güncellemeler kurulsun mu?') : POps.t('Güvenlik güncellemeleri kurulsun mu?'), message: POps.t('Ajan bilgisayarı yeniden başlatmaz; gerekirse "yeniden başlatma gerekiyor" olarak bildirir.'), confirmText: POps.t('Kur') });
            if (!ok) return;
            await POps.act(b, () => POps.post('/api/patches/install', Object.assign(body, { scope: kind === 'all' ? 'all' : 'security' })), { success: POps.t('Kurulum isteği gönderildi.') });
        }
        loadPatches();
    });

    function activityRowHtml(a) {
        if (a.kind === 'vision') {
            const dur = a.ended_at && a.at ? POps.duration((POps.toDate(a.ended_at) - POps.toDate(a.at)) / 1000) : '';
            return `<div class="act"><div class="res">${POps.iconHtml('eye')}</div><div style="min-width:0"><div class="what">${a.mandatory ? POps.tHtml('Uzak ekran (zorunlu)') : POps.tHtml('Uzak ekran')}</div>`
                + `<div class="meta">${escapeHtml(a.by || '?')} · ${POps.timeHtml(a.at)}${dur ? ' · ' + escapeHtml(dur) : ''}${a.reason ? ' · ' + escapeHtml(a.reason) : ''}</div></div>`
                + `<div class="side"><span class="word">${a.ended_at ? POps.tHtml('Bitti') : POps.tHtml('Sürüyor')}</span></div></div>`;
        }
        const k = POps.taskState(a.status);
        const why = k === 'bad' ? dev.failReason(a) : '';
        return `<div class="act"><div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div><div style="min-width:0"><div class="what">${escapeHtml(dev.taskTitle(a))}</div>`
            + `<div class="meta">${escapeHtml(POps.tPattern(a.by || '?'))} · ${POps.timeHtml(a.at)}${a.source ? ' · ' + escapeHtml(dev.sourceText(a.source)) : ''}${a.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: a.reason }) : ''}</div>`
            + `${why ? `<div class="why">${escapeHtml(why)}</div>` : ''}</div><div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(dev.statusWord(a.status))}</span></div></div>`;
    }
    async function loadHistory() {
        const el = $('paneHistory');
        paneLoading(el);
        let r;
        try { r = await POps.get('/api/devices/' + encodeURIComponent(host) + '/activity?limit=50'); } catch (e) { POps.setError(el, e); return; }
        const items = (r && r.items) || [];
        if (!items.length) { POps.setEmpty(el, { icon: 'clock', title: POps.t('Bu bilgisayarda henüz işlem yok'), text: POps.t('Gönderilen komutlar, güç işlemleri, dağıtımlar ve uzak ekran oturumları burada görünür.') }); return; }
        el.innerHTML = `<div class="pane-bar"><span class="faint">${POps.tHtml('Son {n} işlem', { n: items.length })}</span><span class="grow"></span><a class="btn secondary" href="tasks">${POps.iconHtml('jobs', 'sm')}${POps.tHtml('Bütün işlemler')}</a></div>`
            + `<div class="pc-list">${items.map(activityRowHtml).join('')}</div>`;
    }

    function eventRowHtml(e) {
        const res = e.sev === 'bad' ? 'bad' : e.sev === 'warn' ? 'warn' : 'neu';
        return `<div class="act"><div class="res ${escapeHtml(res)}">${POps.iconHtml(e.icon)}</div><div style="min-width:0"><div class="what">${escapeHtml(e.title)}</div>`
            + `<div class="meta">${escapeHtml(e.who || '')}${e.who ? ' · ' : ''}${POps.timeHtml(e.at)}</div>`
            + `${e.why && e.sev !== 'info' ? `<div class="why${e.sev === 'warn' ? ' warn' : ''}">${escapeHtml(e.why)}</div>` : ''}</div>`
            + `<div class="side"><span class="word ${e.sev === 'bad' ? 'bad' : e.sev === 'warn' ? 'warn' : ''}">${escapeHtml(L.SEV_WORD[e.sev] || POps.t('Bilgi'))}</span></div></div>`;
    }
    async function loadEvents() {
        const el = $('paneEvents');
        paneLoading(el);
        let rows;
        try { rows = await POps.get('/api/logs?limit=200&pc=' + encodeURIComponent(host)); } catch (e) { POps.setError(el, e); return; }
        const items = (rows || []).map(L.norm);
        if (!items.length) { POps.setEmpty(el, { icon: 'list', title: POps.t('Bu bilgisayardan olay yok'), text: POps.t('Oturum açma, yasaklı site, karantina ve ajan olayları burada görünür.') }); return; }
        el.innerHTML = `<div class="pane-bar"><span class="faint">${POps.tHtml('Son {n} olay', { n: items.length })}</span><span class="grow"></span><a class="btn secondary" href="logger?pc=${encodeURIComponent(host)}">${POps.iconHtml('list', 'sm')}${POps.tHtml('Kayıtlar sayfasında aç')}</a></div>`
            + `<div class="pc-list">${items.map(eventRowHtml).join('')}</div>`;
    }

    const FILE_WORD = { sent: 'Bekliyor', downloading: 'İndiriliyor', uploading: 'Yükleniyor', rejected: 'Reddedildi', failed: 'Başarısız', expired: 'Süresi doldu' };
    const FILE_K = { done: 'ok', rejected: 'bad', failed: 'bad', expired: 'warn' };
    function fileRowHtml(f) {
        const k = FILE_K[f.status] || 'run';
        const push = f.direction === 'push';
        const word = f.status === 'done' ? (push ? POps.t('Teslim edildi') : POps.t('Alındı')) : (FILE_WORD[f.status] ? POps.t(FILE_WORD[f.status]) : f.status);
        const metaHtml = escapeHtml(push ? POps.t('Bilgisayara') : POps.t('Bilgisayardan')) + ' · ' + escapeHtml(f.created_by || '?') + ' · ' + POps.timeHtml(f.created_at)
            + (f.size ? ' · ' + escapeHtml(dev.sizeText(f.size)) : '') + (f.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: f.reason }) : '');
        const linkHtml = f.downloadable && CAN_ADMIN
            ? `<a class="btn secondary sm" href="/api/files/${encodeURIComponent(f.transfer_id)}/content" download>${POps.iconHtml('download', 'sm')}${POps.tHtml('İndir')}</a>` : '';
        return `<div class="act"><div class="res ${escapeHtml(k)}">${POps.iconHtml(push ? 'upload' : 'download')}</div><div style="min-width:0"><div class="what">${escapeHtml(f.name || f.path || '?')}</div>`
            + `<div class="meta">${metaHtml}</div>${f.path ? `<div class="meta mono">${escapeHtml(f.path)}</div>` : ''}${(k === 'bad' || k === 'warn') && f.detail ? `<div class="why">${escapeHtml(f.detail)}</div>` : ''}</div>`
            + `<div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(word)}</span>${linkHtml}</div></div>`;
    }
    async function loadFiles() {
        clearTimeout(filesTimer);
        const el = $('paneFiles');
        paneLoading(el);
        let items;
        try { items = (await POps.get('/api/files?limit=50&pc=' + encodeURIComponent(host))).items || []; } catch (e) { POps.setError(el, e); return; }
        const d = dev.find(host);
        const ok = d && dev.filesState(d) === 'ok';
        const actsHtml = CAN_ADMIN ? `<button type="button" class="btn secondary" data-file="push" ${ok ? '' : 'disabled'}>${POps.iconHtml('upload', 'sm')}${POps.tHtml('Dosya gönder')}</button>`
            + `<button type="button" class="btn secondary" data-file="pull" ${ok ? '' : 'disabled'}>${POps.iconHtml('download', 'sm')}${POps.tHtml('Dosya al')}</button>` : '';
        const blockHtml = d && !ok && CAN_ADMIN ? `<span class="faint">${escapeHtml(dev.filesBlockText(d))}</span>` : '';
        el.innerHTML = `<div class="pane-bar">${blockHtml}<span class="grow"></span>${actsHtml}</div>`
            + (items.length ? `<div class="pc-list">${items.map(fileRowHtml).join('')}</div>` : '<div data-files-empty></div>');
        if (!items.length) POps.setEmpty(el.querySelector('[data-files-empty]'), { icon: 'file', title: POps.t('Henüz dosya aktarımı yok'), text: POps.t('Bilgisayara gönderilen ve bilgisayardan alınan dosyalar burada görünür.') });
        if (items.some(f => ['sent', 'downloading', 'uploading'].includes(f.status))) filesTimer = setTimeout(() => { if (tabs.current() === 'files') loadFiles(); }, 3000);
    }
    $('paneFiles').addEventListener('click', async (e) => {
        const b = e.target.closest('[data-file]');
        if (!b || b.disabled) return;
        if (b.dataset.file === 'push') await dev.sendFile([host]); else await dev.pullFile(host);
        loadFiles();
    });

    // ---------------------------------------------------------------- Yükleme
    function render() {
        const d = dev.find(host);
        if (!state.devicesLoaded) return;
        if (!d) {
            $('pcName').textContent = host;
            $('pcSub').textContent = POps.t('Bu bilgisayar bulunamadı (silinmiş ya da yetki kapsamınız dışında).');
            $('pcActs').replaceChildren();
            $('pcTabs').hidden = true;
            document.querySelectorAll('[data-pane]').forEach(p => { p.hidden = true; });
            return;
        }
        renderHead(d);
        renderIssues(d);
        renderGeneral(d);
    }
    document.addEventListener('pops_data_updated', render);
    POps.watchDevices({ inventory: false });
    render();
    // Genel'deki MAC adresi: envanter bir kez okunur; ajan "-" bildirdiyse boş sayılır
    (async function loadInvMac() {
        try {
            const r = ((await POps.get('/api/inventory')) || []).find(x => x.pc_name === host);
            invMac = r && r.mac_address && r.mac_address !== '-' ? r.mac_address : null;
            render();
        } catch (e) { /* MAC yalnızca bilgi; envanter okunamazsa — kalır */ }
    })();
    if (tabs.current() !== 'general') loadPane(tabs.current());
})();
</script>

<?php include 'includes/footer.php'; ?>
