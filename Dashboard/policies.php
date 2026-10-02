<?php include 'includes/header.php'; ?>
<?php $canEdit = ($_SESSION['role'] ?? '') !== 'viewer'; ?>

<style>
    .pol-wrap { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(620px, 100%), 1fr)); gap: var(--space-5); align-items: start; padding: var(--space-2) 0; }
    .pol-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-6); box-shadow: var(--shadow-sm); }
    .pol-head { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; flex-wrap: wrap; margin-bottom: var(--space-3); }
    .pol-head h2 { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin: 0; display: flex; align-items: center; gap: 0.5rem; }
    .pol-head h2 i { color: var(--primary-500); }
    .pol-desc { color: var(--text-tertiary); font-size: var(--text-sm); margin: 0 0 var(--space-4); line-height: 1.55; }
    .badge { display: inline-flex; align-items: center; gap: 0.375rem; padding: 0.3rem 0.7rem; border-radius: 999px; font-size: var(--text-xs); font-weight: var(--fw-semibold); white-space: nowrap; }
    .badge.ok { background: var(--success-bg); color: var(--success-text); }
    .badge.warn { background: var(--warning-bg); color: var(--warning-text); }
    .badge.muted { background: var(--bg-surface-2); color: var(--text-tertiary); }

    .status-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: var(--space-3); }
    .status-tile { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-4); }
    .status-tile .l { font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); }
    .status-tile .v { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin-top: 0.3rem; display: flex; align-items: center; gap: 0.4rem; }
    .status-tile .s { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.3rem; line-height: 1.45; }

    textarea.fld, input.fld { width: 100%; padding: 0.625rem 0.75rem; border: 1px solid var(--border-default); border-radius: var(--radius-md); background: var(--bg-surface-2); color: var(--text-primary); font-size: var(--text-sm); }
    textarea.fld { min-height: 110px; resize: vertical; line-height: 1.5; }
    textarea.domains { font-family: var(--font-mono); font-size: 0.8125rem; min-height: 90px; }
    .row { display: flex; align-items: center; gap: 0.75rem; flex-wrap: wrap; }
    .muted { color: var(--text-tertiary); font-size: var(--text-sm); }

    .cat { border: 1px solid var(--border-subtle); border-radius: var(--radius-md); margin-bottom: 0.625rem; background: var(--bg-surface); }
    .cat.on { border-color: var(--primary-500); }
    .cat-head { display: flex; align-items: center; gap: 0.75rem; padding: 0.75rem 0.875rem; cursor: pointer; }
    .cat-head .t { font-weight: var(--fw-semibold); color: var(--text-primary); font-size: var(--text-sm); }
    .cat-head .d { font-size: var(--text-xs); color: var(--text-tertiary); }
    .cat-head .cnt { margin-left: auto; }
    .cat-body { display: none; padding: 0 0.875rem 0.875rem; }
    .cat.open .cat-body { display: block; }
    .cat-body .hint { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.375rem; }
    .cat-head .chev { color: var(--text-tertiary); transition: transform 0.15s; }
    .cat.open .cat-head .chev { transform: rotate(90deg); }

    .switch { position: relative; width: 38px; height: 22px; flex: none; display: inline-block; }
    .switch input { opacity: 0; width: 0; height: 0; position: absolute; }
    .switch span { position: absolute; inset: 0; background: var(--border-default); border-radius: 999px; transition: background 0.15s; cursor: pointer; }
    .switch span::before { content: ''; position: absolute; width: 16px; height: 16px; left: 3px; top: 3px; background: #fff; border-radius: 50%; transition: transform 0.15s; }
    .switch input:checked + span { background: var(--primary-500); }
    .switch input:checked + span::before { transform: translateX(16px); }
    .switch input:disabled + span { opacity: 0.5; cursor: not-allowed; }

    .btn { padding: 0.625rem 1rem; border-radius: var(--radius-md); font-size: var(--text-sm); font-weight: var(--fw-semibold); cursor: pointer; border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); display: inline-flex; align-items: center; gap: 0.5rem; }
    .btn.primary { background: var(--primary-500); color: #fff; border-color: var(--primary-500); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .savebar { position: sticky; bottom: 0; background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-4) var(--space-5); display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; flex-wrap: wrap; box-shadow: var(--shadow-sm); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-shield-halved"></i> Politikalar</h1>
        <p>Kullanıcıya gösterilen aydınlatma metni ve yasaklı alan adlarına erişimin tespiti</p>
    </div>
</div>

<div class="pol-wrap">
    <!-- DURUM -->
    <div class="pol-card">
        <div class="pol-head"><h2><i class="fas fa-circle-info"></i> Ajanlarda durum</h2></div>
        <div class="status-grid">
            <div class="status-tile">
                <div class="l">Aydınlatma metni</div>
                <div class="v" id="stFair">…</div>
                <div class="s">Ajan açılınca kullanıcıya tepsiden gösterilir; kullanıcı onaylayana kadar dakikada bir yeniden sorulur.</div>
            </div>
            <div class="status-tile">
                <div class="l">DNS ihlal tespiti</div>
                <div class="v" id="stDns">…</div>
                <div class="s" id="stDnsSub">Tespit, 0.1.5-alpha ve sonrası ajanlarda çalışır.</div>
            </div>
            <div class="status-tile">
                <div class="l">Otomatik karantina</div>
                <div class="v" id="stQ">…</div>
                <div class="s">Yalnızca DNS tespiti çalışan ajanlarda etkilidir.</div>
            </div>
        </div>
    </div>

    <!-- AYDINLATMA METNİ -->
    <div class="pol-card">
        <div class="pol-head"><h2><i class="fas fa-file-contract"></i> Aydınlatma ve adil kullanım metni</h2></div>
        <p class="pol-desc">
            Öğrenciye ya da personele bu bilgisayarın okul tarafından yönetildiğini, neyin kaydedildiğini ve neyin
            kaydedilmediğini anlatan kısa metin. Boş bırakılırsa gösterilmez. Kurumunuzun KVKK aydınlatma metnine
            bağlantı vermeniz önerilir (şablon: <code>docs/kvkk-aydinlatma.md</code>).
        </p>
        <textarea class="fld" id="fairUseText" maxlength="4000" <?= $canEdit ? '' : 'disabled' ?> placeholder="Örn: Bu bilgisayar okul tarafından yönetilmektedir…"></textarea>
        <?php if ($canEdit): ?>
        <div class="row" style="margin-top:0.625rem;">
            <button class="btn" type="button" id="fairDefault"><i class="fas fa-wand-magic-sparkles"></i> Örnek metni kullan</button>
            <span class="muted" id="fairCount"></span>
        </div>
        <?php endif; ?>
    </div>

    <!-- DNS -->
    <div class="pol-card">
        <div class="pol-head"><h2><i class="fas fa-filter"></i> Yasaklı alan adı tespiti (DNS)</h2></div>
        <p class="pol-desc">
            Ajan, bilgisayarın DNS önbelleğine bakar ve açık bir kategorinin listesinde <strong>birebir</strong> geçen
            alan adını ya da onun alt alan adını görürse ihlal kaydeder ve yöneticilere bildirir. Sayfa içeriği, arama,
            klavye ya da şifre kaydedilmez. <strong>Engelleme yapılmaz</strong>; engelleme için okulun ağ filtresini
            kullanın. Açık bir kategorinin listesi boşsa o kategoride hiçbir şey işaretlenmez.
        </p>
        <div id="cats"></div>
    </div>

    <!-- KARANTİNA -->
    <div class="pol-card">
        <div class="pol-head">
            <h2><i class="fas fa-biohazard"></i> Otomatik karantina</h2>
            <label class="switch" title="Otomatik karantina"><input type="checkbox" id="autoQ" <?= $canEdit ? '' : 'disabled' ?>><span></span></label>
        </div>
        <p class="pol-desc">
            Açıkken, bir bilgisayarda (ajan son açıldığından beri) belirlenen sayıda ihlal tespit edilince o bilgisayar
            kendini ağdan yalıtır: yalnızca POps sunucusuna, DNS ve DHCP'ye erişebilir. Yalıtım, <strong>Cihazlar</strong>
            sayfasından karantina kaldırılarak ya da çevrimdışı bypass koduyla kalkar.
        </p>
        <div class="row">
            <label class="muted" for="qThreshold">Eşik (ihlal sayısı)</label>
            <input class="fld" type="number" id="qThreshold" min="1" max="100" value="3" style="width:110px;" <?= $canEdit ? '' : 'disabled' ?>>
        </div>
    </div>

    <?php if ($canEdit): ?>
    <div class="savebar">
        <span class="muted" id="saveState">Değişiklik yok.</span>
        <button class="btn primary" id="saveBtn" disabled><i class="fas fa-floppy-disk"></i> Kaydet</button>
    </div>
    <?php else: ?>
    <div class="muted" style="text-align:center;">Bu sayfayı yalnızca görüntüleyebilirsiniz.</div>
    <?php endif; ?>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const $ = (id) => document.getElementById(id);
    const canEdit = <?= $canEdit ? 'true' : 'false' ?>;
    // Kategori anahtarları ajanla ortak sözleşmedir (dns_categories / dns_domains)
    const CATS = [
        { key: 'pornografi', t: 'Pornografik içerik', d: 'Yetişkinlere yönelik siteler' },
        { key: 'yasadisi_bahis', t: 'Yasadışı bahis ve kumar', d: 'Lisanssız bahis ve kumar siteleri' },
        { key: 'teror_siddet', t: 'Terör ve şiddet propagandası', d: 'Radikal örgüt ve şiddet içerikli siteler' },
        { key: 'zararli_yazilim', t: 'Zararlı yazılım ve kimlik avı', d: 'Malware, phishing ve komuta-kontrol alan adları' },
        { key: 'okul_ozel', t: 'Okulun özel listesi', d: 'Okulunuzun ayrıca izlemek istediği alan adları' },
    ];
    const DEFAULT_TEXT = 'Bu bilgisayar okulumuz tarafından POps ile yönetilmektedir. Bilgisayarın açık/kapalı durumu, ' +
        'donanım bilgileri ve belirlenen yasaklı alan adlarına erişim kaydedilir. Klavye, şifre ve sayfa içerikleri kaydedilmez. ' +
        'Yönetici ekranı yalnızca kayıtlı bir oturumda görüntüleyebilir; uzaktan kontrol için ekranda bildirim gösterilir. ' +
        'Ayrıntılı bilgi için okul yönetiminin KVKK aydınlatma metnine bakınız.';
    let saved = '';
    let policy = {};

    const lines = (t) => t.split(/[\s,;]+/).map(s => s.trim()).filter(Boolean);
    function renderCats() {
        const cats = new Set(policy.dns_categories || []);
        const domains = policy.dns_domains || {};
        $('cats').innerHTML = CATS.map(c => {
            const list = domains[c.key] || [];
            const on = cats.has(c.key);
            return `<div class="cat${on ? ' on' : ''}" data-key="${c.key}">
                <div class="cat-head">
                    <label class="switch" onclick="event.stopPropagation()"><input type="checkbox" class="cat-on" ${on ? 'checked' : ''} ${canEdit ? '' : 'disabled'}><span></span></label>
                    <div><div class="t">${escapeHtml(c.t)}</div><div class="d">${escapeHtml(c.d)}</div></div>
                    <span class="cnt"></span>
                    <i class="fas fa-chevron-right chev"></i>
                </div>
                <div class="cat-body">
                    <textarea class="fld domains" ${canEdit ? '' : 'disabled'} placeholder="ornek-bahis.com&#10;baska-site.net">${escapeHtml(list.join('\n'))}</textarea>
                    <div class="hint">Her satıra bir alan adı. Alt alan adları kendiliğinden dahildir (<code>ornek.com</code> yazınca <code>www.ornek.com</code> da eşleşir). <code>https://</code>, yol ve <code>*.</code> kaydederken temizlenir.</div>
                </div>
            </div>`;
        }).join('');
        document.querySelectorAll('.cat').forEach(el => {
            el.querySelector('.cat-head').addEventListener('click', () => el.classList.toggle('open'));
            el.querySelector('.cat-on').addEventListener('change', (e) => { el.classList.toggle('on', e.target.checked); if (e.target.checked) el.classList.add('open'); refresh(); });
            el.querySelector('textarea').addEventListener('input', refresh);
        });
    }

    function collect() {
        const cats = [], domains = {};
        document.querySelectorAll('.cat').forEach(el => {
            if (el.querySelector('.cat-on').checked) cats.push(el.dataset.key);
            domains[el.dataset.key] = lines(el.querySelector('textarea').value);
        });
        return {
            fair_use_text: $('fairUseText').value.trim(),
            dns_categories: cats,
            auto_quarantine: $('autoQ').checked,
            quarantine_threshold: Math.max(1, Math.min(100, parseInt($('qThreshold').value) || 3)),
            dns_domains: domains,
        };
    }

    function refresh() {
        const p = collect();
        document.querySelectorAll('.cat').forEach(el => {
            const n = lines(el.querySelector('textarea').value).length, on = el.querySelector('.cat-on').checked;
            el.querySelector('.cnt').innerHTML = !on ? '<span class="badge muted">kapalı</span>'
                : n ? `<span class="badge ok">${n} alan adı</span>` : '<span class="badge warn">liste boş</span>';
        });
        const activeCats = p.dns_categories.filter(k => (p.dns_domains[k] || []).length);
        $('stFair').innerHTML = p.fair_use_text ? '<span class="badge ok"><i class="fas fa-check"></i> Tanımlı</span>' : '<span class="badge muted">Boş (gösterilmez)</span>';
        $('stDns').innerHTML = activeCats.length ? `<span class="badge ok"><i class="fas fa-check"></i> ${activeCats.length} kategori, ${activeCats.reduce((a, k) => a + p.dns_domains[k].length, 0)} alan adı</span>`
            : '<span class="badge muted">Kapalı (listeli açık kategori yok)</span>';
        $('stQ').innerHTML = p.auto_quarantine ? `<span class="badge warn"><i class="fas fa-lock"></i> Açık · eşik ${p.quarantine_threshold}</span>` : '<span class="badge muted">Kapalı</span>';
        if (!canEdit) return;
        $('fairCount').textContent = `${p.fair_use_text.length}/4000 karakter`;
        const dirty = JSON.stringify(p) !== saved;
        $('saveBtn').disabled = !dirty;
        $('saveState').textContent = dirty ? 'Kaydedilmemiş değişiklik var. Ajanlar yeni politikayı bir dakika içinde alır.' : 'Değişiklik yok.';
    }

    async function load() {
        try {
            const res = await fetch('/api/agent_policies');
            policy = res.ok ? await res.json() : {};
        } catch (e) { policy = {}; }
        $('fairUseText').value = policy.fair_use_text || '';
        $('autoQ').checked = !!policy.auto_quarantine;
        $('qThreshold').value = policy.quarantine_threshold || 3;
        renderCats();
        saved = JSON.stringify(collect());
        refresh();
        loadFleet();
    }

    // Kaç ajan DNS tespitini çalıştırabilir (0.1.5-alpha ve sonrası)
    async function loadFleet() {
        try {
            const devs = await (await fetch('/api/devices')).json();
            const ver = (v) => { const m = String(v || '').match(/(\d+)\.(\d+)\.(\d+)/); return m ? [+m[1], +m[2], +m[3]] : null; };
            const ok = devs.filter(d => {
                const v = ver(d.agent_version);
                if (!v || /stable/i.test(d.agent_version)) return false;   // "v1.0.0 Stable" eski, sürümsüz derleme
                return v[0] > 0 || v[1] > 1 || (v[1] === 1 && v[2] >= 5);
            }).length;
            $('stDnsSub').textContent = `Tespit, 0.1.5-alpha ve sonrası ajanlarda çalışır: şu an ${ok}/${devs.length} ajan.`;
        } catch (e) { /* sessiz */ }
    }

    if (canEdit) {
        $('fairUseText').addEventListener('input', refresh);
        $('autoQ').addEventListener('change', refresh);
        $('qThreshold').addEventListener('input', refresh);
        $('fairDefault').addEventListener('click', async () => {
            if ($('fairUseText').value.trim() && !await POps.confirm({ title: 'Metin değiştirilsin mi?', message: 'Mevcut metin örnek metinle değiştirilecek (kaydetmeden önce düzenleyebilirsiniz).', confirmText: 'Değiştir' })) return;
            $('fairUseText').value = DEFAULT_TEXT; refresh();
        });
        $('saveBtn').addEventListener('click', async function () {
            const p = collect();
            if (p.auto_quarantine && !p.dns_categories.some(k => (p.dns_domains[k] || []).length)
                && !await POps.confirm({ title: 'Yine de kaydedilsin mi?', message: 'Otomatik karantina açık ama alan adı listesi olan açık bir kategori yok; karantina hiç tetiklenmez.', confirmText: 'Kaydet' })) return;
            this.disabled = true;
            try {
                await POps.post('/api/agent_policies', p);
                POps.toast('success', 'Politikalar kaydedildi; ajanlar bir dakika içinde alır.');
                await load();
            } catch (e) { showToast('Kaydedilemedi: ' + e.message, 'error'); refresh(); }
        });
    }
    load();
})();
</script>
