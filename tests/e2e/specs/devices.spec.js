// Bilgisayarlar (liste): arama listeyi süzer, satıra tıklayınca bilgisayarın sayfası açılır. Örnek veride 12 bilgisayar var (seed.sql).
import { test, expect, settled } from '../fixtures.js';

test.beforeEach(async ({ page }) => {
    await page.goto('/devices');
    await settled(page);
});

test('arama listeyi süzer', async ({ page }) => {
    const rows = page.locator('#devBody tr[data-host]');
    const search = page.getByRole('searchbox', { name: 'Cihaz ara' });
    await expect(rows).toHaveCount(12);

    await search.fill('LAB2');
    await expect(rows).toHaveCount(3);
    await expect(rows.locator('.nm')).toHaveText([/^LAB2-PC01/, /^LAB2-PC02/, /^LAB2-PC03/]);

    await search.fill('10.20.3.12');   // IP ile
    await expect(rows).toHaveCount(1);
    await expect(rows.first().locator('.nm')).toHaveText('KUTUP-02');

    // HTML'e benzeyen ad metin olarak görünür, öğe olarak değil (pencere açılmadığını fixture denetler)
    await search.fill('onerror');
    await expect(rows).toHaveCount(1);
    await expect(rows.first().locator('.nm')).toContainText('<img src=x onerror=alert(1)>');
    await expect(page.locator('#devBody img')).toHaveCount(0);

    await search.fill('boyle-bir-cihaz-yok');
    await expect(rows).toHaveCount(0);
    await expect(page.locator('#devBody')).toContainText('Süzgece uyan cihaz yok');

    await search.fill('');
    await expect(rows).toHaveCount(12);
});

test('cihaza tıklayınca bilgisayar sayfası açılır', async ({ page }) => {
    await page.locator('#devBody tr[data-host]').filter({ hasText: 'LAB1-PC01' }).locator('.nm').click();
    await expect(page).toHaveURL(/\/pc\?id=HW-E2E0000102$/);
    await expect(page.locator('#pcName')).toHaveText('LAB1-PC01');

    const fact = (label) => page.locator('#paneGeneral .kv').filter({ has: page.locator('span', { hasText: new RegExp(`^${label}$`) }) });
    await expect(fact('IP adresi')).toContainText('10.20.1.11');
    await expect(fact('Sınıf')).toContainText('Lab-1 Yazılım');
    await expect(fact('Donanım kimliği')).toContainText('HW-E2E0000102');
    // Geçmiş sunucudan ayrıca yüklenir (örnek veride bu bilgisayara tamamlanmış bir görev var)
    await page.locator('#pcTabs [data-tab="history"]').click();
    await expect(page.locator('#paneHistory .act').first()).toContainText('Tamamlandı');
});
