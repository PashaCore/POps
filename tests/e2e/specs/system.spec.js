// Sistem iki görünümdür: Sistem ve Güncelleme (system) ile Ayarlar (system?view=ayarlar). Bölümler yan menüden açılır
// (sayfanın sekme çubuğu .tabs-in-nav gizlidir) ve adreste ?tab= olarak kalır. Eğilimler Sağlık ve yedek'in altında
// kapalı gelir, açılınca yüklenir. Yeni veritabanında sunucu ölçümü henüz yoktur: ölçüm grafikleri "Ölçümler
// toplanıyor" da diyebilir.
import { test, expect, settled } from '../fixtures.js';

const MAIN = [['updates', 'Güncellemeler'], ['health', 'Sağlık ve yedek']];
const CONFIG = [['security', 'Cihaz kaydı ve güvenlik'], ['notify', 'Bildirimler ve saklama'], ['modules', 'Modüller'], ['integrations', 'Entegrasyonlar']];
const pane = (page, name) => page.locator(`.sys-wrap > [data-pane="${name}"]`);

test('Sistem ve Güncelleme: bölümler menüden açılır ve ?tab= adreste kalır', async ({ page }) => {
    await page.goto('/system');
    await settled(page);
    await expect(page.locator('.page-header h1')).toHaveText('Sistem ve Güncelleme');
    const nav = page.locator('.nav-tabs[data-for="system"]');
    await expect(nav.locator('a[data-tab]')).toHaveText(MAIN.map(([, label]) => label));
    await expect(page.locator('#sysTabs')).toBeHidden();
    await expect(page.locator('#sysTabs [data-tab]')).toHaveCount(MAIN.length);
    await expect(nav.locator('a[data-tab="updates"]')).toHaveClass(/\bactive\b/);
    await expect(pane(page, 'updates')).toBeVisible();
    await expect(pane(page, 'security')).toHaveCount(0);

    await nav.locator('a[data-tab="health"]').click();
    await expect(page).toHaveURL(/\/system\?tab=health$/);
    await expect(nav.locator('a[data-tab="health"]')).toHaveClass(/\bactive\b/);
    await expect(pane(page, 'health')).toBeVisible();
    await expect(pane(page, 'updates')).toBeHidden();
    await expect(page.locator('#hlBody .srow').first()).toBeVisible();
    await expect(page.locator('#bkSet .act')).toHaveCount(1);

    // Yenileyince seçili bölüm korunur; varsayılan bölüm adresten ?tab= kaldırır
    await page.reload();
    await settled(page);
    await expect(pane(page, 'health')).toBeVisible();
    await nav.locator('a[data-tab="updates"]').click();
    await expect(page).toHaveURL(/\/system$/);
    await expect(pane(page, 'updates')).toBeVisible();
    await expect(pane(page, 'health')).toBeHidden();

    // Özetteki sağlık öğesi bölümü açar
    await page.locator('#sysSummary [data-go="health"]').click();
    await expect(page).toHaveURL(/\/system\?tab=health$/);
    await expect(pane(page, 'health')).toBeVisible();
});

