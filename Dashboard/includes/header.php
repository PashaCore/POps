<?php
require_once __DIR__ . '/session.php';
pops_session_start();
if (!isset($_SESSION['loggedin']) || $_SESSION['loggedin'] !== true || empty($_SESSION['jwt_token'])) {
    header('Location: login.php');
    exit;
}

// JWT çerezi eksik veya eskiyse oturumdaki token ile yenile (httpOnly, JS erişemez)
if (($_COOKIE[POPS_JWT_COOKIE] ?? '') !== $_SESSION['jwt_token']) {
    pops_set_jwt_cookie($_SESSION['jwt_token']);
}

// Yetki Kontrolü
$current_page = basename($_SERVER['PHP_SELF'], '.php');
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
        die("<div style='max-width:480px;margin:80px auto;text-align:center;font-family:Inter,sans-serif;padding:32px;background:#fff;border-radius:12px;border:1px solid #e5e7eb;'>
                <i class='fas fa-lock' style='font-size:2.5rem;color:#ef4444;margin-bottom:16px;'></i>
                <h2 style='color:#0f172a;margin-bottom:8px;'>Yetkisiz Erişim</h2>
                <p style='color:#64748b;margin-bottom:20px;'>Bu sayfayı görüntüleme yetkiniz bulunmuyor.</p>
                <a href='index.php' style='display:inline-block;padding:10px 20px;background:#2563eb;color:#fff;border-radius:8px;text-decoration:none;font-weight:500;'>Ana Sayfaya Dön</a>
             </div>");
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
$pops_role = $_SESSION['role'] ?? 'admin';
$pops_role_label = ['superadmin' => 'Süper Admin', 'admin' => 'Yönetici', 'viewer' => 'İzleyici'][$pops_role] ?? 'Yönetici';
?>
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <meta name="color-scheme" content="light">
    <title>POps | Merkez Komuta</title>
    <link rel="icon" type="image/png" href="assets/favicon/favicon-96x96.png" sizes="96x96" />
    <link rel="icon" type="image/svg+xml" href="assets/favicon/favicon.svg" />
    <link rel="shortcut icon" href="assets/favicon/favicon.ico" />
    <link rel="apple-touch-icon" sizes="180x180" href="assets/favicon/apple-touch-icon.png" />
    <link rel="manifest" href="assets/favicon/site.webmanifest" />
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
    <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css">
    <link rel="stylesheet" href="<?php echo htmlspecialchars(pops_asset('assets/pops_theme.css'), ENT_QUOTES, 'UTF-8'); ?>">
    <script>window.USER_ROLE = <?php echo json_encode($pops_role, JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?>;</script>
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
            window.location.href = '/logout.php';
            return new Promise(() => {}); // Halt execution
        }
        return response;
    };
    </script>
    <!-- Ortak betikler sayfa betiklerinden ÖNCE yüklenir (POps.*, showToast, apiRequest, state) -->
    <script src="<?php echo htmlspecialchars(pops_asset('assets/pops_config.js'), ENT_QUOTES, 'UTF-8'); ?>"></script>
    <script src="<?php echo htmlspecialchars(pops_asset('assets/pops_script.js'), ENT_QUOTES, 'UTF-8'); ?>"></script>
    <style>
        /* ============ UYGULAMA İSKELETİ ============ */
        .app-shell { display: flex; min-height: 100vh; }
        .app-sidebar { width: var(--sidebar-width); background: var(--bg-surface); border-right: 1px solid var(--border-subtle); display: flex; flex-direction: column; flex-shrink: 0; position: fixed; top: 0; left: 0; bottom: 0; z-index: var(--z-sidebar); transition: transform 0.25s var(--ease); }
        .app-main { flex: 1; margin-left: var(--sidebar-width); min-width: 0; display: flex; flex-direction: column; }
        .app-topbar { height: var(--topbar-height); background: rgba(255, 255, 255, 0.92); backdrop-filter: saturate(1.4) blur(6px); border-bottom: 1px solid var(--border-subtle); padding: 0 var(--space-8); display: flex; align-items: center; justify-content: space-between; position: sticky; top: 0; z-index: 40; }
        .app-content { flex: 1; padding: var(--space-6) var(--space-8); width: 100%; min-width: 0; }

        /* ============ YAN MENÜ ============ */
        .sidebar-header { height: var(--topbar-height); padding: 0 var(--space-5); display: flex; align-items: center; gap: 10px; border-bottom: 1px solid var(--border-subtle); flex-shrink: 0; }
        .sidebar-header img.brand-icon { width: 32px; height: 32px; object-fit: contain; }
        .sidebar-header img.brand-name { width: 100%; max-width: 136px; height: auto; object-fit: contain; }
        .sidebar-nav { padding: var(--space-4) var(--space-3); flex: 1; overflow-y: auto; }
        .nav-section { margin-bottom: var(--space-5); }
        .nav-section-title { font-size: 0.6875rem; font-weight: var(--fw-semibold); color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.08em; padding: 0 var(--space-3); margin-bottom: 0.375rem; }
        .nav-item { display: flex; align-items: center; gap: 0.75rem; padding: 0.5rem 0.75rem; color: var(--text-secondary); text-decoration: none; border-radius: var(--radius-md); font-size: var(--text-sm); font-weight: var(--fw-medium); margin-bottom: 0.125rem; transition: background-color 0.15s, color 0.15s; position: relative; }
        .nav-item:hover { background: var(--bg-hover); color: var(--text-primary); }
        .nav-item:focus-visible { outline: none; box-shadow: var(--focus-ring); }
        .nav-item.active { background: var(--primary-50); color: var(--primary-600); font-weight: var(--fw-semibold); }
        .nav-item.active::before { content: ''; position: absolute; left: -12px; top: 50%; transform: translateY(-50%); width: 3px; height: 20px; background: var(--primary-500); border-radius: 0 2px 2px 0; }
        .nav-item i { width: 18px; text-align: center; font-size: 0.95rem; flex-shrink: 0; opacity: 0.85; }

        .sidebar-footer { padding: var(--space-3); border-top: 1px solid var(--border-subtle); }
        .user-card { display: flex; align-items: center; gap: 0.625rem; padding: 0.5rem; border-radius: var(--radius-md); margin-bottom: 0.5rem; }
        .user-avatar { width: 34px; height: 34px; border-radius: 50%; background: linear-gradient(135deg, var(--primary-500), var(--primary-700)); color: white; display: flex; align-items: center; justify-content: center; font-weight: var(--fw-semibold); font-size: 0.875rem; flex-shrink: 0; }
        .user-info { flex: 1; min-width: 0; }
        .user-name { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .user-role { font-size: 0.6875rem; color: var(--text-tertiary); }
        .sidebar-action-btn { width: 100%; min-height: 34px; padding: 0.4375rem; background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); color: var(--text-secondary); font-size: var(--text-sm); font-weight: var(--fw-medium); transition: background-color 0.15s, color 0.15s, border-color 0.15s; display: flex; align-items: center; justify-content: center; gap: 0.5rem; }
        .sidebar-action-btn.danger { color: var(--danger-text); }
        .sidebar-action-btn.danger:hover { background: var(--danger-bg); border-color: var(--danger-border); color: var(--danger-text); }

        /* ============ ÜST ÇUBUK ============ */
        .topbar-left { display: flex; align-items: center; gap: 0.75rem; min-width: 0; }
        .topbar-breadcrumb { display: flex; align-items: center; gap: 0.5rem; font-size: var(--text-sm); color: var(--text-tertiary); min-width: 0; }
        .topbar-breadcrumb a { color: var(--text-tertiary); display: inline-flex; }
        .topbar-breadcrumb a:hover { color: var(--text-primary); }
        .topbar-breadcrumb strong { color: var(--text-primary); font-weight: var(--fw-semibold); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .topbar-breadcrumb .separator { color: var(--text-muted); }
        .topbar-actions { display: flex; align-items: center; gap: 0.5rem; }
        .topbar-icon-btn { width: 38px; height: 38px; border-radius: var(--radius-md); color: var(--text-secondary); display: flex; align-items: center; justify-content: center; transition: background-color 0.15s, color 0.15s; position: relative; }
        .topbar-icon-btn:hover, .topbar-icon-btn[aria-expanded="true"] { background: var(--bg-hover); color: var(--text-primary); }
        .topbar-icon-btn:focus-visible { outline: none; box-shadow: var(--focus-ring); }
        /* Bildirim zili */
        .notif-wrap { position: relative; }
        .notif-count { position: absolute; top: 3px; right: 3px; min-width: 16px; height: 16px; padding: 0 4px; border-radius: 8px; background: var(--danger-solid); color: #fff; font-size: 10px; font-weight: 700; line-height: 16px; text-align: center; display: none; box-shadow: 0 0 0 2px var(--bg-surface); }
        .notif-panel { position: absolute; right: 0; top: 46px; width: 380px; max-width: calc(100vw - 24px); max-height: 460px; overflow-y: auto; background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); box-shadow: var(--shadow-lg); display: none; z-index: 60; }
        .notif-panel.open { display: block; animation: modalIn 0.15s var(--ease); }
        .notif-head { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; padding: 0.625rem 0.75rem 0.625rem 1rem; border-bottom: 1px solid var(--border-subtle); font-weight: var(--fw-semibold); font-size: var(--text-sm); position: sticky; top: 0; background: var(--bg-surface); z-index: 1; }
        .notif-head .btn-group { gap: 0.25rem; }
        .notif-item { display: flex; gap: 0.625rem; padding: 0.625rem 1rem; border-bottom: 1px solid var(--border-subtle); font-size: var(--text-sm); }
        .notif-item:last-child { border-bottom: 0; }
        .notif-item.unread { background: var(--primary-50); }
        .notif-item .sev { width: 8px; height: 8px; border-radius: 50%; margin-top: 6px; flex-shrink: 0; background: var(--text-muted); }
        .notif-item .sev.critical { background: var(--danger-solid); } .notif-item .sev.high { background: var(--warning-solid); } .notif-item .sev.medium { background: var(--primary-500); }
        .notif-item .t { color: var(--text-primary); font-weight: var(--fw-medium); }
        .notif-item .m { color: var(--text-tertiary); font-size: var(--text-xs); margin-top: 2px; overflow-wrap: anywhere; }
        .notif-empty { padding: 1.5rem 1rem; text-align: center; color: var(--text-tertiary); font-size: var(--text-sm); }
        .topbar-clock { display: flex; flex-direction: column; align-items: flex-end; line-height: 1.2; padding: 0 0.75rem; border-right: 1px solid var(--border-subtle); margin-right: 0.25rem; }
        .topbar-clock .time { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); font-variant-numeric: tabular-nums; }
        .topbar-clock .date { font-size: 0.6875rem; color: var(--text-tertiary); }

        .menu-toggle { display: none; width: 38px; height: 38px; border: 1px solid var(--border-default); color: var(--text-primary); font-size: 1rem; border-radius: var(--radius-md); align-items: center; justify-content: center; transition: background-color 0.15s; flex-shrink: 0; }
        .menu-toggle:hover { background: var(--bg-hover); }
        .menu-toggle:focus-visible { outline: none; box-shadow: var(--focus-ring); }

        .sidebar-overlay { display: none; position: fixed; inset: 0; background: rgba(15, 23, 42, 0.45); z-index: calc(var(--z-sidebar) - 1); backdrop-filter: blur(2px); }

        /* ============ DAR EKRAN ============ */
        @media (max-width: 1024px) {
            .app-sidebar { transform: translateX(-100%); box-shadow: var(--shadow-xl); }
            .app-sidebar.open { transform: translateX(0); }
            .app-main { margin-left: 0; }
            .menu-toggle { display: inline-flex; }
            .sidebar-overlay.open { display: block; animation: fadeIn 0.15s ease; }
            .app-content { padding: var(--space-6); }
            .app-topbar { padding: 0 var(--space-6); }
        }
        @media (max-width: 768px) {
            .app-content { padding: var(--space-4); }
            .app-topbar { padding: 0 var(--space-4); }
            .topbar-clock { display: none; }
        }
    </style>
