<?php include 'includes/header.php'; ?>
<?php
$isSuper = ($_SESSION['role'] ?? '') === 'superadmin';
// Kullanıcının açabileceği sayfalar (kontrol merkezi herkese açık, Sistem yalnızca süper admin)
$permPages = [
    'devices' => 'Cihazlar', 'labs' => 'Sınıflar', 'tasks' => 'İşlemler', 'terminal' => 'Uzak komut',
    'vision' => 'Uzak ekran', 'deploy' => 'Dağıtım', 'policies' => 'Politikalar', 'logger' => 'Kayıtlar',
    'reports' => 'Raporlar', 'helpdesk' => 'Destek talepleri', 'settings' => 'Ayarlar',
];
$viewerBlocked = ['deploy', 'settings', 'terminal'];   // includes/header.php ile aynı
?>

<style>
    /* Ayar bölümü: solda başlık ve kısa açıklama, sağda ayar satırları; dar ekranda alt alta */
    .sects > .sect { display: grid; grid-template-columns: minmax(200px, 300px) minmax(0, 1fr); gap: 14px 48px; padding: 28px 0; border-top: 1px solid var(--border-subtle); }
    .sects > .sect:first-child { border-top: 0; padding-top: 4px; }
    .set-tabs { margin-bottom: 8px; }
    .sects > .sect[hidden] { display: none; }
    /* Sekmede görünen ilk bölüm üst çizgisiz başlar */
    .sects > .sect.first-visible { border-top: 0; padding-top: 12px; }
    .sect-head h2 { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .sect-head p { font-size: var(--text-sm); color: var(--text-tertiary); line-height: 1.55; margin-top: 6px; }
    .sect-body { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
    .srow.block { display: block; }
    .srow .val { font-family: var(--font-mono); font-size: 12.5px; color: var(--text-secondary); text-align: right; overflow-wrap: anywhere; min-width: 0; max-width: 60%; }
    .srow .st { display: inline-flex; align-items: center; gap: 7px; font-size: var(--text-sm); color: var(--text-secondary); white-space: nowrap; }
    .srow input.org-name { width: min(280px, 100%); }
    .org-logo { width: 44px; height: 44px; object-fit: contain; border-radius: 10px; background: var(--bg-surface); box-shadow: 0 0 0 1px var(--border-subtle); padding: 4px; }
    .srow input[type=number] { width: 84px; text-align: right; font-variant-numeric: tabular-nums; }
    .srow .unit { font-size: var(--text-sm); color: var(--text-tertiary); }
    .faint { color: var(--text-muted); }

    /* Kullanıcılar */
    .user-table tbody tr { cursor: pointer; }
    .user-table tbody tr.is-focus { background: var(--primary-50); }
    .user-table td { padding-top: 11px; padding-bottom: 11px; white-space: nowrap; }
    body.drawer-open .user-table .col-access { display: none; }   /* panel açıkken tablo daralır; erişim panelde yazar */
    .user-table .nm { display: flex; align-items: center; gap: 10px; font-weight: var(--fw-semibold); min-width: 0; }
    .user-table .nm > span:last-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .av { width: 28px; height: 28px; border-radius: 99px; background: var(--bg-surface-3); color: var(--text-secondary); display: inline-flex; align-items: center; justify-content: center; font-size: 12px; font-weight: var(--fw-semibold); flex: none; }
    .you { font-size: var(--text-xs); color: var(--text-muted); font-weight: var(--fw-regular); margin-left: 6px; }
    .user-table td.when { color: var(--text-tertiary); white-space: nowrap; }

    /* Kullanıcı paneli: işlem listesi */
    .uact { overflow: hidden; }
    .uact .srow { width: 100%; text-align: left; font-size: var(--text-sm); color: var(--text-primary); padding: 12px 14px; gap: 12px; }
    .uact .srow:hover { background: var(--bg-surface-2); }
    .uact .srow .ico { color: var(--text-tertiary); }
    .uact .srow .grow { flex: 1; }
    .uact .srow.danger, .uact .srow.danger .ico { color: var(--danger-text); }
    .uact .srow.danger:hover { background: var(--danger-bg); }
    .drawer .dnote { font-size: var(--text-xs); color: var(--text-muted); line-height: 1.5; }

    /* 2FA kurulumu */
    .tf-setup { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 20px 24px; align-items: start; }
    .tf-qr { background: #fff; padding: 10px; border-radius: 12px; box-shadow: 0 0 0 1px var(--border-subtle); min-width: 200px; min-height: 200px; display: flex; align-items: center; justify-content: center; font-size: var(--text-xs); color: var(--text-muted); text-align: center; }
    .tf-steps { display: flex; flex-direction: column; gap: 14px; min-width: 0; }
    .tf-step { display: flex; gap: 10px; align-items: baseline; font-size: var(--text-sm); color: var(--text-primary); }
    .tf-step .n { width: 20px; height: 20px; border-radius: 99px; background: var(--bg-surface-3); color: var(--text-secondary); font-size: 11px; font-weight: var(--fw-semibold); display: inline-flex; align-items: center; justify-content: center; flex: none; }
    .tf-steps .input-group input { font-family: var(--font-mono); }
    #twofaCode { max-width: 140px; letter-spacing: 0.12em; }

    /* Kullanıcı formu */
    .perm-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(150px, 1fr)); gap: 4px 12px; }
    .perm-grid .check { padding: 5px 0; }
    .perm-grid .check.is-blocked { color: var(--text-muted); cursor: not-allowed; }

    /* Kaydedilmemiş değişiklik çubuğu */
    .savebar { position: sticky; bottom: 16px; z-index: 50; width: fit-content; max-width: 100%; margin: 24px auto 0; display: flex; align-items: center; gap: 10px; padding: 7px 7px 7px 16px; background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle), 0 8px 28px rgba(0, 0, 0, 0.10); font-size: var(--text-sm); color: var(--text-primary); }
    .savebar .btn { margin-left: 2px; }
    @media (max-width: 960px) { .sects > .sect { grid-template-columns: minmax(0, 1fr); gap: 12px; padding: 22px 0; } }
    @media (max-width: 640px) {
        .hide-sm { display: none; }
        .tf-setup { grid-template-columns: minmax(0, 1fr); }
        .tf-qr { justify-self: center; }
        .savebar { width: 100%; }
        .savebar .sb-text { flex: 1; }
    }
