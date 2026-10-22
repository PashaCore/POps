<?php
// Yan menü: etkin sayfa ve yetkiye göre. Bilgisayarlar tek satırdır (sınıflar, liste ve bilgisayar sayfası).
// Bölüm adları Türkçe anahtardır, yazılırken çevrilir ('nav' bağlamı: "İşlem" bölümü İşlemler sayfası değildir).
$current_file = basename($_SERVER['PHP_SELF']);

$role = $_SESSION['role'] ?? 'admin';
$perms = $_SESSION['permissions'] ?? [];
function can_view($page) {
    global $role, $perms, $viewer_blocked;
    // header.php'nin izleyiciye kapattığı sayfalar menüde de görünmez (yetki listesinde olsalar bile)
    if ($role === 'viewer' && in_array($page, (array) ($viewer_blocked ?? []), true)) {
        return false;
    }
    return ($role === 'superadmin' || in_array($page, $perms));
}

$nav_items = [
    ['page' => 'index', 'icon' => 'home', 'show' => true, 'section' => ''],
    // Bilgisayarlar: sınıflar (labs), düz liste (devices) ve bilgisayar sayfası (pc) tek bölüm
    ['page' => 'labs', 'href' => 'labs', 'icon' => 'devices', 'show' => can_view('labs') || can_view('devices'), 'section' => '', 'also' => ['devices', 'pc'], 'count' => 'devices'],
    ['page' => 'tasks', 'icon' => 'jobs', 'show' => can_view('tasks'), 'section' => 'İşlem'],
    ['page' => 'terminal', 'icon' => 'terminal', 'show' => can_view('terminal'), 'section' => 'İşlem'],
    ['page' => 'vision', 'icon' => 'eye', 'show' => can_view('vision'), 'section' => 'İşlem'],
    ['page' => 'deploy', 'icon' => 'package', 'show' => can_view('deploy'), 'section' => 'İşlem'],
    ['page' => 'policies', 'icon' => 'shield', 'show' => can_view('policies') || $role === 'superadmin', 'section' => 'Yönetim'],
    ['page' => 'logger', 'icon' => 'list', 'show' => can_view('logger'), 'section' => 'Yönetim'],
    ['page' => 'reports', 'icon' => 'chart', 'show' => can_view('reports'), 'section' => 'Yönetim'],
    ['page' => 'helpdesk', 'icon' => 'help', 'show' => can_view('helpdesk'), 'section' => 'Yönetim'],
    ['page' => 'system', 'icon' => 'server', 'show' => $role === 'superadmin', 'section' => 'Sunucu'],
    ['page' => 'system', 'key' => 'system-config', 'href' => 'system?view=ayarlar', 'icon' => 'sliders', 'show' => $role === 'superadmin', 'section' => 'Sunucu'],
    ['page' => 'settings', 'icon' => 'users', 'show' => can_view('settings'), 'section' => 'Sunucu'],
];
// Sunucu sayfalarının bölümleri menüde alt satır: sayfanın kendi sekme çubuğu (.tabs-in-nav) gizlenir ve menüden
// sürülür (pops_script.js). tab: sayfanın ?tab= değeri, ilk satır varsayılan; show: yetki.
$nav_subs = [
    'system' => ['base' => 'system', 'items' => [
        ['tab' => 'updates', 'label' => __('Güncellemeler')],
        ['tab' => 'health', 'label' => __('Sağlık ve yedek')],
    ]],
    'system-config' => ['base' => 'system?view=ayarlar', 'items' => [
        ['tab' => 'security', 'label' => __('Cihaz kaydı ve güvenlik')],
        ['tab' => 'notify', 'label' => __('Bildirimler ve saklama')],
        ['tab' => 'modules', 'label' => __('Modüller')],
        ['tab' => 'integrations', 'label' => __('Entegrasyonlar')],
    ]],
    'settings' => ['base' => 'settings', 'items' => [
        ['tab' => 'users', 'label' => __('Kullanıcılar')],
        ['tab' => 'units', 'label' => __('Birimler'), 'show' => $role === 'superadmin'],
        ['tab' => 'security', 'label' => __('Giriş ve güvenlik')],
        ['tab' => 'general', 'label' => __('Genel')],
    ]],
];

$grouped = [];
foreach ($nav_items as $item) {
    if (!$item['show']) continue;
    $grouped[$item['section']][] = $item;
}

foreach ($grouped as $section => $items): ?>
    <div class="nav-section">
        <?php if ($section !== ''): ?><div class="nav-section-title"><?php echo htmlspecialchars(__x($section, 'nav'), ENT_QUOTES, 'UTF-8'); ?></div><?php endif; ?>
        <?php foreach ($items as $item):
            $key = $item['key'] ?? $item['page'];
            $isActive = in_array($pops_page_key, array_merge([$key], $item['also'] ?? []), true);
            $subs = isset($nav_subs[$key]) ? array_values(array_filter($nav_subs[$key]['items'], fn($x) => $x['show'] ?? true)) : [];
        ?>
            <a href="<?php echo htmlspecialchars($item['href'] ?? ($item['page'] === 'index' ? './' : $item['page']), ENT_QUOTES, 'UTF-8'); ?>" class="nav-item<?php echo $isActive ? ' active' : ''; ?><?php echo $subs ? ' has-subs' : ''; ?>"<?php echo $isActive ? ' aria-current="page"' : ''; ?> data-page="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>">
                <?php echo pops_icon($item['icon']); ?>
                <span><?php echo htmlspecialchars($pops_titles[$key] ?? $item['page'], ENT_QUOTES, 'UTF-8'); ?></span>
                <span class="n" data-count="<?php echo htmlspecialchars($item['count'] ?? $item['page'], ENT_QUOTES, 'UTF-8'); ?>"></span>
                <?php if ($subs): ?><span class="nav-caret" data-caret="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>" role="button" aria-label="<?php echo htmlspecialchars(__('Bölümleri göster'), ENT_QUOTES, 'UTF-8'); ?>" aria-expanded="<?php echo $isActive ? 'true' : 'false'; ?>"><?php echo pops_icon('chev'); ?></span><?php endif; ?>
            </a>
            <?php if ($subs):
                $curTab = $isActive ? ($_GET['tab'] ?? '') : '';
                if (!in_array($curTab, array_column($subs, 'tab'), true)) { $curTab = $subs[0]['tab']; }
                $base = $nav_subs[$key]['base'];
            ?>
            <div class="nav-sub nav-tabs" data-for="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>"<?php echo $isActive ? '' : ' hidden'; ?>>
                <?php foreach ($subs as $i => $sub):
                    $href = $i === 0 ? $base : $base . (strpos($base, '?') === false ? '?' : '&') . 'tab=' . rawurlencode($sub['tab']);
                    $on = $isActive && $curTab === $sub['tab'];
                ?>
                <a href="<?php echo htmlspecialchars($href, ENT_QUOTES, 'UTF-8'); ?>" data-tab="<?php echo htmlspecialchars($sub['tab'], ENT_QUOTES, 'UTF-8'); ?>"<?php echo $on ? ' class="active" aria-current="true"' : ''; ?>><span><?php echo htmlspecialchars($sub['label'], ENT_QUOTES, 'UTF-8'); ?></span></a>
                <?php endforeach; ?>
            </div>
            <?php endif; ?>
        <?php endforeach; ?>
    </div>
<?php endforeach; ?>
