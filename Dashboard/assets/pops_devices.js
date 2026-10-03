// =================================================================
// POps panel — cihaz işlemleri ve cihaz ayrıntı paneli (Sınıflar, Cihazlar, arama ve diğer sayfalar)
//   POps.dev.state(d)                 : { cls: on|idle|off, word, since }
//   POps.dev.issues(d)                : cihazın sorunları (işaret + açıklama)
//   POps.dev.power(eylem, hostlar)    : wake | restart | shutdown (onay, kapalıları atlar, işlem merkezine düşer)
//   POps.dev.message / move / quarantine / unquarantine / rename / remove
//   POps.dev.open(host)               : sağdaki ayrıntı paneli
// Hedefler her zaman cihaz kimliğidir (hostname = HW- kimliği).
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
        if (s === 'online') return { cls: 'on', word: 'Çevrimiçi', since: '' };
        if (s === 'idle') return { cls: 'idle', word: 'Boşta', since: '' };
        return { cls: 'off', word: 'Kapalı', since: d && (d.last_disconnect_at || d.last_seen) };
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
        if (st.cls === 'off') return 'kapalı';
        return dev.user(d) || (st.cls === 'idle' ? 'boşta' : 'oturum yok');
    };

    // Sorunlar: { kind: err|upd|old|lock, glyph, text }
    dev.issues = function (d, newest) {
        const out = [];
        if (!d) return out;
        if (d.is_quarantined) out.push({ kind: 'lock', icon: 'lock', text: 'Karantinada: kullanıcı ekranı kilitli' });
        const v = dev.version(d);
        const top = newest === undefined ? dev.newestVersion() : newest;
        if (v && top && dev.cmpVersion(v, top) < 0) out.push({ kind: 'old', glyph: '↑', text: `Ajan eski: ${v} (güncel ${top})` });
        const h = d.agent_health || {};
        if (Number(h.loop_errors_1h) > 0) out.push({ kind: 'err', glyph: '!', text: `Ajan son 1 saatte ${Number(h.loop_errors_1h)} hata bildirdi` });
        if (d.cap_terminal_enabled === false) out.push({ kind: 'lock', icon: 'terminal', text: 'Uzak komut bu cihazda kapalı' });
        if (d.cap_vision_enabled === false) out.push({ kind: 'lock', icon: 'eye', text: 'Uzak ekran bu cihazda kapalı' });
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
        return names.slice(0, 5).join(', ') + (names.length > 5 ? ` ve ${names.length - 5} bilgisayar daha` : '');
    };
    const count = (n) => n === 1 ? 'bilgisayar' : `${n} bilgisayar`;

    // ---- Güç
    const POWER = {
        restart: { verb: 'yeniden başlatılsın', btn: 'yeniden başlat', step: 'Yeniden başlat', cmd: 'shutdown /r /f /t 5', icon: 'fa-rotate-right' },
        shutdown: { verb: 'kapatılsın', btn: 'kapat', step: 'Kapat', cmd: 'shutdown /s /f /t 5', icon: 'fa-power-off' }
    };
    dev.power = async function (action, hosts, opts) {
        const o = opts || {};
        hosts = [...new Set(hosts || [])];
        if (!hosts.length) return POps.toast('warning', 'Hedef bilgisayar yok.');
        if (action === 'wake') return dev.wake(hosts, o);
        const p = POWER[action];
        const on = hosts.filter(h => { const d = byHost(h); return d && !POps.isOffline(d); });
        const skipped = hosts.length - on.length;
        if (!on.length) return POps.toast('warning', hosts.length === 1 ? 'Bilgisayar kapalı; komut gönderilmedi.' : 'Seçili bilgisayarların hiçbiri açık değil; komut gönderilmedi.');
        const title = on.length === 1 ? `${dev.name(on[0])} ${p.verb} mı?` : `${on.length} bilgisayar ${p.verb} mı?`;
        const ok = await POps.confirm({
            title,
            message: (on.length > 1 ? namesText(on) + '\n' : '') + '5 saniye içinde uygulanır; kaydedilmemiş işler kaybolabilir.',
            note: skipped ? `Kapalı ${skipped} bilgisayar atlanacak.` : '',
            confirmText: on.length === 1 ? (p.btn.charAt(0).toUpperCase() + p.btn.slice(1)) : `${on.length} bilgisayarı ${p.btn}`,
            danger: true,
            icon: p.icon
        });
        if (!ok) return;
        await POps.act(o.btn, () => POps.post('/api/deploy_orchestration', {
            target_mode: 'PC', targets: on, taskSequence: [{ name: p.step, type: 'CMD', command: p.cmd }],
            title: p.step + (o.scopeLabel ? ' · ' + o.scopeLabel : ''), source: o.source || null
        }, { jobTitle: p.step + ' · ' + (o.scopeLabel || count(on.length)) }), { success: (r) => `Komut ${(r && r.created) || on.length} bilgisayara gönderildi.` });
    };
    dev.wake = async function (hosts, o) {
        const off = hosts.filter(h => { const d = byHost(h); return !d || POps.isOffline(d); });
        if (!off.length) return POps.toast('info', hosts.length === 1 ? 'Bilgisayar zaten açık.' : 'Seçili bilgisayarların hepsi zaten açık.');
        if (o.lab && off.length === hosts.length && o.wholeLab) {
            return POps.act(o.btn, () => POps.post('/api/wake_lab/' + encodeURIComponent(o.lab)), { success: (r) => `${o.lab}: ${(r && r.woken_pcs) || 0} bilgisayara uyandırma sinyali gönderildi.` });
        }
        await POps.busy(o.btn, async () => {
            const res = await Promise.allSettled(off.map(h => POps.post('/api/wake_pc/' + encodeURIComponent(h))));
            const okN = res.filter(r => r.status === 'fulfilled').length;
            const bad = off.filter((h, i) => res[i].status === 'rejected');
            if (okN) POps.toast('success', `${okN} bilgisayara uyandırma sinyali gönderildi.`);
            if (bad.length) POps.toast('warning', `${bad.length} bilgisayar uyandırılamadı (MAC adresi bilinmiyor olabilir): ${namesText(bad)}`);
        });
    };

    // ---- Mesaj: Windows'un msg komutuyla oturumdaki kullanıcılara kısa not
    dev.message = async function (hosts, o) {
        o = o || {};
        const on = hosts.filter(h => { const d = byHost(h); return d && !POps.isOffline(d); });
        if (!on.length) return POps.toast('warning', 'Açık bilgisayar yok; mesaj gönderilmedi.');
        const text = await POps.prompt({
            title: on.length === 1 ? `${dev.name(on[0])} ekranına mesaj` : `${on.length} bilgisayara mesaj`,
            message: 'Oturumdaki kullanıcının ekranında 2 dakika görünür.',
            label: 'Mesaj', multiline: true, maxLength: 250, confirmText: 'Gönder', icon: 'fa-message',
            note: hosts.length > on.length ? `Kapalı ${hosts.length - on.length} bilgisayar atlanacak.` : ''
        });
        if (text === null) return;
        const clean = text.replace(/[\r\n\t]+/g, ' ').replace(/["%^]/g, "'").replace(/[\u0000-\u001f]/g, '').trim();
        if (!clean) return;
        await POps.act(o.btn, () => POps.post('/api/deploy_orchestration', {
            target_mode: 'PC', targets: on, taskSequence: [{ name: 'Mesaj', type: 'CMD', command: `msg * /TIME:120 "${clean}"` }],
            title: 'Mesaj', source: o.source || null
        }, { jobTitle: 'Mesaj · ' + (o.scopeLabel || count(on.length)) }), { success: `Mesaj ${on.length} bilgisayara gönderildi.` });
    };

    // ---- Taşı
    dev.labs = function () {
        return [...new Set([...(state.customLabs || []), ...(state.devices || []).map(d => d.lab)])].filter(l => l && l !== UNASSIGNED).sort((a, b) => a.localeCompare(b, 'tr'));
    };
    dev.moveTo = async function (hosts, lab, o) {
        o = o || {};
        if (await POps.act(o.btn, () => POps.post('/api/move_pcs', { pc_names: hosts, new_lab: lab }), {
            success: lab === UNASSIGNED ? `${count(hosts.length)} sınıftan çıkarıldı.` : `${count(hosts.length)} ${lab} sınıfına taşındı.`
        })) { POps.loadDevices().catch(() => {}); return true; }
        return false;
    };
    dev.moveMenu = function (anchor, hosts, o) {
        o = o || {};
        const current = o.currentLab;
        const items = [{ header: hosts.length === 1 ? `${dev.name(hosts[0])} nereye taşınsın?` : `${hosts.length} bilgisayar nereye taşınsın?` }];
        dev.labs().filter(l => l !== current).forEach(l => items.push({ label: l, icon: 'labs', onClick: () => dev.moveTo(hosts, l, o) }));
        items.push('-', { label: 'Yeni sınıf…', icon: 'plus', onClick: async () => {
            const name = await POps.prompt({ title: 'Yeni sınıf', label: 'Sınıf adı', maxLength: 60, confirmText: 'Oluştur ve taşı' });
            if (!name || !name.trim()) return;
            try { await POps.post('/api/create_lab', { lab_name: name.trim() }); } catch (e) { return POps.toast('error', POps.errorMessage(e)); }
            dev.moveTo(hosts, name.trim(), o);
        } });
        if (current && current !== UNASSIGNED) items.push({ label: 'Sınıftan çıkar', icon: 'x', onClick: () => dev.moveTo(hosts, UNASSIGNED, o) });
        POps.menu(anchor, items);
    };

    // ---- Karantina
    dev.quarantine = async function (hosts, o) {
        o = o || {};
        const reason = await POps.prompt({
            title: hosts.length === 1 ? `${dev.name(hosts[0])} karantinaya alınsın mı?` : `${hosts.length} bilgisayar karantinaya alınsın mı?`,
            message: 'Kullanıcının ekranı kilitlenir; kaldırana kadar bilgisayar kullanılamaz. Kapalı bilgisayar açılınca kilitlenir.',
            label: 'Gerekçe', placeholder: 'Örn. sınav sırasında yetkisiz kullanım', maxLength: 300,
            confirmText: hosts.length === 1 ? 'Karantinaya al' : `${hosts.length} bilgisayarı karantinaya al`, danger: true, icon: 'fa-shield-halved'
        });
        if (reason === null) return;
        await POps.busy(o.btn, async () => {
            const res = await Promise.allSettled(hosts.map(h => POps.post('/api/security/lockdown', { target_pc: h, reason: reason.trim() })));
            const okN = res.filter(r => r.status === 'fulfilled').length;
            const failed = res.map((r, i) => r.status === 'rejected' ? `${dev.name(hosts[i])}: ${POps.errorMessage(r.reason)}` : null).filter(Boolean);
            if (okN) POps.toast('success', `${count(okN)} karantinaya alındı.`);
            if (failed.length) POps.toast('error', failed.slice(0, 3).join('\n'));
        });
        POps.loadDevices().catch(() => {});
    };
    dev.unquarantine = async function (hosts, o) {
        o = o || {};
        const reason = await POps.prompt({
            title: hosts.length === 1 ? `${dev.name(hosts[0])} karantinadan çıkarılsın mı?` : `${hosts.length} bilgisayar karantinadan çıkarılsın mı?`,
            label: 'Gerekçe (isteğe bağlı)', maxLength: 300, required: false, confirmText: 'Karantinayı kaldır', icon: 'fa-unlock'
        });
        if (reason === null) return;
        await POps.busy(o.btn, async () => {
            const res = await Promise.allSettled(hosts.map(h => POps.post('/api/security/unlock', { target_pc: h, reason: reason.trim() })));
            const okN = res.filter(r => r.status === 'fulfilled').length;
            if (okN) POps.toast('success', `${count(okN)} karantinadan çıkarıldı.`);
            const bad = res.length - okN;
            if (bad) POps.toast('error', `${bad} bilgisayarda kaldırılamadı.`);
        });
        POps.loadDevices().catch(() => {});
    };

    dev.rename = async function (host) {
        const d = byHost(host) || { hostname: host };
        const name = await POps.prompt({ title: 'Bilgisayarın adını değiştir', message: 'Panelde bu adla görünür. Boş bırakırsanız bilgisayarın kendi adı kullanılır.', label: 'Görünen ad', defaultValue: POps.deviceName(d), maxLength: 100, required: false, confirmText: 'Kaydet' });
        if (name === null) return;
        if (await POps.act(null, () => POps.post('/api/rename_device', { pc_name: host, display_name: name.trim() }), { success: 'Ad güncellendi.' })) POps.loadDevices().catch(() => {});
    };
    dev.remove = async function (host) {
        const ok = await POps.confirm({ title: `${dev.name(host)} silinsin mi?`, message: 'Cihaz kaydı, envanteri ve bekleyen görevleri silinir. Ajan çalışıyorsa bir sonraki bağlantıda yeniden kayıt ister.', confirmText: 'Cihazı sil', danger: true, icon: 'fa-trash' });
        if (!ok) return;
        if (await POps.act(null, () => POps.del('/api/devices/' + encodeURIComponent(host)), { success: 'Cihaz silindi.' })) {
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
        await POps.alert({
            title: 'Çevrimdışı açma kodu', icon: 'fa-key', codes,
            message: `${dev.name(host)} · geçerli: ${data.valid_for}` + (data.n ? ` · bugünün ${data.n + 1}. kodu` : ''),
            note: (data.fallback_token ? 'İlk kod kabul edilmezse ikincisini deneyin (cihaz anahtarı henüz onaylanmadı). ' : '')
                + 'Kullanıcı kodu tepsi simgesi → "Yönetici Müdahalesi (Bypass)" menüsüne girer. Her kod bir kez geçerlidir.'
        });
    };
    dev.setTeacher = async function (lab, host) {
        const removing = (state.mainPcs || {})[lab] === host;   // aynı bilgisayar gönderilince sunucu kaldırır
        if (await POps.act(null, () => POps.post('/api/set_main_pc', { lab_name: lab, pc_name: host }), { success: removing ? `${dev.name(host)} artık öğretmen bilgisayarı değil.` : `${dev.name(host)} öğretmen bilgisayarı yapıldı.` })) POps.loadDevices().catch(() => {});
    };
    dev.screenUrl = (hosts) => 'vision?pc=' + hosts.map(encodeURIComponent).join(',');
    dev.commandUrl = (hosts) => 'terminal?pc=' + hosts.map(encodeURIComponent).join(',');

    // ---- Görev kaydının okunur hali (Son işlemler, Kayıtlar, İşlemler)
    const SOURCE_TEXT = { labs: 'Sınıflar', devices: 'Cihazlar', terminal: 'Uzak komut', deploy: 'Dağıtım', tasks: 'İşlemler', vision: 'Uzak ekran', schedule: 'Zamanlanmış', index: 'Kontrol merkezi', system: 'Sistem' };
    dev.sourceText = (s) => SOURCE_TEXT[s] || s || '';
    const STATUS_WORD = {
        Pending: 'Sırada', Running: 'Çalışıyor', Paused: 'Duraklatıldı', Completed: 'Tamamlandı', 'Completed (Rebooted)': 'Tamamlandı',
        Failed: 'Başarısız', Error: 'Hata', Cancelled: 'İptal edildi', Interrupted: 'Yarıda kaldı', 'Timed Out': 'Zaman aşımı',
        Denied: 'Reddedildi', Unknown: 'Bilinmiyor', Expired: 'Süresi doldu'
    };
    dev.statusWord = (s) => STATUS_WORD[s] || s || '—';
    // Bilinen komutları okunur ada çevirir (eski kayıtlarda başlık yok)
    dev.taskTitle = function (t) {
        const c = String(t.command || t.script_path || '');
        // Uzak komut sayfasından yazılan serbest komut: adı "Komut" (eskiden "Terminal"), komutun kendisi gösterilir
        if (t.title === 'Komut' || t.title === 'Terminal') return 'Komut: ' + (c.length > 50 ? c.slice(0, 47) + '…' : c);
        if (t.title) return String(t.title);
        if (/^shutdown \/r/i.test(c)) return 'Yeniden başlat';
        if (/^shutdown \/s/i.test(c)) return 'Kapat';
        if (/^msg \*/i.test(c)) return 'Mesaj';
        if (/^POPS_UPDATE_AGENT/i.test(c) || /agent.?update/i.test(c)) return 'Ajan güncellemesi';
        return c.length > 60 ? c.slice(0, 57) + '…' : (c || 'Görev');
    };
    // Reddedildi / başarısız için açık neden
    dev.failReason = function (t) {
        const s = t.status, x = t.exit_code;
        if (s === 'Denied') return x === -5 ? 'Cihazdaki ajan bu komutu yetki politikası gereği çalıştırmadı (uzak komut bu cihazda kapalı olabilir).' : 'Cihaz komutu reddetti.';
        if (s === 'Timed Out') return 'Komut süre sınırını aştı ve durduruldu.';
        if (s === 'Expired') return 'Cihaz zamanında bağlanmadığı için görev başlatılmadı.';
        if (s === 'Interrupted') return 'Komut çalışırken bağlantı koptu ya da cihaz yeniden başladı.';
        if (s === 'Cancelled') return 'İşlem iptal edildi.';
        if ((s === 'Failed' || s === 'Error') && x != null) return `Komut ${x} çıkış koduyla bitti.`;
        return '';
    };

    // ---- Olay sözlüğü (Kayıtlar ve Kontrol merkezi)
    // Kayıtlar agent_logs_v2'den gelir: event_type / action ham anahtardır; burada okunur cümleye çevrilir.
    // Önem dürüst: oturum açma/kapama ve yönetici işlemleri "Bilgi"; yalnızca gerçek sorunlar uyarı ya da kritik.
    const SEV_WORD = { info: 'Bilgi', warn: 'Uyarı', bad: 'Kritik' };
    const KIND_LABEL = { auth: 'Oturumlar', policy: 'Kural ihlalleri', quarantine: 'Karantina', command: 'Komutlar', agent: 'Ajan ve bakım', other: 'Diğer' };
    const CAT_LABEL = { security: 'Güvenlik', restricted_content: 'Kural ihlali', system_maintenance: 'Bakım', legacy: 'Eski kayıt' };
    const RISK_LABEL = { info: 'bilgi', low: 'düşük', medium: 'orta', high: 'yüksek', critical: 'kritik' };
    const CAP = { terminal: 'uzak komut', vision: 'uzak ekran' };
    const BY_TYPE = {
        'auth.login': 'login', 'auth.logout': 'logout', 'auth.failed': 'login_failed', 'policy.alert': 'dns_block',
        'security.lockdown': 'lockdown', 'security.unlock': 'unlock', 'security.bypass_code': 'bypass_code',
        'deploy.execution': 'execute_queue', 'agent.update': 'update_problem', 'agent.capability_denied': 'capability_denied',
        'agent.enroll_denied': 'enroll_denied', 'agent.auto_quarantine': 'auto_quarantine', 'agent.unlock_failed': 'unlock_failed',
        'agent.offline_bypass': 'offline_bypass'
    };
    const commandOf = (r, m) => String(m.raw_command || String(r.message || '').replace(/^G(ö|o)rev:\s*/i, '')).trim();
    const EVENTS = {
        login: { title: () => 'Oturum açıldı', icon: 'user', kind: 'auth', sev: 'info' },
        logout: { title: () => 'Oturum kapatıldı', icon: 'logout', kind: 'auth', sev: 'info' },
        login_failed: { title: () => 'Hatalı giriş denemesi', icon: 'user', kind: 'auth' },
        dns_block: { title: (r, m) => m.domain ? 'Yasaklı siteye erişim: ' + m.domain : 'Yasaklı siteye erişim', icon: 'shield', kind: 'policy', quietReason: true },
        lockdown: { title: () => 'Karantinaya alındı', icon: 'lock', kind: 'quarantine', sev: 'warn', admin: true },
        unlock: { title: () => 'Karantina kaldırıldı', icon: 'unlock', kind: 'quarantine', sev: 'info', admin: true },
        auto_quarantine: { title: () => 'Kural ihlali eşiğinde kendini karantinaya aldı', icon: 'lock', kind: 'quarantine' },
        unlock_failed: { title: () => 'Karantina kaldırılamadı, ağ yalıtımı sürüyor', icon: 'lock', kind: 'quarantine' },
        offline_bypass: { title: () => 'Çevrimdışı açma koduyla karantinadan çıktı', icon: 'key', kind: 'quarantine' },
        bypass_code: { title: () => 'Çevrimdışı açma kodu üretildi', icon: 'key', kind: 'quarantine', sev: 'info' },
        execute_queue: { title: (r, m) => { const c = commandOf(r, m); return c ? 'Komut gönderildi: ' + dev.taskTitle({ command: c }) : 'Komut gönderildi'; }, icon: 'terminal', kind: 'command', sev: 'info' },
        update_problem: { title: () => 'Ajan güncellemesi sorunlu bitti', icon: 'refresh', kind: 'agent' },
        capability_denied: { title: (r) => CAP[r.reason] ? `Kapalı ${CAP[r.reason]} istendi, ajan reddetti` : 'Kapalı bir özellik istendi, ajan reddetti', icon: 'eye', kind: 'agent', quietReason: true },
        enroll_denied: { title: () => 'Kayıtlı bilgisayarın anahtarı yeniden istendi, reddedildi', icon: 'alert', kind: 'other' }
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
        if (!a || a === 'Agent' || a === r.pc_name) return 'Ajan';
        if (/^system/i.test(a)) return 'Sistem';
        return a;
    }
    function sourceOf(r) {
        const t = String(r.event_type || '');
        if (/^security\./.test(t)) return 'Panel';
        if (/^deploy\./.test(t)) return 'Görev kuyruğu';
        if (/^(agent|auth|policy)\./.test(t)) return 'Bilgisayardaki ajan';
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
            if (key === 'enroll_denied' && reason === 'already_enrolled') why = 'Bilgisayar zaten kayıtlı; yeni anahtar verilmedi.';
            else why = def && def.admin ? 'Gerekçe: ' + reason : cap1(reason);
        }
        const title = def ? def.title(r, m) : (cleanMsg(r.message).slice(0, 160) || r.event_type || 'Olay');
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
        const sub = st.cls === 'off' ? `Kapalı${st.since ? ' · ' + POps.timeHtml(st.since) : ''}` : escapeHtml(st.word) + (dev.user(d) ? ' · ' + escapeHtml(dev.user(d)) : '');
        return `<div class="drawer-head">
            <div class="drawer-title">
                <span class="drawer-ico ${escapeHtml(st.cls)}">${POps.iconHtml('monitor', 'lg')}</span>
                <div style="min-width:0"><h2>${escapeHtml(POps.deviceName(d))}</h2><div class="sub"><span class="dot ${escapeHtml(st.cls)}"></span>${sub}</div></div>
            </div>
            <button type="button" class="ibtn sm" data-act="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button>
        </div>`;
    }
    function circlesHtml(d) {
        if (!CAN_ADMIN) return '';
        const off = POps.isOffline(d);
        const visionOff = d.cap_vision_enabled === false || off;
        const termOff = d.cap_terminal_enabled === false || off;
        return `<div class="circs">
            <button type="button" class="circ" data-act="screen" ${visionOff ? 'disabled' : ''}><span>${POps.iconHtml('eye')}</span>Ekran</button>
            <button type="button" class="circ" data-act="command" ${termOff ? 'disabled' : ''}><span>${POps.iconHtml('terminal')}</span>Komut</button>
            <button type="button" class="circ" data-act="power" aria-haspopup="menu"><span>${POps.iconHtml('power')}</span>Güç</button>
            <button type="button" class="circ" data-act="more" aria-haspopup="menu"><span>${POps.iconHtml('more')}</span>Diğer</button>
        </div>`;
    }
    function factsHtml(d) {
        const off = POps.isOffline(d);
        // [etiket, düz metin değer, eşaralıklı mı]; boş değerli satır gösterilmez
        const facts = [
            ['Kullanıcı', dev.user(d)], ['Uygulama', dev.app(d)], ['Sınıf', d.lab && d.lab !== UNASSIGNED ? d.lab : 'Atanmamış'],
            ['IP', d.ip, true], ['MAC', d.mac, true], ['Ajan', dev.version(d)], ['Bellek', d.cap_ram_readable],
            ['Son görülme', off ? null : 'şimdi'], ['Son kopuş', off ? d.last_disconnect_reason : null], ['Kimlik', d.hostname, true]
        ];
        const seenHtml = off ? `<div class="grow"><span>Son görülme</span><span>${POps.timeHtml(d.last_seen)}</span></div>` : '';
        const rowsHtml = facts.filter(f => f[1]).map(f => `<div class="grow"><span>${escapeHtml(f[0])}</span><span${f[2] ? ' class="mono"' : ''}>${escapeHtml(f[1])}</span></div>`).join('');
        return `<div class="glist">${rowsHtml}${seenHtml}</div>`;
    }
    function issuesHtml(d) {
        return dev.issues(d).map(i => `<div class="issue ${escapeHtml(i.kind)}">${i.icon ? POps.iconHtml(i.icon, 'sm') : `<span class="mark ${escapeHtml(i.kind)}">${escapeHtml(i.glyph)}</span>`}<span style="flex:1">${escapeHtml(i.text)}</span></div>`).join('');
    }
    function activityRowHtml(a) {
        if (a.kind === 'vision') {
            const dur = a.ended_at && a.at ? POps.duration((POps.toDate(a.ended_at) - POps.toDate(a.at)) / 1000) : '';
            return `<div class="act"><div class="res">${POps.iconHtml('eye')}</div><div style="min-width:0"><div class="what">Uzak ekran${a.mandatory ? ' (zorunlu)' : ''}</div>
                <div class="meta">${escapeHtml(a.by || '?')} · ${POps.timeHtml(a.at)}${dur ? ' · ' + escapeHtml(dur) : ''}${a.reason ? ' · ' + escapeHtml(a.reason) : ''}</div></div>
                <div class="side"><span class="word">${a.ended_at ? 'Bitti' : 'Sürüyor'}</span></div></div>`;
        }
        const k = POps.taskState(a.status);
        const why = k === 'bad' ? dev.failReason(a) : '';
        const metaHtml = escapeHtml(a.by || '?') + ' · ' + POps.timeHtml(a.at) + (a.source ? ' · ' + escapeHtml(dev.sourceText(a.source)) : '');
        return `<div class="act"><div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div><div style="min-width:0"><div class="what">${escapeHtml(dev.taskTitle(a))}</div>
            <div class="meta">${metaHtml}${a.reason ? ' · gerekçe: ' + escapeHtml(a.reason) : ''}</div>
            ${why ? `<div class="why">${escapeHtml(why)}</div>` : ''}</div>
            <div class="side"><span class="word ${escapeHtml(k)}">${escapeHtml(dev.statusWord(a.status))}</span></div></div>`;
    }
    async function loadActivity(host, box) {
        try {
            const r = await POps.get('/api/devices/' + encodeURIComponent(host) + '/activity?limit=8');
            if (openHost !== host) return;
            const items = r.items || [];
            box.innerHTML = items.length ? items.map(activityRowHtml).join('') : '<div class="faint" style="font-size:var(--text-sm);padding:6px 0">Bu cihazda henüz işlem yok.</div>';
        } catch (e) {
            if (openHost === host) box.textContent = 'Son işlemler alınamadı: ' + POps.errorMessage(e);
        }
    }
    function render(body, d, keepRecent) {
        const recent = keepRecent ? body.querySelector('#devRecent') : null;
        body.innerHTML = headHtml(d) + circlesHtml(d) + issuesHtml(d) + factsHtml(d)
            + '<div><h3>Son işlemler</h3><div id="devRecent"><div class="faint" style="font-size:var(--text-sm);padding:6px 0">Yükleniyor…</div></div></div>'
            + `<a href="devices?pc=${encodeURIComponent(d.hostname)}" style="font-size:var(--text-sm)">Cihazlar sayfasında aç</a>`;
        if (recent) body.querySelector('#devRecent').replaceWith(recent);
    }
    dev.open = async function (host, opts) {
        const o = opts || {};
        let d = byHost(host);
        if (!d) {
            try { await POps.loadDevices(); } catch (e) { return POps.toast('error', POps.errorMessage(e)); }
            d = byHost(host);
            if (!d) return POps.toast('warning', 'Cihaz bulunamadı.');
        }
        // Panel başka bir bilgisayar açıkken açılırsa önce onun kapanışı çalışır; yeni bilgisayar ondan sonra
        // işaretlenir (eskiden kapanış yenisini de siliyordu ve "Son işlemler" yüklenmiyordu)
        const body = POps.drawer.open('pc:' + host, { onClose: () => { if (openHost === host) openHost = null; if (o.onClose) o.onClose(); } });
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
                    { label: 'Uyandır', icon: 'zap', disabled: !POps.isOffline(cur), onClick: () => dev.power('wake', [h], { source: o.source }) },
                    { label: 'Yeniden başlat', icon: 'restart', disabled: POps.isOffline(cur), onClick: () => dev.power('restart', [h], { source: o.source }) },
                    { label: 'Kapat', icon: 'power', danger: true, disabled: POps.isOffline(cur), onClick: () => dev.power('shutdown', [h], { source: o.source }) }
                ]);
                else if (act === 'more') POps.menu(b, [
                    { label: 'Mesaj gönder', icon: 'message', disabled: POps.isOffline(cur), onClick: () => dev.message([h], { source: o.source }) },
                    { label: 'Yeniden adlandır', icon: 'edit', onClick: () => dev.rename(h) },
                    { label: 'Başka sınıfa taşı…', icon: 'move', onClick: () => dev.moveMenu(b, [h], { currentLab: cur.lab }) },
                    cur.lab && cur.lab !== UNASSIGNED ? { label: (state.mainPcs || {})[cur.lab] === h ? 'Öğretmen bilgisayarı olmaktan çıkar' : 'Öğretmen bilgisayarı yap', icon: 'crown', onClick: () => dev.setTeacher(cur.lab, h) } : null,
                    '-',
                    cur.is_quarantined ? { label: 'Karantinayı kaldır', icon: 'unlock', onClick: () => dev.unquarantine([h]) } : { label: 'Karantinaya al', icon: 'lock', danger: true, onClick: () => dev.quarantine([h]) },
                    cur.is_quarantined ? { label: 'Çevrimdışı açma kodu', icon: 'key', onClick: () => dev.bypass(h) } : null,
                    IS_SUPER ? { label: 'Cihazı sil', icon: 'trash', danger: true, onClick: () => dev.remove(h) } : null
                ]);
            });
        }
        loadActivity(host, body.querySelector('#devRecent'));
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
        if (d) render(POps.drawer.body(), d, true);
    });
})();
