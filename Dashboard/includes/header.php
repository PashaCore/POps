<?php
session_start();
if (!isset($_SESSION['loggedin']) || $_SESSION['loggedin'] !== true || empty($_SESSION['jwt_token'])) {
    header('Location: login.php');
    exit;
}

// Yetki Kontrolü
$current_page = basename($_SERVER['PHP_SELF'], '.php');
if ($current_page !== 'index' && $current_page !== 'logout') {
    $role = $_SESSION['role'] ?? 'admin';
    $permissions = $_SESSION['permissions'] ?? [];
    
    // Viewer can never access these pages regardless of permissions
    $viewer_blocked = ['deploy', 'settings', 'update', 'terminal'];
    
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
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>POps | Merkez Komuta</title>
    <link rel="icon" type="image/png" href="assets/favicon/favicon-96x96.png" sizes="96x96" />
    <link rel="icon" type="image/svg+xml" href="assets/favicon/favicon.svg" />
    <script>window.USER_ROLE = "<?php echo $_SESSION['role'] ?? 'admin'; ?>";</script>
    <link rel="shortcut icon" href="assets/favicon/favicon.ico" />
    <link rel="apple-touch-icon" sizes="180x180" href="assets/favicon/apple-touch-icon.png" />
    <link rel="manifest" href="assets/favicon/site.webmanifest" />
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap" rel="stylesheet">
    <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css">
    <link rel="stylesheet" href="assets/pops_theme.css?v=<?php echo time(); ?>">
    <script>
        // Sayfa yüklenmeden temayı uygula (FOUC engellemek için)
        const savedTheme = localStorage.getItem('pops_theme') || 'light';
        if (savedTheme === 'dark') {
            document.documentElement.setAttribute('data-theme', 'dark');
        }
        
        // PHP session'dan JWT'yi JS'e aktar (her sayfada localStorage güncellenir)
        const sessionJwt = "<?php echo htmlspecialchars($_SESSION['jwt_token'] ?? '', ENT_QUOTES, 'UTF-8'); ?>";
        if (sessionJwt) {
            localStorage.setItem('pops_jwt', sessionJwt);
        }
        
        // Global Fetch Interceptor to attach JWT and handle 401 Unauthorized
        const originalFetch = window.fetch;
        window.fetch = async function(resource, config) {
            if (typeof resource === 'string' && resource.includes('/api/')) {
                config = config || {};
                config.headers = config.headers || {};
                if (config.headers instanceof Headers) {
                    if (!config.headers.has('Authorization')) {
                        config.headers.set('Authorization', 'Bearer ' + sessionJwt);
                    }
                } else {
                    if (!config.headers['Authorization']) {
                        config.headers['Authorization'] = 'Bearer ' + sessionJwt;
                    }
                }
            }
            
            try {
                const response = await originalFetch(resource, config);
                if (response.status === 401) {
                    localStorage.removeItem('pops_jwt');
                    window.location.href = '/login.php';
                    return new Promise(() => {}); // Halt execution
                }
                return response;
            } catch (error) {
                throw error;
            }
        };
        
        // Eğer URL'de gelirse (eski login akışı yedeği), URL'yi temizle
        (function() {
            const params = new URLSearchParams(window.location.search);
            if (params.has('_jwt')) {
                params.delete('_jwt');
                const newUrl = window.location.pathname + (params.toString() ? '?' + params.toString() : '');
                history.replaceState({}, '', newUrl);
            }
        })();
    </script>
    <style>
        /* ============ APP SHELL LAYOUT ============ */
        .app-shell { display: flex; min-height: 100vh; }
        .app-sidebar { width: var(--sidebar-width); background: var(--bg-surface); border-right: 1px solid var(--border-subtle); display: flex; flex-direction: column; flex-shrink: 0; position: fixed; top: 0; left: 0; bottom: 0; z-index: var(--z-sidebar); transition: transform 0.25s ease; }
        .app-main { flex: 1; margin-left: var(--sidebar-width); min-width: 0; display: flex; flex-direction: column; }
        .app-topbar { height: var(--topbar-height); background: var(--bg-surface); border-bottom: 1px solid var(--border-subtle); padding: 0 var(--space-8); display: flex; align-items: center; justify-content: space-between; position: sticky; top: 0; z-index: 40; }
        .app-content { flex: 1; padding: var(--space-8); max-width: 100%; overflow-x: hidden; }

        /* ============ SIDEBAR ============ */
        .sidebar-header { padding: var(--space-5) var(--space-6); display: flex; align-items: center; justify-content: center; border-bottom: 1px solid var(--border-subtle); }
        .sidebar-logo { width: 100%; max-width: 180px; display: flex; align-items: center; justify-content: center; }

        .sidebar-nav { padding: var(--space-4); flex: 1; overflow-y: auto; }
        .nav-section { margin-bottom: var(--space-5); }
        .nav-section-title { font-size: 0.6875rem; font-weight: var(--fw-semibold); color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.08em; padding: 0 var(--space-3); margin-bottom: 0.5rem; }
        .nav-item { display: flex; align-items: center; gap: 0.75rem; padding: 0.5625rem 0.75rem; color: var(--text-secondary); text-decoration: none; border-radius: var(--radius-md); font-size: var(--text-sm); font-weight: var(--fw-medium); margin-bottom: 0.125rem; transition: background-color 0.15s, color 0.15s; cursor: pointer; position: relative; }
        .nav-item:hover { background: var(--bg-surface-2); color: var(--text-primary); }
        .nav-item.active { background: var(--primary-50); color: var(--primary-600); }
        .nav-item.active::before { content: ''; position: absolute; left: -16px; top: 50%; transform: translateY(-50%); width: 3px; height: 20px; background: var(--primary-500); border-radius: 0 2px 2px 0; }
        .nav-item i { width: 18px; text-align: center; font-size: 0.95rem; flex-shrink: 0; }
        [data-theme="dark"] .nav-item.active { background: rgba(59, 130, 246, 0.12); color: var(--primary-600); }

        .sidebar-footer { padding: var(--space-4); border-top: 1px solid var(--border-subtle); }
        .user-card { display: flex; align-items: center; gap: 0.625rem; padding: 0.5rem; border-radius: var(--radius-md); margin-bottom: 0.5rem; }
        .user-avatar { width: 36px; height: 36px; border-radius: 50%; background: linear-gradient(135deg, var(--primary-500), var(--primary-700)); color: white; display: flex; align-items: center; justify-content: center; font-weight: var(--fw-semibold); font-size: 0.875rem; flex-shrink: 0; }
        .user-info { flex: 1; min-width: 0; }
        .user-name { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .user-role { font-size: 0.6875rem; color: var(--text-tertiary); }
        .sidebar-actions { display: flex; gap: 0.375rem; }
        .sidebar-action-btn { flex: 1; padding: 0.4375rem; background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); color: var(--text-secondary); cursor: pointer; font-size: 0.8125rem; transition: background-color 0.15s, color 0.15s; display: flex; align-items: center; justify-content: center; gap: 0.375rem; }
        .sidebar-action-btn:hover { background: var(--bg-app); color: var(--text-primary); }
        .sidebar-action-btn.danger { color: var(--danger-text); }
        .sidebar-action-btn.danger:hover { background: var(--danger-bg); }

        /* ============ TOPBAR ============ */
        .topbar-breadcrumb { display: flex; align-items: center; gap: 0.5rem; font-size: var(--text-sm); color: var(--text-tertiary); }
        .topbar-breadcrumb strong { color: var(--text-primary); font-weight: var(--fw-semibold); }
        .topbar-breadcrumb .separator { color: var(--text-muted); }
        .topbar-actions { display: flex; align-items: center; gap: 0.5rem; }
        .topbar-icon-btn { width: 36px; height: 36px; border-radius: var(--radius-md); background: transparent; border: none; color: var(--text-secondary); cursor: pointer; display: flex; align-items: center; justify-content: center; transition: background-color 0.15s, color 0.15s; padding: 0; }
        .topbar-icon-btn:hover { background: var(--bg-surface-2); color: var(--text-primary); }
        .topbar-clock { display: flex; flex-direction: column; align-items: flex-end; line-height: 1.2; padding: 0 0.75rem; border-right: 1px solid var(--border-subtle); margin-right: 0.5rem; }
        .topbar-clock .time { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); font-variant-numeric: tabular-nums; }
        .topbar-clock .date { font-size: 0.6875rem; color: var(--text-tertiary); }

        .menu-toggle { display: none; background: transparent; border: 1px solid var(--border-default); color: var(--text-primary); font-size: 1.125rem; cursor: pointer; padding: 0.5rem 0.75rem; border-radius: var(--radius-md); transition: background-color 0.15s; }
        .menu-toggle:hover { background: var(--bg-surface-2); }

        .sidebar-overlay { display: none; position: fixed; inset: 0; background: rgba(15, 23, 42, 0.50); z-index: calc(var(--z-sidebar) - 1); backdrop-filter: blur(2px); opacity: 0; transition: opacity 0.2s; }

        /* ============ MOBILE ============ */
        @media (max-width: 1024px) {
            .app-sidebar { transform: translateX(-100%); }
            .app-sidebar.open { transform: translateX(0); }
            .app-main { margin-left: 0; }
            .menu-toggle { display: flex; }
            .sidebar-overlay.open { display: block; opacity: 1; }
        }
        @media (max-width: 768px) {
            .app-content { padding: var(--space-4); }
            .app-topbar { padding: 0 var(--space-4); }
            .topbar-clock { display: none; }
        }
    </style>
