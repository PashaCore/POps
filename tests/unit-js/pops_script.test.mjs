// Ortak betiğin (Dashboard/assets/pops_script.js) ve header.php'deki escapeHtml/jsArg'ın saf yardımcıları.
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { loadPanel, dictFor, plain } from './harness.mjs';

const tr = loadPanel();
const en = loadPanel({ lang: 'en', dict: dictFor() });
const esc = (s) => tr.ctx.escapeHtml(s);

describe('escapeHtml / jsArg (header.php)', () => {
    it('boş ve null değerde boş metin', () => {
        assert.equal(esc(null), '');
        assert.equal(esc(undefined), '');
        assert.equal(esc(''), '');
    });
    it('HTML özel karakterlerini kaçırır', () => {
        assert.equal(esc(`<img src=x onerror="alert('1')">&`), '&lt;img src=x onerror=&quot;alert(&#39;1&#39;)&quot;&gt;&amp;');
        assert.equal(esc('&amp;'), '&amp;amp;');
    });
    it('sayı ve Türkçe karakterler olduğu gibi', () => {
        assert.equal(esc(0), '0');
        assert.equal(esc(false), 'false');
        assert.equal(esc('İğüşöçı ĞÜŞÖÇI'), 'İğüşöçı ĞÜŞÖÇI');
    });
    it('POps.escape aynı kaçırıcı', () => {
        assert.equal(tr.POps.escape('<b>"x"</b>'), '&lt;b&gt;&quot;x&quot;&lt;/b&gt;');
    });
    it('jsArg önce JSON, sonra HTML olarak kaçırır', () => {
        assert.equal(tr.ctx.jsArg(`a'b"<`), '&quot;a&#39;b\\&quot;&lt;&quot;');
        assert.equal(tr.ctx.jsArg(null), '&quot;&quot;');
        assert.equal(tr.ctx.jsArg(5), '5');
    });
});

describe('POps.t ailesi (Türkçe: anahtar metnin kendisi)', () => {
    const { POps } = tr;
    it('sözlük yokken metni döndürür, null/undefined boş', () => {
        assert.equal(POps.lang, 'tr');
        assert.equal(POps.locale, 'tr-TR');
        assert.equal(POps.t('Cihazlar'), 'Cihazlar');
        assert.equal(POps.t(null), '');
        assert.equal(POps.t(undefined), '');
    });
    it('yer tutucuları doldurur; eksik ya da null değer yer tutucuyu bırakır', () => {
        assert.equal(POps.t('{name} silinsin mi?', { name: 'LAB-1 Yazılım' }), 'LAB-1 Yazılım silinsin mi?');
        assert.equal(POps.t('{name} silinsin mi?'), '{name} silinsin mi?');
        assert.equal(POps.t('{name} silinsin mi?', { name: null }), '{name} silinsin mi?');
        assert.equal(POps.t('{n} kayıt', { n: 0 }), '0 kayıt');
    });
    it('değerin içindeki yer tutucu yeniden doldurulmaz', () => {
        assert.equal(POps.t('{a} ve {b}', { a: '{b}', b: 'x' }), '{b} ve x');
    });
    it('tn n parametresini ekler; tx sözlükte bağlam yoksa Türkçe kalır', () => {
        assert.equal(POps.tn('{n} bilgisayar', 3), '3 bilgisayar');
        assert.equal(POps.tn('{n} bilgisayar · {lab}', 2, { lab: 'Kütüphane' }), '2 bilgisayar · Kütüphane');
        assert.equal(POps.tx('Kapat', 'power'), 'Kapat');
    });
    it('tHtml metni ve değerleri kaçırır, html parçalarını olduğu gibi koyar', () => {
        assert.equal(POps.tHtml('{name} <b>silinsin</b>', { name: '<x>' }), '&lt;x&gt; &lt;b&gt;silinsin&lt;/b&gt;');
        assert.equal(POps.tHtml('{n} cihaz', null, { n: '<b>3</b>' }), '<b>3</b> cihaz');
        assert.equal(POps.tnHtml('{n} uyarı & {m}', 2, { m: '"q"' }), '2 uyarı &amp; &quot;q&quot;');
    });
    it('tNodes yer tutucuya verilen nesneyi koyar', () => {
        const node = { tag: 'a' };
        const parts = POps.tNodes('Önce {link} sonra {n}', { n: 4 }, { link: node });
        assert.equal(parts.length, 4);
        assert.equal(parts[0], 'Önce ');
        assert.equal(parts[1], node);
        assert.equal(parts[3], '4');
    });
    it('pct: Türkçede yüzde işareti önde', () => {
        assert.equal(POps.pct(40), '%40');
    });
});

