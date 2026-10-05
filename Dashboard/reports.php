<?php include 'includes/header.php'; ?>
<?php $rpCanEdit = in_array($_SESSION['role'] ?? '', ['admin', 'superadmin'], true); ?>

<style>
    .rp-head { display: flex; align-items: flex-end; gap: 12px; flex-wrap: wrap; border-bottom: 1px solid var(--border-subtle); margin-bottom: 20px; }
    .rp-head .tabs { border-bottom: 0; flex: 1 1 auto; min-width: 0; }
    .rp-tools { display: flex; align-items: center; gap: 8px; padding-bottom: 8px; }
    .rp-tools select { width: auto; min-width: 140px; }
    .rp-pane { display: flex; flex-direction: column; gap: 20px; }
    .rp-pane[hidden] { display: none; }
    .rp-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .rp-bar .grow { flex: 1; }
    .rp-bar .search-field { flex: 0 1 280px; min-width: 180px; }
    .rp-meta { font-size: var(--text-sm); color: var(--text-tertiary); }
    .rp-meta b { color: var(--text-primary); font-weight: var(--fw-semibold); font-variant-numeric: tabular-nums; }
    .rp-note { font-size: var(--text-xs); color: var(--text-muted); line-height: 1.55; padding: 0 4px; margin-top: -8px; }
    .rp-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); gap: 20px; align-items: start; }
    .rp-grid > .card + .card { margin-top: 0; }
    .rp-card .card-body { padding-top: 14px; }
    .kpi .f .dot { margin-right: 5px; vertical-align: 1px; }
    .segmented .count { color: var(--text-muted); font-variant-numeric: tabular-nums; font-weight: var(--fw-regular); }
    .kpi .f .go { display: inline-flex; align-items: center; }
    .rp-kpis { grid-template-columns: repeat(3, minmax(0, 1fr)); }
    .rp-kpis .kpi { display: flex; flex-direction: column; justify-content: flex-start; align-items: stretch; }
    .rp-kpis .kpi .f { margin-top: auto; padding-top: 10px; align-items: flex-end; }
    button.kpi { font: inherit; cursor: pointer; }
    #licModal .form-grid { margin-bottom: var(--space-4); }
    a.kpi { cursor: pointer; }

    /* Günlük olay grafiği: tek seri, ince sütun, üstü yuvarlak, 2px boşluk; üzerine gelince değer */
    .daychart { position: relative; display: flex; align-items: flex-end; gap: 2px; height: 150px; border-bottom: 1px solid var(--border-subtle); }
    .daychart .grid { position: absolute; left: 0; right: 0; top: 0; border-top: 1px solid #f0f0f3; pointer-events: none; }
    .daychart .grid span { position: absolute; right: 0; top: -18px; font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
    .daychart .col { flex: 1 1 0; height: 100%; display: flex; align-items: flex-end; justify-content: center; min-width: 2px; border-radius: 4px 4px 0 0; }
    .daychart .col:hover { background: var(--bg-surface-2); }
    .daychart .bar { width: 100%; max-width: 24px; background: var(--primary-500); border-radius: 4px 4px 0 0; }
    .daychart .col:hover .bar { background: var(--primary-600); }
    .daychart .col.zero .bar { height: 2px !important; background: #ececf0; border-radius: 1px; }
    .daychart-axis { display: flex; justify-content: space-between; font-size: 11px; color: var(--text-muted); margin-top: 6px; }
    .rp-calm { display: flex; align-items: center; gap: 8px; font-size: var(--text-sm); color: var(--text-tertiary); padding: 10px 0; }

    /* Yatay çubuk listesi */
    .hbar { display: grid; grid-template-columns: minmax(90px, 150px) minmax(0, 1fr) 40px; gap: 12px; align-items: center; font-size: var(--text-sm); padding: 6px 0; }
    .hbar .k { color: var(--text-secondary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .hbar .t { background: #f2f2f7; border-radius: 4px; height: 8px; overflow: hidden; }
    .hbar .f { background: var(--primary-500); height: 8px; border-radius: 0 4px 4px 0; }
    .hbar .n { color: var(--text-primary); font-variant-numeric: tabular-nums; text-align: right; }

    /* Sade liste satırı */
    .rp-li { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 10px 0; border-top: 1px solid #f0f0f3; font-size: var(--text-sm); }
    .rp-li:first-child { border-top: 0; padding-top: 2px; }
    .rp-li .k { min-width: 0; display: flex; align-items: center; gap: 8px; }
    .rp-li .k > span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .rp-li .sub { color: var(--text-muted); font-size: var(--text-xs); }
    .rp-li .n { font-variant-numeric: tabular-nums; color: var(--text-primary); font-weight: var(--fw-medium); }
    a.rp-li { color: inherit; border-radius: 8px; }
    a.rp-li:hover { background: var(--bg-surface-2); margin: 0 -8px; padding-left: 8px; padding-right: 8px; }

    /* Tablolar */
    .rp-table tbody tr { cursor: pointer; }
    .rp-table tbody tr.is-focus { background: #f0f6ff; }
    .rp-table .nm { font-weight: var(--fw-semibold); }
    .rp-table .sub { font-size: var(--text-xs); color: var(--text-muted); margin-top: 1px; }
    .rp-table .st { display: inline-flex; align-items: center; gap: 7px; white-space: nowrap; }
    .rp-table td.faint, .rp-table .faint { color: var(--text-muted); }
    .rp-table td.num .sub { white-space: nowrap; }
    .use { display: flex; align-items: center; gap: 10px; white-space: nowrap; }
    .use .pbar { width: 90px; }
    .rp-dlist .rp-li { padding: 9px 0; }
    .rp-dlist .rp-li > .ico { color: var(--text-muted); }
    .rp-dacts { display: flex; gap: 8px; flex-wrap: wrap; }
    .rp-preview { font-size: var(--text-xs); color: var(--text-tertiary); line-height: 1.5; min-height: 1.2em; }
    @media (max-width: 640px) {
        .rp-head { align-items: stretch; }
        .rp-tools { width: 100%; padding: 8px 0; }
        .rp-tools select, .rp-tools .btn { flex: 1 1 auto; }
        .rp-bar .search-field { flex: 1 1 100%; }
        .rp-grid { grid-template-columns: minmax(0, 1fr); }
        .hbar { grid-template-columns: minmax(70px, 110px) minmax(0, 1fr) 36px; }
        .rp-kpis .kpi { padding: 14px; }
        .rp-kpis .kpi .v { font-size: 24px; }
    }
    @media (max-width: 900px) { .rp-kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; } }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Raporlar'); ?></h1>
        <div class="summary" id="rpSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="rpExport" data-tip="<?php _e('CSV olarak indir'); ?>" data-tip-pos="left" aria-label="<?php _e('CSV olarak indir'); ?>" aria-haspopup="menu"><?php echo pops_icon('download'); ?></button>
    </div>
</div>

<div class="rp-head">
    <div class="tabs" id="rpTabs" role="tablist" aria-label="<?php _e('Raporlar'); ?>">
        <button type="button" class="tab active" data-tab="summary" role="tab" aria-selected="true"><?php _e('Özet'); ?></button>
        <button type="button" class="tab" data-tab="software" role="tab" aria-selected="false"><?php _e('Yazılım'); ?></button>
        <button type="button" class="tab" data-tab="patches" role="tab" aria-selected="false"><?php _e('Windows güncellemeleri'); ?></button>
        <button type="button" class="tab" data-tab="licenses" role="tab" aria-selected="false"><?php _e('Lisanslar'); ?></button>
    </div>
    <div class="rp-tools" data-for="summary">
        <select id="rpDays" aria-label="<?php _e('Dönem'); ?>">
            <option value="7"><?php _e('Son {n} gün', ['n' => 7]); ?></option>
            <option value="30" selected><?php _e('Son {n} gün', ['n' => 30]); ?></option>
            <option value="90"><?php _e('Son {n} gün', ['n' => 90]); ?></option>
        </select>
    </div>
    <?php if ($rpCanEdit): ?>
    <div class="rp-tools" data-for="licenses" hidden>
        <button type="button" class="btn" id="licNew"><?php echo pops_icon('plus', 'sm'); ?><?php _e('Lisans ekle'); ?></button>
    </div>
    <?php endif; ?>
</div>

<!-- ÖZET -->
<section class="rp-pane" id="pane-summary">
    <div class="kpis rp-kpis" id="kpis"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Rapor hazırlanıyor…'); ?></div></div>
    <div class="card rp-card">
        <div class="card-header"><div><div class="card-title"><?php _e('Günlük yüksek ve kritik olaylar'); ?></div><div class="card-subtitle" id="dayMeta"></div></div></div>
        <div class="card-body" id="dayChart"></div>
    </div>
    <div class="rp-grid">
        <div class="card rp-card"><div class="card-header"><div class="card-title"><?php _e('Ajan sürümleri'); ?></div></div><div class="card-body" id="versions"></div></div>
        <div class="card rp-card"><div class="card-header"><div><div class="card-title"><?php _e('Ajan güncelleme sonuçları'); ?></div><div class="card-subtitle" id="updMeta"></div></div></div><div class="card-body" id="updates"></div></div>
    </div>
    <div class="rp-grid">
        <div class="card rp-card"><div class="card-header"><div><div class="card-title"><?php _e('En çok engellenen alan adları'); ?></div><div class="card-subtitle"><?php _e('Yasaklı siteye erişim denemeleri'); ?></div></div></div><div class="card-body" id="topPolicy"></div></div>
        <div class="card rp-card"><div class="card-header"><div><div class="card-title"><?php _e('En çok uyarı alan bilgisayarlar'); ?></div><div class="card-subtitle"><?php _e('Yüksek ve kritik olaylar'); ?></div></div></div><div class="card-body" id="topDevices"></div></div>
    </div>
</section>

<!-- YAZILIM -->
<section class="rp-pane" id="pane-software" hidden>
    <div class="rp-bar">
        <div class="search-field">
            <?php echo pops_icon('search', 'sm'); ?>
            <input type="search" id="swQ" placeholder="<?php _e('Program ya da yayıncı (ör. chrome, adobe)'); ?>" aria-label="<?php _e('Programlarda ara'); ?>">
        </div>
        <span class="grow"></span>
        <span class="rp-meta" id="swMeta"></span>
    </div>
    <div class="table-wrap">
        <table class="data-table rp-table">
            <thead><tr><th>Program</th><th><?php _e('Sürümler'); ?></th><th class="num"><?php _ex('Bilgisayar', 'count'); ?></th></tr></thead>
            <tbody id="swBody"></tbody>
        </table>
    </div>
    <div class="rp-note"><?php _e('Kurulu program listesi 0.1.5 ve sonraki ajanlardan gelir; programa tıklayınca kurulu olduğu bilgisayarlar görünür.'); ?></div>
</section>

<!-- WINDOWS GÜNCELLEMELERİ -->
<section class="rp-pane" id="pane-patches" hidden>
    <div class="rp-bar">
        <div class="actionbar" id="ptBar" role="toolbar" aria-label="<?php _e('Windows güncelleme işlemleri'); ?>"></div>
        <span class="grow"></span>
        <div class="segmented" id="ptFilter" role="group" aria-label="<?php _e('Duruma göre süz'); ?>">
            <button type="button" data-f="all" class="active" aria-pressed="true"><?php _e('Tümü'); ?></button>
            <button type="button" data-f="need" aria-pressed="false"><?php _e('Eksik'); ?></button>
            <button type="button" data-f="none" aria-pressed="false"><?php _e('Bildirmedi'); ?></button>
        </div>
        <div class="search-field">
            <?php echo pops_icon('search', 'sm'); ?>
            <input type="search" id="ptQ" placeholder="<?php _e('Bilgisayar ya da sınıf'); ?>" aria-label="<?php _e('Bilgisayar ara'); ?>">
        </div>
    </div>
    <div class="table-wrap">
        <table class="data-table rp-table wide">
            <thead id="ptHead"></thead>
            <tbody id="ptBody"></tbody>
        </table>
    </div>
    <div class="rp-note"><?php _e('Durum, Windows güncelleme bildirimini destekleyen ajanlardan (0.1.5 ve sonrası) gelir; eski ajanlar "Bildirmedi" görünür ve komutları yok sayar. Ajan güncellemeleri kurar ama bilgisayarı yeniden başlatmaz; gerekirse Windows kendi ayarına göre, etkin saatler dışında yeniden başlatır.'); ?></div>
</section>

<!-- LİSANSLAR -->
<section class="rp-pane" id="pane-licenses" hidden>
    <div class="rp-bar"><div class="summary" id="licSummary" style="margin-top:0"></div></div>
    <div class="table-wrap">
        <table class="data-table rp-table wide">
            <thead><tr><th><?php _e('Lisans'); ?></th><th><?php _e('Tür'); ?></th><th><?php _e('Kullanım'); ?></th><th><?php _e('Durum'); ?></th><th><?php _e('Bitiş'); ?></th></tr></thead>
            <tbody id="licBody"></tbody>
        </table>
    </div>
    <div class="rp-note"><?php _e('Kullanım, yazılım envanterinde program adında eşleşme ifadesi geçen bilgisayarlar sayılarak bulunur (0.1.5 ve sonraki ajanlar). Koltuk boşsa sınırsız (site ya da kampüs) lisans sayılır. Aşım ve 30 gün içinde bitecek lisanslar için günde bir bildirim gider.'); ?></div>
</section>

<?php if ($rpCanEdit): ?>
<div id="licModal" class="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="licModalTitle">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="licModalTitle"><?php _e('Lisans ekle'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <input type="hidden" id="lfId">
            <div class="field"><label for="lfName"><?php _e('Lisans adı'); ?></label><input id="lfName" maxlength="200" placeholder="<?php _e('Örn. Office LTSC 2021 okul lisansı'); ?>"></div>
            <div class="form-grid">
                <div class="field"><label for="lfPattern"><?php _e('Eşleşme ifadesi'); ?></label><input id="lfPattern" maxlength="200" placeholder="<?php _e('Örn. Office LTSC'); ?>">
                    <div class="field-hint"><?php _e('Program adında geçen düz metin; kurulumlar bununla sayılır.'); ?></div></div>
                <div class="field"><label for="lfPublisher"><?php _e('Yayıncı (isteğe bağlı)'); ?></label><input id="lfPublisher" maxlength="200" placeholder="<?php _e('Örn. Microsoft'); ?>"></div>
            </div>
            <div class="field"><div class="rp-preview" id="lfPreview" aria-live="polite"></div></div>
            <div class="form-grid">
                <div class="field"><label for="lfSeats"><?php _e('Koltuk'); ?></label><input id="lfSeats" type="number" min="0" placeholder="<?php _e('Boş bırakılırsa sınırsız'); ?>"></div>
                <div class="field"><label for="lfType"><?php _e('Tür'); ?></label><select id="lfType"><option value="per_device"><?php _e('Cihaz başına'); ?></option><option value="site"><?php _e('Site ya da kampüs'); ?></option><option value="subscription"><?php _e('Abonelik'); ?></option></select></div>
                <div class="field"><label for="lfExpires"><?php _e('Bitiş tarihi (isteğe bağlı)'); ?></label><input id="lfExpires" type="date"></div>
            </div>
            <div class="field"><label for="lfNotes"><?php _e('Notlar'); ?></label><textarea id="lfNotes" maxlength="2000" rows="2" placeholder="<?php _e('Sözleşme no, tedarikçi…'); ?>"></textarea></div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="lfSave"><?php _e('Kaydet'); ?></button>
        </div>
    </div>
</div>
<?php endif; ?>

<?php /* Sayfa betiği assets/pages/reports.js: sayfada satır içi betik yok; PHP'nin verdiği değerler bu JSON'da */ ?>
<script type="application/json" id="rpData"><?php echo json_encode(['canEditLic' => $rpCanEdit], JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT); ?></script>
<script src="<?php echo htmlspecialchars(pops_asset('assets/pages/reports.js'), ENT_QUOTES, 'UTF-8'); ?>" defer></script>

<?php include 'includes/footer.php'; ?>
