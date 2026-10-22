// Sayfa taraması (1440×900 ve 390×844): her sayfa oturumla açılır, başlığı doğrudur, yükleniyor göstergeleri
// kaybolur ve dar ekranda yatay taşma olmaz. Konsol/sayfa hatası, dış istek ve PHP uyarısı fixtures.js'te denetlenir.
import { test, expect, settled } from '../fixtures.js';

const PAGES = [
    ['/', 'Kontrol merkezi'],
    ['/devices', 'Cihazlar'],
    ['/labs', 'Sınıflar'],
    ['/tasks', 'İşlemler'],
    ['/terminal', 'Uzak komut'],
    ['/vision', 'Uzak ekran'],
    ['/deploy', 'Dağıtım'],
    ['/policies', 'Politikalar'],
    ['/logger', 'Kayıtlar'],
    ['/reports', 'Raporlar'],
    ['/helpdesk', 'Destek talepleri'],
    ['/settings', 'Ayarlar'],
    ['/system', 'Sistem'],
];

async function expectNoHorizontalOverflow(page) {
    const { sw, iw } = await page.evaluate(() => ({ sw: document.documentElement.scrollWidth, iw: window.innerWidth }));
    expect(sw, `yatay taşma: scrollWidth ${sw} > innerWidth ${iw}`).toBeLessThanOrEqual(iw + 1);
}

test.describe('giriş sayfası', () => {
    test.use({ storageState: { cookies: [], origins: [] } });

    test('oturumsuz açılır', async ({ page }) => {
        const res = await page.goto('/login');
        expect(res.status()).toBe(200);
        await expect(page).toHaveTitle('POps | Yönetici Girişi');
        await expect(page.locator('.login-header h2')).not.toBeEmpty();
        await expect(page.locator('input[name="username"]')).toBeVisible();
        await expect(page.locator('input[name="password"]')).toBeVisible();
        await expect(page.getByRole('button', { name: 'Giriş yap' })).toBeVisible();
        await expectNoHorizontalOverflow(page);
    });
});

for (const [path, title] of PAGES) {
    test(`${title} (${path}) açılır`, async ({ page }) => {
        const res = await page.goto(path);
        expect(res.status()).toBe(200);
        await expect(page, 'oturum açıkken giriş sayfasına dönmemeli').toHaveURL(new URL(path, page.url()).href);

        // Üst çubuk (dar ekran) ve yan menüdeki etkin sayfa sunucuda yazılır; yan menü dar ekranda gizli de olsa DOM'da
        await expect(page.locator('#pageTitle')).toHaveText(title);
        await expect(page.locator('.sidebar-nav a.nav-item[aria-current="page"] > span').first()).toHaveText(title);
        await settled(page);
        const h1 = page.locator('.page-header h1');
        await expect(h1).toBeVisible();
        if (path === '/labs') {
            // Sınıflar sayfası başlığa seçili sınıfın adını yazar
            await expect(h1).not.toHaveText('');
            await expect(page).toHaveTitle(`${(await h1.textContent()).trim()} · POps`);
        } else {
            await expect(h1).toHaveText(title);
            await expect(page).toHaveTitle(`${title} · POps`);
        }
        await expectNoHorizontalOverflow(page);
    });
}
