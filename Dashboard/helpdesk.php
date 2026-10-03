<?php include 'includes/header.php'; ?>

<style>
    .faint { color: var(--text-muted); }
    .hd-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
    .hd-bar .grow { flex: 1; }
    .hd-bar .search-field { flex: 0 1 320px; min-width: 200px; }
    .segmented .n { color: var(--text-muted); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }
    .hd-list { padding: 4px 12px; }
    .hd-list .act { padding-left: 8px; padding-right: 8px; }
    .hd-list .act.is-focus { background: var(--primary-50); }
    .hd-list .act .what .mark { margin-left: 6px; vertical-align: -3px; }
    .hd-list .act .side .when { font-variant-numeric: tabular-nums; }

    /* Ayrıntı paneli */
    .tk-main { display: flex; flex-direction: column; gap: 18px; }
    .tk-main .glist .grow > span:last-child { display: inline-flex; align-items: center; justify-content: flex-end; gap: 6px; flex-wrap: wrap; }
    .thread { display: flex; flex-direction: column; gap: 10px; margin-top: 8px; }
    .msg { border-radius: 12px; padding: 10px 12px; background: var(--bg-app); font-size: var(--text-sm); }
    .msg.user { background: var(--bg-surface); box-shadow: inset 0 0 0 1px var(--border-subtle); }
    .msg.note { background: transparent; border: 1px dashed var(--border-default); }
    .msg .mh { display: flex; gap: 6px; align-items: baseline; flex-wrap: wrap; font-size: var(--text-xs); color: var(--text-muted); margin-bottom: 4px; }
    .msg .mh b { color: var(--text-primary); font-weight: var(--fw-semibold); }
    .msg .mh .t { margin-left: auto; white-space: nowrap; }
    .msg .mb { white-space: pre-wrap; overflow-wrap: anywhere; color: var(--text-primary); line-height: 1.5; }
    .ev { display: flex; gap: 8px; align-items: center; font-size: var(--text-xs); color: var(--text-muted); padding: 0 4px; }
    .ev .dot { width: 5px; height: 5px; }
    .ev .tx { min-width: 0; overflow-wrap: anywhere; }
    .ev .t { margin-left: auto; white-space: nowrap; padding-left: 8px; }
    .tk-reply { position: sticky; bottom: -20px; margin: 0 -20px -20px; padding: 12px 20px 20px; background: var(--bg-surface); border-top: 1px solid var(--border-subtle); display: flex; flex-direction: column; gap: 8px; }
    .tk-reply textarea { min-height: 76px; }
    .tk-reply .row { display: flex; align-items: center; justify-content: space-between; gap: 10px; flex-wrap: wrap; }
    .tk-reply .check { font-size: var(--text-xs); }

    @media (max-width: 640px) {
        .hd-bar .search-field { flex-basis: 100%; }
        .hd-list { padding: 2px 6px; }
        .hd-list .act .side .word { white-space: normal; text-align: right; max-width: 92px; }
    }
</style>

<div class="page-header">
    <div>
        <h1>Destek talepleri</h1>
        <div class="summary" id="hdSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="btn" id="newBtn"><?php echo pops_icon('plus', 'sm'); ?>Yeni talep</button>
    </div>
</div>

<div class="hd-bar">
    <div class="segmented" id="hdFilter" role="group" aria-label="Duruma göre süz"></div>
    <span class="grow"></span>
    <div class="search-field">
        <i class="fas fa-search" aria-hidden="true"></i>
        <input type="search" id="hdSearch" placeholder="Konu, açıklama, bildiren ya da bilgisayar" aria-label="Talep ara">
    </div>
</div>

<div class="card hd-list" id="hdList"><div class="loading-state" role="status"><span class="spinner"></span>Talepler yükleniyor…</div></div>