</style>

<div class="page-header">
    <div>
        <h1>Ayarlar</h1>
        <div class="summary">
            <span class="sum" id="sumUsers">Yükleniyor…</span>
            <span class="sum" id="sumConn" hidden></span>
            <span class="sum" id="sumTwofa" hidden></span>
        </div>
    </div>
    <?php if ($isSuper): ?>
    <div class="page-header-actions">
        <button type="button" class="btn" id="addUserBtn"><?php echo pops_icon('plus', 'sm'); ?>Kullanıcı ekle</button>
    </div>
    <?php endif; ?>
</div>

<div class="tabs set-tabs" id="setTabs" aria-label="Ayarlar bölümleri">
    <button type="button" class="tab" data-tab="users">Kullanıcılar</button>
    <button type="button" class="tab" data-tab="security">Güvenlik</button>
    <button type="button" class="tab" data-tab="general">Genel</button>
</div>

<div class="sects">
    <section class="sect" data-pane="users" aria-labelledby="hUsers">
        <div class="sect-head">
            <h2 id="hUsers">Kullanıcılar</h2>
            <p>Panele kimlerin girebileceği ve hangi sayfaları açabileceği. Ayrıntı ve işlemler için bir kullanıcıya tıklayın.<?php if (!$isSuper): ?> Kullanıcıları yalnızca süper admin ekler, düzenler ve siler.<?php endif; ?></p>
        </div>
        <div class="sect-body">
            <div class="table-wrap">
                <table class="data-table user-table">
                    <thead><tr><th>Kullanıcı</th><th>Rol</th><th class="hide-sm col-access">Erişim</th><th>Son giriş</th></tr></thead>
                    <tbody id="userBody"><tr><td colspan="4"><div class="loading-state" role="status"><span class="spinner"></span>Kullanıcılar yükleniyor…</div></td></tr></tbody>
                </table>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="security" id="twofaCard" aria-labelledby="hTwofa">
        <div class="sect-head">
            <h2 id="hTwofa">İki adımlı doğrulama</h2>
            <p>Girişte şifreye ek olarak doğrulama uygulamasından (Google Authenticator, Authy, Microsoft Authenticator) 6 haneli kod istenir. Yalnızca kendi hesabınız için geçerlidir.</p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t">Hesabınızda 2FA</div>
                        <div class="d" id="twofaDesc">Önerilir, zorunlu değildir.</div>
                    </div>
                    <span class="st" id="twofaState"><span class="spinner sm"></span></span>
                    <button type="button" class="btn secondary sm" id="twofaBtn" hidden></button>
                </div>
                <div class="srow block" id="twofaSetup" hidden>
                    <div class="tf-setup">
                        <div class="tf-qr" id="twofaQr" aria-label="2FA kurulum QR kodu"></div>
                        <div class="tf-steps">
                            <div class="tf-step"><span class="n">1</span><span>Doğrulama uygulamasında yeni hesap ekleyip QR kodu okutun.</span></div>
                            <div class="field" style="margin:0">
                                <label for="twofaSecret">QR okutamıyorsanız bu anahtarı elle girin</label>
                                <div class="input-group">
                                    <input type="text" id="twofaSecret" readonly spellcheck="false">
                                    <button type="button" class="ibtn boxed" id="twofaCopy" data-tip="Anahtarı kopyala" data-tip-pos="left" aria-label="Anahtarı kopyala"><?php echo pops_icon('copy'); ?></button>
                                </div>
                            </div>
                            <div class="tf-step"><span class="n">2</span><span>Uygulamanın gösterdiği 6 haneli kodu yazın.</span></div>
                            <div class="input-group">
                                <input type="text" id="twofaCode" inputmode="numeric" maxlength="6" placeholder="123456" autocomplete="one-time-code" aria-label="6 haneli kod">
                                <button type="button" class="btn" id="twofaEnableBtn">Etkinleştir</button>
                                <button type="button" class="btn ghost" id="twofaCancelBtn">Vazgeç</button>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="general" aria-labelledby="hOrg">
        <div class="sect-head">
            <h2 id="hOrg">Kurum</h2>
            <p>Giriş ekranında görünen ad ve logo. Yalnızca süper admin değiştirir.</p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t" id="orgNameLabel">Kurum adı</div>
                        <div class="d">Giriş ekranının başlığı olur. Boş bırakılırsa "POps" yazar.</div>
                    </div>
                    <input type="text" id="orgName" class="org-name" maxlength="80" autocomplete="organization" aria-labelledby="orgNameLabel" placeholder="Örn. Atatürk Anadolu Lisesi" disabled>
                    <button type="button" class="btn secondary sm" id="orgSave" disabled>Kaydet</button>
                </div>
                <div class="srow">
                    <div class="grow">
                        <div class="t">Logo</div>
                        <div class="d">PNG, JPEG ya da WebP, en çok 256 KB. Kare ya da yatay bir logo en iyi görünür.</div>
                    </div>
                    <img id="orgLogo" class="org-logo" alt="Kurum logosu" hidden>
                    <input type="file" id="orgLogoFile" accept="image/png,image/jpeg,image/webp" hidden>
                    <button type="button" class="btn secondary sm" id="orgLogoPick" disabled><?php echo pops_icon('upload', 'sm'); ?>Logo yükle</button>
                    <button type="button" class="ibtn sm" id="orgLogoDel" data-tip="Logoyu kaldır" data-tip-pos="left" aria-label="Logoyu kaldır" hidden><?php echo pops_icon('trash', 'sm'); ?></button>
                </div>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="general" aria-labelledby="hQueue">
        <div class="sect-head">
            <h2 id="hQueue">Görev kuyruğu</h2>
            <p>Dosya indirme ve kurulum gibi görevler ağ boğulmasın diye paketler halinde gönderilir.</p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t" id="queueLimitLabel">Eşzamanlı görev sınırı</div>
                        <div class="d">Aynı anda görev alan en çok bilgisayar sayısı. 1 Gbit ağda en çok 15 önerilir.</div>
                    </div>
                    <input type="number" id="queueLimit" min="1" max="200" inputmode="numeric" aria-labelledby="queueLimitLabel" disabled>
                    <span class="unit">bilgisayar</span>
                </div>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="general" aria-labelledby="hServer">
        <div class="sect-head">
            <h2 id="hServer">Sunucu bağlantısı</h2>
            <p>Adresler sunucudaki <code>.env</code> dosyasından okunur (<code>POPS_API_URL</code>, <code>POPS_API_INTERNAL_URL</code>) ve buradan değiştirilemez. Ayrıntı: <code>docs/configuration.md</code></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t">Bağlantı</div>
                        <div class="d" id="connDesc">Panelin merkez sunucuya ulaşıp ulaşmadığı</div>
                    </div>
                    <span class="st" id="connState"><span class="spinner sm"></span>Sınanıyor</span>
                    <button type="button" class="ibtn sm" id="connRetry" data-tip="Yeniden sına" data-tip-pos="left" aria-label="Bağlantıyı yeniden sına"><?php echo pops_icon('refresh', 'sm'); ?></button>
                </div>
                <div class="srow">
                    <div class="grow"><div class="t">REST API adresi</div></div>
                    <span class="val" id="dispHttpUrl">—</span>
                </div>
                <div class="srow">
                    <div class="grow"><div class="t">WebSocket adresi</div></div>
                    <span class="val" id="dispWsUrl">—</span>
                </div>
            </div>
        </div>
    </section>

    <div class="savebar" id="saveBar" role="region" aria-label="Kaydedilmemiş değişiklikler" hidden>
        <span class="dot warn" aria-hidden="true"></span>
        <span class="sb-text">Kaydedilmemiş değişiklik var</span>
        <button type="button" class="btn secondary sm" id="discardBtn">Vazgeç</button>
        <button type="button" class="btn sm" id="saveBtn">Kaydet</button>
    </div>
