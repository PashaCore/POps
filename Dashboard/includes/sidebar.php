<?php
// Sidebar menüsü - Aktif sayfa ve yetki sistemine göre
$current_file = basename($_SERVER['PHP_SELF']);

$role = $_SESSION['role'] ?? 'admin';
$perms = $_SESSION['permissions'] ?? [];
function can_view($page) {
    global $role, $perms;
    return ($role === 'superadmin' || in_array($page, $perms));
}

$nav_items = [
    ['page' => 'index', 'icon' => 'fa-house', 'label' => 'Dashboard', 'show' => true, 'section' => 'Genel'],
    ['page' => 'devices', 'icon' => 'fa-desktop', 'label' => 'Cihaz Yönetimi', 'show' => can_view('devices'), 'section' => 'Yönetim'],
    ['page' => 'labs', 'icon' => 'fa-network-wired', 'label' => 'Laboratuvarlar', 'show' => can_view('labs'), 'section' => 'Yönetim'],
    ['page' => 'tasks', 'icon' => 'fa-tasks', 'label' => 'Görev Kuyruğu', 'show' => can_view('tasks'), 'section' => 'Yönetim'],
    ['page' => 'vision', 'icon' => 'fa-eye', 'label' => 'POpsVision', 'show' => can_view('vision'), 'section' => 'Operasyon'],
    ['page' => 'deploy', 'icon' => 'fa-cloud-arrow-up', 'label' => 'Dosya Dağıtımı', 'show' => can_view('deploy'), 'section' => 'Operasyon'],
    ['page' => 'terminal', 'icon' => 'fa-terminal', 'label' => 'Terminal', 'show' => can_view('terminal'), 'section' => 'Operasyon'],
    ['page' => 'update', 'icon' => 'fa-arrows-rotate', 'label' => 'Ajan Güncelleme', 'show' => can_view('update'), 'section' => 'Sistem'],
    ['page' => 'logger', 'icon' => 'fa-clipboard-list', 'label' => 'Log & Envanter', 'show' => can_view('logger'), 'section' => 'Sistem'],
    ['page' => 'policies', 'icon' => 'fa-shield-halved', 'label' => 'Politikalar', 'show' => can_view('policies') || $role === 'superadmin', 'section' => 'Sistem'],
    ['page' => 'settings', 'icon' => 'fa-gear', 'label' => 'Ayarlar', 'show' => can_view('settings'), 'section' => 'Sistem'],
];

// Section'lara göre grupla
$grouped = [];
foreach ($nav_items as $item) {
    if (!$item['show']) continue;
    $grouped[$item['section']][] = $item;
}

foreach ($grouped as $section => $items): ?>
    <div class="nav-section">
        <div class="nav-section-title"><?php echo htmlspecialchars($section); ?></div>
        <?php foreach ($items as $item):
            $isActive = $current_file === $item['page'] . '..php';
        ?>
            <a href="<?php echo $item['page']; ?>.php" class="nav-item <?php echo $isActive ? 'active' : ''; ?>">
                <i class="fas <?php echo $item['icon']; ?>"></i>
                <span><?php echo htmlspecialchars($item['label']); ?></span>
            </a>
        <?php endforeach; ?>
    </div>
<?php endforeach; ?>