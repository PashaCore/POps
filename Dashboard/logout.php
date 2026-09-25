<?php
// =====================================================================
// POps V4 Enterprise - Güvenli Çıkış (Logout) Modülü
// =====================================================================
require_once __DIR__ . '/includes/session.php';
pops_session_start();

// Tüm oturum değişkenlerini boşalt
$_SESSION = array();

// Oturum çerezini (cookie) tarayıcıdan tamamen sil (Tam Güvenlik)
if (ini_get("session.use_cookies")) {
    $params = session_get_cookie_params();
    setcookie(session_name(), '', time() - 42000,
        $params["path"], $params["domain"],
        $params["secure"], $params["httponly"]
    );
}

// Oturumu fiziksel olarak sunucuda yok et
session_destroy();

// httpOnly JWT çerezini de sil
pops_clear_jwt_cookie();

// Güvenli bir şekilde giriş ekranına şutla
header("Location: login.php");
exit;
?>