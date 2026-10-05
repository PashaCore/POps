// Ayarlar → Genel: kurum adı kaydedilir ve giriş sayfasında görünür; eşzamanlı görev sınırı kaydedilir.
// Değiştirilen ayar test sonunda API ile eski haline döner (diğer testler etkilenmesin).
import { test, expect, settled, apiJson, uniq } from '../fixtures.js';
import { PANEL_URL } from '../stack.js';

test('kurum adı kaydedilir ve giriş sayfasında görünür', async ({ page, browser, watch }) => {
    const before = await apiJson(page.request, 'GET', '/api/branding');
    const name = uniq('E2E Okulu');
    try {
        await page.goto('/settings?tab=general');
        await settled(page);
        const input = page.locator('#orgName');
        const save = page.locator('#orgSave');
        await expect(input).toBeEnabled();
        await expect(save).toBeDisabled();
        await input.fill(name);
        await expect(save).toBeEnabled();
        await save.click();
        await expect(page.locator('#toastContainer .toast.success')).toContainText('Kurum adı kaydedildi.');
        await expect(save).toBeDisabled();

        await page.reload();
        await settled(page);
        await expect(page.locator('#orgName')).toHaveValue(name);

        // Giriş sayfası (oturumsuz, ayrı bağlam) kurumun adını başlık yapar
        const ctx = await browser.newContext({ baseURL: PANEL_URL, storageState: { cookies: [], origins: [] } });
        try {
            const anon = await ctx.newPage();
            watch(anon);
            await anon.goto('/login');
            await expect(anon.locator('.login-header h2')).toHaveText(name);
            await expect(anon.locator('.login-header p')).toHaveText('POps yönetim paneli');
        } finally {
            await ctx.close();
        }
    } finally {
        await apiJson(page.request, 'POST', '/api/system/branding', { org_name: before.org_name || null });
    }
});

test('eşzamanlı görev sınırı kaydedilir', async ({ page }) => {
    const { limit: before } = await apiJson(page.request, 'GET', '/api/get_concurrent_limit');
    const next = before >= 150 ? before - 7 : before + 7;
    try {
        await page.goto('/settings?tab=general');
        await settled(page);
        const input = page.getByRole('spinbutton', { name: 'Eşzamanlı görev sınırı' });
        const bar = page.getByRole('region', { name: 'Kaydedilmemiş değişiklikler' });
        await expect(input).toHaveValue(String(before));
        await expect(bar).toBeHidden();

        // Geçersiz değer kaydedilmez
        await input.fill('0');
        await expect(bar).toBeVisible();
        await bar.getByRole('button', { name: 'Kaydet' }).click();
        await expect(page.locator('#toastContainer .toast.warning')).toContainText('1 ile 200 arasında');
        await expect(input).toHaveClass(/\bis-invalid\b/);

        await input.fill(String(next));
        await bar.getByRole('button', { name: 'Kaydet' }).click();
        await expect(page.locator('#toastContainer .toast.success')).toContainText(`Eşzamanlı görev sınırı ${next} bilgisayar oldu.`);
        await expect(bar).toBeHidden();
        expect((await apiJson(page.request, 'GET', '/api/get_concurrent_limit')).limit).toBe(next);

        await page.reload();
        await settled(page);
        await expect(page.getByRole('spinbutton', { name: 'Eşzamanlı görev sınırı' })).toHaveValue(String(next));
    } finally {
        await apiJson(page.request, 'POST', '/api/set_concurrent_limit', { limit: before });
    }
});
