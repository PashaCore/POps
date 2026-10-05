// Sistem: sekmeler değişir ve adreste ?tab= olarak kalır; Genel bakış kutucukları ve grafikleri çizilir.
// Yeni veritabanında sunucu ölçümü henüz yoktur: ölçüm grafikleri "Ölçümler toplanıyor" da diyebilir.
import { test, expect, settled } from '../fixtures.js';

const TABS = [
    ['overview', 'Genel bakış'],
    ['updates', 'Güncellemeler'],
    ['security', 'Güvenlik'],
    ['health', 'Sağlık ve yedek'],
    ['notify', 'Bildirimler ve saklama'],
];

test('sekmeler değişir ve ?tab= adreste kalır', async ({ page }) => {
    await page.goto('/system');
    await settled(page);
    const bar = page.locator('#sysTabs');
    const pane = (name) => page.locator(`.sys-wrap > [data-pane="${name}"]`);
    await expect(bar.getByRole('tab')).toHaveText(TABS.map(([, label]) => label));
    await expect(bar.getByRole('tab', { name: 'Genel bakış' })).toHaveAttribute('aria-selected', 'true');

    for (const [name, label] of TABS.slice(1)) {
        await bar.getByRole('tab', { name: label }).click();
        await expect(page).toHaveURL(new RegExp(`/system\\?tab=${name}$`));
        await expect(bar.getByRole('tab', { name: label })).toHaveAttribute('aria-selected', 'true');
        await expect(pane(name)).toBeVisible();
        await expect(pane('overview')).toBeHidden();
    }

    // Yenileyince seçili sekme korunur
    await page.reload();
    await settled(page);
    await expect(bar.getByRole('tab', { name: 'Bildirimler ve saklama' })).toHaveAttribute('aria-selected', 'true');
    await expect(pane('notify')).toBeVisible();

    // Doğrudan adresle açılır; varsayılan sekme adresten ?tab= kaldırır
    await page.goto('/system?tab=health');
    await settled(page);
    await expect(bar.getByRole('tab', { name: 'Sağlık ve yedek' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('#hlBody')).not.toBeEmpty();
    await bar.getByRole('tab', { name: 'Genel bakış' }).click();
    await expect(page).toHaveURL(/\/system$/);
    await expect(pane('overview')).toBeVisible();
    await expect(pane('health')).toBeHidden();

    // Genel bakış kutucuğu ilgili sekmeyi açar
    await page.locator('#ovGrid .ov').filter({ hasText: 'Yedekler' }).click();
    await expect(page).toHaveURL(/\/system\?tab=health$/);
});

test('Genel bakış kutucukları ve grafikleri', async ({ page }) => {
    await page.goto('/system');
    await settled(page);

    // Kutucuklar bölüm başlıklarındaki durumu kopyalar (sunucu, ajanlar, sağlık, yedek, kayıt, yetenek, bütünlük, bildirim)
    const tiles = page.locator('#ovGrid .ov').filter({ visible: true });
    await expect(tiles).toHaveCount(8);
    for (const t of ['Sunucu', 'Ajanlar', 'Sağlık']) {
        await expect(tiles.filter({ hasText: t }).first().locator('.ov-s')).not.toBeEmpty();
    }

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
