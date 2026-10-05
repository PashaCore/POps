<?php
require_once __DIR__ . '/includes/config.php';
require_once __DIR__ . '/includes/session.php';
require_once __DIR__ . '/includes/i18n.php';
require_once __DIR__ . '/includes/sso.php';
pops_session_start();

// Dil seçici (Türkçe / English): seçim çereze yazılır, sayfa yeniden açılır (bekleyen 2FA adımı oturumda kalır)
if (isset($_GET['lang']) && ($_GET['lang'] === 'tr' || $_GET['lang'] === 'en')) {
    pops_set_lang_cookie($_GET['lang']);
    header('Location: login');
    exit;
}
// Çerez yoksa tarayıcının dili (Accept-Language) seçilir ve çereze yazılır; panel girişten sonra aynı dilde açılır
pops_i18n_init('login', true);

if (isset($_SESSION['loggedin']) && $_SESSION['loggedin'] === true) {
    header('Location: ./');
    exit;
}

// "Baştan giriş yap": bekleyen 2FA challenge'ını temizle.
if (isset($_GET['reset'])) {
    unset($_SESSION['totp_challenge'], $_SESSION['totp_username'], $_SESSION['sso_next']);
    header('Location: login');
    exit;
}

// Kurumsal giriş (OpenID Connect): sağlayıcıya git (includes/sso.php)
if (($_GET['sso'] ?? '') === 'start') {
    pops_sso_start($_GET['next'] ?? '');
}

// API'ye JSON POST atan yardımcı; [$responseData, $httpcode, $error] döner.
function pops_api_post($path, $payload) {
    $ch = curl_init(API_INTERNAL_URL . $path);
    $data = json_encode($payload);
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_POST, true);
    curl_setopt($ch, CURLOPT_POSTFIELDS, $data);
    curl_setopt($ch, CURLOPT_HTTPHEADER, [
        'Content-Type: application/json',
        'Content-Length: ' . strlen($data),
        // Giriş denemesi sınırı (dakikada 10) tarayıcının IP'sine göre işlesin; aksi halde
        // bütün girişler PHP'nin 127.0.0.1 adresinden geliyor görünür ve tek kotayı paylaşır.
        // REMOTE_ADDR, Apache mod_remoteip sayesinde Cloudflare arkasındaki gerçek istemcidir.
        'X-Forwarded-For: ' . ($_SERVER['REMOTE_ADDR'] ?? '')
    ]);
    curl_setopt($ch, CURLOPT_TIMEOUT, 10);
    $response = curl_exec($ch);
    $httpcode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    $curl_error = curl_error($ch);
    curl_close($ch);
    if ($response === false) {
        return [null, 0, __('API Sunucusuna Ulaşılamıyor! (Detay: {detail})', ['detail' => $curl_error])];
    }
    $decoded = json_decode($response, true);
    if (json_last_error() !== JSON_ERROR_NONE) {
        return [null, $httpcode, __('API Yanıt Hatası (HTTP {code}): {body}', ['code' => $httpcode, 'body' => strip_tags(substr($response, 0, 150))])];
    }
    return [$decoded, $httpcode, ''];
}

// Kurum adı ve logosu (Ayarlar → Genel → Kurum); sunucuya ulaşılamazsa POps'un kendi görünümü kalır
function pops_branding() {
    $ch = curl_init(API_INTERNAL_URL . '/api/branding');
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_TIMEOUT, 2);
    $response = curl_exec($ch);
    $httpcode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);
    $data = ($response !== false && $httpcode === 200) ? json_decode($response, true) : null;
    return is_array($data) ? $data : [];
}

// Sunucunun Türkçe hata iletisi (detail / message) gösterilecek dilde; ileti yoksa $fallback
function pops_login_error($responseData, $fallback) {
    $msg = $responseData['detail'] ?? $responseData['message'] ?? null;
    return is_string($msg) && $msg !== '' ? __($msg) : $fallback;
}

