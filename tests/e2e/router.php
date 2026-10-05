<?php
// Yalnızca uçtan uca testler için `php -S` yönlendiricisi (bkz. global-setup.js). Gerçek kurulumda bu işi
// nginx/Apache yapar (Installer/server/nginx.example.conf, Dashboard/.htaccess örneği):
//   - temiz adresler: /devices -> devices.php
//   - /api/, /download/, /updates/ arka uca (POPS_TEST_BACKEND) aktarılır
// `php -S` WebSocket aktaramaz; /ws/ istekleri 404 alır (testler bu el sıkışma hatasını ayrıca süzer).
$uri = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);
if (strncmp($uri, '/ws/', 4) === 0) {
    http_response_code(404);   // aksi halde php -S olmayan yol için index.php'yi çalıştırır
    return true;
}
if (!preg_match('#^/(api|download|updates)/#', $uri)) {
    if (preg_match('#^/([A-Za-z0-9_-]+)$#', $uri, $m) && is_file($_SERVER['DOCUMENT_ROOT'] . '/' . $m[1] . '.php')) {
        chdir($_SERVER['DOCUMENT_ROOT']);
        $_SERVER['SCRIPT_NAME'] = $_SERVER['PHP_SELF'] = '/' . $m[1] . '.php';
        require $_SERVER['DOCUMENT_ROOT'] . '/' . $m[1] . '.php';
        return true;
    }
    return false;   // dosya olduğu gibi sunulur (varlıklar, index.php)
}

$backend = getenv('POPS_TEST_BACKEND');
if (!$backend) {
    http_response_code(500);
    header('Content-Type: application/json');
    echo json_encode(['detail' => 'router: POPS_TEST_BACKEND tanımlı değil']);
    return true;
}
$ch = curl_init(rtrim($backend, '/') . $_SERVER['REQUEST_URI']);
$method = $_SERVER['REQUEST_METHOD'];
curl_setopt($ch, CURLOPT_CUSTOMREQUEST, $method);
curl_setopt($ch, CURLOPT_RETURNTRANSFER, true);
curl_setopt($ch, CURLOPT_HEADER, true);
curl_setopt($ch, CURLOPT_TIMEOUT, 60);
$headers = [];
foreach (getallheaders() as $k => $v) {
    $lk = strtolower($k);
    if (in_array($lk, ['host', 'content-length', 'connection', 'accept-encoding'], true)) continue;
    if ($lk === 'content-type' && stripos($v, 'multipart/form-data') === 0) continue;
    $headers[] = "$k: $v";
}
$headers[] = 'X-Forwarded-For: ' . ($_SERVER['REMOTE_ADDR'] ?? '127.0.0.1');
if (!in_array($method, ['GET', 'HEAD'], true)) {
    $ct = $_SERVER['CONTENT_TYPE'] ?? '';
    if (stripos($ct, 'multipart/form-data') === 0) {
        // php -S çok parçalı gövdeyi kendisi ayrıştırır; curl için yeniden kurulur
        $fields = $_POST;
        foreach ($_FILES as $name => $f) {
            if (is_array($f['name'])) {
                foreach ($f['name'] as $j => $n) {
                    $fields[$name . '[' . $j . ']'] = new CURLFile($f['tmp_name'][$j], $f['type'][$j], $n);
                }
            } else {
                $fields[$name] = new CURLFile($f['tmp_name'], $f['type'], $f['name']);
            }
        }
        curl_setopt($ch, CURLOPT_POSTFIELDS, $fields);
    } else {
        curl_setopt($ch, CURLOPT_POSTFIELDS, file_get_contents('php://input'));
    }
}
curl_setopt($ch, CURLOPT_HTTPHEADER, $headers);
$resp = curl_exec($ch);
if ($resp === false) {
    http_response_code(502);
    header('Content-Type: application/json');
    echo json_encode(['detail' => 'router: ' . curl_error($ch)]);
    return true;
}
$code = curl_getinfo($ch, CURLINFO_HTTP_CODE);
$hsize = curl_getinfo($ch, CURLINFO_HEADER_SIZE);
http_response_code($code);
foreach (explode("\r\n", substr($resp, 0, $hsize)) as $line) {
    if (preg_match('#^(content-type|content-disposition|cache-control|set-cookie|x-request-id):#i', $line)) {
        header($line, false);
    }
}
echo substr($resp, $hsize);
return true;
