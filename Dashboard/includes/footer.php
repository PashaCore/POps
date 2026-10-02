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

        // Saat
        (function () {
            const timeEl = document.getElementById('topbarTime');
            const dateEl = document.getElementById('topbarDate');
            if (!timeEl) return;
            const update = () => {
                const d = new Date();
                timeEl.textContent = d.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
                dateEl.textContent = d.toLocaleDateString('tr-TR', { day: '2-digit', month: 'long', year: 'numeric' });
            };
            update();
            setInterval(update, 1000);
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
                count.style.display = d.unread ? 'block' : 'none';
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
