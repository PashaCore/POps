// Yönetici arayüzden giriş yapar; oturum çerezleri (PHP oturumu + httpOnly JWT) diğer testlere verilir.
// Giriş ucu dakikada 10 denemeyle sınırlı: testler her seferinde yeniden giriş yapmaz.
import { test, expect } from '../fixtures.js';
import { ADMIN_STATE, ADMIN_USER, adminPass } from '../stack.js';

test('yönetici girişi', async ({ page }) => {
    await page.goto('/login');
    await page.locator('input[name="username"]').fill(ADMIN_USER);
    await page.locator('input[name="password"]').fill(adminPass());
    await page.getByRole('button', { name: 'Giriş yap' }).click();
    await expect(page).toHaveURL(/\/$/);
    await expect(page.locator('.page-header h1')).toHaveText('Kontrol merkezi');
    await page.context().storageState({ path: ADMIN_STATE });
});