// Başarılı yanıtı session'a yazıp panele yönlendirir.
function pops_finish_login($responseData, $fallback_username) {
    $_SESSION['loggedin'] = true;
    $_SESSION['username'] = $responseData['username'] ?? $fallback_username;
    $_SESSION['role'] = $responseData['role'] ?? 'superadmin';
    $_SESSION['jwt_token'] = $responseData['token'] ?? '';
    $perms = [];
    if (isset($responseData['permissions'])) {
        $decoded = json_decode($responseData['permissions'], true);
        if (is_array($decoded)) $perms = $decoded;
    }
    $_SESSION['permissions'] = $perms;
    unset($_SESSION['totp_challenge'], $_SESSION['totp_username']);
    $next = pops_sso_take_next();
    session_regenerate_id(true);
    pops_set_jwt_cookie($_SESSION['jwt_token']);
    header('Location: ' . $next);
    exit;
}

$error = '';
$show_otp = false;   // true ise şifre doğrulandı, 2. adım (kod) formunu göster
if ($_SERVER['REQUEST_METHOD'] === 'GET' && isset($_GET['sso_error'])) {
    $error = pops_sso_error_text($_GET['sso_error']);
} elseif ($_SERVER['REQUEST_METHOD'] === 'GET' && isset($_GET['sso'])) {
    // Sağlayıcıdan dönüş: tek kullanımlık bilet bu oturumun bağıyla bozdurulur
    list($responseData, $httpcode, $err) = pops_sso_redeem($_GET['sso']);
    if ($err) {
        $error = $err;
    } elseif (($responseData['status'] ?? '') === 'success') {
        pops_finish_login($responseData, '');
    } elseif (($responseData['status'] ?? '') === 'totp_required') {
        $_SESSION['totp_challenge'] = $responseData['challenge'] ?? '';
        $_SESSION['totp_username'] = '';
        $show_otp = true;
    } else {
        $error = pops_login_error($responseData, __('Kurumsal giriş tamamlanamadı.'));
    }
} elseif ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $otp = trim($_POST['otp'] ?? '');

    // 2. ADIM: şifre zaten doğrulandı, elimizde challenge var; sadece kodu doğrula.
    if ($otp !== '' && !empty($_SESSION['totp_challenge'])) {
        list($responseData, $httpcode, $err) = pops_api_post('/api/admin/login/totp', [
            'challenge' => $_SESSION['totp_challenge'],
            'otp'       => $otp,
        ]);
        if ($err) {
            $error = $err;
        } elseif (($responseData['status'] ?? '') === 'success') {
            pops_finish_login($responseData, $_SESSION['totp_username'] ?? '');
        } else {
            $error = pops_login_error($responseData, __('Kod doğrulanamadı (HTTP {code})', ['code' => $httpcode]));
            $show_otp = true;   // aynı ekranda kal, tekrar denesin
        }

    // 1. ADIM: kullanıcı adı + şifre.
    } else {
        $username = trim($_POST['username'] ?? '');
        $password = $_POST['password'] ?? '';
        if (empty($username) || empty($password)) {
            $error = __('Kullanıcı adı ve şifre boş bırakılamaz.');
        } else {
            list($responseData, $httpcode, $err) = pops_api_post('/api/admin/login', [
                'username' => $username,
                'password' => $password,
            ]);
            if ($err) {
                $error = $err;
            } elseif (($responseData['status'] ?? '') === 'success') {
                pops_finish_login($responseData, $username);
            } elseif (($responseData['status'] ?? '') === 'totp_required') {
                // Şifre doğru; 2FA açık. Challenge'ı session'da tut, kod formunu göster.
                $_SESSION['totp_challenge'] = $responseData['challenge'] ?? '';
                $_SESSION['totp_username'] = $username;
                $show_otp = true;
            } else {
                $error = pops_login_error($responseData, __('Giriş reddedildi (HTTP {code})', ['code' => $httpcode]));
            }
        }
    }
} elseif (!empty($_SESSION['totp_challenge'])) {
    // Sayfa yenilendi ama challenge hâlâ geçerli olabilir → kod ekranında kal.
    $show_otp = true;
}
?>
<!DOCTYPE html>
<html lang="<?php echo htmlspecialchars(pops_lang(), ENT_QUOTES, 'UTF-8'); ?>">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <meta name="color-scheme" content="light">
    <title>POps | <?php _e('Yönetici Girişi'); ?></title>
    <link rel="icon" type="image/png" href="assets/favicon/favicon-96x96.png" sizes="96x96" />
    <link rel="icon" type="image/svg+xml" href="assets/favicon/favicon.svg" />
    <link rel="shortcut icon" href="assets/favicon/favicon.ico" />
    <link rel="apple-touch-icon" sizes="180x180" href="assets/favicon/apple-touch-icon.png" />
    <link rel="manifest" href="assets/favicon/site.webmanifest" />
    <link rel="stylesheet" href="assets/pops_theme.css?v=<?php echo time(); ?>">
    <script>
        try { localStorage.removeItem('pops_theme'); } catch (e) {}
    </script>
    <style>
        body { 
            display: flex; 
            align-items: center; 
            justify-content: center; 
            min-height: 100vh;
            background: var(--bg-app);
        }
        .login-wrapper {
            width: 100%;
            max-width: 420px;
            padding: var(--space-4);
            position: relative;
        }
        .login-logo {
            width: 80px;
            height: 80px;
            border-radius: var(--radius-lg);
            display: flex;
            align-items: center;
            justify-content: center;
            font-size: 1.75rem;
            font-weight: var(--fw-bold);
            margin: 0 auto var(--space-6);
            box-shadow: var(--shadow-md);
        }
        .login-header {
            text-align: center;
            margin-bottom: var(--space-8);
        }
        .login-header h2 {
            font-size: var(--text-2xl);
            margin-bottom: var(--space-2);
            color: var(--text-primary);
        }
        .login-header p {
            color: var(--text-tertiary);
            font-size: var(--text-sm);
        }
        .input-wrapper {
            margin-bottom: var(--space-5);
        }
        .input-wrapper label {
            display: block;
            font-size: var(--text-sm);
            font-weight: var(--fw-medium);
            color: var(--text-secondary);
            margin-bottom: 0.375rem;
        }
        .login-logo.org { background: var(--bg-surface); box-shadow: 0 0 0 1px var(--border-subtle); padding: 10px; }
        .login-logo.org img { width: 100%; height: 100%; object-fit: contain; }
        .sso-sep { display: flex; align-items: center; gap: var(--space-3); margin: var(--space-5) 0; font-size: var(--text-xs); color: var(--text-muted); }
        .sso-sep::before, .sso-sep::after { content: ''; flex: 1; border-top: 1px solid var(--border-subtle); }
        .sso-btn { padding: 0.875rem; text-align: center; }
        .login-foot { text-align: center; margin-top: var(--space-6); font-size: var(--text-xs); color: var(--text-muted); }
        .login-foot a { color: inherit; }
        .login-foot a:hover { color: var(--primary-500); }
        .login-lang { display: flex; justify-content: center; gap: 2px; width: fit-content; margin: var(--space-4) auto 0; padding: 2px; border-radius: 8px; background: var(--bg-surface-2); box-shadow: 0 0 0 1px var(--border-subtle); }
        .login-lang a { padding: 3px 12px; border-radius: 6px; font-size: var(--text-xs); color: var(--text-tertiary); text-decoration: none; }
        .login-lang a:hover { color: var(--text-primary); }
        .login-lang a[aria-current="true"] { background: var(--bg-surface); color: var(--text-primary); box-shadow: 0 1px 2px rgba(0, 0, 0, 0.08); }
        .error-box {
            background: var(--danger-bg);
            border: 1px solid var(--danger-border);
            color: var(--danger-text);
            padding: var(--space-3) var(--space-4);
            border-radius: var(--radius-md);
            font-size: var(--text-sm);
            text-align: center;
            margin-bottom: var(--space-6);
        }
    </style>
