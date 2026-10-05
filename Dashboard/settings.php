<?php include 'includes/header.php'; ?>
<?php
$isSuper = ($_SESSION['role'] ?? '') === 'superadmin';
// Kullanıcının açabileceği sayfalar (kontrol merkezi herkese açık, Sistem yalnızca süper admin)
// Anahtarlar veridir (yetki listesi); adlar yalnızca gösterilir
$permPages = [
    'devices' => __('Cihazlar'), 'labs' => __('Sınıflar'), 'tasks' => __('İşlemler'), 'terminal' => __('Uzak komut'),
    'vision' => __('Uzak ekran'), 'deploy' => __('Dağıtım'), 'policies' => __('Politikalar'), 'logger' => __('Kayıtlar'),
    'reports' => __('Raporlar'), 'helpdesk' => __('Destek talepleri'), 'settings' => __('Ayarlar'),
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
    .user-table .nm > span:not(.av):not(.badge) { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .user-table .nm > .badge { flex: none; }
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

    /* API jetonları */
    .tok-row .ico.lead { color: var(--text-tertiary); flex: none; }
    .tok-row .t { display: flex; align-items: center; gap: 8px; min-width: 0; }
    .tok-row .t > span:first-child { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tok-row .d code { font-size: 11.5px; }
    .tok-row.is-off .t > span:first-child { color: var(--text-tertiary); }

    /* Kimlik sağlayıcıları (LDAP / OIDC) */
    .sso-form .form-grid { margin-bottom: var(--space-4); }
    .sso-form textarea.mono, .sso-form input.mono { font-family: var(--font-mono); font-size: 12.5px; }
    .sso-form .sso-sub { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); margin: var(--space-5) 0 var(--space-2); }
    .sso-form .sso-sub:first-child { margin-top: 0; }
    .smap { display: flex; flex-direction: column; gap: 8px; }
    .smap-row { border: 1px solid var(--border-subtle); border-radius: 10px; padding: 10px; display: flex; flex-direction: column; gap: 8px; }
    .smap-top { display: flex; gap: 8px; align-items: center; }
    .smap-top .smap-group { flex: 1; min-width: 0; }
    .smap-top .smap-role { width: auto; flex: none; }
    .perm-grid.smap-pages { grid-template-columns: repeat(auto-fill, minmax(116px, 1fr)); }
    .smap-empty { font-size: var(--text-sm); color: var(--text-tertiary); }
    .sso-test { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .sso-test input { flex: 1; min-width: 160px; }
    .sso-result { margin-top: var(--space-3); }
    .sso-result ul { margin: 6px 0 0 18px; padding: 0; }
    .sso-result code { overflow-wrap: anywhere; }
    @media (max-width: 640px) { .smap-top { flex-wrap: wrap; } .smap-top .smap-group { flex-basis: 100%; } }

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
        <h1><?php _e('Ayarlar'); ?></h1>
        <div class="summary">
            <span class="sum" id="sumUsers"><?php _e('Yükleniyor…'); ?></span>
            <span class="sum" id="sumConn" hidden></span>
            <span class="sum" id="sumTwofa" hidden></span>
        </div>
    </div>
    <?php if ($isSuper): ?>
    <div class="page-header-actions">
        <button type="button" class="btn" id="addUserBtn"><?php echo pops_icon('plus', 'sm'); ?><?php _e('Kullanıcı ekle'); ?></button>
    </div>
    <?php endif; ?>
</div>

<div class="tabs set-tabs" id="setTabs" aria-label="<?php _e('Ayarlar bölümleri'); ?>">
    <button type="button" class="tab" data-tab="users"><?php _e('Kullanıcılar'); ?></button>
    <button type="button" class="tab" data-tab="security"><?php _e('Güvenlik'); ?></button>
    <button type="button" class="tab" data-tab="general"><?php _e('Genel'); ?></button>
</div>

<div class="sects">
    <section class="sect" data-pane="users" aria-labelledby="hUsers">
        <div class="sect-head">
            <h2 id="hUsers"><?php _e('Kullanıcılar'); ?></h2>
            <p><?php _e('Panele kimlerin girebileceği ve hangi sayfaları açabileceği. Ayrıntı ve işlemler için bir kullanıcıya tıklayın.'); ?><?php if (!$isSuper): ?> <?php _e('Kullanıcıları yalnızca süper admin ekler, düzenler ve siler.'); ?><?php endif; ?></p>
        </div>
        <div class="sect-body">
            <div class="table-wrap">
                <table class="data-table user-table">
                    <thead><tr><th><?php _e('Kullanıcı'); ?></th><th><?php _e('Rol'); ?></th><th class="hide-sm col-access"><?php _e('Erişim'); ?></th><th><?php _e('Son giriş'); ?></th></tr></thead>
                    <tbody id="userBody"><tr><td colspan="4"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Kullanıcılar yükleniyor…'); ?></div></td></tr></tbody>
                </table>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="security" id="twofaCard" aria-labelledby="hTwofa">
        <div class="sect-head">
            <h2 id="hTwofa"><?php _e('İki adımlı doğrulama'); ?></h2>
            <p><?php _e('Girişte şifreye ek olarak doğrulama uygulamasından (Google Authenticator, Authy, Microsoft Authenticator) 6 haneli kod istenir. Yalnızca kendi hesabınız için geçerlidir.'); ?></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t"><?php _e('Hesabınızda 2FA'); ?></div>
                        <div class="d" id="twofaDesc"><?php _e('Önerilir, zorunlu değildir.'); ?></div>
                    </div>
                    <span class="st" id="twofaState"><span class="spinner sm"></span></span>
                    <button type="button" class="btn secondary sm" id="twofaBtn" hidden></button>
                </div>
                <div class="srow block" id="twofaSetup" hidden>
                    <div class="tf-setup">
                        <div class="tf-qr" id="twofaQr" aria-label="<?php _e('2FA kurulum QR kodu'); ?>"></div>
                        <div class="tf-steps">
                            <div class="tf-step"><span class="n">1</span><span><?php _e('Doğrulama uygulamasında yeni hesap ekleyip QR kodu okutun.'); ?></span></div>
                            <div class="field" style="margin:0">
                                <label for="twofaSecret"><?php _e('QR okutamıyorsanız bu anahtarı elle girin'); ?></label>
                                <div class="input-group">
                                    <input type="text" id="twofaSecret" readonly spellcheck="false">
                                    <button type="button" class="ibtn boxed" id="twofaCopy" data-tip="<?php _e('Anahtarı kopyala'); ?>" data-tip-pos="left" aria-label="<?php _e('Anahtarı kopyala'); ?>"><?php echo pops_icon('copy'); ?></button>
                                </div>
                            </div>
                            <div class="tf-step"><span class="n">2</span><span><?php _e('Uygulamanın gösterdiği 6 haneli kodu yazın.'); ?></span></div>
                            <div class="input-group">
                                <input type="text" id="twofaCode" inputmode="numeric" maxlength="6" placeholder="123456" autocomplete="one-time-code" aria-label="<?php _e('6 haneli kod'); ?>">
                                <button type="button" class="btn" id="twofaEnableBtn"><?php _e('Etkinleştir'); ?></button>
                                <button type="button" class="btn ghost" id="twofaCancelBtn"><?php _e('Vazgeç'); ?></button>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
        </div>
    </section>

    <?php if ($isSuper): ?>
    <section class="sect" data-pane="security" aria-labelledby="hTokens">
        <div class="sect-head">
            <h2 id="hTokens"><?php _e('API jetonları'); ?></h2>
            <p><?php _e('Betikler ve dış sistemler panel girişi olmadan {path} uçlarını bu jetonlarla kullanır.', ['path' => '/api/v1']); ?> <?php _e('Jeton yalnızca oluşturulurken bir kez gösterilir; sunucuda özeti saklanır. Görüntüleyici yalnızca okur; Yönetici günlük işleri yapar ama süper admin işlemlerine, kullanıcılara, jetonlara ve uzak ekrana erişemez.'); ?> <?php _e('Ayrıntı:'); ?> <code>docs/api.md</code></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t"><?php _e('Jetonlar'); ?></div>
                        <div class="d" id="tokSummary"><?php _e('Yükleniyor…'); ?></div>
                    </div>
                    <button type="button" class="btn secondary sm" id="tokNew"><?php echo pops_icon('plus', 'sm'); ?><?php _e('Jeton oluştur'); ?></button>
                </div>
            </div>
            <div class="set" id="tokList" hidden></div>
        </div>
    </section>
    <?php endif; ?>

    <?php if ($isSuper): ?>
    <section class="sect" data-pane="security" aria-labelledby="hSso">
        <div class="sect-head">
            <h2 id="hSso"><?php _e('Kimlik sağlayıcıları'); ?></h2>
            <p><?php _e('Okulun dizin hesaplarıyla (Active Directory / LDAP) ya da tek oturum açma sağlayıcısıyla (OpenID Connect: Microsoft Entra ID, Google, Keycloak) panele giriş. Rol ve sayfalar dizindeki gruplardan gelir. Yerel hesaplar her zaman çalışır: dizine ulaşılamasa da yerel süper admin girer.'); ?> <code>docs/security.md</code></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t">Active Directory / LDAP</div>
                        <div class="d" id="ssoLdapDesc"><?php _e('Yükleniyor…'); ?></div>
                    </div>
                    <span class="st" id="ssoLdapState"></span>
                    <button type="button" class="btn secondary sm" id="ssoLdapEdit" disabled><?php _e('Ayarla'); ?></button>
                </div>
                <div class="srow">
                    <div class="grow">
                        <div class="t">OpenID Connect</div>
                        <div class="d" id="ssoOidcDesc"><?php _e('Yükleniyor…'); ?></div>
                    </div>
                    <span class="st" id="ssoOidcState"></span>
                    <button type="button" class="btn secondary sm" id="ssoOidcEdit" disabled><?php _e('Ayarla'); ?></button>
                </div>
            </div>
        </div>
    </section>
    <?php endif; ?>

    <section class="sect" data-pane="general" aria-labelledby="hOrg">
        <div class="sect-head">
            <h2 id="hOrg"><?php _e('Kurum'); ?></h2>
            <p><?php _e('Giriş ekranında görünen ad ve logo. Yalnızca süper admin değiştirir.'); ?></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t" id="orgNameLabel"><?php _e('Kurum adı'); ?></div>
                        <div class="d"><?php _e('Giriş ekranının başlığı olur. Boş bırakılırsa "POps" yazar.'); ?></div>
                    </div>
                    <input type="text" id="orgName" class="org-name" maxlength="80" autocomplete="organization" aria-labelledby="orgNameLabel" placeholder="<?php _e('Örn. Atatürk Anadolu Lisesi'); ?>" disabled>
                    <button type="button" class="btn secondary sm" id="orgSave" disabled><?php _e('Kaydet'); ?></button>
                </div>
                <div class="srow">
                    <div class="grow">
                        <div class="t">Logo</div>
                        <div class="d"><?php _e('PNG, JPEG ya da WebP, en çok 256 KB. Kare ya da yatay bir logo en iyi görünür.'); ?></div>
                    </div>
                    <img id="orgLogo" class="org-logo" alt="<?php _e('Kurum logosu'); ?>" hidden>
                    <input type="file" id="orgLogoFile" accept="image/png,image/jpeg,image/webp" hidden>
                    <button type="button" class="btn secondary sm" id="orgLogoPick" disabled><?php echo pops_icon('upload', 'sm'); ?><?php _e('Logo yükle'); ?></button>
                    <button type="button" class="ibtn sm" id="orgLogoDel" data-tip="<?php _e('Logoyu kaldır'); ?>" data-tip-pos="left" aria-label="<?php _e('Logoyu kaldır'); ?>" hidden><?php echo pops_icon('trash', 'sm'); ?></button>
                </div>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="general" aria-labelledby="hQueue">
        <div class="sect-head">
            <h2 id="hQueue"><?php _e('Görev kuyruğu'); ?></h2>
            <p><?php _e('Dosya indirme ve kurulum gibi görevler ağ boğulmasın diye paketler halinde gönderilir.'); ?></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t" id="queueLimitLabel"><?php _e('Eşzamanlı görev sınırı'); ?></div>
                        <div class="d"><?php _e('Aynı anda görev alan en çok bilgisayar sayısı. 1 Gbit ağda en çok 15 önerilir.'); ?></div>
                    </div>
                    <input type="number" id="queueLimit" min="1" max="200" inputmode="numeric" aria-labelledby="queueLimitLabel" disabled>
                    <span class="unit"><?php _e('bilgisayar'); ?></span>
                </div>
            </div>
        </div>
    </section>

    <section class="sect" data-pane="general" aria-labelledby="hServer">
        <div class="sect-head">
            <h2 id="hServer"><?php _e('Sunucu bağlantısı'); ?></h2>
            <p><?php _e('Adresler sunucudaki {file} dosyasından okunur ({names}) ve buradan değiştirilemez.', ['file' => '.env', 'names' => 'POPS_API_URL, POPS_API_INTERNAL_URL']); ?> <?php _e('Ayrıntı:'); ?> <code>docs/configuration.md</code></p>
        </div>
        <div class="sect-body">
            <div class="set">
                <div class="srow">
                    <div class="grow">
                        <div class="t"><?php _e('Bağlantı'); ?></div>
                        <div class="d" id="connDesc"><?php _e('Panelin merkez sunucuya ulaşıp ulaşmadığı'); ?></div>
                    </div>
                    <span class="st" id="connState"><span class="spinner sm"></span><?php _e('Sınanıyor'); ?></span>
                    <button type="button" class="ibtn sm" id="connRetry" data-tip="<?php _e('Yeniden sına'); ?>" data-tip-pos="left" aria-label="<?php _e('Bağlantıyı yeniden sına'); ?>"><?php echo pops_icon('refresh', 'sm'); ?></button>
                </div>
                <div class="srow">
                    <div class="grow"><div class="t"><?php _e('REST API adresi'); ?></div></div>
                    <span class="val" id="dispHttpUrl">—</span>
                </div>
                <div class="srow">
                    <div class="grow"><div class="t"><?php _e('WebSocket adresi'); ?></div></div>
                    <span class="val" id="dispWsUrl">—</span>
                </div>
            </div>
        </div>
    </section>

    <div class="savebar" id="saveBar" role="region" aria-label="<?php _e('Kaydedilmemiş değişiklikler'); ?>" hidden>
        <span class="dot warn" aria-hidden="true"></span>
        <span class="sb-text"><?php _e('Kaydedilmemiş değişiklik var'); ?></span>
        <button type="button" class="btn secondary sm" id="discardBtn"><?php _e('Vazgeç'); ?></button>
        <button type="button" class="btn sm" id="saveBtn"><?php _e('Kaydet'); ?></button>
    </div>
</div>

<?php if ($isSuper): ?>
<div class="modal-overlay" id="userModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="umTitle"><?php _e('Kullanıcı ekle'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x', 'sm'); ?></button>
        </div>
        <div class="modal-body">
            <div class="field" id="umNameField">
                <label for="umName"><?php _e('Kullanıcı adı'); ?></label>
                <input type="text" id="umName" maxlength="64" autocomplete="off" spellcheck="false">
                <div class="field-error"><?php _e('Kullanıcı adı girin.'); ?></div>
            </div>
            <div class="field" id="umSrcField" hidden>
                <span class="field-label"><?php _e('Kimlik kaynağı'); ?></span>
                <div class="segmented block" id="umSrc" role="group" aria-label="<?php _e('Kimlik kaynağı'); ?>">
                    <button type="button" data-src="local" aria-pressed="false"><?php _e('Yerel'); ?></button>
                    <button type="button" data-src="ldap" aria-pressed="false"><?php _e('Dizin (LDAP)'); ?></button>
                    <button type="button" data-src="oidc" aria-pressed="false">OpenID Connect</button>
                </div>
                <div class="field-hint" id="umSrcHint"></div>
            </div>
            <div class="field" id="umPassField">
                <label for="umPass"><?php _e('Şifre'); ?></label>
                <input type="password" id="umPass" autocomplete="new-password">
                <div class="field-error"><?php _e('Şifre girin.'); ?></div>
                <div class="field-hint"><?php _e('En az 8 karakter önerilir.'); ?></div>
            </div>
            <div class="field">
                <span class="field-label"><?php _e('Rol'); ?></span>
                <div class="segmented block" id="umRole" role="group" aria-label="<?php _e('Rol'); ?>">
                    <button type="button" data-role="viewer" aria-pressed="false"><?php _e('İzleyici'); ?></button>
                    <button type="button" data-role="admin" aria-pressed="false"><?php _e('Yönetici'); ?></button>
                    <button type="button" data-role="superadmin" aria-pressed="false"><?php _e('Süper admin'); ?></button>
                </div>
                <div class="field-hint" id="umRoleHint"></div>
            </div>
            <div class="field" id="umPerms">
                <span class="field-label"><?php _e('Açabileceği sayfalar'); ?></span>
                <div class="perm-grid">
                    <?php foreach ($permPages as $key => $label): ?>
                    <label class="check" data-page="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>"><input type="checkbox" class="perm-cb" value="<?php echo htmlspecialchars($key, ENT_QUOTES, 'UTF-8'); ?>"><?php echo htmlspecialchars($label, ENT_QUOTES, 'UTF-8'); ?></label>
                    <?php endforeach; ?>
                </div>
                <div class="field-hint" id="umPermsHint"><?php _e('Kontrol merkezi herkese açıktır.'); ?></div>
            </div>
            <div class="alert info" id="umSelf" hidden><div><?php _e('Kendi hesabınızı değiştirince yeniden giriş yapmanız gerekir.'); ?></div></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="umSave"><?php _e('Kullanıcıyı ekle'); ?></button>
        </div>
    </div>
</div>

<div class="modal-overlay" id="tokModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><?php _e('API jetonu oluştur'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x', 'sm'); ?></button>
        </div>
        <div class="modal-body">
            <div class="field" id="tkNameField">
                <label for="tkName"><?php _e('Ad'); ?></label>
                <input type="text" id="tkName" maxlength="64" autocomplete="off" spellcheck="false" placeholder="<?php _e('Örn. envanter-betigi'); ?>">
                <div class="field-error"><?php _e('Harf, rakam, boşluk, nokta, alt çizgi ya da tire; en çok 64 karakter.'); ?></div>
                <div class="field-hint"><?php _e('Jetonla yapılan işler kayıtlara {name} olarak yazılır. Ad tektir.', ['name' => __('token:ad')]); ?></div>
            </div>
            <div class="field">
                <span class="field-label"><?php _e('Yetki'); ?></span>
                <div class="segmented block" id="tkRole" role="group" aria-label="<?php _e('Yetki'); ?>">
                    <button type="button" data-trole="viewer" aria-pressed="false"><?php _e('Görüntüleyici'); ?></button>
                    <button type="button" data-trole="admin" aria-pressed="false"><?php _e('Yönetici'); ?></button>
                </div>
                <div class="field-hint" id="tkRoleHint"></div>
            </div>
            <div class="field" id="tkDaysField">
                <label for="tkDays"><?php _e('Geçerlilik (gün)'); ?></label>
                <input type="number" id="tkDays" min="1" max="3650" value="90" inputmode="numeric">
                <div class="field-error"><?php _e('1 ile 3650 arasında bir sayı girin ya da boş bırakın.'); ?></div>
                <div class="field-hint"><?php _e('Boş bırakılırsa süresiz olur.'); ?></div>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="tkCreate"><?php _e('Jeton oluştur'); ?></button>
        </div>
    </div>
</div>
<div class="modal-overlay" id="ssoLdapModal">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title">Active Directory / LDAP</div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x', 'sm'); ?></button>
        </div>
        <div class="modal-body sso-form">
            <label class="switch-field" style="margin-bottom:var(--space-4)"><span class="switch"><input type="checkbox" id="slEnabled"><span></span></span><?php _e('Dizin hesaplarıyla girişe izin ver'); ?></label>
            <div class="sso-sub"><?php _e('Sunucu'); ?></div>
            <div class="form-grid">
                <div class="field"><label for="slHost"><?php _e('Sunucu adı'); ?></label><input type="text" id="slHost" class="mono" maxlength="255" spellcheck="false" placeholder="dc1.okul.local"></div>
                <div class="field"><label for="slPort">Port</label><input type="number" id="slPort" min="1" max="65535" inputmode="numeric"></div>
                <div class="field"><span class="field-label"><?php _e('Bağlantı'); ?></span>
                    <div class="segmented block" id="slSec" role="group" aria-label="<?php _e('Bağlantı'); ?>">
                        <button type="button" data-sec="ldaps" aria-pressed="false">LDAPS</button>
                        <button type="button" data-sec="starttls" aria-pressed="false">StartTLS</button>
                    </div>
                </div>
            </div>
            <div class="field"><label for="slCa"><?php _e('CA sertifikası (PEM, isteğe bağlı)'); ?></label>
                <textarea id="slCa" class="mono" rows="3" spellcheck="false" placeholder="-----BEGIN CERTIFICATE-----"></textarea>
                <div class="field-hint"><?php _e('Dizin sunucusunun sertifikasını imzalayan kurum CA\'sı. Boşsa sunucunun sistem CA deposu kullanılır. Sertifika ve sunucu adı her zaman doğrulanır; şifresiz LDAP kabul edilmez.'); ?></div>
            </div>
            <div class="sso-sub"><?php _e('Hizmet hesabı ve arama'); ?></div>
            <div class="form-grid">
                <div class="field"><label for="slBindDn"><?php _e('Hizmet hesabı (DN)'); ?></label><input type="text" id="slBindDn" class="mono" maxlength="1024" spellcheck="false" placeholder="CN=pops-svc,OU=Servis,DC=okul,DC=local"></div>
                <div class="field"><label for="slBindPw"><?php _e('Hizmet hesabının şifresi'); ?></label><input type="password" id="slBindPw" maxlength="1024" autocomplete="new-password"><div class="field-hint" id="slBindPwHint"></div></div>
            </div>
            <div class="field"><label for="slBase"><?php _e('Arama kökü (base DN)'); ?></label><input type="text" id="slBase" class="mono" maxlength="1024" spellcheck="false" placeholder="DC=okul,DC=local"></div>
            <div class="form-grid">
                <div class="field"><label for="slFilter"><?php _e('Kullanıcı filtresi'); ?></label><input type="text" id="slFilter" class="mono" maxlength="512" spellcheck="false"><div class="field-hint"><?php _e('{username} girilen kullanıcı adıdır (filtre için kaçırılır).'); ?></div></div>
                <div class="field"><label for="slUserAttr"><?php _e('Kullanıcı adı özniteliği'); ?></label><input type="text" id="slUserAttr" class="mono" maxlength="64" spellcheck="false"></div>
            </div>
            <div class="form-grid">
                <div class="field"><label for="slGroupBase"><?php _e('Grup arama kökü (isteğe bağlı)'); ?></label><input type="text" id="slGroupBase" class="mono" maxlength="1024" spellcheck="false" placeholder="OU=Gruplar,DC=okul,DC=local"></div>
                <div class="field"><label for="slGroupFilter"><?php _e('Grup filtresi'); ?></label><input type="text" id="slGroupFilter" class="mono" maxlength="512" spellcheck="false"></div>
            </div>
            <div class="field-hint" style="margin:-8px 0 0"><?php _e('Gruplar kullanıcının memberOf özniteliğinden okunur; grup arama kökü verilirse bu filtreyle de aranır ({user_dn}, {username}). İç içe AD grupları için: (member:1.2.840.113556.1.4.1941:={user_dn})'); ?></div>
            <div class="sso-sub"><?php _e('Grup → rol'); ?></div>
            <div class="smap" id="slMap"></div>
            <button type="button" class="btn ghost sm" id="slMapAdd" style="align-self:flex-start;margin-top:8px"><?php echo pops_icon('plus', 'sm'); ?><?php _e('Eşleme ekle'); ?></button>
            <div class="field-hint" style="margin-top:6px"><?php _e('Eşlenen bir grupta olmayan dizin hesabı giremez. Birden çok grup eşleşirse en yüksek rol ve bütün eşleşmelerin sayfaları geçerli olur. Rol ve sayfalar her girişte yeniden yazılır.'); ?></div>
            <div class="sso-sub"><?php _e('Bağlantıyı sına'); ?></div>
            <div class="sso-test">
                <input type="text" id="slTestUser" maxlength="256" autocomplete="off" spellcheck="false" placeholder="<?php _e('Kullanıcı adı (isteğe bağlı)'); ?>" aria-label="<?php _e('Sınanacak kullanıcı adı'); ?>">
                <button type="button" class="btn secondary sm" id="slTest"><?php _e('Bağlantıyı sına'); ?></button>
            </div>
            <div class="sso-result" id="slResult" hidden></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="slSave"><?php _e('Kaydet'); ?></button>
        </div>
    </div>
</div>

<div class="modal-overlay" id="ssoOidcModal">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title">OpenID Connect</div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x', 'sm'); ?></button>
        </div>
        <div class="modal-body sso-form">
            <label class="switch-field" style="margin-bottom:var(--space-4)"><span class="switch"><input type="checkbox" id="soEnabled"><span></span></span><?php _e('Giriş ekranında sağlayıcı düğmesini göster'); ?></label>
            <div class="sso-sub"><?php _e('Sağlayıcı'); ?></div>
            <div class="form-grid">
                <div class="field"><label for="soName"><?php _e('Düğmedeki ad'); ?></label><input type="text" id="soName" maxlength="60" placeholder="<?php _e('Örn. Okul hesabı'); ?>"></div>
                <div class="field"><label for="soIssuer"><?php _e('Sağlayıcı adresi (issuer)'); ?></label><input type="text" id="soIssuer" class="mono" maxlength="512" spellcheck="false" placeholder="https://login.microsoftonline.com/…/v2.0"></div>
            </div>
            <div class="form-grid">
                <div class="field"><label for="soClient"><?php _e('İstemci kimliği (client ID)'); ?></label><input type="text" id="soClient" class="mono" maxlength="512" spellcheck="false"></div>
                <div class="field"><label for="soSecret"><?php _e('İstemci sırrı'); ?></label><input type="password" id="soSecret" maxlength="2048" autocomplete="new-password"><div class="field-hint" id="soSecretHint"></div></div>
            </div>
            <div class="field"><label for="soRedirect"><?php _e('Dönüş adresi (redirect URI)'); ?></label><input type="text" id="soRedirect" class="mono" maxlength="512" spellcheck="false"><div class="field-hint"><?php _e('Sağlayıcıya bu adresi kaydedin. Panelin https adresi ve /api/auth/oidc/callback olmalı.'); ?></div></div>
            <div class="form-grid">
                <div class="field"><label for="soScopes"><?php _e('Kapsamlar (scopes)'); ?></label><input type="text" id="soScopes" class="mono" maxlength="256" spellcheck="false"></div>
                <div class="field"><label for="soUserClaim"><?php _e('Kullanıcı adı talebi'); ?></label><input type="text" id="soUserClaim" class="mono" maxlength="64" spellcheck="false" placeholder="email"><div class="field-hint"><?php _e('E-posta yalnızca doğrulanmışsa kabul edilir. Entra ID: preferred_username'); ?></div></div>
                <div class="field"><label for="soGroupClaim"><?php _e('Grup talebi'); ?></label><input type="text" id="soGroupClaim" class="mono" maxlength="64" spellcheck="false" placeholder="groups"></div>
            </div>
            <div class="field"><label for="soCa"><?php _e('CA sertifikası (PEM, isteğe bağlı)'); ?></label>
                <textarea id="soCa" class="mono" rows="3" spellcheck="false" placeholder="-----BEGIN CERTIFICATE-----"></textarea>
                <div class="field-hint"><?php _e('Yalnızca sağlayıcı kurum içi bir CA ile imzalıysa (ör. kurum içi Keycloak, AD FS).'); ?></div>
            </div>
            <div class="sso-sub"><?php _e('Grup → rol'); ?></div>
            <div class="smap" id="soMap"></div>
            <button type="button" class="btn ghost sm" id="soMapAdd" style="align-self:flex-start;margin-top:8px"><?php echo pops_icon('plus', 'sm'); ?><?php _e('Eşleme ekle'); ?></button>
            <div class="field-hint" style="margin-top:6px"><?php _e('Grup talebindeki değer (Entra ID: grup nesne kimliği; Keycloak: /grup-yolu). Birden çok grup eşleşirse en yüksek rol geçerli olur.'); ?></div>
            <div class="sso-sub"><?php _e('E-posta alan adı'); ?></div>
            <div class="field"><label for="soDomains"><?php _e('İzinli alan adları'); ?></label><input type="text" id="soDomains" class="mono" maxlength="1000" spellcheck="false" placeholder="okul.k12.tr"><div class="field-hint"><?php _e('Virgülle ayırın. Doluysa yalnızca doğrulanmış e-postası bu alan adlarında olanlar girer.'); ?></div></div>
            <div class="field"><span class="field-label"><?php _e('Eşlenen grubu olmayanlara varsayılan rol'); ?></span>
                <div class="segmented block" id="soDefRole" role="group" aria-label="<?php _e('Varsayılan rol'); ?>">
                    <button type="button" data-drole="" aria-pressed="false"><?php _e('Yok (giremez)'); ?></button>
                    <button type="button" data-drole="viewer" aria-pressed="false"><?php _e('İzleyici'); ?></button>
                    <button type="button" data-drole="admin" aria-pressed="false"><?php _e('Yönetici'); ?></button>
                </div>
            </div>
            <div class="perm-grid" id="soDefPages"></div>
            <div class="sso-sub"><?php _e('Bağlantıyı sına'); ?></div>
            <div class="sso-test"><button type="button" class="btn secondary sm" id="soTest"><?php _e('Bağlantıyı sına'); ?></button></div>
            <div class="sso-result" id="soResult" hidden></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="soSave"><?php _e('Kaydet'); ?></button>
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
    // Metinler Türkçe anahtardır; gösterilirken çevrilir
    const ROLES = {
        superadmin: { word: 'Süper admin', hint: 'Her şeye erişir: bütün sayfalar, kullanıcılar ve Sistem.' },
        admin: { word: 'Yönetici', hint: 'Seçilen sayfalarda günlük işleri yapar.' },
        viewer: { word: 'İzleyici', hint: 'Seçilen sayfaları yalnızca görüntüler; Dağıtım, Uzak komut ve Ayarlar kapalıdır.' }
    };
    const roleWord = (r) => (ROLES[r] ? POps.t(ROLES[r].word) : r || '—');
    // Kimlik kaynağı: yerel şifre ya da dizin / OpenID Connect (bkz. Güvenlik → Kimlik sağlayıcıları)
    const SOURCES = { local: 'Yerel', ldap: 'Dizin (LDAP)', oidc: 'OpenID Connect' };
    const isSso = (u) => !!(u && u.auth_source && u.auth_source !== 'local');
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
        if (u.role === 'superadmin') return POps.t('Bütün sayfalar');
        const n = permsOf(u).filter(k => PAGES[k]).length;
        return n ? POps.tn('{n} sayfa', n) : POps.t('Yalnızca kontrol merkezi');
    }
    function rowHtml(u) {
        const self = u.username === ME;
        const initial = String(u.username || '?').charAt(0).toLocaleUpperCase('tr');
        const when = loginDate(u.last_login);
        const lastHtml = when ? POps.timeHtml(when) : '<span class="faint">' + POps.tHtml('Hiç girmedi') + '</span>';
        return `<tr data-id="${Number(u.id)}" tabindex="0" class="${focusId === u.id ? 'is-focus' : ''}">
            <td><div class="nm"><span class="av" aria-hidden="true">${escapeHtml(initial)}</span><span>${escapeHtml(u.username)}${self ? '<span class="you">' + POps.tHtml('siz') + '</span>' : ''}</span>${isSso(u) ? `<span class="badge muted">${escapeHtml(POps.t(SOURCES[u.auth_source] || u.auth_source))}</span>` : ''}</div></td>
            <td>${escapeHtml(roleWord(u.role))}</td>
            <td class="hide-sm col-access" title="${escapeHtml(u.role === 'superadmin' ? '' : permsOf(u).map(pageName).join(', '))}">${escapeHtml(accessText(u))}</td>
            <td class="when">${lastHtml}</td>
        </tr>`;
    }
    function renderUsers() {
        const body = $('userBody');
        if (!users.length) {
            POps.setEmpty(body, { tag: 'tr', colspan: 4, icon: 'users', title: POps.t('Kayıtlı kullanıcı yok') });
            return;
        }
        body.innerHTML = users.map(rowHtml).join('');
    }
    function renderSummary() {
        if (usersLoaded) {
            const supers = users.filter(u => u.role === 'superadmin').length;
            const boldHtml = (x) => `<b>${Number(x)}</b>`;
            $('sumUsers').innerHTML = POps.tnHtml('{n} kullanıcı', users.length, null, { n: boldHtml(users.length) })
                + (supers && supers < users.length ? ' · ' + POps.tnHtml('{n} süper admin', supers, null, { n: boldHtml(supers) }) : '');
        }
    }
    async function loadUsers() {
        try {
            const r = await POps.get('/api/admin/users');
            users = (r && Array.isArray(r.users)) ? r.users : [];
            usersLoaded = true;
        } catch (e) {
            POps.setError($('userBody'), e, { tag: 'tr', colspan: 4 });
            $('sumUsers').textContent = POps.t('Kullanıcılar alınamadı');
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
        const pagesText = u.role === 'superadmin' ? POps.t('Bütün sayfalar') : (perms.length ? perms.map(pageName).join(', ') : POps.t('Yalnızca kontrol merkezi'));
        const factsHtml = `<div class="grow"><span>${POps.tHtml('Rol')}</span><span>${escapeHtml(roleWord(u.role))}</span></div>`
            + `<div class="grow"><span>${POps.tHtml('Sayfalar')}</span><span>${escapeHtml(pagesText)}</span></div>`
            + `<div class="grow"><span>${POps.tHtml('Son giriş')}</span><span>${when ? POps.timeHtml(when) : POps.tHtml('Hiç girmedi')}</span></div>`
            + (self && twofaOn !== null ? `<div class="grow"><span>2FA</span><span>${twofaOn ? POps.tHtml('Açık') : escapeHtml(POps.tx('Kapalı', 'switch'))}</span></div>` : '')
            + `<div class="grow"><span>${POps.tHtml('Kimlik kaynağı')}</span><span>${escapeHtml(POps.t(SOURCES[u.auth_source] || SOURCES.local))}</span></div>`
            + `<div class="grow"><span>${POps.tHtml('Kimlik')}</span><span>#${Number(u.id)}</span></div>`;
        const canDelete = u.role !== 'superadmin' && !self;
        const actionsHtml = IS_SUPER
            ? `<div class="set uact">
                <button type="button" class="srow" data-act="edit">${POps.iconHtml('sliders', 'sm')}<span class="grow">${POps.tHtml('Rolü ve yetkileri düzenle')}</span>${POps.iconHtml('right', 'sm')}</button>
                ${isSso(u) ? '' : `<button type="button" class="srow" data-act="password">${POps.iconHtml('key', 'sm')}<span class="grow">${POps.tHtml('Şifreyi sıfırla')}</span>${POps.iconHtml('right', 'sm')}</button>`}
                ${canDelete ? `<button type="button" class="srow danger" data-act="delete">${POps.iconHtml('trash', 'sm')}<span class="grow">${POps.tHtml('Kullanıcıyı sil')}</span></button>` : ''}
              </div>`
              + (canDelete ? '' : `<div class="dnote">${self ? POps.tHtml('Kendi hesabınızı silemezsiniz.') : POps.tHtml('Süper admin hesabı silinemez; silmek için önce rolünü değiştirin.')}</div>`)
              + (isSso(u) ? `<div class="dnote">${POps.tHtml('Yerel şifresi yoktur. Rol ve sayfalar her girişte dizindeki gruplardan yeniden yazılır.')}</div>` : '')
            : '<div class="dnote">' + POps.tHtml('Kullanıcıları yalnızca süper admin düzenleyebilir.') + '</div>';
        return `<div class="drawer-head">
                <div class="drawer-title">
                    <span class="drawer-ico">${POps.iconHtml('user', 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(u.username)}</h2><div class="sub">${escapeHtml(roleWord(u.role))}${self ? ' · ' + POps.tHtml('siz') : ''}</div></div>
                </div>
                <button type="button" class="ibtn sm" data-act="close" data-tip="${escapeHtml(POps.t('Kapat (Esc)'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button>
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
        $('umRoleHint').textContent = POps.t(ROLES[r].hint);
        $('umPerms').hidden = r === 'superadmin';
        // İzleyici bu sayfaları yetki verilse de açamaz (includes/header.php)
        document.querySelectorAll('#umPerms .check').forEach(l => {
            const blocked = r === 'viewer' && VIEWER_BLOCKED.includes(l.dataset.page);
            l.classList.toggle('is-blocked', blocked);
            l.querySelector('input').disabled = blocked;
            l.title = blocked ? POps.t('İzleyici bu sayfayı açamaz') : '';
        });
        $('umPermsHint').textContent = r === 'viewer' ? POps.t('Kontrol merkezi herkese açıktır. İzleyici Dağıtım, Uzak komut ve Ayarlar sayfalarını açamaz.') : POps.t('Kontrol merkezi herkese açıktır.');
    }
    let formSrc = 'local';
    // Şifre alanı: yeni yerel hesapta ve dizin/OIDC hesabı yerele dönerken (var olan yerel hesabın şifresi
    // "Şifreyi sıfırla" ile değişir)
    function setSrc(src) {
        formSrc = SOURCES[src] ? src : 'local';
        $('umSrc').querySelectorAll('button').forEach(b => { const on = b.dataset.src === formSrc; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        const back = isSso(editing) && formSrc === 'local';
        $('umPassField').hidden = !(formSrc === 'local' && (!editing || back));
        $('umSrcHint').textContent = formSrc === 'local'
            ? (back ? POps.t('Yerel hesaba dönen kullanıcıya yeni bir şifre verin.') : '')
            : POps.t('Yerel şifresi olmaz. İlk girişte dizindeki ya da sağlayıcıdaki aynı adlı hesaba bağlanır; rol ve sayfalar o zaman grup eşlemesinden yazılır.');
    }
    function clearErrors() { document.querySelectorAll('#userModal .field.has-error').forEach(f => f.classList.remove('has-error')); }
    function openEditor(u) {
        editing = u || null;
        clearErrors();
        $('umTitle').textContent = u ? POps.t('Kullanıcıyı düzenle') : POps.t('Kullanıcı ekle');
        $('umSave').textContent = u ? POps.t('Değişiklikleri kaydet') : POps.t('Kullanıcıyı ekle');
        $('umName').value = u ? u.username : '';
        $('umPass').value = '';
        // Kimlik kaynağı yalnızca bir sağlayıcı ayarlıysa ya da hesap zaten dizin/OIDC hesabıysa sorulur
        $('umSrcField').hidden = !(isSso(u) || (sso && (sso.ldap.host || sso.oidc.issuer)));
        setSrc(u ? (u.auth_source || 'local') : 'local');
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
        if (!$('umPassField').hidden && !password) { $('umPassField').classList.add('has-error'); bad = true; }
        if (bad) { (username ? $('umPass') : $('umName')).focus(); return; }
        // Görünmeyen (ör. süper admine geçince gizlenen) seçimler de korunur
        const perms = [...document.querySelectorAll('.perm-cb')].filter(cb => cb.checked).map(cb => cb.value);
        if (editing) permsOf(editing).filter(k => !PAGES[k]).forEach(k => perms.push(k));   // panelin bilmediği eski anahtarlar silinmesin
        const payload = { username, role: formRole, permissions: JSON.stringify(perms), auth_source: formSrc };
        if (!$('umPassField').hidden) payload.password = password;
        const target = editing;
        const ok = await POps.act($('umSave'), () => target
            ? POps.api('/api/admin/users/' + encodeURIComponent(target.id), { method: 'PUT', body: payload })
            : POps.post('/api/admin/users', payload), { success: target ? POps.t('{name} güncellendi.', { name: username }) : POps.t('{name} eklendi.', { name: username }) });
        if (!ok) return;
        closeModal('userModal');
        await loadUsers();
        if (!target) { const nu = users.find(x => x.username === username); if (nu) openUser(nu.id); }
    }
    async function resetPassword(u) {
        const self = u.username === ME;
        const pw = await POps.prompt({
            title: POps.t('{name} için yeni şifre', { name: u.username }),
            message: self ? POps.t('Şifreyi değiştirince yeniden giriş yapmanız gerekir.') : POps.t('Kullanıcının açık oturumları kapanır; yeni şifreyle yeniden girer.'),
            label: POps.t('Yeni şifre'), inputType: 'password', autocomplete: 'new-password', trim: false,
            hint: POps.t('En az 8 karakter önerilir.'), confirmText: POps.t('Şifreyi değiştir'), icon: 'key'
        });
        if (pw === null) return;
        const payload = { username: u.username, role: u.role, permissions: JSON.stringify(permsOf(u)), password: pw };
        if (await POps.act(null, () => POps.api('/api/admin/users/' + encodeURIComponent(u.id), { method: 'PUT', body: payload }), { success: POps.t('{name} için şifre değişti.', { name: u.username }) })) loadUsers();
    }
    async function deleteUser(u) {
        const ok = await POps.confirm({ title: POps.t('{name} silinsin mi?', { name: u.username }), message: POps.t('Kullanıcının açık oturumları da kapanır. Bu işlem geri alınamaz.'), confirmText: POps.t('Kullanıcıyı sil'), danger: true, icon: 'trash' });
        if (!ok) return;
        if (await POps.act(null, () => POps.del('/api/admin/users/' + encodeURIComponent(u.id)), { success: POps.t('{name} silindi.', { name: u.username }) })) {
            POps.drawer.close();
            loadUsers();
        }
    }
    if (IS_SUPER) {
        $('addUserBtn').addEventListener('click', () => openEditor(null));
        $('umRole').addEventListener('click', (e) => { const b = e.target.closest('button[data-role]'); if (b) setRole(b.dataset.role); });
        $('umSrc').addEventListener('click', (e) => { const b = e.target.closest('button[data-src]'); if (b) setSrc(b.dataset.src); });
        $('umSave').addEventListener('click', saveUser);
        $('userModal').addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.tagName === 'INPUT' && e.target.type !== 'checkbox') { e.preventDefault(); saveUser(); } });
        ['umName', 'umPass'].forEach(id => $(id).addEventListener('input', (e) => e.target.closest('.field').classList.remove('has-error')));
    }

    // ================= İKİ ADIMLI DOĞRULAMA =================
    function renderTwofa(enabled) {
        twofaOn = enabled;
        $('twofaSetup').hidden = true;
        $('twofaState').innerHTML = enabled ? '<span class="dot ok"></span>' + POps.tHtml('Açık') : '<span class="dot off"></span>' + escapeHtml(POps.tx('Kapalı', 'switch'));
        $('twofaDesc').textContent = enabled ? POps.t('Girişte doğrulama kodu istenir.') : POps.t('Önerilir, zorunlu değildir.');
        const b = $('twofaBtn');
        b.hidden = false;
        b.textContent = enabled ? POps.t("2FA'yı kapat") : POps.t("2FA'yı kur");
        b.dataset.mode = enabled ? 'disable' : 'setup';
        $('sumTwofa').hidden = false;
        $('sumTwofa').innerHTML = enabled ? '<span class="dot ok"></span>' + POps.tHtml('2FA açık') : '<span class="dot off"></span>' + POps.tHtml('2FA kapalı');
        if (typeof popsTwofaNudge === 'function') popsTwofaNudge(enabled);
        const u = focusId !== null && users.find(x => x.id === focusId);
        if (u && u.username === ME && POps.drawer.isOpen('user:' + u.id)) renderDrawer(u);
    }
    async function loadTwofa() {
        try {
            const r = await POps.get('/api/admin/2fa/status');
            renderTwofa(!!(r && r.enabled));
        } catch (e) {
            $('twofaState').innerHTML = '<span class="dot bad"></span>' + POps.tHtml('Alınamadı');
            $('twofaDesc').textContent = POps.errorMessage(e);
        }
    }
    async function twofaSetup(btn) {
        let r;
        try { r = await POps.busy(btn, () => POps.post('/api/admin/2fa/setup')); }
        catch (e) { POps.toast('error', POps.t('Kurulum başlatılamadı: {error}', { error: POps.errorMessage(e) })); return; }
        $('twofaSecret').value = (r && r.secret) || '';
        const qr = $('twofaQr');
        qr.replaceChildren();
        if (typeof QRCode !== 'undefined' && r && r.otpauth_uri) new QRCode(qr, { text: r.otpauth_uri, width: 180, height: 180 });
        else qr.textContent = POps.t('QR kodu oluşturulamadı; yandaki anahtarı elle girin.');
        $('twofaCode').value = '';
        $('twofaSetup').hidden = false;
        $('twofaBtn').hidden = true;
        $('twofaCode').focus();
    }
    async function twofaEnable() {
        const code = $('twofaCode').value.trim();
        if (!/^\d{6}$/.test(code)) { $('twofaCode').classList.add('is-invalid'); $('twofaCode').focus(); POps.toast('warning', POps.t('6 haneli kodu girin.')); return; }
        if (await POps.act($('twofaEnableBtn'), () => POps.post('/api/admin/2fa/enable', { otp: code }), { success: POps.t('2FA açıldı. Bir sonraki girişte kod istenecek.') })) loadTwofa();
    }
    async function twofaDisable() {
        const code = await POps.prompt({ title: POps.t("2FA'yı kapatmak istiyor musunuz?"), message: POps.t('Doğrulama uygulamasındaki 6 haneli kodu girin.'), label: POps.t('Kod'), placeholder: '123456', maxLength: 6, inputMode: 'numeric', confirmText: POps.t("2FA'yı kapat"), danger: true,
            validate: (v) => /^\d{6}$/.test(v.trim()) ? null : POps.t('6 haneli kodu girin.') });
        if (code === null) return;
        if (await POps.act($('twofaBtn'), () => POps.post('/api/admin/2fa/disable', { otp: code.trim() }), { success: POps.t('2FA kapatıldı.') })) loadTwofa();
    }
    $('twofaBtn').addEventListener('click', (e) => { if (e.currentTarget.dataset.mode === 'disable') twofaDisable(); else twofaSetup(e.currentTarget); });
    $('twofaEnableBtn').addEventListener('click', twofaEnable);
    $('twofaCode').addEventListener('input', (e) => e.target.classList.remove('is-invalid'));
    $('twofaCode').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); twofaEnable(); } });
    $('twofaCancelBtn').addEventListener('click', () => { $('twofaSetup').hidden = true; $('twofaBtn').hidden = false; });
    $('twofaCopy').addEventListener('click', async () => {
        try { await navigator.clipboard.writeText($('twofaSecret').value); POps.toast('success', POps.t('Anahtar kopyalandı.')); }
        catch (e) { $('twofaSecret').select(); POps.toast('warning', POps.t('Kopyalanamadı; anahtarı seçip elle kopyalayın.')); }
    });
    $('twofaSecret').addEventListener('click', (e) => e.target.select());

    // ================= API JETONLARI (yalnızca süper admin) =================
    // Liste ve iptal: /api/tokens. Jeton yalnızca oluşturma yanıtında gelir ve bir kez gösterilir.
    // Metinler Türkçe anahtardır; gösterilirken çevrilir
    const TOKEN_ROLES = {
        viewer: { word: 'Görüntüleyici', hint: 'Yalnızca okur (GET): cihazlar, görevler, raporlar.' },
        admin: { word: 'Yönetici', hint: 'Günlük işleri yapar (görev, sınıf, karantina). Süper admin işlemleri, kullanıcılar, jetonlar ve uzak ekran kapalıdır.' }
    };
    const TOKEN_STATES = { active: ['ok', 'Etkin'], expired: ['off', 'Süresi doldu'], revoked: ['off', 'İptal edildi'] };
    let apiTokens = [];
    let tokenRole = 'viewer';
    function tokenRowHtml(t) {
        const st = TOKEN_STATES[t.state] ? [TOKEN_STATES[t.state][0], POps.t(TOKEN_STATES[t.state][1])] : ['off', t.state || '—'];
        const role = TOKEN_ROLES[t.role] ? POps.t(TOKEN_ROLES[t.role].word) : t.role || '—';
        const metaHtml = `<code>pops_${escapeHtml(t.token_prefix || '')}…</code>`
            + ' · ' + POps.tHtml('oluşturuldu {time}', null, { time: POps.timeHtml(t.created_at) }) + (t.created_by ? ' · ' + escapeHtml(t.created_by) : '')
            + ' · ' + (t.last_used_at ? POps.tHtml('son kullanım {time}', null, { time: POps.timeHtml(t.last_used_at) }) : POps.tHtml('hiç kullanılmadı'))
            + ' · ' + (t.revoked_at ? POps.tHtml('iptal {time}', null, { time: POps.timeHtml(t.revoked_at) }) : t.expires_at ? POps.tHtml('bitiş {time}', null, { time: POps.timeHtml(t.expires_at) }) : POps.tHtml('süresiz'));
        return `<div class="srow tok-row${t.state === 'active' ? '' : ' is-off'}">
            ${POps.iconHtml('key', 'lead')}
            <div class="grow">
                <div class="t"><span>${escapeHtml(t.name)}</span><span class="badge muted">${escapeHtml(role)}</span></div>
                <div class="d">${metaHtml}</div>
            </div>
            <span class="st"><span class="dot ${escapeHtml(st[0])}"></span>${escapeHtml(st[1])}</span>
            ${t.state === 'revoked' ? '' : `<button type="button" class="ibtn sm" data-revoke="${Number(t.id)}" data-tip="${escapeHtml(POps.t('Jetonu iptal et'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('{name} jetonunu iptal et', { name: t.name }))}">${POps.iconHtml('trash', 'sm')}</button>`}
        </div>`;
    }
    function renderApiTokens() {
        const active = apiTokens.filter(t => t.state === 'active').length;
        $('tokSummary').textContent = apiTokens.length
            ? POps.t('{n} etkin', { n: active }) + (apiTokens.length > active ? ', ' + POps.t('{n} iptal edilmiş ya da süresi dolmuş', { n: apiTokens.length - active }) : '')
            : POps.t('Henüz jeton yok.');
        $('tokList').hidden = !apiTokens.length;
        $('tokList').innerHTML = apiTokens.map(tokenRowHtml).join('');
    }
    async function loadApiTokens() {
        try { apiTokens = (await POps.get('/api/tokens')) || []; }
        catch (e) { $('tokSummary').textContent = POps.t('Jetonlar alınamadı: {error}', { error: POps.errorMessage(e) }); return; }
        renderApiTokens();
    }
    function setTokenRole(r) {
        tokenRole = r;
        $('tkRole').querySelectorAll('button').forEach(b => { const on = b.dataset.trole === r; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        $('tkRoleHint').textContent = POps.t(TOKEN_ROLES[r].hint);
    }
    async function createApiToken() {
        document.querySelectorAll('#tokModal .field.has-error').forEach(f => f.classList.remove('has-error'));
        const name = $('tkName').value.trim();
        const daysText = $('tkDays').value.trim();
        const days = daysText === '' ? null : Number(daysText);
        let bad = false;
        if (!/^[\p{L}\p{N}_ .-]{1,64}$/u.test(name)) { $('tkNameField').classList.add('has-error'); bad = true; }
        if (days !== null && !(Number.isInteger(days) && days >= 1 && days <= 3650)) { $('tkDaysField').classList.add('has-error'); bad = true; }
        if (bad) return;
        let d = null;
        const ok = await POps.act($('tkCreate'), async () => { d = await POps.post('/api/tokens', { name, role: tokenRole, expires_days: days }); });
        if (!ok || !d) return;
        closeModal('tokModal');
        loadApiTokens();
        await POps.alert({
            title: POps.t('API jetonu hazır'), icon: 'key', codes: [d.token], confirmText: POps.t('Kopyaladım, kapat'),
            message: d.name + ' · ' + (TOKEN_ROLES[d.role] ? POps.t(TOKEN_ROLES[d.role].word) : d.role) + ' · ' + (d.expires_at ? POps.t('{date} tarihine kadar', { date: POps.fullTime(d.expires_at) }) : POps.t('süresiz')),
            note: POps.t('Jeton bir daha gösterilmez; şimdi kopyalayıp güvenli bir yerde saklayın. İsteklerde Authorization: Bearer <jeton> başlığıyla /api/v1 uçlarına gönderin.')
        });
    }
    async function revokeApiToken(id) {
        const t = apiTokens.find(x => x.id === id);
        if (!t) return;
        const ok = await POps.confirm({ title: POps.t('{name} jetonu iptal edilsin mi?', { name: t.name }), message: POps.t('Bu jetonu kullanan betikler hemen erişimini kaybeder. Bu işlem geri alınamaz; jeton listede iptal edilmiş olarak kalır.'), confirmText: POps.t('Jetonu iptal et'), danger: true, icon: 'trash' });
        if (!ok) return;
        if (await POps.act(null, () => POps.del('/api/tokens/' + encodeURIComponent(id)), { success: POps.t('{name} iptal edildi.', { name: t.name }) })) loadApiTokens();
    }
    if (IS_SUPER && $('tokNew')) {
        $('tokNew').addEventListener('click', () => {
            $('tkName').value = '';
            $('tkDays').value = '90';
            document.querySelectorAll('#tokModal .field.has-error').forEach(f => f.classList.remove('has-error'));
            setTokenRole('viewer');
            openModal('tokModal');
            setTimeout(() => $('tkName').focus(), 50);
        });
        $('tkRole').addEventListener('click', (e) => { const b = e.target.closest('button[data-trole]'); if (b) setTokenRole(b.dataset.trole); });
        $('tkCreate').addEventListener('click', createApiToken);
        $('tokModal').addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.tagName === 'INPUT') { e.preventDefault(); createApiToken(); } });
        ['tkName', 'tkDays'].forEach(id => $(id).addEventListener('input', (e) => e.target.closest('.field').classList.remove('has-error')));
        $('tokList').addEventListener('click', (e) => { const b = e.target.closest('[data-revoke]'); if (b) revokeApiToken(Number(b.dataset.revoke)); });
        loadApiTokens();
    }

    // ================= KİMLİK SAĞLAYICILARI (yalnızca süper admin) =================
    // LDAP / AD ve OpenID Connect: /api/sso/settings. Sırlar sunucudan dönmez; şifre alanı boş bırakılırsa kayıtlı
    // sır kalır. Sunucu ya da hesap değişirse sunucu sırrın yeniden yazılmasını ister. Ayrıntı: docs/security.md
    const SSO_ROLES = { viewer: 'İzleyici', admin: 'Yönetici', superadmin: 'Süper admin' };
    const SSO_SEC = { ldaps: 'LDAPS', starttls: 'StartTLS', plain: 'LDAP' };
    let sso = null;
    let ldapSec = 'ldaps';
    let oidcDefRole = '';
    function ssoPagesHtml(selected) {
        return Object.keys(PAGES).map(k => `<label class="check" data-page="${escapeHtml(k)}"><input type="checkbox" value="${escapeHtml(k)}"${selected.includes(k) ? ' checked' : ''}>${escapeHtml(POps.t(PAGES[k]))}</label>`).join('');
    }
    // İzleyici Dağıtım, Uzak komut ve Ayarlar'ı açamaz; süper admin her sayfayı açar (sayfa listesi gizlenir)
    function ssoPagesFor(grid, role) {
        grid.hidden = !role || role === 'superadmin';
        grid.querySelectorAll('.check').forEach(l => {
            const blocked = role === 'viewer' && VIEWER_BLOCKED.includes(l.dataset.page);
            l.classList.toggle('is-blocked', blocked);
            l.querySelector('input').disabled = blocked;
        });
    }
    function ssoMapRowHtml(m, dn) {
        const role = SSO_ROLES[m.role] ? m.role : 'viewer';
        const opts = Object.keys(SSO_ROLES).map(r => `<option value="${escapeHtml(r)}"${r === role ? ' selected' : ''}>${escapeHtml(POps.t(SSO_ROLES[r]))}</option>`).join('');
        const ph = dn ? 'CN=POps-Yoneticiler,OU=Gruplar,DC=okul,DC=local' : 'pops-admins';
        return `<div class="smap-row">
            <div class="smap-top">
                <input type="text" class="smap-group mono" maxlength="512" spellcheck="false" value="${escapeHtml(m.group || '')}" placeholder="${escapeHtml(ph)}" aria-label="${escapeHtml(POps.t('Grup'))}">
                <select class="smap-role" aria-label="${escapeHtml(POps.t('Rol'))}">${opts}</select>
                <button type="button" class="ibtn sm smap-del" data-tip="${escapeHtml(POps.t('Eşlemeyi kaldır'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Eşlemeyi kaldır'))}">${POps.iconHtml('trash', 'sm')}</button>
            </div>
            <div class="perm-grid smap-pages">${ssoPagesHtml(m.pages || [])}</div>
        </div>`;
    }
    function ssoRenderMap(box, rows, dn) {
        box.innerHTML = rows.length ? rows.map(m => ssoMapRowHtml(m, dn)).join('') : `<div class="smap-empty">${POps.tHtml('Henüz eşleme yok: kimse bu yolla giremez.')}</div>`;
        box.querySelectorAll('.smap-row').forEach(row => ssoPagesFor(row.querySelector('.smap-pages'), row.querySelector('.smap-role').value));
    }
    function ssoReadMap(box) {
        return [...box.querySelectorAll('.smap-row')].map(row => {
            const role = row.querySelector('.smap-role').value;
            const pages = role === 'superadmin' ? [] : [...row.querySelectorAll('.smap-pages input:checked:not(:disabled)')].map(i => i.value);
            return { group: row.querySelector('.smap-group').value.trim(), role, pages };
        }).filter(m => m.group);
    }
    function ssoWireMap(box, addBtn, dn) {
        box.addEventListener('change', (e) => { if (e.target.classList.contains('smap-role')) ssoPagesFor(e.target.closest('.smap-row').querySelector('.smap-pages'), e.target.value); });
        box.addEventListener('click', (e) => {
            const del = e.target.closest('.smap-del');
            if (!del) return;
            const rows = ssoReadMap(box);
            const all = [...box.querySelectorAll('.smap-row')];
            rows.splice(all.indexOf(del.closest('.smap-row')), 1);
            ssoRenderMap(box, rows, dn);
        });
        addBtn.addEventListener('click', () => {
            const rows = [...box.querySelectorAll('.smap-row')].map(row => ({ group: row.querySelector('.smap-group').value, role: row.querySelector('.smap-role').value, pages: [...row.querySelectorAll('.smap-pages input:checked')].map(i => i.value) }));
            rows.push({ group: '', role: 'viewer', pages: [] });
            ssoRenderMap(box, rows, dn);
            const inputs = box.querySelectorAll('.smap-group');
            inputs[inputs.length - 1].focus();
        });
    }
    function ssoResult(box, ok, lines) {
        box.hidden = false;
        box.className = 'sso-result alert ' + (ok ? 'success' : 'danger');
        const ul = POps.el('ul');
        lines.slice(1).forEach(t => ul.append(POps.el('li', { text: t })));
        box.replaceChildren(POps.el('div', null, [POps.el('div', { text: lines[0] }), lines.length > 1 ? ul : null]));
    }
    function ssoStateHtml(on) {
        return on ? `<span class="dot ok"></span>${POps.tHtml('Etkin')}` : `<span class="dot off"></span>${POps.tHtml('Devre dışı')}`;
    }
    function ssoHost(url) {
        try { return new URL(url).host; } catch (e) { return url; }
    }
    function renderSso() {
        const l = sso.ldap, o = sso.oidc;
        $('ssoLdapState').innerHTML = ssoStateHtml(l.enabled);
        $('ssoLdapDesc').textContent = l.host
            ? `${l.host}:${l.port} · ${SSO_SEC[l.security] || l.security} · ` + POps.tn('{n} grup eşlemesi', (l.group_map || []).length)
            : POps.t('Ayarlanmadı. Okulun Active Directory ya da LDAP sunucusuyla giriş.');
        $('ssoOidcState').innerHTML = ssoStateHtml(o.enabled);
        $('ssoOidcDesc').textContent = o.issuer
            ? `${o.display_name || 'OpenID Connect'} · ${ssoHost(o.issuer)} · ` + POps.tn('{n} grup eşlemesi', (o.group_map || []).length)
              + ((o.allowed_domains || []).length ? ' · ' + o.allowed_domains.join(', ') : '')
            : POps.t('Ayarlanmadı. Microsoft Entra ID, Google Workspace, Keycloak gibi bir sağlayıcıyla giriş.');
        $('ssoLdapEdit').disabled = false;
        $('ssoOidcEdit').disabled = false;
    }
    async function loadSso() {
        try { sso = await POps.get('/api/sso/settings'); }
        catch (e) {
            $('ssoLdapDesc').textContent = POps.t('Ayarlar alınamadı: {error}', { error: POps.errorMessage(e) });
            $('ssoOidcDesc').textContent = '';
            return;
        }
        renderSso();
    }
    function secretHint(has, word) {
        return has ? POps.t('Kayıtlı. Değiştirmek için yazın; boş bırakılırsa kayıtlı {what} kalır.', { what: word }) : POps.t('Henüz kaydedilmedi.');
    }

    // ---- LDAP / AD
    function setLdapSec(sec, keepPort) {
        const old = ldapSec;
        ldapSec = SSO_SEC[sec] && sec !== 'plain' ? sec : 'ldaps';
        $('slSec').querySelectorAll('button').forEach(b => { const on = b.dataset.sec === ldapSec; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        // Varsayılan port bağlantı türüyle birlikte değişir (elle yazılmış port kalır)
        const port = $('slPort').value.trim();
        if (!keepPort && old !== ldapSec && (port === '' || port === (old === 'ldaps' ? '636' : '389'))) $('slPort').value = ldapSec === 'ldaps' ? '636' : '389';
    }
    function openLdap() {
        const l = sso.ldap;
        $('slEnabled').checked = !!l.enabled;
        $('slHost').value = l.host || '';
        $('slPort').value = l.port || 636;
        setLdapSec(l.security, true);
        $('slCa').value = l.ca_pem || '';
        $('slBindDn').value = l.bind_dn || '';
        $('slBindPw').value = '';
        $('slBindPwHint').textContent = secretHint(l.has_secret, POps.t('şifre'));
        $('slBase').value = l.base_dn || '';
        $('slFilter').value = l.user_filter || '';
        $('slUserAttr').value = l.username_attribute || '';
        $('slGroupBase').value = l.group_base_dn || '';
        $('slGroupFilter').value = l.group_filter || '';
        ssoRenderMap($('slMap'), l.group_map || [], true);
        $('slTestUser').value = '';
        $('slResult').hidden = true;
        openModal('ssoLdapModal');
    }
    function ldapPayload() {
        const p = {
            enabled: $('slEnabled').checked, host: $('slHost').value.trim(), port: Number($('slPort').value) || 636,
            security: ldapSec, base_dn: $('slBase').value.trim(), bind_dn: $('slBindDn').value.trim(),
            user_filter: $('slFilter').value.trim(), username_attribute: $('slUserAttr').value.trim(),
            group_base_dn: $('slGroupBase').value.trim(), group_filter: $('slGroupFilter').value.trim(),
            group_map: ssoReadMap($('slMap')), ca_pem: $('slCa').value.trim(), timeout: sso.ldap.timeout || 5
        };
        if ($('slBindPw').value) p.bind_password = $('slBindPw').value;
        return p;
    }
    async function saveLdap() {
        let r = null;
        if (!await POps.act($('slSave'), async () => { r = await POps.api('/api/sso/settings/ldap', { method: 'PUT', body: ldapPayload() }); },
            { success: POps.t('Dizin ayarları kaydedildi.') })) return;
        sso.ldap = r;
        renderSso();
        closeModal('ssoLdapModal');
    }
    async function testLdap() {
        const box = $('slResult');
        const user = $('slTestUser').value.trim();
        let r;
        try { r = await POps.busy($('slTest'), () => POps.post('/api/sso/test/ldap', Object.assign(ldapPayload(), { test_username: user || null }))); }
        catch (e) { ssoResult(box, false, [POps.errorMessage(e)]); return; }
        if (!r || !r.ok) { ssoResult(box, false, [POps.t((r && r.message) || 'Sınama başarısız.')]); return; }
        const lines = [POps.t('Bağlantı ve hizmet hesabı çalışıyor ({security}).', { security: SSO_SEC[r.security] || r.security })];
        let ok = true;
        if (r.user_dn) {
            lines.push(POps.t('Kullanıcı: {dn}', { dn: r.user_dn }));
            lines.push((r.groups || []).length ? POps.t('Gruplar: {groups}', { groups: r.groups.join('; ') }) : POps.t('Grup bulunamadı.'));
            if (r.disabled) { ok = false; lines.push(POps.t('Hesap dizinde devre dışı: giremez.')); }
            else if (r.role) lines.push(POps.t('Girişteki rolü: {role}', { role: POps.t(SSO_ROLES[r.role] || r.role) }));
            else { ok = false; lines.push(POps.t('Eşlenen bir grupta değil: giremez.')); }
        }
        ssoResult(box, ok, lines);
    }

    // ---- OpenID Connect
    function setOidcDefRole(r) {
        oidcDefRole = r || '';
        $('soDefRole').querySelectorAll('button').forEach(b => { const on = b.dataset.drole === oidcDefRole; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        ssoPagesFor($('soDefPages'), oidcDefRole);
    }
    function openOidc() {
        const o = sso.oidc;
        $('soEnabled').checked = !!o.enabled;
        $('soName').value = o.display_name || '';
        $('soIssuer').value = o.issuer || '';
        $('soClient').value = o.client_id || '';
        $('soSecret').value = '';
        $('soSecretHint').textContent = secretHint(o.has_secret, POps.t('sır'));
        $('soRedirect').value = o.redirect_uri || (location.origin + '/api/auth/oidc/callback');
        $('soScopes').value = o.scopes || 'openid email profile';
        $('soUserClaim').value = o.username_claim || '';
        $('soGroupClaim').value = o.groups_claim || '';
        $('soCa').value = o.ca_pem || '';
        ssoRenderMap($('soMap'), o.group_map || [], false);
        $('soDomains').value = (o.allowed_domains || []).join(', ');
        $('soDefPages').innerHTML = ssoPagesHtml(o.default_pages || []);
        setOidcDefRole(o.default_role || '');
        $('soResult').hidden = true;
        openModal('ssoOidcModal');
    }
    function oidcPayload() {
        const p = {
            enabled: $('soEnabled').checked, display_name: $('soName').value.trim(), issuer: $('soIssuer').value.trim(),
            client_id: $('soClient').value.trim(), redirect_uri: $('soRedirect').value.trim(), scopes: $('soScopes').value.trim(),
            username_claim: $('soUserClaim').value.trim() || 'email', groups_claim: $('soGroupClaim').value.trim(),
            group_map: ssoReadMap($('soMap')), ca_pem: $('soCa').value.trim(),
            allowed_domains: $('soDomains').value.split(/[,\s]+/).map(x => x.trim()).filter(Boolean),
            default_role: oidcDefRole,
            default_pages: oidcDefRole ? [...$('soDefPages').querySelectorAll('input:checked:not(:disabled)')].map(i => i.value) : []
        };
        if ($('soSecret').value) p.client_secret = $('soSecret').value;
        return p;
    }
    async function saveOidc() {
        let r = null;
        if (!await POps.act($('soSave'), async () => { r = await POps.api('/api/sso/settings/oidc', { method: 'PUT', body: oidcPayload() }); },
            { success: POps.t('OpenID Connect ayarları kaydedildi.') })) return;
        sso.oidc = r;
        renderSso();
        closeModal('ssoOidcModal');
    }
    async function testOidc() {
        const box = $('soResult');
        let r;
        try { r = await POps.busy($('soTest'), () => POps.post('/api/sso/test/oidc', oidcPayload())); }
        catch (e) { ssoResult(box, false, [POps.errorMessage(e)]); return; }
        if (!r || !r.ok) { ssoResult(box, false, [POps.t((r && r.message) || 'Sınama başarısız.')]); return; }
        ssoResult(box, true, [
            POps.t('Sağlayıcının keşif belgesi okundu: {issuer}', { issuer: r.issuer }),
            POps.tn('{n} imza anahtarı', r.keys || 0) + ' · ' + (r.algorithms || []).join(', '),
            r.pkce ? POps.t('PKCE (S256) destekleniyor.') : POps.t('Sağlayıcı PKCE (S256) bildirmiyor; yine de gönderilir.'),
            POps.t('İstemci sırrı ilk girişte sınanır.')
        ]);
    }
    if (IS_SUPER && $('ssoLdapEdit')) {
        $('ssoLdapEdit').addEventListener('click', openLdap);
        $('ssoOidcEdit').addEventListener('click', openOidc);
        $('slSec').addEventListener('click', (e) => { const b = e.target.closest('button[data-sec]'); if (b) setLdapSec(b.dataset.sec); });
        $('soDefRole').addEventListener('click', (e) => { const b = e.target.closest('button[data-drole]'); if (b) setOidcDefRole(b.dataset.drole); });
        ssoWireMap($('slMap'), $('slMapAdd'), true);
        ssoWireMap($('soMap'), $('soMapAdd'), false);
        $('slSave').addEventListener('click', saveLdap);
        $('soSave').addEventListener('click', saveOidc);
        $('slTest').addEventListener('click', testLdap);
        $('soTest').addEventListener('click', testOidc);
        $('slTestUser').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); testLdap(); } });
        loadSso();
    }

    // ================= KURUM =================
    // Giriş ekranındaki kurum adı ve logosu: GET /api/branding (oturumsuz), değiştirmek yalnızca süper admin
    let orgSaved = '';
    function renderBrand(b) {
        orgSaved = (b && b.org_name) || '';
        $('orgName').value = orgSaved;
        const logo = $('orgLogo');
        if (b && b.logo) {
            logo.src = (window.POPS_API ? window.POPS_API.HTTP_URL : '') + '/api/branding/logo?v=' + encodeURIComponent(b.logo_v || '');
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
        catch (e) { $('orgName').placeholder = POps.t('Alınamadı: {error}', { error: POps.errorMessage(e) }); return; }
        $('orgName').disabled = !IS_SUPER;
        $('orgLogoPick').disabled = !IS_SUPER;
    }
    $('orgName').addEventListener('input', () => { $('orgSave').disabled = $('orgName').value.trim() === orgSaved; });
    $('orgName').addEventListener('keydown', (e) => { if (e.key === 'Enter' && !$('orgSave').disabled) { e.preventDefault(); $('orgSave').click(); } });
    $('orgSave').addEventListener('click', async function () {
        const name = $('orgName').value.trim();
        let r = null;
        if (await POps.act(this, async () => { r = await POps.post('/api/system/branding', { org_name: name || null }); },
            { success: name ? POps.t('Kurum adı kaydedildi.') : POps.t('Kurum adı kaldırıldı.') })) renderBrand(r);
    });
    $('orgLogoPick').addEventListener('click', () => $('orgLogoFile').click());
    $('orgLogoFile').addEventListener('change', async function () {
        const f = this.files && this.files[0];
        this.value = '';
        if (!f) return;
        if (f.size > 256 * 1024) { POps.toast('warning', POps.t('Logo en çok 256 KB olabilir.')); return; }
        const fd = new FormData();
        fd.append('file', f);
        let r = null;
        if (await POps.act($('orgLogoPick'), async () => { r = await POps.post('/api/system/branding/logo', fd); }, { success: POps.t('Logo yüklendi.') })) renderBrand(r);
    });
    $('orgLogoDel').addEventListener('click', async function () {
        if (!await POps.confirm({ title: POps.t('Logo kaldırılsın mı?'), message: POps.t('Giriş ekranında yeniden POps logosu görünür.'), confirmText: POps.t('Kaldır'), danger: true })) return;
        let r = null;
        if (await POps.act(this, async () => { r = await POps.del('/api/system/branding/logo'); }, { success: POps.t('Logo kaldırıldı.') })) renderBrand(r);
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
            $('queueLimit').title = POps.t('Sınır alınamadı: {error}', { error: POps.errorMessage(e) });
        }
        refreshBar();
    }
    async function saveLimit() {
        if (!isDirty()) return;
        const v = parseInt(limitValue(), 10);
        if (!v || v < 1 || v > 200 || String(v) !== limitValue()) {
            $('queueLimit').classList.add('is-invalid');
            $('queueLimit').focus();
            POps.toast('warning', POps.t('1 ile 200 arasında bir sayı girin.'));
            return;
        }
        if (await POps.act($('saveBtn'), () => POps.post('/api/set_concurrent_limit', { limit: v }), { success: POps.tn('Eşzamanlı görev sınırı {n} bilgisayar oldu.', v) })) {
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
        st.innerHTML = '<span class="spinner sm"></span>' + POps.tHtml('Sınanıyor');
        const t0 = performance.now();
        try {
            await POps.get('/api/devices');
            const ms = Math.round(performance.now() - t0);
            st.innerHTML = `<span class="dot ok"></span>${POps.tHtml('Çalışıyor · {ms} ms', { ms: Number(ms) })}`;
            $('connDesc').textContent = POps.t('Panel merkez sunucuya ulaşıyor.');
            $('sumConn').innerHTML = '<span class="dot ok"></span>' + POps.tHtml('Sunucu çalışıyor');
        } catch (e) {
            st.innerHTML = '<span class="dot bad"></span>' + POps.tHtml('Ulaşılamıyor');
            $('connDesc').textContent = POps.errorMessage(e);
            $('sumConn').innerHTML = '<span class="dot bad"></span>' + POps.tHtml('Sunucuya ulaşılamıyor');
        }
        $('sumConn').hidden = false;
    }
    $('connRetry').addEventListener('click', testConnection);

    if (typeof POPS_API !== 'undefined') {
        $('dispHttpUrl').textContent = POPS_API.HTTP_URL;
        $('dispWsUrl').textContent = POPS_API.WS_URL;
    } else {
        $('dispHttpUrl').textContent = POps.t('pops_config.js okunamadı');
        $('dispWsUrl').textContent = POps.t('pops_config.js okunamadı');
    }
    testConnection();
    loadUsers();
    loadTwofa();
    loadLimit();
    loadBrand();
})();
</script>

<?php include 'includes/footer.php'; ?>
