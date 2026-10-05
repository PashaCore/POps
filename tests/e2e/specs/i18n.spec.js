// Arayüz dili: giriş sayfası çerez yokken tarayıcının diline (Accept-Language) uyar ve seçimi çereze yazar;
// Türkçe / English bağlantıları dili değiştirir. pops_lang=en çereziyle bütün sayfalar İngilizce açılır
// (başlık ve sekmeler); konsol hatası, dış istek ve PHP uyarısı fixtures.js'te denetlenir.
import { test, expect, settled } from '../fixtures.js';
import { PANEL_URL } from '../stack.js';

const langCookie = async (context) => ((await context.cookies()).find((c) => c.name === 'pops_lang') || {}).value;

test.describe('giriş sayfası', () => {
    test.use({ storageState: { cookies: [], origins: [] }, locale: 'en-GB' });

    test('tarayıcı İngilizce isterse İngilizce açılır; dil bağlantıları çereze yazar', async ({ page, context }) => {
        await page.goto('/login');
        await expect(page.locator('html')).toHaveAttribute('lang', 'en');
        await expect(page).toHaveTitle('POps | Admin sign-in');
        await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
        expect(await langCookie(context)).toBe('en');

        await page.getByRole('link', { name: 'Türkçe' }).click();
        await expect(page).toHaveURL(/\/login$/);
        await expect(page.getByRole('button', { name: 'Giriş yap' })).toBeVisible();
        await expect(page.locator('html')).toHaveAttribute('lang', 'tr');
        expect(await langCookie(context)).toBe('tr');

        // Çerez tarayıcının dilinden önce gelir
        await page.reload();
        await expect(page.getByRole('button', { name: 'Giriş yap' })).toBeVisible();
        await page.getByRole('link', { name: 'English' }).click();
        await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();
        expect(await langCookie(context)).toBe('en');
    });
});

const PAGES = [
    ['/', 'Overview'],
    ['/devices', 'Devices'],
    ['/tasks', 'Jobs'],
    ['/terminal', 'Remote command'],
    ['/vision', 'Remote screen'],
    ['/deploy', 'Deploy'],
    ['/policies', 'Policies'],
    ['/logger', 'Logs'],
    ['/reports', 'Reports'],
    ['/helpdesk', 'Help desk'],
    ['/settings', 'Settings'],
    ['/system', 'System'],
];

test.describe('İngilizce arayüz', () => {
    test.beforeEach(async ({ context }) => {
        const url = new URL(PANEL_URL);
        await context.addCookies([{ name: 'pops_lang', value: 'en', domain: url.hostname, path: '/' }]);
    });

    for (const [path, title] of PAGES) {
        test(`${title} (${path}) İngilizce açılır`, async ({ page }) => {
            await page.goto(path);
            await expect(page.locator('html')).toHaveAttribute('lang', 'en');
            await expect(page.locator('#pageTitle')).toHaveText(title);
            await settled(page);
            await expect(page.locator('.page-header h1')).toHaveText(title);
        });
    }

    test('Sistem ve Ayarlar sekmeleri İngilizce', async ({ page }) => {
        await page.goto('/system');
        await settled(page);
        await expect(page.locator('#sysTabs').getByRole('tab')).toHaveText(['Overview', 'Updates', 'Security', 'Health and backup', 'Notifications and retention', 'Modules', 'Integrations']);
        await page.goto('/settings');
        await settled(page);
        await expect(page.locator('#setTabs').getByRole('tab')).toHaveText(['Users', 'Security', 'General']);
    });
});
