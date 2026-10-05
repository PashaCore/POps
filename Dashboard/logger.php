<?php include 'includes/header.php'; ?>

<style>
    .lg-tabs { margin-bottom: 16px; }
    .lg-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 16px; }
    .lg-bar .grow { flex: 1; }
    .lg-bar .search-field { flex: 0 1 260px; min-width: 180px; }
    .lg-bar select { width: auto; min-width: 150px; }
    .segmented .count { color: var(--text-muted); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }
    .lg-list { padding: 4px 16px 8px; }
    .lg-list .act { padding: 13px 6px; outline: none; }
    .lg-list .act:focus-visible { box-shadow: var(--focus-ring); }
    .lg-list .act > .facts { grid-column: 2 / -1; margin-top: -2px; cursor: text; }
    .lg-day { font-size: var(--text-xs); font-weight: var(--fw-semibold); color: var(--text-muted); padding: 18px 6px 2px; }
    .lg-day:first-child { padding-top: 12px; }
    .lg-links { grid-column: 1 / -1; display: flex; gap: 14px; flex-wrap: wrap; padding-top: 6px; }
    .lg-links button { color: var(--primary-500); font-size: var(--text-xs); font-weight: var(--fw-medium); }
    .lg-links button:hover { text-decoration: underline; }
    .lg-note { font-size: var(--text-xs); color: var(--text-muted); padding: 10px 4px 0; display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .lg-note button { color: var(--primary-500); font-size: var(--text-xs); font-weight: var(--fw-medium); }
    .lg-note button:hover { text-decoration: underline; }
    .lg-fbtn .badge { min-width: 18px; justify-content: center; padding: 0 6px; background: var(--primary-500); color: #fff; }
    .lg-fpanel { position: fixed; z-index: 900; width: 340px; max-width: calc(100vw - 24px); background: var(--bg-surface); border-radius: 14px; box-shadow: var(--shadow-xl); padding: 14px 16px; display: flex; flex-direction: column; gap: 12px; animation: fadeIn 0.12s ease; }
    .lg-fpanel[hidden] { display: none; }
    .lg-fpanel label { display: flex; flex-direction: column; gap: 5px; margin: 0; font-size: var(--text-xs); font-weight: var(--fw-medium); color: var(--text-tertiary); }
    .lg-fpanel .two { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
    .lg-fpanel .foot { display: flex; justify-content: space-between; align-items: center; padding-top: 4px; border-top: 1px solid #f0f0f3; }
    .lg-chips { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; margin: -6px 0 12px; }
    .lg-chips:empty { display: none; }
    .lg-chips .chip { gap: 6px; padding-right: 5px; cursor: default; }
    .lg-chips .chip b { font-weight: 600; }
    .lg-chips .chip button { width: 20px; height: 20px; border-radius: 99px; display: inline-flex; align-items: center; justify-content: center; color: inherit; }
    .lg-chips .chip button:hover { background: var(--bg-hover); }
    .lg-chips .clear { font-size: var(--text-sm); color: var(--primary-500); padding: 0 4px; }
    .lg-chips .clear:hover { text-decoration: underline; }
    .lg-pager { display: flex; align-items: center; justify-content: space-between; gap: 12px; flex-wrap: wrap; padding: 12px 2px 0; font-size: var(--text-sm); color: var(--text-tertiary); }
    .lg-pager .pages { display: flex; align-items: center; gap: 2px; }
    .lg-pager .pages button { min-width: 32px; height: 32px; padding: 0 8px; border-radius: 8px; color: var(--text-primary); font-variant-numeric: tabular-nums; }
    .lg-pager .pages button:hover:not(:disabled) { background: var(--bg-hover); }
    .lg-pager .pages button.on { background: var(--text-primary); color: #fff; }
    .lg-pager .pages button:disabled { color: var(--text-muted); cursor: default; }
    .lg-pager .pages .gap { min-width: 24px; text-align: center; }
    .lg-pager .size { display: flex; align-items: center; gap: 8px; }
    .lg-pager .size select { width: auto; min-height: 32px; padding-top: 0; padding-bottom: 0; }
    .hw-table tbody tr { cursor: pointer; }
    .hw-table tbody tr.is-focus { background: #f0f6ff; }
    .hw-table .nm { font-weight: var(--fw-semibold); }
    .hw-table .sub { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .hw-table td.mono { font-family: var(--font-mono); font-size: 12px; color: var(--text-secondary); }
    .hw-table .wait { color: var(--text-muted); }
    .hw-table td:nth-child(3), .hw-table td:nth-last-child(-n+2) { white-space: nowrap; }
    .lg-dlinks { display: flex; gap: 8px; flex-wrap: wrap; }
    @media (max-width: 640px) {
        .lg-bar select { flex: 1 1 auto; }
                .lg-list { padding: 2px 10px 6px; }
        .lg-list .act > .facts { grid-column: 1 / -1; grid-template-columns: 96px minmax(0, 1fr); }
        .lg-bar .search-field { flex: 1 1 100%; }
    }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Kayıtlar'); ?></h1>
        <div class="summary" id="lgSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="lgExport" data-tip="<?php _e('Listeyi dışa aktar (CSV)'); ?>" data-tip-pos="left" aria-label="<?php _e('Listeyi dışa aktar'); ?>"><?php echo pops_icon('download'); ?></button>
        <button type="button" class="ibtn boxed" id="lgMenuBtn" data-tip="<?php _e('Diğer işlemler'); ?>" data-tip-pos="left" aria-label="<?php _e('Diğer işlemler'); ?>" aria-haspopup="menu" hidden><?php echo pops_icon('more'); ?></button>
    </div>
</div>

<div class="tabs lg-tabs" id="lgTabs" role="tablist" aria-label="<?php _e('Görünüm'); ?>">
    <button type="button" class="tab active" data-tab="log" role="tab" aria-selected="true"><?php _e('Olaylar'); ?></button>
    <button type="button" class="tab" data-tab="hw" role="tab" aria-selected="false"><?php _e('Donanım'); ?></button>
</div>

<section id="lgPaneLog">
    <div class="lg-bar">
        <div class="segmented" id="lgFilter" role="group" aria-label="<?php _e('Önem derecesine göre süz'); ?>">
            <button type="button" data-f="all" class="active" aria-pressed="true"><?php _e('Tümü'); ?> <span class="count" data-n="all"></span></button>
            <button type="button" data-f="warn" aria-pressed="false"><?php _e('Uyarılar'); ?> <span class="count" data-n="warn"></span></button>
            <button type="button" data-f="sec" aria-pressed="false"><?php _e('Güvenlik'); ?> <span class="count" data-n="sec"></span></button>
        </div>
        <button type="button" class="btn secondary lg-fbtn" id="lgFiltersBtn" aria-haspopup="dialog" aria-expanded="false" aria-controls="lgFPanel"><?php echo pops_icon('filter', 'sm'); ?><?php _e('Süzgeçler'); ?><span class="badge" id="lgFCount" hidden></span></button>
        <span class="grow"></span>
        <div class="search-field">
            <?php echo pops_icon('search', 'sm'); ?>
            <input type="search" id="lgSearch" placeholder="<?php _e('Olay, bilgisayar ya da kişi'); ?>" aria-label="<?php _e('Kayıtlarda ara'); ?>">
        </div>
    </div>
    <div class="lg-fpanel" id="lgFPanel" role="dialog" aria-label="<?php _e('Süzgeçler'); ?>" hidden>
        <label><?php _e('Tür'); ?><select id="lgKind"><option value=""><?php _e('Bütün türler'); ?></option></select></label>
        <label><?php _e('Sınıf'); ?><select id="lgLab"><option value=""><?php _e('Bütün sınıflar'); ?></option></select></label>
        <label><?php _e('Kişi'); ?><select id="lgWho"><option value=""><?php _e('Herkes'); ?></option></select></label>
        <div class="two">
            <label><?php _e('Başlangıç'); ?><input type="date" id="lgFrom"></label>
            <label><?php _e('Bitiş'); ?><input type="date" id="lgTo"></label>
        </div>
        <div class="foot"><button type="button" class="btn ghost sm" id="lgFClear"><?php _e('Süzgeçleri temizle'); ?></button><button type="button" class="btn sm" id="lgFDone"><?php _e('Tamam'); ?></button></div>
    </div>
    <div class="lg-chips" id="lgChips"></div>
    <div class="card"><div class="lg-list" id="lgList" aria-live="polite"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Kayıtlar yükleniyor…'); ?></div></div></div>
    <div class="lg-pager" id="lgPager" hidden>
        <span id="lgRange"></span>
        <div class="pages" id="lgPages" role="navigation" aria-label="<?php _e('Sayfalar'); ?>"></div>
        <label class="size"><?php _e('Sayfa başına'); ?> <select id="lgSize" aria-label="<?php _e('Sayfa başına kayıt'); ?>"><option>25</option><option>50</option><option>75</option><option>100</option></select></label>
    </div>
    <div class="lg-note" id="lgNote"></div>
</section>

<section id="lgPaneHw" hidden>
    <div class="lg-bar">
        <select id="hwLab" aria-label="<?php _e('Sınıfa göre süz'); ?>"><option value=""><?php _e('Bütün sınıflar'); ?></option></select>
        <span class="grow"></span>
        <div class="search-field">
            <?php echo pops_icon('search', 'sm'); ?>
            <input type="search" id="hwSearch" placeholder="<?php _e('Bilgisayar, işlemci, IP, MAC'); ?>" aria-label="<?php _e('Donanımda ara'); ?>">
        </div>
    </div>
    <div class="table-wrap">
        <table class="data-table hw-table wide">
            <thead><tr><th><?php _e('Bilgisayar'); ?></th><th><?php _e('İşlemci'); ?></th><th><?php _e('Bellek'); ?></th><th>Disk</th><th><?php _e('İşletim sistemi'); ?></th><th>IP</th><th><?php _e('Güncelleme'); ?></th></tr></thead>
            <tbody id="hwBody"><tr><td colspan="7"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Donanım bilgileri yükleniyor…'); ?></div></td></tr></tbody>
        </table>
    </div>
    <div class="lg-note"><?php _e('Donanım bilgisini ajan açılışta ve günde bir kez gönderir.'); ?></div>
</section>

<?php /* Sayfa betiği assets/pages/logger.js: sayfada satır içi betik yok */ ?>
<script src="<?php echo htmlspecialchars(pops_asset('assets/pages/logger.js'), ENT_QUOTES, 'UTF-8'); ?>" defer></script>

<?php include 'includes/footer.php'; ?>