</div>

<?php if ($isSuper): ?>
<div class="modal-overlay" id="userModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="umTitle">Kullanıcı ekle</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x', 'sm'); ?></button>
        </div>
        <div class="modal-body">
            <div class="field" id="umNameField">
                <label for="umName">Kullanıcı adı</label>
                <input type="text" id="umName" maxlength="64" autocomplete="off" spellcheck="false">
                <div class="field-error">Kullanıcı adı girin.</div>
            </div>
            <div class="field" id="umPassField">
                <label for="umPass">Şifre</label>
                <input type="password" id="umPass" autocomplete="new-password">
                <div class="field-error">Şifre girin.</div>
                <div class="field-hint">En az 8 karakter önerilir.</div>
            </div>
            <div class="field">
                <span class="field-label">Rol</span>
                <div class="segmented block" id="umRole" role="group" aria-label="Rol">
                    <button type="button" data-role="viewer" aria-pressed="false">İzleyici</button>
                    <button type="button" data-role="admin" aria-pressed="false">Yönetici</button>
                    <button type="button" data-role="superadmin" aria-pressed="false">Süper admin</button>
                </div>
                <div class="field-hint" id="umRoleHint"></div>
            </div>
            <div class="field" id="umPerms">
                <span class="field-label">Açabileceği sayfalar</span>
                <div class="perm-grid">
                    <?php foreach ($permPages as $key => $label): ?>
                    <label class="check" data-page="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>"><input type="checkbox" class="perm-cb" value="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>"><?php echo htmlspecialchars($label, ENT_QUOTES, 'UTF-8'); ?></label>
                    <?php endforeach; ?>
                </div>
                <div class="field-hint" id="umPermsHint">Kontrol merkezi herkese açıktır.</div>
            </div>
            <div class="alert info" id="umSelf" hidden><div>Kendi hesabınızı değiştirince yeniden giriş yapmanız gerekir.</div></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="umSave">Kullanıcıyı ekle</button>
        </div>
    </div>
