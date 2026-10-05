// Panel uçtan uca testleri: yalnızca Chromium. Yığını global-setup.js kurar (bkz. docs/testing.md).
import { defineConfig, devices } from '@playwright/test';
import { ADMIN_STATE, PANEL_URL } from './stack.js';

const CI = !!process.env.CI;

export default defineConfig({
    testDir: './specs',
    globalSetup: './global-setup.js',
    timeout: 30_000,
    expect: { timeout: 10_000 },
    globalTimeout: 10 * 60_000,
    forbidOnly: CI,
    retries: CI ? 1 : 0,
    workers: CI ? 2 : 3,
    reporter: CI ? [['list'], ['github'], ['html', { open: 'never' }]] : [['list'], ['html', { open: 'never' }]],
    use: {
        baseURL: PANEL_URL,
        locale: 'tr-TR',
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
    },
    projects: [
        // Yönetici bir kez arayüzden giriş yapar; oturum (çerezler) diğer projelere verilir
        { name: 'setup', testMatch: /auth\.setup\.js$/, use: { ...devices['Desktop Chrome'] } },
        {
            name: 'desktop',
            testMatch: /\.spec\.js$/,
            dependencies: ['setup'],
            use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, storageState: ADMIN_STATE },
        },
        {
            // Dar ekran: yalnızca sayfa taraması (her sayfa açılır, yatay taşma yok)
            name: 'mobile',
            testMatch: /smoke\.spec\.js$/,
            dependencies: ['setup'],
            use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 }, storageState: ADMIN_STATE },
        },
    ],
});
