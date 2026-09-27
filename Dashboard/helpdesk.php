<?php include 'includes/header.php'; ?>

<style>
    .hd-top { display: flex; gap: 0.5rem; flex-wrap: wrap; align-items: center; margin-bottom: var(--space-4); }
    .chip { padding: 0.4rem 0.8rem; border-radius: 999px; border: 1px solid var(--border-subtle); background: var(--bg-surface); color: var(--text-secondary); font-size: var(--text-sm); font-weight: var(--fw-semibold); cursor: pointer; }
    .chip.active { background: var(--primary-50); border-color: var(--primary-500); color: var(--primary-600); }
    .chip .n { font-variant-numeric: tabular-nums; opacity: 0.75; margin-left: 0.25rem; }
    .fld { width: auto; padding: 0.5rem 0.75rem; border: 1px solid var(--border-default); border-radius: var(--radius-md); background: var(--bg-surface-2); color: var(--text-primary); font-size: var(--text-sm); }
    .btn { padding: 0.55rem 0.95rem; border-radius: var(--radius-md); font-size: var(--text-sm); font-weight: var(--fw-semibold); cursor: pointer; border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); display: inline-flex; align-items: center; gap: 0.45rem; }
    .btn.primary { background: var(--primary-500); border-color: var(--primary-500); color: #fff; }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .hd-grid { display: grid; grid-template-columns: minmax(300px, 420px) 1fr; gap: var(--space-4); align-items: start; }
    @media (max-width: 960px) { .hd-grid { grid-template-columns: 1fr; } }
    .card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); box-shadow: var(--shadow-sm); }
    .list { max-height: calc(100vh - 260px); overflow-y: auto; }
    .item { padding: 0.75rem 1rem; border-bottom: 1px solid var(--border-subtle); cursor: pointer; }
    .item:hover { background: var(--bg-surface-2); }
    .item.sel { background: var(--primary-50); }
    .item .s { font-weight: var(--fw-semibold); color: var(--text-primary); font-size: var(--text-sm); }
    .item .m { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.25rem; display: flex; gap: 0.4rem; flex-wrap: wrap; align-items: center; }
    .pill { display: inline-flex; align-items: center; gap: 0.25rem; font-size: 0.6875rem; font-weight: var(--fw-semibold); padding: 0.1rem 0.45rem; border-radius: 999px; background: var(--bg-surface-2); color: var(--text-secondary); white-space: nowrap; }
    .pill.open { background: var(--info-bg); color: var(--info-text, var(--primary-600)); }
    .pill.in_progress { background: var(--warning-bg); color: var(--warning-text); }
    .pill.waiting { background: var(--bg-surface-2); color: var(--text-secondary); }
    .pill.resolved, .pill.closed { background: var(--success-bg); color: var(--success-text); }
    .pill.high { background: var(--danger-bg); color: var(--danger-text); }
    .empty { padding: 2rem 1rem; text-align: center; color: var(--text-tertiary); font-size: var(--text-sm); }
    .detail { padding: var(--space-5); }
    .detail h2 { margin: 0; font-size: var(--text-lg); color: var(--text-primary); }
    .meta { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 0.5rem; margin: var(--space-4) 0; }
    .meta > div { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.5rem 0.75rem; }
    .meta .l { font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); }
    .meta .v { font-size: var(--text-sm); color: var(--text-primary); margin-top: 0.15rem; word-break: break-word; }
    .body { white-space: pre-wrap; font-size: var(--text-sm); color: var(--text-secondary); background: var(--bg-surface-2); border-radius: var(--radius-md); padding: 0.75rem 1rem; }
    .ctl { display: flex; gap: 0.5rem; flex-wrap: wrap; align-items: center; margin: var(--space-4) 0; }
    .thread { display: flex; flex-direction: column; gap: 0.5rem; margin-top: var(--space-4); }
    .msg { border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.625rem 0.875rem; font-size: var(--text-sm); }
    .msg.internal { background: var(--warning-bg); border-color: transparent; }
    .msg .h { font-size: var(--text-xs); color: var(--text-tertiary); margin-bottom: 0.25rem; }
    .msg .t { white-space: pre-wrap; color: var(--text-primary); }
    textarea.fld { width: 100%; min-height: 90px; resize: vertical; }
    .reply { margin-top: var(--space-4); }
    .row { display: flex; gap: 0.5rem; align-items: center; flex-wrap: wrap; }
    .newform { display: none; padding: var(--space-5); margin-bottom: var(--space-4); }
    .newform.open { display: block; }
    .newform .g { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 0.5rem; margin-bottom: 0.5rem; }
    .newform .g .fld { width: 100%; }
    .note { font-size: var(--text-xs); color: var(--text-tertiary); }
