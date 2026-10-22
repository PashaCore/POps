// Bilgisayarlar → sınıflar: "Yeni sınıf" ile oluştur, sınıfın "Diğer" menüsünden yeniden adlandır ve sil. Her adım
// sayfa yenilenince de görünür olmalı (sunucuya yazıldı). Sınıf seçilmemişken bütün sınıflar kutu kutu görünür.
import { test, expect, settled, uniq } from '../fixtures.js';

async function labMenu(page, item) {
    await page.locator('#labMenuBtn').click();
    await page.getByRole('menuitem', { name: item }).click();
}

test('sınıf oluştur, yeniden adlandır, sil', async ({ page }) => {
    const name = uniq('E2E Sınıf');
    const renamed = `${name} Yeni`;
    const title = page.locator('#labTitle');
    const tile = (lab) => page.locator(`#labMap .lab-tile[data-lab="${lab}"]`);
    const toast = page.locator('#toastContainer .toast.success .toast-msg');

    await page.goto('/labs');
    await settled(page);
    await expect(title).toHaveText('Bilgisayarlar');
    await expect(page.locator('#labMap .lab-tile').first()).toBeVisible();

    // Oluştur
    await page.getByRole('button', { name: 'Yeni sınıf' }).click();
    let dlg = page.getByRole('dialog', { name: 'Yeni sınıf' });
    await dlg.getByLabel('Sınıf adı').fill(name);
    await dlg.getByRole('button', { name: 'Oluştur' }).click();
    await expect(dlg).toHaveCount(0);
    await expect(toast.filter({ hasText: `${name} oluşturuldu.` })).toBeVisible();
    await expect(title).toHaveText(name);
    await expect(page.locator('#labMap')).toContainText('Bu sınıfta bilgisayar yok');

    // Genel görünümde kutusu var; kutuya tıklayınca sınıf açılır
    await page.goto('/labs');
    await settled(page);
    await expect(tile(name)).toHaveCount(1);
    await tile(name).click();
    await expect(title).toHaveText(name);
    await expect(page).toHaveURL(new RegExp(`/labs\\?lab=${encodeURIComponent(name)}$`));

    // Yeniden adlandır
    await labMenu(page, 'Yeniden adlandır…');
    dlg = page.getByRole('dialog', { name: 'Sınıfı yeniden adlandır' });
    await expect(dlg.getByLabel('Yeni ad')).toHaveValue(name);
    await dlg.getByLabel('Yeni ad').fill(renamed);
    await dlg.getByRole('button', { name: 'Kaydet' }).click();
    await expect(toast.filter({ hasText: 'Sınıf adı güncellendi.' })).toBeVisible();
    await expect(title).toHaveText(renamed);

    await page.goto(`/labs?lab=${encodeURIComponent(renamed)}`);
    await settled(page);
    await expect(title).toHaveText(renamed);

    // Sil (onay penceresiyle); ardından genel görünüme döner
    await labMenu(page, 'Sınıfı sil');
    const confirm = page.getByRole('alertdialog', { name: `${renamed} silinsin mi?` });
    await expect(confirm).toContainText('Sınıf boş.');
    await confirm.getByRole('button', { name: 'Sınıfı sil' }).click();
    await expect(toast.filter({ hasText: `${renamed} silindi.` })).toBeVisible();
    await expect(title).toHaveText('Bilgisayarlar');
    await expect(tile(renamed)).toHaveCount(0);

    await page.reload();
    await settled(page);
    await expect(page.locator('#labMap .lab-tile').first()).toBeVisible();
    await expect(tile(renamed)).toHaveCount(0);
    await expect(tile(name)).toHaveCount(0);
});
