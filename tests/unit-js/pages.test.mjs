// Sayfa betiklerinin (Dashboard/assets/pages/*.js) saf yardımcıları.
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { loadPageHelpers, dictFor, plain } from './harness.mjs';

describe('logger.js', () => {
    const { helpers } = loadPageHelpers('logger');

    it('yalnızca yardımcıları verir', () => {
        assert.deepEqual(Object.keys(helpers).sort(), ['csvCell', 'dayKey', 'pageNumbers']);
    });

    it('csvCell: boş değerler, sayılar ve Türkçe metin', () => {
        const { csvCell } = helpers;
        assert.equal(csvCell(null), '');
        assert.equal(csvCell(undefined), '');
        assert.equal(csvCell(''), '');
        assert.equal(csvCell(0), '0');
        assert.equal(csvCell('Öğretmen PC · İşlemci'), 'Öğretmen PC · İşlemci');
    });
    it('csvCell: formül olarak çalışabilecek hücrenin önüne tek tırnak', () => {
        const { csvCell } = helpers;
        assert.equal(csvCell('=SUM(1+1)'), "'=SUM(1+1)");
        assert.equal(csvCell('+90 555'), "'+90 555");
        assert.equal(csvCell('-1'), "'-1");
        assert.equal(csvCell(-1), "'-1");
        assert.equal(csvCell('@cmd'), "'@cmd");
        assert.equal(csvCell('\tx'), "'\tx");
        assert.equal(csvCell('a=b'), 'a=b');
    });
    it('csvCell: ayraç, tırnak ve satır sonu tırnak içinde', () => {
        const { csvCell } = helpers;
        assert.equal(csvCell('a;b'), '"a;b"');
        assert.equal(csvCell('dedi "evet"'), '"dedi ""evet"""');
        assert.equal(csvCell('iki\nsatır'), '"iki\nsatır"');
        assert.equal(csvCell('=a;b'), '"\'=a;b"');
        assert.equal(csvCell('<script>'), '<script>');
    });

    it('pageNumbers: tek sayfa ve az sayfa', () => {
        const { pageNumbers } = helpers;
        assert.deepEqual(plain(pageNumbers(1, 1)), [1]);
        assert.deepEqual(plain(pageNumbers(1, 2)), [1, 2]);
        assert.deepEqual(plain(pageNumbers(2, 4)), [1, 2, 3, 4]);
    });
    it('pageNumbers: baş, orta ve son', () => {
        const { pageNumbers } = helpers;
        assert.deepEqual(plain(pageNumbers(1, 10)), [1, 2, 3, 4, 10]);
        assert.deepEqual(plain(pageNumbers(3, 10)), [1, 2, 3, 4, 10]);
        assert.deepEqual(plain(pageNumbers(4, 10)), [1, 3, 4, 5, 10]);
        assert.deepEqual(plain(pageNumbers(5, 10)), [1, 4, 5, 6, 10]);
        assert.deepEqual(plain(pageNumbers(8, 10)), [1, 7, 8, 9, 10]);
        assert.deepEqual(plain(pageNumbers(10, 10)), [1, 7, 8, 9, 10]);
    });

    it('dayKey: yerel tarih, sıfırla doldurulmuş ay ve gün', () => {
        const { dayKey } = helpers;
        assert.equal(dayKey(new Date(2026, 0, 5, 23, 59)), '2026-01-05');
        assert.equal(dayKey(new Date(2026, 11, 31, 0, 0)), '2026-12-31');
    });
});

describe('reports.js', () => {
    const tr = loadPageHelpers('reports');
    const en = loadPageHelpers('reports', { lang: 'en', dict: dictFor('reports') });

    it('yalnızca yardımcıları verir', () => {
        assert.deepEqual(Object.keys(tr.helpers).sort(), ['dayKey', 'ptNeeds', 'ptState']);
    });

    it('ptState: bildirmeyen, kritik, güvenlik, diğer, güncel (öncelik sırası)', () => {
        const { ptState } = tr.helpers;
        assert.deepEqual(plain(ptState({ reported: false, pending_critical: 3 })), { cls: 'off', word: 'Bildirmedi' });
        assert.deepEqual(plain(ptState({ reported: true, pending_critical: 1, pending_security: 2, pending_count: 5 })), { cls: 'bad', word: 'Kritik eksik' });
        assert.deepEqual(plain(ptState({ reported: true, pending_security: 2, pending_count: 5 })), { cls: 'warn', word: 'Güvenlik eksik' });
        assert.deepEqual(plain(ptState({ reported: true, pending_count: 5 })), { cls: '', word: 'Güncelleme var' });
        assert.deepEqual(plain(ptState({ reported: true, pending_count: 0, reboot_required: true })), { cls: 'ok', word: 'Güncel' });
    });
    it('ptState İngilizce sözlükle (lang/en/reports.json)', () => {
        const { ptState } = en.helpers;
        assert.equal(ptState({}).word, 'Not reported');
        assert.equal(ptState({ reported: true }).word, 'Up to date');
    });

    it('ptNeeds: yalnızca bildiren ve eksiği ya da yeniden başlatması olan', () => {
        const { ptNeeds } = tr.helpers;
        assert.equal(ptNeeds({ reported: false, pending_count: 3, reboot_required: true }), false);
        assert.equal(ptNeeds({ reported: true }), false);
        assert.equal(ptNeeds({ reported: true, pending_count: 0, reboot_required: false }), false);
        assert.equal(ptNeeds({ reported: true, pending_count: 2 }), true);
        assert.equal(ptNeeds({ reported: true, reboot_required: true }), true);
    });

    it('dayKey logger.js ile aynı biçim', () => {
        const d = new Date(2026, 8, 9);
        assert.equal(tr.helpers.dayKey(d), '2026-09-09');
        assert.equal(tr.helpers.dayKey(d), loadPageHelpers('logger').helpers.dayKey(d));
    });
});
