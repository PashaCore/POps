     </main>
        </div>
    </div>

    <div class="toast-container" id="toastContainer"></div>

    <script src="assets/pops_config.js?v=<?php echo time(); ?>"></script>
    <script src="assets/pops_script.js?v=<?php echo time(); ?>"></script>
    <script>
        // ============ GLOBAL UI SCRIPTS ============
        function toggleTheme() {
            const html = document.documentElement;
            const isDark = html.getAttribute('data-theme') === 'dark';
            if (isDark) {
                html.removeAttribute('data-theme');
                localStorage.setItem('pops_theme', 'light');
            } else {
                html.setAttribute('data-theme', 'dark');
                localStorage.setItem('pops_theme', 'dark');
            }
        }

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