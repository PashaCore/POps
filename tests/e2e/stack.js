// Test yığınının ortak sabitleri: global-setup.js, playwright.config.js ve testler buradan okur.
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const HERE = path.dirname(fileURLToPath(import.meta.url));
export const ROOT = path.resolve(HERE, '..', '..');

// Portlar ortamdan değiştirilebilir (yerelde doluysa): E2E_PANEL_PORT, E2E_API_PORT
export const PANEL_PORT = Number(process.env.E2E_PANEL_PORT || 8098);
export const API_PORT = Number(process.env.E2E_API_PORT || 8099);
export const PANEL_URL = `http://127.0.0.1:${PANEL_PORT}`;
export const API_URL = `http://127.0.0.1:${API_PORT}`;

// Çalışma klasörü (git'e girmez): günlükler, panelin kopyası, arka ucun durum klasörleri, oturum dosyaları
export const RUN_DIR = path.join(HERE, '.stack');
export const PHP_LOG = path.join(RUN_DIR, 'php.log');
export const ADMIN_STATE = path.join(RUN_DIR, 'admin-state.json');

// Arka uç ilk açılışta bu yöneticiyi oluşturur; şifreyi global-setup üretir (PANEL_ADMIN_PASS)
export const ADMIN_USER = 'admin';
export const adminPass = () => {
    const p = process.env.PANEL_ADMIN_PASS;
    if (!p) throw new Error('PANEL_ADMIN_PASS yok: testler global-setup.js üzerinden çalışmalı (npx playwright test)');
    return p;
};
