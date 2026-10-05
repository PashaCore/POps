// Giriş ve çıkış (oturumsuz bağlam). Giriş ucu dakikada 10 denemeyle sınırlı; buradaki iki deneme yeter.
import { test, expect } from '../fixtures.js';
import { ADMIN_USER, adminPass } from '../stack.js';

test.use({ storageState: { cookies: [], origins: [] } });

async function login(page, user, pass) {
    await page.goto('/login');
    await page.locator('input[name="username"]').fill(user);
    await page.locator('input[name="password"]').fill(pass);
    await page.getByRole('button', { name: 'Giriş yap' }).click();
}

test('oturumsuz sayfa girişe yönlenir; giriş ve çıkış', async ({ page }) => {
    await page.goto('/devices');
    await expect(page).toHaveURL(/\/login$/);

    await login(page, ADMIN_USER, adminPass());
    await expect(page).toHaveURL(/\/$/);
    await expect(page.locator('.page-header h1')).toHaveText('Kontrol merkezi');
    await expect(page.locator('.user-card .user-name')).toHaveText(ADMIN_USER);
    await expect(page.locator('.user-card .user-role')).toHaveText('Süper Admin');

    await page.getByRole('link', { name: 'Çıkış yap' }).click();
    await expect(page).toHaveURL(/\/login$/);
    // Oturum gerçekten kapandı: korumalı sayfa yine girişe döner
    await page.goto('/devices');
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('button', { name: 'Giriş yap' })).toBeVisible();
});

test('yanlış şifre hata gösterir', async ({ page }) => {
    await login(page, ADMIN_USER, 'yanlis-sifre-e2e');
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.locator('.error-box')).toHaveText('Geçersiz kullanıcı adı veya şifre');
    await expect(page.locator('input[name="password"]')).toBeVisible();
});