test('Güncellemeler: sunucu, kullanılan sürümler, iki adım ve sürüm paneli', async ({ page }) => {
    await page.goto('/system');
    await settled(page);
    await expect(page.getByRole('button', { name: 'Sürüm notları' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Güncellemeleri denetle' })).toBeVisible();
    await expect(page.locator('#srvSet .vcell')).toHaveCount(2);

    // Sürüm başına bir satır: sürüm, bilgisayar sayısı ve güncel/eski
    const vers = page.locator('#agVers [data-ver]');
    await expect(vers.first()).toBeVisible();
    await expect(vers.first().locator('.badge')).toHaveText(/^(güncel|eski|bilinmiyor)$/);
    await expect(vers.first().locator('.vc')).toContainText('bilgisayar');

    const steps = page.locator('#agSteps .step');
    await expect(steps).toHaveCount(2);
    await expect(steps.nth(0).locator('.t')).toContainText('Ajan paketini hazırla');
    await expect(steps.nth(1).locator('.t')).toContainText('Ajanları güncelle');
    await expect(steps.nth(0).getByRole('button', { name: 'Paketi elle yükle' })).toBeVisible();

    // Sürüm satırı o sürümdeki bilgisayarları açar; bilgisayara pc?id= ile gidilir
    await vers.first().click();
    const drawer = page.locator('#popsDrawer');
    await expect(drawer).toHaveClass(/\bopen\b/);
    await expect(drawer.locator('.dr-pcs a[href^="pc?id="]').first()).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(drawer).not.toHaveClass(/\bopen\b/);
});

test('Eğilimler kapalı gelir; açılınca yüklenir ve grafikler çizilir', async ({ page }) => {
    const asked = [];
    page.on('request', (r) => { if (r.url().includes('/api/system/overview')) asked.push(r.url()); });
    await page.goto('/system?tab=health');
    await settled(page);
    const trends = page.locator('#trends');
    expect(await trends.evaluate((d) => d.open)).toBe(false);
    await expect(page.locator('#chGrid')).toBeHidden();
    expect(asked, 'kapalıyken grafik verisi istenmez').toHaveLength(0);

    await trends.locator('summary').click();
    await expect.poll(() => asked.length).toBeGreaterThan(0);
    const charts = page.locator('#chGrid .ch');
    await expect(charts).toHaveCount(6);
    const chart = (key) => page.locator(`#chGrid .ch[data-ch="${key}"]`);
    // Görev ve olay grafikleri kayıtlardan çizilir (örnek veride son 24 saatte ikisi de var)
    for (const key of ['tasks', 'events']) {
        await expect(chart(key).locator('.ch-b svg.ch-svg')).toHaveCount(1);
        await expect(chart(key).locator('.ch-n')).toHaveText(/^\d[\d.]*\s*(işlem|olay)$/);
    }
    // Ölçüm grafikleri: çizgi/çubuk ya da yeni sunucuda "Ölçümler toplanıyor"
    for (const key of ['agents', 'load', 'requests', 'storage']) {
        const body = chart(key).locator('.ch-b');
        await expect(body.locator('svg.ch-svg, .ch-empty')).toHaveCount(1);
        if (await body.locator('.ch-empty').count()) await expect(body.locator('.ch-empty')).toContainText('Ölçümler toplanıyor');
        await expect(chart(key).locator('.ch-n')).not.toBeEmpty();
    }

    // Aralık seçimi: 7 gün seçilince grafikler yeniden çizilir
    const span = page.getByRole('group', { name: 'Zaman aralığı' });
    await span.getByRole('button', { name: '7 gün' }).click();
    await expect(span.getByRole('button', { name: '7 gün' })).toHaveAttribute('aria-pressed', 'true');
    await expect(chart('events').locator('.ch-b svg.ch-svg')).toHaveCount(1);
});

test('Ayarlar: bölümler menüden açılır; ?do=token jeton penceresini açar', async ({ page }) => {
    await page.goto('/system?view=ayarlar');
    await settled(page);
    await expect(page.locator('.page-header h1')).toHaveText('Ayarlar');
    await expect(page.locator('#notesBtn, #checkBtn')).toHaveCount(0);
    const nav = page.locator('.nav-tabs[data-for="system-config"]');
    await expect(nav.locator('a[data-tab]')).toHaveText(CONFIG.map(([, label]) => label));
    await expect(page.locator('#sysTabs [data-tab]')).toHaveCount(CONFIG.length);
    await expect(pane(page, 'security')).toBeVisible();
    await expect(pane(page, 'updates')).toHaveCount(0);
    await expect(page.locator('#enSet #tokNew')).toBeVisible();

    for (const [name] of CONFIG.slice(1)) {
        await nav.locator(`a[data-tab="${name}"]`).click();
        await expect(page).toHaveURL(new RegExp(`/system\\?view=ayarlar&tab=${name}$`));
        await expect(pane(page, name)).toBeVisible();
        await expect(pane(page, 'security')).toBeHidden();
    }

    // Kontrol merkezi → Jeton üret: jetonlar yüklenince pencere açılır, do= adresten kalkar
    await page.goto('/system?view=ayarlar&do=token');
    await settled(page);
    await expect(page.locator('#tokenModal')).toHaveClass(/\bopen\b/);
    await expect(page).toHaveURL(/\/system\?view=ayarlar$/);
});