<div class="modal-overlay" id="newModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title">Yeni talep</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <div class="field" id="nfSubjectField">
                <label for="nfSubject">Konu</label>
                <input type="text" id="nfSubject" maxlength="200" placeholder="Örn. Lab-1 yazıcısı çıktı vermiyor" autocomplete="off">
                <div class="field-error" id="nfSubjectErr">Konu en az 3 karakter olmalı.</div>
            </div>
            <div class="form-grid" style="margin-bottom:var(--space-4)">
                <div class="field"><label for="nfCategory">Kategori</label><select id="nfCategory"></select></div>
                <div class="field"><label for="nfPriority">Öncelik</label>
                    <select id="nfPriority"><option value="normal">Normal</option><option value="high">Yüksek</option><option value="low">Düşük</option></select></div>
            </div>
            <div class="field">
                <label for="nfPc">Bilgisayar <span class="field-hint" style="display:inline">(isteğe bağlı)</span></label>
                <select id="nfPc"><option value="">Bilgisayar yok</option></select>
            </div>
            <div class="field">
                <label for="nfReporter">Bildiren</label>
                <input type="text" id="nfReporter" maxlength="100" autocomplete="off">
            </div>
            <div class="field">
                <label for="nfBody">Açıklama</label>
                <textarea id="nfBody" rows="4" maxlength="5000" placeholder="Ne oldu, ne zamandan beri, ne denendi?"></textarea>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="nfSave">Talebi aç</button>
        </div>
    </div>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const dev = POps.dev;
    const ME = <?= json_encode($_SESSION['username'] ?? '', JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT) ?>;
    const $ = (id) => document.getElementById(id);
    const params = new URLSearchParams(location.search);
    const CAT = { donanim: 'Donanım', yazilim: 'Yazılım', ag: 'Ağ / İnternet', yazici: 'Yazıcı', hesap: 'Hesap / şifre', diger: 'Diğer' };
    // Durum: kelime, renk (.word/.dot/.res) ve simge
    const ST = {
        open: { w: 'Açık', c: 'warn', i: 'message' },
        in_progress: { w: 'Üzerinde çalışılıyor', c: 'run', i: 'clock' },
        waiting: { w: 'Yanıt bekleniyor', c: '', i: 'send' },
        resolved: { w: 'Çözüldü', c: 'ok', i: 'check' },
        closed: { w: 'Kapatıldı', c: '', i: 'check' }
    };
    const PR = { high: 'Yüksek', normal: 'Normal', low: 'Düşük' };
    const st = (s) => ST[s] || { w: s || '—', c: '', i: 'message' };
    const ACTIVE = ['open', 'in_progress', 'waiting'];
    const ui = { f: params.get('f') === 'all' ? 'all' : params.get('f') === 'closed' ? 'closed' : 'open', q: '', items: [], counts: {}, loaded: false, focus: null, detail: null, detailSig: '', seq: 0, sig: '' };
    const list = $('hdList');

    const devName = (t) => t.display_name || t.hostname || t.pc_name || '';
    const labText = (t) => (t.lab_name && t.lab_name !== dev.UNASSIGNED ? t.lab_name : '');

    // ================= Liste =================
    function rowHtml(t) {
        const s = st(t.status);
        const where = devName(t) ? devName(t) + (labText(t) ? ' · ' + labText(t) : '') : '';
        const meta = [t.reporter || (t.source === 'agent' ? 'tepsiden' : 'panelden'), where, CAT[t.category] || t.category, t.assignee ? 'atanan: ' + t.assignee : '', Number(t.message_count) ? t.message_count + ' mesaj' : '', '#' + t.id].filter(Boolean).join(' · ');
        const markHtml = t.priority === 'high' ? dev.markHtml({ kind: 'err', glyph: '!', text: 'Yüksek öncelik' }) : '';
        return `<div class="act clickable${ui.focus === t.id ? ' is-focus' : ''}" data-id="${Number(t.id)}" role="button" tabindex="0">
            <div class="res ${escapeHtml(s.c)}">${POps.iconHtml(s.i)}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(t.subject)}${markHtml}</div><div class="meta">${escapeHtml(meta)}</div></div>
            <div class="side"><span class="word ${escapeHtml(s.c)}">${escapeHtml(s.w)}</span><span class="when">${POps.timeHtml(t.updated_at)}</span></div>
        </div>`;
    }
    function renderHead() {
        const c = ui.counts;
        const n = (k) => Number(c[k] || 0);
        const active = n('open') + n('in_progress') + n('waiting');
        const closed = n('resolved') + n('closed');
        $('hdSummary').innerHTML = `<span class="sum"><span class="dot warn"></span><b>${n('open')}</b> açık</span>`
            + (n('in_progress') ? `<span class="sum"><span class="dot run"></span><b>${n('in_progress')}</b> üzerinde çalışılıyor</span>` : '')
            + (n('waiting') ? `<span class="sum"><span class="dot"></span><b>${n('waiting')}</b> yanıt bekleniyor</span>` : '')
            + `<span class="sum"><span class="dot ok"></span><b>${Number(closed)}</b> kapalı</span>`;
        const btnHtml = (k, label, num) => `<button type="button" data-f="${escapeHtml(k)}" class="${ui.f === k ? 'active' : ''}" aria-pressed="${ui.f === k ? 'true' : 'false'}">${escapeHtml(label)} <span class="n">${Number(num)}</span></button>`;
        $('hdFilter').innerHTML = btnHtml('open', 'Açık', active) + btnHtml('all', 'Tümü', active + closed) + btnHtml('closed', 'Kapalı', closed);
    }
    function renderList(force) {
        if (!ui.loaded) return;
        renderHead();
        const sig = JSON.stringify([ui.focus, ui.items]);
        if (!force && sig === ui.sig) return;
        ui.sig = sig;
        if (!ui.items.length) {
            if (ui.q) POps.setEmpty(list, { icon: 'fa-filter', title: 'Süzgece uyan talep yok', text: 'Aramayı ya da süzgeci değiştirin.' });
            else if (ui.f === 'open') POps.setEmpty(list, { icon: 'fa-check', kind: 'success', title: 'Açık talep yok', text: 'Öğrenciler tepsi simgesinden, siz buradan talep açabilirsiniz.' });
            else POps.setEmpty(list, { icon: 'fa-inbox', title: ui.f === 'closed' ? 'Kapalı talep yok' : 'Henüz talep yok', text: 'Öğrenciler tepsi simgesinden, siz buradan talep açabilirsiniz.' });
            return;
        }
        list.innerHTML = ui.items.map(rowHtml).join('');
    }
    async function load() {
        const seq = ++ui.seq;
        const q = encodeURIComponent(ui.q);
        let d;
        try {
            if (ui.f === 'closed') {
                const [a, b] = await Promise.all(['resolved', 'closed'].map(s => POps.get(`/api/tickets?status=${s}&q=${q}`)));
                d = { items: (a.items || []).concat(b.items || []).sort((x, y) => String(y.updated_at).localeCompare(String(x.updated_at))), counts: b.counts || a.counts };
            } else {
                d = await POps.get(`/api/tickets?status=${ui.f === 'open' ? 'active' : 'all'}&q=${q}`);
            }
        } catch (e) {
            if (seq !== ui.seq) return;
            if (!ui.loaded) POps.setError(list, e);
            throw e;
        }
        if (seq !== ui.seq) return;
        ui.items = d.items || [];
        ui.counts = d.counts || {};
        ui.loaded = true;
        renderList(false);
    }

    // ================= Ayrıntı paneli =================
    const isChangeNote = (m) => m.internal && /^(durum|öncelik|atanan): /.test(m.body || '') && String(m.body).split('; ').every(p => /^(durum|öncelik|atanan): /.test(p));
    const changeText = (body) => String(body).split('; ').map(p => p.replace(/^öncelik: (high|normal|low)$/, (x, v) => 'öncelik: ' + PR[v].toLocaleLowerCase('tr'))).join(', ');
    function threadHtml(t) {
        const src = t.source === 'agent' ? 'tepsiden' : 'panelden';
        const openHtml = `<div class="ev"><span class="dot"></span><span class="tx">${escapeHtml((t.reporter || 'Bilinmeyen kullanıcı') + ' talebi ' + src + ' açtı')}</span><span class="t">${POps.timeHtml(t.created_at)}</span></div>`;
        const bodyHtml = t.body ? `<div class="msg user"><div class="mh"><b>${escapeHtml(t.reporter || 'Bildiren')}</b><span>açıklama</span><span class="t">${POps.timeHtml(t.created_at)}</span></div><div class="mb">${escapeHtml(t.body)}</div></div>` : '';
        const msgHtml = (m) => isChangeNote(m)
            ? `<div class="ev"><span class="dot"></span><span class="tx">${escapeHtml(m.author + ' · ' + changeText(m.body))}</span><span class="t">${POps.timeHtml(m.created_at)}</span></div>`
            : `<div class="msg ${m.internal ? 'note' : 'reply'}"><div class="mh"><b>${escapeHtml(m.author)}</b><span>${m.internal ? 'iç not' : (t.source === 'agent' ? 'kullanıcıya yanıt' : 'yanıt')}</span><span class="t">${POps.timeHtml(m.created_at)}</span></div><div class="mb">${escapeHtml(m.body)}</div></div>`;
        return `<div><h3>Yazışma</h3><div class="thread">${openHtml}${bodyHtml}${(t.messages || []).map(msgHtml).join('')}</div></div>`;
    }
    function mainHtml(t) {
        const s = st(t.status);
        const ico = t.status === 'open' ? 'idle' : t.status === 'resolved' ? 'on' : '';
        const next = t.status === 'open' ? ['take', 'play', 'Üzerine al'] : ACTIVE.includes(t.status) ? ['resolve', 'check', 'Çözüldü'] : ['reopen', 'refresh', 'Yeniden aç'];
        const devOn = !POps.isOffline({ status: t.device_status });
        const pcHtml = t.pc_name
            ? `<span class="dot ${devOn ? 'on' : 'off'}"></span>${escapeHtml(devName(t) + (labText(t) ? ' · ' + labText(t) : ''))}`
            : '<span class="faint">yok</span>';
        const user = t.logged_user && !['-', 'None'].includes(t.logged_user) ? t.logged_user : '';
        const app = t.pc_name && t.active_window && t.active_window !== '-' ? t.active_window : '';
        const facts = [
            ['Bildiren', (t.reporter || '—') + ' · ' + (t.source === 'agent' ? 'tepsiden' : 'panelden')],
            ['Oturumdaki kullanıcı', devOn ? user : ''], ['Ön plandaki program', devOn ? app : ''],
            ['Kategori', CAT[t.category] || t.category], ['Atanan', t.assignee || 'kimse']
        ];
        const factsHtml = '<div class="glist">'
            + `<div class="grow"><span>Bilgisayar</span><span>${pcHtml}</span></div>`
            + facts.filter(f => f[1]).map(f => `<div class="grow"><span>${escapeHtml(f[0])}</span><span>${escapeHtml(f[1])}</span></div>`).join('')
            + `<div class="grow"><span>Öncelik</span><span class="${t.priority === 'high' ? 'word bad' : ''}">${escapeHtml(PR[t.priority] || t.priority)}</span></div>`
            + `<div class="grow"><span>Açıldı</span><span>${POps.timeHtml(t.created_at)}</span></div>`
            + `<div class="grow"><span>Son hareket</span><span>${POps.timeHtml(t.updated_at)}</span></div>`
            + (t.resolved_at ? `<div class="grow"><span>Kapandı</span><span>${POps.timeHtml(t.resolved_at)}</span></div>` : '')
            + '</div>';
        return `<div class="drawer-head">
                <div class="drawer-title"><span class="drawer-ico ${escapeHtml(ico)}">${POps.iconHtml('help', 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(t.subject)}</h2><div class="sub"><span class="dot ${escapeHtml(s.c)}"></span>${escapeHtml(s.w)} · #${Number(t.id)}</div></div></div>
                <button type="button" class="ibtn sm" data-act="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button>
            </div>
            <div class="circs">
                <button type="button" class="circ" data-act="${escapeHtml(next[0])}"><span>${POps.iconHtml(next[1])}</span>${escapeHtml(next[2])}</button>
                <button type="button" class="circ" data-act="status" aria-haspopup="menu"><span>${POps.iconHtml('tag')}</span>Durum</button>
                <button type="button" class="circ" data-act="assign" aria-haspopup="menu"><span>${POps.iconHtml('users')}</span>Ata</button>
                <button type="button" class="circ" data-act="more" aria-haspopup="menu"><span>${POps.iconHtml('more')}</span>Diğer</button>
            </div>
            ${factsHtml}${threadHtml(t)}`;
    }
    function renderDrawer(keep) {
        const t = ui.detail;
        if (!t || !POps.drawer.isOpen('tk:' + t.id)) return;
        const body = POps.drawer.body();
        if (body.dataset.tk !== String(t.id) || !body.querySelector('.tk-main')) {
            body.dataset.tk = String(t.id);
            body.innerHTML = `<div class="tk-main"></div>
                <div class="tk-reply">
                    <textarea id="tkReply" rows="3" maxlength="5000" aria-label="Yanıt"></textarea>
                    <div class="row">
                        <label class="check"><input type="checkbox" id="tkInternal"> İç not (kullanıcı görmez)</label>
                        <button type="button" class="btn sm" data-act="send">${POps.iconHtml('send', 'sm')}<span id="tkSendText">Gönder</span></button>
                    </div>
                </div>`;
            keep = false;
        }
        $('tkReply').placeholder = t.source === 'agent' ? 'Kullanıcıya yanıt (tepsideki Taleplerim listesinde görür)' : 'Not ya da yanıt';
        const scroll = body.scrollTop;
        body.querySelector('.tk-main').innerHTML = mainHtml(t);
        body.scrollTop = keep ? scroll : 0;
    }
    async function loadDetail(id, keep) {
        let t;
        try { t = await POps.get('/api/tickets/' + encodeURIComponent(id)); }
        catch (e) {
            if (!keep && POps.drawer.isOpen('tk:' + id)) {
                const body = POps.drawer.body();
                body.dataset.tk = '';
                POps.setError(body, e, { title: 'Talep alınamadı' });
            }
            return;
        }
        if (!POps.drawer.isOpen('tk:' + id)) return;
        const sig = JSON.stringify(t);
        if (keep && sig === ui.detailSig) return;
        ui.detail = t;
        ui.detailSig = sig;
        renderDrawer(keep);
    }
    function openTicket(id) {
        // Önce panel açılır: başka bir talep açıksa onun kapanışı (odak sıfırlama) yenisinden önce çalışır
        const body = POps.drawer.open('tk:' + id, { onClose: () => { ui.focus = null; ui.detail = null; ui.detailSig = ''; renderList(true); } });
        ui.focus = id;
        renderList(true);
        wireDrawer(body);
        if (!ui.detail || ui.detail.id !== id) { body.dataset.tk = ''; POps.setLoading(body, 'Talep yükleniyor…'); }
        loadDetail(id, false);
    }
    async function update(t, data, msg) {
        const ok = await POps.act(null, () => POps.post(`/api/tickets/${encodeURIComponent(t.id)}/update`, data), { success: msg });
        if (ok) { await Promise.all([load().catch(() => {}), loadDetail(t.id, true)]); }
    }
    async function send() {
        const t = ui.detail;
        const ta = $('tkReply');
        if (!t || !ta) return;
        const body = ta.value.trim();
        if (!body) { ta.focus(); return; }
        const internal = $('tkInternal').checked;
        const btn = POps.drawer.body().querySelector('[data-act="send"]');
        try { await POps.busy(btn, () => POps.post(`/api/tickets/${encodeURIComponent(t.id)}/messages`, { body, internal })); }
        catch (e) { POps.toast('error', 'Gönderilemedi: ' + POps.errorMessage(e)); return; }
        ta.value = '';
        $('tkInternal').checked = false;
        $('tkSendText').textContent = 'Gönder';
        if (!internal && t.source === 'agent') POps.toast('success', 'Yanıt gönderildi; kullanıcı tepside görür.');
        await Promise.all([load().catch(() => {}), loadDetail(t.id, true)]);
        const b = POps.drawer.body();
        b.scrollTop = b.scrollHeight;
    }
    function wireDrawer(body) {
        if (body.dataset.tkWired) return;
        body.dataset.tkWired = '1';
        body.addEventListener('click', (e) => {
            const key = POps.drawer.key() || '';
            if (!key.startsWith('tk:')) return;
            const b = e.target.closest('[data-act]');
            if (!b) return;
            const t = ui.detail;
            const act = b.dataset.act;
            if (act === 'close') { POps.drawer.close(); return; }
            if (!t) return;
            if (act === 'send') send();
            else if (act === 'take') update(t, ME ? { status: 'in_progress', assignee: ME } : { status: 'in_progress' }, 'Talep üzerinize alındı.');
            else if (act === 'resolve') update(t, { status: 'resolved' }, 'Talep çözüldü olarak işaretlendi.');
            else if (act === 'reopen') update(t, { status: 'open' }, 'Talep yeniden açıldı.');
            else if (act === 'status') POps.menu(b, [{ header: 'Durum' }].concat(Object.keys(ST).map(k => ({
                label: ST[k].w, icon: ST[k].i, disabled: k === t.status, hint: k === t.status ? 'şu an' : '',
                onClick: () => update(t, { status: k }, `Durum: ${ST[k].w}.`)
            }))));
            else if (act === 'assign') POps.menu(b, [
                { header: t.assignee ? `Atanan: ${t.assignee}` : 'Kimseye atanmadı' },
                ME ? { label: 'Bana ata', icon: 'user', disabled: t.assignee === ME, hint: ME, onClick: () => update(t, { assignee: ME }, 'Talep size atandı.') } : null,
                { label: 'Başkasına ata…', icon: 'users', onClick: async () => {
                    const who = await POps.prompt({ title: 'Talebi ata', label: 'Atanan kişi', defaultValue: t.assignee || '', maxLength: 100, confirmText: 'Ata', icon: 'fa-user' });
                    if (who === null || !who.trim()) return;
                    update(t, { assignee: who.trim() }, `Talep ${who.trim()} kişisine atandı.`);
                } },
                t.assignee ? '-' : null,
                t.assignee ? { label: 'Atamayı kaldır', icon: 'x', onClick: () => update(t, { assignee: '' }, 'Atama kaldırıldı.') } : null
            ]);
            else if (act === 'more') {
                const off = POps.isOffline({ status: t.device_status });
                POps.menu(b, [
                    { header: 'Öncelik' },
                    ...['high', 'normal', 'low'].map(k => ({ label: PR[k], icon: k === 'high' ? 'alert' : k === 'low' ? 'arrow-down' : 'tag', disabled: k === t.priority, hint: k === t.priority ? 'şu an' : '', onClick: () => update(t, { priority: k }, `Öncelik: ${PR[k].toLocaleLowerCase('tr')}.`) })),
                    t.pc_name ? '-' : null,
                    t.pc_name ? { label: 'Ekranı izle', icon: 'eye', disabled: off, title: off ? 'Bilgisayar kapalı' : '', onClick: () => { location.href = dev.screenUrl([t.pc_name]); } } : null,
                    t.pc_name ? { label: 'Bilgisayarı aç', icon: 'monitor', onClick: () => { location.href = 'devices?pc=' + encodeURIComponent(t.pc_name); } } : null
                ]);
            }
        });
        body.addEventListener('change', (e) => {
            if (e.target.id === 'tkInternal') $('tkSendText').textContent = e.target.checked ? 'Not ekle' : 'Gönder';
        });
        body.addEventListener('keydown', (e) => {
            if (e.target.id === 'tkReply' && e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(); }
        });
    }

    // ================= Yeni talep =================
    $('nfCategory').innerHTML = Object.entries(CAT).map(([k, v]) => `<option value="${escapeHtml(k)}">${escapeHtml(v)}</option>`).join('');
    $('nfReporter').placeholder = 'Boş bırakılırsa siz' + (ME ? ` (${ME})` : '');
    $('newBtn').addEventListener('click', async () => {
        ['nfSubject', 'nfReporter', 'nfBody'].forEach(i => { $(i).value = ''; });
        $('nfCategory').value = 'diger';
        $('nfPriority').value = 'normal';
        $('nfSubjectField').classList.remove('has-error');
        openModal('newModal');
        try {
            const devs = await POps.get('/api/devices');
            const byLab = new Map();
            (Array.isArray(devs) ? devs : []).slice().sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true })).forEach(d => {
                const l = d.lab && d.lab !== dev.UNASSIGNED ? d.lab : 'Atanmamış';
                if (!byLab.has(l)) byLab.set(l, []);
                byLab.get(l).push(d);
            });
            const cur = $('nfPc').value;
            $('nfPc').innerHTML = '<option value="">Bilgisayar yok</option>' + [...byLab.keys()].sort((a, b) => a.localeCompare(b, 'tr')).map(l =>
                `<optgroup label="${escapeHtml(l)}">${byLab.get(l).map(d => `<option value="${escapeHtml(d.hw_id || d.hostname)}">${escapeHtml(POps.deviceName(d))}</option>`).join('')}</optgroup>`).join('');
            $('nfPc').value = cur;
        } catch (e) { /* bilgisayar isteğe bağlı */ }
    });
    $('nfSubject').addEventListener('input', () => $('nfSubjectField').classList.remove('has-error'));
    $('nfSave').addEventListener('click', async (e) => {
        const subject = $('nfSubject').value.trim();
        if (subject.length < 3) { $('nfSubjectField').classList.add('has-error'); $('nfSubject').focus(); return; }
        let r;
        try {
            r = await POps.busy(e.currentTarget, () => POps.post('/api/tickets', {
                subject, category: $('nfCategory').value, priority: $('nfPriority').value,
                pc_name: $('nfPc').value || null, reporter: $('nfReporter').value.trim() || null, body: $('nfBody').value
            }));
        } catch (err) { POps.toast('error', POps.errorMessage(err)); return; }
        if (!r) return;
        closeModal('newModal');
        POps.toast('success', `Talep #${r.id} açıldı.`);
        if (ui.f === 'closed') ui.f = 'open';
        await load().catch(() => {});
        openTicket(r.id);
    });

    // ================= Liste etkileşimi =================
    list.addEventListener('click', (e) => {
        const row = e.target.closest('.act[data-id]');
        if (row && !e.target.closest('a, .mark')) openTicket(Number(row.dataset.id));
    });
    list.addEventListener('keydown', (e) => {
        const row = e.target.closest('.act[data-id]');
        if (row && e.target === row && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openTicket(Number(row.dataset.id)); }
    });
    $('hdFilter').addEventListener('click', (e) => {
        const b = e.target.closest('button[data-f]');
        if (!b || b.dataset.f === ui.f) return;
        ui.f = b.dataset.f;
        renderHead();
        load().catch(() => {});
    });
    let qt = null;
    $('hdSearch').addEventListener('input', (e) => { clearTimeout(qt); qt = setTimeout(() => { ui.q = e.target.value.trim().slice(0, 100); load().catch(() => {}); }, 300); });

    // Esc yalnızca üstteki pencereyi kapatsın: ortak modal dinleyicisi pencereyi kapattıktan sonra ayrıntı paneli de
    // Esc'yi görüp kapanıyordu (pops_script.js). Pencere açıkken Esc burada yakalanır.
    window.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape' || document.querySelector('.pops-dialog-overlay, .pops-menu, .palette-overlay')) return;
        const m = ['newModal'].map(id => document.getElementById(id)).find(x => x && x.classList.contains('open'));
        if (!m) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        closeModal(m);
    }, true);

    load().then(() => {
        const want = Number(params.get('id'));
        if (want) openTicket(want);
    }).catch(() => {});
    popsPoll(async () => {
        await load();
        if (ui.focus) await loadDetail(ui.focus, true);
    }, 30000);
})();
</script>
