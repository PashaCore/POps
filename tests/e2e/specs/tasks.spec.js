// İşlemler: API ile açılan görev listede durumuyla görünür; durumu değişince liste kendiliğinden tazelenir.
// Bağlı ajan yok: görev "Sırada" bekler, sonra API ile iptal edilir.
import { test, expect, settled, apiJson, uniq } from '../fixtures.js';

test('API ile açılan görev durumuyla görünür', async ({ page }) => {
    const title = uniq('E2E görev');
    const created = await apiJson(page.request, 'POST', '/api/deploy_orchestration', {
        target_mode: 'PC',
        targets: ['HW-E2E0000102'],
        taskSequence: [{ name: title, type: 'CMD', command: 'echo e2e' }],
        title,
        source: 'tasks',
    });
    expect(created.created).toBe(1);
    const [taskId] = created.task_ids;

    await page.goto('/tasks');
    await settled(page);
    const job = page.locator('#jobList .act[data-job]').filter({ has: page.locator('.what', { hasText: title }) });
    await expect(job).toHaveCount(1);
    await expect(job.locator('.side .word')).toHaveText('Sırada');
    await expect(job.locator('.meta')).toContainText('LAB1-PC01');
    await expect(job.locator('.meta')).toContainText('admin');

    // Ayrıntı paneli: komut ve hedef bilgisayar
    await job.click();
    const drawer = page.locator('#popsDrawer');
    await expect(drawer).toHaveClass(/\bopen\b/);
    await expect(drawer.locator('.drawer-head h2')).toHaveText(title);
    await expect(drawer.locator('.tk-cmd')).toHaveText('echo e2e');
    await expect(drawer.locator('.tk-dev .act[data-task]')).toHaveCount(1);
    await expect(drawer.locator('.tk-dev .act[data-task] .what')).toHaveText('LAB1-PC01');
    await drawer.getByRole('button', { name: 'Paneli kapat' }).click();

    // Durum sunucuda değişince liste yoklamayla güncellenir (sayfa yenilenmeden)
    const r = await apiJson(page.request, 'POST', '/api/tasks/action', { action: 'CANCEL', target_mode: 'TASK', target_id: String(taskId) });
    expect(r.changed).toBe(1);
    await expect(job.locator('.side .word')).toHaveText('İptal edildi', { timeout: 15_000 });

    // "Sorunlu" süzgeci iptal edilen işi gösterir, "Sürüyor" göstermez
    await page.locator('#tkFilter').getByRole('button', { name: 'Sürüyor' }).click();
    await expect(job).toHaveCount(0);
    await page.locator('#tkFilter').getByRole('button', { name: 'Sorunlu' }).click();
    await expect(job).toHaveCount(1);
});
