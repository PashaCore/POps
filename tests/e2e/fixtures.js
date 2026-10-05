// Her testte geçerli denetimler. Test bittiğinde bunlardan biri olduysa test başarısız olur:
//   - sayfada yakalanmamış JavaScript hatası (pageerror) ya da konsol hatası (başarısız istekler dahil);
//     tek istisna panelin kendi /ws/ adresine WebSocket el sıkışma hatası (php -S WebSocket aktaramaz)
//   - panelin kendi kökeni (127.0.0.1:E2E_PANEL_PORT) dışına herhangi bir istek: panel internetsiz çalışmalı
//   - beklenmeyen tarayıcı penceresi (alert/confirm): örnek verideki HTML'e benzeyen metinler çalışmamalı
//   - test sürerken php.log'a düşen PHP uyarısı, bildirimi ya da hatası
import fs from 'node:fs';
import { test as base, expect } from '@playwright/test';
import { PANEL_PORT, PHP_LOG } from './stack.js';

const PANEL_HOST = `127.0.0.1:${PANEL_PORT}`;
const WS_HANDSHAKE = new RegExp(`^WebSocket connection to 'ws://127\\.0\\.0\\.1:${PANEL_PORT}/ws/[^'\\s]*' failed`);
const PHP_PROBLEM = /PHP (Warning|Notice|Deprecated|Fatal error|Parse error|Recoverable fatal error)\b/;

// Sayfanın olaylarını issues dizisine yazar; test kendi açtığı sayfaları da bununla izletir (watch fixture)
export function watchPage(page, issues) {
    const where = () => { try { return new URL(page.url()).pathname; } catch { return page.url(); } };
    page.on('console', (m) => {
        if (m.type() === 'error' && !WS_HANDSHAKE.test(m.text())) issues.push(`konsol hatası (${where()}): ${m.text()}`);
    });
    page.on('pageerror', (e) => issues.push(`sayfa hatası (${where()}): ${e.message}`));
    page.on('request', (r) => {
        const u = new URL(r.url());
        if (/^(https?|wss?):$/.test(u.protocol) && u.host !== PANEL_HOST) issues.push(`dış istek (${where()}): ${r.url()}`);
    });
    page.on('websocket', (ws) => {
        if (new URL(ws.url()).host !== PANEL_HOST) issues.push(`dış WebSocket (${where()}): ${ws.url()}`);
    });
    page.on('dialog', async (d) => {
        issues.push(`beklenmeyen pencere (${where()}): ${d.type()} "${d.message()}"`);
        await d.dismiss().catch(() => {});
    });
}

function phpLogSize() {
    try { return fs.statSync(PHP_LOG).size; } catch { return 0; }
}

function phpProblemsSince(offset) {
    let text = '';
    try {
        const fd = fs.openSync(PHP_LOG, 'r');
        const size = fs.fstatSync(fd).size;
        const buf = Buffer.alloc(Math.max(0, size - offset));
        fs.readSync(fd, buf, 0, buf.length, offset);
        fs.closeSync(fd);
        text = buf.toString('utf8');
    } catch { return []; }
    return text.split('\n').filter((l) => PHP_PROBLEM.test(l)).map((l) => `PHP: ${l.trim()}`);
}

export const test = base.extend({
    issues: async ({}, use) => {
        const issues = [];
        const phpFrom = phpLogSize();
        await use(issues);
        issues.push(...phpProblemsSince(phpFrom));
        expect(issues, 'konsol/sayfa hatası, dış istek, beklenmeyen pencere ya da PHP uyarısı olmamalı').toEqual([]);
    },
    page: async ({ page, issues }, use) => {
        watchPage(page, issues);
        await use(page);
    },
    // Testin kendi açtığı bağlamlardaki sayfalar için: const p = await ctx.newPage(); watch(p);
    watch: async ({ issues }, use) => {
        await use((p) => watchPage(p, issues));
    },
});

export { expect };

// Sayfadaki yükleniyor göstergeleri kaybolana ve açılıştaki istekler bitene kadar bekler (gizli sekmelerdekiler
// sayılmaz). İstekler bitmeden test kapanırsa geç gelen hata yanıtı konsola düşmeden kaybolurdu.
export async function settled(page) {
    await expect(page.locator('#mainContent .loading-state').filter({ visible: true })).toHaveCount(0, { timeout: 15_000 });
    await page.waitForLoadState('networkidle');
}

// Arka uca panelin kendi oturumuyla (çerez + CSRF başlığı) istek: tarayıcının yaptığının aynısı
export const XHR = { 'X-Requested-With': 'XMLHttpRequest' };

export async function apiJson(request, method, url, data) {
    const r = await request.fetch(url, { method, data, headers: XHR });
    const body = await r.text();
    expect(r.ok(), `${method} ${url} -> ${r.status()} ${body.slice(0, 300)}`).toBeTruthy();
    return body ? JSON.parse(body) : null;
}


// Benzersiz ad: tekrar denemede (retry) ya da yerelde art arda çalıştırmada çakışmasın
export const uniq = (prefix) => `${prefix} ${Date.now().toString(36).slice(-5)}${Math.floor(Math.random() * 1e3)}`;
