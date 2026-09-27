     </main>
        </div>
    </div>

    <div class="toast-container" id="toastContainer"></div>

    <script src="assets/pops_config.js?v=<?php echo time(); ?>"></script>
    <script src="assets/pops_script.js?v=<?php echo time(); ?>"></script>
    <script>
        // ============ GLOBAL UI SCRIPTS ============
        function toggleMobileSidebar() {
            const sidebar = document.getElementById('appSidebar');
            const overlay = document.getElementById('sidebarOverlay');
            const isOpen = sidebar.classList.contains('open');
            if (isOpen) {
                sidebar.classList.remove('open');
                if (overlay) { overlay.classList.remove('open'); document.body.style.overflow = ''; }
            } else {
                sidebar.classList.add('open');
                if (overlay) { overlay.classList.add('open'); document.body.style.overflow = 'hidden'; }
            }
        }

        // Saat güncelleyici
        (function() {
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

        // Modal kapatma yardımcıları
        window.closeModal = function(id) {
            const el = typeof id === 'string' ? document.getElementById(id) : id;
            if (el) el.classList.remove('open');
        };
        window.openModal = function(id) {
            const el = typeof id === 'string' ? document.getElementById(id) : id;
            if (el) el.classList.add('open');
        };

        // Bildirim zili (admin ve superadmin): okunmamış sayısı dakikada bir yenilenir
        (function() {
            if (!['admin', 'superadmin'].includes(window.USER_ROLE)) return;
            const wrap = document.getElementById('notifWrap');
            if (!wrap) return;
            wrap.style.display = '';
            const btn = document.getElementById('notifBtn'), panel = document.getElementById('notifPanel');
            const list = document.getElementById('notifList'), count = document.getElementById('notifCount');
            const when = (iso) => { try { return new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }); } catch (e) { return ''; } };
            async function load() {
                try {
                    const r = await fetch('/api/notifications?limit=30');
                    if (!r.ok) return;
                    const d = await r.json();
                    count.textContent = d.unread > 99 ? '99+' : d.unread;
                    count.style.display = d.unread ? 'block' : 'none';
                    list.innerHTML = (d.items || []).length ? d.items.map(n => `
                        <div class="notif-item${n.is_read ? '' : ' unread'}">
                            <span class="sev ${escapeHtml(n.severity)}"></span>
                            <div><div class="t">${escapeHtml(n.title)}</div>
                            <div class="m">${escapeHtml(when(n.created_at))}${n.pc_name ? ' · ' + escapeHtml(n.pc_name) : ''}${n.detail ? ' · ' + escapeHtml(n.detail) : ''}${n.delivery_error ? ' · gönderilemedi' : ''}</div></div>
                        </div>`).join('') : '<div class="notif-empty">Bildirim yok.</div>';
                } catch (e) { /* çevrimdışı: sessiz */ }
            }
            btn.addEventListener('click', (e) => { e.stopPropagation(); panel.classList.toggle('open'); if (panel.classList.contains('open')) load(); });
            document.addEventListener('click', (e) => { if (!wrap.contains(e.target)) panel.classList.remove('open'); });
            document.getElementById('notifReadAll').addEventListener('click', async () => {
                await fetch('/api/notifications/read', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ ids: [] }) });
                load();
            });
            load();
            setInterval(load, 60000);
        })();

        // ESC ile modal kapat
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape') {
                document.querySelectorAll('.modal-overlay.open').forEach(m => m.classList.remove('open'));
            }
        });
    </script>
    <script src="https://cdn.jsdelivr.net/npm/sortablejs@latest/Sortable.min.js"></script>
</body>
</html>