describe('POps.t ailesi (İngilizce sözlük, lang/en/common.json)', () => {
    const { POps } = en;
    it('çeviriyi ve yerel ayarı kullanır', () => {
        assert.equal(POps.lang, 'en');
        assert.equal(POps.locale, 'en-GB');
        assert.equal(POps.t('Beklenmeyen hata.'), 'Unexpected error.');
        assert.equal(POps.pct(40), '40%');
    });
    it('sözlükte olmayan metin Türkçe kalır', () => {
        assert.equal(POps.t('Sözlükte olmayan bir cümle {x}', { x: 'İ' }), 'Sözlükte olmayan bir cümle İ');
    });
    it('çoğul: n mutlak değeri 1 ise one, değilse other', () => {
        assert.equal(POps.tn('{n} gün', 1), '1 day');
        assert.equal(POps.tn('{n} gün', -1), '-1 day');
        assert.equal(POps.tn('{n} gün', '1'), '1 day');
        assert.equal(POps.tn('{n} gün', 0), '0 days');
        assert.equal(POps.tn('{n} gün', 2), '2 days');
        // n'siz çağrı other biçimini kullanır
        assert.equal(POps.t('{n} gün'), '{n} days');
    });
    it('tx bağlamlı anahtar; taskName yalnızca ilk parçayı ve |task girdilerini çevirir', () => {
        assert.equal(POps.tx('Kapat', 'power'), 'Shut down');
        assert.equal(POps.tx('Kapat', 'olmayan_baglam'), 'Kapat');
        assert.equal(POps.taskName('Kapat · LAB1'), 'Shut down · LAB1');
        assert.equal(POps.taskName('Yeniden başlat'), 'Restart');
        assert.equal(POps.taskName('Benim görevim · Kapat'), 'Benim görevim · Kapat');
        assert.equal(POps.taskName(null), '');
    });
});

describe('zaman ve süre', () => {
    it('toDate: boş değerler null, saniye ve milisaniye sayıları, geçersiz metin', () => {
        const { POps } = tr;
        for (const v of [null, undefined, '', '-', 'tarih değil']) assert.equal(POps.toDate(v), null, String(v));
        assert.equal(POps.toDate(1700000000).getTime(), 1700000000000);
        assert.equal(POps.toDate(1700000000123).getTime(), 1700000000123);
        const d = new Date('2026-10-05T12:00:00Z');
        assert.equal(POps.toDate('2026-10-05T12:00:00Z').getTime(), d.getTime());
    });
    it('duration: saniye, dakika, saat, gün sınırları', () => {
        const { POps } = tr;
        const cases = [[0, '0 sn'], [-5, '0 sn'], ['abc', '0 sn'], [59.4, '59 sn'], [60, '1 dk'], [61, '1 dk 1 sn'], [599, '9 dk 59 sn'],
            [601, '10 dk'], [3600, '1 sa'], [3660, '1 sa 1 dk'], [86399, '23 sa 59 dk'], [86400, '1 gün'], [3 * 86400 + 5, '3 gün']];
        for (const [sec, want] of cases) assert.equal(POps.duration(sec), want, String(sec));
    });
    it('duration İngilizcede çoğul gün', () => {
        assert.equal(en.POps.duration(90), '1 min 30 s');
        assert.equal(en.POps.duration(86400), '1 day');
        assert.equal(en.POps.duration(2 * 86400), '2 days');
    });
    it('relTime: boş değer, şimdi, dakika önce, yakın gelecek', () => {
        const now = Date.now();
        assert.equal(tr.POps.relTime(null), '—');
        assert.equal(tr.POps.relTime(new Date(now - 10 * 1000)), 'şimdi');
        assert.equal(tr.POps.relTime(new Date(now + 60 * 1000)), 'şimdi');
        assert.equal(tr.POps.relTime(new Date(now - 5 * 60 * 1000)), '5 dk önce');
        assert.equal(en.POps.relTime(new Date(now - 5 * 60 * 1000)), '5 min ago');
    });
    it('timeHtml ve fullTime', () => {
        assert.equal(tr.POps.timeHtml(null), '—');
        assert.equal(tr.POps.fullTime(null), '');
        const html = tr.POps.timeHtml('2026-10-05T12:00:00Z');
        assert.match(html, /^<time datetime="2026-10-05T12:00:00\.000Z" title="[^"<>]+">[^<>]+<\/time>$/);
        assert.match(tr.POps.fullTime('2026-10-05T12:00:00Z'), /2026/);
    });
});

