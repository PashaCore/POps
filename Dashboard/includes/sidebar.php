<?php
// Yan menü: etkin sayfa ve yetkiye göre. Sınıflar sayfasındayken sınıf listesi #navSub-labs'a sayfa betiğiyle dolar.
$current_file = basename($_SERVER['PHP_SELF']);

$role = $_SESSION['role'] ?? 'admin';
$perms = $_SESSION['permissions'] ?? [];
function can_view($page) {
    global $role, $perms;
    return ($role === 'superadmin' || in_array($page, $perms));
}

$nav_items = [
    ['page' => 'index', 'icon' => 'home', 'show' => true, 'section' => ''],
    ['page' => 'devices', 'icon' => 'devices', 'show' => can_view('devices'), 'section' => 'Cihazlar'],
    ['page' => 'labs', 'icon' => 'labs', 'show' => can_view('labs'), 'section' => 'Cihazlar'],
    ['page' => 'tasks', 'icon' => 'jobs', 'show' => can_view('tasks'), 'section' => 'İşlem'],
    ['page' => 'terminal', 'icon' => 'terminal', 'show' => can_view('terminal'), 'section' => 'İşlem'],
    ['page' => 'vision', 'icon' => 'eye', 'show' => can_view('vision'), 'section' => 'İşlem'],
    ['page' => 'deploy', 'icon' => 'package', 'show' => can_view('deploy'), 'section' => 'İşlem'],
    ['page' => 'policies', 'icon' => 'shield', 'show' => can_view('policies') || $role === 'superadmin', 'section' => 'Yönetim'],
    ['page' => 'logger', 'icon' => 'list', 'show' => can_view('logger'), 'section' => 'Yönetim'],
    ['page' => 'reports', 'icon' => 'chart', 'show' => can_view('reports'), 'section' => 'Yönetim'],
    ['page' => 'helpdesk', 'icon' => 'help', 'show' => can_view('helpdesk'), 'section' => 'Yönetim'],
    ['page' => 'settings', 'icon' => 'gear', 'show' => can_view('settings'), 'section' => 'Sunucu'],
    ['page' => 'system', 'icon' => 'server', 'show' => $role === 'superadmin', 'section' => 'Sunucu'],
];

$grouped = [];
foreach ($nav_items as $item) {
    if (!$item['show']) continue;
    $grouped[$item['section']][] = $item;
}

foreach ($grouped as $section => $items): ?>
    <div class="nav-section">
        <?php if ($section !== ''): ?><div class="nav-section-title"><?php echo htmlspecialchars($section, ENT_QUOTES, 'UTF-8'); ?></div><?php endif; ?>
        <?php foreach ($items as $item):
            $isActive = basename($current_file, '.php') === $item['page'];
        ?>
            <a href="<?php echo htmlspecialchars($item['page'] === 'index' ? './' : $item['page'], ENT_QUOTES, 'UTF-8'); ?>" class="nav-item <?php echo $isActive ? 'active' : ''; ?>"<?php echo $isActive ? ' aria-current="page"' : ''; ?> data-page="<?php echo htmlspecialchars($item['page'], ENT_QUOTES, 'UTF-8'); ?>">
                <?php echo pops_icon($item['icon']); ?>
                <span><?php echo htmlspecialchars($pops_titles[$item['page']] ?? $item['page'], ENT_QUOTES, 'UTF-8'); ?></span>
                <span class="n" data-count="<?php echo htmlspecialchars($item['page'], ENT_QUOTES, 'UTF-8'); ?>"></span>
            </a>
            <?php if ($isActive && $item['page'] === 'labs'): ?><div class="nav-sub" id="navSub-labs"></div><?php endif; ?>
        <?php endforeach; ?>
    </div>
<?php endforeach; ?>