</div>
<?php endif; ?>

<!-- QR üretimi tarayıcıda yapılır (gizli anahtar dışarı gitmez). Kütüphane yerelde
     barındırılır → çevrimdışı okullarda da çalışır, harici CDN'e bağımlı değildir. -->
<script src="assets/vendor/qrcode.min.js"></script>

<script>
(function () {

    // ---- Sekmeler: Kullanıcılar / Güvenlik / Genel (2FA önerisindeki #twofaCard bağlantısı Güvenlik'i açar)
    const markFirst = () => {
        let first = true;
        document.querySelectorAll('.sects > .sect').forEach(sec => { sec.classList.toggle('first-visible', !sec.hidden && first); if (!sec.hidden) first = false; });
    };
    const setTabs = POps.pageTabs(document.getElementById('setTabs'), { def: 'users', panes: '.sects > [data-pane]', onChange: markFirst });
    if (location.hash === '#twofaCard') setTabs.set('security');
    window.addEventListener('hashchange', () => { if (location.hash === '#twofaCard') setTabs.set('security'); });
    const $ = (id) => document.getElementById(id);
    const IS_SUPER = window.USER_ROLE === 'superadmin';
    const ME = <?php echo json_encode((string)($_SESSION['username'] ?? ''), JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?>;
    const PAGES = <?php echo json_encode($permPages, JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_UNESCAPED_UNICODE); ?>;
    const VIEWER_BLOCKED = <?php echo json_encode($viewerBlocked, JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?>;
    const ROLES = {
        superadmin: { word: 'Süper admin', hint: 'Her şeye erişir: bütün sayfalar, kullanıcılar ve Sistem.' },
        admin: { word: 'Yönetici', hint: 'Seçilen sayfalarda günlük işleri yapar.' },
        viewer: { word: 'İzleyici', hint: 'Seçilen sayfaları yalnızca görüntüler; Dağıtım, Uzak komut ve Ayarlar kapalıdır.' }
    };
    const roleWord = (r) => (ROLES[r] || { word: r || '—' }).word;
    const pageName = (k) => PAGES[k] || k;
    // Sunucunun "YYYY-MM-DD HH:MM:SS" (yerel saat) biçimi her tarayıcıda okunsun
    const loginDate = (v) => (v ? POps.toDate(String(v).replace(' ', 'T')) : null);
    function permsOf(u) {
        try { const p = JSON.parse(u.permissions || '[]'); return Array.isArray(p) ? p.filter(x => typeof x === 'string') : []; } catch (e) { return []; }
    }

    let users = [];
    let usersLoaded = false;
    let focusId = null;
    let twofaOn = null;
    let limitSaved = null;

    // ================= KULLANICILAR =================
    function accessText(u) {
        if (u.role === 'superadmin') return 'Bütün sayfalar';
        const n = permsOf(u).filter(k => PAGES[k]).length;
        return n ? `${n} sayfa` : 'Yalnızca kontrol merkezi';
    }
    function rowHtml(u) {
        const self = u.username === ME;
        const initial = String(u.username || '?').charAt(0).toLocaleUpperCase('tr');
        const when = loginDate(u.last_login);
        const lastHtml = when ? POps.timeHtml(when) : '<span class="faint">Hiç girmedi</span>';
        return `<tr data-id="${Number(u.id)}" tabindex="0" class="${focusId === u.id ? 'is-focus' : ''}">
            <td><div class="nm"><span class="av" aria-hidden="true">${escapeHtml(initial)}</span><span>${escapeHtml(u.username)}${self ? '<span class="you">siz</span>' : ''}</span></div></td>
            <td>${escapeHtml(roleWord(u.role))}</td>
            <td class="hide-sm col-access" title="${escapeHtml(u.role === 'superadmin' ? '' : permsOf(u).map(pageName).join(', '))}">${escapeHtml(accessText(u))}</td>
            <td class="when">${lastHtml}</td>
        </tr>`;
    }
    function renderUsers() {
        const body = $('userBody');
        if (!users.length) {
            POps.setEmpty(body, { tag: 'tr', colspan: 4, icon: 'users', title: 'Kayıtlı kullanıcı yok' });
            return;
        }
        body.innerHTML = users.map(rowHtml).join('');
    }
    function renderSummary() {
        if (usersLoaded) {
            const supers = users.filter(u => u.role === 'superadmin').length;
            $('sumUsers').innerHTML = `<b>${users.length}</b> kullanıcı` + (supers && supers < users.length ? ` · <b>${Number(supers)}</b> süper admin` : '');
        }
    }
    async function loadUsers() {
        try {
            const r = await POps.get('/api/admin/users');
            users = (r && Array.isArray(r.users)) ? r.users : [];
            usersLoaded = true;
        } catch (e) {
            POps.setError($('userBody'), e, { tag: 'tr', colspan: 4 });
            $('sumUsers').textContent = 'Kullanıcılar alınamadı';
            return;
        }
        if (focusId !== null && !users.some(u => u.id === focusId)) { focusId = null; POps.drawer.close(); }
        renderUsers();
        renderSummary();
        if (focusId !== null && POps.drawer.isOpen('user:' + focusId)) renderDrawer(users.find(u => u.id === focusId));
    }

    // ---- Kullanıcı ayrıntı paneli
    function drawerHtml(u) {
        const self = u.username === ME;
        const perms = permsOf(u);
        const when = loginDate(u.last_login);
        const pagesText = u.role === 'superadmin' ? 'Bütün sayfalar' : (perms.length ? perms.map(pageName).join(', ') : 'Yalnızca kontrol merkezi');
        const factsHtml = `<div class="grow"><span>Rol</span><span>${escapeHtml(roleWord(u.role))}</span></div>`
            + `<div class="grow"><span>Sayfalar</span><span>${escapeHtml(pagesText)}</span></div>`
            + `<div class="grow"><span>Son giriş</span><span>${when ? POps.timeHtml(when) : 'Hiç girmedi'}</span></div>`
            + (self && twofaOn !== null ? `<div class="grow"><span>2FA</span><span>${twofaOn ? 'Açık' : 'Kapalı'}</span></div>` : '')
            + `<div class="grow"><span>Kimlik</span><span>#${Number(u.id)}</span></div>`;
        const canDelete = u.role !== 'superadmin' && !self;
        const actionsHtml = IS_SUPER
            ? `<div class="set uact">
                <button type="button" class="srow" data-act="edit">${POps.iconHtml('sliders', 'sm')}<span class="grow">Rolü ve yetkileri düzenle</span>${POps.iconHtml('right', 'sm')}</button>
                <button type="button" class="srow" data-act="password">${POps.iconHtml('key', 'sm')}<span class="grow">Şifreyi sıfırla</span>${POps.iconHtml('right', 'sm')}</button>
                ${canDelete ? `<button type="button" class="srow danger" data-act="delete">${POps.iconHtml('trash', 'sm')}<span class="grow">Kullanıcıyı sil</span></button>` : ''}
              </div>`
              + (canDelete ? '' : `<div class="dnote">${self ? 'Kendi hesabınızı silemezsiniz.' : 'Süper admin hesabı silinemez; silmek için önce rolünü değiştirin.'}</div>`)
            : '<div class="dnote">Kullanıcıları yalnızca süper admin düzenleyebilir.</div>';
        return `<div class="drawer-head">
                <div class="drawer-title">
                    <span class="drawer-ico">${POps.iconHtml('user', 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(u.username)}</h2><div class="sub">${escapeHtml(roleWord(u.role))}${self ? ' · siz' : ''}</div></div>
                </div>
                <button type="button" class="ibtn sm" data-act="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button>
            </div>
            <div class="glist">${factsHtml}</div>
            ${actionsHtml}`;
    }
    function renderDrawer(u) {
        if (!u) return;
        const body = POps.drawer.body();
        body.innerHTML = drawerHtml(u);
        if (!body.dataset.userWired) {
            body.dataset.userWired = '1';
            body.addEventListener('click', (e) => {
                const b = e.target.closest('[data-act]');
                const k = POps.drawer.key();
                if (!b || !k || !String(k).startsWith('user:')) return;
                const cur = users.find(x => 'user:' + x.id === k);
                const act = b.dataset.act;
                if (act === 'close') POps.drawer.close();
                else if (!cur) return;
                else if (act === 'edit') openEditor(cur);
                else if (act === 'password') resetPassword(cur);
                else if (act === 'delete') deleteUser(cur);
            });
        }
    }
    function openUser(id) {
        const u = users.find(x => x.id === id);
        if (!u) return;
        // Önce panel açılır: başka bir kullanıcı açıksa onun onClose'u odağı temizler
        POps.drawer.open('user:' + id, { onClose: () => { focusId = null; renderUsers(); } });
        focusId = id;
        renderUsers();
        renderDrawer(u);
    }
    $('userBody').addEventListener('click', (e) => {
        const tr = e.target.closest('tr[data-id]');
        if (tr && !e.target.closest('a')) openUser(Number(tr.dataset.id));
    });
    $('userBody').addEventListener('keydown', (e) => {
        const tr = e.target.closest('tr[data-id]');
        if (tr && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openUser(Number(tr.dataset.id)); }
    });

    // ---- Ekle / düzenle (yalnızca süper admin; sunucu da yalnızca süper admine izin verir)
    let editing = null;     // düzenlenen kullanıcı; null = yeni
    let formRole = 'admin';
    function setRole(r) {
        formRole = r;
        $('umRole').querySelectorAll('button').forEach(b => { const on = b.dataset.role === r; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        $('umRoleHint').textContent = ROLES[r].hint;
        $('umPerms').hidden = r === 'superadmin';
        // İzleyici bu sayfaları yetki verilse de açamaz (includes/header.php)
        document.querySelectorAll('#umPerms .check').forEach(l => {
            const blocked = r === 'viewer' && VIEWER_BLOCKED.includes(l.dataset.page);
            l.classList.toggle('is-blocked', blocked);
            l.querySelector('input').disabled = blocked;
            l.title = blocked ? 'İzleyici bu sayfayı açamaz' : '';
        });
        $('umPermsHint').textContent = r === 'viewer' ? 'Kontrol merkezi herkese açıktır. İzleyici Dağıtım, Uzak komut ve Ayarlar sayfalarını açamaz.' : 'Kontrol merkezi herkese açıktır.';
    }
    function clearErrors() { document.querySelectorAll('#userModal .field.has-error').forEach(f => f.classList.remove('has-error')); }
    function openEditor(u) {
        editing = u || null;
        clearErrors();
        $('umTitle').textContent = u ? 'Kullanıcıyı düzenle' : 'Kullanıcı ekle';
        $('umSave').textContent = u ? 'Değişiklikleri kaydet' : 'Kullanıcıyı ekle';
        $('umName').value = u ? u.username : '';
        $('umPass').value = '';
        $('umPassField').hidden = !!u;   // var olan kullanıcının şifresi "Şifreyi sıfırla" ile değişir
        const perms = u ? permsOf(u) : [];
        document.querySelectorAll('.perm-cb').forEach(cb => { cb.checked = perms.includes(cb.value); });
        setRole(u ? (ROLES[u.role] ? u.role : 'admin') : 'admin');
        $('umSelf').hidden = !(u && u.username === ME);
        openModal('userModal');
    }
    async function saveUser() {
        clearErrors();
        const username = $('umName').value.trim();
        const password = $('umPass').value;
        let bad = false;
        if (!username) { $('umNameField').classList.add('has-error'); bad = true; }
        if (!editing && !password) { $('umPassField').classList.add('has-error'); bad = true; }
        if (bad) { (username ? $('umPass') : $('umName')).focus(); return; }
        // Görünmeyen (ör. süper admine geçince gizlenen) seçimler de korunur
        const perms = [...document.querySelectorAll('.perm-cb')].filter(cb => cb.checked).map(cb => cb.value);
        if (editing) permsOf(editing).filter(k => !PAGES[k]).forEach(k => perms.push(k));   // panelin bilmediği eski anahtarlar silinmesin
        const payload = { username, role: formRole, permissions: JSON.stringify(perms) };
        if (!editing) payload.password = password;
        const target = editing;
        const ok = await POps.act($('umSave'), () => target
            ? POps.api('/api/admin/users/' + encodeURIComponent(target.id), { method: 'PUT', body: payload })
            : POps.post('/api/admin/users', payload), { success: target ? `${username} güncellendi.` : `${username} eklendi.` });
        if (!ok) return;
        closeModal('userModal');
        await loadUsers();
        if (!target) { const nu = users.find(x => x.username === username); if (nu) openUser(nu.id); }
    }
    async function resetPassword(u) {
        const self = u.username === ME;
        const pw = await POps.prompt({
            title: `${u.username} için yeni şifre`,
            message: self ? 'Şifreyi değiştirince yeniden giriş yapmanız gerekir.' : 'Kullanıcının açık oturumları kapanır; yeni şifreyle yeniden girer.',
            label: 'Yeni şifre', inputType: 'password', autocomplete: 'new-password', trim: false,
            hint: 'En az 8 karakter önerilir.', confirmText: 'Şifreyi değiştir', icon: 'key'
        });
        if (pw === null) return;
        const payload = { username: u.username, role: u.role, permissions: JSON.stringify(permsOf(u)), password: pw };
        if (await POps.act(null, () => POps.api('/api/admin/users/' + encodeURIComponent(u.id), { method: 'PUT', body: payload }), { success: `${u.username} için şifre değişti.` })) loadUsers();
    }
    async function deleteUser(u) {
        const ok = await POps.confirm({ title: `${u.username} silinsin mi?`, message: 'Kullanıcının açık oturumları da kapanır. Bu işlem geri alınamaz.', confirmText: 'Kullanıcıyı sil', danger: true, icon: 'trash' });
        if (!ok) return;
        if (await POps.act(null, () => POps.del('/api/admin/users/' + encodeURIComponent(u.id)), { success: `${u.username} silindi.` })) {
            POps.drawer.close();
            loadUsers();
        }
    }
    if (IS_SUPER) {
        $('addUserBtn').addEventListener('click', () => openEditor(null));
        $('umRole').addEventListener('click', (e) => { const b = e.target.closest('button[data-role]'); if (b) setRole(b.dataset.role); });
        $('umSave').addEventListener('click', saveUser);
        $('userModal').addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox') { e.preventDefault(); saveUser(); } });
        ['umName', 'umPass'].forEach(id => $(id).addEventListener('input', (e) => e.target.closest('.field').classList.remove('has-error')));
    }

    // ================= İKİ ADIMLI DOĞRULAMA =================
    function renderTwofa(enabled) {
        twofaOn = enabled;
        $('twofaSetup').hidden = true;
        $('twofaState').innerHTML = enabled ? '<span class="dot ok"></span>Açık' : '<span class="dot off"></span>Kapalı';
        $('twofaDesc').textContent = enabled ? 'Girişte doğrulama kodu istenir.' : 'Önerilir, zorunlu değildir.';
        const b = $('twofaBtn');
        b.hidden = false;
        b.textContent = enabled ? "2FA'yı kapat" : "2FA'yı kur";
        b.dataset.mode = enabled ? 'disable' : 'setup';
        $('sumTwofa').hidden = false;
        $('sumTwofa').innerHTML = enabled ? '<span class="dot ok"></span>2FA açık' : '<span class="dot off"></span>2FA kapalı';
        if (typeof popsTwofaNudge === 'function') popsTwofaNudge(enabled);
        const u = focusId !== null && users.find(x => x.id === focusId);
        if (u && u.username === ME && POps.drawer.isOpen('user:' + u.id)) renderDrawer(u);
    }
    async function loadTwofa() {
        try {
            const r = await POps.get('/api/admin/2fa/status');
            renderTwofa(!!(r && r.enabled));
        } catch (e) {
            $('twofaState').innerHTML = '<span class="dot bad"></span>Alınamadı';
            $('twofaDesc').textContent = POps.errorMessage(e);
        }
    }
    async function twofaSetup(btn) {
        let r;
        try { r = await POps.busy(btn, () => POps.post('/api/admin/2fa/setup')); }
        catch (e) { POps.toast('error', 'Kurulum başlatılamadı: ' + POps.errorMessage(e)); return; }
        $('twofaSecret').value = (r && r.secret) || '';
        const qr = $('twofaQr');
        qr.replaceChildren();
        if (typeof QRCode !== 'undefined' && r && r.otpauth_uri) new QRCode(qr, { text: r.otpauth_uri, width: 180, height: 180 });
        else qr.textContent = 'QR kodu oluşturulamadı; yandaki anahtarı elle girin.';
        $('twofaCode').value = '';
        $('twofaSetup').hidden = false;
        $('twofaBtn').hidden = true;
        $('twofaCode').focus();
    }
    async function twofaEnable() {
        const code = $('twofaCode').value.trim();
        if (!/^\d{6}$/.test(code)) { $('twofaCode').classList.add('is-invalid'); $('twofaCode').focus(); POps.toast('warning', '6 haneli kodu girin.'); return; }
        if (await POps.act($('twofaEnableBtn'), () => POps.post('/api/admin/2fa/enable', { otp: code }), { success: '2FA açıldı. Bir sonraki girişte kod istenecek.' })) loadTwofa();
    }
    async function twofaDisable() {
        const code = await POps.prompt({ title: "2FA'yı kapatmak istiyor musunuz?", message: 'Doğrulama uygulamasındaki 6 haneli kodu girin.', label: 'Kod', placeholder: '123456', maxLength: 6, inputMode: 'numeric', confirmText: "2FA'yı kapat", danger: true,
            validate: (v) => /^\d{6}$/.test(v.trim()) ? null : '6 haneli kodu girin.' });
        if (code === null) return;
        if (await POps.act($('twofaBtn'), () => POps.post('/api/admin/2fa/disable', { otp: code.trim() }), { success: '2FA kapatıldı.' })) loadTwofa();
    }
    $('twofaBtn').addEventListener('click', (e) => { if (e.currentTarget.dataset.mode === 'disable') twofaDisable(); else twofaSetup(e.currentTarget); });
    $('twofaEnableBtn').addEventListener('click', twofaEnable);
    $('twofaCode').addEventListener('input', (e) => e.target.classList.remove('is-invalid'));
    $('twofaCode').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); twofaEnable(); } });
    $('twofaCancelBtn').addEventListener('click', () => { $('twofaSetup').hidden = true; $('twofaBtn').hidden = false; });
    $('twofaCopy').addEventListener('click', async () => {
        try { await navigator.clipboard.writeText($('twofaSecret').value); POps.toast('success', 'Anahtar kopyalandı.'); }
        catch (e) { $('twofaSecret').select(); POps.toast('warning', 'Kopyalanamadı; anahtarı seçip elle kopyalayın.'); }
    });
    $('twofaSecret').addEventListener('click', (e) => e.target.select());

    // ================= KURUM =================
    // Giriş ekranındaki kurum adı ve logosu: GET /api/branding (oturumsuz), değiştirmek yalnızca süper admin
    let orgSaved = '';
    function renderBrand(b) {
        orgSaved = (b && b.org_name) || '';
        $('orgName').value = orgSaved;
        const logo = $('orgLogo');
        if (b && b.logo) {
            logo.src = (window.OMYO_API ? window.OMYO_API.HTTP_URL : '') + '/api/branding/logo?v=' + encodeURIComponent(b.logo_v || '');
            logo.hidden = false;
        } else {
            logo.removeAttribute('src');
            logo.hidden = true;
        }
        $('orgLogoDel').hidden = !(b && b.logo) || !IS_SUPER;
        $('orgSave').disabled = true;
    }
    async function loadBrand() {
        try { renderBrand(await POps.get('/api/branding')); }
        catch (e) { $('orgName').placeholder = 'Alınamadı: ' + POps.errorMessage(e); return; }
        $('orgName').disabled = !IS_SUPER;
        $('orgLogoPick').disabled = !IS_SUPER;
    }
    $('orgName').addEventListener('input', () => { $('orgSave').disabled = $('orgName').value.trim() === orgSaved; });
    $('orgName').addEventListener('keydown', (e) => { if (e.key === 'Enter' && !$('orgSave').disabled) { e.preventDefault(); $('orgSave').click(); } });
    $('orgSave').addEventListener('click', async function () {
        const name = $('orgName').value.trim();
        let r = null;
        if (await POps.act(this, async () => { r = await POps.post('/api/system/branding', { org_name: name || null }); },
            { success: name ? 'Kurum adı kaydedildi.' : 'Kurum adı kaldırıldı.' })) renderBrand(r);
    });
    $('orgLogoPick').addEventListener('click', () => $('orgLogoFile').click());
    $('orgLogoFile').addEventListener('change', async function () {
        const f = this.files && this.files[0];
        this.value = '';
        if (!f) return;
        if (f.size > 256 * 1024) { POps.toast('warning', 'Logo en çok 256 KB olabilir.'); return; }
        const fd = new FormData();
        fd.append('file', f);
        let r = null;
        if (await POps.act($('orgLogoPick'), async () => { r = await POps.post('/api/system/branding/logo', fd); }, { success: 'Logo yüklendi.' })) renderBrand(r);
    });
    $('orgLogoDel').addEventListener('click', async function () {
        if (!await POps.confirm({ title: 'Logo kaldırılsın mı?', message: 'Giriş ekranında yeniden POps logosu görünür.', confirmText: 'Kaldır', danger: true })) return;
        let r = null;
        if (await POps.act(this, async () => { r = await POps.del('/api/system/branding/logo'); }, { success: 'Logo kaldırıldı.' })) renderBrand(r);
    });

    // ================= GÖREV KUYRUĞU =================
    const limitValue = () => $('queueLimit').value.trim();
    const isDirty = () => limitSaved !== null && limitValue() !== String(limitSaved);
    function refreshBar() { $('saveBar').hidden = !isDirty(); }
    async function loadLimit() {
        try {
            const r = await POps.get('/api/get_concurrent_limit');
            limitSaved = (r && r.limit != null) ? r.limit : 5;
            $('queueLimit').value = limitSaved;
            $('queueLimit').disabled = false;
        } catch (e) {
            $('queueLimit').placeholder = '—';
            $('queueLimit').title = 'Sınır alınamadı: ' + POps.errorMessage(e);
        }
        refreshBar();
    }
    async function saveLimit() {
        if (!isDirty()) return;
        const v = parseInt(limitValue(), 10);
        if (!v || v < 1 || v > 200 || String(v) !== limitValue()) {
            $('queueLimit').classList.add('is-invalid');
            $('queueLimit').focus();
            POps.toast('warning', '1 ile 200 arasında bir sayı girin.');
            return;
        }
        if (await POps.act($('saveBtn'), () => POps.post('/api/set_concurrent_limit', { limit: v }), { success: `Eşzamanlı görev sınırı ${v} bilgisayar oldu.` })) {
            limitSaved = v;
            $('queueLimit').value = v;
            refreshBar();
        }
    }
    $('queueLimit').addEventListener('input', (e) => { e.target.classList.remove('is-invalid'); refreshBar(); });
    $('queueLimit').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); saveLimit(); } });
    $('saveBtn').addEventListener('click', saveLimit);
    $('discardBtn').addEventListener('click', () => { $('queueLimit').value = limitSaved; $('queueLimit').classList.remove('is-invalid'); refreshBar(); });
    document.addEventListener('keydown', (e) => {
        if ((e.ctrlKey || e.metaKey) && (e.key === 's' || e.key === 'S') && isDirty()) { e.preventDefault(); saveLimit(); }
    });
    window.addEventListener('beforeunload', (e) => {
        if (!isDirty()) return;
        e.preventDefault();
        e.returnValue = '';
    });

    // ================= SUNUCU BAĞLANTISI =================
    async function testConnection() {
        const st = $('connState');
        st.innerHTML = '<span class="spinner sm"></span>Sınanıyor';
        const t0 = performance.now();
        try {
            await POps.get('/api/devices');
            const ms = Math.round(performance.now() - t0);
            st.innerHTML = `<span class="dot ok"></span>Çalışıyor · ${Number(ms)} ms`;
            $('connDesc').textContent = 'Panel merkez sunucuya ulaşıyor.';
            $('sumConn').innerHTML = '<span class="dot ok"></span>Sunucu çalışıyor';
        } catch (e) {
            st.innerHTML = '<span class="dot bad"></span>Ulaşılamıyor';
            $('connDesc').textContent = POps.errorMessage(e);
            $('sumConn').innerHTML = '<span class="dot bad"></span>Sunucuya ulaşılamıyor';
        }
        $('sumConn').hidden = false;
    }
    $('connRetry').addEventListener('click', testConnection);

    if (typeof OMYO_API !== 'undefined') {
        $('dispHttpUrl').textContent = OMYO_API.HTTP_URL;
        $('dispWsUrl').textContent = OMYO_API.WS_URL;
    } else {
        $('dispHttpUrl').textContent = 'pops_config.js okunamadı';
        $('dispWsUrl').textContent = 'pops_config.js okunamadı';
    }
    testConnection();
    loadUsers();
    loadTwofa();
    loadLimit();
    loadBrand();
})();
</script>

<?php include 'includes/footer.php'; ?>
