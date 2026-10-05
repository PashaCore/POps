<?php include 'includes/header.php'; ?>

<style>
    .faint { color: var(--text-muted); }
    .dep-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
    .dep-bar .grow { flex: 1; }
    .dep-bar .search-field { flex: 0 1 280px; min-width: 200px; }
    .segmented .n { color: var(--text-muted); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }

    .dep-table td { padding-top: 12px; padding-bottom: 12px; }
    .dep-table tbody tr[data-id] { cursor: pointer; }
    .dep-table tbody tr.is-focus { background: var(--primary-50); }
    .dep-table .c-size { white-space: nowrap; color: var(--text-secondary); font-variant-numeric: tabular-nums; }
    .dep-table .c-added { white-space: nowrap; color: var(--text-secondary); }
    .dep-table .c-last { min-width: 150px; }
    .dep-name { display: flex; align-items: center; gap: 12px; min-width: 0; }
    .dep-ico { width: 34px; height: 34px; border-radius: 10px; background: var(--bg-surface-3); color: var(--text-tertiary); display: flex; align-items: center; justify-content: center; flex: none; }
    .dep-name .tx { min-width: 0; }
    .dep-name .t { font-weight: var(--fw-semibold); color: var(--text-primary); display: flex; align-items: center; gap: 8px; }
    .dep-name .s { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; max-width: 460px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .dep-table .cell-sub { white-space: nowrap; }
    .dep-table .c-last .cell-sub { white-space: normal; }
    .dep-table .pbar { width: 110px; margin-top: 6px; }

    .hash { font-family: var(--font-mono); font-size: 12px; color: var(--text-secondary); display: inline-flex; align-items: center; gap: 6px; padding: 3px 6px; margin: -3px -6px; border-radius: 6px; text-align: left; white-space: nowrap; }
    .hash .ico { width: 13px; height: 13px; color: var(--text-muted); opacity: 0; transition: opacity 0.12s; }
    .hash:hover { background: var(--bg-hover); color: var(--text-primary); }
    .hash:hover .ico, .hash:focus-visible .ico { opacity: 1; }
    .hash.full { display: flex; margin: 0; padding: 0; white-space: normal; overflow-wrap: anywhere; word-break: break-all; line-height: 1.5; }
    .hash.full:hover { background: transparent; }
    .hash.full .ico { opacity: 1; }

    /* Ayrıntı paneli */
    .dp-actions { display: flex; align-items: center; gap: 8px; }
    .dp-actions .grow { flex: 1; }
    .glist .grow.stack { flex-direction: column; gap: 4px; }
    .glist .grow.stack > span:last-child, .glist .grow.stack > button { text-align: left; }
    .glist .grow .mono { font-size: 12px; }
    .dp-code { background: var(--bg-app); border-radius: 12px; padding: 12px 14px; font-family: var(--font-mono); font-size: 12px; line-height: 1.55; white-space: pre-wrap; overflow-wrap: anywhere; max-height: 240px; overflow: auto; color: var(--text-secondary); margin: 6px 0 0; }
    .dp-deps .act .side .pbar { width: 72px; }
    .dp-deps .act .when { font-variant-numeric: tabular-nums; }
    .dp-deps .why { white-space: pre-line; }
    .dp-deps .dp-detail { grid-column: 2 / -1; min-width: 0; margin-top: -4px; }
    .dp-deps .dp-detail .facts { grid-template-columns: 84px minmax(0, 1fr); }
    .dp-more { font-size: var(--text-sm); display: inline-block; margin-top: 8px; }
    .dp-empty { font-size: var(--text-sm); color: var(--text-muted); padding: 6px 0; }

    /* Paket yükleme */
    .drop { position: relative; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; padding: 22px 16px; border: 1px dashed var(--border-default); border-radius: 12px; background: var(--bg-surface-2); text-align: center; cursor: pointer; margin: 0; transition: border-color 0.12s, background-color 0.12s; }
    .drop:hover, .drop.over { border-color: var(--primary-500); background: var(--primary-50); }
    .drop input[type=file] { position: absolute; inset: 0; width: 100%; height: 100%; opacity: 0; cursor: pointer; }
    .drop .drop-ico { color: var(--text-tertiary); margin-bottom: 4px; }
    .drop .drop-t { font-size: var(--text-sm); font-weight: var(--fw-medium); color: var(--text-primary); overflow-wrap: anywhere; }
    .drop .drop-s { font-size: var(--text-xs); color: var(--text-muted); }
    .drop.has .drop-ico { color: var(--success-solid); }
    .up-prog { display: flex; align-items: center; gap: 10px; margin-top: 10px; font-size: var(--text-xs); color: var(--text-tertiary); font-variant-numeric: tabular-nums; }
    .up-prog .pbar { flex: 1; }
    .zip-help { margin: -4px 0 18px; font-size: var(--text-sm); color: var(--text-secondary); }
    .zip-help summary { cursor: pointer; color: var(--primary-500); font-size: var(--text-sm); width: fit-content; }
    .zip-help ul { margin: 10px 0 8px; padding-left: 1.1rem; line-height: 1.55; font-size: var(--text-xs); color: var(--text-tertiary); }
    .zip-help li + li { margin-top: 4px; }
    .zip-help pre { background: var(--bg-app); border-radius: 10px; padding: 10px 12px; font-size: 12px; margin: 0; white-space: pre-wrap; }
    .opt-row { display: flex; align-items: center; gap: 14px; padding: 12px 14px; border-radius: 12px; background: var(--bg-app); }
    .opt-row .grow { flex: 1; min-width: 0; }
    .opt-row .t { font-size: var(--text-sm); font-weight: var(--fw-medium); color: var(--text-primary); }
    .opt-row .d { font-size: var(--text-xs); color: var(--text-muted); margin-top: 2px; }
    textarea.mono, input.mono { font-family: var(--font-mono); font-size: 12.5px; }

    /* winget paketi */
    .wg-search { display: flex; gap: 8px; }
    .wg-search .search-field { flex: 1; min-width: 0; max-width: none; }
    .wg-search select { width: auto; flex: none; max-width: 46%; }
    .wg-list { margin-top: 8px; max-height: 236px; overflow-y: auto; border-radius: 12px; box-shadow: inset 0 0 0 1px var(--border-subtle); }
    .wg-list .empty-state { padding: 18px 12px; }
    .wg-item { display: flex; align-items: flex-start; gap: 12px; width: 100%; padding: 9px 12px; border-top: 1px solid var(--border-subtle); }
    .wg-item:first-child { border-top: 0; }
    .wg-item:hover { background: var(--bg-surface-2); }
    .wg-item.on { background: var(--primary-50); }
    .wg-item:focus-visible { outline: none; box-shadow: inset var(--focus-ring); }
    .wg-item .tx { flex: 1; min-width: 0; }
    .wg-item b { display: block; font-weight: var(--fw-medium); font-size: var(--text-sm); color: var(--text-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .wg-item small { display: block; font-size: var(--text-xs); color: var(--text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .wg-item small.note { color: var(--warning-text); }
    .wg-item .id { flex: none; max-width: 40%; font-family: var(--font-mono); font-size: 11.5px; color: var(--text-tertiary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; padding-top: 2px; }
    .wg-grid { display: grid; grid-template-columns: minmax(0, 3fr) minmax(0, 2fr); gap: 14px 12px; margin-bottom: 14px; }
    .wg-grid > .field { margin-bottom: 0; }
    .wg-grid .field.has-error .field-hint { display: none; }
    .wg-foot { font-size: var(--text-xs); color: var(--text-muted); line-height: 1.5; margin: 0; }

    /* Dağıtım penceresi */
    .run-sec { margin-bottom: 22px; }
    .run-h { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 10px; font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); flex-wrap: wrap; }
    .run-h .faint { font-weight: var(--fw-regular); font-size: var(--text-xs); }
    .run-steps { border-radius: 12px; background: var(--bg-app); }
    .step { display: flex; align-items: center; gap: 10px; padding: 9px 8px 9px 12px; border-top: 1px solid var(--border-subtle); }
    .step:first-child { border-top: 0; }
    .step .n { width: 22px; height: 22px; border-radius: 99px; background: var(--bg-surface); box-shadow: inset 0 0 0 1px var(--border-subtle); font-size: 11px; font-weight: var(--fw-semibold); display: flex; align-items: center; justify-content: center; color: var(--text-tertiary); flex: none; }
    .step .tx { flex: 1; min-width: 0; }
    .step .tx b { display: block; font-weight: var(--fw-medium); font-size: var(--text-sm); color: var(--text-primary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .step .tx small { display: block; font-size: var(--text-xs); color: var(--text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .step.empty { color: var(--text-muted); font-size: var(--text-sm); padding: 14px; }
    .run-add { margin-top: 8px; width: auto; max-width: 100%; min-width: 200px; }
    .run-list { margin-top: 10px; max-height: 300px; overflow-y: auto; border-radius: 12px; box-shadow: inset 0 0 0 1px var(--border-subtle); }
    .pick { display: flex; align-items: center; gap: 12px; padding: 9px 12px; border-top: 1px solid var(--border-subtle); margin: 0; cursor: pointer; font-weight: var(--fw-regular); }
    .pick:first-child, .pick-h + .pick { border-top: 0; }
    .pick:hover { background: var(--bg-surface-2); }
    .pick .tx { flex: 1; min-width: 0; }
    .pick .tx b { display: block; font-weight: var(--fw-medium); color: var(--text-primary); font-size: var(--text-sm); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .pick .tx small { display: block; font-size: var(--text-xs); color: var(--text-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .pick .st { font-size: var(--text-xs); color: var(--text-tertiary); display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; }
    .pick.off .tx b { color: var(--text-muted); }
    .pick.all .tx b { font-weight: var(--fw-semibold); }
    .pick input:disabled { cursor: default; }
    .pick-h { position: sticky; top: 0; z-index: 1; display: flex; justify-content: space-between; align-items: center; gap: 8px; padding: 7px 12px; background: var(--bg-surface-2); border-top: 1px solid var(--border-subtle); font-size: var(--text-xs); color: var(--text-muted); }
    .pick-h:first-child { border-top: 0; border-radius: 12px 12px 0 0; }
    .linkbtn { color: var(--primary-500); font-size: var(--text-xs); }
    .linkbtn:hover { text-decoration: underline; }
    .run-none { padding: 18px 12px; text-align: center; font-size: var(--text-sm); color: var(--text-muted); }
    .run-sum { display: flex; align-items: center; gap: 8px 16px; flex-wrap: wrap; margin-top: 12px; font-size: var(--text-sm); color: var(--text-tertiary); min-height: 22px; }
    .run-sum b { color: var(--text-primary); font-variant-numeric: tabular-nums; }
    .run-sum .sum { display: inline-flex; align-items: center; gap: 6px; }
    .run-sum .switch-field { font-weight: var(--fw-regular); color: var(--text-secondary); margin: 0; cursor: pointer; }
    .run-foot-note { margin-right: auto; align-self: center; font-size: var(--text-xs); color: var(--text-muted); }

    @media (max-width: 1500px) { body.drawer-open .dep-table .c-added { display: none; } }
    @media (max-width: 1100px) { .dep-table .c-added { display: none; } }
    @media (max-width: 640px) {
        .dep-table .c-size, .dep-table .c-hash { display: none; }
        .dep-bar .search-field { flex-basis: 100%; }
        .dep-ico { display: none; }
        .dep-name .s { max-width: 170px; }
        .dep-table .c-last { min-width: 0; }
        .dep-table .c-last .cell-sub { white-space: normal; }
        .dep-table .pbar { width: 72px; }
        .run-foot-note { display: none; }
        .wg-grid { grid-template-columns: 1fr; }
        .wg-search { flex-direction: column; }
        .wg-search select { width: 100%; max-width: none; }
        .wg-item .id { display: none; }
    }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Dağıtım'); ?></h1>
        <div class="summary" id="depSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="chainBtn" data-tip="<?php _e('Görev zinciri oluştur'); ?>" aria-label="<?php _e('Görev zinciri oluştur'); ?>"><?php echo pops_icon('list'); ?></button>
        <button type="button" class="ibtn boxed" id="limitBtn" data-tip="<?php _e('Eşzamanlı kurulum sınırı'); ?>" aria-label="<?php _e('Eşzamanlı kurulum sınırı'); ?>"><?php echo pops_icon('sliders'); ?></button>
        <button type="button" class="btn" id="uploadBtn"><?php echo pops_icon('upload', 'sm'); ?><?php _e('Paket yükle'); ?></button>
    </div>
</div>

<div class="dep-bar">
    <div class="segmented" id="depFilter" role="group" aria-label="<?php _e('Türe göre süz'); ?>"></div>
    <span class="grow"></span>
    <div class="search-field">
        <?php echo pops_icon('search', 'sm'); ?>
        <input type="search" id="depSearch" placeholder="<?php _e('Ad, dosya, özet ya da yükleyen'); ?>" aria-label="<?php _e('Paket ara'); ?>">
    </div>
</div>

<div class="table-wrap">
    <table class="data-table dep-table">
        <thead><tr><th><?php _e('Ad'); ?></th><th class="c-size"><?php _e('Boyut'); ?></th><th class="c-hash">SHA-256</th><th class="c-added"><?php _e('Eklendi'); ?></th><th class="c-last"><?php _e('Son dağıtım'); ?></th></tr></thead>
        <tbody id="depBody"><tr><td colspan="5"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Paketler yükleniyor…'); ?></div></td></tr></tbody>
    </table>
</div>

<!-- Paket / betik ekleme ve düzenleme -->
<div class="modal-overlay" id="pkgModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="pkgTitle"><?php _e('Paket yükle'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <div class="segmented block" id="pkgType" role="group" aria-label="<?php _e('Tür'); ?>" style="margin-bottom:18px">
                <button type="button" data-t="package" class="active" aria-pressed="true"><?php _e('Kurulum paketi'); ?></button>
                <button type="button" data-t="script" aria-pressed="false"><?php _e('Betik'); ?></button>
                <button type="button" data-t="winget" aria-pressed="false"><?php _e('winget paketi'); ?></button>
            </div>
            <div class="field">
                <label for="pkgName"><?php _e('Ad'); ?></label>
                <input type="text" id="pkgName" maxlength="120" placeholder="<?php _e('Örn. SumatraPDF'); ?>" autocomplete="off">
            </div>
            <div id="pkgFileArea">
                <div class="field">
                    <span class="field-label"><?php _e('Kurulum dosyası'); ?></span>
                    <label class="drop" id="pkgDrop">
                        <input type="file" id="pkgFile" aria-label="<?php _e('Kurulum dosyası'); ?>">
                        <span class="drop-ico"><?php echo pops_icon('upload'); ?></span>
                        <span class="drop-t" id="pkgDropT"><?php _e('Dosya seçin ya da buraya bırakın'); ?></span>
                        <span class="drop-s" id="pkgDropS"><?php _e('.exe, .msi ya da install.bat içeren .zip'); ?></span>
                    </label>
                    <div class="up-prog" id="pkgProg" hidden><div class="pbar"><i class="run" id="pkgProgBar" style="width:0%"></i></div><span id="pkgProgTxt"><?php _e('%{n}', ['n' => 0]); ?></span></div>
                </div>
                <div class="field">
                    <label for="pkgArgs"><?php _e('Sessiz kurulum parametreleri'); ?></label>
                    <input type="text" id="pkgArgs" class="mono" placeholder="/S" autocomplete="off" spellcheck="false">
                    <div class="field-hint"><?php _e('MSI için /qn /norestart kendiliğinden eklenir. Kurulum adımları hedefte {path} dosyasına yazılır.', ['path' => 'C:\POpsLogs\deploy_trace.txt']); ?></div>
                </div>
                <details class="zip-help">
                    <summary><?php _e('ZIP paketi nasıl hazırlanır?'); ?></summary>
                    <ul>
                        <li><?php _e("ZIP'in ana dizininde (alt klasörde değil) install.bat bulunmalı."); ?></li>
                        <li><?php _e("POps ZIP'i hedefte açar ve install.bat'ı yukarıdaki parametrelerle yönetici olarak çalıştırır."); ?></li>
                        <li><?php _e('Kurulum pencere açmadan bitsin diye install.bat içindeki komutlara sessiz kurulum parametrelerini (/S, /qn, /quiet) ekleyin.'); ?></li>
                    </ul>
                    <pre>:: <?php _e('install.bat örneği'); echo "\n"; ?>
setup.exe /S
msiexec.exe /i program.msi /qn /norestart</pre>
                </details>
            </div>
            <div id="pkgCodeArea" hidden>
                <div class="field">
                    <label for="pkgCode"><?php _e('Komut'); ?></label>
                    <textarea id="pkgCode" rows="7" class="mono" spellcheck="false" placeholder="<?php _e('Örn. w32tm /resync'); ?>"></textarea>
                    <div class="field-hint"><?php _e('PowerShell ya da CMD komutları; hedefte SYSTEM hesabıyla çalışır.'); ?></div>
                </div>
            </div>
            <div id="pkgWingetArea" hidden>
                <div class="field">
                    <span class="field-label"><?php _e('winget kataloğu'); ?></span>
                    <div class="wg-search">
                        <div class="search-field">
                            <?php echo pops_icon('search', 'sm'); ?>
                            <input type="search" id="wgSearch" placeholder="<?php _e('Ad, yayıncı ya da kategori'); ?>" aria-label="<?php _e('winget kataloğunda ara'); ?>" autocomplete="off">
                        </div>
                        <select id="wgCat" aria-label="<?php _e('Kategori'); ?>"><option value=""><?php _e('Tüm kategoriler'); ?></option></select>
                    </div>
                    <div class="wg-list" id="wgList" role="listbox" aria-label="<?php _e('winget paketleri'); ?>"></div>
                </div>
                <div class="wg-grid">
                    <div class="field" id="wgIdField">
                        <label for="wgId"><?php _e('winget kimliği'); ?></label>
                        <input type="text" id="wgId" class="mono" maxlength="128" placeholder="<?php _e('Örn. Mozilla.Firefox'); ?>" autocomplete="off" spellcheck="false" aria-describedby="wgIdErr wgIdHint">
                        <div class="field-error" id="wgIdErr"><?php _e('Harf ya da rakamla başlamalı; yalnızca harf, rakam ve . + _ - (2-128 karakter).'); ?></div>
                        <div class="field-hint" id="wgIdHint"><?php _e('Katalogda olmayan paketin kimliğini de yazabilirsiniz (winget search ile bulunur).'); ?></div>
                    </div>
                    <div class="field" id="wgVerField">
                        <label for="wgVer"><?php _e('Sürüm'); ?> <span class="faint"><?php _e('(isteğe bağlı)'); ?></span></label>
                        <input type="text" id="wgVer" class="mono" maxlength="40" placeholder="<?php _e('En son sürüm'); ?>" autocomplete="off" spellcheck="false" aria-describedby="wgVerErr">
                        <div class="field-error" id="wgVerErr"><?php _e('Yalnızca harf, rakam ve . + _ - (en çok 40 karakter).'); ?></div>
                    </div>
                </div>
                <p class="wg-foot"><?php _e('Hedefte winget ile SYSTEM hesabıyla, sessiz ve bütün kullanıcılar için kurulur. Bilgisayarda winget (Uygulama Yükleyicisi) ve winget destekleyen bir POps ajanı gerekir.'); ?></p>
            </div>
            <div class="opt-row" id="pkgRebootRow">
                <div class="grow"><div class="t"><?php _e('Bitince yeniden başlat'); ?></div><div class="d" id="pkgRebootHint"><?php _e('Başarılı kurulumdan 15 saniye sonra bilgisayar yeniden başlar.'); ?></div></div>
                <label class="switch"><input type="checkbox" id="pkgReboot" aria-label="<?php _e('Bitince yeniden başlat'); ?>"><span></span></label>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="pkgSave"><?php _e('Yükle ve ekle'); ?></button>
        </div>
    </div>
</div>

<!-- Dağıtım: adımlar, hedefler, gerekçe -->
<div class="modal-overlay" id="runModal">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="runTitle"><?php _e('Dağıt'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <section class="run-sec">
                <div class="run-h"><span><?php _e('Adımlar'); ?></span><span class="faint" id="runStepsNote"><?php _e('Hedefte sırayla çalışır'); ?></span></div>
                <div class="run-steps" id="runSteps"></div>
                <select id="runAdd" class="run-add" aria-label="<?php _e('Adım ekle'); ?>"></select>
            </section>
            <section class="run-sec">
                <div class="run-h">
                    <span><?php _e('Hedef'); ?></span>
                    <div class="segmented" id="runMode" role="group" aria-label="<?php _e('Hedef türü'); ?>">
                        <button type="button" data-m="lab" class="active" aria-pressed="true"><?php _e('Sınıflar'); ?></button>
                        <button type="button" data-m="pc" aria-pressed="false"><?php _e('Bilgisayarlar'); ?></button>
                    </div>
                </div>
                <div class="search-field" style="max-width:none">
                    <?php echo pops_icon('search', 'sm'); ?>
                    <input type="search" id="runSearch" placeholder="<?php _e('Sınıf ya da bilgisayar ara'); ?>" aria-label="<?php _e('Hedef ara'); ?>" autofocus>
                </div>
                <div class="run-list" id="runList"></div>
                <div class="run-sum" id="runSum" aria-live="polite"></div>
            </section>
            <div class="field">
                <label for="runReason"><?php _e('Gerekçe'); ?> <span class="faint"><?php _e('(isteğe bağlı)'); ?></span></label>
                <input type="text" id="runReason" maxlength="300" placeholder="<?php _e('Örn. bilişim dersi için PDF okuyucu'); ?>" autocomplete="off">
                <div class="field-hint"><?php _e('İşlem geçmişine ve denetim kaydına yazılır.'); ?></div>
            </div>
        </div>
        <div class="modal-footer">
            <span class="run-foot-note"><?php _e('Adımlar SYSTEM hesabıyla çalışır.'); ?></span>
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="runGo" disabled><?php _e('Dağıt'); ?></button>
        </div>
    </div>
</div>

<script>
(function () {
    const dev = POps.dev;
    const ME = <?= json_encode($_SESSION['username'] ?? '', JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT) ?>;
    const $ = (id) => document.getElementById(id);
    const params = new URLSearchParams(location.search);
    const ui = { f: 'all', q: '', pkgs: [], loaded: false, tasks: [], tasksAt: 0, pkgAt: 0, limit: null, focus: null, sig: '', deps: new Map(), openJobs: new Set() };
    const tbody = $('depBody');

    // ================= Paket komutu =================
    // Paket komutu, PowerShell betiğinin UTF-16LE base64 halidir; betiğin içinde indirilecek dosyanın parametreleri
    // (U: imzalı adres, F: hedefteki yol, A: sessiz kurulum parametreleri, R: yeniden başlat, H: SHA-256) durur.
    function strToBase64UTF16LE(str) {
        let bin = '';
        for (let i = 0; i < str.length; i++) { const c = str.charCodeAt(i); bin += String.fromCharCode(c & 0xff, c >> 8); }
        return btoa(bin);
    }
    function base64UTF16LEToStr(b64) {
        try { const bin = atob(b64); let s = ''; for (let i = 0; i < bin.length; i += 2) s += String.fromCharCode(bin.charCodeAt(i) | (bin.charCodeAt(i + 1) << 8)); return s; } catch (e) { return ''; }
    }
    function b64utf8(s) { let bin = ''; new TextEncoder().encode(s).forEach(b => { bin += String.fromCharCode(b); }); return btoa(bin); }
    function unb64utf8(b64) { return new TextDecoder().decode(Uint8Array.from(atob(b64), c => c.charCodeAt(0))); }
    function payloadOf(command) {
        try {
            const b64 = String(command || '').split('-EncodedCommand ')[1];
            if (!b64) return null;
            const m = base64UTF16LEToStr(b64.trim()).match(/FromBase64String\('([^']+)'\)/);
            return m ? JSON.parse(unb64utf8(m[1])) : null;
        } catch (e) { return null; }
    }
    function packageCommand(p) {
        const b64Params = b64utf8(JSON.stringify(p));
        const psCode = `New-Item -ItemType Directory -Force -Path 'C:\\POpsLogs' | Out-Null; $L='C:\\POpsLogs\\deploy_trace.txt'; function T($m){ $d='['+(Get-Date -f 'HH:mm:ss')+'] '+$m; Add-Content $L $d; Write-Output $d }; T '--- OPERASYON BASLADI ---'; try { T '1. Parametreler'; $j=ConvertFrom-Json([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${b64Params}'))); T ('2. URL: '+$j.U); [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; try { (New-Object System.Net.WebClient).DownloadFile($j.U, $j.F); } catch { T '[HATA] Indirilemedi'; exit 1; } if (!(Test-Path $j.F)) { exit 1; } if ($j.H -and ((Get-FileHash -Algorithm SHA256 $j.F).Hash -ne $j.H)) { T '[HATA] Dosya ozeti uyusmuyor; calistirilmadi'; Remove-Item $j.F -Force -ea 0; exit 1 } T '4. Indi.'; Unblock-File $j.F -ea 0; $Ext = [IO.Path]::GetExtension($j.F).ToLower(); if($Ext -eq '.zip'){ Expand-Archive $j.F 'C:\\POpsLogs\\T' -Force; $b=Get-ChildItem 'C:\\POpsLogs\\T' -Filter 'install.bat' -Recurse | Select -First 1; if(!$b){ exit 1 }; $p=Start-Process 'cmd.exe' "/c \`"$($b.FullName)\`" $($j.A)" -Wait -NoNewWindow -PassThru } elseif($Ext -eq '.msi'){ $p=Start-Process 'msiexec.exe' "/i \`"$($j.F)\`" /qn /norestart $($j.A)" -Wait -NoNewWindow -PassThru } else { $p=Start-Process $j.F -ArgumentList $($j.A) -Wait -NoNewWindow -PassThru }; T ('Bitti: '+$p.ExitCode); if($p.ExitCode -in 0,3010){ if($j.R -eq 1){ shutdown -r -t 15 } } else { exit 1 } } catch { T ('[HATA] '+$_); exit 1 }`;
        return 'powershell.exe -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -EncodedCommand ' + strToBase64UTF16LE(psCode);
    }
    const SCRIPT_REBOOT = '\n\nping 127.0.0.1 -n 3 > nul\nshutdown -r -t 15';

    // ================= winget =================
    // Kimlik ve sürüm sunucudaki denetimle aynı (Backend/pops/winget.py): boşluk, tırnak, kabuk karakteri geçmez.
    // Kitaplıktaki winget kaydının komutu, ajanın çalıştırdığı komutun okunur hâlidir (sunucu görev kaydına da bunu
    // yazar); dağıtımda ajana komut değil, kimlik ve sürüm gider.
    const WG_ID = /^[A-Za-z0-9][A-Za-z0-9.+_-]{1,127}$/;
    const WG_VER = /^[0-9A-Za-z.+_-]{1,40}$/;
    const WG_ARGS = ' -e --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity';
    const wingetCommand = (id, ver) => 'winget install --id ' + id + WG_ARGS + (ver ? ' --version ' + ver : '');
    function wingetOf(command) {
        const c = String(command || '');
        const id = (/--id (\S+)/.exec(c) || [])[1] || '';
        const ver = (/--version (\S+)/.exec(c) || [])[1] || '';
        return { id: WG_ID.test(id) ? id : '', ver: WG_VER.test(ver) ? ver : '' };
    }
    // Kategori adları (sunucudaki kimlikler, Backend/pops/winget.py CATEGORY_LABELS)
    const WG_CAT = { browser: 'Tarayıcılar', office: 'Ofis ve PDF', education: 'Eğitim', programming: 'Yazılım geliştirme', graphics: 'Grafik ve tasarım', media: 'Ses ve video', communication: 'İletişim', utility: 'Araçlar', runtime: 'Çalışma ortamları' };
    const wgCatName = (c) => (WG_CAT[c] ? POps.t(WG_CAT[c]) : String(c || ''));
    const wgText = (p, key) => String((POps.lang === 'en' && p[key + '_en']) || p[key] || '');
    const wg = { all: null, byId: new Map(), cats: [], q: '', cat: '', items: null, seq: 0, sel: '', auto: '' };
    async function loadCatalog() {
        if (wg.all) return wg.all;
        const r = await POps.get('/api/deploy/winget/catalog?limit=500');
        wg.all = Array.isArray(r && r.items) ? r.items : [];
        wg.byId = new Map(wg.all.map(p => [String(p.id).toLowerCase(), p]));
        wg.cats = Array.isArray(r && r.categories) ? r.categories : [];
        return wg.all;
    }
    const wgInfo = (id) => wg.byId.get(String(id || '').toLowerCase()) || null;
    function scriptCode(command) {
        const c = String(command || '');
        return c.includes('ping 127.0.0.1 -n 3') ? c.split('ping 127.0.0.1 -n 3')[0].trim() : c;
    }

    // meta: "dosya | boyut [(Reboot)] | yükleyen" (paket) ya da "Sistem Betiği [(Reboot)] | Betik | ekleyen" (betik).
    // Eski kayıtlarda üçüncü parça yoktur; Kontrol merkezi ikinci parçayı alt yazı olarak gösterir.
    function buildMeta(type, file, size, reboot, by) {
        const rb = reboot ? ' (Reboot)' : '';
        if (type === 'package') return `${file} | ${size}${rb}` + (by ? ` | ${by}` : '');
        return `Sistem Betiği${rb}` + (by ? ` | Betik | ${by}` : '');
    }
    // Türkçe anahtarlar; info() POps.t ile çevirir (tür yalnızca gösterilir ve aramada kullanılır)
    const KIND = { msi: 'MSI paketi', exe: 'Kurulum (EXE)', zip: 'ZIP · install.bat' };
    function info(p) {
        const meta = String(p.meta || '');
        const parts = meta.replace(/\s*\(Reboot\)/g, '').split('|').map(s => s.trim());
        const o = {
            id: String(p.id || ''), name: p.name || POps.t('(adsız)'), type: p.type === 'script' || p.type === 'winget' ? p.type : 'package', command: String(p.command || ''),
            reboot: meta.includes('(Reboot)'), file: '', size: '', by: parts[2] || '', hash: '', args: '', url: '', path: '', code: '', at: null, payload: null, meta
        };
        const m = /^mod-(\d{12,14})$/.exec(o.id);
        if (m) o.at = Number(m[1]);
        if (o.type === 'winget') {
            const w = wingetOf(o.command);
            o.wid = w.id;
            o.wver = w.ver;
            o.kind = POps.t('winget paketi');
        } else if (o.type === 'package') {
            o.file = parts[0] || '';
            o.size = parts[1] || '';
            const j = payloadOf(o.command);
            if (j) {
                o.payload = j;
                o.hash = String(j.H || '').toLowerCase();
                o.args = String(j.A || '');
                o.url = String(j.U || '');
                o.path = String(j.F || '');
                if (Number(j.R) === 1) o.reboot = true;
                if (!o.file && o.path) o.file = o.path.split('\\').pop();
            }
            o.kind = POps.t(KIND[(o.file.split('.').pop() || '').toLowerCase()] || 'Kurulum dosyası');
        } else {
            o.code = scriptCode(o.command);
            o.kind = POps.t('Betik');
        }
        return o;
    }
    const shortHash = (h) => h.length > 16 ? h.slice(0, 8) + '…' + h.slice(-6) : h;
    const typeIcon = (t) => (t === 'package' ? 'package' : t === 'winget' ? 'download' : 'terminal');
    // Görev başlığı (sunucuya giden veri): winget adımı "winget: <ad>" olarak görünür
    const stepName = (o) => (o.type === 'winget' ? 'winget: ' + o.name : o.name);
    const firstLine = (s) => { const l = String(s || '').split('\n').find(x => x.trim()) || ''; return l.length > 70 ? l.slice(0, 67) + '…' : l; };
    function sizeText(bytes) {
        const n = Number(bytes) || 0;
        const fmt = (v, d) => v.toLocaleString(POps.locale, { maximumFractionDigits: d, minimumFractionDigits: d });
        if (n >= 1024 * 1024 * 1024) return fmt(n / (1024 * 1024 * 1024), 2) + ' GB';
        if (n >= 1024 * 1024) return fmt(n / (1024 * 1024), 1) + ' MB';
        return Math.max(1, Math.round(n / 1024)) + ' KB';
    }
    function legacyCopy(text) {
        const ta = POps.el('textarea', { style: 'position:fixed;opacity:0;top:0;left:0', 'aria-hidden': 'true' });
        ta.value = text;
        document.body.append(ta);
        ta.select();
        let ok = false;
        try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
        ta.remove();
        return ok;
    }
    async function copyText(text, okText) {
        let ok = false;
        try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); ok = true; } } catch (e) { ok = false; }
        if (!ok) ok = legacyCopy(text);
        if (ok) POps.toast('success', okText);
        else POps.toast('warning', POps.t('Kopyalanamadı; metni seçip elle kopyalayın.'));
    }

    // ================= Dağıtım geçmişi (görev kayıtlarından) =================
    // Aynı istekte açılan görevler tek dağıtımdır (batch_id); eski kayıtlarda zaman + kişi + komut (İşlemler sayfasıyla aynı anahtar)
    const tsOf = (s) => (s ? String(s).replace(' ', 'T') : s);
    const jobKey = (t) => t.batch_id || ['legacy', t.created_at, t.created_by, t.script_path, t.retry_of ? 'r' : ''].join('|');
    const WAITING = ['Pending', 'Paused'];
    // Görev pakete komutuyla bağlanır; aynı komutlu iki paket varsa görevin başlığı (adımın adı) ayırır. Paket yeniden
    // yüklenince komut değişir: Dağıtım sayfasından açılmış görev adıyla da eşleşir.
    // winget görevi kitaplık kaydına paketle (kimlik + sürüm) bağlanır; aynı paket iki kez kayıtlıysa başlık ayırır.
    function belongs(t, o) {
        if (o.type === 'winget' || t.kind === 'winget') {
            const w = t.kind === 'winget' && t.payload && typeof t.payload === 'object' ? t.payload : null;
            if (o.type !== 'winget' || !w || !o.wid || String(w.id || '').toLowerCase() !== o.wid.toLowerCase() || String(w.version || '') !== o.wver) return false;
            return !t.title || t.title === stepName(o) || !ui.pkgs.some(p => p !== o && p.type === 'winget' && p.wid.toLowerCase() === o.wid.toLowerCase() && p.wver === o.wver && stepName(p) === t.title);
        }
        if (t.script_path === o.command) return !t.title || t.title === o.name || !ui.pkgs.some(p => p !== o && p.command === o.command && p.name === t.title);
        return t.source === 'deploy' && !!t.title && t.title === o.name;
    }
    function groupJobs(tasks) {
        const map = new Map();
        tasks.forEach(t => {
            const k = jobKey(t);
            let j = map.get(k);
            if (!j) { j = { key: k, tasks: [], by: t.created_by, at: t.created_at, reason: t.reason, ip: t.client_ip, source: t.source, minId: t.id }; map.set(k, j); }
            j.tasks.push(t);
            if (t.id < j.minId) j.minId = t.id;
        });
        return [...map.values()].map(j => {
            const c = { ok: 0, bad: 0, run: 0, wait: 0, total: j.tasks.length };
            j.tasks.forEach(t => { c[POps.taskState(t.status)] += 1; if (WAITING.includes(t.status)) c.wait += 1; });
            j.c = c;
            j.state = c.run ? 'run' : c.bad ? 'bad' : 'ok';
            j.labs = [...new Set(j.tasks.map(t => t.target_lab).filter(l => l && l !== dev.UNASSIGNED))];
            return j;
        }).sort((a, b) => b.minId - a.minId);
    }
    function rebuildDeps() {
        ui.deps = new Map();
        ui.pkgs.forEach(o => ui.deps.set(o.id, groupJobs(ui.tasks.filter(t => belongs(t, o)))));
    }
    function jobWord(j) {
        if (j.c.run) return j.c.wait === j.c.run && !j.tasks.some(t => t.status === 'Running') ? (j.tasks.some(t => t.status === 'Paused') ? POps.t('Duraklatıldı') : POps.t('Sırada')) : POps.t('Sürüyor');
        if (j.c.bad) return j.c.bad === j.c.total ? (j.c.total === 1 ? dev.statusWord(j.tasks[0].status) : POps.t('Başarısız')) : POps.t('{n} başarısız', { n: j.c.bad });
        return POps.t('Tamamlandı');
    }
    function targetText(j) {
        if (j.c.total === 1) return dev.name(j.tasks[0].target_pc);
        return (j.labs.length === 1 ? j.labs[0] + ' · ' : j.labs.length > 1 ? POps.tn('{n} sınıf', j.labs.length) + ' · ' : '') + POps.tn('{n} bilgisayar', j.c.total);
    }
    function pbarHtml(c) {
        const segHtml = (cls, n) => n ? `<i class="${escapeHtml(cls)}" style="width:${(n / c.total * 100).toFixed(2)}%"></i>` : '';
        return `<div class="pbar">${segHtml('ok', c.ok)}${segHtml('bad', c.bad)}${segHtml('run', c.run - c.wait)}</div>`;
    }
    const isToday = (v) => { const d = POps.toDate(tsOf(v)); return !!d && d.toDateString() === new Date().toDateString(); };

    // ================= Liste =================
    function visible() {
        const q = ui.q.toLocaleLowerCase('tr');
        return ui.pkgs.filter(o => {
            if (ui.f !== 'all' && o.type !== ui.f) return false;
            if (!q) return true;
            return [o.name, o.file, o.hash, o.args, o.code, o.by, o.kind, o.wid].some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
        });
    }
    function rowHtml(o) {
        const j = (ui.deps.get(o.id) || [])[0];
        const subText = o.type === 'winget' ? [o.kind, o.wid, o.wver].filter(Boolean).join(' · ')
            : (o.type === 'package' ? [o.kind, o.file].filter(Boolean).join(' · ') : o.kind + ' · ' + firstLine(o.code)) + (o.reboot ? ' · ' + POps.t('yeniden başlatır') : '');
        const markHtml = o.type === 'package' && !o.hash ? dev.markHtml({ kind: 'upd', glyph: '!', text: POps.t('Dosya özeti yok: indirilen dosya doğrulanmaz') }) : '';
        const hashHtml = o.hash
            ? `<button type="button" class="hash" data-copy="${escapeHtml(o.hash)}" title="${escapeHtml(POps.t('Tam özeti kopyala'))}" aria-label="${escapeHtml(POps.t('SHA-256 özetini kopyala'))}">${escapeHtml(shortHash(o.hash))}${POps.iconHtml('copy')}</button>`
            : '<span class="faint">—</span>';
        const addedHtml = o.at || o.by ? (o.by ? escapeHtml(o.by) + (o.at ? ' · ' : '') : '') + (o.at ? POps.timeHtml(o.at) : '') : '<span class="faint">—</span>';
        const lastHtml = j
            ? `<span class="word ${escapeHtml(j.state)}">${escapeHtml(jobWord(j))}${j.state === 'run' ? ' · ' + Number(j.c.ok + j.c.bad) + '/' + Number(j.c.total) : ''}</span><div class="cell-sub">${POps.timeHtml(tsOf(j.at))} · ${escapeHtml(targetText(j))}</div>${j.state === 'run' && j.c.total > 1 ? pbarHtml(j.c) : ''}`
            : `<span class="faint">${POps.tHtml('Henüz dağıtılmadı')}</span>`;
        return `<tr data-id="${escapeHtml(o.id)}" tabindex="0" class="${ui.focus === o.id ? 'is-focus' : ''}">
            <td><div class="dep-name"><span class="dep-ico">${POps.iconHtml(typeIcon(o.type))}</span><div class="tx"><div class="t">${escapeHtml(o.name)}${markHtml}</div><div class="s" title="${escapeHtml(subText)}">${escapeHtml(subText)}</div></div></div></td>
            <td class="c-size">${o.size ? escapeHtml(o.size) : '<span class="faint">—</span>'}</td>
            <td class="c-hash">${hashHtml}</td>
            <td class="c-added">${addedHtml}</td>
            <td class="c-last">${lastHtml}</td>
        </tr>`;
    }
    function renderFilter() {
        const n = { all: ui.pkgs.length, package: ui.pkgs.filter(o => o.type === 'package').length, script: ui.pkgs.filter(o => o.type === 'script').length, winget: ui.pkgs.filter(o => o.type === 'winget').length };
        const btnHtml = (k, label) => `<button type="button" data-f="${escapeHtml(k)}" class="${ui.f === k ? 'active' : ''}" aria-pressed="${ui.f === k ? 'true' : 'false'}">${escapeHtml(label)} <span class="n">${Number(n[k])}</span></button>`;
        $('depFilter').innerHTML = btnHtml('all', POps.t('Tümü')) + btnHtml('package', POps.t('Paketler')) + btnHtml('script', POps.t('Betikler'))
            + btnHtml('winget', 'winget');
    }
    function renderSummary() {
        const nPkg = ui.pkgs.filter(o => o.type === 'package').length;
        const nScr = ui.pkgs.filter(o => o.type === 'script').length;
        const nWg = ui.pkgs.length - nPkg - nScr;
        const seen = new Map();
        ui.deps.forEach(list => list.forEach(j => seen.set(j.key, j)));
        const jobs = [...seen.values()];
        const run = jobs.filter(j => j.state === 'run').length;
        const badToday = jobs.filter(j => j.c.bad && isToday(j.at)).length;
        // Sayı kalın: yer tutucuya HTML parçası (POps.tnHtml'in son argümanı)
        const boldHtml = (n) => `<b>${Number(n)}</b>`;
        const limitHtml = ui.limit === null ? '' : ui.limit === 0
            ? `<span class="sum">${POps.tHtml('{limit} eşzamanlı kurulum', null, { limit: `<b>${POps.tHtml('Sınırsız')}</b>` })}</span>`
            : `<span class="sum">${POps.tnHtml('{n} eşzamanlı kurulum', ui.limit, null, { n: boldHtml(ui.limit) })}</span>`;
        $('depSummary').innerHTML = `<span class="sum">${POps.tnHtml('{n} paket', nPkg, null, { n: boldHtml(nPkg) })}</span><span class="sum">${POps.tnHtml('{n} betik', nScr, null, { n: boldHtml(nScr) })}</span>`
            + (nWg ? `<span class="sum">${POps.tnHtml('{n} winget paketi', nWg, null, { n: boldHtml(nWg) })}</span>` : '')
            + (run ? `<span class="sum"><span class="dot run"></span>${POps.tnHtml('{n} dağıtım sürüyor', run, null, { n: boldHtml(run) })}</span>` : '')
            + (badToday ? `<span class="sum"><span class="dot bad"></span>${POps.tnHtml('{n} sorunlu dağıtım (bugün)', badToday, null, { n: boldHtml(badToday) })}</span>` : '')
            + limitHtml;
    }
    function renderTable(force) {
        if (!ui.loaded) return;
        renderSummary();
        renderFilter();
        const rows = visible();
        const sig = JSON.stringify([ui.f, ui.q, ui.focus, rows.map(o => [o.id, o.name, o.meta, o.command.length]), rows.map(o => (ui.deps.get(o.id) || []).slice(0, 1).map(j => [j.key, j.c]))]);
        if (!force && sig === ui.sig) return;
        ui.sig = sig;
        if (!rows.length) {
            if (ui.pkgs.length) {
                POps.setEmpty(tbody, { tag: 'tr', colspan: 5, icon: 'filter', title: POps.t('Süzgece uyan paket yok'), text: POps.t('Arama ya da süzgeci değiştirin.') });
            } else {
                POps.setEmpty(tbody, { tag: 'tr', colspan: 5, icon: 'package', title: POps.t('Henüz paket yok'), text: POps.t('Kurulum dosyası ya da betik ekleyin; buradan sınıflara dağıtılır.') });
                const b = POps.el('button', { type: 'button', className: 'btn', text: POps.t('Paket yükle') });
                b.addEventListener('click', () => openPkgModal(null, 'package'));
                tbody.querySelector('.empty-state').append(b);
            }
            return;
        }
        tbody.innerHTML = rows.map(rowHtml).join('');
    }

    async function loadPackages() {
        try {
            const [packages, lim] = await Promise.all([POps.get('/api/packages'), POps.get('/api/get_concurrent_limit').catch(() => null)]);
            ui.pkgs = (Array.isArray(packages) ? packages : []).map(info).sort((a, b) => a.name.localeCompare(b.name, 'tr', { numeric: true }));
            if (lim && lim.limit != null) ui.limit = Number(lim.limit);
            ui.loaded = true;
            ui.pkgAt = Date.now();
            rebuildDeps();
            renderTable(true);
            if (ui.focus) renderDrawer(ui.focus, true);
            const want = params.get('pkg');
            if (want && !ui.openedFromUrl) { ui.openedFromUrl = true; if (ui.pkgs.some(o => o.id === want)) openPkg(want); }
        } catch (e) {
            if (!ui.loaded) POps.setError(tbody, e, { tag: 'tr', colspan: 5 });
            else POps.toast('error', POps.t('Paketler alınamadı: {error}', { error: POps.errorMessage(e) }));
        }
    }
    async function loadTasks() {
        try {
            const tasks = await POps.get('/api/tasks?limit=500');
            ui.tasks = Array.isArray(tasks) ? tasks : [];
            ui.tasksAt = Date.now();
            rebuildDeps();
            renderTable(false);
            if (ui.focus) renderDrawer(ui.focus, true);
        } catch (e) { /* geçmiş isteğe bağlı; bir sonraki turda yeniden denenir */ }
    }
    const anyRunning = () => [...ui.deps.values()].some(list => list.some(j => j.state === 'run'));

    // ================= Ayrıntı paneli =================
    function openPkg(id) {
        // Önce panel açılır: başka bir paket açıksa onun kapanışı (odak sıfırlama) yenisinden önce çalışır
        const body = POps.drawer.open('pkg:' + id, { onClose: () => { ui.focus = null; ui.openJobs.clear(); renderTable(true); } });
        ui.focus = id;
        ui.openJobs.clear();
        renderTable(true);
        wireDrawer(body);
        renderDrawer(id, false);
        if (Date.now() - ui.tasksAt > 5000) loadTasks();
    }
    function depRowHtml(j) {
        const open = ui.openJobs.has(j.key);
        const icon = j.state === 'ok' ? 'check' : j.state === 'bad' ? 'x' : 'clock';
        const failed = j.tasks.filter(t => POps.taskState(t.status) === 'bad');
        const singleWhy = j.c.total === 1 && failed.length ? dev.failReason(failed[0]) : '';
        const failText = failed.slice(0, 6).map(t => dev.name(t.target_pc) + ': ' + (dev.failReason(t) || dev.statusWord(t.status))).join('\n') + (failed.length > 6 ? '\n' + POps.tn('+{n} bilgisayar daha', failed.length - 6) : '');
        const facts = [[POps.t('Gönderen'), j.by || POps.t('sistem')], [POps.t('Kaynak'), dev.sourceText(j.source)], [POps.t('Zaman'), POps.fullTime(tsOf(j.at))], ['IP', j.ip], [POps.t('Gerekçe'), j.reason], [POps.t('Hedef'), targetText(j)]];
        const detailHtml = open
            ? '<div class="dp-detail">' + (failed.length && j.c.total > 1 ? `<div class="why">${escapeHtml(failText)}</div>` : '')
              + `<div class="facts">${facts.filter(f => f[1]).map(f => `<span>${escapeHtml(f[0])}</span><span>${escapeHtml(f[1])}</span>`).join('')}</div>`
              + `<a class="dp-more" href="tasks?job=${encodeURIComponent(j.key)}">${POps.tHtml('İşlemler sayfasında aç')}</a></div>`
            : '';
        return `<div class="act clickable" data-job="${escapeHtml(j.key)}" role="button" tabindex="0" aria-expanded="${open ? 'true' : 'false'}">
            <div class="res ${escapeHtml(j.state)}">${POps.iconHtml(icon)}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(targetText(j))}</div>
                <div class="meta">${escapeHtml(j.by || POps.t('sistem'))} · ${POps.timeHtml(tsOf(j.at))}${j.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: j.reason }) : ''}</div>
                ${singleWhy ? `<div class="why">${escapeHtml(singleWhy)}</div>` : ''}</div>
            <div class="side"><span class="word ${escapeHtml(j.state)}">${escapeHtml(jobWord(j))}</span><span class="when">${Number(j.c.ok + j.c.bad)}/${Number(j.c.total)}</span>${j.c.total > 1 ? pbarHtml(j.c) : ''}</div>
            ${detailHtml}
        </div>`;
    }
    function renderDrawer(id, keep) {
        if (!POps.drawer.isOpen('pkg:' + id)) return;
        const body = POps.drawer.body();
        const o = ui.pkgs.find(x => x.id === id);
        if (!o) {
            body.innerHTML = `<div class="drawer-head"><div class="drawer-title"><h2>${POps.tHtml('Paket bulunamadı')}</h2></div><button type="button" class="ibtn sm" data-act="close" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button></div><p class="dp-empty">${POps.tHtml('Silinmiş olabilir.')}</p>`;
            return;
        }
        const scroll = keep ? body.scrollTop : 0;
        const deps = ui.deps.get(id) || [];
        const cat = o.type === 'winget' ? wgInfo(o.wid) : null;
        const subText = o.type === 'winget' ? [o.kind, cat && cat.publisher].filter(Boolean).join(' · ') : o.kind + (o.size ? ' · ' + o.size : '');
        const rebootText = o.reboot ? POps.t('Yeniden başlatır') : POps.t('Yeniden başlatmaz');
        const facts = o.type === 'winget'
            ? [[POps.t('winget kimliği'), o.wid || POps.t('okunamadı'), 'mono'], [POps.t('Sürüm'), o.wver || POps.t('en son sürüm'), o.wver ? 'mono' : ''],
                [POps.t('Yayıncı'), cat ? cat.publisher : ''], [POps.t('Kategori'), cat ? wgCatName(cat.category) : POps.t('katalogda yok')], [POps.t('Ekleyen'), o.by]]
            : o.type === 'package'
            ? [[POps.t('Dosya'), o.file, 'mono'], [POps.t('Boyut'), o.size], [POps.t('Sessiz kurulum'), o.payload ? (o.args || POps.t('yok')) : '', o.args ? 'mono' : ''], [POps.t('Bitince'), rebootText], [POps.t('Yükleyen'), o.by]]
            : [[POps.t('Bitince'), rebootText], [POps.t('Ekleyen'), o.by]];
        const factsHtml = '<div class="glist">'
            + facts.filter(f => f[1]).map(f => `<div class="grow"><span>${escapeHtml(f[0])}</span><span class="${escapeHtml(f[2] || '')}">${escapeHtml(f[1])}</span></div>`).join('')
            + (o.at ? `<div class="grow"><span>${POps.tHtml('Eklendi')}</span><span>${POps.timeHtml(o.at)}</span></div>` : '')
            + (o.hash ? `<div class="grow stack"><span>SHA-256</span><button type="button" class="hash full" data-copy="${escapeHtml(o.hash)}" title="${escapeHtml(POps.t('Kopyala'))}" aria-label="${escapeHtml(POps.t('SHA-256 özetini kopyala'))}">${escapeHtml(o.hash)}${POps.iconHtml('copy')}</button></div>` : '')
            + `<div class="grow"><span>${POps.tHtml('Kimlik')}</span><span class="mono">${escapeHtml(o.id)}</span></div>`
            + '</div>';
        const wgOld = o.type === 'winget' ? wingetGap((state.devices || []).map(d => d.hostname)) : 0;
        const issueHtml = o.type === 'package' && !o.hash
            ? `<div class="issue upd">${POps.iconHtml('alert', 'sm')}<span style="flex:1">${o.payload ? POps.tHtml('Dosya özeti kayıtlı değil: bilgisayarlar indirdikleri dosyayı doğrulamadan çalıştırır. Dosyayı yeniden yükleyin.') : POps.tHtml('Paketin dosya bilgisi okunamadı. Düzenleyip dosyayı yeniden yükleyin.')}</span></div>`
            : o.type === 'winget' && !o.wid
            ? `<div class="issue upd">${POps.iconHtml('alert', 'sm')}<span style="flex:1">${POps.tHtml('winget kimliği okunamadı. Düzenleyip kimliği yeniden yazın.')}</span></div>`
            : (cat && wgText(cat, 'note') ? `<div class="issue old">${POps.iconHtml('info', 'sm')}<span style="flex:1">${escapeHtml(wgText(cat, 'note'))}</span></div>` : '')
              + (wgOld ? `<div class="issue upd">${POps.iconHtml('alert', 'sm')}<span style="flex:1">${POps.tnHtml('{n} bilgisayarın ajanı winget desteklemiyor; bu pakete orada kurulmaz. Ajanı güncelleyin.', wgOld)}</span></div>` : '');
        const codeHtml = o.type === 'script' ? `<div><h3>${POps.tHtml('Komut')}</h3><pre class="dp-code">${escapeHtml(o.code)}</pre></div>`
            : o.type === 'winget' && o.wid ? `<div><h3>${POps.tHtml('Hedefte çalışan komut')}</h3><pre class="dp-code">${escapeHtml(o.command)}</pre><p class="wg-foot" style="margin-top:6px">${POps.tHtml('Ajan winget\'i SYSTEM hesabıyla, kabuk kullanmadan çalıştırır; kimlik ve sürüm ayrı argüman olarak gider.')}</p></div>` : '';
        const depsHtml = deps.length
            ? deps.slice(0, 8).map(depRowHtml).join('') + (deps.length > 8 ? `<a class="dp-more" href="tasks">${POps.tnHtml('{n} dağıtım daha', deps.length - 8)} · ${POps.tHtml('İşlemler')}</a>` : '')
            : `<div class="dp-empty">${ui.tasksAt ? POps.tHtml('Henüz dağıtılmadı.') : POps.tHtml('Yükleniyor…')}</div>`;
        body.innerHTML = `<div class="drawer-head">
                <div class="drawer-title"><span class="drawer-ico">${POps.iconHtml(typeIcon(o.type), 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(o.name)}</h2><div class="sub">${escapeHtml(subText)}</div></div></div>
                <button type="button" class="ibtn sm" data-act="close" data-tip="${escapeHtml(POps.t('Kapat (Esc)'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button>
            </div>
            <div class="dp-actions">
                <button type="button" class="btn" data-act="deploy">${POps.iconHtml('send', 'sm')}${POps.tHtml('Dağıt')}</button>
                <button type="button" class="btn secondary" data-act="edit">${POps.iconHtml('edit', 'sm')}${POps.tHtml('Düzenle')}</button>
                <span class="grow"></span>
                <button type="button" class="ibtn" data-act="more" data-tip="${escapeHtml(POps.t('Diğer işlemler'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Diğer işlemler'))}" aria-haspopup="menu">${POps.iconHtml('more')}</button>
            </div>
            ${issueHtml}${factsHtml}${codeHtml}
            <div><h3>${POps.tHtml('Son dağıtımlar')}</h3><div class="dp-deps">${depsHtml}</div></div>`;
        body.scrollTop = scroll;
    }
    function wireDrawer(body) {
        if (body.dataset.depWired) return;
        body.dataset.depWired = '1';
        const toggleJob = (row) => {
            const k = row.dataset.job;
            ui.openJobs.has(k) ? ui.openJobs.delete(k) : ui.openJobs.add(k);
            if (ui.focus) renderDrawer(ui.focus, true);
        };
        body.addEventListener('click', (e) => {
            const key = POps.drawer.key() || '';
            if (!key.startsWith('pkg:')) return;
            const o = ui.pkgs.find(x => x.id === key.slice(4));
            const copy = e.target.closest('[data-copy]');
            if (copy) { copyText(copy.dataset.copy, POps.t('Özet kopyalandı.')); return; }
            const b = e.target.closest('[data-act]');
            if (b) {
                const act = b.dataset.act;
                if (act === 'close') POps.drawer.close();
                else if (!o) return;
                else if (act === 'deploy') openRun([o]);
                else if (act === 'edit') openPkgModal(o);
                else if (act === 'more') POps.menu(b, [
                    o.hash ? { label: POps.t('Özeti kopyala'), icon: 'copy', onClick: () => copyText(o.hash, POps.t('Özet kopyalandı.')) } : null,
                    o.url ? { label: POps.t('İndirme adresini kopyala'), icon: 'download', onClick: () => copyText(o.url, POps.t('Adres kopyalandı.')) } : null,
                    o.type === 'script' ? { label: POps.t('Komutu kopyala'), icon: 'copy', onClick: () => copyText(o.code, POps.t('Komut kopyalandı.')) } : null,
                    o.type === 'winget' && o.wid ? { label: POps.t('winget kimliğini kopyala'), icon: 'copy', onClick: () => copyText(o.wid, POps.t('Kimlik kopyalandı.')) } : null,
                    { label: POps.t('Görev zincirine ekle'), icon: 'list', onClick: () => openRun([o], true) },
                    '-',
                    { label: o.type === 'script' ? POps.t('Betiği sil') : POps.t('Paketi sil'), icon: 'trash', danger: true, onClick: () => removePkg(o) }
                ]);
                return;
            }
            if (e.target.closest('a')) return;
            const row = e.target.closest('.act[data-job]');
            if (row) toggleJob(row);
        });
        body.addEventListener('keydown', (e) => {
            const row = e.target.closest('.act[data-job]');
            if (row && (e.key === 'Enter' || e.key === ' ') && e.target === row) { e.preventDefault(); toggleJob(row); const again = body.querySelector(`.act[data-job="${CSS.escape(row.dataset.job)}"]`); if (again) again.focus(); }
        });
    }
    async function removePkg(o) {
        const ok = await POps.confirm({
            title: POps.t('{name} silinsin mi?', { name: o.name }),
            message: POps.t('Kütüphaneden kaldırılır. Gönderilmiş görevler ve dağıtım geçmişi etkilenmez.'),
            confirmText: o.type === 'script' ? POps.t('Betiği sil') : POps.t('Paketi sil'), danger: true, icon: 'trash'
        });
        if (!ok) return;
        if (await POps.act(null, () => POps.post('/api/delete_package', { id: o.id }), { success: POps.t('{name} silindi.', { name: o.name }) })) {
            if (POps.drawer.isOpen('pkg:' + o.id)) POps.drawer.close();
            run.steps = run.steps.filter(s => s.id !== o.id);
            loadPackages();
        }
    }

    // ================= Paket / betik ekleme ve düzenleme =================
    // toRun: pencere dağıtım penceresinden açıldı; kaydedilen winget paketi açık zincire adım olarak eklenir
    const up = { edit: null, type: 'package', xhr: null, toRun: false };
    function setType(t) {
        up.type = t;
        $('pkgType').querySelectorAll('button').forEach(b => { const on = b.dataset.t === t; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        $('pkgFileArea').hidden = t !== 'package';
        $('pkgCodeArea').hidden = t !== 'script';
        $('pkgWingetArea').hidden = t !== 'winget';
        $('pkgRebootRow').hidden = t === 'winget';
        $('pkgRebootHint').textContent = t === 'package' ? POps.t('Başarılı kurulumdan 15 saniye sonra bilgisayar yeniden başlar.') : POps.t('Komut bittikten 15 saniye sonra bilgisayar yeniden başlar.');
        if (!up.edit) $('pkgTitle').textContent = t === 'package' ? POps.t('Paket yükle') : t === 'winget' ? POps.t('winget paketi ekle') : POps.t('Betik ekle');
        if (!up.edit) $('pkgSave').textContent = t === 'winget' ? POps.t('Ekle') : POps.t('Yükle ve ekle');
        if (t === 'winget') wgSearch();
    }

    // ---- winget kataloğu (paket penceresi)
    function wgSetError(field, on) { $(field).classList.toggle('has-error', on); $(field).querySelector('input').setAttribute('aria-invalid', on ? 'true' : 'false'); }
    function wgValidate(show) {
        const id = $('wgId').value.trim(), ver = $('wgVer').value.trim();
        const idOk = WG_ID.test(id), verOk = !ver || WG_VER.test(ver);
        wgSetError('wgIdField', !!show && !idOk && (!!id || show === 'save'));
        wgSetError('wgVerField', !!show && !verOk);
        return idOk && verOk;
    }
    function renderWgCats() {
        const sel = $('wgCat');
        if (sel.options.length > 1 || !wg.cats.length) return;
        wg.cats.forEach(c => sel.append(POps.el('option', { value: c.id, text: wgCatName(c.id) + ' (' + Number(c.count) + ')' })));
        sel.value = wg.cat;
    }
    function wgItemHtml(p) {
        const on = wg.sel && p.id.toLowerCase() === wg.sel.toLowerCase();
        const note = wgText(p, 'note');
        return `<button type="button" class="wg-item${on ? ' on' : ''}" role="option" aria-selected="${on ? 'true' : 'false'}" data-wg="${escapeHtml(p.id)}">
            <span class="tx"><b>${escapeHtml(p.name)}</b><small>${escapeHtml([p.publisher, wgCatName(p.category)].filter(Boolean).join(' · '))}</small><small title="${escapeHtml(wgText(p, 'description'))}">${escapeHtml(wgText(p, 'description'))}</small>${note ? `<small class="note">${escapeHtml(note)}</small>` : ''}</span>
            <span class="id">${escapeHtml(p.id)}</span></button>`;
    }
    function renderWgList() {
        const box = $('wgList');
        const items = wg.items || [];
        if (!items.length) {
            POps.setEmpty(box, { compact: true, icon: 'search', title: POps.t('Katalogda eşleşen paket yok'), text: POps.t('Paketin winget kimliğini aşağıya yazabilirsiniz.') });
            return;
        }
        const top = box.scrollTop;
        box.innerHTML = items.map(wgItemHtml).join('');
        box.scrollTop = top;
    }
    // Arama sunucuda yapılır (Türkçe harf ve büyük/küçük harf farkı yok sayılır); yalnızca son isteğin yanıtı çizilir
    async function wgSearch() {
        const n = ++wg.seq;
        const box = $('wgList');
        if (!wg.all) POps.setLoading(box, POps.t('Katalog yükleniyor…'));
        try {
            await loadCatalog();
            renderWgCats();
            let items = wg.all;
            if (wg.q || wg.cat) {
                const qs = new URLSearchParams({ limit: '500' });
                if (wg.q) qs.set('q', wg.q);
                if (wg.cat) qs.set('category', wg.cat);
                const r = await POps.get('/api/deploy/winget/catalog?' + qs.toString());
                items = Array.isArray(r && r.items) ? r.items : [];
            }
            if (n !== wg.seq) return;
            wg.items = items;
            renderWgList();
        } catch (e) {
            if (n === wg.seq) POps.setError(box, e, { compact: true, title: POps.t('Katalog alınamadı') });
        }
    }
    function wgPick(id) {
        const p = wgInfo(id) || (wg.items || []).find(x => x.id === id);
        if (!p) return;
        wg.sel = p.id;
        $('wgId').value = p.id;
        // Ad boşsa ya da önceki seçimden geldiyse katalogdaki adla doldurulur; elle yazılmış ad korunur
        const name = $('pkgName').value.trim();
        if (!name || name === wg.auto) { $('pkgName').value = p.name; wg.auto = p.name; }
        wgValidate(false);
        renderWgList();
    }
    let wq = null;
    $('wgSearch').addEventListener('input', (e) => { clearTimeout(wq); wq = setTimeout(() => { wg.q = e.target.value.trim().slice(0, 100); wgSearch(); }, 150); });
    $('wgCat').addEventListener('change', (e) => { wg.cat = e.target.value; wgSearch(); });
    $('wgList').addEventListener('click', (e) => { const b = e.target.closest('[data-wg]'); if (b) wgPick(b.dataset.wg); });
    $('wgId').addEventListener('input', () => {
        const id = $('wgId').value.trim();
        wg.sel = wgInfo(id) ? wgInfo(id).id : '';
        if (!$('wgList').querySelector('.empty-state, .loading-state')) renderWgList();
        wgValidate(true);
    });
    $('wgVer').addEventListener('input', () => wgValidate(true));
    function showFile() {
        const f = $('pkgFile').files && $('pkgFile').files[0];
        const drop = $('pkgDrop');
        drop.classList.toggle('has', !!f);
        if (f) { $('pkgDropT').textContent = f.name; $('pkgDropS').textContent = sizeText(f.size) + ' · ' + POps.t('değiştirmek için yeniden seçin'); }
        else if (up.edit && up.edit.type === 'package' && up.edit.file) { $('pkgDropT').textContent = up.edit.file; $('pkgDropS').textContent = (up.edit.size ? up.edit.size + ' · ' : '') + POps.t('sunucuda kayıtlı; değiştirmek için yeni dosya seçin'); }
        else { $('pkgDropT').textContent = POps.t('Dosya seçin ya da buraya bırakın'); $('pkgDropS').textContent = POps.t('.exe, .msi ya da install.bat içeren .zip'); }
    }
    function setProgress(frac) {
        const p = $('pkgProg');
        if (frac === null) { p.hidden = true; return; }
        p.hidden = false;
        const pct = Math.max(0, Math.min(100, Math.round(frac * 100)));
        $('pkgProgBar').style.width = pct + '%';
        $('pkgProgTxt').textContent = pct >= 100 ? POps.t('Özet hesaplanıyor…') : POps.pct(pct);
    }
    function openPkgModal(o, type, toRun) {
        up.edit = o || null;
        up.toRun = !!toRun;
        $('pkgName').value = o ? o.name : '';
        $('wgId').value = o && o.type === 'winget' ? o.wid : '';
        $('wgVer').value = o && o.type === 'winget' ? o.wver : '';
        $('wgSearch').value = '';
        wg.q = ''; wg.cat = ''; wg.sel = o && o.type === 'winget' ? o.wid : ''; wg.auto = '';
        $('wgCat').value = '';
        wgSetError('wgIdField', false); wgSetError('wgVerField', false);
        $('pkgArgs').value = o ? o.args : '';
        $('pkgCode').value = o && o.type === 'script' ? o.code : '';
        $('pkgReboot').checked = !!(o && o.reboot);
        $('pkgFile').value = '';
        setProgress(null);
        setType(o ? o.type : (type || 'package'));
        if (o) $('pkgTitle').textContent = o.type === 'script' ? POps.t('Betiği düzenle') : POps.t('Paketi düzenle');
        if (o) $('pkgSave').textContent = POps.t('Kaydet');
        showFile();
        openModal('pkgModal');
    }
    // Dosya, ilerleme gösterilebilsin diye XMLHttpRequest ile yüklenir (POps.api ile aynı başlık ve hata metni)
    function uploadFile(file) {
        return new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            up.xhr = xhr;
            xhr.open('POST', ((typeof POPS_API !== 'undefined') ? POPS_API.HTTP_URL : '') + '/api/upload');
            xhr.withCredentials = true;
            xhr.setRequestHeader('X-Requested-With', 'XMLHttpRequest');
            xhr.upload.onprogress = (e) => { if (e.lengthComputable) setProgress(e.loaded / e.total); };
            xhr.onload = () => {
                up.xhr = null;
                if (xhr.status === 401) { window.location.href = '/logout'; return; }
                let data = null;
                try { data = JSON.parse(xhr.responseText); } catch (e) { data = null; }
                if (xhr.status >= 200 && xhr.status < 300 && data && data.status !== 'error') { resolve(data); return; }
                const d = data && data.detail;
                const msg = typeof d === 'string' ? POps.t(d) : (data && data.message) || ({ 413: POps.t('Dosya çok büyük.'), 403: POps.t('Bu işlem için yetkiniz yok.') }[xhr.status]) || POps.t('Sunucu hatası (HTTP {status}).', { status: xhr.status });
                reject(new POps.ApiError(msg, xhr.status, data));
            };
            xhr.onerror = () => { up.xhr = null; reject(new POps.ApiError(POps.t('Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.'), 0, null)); };
            xhr.onabort = () => { up.xhr = null; const e = new Error('abort'); e.name = 'AbortError'; reject(e); };
            const fd = new FormData();
            fd.append('file', file);
            xhr.send(fd);
        });
    }
    async function savePackage() {
        const btn = $('pkgSave');
        const o = up.edit;
        const type = up.type;
        const name = $('pkgName').value.trim();
        const reboot = $('pkgReboot').checked;
        const file = $('pkgFile').files && $('pkgFile').files[0];
        const savedId = o ? o.id : 'mod-' + Date.now();
        if (type === 'winget' && !wgValidate('save')) { (WG_ID.test($('wgId').value.trim()) ? $('wgVer') : $('wgId')).focus(); return; }
        if (!name) { POps.toast('warning', POps.t('Bir ad verin.')); $('pkgName').focus(); return; }
        let command, meta;
        try {
            await POps.busy(btn, async () => {
                if (type === 'winget') {
                    // Komut yalnızca gösterim içindir; dağıtımda kimlik ve sürüm gider (bkz. runGo)
                    const wid = $('wgId').value.trim();
                    command = wingetCommand(wid, $('wgVer').value.trim());
                    meta = `${wid} | winget | ${o && o.type === 'winget' ? o.by : ME}`;
                } else if (type === 'package') {
                    let U, F, H, fileName, size, by;
                    const old = o && o.type === 'package' ? o.payload : null;
                    if (file) {
                        setProgress(0);
                        const r = await uploadFile(file);
                        // İmzalı adres (sunucu /download'u imzasız vermez) ve dosyanın SHA-256 özeti
                        U = POPS_API.DOWNLOAD_URL + '/' + encodeURIComponent(r.filename) + '?sig=' + encodeURIComponent(r.sig);
                        F = 'C:\\POpsLogs\\' + r.filename;
                        H = r.sha256 || '';
                        fileName = r.filename;
                        size = sizeText(file.size);
                        by = ME;
                    } else if (old && old.U && old.F) {
                        U = old.U; F = old.F; H = old.H || '';
                        fileName = o.file; size = o.size; by = o.by;
                    } else {
                        throw new Error(o && o.type === 'package' ? POps.t('Bu paketin dosya bilgisi okunamadı; kurulum dosyasını yeniden seçin.') : POps.t('Kurulum dosyasını seçin.'));
                    }
                    command = packageCommand({ U, F, A: $('pkgArgs').value.trim(), R: reboot ? 1 : 0, H });
                    meta = buildMeta('package', fileName, size, reboot, by);
                } else {
                    const code = $('pkgCode').value.trim();
                    if (!code) throw new Error(POps.t('Çalıştırılacak komutu yazın.'));
                    command = code + (reboot ? SCRIPT_REBOOT : '');
                    meta = buildMeta('script', '', '', reboot, o && o.type === 'script' ? o.by : ME);
                }
                await POps.post('/api/add_package', {
                    id: savedId, name, type, meta, command,
                    icon: typeIcon(type), color: type === 'package' ? '#3b82f6' : type === 'winget' ? '#10b981' : '#f59e0b'
                });
            });
        } catch (e) {
            setProgress(null);
            if (e && e.name === 'AbortError') return;
            POps.toast('error', POps.errorMessage(e));
            return;
        }
        if (!command) return;   // düğme zaten meşguldü
        setProgress(null);
        closeModal('pkgModal');
        POps.toast('success', o ? POps.t('{name} güncellendi.', { name }) : POps.t('{name} eklendi.', { name }));
        await loadPackages();
        // Açık dağıtım penceresindeki adımlar yeni haliyle gider; pencereden eklenen winget paketi zincire girer
        run.steps = run.steps.map(s => ui.pkgs.find(x => x.id === s.id) || s);
        const added = up.toRun && ui.pkgs.find(x => x.id === savedId);
        if (added && $('runModal').classList.contains('open')) run.steps.push(added);
        if ($('runModal').classList.contains('open')) { renderSteps(); renderRunSum(); }
    }
    $('pkgType').addEventListener('click', (e) => { const b = e.target.closest('button[data-t]'); if (b) setType(b.dataset.t); });
    $('pkgFile').addEventListener('change', showFile);
    ['dragenter', 'dragover'].forEach(ev => $('pkgDrop').addEventListener(ev, () => $('pkgDrop').classList.add('over')));
    ['dragleave', 'drop'].forEach(ev => $('pkgDrop').addEventListener(ev, () => $('pkgDrop').classList.remove('over')));
    $('pkgSave').addEventListener('click', savePackage);
    $('pkgModal').addEventListener('pops:modal-close', () => { if (up.xhr) up.xhr.abort(); });
    $('uploadBtn').addEventListener('click', () => openPkgModal(null, 'package'));

    // ================= Dağıtım penceresi =================
    const run = { steps: [], mode: 'lab', q: '', all: false, labs: new Set(), pcs: new Set(), incOff: false, sig: '' };
    const WG_NEW = '__winget_new';   // adım listesinde "winget kataloğundan ekle…"
    const labOf = (d) => (d.lab && d.lab !== dev.UNASSIGNED ? d.lab : dev.UNASSIGNED);
    const labName = (l) => (l === dev.UNASSIGNED ? POps.t('Atanmamış') : l);
    function runLabs() {
        const devs = state.devices || [];
        return dev.labs().concat(devs.some(d => labOf(d) === dev.UNASSIGNED) ? [dev.UNASSIGNED] : []);
    }
    function runTargets() {
        const on = [], off = [];
        (state.devices || []).forEach(d => {
            if (!(run.all || run.labs.has(labOf(d)) || run.pcs.has(d.hostname))) return;
            (POps.isOffline(d) ? off : on).push(d.hostname);
        });
        return { on, off, send: run.incOff ? on.concat(off) : on };
    }
    // İşlemin başlığı sunucuya Türkçe gider (veri); pencerede ve bildirimlerde stepsLabel gösterilir
    function stepsTitle() {
        if (!run.steps.length) return '';
        return run.steps.length === 1 ? stepName(run.steps[0]) : `${stepName(run.steps[0])} +${run.steps.length - 1} adım`;
    }
    function stepsLabel() {
        if (!run.steps.length) return '';
        return run.steps.length === 1 ? stepName(run.steps[0]) : POps.tn('{name} +{n} adım', run.steps.length - 1, { name: stepName(run.steps[0]) });
    }
    // Seçilen hedeflerden ajanı winget'i duyurmayanlar (X-Agent-Features; eski ajan): winget adımı orada reddedilir
    const wingetReady = (d) => Array.isArray(d.agent_features) && d.agent_features.includes('winget');
    function wingetGap(hosts) {
        const want = new Set(hosts);
        return (state.devices || []).filter(d => want.has(d.hostname) && !wingetReady(d)).length;
    }
    function openRun(steps, chain) {
        run.steps = steps.slice();
        run.chain = !!chain || !steps.length;
        run.mode = 'lab'; run.q = ''; run.all = false; run.labs.clear(); run.pcs.clear(); run.incOff = false; run.sig = '';
        $('runSearch').value = '';
        $('runReason').value = '';
        renderRun();
        openModal('runModal');
    }
    function renderSteps() {
        const n = run.steps.length;
        const multi = n > 1;
        $('runTitle').textContent = !n || run.chain || multi ? POps.t('Görev zinciri') : POps.t('Dağıt · {name}', { name: run.steps[0].name });
        $('runStepsNote').textContent = multi ? POps.t('Hedefte bu sırayla çalışır') : n ? POps.t('Zincir için adım ekleyebilirsiniz') : '';
        $('runSteps').innerHTML = n
            ? run.steps.map((s, i) => `<div class="step"><span class="n">${Number(i + 1)}</span>
                <span class="tx"><b>${escapeHtml(s.name)}</b><small>${escapeHtml(s.type === 'winget' ? [s.kind, s.wid, s.wver].filter(Boolean).join(' · ') : s.kind + (s.size ? ' · ' + s.size : '') + (s.reboot ? ' · ' + POps.t('yeniden başlatır') : ''))}</small></span>
                ${multi ? `<button type="button" class="ibtn sm" data-step="up" data-i="${Number(i)}" title="${escapeHtml(POps.t('Yukarı taşı'))}" aria-label="${escapeHtml(POps.t('Yukarı taşı'))}" ${i === 0 ? 'disabled' : ''}>${POps.iconHtml('arrow-up', 'sm')}</button><button type="button" class="ibtn sm" data-step="down" data-i="${Number(i)}" title="${escapeHtml(POps.t('Aşağı taşı'))}" aria-label="${escapeHtml(POps.t('Aşağı taşı'))}" ${i === n - 1 ? 'disabled' : ''}>${POps.iconHtml('arrow-down', 'sm')}</button>` : ''}
                <button type="button" class="ibtn sm danger" data-step="rm" data-i="${Number(i)}" title="${escapeHtml(POps.t('Adımı çıkar'))}" aria-label="${escapeHtml(POps.t('Adımı çıkar'))}">${POps.iconHtml('x', 'sm')}</button></div>`).join('')
            : `<div class="step empty">${POps.tHtml('Henüz adım yok. Aşağıdan paket ya da betik ekleyin.')}</div>`;
        const optHtml = (list) => list.map(o => `<option value="${escapeHtml(o.id)}">${escapeHtml(o.name)}</option>`).join('');
        const pk = ui.pkgs.filter(o => o.type === 'package'), sc = ui.pkgs.filter(o => o.type === 'script'), wl = ui.pkgs.filter(o => o.type === 'winget' && o.wid);
        $('runAdd').innerHTML = `<option value="">${n ? POps.tHtml('Adım ekle…') : POps.tHtml('Paket ya da betik seçin…')}</option>`
            + (pk.length ? `<optgroup label="${escapeHtml(POps.t('Paketler'))}">${optHtml(pk)}</optgroup>` : '')
            + (sc.length ? `<optgroup label="${escapeHtml(POps.t('Betikler'))}">${optHtml(sc)}</optgroup>` : '')
            + `<optgroup label="${escapeHtml(POps.t('winget paketleri'))}">${optHtml(wl)}<option value="${WG_NEW}">${POps.tHtml('winget kataloğundan ekle…')}</option></optgroup>`;
    }
    function renderRunList(force) {
        const box = $('runList');
        $('runMode').querySelectorAll('button').forEach(b => { const on = b.dataset.m === run.mode; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        if (!state.devicesLoaded) { POps.setLoading(box, POps.t('Bilgisayarlar yükleniyor…')); return; }
        const devs = state.devices || [];
        const sig = JSON.stringify([run.mode, run.q, run.all, [...run.labs], [...run.pcs], devs.map(d => [d.hostname, d.lab, d.status, POps.deviceName(d), d.current_user])]);
        if (!force && sig === run.sig) return;
        run.sig = sig;
        const q = run.q.toLocaleLowerCase('tr');
        const hit = (...vals) => !q || vals.some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
        const labs = runLabs();
        let html = '';
        if (run.mode === 'lab') {
            const countText = (list) => { const on = list.filter(d => !POps.isOffline(d)).length; return list.length ? POps.t('{on} açık · {off} kapalı', { on, off: list.length - on }) : POps.t('boş'); };
            if (!q) html += `<label class="pick all"><input type="checkbox" data-all="1" ${run.all ? 'checked' : ''}><span class="tx"><b>${POps.tHtml('Bütün ağ')}</b><small>${escapeHtml(countText(devs))}</small></span></label>`;
            // Boş sınıf hedef olamaz; listede gösterilmez
            html += labs.filter(l => hit(labName(l))).map(l => [l, devs.filter(d => labOf(d) === l)]).filter(x => x[1].length).map(([l, list]) =>
                `<label class="pick"><input type="checkbox" data-lab="${escapeHtml(l)}" ${run.all || run.labs.has(l) ? 'checked' : ''} ${run.all ? 'disabled' : ''}><span class="tx"><b>${escapeHtml(labName(l))}</b><small>${escapeHtml(countText(list))}</small></span></label>`
            ).join('');
        } else {
            labs.forEach(l => {
                const list = devs.filter(d => labOf(d) === l && hit(POps.deviceName(d), d.hostname, dev.user(d), labName(l)))
                    .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
                if (!list.length) return;
                const viaLab = run.all || run.labs.has(l);
                const allSel = viaLab || list.every(d => run.pcs.has(d.hostname));
                html += `<div class="pick-h"><span>${escapeHtml(labName(l))}</span>${viaLab ? `<span>${POps.tHtml('sınıfla seçili')}</span>` : `<button type="button" class="linkbtn" data-group="${escapeHtml(l)}">${allSel ? POps.tHtml('Hiçbirini seçme') : POps.tHtml('Hepsini seç')}</button>`}</div>`;
                html += list.map(d => {
                    const st = dev.state(d);
                    const on = viaLab || run.pcs.has(d.hostname);
                    return `<label class="pick${st.cls === 'off' ? ' off' : ''}"><input type="checkbox" data-pc="${escapeHtml(d.hostname)}" data-g="${escapeHtml(l)}" ${on ? 'checked' : ''} ${viaLab ? 'disabled' : ''}>
                        <span class="tx"><b>${escapeHtml(POps.deviceName(d))}</b><small>${escapeHtml(dev.subline(d))}</small></span>
                        <span class="st"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.cls === 'off' ? POps.t('Kapalı') : st.word)}</span></label>`;
                }).join('');
            });
        }
        const top = box.scrollTop;
        if (html) box.innerHTML = html;
        else box.innerHTML = `<div class="run-none">${q ? POps.tHtml('Aramaya uyan hedef yok.') : POps.tHtml('Henüz bilgisayar yok.')}</div>`;
        box.scrollTop = top;
    }
    function renderRunSum() {
        const t = runTargets();
        const total = t.on.length + t.off.length;
        const n = t.send.length;
        const boldHtml = (x) => `<b>${Number(x)}</b>`;
        $('runSum').innerHTML = total
            ? `<span class="sum">${POps.tnHtml('{n} bilgisayar seçildi', total, null, { n: boldHtml(total) })}</span>`
              + (t.off.length ? `<span class="sum"><span class="dot off"></span>${POps.tnHtml('{n} kapalı', t.off.length, null, { n: boldHtml(t.off.length) })} · ${run.incOff ? POps.tHtml('açılınca kuracak') : POps.tHtml('atlanacak')}</span>`
                + `<label class="switch-field"><span class="switch"><input type="checkbox" id="runIncOff" ${run.incOff ? 'checked' : ''}><span></span></span>${POps.tHtml('Kapalılar açılınca kursun')}</label>` : '')
            : `<span>${POps.tHtml('Hedef seçilmedi.')}</span>`;
        const gap = run.steps.some(s => s.type === 'winget') ? wingetGap(t.send) : 0;
        if (gap) $('runSum').insertAdjacentHTML('beforeend', `<span class="sum"><span class="dot warn"></span>${POps.tnHtml('{n} bilgisayarın ajanı winget desteklemiyor; winget adımı orada reddedilir', gap, null, { n: boldHtml(gap) })}</span>`);
        const go = $('runGo');
        go.disabled = !n || !run.steps.length;
        go.textContent = n ? POps.tn('{n} bilgisayara dağıt', n) : POps.t('Dağıt');
    }
    function renderRun() { renderSteps(); renderRunList(true); renderRunSum(); }

    $('runSteps').addEventListener('click', (e) => {
        const b = e.target.closest('button[data-step]');
        if (!b || b.disabled) return;
        const i = Number(b.dataset.i);
        if (b.dataset.step === 'rm') run.steps.splice(i, 1);
        else {
            const j = b.dataset.step === 'up' ? i - 1 : i + 1;
            if (j < 0 || j >= run.steps.length) return;
            [run.steps[i], run.steps[j]] = [run.steps[j], run.steps[i]];
        }
        renderSteps(); renderRunSum();
        const again = $('runSteps').querySelector(`button[data-step="${b.dataset.step}"][data-i="${b.dataset.step === 'rm' ? Math.min(i, run.steps.length - 1) : (b.dataset.step === 'up' ? i - 1 : i + 1)}"]`);
        if (again && !again.disabled) again.focus();
    });
    $('runAdd').addEventListener('change', (e) => {
        if (e.target.value === WG_NEW) { e.target.value = ''; openPkgModal(null, 'winget', true); return; }
        const o = ui.pkgs.find(x => x.id === e.target.value);
        if (o) { run.steps.push(o); renderSteps(); renderRunSum(); }
        e.target.value = '';
    });
    $('runMode').addEventListener('click', (e) => { const b = e.target.closest('button[data-m]'); if (b && b.dataset.m !== run.mode) { run.mode = b.dataset.m; renderRunList(true); } });
    let rq = null;
    $('runSearch').addEventListener('input', (e) => { clearTimeout(rq); rq = setTimeout(() => { run.q = e.target.value.trim(); renderRunList(true); }, 120); });
    $('runList').addEventListener('change', (e) => {
        const c = e.target;
        if (c.dataset.all) run.all = c.checked;
        else if (c.dataset.lab) c.checked ? run.labs.add(c.dataset.lab) : run.labs.delete(c.dataset.lab);
        else if (c.dataset.pc) c.checked ? run.pcs.add(c.dataset.pc) : run.pcs.delete(c.dataset.pc);
        else return;
        renderRunList(true); renderRunSum();
    });
    $('runList').addEventListener('click', (e) => {
        const b = e.target.closest('button[data-group]');
        if (!b) return;
        e.preventDefault();
        // Aramayla görünenler seçilir
        const hosts = [...$('runList').querySelectorAll('input[data-pc]')].filter(c => c.dataset.g === b.dataset.group).map(c => c.dataset.pc);
        const allSel = hosts.every(h => run.pcs.has(h));
        hosts.forEach(h => { allSel ? run.pcs.delete(h) : run.pcs.add(h); });
        renderRunList(true); renderRunSum();
    });
    $('runSum').addEventListener('change', (e) => { if (e.target.id === 'runIncOff') { run.incOff = e.target.checked; renderRunSum(); } });
    $('runGo').addEventListener('click', async (e) => {
        const btn = e.currentTarget;
        const t = runTargets();
        if (!run.steps.length) { POps.toast('warning', POps.t('Önce bir adım ekleyin.')); return; }
        if (!t.send.length) { POps.toast('warning', t.off.length ? POps.t('Seçilen bilgisayarların hepsi kapalı.') : POps.t('En az bir hedef seçin.')); return; }
        const title = stepsTitle();
        const label = stepsLabel();
        const n = t.send.length;
        const reason = $('runReason').value.trim();
        const steps = run.steps.slice();
        let r;
        try {
            r = await POps.busy(btn, () => POps.post('/api/deploy_orchestration', {
                target_mode: 'PC', targets: t.send,
                taskSequence: steps.map(s => (s.type === 'winget'
                    ? { name: stepName(s), type: 'WINGET', winget: { id: s.wid, version: s.wver || null } }
                    : { name: s.name, type: s.type, command: s.command })),
                title: title.slice(0, 200), source: 'deploy', reason: reason || null
            }, { jobTitle: label + ' · ' + POps.tn('{n} bilgisayar', n) }));
        } catch (err) { POps.toast('error', POps.t('Dağıtım başlatılamadı: {error}', { error: POps.errorMessage(err) })); return; }
        if (!r) return;
        closeModal('runModal');
        const pcs = Math.round((r.created || 0) / steps.length);
        const closed = r.skipped_module_closed ? ' ' + POps.tn('{n} bilgisayarda dosya dağıtımı modülü kapalı; atlandı.', r.skipped_module_closed) : '';
        POps.toast(r.duplicate ? 'info' : 'success', (r.duplicate ? POps.t('Aynı dağıtım az önce gönderilmişti.') : POps.tn('{title} {n} bilgisayara gönderildi.', pcs, { title: label })) + closed);
        loadTasks();
    });
    $('chainBtn').addEventListener('click', () => {
        if (!ui.loaded) return;
        if (!ui.pkgs.length) { POps.toast('info', POps.t('Önce bir paket ya da betik ekleyin.')); return; }
        openRun([], true);
    });

    // ================= Eşzamanlı kurulum sınırı =================
    $('limitBtn').addEventListener('click', async () => {
        const v = await POps.prompt({
            title: POps.t('Eşzamanlı kurulum sınırı'),
            message: POps.t('Aynı anda en çok kaç bilgisayarda görev çalışsın? Fazlası sırada bekler; büyük dağıtımlar ağı ve sunucuyu tıkamaz.'),
            label: POps.t('Bilgisayar sayısı'), inputType: 'number', inputMode: 'numeric', defaultValue: ui.limit || 5, confirmText: POps.t('Kaydet'), icon: 'sliders',
            validate: (s) => { const n = Number(s); return Number.isInteger(n) && n >= 1 && n <= 100 ? '' : POps.t('1 ile 100 arasında bir sayı girin.'); }
        });
        if (v === null) return;
        const lim = Number(v);
        if (await POps.act(null, () => POps.post('/api/set_concurrent_limit', { limit: lim }), { success: POps.tn('Aynı anda en çok {n} bilgisayarda görev çalışacak.', lim), error: POps.t('Kaydedilemedi.') })) {
            ui.limit = lim;
            renderSummary();
        }
    });

    // ================= Liste etkileşimi =================
    tbody.addEventListener('click', (e) => {
        const copy = e.target.closest('[data-copy]');
        if (copy) { e.stopPropagation(); copyText(copy.dataset.copy, POps.t('Özet kopyalandı.')); return; }
        const tr = e.target.closest('tr[data-id]');
        if (tr && !e.target.closest('a, .mark')) openPkg(tr.dataset.id);
    });
    tbody.addEventListener('keydown', (e) => {
        const tr = e.target.closest('tr[data-id]');
        if (tr && e.target === tr && e.key === 'Enter') { e.preventDefault(); openPkg(tr.dataset.id); }
    });
    $('depFilter').addEventListener('click', (e) => { const b = e.target.closest('button[data-f]'); if (b) { ui.f = b.dataset.f; renderTable(true); } });
    let qt = null;
    $('depSearch').addEventListener('input', (e) => { clearTimeout(qt); qt = setTimeout(() => { ui.q = e.target.value.trim(); renderTable(true); }, 120); });

    // Esc yalnızca üstteki pencereyi kapatsın: ortak modal dinleyicisi pencereyi kapattıktan sonra ayrıntı paneli de
    // Esc'yi görüp kapanıyordu (pops_script.js). Pencere açıkken Esc burada yakalanır.
    window.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape' || document.querySelector('.pops-dialog-overlay, .pops-menu, .palette-overlay')) return;
        const m = ['pkgModal', 'runModal'].map(id => document.getElementById(id)).find(x => x && x.classList.contains('open'));
        if (!m) return;
        e.preventDefault();
        e.stopImmediatePropagation();
        closeModal(m);
    }, true);

    // Hedef listesi ve cihaz adları ortak cihaz listesinden gelir
    let namesReady = false;
    document.addEventListener('pops_data_updated', () => {
        if (!state.devicesLoaded) return;
        if ($('runModal').classList.contains('open')) { renderRunList(false); renderRunSum(); }
        if (!namesReady) { namesReady = true; renderTable(true); if (ui.focus) renderDrawer(ui.focus, true); }
    });
    POps.watchDevices();
    loadPackages();
    loadTasks();
    // Geçmiş: dağıtım sürerken 10 sn'de, yoksa dakikada bir; paket listesi dakikada bir tazelenir
    popsPoll(async () => {
        if (Date.now() - ui.pkgAt > 60000) await loadPackages();
        if (anyRunning() || Date.now() - ui.tasksAt > 60000) await loadTasks();
    }, 10000);
})();
</script>

<?php include 'includes/footer.php'; ?>