</head>
<body>
    <a class="sr-only" href="#mainContent">İçeriğe geç</a>
    <div class="app-shell">
        <aside class="app-sidebar" id="appSidebar" aria-label="Ana menü">
            <div class="sidebar-header">
                <img class="brand-icon" src="assets/favicon/favicon-96x96.png" alt="">
                <img class="brand-name" src="assets/favicon/sidemenu.png" alt="POps Operations Platform">
            </div>
            <nav class="sidebar-nav">
                <?php include __DIR__ . '/sidebar.php'; ?>
            </nav>
            <div class="sidebar-footer">
                <div class="user-card">
                    <div class="user-avatar" aria-hidden="true"><?php echo htmlspecialchars(strtoupper(substr($_SESSION['username'] ?? 'A', 0, 1)), ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8'); ?></div>
                    <div class="user-info">
                        <div class="user-name"><?php echo htmlspecialchars($_SESSION['username'] ?? 'Admin'); ?></div>
                        <div class="user-role"><?php echo htmlspecialchars($pops_role_label, ENT_QUOTES, 'UTF-8'); ?></div>
                    </div>
                </div>
                <a href="logout.php" class="sidebar-action-btn danger">
                    <i class="fas fa-right-from-bracket" aria-hidden="true"></i>
                    <span>Çıkış Yap</span>
                </a>
            </div>
        </aside>
        <div class="sidebar-overlay" id="sidebarOverlay"></div>
        <div class="app-main">
            <header class="app-topbar">
                <div class="topbar-left">
                    <button type="button" class="menu-toggle" id="menuToggle" aria-label="Menüyü aç" aria-controls="appSidebar" aria-expanded="false"><i class="fas fa-bars"></i></button>
                    <div class="topbar-breadcrumb">
                        <a href="index.php" aria-label="Ana sayfa"><i class="fas fa-house" style="opacity:0.6;"></i></a>
                        <span class="separator">/</span>
                        <strong id="pageTitle"><?php
                            $titles = [
                                'index' => 'Dashboard', 'devices' => 'Cihaz Yönetimi', 'labs' => 'Laboratuvar Yönetimi',
                                'vision' => 'POpsVision', 'tasks' => 'Görev Kuyruğu', 'deploy' => 'Dosya Dağıtımı',
                                'logger' => 'Log & Envanter', 'terminal' => 'Terminal',
                                'settings' => 'Sistem Ayarları', 'system' => 'Sistem & Sürüm', 'reports' => 'Raporlar', 'policies' => 'Politikalar', 'helpdesk' => 'Yardım Masası'
                            ];
                            echo htmlspecialchars($titles[$current_page] ?? ucfirst($current_page), ENT_QUOTES, 'UTF-8');
                        ?></strong>
                    </div>
                </div>
                <div class="topbar-actions">
                    <div class="topbar-clock" aria-hidden="true">
                        <span class="time" id="topbarTime">--:--:--</span>
                        <span class="date" id="topbarDate">--/--/----</span>
                    </div>
                    <div class="notif-wrap" id="notifWrap" hidden>
                        <button type="button" class="topbar-icon-btn" id="notifBtn" title="Bildirimler" aria-label="Bildirimler" aria-haspopup="true" aria-expanded="false" aria-controls="notifPanel"><i class="fas fa-bell"></i><span class="notif-count" id="notifCount"></span></button>
                        <div class="notif-panel" id="notifPanel" role="region" aria-label="Bildirimler">
                            <div class="notif-head">
                                <span>Bildirimler</span>
                                <span class="btn-group">
                                    <button type="button" class="btn ghost sm" id="notifReadAll">Tümü okundu</button>
                                    <button type="button" class="btn ghost sm" id="notifClear" title="Okunmuş bildirimleri sil">Okunanları temizle</button>
                                </span>
                            </div>
                            <div id="notifList"><div class="notif-empty">Yükleniyor…</div></div>
                        </div>
                    </div>
                </div>
            </header>
            <main class="app-content" id="mainContent">
