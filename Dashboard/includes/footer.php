            </main>
        </div>
    </div>

    <div class="toast-container" id="toastContainer" aria-live="polite"></div>

    <script>
        // ============ ORTAK ARAYÜZ ============
        // Dar ekranda yan menü
        (function () {
            const sidebar = document.getElementById('appSidebar');
            const overlay = document.getElementById('sidebarOverlay');
            const toggle = document.getElementById('menuToggle');
            function setOpen(open) {
                sidebar.classList.toggle('open', open);
                overlay.classList.toggle('open', open);
                toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
                toggle.setAttribute('aria-label', open ? 'Menüyü kapat' : 'Menüyü aç');
                document.body.style.overflow = open ? 'hidden' : '';
                if (open) { const first = sidebar.querySelector('.nav-item'); if (first) first.focus(); }
            }
            window.toggleMobileSidebar = () => setOpen(!sidebar.classList.contains('open'));
            toggle.addEventListener('click', window.toggleMobileSidebar);
            overlay.addEventListener('click', () => setOpen(false));
            document.addEventListener('keydown', (e) => {
                if (e.key === 'Escape' && sidebar.classList.contains('open')) { setOpen(false); toggle.focus(); }
            });
        })();

        // Komut paleti (Ctrl+K): sayfa, cihaz ve sınıf arar
        (function () {
            const openBtn = document.getElementById('paletteOpen');
            if (!openBtn) return;
            let overlay = null, input = null, list = null, results = [], cur = 0, cache = null, cacheAt = 0;
            const lc = (t) => String(t || '').toLocaleLowerCase('tr');
            const pages = [...document.querySelectorAll('.sidebar-nav .nav-item')].map(a => ({
                kind: 'page', label: a.querySelector('span').textContent.trim(), href: a.getAttribute('href'), icon: (a.querySelector('use') || { getAttribute: () => '' }).getAttribute('href').split('#i-')[1] || 'right'
            }));
            async function data() {
                if (cache && Date.now() - cacheAt < 30000) return cache;
                const [devs, labs] = await Promise.all([POps.get('/api/devices').catch(() => []), POps.get('/api/custom_labs').catch(() => [])]);
                const labSet = new Set((Array.isArray(labs) ? labs : []).map(l => typeof l === 'string' ? l : (l && l.name)).filter(Boolean));
                (Array.isArray(devs) ? devs : []).forEach(d => { if (d.lab) labSet.add(d.lab); });
                cache = {
                    devices: (Array.isArray(devs) ? devs : []).map(d => ({ kind: 'pc', label: POps.deviceName(d), sub: [d.lab, POps.isOnline(d) ? 'çevrimiçi' : 'kapalı'].filter(Boolean).join(' · '), href: 'devices.php?pc=' + encodeURIComponent(d.hostname), icon: 'monitor', key: lc(POps.deviceName(d) + ' ' + d.hostname + ' ' + (d.ip || '')) })),
                    labs: [...labSet].map(n => ({ kind: 'lab', label: n, sub: 'Sınıf', href: 'labs.php?lab=' + encodeURIComponent(n), icon: 'labs', key: lc(n) }))
                };
                cacheAt = Date.now();
                return cache;
            }
            function rank(items, q, n) {
                if (!q) return items.slice(0, n);
                return items.filter(i => (i.key || lc(i.label)).includes(q)).sort((a, b) => ((a.key || lc(a.label)).startsWith(q) ? 0 : 1) - ((b.key || lc(b.label)).startsWith(q) ? 0 : 1)).slice(0, n);
            }
            async function render() {
                const q = lc(input.value.trim());
                const groups = [['Sayfalar', rank(pages.map(p => Object.assign({ key: lc(p.label) }, p)), q, q ? 6 : 13)]];
                if (q) {
                    const d = await data();
                    if (lc(input.value.trim()) !== q) return;
                    groups.push(['Cihazlar', rank(d.devices, q, 8)], ['Sınıflar', rank(d.labs, q, 5)]);
                }
                results = [];
                list.replaceChildren();
                groups.forEach(([title, items]) => {
                    if (!items.length) return;
                    list.append(POps.el('div', { className: 'palette-h', text: title }));
                    items.forEach(it => {
                        const idx = results.length;
                        const b = POps.el('button', { type: 'button', className: 'palette-item', role: 'option' }, [POps.iconEl(it.icon), POps.el('span', { text: it.label }), it.sub ? POps.el('span', { className: 'pi-sub', text: it.sub }) : null]);
                        b.addEventListener('mousemove', () => select(idx));
                        b.addEventListener('click', () => go(idx));
                        list.append(b);
                        results.push({ it, b });
                    });
                });
                if (!results.length) list.append(POps.el('div', { className: 'palette-empty', text: 'Sonuç yok.' }));
                select(0);
            }
            function select(i) {
                if (!results.length) return;
                cur = (i + results.length) % results.length;
                results.forEach((r, k) => r.b.classList.toggle('cur', k === cur));
                results[cur].b.scrollIntoView({ block: 'nearest' });
            }
            function go(i) { const r = results[i]; if (r) window.location.href = r.it.href; }
            function close() { if (overlay) { overlay.remove(); overlay = null; openBtn.focus(); } }
            function open() {
                if (overlay) return;
                input = POps.el('input', { type: 'text', placeholder: 'Sayfa, cihaz ya da sınıf ara', 'aria-label': 'Ara', autocomplete: 'off', spellcheck: 'false' });
                list = POps.el('div', { className: 'palette-list', role: 'listbox' });
                const box = POps.el('div', { className: 'palette', role: 'dialog', 'aria-modal': 'true', 'aria-label': 'Ara' }, [POps.el('div', { className: 'palette-in' }, [POps.iconEl('search'), input]), list]);
                overlay = POps.el('div', { className: 'palette-overlay' }, [box]);
                overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) close(); });
                let t = null;
                input.addEventListener('input', () => { clearTimeout(t); t = setTimeout(render, 120); });
                input.addEventListener('keydown', (e) => {
                    if (e.key === 'ArrowDown') { e.preventDefault(); select(cur + 1); }
                    else if (e.key === 'ArrowUp') { e.preventDefault(); select(cur - 1); }
                    else if (e.key === 'Enter') { e.preventDefault(); go(cur); }
                    else if (e.key === 'Escape') { e.preventDefault(); close(); }
                });
                document.body.append(overlay);
                input.focus();
                render();
            }
            openBtn.addEventListener('click', open);
            document.addEventListener('keydown', (e) => {
                if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 'k' || e.key === 'K')) { e.preventDefault(); overlay ? close() : open(); }
            });
        })();

        // İşlem merkezi: yan menünün altındaki özet ve açılan liste (POps.jobs)
        (function () {
            const btn = document.getElementById('jobsBtn'), panel = document.getElementById('jobsPanel');
            if (!btn || !panel || !POps.jobs) return;
            function render() {
                const jobs = POps.jobs.list();
                btn.hidden = !jobs.length;
                if (!jobs.length) { panel.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); return; }
                let total = 0, done = 0, running = 0;
                jobs.forEach(j => { const c = POps.jobs.counts(j); total += c.total; done += c.ok + c.bad; if (!j.doneAt) running += 1; });
                const pct = total ? Math.round(done / total * 100) : 0;
                const bar = POps.el('div', { className: 'pbar' }, [POps.el('i', { className: running ? 'run' : 'ok' })]);
                bar.firstChild.style.width = (running ? pct : 100) + '%';
                btn.replaceChildren(POps.el('div', { className: 't' }, [POps.el('span', { text: running ? `${running} işlem sürüyor` : 'İşlemler tamamlandı' }), POps.el('span', { text: running ? '%' + pct : '' })]), bar);
                const rows = jobs.map(j => {
                    const c = POps.jobs.counts(j);
                    const parts = [['ok', c.ok], ['bad', c.bad], ['run', c.run]].filter(x => x[1]).map(([k, n]) => { const i = POps.el('i', { className: k }); i.style.width = (n / c.total * 100) + '%'; return i; });
                    const bad = j.ids.map(id => j.items[id]).filter(t => t && POps.taskState(t.status) === 'bad').map(t => t.pc).filter(Boolean);
                    const word = j.doneAt ? (c.bad ? `${c.bad} başarısız` : 'Tamamlandı') : `${c.ok + c.bad}/${c.total}`;
                    return POps.el('div', { className: 'job' }, [
                        POps.el('div', { className: 'jt' }, [POps.el('b', { text: j.title }), POps.el('span', { className: 'word ' + (j.doneAt ? (c.bad ? 'bad' : 'ok') : 'run'), text: word })]),
                        POps.el('div', { className: 'jm', text: `${c.total} cihaz · ${POps.relTime(j.at)}` + (bad.length ? ' · başarısız: ' + bad.slice(0, 4).join(', ') + (bad.length > 4 ? ` +${bad.length - 4}` : '') : '') }),
                        POps.el('div', { className: 'pbar' }, parts)
                    ]);
                });
                const clearBtn = POps.el('button', { type: 'button', className: 'btn ghost sm', text: 'Bitenleri temizle' });
                clearBtn.addEventListener('click', (e) => { e.stopPropagation(); POps.jobs.clearDone(); });
                const foot = POps.el('div', { className: 'job', style: 'display:flex;justify-content:space-between;align-items:center' }, [
                    POps.el('a', { href: 'tasks.php', text: 'Bütün işlemler' }),
                    clearBtn
                ]);
                panel.replaceChildren(...rows, foot);
            }
            btn.addEventListener('click', (e) => { e.stopPropagation(); const open = !panel.classList.contains('open'); panel.classList.toggle('open', open); btn.setAttribute('aria-expanded', open ? 'true' : 'false'); });
            document.addEventListener('click', (e) => { if (panel.classList.contains('open') && !panel.contains(e.target) && !btn.contains(e.target)) { panel.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); } });
            document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && panel.classList.contains('open')) { panel.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); btn.focus(); } });
            document.addEventListener('pops_jobs', render);
            setInterval(() => { if (!document.hidden && POps.jobs.list().length) render(); }, 30000);
            POps.jobs.start();
        })();

        // Bildirim zili (admin ve superadmin): okunmamış sayısı dakikada bir yenilenir
        (function () {
            if (!['admin', 'superadmin'].includes(window.USER_ROLE)) return;
            const wrap = document.getElementById('notifWrap');
            if (!wrap) return;
            wrap.hidden = false;
            const btn = document.getElementById('notifBtn'), panel = document.getElementById('notifPanel');
            const list = document.getElementById('notifList'), count = document.getElementById('notifCount');
            const when = (iso) => { try { return new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }); } catch (e) { return ''; } };
            async function load() {
                const d = await POps.get('/api/notifications?limit=30');
                count.textContent = d.unread > 99 ? '99+' : d.unread;
                count.style.display = d.unread ? 'inline-block' : 'none';
                btn.setAttribute('aria-label', d.unread ? `Bildirimler (${d.unread} okunmamış)` : 'Bildirimler');
                list.innerHTML = (d.items || []).length ? d.items.map(n => `
                    <div class="notif-item${n.is_read ? '' : ' unread'}">
                        <span class="sev ${escapeHtml(n.severity)}"></span>
                        <div><div class="t">${escapeHtml(n.title)}</div>
                        <div class="m">${escapeHtml(when(n.created_at))}${n.pc_name ? ' · ' + escapeHtml(n.pc_name) : ''}${n.detail ? ' · ' + escapeHtml(n.detail) : ''}${n.delivery_error ? ` · <span title="${escapeHtml(n.delivery_error)}" style="color:var(--danger-text);">gönderilemedi</span>` : ''}</div></div>
                    </div>`).join('') : '<div class="notif-empty">Bildirim yok.</div>';
            }
            function setOpen(open) {
                panel.classList.toggle('open', open);
                btn.setAttribute('aria-expanded', open ? 'true' : 'false');
                if (open) load().catch(e => { list.innerHTML = '<div class="notif-empty">Bildirimler alınamadı.</div>'; });
            }
            btn.addEventListener('click', (e) => { e.stopPropagation(); setOpen(!panel.classList.contains('open')); });
            document.addEventListener('click', (e) => { if (!wrap.contains(e.target) && !e.target.closest('.pops-dialog-overlay')) setOpen(false); });
            document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && panel.classList.contains('open') && !document.querySelector('.pops-dialog-overlay')) { setOpen(false); btn.focus(); } });
            document.getElementById('notifClear').addEventListener('click', async function (e) {
                e.stopPropagation();
                const ok = await POps.confirm({ title: 'Okunmuş bildirimler silinsin mi?', message: 'Zildeki okunmuş bildirimler silinir. Olayların kendisi denetim kayıtlarında kalır.', confirmText: 'Sil', danger: true, icon: 'fa-trash' });
                if (!ok) return;
                let deleted = 0;
                if (await POps.act(this, async () => { deleted = (await POps.post('/api/notifications/clear', { ids: [] })).deleted || 0; })) {
                    POps.toast('success', deleted ? `${deleted} bildirim silindi.` : 'Silinecek okunmuş bildirim yok.');
                    load().catch(() => {});
                }
            });
            document.getElementById('notifReadAll').addEventListener('click', async function (e) {
                e.stopPropagation();
                if (await POps.act(this, () => POps.post('/api/notifications/read', { ids: [] }))) load().catch(() => {});
            });
            load().catch(() => {});
            popsPoll(load, 60000);
        })();
    </script>
</body>
</html>
