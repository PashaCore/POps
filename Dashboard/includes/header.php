<?php
require_once __DIR__ . '/session.php';
require_once __DIR__ . '/i18n.php';
pops_session_start();
if (!isset($_SESSION['loggedin']) || $_SESSION['loggedin'] !== true || empty($_SESSION['jwt_token'])) {
    header('Location: login');
    exit;
}

// JWT çerezi eksik veya eskiyse oturumdaki token ile yenile (httpOnly, JS erişemez)
if (($_COOKIE[POPS_JWT_COOKIE] ?? '') !== $_SESSION['jwt_token']) {
    pops_set_jwt_cookie($_SESSION['jwt_token']);
}

// Yetki Kontrolü
$current_page = basename($_SERVER['PHP_SELF'], '.php');
// Arayüz dili: ortak sözlük + bu sayfanın sözlüğü (lang/en/<sayfa>.json)
pops_i18n_init($current_page);
if ($current_page !== 'index' && $current_page !== 'logout') {
    $role = $_SESSION['role'] ?? 'admin';
    $permissions = $_SESSION['permissions'] ?? [];
    
    // Viewer can never access these pages regardless of permissions
    $viewer_blocked = ['deploy', 'settings', 'terminal'];
    
    $is_unauthorized = false;
    if ($role === 'viewer' && in_array($current_page, $viewer_blocked)) {
        $is_unauthorized = true;
    } elseif ($role !== 'superadmin' && !in_array($current_page, $permissions)) {
        $is_unauthorized = true;
    }

    if ($is_unauthorized) {
        // Tema ve simge dosyası bu sayfada yüklenmez: kilit simgesi satır içi (pops_icons.svg'deki i-lock)
        echo "<div style='max-width:480px;margin:80px auto;text-align:center;font-family:Inter,sans-serif;padding:32px;background:#fff;border-radius:12px;border:1px solid #e5e7eb;'>
                <svg width='40' height='40' viewBox='0 0 24 24' fill='none' stroke='#ef4444' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round' style='display:block;margin:0 auto 16px;' aria-hidden='true'><rect x='4' y='11' width='16' height='10' rx='2'/><path d='M8 11V7a4 4 0 0 1 8 0v4'/></svg>
                <h2 style='color:#0f172a;margin-bottom:8px;'>",
            htmlspecialchars(__('Yetkisiz Erişim'), ENT_QUOTES, 'UTF-8'),
            "</h2>
                <p style='color:#64748b;margin-bottom:20px;'>",
            htmlspecialchars(__('Bu sayfayı görüntüleme yetkiniz bulunmuyor.'), ENT_QUOTES, 'UTF-8'),
            "</p>
                <a href='./' style='display:inline-block;padding:10px 20px;background:#2563eb;color:#fff;border-radius:8px;text-decoration:none;font-weight:500;'>",
            htmlspecialchars(__('Ana Sayfaya Dön'), ENT_QUOTES, 'UTF-8'),
            "</a>
             </div>";
        exit;
    }
}
?>
<?php
// Varlık sürümü dosya değişince değişir: tarayıcı önbelleği kullanılır, güncellemede yenisi iner
function pops_asset(string $rel): string
{
    $mtime = @filemtime(__DIR__ . '/../' . $rel);
    return $rel . '?v=' . ($mtime ?: '0');
}
// Çizgi simge (assets/pops_icons.svg): pops_icon('power'), pops_icon('x', 'sm')
function pops_icon(string $name, string $cls = ''): string
{
    static $sprite = null;
    if ($sprite === null) { $sprite = pops_asset('assets/pops_icons.svg'); }
    $c = 'ico' . ($cls !== '' ? ' ' . $cls : '');
    return '<svg class="' . htmlspecialchars($c, ENT_QUOTES, 'UTF-8') . '" aria-hidden="true"><use href="'
        . htmlspecialchars($sprite . '#i-' . $name, ENT_QUOTES, 'UTF-8') . '"></use></svg>';
}
$pops_titles = [
    'index' => __('Kontrol merkezi'), 'devices' => __('Cihazlar'), 'labs' => __('Sınıflar'),
    'tasks' => __('İşlemler'), 'terminal' => __('Uzak komut'), 'vision' => __('Uzak ekran'),
    'deploy' => __('Dağıtım'), 'policies' => __('Politikalar'), 'logger' => __('Kayıtlar'),
    'reports' => __('Raporlar'), 'helpdesk' => __('Destek talepleri'), 'settings' => __('Ayarlar'),
    'system' => __('Sistem'),
];
$pops_role = $_SESSION['role'] ?? 'admin';
$pops_role_label = ['superadmin' => __('Süper Admin'), 'admin' => __('Yönetici'), 'viewer' => __('İzleyici')][$pops_role] ?? __('Yönetici');
?>
<!DOCTYPE html>
<html lang="<?php echo htmlspecialchars(pops_lang(), ENT_QUOTES, 'UTF-8'); ?>">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <meta name="color-scheme" content="light">
    <title><?php echo htmlspecialchars(($pops_titles[$current_page] ?? 'POps') . ' · POps', ENT_QUOTES, 'UTF-8'); ?></title>
    <link rel="icon" type="image/png" href="assets/favicon/favicon-96x96.png" sizes="96x96" />
    <link rel="icon" type="image/svg+xml" href="assets/favicon/favicon.svg" />
    <link rel="shortcut icon" href="assets/favicon/favicon.ico" />
    <link rel="apple-touch-icon" sizes="180x180" href="assets/favicon/apple-touch-icon.png" />
    <link rel="manifest" href="assets/favicon/site.webmanifest" />
    <?php /* Yazı tipi, simge ve betiklerin hepsi panelden gelir; dış adres (CDN) eklenmez: panel internetsiz ağda
             çalışır ve yöneticinin tarayıcısı üçüncü taraflara istek atmaz */ ?>
    <link rel="stylesheet" href="<?php echo htmlspecialchars(pops_asset('assets/pops_theme.css'), ENT_QUOTES, 'UTF-8'); ?>">
    <script>window.USER_ROLE = <?php echo json_encode($pops_role, JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?>;
    window.POPS_ICONS = <?php echo json_encode(pops_asset('assets/pops_icons.svg'), JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?>;
    window.POPS_LANG = <?php echo json_encode(pops_lang(), JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?>;
    <?php if (pops_lang() !== 'tr'): /* Türkçede sözlük yok: metinler koddaki anahtarın kendisi */ ?>
    window.POPS_I18N = <?php echo json_encode((object) pops_i18n_dict(), JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_UNESCAPED_UNICODE); ?>;
    <?php endif; ?></script>
    <script>
    // Sayfa yoklama yardımcısı (bütün sayfalar): sekme arka plandayken sunucuya hiç istek atılmaz, sekmeye
    // dönünce hemen bir kez tazelenir; 5 dk boyunca fare/klavye yoksa aralık 4 katına çıkar (açık unutulan
    // sekmeler sunucuyu dövmesin); istek hata verirse aralık 60 sn'ye kadar ikiye katlanır, başarıda normale döner.
    window.popsPoll = function (fn, intervalMs) {
        let lastInput = Date.now(), timer = null, failures = 0, running = false;
        ['mousemove', 'keydown', 'click', 'touchstart', 'scroll'].forEach(ev =>
            document.addEventListener(ev, () => { lastInput = Date.now(); }, { passive: true }));
        const delay = () => {
            const idle = Date.now() - lastInput > 5 * 60 * 1000;
            return Math.min(60000, intervalMs * (idle ? 4 : 1) * Math.pow(2, failures));
        };
        const tick = async () => {
            timer = null;
            if (!document.hidden && !running) {
                running = true;
                try { await fn(); failures = 0; } catch (e) { failures = Math.min(failures + 1, 4); }
                running = false;
            }
            timer = setTimeout(tick, delay());
        };
        document.addEventListener('visibilitychange', () => { if (!document.hidden && timer) { clearTimeout(timer); tick(); } });
        timer = setTimeout(tick, delay());
        return { now: () => { if (timer) clearTimeout(timer); tick(); } };
    };

    // XSS koruması: API'den gelen değerler (pc_name, hostname, lab adı, log vb.) HTML'e
    // basılmadan önce escapeHtml ile kaçırılır. Satır içi olay niteliklerine
    // (onclick="fn(...)") verilen argümanlar ise jsArg ile önce JS, sonra HTML olarak kaçırılır.
    function escapeHtml(str) {
        if (str == null) return '';
        return String(str).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);
    }
    function jsArg(value) {
        return escapeHtml(JSON.stringify(value == null ? '' : value));
    }

    // Eski sürümlerin localStorage'a yazdığı token'ı ve kaldırılan tema tercihini temizle
    try { localStorage.removeItem('pops_jwt'); localStorage.removeItem('pops_theme'); } catch (e) {}

    // Global fetch sarmalayıcı: JWT httpOnly çerezde taşınır, JS token'ı görmez.
    // /api/ isteklerine CSRF başlığı eklenir; 401 gelirse oturum kapatılır.
    const originalFetch = window.fetch;
    window.fetch = async function(resource, config) {
        if (typeof resource === 'string' && resource.includes('/api/')) {
            config = Object.assign({ credentials: 'same-origin' }, config || {});
            const headers = new Headers(config.headers || {});
            if (!headers.has('X-Requested-With')) headers.set('X-Requested-With', 'XMLHttpRequest');
            config.headers = headers;
        }
        const response = await originalFetch(resource, config);
        if (response.status === 401) {
            window.location.href = '/logout';
            return new Promise(() => {}); // Halt execution
        }
        return response;
    };
    </script>
    <!-- Ortak betikler sayfa betiklerinden ÖNCE yüklenir (POps.*, showToast, apiRequest, state) -->
    <script src="<?php echo htmlspecialchars(pops_asset('assets/pops_config.js'), ENT_QUOTES, 'UTF-8'); ?>"></script>
    <script src="<?php echo htmlspecialchars(pops_asset('assets/pops_script.js'), ENT_QUOTES, 'UTF-8'); ?>"></script>
    <script src="<?php echo htmlspecialchars(pops_asset('assets/pops_devices.js'), ENT_QUOTES, 'UTF-8'); ?>"></script>
    <style>
        /* ============ UYGULAMA İSKELETİ ============ */
        .app-shell { display: flex; min-height: 100vh; }
        .app-sidebar { width: var(--sidebar-width); background: #0b1220; color: #a3acba; display: flex; flex-direction: column; flex-shrink: 0; position: fixed; top: 0; left: 0; bottom: 0; z-index: var(--z-sidebar); transition: transform 0.25s var(--ease); padding: 14px 12px 12px; gap: 14px; }
        .app-main { flex: 1; margin-left: var(--sidebar-width); min-width: 0; display: flex; flex-direction: column; }
        .app-content { flex: 1; padding: 28px 32px 48px; width: 100%; min-width: 0; }
        .app-topbar { display: none; }

        /* ============ YAN MENÜ ============ */
        .sb-brand { display: flex; align-items: center; gap: 10px; padding: 2px 8px; color: #fff; text-decoration: none; }
        .sb-brand:hover { color: #fff; }
        .sb-brand img { width: 30px; height: 30px; border-radius: 8px; object-fit: contain; }
        .sb-brand b { display: block; font-size: 14px; font-weight: 600; line-height: 1.2; }
        .sb-brand small { display: block; font-size: 11px; color: #6b7587; }
        .sb-search { display: flex; align-items: center; gap: 8px; height: 34px; padding: 0 10px; border-radius: 9px; background: #151e30; color: #7d8798; font-size: 13px; width: 100%; transition: background-color 0.12s, color 0.12s; }
        .sb-search:hover { background: #1b2539; color: #c9d0db; }
        .sb-search kbd { margin-left: auto; font-family: inherit; font-size: 11px; color: #5b6577; }
        .sidebar-nav { flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 12px; margin: 0 -4px; padding: 0 4px; scrollbar-color: #26324a transparent; }
        .nav-section { display: flex; flex-direction: column; gap: 1px; }
        .nav-section-title { font-size: 11px; font-weight: 600; color: #5b6577; padding: 4px 10px; }
        .nav-item { display: flex; align-items: center; gap: 10px; height: 32px; padding: 0 10px; color: #a3acba; text-decoration: none; border-radius: 8px; font-size: 13px; font-weight: 500; transition: background-color 0.12s, color 0.12s; width: 100%; }
        .nav-item .ico { width: 17px; height: 17px; opacity: 0.9; }
        .nav-item:hover { background: #151e30; color: #fff; }
        .nav-item:focus-visible { outline: none; box-shadow: 0 0 0 2px #0a84ff; }
        .nav-item.active { background: #1c2840; color: #fff; }
        .nav-item .n { margin-left: auto; font-size: 11px; color: #6b7587; font-variant-numeric: tabular-nums; }
        .nav-item .n.alert { color: #fff; background: #ff3b30; border-radius: 99px; padding: 0 6px; line-height: 17px; font-weight: 600; }
        .nav-sub { display: flex; flex-direction: column; gap: 1px; margin: 2px 0 4px; }
        .nav-sub:empty { display: none; }
        .nav-sub a { display: flex; align-items: center; gap: 8px; height: 28px; padding: 0 10px 0 37px; border-radius: 8px; color: #8b95a6; font-size: 12.5px; text-decoration: none; position: relative; }
        .nav-sub a:hover { background: #151e30; color: #fff; }
        .nav-sub a.active { color: #fff; }
        .nav-sub a.active::before { content: ''; position: absolute; left: 22px; width: 5px; height: 5px; border-radius: 99px; background: #0a84ff; }
        .nav-sub a span:first-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .nav-sub a .n { margin-left: auto; font-size: 11px; color: #6b7587; }
        .nav-sub a .dot { width: 6px; height: 6px; }
        .sb-foot { display: flex; flex-direction: column; gap: 4px; }
        .sb-jobs { display: block; width: 100%; text-align: left; background: #151e30; border-radius: 10px; padding: 9px 11px; color: #d6dbe3; font-size: 12px; }
        .sb-jobs:hover { background: #1b2539; }
        .sb-jobs .t { display: flex; justify-content: space-between; gap: 8px; }
        .sb-jobs .t span:last-child { color: #7d8798; font-variant-numeric: tabular-nums; }
        .sb-jobs .pbar { background: #26324a; height: 4px; margin-top: 7px; }
        .user-card { display: flex; align-items: center; gap: 10px; padding: 6px 4px 2px 8px; }
        .user-avatar { width: 28px; height: 28px; border-radius: 99px; background: #26324a; color: #fff; display: flex; align-items: center; justify-content: center; font-weight: 600; font-size: 12px; flex-shrink: 0; }
        .user-info { flex: 1; min-width: 0; }
        .user-name { font-size: 12.5px; font-weight: 500; color: #d6dbe3; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .user-role { font-size: 11px; color: #6b7587; }
        .user-card .ibtn { color: #7d8798; }
        .user-card .ibtn:hover { background: #151e30; color: #fff; }
        .sb-lang { display: flex; gap: 2px; margin: 4px 4px 0 8px; padding: 2px; border-radius: 8px; background: #151e30; }
        .sb-lang button { flex: 1; height: 24px; border-radius: 6px; color: #7d8798; font-size: 11.5px; font-weight: 500; text-align: center; transition: background-color 0.12s, color 0.12s; }
        .sb-lang button:hover { color: #fff; }
        .sb-lang button[aria-pressed="true"] { background: #26324a; color: #fff; }
        .sb-lang button:focus-visible { outline: none; box-shadow: 0 0 0 2px #0a84ff; }

        /* Bildirimler (yan menüden açılır) */
        .notif-wrap { position: relative; }
        .notif-panel { position: fixed; left: calc(var(--sidebar-width) + 8px); bottom: 12px; width: 380px; max-width: calc(100vw - 24px); max-height: 70vh; overflow-y: auto; background: var(--bg-surface); border-radius: 14px; box-shadow: var(--shadow-xl); display: none; z-index: 960; color: var(--text-primary); }
        .notif-panel.open { display: block; animation: modalIn 0.15s var(--ease); }
        .notif-head { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; padding: 0.625rem 0.75rem 0.625rem 1rem; border-bottom: 1px solid #f0f0f3; font-weight: var(--fw-semibold); font-size: var(--text-sm); position: sticky; top: 0; background: var(--bg-surface); z-index: 1; }
        .notif-head .btn-group { gap: 0.25rem; }
        .notif-item { display: flex; gap: 0.625rem; padding: 0.625rem 1rem; border-bottom: 1px solid #f0f0f3; font-size: var(--text-sm); }
        .notif-item:last-child { border-bottom: 0; }
        .notif-item.unread { background: var(--primary-50); }
        .notif-item .sev { width: 7px; height: 7px; border-radius: 50%; margin-top: 6px; flex-shrink: 0; background: var(--text-muted); }
        .notif-item .sev.critical { background: var(--danger-solid); } .notif-item .sev.high { background: var(--warning-solid); } .notif-item .sev.medium { background: var(--primary-500); }
        .notif-item .t { color: var(--text-primary); font-weight: var(--fw-medium); }
        .notif-item .m { color: var(--text-tertiary); font-size: var(--text-xs); margin-top: 2px; overflow-wrap: anywhere; }
        .notif-empty { padding: 1.5rem 1rem; text-align: center; color: var(--text-tertiary); font-size: var(--text-sm); }

        .menu-toggle { width: 36px; height: 36px; color: var(--text-primary); border-radius: var(--radius-md); display: inline-flex; align-items: center; justify-content: center; flex-shrink: 0; }
        .menu-toggle:hover { background: var(--bg-hover); }
        .sidebar-overlay { display: none; position: fixed; inset: 0; background: rgba(0, 0, 0, 0.35); z-index: calc(var(--z-sidebar) - 1); backdrop-filter: blur(2px); }

        /* ============ DAR EKRAN ============ */
        @media (max-width: 1024px) {
            .app-sidebar { transform: translateX(-100%); box-shadow: var(--shadow-xl); }
            .app-sidebar.open { transform: translateX(0); }
            .app-main { margin-left: 0; }
            .app-topbar { display: flex; align-items: center; gap: 10px; height: 52px; padding: 0 16px; background: rgba(245, 245, 247, 0.9); backdrop-filter: saturate(1.4) blur(8px); border-bottom: 1px solid var(--border-subtle); position: sticky; top: 0; z-index: 40; font-weight: 600; }
            .sidebar-overlay.open { display: block; animation: fadeIn 0.15s ease; }
            .app-content { padding: 20px 20px 40px; }
            .notif-panel, .jobs-panel { left: 12px; right: 12px; width: auto; }
        }
        @media (max-width: 640px) {
            .app-content { padding: 16px 16px 32px; }
            .drawer { width: 100vw; }
        }
    </style>
</head>
<body>
    <a class="sr-only" href="#mainContent"><?php _e('İçeriğe geç'); ?></a>
    <div class="app-shell">
        <aside class="app-sidebar" id="appSidebar" aria-label="<?php _e('Ana menü'); ?>">
            <a class="sb-brand" href="./">
                <img src="assets/favicon/favicon-96x96.png" alt="">
                <span><b>POps</b><small><?php _e('Yönetim paneli'); ?></small></span>
            </a>
            <button type="button" class="sb-search" id="paletteOpen" aria-label="<?php _e('Ara (Ctrl+K)'); ?>"><?php echo pops_icon('search', 'sm'); ?><span><?php _e('Ara'); ?></span><kbd>Ctrl K</kbd></button>
            <nav class="sidebar-nav">
                <?php include __DIR__ . '/sidebar.php'; ?>
            </nav>
            <div class="sb-foot">
                <button type="button" class="sb-jobs" id="jobsBtn" hidden aria-haspopup="true" aria-expanded="false" aria-controls="jobsPanel"></button>
                <div class="notif-wrap" id="notifWrap" hidden>
                    <button type="button" class="nav-item" id="notifBtn" aria-haspopup="true" aria-expanded="false" aria-controls="notifPanel"><?php echo pops_icon('bell'); ?><span><?php _e('Bildirimler'); ?></span><span class="n alert" id="notifCount" style="display:none"></span></button>
                    <div class="notif-panel" id="notifPanel" role="region" aria-label="<?php _e('Bildirimler'); ?>">
                        <div class="notif-head">
                            <span><?php _e('Bildirimler'); ?></span>
                            <span class="btn-group">
                                <button type="button" class="btn ghost sm" id="notifReadAll"><?php _e('Tümü okundu'); ?></button>
                                <button type="button" class="btn ghost sm" id="notifClear" title="<?php _e('Okunmuş bildirimleri sil'); ?>"><?php _e('Okunanları temizle'); ?></button>
                            </span>
                        </div>
                        <div id="notifList"><div class="notif-empty"><?php _e('Yükleniyor…'); ?></div></div>
                    </div>
                </div>
                <div class="user-card">
                    <div class="user-avatar" aria-hidden="true"><?php echo htmlspecialchars(mb_strtoupper(mb_substr($_SESSION['username'] ?? 'A', 0, 1)), ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'); ?></div>
                    <div class="user-info">
                        <div class="user-name"><?php echo htmlspecialchars($_SESSION['username'] ?? 'Admin'); ?></div>
                        <div class="user-role"><?php echo htmlspecialchars($pops_role_label, ENT_QUOTES, 'UTF-8'); ?></div>
                    </div>
                    <a href="logout" class="ibtn sm" data-tip="<?php _e('Çıkış yap'); ?>" data-tip-pos="up" aria-label="<?php _e('Çıkış yap'); ?>"><?php echo pops_icon('logout', 'sm'); ?></a>
                </div>
                <?php /* Dil seçimi: dil adları kendi dilinde yazılır, çevrilmez (footer.php'deki betik çerezi yazar) */ ?>
                <div class="sb-lang" role="group" aria-label="Dil / Language">
                    <button type="button" data-set-lang="tr" lang="tr" aria-pressed="<?php echo pops_lang() === 'tr' ? 'true' : 'false'; ?>">Türkçe</button>
                    <button type="button" data-set-lang="en" lang="en" aria-pressed="<?php echo pops_lang() === 'en' ? 'true' : 'false'; ?>">English</button>
                </div>
            </div>
        </aside>
        <div class="sidebar-overlay" id="sidebarOverlay"></div>
        <div class="jobs-panel" id="jobsPanel" role="region" aria-label="<?php _e('Süren işlemler'); ?>"></div>
        <div class="app-main">
            <header class="app-topbar">
                <button type="button" class="menu-toggle" id="menuToggle" aria-label="<?php _e('Menüyü aç'); ?>" aria-controls="appSidebar" aria-expanded="false"><?php echo pops_icon('menu'); ?></button>
                <span id="pageTitle"><?php echo htmlspecialchars($pops_titles[$current_page] ?? ucfirst($current_page), ENT_QUOTES, 'UTF-8'); ?></span>
            </header>
            <main class="app-content" id="mainContent">
