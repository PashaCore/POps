// =================================================================
// Raporlar sayfasının betiği (reports.php). Ortak betiklerden (POps.*, POps.dev, state) sonra defer ile yüklenir.
// Sayfa verisi PHP'den <script type="application/json" id="rpData"> ile gelir: { canEditLic }.
// En üstteki saf yardımcılar DOM'a dokunmaz; tests/unit-js/pages.test.mjs sınar (bkz. docs/dashboard.md).
// =================================================================
(function () {
    // ---------------------------------------------------------------- saf yardımcılar
    // Yerel gün anahtarı: 2026-10-05 (sunucunun by_day alanıyla aynı biçim)
    function dayKey(t) { return t.getFullYear() + '-' + String(t.getMonth() + 1).padStart(2, '0') + '-' + String(t.getDate()).padStart(2, '0'); }
    // Windows güncellemesi: bildiren ve eksik güncellemesi ya da yeniden başlatması bekleyen ("Eksik" süzgeci)
    const ptNeeds = (r) => !!(r.reported && (r.pending_count || r.reboot_required));
    function ptState(r) {
        if (!r.reported) return { cls: 'off', word: POps.t('Bildirmedi') };
        if (r.pending_critical) return { cls: 'bad', word: POps.t('Kritik eksik') };
        if (r.pending_security) return { cls: 'warn', word: POps.t('Güvenlik eksik') };
        if (r.pending_count) return { cls: '', word: POps.t('Güncelleme var') };
        return { cls: 'ok', word: POps.t('Güncel') };
    }
    // Node testleri dosyayı bir module nesnesiyle yükler: yalnızca yardımcılar verilir, sayfa kurulmaz
    if (typeof module === 'object' && module && module.exports) { module.exports = { dayKey, ptNeeds, ptState }; return; }

    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const CAN_ACT = dev.canAdmin;
    const PAGE = JSON.parse($('rpData').textContent);
    const CAN_EDIT_LIC = PAGE.canEditLic === true;
    const params = new URLSearchParams(location.search);
    const TABS = ['summary', 'software', 'patches', 'licenses'];
    const ui = { tab: TABS.includes(params.get('tab')) ? params.get('tab') : 'summary', loaded: {}, focus: null };
    const fmtN = (v) => Number(v || 0).toLocaleString(POps.locale);
    const nHtml = (v) => escapeHtml(fmtN(v));
    // Sayı kalın: yer tutucuya HTML parçası (POps.tHtml'in üçüncü argümanı)
    const boldHtml = (v) => `<b>${nHtml(v)}</b>`;
    const devName = (r) => r.display_name || r.hostname || r.pc_name || '';
    const labName = (l) => l && l !== dev.UNASSIGNED ? l : POps.t('Atanmamış');
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
            { header: POps.t('CSV olarak indir') },
            { label: POps.t('Cihazlar'), icon: 'devices', onClick: () => go('devices') },
            { label: POps.t('Yazılımlar'), icon: 'package', onClick: () => go('software') },
            { label: POps.t('Windows güncellemeleri'), icon: 'shield', onClick: () => go('patches') },
            { label: POps.t('Lisanslar'), icon: 'key', onClick: () => go('licenses') },
            { label: POps.tn('Olaylar (son {n} gün)', Number(days)), icon: 'list', onClick: () => go('events') }
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
        success: ['ok', POps.t('Başarılı')], rolled_back: ['warn', POps.t('Geri alındı')], pending_reboot: ['warn', POps.t('Yeniden başlatma bekliyor')],
        rollback_pending_reboot: ['warn', POps.t('Geri alma yeniden başlatma bekliyor')], install_failed: ['', POps.t('Başlamadı, değişiklik yok')],
        rollback_failed: ['bad', POps.t('Geri alma başarısız')], failed: ['bad', POps.t('Başarısız')], error: ['bad', POps.t('Hata')], rejected: ['bad', POps.t('Reddedildi')]
    };
    async function loadSummary() {
        let d;
        try { d = await POps.get('/api/reports/summary?days=' + encodeURIComponent($('rpDays').value)); }
        catch (e) { POps.setError($('kpis'), e); $('kpis').firstElementChild.style.gridColumn = '1 / -1'; ['dayChart', 'versions', 'updates', 'topPolicy', 'topDevices'].forEach(id => { $(id).textContent = '—'; }); return; }
        const dv = d.devices || {}, p = d.patches || {}, sw = d.software || {}, ev = (d.events && d.events.by_risk) || {};
        const labs = (dv.labs || []).filter(l => l.lab && l.lab !== UNASSIGNED_KEY).length;
        $('rpSummary').innerHTML = `<span class="sum">${POps.tnHtml('{n} cihaz', dv.total, null, { n: boldHtml(dv.total) })}</span><span class="sum">${POps.tnHtml('{n} sınıf', labs, null, { n: boldHtml(labs) })}</span>`
            + `<span class="sum">${POps.tnHtml('son {n} gün', d.days, null, { n: boldHtml(d.days) })}</span><span class="sum">${POps.tHtml('hazırlandı {time}', null, { time: POps.timeHtml(d.generated_at) })}</span>`;

        // Açık/boşta/kapalı sayıları Cihazlar sayfasıyla aynı kuralla (sunucu özeti boştakileri kapalı sayar)
        await devReady;
        const list = state.devicesLoaded ? (state.devices || []) : null;
        const onN = list ? list.filter(x => dev.state(x).cls === 'on').length : (dv.online || 0);
        const idleN = list ? list.filter(x => dev.state(x).cls === 'idle').length : 0;
        const off = (list ? list.length : (dv.total || 0)) - onN - idleN;
        const notReported = (dv.total || 0) - (p.reporting || 0);
        const unenrolled = (dv.total || 0) - (dv.enrolled || 0);
        const highCrit = (ev.high || 0) + (ev.critical || 0);
        // Sayı yer tutucusu: n çoğul biçimini seçer, gösterilen sayı biçimlenmiş HTML'dir (nHtml)
        const cntHtml = (text, v) => POps.tnHtml(text, Number(v || 0), null, { n: nHtml(v) });
        const secFootHtml = p.reporting
            ? (p.pending_critical ? `<span class="dot bad"></span>${cntHtml('{n} kritik', p.pending_critical)}` : POps.tHtml('Kritik eksik yok')) + (p.reboot_required ? ` · ${cntHtml('{n} yeniden başlatma', p.reboot_required)}` : '') + (notReported ? ` · ${cntHtml('{n} bildirmedi', notReported)}` : '')
            : POps.tHtml('Henüz bildiren bilgisayar yok');
        $('kpis').innerHTML = [
            kpiHtml(POps.t('Cihaz'), nHtml(dv.total), `<span class="dot on"></span>${cntHtml('{n} açık', onN)}` + (idleN ? ` · ${cntHtml('{n} boşta', idleN)}` : '') + ` · ${cntHtml('{n} kapalı', off)}`, { page: 'devices' }),
            kpiHtml(POps.t('Kayıtlı ajan'), `${nHtml(dv.enrolled)} <small>/ ${nHtml(dv.total)}</small>`,
                unenrolled ? `<span class="dot warn"></span>${cntHtml('{n} ajan anahtarsız', unenrolled)}` : `<span class="dot ok"></span>${POps.tHtml('Hepsi anahtarlı')}`, null),
            kpiHtml(POps.t('Karantinada'), nHtml(dv.quarantined), dv.quarantined ? `<span class="dot bad"></span>${POps.tHtml('Kullanımı kilitli bilgisayar var')}` : POps.tHtml('Yok'), dv.quarantined ? { page: 'devices', f: 'issue' } : null),
            kpiHtml(POps.t('Güvenlik güncellemesi bekleyen'), nHtml(p.pending_security), secFootHtml, { tab: 'patches' }),
            kpiHtml(POps.t('Yazılım bildiren bilgisayar'), nHtml(sw.reporting_devices), cntHtml('{n} farklı program', sw.titles), { tab: 'software' }),
            kpiHtml(POps.t('Yüksek ve kritik olay'), nHtml(highCrit), `${cntHtml('son {n} gün', d.days)} · ${cntHtml('{n} kritik', ev.critical || 0)} · ${cntHtml('{n} orta', ev.medium || 0)}`, { page: 'logger', f: 'warn' })
        ].join('');

        // Günlük seri: dönemdeki her gün (olay olmayan günler 0)
        const byDay = {};
        ((d.events && d.events.by_day) || []).forEach(r => { byDay[r.day] = (r.critical || 0) + (r.high || 0); });
        const days = [];
        for (let i = d.days - 1; i >= 0; i--) {
            const t = new Date(); t.setDate(t.getDate() - i);
            days.push({ v: byDay[dayKey(t)] || 0, label: t.toLocaleDateString(POps.locale, { day: 'numeric', month: 'short' }) });
        }
        const max = Math.max(1, ...days.map(x => x.v));
        const total = days.reduce((a, x) => a + x.v, 0);
        $('dayMeta').textContent = total ? POps.tn('Son {days} günde {total} olay · en yoğun gün {max}', total, { days: fmtN(d.days), total: fmtN(total), max: fmtN(max) }) : POps.tn('Son {n} gün', d.days);
        const mid = days[Math.floor(days.length / 2)];
        $('dayChart').innerHTML = total
            ? `<div class="daychart" role="img" aria-label="${escapeHtml(POps.t('Son {days} günde günlük yüksek ve kritik olay sayısı; toplam {total}', { days: fmtN(d.days), total: fmtN(total) }))}">
                <div class="grid"><span>${nHtml(max)}</span></div>
                ${days.map(x => `<div class="col${x.v ? '' : ' zero'}" data-tip="${escapeHtml(POps.tn('{date} · {count} olay', x.v, { date: x.label, count: fmtN(x.v) }))}" data-tip-pos="up"><div class="bar" style="height:${Number((x.v / max) * 100).toFixed(1)}%"></div></div>`).join('')}
               </div>
               <div class="daychart-axis"><span>${escapeHtml(days[0].label)}</span><span>${escapeHtml(mid.label)}</span><span>${escapeHtml(days[days.length - 1].label)}</span></div>`
            : `<div class="rp-calm"><span class="dot ok"></span>${POps.tHtml('Bu dönemde yüksek ya da kritik olay yok.')}</div>`;

        const versions = d.versions || [];
        const vmax = Math.max(1, ...versions.map(v => v.devices));
        $('versions').innerHTML = versions.length ? versions.map(v => `
            <div class="hbar"><span class="k" title="${escapeHtml(v.version)}">${escapeHtml(v.version === 'bilinmiyor' ? POps.t('Bilinmiyor') : v.version)}</span>
            <div class="t"><div class="f" style="width:${Number((v.devices / vmax) * 100).toFixed(1)}%"></div></div><span class="n">${nHtml(v.devices)}</span></div>`).join('')
            : `<div class="rp-calm">${POps.tHtml('Bilgisayar yok.')}</div>`;

        const ups = Object.entries(d.updates || {}).sort((a, b) => b[1] - a[1]);
        $('updMeta').textContent = POps.tn('Son {n} gün', d.days);
        $('updates').innerHTML = ups.length ? ups.map(([k, c]) => {
            const u = UPD[k] || ['', POps.t('Bilinmeyen sonuç')];
            return `<div class="rp-li"><span class="k"><span class="dot ${escapeHtml(u[0])}"></span><span title="${escapeHtml(k)}">${escapeHtml(u[1])}</span></span><span class="n">${nHtml(c)}</span></div>`;
        }).join('') : `<div class="rp-calm">${POps.tHtml('Bu dönemde ajan güncellemesi olmadı.')}</div>`;

        const tp = (d.events && d.events.top_policy) || [];
        $('topPolicy').innerHTML = tp.length ? tp.map(r => `<div class="rp-li"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px"><span>${escapeHtml(r.domain || '—')}</span><span class="sub">${escapeHtml(r.category || POps.t('kategori yok'))}</span></span><span class="n">${nHtml(r.n)}</span></div>`).join('')
            : `<div class="rp-calm"><span class="dot ok"></span>${POps.tHtml('Bu dönemde engellenen site yok.')}</div>`;
        const td = (d.events && d.events.top_devices) || [];
        $('topDevices').innerHTML = td.length ? td.map(r => {
            const lab = (dev.find(r.pc_name) || {}).lab;
            return `<a class="rp-li" href="logger?pc=${encodeURIComponent(r.pc_name)}&amp;f=warn" title="${escapeHtml(POps.t('Kayıtlarda göster'))}"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px"><span>${escapeHtml(devName(r))}</span><span class="sub">${escapeHtml(lab ? labName(lab) : r.pc_name)}</span></span><span class="n">${nHtml(r.n)}</span></a>`;
        }).join('') : `<div class="rp-calm"><span class="dot ok"></span>${POps.tHtml('Bu dönemde uyarı alan bilgisayar yok.')}</div>`;
    }
    $('rpDays').addEventListener('change', loadSummary);
    $('kpis').addEventListener('click', (e) => { const b = e.target.closest('button[data-go]'); if (b) setTab(b.dataset.go); });

    // ---------------------------------------------------------------- ayrıntı paneli (yazılım, güncelleme, lisans)
    function drawerHeadHtml(title, subText, dotCls, icon) {
        return `<div class="drawer-head">
            <div class="drawer-title"><span class="drawer-ico">${POps.iconHtml(icon, 'lg')}</span>
                <div style="min-width:0"><h2>${escapeHtml(title)}</h2>${subText ? `<div class="sub">${dotCls !== null ? `<span class="dot ${escapeHtml(dotCls)}"></span>` : ''}${escapeHtml(subText)}</div>` : ''}</div></div>
            <button type="button" class="ibtn sm" data-rp="close" data-tip="${escapeHtml(POps.t('Kapat (Esc)'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button>
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
        if (!rows.length) return `<div class="rp-calm">${POps.tHtml('Eşleşen bilgisayar yok.')}</div>`;
        return `<div class="rp-dlist">${rows.map(r => `<a href="#" class="rp-li" data-rp="pc" data-host="${escapeHtml(r.pc_name)}"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px;white-space:normal"><span>${escapeHtml(devName(r))}</span><span class="sub" style="white-space:normal">${escapeHtml([labName(r.lab_name), lineOf(r)].filter(Boolean).join(' · '))}</span></span>${POps.iconHtml('right', 'sm')}</a>`).join('')}</div>`;
    }
    document.addEventListener('click', (e) => { if (e.target.closest('#popsDrawer a[data-rp="pc"]')) e.preventDefault(); });

    // ---------------------------------------------------------------- YAZILIM
    let swTimer = null, swItems = [], swSeq = 0;
    async function loadSoftware() {
        const body = $('swBody');
        if (!swItems.length) loadingRow(body, 3, POps.t('Programlar yükleniyor…'));
        const seq = ++swSeq;
        let d;
        try { d = await POps.get('/api/software?q=' + encodeURIComponent($('swQ').value.trim())); }
        catch (e) { if (seq === swSeq) { POps.setError(body, e, { tag: 'tr', colspan: 3 }); $('swMeta').textContent = ''; } return; }
        if (seq !== swSeq) return;
        swItems = d.items || [];
        $('swMeta').innerHTML = d.reporting_devices ? `${POps.tnHtml('{n} bilgisayar bildiriyor', d.reporting_devices, null, { n: boldHtml(d.reporting_devices) })} · ${POps.tnHtml('{n} program', swItems.length, null, { n: boldHtml(swItems.length) })}` : '';
        if (!d.reporting_devices) {
            POps.setEmpty(body, { tag: 'tr', colspan: 3, icon: 'package', title: POps.t('Henüz yazılım bildiren bilgisayar yok'), text: POps.t('Kurulu programlar 0.1.5 ve sonraki ajanlardan gelir.') });
            return;
        }
        if (!swItems.length) {
            POps.setEmpty(body, { tag: 'tr', colspan: 3, icon: 'filter', title: POps.t('Süzgece uyan program yok'), text: POps.t('Başka bir ad ya da yayıncı deneyin.') });
            return;
        }
        body.innerHTML = swItems.map((r, i) => {
            const vs = (r.versions || []).filter(Boolean);
            return `<tr data-key="sw:${escapeHtml(r.name)}" data-i="${Number(i)}">
                <td><div class="nm">${escapeHtml(r.name)}</div><div class="sub">${escapeHtml(r.publisher || POps.t('Yayıncı bilinmiyor'))}</div></td>
                <td class="faint">${escapeHtml(vs.slice(0, 3).join(', '))}${vs.length > 3 ? ` <span class="faint">${POps.tnHtml('ve {n} sürüm daha', vs.length - 3)}</span>` : ''}</td>
                <td class="num">${nHtml(r.devices)}</td></tr>`;
        }).join('');
        markFocus();
    }
    async function openSoftware(r) {
        const vs = (r.versions || []).filter(Boolean);
        const headHtml = drawerHeadHtml(r.name, r.publisher || POps.t('Yayıncı bilinmiyor'), null, 'package');
        const factsHtml = glistHtml([[POps.t('Kurulu olduğu bilgisayar'), fmtN(r.devices)], [POps.t('Sürüm'), vs.length ? vs.join(', ') : '—']]);
        const body = openDrawer('sw:' + r.name, headHtml + factsHtml + `<div><h3>${POps.tHtml('Bilgisayarlar')}</h3><div id="rpDList"><div class="rp-calm">${POps.tHtml('Yükleniyor…')}</div></div></div>`);
        let rows;
        try { rows = await POps.get('/api/software/devices?name=' + encodeURIComponent(r.name)); }
        catch (e) { if (POps.drawer.isOpen('sw:' + r.name)) body.querySelector('#rpDList').textContent = POps.t('Liste alınamadı: {error}', { error: POps.errorMessage(e) }); return; }
        if (POps.drawer.isOpen('sw:' + r.name)) body.querySelector('#rpDList').innerHTML = deviceListHtml(rows || [], (x) => x.version || POps.t('sürüm yok'));
    }
    $('swBody').addEventListener('click', (e) => { const tr = e.target.closest('tr[data-i]'); if (tr && swItems[tr.dataset.i]) openSoftware(swItems[tr.dataset.i]); });
    $('swQ').addEventListener('input', () => { clearTimeout(swTimer); swTimer = setTimeout(loadSoftware, 300); });

    // ---------------------------------------------------------------- WINDOWS GÜNCELLEMELERİ
    const pt = { rows: [], f: 'all', q: '', sel: new Set() };
    const isOn = (r) => r && !POps.isOffline(r);
    function ptList() {
        const q = pt.q.toLocaleLowerCase('tr');
        return pt.rows.filter(r => {
            if (pt.f === 'need' && !ptNeeds(r)) return false;
            if (pt.f === 'none' && r.reported) return false;
            if (q && ![devName(r), r.pc_name, labName(r.lab_name)].some(v => String(v || '').toLocaleLowerCase('tr').includes(q))) return false;
            return true;
        });
    }
    function ptRowHtml(r) {
        const st = ptState(r);
        const k = r.pc_name;
        const sub = labName(r.lab_name) + ' · ' + (isOn(r) ? POps.t('açık') : POps.t('kapalı')) + (r.agent_version ? ' · ' + r.agent_version : '');
        const pendHtml = r.reported
            ? `${nHtml(r.pending_count)}${r.pending_security || r.pending_critical ? `<div class="sub">${POps.tHtml('{security} güvenlik · {critical} kritik', { security: fmtN(r.pending_security), critical: fmtN(r.pending_critical) })}</div>` : ''}`
            : '<span class="faint">—</span>';
        return `<tr data-key="pt:${escapeHtml(k)}" data-host="${escapeHtml(k)}" class="${pt.sel.has(k) ? 'is-selected' : ''}">
            ${CAN_ACT ? `<td class="check-col"><input type="checkbox" class="pt-cb" data-host="${escapeHtml(k)}" ${pt.sel.has(k) ? 'checked' : ''} aria-label="${escapeHtml(POps.t('{name} seç', { name: devName(r) }))}"></td>` : ''}
            <td><div class="nm">${escapeHtml(devName(r))}</div><div class="sub">${escapeHtml(sub)}</div></td>
            <td><span class="st"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}</span></td>
            <td class="num">${pendHtml}</td>
            <td>${r.reported ? (r.reboot_required ? `<span class="st"><span class="dot warn"></span>${POps.tHtml('Gerekli')}</span>` : `<span class="faint">${POps.tHtml('Hayır')}</span>`) : '<span class="faint">—</span>'}</td>
            <td>${r.last_search ? POps.timeHtml(r.last_search) : '<span class="faint">—</span>'}</td>
        </tr>`;
    }
    function ptScope(rows) { return pt.sel.size ? [...pt.sel] : rows.map(r => r.pc_name); }
    function renderPtBar(rows) {
        const bar = $('ptBar');
        if (!CAN_ACT) { bar.innerHTML = `<span class="scope">${POps.tnHtml('{n} bilgisayar', rows.length, null, { n: `<b>${rows.length}</b>` })}</span>`; return; }
        const n = pt.sel.size;
        const on = ptScope(rows).filter(h => isOn(pt.rows.find(r => r.pc_name === h))).length;
        const tgt = (n ? POps.tn('{n} seçili', n) : POps.tn('listedeki {n}', rows.length)) + ' · ' + POps.tn('{n} açık', on);
        const tip = (label) => escapeHtml(POps.t(label) + ' · ' + tgt);
        bar.innerHTML = (n
            ? `<span class="scope sel">${POps.tnHtml('{n} seçili', n)}<button type="button" data-act="clear" aria-label="${escapeHtml(POps.t('Seçimi temizle'))}" data-tip="${escapeHtml(POps.t('Seçimi temizle'))}">${POps.iconHtml('x', 'sm')}</button></span>`
            : `<span class="scope">${POps.tnHtml('Listedeki {n}', rows.length, null, { n: `<b>${rows.length}</b>` })}</span>`)
            + '<span class="sep"></span>'
            + `<button type="button" class="ibtn" data-act="scan" data-tip="${tip('Güncellemeleri tara')}" aria-label="${escapeHtml(POps.t('Güncellemeleri tara'))}">${POps.iconHtml('refresh')}</button>`
            + `<button type="button" class="ibtn" data-act="security" data-tip="${tip('Güvenlik güncellemelerini kur')}" aria-label="${escapeHtml(POps.t('Güvenlik güncellemelerini kur'))}">${POps.iconHtml('shield')}</button>`
            + `<button type="button" class="ibtn" data-act="all" data-tip="${tip('Bütün güncellemeleri kur')}" aria-label="${escapeHtml(POps.t('Bütün güncellemeleri kur'))}">${POps.iconHtml('download')}</button>`;
    }
    function renderPatches() {
        const rows = ptList();
        pt.sel = new Set([...pt.sel].filter(h => pt.rows.some(r => r.pc_name === h)));
        const all = rows.length && rows.every(r => pt.sel.has(r.pc_name));
        $('ptHead').innerHTML = '<tr>' + (CAN_ACT ? `<th class="check-col"><input type="checkbox" id="ptAll" ${all ? 'checked' : ''} aria-label="${escapeHtml(POps.t('Listedekilerin hepsini seç'))}"></th>` : '')
            + `<th>${POps.tHtml('Bilgisayar')}</th><th>${POps.tHtml('Durum')}</th><th class="num">${POps.tHtml('Bekleyen')}</th><th>${POps.tHtml('Yeniden başlatma')}</th><th>${POps.tHtml('Son tarama')}</th></tr>`;
        const cnt = { all: pt.rows.length, need: pt.rows.filter(ptNeeds).length, none: pt.rows.filter(r => !r.reported).length };
        $('ptFilter').querySelectorAll('button[data-f]').forEach(b => {
            const on = b.dataset.f === pt.f;
            b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false');
            b.replaceChildren(document.createTextNode(POps.t(({ all: 'Tümü', need: 'Eksik', none: 'Bildirmedi' })[b.dataset.f]) + ' '), POps.el('span', { className: 'count', text: String(cnt[b.dataset.f]) }));
        });
        renderPtBar(rows);
        const body = $('ptBody');
        if (!rows.length) {
            POps.setEmpty(body, pt.rows.length
                ? { tag: 'tr', colspan: 6, icon: 'filter', title: POps.t('Süzgece uyan bilgisayar yok'), text: POps.t('Arama ya da süzgeci değiştirin.') }
                : { tag: 'tr', colspan: 6, icon: 'devices', title: POps.t('Henüz bilgisayar yok') });
            return;
        }
        body.innerHTML = rows.map(ptRowHtml).join('');
        markFocus();
    }
    async function loadPatches() {
        if (!pt.rows.length) loadingRow($('ptBody'), 6, POps.t('Güncelleme durumu yükleniyor…'));
        try { pt.rows = await POps.get('/api/patches') || []; }
        catch (e) { POps.setError($('ptBody'), e, { tag: 'tr', colspan: 6 }); $('ptBar').textContent = ''; return; }
        renderPatches();
        if (POps.drawer.isOpen() && String(POps.drawer.key()).startsWith('pt:')) {
            const r = pt.rows.find(x => 'pt:' + x.pc_name === POps.drawer.key());
            if (r) openPatch(r);
        }
    }
    // Kurulum onayı: tek bilgisayar (ad) ya da çok bilgisayar (sayı) için ayrı cümle
    const PT_TEXT = {
        security: {
            one: (name) => POps.t('{name} için güvenlik güncellemeleri kurulsun mu?', { name }),
            many: (n) => POps.tn('{n} bilgisayarda güvenlik güncellemeleri kurulsun mu?', n),
            msg: () => POps.t('Güvenlik ve kritik güncellemeler kurulur. Bilgisayarlar yeniden başlatılmaz; gerekirse listede "Yeniden başlatma: Gerekli" görünür.')
        },
        all: {
            one: (name) => POps.t('{name} için bütün güncellemeler kurulsun mu?', { name }),
            many: (n) => POps.tn('{n} bilgisayarda bütün güncellemeler kurulsun mu?', n),
            msg: () => POps.t('Bekleyen bütün güncellemeler kurulur; isteğe bağlı ve sürüm yükseltme güncellemeleri kurulmaz. Bilgisayarlar yeniden başlatılmaz; gerekirse listede görünür.')
        }
    };
    async function patchCmd(act, hosts, btn) {
        const on = hosts.filter(h => isOn(pt.rows.find(r => r.pc_name === h)));
        const skipped = hosts.length - on.length;
        if (!on.length) return POps.toast('warning', hosts.length === 1 ? POps.t('Bilgisayar kapalı; komut gönderilmedi.') : POps.t('Açık bilgisayar yok; komut gönderilmedi.'));
        if (act !== 'scan') {
            const t = PT_TEXT[act];
            const ok = await POps.confirm({
                title: on.length === 1 ? t.one(devName(pt.rows.find(r => r.pc_name === on[0]))) : t.many(on.length),
                message: t.msg(), note: skipped ? POps.tn('Kapalı {n} bilgisayar atlanacak.', skipped) : '',
                confirmText: on.length === 1 ? POps.t('Güncellemeleri kur') : POps.tn('{n} bilgisayara kur', on.length), icon: 'shield'
            });
            if (!ok) return;
        }
        const kind = act === 'scan' ? 'scan' : 'install';
        const scope = act === 'all' ? 'all' : 'security';
        let d;
        try { d = await POps.busy(btn, () => POps.post('/api/patches/' + kind, { target_mode: 'PC', targets: on, scope })); }
        catch (e) { POps.toast('error', POps.errorMessage(e)); return; }
        const sent = ((d && d.dispatched) || []).length, off = ((d && d.skipped_offline) || []).length + skipped, closed = ((d && d.skipped_module_closed) || []).length;
        const rest = (off ? ' ' + POps.tn('Bağlı olmayan {n} bilgisayar atlandı.', off) : '') + (closed ? ' ' + POps.tn('{n} bilgisayarda Windows güncelleme modülü kapalı.', closed) : '');
        if (!sent) { POps.toast('warning', POps.t('İstek hiçbir bilgisayara gönderilemedi.') + rest); return; }
        POps.toast('success', (act === 'scan' ? POps.tn('Tarama isteği {n} bilgisayara gönderildi; sonuçlar birkaç dakika içinde gelir.', sent) : POps.tn('Kurulum isteği {n} bilgisayara gönderildi; sonuçlar birkaç dakika içinde gelir.', sent)) + rest);
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
                <button type="button" class="circ" data-rp="scan" ${on ? '' : 'disabled'} title="${escapeHtml(POps.t('Windows güncellemelerini tara'))}"><span>${POps.iconHtml('refresh')}</span>${POps.tHtml('Tara')}</button>
                <button type="button" class="circ" data-rp="security" ${on && r.reported ? '' : 'disabled'} title="${escapeHtml(POps.t('Güvenlik ve kritik güncellemeleri kur'))}"><span>${POps.iconHtml('shield')}</span>${POps.tHtml('Güvenlik')}</button>
                <button type="button" class="circ" data-rp="all" ${on && r.reported ? '' : 'disabled'} title="${escapeHtml(POps.t('Bekleyen bütün güncellemeleri kur'))}"><span>${POps.iconHtml('download')}</span>${POps.tHtml('Tümü')}</button>
            </div>` : '';
        const facts = r.reported ? [
            [POps.t('Bekleyen'), fmtN(r.pending_count)], [POps.t('Güvenlik'), fmtN(r.pending_security)], [POps.t('Kritik'), fmtN(r.pending_critical)],
            [POps.t('Yeniden başlatma'), r.reboot_required ? POps.t('Gerekli') : POps.t('Gerekmiyor')], [POps.t('Son sonuç'), r.last_result || '—'],
            [POps.t('Sınıf'), labName(r.lab_name)], [POps.t('Ajan'), r.agent_version || '—']
        ] : [[POps.t('Sınıf'), labName(r.lab_name)], [POps.t('Ajan'), r.agent_version || '—']];
        const times = r.reported ? [[POps.t('Son tarama'), r.last_search], [POps.t('Son kurulum'), r.last_install], [POps.t('Son bildirim'), r.updated_at]] : [];
        const ups = r.updates || [];
        const upsHtml = ups.length ? `<div><h3>${POps.tHtml('Bekleyen güncellemeler')}</h3>${ups.map(u => `<div class="rp-li"><span class="k" style="flex-direction:column;align-items:flex-start;gap:1px;white-space:normal"><span style="white-space:normal">${escapeHtml(u.title || POps.t('Adsız güncelleme'))}</span><span class="sub">${escapeHtml([u.kb, u.severity, u.is_security ? POps.t('güvenlik') : ''].filter(Boolean).join(' · '))}</span></span></div>`).join('')}</div>` : '';
        const noteHtml = r.reported ? '' : `<div class="issue lock">${POps.tHtml('Bu bilgisayarın ajanı Windows güncelleme durumunu bildirmiyor (0.1.5 ve sonrası gerekir).')}</div>`;
        openDrawer('pt:' + r.pc_name, drawerHeadHtml(devName(r), st.word + (on ? '' : ' · ' + POps.t('kapalı')), st.cls, 'shield') + actsHtml + noteHtml + glistHtml(facts, times) + upsHtml
            + `<a href="#" data-rp="pc" data-host="${escapeHtml(r.pc_name)}" style="font-size:var(--text-sm)">${POps.tHtml('Bilgisayarın ayrıntıları')}</a>`, {
            scan: (b) => patchCmd('scan', [r.pc_name], b),
            security: (b) => patchCmd('security', [r.pc_name], b),
            all: (b) => patchCmd('all', [r.pc_name], b)
        });
    }

    // ---------------------------------------------------------------- LİSANSLAR
    const LT = { per_device: POps.t('Cihaz başına'), site: POps.t('Site ya da kampüs'), subscription: POps.t('Abonelik') };
    const LS = { ok: ['ok', POps.t('Uygun')], over: ['bad', POps.t('Aşım')], expiring: ['warn', POps.t('Bitiyor')], expired: ['bad', POps.t('Süresi doldu')] };
    // Özet satırındaki sayaçlar ("2 aşım")
    const LS_COUNT = { over: '{n} aşım', expiring: '{n} bitiyor', expired: '{n} süresi doldu' };
    let licenses = [];
    const fmtDate = (v) => { const d = POps.toDate(v && v.length === 10 ? v + 'T00:00:00' : v); return d ? d.toLocaleDateString(POps.locale, { day: 'numeric', month: 'long', year: 'numeric' }) : '—'; };
    function useHtml(l) {
        if (l.seats == null) return `<span class="faint">${POps.tHtml('{n} kurulu · sınırsız', null, { n: nHtml(l.installed) })}</span>`;
        const pct = Math.min(100, (l.installed / Math.max(1, l.seats)) * 100);
        return `<span class="use"><span class="pbar"><i class="${l.installed > l.seats ? 'bad' : 'ok'}" style="width:${Number(pct).toFixed(1)}%"></i></span><span>${nHtml(l.installed)} / ${nHtml(l.seats)}</span></span>`;
    }
    async function loadLicenses() {
        const body = $('licBody');
        if (!licenses.length) loadingRow(body, 5, POps.t('Lisanslar yükleniyor…'));
        let d;
        try { d = await POps.get('/api/licenses'); }
        catch (e) { POps.setError(body, e, { tag: 'tr', colspan: 5 }); return; }
        licenses = d.items || [];
        const sm = d.summary || {};
        const probHtml = ['over', 'expired', 'expiring'].filter(k => sm[k]).map(k => `<span class="sum"><span class="dot ${escapeHtml(LS[k][0])}"></span>${POps.tnHtml(LS_COUNT[k], Number(sm[k]), null, { n: boldHtml(sm[k]) })}</span>`).join('');
        $('licSummary').innerHTML = licenses.length
            ? `<span class="sum">${POps.tnHtml('{n} lisans', licenses.length, null, { n: `<b>${licenses.length}</b>` })}</span>` + (probHtml || `<span class="sum"><span class="dot ok"></span>${POps.tHtml('Hepsi uygun')}</span>`)
            : '';
        if (!licenses.length) {
            POps.setEmpty(body, { tag: 'tr', colspan: 5, icon: 'key', title: POps.t('Tanımlı lisans yok'), text: CAN_EDIT_LIC ? POps.t('"Lisans ekle" ile koltuk ve bitiş tarihini takip etmeye başlayın.') : POps.t('Bir yönetici lisans eklediğinde burada görünür.') });
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
                <button type="button" class="btn secondary sm" data-rp="edit">${POps.iconHtml('edit', 'sm')}${POps.tHtml('Düzenle')}</button>
                <button type="button" class="btn danger-soft sm" data-rp="del">${POps.iconHtml('trash', 'sm')}${POps.tHtml('Sil')}</button>
            </div>` : '';
        const facts = [
            [POps.t('Eşleşme ifadesi'), '"' + l.match_pattern + '"'], [POps.t('Yayıncı'), l.publisher || POps.t('Her yayıncı')], [POps.t('Tür'), LT[l.license_type] || l.license_type],
            [POps.t('Koltuk'), l.seats == null ? POps.t('Sınırsız') : fmtN(l.seats)], [POps.t('Kurulu'), fmtN(l.installed)],
            [POps.t('Boş koltuk'), l.free == null ? '' : (l.free < 0 ? POps.tn('{count} fazla kurulum', -l.free, { count: fmtN(-l.free) }) : fmtN(l.free))],
            [POps.t('Bitiş'), l.expires_at ? fmtDate(l.expires_at) : POps.t('Yok')], [POps.t('Notlar'), l.notes || ''], [POps.t('Ekleyen'), l.created_by || '']
        ];
        const whyHtml = l.state === 'over' ? `<div class="issue err">${POps.tHtml('Koltuk sayısı aşıldı: {installed} kurulum, {seats} koltuk.', { installed: fmtN(l.installed), seats: fmtN(l.seats) })}</div>`
            : l.state === 'expired' ? `<div class="issue err">${POps.tHtml('Lisansın süresi doldu.')}</div>`
            : l.state === 'expiring' ? `<div class="issue upd">${POps.tHtml('Lisans 30 gün içinde bitiyor.')}</div>` : '';
        const body = openDrawer('lic:' + l.id, drawerHeadHtml(l.name, st[1], st[0], 'key') + actsHtml + whyHtml + glistHtml(facts, [[POps.t('Eklenme'), l.created_at]])
            + `<div><h3>${POps.tHtml('Eşleşen kurulumlar')}</h3><div id="rpDList"><div class="rp-calm">${POps.tHtml('Yükleniyor…')}</div></div></div>`, {
            edit: () => licEdit(l),
            del: (b) => licDelete(l, b)
        });
        let rows;
        try { rows = await POps.get(`/api/licenses/${encodeURIComponent(l.id)}/devices`); }
        catch (e) { if (POps.drawer.isOpen('lic:' + l.id)) body.querySelector('#rpDList').textContent = POps.t('Liste alınamadı: {error}', { error: POps.errorMessage(e) }); return; }
        if (POps.drawer.isOpen('lic:' + l.id)) body.querySelector('#rpDList').innerHTML = deviceListHtml(rows || [], (x) => [x.name, x.version].filter(Boolean).join(' '));
    }
    $('licBody').addEventListener('click', (e) => {
        const tr = e.target.closest('tr[data-id]');
        if (!tr) return;
        const l = licenses.find(x => String(x.id) === tr.dataset.id);
        if (l) openLicense(l);
    });
    async function licDelete(l, btn) {
        const ok = await POps.confirm({ title: POps.t('"{name}" lisansı silinsin mi?', { name: l.name }), message: POps.t('Kurulumlar etkilenmez; yalnızca bu tanım ve koltuk bilgisi silinir.'), confirmText: POps.t('Lisansı sil'), danger: true, icon: 'trash' });
        if (!ok) return;
        if (await POps.act(btn, () => POps.del('/api/licenses/' + encodeURIComponent(l.id)), { success: POps.t('Lisans silindi.') })) { POps.drawer.close(); loadLicenses(); }
    }
    function licEdit(l) {
        if (!CAN_EDIT_LIC) return;
        $('licModalTitle').textContent = l ? POps.t('Lisansı düzenle') : POps.t('Lisans ekle');
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
        if (q.length < 2) { box.textContent = POps.t('Eşleşme ifadesi yazınca uyan programlar burada görünür.'); return; }
        try {
            const d = await POps.get('/api/software?limit=8&q=' + encodeURIComponent(q));
            if (q !== $('lfPattern').value.trim()) return;
            box.textContent = (d.items || []).length
                ? POps.t('Uyan programlar: {list}', { list: d.items.map(i => POps.tn('{name} ({count} bilgisayar)', Number(i.devices || 0), { name: i.name, count: fmtN(i.devices) })).join(', ') })
                : POps.t('Envanterde bu ifadeye uyan program yok (henüz yazılım bildiren ajan olmayabilir).');
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
            if (await POps.act(e.currentTarget, () => POps.post(id ? '/api/licenses/' + encodeURIComponent(id) : '/api/licenses', body), { success: id ? POps.t('Lisans güncellendi.') : POps.t('Lisans eklendi.') })) {
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
