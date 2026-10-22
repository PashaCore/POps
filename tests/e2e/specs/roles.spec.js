// Yetki: izleyici (viewer) yönetici işlemlerini ve sayfalarını görmez. İzleyici, örnek yönetici olarak API ile
// açılır. Yetki listesinde Dağıtım, Uzak komut ve Ayarlar da var: yönetici iken bu yetkileri olan biri izleyiciye
// düşürülünce de böyle kalır; header.php bu üç sayfayı izleyiciye yetkiden bağımsız kapatır.
import crypto from 'node:crypto';
import { test, expect, settled, apiJson } from '../fixtures.js';
import { PANEL_URL } from '../stack.js';

const PERMS = ['devices', 'labs', 'tasks', 'logger', 'deploy', 'terminal', 'settings'];

test('izleyici yönetici işlemlerini ve sayfalarını görmez', async ({ page: admin, browser, watch }) => {
    const username = `e2e-izleyici-${crypto.randomBytes(3).toString('hex')}`;
    const password = crypto.randomBytes(12).toString('base64url');
    await apiJson(admin.request, 'POST', '/api/admin/users', { username, password, role: 'viewer', permissions: JSON.stringify(PERMS) });

    const ctx = await browser.newContext({ baseURL: PANEL_URL, viewport: { width: 1440, height: 900 }, storageState: { cookies: [], origins: [] } });
    const page = await ctx.newPage();
    watch(page);
    try {
        await test.step('giriş', async () => {
            await page.goto('/login');
            await page.locator('input[name="username"]').fill(username);
            await page.locator('input[name="password"]').fill(password);
            await page.getByRole('button', { name: 'Giriş yap' }).click();
            await expect(page.locator('.page-header h1')).toHaveText('Kontrol merkezi');
            await expect(page.locator('.user-card .user-role')).toHaveText('İzleyici');
            await settled(page);
        });

        await test.step('yan menüde yalnızca açabildiği sayfalar', async () => {
            const nav = page.locator('.sidebar-nav a.nav-item > span:first-of-type');
            await expect(nav).toHaveText(['Kontrol merkezi', 'Cihazlar', 'Sınıflar', 'İşlemler', 'Kayıtlar']);
        });

        await test.step('yönetici sayfaları kapalı', async () => {
            for (const path of ['/deploy', '/terminal', '/settings', '/system', '/vision', '/policies']) {
                await page.goto(path);
                await expect(page.getByRole('heading', { name: 'Yetkisiz Erişim' }), path).toBeVisible();
                await expect(page.locator('#mainContent')).toHaveCount(0);
            }
        });

        await test.step('Cihazlar: seçim ve toplu işlem yok, ayrıntıda işlem düğmesi yok', async () => {
            await page.goto('/devices');
            await settled(page);
            await expect(page.locator('#devBody tr[data-host]')).toHaveCount(12);
            await expect(page.locator('#devHead input[type="checkbox"], #devBody input[type="checkbox"]')).toHaveCount(0);
            await expect(page.locator('#devBar [data-act]')).toHaveCount(0);
            await expect(page.locator('#devBar')).toHaveText('12 bilgisayar');
            await page.locator('#devBody tr[data-host]').filter({ hasText: 'LAB1-PC01' }).locator('.nm').click();
            const drawer = page.locator('#popsDrawer');
            await expect(drawer.locator('.drawer-head h2')).toHaveText('LAB1-PC01');
            await expect(drawer.locator('.circs, [data-act="power"], [data-act="more"], [data-act="command"]')).toHaveCount(0);
        });

        await test.step('Sınıflar: sınıf işlemleri ve yerleşim düzenleme yok', async () => {
            await page.goto('/labs');
            await settled(page);
            await expect(page.locator('#labTitle')).not.toHaveText('Sınıflar');
            await expect(page.getByRole('button', { name: 'Sınıf işlemleri' })).toBeHidden();
            await expect(page.getByRole('button', { name: 'Yerleşimi düzenle' })).toBeHidden();
            await expect(page.locator('#labBar [data-act]')).toHaveCount(0);
        });

        await test.step('İşlemler: kuyruk düğmeleri ve zamanlanmış görevler yok', async () => {
            await page.goto('/tasks');
            await settled(page);
            await expect(page.locator('#jobList .act[data-job]').first()).toBeVisible();
            await expect(page.locator('#tkLimitBtn, #tkMenuBtn, #schedNewBtn, #schedModal')).toHaveCount(0);
            await expect(page.locator('#tkTab [data-tab="sched"]')).toBeHidden();
        });

        await test.step('Kayıtlar: yönetici menüsü yok', async () => {
            await page.goto('/logger');
            await settled(page);
            await expect(page.locator('#lgList .act[data-id]').first()).toBeVisible();
            await expect(page.getByRole('button', { name: 'Diğer işlemler' })).toBeHidden();
        });

        await test.step('API de reddeder', async () => {
            const r = await page.request.post('/api/create_lab', { data: { lab_name: 'izleyici-sinifi' }, headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            expect(r.status()).toBe(403);
        });
    } finally {
        await ctx.close();
        const { users } = await apiJson(admin.request, 'GET', '/api/admin/users');
        const u = users.find((x) => x.username === username);
        if (u) await apiJson(admin.request, 'DELETE', `/api/admin/users/${u.id}`);
    }
});
