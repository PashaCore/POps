<?php
// Bu dosya kurulum sihirbazi tarafindan otomatik uretilmistir.
define('API_URL', 'https://dev.pashacore.com.tr/api');

// Ortama ozel degerler (IP, sifre vb.) koda gomulmez. Once web sunucusunun ortam
// degiskenlerine, sonra proje kokundeki .env dosyasina bakilir (bkz. .env.example).
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

// Panelin sunucu tarafindan (PHP -> FastAPI) konustugu ic API adresi
define('API_INTERNAL_URL', rtrim(pops_env('POPS_API_INTERNAL_URL', 'http://localhost:8000'), '/'));
