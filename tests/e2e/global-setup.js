// Test yığınını kurar ve testlerden sonra kapatır (CI'da ve yerelde aynı):
//   1. db.py prepare : DB_NAME boş (ya da önceki bir e2e çalıştırmasının) olmalı; başka veritabanında durur
//   2. migrate.py, ardından arka uç: uvicorn 127.0.0.1:E2E_API_PORT (8099)
//   3. db.py seed    : seed.sql (uydurma bilgisayarlar, sınıflar, görevler, kayıtlar)
//   4. panel         : Dashboard/'un kopyası (config.php = config.example.php) + php -S 127.0.0.1:E2E_PANEL_PORT
//                      (8098) router.php ile
// Gerekenler: DB_HOST, DB_PORT, DB_USER, DB_PASS, DB_NAME; python (E2E_PYTHON, varsayılan python3) arka ucun
// bağımlılıklarıyla; php (E2E_PHP) curl eklentisiyle. Yönetici şifresi PANEL_ADMIN_PASS (yoksa burada üretilir).
import { spawn, spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { ADMIN_USER, API_PORT, API_URL, HERE, PANEL_PORT, PANEL_URL, ROOT, RUN_DIR } from './stack.js';

const BACKEND = path.join(ROOT, 'Backend');
const PY = process.env.E2E_PYTHON || 'python3';
const PHP = process.env.E2E_PHP || 'php';
const children = [];

function need(name) {
    const v = process.env[name];
    if (!v) throw new Error(`[e2e] ${name} tanımlı değil. Boş, atılabilir bir PostgreSQL veritabanı verin: DB_HOST, DB_PORT, DB_USER, DB_PASS, DB_NAME (bkz. docs/testing.md)`);
    return v;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function portFree(port) {
    return new Promise((resolve) => {
        const s = net.createServer();
        s.once('error', () => resolve(false));
        s.listen(port, '127.0.0.1', () => s.close(() => resolve(true)));
    });
}

function tail(file, lines = 25) {
    try { return fs.readFileSync(file, 'utf8').trimEnd().split('\n').slice(-lines).join('\n'); } catch { return ''; }
}

// Kısa süren adım; çıktısı RUN_DIR/<log>'a yazılır, hata verirse son satırlarıyla durur
function run(cmd, args, opts, log) {
    const file = path.join(RUN_DIR, log);
    const r = spawnSync(cmd, args, { ...opts, encoding: 'utf8', timeout: 180_000 });
    fs.writeFileSync(file, (r.stdout || '') + (r.stderr || ''));
    if (r.error) throw new Error(`[e2e] ${cmd} çalıştırılamadı: ${r.error.message}`);
    if (r.status !== 0) throw new Error(`[e2e] ${path.basename(cmd)} ${args.join(' ')} başarısız (çıkış ${r.status}), ${file}:\n${tail(file)}`);
}

// Uzun süren süreç; kendi süreç grubunda başlar (php -S işçileri dahil hepsi birlikte kapanır)
function start(name, cmd, args, opts) {
    const fd = fs.openSync(path.join(RUN_DIR, `${name}.log`), 'a');
    const child = spawn(cmd, args, { ...opts, stdio: ['ignore', fd, fd], detached: true });
    fs.closeSync(fd);
    child.on('error', (e) => { child.startError = e; });
    children.push(child);
    return child;
}

async function waitReady(name, url, child, ms = 60_000) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
        if (child.startError) throw new Error(`[e2e] ${name} başlatılamadı: ${child.startError.message}`);
        if (child.exitCode !== null) throw new Error(`[e2e] ${name} kapandı (çıkış ${child.exitCode}):\n${tail(path.join(RUN_DIR, `${name}.log`))}`);
        try {
            const r = await fetch(url, { redirect: 'manual' });
            if (r.status === 200) return;
        } catch { /* henüz dinlemiyor */ }
        await sleep(250);
    }
    throw new Error(`[e2e] ${name} ${ms / 1000} sn içinde hazır olmadı (${url}):\n${tail(path.join(RUN_DIR, `${name}.log`))}`);
}

function signalAll(sig) {
    for (const c of children) {
        if (c.pid && c.exitCode === null && c.signalCode === null) {
            try { process.kill(-c.pid, sig); } catch { /* zaten kapandı */ }
        }
    }
}

async function stopAll() {
    signalAll('SIGTERM');
    const end = Date.now() + 15_000;   // uvicorn düzgün kapanışta en çok ~10 sn bekler
    while (Date.now() < end && children.some((c) => c.exitCode === null && c.signalCode === null)) await sleep(100);
    signalAll('SIGKILL');
}

