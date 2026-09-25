<?php
// Oturum ve JWT cerezi yardimcilari.
// JWT, JavaScript'in okuyamayacagi httpOnly bir cerezde tasinir (localStorage kullanilmaz);
// tarayici bu cerezi ayni origin'e giden /api/ ve /ws/ isteklerine kendisi ekler.

const POPS_JWT_COOKIE = 'pops_jwt';

function pops_is_https(): bool
{
    return (!empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off')
        || strtolower($_SERVER['HTTP_X_FORWARDED_PROTO'] ?? '') === 'https';
}

// PHP oturum cerezi de httpOnly olmali; aksi halde XSS ile calinan oturumdan JWT tekrar alinabilir.
function pops_session_start(): void
{
    if (session_status() === PHP_SESSION_ACTIVE) {
        return;
    }
    session_set_cookie_params([
        'path' => '/',
        'secure' => pops_is_https(),
        'httponly' => true,
        'samesite' => 'Lax',
    ]);
    session_start();
}

function pops_set_jwt_cookie(string $token): void
{
    setcookie(POPS_JWT_COOKIE, $token, [
        'expires' => 0,
        'path' => '/',
        'secure' => pops_is_https(),
        'httponly' => true,
        'samesite' => 'Strict',
    ]);
}

function pops_clear_jwt_cookie(): void
{
    setcookie(POPS_JWT_COOKIE, '', [
        'expires' => time() - 3600,
        'path' => '/',
        'secure' => pops_is_https(),
        'httponly' => true,
        'samesite' => 'Strict',
    ]);
}
