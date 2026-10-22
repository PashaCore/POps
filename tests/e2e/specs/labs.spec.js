// Sınıflar: "Sınıf işlemleri" menüsünden sınıf oluştur, yeniden adlandır, sil. Her adım sayfa yenilenince de
// görünür olmalı (sunucuya yazıldı).
import { test, expect, settled, uniq } from '../fixtures.js';

async function labMenu(page, item) {
    await page.getByRole('button', { name: 'Sınıf işlemleri' }).click();
    await page.getByRole('menuitem', { name: item }).click();
}

test('sınıf oluştur, yeniden adlandır, sil', async ({ page }) => {
    const name = uniq('E2E Sınıf');
    const renamed = `${name} Yeni`;
    const title = page.locator('#labTitle');
    const navLink = (lab) => page.locator('#navSub-labs a[data-lab]').filter({ has: page.getByText(lab, { exact: true }) });
    const toast = page.locator('#toastContainer .toast.success .toast-msg');

    await page.goto('/labs');
    await settled(page);

    // Oluştur
    await labMenu(page, 'Yeni sınıf…');
    let dlg = page.getByRole('dialog', { name: 'Yeni sınıf' });
    await dlg.getByLabel('Sınıf adı').fill(name);
    await dlg.getByRole('button', { name: 'Oluştur' }).click();
    await expect(dlg).toHaveCount(0);
    await expect(toast.filter({ hasText: `${name} oluşturuldu.` })).toBeVisible();
    await expect(title).toHaveText(name);
    await expect(page.locator('#labMap')).toContainText('Bu sınıfta bilgisayar yok');
    await expect(navLink(name)).toHaveClass(/\bactive\b/);

    // Yeniden adlandır
    await labMenu(page, 'Yeniden adlandır…');
    dlg = page.getByRole('dialog', { name: 'Sınıfı yeniden adlandır' });
    await expect(dlg.getByLabel('Yeni ad')).toHaveValue(name);
    await dlg.getByLabel('Yeni ad').fill(renamed);
    await dlg.getByRole('button', { name: 'Kaydet' }).click();
    await expect(toast.filter({ hasText: 'Sınıf adı güncellendi.' })).toBeVisible();
    await expect(title).toHaveText(renamed);
    await expect(navLink(renamed)).toHaveCount(1);
    await expect(navLink(name)).toHaveCount(0);

    await page.goto(`/labs?lab=${encodeURIComponent(renamed)}`);
    await settled(page);
    await expect(title).toHaveText(renamed);

    // Sil (onay penceresiyle)
    await labMenu(page, 'Sınıfı sil');
    const confirm = page.getByRole('alertdialog', { name: `${renamed} silinsin mi?` });
    await expect(confirm).toContainText('Sınıf boş.');
    await confirm.getByRole('button', { name: 'Sınıfı sil' }).click();
    await expect(toast.filter({ hasText: `${renamed} silindi.` })).toBeVisible();
    await expect(title).not.toHaveText(renamed);
    await expect(navLink(renamed)).toHaveCount(0);

    await page.reload();
    await settled(page);
    await expect(page.locator('#navSub-labs a[data-lab]').first()).toBeVisible();
    await expect(navLink(renamed)).toHaveCount(0);
    await expect(navLink(name)).toHaveCount(0);
});
