<?php include 'includes/header.php'; ?>
<?php $canEdit = ($_SESSION['role'] ?? '') !== 'viewer'; ?>

<style>
    /* Ayar bölümü: solda başlık ve kısa açıklama, sağda ayar satırları; dar ekranda alt alta */
    .sects > .sect { display: grid; grid-template-columns: minmax(200px, 300px) minmax(0, 1fr); gap: 14px 48px; padding: 28px 0; border-top: 1px solid var(--border-subtle); }
    .sects > .sect:first-child { border-top: 0; padding-top: 4px; }
    .sect-head h2 { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .sect-head p { font-size: var(--text-sm); color: var(--text-tertiary); line-height: 1.55; margin-top: 6px; }
    .sect-head .meta { font-size: var(--text-xs); color: var(--text-muted); margin-top: 10px; display: flex; align-items: center; gap: 6px; }
    .sect-body { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
    .srow.block { display: block; }
    .srow textarea { width: 100%; }
    .srow .foot { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin-top: 8px; font-size: var(--text-xs); color: var(--text-muted); }
    .srow input[type=number] { width: 84px; text-align: right; font-variant-numeric: tabular-nums; }
    .srow .unit { font-size: var(--text-sm); color: var(--text-tertiary); }
    .srow.is-off .t, .srow.is-off .unit { color: var(--text-muted); }
    /* Kategori satırı: tıklayınca alan adı listesi açılır */
    .cat-open { flex: 1; min-width: 0; display: flex; align-items: center; gap: 10px; text-align: left; border-radius: 8px; }
    .cat-open > div { flex: 1; min-width: 0; }
    .cat-open .word { white-space: nowrap; }
    .cat-open .chev { color: var(--text-muted); transition: transform 0.15s; }
    .cat-open[aria-expanded="true"] .chev { transform: rotate(90deg); }
    .cat-open:hover .t { color: var(--primary-500); }
    .cat-body { padding: 0 16px 16px; }
    .cat-body textarea { font-family: var(--font-mono); font-size: 12.5px; min-height: 120px; }
    .cat-body .hint { font-size: var(--text-xs); color: var(--text-muted); margin-top: 6px; line-height: 1.5; }
    .faint { color: var(--text-muted); }
    /* Kaydedilmemiş değişiklik çubuğu */
    .savebar { position: sticky; bottom: 16px; z-index: 50; width: fit-content; max-width: 100%; margin: 24px auto 0; display: flex; align-items: center; gap: 10px; padding: 7px 7px 7px 16px; background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle), 0 8px 28px rgba(0, 0, 0, 0.10); font-size: var(--text-sm); color: var(--text-primary); }
    .savebar .sb-sub { color: var(--text-muted); }
    .savebar .btn { margin-left: 2px; }
    @media (max-width: 960px) { .sects > .sect { grid-template-columns: minmax(0, 1fr); gap: 12px; padding: 22px 0; } }
    @media (max-width: 640px) {
        .savebar { width: 100%; }
        .savebar .sb-sub { display: none; }
        .savebar .sb-text { flex: 1; }
    }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Politikalar'); ?></h1>
        <div class="summary" id="polSummary"><span class="sum"><?php _e('Yükleniyor…'); ?></span></div>
    </div>
</div>

<div id="polError" hidden></div>

<div class="sects" id="polForm" hidden>
    <section class="sect" aria-labelledby="hFair">
        <div class="sect-head">
            <h2 id="hFair"><?php _e('Aydınlatma metni'); ?></h2>
            <p><?php _e('Bilgisayarın okulca yönetildiğini, neyin kaydedilip neyin kaydedilmediğini anlatan kısa metin. Ajan açılınca tepside gösterilir; kullanıcı onaylayana kadar dakikada bir sorulur.'); ?></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow block">
                    <label for="fairUseText" class="sr-only"><?php _e('Aydınlatma ve adil kullanım metni'); ?></label>
                    <textarea id="fairUseText" rows="5" maxlength="4000" <?= $canEdit ? '' : 'readonly' ?> placeholder="<?php _e('Örn. Bu bilgisayar okul tarafından yönetilmektedir…'); ?>"></textarea>
                    <div class="foot">
                        <span id="fairCount"><?php _e('Boş bırakılırsa gösterilmez.'); ?></span>
                        <?php if ($canEdit): ?>
                        <button type="button" class="btn ghost sm" id="fairDefault"><?php echo pops_icon('file', 'sm'); ?><?php _e('Örnek metni kullan'); ?></button>
                        <?php endif; ?>
                    </div>
                </div>
            </div>
            <div class="set-note"><?php _e('Kurumunuzun KVKK aydınlatma metnine bağlantı vermeniz önerilir.'); ?> <?php _e('Şablon:'); ?> <code>docs/kvkk-aydinlatma.md</code></div>
        </div>
    </section>

    <section class="sect" aria-labelledby="hDns">
        <div class="sect-head">
            <h2 id="hDns"><?php _e('Yasaklı alan adı tespiti'); ?></h2>
            <p><?php _e('Açık bir kategorinin listesindeki alan adına girilince ajan ihlal kaydeder ve size bildirir. Engelleme yapmaz; bunun için okulun ağ filtresini kullanın. Sayfa içeriği, arama ve klavye kaydedilmez.'); ?></p>
            <div class="meta" id="fleetNote"><?php _e('0.1.5 ve sonrası ajanlarda çalışır.'); ?></div>
        </div>
        <div class="sect-body">
            <div id="dnsNotes"></div>
            <div class="set" id="cats"></div>
        </div>
    </section>

    <section class="sect" aria-labelledby="hQ">
        <div class="sect-head">
            <h2 id="hQ"><?php _e('Otomatik karantina'); ?></h2>
            <p><?php _e("Eşiğe ulaşan bilgisayar kendini ağdan yalıtır: yalnızca POps sunucusuna, DNS ve DHCP'ye erişebilir. Yalnızca DNS tespiti çalışan ajanlarda etkilidir."); ?></p>
        </div>
        <div class="sect-body">
            <div id="qNotes"></div>
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t" id="autoQLabel"><?php _e('Otomatik karantina'); ?></div>
                        <div class="d"><?php _e('Eşikte bilgisayarı kendiliğinden karantinaya al'); ?></div>
                    </div>
                    <label class="switch"><input type="checkbox" id="autoQ" aria-labelledby="autoQLabel" <?= $canEdit ? '' : 'disabled' ?>><span></span></label>
                </div>
                <div class="srow" id="qRow">
                    <div class="grow">
                        <div class="t" id="qThresholdLabel"><?php _e('Eşik'); ?></div>
                        <div class="d"><?php _e('Ajan son açıldığından beri tespit edilen ihlal sayısı'); ?></div>
                    </div>
                    <input type="number" id="qThreshold" min="1" max="100" value="3" inputmode="numeric" aria-labelledby="qThresholdLabel" <?= $canEdit ? '' : 'disabled' ?>>
                    <span class="unit"><?php _e('ihlal'); ?></span>
                </div>
            </div>
            <div class="set-note" id="qReleaseNote"></div>
        </div>
    </section>

    <?php if ($canEdit): ?>
    <div class="savebar" id="saveBar" role="region" aria-label="<?php _e('Kaydedilmemiş değişiklikler'); ?>" hidden>
        <span class="dot warn" aria-hidden="true"></span>
        <span class="sb-text"><?php _e('Kaydedilmemiş değişiklik var'); ?></span>
        <span class="sb-sub"><?php _e('· ajanlar kaydettikten sonra 1 dakika içinde alır'); ?></span>
        <button type="button" class="btn secondary sm" id="discardBtn"><?php _ex('Vazgeç', 'discard'); ?></button>
        <button type="button" class="btn sm" id="saveBtn"><?php _e('Kaydet'); ?></button>
    </div>
    <?php endif; ?>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const $ = (id) => document.getElementById(id);
    const canEdit = <?= $canEdit ? 'true' : 'false' ?>;
    // Kategori anahtarları ajanla ortak sözleşmedir (dns_categories / dns_domains); t ve d gösterilirken çevrilir
    const CATS = [
        { key: 'pornografi', t: 'Pornografik içerik', d: 'Yetişkinlere yönelik siteler' },
        { key: 'yasadisi_bahis', t: 'Yasadışı bahis ve kumar', d: 'Lisanssız bahis ve kumar siteleri' },
        { key: 'teror_siddet', t: 'Terör ve şiddet propagandası', d: 'Radikal örgüt ve şiddet içerikli siteler' },
        { key: 'zararli_yazilim', t: 'Zararlı yazılım ve kimlik avı', d: 'Malware, phishing ve komuta-kontrol alan adları' },
        { key: 'okul_ozel', t: 'Okulun özel listesi', d: 'Okulunuzun ayrıca izlemek istediği alan adları' },
    ];
    // Örnek metin arayüzün dilinde eklenir; yönetici düzenleyip kaydeder (sözlük anahtarı tek parça olmalı)
    const DEFAULT_TR = 'Bu bilgisayar okulumuz tarafından POps ile yönetilmektedir. Bilgisayarın açık/kapalı durumu, donanım bilgileri ve belirlenen yasaklı alan adlarına erişim kaydedilir. Klavye, şifre ve sayfa içerikleri kaydedilmez. Yönetici ekranı yalnızca kayıtlı bir oturumda görüntüleyebilir; uzaktan kontrol için ekranda bildirim gösterilir. Ayrıntılı bilgi için okul yönetiminin KVKK aydınlatma metnine bakınız.';
    const DEFAULT_TEXT = POps.t(DEFAULT_TR);
    let policy = null;      // sunucudaki (kayıtlı) politika
    let savedSig = '';
    let fleet = null;       // { ok, total }: DNS tespitini çalıştırabilen ajan sayısı
    let meta = null;        // { updated_by, updated_at }: politikayı en son kim, ne zaman değiştirdi
    let saving = false;

    const lines = (t) => t.split(/[\s,;]+/).map(s => s.trim()).filter(Boolean);
    const catRow = (key) => document.querySelector(`#cats .srow[data-key="${key}"]`);
    const catBody = (key) => $('cat-' + key);

    // Satırlar bir kez kurulur; değerler DOM üzerinden yazılır (sunucu verisi HTML'e girmez)
    function buildCats() {
        const ex = POps.t('ornek.com');
        const hintHtml = POps.tHtml('Her satıra bir alan adı. Alt alan adları kendiliğinden dahildir: {domain} yazınca {sub} da eşleşir. {scheme}, yol ve {wild} kaydederken temizlenir.', null,
            { domain: `<code>${escapeHtml(ex)}</code>`, sub: `<code>${escapeHtml('www.' + ex)}</code>`, scheme: '<code>https://</code>', wild: '<code>*.</code>' });
        $('cats').innerHTML = CATS.map(c => `<div class="srow" data-key="${escapeHtml(c.key)}">
                <button type="button" class="cat-open" aria-expanded="false" aria-controls="cat-${escapeHtml(c.key)}">
                    <div><div class="t">${escapeHtml(POps.t(c.t))}</div><div class="d">${escapeHtml(POps.t(c.d))}<span class="cnt"></span></div></div>
                    <span class="word warn" data-empty hidden>${POps.tHtml('Liste boş')}</span>
                    ${POps.iconHtml('right', 'sm chev')}
                </button>
                <label class="switch"><input type="checkbox" class="cat-on" aria-label="${escapeHtml(POps.t(c.t))}" ${canEdit ? '' : 'disabled'}><span></span></label>
            </div>
            <div class="cat-body" id="cat-${escapeHtml(c.key)}" hidden>
                <textarea class="domains" rows="6" spellcheck="false" aria-label="${escapeHtml(POps.t('{category} alan adları', { category: POps.t(c.t) }))}" ${canEdit ? '' : 'readonly'} placeholder="${escapeHtml(POps.t('ornek-site.com\nbaska-site.net'))}"></textarea>
                <div class="hint">${hintHtml}</div>
            </div>`).join('');
    }
    function setOpen(key, open) {
        catBody(key).hidden = !open;
        catRow(key).querySelector('.cat-open').setAttribute('aria-expanded', open ? 'true' : 'false');
    }

    function fill(p) {
        $('fairUseText').value = p.fair_use_text || '';
        $('autoQ').checked = !!p.auto_quarantine;
        $('qThreshold').value = p.quarantine_threshold || 3;
        const on = new Set(p.dns_categories || []);
        const dom = p.dns_domains || {};
        CATS.forEach(c => {
            catRow(c.key).querySelector('.cat-on').checked = on.has(c.key);
            catBody(c.key).querySelector('textarea').value = (dom[c.key] || []).join('\n');
        });
    }

    function collect() {
        const cats = [], domains = {};
        CATS.forEach(c => {
            if (catRow(c.key).querySelector('.cat-on').checked) cats.push(c.key);
            domains[c.key] = lines(catBody(c.key).querySelector('textarea').value);
        });
        return {
            fair_use_text: $('fairUseText').value.trim(),
            dns_categories: cats,
            auto_quarantine: $('autoQ').checked,
            quarantine_threshold: Math.max(1, Math.min(100, parseInt($('qThreshold').value, 10) || 3)),
            dns_domains: domains,
        };
    }
    const isDirty = () => canEdit && policy !== null && JSON.stringify(collect()) !== savedSig;

    // Formun anlık hali: sayılar, boş liste uyarısı, eşik satırı ve kaydet çubuğu
    function refresh() {
        const p = collect();
        CATS.forEach(c => {
            const row = catRow(c.key);
            const n = p.dns_domains[c.key].length, on = p.dns_categories.includes(c.key);
            // Açık ve boş liste sağda uyarı olarak görünür; aynı bilgi açıklamada tekrarlanmaz
            row.querySelector('.cnt').textContent = n ? ' · ' + POps.tn('{n} alan adı', n) : (on ? '' : ' · ' + POps.t('liste boş'));
            row.querySelector('[data-empty]').hidden = !(on && !n);
            row.classList.toggle('is-off', !on);
        });
        $('qRow').classList.toggle('is-off', !p.auto_quarantine);
        if (canEdit) $('qThreshold').disabled = !p.auto_quarantine;
        const len = $('fairUseText').value.trim().length;
        $('fairCount').textContent = len ? POps.t('{len}/4000 karakter', { len }) : POps.t('Boş bırakılırsa gösterilmez.');
        if (canEdit) $('saveBar').hidden = !isDirty();
    }

    // Başlık özeti: ajanlara giden (kayıtlı) politika
    function renderSummary() {
        if (!policy) return;
        const p = policy;
        const dom = p.dns_domains || {};
        const active = (p.dns_categories || []).filter(k => (dom[k] || []).length);
        const nDomains = active.reduce((a, k) => a + dom[k].length, 0);
        const fair = String(p.fair_use_text || '').trim();
        // Sayı kalın: yer tutucuya HTML parçası (POps.tHtml / tnHtml'in son argümanı)
        const boldHtml = (n) => `<b>${Number(n)}</b>`;
        const sumHtml = `<span class="sum">${POps.tHtml('Kurum geneli')}</span>`
            + (fair ? `<span class="sum"><span class="dot ok"></span>${POps.tHtml('Aydınlatma metni gösteriliyor')}</span>` : `<span class="sum"><span class="dot off"></span>${POps.tHtml('Aydınlatma metni yok')}</span>`)
            + (active.length ? `<span class="sum"><span class="dot ok"></span>${POps.tnHtml('{n} kategori', active.length, null, { n: boldHtml(active.length) })} · ${POps.tnHtml('{n} alan adı izleniyor', nDomains, null, { n: boldHtml(nDomains) })}</span>` : `<span class="sum"><span class="dot off"></span>${POps.tHtml('DNS tespiti kapalı')}</span>`)
            + (p.auto_quarantine ? `<span class="sum"><span class="dot ok"></span>${POps.tHtml('Otomatik karantina açık · eşik {n}', null, { n: boldHtml(p.quarantine_threshold) })}</span>` : `<span class="sum"><span class="dot off"></span>${POps.tHtml('Otomatik karantina kapalı')}</span>`)
            + (fleet && fleet.total ? `<span class="sum">${POps.tHtml('{count} ajan destekliyor', null, { count: `<b>${Number(fleet.ok)}/${Number(fleet.total)}</b>` })}</span>` : '')
            + (meta && meta.updated_at ? `<span class="sum faint">${POps.tHtml('Son değişiklik: {who} · {time}', { who: meta.updated_by || '?' }, { time: POps.timeHtml(meta.updated_at) })}</span>` : '')
            + (canEdit ? '' : `<span class="sum">${POps.tHtml('Yalnızca görüntüleme')}</span>`);
        $('polSummary').innerHTML = sumHtml;
    }

    async function load() {
        try {
            policy = await POps.get('/api/agent_policies');
        } catch (e) {
            // Politika okunamazken form gösterilmez: boş formu kaydetmek kayıtlı listeleri silerdi
            $('polForm').hidden = true;
            $('polError').hidden = false;
            $('polSummary').textContent = '';
            POps.setError($('polError'), e);
            return false;
        }
        if (!policy || typeof policy !== 'object') policy = {};
        fill(policy);
        savedSig = JSON.stringify(collect());
        $('polError').hidden = true;
        $('polForm').hidden = false;
        refresh();
        renderSummary();
        POps.get('/api/agent_policies/meta').then(m => { meta = m; renderSummary(); }).catch(() => { /* eski sunucu: satır gösterilmez */ });
        return true;
    }

    // Kaç ajan DNS tespitini çalıştırabilir (0.1.5-alpha ve sonrası)
    async function loadFleet() {
        try {
            const devs = await POps.get('/api/devices');
            if (!Array.isArray(devs)) return;
            const ver = (v) => { const m = String(v || '').match(/(\d+)\.(\d+)\.(\d+)/); return m ? [+m[1], +m[2], +m[3]] : null; };
            const ok = devs.filter(d => {
                const v = ver(d.agent_version);
                if (!v || /stable/i.test(d.agent_version)) return false;   // "v1.0.0 Stable" eski, sürümsüz derleme
                return v[0] > 0 || v[1] > 1 || (v[1] === 1 && v[2] >= 5);
            }).length;
            fleet = { ok, total: devs.length };
            const note = $('fleetNote');
            note.replaceChildren();
            if (devs.length && ok < devs.length) note.append(POps.el('span', { className: 'dot warn' }));
            note.append(document.createTextNode(devs.length
                ? POps.t('0.1.5 ve sonrası ajanlarda çalışır · şu an {ok}/{total} ajan', { ok, total: devs.length })
                : POps.t('0.1.5 ve sonrası ajanlarda çalışır.')));
            renderSummary();
        } catch (e) { /* sessiz: yalnızca bilgi satırı */ }
    }

    // Modül ayarları (kurum ya da sınıf bazında): DNS politikası ve karantina bir sınıfta kapalıysa
    // politika o sınıfta işlemez. Burada yalnızca gösterilir.
    async function loadModules() {
        let r;
        try { r = await POps.get('/api/modules'); } catch (e) { return; }
        const mods = {};
        (r && Array.isArray(r.modules) ? r.modules : []).forEach(m => { mods[m.id] = m; });
        const unassigned = (POps.dev && POps.dev.UNASSIGNED) || 'Atanmamis_Cihazlar';
        const labName = (l) => l === unassigned ? POps.t('Atanmamış') : l;
        function note(m, offText) {
            if (!m) return null;
            const labs = Object.keys(m.lab_enabled || {});
            const on = labs.filter(l => m.lab_enabled[l]).map(labName);
            const off = labs.filter(l => !m.lab_enabled[l]).map(labName);
            if (!m.enabled && !on.length) return { kind: 'upd', icon: 'alert', text: offText };
            // Modül adı sunucudan Türkçe gelir (DNS politikası, Karantina): bilinenler çevrilir
            if (!m.enabled) return { kind: 'lock', icon: 'info', text: POps.t('{module} modülü yalnızca şu sınıflarda açık: {labs}.', { module: POps.t(m.name), labs: on.join(', ') }) };
            if (off.length) return { kind: 'lock', icon: 'info', text: POps.t('{module} modülü şu sınıflarda kapalı: {labs}. Bu sınıflarda politika işlemez.', { module: POps.t(m.name), labs: off.join(', ') }) };
            return null;
        }
        function show(box, n) {
            box.replaceChildren();
            if (n) box.append(POps.el('div', { className: 'issue ' + n.kind }, [POps.iconEl(n.icon, 'sm'), POps.el('span', { text: n.text })]));
        }
        show($('dnsNotes'), note(mods.dns_policy, POps.t('DNS politikası modülü kapalı: listeler ajanlara gönderilmiyor ve tespit yapılmıyor.')));
        show($('qNotes'), note(mods.quarantine, POps.t('Karantina modülü kapalı: otomatik karantina çalışmıyor.')));
    }

    async function save() {
        if (saving || !isDirty()) return;
        const p = collect();
        if (p.auto_quarantine && !p.dns_categories.some(k => (p.dns_domains[k] || []).length)) {
            const ok = await POps.confirm({ title: POps.t('Yine de kaydedilsin mi?'), message: POps.t('Otomatik karantina açık ama listesi dolu açık bir kategori yok; karantina hiç tetiklenmez.'), confirmText: POps.t('Yine de kaydet') });
            if (!ok) return;
        }
        saving = true;
        const ok = await POps.act($('saveBtn'), () => POps.post('/api/agent_policies', p), { success: POps.t('Politikalar kaydedildi. Ajanlar 1 dakika içinde alır.'), error: POps.t('Kaydedilemedi.') });
        saving = false;
        if (ok) await load();
        else refresh();
    }
    function discard() {
        if (!policy) return;
        fill(policy);
        refresh();
    }

    // ---- Etkileşim
    buildCats();
    // Bağlantı cümlenin içinde; yeri dile göre değişir
    $('qReleaseNote').append(...POps.tNodes('Karantina {link} sayfasından kaldırılır ya da bilgisayarda çevrimdışı açma koduyla kalkar.', null,
        { link: POps.el('a', { href: 'devices', text: POps.t('Cihazlar') }) }));
    $('cats').addEventListener('click', (e) => {
        const b = e.target.closest('.cat-open');
        if (!b) return;
        const key = b.closest('.srow').dataset.key;
        setOpen(key, catBody(key).hidden);
    });
    $('cats').addEventListener('change', (e) => {
        if (!e.target.classList.contains('cat-on')) return;
        const key = e.target.closest('.srow').dataset.key;
        // Açılan kategorinin listesi boşsa doldurulsun diye liste açılır
        if (e.target.checked && !lines(catBody(key).querySelector('textarea').value).length) {
            setOpen(key, true);
            catBody(key).querySelector('textarea').focus();
        }
        refresh();
    });
    $('cats').addEventListener('input', refresh);
    if (canEdit) {
        $('fairUseText').addEventListener('input', refresh);
        $('autoQ').addEventListener('change', refresh);
        $('qThreshold').addEventListener('input', refresh);
        $('fairDefault').addEventListener('click', async () => {
            const cur = $('fairUseText').value.trim();
            if (cur && cur !== DEFAULT_TEXT && cur !== DEFAULT_TR
                && !await POps.confirm({ title: POps.t('Metin örnek metinle değiştirilsin mi?'), message: POps.t('Şu anki metnin yerine örnek metin yazılır. Kaydetmeden önce düzenleyebilirsiniz.'), confirmText: POps.t('Metni değiştir') })) return;
            $('fairUseText').value = DEFAULT_TEXT;
            refresh();
            $('fairUseText').focus();
        });
        $('saveBtn').addEventListener('click', save);
        $('discardBtn').addEventListener('click', discard);
        document.addEventListener('keydown', (e) => {
            if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S') && isDirty()) { e.preventDefault(); save(); }
        });
        // Kaydedilmemiş değişiklikle sayfadan çıkarken sorulur
        window.addEventListener('beforeunload', (e) => {
            if (!isDirty()) return;
            e.preventDefault();
            e.returnValue = '';
        });
    }
    load().then(ok => { if (ok) { loadFleet(); loadModules(); } });
})();
</script>
