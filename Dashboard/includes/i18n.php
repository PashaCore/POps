<?php
// Arayüz dili: Türkçe (varsayılan) ya da İngilizce; tarayıcı başına pops_lang çerezinde (tr | en) tutulur.
// Türkçe metin koddaki anahtardır (gettext gibi): İngilizce karşılığı lang/en/common.json (ortak kabuk ve
// ortak betikler) ile lang/en/<sayfa>.json'dan gelir; karşılığı olmayan metin Türkçe kalır.
//   __('Cihazlar')                     metni döndürür (kaçırılmamış: HTML'e htmlspecialchars ile yazılır)
//   _e('Cihazlar')                     HTML'e kaçırılmış yazar (nitelik değerinde de güvenli)
//   __('{n} bilgisayar', ['n' => 3])   yer tutucular; İngilizce {"one": …, "other": …} ise n'ye göre seçilir
//   __x('Kapat', 'power')              aynı Türkçe metnin başka anlamı (sözlükte "Kapat|power")
// Sayfa betikleri için aynı sözlük header.php'de window.POPS_I18N olarak basılır (yalnızca İngilizcede).
// Kurallar ve sözlük: docs/i18n.md

const POPS_LANG_COOKIE = 'pops_lang';

function pops_i18n_state(?array $set = null): array
{
    static $state = ['lang' => 'tr', 'dict' => []];
    if ($set !== null) {
        $state = $set;
    }
    return $state;
}

function pops_lang(): string
{
    return pops_i18n_state()['lang'];
}

// Tarih ve sayı biçimi (JS'de POps.locale)
function pops_locale(): string
{
    return pops_lang() === 'en' ? 'en-GB' : 'tr-TR';
}

// Accept-Language'da desteklenen diller içinde en çok tercih edilen (q'ya göre); ikisi de yoksa null
function pops_lang_from_header(string $header): ?string
{
    $best = null;
    $bestQ = 0.0;
    foreach (explode(',', $header) as $part) {
        $bits = explode(';', trim($part));
        $tag = strtolower(trim($bits[0]));
        $lang = substr($tag, 0, 2);
        if (($lang !== 'tr' && $lang !== 'en') || (strlen($tag) > 2 && $tag[2] !== '-')) {
            continue;
        }
        $q = 1.0;
        foreach (array_slice($bits, 1) as $p) {
            if (preg_match('/^\s*q\s*=\s*([01](?:\.\d{0,3})?)\s*$/i', $p, $m)) {
                $q = (float) $m[1];
            }
        }
        if ($q > $bestQ) {
            $best = $lang;
            $bestQ = $q;
        }
    }
    return $best;
}

function pops_set_lang_cookie(string $lang): void
{
    setcookie(POPS_LANG_COOKIE, $lang, [
        'expires' => time() + 365 * 24 * 3600,
        'path' => '/',
        'secure' => function_exists('pops_is_https') && pops_is_https(),
        'httponly' => false,   // dil seçicisi (JS) yazar; gizli bir değer değildir
        'samesite' => 'Lax',
    ]);
}

// Dosya bulunamaz ya da bozuksa boş sözlük: metinler Türkçe kalır, sayfa çalışmaya devam eder
function pops_i18n_load(string $name): array
{
    if (!preg_match('/^[a-z0-9_-]+$/', $name)) {
        return [];
    }
    $path = __DIR__ . '/../lang/en/' . $name . '.json';
    if (!is_file($path)) {
        return [];
    }
    $data = json_decode((string) file_get_contents($path), true);
    if (!is_array($data)) {
        error_log('POps i18n: ' . $name . '.json okunamadı: ' . json_last_error_msg());
        return [];
    }
    $out = [];
    foreach ($data as $k => $v) {
        if (is_string($v) && $v !== '') {
            $out[(string) $k] = $v;
        } elseif (is_array($v) && isset($v['other']) && is_string($v['other'])) {
            $out[(string) $k] = array_filter($v, 'is_string');
        }
    }
    return $out;
}

// Sayfa başında bir kez: dili seçer, İngilizcede ortak sözlükle sayfanın sözlüğünü birleştirir (sayfanınki önce gelir).
// $detect: çerez yoksa Accept-Language'a bakılır ve seçim çereze yazılır (giriş sayfası; sonra panel aynı dilde açılır).
function pops_i18n_init(string $page, bool $detect = false): void
{
    $cookie = $_COOKIE[POPS_LANG_COOKIE] ?? '';
    if ($cookie === 'tr' || $cookie === 'en') {
        $lang = $cookie;
    } else {
        $lang = 'tr';
        if ($detect) {
            $lang = pops_lang_from_header($_SERVER['HTTP_ACCEPT_LANGUAGE'] ?? '') === 'en' ? 'en' : 'tr';
            if (!headers_sent()) {
                pops_set_lang_cookie($lang);
            }
        }
    }
    $dict = [];
    if ($lang === 'en') {
        $dict = pops_i18n_load('common');
        if ($page !== 'common') {
            $dict = pops_i18n_load($page) + $dict;
        }
    }
    pops_i18n_state(['lang' => $lang, 'dict' => $dict]);
}

// window.POPS_I18N için (Türkçede boş)
function pops_i18n_dict(): array
{
    return pops_i18n_state()['dict'];
}

function pops_i18n_fill(string $text, array $params): string
{
    if (!$params) {
        return $text;
    }
    return preg_replace_callback('/\{(\w+)\}/u', static function ($m) use ($params) {
        return array_key_exists($m[1], $params) && $params[$m[1]] !== null ? (string) $params[$m[1]] : $m[0];
    }, $text);
}

function pops_i18n_lookup(string $key, string $fallback, array $params): string
{
    $v = pops_i18n_state()['dict'][$key] ?? null;
    if (is_array($v)) {
        $one = isset($params['n']) && is_numeric($params['n']) && abs((float) $params['n']) == 1;
        $v = ($one && isset($v['one'])) ? $v['one'] : ($v['other'] ?? null);
    }
    return pops_i18n_fill(is_string($v) && $v !== '' ? $v : $fallback, $params);
}

function __(string $text, array $params = []): string
{
    return pops_i18n_lookup($text, $text, $params);
}

function __x(string $text, string $context, array $params = []): string
{
    return pops_i18n_lookup($text . '|' . $context, $text, $params);
}

function _e(string $text, array $params = []): void
{
    echo htmlspecialchars(__($text, $params), ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}

function _ex(string $text, string $context, array $params = []): void
{
    echo htmlspecialchars(__x($text, $context, $params), ENT_QUOTES | ENT_SUBSTITUTE, 'UTF-8');
}