</style>

<div class="page-header" style="display:flex;align-items:flex-end;justify-content:space-between;gap:1rem;flex-wrap:wrap;">
    <div>
        <h1><i class="fas fa-life-ring"></i> Yardım Masası</h1>
        <p>Öğrenci ve personelin tepsiden bildirdiği sorunlar ve BT ekibinin kayıtları</p>
    </div>
    <button class="btn primary" id="newBtn"><i class="fas fa-plus"></i> Yeni talep</button>
</div>

<div class="card newform" id="newForm">
    <div class="g">
        <input class="fld" id="nfSubject" maxlength="200" placeholder="Konu">
        <select class="fld" id="nfCategory"></select>
        <select class="fld" id="nfPriority"><option value="normal">Normal öncelik</option><option value="high">Yüksek öncelik</option><option value="low">Düşük öncelik</option></select>
        <select class="fld" id="nfPc"><option value="">Cihaz (isteğe bağlı)</option></select>
        <input class="fld" id="nfReporter" maxlength="100" placeholder="Bildiren (boşsa siz)">
    </div>
    <textarea class="fld" id="nfBody" maxlength="5000" placeholder="Açıklama"></textarea>
    <div class="row" style="margin-top:0.5rem;"><button class="btn primary" id="nfSave"><i class="fas fa-floppy-disk"></i> Kaydet</button><button class="btn" id="nfCancel">Vazgeç</button></div>
</div>

<div class="hd-top" id="chips"></div>
<div class="hd-top"><input class="fld" id="q" style="flex:1;min-width:220px;" placeholder="Konu, açıklama, bildiren ya da bilgisayar adı ara"></div>

