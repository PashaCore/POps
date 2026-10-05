// =================================================================
// POps panel — cihaz işlemleri ve cihaz ayrıntı paneli (Sınıflar, Cihazlar, arama ve diğer sayfalar)
//   POps.dev.state(d)                 : { cls: on|idle|off, word, since }
//   POps.dev.issues(d)                : cihazın sorunları (işaret + açıklama)
//   POps.dev.power(eylem, hostlar)    : wake | restart | shutdown (onay, kapalıları atlar, işlem merkezine düşer)
//   POps.dev.message / move / quarantine / unquarantine / rename / remove
//   POps.dev.sendFile(hostlar) / pullFile(host): dosya gönder / dosyayı bilgisayardan al (routers/files.py)
//   POps.dev.open(host)               : sağdaki ayrıntı paneli
//   POps.dev.exam                     : sınav modu (sınıfın durumu, kalan süre, bilgisayar durumları)
// Hedefler her zaman cihaz kimliğidir (hostname = HW- kimliği).
// Görünen metinler POps.t ile çevrilir (lang/en/common.json); sunucuya giden görev adları Türkçe kalır.
// =================================================================
(function () {
    const dev = POps.dev = {};
    const UNASSIGNED = 'Atanmamis_Cihazlar';
    const CAN_ADMIN = ['admin', 'superadmin'].includes(window.USER_ROLE);
    const IS_SUPER = window.USER_ROLE === 'superadmin';
    dev.canAdmin = CAN_ADMIN;
    dev.UNASSIGNED = UNASSIGNED;

    const byHost = (h) => (state.devices || []).find(d => d.hostname === h) || null;
    dev.find = byHost;
    dev.name = (h) => { const d = byHost(h); return d ? POps.deviceName(d) : h; };

    // "0.1.15-alpha" -> [0, 1, 15]
    dev.parseVersion = function (v) {
        const m = String(v || '').match(/(\d+)\.(\d+)\.(\d+)/);
        return m ? [Number(m[1]), Number(m[2]), Number(m[3])] : null;
    };
    dev.cmpVersion = function (a, b) {
        const x = dev.parseVersion(a), y = dev.parseVersion(b);
        if (!x || !y) return 0;
        for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
        return 0;
    };
    dev.version = (d) => d.running_version || (d.agent_version && d.agent_version !== 'Bilinmiyor' ? d.agent_version : '');
    // Ağdaki en yeni ajan sürümü: bundan eski olan cihaz "eski ajan" sayılır
    dev.newestVersion = function () {
        let best = '';
        (state.devices || []).forEach(d => { const v = dev.version(d); if (v && (!best || dev.cmpVersion(v, best) > 0)) best = v; });
        return best;
    };

    dev.state = function (d) {
        const s = String((d && d.status) || '').toLowerCase();
        if (s === 'online') return { cls: 'on', word: POps.t('Çevrimiçi'), since: '' };
        if (s === 'idle') return { cls: 'idle', word: POps.t('Boşta'), since: '' };
        return { cls: 'off', word: POps.t('Kapalı'), since: d && (d.last_disconnect_at || d.last_seen) };
    };
    dev.user = function (d) {
        const u = d && d.current_user;
        return u && u !== '-' && u !== 'None' ? String(u) : '';
    };
    dev.app = function (d) {
        const a = d && d.active_window;
        return a && a !== '-' ? String(a) : '';
    };

    // Tile/satır alt yazısı: kim oturuyor; yoksa durum
    dev.subline = function (d) {
        const st = dev.state(d);
        if (st.cls === 'off') return POps.t('kapalı');
        return dev.user(d) || (st.cls === 'idle' ? POps.t('boşta') : POps.t('oturum yok'));
    };

    // Sorunlar: { kind: err|upd|old|lock, glyph, text }
    dev.issues = function (d, newest) {
        const out = [];
        if (!d) return out;
        if (d.is_quarantined) out.push({ kind: 'lock', icon: 'lock', text: POps.t('Karantinada: kullanıcı ekranı kilitli') });
        const v = dev.version(d);
        const top = newest === undefined ? dev.newestVersion() : newest;
        if (v && top && dev.cmpVersion(v, top) < 0) out.push({ kind: 'old', glyph: '↑', text: POps.t('Ajan eski: {version} (güncel {latest})', { version: v, latest: top }) });
        const h = d.agent_health || {};
        if (Number(h.loop_errors_1h) > 0) out.push({ kind: 'err', glyph: '!', text: POps.tn('Ajan son 1 saatte {n} hata bildirdi', Number(h.loop_errors_1h)) });
        if (d.cap_terminal_enabled === false) out.push({ kind: 'lock', icon: 'terminal', text: POps.t('Uzak komut bu cihazda kapalı') });
        if (d.cap_vision_enabled === false) out.push({ kind: 'lock', icon: 'eye', text: POps.t('Uzak ekran bu cihazda kapalı') });
        if (d.cap_files_enabled === false) out.push({ kind: 'lock', icon: 'file', text: POps.t('Dosya aktarımı bu cihazda kapalı') });
        return out;
    };
    // Kutucukta gösterilecek tek işaret (en önemlisi)
    dev.mark = function (d, newest) {
        const all = dev.issues(d, newest).filter(i => i.kind !== 'lock' || i.icon === 'lock');
        return all[0] || null;
    };
    dev.markHtml = function (i) {
        if (!i) return '';
        const inner = i.icon ? POps.iconHtml(i.icon, 'sm') : escapeHtml(i.glyph || '!');
        return `<span class="mark ${escapeHtml(i.kind)}" data-tip="${escapeHtml(i.text)}" data-tip-pos="left" aria-label="${escapeHtml(i.text)}">${inner}</span>`;
    };

    const namesText = (hosts) => {
        const names = hosts.map(dev.name);
        const head = names.slice(0, 5).join(', ');
        return names.length > 5 ? POps.tn('{names} ve {n} bilgisayar daha', names.length - 5, { names: head }) : head;
    };
    const count = (n) => POps.tn('{n} bilgisayar', n);

    // ---- Güç
    // step: sunucuya giden görev adı (Türkçe veri); görünen metinler cümle cümle çevrilir
    const POWER = {
        restart: {
            step: 'Yeniden başlat', cmd: 'shutdown /r /f /t 5', icon: 'restart',
            label: () => POps.t('Yeniden başlat'),
            title1: (name) => POps.t('{name} yeniden başlatılsın mı?', { name }),
            titleN: (n) => POps.tn('{n} bilgisayar yeniden başlatılsın mı?', n),
            btnN: (n) => POps.tn('{n} bilgisayarı yeniden başlat', n)
        },
        shutdown: {
            step: 'Kapat', cmd: 'shutdown /s /f /t 5', icon: 'power',
            label: () => POps.tx('Kapat', 'power'),
            title1: (name) => POps.t('{name} kapatılsın mı?', { name }),
            titleN: (n) => POps.tn('{n} bilgisayar kapatılsın mı?', n),
            btnN: (n) => POps.tn('{n} bilgisayarı kapat', n)
        }
    };
    dev.power = async function (action, hosts, opts) {
        const o = opts || {};
        hosts = [...new Set(hosts || [])];
        if (!hosts.length) return POps.toast('warning', POps.t('Hedef bilgisayar yok.'));
        if (action === 'wake') return dev.wake(hosts, o);
        const p = POWER[action];
        const on = hosts.filter(h => { const d = byHost(h); return d && !POps.isOffline(d); });
        const skipped = hosts.length - on.length;
        if (!on.length) return POps.toast('warning', hosts.length === 1 ? POps.t('Bilgisayar kapalı; komut gönderilmedi.') : POps.t('Seçili bilgisayarların hiçbiri açık değil; komut gönderilmedi.'));
        const ok = await POps.confirm({
            title: on.length === 1 ? p.title1(dev.name(on[0])) : p.titleN(on.length),
            message: (on.length > 1 ? namesText(on) + '\n' : '') + POps.t('5 saniye içinde uygulanır; kaydedilmemiş işler kaybolabilir.'),
            note: skipped ? POps.tn('Kapalı {n} bilgisayar atlanacak.', skipped) : '',
            confirmText: on.length === 1 ? p.label() : p.btnN(on.length),
            danger: true,
            icon: p.icon
        });
        if (!ok) return;
        await POps.act(o.btn, () => POps.post('/api/deploy_orchestration', {
            target_mode: 'PC', targets: on, taskSequence: [{ name: p.step, type: 'CMD', command: p.cmd }],
            title: p.step + (o.scopeLabel ? ' · ' + o.scopeLabel : ''), source: o.source || null
        }, { jobTitle: o.scopeLabel ? POps.taskName(p.step + ' · ' + o.scopeLabel) : p.label() + ' · ' + count(on.length) }), { success: (r) => POps.tn('Komut {n} bilgisayara gönderildi.', (r && r.created) || on.length) });
    };
    dev.wake = async function (hosts, o) {
        const off = hosts.filter(h => { const d = byHost(h); return !d || POps.isOffline(d); });
        if (!off.length) return POps.toast('info', hosts.length === 1 ? POps.t('Bilgisayar zaten açık.') : POps.t('Seçili bilgisayarların hepsi zaten açık.'));
        if (o.lab && off.length === hosts.length && o.wholeLab) {
            return POps.act(o.btn, () => POps.post('/api/wake_lab/' + encodeURIComponent(o.lab)), { success: (r) => POps.tn('{lab}: {n} bilgisayara uyandırma sinyali gönderildi.', (r && r.woken_pcs) || 0, { lab: o.lab }) });
        }
        await POps.busy(o.btn, async () => {
            const res = await Promise.allSettled(off.map(h => POps.post('/api/wake_pc/' + encodeURIComponent(h))));
            const okN = res.filter(r => r.status === 'fulfilled').length;
            const bad = off.filter((h, i) => res[i].status === 'rejected');
            if (okN) POps.toast('success', POps.tn('{n} bilgisayara uyandırma sinyali gönderildi.', okN));
            if (bad.length) POps.toast('warning', POps.tn('{n} bilgisayar uyandırılamadı (MAC adresi bilinmiyor olabilir): {names}', bad.length, { names: namesText(bad) }));
        });
    };

    // ---- Mesaj: Windows'un msg komutuyla oturumdaki kullanıcılara kısa not
    dev.message = async function (hosts, o) {
        o = o || {};
        const on = hosts.filter(h => { const d = byHost(h); return d && !POps.isOffline(d); });
        if (!on.length) return POps.toast('warning', POps.t('Açık bilgisayar yok; mesaj gönderilmedi.'));
        const text = await POps.prompt({
            title: on.length === 1 ? POps.t('{name} ekranına mesaj', { name: dev.name(on[0]) }) : POps.tn('{n} bilgisayara mesaj', on.length),
            message: POps.t('Oturumdaki kullanıcının ekranında 2 dakika görünür.'),
            label: POps.t('Mesaj'), multiline: true, maxLength: 250, confirmText: POps.t('Gönder'), icon: 'message',
            note: hosts.length > on.length ? POps.tn('Kapalı {n} bilgisayar atlanacak.', hosts.length - on.length) : ''
        });
        if (text === null) return;
        const clean = text.replace(/[\r\n\t]+/g, ' ').replace(/["%^]/g, "'").replace(/[\u0000-\u001f]/g, '').trim();
        if (!clean) return;
        await POps.act(o.btn, () => POps.post('/api/deploy_orchestration', {
            target_mode: 'PC', targets: on, taskSequence: [{ name: 'Mesaj', type: 'CMD', command: `msg * /TIME:120 "${clean}"` }],
            title: 'Mesaj', source: o.source || null
        }, { jobTitle: o.scopeLabel ? POps.taskName(['Mesaj', o.scopeLabel].join(' · ')) : POps.taskName('Mesaj') + ' · ' + count(on.length) }), { success: POps.tn('Mesaj {n} bilgisayara gönderildi.', on.length) });
    };

    // ---- Taşı
    dev.labs = function () {
        return [...new Set([...(state.customLabs || []), ...(state.devices || []).map(d => d.lab)])].filter(l => l && l !== UNASSIGNED).sort((a, b) => a.localeCompare(b, 'tr'));
    };
    dev.moveTo = async function (hosts, lab, o) {
        o = o || {};
        if (await POps.act(o.btn, () => POps.post('/api/move_pcs', { pc_names: hosts, new_lab: lab }), {
            success: lab === UNASSIGNED ? POps.tn('{n} bilgisayar sınıftan çıkarıldı.', hosts.length) : POps.tn('{n} bilgisayar {lab} sınıfına taşındı.', hosts.length, { lab })
        })) { POps.loadDevices().catch(() => {}); return true; }
        return false;
    };
    dev.moveMenu = function (anchor, hosts, o) {
        o = o || {};
        const current = o.currentLab;
        const items = [{ header: hosts.length === 1 ? POps.t('{name} nereye taşınsın?', { name: dev.name(hosts[0]) }) : POps.tn('{n} bilgisayar nereye taşınsın?', hosts.length) }];
        dev.labs().filter(l => l !== current).forEach(l => items.push({ label: l, icon: 'labs', onClick: () => dev.moveTo(hosts, l, o) }));
        items.push('-', { label: POps.t('Yeni sınıf…'), icon: 'plus', onClick: async () => {
            const name = await POps.prompt({ title: POps.t('Yeni sınıf'), label: POps.t('Sınıf adı'), maxLength: 60, confirmText: POps.t('Oluştur ve taşı') });
            if (!name || !name.trim()) return;
            try { await POps.post('/api/create_lab', { lab_name: name.trim() }); } catch (e) { return POps.toast('error', POps.errorMessage(e)); }
            dev.moveTo(hosts, name.trim(), o);
        } });
        if (current && current !== UNASSIGNED) items.push({ label: POps.t('Sınıftan çıkar'), icon: 'x', onClick: () => dev.moveTo(hosts, UNASSIGNED, o) });
        POps.menu(anchor, items);
    };

    // ---- Karantina
    dev.quarantine = async function (hosts, o) {
        o = o || {};
        const reason = await POps.prompt({
            title: hosts.length === 1 ? POps.t('{name} karantinaya alınsın mı?', { name: dev.name(hosts[0]) }) : POps.tn('{n} bilgisayar karantinaya alınsın mı?', hosts.length),
            message: POps.t('Kullanıcının ekranı kilitlenir; kaldırana kadar bilgisayar kullanılamaz. Kapalı bilgisayar açılınca kilitlenir.'),
            label: POps.t('Gerekçe'), placeholder: POps.t('Örn. sınav sırasında yetkisiz kullanım'), maxLength: 300,
            confirmText: hosts.length === 1 ? POps.t('Karantinaya al') : POps.tn('{n} bilgisayarı karantinaya al', hosts.length), danger: true, icon: 'shield'
        });
        if (reason === null) return;
        await POps.busy(o.btn, async () => {
            const res = await Promise.allSettled(hosts.map(h => POps.post('/api/security/lockdown', { target_pc: h, reason: reason.trim() })));
            const okN = res.filter(r => r.status === 'fulfilled').length;
            const failed = res.map((r, i) => r.status === 'rejected' ? `${dev.name(hosts[i])}: ${POps.errorMessage(r.reason)}` : null).filter(Boolean);
            if (okN) POps.toast('success', POps.tn('{n} bilgisayar karantinaya alındı.', okN));
            if (failed.length) POps.toast('error', failed.slice(0, 3).join('\n'));
        });
        POps.loadDevices().catch(() => {});
    };
    dev.unquarantine = async function (hosts, o) {
        o = o || {};
        const reason = await POps.prompt({
            title: hosts.length === 1 ? POps.t('{name} karantinadan çıkarılsın mı?', { name: dev.name(hosts[0]) }) : POps.tn('{n} bilgisayar karantinadan çıkarılsın mı?', hosts.length),
            label: POps.t('Gerekçe (isteğe bağlı)'), maxLength: 300, required: false, confirmText: POps.t('Karantinayı kaldır'), icon: 'unlock'
        });
        if (reason === null) return;
        await POps.busy(o.btn, async () => {
            const res = await Promise.allSettled(hosts.map(h => POps.post('/api/security/unlock', { target_pc: h, reason: reason.trim() })));
            const okN = res.filter(r => r.status === 'fulfilled').length;
            if (okN) POps.toast('success', POps.tn('{n} bilgisayar karantinadan çıkarıldı.', okN));
            const bad = res.length - okN;
            if (bad) POps.toast('error', POps.tn('{n} bilgisayarda kaldırılamadı.', bad));
        });
        POps.loadDevices().catch(() => {});
    };

    dev.rename = async function (host) {
        const d = byHost(host) || { hostname: host };
        const name = await POps.prompt({ title: POps.t('Bilgisayarın adını değiştir'), message: POps.t('Panelde bu adla görünür. Boş bırakırsanız bilgisayarın kendi adı kullanılır.'), label: POps.t('Görünen ad'), defaultValue: POps.deviceName(d), maxLength: 100, required: false, confirmText: POps.t('Kaydet') });
        if (name === null) return;
        if (await POps.act(null, () => POps.post('/api/rename_device', { pc_name: host, display_name: name.trim() }), { success: POps.t('Ad güncellendi.') })) POps.loadDevices().catch(() => {});
    };
    dev.remove = async function (host) {
        const ok = await POps.confirm({ title: POps.t('{name} silinsin mi?', { name: dev.name(host) }), message: POps.t('Cihaz kaydı, envanteri ve bekleyen görevleri silinir. Ajan çalışıyorsa bir sonraki bağlantıda yeniden kayıt ister.'), confirmText: POps.t('Cihazı sil'), danger: true, icon: 'trash' });
        if (!ok) return;
        if (await POps.act(null, () => POps.del('/api/devices/' + encodeURIComponent(host)), { success: POps.t('Cihaz silindi.') })) {
            if (POps.drawer.isOpen('pc:' + host)) POps.drawer.close();
            POps.loadDevices().catch(() => {});
        }
    };
    // Karantinadaki çevrimdışı bilgisayar için tek kullanımlık kod (tepsi simgesi → Yönetici Müdahalesi)
    dev.bypass = async function (host) {
        let data;
        try { data = await POps.post('/api/security/bypass_token/' + encodeURIComponent(host)); }
        catch (e) { POps.toast('error', POps.errorMessage(e)); return; }
        if (!data) return;
        const codes = [data.token];
        if (data.fallback_token) codes.push(data.fallback_token);
        // "Yönetici Müdahalesi (Bypass)": ajanın tepsi menüsündeki ad (ajan arayüzü Türkçe), çeviride de aynı kalır
        await POps.alert({
            title: POps.t('Çevrimdışı açma kodu'), icon: 'key', codes,
            message: [dev.name(host), POps.t('geçerli: {valid}', { valid: data.valid_for }), data.n ? POps.t('bugünün {n}. kodu', { n: data.n + 1 }) : ''].filter(Boolean).join(' · '),
            note: (data.fallback_token ? POps.t('İlk kod kabul edilmezse ikincisini deneyin (cihaz anahtarı henüz onaylanmadı).') + ' ' : '')
                + POps.t('Kullanıcı kodu tepsi simgesi → "Yönetici Müdahalesi (Bypass)" menüsüne girer. Her kod bir kez geçerlidir.')
        });
    };
    dev.setTeacher = async function (lab, host) {
        const removing = (state.mainPcs || {})[lab] === host;   // aynı bilgisayar gönderilince sunucu kaldırır
        if (await POps.act(null, () => POps.post('/api/set_main_pc', { lab_name: lab, pc_name: host }), { success: removing ? POps.t('{name} artık öğretmen bilgisayarı değil.', { name: dev.name(host) }) : POps.t('{name} öğretmen bilgisayarı yapıldı.', { name: dev.name(host) }) })) POps.loadDevices().catch(() => {});
    };
    // ---- Sınav modu (bkz. docs/dashboard.md "Sınıflar"): sınıfın süren sınavı ve her bilgisayarın durumu.
    // Kalan süre sunucunun verdiği remaining_seconds'tan yerel saatle sayılır (tarayıcının saati kaymış olabilir).
    const exam = dev.exam = { byLab: {}, active: {}, activeAt: 0 };
    // Sınıf adı "9/A" gibi eğik çizgi içerebilir: bölümler ayrı kodlanır (Apache kodlanmış %2F'yi reddeder)
    exam.path = (lab) => '/api/labs/' + String(lab).split('/').map(encodeURIComponent).join('/') + '/exam';
    const EXAM_STATE = {
        in_exam: { cls: 'ok', word: 'Sınavda', tip: 'Bilgisayar sınav modunda.' },
        left: { cls: 'bad', word: 'Ayrıldı', tip: 'Sınav bitmeden sınav modundan çıktı: yerel olarak kapatılmış ya da kurcalanmış olabilir.' },
        unreachable: { cls: 'muted', word: 'Ulaşılamıyor', tip: 'Bilgisayar kapalı ya da bağlı değil; bağlanınca sınavı alır.' },
        unsupported: { cls: 'warn', word: 'Desteklemiyor (eski ajan)', tip: 'Ajan sınav modunu tanımıyor; ajan güncellenmeli.' },
        pending: { cls: 'run', word: 'Bekleniyor', tip: 'Sınav gönderildi, ajanın yanıtı bekleniyor.' },
        denied: { cls: 'bad', word: 'Reddetti', tip: 'Sınav modu bu bilgisayarda yerel olarak kapalı.' }
    };
    exam.state = (key) => { const s = EXAM_STATE[key] || EXAM_STATE.pending; return { cls: s.cls, word: POps.t(s.word), tip: POps.t(s.tip) }; };
    function stampExam(e) {
        if (e && e.active) e.endsAt = Date.now() + Number(e.remaining_seconds || 0) * 1000;
        return e;
    }
    exam.secondsLeft = (e) => (e && e.endsAt ? Math.max(0, Math.round((e.endsAt - Date.now()) / 1000)) : 0);
    exam.leftText = function (e) {
        const sec = exam.secondsLeft(e);
        if (sec < 60) return POps.t('1 dakikadan az kaldı');
        const min = Math.ceil(sec / 60);
        if (min < 60) return POps.tn('{n} dk kaldı', min);
        return POps.t('{h} sa {m} dk kaldı', { h: Math.floor(min / 60), m: min % 60 });
    };
    exam.shortLeft = function (e) {
        const min = Math.ceil(exam.secondsLeft(e) / 60);
        return min < 60 ? POps.tn('{n} dk', Math.max(min, 1)) : POps.t('{h} sa {m} dk', { h: Math.floor(min / 60), m: min % 60 });
    };
    exam.badgeText = (e) => POps.t('Sınav modu · {left}', { left: exam.leftText(e) });
    // Sınıfın durumu (sınav, bilgisayarlar); kısa süre önbellekte. Hata çağırana gider.
    exam.load = async function (lab, maxAgeMs) {
        const hit = exam.byLab[lab];
        if (hit && Date.now() - hit.at < (maxAgeMs == null ? 5000 : maxAgeMs)) return hit.data;
        const data = await POps.get(exam.path(lab));
        stampExam(data.exam);
        data.byPc = {};
        (data.devices || []).forEach(d => { data.byPc[d.pc_name] = d; });
        exam.byLab[lab] = { at: Date.now(), data };
        return data;
    };
    // Süren bütün sınavlar (sınıf -> sınav): yan menü ve sınıf başlığı
    exam.loadActive = async function () {
        const r = await POps.get('/api/exams?active=true');
        const map = {};
        (r.items || []).forEach(e => { map[e.lab] = stampExam(e); });
        exam.active = map;
        exam.activeAt = Date.now();
        return map;
    };
    exam.forget = (lab) => { delete exam.byLab[lab]; exam.activeAt = 0; };

    dev.screenUrl = (hosts) => 'vision?pc=' + hosts.map(encodeURIComponent).join(',');
    dev.commandUrl = (hosts) => 'terminal?pc=' + hosts.map(encodeURIComponent).join(',');

    // ---- Dosya aktarımı: admin bilgisayara dosya gönderir ya da bilgisayardan dosya alır. Sunucu yalnızca çevrimiçi
    // ve dosya aktarımı açık (ajan files_enabled bildirmiş) bilgisayara gönderir; atlananları sebebiyle döner.
    const MB = 1024 * 1024;
    const FILE_MAX = 200 * MB;
    const PULL_SIZES = [10 * MB, 50 * MB, 200 * MB];
    const PULL_KEEP_DAYS = 7;
    const EXEC_EXT = /\.(lnk|url|scr)$/i;
    dev.sizeText = function (n) {
        n = Number(n) || 0;
        const f = (v, d) => v.toLocaleString(POps.locale, { maximumFractionDigits: d });
        if (n >= 1024 * MB) return f(n / (1024 * MB), 1) + ' GB';
        if (n >= MB) return f(n / MB, n >= 10 * MB ? 0 : 1) + ' MB';
        if (n >= 1024) return f(n / 1024, 0) + ' KB';
        return f(n, 0) + ' B';
    };
    // ok | offline | disabled (bilgisayarda kapatılmış) | unsupported (ajan bilmiyor)
    dev.filesState = function (d) {
        if (!d || POps.isOffline(d)) return 'offline';
        if (d.cap_files_enabled === true) return 'ok';
        return d.cap_files_enabled === false ? 'disabled' : 'unsupported';
    };
    const FILE_BLOCK = {
        offline: () => POps.t('Bilgisayar kapalı.'),
        disabled: () => POps.t('Dosya aktarımı bu bilgisayarda kapalı.'),
        unsupported: () => POps.t('Bu bilgisayardaki ajan dosya aktarımını desteklemiyor.')
    };
    dev.filesBlockText = (d) => { const st = dev.filesState(d); return st === 'ok' ? '' : FILE_BLOCK[st](); };

    // Form öğeleri (POps.form içinde); metinler textContent ile yazılır
    function fieldEl(label, control, opts) {
        const o = opts || {};
        const f = POps.el('div', { className: 'field' });
        if (label) f.append(o.forId ? POps.el('label', { for: o.forId, text: label }) : POps.el('span', { className: 'field-label', text: label }));
        f.append(control);
        if (o.hint) f.append(o.hint.nodeType ? o.hint : POps.el('div', { className: 'field-hint', text: o.hint }));
        const err = POps.el('div', { className: 'field-error', 'aria-live': 'polite' });
        f.append(err);
        f._error = (text) => { err.textContent = text || ''; f.classList.toggle('has-error', !!text); };
        return f;
    }
    function segmentedEl(label, items, value, onChange) {
        const box = POps.el('div', { className: 'segmented block', role: 'group', 'aria-label': label });
        const set = (v) => {
            box.dataset.value = v;
            box.querySelectorAll('button').forEach(b => { const on = b.dataset.v === String(v); b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
            if (onChange) onChange(v);
        };
        items.forEach(([v, text]) => {
            const b = POps.el('button', { type: 'button', dataset: { v: String(v) }, text });
            b.addEventListener('click', () => set(String(v)));
            box.append(b);
        });
        set(String(value));
        return box;
    }
    function checkEl(id, text) {
        const cb = POps.el('input', { type: 'checkbox', id });
        return { cb, el: POps.el('label', { className: 'check', for: id }, [cb, POps.el('span', { text })]) };
    }
    function reasonField(id, placeholder) {
        const input = POps.el('input', { type: 'text', id, maxlength: '300', autocomplete: 'off', placeholder });
        const f = fieldEl(POps.t('Gerekçe'), input, { forId: id, hint: POps.t('Denetim kaydına yazılır.') });
        input.addEventListener('input', () => f._error(''));
        return { input, f };
    }

    dev.sendFile = async function (hosts, o) {
        o = o || {};
        hosts = [...new Set(hosts || [])];
        if (!hosts.length) return POps.toast('warning', POps.t('Hedef bilgisayar yok.'));
        const ready = hosts.filter(h => dev.filesState(byHost(h)) === 'ok');
        if (!ready.length) {
            return POps.toast('warning', hosts.length === 1 ? dev.filesBlockText(byHost(hosts[0])) : POps.t('Seçili bilgisayarların hiçbirine dosya gönderilemez (kapalı ya da dosya aktarımı yok).'));
        }
        const fileInput = POps.el('input', { type: 'file', id: 'ftFile' });
        const dropT = POps.el('span', { className: 'drop-t', text: POps.t('Dosya seçin ya da buraya bırakın') });
        const dropS = POps.el('span', { className: 'drop-s', text: POps.t('En fazla {size}', { size: dev.sizeText(FILE_MAX) }) });
        const drop = POps.el('label', { className: 'pops-drop', for: 'ftFile' }, [fileInput, POps.iconEl('upload'), dropT, dropS]);
        const bar = POps.el('i', { className: 'run', style: 'width:0%' });
        const pct = POps.el('span', { text: POps.pct(0) });
        const prog = POps.el('div', { className: 'pops-prog', hidden: true }, [POps.el('div', { className: 'pbar' }, [bar]), pct]);
        const fileF = fieldEl(POps.t('Dosya'), drop);
        fileF.insertBefore(prog, fileF.lastChild);
        const pick = () => {
            const f = fileInput.files && fileInput.files[0];
            drop.classList.toggle('has', !!f);
            dropT.textContent = f ? f.name : POps.t('Dosya seçin ya da buraya bırakın');
            dropS.textContent = f ? dev.sizeText(f.size) : POps.t('En fazla {size}', { size: dev.sizeText(FILE_MAX) });
            fileF._error('');
        };
        fileInput.addEventListener('change', pick);
        ['dragenter', 'dragover'].forEach(t => drop.addEventListener(t, () => drop.classList.add('over')));
        ['dragleave', 'drop'].forEach(t => drop.addEventListener(t, () => drop.classList.remove('over')));

        const DEST_HINT = {
            public_desktop: () => POps.t('Bilgisayardaki bütün kullanıcıların masaüstünde görünür.'),
            inbox: () => POps.t('Ajanın gelen kutusu klasörüne yazılır; masaüstünü doldurmaz.')
        };
        const destHint = POps.el('div', { className: 'field-hint' });
        const dest = segmentedEl(POps.t('Hedef klasör'), [['public_desktop', POps.t('Ortak masaüstü')], ['inbox', POps.t('POps gelen kutusu')]], 'public_desktop', (v) => { destHint.textContent = DEST_HINT[v](); });
        const destF = fieldEl(POps.t('Hedef klasör'), dest, { hint: destHint });
        const reason = reasonField('ftReason', POps.t('Örn. 9-A ödev dosyası'));
        const exec = checkEl('ftExec', POps.t('Çalıştırılabilir dosyaya izin ver'));
        const execF = fieldEl('', exec.el, { hint: POps.t('Kısayol ve ekran koruyucu dosyaları (.lnk, .url, .scr) yalnızca bu işaretliyse yazılır.') });
        exec.cb.addEventListener('change', () => execF._error(''));
        const content = POps.el('div', { className: 'pops-form' }, [fileF, destF, reason.f, execF]);

        const skipped = hosts.length - ready.length;
        const result = await POps.form({
            title: POps.t('Dosya gönder'),
            icon: 'upload',
            message: (ready.length === 1 ? dev.name(ready[0]) : namesText(ready))
                + (skipped ? '\n' + POps.tn('{n} bilgisayar atlanacak (kapalı ya da dosya aktarımı yok).', skipped) : ''),
            content,
            confirmText: ready.length === 1 ? POps.t('Gönder') : POps.tn('{n} bilgisayara gönder', ready.length),
            submit: async ({ signal }) => {
                const file = fileInput.files && fileInput.files[0];
                let bad = false;
                if (!file) { fileF._error(POps.t('Bir dosya seçin.')); bad = true; }
                else if (!file.size) { fileF._error(POps.t('Dosya boş.')); bad = true; }
                else if (file.size > FILE_MAX) { fileF._error(POps.t('Dosya çok büyük (en fazla {size}).', { size: dev.sizeText(FILE_MAX) })); bad = true; }
                const why = reason.input.value.trim();
                if (why.length < 3) { reason.f._error(POps.t('Gerekçe yazın (en az 3 karakter).')); bad = true; }
                if (file && EXEC_EXT.test(file.name) && !exec.cb.checked) { execF._error(POps.t('Bu dosya türü için kutuyu işaretleyin.')); bad = true; }
                if (bad) return false;
                const fd = new FormData();
                fd.append('file', file);
                ready.forEach(h => fd.append('pcs', h));
                fd.append('dest', dest.dataset.value);
                fd.append('reason', why);
                fd.append('allow_exec', exec.cb.checked ? 'true' : 'false');
                prog.hidden = false;
                const setP = (r) => { const p = Math.round(r * 100); bar.style.width = p + '%'; pct.textContent = POps.pct(p); };
                setP(0);
                try {
                    return await POps.upload('/api/files/push', fd, { signal, onProgress: setP });
                } catch (e) {
                    prog.hidden = true;
                    throw e;
                }
            }
        });
        if (!result) return;
        const n = (result.transfers || []).length;
        POps.toast('success', n === 1 ? POps.t('{file} gönderildi; bilgisayar indiriyor.', { file: result.name }) : POps.tn('{file} {n} bilgisayara gönderildi.', n, { file: result.name }));
        const left = (result.skipped || []).length;
        if (left) POps.toast('warning', POps.tn('{n} bilgisayar atlandı (kapalı ya da dosya aktarımı yok).', left));
        if (openHost && ready.includes(openHost)) loadFiles(openHost);
    };

    dev.pullFile = async function (host) {
        const d = byHost(host);
        if (dev.filesState(d) !== 'ok') return POps.toast('warning', dev.filesBlockText(d));
        const path = POps.el('input', { type: 'text', id: 'ftPath', className: 'mono', maxlength: '1024', autocomplete: 'off', spellcheck: 'false', placeholder: 'C:\\Users\\Public\\Documents\\rapor.pdf' });
        const pathF = fieldEl(POps.t('Dosyanın yolu'), path, { forId: 'ftPath', hint: POps.t('Bilgisayardaki tam yol. Ağ yolları kabul edilmez.') });
        path.addEventListener('input', () => pathF._error(''));
        const size = segmentedEl(POps.t('En büyük boyut'), PULL_SIZES.map(v => [v, dev.sizeText(v)]), 50 * MB);
        const sizeF = fieldEl(POps.t('En büyük boyut'), size, { hint: POps.t('Daha büyük dosya alınmaz.') });
        const reason = reasonField('ftPullReason', POps.t('Örn. sınav dosyasının kontrolü'));
        const parts = [pathF, sizeF, reason.f];
        let any = null;
        if (IS_SUPER) {
            any = checkEl('ftAny', POps.t('Başka kullanıcıların profilinden de'));
            parts.push(fieldEl('', any.el, { hint: POps.t('İşaretli değilse başka kullanıcıların profil klasörlerinden dosya alınmaz. Yalnızca süper admin.') }));
        }
        const result = await POps.form({
            title: POps.t('Dosya al'),
            icon: 'download',
            message: dev.name(host),
            note: POps.tn('Alınan dosya {n} gün saklanır, sonra silinir.', PULL_KEEP_DAYS),
            content: POps.el('div', { className: 'pops-form' }, parts),
            confirmText: POps.t('Dosyayı iste'),
            submit: async ({ signal }) => {
                const p = path.value.trim();
                const why = reason.input.value.trim();
                let bad = false;
                if (!/^[A-Za-z]:[\\/]./.test(p)) { pathF._error(POps.t('Tam yol yazın (ör. C:\\Users\\Public\\Documents\\rapor.pdf).')); bad = true; }
                if (why.length < 3) { reason.f._error(POps.t('Gerekçe yazın (en az 3 karakter).')); bad = true; }
                if (bad) return false;
                return POps.post('/api/files/pull', { pc: host, path: p, max_size: Number(size.dataset.value), reason: why, any_profile: !!(any && any.cb.checked) }, { signal });
            }
        });
        if (!result) return;
        POps.toast('success', POps.t('İstek gönderildi. Dosya gelince "Dosya aktarımları" listesinde indirme bağlantısı çıkar.'));
        if (openHost === host) loadFiles(host);
    };

    // Aktarım listesi (ayrıntı panelinde)
    const FILE_LIVE = ['sent', 'downloading', 'uploading'];
    const FILE_STATE = { done: 'ok', rejected: 'bad', failed: 'bad', expired: 'warn' };
    function fileStatusWord(f) {
        if (f.status === 'done') return f.direction === 'push' ? POps.t('Teslim edildi') : POps.t('Alındı');
        const w = { sent: 'Bekliyor', downloading: 'İndiriliyor', uploading: 'Yükleniyor', rejected: 'Reddedildi', failed: 'Başarısız', expired: 'Süresi doldu' }[f.status];
        return w ? POps.t(w) : f.status;
    }
    function fileRowHtml(f) {
        const k = FILE_STATE[f.status] || 'run';
        const push = f.direction === 'push';
        const dirWord = push ? POps.t('Bilgisayara') : POps.t('Bilgisayardan');
        const meta = [dirWord, f.created_by || '?'];
        const metaHtml = meta.map(escapeHtml).join(' · ') + ' · ' + POps.timeHtml(f.created_at)
            + (f.size ? ' · ' + escapeHtml(dev.sizeText(f.size)) : '')
            + (f.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: f.reason }) : '');
        const whyHtml = (k === 'bad' || k === 'warn') && f.detail ? `<div class="why${k === 'warn' ? ' warn' : ''}">${escapeHtml(f.detail)}</div>` : '';
        const pathHtml = f.path ? `<div class="meta path" title="${escapeHtml(f.path)}">${escapeHtml(f.path)}</div>` : '';
        // Alınan dosya: aynı kökteki API'den, oturum çereziyle; sunucu her zaman ek (attachment) olarak verir
        let linkHtml = '';
        if (f.downloadable && CAN_ADMIN) {
            const tip = f.expires_at ? POps.t('{date} tarihine kadar indirilebilir', { date: POps.fullTime(f.expires_at) }) : '';
            linkHtml = `<a class="btn secondary sm" href="/api/files/${encodeURIComponent(f.transfer_id)}/content" download data-tip="${escapeHtml(tip)}" data-tip-pos="left">${POps.iconHtml('download', 'sm')}${POps.tHtml('İndir')}</a>`;
        } else if (f.purged && !push) {
            linkHtml = `<span class="word">${POps.tHtml('Silindi')}</span>`;
        }
        return `<div class="act"><div class="res ${escapeHtml(k)}" aria-label="${escapeHtml(dirWord)}">${POps.iconHtml(push ? 'upload' : 'download')}</div><div style="min-width:0"><div class="what">${escapeHtml(f.name || '—')}</div>
            <div class="meta">${metaHtml}</div>${pathHtml}${whyHtml}</div>
            <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(fileStatusWord(f))}</span>${linkHtml}</div></div>`;
    }
    let filesTimer = null;
    async function loadFiles(host) {
        clearTimeout(filesTimer);
        const box = POps.drawer.isOpen('pc:' + host) ? POps.drawer.body().querySelector('#devFiles') : null;
        if (!box) return;
        let items;
        try {
            items = (await POps.get('/api/files?pc=' + encodeURIComponent(host) + '&limit=8')).items || [];
        } catch (e) {
            if (openHost === host) box.textContent = POps.t('Dosya aktarımları alınamadı: {error}', { error: POps.errorMessage(e) });
            return;
        }
        if (openHost !== host) return;
        box.innerHTML = items.length ? `<h3>${POps.tHtml('Dosya aktarımları')}</h3>` + items.map(fileRowHtml).join('') : '';
        // Süren aktarım varsa liste 3 sn'de bir tazelenir (panel kapanınca ya da başka bilgisayar açılınca durur)
        if (items.some(f => FILE_LIVE.includes(f.status))) filesTimer = setTimeout(() => { if (openHost === host) loadFiles(host); }, 3000);
    }

    // ---- Görev kaydının okunur hali (Son işlemler, Kayıtlar, İşlemler)
    const SOURCE_TEXT = { labs: 'Sınıflar', devices: 'Cihazlar', terminal: 'Uzak komut', deploy: 'Dağıtım', tasks: 'İşlemler', vision: 'Uzak ekran', schedule: 'Zamanlanmış', index: 'Kontrol merkezi', system: 'Sistem' };
    dev.sourceText = (s) => (SOURCE_TEXT[s] ? POps.t(SOURCE_TEXT[s]) : s || '');
    const STATUS_WORD = {
        Pending: 'Sırada', Running: 'Çalışıyor', Paused: 'Duraklatıldı', Completed: 'Tamamlandı', 'Completed (Rebooted)': 'Tamamlandı',
        Failed: 'Başarısız', Error: 'Hata', Cancelled: 'İptal edildi', Interrupted: 'Yarıda kaldı', 'Timed Out': 'Zaman aşımı',
        Denied: 'Reddedildi', Unknown: 'Bilinmiyor', Expired: 'Süresi doldu'
    };
    dev.statusWord = (s) => (STATUS_WORD[s] ? POps.t(STATUS_WORD[s]) : s || '—');
    // Bilinen komutları okunur ada çevirir (eski kayıtlarda başlık yok)
    dev.taskTitle = function (t) {
        const c = String(t.command || t.script_path || '');
        // Uzak komut sayfasından yazılan serbest komut: adı "Komut" (eskiden "Terminal"), komutun kendisi gösterilir
        if (t.title === 'Komut' || t.title === 'Terminal') return POps.t('Komut: {command}', { command: c.length > 50 ? c.slice(0, 47) + '…' : c });
        // Başlık sunucudaki veridir (panel Türkçe yazar): bilinen adlar POps.taskName ile çevrilir
        if (t.title) return POps.taskName(t.title);
        if (/^shutdown \/r/i.test(c)) return POps.taskName('Yeniden başlat');
        if (/^shutdown \/s/i.test(c)) return POps.taskName('Kapat');
        if (/^msg \*/i.test(c)) return POps.taskName('Mesaj');
        if (/^POPS_UPDATE_AGENT/i.test(c) || /agent.?update/i.test(c)) return POps.taskName('Ajan güncellemesi');
        return c.length > 60 ? c.slice(0, 57) + '…' : (c || POps.t('Görev'));
    };
    // Reddedildi / başarısız için açık neden
    dev.failReason = function (t) {
        const s = t.status, x = t.exit_code;
        if (s === 'Denied') return x === -5 ? POps.t('Cihazdaki ajan bu komutu yetki politikası gereği çalıştırmadı (uzak komut bu cihazda kapalı olabilir).') : POps.t('Cihaz komutu reddetti.');
        if (s === 'Timed Out') return POps.t('Komut süre sınırını aştı ve durduruldu.');
        if (s === 'Expired') return POps.t('Cihaz zamanında bağlanmadığı için görev başlatılmadı.');
        if (s === 'Interrupted') return POps.t('Komut çalışırken bağlantı koptu ya da cihaz yeniden başladı.');
        if (s === 'Cancelled') return POps.t('İşlem iptal edildi.');
        if ((s === 'Failed' || s === 'Error') && x != null) return POps.t('Komut {code} çıkış koduyla bitti.', { code: x });
        return '';
    };

    // ---- Olay sözlüğü (Kayıtlar ve Kontrol merkezi)
    // Kayıtlar agent_logs_v2'den gelir: event_type / action ham anahtardır; burada okunur cümleye çevrilir.
    // Önem dürüst: oturum açma/kapama ve yönetici işlemleri "Bilgi"; yalnızca gerçek sorunlar uyarı ya da kritik.
    // Tablolar sayfa yüklenirken bir kez çevrilir (dil sayfa boyunca değişmez); anahtarları ham değerlerdir.
    const tAll = (o) => { Object.keys(o).forEach(k => { o[k] = POps.t(o[k]); }); return o; };
    const SEV_WORD = tAll({ info: 'Bilgi', warn: 'Uyarı', bad: 'Kritik' });
    const KIND_LABEL = tAll({ auth: 'Oturumlar', policy: 'Kural ihlalleri', quarantine: 'Karantina', command: 'Komutlar', agent: 'Ajan ve bakım', other: 'Diğer' });
    const CAT_LABEL = tAll({ security: 'Güvenlik', restricted_content: 'Kural ihlali', system_maintenance: 'Bakım', legacy: 'Eski kayıt' });
    const RISK_LABEL = tAll({ info: 'bilgi', low: 'düşük', medium: 'orta', high: 'yüksek', critical: 'kritik' });
    const CAP = tAll({ terminal: 'uzak komut', vision: 'uzak ekran', files: 'dosya aktarımı', exam: 'sınav modu' });
    const BY_TYPE = {
        'auth.login': 'login', 'auth.logout': 'logout', 'auth.failed': 'login_failed', 'policy.alert': 'dns_block',
        'security.lockdown': 'lockdown', 'security.unlock': 'unlock', 'security.bypass_code': 'bypass_code',
        'deploy.execution': 'execute_queue', 'agent.update': 'update_problem', 'agent.capability_denied': 'capability_denied',
        'agent.enroll_denied': 'enroll_denied', 'agent.auto_quarantine': 'auto_quarantine', 'agent.unlock_failed': 'unlock_failed',
        'agent.offline_bypass': 'offline_bypass'
    };
    const commandOf = (r, m) => String(m.raw_command || String(r.message || '').replace(/^G(ö|o)rev:\s*/i, '')).trim();
    const EVENTS = {
        login: { title: () => POps.t('Oturum açıldı'), icon: 'user', kind: 'auth', sev: 'info' },
        logout: { title: () => POps.t('Oturum kapatıldı'), icon: 'logout', kind: 'auth', sev: 'info' },
        login_failed: { title: () => POps.t('Hatalı giriş denemesi'), icon: 'user', kind: 'auth' },
        dns_block: { title: (r, m) => m.domain ? POps.t('Yasaklı siteye erişim: {domain}', { domain: m.domain }) : POps.t('Yasaklı siteye erişim'), icon: 'shield', kind: 'policy', quietReason: true },
        lockdown: { title: () => POps.t('Karantinaya alındı'), icon: 'lock', kind: 'quarantine', sev: 'warn', admin: true },
        unlock: { title: () => POps.t('Karantina kaldırıldı'), icon: 'unlock', kind: 'quarantine', sev: 'info', admin: true },
        auto_quarantine: { title: () => POps.t('Kural ihlali eşiğinde kendini karantinaya aldı'), icon: 'lock', kind: 'quarantine' },
        unlock_failed: { title: () => POps.t('Karantina kaldırılamadı, ağ yalıtımı sürüyor'), icon: 'lock', kind: 'quarantine' },
        offline_bypass: { title: () => POps.t('Çevrimdışı açma koduyla karantinadan çıktı'), icon: 'key', kind: 'quarantine' },
        bypass_code: { title: () => POps.t('Çevrimdışı açma kodu üretildi'), icon: 'key', kind: 'quarantine', sev: 'info' },
        execute_queue: { title: (r, m) => { const c = commandOf(r, m); return c ? POps.t('Komut gönderildi: {command}', { command: dev.taskTitle({ command: c }) }) : POps.t('Komut gönderildi'); }, icon: 'terminal', kind: 'command', sev: 'info' },
        update_problem: { title: () => POps.t('Ajan güncellemesi sorunlu bitti'), icon: 'refresh', kind: 'agent' },
        capability_denied: { title: (r) => CAP[r.reason] ? POps.t('Kapalı {feature} istendi, ajan reddetti', { feature: CAP[r.reason] }) : POps.t('Kapalı bir özellik istendi, ajan reddetti'), icon: 'eye', kind: 'agent', quietReason: true },
        enroll_denied: { title: () => POps.t('Kayıtlı bilgisayarın anahtarı yeniden istendi, reddedildi'), icon: 'alert', kind: 'other' }
    };

    function parseMeta(v) {
        if (v && typeof v === 'object') return v;
        try { const o = JSON.parse(v || '{}'); return o && typeof o === 'object' && !Array.isArray(o) ? o : {}; } catch (e) { return {}; }
    }
    // Sunucu zamanı yerel saatle "YYYY-AA-GG SS:DD:ss" metnidir; T ile her tarayıcıda yerel saat olarak okunur
    const isoOf = (ts) => typeof ts === 'string' ? ts.replace(' ', 'T') : ts;
    const cleanMsg = (s) => String(s || '').replace(/^[^\p{L}\p{N}]+/u, '').trim();
    const cap1 = (s) => s ? s.charAt(0).toLocaleUpperCase('tr') + s.slice(1) : s;
    function riskSev(r) {
        const k = String(r.risk_level || '').toLowerCase();
        if (k === 'critical') return 'bad';
        if (k === 'high' || k === 'medium') return 'warn';
        if (!k && r.log_type) return /error|critical/i.test(r.log_type) ? 'bad' : /warning/i.test(r.log_type) ? 'warn' : 'info';
        return 'info';
    }
    function whoOf(r, m) {
        const a = String(m.created_by || r.actor_id || '').trim();
        if (!a || a === 'Agent' || a === r.pc_name) return POps.t('Ajan');
        if (/^system/i.test(a)) return POps.t('Sistem');
        return POps.tPattern(a);   // "admin (zamanlanmış #4)" gibi sunucu ekleri çevrilir
    }
    function sourceOf(r) {
        const t = String(r.event_type || '');
        if (/^security\./.test(t)) return POps.t('Panel');
        if (/^deploy\./.test(t)) return POps.t('Görev kuyruğu');
        if (/^(agent|auth|policy)\./.test(t)) return POps.t('Bilgisayardaki ajan');
        return '';
    }
    function norm(r) {
        const m = parseMeta(r.meta_data);
        const key = BY_TYPE[r.event_type] || (EVENTS[r.action] ? r.action : '');
        const def = EVENTS[key] || null;
        const cat = String(r.category || r.log_type || '').toLowerCase();
        const sev = def && def.sev ? def.sev : riskSev(r);
        const reason = String(r.reason || '').trim();
        let why = '';
        if (reason && !(def && def.quietReason)) {
            if (key === 'enroll_denied' && reason === 'already_enrolled') why = POps.t('Bilgisayar zaten kayıtlı; yeni anahtar verilmedi.');
            else why = def && def.admin ? POps.t('Gerekçe: {reason}', { reason }) : cap1(reason);
        }
        // Ajanın yazdığı serbest ileti (veri) çevrilmez
        const title = def ? def.title(r, m) : (cleanMsg(r.message).slice(0, 160) || r.event_type || POps.t('Olay'));
        return {
            id: String(r.id), r, m, key, sev, title, why,
            kind: def ? def.kind : (cat === 'restricted_content' ? 'policy' : cat === 'system_maintenance' ? 'agent' : 'other'),
            icon: def ? def.icon : (sev === 'info' ? 'info' : 'alert'),
            security: cat === 'security' || cat === 'restricted_content' || /^(security|auth|policy)\./.test(String(r.event_type || '')),
            who: whoOf(r, m), at: isoOf(r.timestamp), day: String(r.timestamp || '').slice(0, 10), pc: r.pc_name || ''
        };
    }
    POps.logs = { SEV_WORD, KIND_LABEL, CAT_LABEL, RISK_LABEL, CAP, BY_TYPE, commandOf, EVENTS, parseMeta, isoOf, cleanMsg, cap1, riskSev, whoOf, sourceOf, norm };

    // ---- Ayrıntı paneli
    let openHost = null;
    function headHtml(d) {
        const st = dev.state(d);
        const sub = st.cls === 'off' ? `${escapeHtml(st.word)}${st.since ? ' · ' + POps.timeHtml(st.since) : ''}` : escapeHtml(st.word) + (dev.user(d) ? ' · ' + escapeHtml(dev.user(d)) : '');
        return `<div class="drawer-head">
            <div class="drawer-title">
                <span class="drawer-ico ${escapeHtml(st.cls)}">${POps.iconHtml('monitor', 'lg')}</span>
                <div style="min-width:0"><h2>${escapeHtml(POps.deviceName(d))}</h2><div class="sub"><span class="dot ${escapeHtml(st.cls)}"></span>${sub}</div></div>
            </div>
            <button type="button" class="ibtn sm" data-act="close" data-tip="${escapeHtml(POps.t('Kapat (Esc)'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button>
        </div>`;
    }
    function circlesHtml(d) {
        if (!CAN_ADMIN) return '';
        const off = POps.isOffline(d);
        const visionOff = d.cap_vision_enabled === false || off;
        const termOff = d.cap_terminal_enabled === false || off;
        return `<div class="circs">
            <button type="button" class="circ" data-act="screen" ${visionOff ? 'disabled' : ''}><span>${POps.iconHtml('eye')}</span>${POps.tHtml('Ekran')}</button>
            <button type="button" class="circ" data-act="command" ${termOff ? 'disabled' : ''}><span>${POps.iconHtml('terminal')}</span>${POps.tHtml('Komut')}</button>
            <button type="button" class="circ" data-act="power" aria-haspopup="menu"><span>${POps.iconHtml('power')}</span>${POps.tHtml('Güç')}</button>
            <button type="button" class="circ" data-act="more" aria-haspopup="menu"><span>${POps.iconHtml('more')}</span>${escapeHtml(POps.tx('Diğer', 'menu'))}</button>
        </div>`;
    }
    function factsHtml(d) {
        const off = POps.isOffline(d);
        // [etiket, düz metin değer, eşaralıklı mı]; boş değerli satır gösterilmez
        const facts = [
            ['Kullanıcı', dev.user(d)], ['Uygulama', dev.app(d)], ['Sınıf', d.lab && d.lab !== UNASSIGNED ? d.lab : POps.t('Atanmamış')],
            ['IP', d.ip, true], ['MAC', d.mac, true], ['Ajan', dev.version(d)], ['Bellek', d.cap_ram_readable],
            ['Son görülme', off ? null : POps.t('şimdi')], ['Son kopuş', off ? d.last_disconnect_reason : null], ['Kimlik', d.hostname, true]
        ];
        const seenHtml = off ? `<div class="grow"><span>${POps.tHtml('Son görülme')}</span><span>${POps.timeHtml(d.last_seen)}</span></div>` : '';
        const rowsHtml = facts.filter(f => f[1]).map(f => `<div class="grow"><span>${POps.tHtml(f[0])}</span><span${f[2] ? ' class="mono"' : ''}>${escapeHtml(f[1])}</span></div>`).join('');
        return `<div class="glist">${rowsHtml}${seenHtml}</div>`;
    }
    function issuesHtml(d) {
        return dev.issues(d).map(i => `<div class="issue ${escapeHtml(i.kind)}">${i.icon ? POps.iconHtml(i.icon, 'sm') : `<span class="mark ${escapeHtml(i.kind)}">${escapeHtml(i.glyph)}</span>`}<span style="flex:1">${escapeHtml(i.text)}</span></div>`).join('');
    }
    function activityRowHtml(a) {
        if (a.kind === 'vision') {
            const dur = a.ended_at && a.at ? POps.duration((POps.toDate(a.ended_at) - POps.toDate(a.at)) / 1000) : '';
            return `<div class="act"><div class="res">${POps.iconHtml('eye')}</div><div style="min-width:0"><div class="what">${a.mandatory ? POps.tHtml('Uzak ekran (zorunlu)') : POps.tHtml('Uzak ekran')}</div>
                <div class="meta">${escapeHtml(a.by || '?')} · ${POps.timeHtml(a.at)}${dur ? ' · ' + escapeHtml(dur) : ''}${a.reason ? ' · ' + escapeHtml(a.reason) : ''}</div></div>
                <div class="side"><span class="word">${a.ended_at ? POps.tHtml('Bitti') : POps.tHtml('Sürüyor')}</span></div></div>`;
        }
        const k = POps.taskState(a.status);
        const why = k === 'bad' ? dev.failReason(a) : '';
        const metaHtml = escapeHtml(POps.tPattern(a.by || '?')) + ' · ' + POps.timeHtml(a.at) + (a.source ? ' · ' + escapeHtml(dev.sourceText(a.source)) : '');
        return `<div class="act"><div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div><div style="min-width:0"><div class="what">${escapeHtml(dev.taskTitle(a))}</div>
            <div class="meta">${metaHtml}${a.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: a.reason }) : ''}</div>
            ${why ? `<div class="why">${escapeHtml(why)}</div>` : ''}</div>
            <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(dev.statusWord(a.status))}</span></div></div>`;
    }
    // Sınav modu: bilgisayarın sınıfında süren sınav varsa durumu (önbellekten; panel açılınca tazelenir)
    function examHtml(d) {
        const hit = d.lab && exam.byLab[d.lab];
        const data = hit && hit.data;
        if (!data || !data.active || !data.exam) return '';
        const e = data.exam;
        const row = data.byPc[d.hostname];
        const st = exam.state(row ? row.state : 'pending');
        const allow = (e.allow || []).length ? e.allow.join(', ') : POps.t('yalnızca POps sunucusu');
        const rowHtml = (label, valueHtml) => `<div class="grow"><span>${escapeHtml(label)}</span><span>${valueHtml}</span></div>`;
        const rowsHtml = rowHtml(POps.t('Durum'), `<span class="badge ${escapeHtml(st.cls)}" data-tip="${escapeHtml(st.tip)}">${escapeHtml(st.word)}</span>`)
            + rowHtml(POps.t('Kalan süre'), escapeHtml(exam.leftText(e)))
            + rowHtml(POps.t('İzin verilen'), escapeHtml(allow))
            + ((e.block_apps || []).length ? rowHtml(POps.t('Engellenen programlar'), escapeHtml(e.block_apps.join(', '))) : '')
            + rowHtml(POps.t('Başlatan'), escapeHtml(e.started_by || '?') + ' · ' + POps.timeHtml(e.started_at));
        return `<div><h3>${POps.tHtml('Sınav modu')}</h3><div class="glist">${rowsHtml}</div></div>`;
    }
    async function refreshExam(host, lab) {
        if (!lab || lab === UNASSIGNED) return;
        const had = exam.byLab[lab];
        try { await exam.load(lab, 10000); } catch (e) { return; }
        if (openHost !== host || exam.byLab[lab] === had || !POps.drawer.isOpen('pc:' + host)) return;
        const d = byHost(host);
        if (d) render(POps.drawer.body(), d, true);
    }
    async function loadActivity(host, box) {
        try {
            const r = await POps.get('/api/devices/' + encodeURIComponent(host) + '/activity?limit=8');
            if (openHost !== host) return;
            const items = r.items || [];
            box.innerHTML = items.length ? items.map(activityRowHtml).join('') : `<div class="faint" style="font-size:var(--text-sm);padding:6px 0">${POps.tHtml('Bu cihazda henüz işlem yok.')}</div>`;
        } catch (e) {
            if (openHost === host) box.textContent = POps.t('Son işlemler alınamadı: {error}', { error: POps.errorMessage(e) });
        }
    }
    function render(body, d, keepRecent) {
        const recent = keepRecent ? body.querySelector('#devRecent') : null;
        const files = keepRecent ? body.querySelector('#devFiles') : null;
        body.innerHTML = headHtml(d) + circlesHtml(d) + issuesHtml(d) + examHtml(d) + factsHtml(d)
            + `<div><h3>${POps.tHtml('Son işlemler')}</h3><div id="devRecent"><div class="faint" style="font-size:var(--text-sm);padding:6px 0">${POps.tHtml('Yükleniyor…')}</div></div></div>`
            + '<div id="devFiles"></div>'
            + `<a href="devices?pc=${encodeURIComponent(d.hostname)}" style="font-size:var(--text-sm)">${POps.tHtml('Cihazlar sayfasında aç')}</a>`;
        if (recent) body.querySelector('#devRecent').replaceWith(recent);
        if (files) body.querySelector('#devFiles').replaceWith(files);
    }
    dev.open = async function (host, opts) {
        const o = opts || {};
        let d = byHost(host);
        if (!d) {
            try { await POps.loadDevices(); } catch (e) { return POps.toast('error', POps.errorMessage(e)); }
            d = byHost(host);
            if (!d) return POps.toast('warning', POps.t('Cihaz bulunamadı.'));
        }
        // Panel başka bir bilgisayar açıkken açılırsa önce onun kapanışı çalışır; yeni bilgisayar ondan sonra
        // işaretlenir (eskiden kapanış yenisini de siliyordu ve "Son işlemler" yüklenmiyordu)
        const body = POps.drawer.open('pc:' + host, { onClose: () => { if (openHost === host) { openHost = null; clearTimeout(filesTimer); } if (o.onClose) o.onClose(); } });
        openHost = host;
        render(body, d, false);
        if (!body.dataset.wired) {
            body.dataset.wired = '1';
            body.addEventListener('click', (e) => {
                const b = e.target.closest('[data-act]');
                if (!b || !openHost || b.disabled) return;
                const h = openHost, cur = byHost(h) || { hostname: h };
                const act = b.dataset.act;
                if (act === 'close') POps.drawer.close();
                else if (act === 'screen') window.location.href = dev.screenUrl([h]);
                else if (act === 'command') window.location.href = dev.commandUrl([h]);
                else if (act === 'power') POps.menu(b, [
                    { label: POps.t('Uyandır'), icon: 'zap', disabled: !POps.isOffline(cur), onClick: () => dev.power('wake', [h], { source: o.source }) },
                    { label: POps.t('Yeniden başlat'), icon: 'restart', disabled: POps.isOffline(cur), onClick: () => dev.power('restart', [h], { source: o.source }) },
                    { label: POps.tx('Kapat', 'power'), icon: 'power', danger: true, disabled: POps.isOffline(cur), onClick: () => dev.power('shutdown', [h], { source: o.source }) }
                ]);
                else if (act === 'more') POps.menu(b, [
                    { label: POps.t('Mesaj gönder'), icon: 'message', disabled: POps.isOffline(cur), onClick: () => dev.message([h], { source: o.source }) },
                    { label: POps.t('Dosya gönder'), icon: 'upload', disabled: dev.filesState(cur) !== 'ok', title: dev.filesBlockText(cur) || null, onClick: () => dev.sendFile([h]) },
                    { label: POps.t('Dosya al'), icon: 'download', disabled: dev.filesState(cur) !== 'ok', title: dev.filesBlockText(cur) || null, onClick: () => dev.pullFile(h) },
                    { label: POps.t('Yeniden adlandır'), icon: 'edit', onClick: () => dev.rename(h) },
                    { label: POps.t('Başka sınıfa taşı…'), icon: 'move', onClick: () => dev.moveMenu(b, [h], { currentLab: cur.lab }) },
                    cur.lab && cur.lab !== UNASSIGNED ? { label: (state.mainPcs || {})[cur.lab] === h ? POps.t('Öğretmen bilgisayarı olmaktan çıkar') : POps.t('Öğretmen bilgisayarı yap'), icon: 'crown', onClick: () => dev.setTeacher(cur.lab, h) } : null,
                    '-',
                    cur.is_quarantined ? { label: POps.t('Karantinayı kaldır'), icon: 'unlock', onClick: () => dev.unquarantine([h]) } : { label: POps.t('Karantinaya al'), icon: 'lock', danger: true, onClick: () => dev.quarantine([h]) },
                    cur.is_quarantined ? { label: POps.t('Çevrimdışı açma kodu'), icon: 'key', onClick: () => dev.bypass(h) } : null,
                    IS_SUPER ? { label: POps.t('Cihazı sil'), icon: 'trash', danger: true, onClick: () => dev.remove(h) } : null
                ]);
            });
        }
        loadActivity(host, body.querySelector('#devRecent'));
        loadFiles(host);
        refreshExam(host, d.lab);
    };
    // Yan menüdeki sayılar (cihaz listesini yoklayan sayfalarda)
    document.addEventListener('pops_data_updated', () => {
        const set = (page, n) => { const el = document.querySelector(`.nav-item [data-count="${page}"]`); if (el) el.textContent = n ? String(n) : ''; };
        set('devices', (state.devices || []).length);
        set('labs', dev.labs().length);
    });
    // Liste yenilenince açık panelin üst kısmı tazelenir (son işlemler korunur)
    document.addEventListener('pops_data_updated', () => {
        if (!openHost || !POps.drawer.isOpen('pc:' + openHost)) return;
        const d = byHost(openHost);
        if (d) { render(POps.drawer.body(), d, true); refreshExam(openHost, d.lab); }
    });
})();
