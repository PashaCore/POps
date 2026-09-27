<?php include 'includes/header.php'; ?>

<style>
    .rp-wrap { display: flex; flex-direction: column; gap: var(--space-5); }
    .rp-tabs { display: flex; gap: 0.25rem; border-bottom: 1px solid var(--border-subtle); flex-wrap: wrap; }
    .rp-tab { padding: 0.625rem 1rem; border: none; background: none; color: var(--text-secondary); font-weight: var(--fw-semibold); font-size: var(--text-sm); cursor: pointer; border-bottom: 2px solid transparent; margin-bottom: -1px; }
    .rp-tab.active { color: var(--primary-600); border-bottom-color: var(--primary-500); }
    .rp-pane { display: none; flex-direction: column; gap: var(--space-5); }
    .rp-pane.active { display: flex; }
    .rp-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-5); box-shadow: var(--shadow-sm); min-width: 0; }
    .rp-card h3 { font-size: var(--text-md); margin: 0 0 var(--space-4); color: var(--text-primary); display: flex; align-items: center; gap: 0.5rem; }
    .rp-card h3 i { color: var(--primary-500); }
    .rp-cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(320px, 1fr)); gap: var(--space-5); }
    .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: var(--space-3); }
    .kpi { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-4); }
    .kpi .l { font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); }
    .kpi .v { font-size: 1.75rem; font-weight: var(--fw-bold, 700); color: var(--text-primary); font-variant-numeric: tabular-nums; margin-top: 0.25rem; }
    .kpi .s { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.25rem; }
    .kpi .s i.warn { color: var(--warning-solid); } .kpi .s i.bad { color: var(--danger-solid); } .kpi .s i.ok { color: var(--success-solid); }

    /* Günlük olay grafiği: tek seri, tek renk, ince çubuk, üzerine gelince değer */
    .daychart { display: flex; align-items: flex-end; gap: 2px; height: 140px; border-bottom: 1px solid var(--border-subtle); padding-top: 18px; }
    .daychart .col { flex: 1; height: 100%; display: flex; align-items: flex-end; position: relative; cursor: default; min-width: 3px; }
    .daychart .bar { width: 100%; max-width: 18px; margin: 0 auto; background: var(--primary-500); border-radius: 4px 4px 0 0; min-height: 0; }
    .daychart .col:hover .bar { background: var(--primary-600); }
    .daychart .col:hover::after { content: attr(data-tip); position: absolute; bottom: calc(100% + 4px); left: 50%; transform: translateX(-50%); background: var(--text-primary); color: var(--bg-surface); font-size: 0.75rem; padding: 0.25rem 0.5rem; border-radius: var(--radius-sm); white-space: nowrap; z-index: 5; }
    .daychart-axis { display: flex; justify-content: space-between; font-size: 0.6875rem; color: var(--text-tertiary); margin-top: 0.375rem; }

    /* Yatay çubuklar (sürüm dağılımı): tek renk, değer etiketi metin renginde */
    .hbar { display: grid; grid-template-columns: minmax(90px, 160px) 1fr 40px; gap: 0.625rem; align-items: center; font-size: var(--text-sm); margin-bottom: 0.5rem; }
    .hbar .k { color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .hbar .t { background: var(--bg-surface-2); border-radius: 4px; height: 10px; }
    .hbar .f { background: var(--primary-500); height: 10px; border-radius: 0 4px 4px 0; }
    .hbar .n { color: var(--text-primary); font-variant-numeric: tabular-nums; text-align: right; }

    .rp-table { width: 100%; border-collapse: collapse; font-size: var(--text-sm); }
    .rp-table th { text-align: left; font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); padding: 0.5rem; border-bottom: 1px solid var(--border-subtle); white-space: nowrap; }
    .rp-table input[type=checkbox] { width: auto; margin: 0; }
    .rp-table td { padding: 0.5rem; border-bottom: 1px solid var(--border-subtle); color: var(--text-secondary); vertical-align: top; }
    .rp-table td.num { text-align: right; font-variant-numeric: tabular-nums; color: var(--text-primary); }
    .rp-table tr.click { cursor: pointer; } .rp-table tr.click:hover td { background: var(--bg-surface-2); }
    .rp-scroll { overflow-x: auto; }
    .rp-empty { color: var(--text-tertiary); font-size: var(--text-sm); padding: 0.75rem 0; }
    .pill { display: inline-flex; align-items: center; gap: 0.3rem; font-size: 0.75rem; font-weight: var(--fw-semibold); padding: 0.1rem 0.5rem; border-radius: 999px; background: var(--bg-surface-2); color: var(--text-secondary); white-space: nowrap; }
    .pill.ok { background: var(--success-bg); color: var(--success-text); } .pill.warn { background: var(--warning-bg); color: var(--warning-text); } .pill.bad { background: var(--danger-bg); color: var(--danger-text); }
    .rp-btn { padding: 0.5rem 0.875rem; border-radius: var(--radius-md); border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); font-size: var(--text-sm); font-weight: var(--fw-semibold); cursor: pointer; display: inline-flex; align-items: center; gap: 0.4rem; }
    .rp-btn.primary { background: var(--primary-500); border-color: var(--primary-500); color: #fff; }
    .rp-btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .rp-fld { width: auto; padding: 0.5rem 0.75rem; border: 1px solid var(--border-default); border-radius: var(--radius-md); background: var(--bg-surface-2); color: var(--text-primary); font-size: var(--text-sm); }
    .rp-row { display: flex; gap: 0.5rem; align-items: center; flex-wrap: wrap; }
    .rp-note { font-size: var(--text-sm); color: var(--text-secondary); background: var(--info-bg); border-left: 3px solid var(--info-solid, #3b82f6); padding: 0.625rem 0.875rem; border-radius: var(--radius-sm); }
</style>

<div class="page-header" style="display:flex;align-items:flex-end;justify-content:space-between;gap:1rem;flex-wrap:wrap;">
    <div>
        <h1><i class="fas fa-chart-column"></i> Raporlar</h1>
        <p>Filo durumu, güvenlik olayları, kurulu yazılımlar ve Windows güncellemeleri</p>
    </div>
    <div class="rp-row" style="flex-wrap:nowrap;">
        <select id="rpDays" class="rp-fld" aria-label="Dönem">
            <option value="7">Son 7 gün</option>
            <option value="30" selected>Son 30 gün</option>
            <option value="90">Son 90 gün</option>
        </select>
        <select id="rpExport" class="rp-fld" aria-label="CSV indir">
            <option value="">CSV indir…</option>
            <option value="devices">Cihazlar</option>
            <option value="software">Yazılımlar</option>
            <option value="patches">Windows güncellemeleri</option>
            <option value="events">Olaylar (seçili dönem)</option>
        </select>
    </div>
</div>

<div class="rp-wrap">
    <div class="rp-tabs">
        <button class="rp-tab active" data-tab="summary"><i class="fas fa-gauge"></i> Özet</button>
        <button class="rp-tab" data-tab="software"><i class="fas fa-box"></i> Yazılım</button>
        <button class="rp-tab" data-tab="patches"><i class="fas fa-shield-virus"></i> Windows güncellemeleri</button>
    </div>

    <!-- ÖZET -->
    <div class="rp-pane active" id="pane-summary">
        <div class="kpi-grid" id="kpis"></div>
        <div class="rp-card">
            <h3><i class="fas fa-triangle-exclamation"></i> Günlük yüksek ve kritik olaylar</h3>
            <div id="dayChart"></div>
        </div>
        <div class="rp-cols">
            <div class="rp-card"><h3><i class="fas fa-code-branch"></i> Ajan sürümleri</h3><div id="versions"></div></div>
            <div class="rp-card"><h3><i class="fas fa-rotate"></i> Ajan güncelleme sonuçları</h3><div id="updates"></div></div>
        </div>
        <div class="rp-cols">
            <div class="rp-card"><h3><i class="fas fa-ban"></i> En çok ihlal edilen alan adları</h3><div id="topPolicy"></div></div>
            <div class="rp-card"><h3><i class="fas fa-desktop"></i> En çok yüksek riskli olay yaşayan cihazlar</h3><div id="topDevices"></div></div>
        </div>
    </div>

    <!-- YAZILIM -->
    <div class="rp-pane" id="pane-software">
        <div class="rp-card">
            <div class="rp-row" style="justify-content:space-between;margin-bottom:var(--space-4);">
                <input id="swQ" class="rp-fld" style="flex:1;min-width:220px;" placeholder="Program ya da yayıncı ara (ör. chrome, adobe)">
                <span class="rp-empty" id="swMeta" style="padding:0;"></span>
            </div>
            <div class="rp-scroll"><table class="rp-table" id="swTable"></table></div>
        </div>
    </div>

    <!-- WINDOWS GÜNCELLEMELERİ -->
    <div class="rp-pane" id="pane-patches">
        <div class="rp-note" id="ptNote">
            <i class="fas fa-circle-info"></i> Windows güncelleme durumu, bu özelliği destekleyen ajanlardan gelir (0.1.5-alpha ve sonrası).
            Eski ajanlar "bildirmedi" görünür ve tarama/kurma komutunu yok sayar. Ajan güncellemeleri kurar ama bilgisayarı kendiliğinden yeniden başlatmaz.
        </div>
        <div class="rp-card">
            <div class="rp-row" style="justify-content:space-between;margin-bottom:var(--space-4);" id="ptActions">
                <span class="rp-empty" id="ptMeta" style="padding:0;"></span>
                <div class="rp-row">
                    <button class="rp-btn" id="ptScan" disabled><i class="fas fa-magnifying-glass"></i> Tara</button>
                    <button class="rp-btn primary" id="ptSec" disabled><i class="fas fa-shield"></i> Güvenlik güncellemelerini kur</button>
                    <button class="rp-btn" id="ptAll" disabled><i class="fas fa-download"></i> Tümünü kur</button>
                </div>
            </div>
            <div class="rp-scroll"><table class="rp-table" id="ptTable"></table></div>
        </div>
    </div>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const $ = (id) => document.getElementById(id);
    const canAct = ['admin', 'superadmin'].includes(window.USER_ROLE);
    const fmt = (iso) => { if (!iso) return '—'; try { return new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }); } catch (e) { return iso; } };
    const devName = (d) => d.display_name || d.hostname || d.pc_name;
    const n = (v) => Number(v || 0).toLocaleString('tr-TR');
    async function api(path, opts) {
        const r = await fetch(path, opts);
        const d = await r.json().catch(() => ({}));
        if (!r.ok) throw new Error(d.detail || ('HTTP ' + r.status));
        return d;
    }

    // ---- sekmeler ----
    const loaded = {};
    document.querySelectorAll('.rp-tab').forEach(t => t.addEventListener('click', () => {
        document.querySelectorAll('.rp-tab').forEach(x => x.classList.toggle('active', x === t));
        document.querySelectorAll('.rp-pane').forEach(p => p.classList.toggle('active', p.id === 'pane-' + t.dataset.tab));
        if (!loaded[t.dataset.tab]) { loaded[t.dataset.tab] = true; ({ software: loadSoftware, patches: loadPatches })[t.dataset.tab]?.(); }
    }));

    // ---- CSV ----
    $('rpExport').addEventListener('change', function () {
        if (!this.value) return;
        window.location.href = '/api/reports/export?kind=' + encodeURIComponent(this.value) + '&days=' + $('rpDays').value;
        this.value = '';
    });

    // ---- ÖZET ----
    function kpi(label, value, sub) { return `<div class="kpi"><div class="l">${label}</div><div class="v">${value}</div><div class="s">${sub || '&nbsp;'}</div></div>`; }
    async function loadSummary() {
        let d;
        try { d = await api('/api/reports/summary?days=' + $('rpDays').value); }
        catch (e) { $('kpis').innerHTML = `<div class="rp-empty">Rapor alınamadı: ${escapeHtml(e.message)}${e.message.includes('404') ? ' (sunucu güncellemesi gerekli)' : ''}</div>`; return; }
        const dv = d.devices, p = d.patches, sw = d.software, ev = d.events.by_risk || {};
        const notReported = dv.total - p.reporting;
        $('kpis').innerHTML = [
            kpi('Cihaz', n(dv.total), `${n(dv.online)} açık · ${n(dv.total - dv.online)} kapalı`),
            kpi('Kayıtlı ajan', `${n(dv.enrolled)}<span style="font-size:1rem;color:var(--text-tertiary);"> / ${n(dv.total)}</span>`,
                dv.enrolled === dv.total ? '<i class="fas fa-circle-check ok"></i> Hepsi anahtarlı' : '<i class="fas fa-circle-exclamation warn"></i> Kayıtsız ajan var'),
            kpi('Karantinada', n(dv.quarantined), dv.quarantined ? '<i class="fas fa-lock warn"></i> Ağdan yalıtılmış cihaz var' : 'Yok'),
            kpi('Güvenlik güncellemesi bekleyen', n(p.pending_security),
                `${n(p.pending_critical)} kritik · ${n(p.reboot_required)} yeniden başlatma` + (notReported ? ` · ${n(notReported)} bildirmedi` : '')),
            kpi('Yazılım bildiren cihaz', n(sw.reporting_devices), `${n(sw.titles)} farklı program`),
            kpi(`Yüksek/kritik olay (${d.days} gün)`, n((ev.high || 0) + (ev.critical || 0)), `${n(ev.critical || 0)} kritik · ${n(ev.medium || 0)} orta`),
        ].join('');

        // günlük seri: dönemdeki her gün (olay olmayan günler 0)
        const byDay = {};
        (d.events.by_day || []).forEach(r => { byDay[r.day] = (r.critical || 0) + (r.high || 0); });
        const days = [];
        for (let i = d.days - 1; i >= 0; i--) {
            const t = new Date(); t.setDate(t.getDate() - i);
            const key = t.getFullYear() + '-' + String(t.getMonth() + 1).padStart(2, '0') + '-' + String(t.getDate()).padStart(2, '0');
            days.push({ key, v: byDay[key] || 0, label: t.toLocaleDateString('tr-TR', { day: '2-digit', month: 'short' }) });
        }
        const max = Math.max(1, ...days.map(x => x.v));
        const total = days.reduce((a, x) => a + x.v, 0);
        $('dayChart').innerHTML = total ? `
            <div class="daychart" role="img" aria-label="Günlük yüksek ve kritik olay sayısı">
                ${days.map(x => `<div class="col" data-tip="${x.label}: ${x.v} olay"><div class="bar" style="height:${(x.v / max) * 100}%"></div></div>`).join('')}
            </div>
            <div class="daychart-axis"><span>${days[0].label}</span><span>en yüksek: ${max}</span><span>${days[days.length - 1].label}</span></div>`
            : '<div class="rp-empty"><i class="fas fa-circle-check" style="color:var(--success-solid)"></i> Bu dönemde yüksek ya da kritik olay yok.</div>';

        const vmax = Math.max(1, ...d.versions.map(v => v.devices));
        $('versions').innerHTML = d.versions.length ? d.versions.map(v => `
            <div class="hbar"><span class="k" title="${escapeHtml(v.version)}">${escapeHtml(v.version)}</span>
            <div class="t"><div class="f" style="width:${(v.devices / vmax) * 100}%"></div></div><span class="n">${n(v.devices)}</span></div>`).join('')
            : '<div class="rp-empty">Cihaz yok.</div>';

        const U = { success: ['ok', 'fa-circle-check', 'Başarılı'], rolled_back: ['warn', 'fa-rotate-left', 'Geri alındı'],
                    pending_reboot: ['warn', 'fa-power-off', 'Yeniden başlatma bekliyor'], install_failed: ['', 'fa-circle-minus', 'Başlamadı (değişiklik yok)'],
                    rollback_failed: ['bad', 'fa-circle-xmark', 'Geri alma başarısız'], failed: ['bad', 'fa-circle-xmark', 'Başarısız'],
                    error: ['bad', 'fa-circle-xmark', 'Hata'], rejected: ['bad', 'fa-ban', 'Reddedildi'] };
        const ups = Object.entries(d.updates || {});
        $('updates').innerHTML = ups.length ? `<table class="rp-table">${ups.sort((a, b) => b[1] - a[1]).map(([k, c]) => {
            const u = U[k] || ['', 'fa-circle-question', k];
            return `<tr><td><span class="pill ${u[0]}"><i class="fas ${u[1]}"></i> ${escapeHtml(u[2])}</span></td><td class="num">${n(c)}</td></tr>`;
        }).join('')}</table>` : '<div class="rp-empty">Bu dönemde ajan güncelleme sonucu yok.</div>';

        $('topPolicy').innerHTML = d.events.top_policy.length ? `<table class="rp-table"><tr><th>Alan adı</th><th>Kategori</th><th style="text-align:right">Olay</th></tr>
            ${d.events.top_policy.map(r => `<tr><td>${escapeHtml(r.domain || '—')}</td><td>${escapeHtml(r.category || '—')}</td><td class="num">${n(r.n)}</td></tr>`).join('')}</table>`
            : '<div class="rp-empty">Bu dönemde kural ihlali yok.</div>';
        $('topDevices').innerHTML = d.events.top_devices.length ? `<table class="rp-table"><tr><th>Cihaz</th><th style="text-align:right">Olay</th></tr>
            ${d.events.top_devices.map(r => `<tr><td>${escapeHtml(devName(r))} <span style="color:var(--text-tertiary);font-size:0.75rem;">${escapeHtml(r.pc_name)}</span></td><td class="num">${n(r.n)}</td></tr>`).join('')}</table>`
            : '<div class="rp-empty">Bu dönemde yüksek riskli olay yok.</div>';
    }
    $('rpDays').addEventListener('change', loadSummary);

    // ---- YAZILIM ----
    let swTimer;
    async function loadSoftware() {
        let d;
        try { d = await api('/api/software?q=' + encodeURIComponent($('swQ').value.trim())); }
        catch (e) { $('swTable').innerHTML = `<tr><td class="rp-empty">Yazılım listesi alınamadı: ${escapeHtml(e.message)}</td></tr>`; return; }
        $('swMeta').textContent = d.reporting_devices ? `${n(d.reporting_devices)} cihaz bildiriyor · ${n(d.items.length)} program listelendi` : '';
        if (!d.reporting_devices) {
            $('swTable').innerHTML = '<tr><td class="rp-empty">Henüz yazılım bildiren ajan yok. Kurulu program listesi, bu özelliği destekleyen ajanlardan gelir (0.1.5-alpha ve sonrası).</td></tr>';
            return;
        }
        $('swTable').innerHTML = `<tr><th>Program</th><th>Yayıncı</th><th>Sürümler</th><th style="text-align:right">Cihaz</th></tr>` +
            (d.items.map(r => `<tr class="click" data-name="${escapeHtml(r.name)}"><td>${escapeHtml(r.name)}</td><td>${escapeHtml(r.publisher || '—')}</td>
                <td>${escapeHtml((r.versions || []).filter(Boolean).slice(0, 4).join(', '))}${(r.versions || []).length > 4 ? ' …' : ''}</td><td class="num">${n(r.devices)}</td></tr>`).join('')
             || '<tr><td colspan="4" class="rp-empty">Eşleşen program yok.</td></tr>');
        $('swTable').querySelectorAll('tr.click').forEach(tr => tr.addEventListener('click', () => toggleDevices(tr)));
    }
    async function toggleDevices(tr) {
        const next = tr.nextElementSibling;
        if (next && next.classList.contains('sw-dev')) { next.remove(); return; }
        const rows = await api('/api/software/devices?name=' + encodeURIComponent(tr.dataset.name)).catch(() => []);
        const el = document.createElement('tr');
        el.className = 'sw-dev';
        el.innerHTML = `<td colspan="4" style="background:var(--bg-surface-2);">${rows.map(r =>
            `<span class="pill" style="margin:0.15rem;">${escapeHtml(devName(r))} · ${escapeHtml(r.lab_name || 'sınıfsız')} · ${escapeHtml(r.version || '?')}</span>`).join('') || 'Cihaz yok.'}</td>`;
        tr.after(el);
    }
    $('swQ').addEventListener('input', () => { clearTimeout(swTimer); swTimer = setTimeout(loadSoftware, 300); });

    // ---- WINDOWS GÜNCELLEMELERİ ----
    let patchRows = [];
    async function loadPatches() {
        try { patchRows = await api('/api/patches'); }
        catch (e) { $('ptTable').innerHTML = `<tr><td class="rp-empty">Liste alınamadı: ${escapeHtml(e.message)}</td></tr>`; return; }
        const rep = patchRows.filter(r => r.reported).length;
        $('ptMeta').textContent = `${n(rep)} / ${n(patchRows.length)} cihaz bildiriyor`;
        if (!canAct) $('ptActions').querySelector('.rp-row').style.display = 'none';
        $('ptTable').innerHTML = `<tr>${canAct ? '<th><input type="checkbox" id="ptAllChk" aria-label="Açık cihazların hepsini seç"></th>' : ''}<th>Cihaz</th><th>Durum</th><th style="text-align:right">Bekleyen</th><th style="text-align:right">Güvenlik</th><th style="text-align:right">Kritik</th><th>Yeniden başlatma</th><th>Son tarama</th><th>Son sonuç</th></tr>` +
            (patchRows.map(r => {
                const on = String(r.status || '').toLowerCase() === 'online';
                const st = !r.reported ? '<span class="pill">bildirmedi</span>'
                    : r.pending_critical ? '<span class="pill bad"><i class="fas fa-circle-xmark"></i> kritik eksik</span>'
                    : r.pending_security ? '<span class="pill warn"><i class="fas fa-triangle-exclamation"></i> güvenlik eksik</span>'
                    : r.pending_count ? '<span class="pill">güncelleme var</span>' : '<span class="pill ok"><i class="fas fa-circle-check"></i> güncel</span>';
                const kbs = (r.updates || []).slice(0, 8).map(u => (u.kb ? u.kb + ' ' : '') + u.title).join('\n');
                return `<tr>${canAct ? `<td><input type="checkbox" value="${escapeHtml(r.pc_name)}" ${on ? '' : 'disabled title="Çevrimdışı"'}></td>` : ''}
                    <td>${escapeHtml(devName(r))}<div style="color:var(--text-tertiary);font-size:0.75rem;">${escapeHtml(r.lab_name || 'sınıfsız')} · ${on ? 'açık' : 'kapalı'} · ${escapeHtml(r.agent_version || '?')}</div></td>
                    <td>${st}</td><td class="num" title="${escapeHtml(kbs)}">${r.reported ? n(r.pending_count) : '—'}</td><td class="num">${r.reported ? n(r.pending_security) : '—'}</td>
                    <td class="num">${r.reported ? n(r.pending_critical) : '—'}</td><td>${r.reported ? (r.reboot_required ? '<span class="pill warn"><i class="fas fa-power-off"></i> gerekli</span>' : 'hayır') : '—'}</td>
                    <td>${fmt(r.last_search)}</td><td>${escapeHtml(r.last_result || '—')}</td></tr>`;
            }).join('') || '<tr><td colspan="9" class="rp-empty">Cihaz yok.</td></tr>');
        const boxes = () => [...$('ptTable').querySelectorAll('tbody input[value], tr input[value]')];
        const sync = () => { const any = boxes().some(b => b.checked); ['ptScan', 'ptSec', 'ptAll'].forEach(id => $(id).disabled = !any); };
        $('ptTable').querySelectorAll('input[value]').forEach(b => b.addEventListener('change', sync));
        const all = $('ptAllChk');
        if (all) all.addEventListener('change', () => { boxes().filter(b => !b.disabled).forEach(b => b.checked = all.checked); sync(); });
        sync();
    }
    async function patchCmd(kind, scope) {
        const ids = [...$('ptTable').querySelectorAll('input[value]:checked')].map(b => b.value);
        if (!ids.length) return;
        const label = kind === 'scan' ? 'Windows Update taraması' : (scope === 'security' ? 'güvenlik güncellemelerinin kurulumu' : 'tüm güncellemelerin kurulumu');
        if (!confirm(`${ids.length} cihaz için ${label} istenecek. Devam edilsin mi?`)) return;
        try {
            const d = await api('/api/patches/' + kind, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ target_mode: 'PC', targets: ids, scope }) });
            showToast(`${d.dispatched.length} cihaza gönderildi` + (d.skipped_offline.length ? `, ${d.skipped_offline.length} çevrimdışı atlandı` : '') + '.', 'success');
            setTimeout(loadPatches, 15000);
        } catch (e) { showToast(e.message, 'error'); }
    }
    $('ptScan').addEventListener('click', () => patchCmd('scan', 'security'));
    $('ptSec').addEventListener('click', () => patchCmd('install', 'security'));
    $('ptAll').addEventListener('click', () => patchCmd('install', 'all'));

    loadSummary();
})();
</script>
