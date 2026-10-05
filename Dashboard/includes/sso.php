<?php
// Kurumsal giriş (OpenID Connect) için giriş sayfasının yardımcıları; yalnızca login.php kullanır (onun
// pops_api_post'u ile). Dizin (LDAP / Active Directory) girişi ayrı bir şey istemez: aynı kullanıcı adı/şifre formu.
// Akış (docs/security.md, Backend/pops/routers/sso.py):
//   1. "<sağlayıcı> ile giriş yap" → login?sso=start: rastgele bir bağ (binding) üretilip PHP oturumuna yazılır,
//      tarayıcı özetiyle birlikte sunucunun /api/auth/oidc/start ucuna gider, oradan sağlayıcıya.
//   2. Sağlayıcıdan dönüşte sunucu tarayıcıyı login?sso=<bilet> adresine gönderir; bilet bu oturumdaki bağla
//      birlikte bozdurulur. Başka bir tarayıcıda başlatılan akışın bileti burada işe yaramaz.
require_once __DIR__ . '/i18n.php';

// Giriş sayfası hangi düğmeleri göstersin; sunucuya ulaşılamazsa hiçbiri
function pops_sso_info(): array
{
    $ch = curl_init(API_INTERNAL_URL . '/api/auth/sso');
    curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
    curl_setopt($ch, CURLOPT_TIMEOUT, 2);
    $response = curl_exec($ch);
    $httpcode = curl_getinfo($ch, CURLINFO_HTTP_CODE);
    curl_close($ch);
    $data = ($response !== false && $httpcode === 200) ? json_decode($response, true) : null;
    return is_array($data) ? $data : [];
}

// Girişten sonra açılacak panel yolu: yalnızca "/" ile başlayan yerel yol (sunucudaki denetimin aynısı)
function pops_sso_safe_next($next): string
{
    if (!is_string($next) || $next === '' || strlen($next) > 256 || strpos($next, '..') !== false) {
        return '';
    }
    return preg_match('~\A/(?![/\\\\])[A-Za-z0-9_\-/.]*(\?[A-Za-z0-9_\-=&%.]*)?\z~', $next) ? $next : '';
}

function pops_sso_start($next): void
{
    $binding = bin2hex(random_bytes(32));
    $_SESSION['sso_binding'] = $binding;
    $query = ['b' => hash('sha256', $binding)];
    $next = pops_sso_safe_next($next);
    if ($next !== '') {
        $query['next'] = $next;
    }
    header('Cache-Control: no-store');
    header('Location: ' . API_URL . '/auth/oidc/start?' . http_build_query($query));
    exit;
}

// [$responseData, $httpcode, $error]: şifreli girişin yanıtıyla aynı biçim (2FA açıksa totp_required)
function pops_sso_redeem($ticket): array
{
    $binding = $_SESSION['sso_binding'] ?? '';
    unset($_SESSION['sso_binding']);
    if (!is_string($ticket) || $ticket === '' || strlen($ticket) > 128 || $binding === '') {
        return [null, 400, __('Kurumsal giriş başka bir tarayıcıda ya da sekmede başlatılmış; yeniden deneyin.')];
    }
    $result = pops_api_post('/api/auth/sso/redeem', ['ticket' => $ticket, 'binding' => $binding]);
    $next = pops_sso_safe_next($result[0]['next'] ?? '');
    if ($next !== '') {
        $_SESSION['sso_next'] = $next;
    }
    return $result;
}

// Girişten sonra gidilecek adres (kurumsal girişte istenen sayfa, yoksa kontrol merkezi)
function pops_sso_take_next(): string
{
    $next = pops_sso_safe_next($_SESSION['sso_next'] ?? '');
    unset($_SESSION['sso_next']);
    return $next !== '' ? '.' . $next : './';
}

// Sunucunun geri dönüşte bildirdiği hata kodu (ayrıntı sunucu logundadır)
function pops_sso_error_text($code): string
{
    switch ($code) {
        case 'state':
            return __('Giriş isteğinin süresi doldu ya da başka bir tarayıcıda başlatıldı; yeniden deneyin.');
        case 'provider':
            return __('Kimlik sağlayıcısı girişi reddetti.');
        case 'token':
            return __('Kimlik sağlayıcısının yanıtı doğrulanamadı; yöneticinize başvurun.');
        case 'access':
            return __('Bu hesabın panele erişim yetkisi yok.');
        case 'conflict':
            return __('Bu kullanıcı adı panelde başka bir hesaba ait; yöneticinize başvurun.');
        case 'unavailable':
            return __('Kimlik sağlayıcısına ulaşılamadı; yerel hesabınızla giriş yapabilirsiniz.');
        default:
            return __('Kurumsal giriş tamamlanamadı.');
    }
}