</head>
<body>
    <div class="app-shell">
        <aside class="app-sidebar" id="appSidebar">
            <div class="sidebar-header" style="display: flex; align-items: center; justify-content: center; gap: 10px;">
                <img src="assets/favicon/favicon-96x96.png" alt="Icon" style="width: 36px; height: 36px; object-fit: contain;">
                <div class="sidebar-logo" style="flex: 1; display: flex; align-items: center;">
                    <img src="assets/favicon/sidemenu.png" alt="POps Operations Platform" style="width: 100%; max-width: 140px; height: auto; object-fit: contain;">
                </div>
            </div>
            <nav class="sidebar-nav">
                <?php include __DIR__ . '/sidebar.php'; ?>
            </nav>
            <div class="sidebar-footer">
                <div class="user-card">
                    <div class="user-avatar"><?php echo strtoupper(substr($_SESSION['username'] ?? 'A', 0, 1)); ?></div>
                    <div class="user-info">
                        <div class="user-name"><?php echo htmlspecialchars($_SESSION['username'] ?? 'Admin'); ?></div>
                        <div class="user-role"><?php echo ($_SESSION['role'] ?? 'admin') === 'superadmin' ? 'Süper Admin' : 'Yönetici'; ?></div>
                    </div>
                </div>
                <div class="sidebar-actions">
                    <button class="sidebar-action-btn" onclick="toggleTheme()" title="Tema Değiştir" aria-label="Tema Değiştir">
                        <i class="fas fa-circle-half-stroke"></i>
                    </button>
                    <a href="logout.php" class="sidebar-action-btn danger" title="Çıkış" aria-label="Çıkış Yap">
                        <i class="fas fa-right-from-bracket"></i>
                    </a>
                </div>
            </div>
        </aside>
        <div class="sidebar-overlay" id="sidebarOverlay" onclick="toggleMobileSidebar()"></div>
        <div class="app-main">
            <header class="app-topbar">
                <div style="display:flex;align-items:center;gap:0.75rem;">
                    <button class="menu-toggle" onclick="toggleMobileSidebar()" aria-label="Menü"><i class="fas fa-bars"></i></button>
                    <div class="topbar-breadcrumb">
                        <i class="fas fa-house" style="opacity:0.5;"></i>
                        <span class="separator">/</span>
                        <strong id="pageTitle"><?php
                            $titles = [
                                'index' => 'Dashboard', 'devices' => 'Cihaz Yönetimi', 'labs' => 'Laboratuvar Yönetimi',
                                'vision' => 'POpsVision', 'tasks' => 'Görev Kuyruğu', 'deploy' => 'Dosya Dağıtımı',
                                'update' => 'Ajan Güncelleme', 'logger' => 'Log & Envanter', 'terminal' => 'Terminal',
                                'settings' => 'Sistem Ayarları'
                            ];
                            echo $titles[$current_page] ?? ucfirst($current_page);
                        ?></strong>
                    </div>
                </div>
                <div class="topbar-actions">
                    <div class="topbar-clock">
                        <span class="time" id="topbarTime">--:--:--</span>
                        <span class="date" id="topbarDate">--/--/----</span>
                    </div>
                    <button class="topbar-icon-btn" title="Bildirimler" aria-label="Bildirimler"><i class="fas fa-bell"></i></button>
                    <button class="topbar-icon-btn" onclick="toggleTheme()" title="Tema" aria-label="Tema Değiştir"><i class="fas fa-circle-half-stroke"></i></button>
                </div>
            </header>
            <main class="app-content">