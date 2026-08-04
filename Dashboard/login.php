<?php
session_start();

if (isset($_SESSION['loggedin']) && $_SESSION['loggedin'] === true) {
    header('Location: index.php');
    exit;
}

$error = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    $username = trim($_POST['username'] ?? '');
    $password = $_POST['password'] ?? '';

    if (empty($username) || empty($password)) {
        $error = 'Kullanıcı adı ve şifre boş bırakılamaz!';
    } else {
        // === DOĞRUDAN SUNUCU İÇİ BAĞLANTI (404 ve IP sorunlarını kökten çözer) ===
        // API'nin gerçek kapısı:
        $url = 'http://127.0.0.1:8000/api/admin/login';
        
        $data = json_encode([
            'username' => $username,
            'password' => $password
        ]);

        $ch = curl_init($url);
        curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
        curl_setopt($ch, CURLOPT_POST, true);
        curl_setopt($ch, CURLOPT_POSTFIELDS, $data);
        curl_setopt($ch, CURLOPT_HTTPHEADER, [
            'Content-Type: application/json',
            'Content-Length: ' . strlen($data)
        ]);
        curl_setopt($ch, CURLOPT_TIMEOUT, 10);

        $response = curl_exec($ch);
        $httpcode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
        $curl_error = curl_error($ch);
        curl_close($ch);

        if ($response === false) {
            $error = 'API Sunucusuna Ulaşılamıyor! (Detay: ' . $curl_error . ')';
        } else {
            $responseData = json_decode($response, true);
            
            if (json_last_error() !== JSON_ERROR_NONE) {
                $error = "API Yanıt Hatası (HTTP $httpcode): " . strip_tags(substr($response, 0, 150));
            } else {
                if (isset($responseData['status']) && $responseData['status'] === 'success') {
                    // Şifre doğru, içeri al!
                    $_SESSION['loggedin'] = true;
                    $_SESSION['username'] = $responseData['username'] ?? $username;
                    $_SESSION['role'] = $responseData['role'] ?? 'superadmin';
                    // JWT token'ı sakla — WebSocket ve AJAX çağrılarında kullanılacak
                    $_SESSION['jwt_token'] = $responseData['token'] ?? '';
                    
                    // Yetkileri session'a dizi olarak kaydet
                    $perms = [];
                    if (isset($responseData['permissions'])) {
                        $decoded = json_decode($responseData['permissions'], true);
                        if (is_array($decoded)) $perms = $decoded;
                    }
                    $_SESSION['permissions'] = $perms;
                    
                    // Token'ı JS'e geçirmek için query param ile yönlendir
                    $jwt = urlencode($responseData['token'] ?? '');
                    session_regenerate_id(true);
                    header("Location: index.php?_jwt=$jwt");
                    exit;
                } else {
                    $error = $responseData['message'] ?? "Giriş reddedildi (HTTP $httpcode)";
                }
            }
        }
    }
}
?>
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
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
        const savedTheme = localStorage.getItem('pops_theme') || 'light';
        if (savedTheme === 'dark') {
            document.documentElement.setAttribute('data-theme', 'dark');
        }
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
        .theme-toggle-btn {
            position: absolute;
            top: var(--space-4);
            right: var(--space-4);
            width: 36px;
            height: 36px;
            border-radius: var(--radius-md);
            background: var(--bg-surface);
            border: 1px solid var(--border-subtle);
            color: var(--text-secondary);
            cursor: pointer;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: all 0.2s;
        }
        .theme-toggle-btn:hover {
            background: var(--bg-surface-2);
            color: var(--text-primary);
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
        <button class="theme-toggle-btn" onclick="toggleTheme()" title="Temayı Değiştir">
            <i class="fas fa-moon"></i>
        </button>
        
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

    <script>
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
    </script>
</body>
</html>