<?php include 'includes/header.php'; ?>
<?php $rpCanEdit = in_array($_SESSION['role'] ?? '', ['admin', 'superadmin'], true); ?>

<style>
    .rp-head { display: flex; align-items: flex-end; gap: 12px; flex-wrap: wrap; border-bottom: 1px solid var(--border-subtle); margin-bottom: 20px; }
    .rp-head .tabs { border-bottom: 0; flex: 1 1 auto; min-width: 0; }
    .rp-tools { display: flex; align-items: center; gap: 8px; padding-bottom: 8px; }
    .rp-tools select { width: auto; min-width: 140px; }
    .rp-pane { display: flex; flex-direction: column; gap: 20px; }
    .rp-pane[hidden] { display: none; }
    .rp-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .rp-bar .grow { flex: 1; }
    .rp-bar .search-field { flex: 0 1 280px; min-width: 180px; }
    .rp-meta { font-size: var(--text-sm); color: var(--text-tertiary); }
    .rp-meta b { color: var(--text-primary); font-weight: var(--fw-semibold); font-variant-numeric: tabular-nums; }
    .rp-note { font-size: var(--text-xs); color: var(--text-muted); line-height: 1.55; padding: 0 4px; margin-top: -8px; }
    .rp-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 20px; align-items: start; }
    .rp-grid > .card + .card { margin-top: 0; }
    .rp-card .card-body { padding-top: 14px; }
    .kpi .f .dot { margin-right: 5px; vertical-align: 1px; }
    .segmented .count { color: var(--text-muted); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }
    .kpi .f .go { display: inline-flex; align-items: center; }
    .rp-kpis { grid-template-columns: repeat(3, minmax(0, 1fr)); }
    .rp-kpis .kpi { display: flex; flex-direction: column; justify-content: flex-start; align-items: stretch; }
    .rp-kpis .kpi .f { margin-top: auto; padding-top: 10px; align-items: flex-end; }
    button.kpi { font: inherit; cursor: pointer; }
    #licModal .form-grid { margin-bottom: var(--space-4); }
    a.kpi { cursor: pointer; }

    /* Günlük olay grafiği: tek seri, ince sütun, üstü yuvarlak, 2px boşluk; üzerine gelince değer */
    .daychart { position: relative; display: flex; align-items: flex-end; gap: 2px; height: 150px; border-bottom: 1px solid var(--border-subtle); }
    .daychart .grid { position: absolute; left: 0; right: 0; top: 0; border-top: 1px solid #f0f0f3; pointer-events: none; }
    .daychart .grid span { position: absolute; right: 0; top: -18px; font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
    .daychart .col { flex: 1 1 0; height: 100%; display: flex; align-items: flex-end; justify-content: center; min-width: 2px; border-radius: 4px 4px 0 0; }
    .daychart .col:hover { background: var(--bg-surface-2); }
    .daychart .bar { width: 100%; max-width: 24px; background: var(--primary-500); border-radius: 4px 4px 0 0; }
    .daychart .col:hover .bar { background: var(--primary-600); }
    .daychart .col.zero .bar { height: 2px !important; background: #ececf0; border-radius: 1px; }
    .daychart-axis { display: flex; justify-content: space-between; font-size: 11px; color: var(--text-muted); margin-top: 6px; }
    .rp-calm { display: flex; align-items: center; gap: 8px; font-size: var(--text-sm); color: var(--text-tertiary); padding: 10px 0; }

    /* Yatay çubuk listesi */
    .hbar { display: grid; grid-template-columns: minmax(90px, 150px) minmax(0, 1fr) 40px; gap: 12px; align-items: center; font-size: var(--text-sm); padding: 6px 0; }
    .hbar .k { color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .hbar .t { background: #f2f2f7; border-radius: 4px; height: 8px; overflow: hidden; }
    .hbar .f { background: var(--primary-500); height: 8px; border-radius: 0 4px 4px 0; }
    .hbar .n { color: var(--text-primary); font-variant-numeric: tabular-nums; text-align: right; }

    /* Sade liste satırı */
    .rp-li { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 0; border-top: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .rp-li:first-child { border-top: 0; padding-top: 2px; }
    .rp-li .k { min-width: 0; display: flex; align-items: center; gap: 8px; }
    .rp-li .k > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .rp-li .sub { color: var(--text-muted); font-size: var(--text-xs); }
    .rp-li .n { font-variant-numeric: tabular-nums; color: var(--text-primary); font-weight: var(--fw-medium); }
    a.rp-li { color: inherit; border-radius: 8px; }
    a.rp-li:hover { background: var(--bg-surface-2); margin: 0 -8px; padding-left: 8px; padding-right: 8px; }

    /* Tablolar */
    .rp-table tbody tr { cursor: pointer; }
    .rp-table tbody tr.is-focus { background: #f0f6ff; }
    .rp-table .nm { font-weight: var(--fw-semibold); }
    .rp-table .sub { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .rp-table .st { display: inline-flex; align-items: center; gap: 7px; white-space: nowrap; }
    .rp-table td.faint, .rp-table .faint { color: var(--text-muted); }
    .rp-table td.num .sub { white-space: nowrap; }
    .use { display: flex; align-items: center; gap: 10px; white-space: nowrap; }
    .use .pbar { width: 90px; }
    .rp-dlist .rp-li { padding: 9px 0; }
    .rp-dlist .rp-li > .ico { color: var(--text-muted); }
    .rp-dacts { display: flex; gap: 8px; flex-wrap: wrap; }
    .rp-preview { font-size: var(--text-xs); color: var(--text-tertiary); line-height: 1.5; min-height: 1.2em; }
    @media (max-width: 640px) {
        .rp-head { align-items: stretch; }
        .rp-tools { width: 100%; padding: 8px 0; }
        .rp-tools select, .rp-tools .btn { flex: 1 1 auto; }
        .rp-bar .search-field { flex: 1 1 100%; }
        .rp-grid { grid-template-columns: minmax(0, 1fr); }
        .hbar { grid-template-columns: minmax(70px, 110px) minmax(0, 1fr) 36px; }
        .rp-kpis .kpi { padding: 14px; }
        .rp-kpis .kpi .v { font-size: 24px; }
    }
    @media (max-width: 900px) { .rp-kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; } }
</style>

<div class="page-header">
    <div>
        <h1>Raporlar</h1>
        <div class="summary" id="rpSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="rpExport" data-tip="CSV olarak indir" data-tip-pos="left" aria-label="CSV olarak indir" aria-haspopup="menu"><?php echo pops_icon('download'); ?></button>
    </div>
</div>

<div class="rp-head">
    <div class="tabs" id="rpTabs" role="tablist" aria-label="Raporlar">
        <button type="button" class="tab active" data-tab="summary" role="tab" aria-selected="true">Özet</button>
        <button type="button" class="tab" data-tab="software" role="tab" aria-selected="false">Yazılım</button>
        <button type="button" class="tab" data-tab="patches" role="tab" aria-selected="false">Windows güncellemeleri</button>
        <button type="button" class="tab" data-tab="licenses" role="tab" aria-selected="false">Lisanslar</button>
    </div>
    <div class="rp-tools" data-for="summary">
        <select id="rpDays" aria-label="Dönem">
            <option value="7">Son 7 gün</option>
            <option value="30" selected>Son 30 gün</option>
            <option value="90">Son 90 gün</option>
        </select>
    </div>
    <?php if ($rpCanEdit): ?>
    <div class="rp-tools" data-for="licenses" hidden>
        <button type="button" class="btn" id="licNew"><?php echo pops_icon('plus', 'sm'); ?>Lisans ekle</button>
    </div>
    <?php endif; ?>
</div>

<!-- ÖZET -->
<section class="rp-pane" id="pane-summary">
    <div class="kpis rp-kpis" id="kpis"><div class="loading-state" role="status"><span class="spinner"></span>Rapor hazırlanıyor…</div></div>
    <div class="card rp-card">
        <div class="card-header"><div><div class="card-title">Günlük yüksek ve kritik olaylar</div><div class="card-subtitle" id="dayMeta"></div></div></div>
        <div class="card-body" id="dayChart"></div>
    </div>
    <div class="rp-grid">
        <div class="card rp-card"><div class="card-header"><div class="card-title">Ajan sürümleri</div></div><div class="card-body" id="versions"></div></div>
        <div class="card rp-card"><div class="card-header"><div><div class="card-title">Ajan güncelleme sonuçları</div><div class="card-subtitle" id="updMeta"></div></div></div><div class="card-body" id="updates"></div></div>
    </div>
    <div class="rp-grid">
        <div class="card rp-card"><div class="card-header"><div><div class="card-title">En çok engellenen alan adları</div><div class="card-subtitle">Yasaklı siteye erişim denemeleri</div></div></div><div class="card-body" id="topPolicy"></div></div>
        <div class="card rp-card"><div class="card-header"><div><div class="card-title">En çok uyarı alan bilgisayarlar</div><div class="card-subtitle">Yüksek ve kritik olaylar</div></div></div><div class="card-body" id="topDevices"></div></div>
    </div>
</section>

<!-- YAZILIM -->
<section class="rp-pane" id="pane-software" hidden>
    <div class="rp-bar">
        <div class="search-field">
            <i class="fas fa-search" aria-hidden="true"></i>
            <input type="search" id="swQ" placeholder="Program ya da yayıncı (ör. chrome, adobe)" aria-label="Programlarda ara">
        </div>
        <span class="grow"></span>
        <span class="rp-meta" id="swMeta"></span>
    </div>
    <div class="table-wrap">
        <table class="data-table rp-table">
            <thead><tr><th>Program</th><th>Sürümler</th><th class="num">Bilgisayar</th></tr></thead>
            <tbody id="swBody"></tbody>
        </table>
    </div>
    <div class="rp-note">Kurulu program listesi 0.1.5 ve sonraki ajanlardan gelir; programa tıklayınca kurulu olduğu bilgisayarlar görünür.</div>
</section>

<!-- WINDOWS GÜNCELLEMELERİ -->
<section class="rp-pane" id="pane-patches" hidden>
    <div class="rp-bar">
        <div class="actionbar" id="ptBar" role="toolbar" aria-label="Windows güncelleme işlemleri"></div>
        <span class="grow"></span>
        <div class="segmented" id="ptFilter" role="group" aria-label="Duruma göre süz">
            <button type="button" data-f="all" class="active" aria-pressed="true">Tümü</button>
            <button type="button" data-f="need" aria-pressed="false">Eksik</button>
            <button type="button" data-f="none" aria-pressed="false">Bildirmedi</button>
        </div>
        <div class="search-field">
            <i class="fas fa-search" aria-hidden="true"></i>
            <input type="search" id="ptQ" placeholder="Bilgisayar ya da sınıf" aria-label="Bilgisayar ara">
        </div>
    </div>
    <div class="table-wrap">
        <table class="data-table rp-table wide">
            <thead id="ptHead"></thead>
            <tbody id="ptBody"></tbody>
        </table>
    </div>
    <div class="rp-note">Durum, Windows güncelleme bildirimini destekleyen ajanlardan (0.1.5 ve sonrası) gelir; eski ajanlar "Bildirmedi" görünür ve komutları yok sayar. Ajan güncellemeleri kurar ama bilgisayarı yeniden başlatmaz; gerekirse Windows kendi ayarına göre, etkin saatler dışında yeniden başlatır.</div>
</section>

<!-- LİSANSLAR -->
<section class="rp-pane" id="pane-licenses" hidden>
    <div class="rp-bar"><div class="summary" id="licSummary" style="margin-top:0"></div></div>
    <div class="table-wrap">
        <table class="data-table rp-table wide">
            <thead><tr><th>Lisans</th><th>Tür</th><th>Kullanım</th><th>Durum</th><th>Bitiş</th></tr></thead>
            <tbody id="licBody"></tbody>
        </table>
    </div>
    <div class="rp-note">Kullanım, yazılım envanterinde program adında eşleşme ifadesi geçen bilgisayarlar sayılarak bulunur (0.1.5 ve sonraki ajanlar). Koltuk boşsa sınırsız (site ya da kampüs) lisans sayılır. Aşım ve 30 gün içinde bitecek lisanslar için günde bir bildirim gider.</div>
</section>

<?php if ($rpCanEdit): ?>
<div id="licModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="licModalTitle">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="licModalTitle">Lisans ekle</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <input type="hidden" id="lfId">
            <div class="field"><label for="lfName">Lisans adı</label><input id="lfName" maxlength="200" placeholder="Örn. Office LTSC 2021 okul lisansı"></div>
            <div class="form-grid">
                <div class="field"><label for="lfPattern">Eşleşme ifadesi</label><input id="lfPattern" maxlength="200" placeholder="Örn. Office LTSC">
                    <div class="field-hint">Program adında geçen düz metin; kurulumlar bununla sayılır.</div></div>
                <div class="field"><label for="lfPublisher">Yayıncı (isteğe bağlı)</label><input id="lfPublisher" maxlength="200" placeholder="Örn. Microsoft"></div>
            </div>
            <div class="field"><div class="rp-preview" id="lfPreview" aria-live="polite"></div></div>
            <div class="form-grid">
                <div class="field"><label for="lfSeats">Koltuk</label><input id="lfSeats" type="number" min="0" placeholder="Boş bırakılırsa sınırsız"></div>
                <div class="field"><label for="lfType">Tür</label><select id="lfType"><option value="per_device">Cihaz başına</option><option value="site">Site ya da kampüs</option><option value="subscription">Abonelik</option></select></div>
                <div class="field"><label for="lfExpires">Bitiş tarihi (isteğe bağlı)</label><input id="lfExpires" type="date"></div>
            </div>
            <div class="field"><label for="lfNotes">Notlar</label><textarea id="lfNotes" maxlength="2000" rows="2" placeholder="Sözleşme no, tedarikçi…"></textarea></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="lfSave">Kaydet</button>
        </div>
    </div>
</div>
<?php endif; ?>

<script>
(function () {
    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const CAN_ACT = dev.canAdmin;
    const CAN_EDIT_LIC = <?php echo $rpCanEdit ? 'true' : 'false'; ?>;
    const params = new URLSearchParams(location.search);
    const TABS = ['summary', 'software', 'patches', 'licenses'];
    const ui = { tab: TABS.includes(params.get('tab')) ? params.get('tab') : 'summary', loaded: {}, focus: null };
    const fmtN = (v) => Number(v || 0).toLocaleString('tr-TR');
    const nHtml = (v) => escapeHtml(fmtN(v));
    const devName = (r) => r.display_name || r.hostname || r.pc_name || '';
    const labName = (l) => l && l !== dev.UNASSIGNED ? l : 'Atanmamış';
    const UNASSIGNED_KEY = dev.UNASSIGNED;
    // Tablo gövdesinde yükleniyor satırı (POps.setLoading tbody'ye div koyar)
    function loadingRow(tbody, cols, text) {
        POps.setLoading(tbody, text);
        const box = tbody.firstElementChild;
        tbody.replaceChildren(POps.el('tr', null, [POps.el('td', { colspan: String(cols) }, [box])]));
    }

    // ---------------------------------------------------------------- sekmeler
    function setTab(tab) {
        ui.tab = tab;
        $('rpTabs').querySelectorAll('.tab').forEach(t => {
            const on = t.dataset.tab === tab;
            t.classList.toggle('active', on); t.setAttribute('aria-selected', on ? 'true' : 'false');
            if (on && t.scrollIntoView) t.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        });
        TABS.forEach(k => { $('pane-' + k).hidden = k !== tab; });
        document.querySelectorAll('.rp-tools[data-for]').forEach(el => { el.hidden = el.dataset.for !== tab; });
        const u = new URL(location.href);
        if (tab === 'summary') u.searchParams.delete('tab'); else u.searchParams.set('tab', tab);
        history.replaceState(null, '', u.pathname + u.search);
        if (POps.drawer.isOpen()) POps.drawer.close();
        if (!ui.loaded[tab]) { ui.loaded[tab] = true; ({ summary: loadSummary, software: loadSoftware, patches: loadPatches, licenses: loadLicenses })[tab](); }
    }
    $('rpTabs').addEventListener('click', (e) => { const t = e.target.closest('.tab[data-tab]'); if (t && t.dataset.tab !== ui.tab) setTab(t.dataset.tab); });

    // ---------------------------------------------------------------- CSV
    $('rpExport').addEventListener('click', (e) => {
        const days = $('rpDays').value;
        const go = (kind) => { window.location.href = '/api/reports/export?kind=' + encodeURIComponent(kind) + '&days=' + encodeURIComponent(days); };
        POps.menu(e.currentTarget, [
            { header: 'CSV olarak indir' },
            { label: 'Cihazlar', icon: 'devices', onClick: () => go('devices') },
            { label: 'Yazılımlar', icon: 'package', onClick: () => go('software') },
            { label: 'Windows güncellemeleri', icon: 'shield', onClick: () => go('patches') },
            { label: 'Lisanslar', icon: 'key', onClick: () => go('licenses') },
            { label: `Olaylar (son ${days} gün)`, icon: 'list', onClick: () => go('events') }
        ]);
    });

    // ---------------------------------------------------------------- ÖZET
    // Sayı kartı: etiket ve sayı burada kaçırılır; alt satır (footHtml) çağıranın hazırladığı HTML
    // go: { page, f } başka sayfaya bağlantı, { tab } bu sayfada sekme, null tıklanmaz
    function kpiHtml(label, valueHtml, footHtml, go) {
        const innerHtml = `<div class="l">${escapeHtml(label)}</div><div class="v">${valueHtml}</div><div class="f"><span>${footHtml}</span>${go ? `<span class="go">${POps.iconHtml('right', 'sm')}</span>` : ''}</div>`;
        if (!go) return `<div class="kpi">${innerHtml}</div>`;
        if (go.tab) return `<button type="button" class="kpi" data-go="${escapeHtml(go.tab)}">${innerHtml}</button>`;
        return `<a class="kpi" href="${encodeURIComponent(go.page)}${go.f ? '?f=' + encodeURIComponent(go.f) : ''}">${innerHtml}</a>`;
    }
    const UPD = {
        success: ['ok', 'Başarılı'], rolled_back: ['warn', 'Geri alındı'], pending_reboot: ['warn', 'Yeniden başlatma bekliyor'],
        rollback_pending_reboot: ['warn', 'Geri alma yeniden başlatma bekliyor'], install_failed: ['', 'Başlamadı, değişiklik yok'],
        rollback_failed: ['bad', 'Geri alma başarısız'], failed: ['bad', 'Başarısız'], error: ['bad', 'Hata'], rejected: ['bad', 'Reddedildi']
    };
    function dayKey(t) { return t.getFullYear() + '-' + String(t.getMonth() + 1).padStart(2, '0') + '-' + String(t.getDate()).padStart(2, '0'); }
    async function loadSummary() {
        let d;
        try { d = await POps.get('/api/reports/summary?days=' + encodeURIComponent($('rpDays').value)); }
        catch (e) { POps.setError($('kpis'), e); $('kpis').firstElementChild.style.gridColumn = '1 / -1'; ['dayChart', 'versions', 'updates', 'topPolicy', 'topDevices'].forEach(id => { $(id).textContent = '—'; }); return; }
        const dv = d.devices || {}, p = d.patches || {}, sw = d.software || {}, ev = (d.events && d.events.by_risk) || {};
        const labs = (dv.labs || []).filter(l => l.lab && l.lab !== UNASSIGNED_KEY).length;
        $('rpSummary').innerHTML = `<span class="sum"><b>${nHtml(dv.total)}</b> cihaz</span><span class="sum"><b>${nHtml(labs)}</b> sınıf</span>`
            + `<span class="sum">son <b>${nHtml(d.days)}</b> gün</span><span class="sum">hazırlandı ${POps.timeHtml(d.generated_at)}</span>`;

        // Açık/boşta/kapalı sayıları Cihazlar sayfasıyla aynı kuralla (sunucu özeti boştakileri kapalı sayar)
        await devReady;
        const list = state.devicesLoaded ? (state.devices || []) : null;
        const onN = list ? list.filter(x => dev.state(x).cls === 'on').length : (dv.online || 0);
        const idleN = list ? list.filter(x => dev.state(x).cls === 'idle').length : 0;
        const off = (list ? list.length : (dv.total || 0)) - onN - idleN;
        const notReported = (dv.total || 0) - (p.reporting || 0);
        const unenrolled = (dv.total || 0) - (dv.enrolled || 0);
        const highCrit = (ev.high || 0) + (ev.critical || 0);
        const secFootHtml = p.reporting
            ? (p.pending_critical ? `<span class="dot bad"></span>${nHtml(p.pending_critical)} kritik` : 'Kritik eksik yok') + (p.reboot_required ? ` · ${nHtml(p.reboot_required)} yeniden başlatma` : '') + (notReported ? ` · ${nHtml(notReported)} bildirmedi` : '')
            : 'Henüz bildiren bilgisayar yok';
        $('kpis').innerHTML = [
            kpiHtml('Cihaz', nHtml(dv.total), `<span class="dot on"></span>${nHtml(onN)} açık` + (idleN ? ` · ${nHtml(idleN)} boşta` : '') + ` · ${nHtml(off)} kapalı`, { page: 'devices' }),
            kpiHtml('Kayıtlı ajan', `${nHtml(dv.enrolled)} <small>/ ${nHtml(dv.total)}</small>`,
                unenrolled ? `<span class="dot warn"></span>${nHtml(unenrolled)} ajan anahtarsız` : '<span class="dot ok"></span>Hepsi anahtarlı', null),
            kpiHtml('Karantinada', nHtml(dv.quarantined), dv.quarantined ? '<span class="dot bad"></span>Kullanımı kilitli bilgisayar var' : 'Yok', dv.quarantined ? { page: 'devices', f: 'issue' } : null),
            kpiHtml('Güvenlik güncellemesi bekleyen', nHtml(p.pending_security), secFootHtml, { tab: 'patches' }),
            kpiHtml('Yazılım bildiren bilgisayar', nHtml(sw.reporting_devices), `${nHtml(sw.titles)} farklı program`, { tab: 'software' }),
            kpiHtml('Yüksek ve kritik olay', nHtml(highCrit), `son ${nHtml(d.days)} gün · ${nHtml(ev.critical || 0)} kritik · ${nHtml(ev.medium || 0)} orta`, { page: 'logger', f: 'warn' })
        ].join('');

        // Günlük seri: dönemdeki her gün (olay olmayan günler 0)
        const byDay = {};
        ((d.events && d.events.by_day) || []).forEach(r => { byDay[r.day] = (r.critical || 0) + (r.high || 0); });
        const days = [];
        for (let i = d.days - 1; i >= 0; i--) {
            const t = new Date(); t.setDate(t.getDate() - i);
            days.push({ v: byDay[dayKey(t)] || 0, label: t.toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' }) });
        }
        const max = Math.max(1, ...days.map(x => x.v));
        const total = days.reduce((a, x) => a + x.v, 0);
        $('dayMeta').textContent = total ? `Son ${d.days} günde ${fmtN(total)} olay · en yoğun gün ${fmtN(max)}` : `Son ${d.days} gün`;
        const mid = days[Math.floor(days.length / 2)];
        $('dayChart').innerHTML = total
            ? `<div class="daychart" role="img" aria-label="Son ${escapeHtml(fmtN(d.days))} günde günlük yüksek ve kritik olay sayısı; toplam ${escapeHtml(fmtN(total))}">
                <div class="grid"><span>${nHtml(max)}</span></div>
                ${days.map(x => `<div class="col${x.v ? '' : ' zero'}" data-tip="${escapeHtml(x.label)} · ${escapeHtml(fmtN(x.v))} olay" data-tip-pos="up"><div class="bar" style="height:${Number((x.v / max) * 100).toFixed(1)}%"></div></div>`).join('')}
               </div>
               <div class="daychart-axis"><span>${escapeHtml(days[0].label)}</span><span>${escapeHtml(mid.label)}</span><span>${escapeHtml(days[days.length - 1].label)}</span></div>`
            : `<div class="rp-calm"><span class="dot ok"></span>Bu dönemde yüksek ya da kritik olay yok.</div>`;

        const versions = d.versions || [];
        const vmax = Math.max(1, ...versions.map(v => v.devices));
        $('versions').innerHTML = versions.length ? versions.map(v => `
            <div class="hbar"><span class="k" title="${escapeHtml(v.version)}">${escapeHtml(v.version === 'bilinmiyor' ? 'Bilinmiyor' : v.version)}</span>
            <div class="t"><div class="f" style="width:${Number((v.devices / vmax) * 100).toFixed(1)}%"></div></div><span class="n">${nHtml(v.devices)}</span></div>`).join('')
            : '<div class="rp-calm">Bilgisayar yok.</div>';

        const ups = Object.entries(d.updates || {}).sort((a, b) => b[1] - a[1]);
        $('updMeta').textContent = `Son ${d.days} gün`;
        $('updates').innerHTML = ups.length ? ups.map(([k, c]) => {
            const u = UPD[k] || ['', 'Bilinmeyen sonuç'];
            return `<div class="rp-li"><span class="k"><span class="dot ${escapeHtml(u[0])}"></span><span title="${escapeHtml(k)}">${escapeHtml(u[1])}</span></span><span class="n">${nHtml(c)}</span></div>`;
        }).join('') : '<div class="rp-calm">Bu dönemde ajan güncellemesi olmadı.</div>';

        const tp = (d.events && d.events.top_policy) || [];
        $('topPolicy').innerHTML = tp.length ? tp.map(r => `<div class="rp-li"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px"><span>${escapeHtml(r.domain || '—')}</span><span class="sub">${escapeHtml(r.category || 'kategori yok')}</span></span><span class="n">${nHtml(r.n)}</span></div>`).join('')
            : '<div class="rp-calm"><span class="dot ok"></span>Bu dönemde engellenen site yok.</div>';
        const td = (d.events && d.events.top_devices) || [];
        $('topDevices').innerHTML = td.length ? td.map(r => {
            const lab = (dev.find(r.pc_name) || {}).lab;
            return `<a class="rp-li" href="logger?pc=${encodeURIComponent(r.pc_name)}&amp;f=warn" title="Kayıtlarda göster"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px"><span>${escapeHtml(devName(r))}</span><span class="sub">${escapeHtml(lab ? labName(lab) : r.pc_name)}</span></span><span class="n">${nHtml(r.n)}</span></a>`;
        }).join('') : '<div class="rp-calm"><span class="dot ok"></span>Bu dönemde uyarı alan bilgisayar yok.</div>';
    }
    $('rpDays').addEventListener('change', loadSummary);
    $('kpis').addEventListener('click', (e) => { const b = e.target.closest('button[data-go]'); if (b) setTab(b.dataset.go); });

    // ---------------------------------------------------------------- ayrıntı paneli (yazılım, güncelleme, lisans)
    function drawerHeadHtml(title, subText, dotCls, icon) {
        return `<div class="drawer-head">
            <div class="drawer-title"><span class="drawer-ico">${POps.iconHtml(icon, 'lg')}</span>
                <div style="min-width:0"><h2>${escapeHtml(title)}</h2>${subText ? `<div class="sub">${dotCls !== null ? `<span class="dot ${escapeHtml(dotCls)}"></span>` : ''}${escapeHtml(subText)}</div>` : ''}</div></div>
            <button type="button" class="ibtn sm" data-rp="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button>
        </div>`;
    }
    // facts: [etiket, düz metin]; times: [etiket, zaman] (göreli, üstüne gelince tam tarih)
    function glistHtml(facts, times) {
        const rowsHtml = facts.filter(f => f[1] !== '' && f[1] != null).map(f => `<div class="grow"><span>${escapeHtml(f[0])}</span><span>${escapeHtml(f[1])}</span></div>`).join('');
        const timesHtml = (times || []).filter(t => t[1]).map(t => `<div class="grow"><span>${escapeHtml(t[0])}</span><span>${POps.timeHtml(t[1])}</span></div>`).join('');
        return `<div class="glist">${rowsHtml}${timesHtml}</div>`;
    }
    let drawerAct = {};
    function openDrawer(key, html, actions, onClose) {
        const body = POps.drawer.open(key, { onClose: () => { ui.focus = null; markFocus(); if (onClose) onClose(); } });
        ui.focus = key;
        markFocus();
        drawerAct = actions || {};
        body.innerHTML = html;
        if (!body.dataset.rpWired) {
            body.dataset.rpWired = '1';
            body.addEventListener('click', (e) => {
                const b = e.target.closest('[data-rp]');
                const k = POps.drawer.key() || '';
                if (!b || !/^(sw|pt|lic):/.test(k) || b.disabled) return;
                if (b.dataset.rp === 'close') POps.drawer.close();
                else if (b.dataset.rp === 'pc') dev.open(b.dataset.host, { source: 'reports' });
                else if (drawerAct[b.dataset.rp]) drawerAct[b.dataset.rp](b);
            });
        }
        return body;
    }
    function markFocus() {
        document.querySelectorAll('.rp-table tbody tr[data-key]').forEach(tr => tr.classList.toggle('is-focus', tr.dataset.key === ui.focus));
    }
    function deviceListHtml(rows, lineOf) {
        if (!rows.length) return '<div class="rp-calm">Eşleşen bilgisayar yok.</div>';
        return `<div class="rp-dlist">${rows.map(r => `<a href="#" class="rp-li" data-rp="pc" data-host="${escapeHtml(r.pc_name)}"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px;white-space:normal"><span>${escapeHtml(devName(r))}</span><span class="sub" style="white-space:normal">${escapeHtml([labName(r.lab_name), lineOf(r)].filter(Boolean).join(' · '))}</span></span>${POps.iconHtml('right', 'sm')}</a>`).join('')}</div>`;
    }
    document.addEventListener('click', (e) => { if (e.target.closest('#popsDrawer a[data-rp="pc"]')) e.preventDefault(); });

    // ---------------------------------------------------------------- YAZILIM
    let swTimer = null, swItems = [], swSeq = 0;
    async function loadSoftware() {
        const body = $('swBody');
        if (!swItems.length) loadingRow(body, 3, 'Programlar yükleniyor…');
        const seq = ++swSeq;
        let d;
        try { d = await POps.get('/api/software?q=' + encodeURIComponent($('swQ').value.trim())); }
        catch (e) { if (seq === swSeq) { POps.setError(body, e, { tag: 'tr', colspan: 3 }); $('swMeta').textContent = ''; } return; }
        if (seq !== swSeq) return;
        swItems = d.items || [];
        $('swMeta').innerHTML = d.reporting_devices ? `<b>${nHtml(d.reporting_devices)}</b> bilgisayar bildiriyor · <b>${nHtml(swItems.length)}</b> program` : '';
        if (!d.reporting_devices) {
            POps.setEmpty(body, { tag: 'tr', colspan: 3, icon: 'fa-box', title: 'Henüz yazılım bildiren bilgisayar yok', text: 'Kurulu programlar 0.1.5 ve sonraki ajanlardan gelir.' });
            return;
        }
        if (!swItems.length) {
            POps.setEmpty(body, { tag: 'tr', colspan: 3, icon: 'fa-filter', title: 'Süzgece uyan program yok', text: 'Başka bir ad ya da yayıncı deneyin.' });
            return;
        }
        body.innerHTML = swItems.map((r, i) => {
            const vs = (r.versions || []).filter(Boolean);
            return `<tr data-key="sw:${escapeHtml(r.name)}" data-i="${Number(i)}">
                <td><div class="nm">${escapeHtml(r.name)}</div><div class="sub">${escapeHtml(r.publisher || 'Yayıncı bilinmiyor')}</div></td>
                <td class="faint">${escapeHtml(vs.slice(0, 3).join(', '))}${vs.length > 3 ? ` <span class="faint">ve ${Number(vs.length - 3)} sürüm daha</span>` : ''}</td>
                <td class="num">${nHtml(r.devices)}</td></tr>`;
        }).join('');
        markFocus();
    }
    async function openSoftware(r) {
        const vs = (r.versions || []).filter(Boolean);
        const headHtml = drawerHeadHtml(r.name, r.publisher || 'Yayıncı bilinmiyor', null, 'package');
        const factsHtml = glistHtml([['Kurulu olduğu bilgisayar', fmtN(r.devices)], ['Sürüm', vs.length ? vs.join(', ') : '—']]);
        const body = openDrawer('sw:' + r.name, headHtml + factsHtml + '<div><h3>Bilgisayarlar</h3><div id="rpDList"><div class="rp-calm">Yükleniyor…</div></div></div>');
        let rows;
        try { rows = await POps.get('/api/software/devices?name=' + encodeURIComponent(r.name)); }
        catch (e) { if (POps.drawer.isOpen('sw:' + r.name)) body.querySelector('#rpDList').textContent = 'Liste alınamadı: ' + POps.errorMessage(e); return; }
        if (POps.drawer.isOpen('sw:' + r.name)) body.querySelector('#rpDList').innerHTML = deviceListHtml(rows || [], (x) => x.version || 'sürüm yok');
    }
    $('swBody').addEventListener('click', (e) => { const tr = e.target.closest('tr[data-i]'); if (tr && swItems[tr.dataset.i]) openSoftware(swItems[tr.dataset.i]); });
    $('swQ').addEventListener('input', () => { clearTimeout(swTimer); swTimer = setTimeout(loadSoftware, 300); });

    // ---------------------------------------------------------------- WINDOWS GÜNCELLEMELERİ
    const pt = { rows: [], f: 'all', q: '', sel: new Set() };
    const isOn = (r) => r && !POps.isOffline(r);
    function ptState(r) {
        if (!r.reported) return { cls: 'off', word: 'Bildirmedi' };
        if (r.pending_critical) return { cls: 'bad', word: 'Kritik eksik' };
        if (r.pending_security) return { cls: 'warn', word: 'Güvenlik eksik' };
        if (r.pending_count) return { cls: '', word: 'Güncelleme var' };
        return { cls: 'ok', word: 'Güncel' };
    }
    function ptList() {
        const q = pt.q.toLocaleLowerCase('tr');
        return pt.rows.filter(r => {
            if (pt.f === 'need' && !(r.reported && (r.pending_count || r.reboot_required))) return false;
            if (pt.f === 'none' && r.reported) return false;
            if (q && ![devName(r), r.pc_name, labName(r.lab_name)].some(v => String(v || '').toLocaleLowerCase('tr').includes(q))) return false;
            return true;
        });
    }
    function ptRowHtml(r) {
        const st = ptState(r);
        const k = r.pc_name;
        const sub = labName(r.lab_name) + ' · ' + (isOn(r) ? 'açık' : 'kapalı') + (r.agent_version ? ' · ' + r.agent_version : '');
        const pendHtml = r.reported
            ? `${nHtml(r.pending_count)}${r.pending_security || r.pending_critical ? `<div class="sub">${nHtml(r.pending_security)} güvenlik · ${nHtml(r.pending_critical)} kritik</div>` : ''}`
            : '<span class="faint">—</span>';
        return `<tr data-key="pt:${escapeHtml(k)}" data-host="${escapeHtml(k)}" class="${pt.sel.has(k) ? 'is-selected' : ''}">
            ${CAN_ACT ? `<td class="check-col"><input type="checkbox" class="pt-cb" data-host="${escapeHtml(k)}" ${pt.sel.has(k) ? 'checked' : ''} aria-label="${escapeHtml(devName(r))} seç"></td>` : ''}
            <td><div class="nm">${escapeHtml(devName(r))}</div><div class="sub">${escapeHtml(sub)}</div></td>
            <td><span class="st"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}</span></td>
            <td class="num">${pendHtml}</td>
            <td>${r.reported ? (r.reboot_required ? '<span class="st"><span class="dot warn"></span>Gerekli</span>' : '<span class="faint">Hayır</span>') : '<span class="faint">—</span>'}</td>
            <td>${r.last_search ? POps.timeHtml(r.last_search) : '<span class="faint">—</span>'}</td>
        </tr>`;
    }
    function ptScope(rows) { return pt.sel.size ? [...pt.sel] : rows.map(r => r.pc_name); }
    function renderPtBar(rows) {
        const bar = $('ptBar');
        if (!CAN_ACT) { bar.innerHTML = `<span class="scope"><b>${rows.length}</b> bilgisayar</span>`; return; }
        const n = pt.sel.size;
        const on = ptScope(rows).filter(h => isOn(pt.rows.find(r => r.pc_name === h))).length;
        const tgt = (n ? `${n} seçili` : `listedeki ${rows.length}`) + ` · ${on} açık`;
        bar.innerHTML = (n
            ? `<span class="scope sel">${Number(n)} seçili<button type="button" data-act="clear" aria-label="Seçimi temizle" data-tip="Seçimi temizle">${POps.iconHtml('x', 'sm')}</button></span>`
            : `<span class="scope">Listedeki <b>${rows.length}</b></span>`)
            + '<span class="sep"></span>'
            + `<button type="button" class="ibtn" data-act="scan" data-tip="Güncellemeleri tara · ${escapeHtml(tgt)}" aria-label="Güncellemeleri tara">${POps.iconHtml('refresh')}</button>`
            + `<button type="button" class="ibtn" data-act="security" data-tip="Güvenlik güncellemelerini kur · ${escapeHtml(tgt)}" aria-label="Güvenlik güncellemelerini kur">${POps.iconHtml('shield')}</button>`
            + `<button type="button" class="ibtn" data-act="all" data-tip="Bütün güncellemeleri kur · ${escapeHtml(tgt)}" aria-label="Bütün güncellemeleri kur">${POps.iconHtml('download')}</button>`;
    }
    function renderPatches() {
        const rows = ptList();
        pt.sel = new Set([...pt.sel].filter(h => pt.rows.some(r => r.pc_name === h)));
        const all = rows.length && rows.every(r => pt.sel.has(r.pc_name));
        $('ptHead').innerHTML = '<tr>' + (CAN_ACT ? `<th class="check-col"><input type="checkbox" id="ptAll" ${all ? 'checked' : ''} aria-label="Listedekilerin hepsini seç"></th>` : '')
            + '<th>Bilgisayar</th><th>Durum</th><th class="num">Bekleyen</th><th>Yeniden başlatma</th><th>Son tarama</th></tr>';
        const cnt = { all: pt.rows.length, need: pt.rows.filter(r => r.reported && (r.pending_count || r.reboot_required)).length, none: pt.rows.filter(r => !r.reported).length };
        $('ptFilter').querySelectorAll('button[data-f]').forEach(b => {
            const on = b.dataset.f === pt.f;
            b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false');
            b.replaceChildren(document.createTextNode(({ all: 'Tümü', need: 'Eksik', none: 'Bildirmedi' })[b.dataset.f] + ' '), POps.el('span', { className: 'count', text: String(cnt[b.dataset.f]) }));
        });
        renderPtBar(rows);
        const body = $('ptBody');
        if (!rows.length) {
            POps.setEmpty(body, pt.rows.length
                ? { tag: 'tr', colspan: 6, icon: 'fa-filter', title: 'Süzgece uyan bilgisayar yok', text: 'Arama ya da süzgeci değiştirin.' }
                : { tag: 'tr', colspan: 6, icon: 'fa-desktop', title: 'Henüz bilgisayar yok' });
            return;
        }
        body.innerHTML = rows.map(ptRowHtml).join('');
        markFocus();
    }
    async function loadPatches() {
        if (!pt.rows.length) loadingRow($('ptBody'), 6, 'Güncelleme durumu yükleniyor…');
        try { pt.rows = await POps.get('/api/patches') || []; }
        catch (e) { POps.setError($('ptBody'), e, { tag: 'tr', colspan: 6 }); $('ptBar').textContent = ''; return; }
        renderPatches();
        if (POps.drawer.isOpen() && String(POps.drawer.key()).startsWith('pt:')) {
            const r = pt.rows.find(x => 'pt:' + x.pc_name === POps.drawer.key());
            if (r) openPatch(r);
        }
    }
    const PT_TEXT = {
        scan: { verb: 'tara', job: 'Windows güncelleme taraması' },
        security: { q: 'güvenlik güncellemeleri kurulsun mu?', btn: 'kur', msg: 'Güvenlik ve kritik güncellemeler kurulur. Bilgisayarlar yeniden başlatılmaz; gerekirse listede "Yeniden başlatma: Gerekli" görünür.' },
        all: { q: 'bütün güncellemeler kurulsun mu?', btn: 'kur', msg: 'Bekleyen bütün güncellemeler kurulur; isteğe bağlı ve sürüm yükseltme güncellemeleri kurulmaz. Bilgisayarlar yeniden başlatılmaz; gerekirse listede görünür.' }
    };
    async function patchCmd(act, hosts, btn) {
        const on = hosts.filter(h => isOn(pt.rows.find(r => r.pc_name === h)));
        const skipped = hosts.length - on.length;
        if (!on.length) return POps.toast('warning', hosts.length === 1 ? 'Bilgisayar kapalı; komut gönderilmedi.' : 'Açık bilgisayar yok; komut gönderilmedi.');
        const who = on.length === 1 ? devName(pt.rows.find(r => r.pc_name === on[0])) : `${on.length} bilgisayar`;
        if (act !== 'scan') {
            const t = PT_TEXT[act];
            const ok = await POps.confirm({
                title: (on.length === 1 ? `${who} için ` : `${on.length} bilgisayarda `) + t.q,
                message: t.msg, note: skipped ? `Kapalı ${skipped} bilgisayar atlanacak.` : '',
                confirmText: on.length === 1 ? 'Güncellemeleri kur' : `${on.length} bilgisayara kur`, icon: 'fa-shield-halved'
            });
            if (!ok) return;
        }
        const kind = act === 'scan' ? 'scan' : 'install';
        const scope = act === 'all' ? 'all' : 'security';
        let d;
        try { d = await POps.busy(btn, () => POps.post('/api/patches/' + kind, { target_mode: 'PC', targets: on, scope })); }
        catch (e) { POps.toast('error', POps.errorMessage(e)); return; }
        const sent = ((d && d.dispatched) || []).length, off = ((d && d.skipped_offline) || []).length + skipped, closed = ((d && d.skipped_module_closed) || []).length;
        const rest = (off ? ` Bağlı olmayan ${off} bilgisayar atlandı.` : '') + (closed ? ` ${closed} bilgisayarda Windows güncelleme modülü kapalı.` : '');
        if (!sent) { POps.toast('warning', 'İstek hiçbir bilgisayara gönderilemedi.' + rest); return; }
        POps.toast('success', `${act === 'scan' ? 'Tarama' : 'Kurulum'} isteği ${sent} bilgisayara gönderildi; sonuçlar birkaç dakika içinde gelir.` + rest);
        setTimeout(loadPatches, 15000);
    }
    $('ptBar').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        if (b.dataset.act === 'clear') { pt.sel.clear(); renderPatches(); return; }
        patchCmd(b.dataset.act, ptScope(ptList()), b);
    });
    $('ptHead').addEventListener('change', (e) => { if (e.target.id === 'ptAll') { ptList().forEach(r => e.target.checked ? pt.sel.add(r.pc_name) : pt.sel.delete(r.pc_name)); renderPatches(); } });
    $('ptBody').addEventListener('change', (e) => { if (e.target.classList.contains('pt-cb')) { e.target.checked ? pt.sel.add(e.target.dataset.host) : pt.sel.delete(e.target.dataset.host); renderPatches(); } });
    $('ptBody').addEventListener('click', (e) => {
        const tr = e.target.closest('tr[data-host]');
        if (!tr || e.target.closest('input, .check-col')) return;
        const r = pt.rows.find(x => x.pc_name === tr.dataset.host);
        if (r) openPatch(r);
    });
    $('ptFilter').addEventListener('click', (e) => { const b = e.target.closest('button[data-f]'); if (b) { pt.f = b.dataset.f; renderPatches(); } });
    let ptT = null;
    $('ptQ').addEventListener('input', (e) => { clearTimeout(ptT); ptT = setTimeout(() => { pt.q = e.target.value.trim(); renderPatches(); }, 150); });
    function openPatch(r) {
        const st = ptState(r);
        const on = isOn(r);
        const actsHtml = CAN_ACT ? `<div class="circs">
                <button type="button" class="circ" data-rp="scan" ${on ? '' : 'disabled'} title="Windows güncellemelerini tara"><span>${POps.iconHtml('refresh')}</span>Tara</button>
                <button type="button" class="circ" data-rp="security" ${on && r.reported ? '' : 'disabled'} title="Güvenlik ve kritik güncellemeleri kur"><span>${POps.iconHtml('shield')}</span>Güvenlik</button>
                <button type="button" class="circ" data-rp="all" ${on && r.reported ? '' : 'disabled'} title="Bekleyen bütün güncellemeleri kur"><span>${POps.iconHtml('download')}</span>Tümü</button>
            </div>` : '';
        const facts = r.reported ? [
            ['Bekleyen', fmtN(r.pending_count)], ['Güvenlik', fmtN(r.pending_security)], ['Kritik', fmtN(r.pending_critical)],
            ['Yeniden başlatma', r.reboot_required ? 'Gerekli' : 'Gerekmiyor'], ['Son sonuç', r.last_result || '—'],
            ['Sınıf', labName(r.lab_name)], ['Ajan', r.agent_version || '—']
        ] : [['Sınıf', labName(r.lab_name)], ['Ajan', r.agent_version || '—']];
        const times = r.reported ? [['Son tarama', r.last_search], ['Son kurulum', r.last_install], ['Son bildirim', r.updated_at]] : [];
        const ups = r.updates || [];
        const upsHtml = ups.length ? `<div><h3>Bekleyen güncellemeler</h3>${ups.map(u => `<div class="rp-li"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px;white-space:normal"><span style="white-space:normal">${escapeHtml(u.title || 'Adsız güncelleme')}</span><span class="sub">${escapeHtml([u.kb, u.severity, u.is_security ? 'güvenlik' : ''].filter(Boolean).join(' · '))}</span></span></div>`).join('')}</div>` : '';
        const noteHtml = r.reported ? '' : '<div class="issue lock">Bu bilgisayarın ajanı Windows güncelleme durumunu bildirmiyor (0.1.5 ve sonrası gerekir).</div>';
        openDrawer('pt:' + r.pc_name, drawerHeadHtml(devName(r), st.word + (on ? '' : ' · kapalı'), st.cls, 'shield') + actsHtml + noteHtml + glistHtml(facts, times) + upsHtml
            + `<a href="#" data-rp="pc" data-host="${escapeHtml(r.pc_name)}" style="font-size:var(--text-sm)">Bilgisayarın ayrıntıları</a>`, {
            scan: (b) => patchCmd('scan', [r.pc_name], b),
            security: (b) => patchCmd('security', [r.pc_name], b),
            all: (b) => patchCmd('all', [r.pc_name], b)
        });
    }

    // ---------------------------------------------------------------- LİSANSLAR
    const LT = { per_device: 'Cihaz başına', site: 'Site ya da kampüs', subscription: 'Abonelik' };
    const LS = { ok: ['ok', 'Uygun'], over: ['bad', 'Aşım'], expiring: ['warn', 'Bitiyor'], expired: ['bad', 'Süresi doldu'] };
    let licenses = [];
    const fmtDate = (v) => { const d = POps.toDate(v && v.length === 10 ? v + 'T00:00:00' : v); return d ? d.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', year: 'numeric' }) : '—'; };
    function useHtml(l) {
        if (l.seats == null) return `<span class="faint">${nHtml(l.installed)} kurulu · sınırsız</span>`;
        const pct = Math.min(100, (l.installed / Math.max(1, l.seats)) * 100);
        return `<span class="use"><span class="pbar"><i class="${l.installed > l.seats ? 'bad' : 'ok'}" style="width:${Number(pct).toFixed(1)}%"></i></span><span>${nHtml(l.installed)} / ${nHtml(l.seats)}</span></span>`;
    }
    async function loadLicenses() {
        const body = $('licBody');
        if (!licenses.length) loadingRow(body, 5, 'Lisanslar yükleniyor…');
        let d;
        try { d = await POps.get('/api/licenses'); }
        catch (e) { POps.setError(body, e, { tag: 'tr', colspan: 5 }); return; }
        licenses = d.items || [];
        const sm = d.summary || {};
        const probHtml = ['over', 'expired', 'expiring'].filter(k => sm[k]).map(k => `<span class="sum"><span class="dot ${escapeHtml(LS[k][0])}"></span><b>${nHtml(sm[k])}</b> ${escapeHtml(LS[k][1].toLocaleLowerCase('tr'))}</span>`).join('');
        $('licSummary').innerHTML = licenses.length
            ? `<span class="sum"><b>${licenses.length}</b> lisans</span>` + (probHtml || '<span class="sum"><span class="dot ok"></span>Hepsi uygun</span>')
            : '';
        if (!licenses.length) {
            POps.setEmpty(body, { tag: 'tr', colspan: 5, icon: 'fa-certificate', title: 'Tanımlı lisans yok', text: CAN_EDIT_LIC ? '"Lisans ekle" ile koltuk ve bitiş tarihini takip etmeye başlayın.' : 'Bir yönetici lisans eklediğinde burada görünür.' });
            return;
        }
        body.innerHTML = licenses.map(l => {
            const st = LS[l.state] || LS.ok;
            return `<tr data-key="lic:${escapeHtml(l.id)}" data-id="${escapeHtml(l.id)}">
                <td><div class="nm">${escapeHtml(l.name)}</div><div class="sub">"${escapeHtml(l.match_pattern)}"${l.publisher ? ' · ' + escapeHtml(l.publisher) : ''}</div></td>
                <td>${escapeHtml(LT[l.license_type] || l.license_type)}</td>
                <td>${useHtml(l)}</td>
                <td><span class="st"><span class="dot ${escapeHtml(st[0])}"></span>${escapeHtml(st[1])}</span></td>
                <td>${l.expires_at ? escapeHtml(fmtDate(l.expires_at)) : '<span class="faint">—</span>'}</td>
            </tr>`;
        }).join('');
        markFocus();
        if (POps.drawer.isOpen() && String(POps.drawer.key()).startsWith('lic:')) {
            const l = licenses.find(x => 'lic:' + x.id === POps.drawer.key());
            if (l) openLicense(l); else POps.drawer.close();
        }
    }
    async function openLicense(l) {
        const st = LS[l.state] || LS.ok;
        const actsHtml = CAN_EDIT_LIC ? `<div class="rp-dacts">
                <button type="button" class="btn secondary sm" data-rp="edit">${POps.iconHtml('edit', 'sm')}Düzenle</button>
                <button type="button" class="btn danger-soft sm" data-rp="del">${POps.iconHtml('trash', 'sm')}Sil</button>
            </div>` : '';
        const facts = [
            ['Eşleşme ifadesi', '"' + l.match_pattern + '"'], ['Yayıncı', l.publisher || 'Her yayıncı'], ['Tür', LT[l.license_type] || l.license_type],
            ['Koltuk', l.seats == null ? 'Sınırsız' : fmtN(l.seats)], ['Kurulu', fmtN(l.installed)],
            ['Boş koltuk', l.free == null ? '' : (l.free < 0 ? `${fmtN(-l.free)} fazla kurulum` : fmtN(l.free))],
            ['Bitiş', l.expires_at ? fmtDate(l.expires_at) : 'Yok'], ['Notlar', l.notes || ''], ['Ekleyen', l.created_by || '']
        ];
        const whyHtml = l.state === 'over' ? `<div class="issue err">Koltuk sayısı aşıldı: ${nHtml(l.installed)} kurulum, ${nHtml(l.seats)} koltuk.</div>`
            : l.state === 'expired' ? '<div class="issue err">Lisansın süresi doldu.</div>'
            : l.state === 'expiring' ? '<div class="issue upd">Lisans 30 gün içinde bitiyor.</div>' : '';
        const body = openDrawer('lic:' + l.id, drawerHeadHtml(l.name, st[1], st[0], 'key') + actsHtml + whyHtml + glistHtml(facts, [['Eklenme', l.created_at]])
            + '<div><h3>Eşleşen kurulumlar</h3><div id="rpDList"><div class="rp-calm">Yükleniyor…</div></div></div>', {
            edit: () => licEdit(l),
            del: (b) => licDelete(l, b)
        });
        let rows;
        try { rows = await POps.get(`/api/licenses/${encodeURIComponent(l.id)}/devices`); }
        catch (e) { if (POps.drawer.isOpen('lic:' + l.id)) body.querySelector('#rpDList').textContent = 'Liste alınamadı: ' + POps.errorMessage(e); return; }
        if (POps.drawer.isOpen('lic:' + l.id)) body.querySelector('#rpDList').innerHTML = deviceListHtml(rows || [], (x) => [x.name, x.version].filter(Boolean).join(' '));
    }
    $('licBody').addEventListener('click', (e) => {
        const tr = e.target.closest('tr[data-id]');
        if (!tr) return;
        const l = licenses.find(x => String(x.id) === tr.dataset.id);
        if (l) openLicense(l);
    });
    async function licDelete(l, btn) {
        const ok = await POps.confirm({ title: `"${l.name}" lisansı silinsin mi?`, message: 'Kurulumlar etkilenmez; yalnızca bu tanım ve koltuk bilgisi silinir.', confirmText: 'Lisansı sil', danger: true, icon: 'fa-trash' });
        if (!ok) return;
        if (await POps.act(btn, () => POps.del('/api/licenses/' + encodeURIComponent(l.id)), { success: 'Lisans silindi.' })) { POps.drawer.close(); loadLicenses(); }
    }
    function licEdit(l) {
        if (!CAN_EDIT_LIC) return;
        $('licModalTitle').textContent = l ? 'Lisansı düzenle' : 'Lisans ekle';
        $('lfId').value = l ? l.id : '';
        $('lfName').value = l ? l.name : ''; $('lfPattern').value = l ? l.match_pattern : ''; $('lfPublisher').value = l ? (l.publisher || '') : '';
        $('lfSeats').value = l && l.seats != null ? l.seats : ''; $('lfType').value = l ? l.license_type : 'per_device';
        $('lfExpires').value = l && l.expires_at ? String(l.expires_at).slice(0, 10) : ''; $('lfNotes').value = l ? (l.notes || '') : '';
        licPreview();
        openModal('licModal');
    }
    let lpT = null;
    async function licPreview() {
        const box = $('lfPreview');
        const q = $('lfPattern').value.trim();
        if (q.length < 2) { box.textContent = 'Eşleşme ifadesi yazınca uyan programlar burada görünür.'; return; }
        try {
            const d = await POps.get('/api/software?limit=8&q=' + encodeURIComponent(q));
            if (q !== $('lfPattern').value.trim()) return;
            box.textContent = (d.items || []).length
                ? 'Uyan programlar: ' + d.items.map(i => `${i.name} (${fmtN(i.devices)} bilgisayar)`).join(', ')
                : 'Envanterde bu ifadeye uyan program yok (henüz yazılım bildiren ajan olmayabilir).';
        } catch (e) { box.textContent = ''; }
    }
    if (CAN_EDIT_LIC) {
        $('licNew').addEventListener('click', () => licEdit(null));
        $('lfPattern').addEventListener('input', () => { clearTimeout(lpT); lpT = setTimeout(licPreview, 300); });
        $('lfSave').addEventListener('click', async (e) => {
            const id = $('lfId').value;
            const body = {
                name: $('lfName').value, match_pattern: $('lfPattern').value, publisher: $('lfPublisher').value || null,
                seats: $('lfSeats').value === '' ? null : parseInt($('lfSeats').value, 10), license_type: $('lfType').value,
                expires_at: $('lfExpires').value || null, notes: $('lfNotes').value || null
            };
            if (await POps.act(e.currentTarget, () => POps.post(id ? '/api/licenses/' + encodeURIComponent(id) : '/api/licenses', body), { success: id ? 'Lisans güncellendi.' : 'Lisans eklendi.' })) {
                closeModal('licModal');
                loadLicenses();
            }
        });
    }

    // Cihaz listesi bir kez (ad, sınıf ve ayrıntı paneli için); bu sayfa yoklamaz
    const devReady = POps.loadDevices().catch(() => null);
    setTab(ui.tab);
    if (ui.tab !== 'summary') { ui.loaded.summary = true; loadSummary(); }
})();
</script>

<?php include 'includes/footer.php'; ?>
