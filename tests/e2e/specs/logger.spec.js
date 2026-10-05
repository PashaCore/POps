// Kayıtlar: sayfa başına kayıt sayısı değişir, sayfalar arasında gezilir; süzgeç paneli listeyi süzer.
// Örnek veride 120'den fazla olay var (seed.sql). Testler sürerken yeni olay eklenebilir: toplam her seferinde
// sayfadan okunur.
import { test, expect, settled } from '../fixtures.js';

const RANGE = /^(\d+)–(\d+) \/ ([\d.]+) kayıt$/;

async function range(page) {
    const m = (await page.locator('#lgRange').textContent()).trim().match(RANGE);
    expect(m, 'kayıt aralığı metni').not.toBeNull();
    return { from: Number(m[1]), to: Number(m[2]), total: Number(m[3].replace(/\./g, '')) };
}

test.beforeEach(async ({ page }) => {
    await page.goto('/logger');
    await settled(page);
    await expect(page.locator('#lgPager')).toBeVisible();
});

test('sayfa başına kayıt ve sayfalar arasında gezinme', async ({ page }) => {
    const rows = page.locator('#lgList .act[data-id]');
    const pages = page.getByRole('navigation', { name: 'Sayfalar' });
    const current = pages.locator('button[aria-current="page"]');

    await expect(page.locator('#lgRange')).toHaveText(/^1–25 \/ /);
    expect((await range(page)).total).toBeGreaterThan(100);
    await expect(rows).toHaveCount(25);
    await expect(current).toHaveText('1');
    await expect(pages.getByRole('button', { name: 'Önceki sayfa' })).toBeDisabled();

    await page.getByRole('combobox', { name: 'Sayfa başına kayıt' }).selectOption('50');
    await expect(page.locator('#lgRange')).toHaveText(/^1–50 \/ /);
    await expect(rows).toHaveCount(50);

    await pages.getByRole('button', { name: 'Sonraki sayfa' }).click();
    await expect(page.locator('#lgRange')).toHaveText(/^51–100 \/ /);
    await expect(current).toHaveText('2');
    await expect(rows).toHaveCount(50);

    // Son sayfa: kalan kayıtlar, "Sonraki" kapalı
    const { total } = await range(page);
    const last = Math.ceil(total / 50);
    await pages.getByRole('button', { name: `Sayfa ${last}`, exact: true }).click();
    await expect(current).toHaveText(String(last));
    await expect(page.locator('#lgRange')).toHaveText(new RegExp(`^${(last - 1) * 50 + 1}–`));
    await expect(pages.getByRole('button', { name: 'Sonraki sayfa' })).toBeDisabled();

    await pages.getByRole('button', { name: 'Sayfa 1', exact: true }).click();
    await expect(page.locator('#lgRange')).toHaveText(/^1–50 \/ /);

    // Seçim bu tarayıcıda hatırlanır
    await page.reload();
    await settled(page);
    await expect(page.locator('#lgRange')).toHaveText(/^1–50 \/ /);
    await expect(page.getByRole('combobox', { name: 'Sayfa başına kayıt' })).toHaveValue('50');
});

test('süzgeç paneli', async ({ page }) => {
    const rows = page.locator('#lgList .act[data-id]');
    const panel = page.getByRole('dialog', { name: 'Süzgeçler' });
    const btn = page.locator('#lgFiltersBtn');   // adı rozetteki sayıyla değişir
    const { total } = await range(page);

    await expect(panel).toBeHidden();
    await btn.click();
    await expect(panel).toBeVisible();
    await expect(btn).toHaveAttribute('aria-expanded', 'true');
    await panel.locator('#lgLab').selectOption('Lab-2 Donanım');
    await panel.getByRole('button', { name: 'Tamam' }).click();
    await expect(panel).toBeHidden();

    // Süzgeç rozeti ve çipi; listede yalnızca o sınıfın bilgisayarları
    await expect(page.locator('#lgFCount')).toHaveText('1');
    const chip = page.locator('#lgChips .chip').filter({ hasText: 'Sınıf:' });
    await expect(chip).toContainText('Lab-2 Donanım');
    const filtered = await range(page);
    expect(filtered.total).toBeGreaterThan(0);
    expect(filtered.total).toBeLessThan(total);
    const metas = await rows.locator('.meta').allTextContents();
    expect(metas.length).toBeGreaterThan(0);
    for (const m of metas) expect(m).toContain('Lab-2 Donanım');

    // İkinci süzgeç (kişi) eklenir; "Süzgeçleri temizle" ikisini de kaldırır
    await btn.click();
    const who = panel.locator('#lgWho');
    await who.selectOption(await who.locator('option').nth(1).getAttribute('value'));
    await expect(page.locator('#lgFCount')).toHaveText('2');
    await expect(page.locator('#lgChips .chip')).toHaveCount(2);
    await panel.getByRole('button', { name: 'Süzgeçleri temizle' }).click();
    await expect(page.locator('#lgFCount')).toBeHidden();
    await expect(page.locator('#lgChips .chip')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await expect(panel).toBeHidden();
    expect((await range(page)).total).toBeGreaterThanOrEqual(total);

    // Çipteki × ile tek süzgeç kaldırılır
    await btn.click();
    await panel.locator('#lgLab').selectOption('Kütüphane');
    await panel.getByRole('button', { name: 'Tamam' }).click();
    await expect(page.locator('#lgFCount')).toHaveText('1');
    await page.getByRole('button', { name: 'Sınıf süzgecini kaldır' }).click();
    await expect(page.locator('#lgFCount')).toBeHidden();
    await expect(page.locator('#lgRange')).toHaveText(/^1–25 \/ /);
});
