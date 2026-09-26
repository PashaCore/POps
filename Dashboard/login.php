<?php
require_once __DIR__ . '/includes/config.php';
require_once __DIR__ . '/includes/session.php';
pops_session_start();

if (isset($_SESSION['loggedin']) && $_SESSION['loggedin'] === true) {
    header('Location: index.php');
    exit;
}

// "Baştan giriş yap": bekleyen 2FA challenge'ını temizle.
if (isset($_GET['reset'])) {
    unset($_SESSION['totp_challenge'], $_SESSION['totp_username']);
    header('Location: login.php');
    exit;
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
        return [null, 0, 'API Sunucusuna Ulaşılamıyor! (Detay: ' . $curl_error . ')'];
    }
    $decoded = json_decode($response, true);
    if (json_last_error() !== JSON_ERROR_NONE) {
        return [null, $httpcode, "API Yanıt Hatası (HTTP $httpcode): " . strip_tags(substr($response, 0, 150))];
    }
    return [$decoded, $httpcode, ''];
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
    session_regenerate_id(true);
    pops_set_jwt_cookie($_SESSION['jwt_token']);
    header('Location: index.php');
    exit;
}

$error = '';
$show_otp = false;   // true ise şifre doğrulandı, 2. adım (kod) formunu göster
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
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
            $error = $responseData['detail'] ?? $responseData['message'] ?? "Kod doğrulanamadı (HTTP $httpcode)";
            $show_otp = true;   // aynı ekranda kal, tekrar denesin
        }

    // 1. ADIM: kullanıcı adı + şifre.
    } else {
        $username = trim($_POST['username'] ?? '');
        $password = $_POST['password'] ?? '';
        if (empty($username) || empty($password)) {
            $error = 'Kullanıcı adı ve şifre boş bırakılamaz!';
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
                $error = $responseData['detail'] ?? $responseData['message'] ?? "Giriş reddedildi (HTTP $httpcode)";
            }
        }
    }
} elseif (!empty($_SESSION['totp_challenge'])) {
    // Sayfa yenilendi ama challenge hâlâ geçerli olabilir → kod ekranında kal.
    $show_otp = true;
}
?>
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <meta name="color-scheme" content="light">
    <title>POps | Yönetici Girişi</title>
    <link rel="icon" type="image/png" href="assets/favicon/favicon-96x96.png" sizes="96x96" />
    <link rel="icon" type="image/svg+xml" href="assets/favicon/favicon.svg" />
    <link rel="shortcut icon" href="assets/favicon/favicon.ico" />
    <link rel="apple-touch-icon" sizes="180x180" href="assets/favicon/apple-touch-icon.png" />
    <link rel="manifest" href="assets/favicon/site.webmanifest" />
    <link rel="preconnect" href="https://fonts.googleapis.com">
    <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet">
    <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.4.0/css/all.min.css">
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
    <div class="login-wrapper">
        <div class="login-logo">
            <img src="assets/favicon/apple-touch-icon.png" alt="POps Logo" style="width:100%;height:100%;object-fit:cover;border-radius:inherit;">
        </div>
        <div class="login-header">
            <h2>POps</h2>
            <p>Operations Platform</p>
        </div>
        
        <div class="card p-6">
            <?php if ($error): ?>
                <div class="error-box"><?php echo htmlspecialchars($error); ?></div>
            <?php endif; ?>

            <?php if ($show_otp): ?>
            <form method="POST" action="">
                <div class="input-wrapper">
                    <label>Doğrulama Kodu</label>
                    <input type="text" name="otp" inputmode="numeric" autocomplete="one-time-code"
                           pattern="[0-9]*" maxlength="6" placeholder="123456" required autofocus
                           style="letter-spacing:0.4em; text-align:center; font-size:1.25rem;">
                    <p style="margin-top:0.5rem; font-size:var(--text-xs); color:var(--text-tertiary);">
                        Authenticator uygulamanızdaki 6 haneli kodu girin.
                    </p>
                </div>
                <button type="submit" class="btn block mt-6" style="padding: 0.875rem;">Doğrula ve Giriş Yap</button>
                <a href="login.php?reset=1" style="display:block; text-align:center; margin-top:var(--space-4); font-size:var(--text-sm); color:var(--text-tertiary);">← Baştan giriş yap</a>
            </form>
            <?php else: ?>
            <form method="POST" action="">
                <div class="input-wrapper">
                    <label>Kullanıcı Adı</label>
                    <input type="text" name="username" placeholder="admin" required autofocus>
                </div>
                <div class="input-wrapper">
                    <label>Şifre</label>
                    <input type="password" name="password" placeholder="••••••••" required>
                </div>
                <button type="submit" class="btn block mt-6" style="padding: 0.875rem;">Sisteme Giriş Yap</button>
            </form>
            <?php endif; ?>
        </div>
        <div style="text-align:center; margin-top: var(--space-6); font-size: var(--text-xs); color: var(--text-muted); line-height: 1.6;">
            &copy; <?php echo date("Y"); ?> POps CORE<br>
            <span style="opacity: 0.8;">
                Created by Mehmet Ali Avcı
                <a href="https://www.linkedin.com/in/p4sha" target="_blank" style="color: inherit; margin-left: 4px; transition: color 0.2s;" onmouseover="this.style.color='var(--primary-500)'" onmouseout="this.style.color='inherit'"><i class="fab fa-linkedin"></i></a>
                <a href="https://github.com/TheP4SHA/TheP4SHA" target="_blank" style="color: inherit; margin-left: 4px; transition: color 0.2s;" onmouseover="this.style.color='var(--primary-500)'" onmouseout="this.style.color='inherit'"><i class="fab fa-github"></i></a>
            </span>
        </div>
    </div>
</body>
</html>