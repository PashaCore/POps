// Kullanıcılar ve giriş: bölümler yan menüden açılır (sekme çubuğu .tabs-in-nav gizli); kullanıcı satırı ayrıntı
// panelini, "Düzenle" düzenleme penceresini açar. Genel: kurum adı kaydedilir ve giriş sayfasında görünür.
// Değiştirilen ayar test sonunda API ile eski haline döner (diğer testler etkilenmesin). Eşzamanlı görev sınırı
// İşlemler ve Dağıtım sayfalarındadır (aynı /api/set_concurrent_limit).
import { test, expect, settled, apiJson, uniq } from '../fixtures.js';
import { PANEL_URL } from '../stack.js';

test('bölümler menüden açılır; kullanıcı satırı paneli, Düzenle pencereyi açar', async ({ page }) => {
    await page.goto('/settings');
    await settled(page);
    await expect(page.locator('.page-header h1')).toHaveText('Kullanıcılar ve giriş');
    await expect(page.getByRole('button', { name: 'Kullanıcı ekle' })).toBeVisible();
    const nav = page.locator('.nav-tabs[data-for="settings"]');
    await expect(nav.locator('a[data-tab]')).toHaveText(['Kullanıcılar', 'Birimler', 'Giriş ve güvenlik', 'Genel']);
    await expect(page.locator('#setTabs')).toBeHidden();

    // Satır kullanıcının panelini açar; işlemler yazılı
    const row = page.locator('#userBody tr[data-id]').filter({ hasText: 'admin' }).first();
    await row.locator('td').first().click();
    const drawer = page.locator('#popsDrawer');
    await expect(drawer).toHaveClass(/\bopen\b/);
    await expect(drawer.locator('h2')).toHaveText('admin');
    await expect(drawer.getByRole('button', { name: 'Rolü ve yetkileri düzenle' })).toBeVisible();
    await page.keyboard.press('Escape');

    // Satırdaki "Düzenle" doğrudan düzenleme penceresini açar
    await row.getByRole('button', { name: 'admin kullanıcısını düzenle' }).click();
    const dlg = page.getByRole('dialog', { name: 'Kullanıcıyı düzenle' });
    await expect(dlg).toBeVisible();
    await expect(dlg.locator('#umName')).toHaveValue('admin');
    await dlg.getByRole('button', { name: 'Vazgeç' }).click();
    await expect(dlg).toBeHidden();

    // Giriş ve güvenlik: 2FA, API jetonları, kimlik sağlayıcıları
    await nav.locator('a[data-tab="security"]').click();
    await expect(page).toHaveURL(/\/settings\?tab=security$/);
    await expect(page.locator('#twofaCard')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'API jetonları' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Kimlik sağlayıcıları' })).toBeVisible();
    await expect(page.locator('[data-pane="users"]')).toBeHidden();
});

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