export default async function globalSetup() {
    const db = {
        DB_HOST: process.env.DB_HOST || '127.0.0.1',
        DB_PORT: process.env.DB_PORT || '5432',
        DB_USER: need('DB_USER'),
        DB_PASS: need('DB_PASS'),
        DB_NAME: need('DB_NAME'),
    };
    for (const port of [PANEL_PORT, API_PORT]) {
        if (!(await portFree(port))) throw new Error(`[e2e] 127.0.0.1:${port} kullanımda. E2E_PANEL_PORT / E2E_API_PORT ile başka port seçin.`);
    }
    // Testçi işlemleri bu süreçten sonra başlar ve ortamı devralır (bkz. stack.js adminPass)
    process.env.PANEL_ADMIN_PASS ||= crypto.randomBytes(18).toString('base64url');

    fs.rmSync(RUN_DIR, { recursive: true, force: true });
    for (const d of ['selfupdate', 'state', 'etc']) fs.mkdirSync(path.join(RUN_DIR, d), { recursive: true });
    fs.writeFileSync(path.join(RUN_DIR, 'etc', 'selfupdate.conf'), 'CHANNEL=release\n');

    // Test süreçleri hiçbir koşulda ayakta kalmasın (Ctrl+C, beklenmeyen çıkış)
    process.once('exit', () => signalAll('SIGKILL'));

    const backendEnv = {
        ...process.env,
        ...db,
        JWT_SECRET: crypto.randomBytes(32).toString('hex'),
        BYPASS_SECRET: crypto.randomBytes(32).toString('hex'),
        PANEL_ADMIN_USER: ADMIN_USER,
        PANEL_ADMIN_PASS: process.env.PANEL_ADMIN_PASS,
        CORS_ALLOWED_ORIGINS: '',
        LOG_FORMAT: 'text',
        PYTHONUNBUFFERED: '1',
        // Sunucunun canlı yollarına (self-update, durum, TLS, disk) dokunulmaz; WoL paketi loopback'e gider
        POPS_SELFUPDATE_DIR: path.join(RUN_DIR, 'selfupdate'),
        POPS_STATE_DIR: path.join(RUN_DIR, 'state'),
        POPS_SELFUPDATE_CONF: path.join(RUN_DIR, 'etc', 'selfupdate.conf'),
        POPS_BACKUP_STATUS: path.join(RUN_DIR, 'state', 'backup-status.json'),
        TLS_CERT_FILES: '',
        TLS_CHECK_URL: '',
        DISK_CHECK_PATHS: '',
        WOL_BROADCAST_ADDR: '127.0.0.1',
        // Yığın internetsiz çalışır: arka ucun dışarıya isteği (GitHub sürüm denetimi) kapalı bir porta gider ve
        // hemen başarısız olur; panelin "denetlenemedi" yolu sınanır, testler ağa ve GitHub kotasına bağlı kalmaz
        HTTPS_PROXY: 'http://127.0.0.1:9',
        HTTP_PROXY: 'http://127.0.0.1:9',
        https_proxy: 'http://127.0.0.1:9',
        http_proxy: 'http://127.0.0.1:9',
        NO_PROXY: '127.0.0.1,localhost',
        no_proxy: '127.0.0.1,localhost',
    };

    run(PY, [path.join(HERE, 'db.py'), 'prepare'], { env: backendEnv }, 'db-prepare.log');
    run(PY, ['migrate.py'], { cwd: BACKEND, env: backendEnv }, 'migrate.log');
    const api = start('backend', PY, ['-m', 'uvicorn', 'server:app', '--host', '127.0.0.1', '--port', String(API_PORT)], { cwd: BACKEND, env: backendEnv });
    try {
        await waitReady('backend', `${API_URL}/api/health`, api);
        run(PY, [path.join(HERE, 'db.py'), 'seed', path.join(HERE, 'seed.sql')], { env: backendEnv }, 'seed.log');

        // Panel kopyadan sunulur: geliştiricinin kendi includes/config.php'si ne kullanılır ne değiştirilir
        const docroot = path.join(RUN_DIR, 'Dashboard');
        fs.cpSync(path.join(ROOT, 'Dashboard'), docroot, { recursive: true });
        fs.copyFileSync(path.join(docroot, 'includes', 'config.example.php'), path.join(docroot, 'includes', 'config.php'));
        const panel = start('php', PHP, [
            // PHP uyarıları sayfaya değil php.log'a ("PHP Warning: ...") yazılır; testler yeni uyarı olmadığını
            // denetler (fixtures.js)
            '-d', 'error_reporting=E_ALL', '-d', 'display_errors=0', '-d', 'log_errors=1', '-d', 'error_log=',
            '-S', `127.0.0.1:${PANEL_PORT}`, '-t', docroot, path.join(HERE, 'router.php'),
        ], {
            env: {
                ...process.env,
                POPS_API_URL: `${PANEL_URL}/api`,          // tarayıcının gördüğü adres (panelin kendi kökeni)
                POPS_API_INTERNAL_URL: API_URL,             // PHP -> arka uç (giriş, kurum adı)
                POPS_TEST_BACKEND: API_URL,                 // router.php'nin aktardığı adres
                PHP_CLI_SERVER_WORKERS: '4',                // php -S tek istekte kilitlenmesin (sayfalar paralel API çağırır)
            },
        });
        await waitReady('php', `${PANEL_URL}/login`, panel);
    } catch (e) {
        await stopAll();
        throw e;
    }
    console.log(`[e2e] yığın hazır: panel ${PANEL_URL}, arka uç ${API_URL}, veritabanı ${db.DB_NAME} (günlükler: ${path.relative(process.cwd(), RUN_DIR) || '.'})`);
    return stopAll;
}