</head>
<body>
    <?php
    $brand = pops_branding();
    $org_name = trim((string) ($brand['org_name'] ?? ''));
    $org_logo = !empty($brand['logo']) ? API_URL . '/branding/logo?v=' . rawurlencode((string) ($brand['logo_v'] ?? '')) : '';
    $demo = explode(':', defined('POPS_DEMO_LOGIN') ? POPS_DEMO_LOGIN : '', 2);   // herkese açık demo: [kullanıcı, şifre]
    ?>
    <div class="login-wrapper">
        <div class="login-logo<?php echo $org_logo ? ' org' : ''; ?>">
            <?php if ($org_logo): ?>
            <img src="<?php echo htmlspecialchars($org_logo, ENT_QUOTES, 'UTF-8'); ?>" alt="<?php echo htmlspecialchars($org_name !== '' ? $org_name : __('Kurum logosu'), ENT_QUOTES, 'UTF-8'); ?>">
            <?php else: ?>
            <img src="assets/favicon/apple-touch-icon.png" alt="POps" style="width:100%;height:100%;object-fit:cover;border-radius:inherit;">
            <?php endif; ?>
        </div>
        <div class="login-header">
            <h2><?php echo htmlspecialchars($org_name !== '' ? $org_name : 'POps', ENT_QUOTES, 'UTF-8'); ?></h2>
            <p><?php $org_name !== '' ? _e('POps yönetim paneli') : _e('Yönetim paneli'); ?></p>
        </div>
        
        <div class="card p-6">
            <?php if ($error): ?>
                <div class="error-box"><?php echo htmlspecialchars($error); ?></div>
            <?php endif; ?>

            <?php if ($show_otp): ?>
            <form method="POST" action="">
                <div class="input-wrapper">
                    <label><?php _e('Doğrulama kodu'); ?></label>
                    <input type="text" name="otp" inputmode="numeric" autocomplete="one-time-code"
                           pattern="[0-9]*" maxlength="6" placeholder="123456" required autofocus
                           style="letter-spacing:0.4em; text-align:center; font-size:1.25rem;">
                    <p style="margin-top:0.5rem; font-size:var(--text-xs); color:var(--text-tertiary);">
                        <?php _e('Authenticator uygulamanızdaki 6 haneli kodu girin.'); ?>
                    </p>
                </div>
                <button type="submit" class="btn block mt-6" style="padding: 0.875rem;"><?php _e('Doğrula ve giriş yap'); ?></button>
                <a href="login?reset=1" style="display:block; text-align:center; margin-top:var(--space-4); font-size:var(--text-sm); color:var(--text-tertiary);">← <?php _e('Baştan giriş yap'); ?></a>
            </form>
            <?php else: ?>
            <form method="POST" action="">
                <?php if (count($demo) === 2): ?><div class="alert info" style="margin-bottom: var(--space-5);"><div><?php _e('Demo (salt okunur):'); ?> <?php _e('kullanıcı'); ?> <b><?php echo htmlspecialchars($demo[0], ENT_QUOTES, 'UTF-8'); ?></b>, <?php _e('şifre'); ?> <b><?php echo htmlspecialchars($demo[1], ENT_QUOTES, 'UTF-8'); ?></b></div></div><?php endif; ?>
                <div class="input-wrapper">
                    <label><?php _e('Kullanıcı adı'); ?></label>
                    <input type="text" name="username" placeholder="admin" value="<?php echo htmlspecialchars($demo[0], ENT_QUOTES, 'UTF-8'); ?>" required autofocus>
                </div>
                <div class="input-wrapper">
                    <label><?php _e('Şifre'); ?></label>
                    <input type="password" name="password" placeholder="••••••••" required>
                </div>
                <button type="submit" class="btn block mt-6" style="padding: 0.875rem;"><?php _e('Giriş yap'); ?></button>
            </form>
            <?php $sso = pops_sso_info(); if (!empty($sso['oidc'])): ?>
            <div class="sso-sep"><span><?php _e('ya da'); ?></span></div>
            <a class="btn secondary block sso-btn" href="login?<?php echo htmlspecialchars(http_build_query(array_filter(['sso' => 'start', 'next' => pops_sso_safe_next($_GET['next'] ?? '')])), ENT_QUOTES, 'UTF-8'); ?>"><?php _e('{name} ile giriş yap', ['name' => trim((string) ($sso['oidc_name'] ?? '')) ?: __('Kurumsal hesap')]); ?></a>
            <?php endif; ?>
            <?php endif; ?>
        </div>
        <div class="login-foot">
            <a href="https://github.com/PashaCore/POps" target="_blank" rel="noopener">POps</a> · Pasha Core
        </div>
        <nav class="login-lang" aria-label="Dil / Language">
            <a href="login?lang=tr" lang="tr" aria-current="<?php echo pops_lang() === 'tr' ? 'true' : 'false'; ?>">Türkçe</a>
            <a href="login?lang=en" lang="en" aria-current="<?php echo pops_lang() === 'en' ? 'true' : 'false'; ?>">English</a>
        </nav>
    </div>
</body>
</html>