describe('cihaz, görev ve hata yardımcıları', () => {
    const { POps } = tr;
    it('isOnline / isOffline: büyük-küçük harf, eksik durum kapalı sayılır', () => {
        assert.equal(POps.isOnline({ status: 'ONLINE' }), true);
        assert.equal(POps.isOnline({ status: 'offline' }), false);
        assert.equal(POps.isOnline(null), false);
        assert.equal(POps.isOffline({ status: 'Offline' }), true);
        assert.equal(POps.isOffline({}), true);
        assert.equal(POps.isOffline(null), true);
        assert.equal(POps.isOffline({ status: 'idle' }), false);
    });
    it('deviceName: görünen ad > gerçek ad > kimlik > donanım kimliği', () => {
        assert.equal(POps.deviceName({ display_name: 'Öğretmen PC', hostname: 'HW-1' }), 'Öğretmen PC');
        assert.equal(POps.deviceName({ real_hostname: 'LAB1-PC01', hostname: 'HW-1' }), 'LAB1-PC01');
        assert.equal(POps.deviceName({ hostname: 'HW-1', hw_id: 'X' }), 'HW-1');
        assert.equal(POps.deviceName({ hw_id: 'X' }), 'X');
        assert.equal(POps.deviceName(null), '');
    });
    it('taskState: bitmiş, başarısız, süren', () => {
        assert.equal(POps.taskState('Completed'), 'ok');
        assert.equal(POps.taskState('Completed (Rebooted)'), 'ok');
        assert.equal(POps.taskState('Timed Out'), 'bad');
        assert.equal(POps.taskState('Pending'), 'run');
        assert.equal(POps.taskState(undefined), 'run');
    });
    it('errorMessage: iletinin kendisi, yedek metin, varsayılan', () => {
        assert.equal(POps.errorMessage({ message: 'Bulunamadı.' }), 'Bulunamadı.');
        assert.equal(POps.errorMessage(null, 'Yedek'), 'Yedek');
        assert.equal(POps.errorMessage(null), 'Beklenmeyen hata.');
        assert.equal(en.POps.errorMessage(null), 'Unexpected error.');
    });
    it('apiErrorText: FastAPI detail biçimleri, HTML gövde, bilinmeyen durum kodu', () => {
        const apiErrorText = vm.runInContext('apiErrorText', tr.ctx);
        assert.equal(apiErrorText({ detail: '  Lisans bulunamadı.  ' }, 404), 'Lisans bulunamadı.');
        assert.equal(apiErrorText({ detail: [{ loc: ['body', 'name'], msg: 'field required' }, { msg: 'x' }] }, 422), 'name: field required; x');
        assert.equal(apiErrorText({ detail: { message: 'İç ileti' } }, 400), 'İç ileti');
        assert.equal(apiErrorText({ message: 'İleti' }, 200), 'İleti');
        assert.equal(apiErrorText('<html><body>Bad gateway</body></html>', 502), 'Sunucuya ulaşılamadı.');
        assert.equal(apiErrorText('Düz hata metni', 500), 'Düz hata metni');
        assert.equal(apiErrorText(null, 418), 'Sunucu hatası (HTTP 418).');
        assert.equal(vm.runInContext('apiErrorText', en.ctx)(null, 418), 'Server error (HTTP 418).');
    });
    it('iconHtml: sınıf ve simge adı kaçırılır', () => {
        assert.equal(POps.iconHtml('x', 'sm'), '<svg class="ico sm" aria-hidden="true"><use href="assets/pops_icons.svg#i-x"></use></svg>');
        assert.equal(POps.iconHtml('a"b'), '<svg class="ico" aria-hidden="true"><use href="assets/pops_icons.svg#i-a&quot;b"></use></svg>');
    });
    it('state başlangıçta boş', () => {
        assert.deepEqual(plain(vm.runInContext('state.devices', tr.ctx)), []);
        assert.equal(vm.runInContext('state.devicesLoaded', tr.ctx), false);
    });
});
