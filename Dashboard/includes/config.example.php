<?php
// KURULUM ŞABLONU — kurulum sihirbazı bunu config.php olarak üretir (config.php depoda izlenmez).
// Ortama özel adresler koda gömülmez: önce ortam değişkeni (getenv), sonra proje kökündeki .env,
// en sonda buradaki güvenli varsayılan kullanılır.
define('API_URL', pops_env('POPS_API_URL', 'https://ornek-alan-adiniz/api'));

function pops_env(string $key, ?string $default = null): ?string
{
    $value = getenv($key);
    if ($value !== false && $value !== '') {
        return $value;
    }

    static $dotenv = null;
    if ($dotenv === null) {
        $dotenv = [];
        $path = dirname(__DIR__, 2) . '/.env';
        if (is_readable($path)) {
            foreach (file($path, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) as $line) {
                $line = trim($line);
                if ($line === '' || $line[0] === '#' || strpos($line, '=') === false) {
                    continue;
                }
                [$k, $v] = explode('=', $line, 2);
                $dotenv[trim($k)] = trim(trim($v), "\"'");
            }
        }
    }

    return ($dotenv[$key] ?? '') !== '' ? $dotenv[$key] : $default;
}

// Panelin sunucu tarafından (PHP -> FastAPI) konuştuğu iç API adresi
define('API_INTERNAL_URL', rtrim(pops_env('POPS_API_INTERNAL_URL', 'http://localhost:8000'), '/'));

// Herkese açık demo: "kullanıcı:şifre" verilirse giriş sayfası bu salt okunur hesabı gösterir (boş = gösterilmez).
// Hesabın kendisi sunucuda POPS_DEMO_USERS ile salt okunur yapılır; bkz. docs/configuration.md
define('POPS_DEMO_LOGIN', (string) pops_env('POPS_DEMO_LOGIN', ''));