<div class="hd-grid">
    <div class="card list" id="list"><div class="empty">Yükleniyor…</div></div>
    <div class="card" id="detail"><div class="empty">Soldan bir talep seçin.</div></div>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const $ = (id) => document.getElementById(id);
    const CAT = { donanim: 'Donanım', yazilim: 'Yazılım', ag: 'Ağ / İnternet', yazici: 'Yazıcı', hesap: 'Hesap / Şifre', diger: 'Diğer' };
    const ST = { open: 'Açık', in_progress: 'Üzerinde çalışılıyor', waiting: 'Yanıt bekleniyor', resolved: 'Çözüldü', closed: 'Kapatıldı' };
    const PR = { high: 'Yüksek', normal: 'Normal', low: 'Düşük' };
    const me = <?= json_encode($_SESSION['username'] ?? '', JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT) ?>;
    let filter = 'active', selected = null, items = [];
    const ago = (iso) => { const s = (Date.now() - new Date(iso)) / 1000; if (s < 60) return 'az önce'; if (s < 3600) return Math.floor(s / 60) + ' dk önce'; if (s < 86400) return Math.floor(s / 3600) + ' sa önce'; return new Date(iso).toLocaleDateString('tr-TR'); };
    const fmt = (iso) => iso ? new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—';
    const dev = (t) => t.display_name || t.hostname || t.pc_name || '';
    async function api(path, opts) {
        const r = await fetch(path, opts);
        const d = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(d.detail || ('HTTP ' + r.status));
        return d;
    }
    const post = (path, body) => api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

    $('nfCategory').innerHTML = Object.entries(CAT).map(([k, v]) => `<option value="${k}">${v}</option>`).join('');

    function renderChips(counts) {
        const active = (counts.open || 0) + (counts.in_progress || 0) + (counts.waiting || 0);
        const all = Object.values(counts).reduce((a, b) => a + b, 0);
        const chips = [['active', 'Aktif', active], ['open', ST.open, counts.open || 0], ['in_progress', ST.in_progress, counts.in_progress || 0],
                       ['waiting', ST.waiting, counts.waiting || 0], ['resolved', ST.resolved, counts.resolved || 0], ['closed', ST.closed, counts.closed || 0], ['all', 'Tümü', all]];
        $('chips').innerHTML = chips.map(([k, l, n]) => `<button class="chip${filter === k ? ' active' : ''}" data-k="${k}">${l}<span class="n">${n}</span></button>`).join('');
        $('chips').querySelectorAll('.chip').forEach(c => c.addEventListener('click', () => { filter = c.dataset.k; load(); }));
    }

    async function load() {
        let d;
        try { d = await api(`/api/tickets?status=${filter}&q=${encodeURIComponent($('q').value.trim())}`); }
        catch (e) { $('list').innerHTML = `<div class="empty">Talepler alınamadı: ${escapeHtml(e.message)}</div>`; return; }
        items = d.items || [];
        renderChips(d.counts || {});
        $('list').innerHTML = items.length ? items.map(t => `
            <div class="item${t.id === selected ? ' sel' : ''}" data-id="${t.id}">
                <div class="s">#${t.id} · ${escapeHtml(t.subject)}</div>
                <div class="m"><span class="pill ${t.status}">${ST[t.status] || escapeHtml(t.status)}</span>${t.priority === 'high' ? '<span class="pill high">Yüksek</span>' : ''}
                    <span>${escapeHtml(CAT[t.category] || t.category)}</span>
                    ${t.pc_name ? `<span><i class="fas fa-desktop"></i> ${escapeHtml(dev(t))}${t.lab_name ? ' · ' + escapeHtml(t.lab_name) : ''}</span>` : ''}
                    ${t.reporter ? `<span><i class="fas fa-user"></i> ${escapeHtml(t.reporter)}</span>` : ''}
                    <span>${ago(t.updated_at)}</span>${t.message_count ? `<span><i class="fas fa-comment"></i> ${t.message_count}</span>` : ''}</div>
            </div>`).join('') : '<div class="empty">Bu görünümde talep yok.</div>';
        $('list').querySelectorAll('.item').forEach(el => el.addEventListener('click', () => open(parseInt(el.dataset.id))));
    }

    async function open(id) {
        selected = id;
        $('list').querySelectorAll('.item').forEach(el => el.classList.toggle('sel', parseInt(el.dataset.id) === id));
        let t;
        try { t = await api('/api/tickets/' + id); } catch (e) { $('detail').innerHTML = `<div class="empty">${escapeHtml(e.message)}</div>`; return; }
        $('detail').innerHTML = `<div class="detail">
            <h2>#${t.id} · ${escapeHtml(t.subject)}</h2>
            <div class="meta">
                <div><div class="l">Bildiren</div><div class="v">${escapeHtml(t.reporter || '—')} <span class="note">(${t.source === 'agent' ? 'tepsiden' : 'panelden'})</span></div></div>
                <div><div class="l">Cihaz</div><div class="v">${t.pc_name ? escapeHtml(dev(t)) + (t.lab_name ? ' · ' + escapeHtml(t.lab_name) : '') + ` <span class="note">${t.device_status === 'Online' ? 'açık' : 'kapalı'}</span>` : '—'}</div></div>
                <div><div class="l">Kategori</div><div class="v">${escapeHtml(CAT[t.category] || t.category)}</div></div>
                <div><div class="l">Açıldı</div><div class="v">${fmt(t.created_at)}</div></div>
                ${t.pc_name && t.active_window && t.active_window !== '-' ? `<div><div class="l">Ön plandaki program</div><div class="v">${escapeHtml(t.active_window)}</div></div>` : ''}
            </div>
            ${t.body ? `<div class="body">${escapeHtml(t.body)}</div>` : ''}
            <div class="ctl">
                <select class="fld" id="dStatus">${Object.entries(ST).map(([k, v]) => `<option value="${k}"${k === t.status ? ' selected' : ''}>${v}</option>`).join('')}</select>
                <select class="fld" id="dPriority">${Object.entries(PR).map(([k, v]) => `<option value="${k}"${k === t.priority ? ' selected' : ''}>${v} öncelik</option>`).join('')}</select>
                <input class="fld" id="dAssignee" maxlength="100" placeholder="Atanan" value="${escapeHtml(t.assignee || '')}" style="width:150px;">
                <button class="btn" id="dMine" type="button">Bana ata</button>
                <button class="btn primary" id="dSave"><i class="fas fa-check"></i> Güncelle</button>
                ${t.pc_name ? `<a class="btn" href="vision.php" title="Uzaktan bakmak için Vision"><i class="fas fa-eye"></i> Vision</a>` : ''}
            </div>
            <div class="thread">${(t.messages || []).map(m => `
                <div class="msg${m.internal ? ' internal' : ''}"><div class="h">${escapeHtml(m.author)} · ${fmt(m.created_at)}${m.internal ? ' · iç not' : ' · kullanıcıya yanıt'}</div><div class="t">${escapeHtml(m.body)}</div></div>`).join('') || '<div class="note">Henüz yanıt yok.</div>'}</div>
            <div class="reply">
                <textarea class="fld" id="dReply" maxlength="5000" placeholder="${t.source === 'agent' ? 'Yanıt (kullanıcı tepsideki Taleplerim listesinde görür)' : 'Not ya da yanıt'}"></textarea>
                <div class="row" style="margin-top:0.5rem;justify-content:space-between;">
                    <label class="note" style="display:flex;gap:0.35rem;align-items:center;"><input type="checkbox" id="dInternal" style="width:auto;"> İç not (yalnızca panelde görünür)</label>
                    <button class="btn primary" id="dSend"><i class="fas fa-paper-plane"></i> Gönder</button>
                </div>
            </div></div>`;
        $('dMine').addEventListener('click', () => { $('dAssignee').value = me; });
        $('dSave').addEventListener('click', async () => {
            try { await post(`/api/tickets/${id}/update`, { status: $('dStatus').value, priority: $('dPriority').value, assignee: $('dAssignee').value });
                  showToast('Talep güncellendi.', 'success'); await load(); await open(id); } catch (e) { showToast(e.message, 'error'); }
        });
        $('dSend').addEventListener('click', async () => {
            const body = $('dReply').value.trim();
            if (!body) return;
            try { await post(`/api/tickets/${id}/messages`, { body, internal: $('dInternal').checked });
                  await load(); await open(id); } catch (e) { showToast(e.message, 'error'); }
        });
    }

    $('newBtn').addEventListener('click', async () => {
        $('newForm').classList.add('open');
        try {
            const devs = await api('/api/devices');
            $('nfPc').innerHTML = '<option value="">Cihaz (isteğe bağlı)</option>' + devs.map(d => `<option value="${escapeHtml(d.hw_id)}">${escapeHtml(d.display_name || d.real_hostname || d.hw_id)}${d.lab ? ' · ' + escapeHtml(d.lab) : ''}</option>`).join('');
        } catch (e) { /* cihaz listesi isteğe bağlı */ }
    });
    $('nfCancel').addEventListener('click', () => $('newForm').classList.remove('open'));
    $('nfSave').addEventListener('click', async () => {
        try {
            const r = await post('/api/tickets', { subject: $('nfSubject').value, category: $('nfCategory').value, priority: $('nfPriority').value,
                                                  pc_name: $('nfPc').value || null, reporter: $('nfReporter').value || null, body: $('nfBody').value });
            $('newForm').classList.remove('open'); ['nfSubject', 'nfReporter', 'nfBody'].forEach(i => $(i).value = '');
            showToast(`Talep #${r.id} açıldı.`, 'success'); filter = 'active'; await load(); open(r.id);
        } catch (e) { showToast(e.message, 'error'); }
    });
    let qt; $('q').addEventListener('input', () => { clearTimeout(qt); qt = setTimeout(load, 300); });
    load();
    setInterval(load, 30000);
})();
</script>
