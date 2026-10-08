<?php include 'includes/header.php'; ?>
<?php $sysSuper = ($_SESSION['role'] ?? '') === 'superadmin'; ?>

<style>
    /* Bölümler geniş ekranda iki sütun; sütun sayısı sayfanın gerçek genişliğine göre (ayrıntı paneli açıkken de doğru) */
    .sys-wrap { container-type: inline-size; }
    .sys-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: 32px; align-items: start; }
    @container (min-width: 960px) { .sys-grid { grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr); } .sys-grid.one { grid-template-columns: minmax(0, 1fr); } }
    .sys-col { display: flex; flex-direction: column; gap: 32px; min-width: 0; }
    .sec { container-type: inline-size; min-width: 0; }
    .sec-h { display: flex; align-items: baseline; gap: 12px; padding: 0 4px 10px; }
    .sec-h h2 { font-size: var(--text-lg); font-weight: var(--fw-semibold); letter-spacing: -0.01em; }
    .sec-h .st { margin-left: auto; display: inline-flex; align-items: center; gap: 7px; font-size: var(--text-sm); color: var(--text-tertiary); white-space: nowrap; }
    .set + .set { margin-top: 12px; }
    .srow { flex-wrap: wrap; row-gap: 10px; }
    .srow > .grow { min-width: min(220px, 100%); }
    .srow .v { font-weight: var(--fw-medium); color: var(--text-primary); font-variant-numeric: tabular-nums; text-align: right; overflow-wrap: anywhere; }
    .srow .v.mono { font-family: var(--font-mono); font-size: 12.5px; }
    .srow .acts { display: flex; align-items: center; gap: 6px; margin-left: auto; flex-wrap: wrap; justify-content: flex-end; }
    .srow .d .lnk, .lnk { color: var(--primary-500); font-size: inherit; }
    .srow .d .lnk:hover, .lnk:hover { text-decoration: underline; }
    .srow.click { cursor: pointer; }
    .srow.click:hover { background: var(--bg-surface-2); }
    .srow.click:first-child:hover { border-radius: 14px 14px 0 0; }
    .srow.click:last-child:hover { border-radius: 0 0 14px 14px; }
    .srow.block { display: block; }
    .srow.click:focus-visible { outline: 2px solid var(--primary-500); outline-offset: -2px; }
    .sec-h { flex-wrap: wrap; }
    .srow .t .word { margin-left: 8px; }
    .drawer .srow > .grow { min-width: min(180px, 100%); }
    .srow input[type=number] { width: 88px; text-align: right; }
    .srow input.wide, .srow select.wide { width: min(320px, 100%); }
    .srow .num { display: inline-flex; align-items: center; gap: 8px; margin-left: auto; }
    .srow .unit { color: var(--text-tertiary); font-size: var(--text-sm); }
    .srow .ico-lead { color: var(--text-tertiary); flex: none; }
    .set > .act { padding: 12px 16px; border-bottom: 0; border-top: 1px solid var(--border-subtle); }
    .set > .act:first-child { border-top: 0; }
    .set .why { margin-top: 8px; padding: 9px 12px; border-radius: 10px; background: var(--danger-bg); color: var(--danger-text); font-size: var(--text-xs); line-height: 1.5; overflow-wrap: anywhere; }
    .set .why.warn { background: var(--warning-bg); color: var(--warning-text); }
    .res-spin { width: 28px; height: 28px; display: flex; align-items: center; justify-content: center; flex: none; }

    /* Sürüm dağılımı ve ilerleme */
    .pbar.dist { height: 8px; margin-top: 12px; }
    .lgd { display: flex; flex-wrap: wrap; gap: 6px 18px; margin-top: 10px; font-size: var(--text-sm); color: var(--text-tertiary); }
    .lgd span { display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; }
    .lgd b { color: var(--text-primary); font-weight: var(--fw-semibold); font-variant-numeric: tabular-nums; }
    .pbar.indet > i { width: 32%; animation: sysIndet 1.3s ease-in-out infinite; }
    @keyframes sysIndet { from { transform: translateX(-100%); } to { transform: translateX(320%); } }
    .ro-head { display: flex; align-items: center; gap: 12px; padding: 14px 16px 6px; flex-wrap: wrap; }
    .ro-head .grow { flex: 1; min-width: 200px; }
    .ro-head .cnt { font-size: var(--text-sm); color: var(--text-tertiary); font-variant-numeric: tabular-nums; }
    .ro-head .cnt b { color: var(--text-primary); }
    .ro-bar { padding: 4px 16px 12px; }
    .ro-peer { display: grid; gap: 4px; padding: 0 16px 12px; }
    .ro-peer .d { display: flex; gap: 6px; align-items: flex-start; font-size: var(--text-xs); color: var(--text-secondary); overflow-wrap: anywhere; }
    .ro-peer .d > svg { flex: none; margin-top: 1px; color: var(--text-tertiary); }
    .ro-list { border-top: 1px solid var(--border-subtle); max-height: 360px; overflow-y: auto; padding: 0 12px; }
    .ro-list .act { padding: 10px 4px; }
    .ro-list .note { margin-top: 4px; font-size: var(--text-xs); color: var(--text-secondary); overflow-wrap: anywhere; }
    .ro-more { padding: 10px 16px; border-top: 1px solid var(--border-subtle); font-size: var(--text-sm); }

    /* Sağlık kutucukları: ince çizgili ızgara */
    .hstats { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1px; background: var(--border-subtle); border-radius: 14px; overflow: hidden; box-shadow: 0 0 0 1px var(--border-subtle); }
    @container (min-width: 520px) { .hstats { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
    .hstat { background: var(--bg-surface); padding: 14px 16px; min-width: 0; }
    .hstat .l { font-size: var(--text-sm); color: var(--text-tertiary); display: flex; align-items: center; gap: 6px; }
    .hstat .val { font-size: 18px; font-weight: var(--fw-semibold); letter-spacing: -0.01em; margin-top: 4px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
    .hstat .s { font-size: var(--text-xs); color: var(--text-muted); margin-top: 2px; overflow-wrap: anywhere; }
    .hstats + .set { margin-top: 12px; }

    /* Ayrıntı paneli içerikleri */
    .notes-sec { font-size: var(--text-sm); color: var(--text-secondary); line-height: 1.55; }
    .notes-sec .ver { font-weight: var(--fw-semibold); color: var(--text-primary); margin: 2px 0 4px; }
    .notes-sec .intro { margin-bottom: 6px; }
    .notes-sec .kind { font-size: var(--text-xs); color: var(--text-muted); font-weight: var(--fw-semibold); margin: 10px 0 4px; }
    .notes-sec ul { margin: 0; padding-left: 1.1rem; }
    .notes-sec li { margin-bottom: 4px; overflow-wrap: anywhere; }
    .notes-sec li details summary { list-style: none; cursor: pointer; }
    .notes-sec li details summary::-webkit-details-marker { display: none; }
    .notes-sec li details[open] summary .more { display: none; }
    .notes-sec .more { color: var(--primary-500); white-space: nowrap; }
    .notes-sec code, .dr-code { font-size: 0.85em; }
    .notes-old summary { cursor: pointer; font-weight: var(--fw-medium); color: var(--text-primary); font-size: var(--text-sm); padding: 6px 0; }
    .dr-note { font-size: var(--text-xs); color: var(--text-muted); line-height: 1.5; }
    .dr-list { max-height: 46vh; overflow-y: auto; background: var(--bg-app); border-radius: 12px; padding: 4px 12px; }
    .dr-list label.check { display: flex; align-items: center; gap: 10px; padding: 8px 2px; border-top: 1px solid var(--border-subtle); }
    .dr-list label.check:first-child { border-top: 0; }
    .dr-list label.check.off { color: var(--text-muted); cursor: not-allowed; }
    .dr-list .nm { flex: 1; min-width: 0; overflow-wrap: anywhere; }
    .dr-list .vv { font-size: var(--text-xs); color: var(--text-muted); white-space: nowrap; }
    .dr-actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .faint { color: var(--text-muted); }
    .dr-sum { font-size: var(--text-sm); color: var(--text-secondary); }

    /* Paketi elle yükle */
    .upload-zone { border: 1.5px dashed var(--border-default); border-radius: 12px; padding: 22px 16px; text-align: center; cursor: pointer; position: relative; background: var(--bg-surface-2); color: var(--text-tertiary); font-size: var(--text-sm); transition: border-color 0.15s, background-color 0.15s; }
    .upload-zone:hover, .upload-zone.dragover { border-color: var(--primary-500); background: var(--primary-50); }
    .upload-zone input[type=file] { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .file-list { list-style: none; margin: 10px 0 0; padding: 0; font-size: var(--text-sm); color: var(--text-secondary); }
    .sys-tabs { margin-bottom: 20px; }
    .sys-pane[hidden] { display: none !important; }
    /* Genel bakış: kutucuklar dar ekranda 1–2, genişte 4, çok genişte 8 sütun (satırlar hep dolu); grafikler 1–3 sütun */
    .ov-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: 14px; }
    @container (min-width: 520px) { .ov-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    @container (min-width: 960px) { .ov-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); } }
    @container (min-width: 1760px) { .ov-grid { grid-template-columns: repeat(8, minmax(0, 1fr)); } }
    .ov-trend { margin-top: 32px; }
    .ov-trend-h { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; padding: 0 4px 12px; }
    .ov-trend-h h2 { font-size: var(--text-lg); font-weight: var(--fw-semibold); letter-spacing: -0.01em; }
    .ov-trend-h .segmented { margin-left: auto; }
    .ch-grid { display: grid; grid-template-columns: minmax(0, 1fr); gap: 14px; }
    @container (min-width: 720px) { .ch-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
    @container (min-width: 1200px) { .ch-grid { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
    .ch { display: flex; flex-direction: column; min-width: 0; padding: 16px 18px 12px; background: var(--bg-surface); border-radius: 16px; box-shadow: 0 0 0 1px var(--border-subtle), 0 1px 2px rgba(0, 0, 0, 0.03); }
    .ch-h { display: flex; align-items: baseline; gap: 10px; min-width: 0; }
    .ch-t { font-weight: var(--fw-semibold); font-size: var(--text-md); }
    .ch-n { margin-left: auto; font-size: var(--text-2xl); font-weight: var(--fw-semibold); letter-spacing: -0.02em; font-variant-numeric: tabular-nums; white-space: nowrap; }
    .ch-n small { margin-left: 4px; font-size: var(--text-sm); font-weight: var(--fw-regular); letter-spacing: 0; color: var(--text-muted); }
    .ch-d { display: flex; flex-wrap: wrap; gap: 4px 14px; min-height: 18px; margin-top: 2px; font-size: var(--text-xs); color: var(--text-muted); }
    .ch-key { display: inline-flex; align-items: center; gap: 6px; }
    .ch-key i { width: 8px; height: 8px; border-radius: 2px; background: var(--ch); }
    .ch-key b { font-weight: var(--fw-medium); color: var(--text-secondary); font-variant-numeric: tabular-nums; }
    .ch-b { position: relative; height: 128px; margin-top: 18px; }
    .ch-top { position: absolute; right: 0; top: -15px; font-size: 10.5px; color: var(--text-muted); font-variant-numeric: tabular-nums; pointer-events: none; }
    .ch-empty { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 0 16px; text-align: center; font-size: var(--text-sm); color: var(--text-muted); }
    .ch-b:empty::before { content: attr(data-loading); position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: var(--text-sm); color: var(--text-muted); }
    .ch-x { display: flex; justify-content: space-between; gap: 8px; margin-top: 6px; font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
    .ov { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; text-align: left; padding: 16px 18px; background: var(--bg-surface); border-radius: 16px; box-shadow: 0 0 0 1px var(--border-subtle), 0 1px 2px rgba(0, 0, 0, 0.03); min-height: 112px; transition: box-shadow 0.12s; }
    .ov:hover { box-shadow: 0 0 0 1px var(--border-default), 0 4px 14px rgba(0, 0, 0, 0.06); }
    .ov:focus-visible { outline: none; box-shadow: var(--focus-ring); }
    .ov-t { font-weight: var(--fw-semibold); font-size: var(--text-md); }
    .ov-s { display: inline-flex; align-items: center; gap: 7px; font-size: var(--text-sm); color: var(--text-primary); min-height: 20px; }
    .ov-s:empty::before { content: attr(data-loading); color: var(--text-muted); }
    .ov-d { font-size: var(--text-xs); color: var(--text-muted); margin-top: auto; }
    .file-list li { display: flex; gap: 8px; padding: 3px 0; overflow-wrap: anywhere; }
    .file-list li span { color: var(--text-muted); white-space: nowrap; }

    /* Modüller */
    .md-row .switch { margin-left: auto; }
    .srow .d.md-why { color: var(--warning-text); }
    .md-foot { margin: 10px 4px 0; font-size: var(--text-xs); color: var(--text-muted); line-height: 1.5; }
    .md-labs .srow .segmented { margin-left: auto; }
    .md-labs .srow .t { overflow-wrap: anywhere; }
    .md-find { width: 100%; }
    .gl-checks { display: flex; flex-wrap: wrap; gap: 6px 18px; margin-top: 8px; font-size: var(--text-sm); }
    .gl-loc .srow input { width: 96px; text-align: right; }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Sistem'); ?></h1>
        <div class="summary" id="sysSummary"><span class="sum"><?php _e('Yükleniyor…'); ?></span></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="notesBtn" data-tip="<?php _e('Sürüm notları'); ?>" aria-label="<?php _e('Sürüm notları'); ?>"><?php echo pops_icon('file'); ?></button>
        <button type="button" class="ibtn boxed" id="checkBtn" data-tip="<?php _e('Güncellemeleri denetle'); ?>" data-tip-pos="left" aria-label="<?php _e('Güncellemeleri denetle'); ?>"><?php echo pops_icon('refresh'); ?></button>
    </div>
</div>

<?php if ($sysSuper): ?>
<div class="tabs sys-tabs" id="sysTabs" aria-label="<?php _e('Sistem bölümleri'); ?>">
    <button type="button" class="tab" data-tab="overview"><?php _e('Genel bakış'); ?></button>
    <button type="button" class="tab" data-tab="updates"><?php _e('Güncellemeler'); ?></button>
    <button type="button" class="tab" data-tab="security"><?php _e('Güvenlik'); ?></button>
    <button type="button" class="tab" data-tab="health"><?php _e('Sağlık ve yedek'); ?></button>
    <button type="button" class="tab" data-tab="notify"><?php _e('Bildirimler ve saklama'); ?></button>
    <button type="button" class="tab" data-tab="modules"><?php _e('Modüller'); ?></button>
    <button type="button" class="tab" data-tab="integrations"><?php _e('Entegrasyonlar'); ?></button>
</div>
<?php endif; ?>

<div class="sys-wrap">
    <?php if ($sysSuper): ?>
    <div class="sys-pane" data-pane="overview">
        <div class="ov-grid" id="ovGrid">
            <button type="button" class="ov" data-go="updates" data-src="srvState"><span class="ov-t"><?php _e('Sunucu'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Sürüm ve sunucu güncellemesi'); ?></span></button>
            <button type="button" class="ov" data-go="updates" data-src="agState"><span class="ov-t"><?php _e('Ajanlar'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Sürüm dağılımı, ajan paketi ve gönderim'); ?></span></button>
            <button type="button" class="ov" data-go="health" data-src="hlState"><span class="ov-t"><?php _e('Sağlık'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Bağlı ajanlar, veritabanı, hatalar, disk'); ?></span></button>
            <button type="button" class="ov" data-go="health" data-src="bkState"><span class="ov-t"><?php _e('Yedekler'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Gece alınan veritabanı yedeği'); ?></span></button>
            <button type="button" class="ov" data-go="security" data-src="enState"><span class="ov-t"><?php _e('Ajan kaydı ve kimlik'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Kimlik zorlaması ve kayıt jetonları'); ?></span></button>
            <button type="button" class="ov" data-go="security" data-src="capState"><span class="ov-t"><?php _e('Cihaz yetenekleri'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Uzak komut ve uzak ekranın kapatıldığı bilgisayarlar'); ?></span></button>
            <button type="button" class="ov" data-go="security" data-src="auState"><span class="ov-t"><?php _e('Kayıt bütünlüğü'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('Denetim zincirinin doğrulanması'); ?></span></button>
            <button type="button" class="ov" data-go="notify" data-src="ntState"><span class="ov-t"><?php _e('Bildirimler'); ?></span><span class="ov-s" data-loading="<?php _e('Yükleniyor…'); ?>"></span><span class="ov-d"><?php _e('E-posta ve webhook, saklama süreleri'); ?></span></button>
        </div>
        <div class="ov-trend">
            <div class="ov-trend-h">
                <h2><?php _e('Eğilimler'); ?></h2>
                <div class="segmented" id="ovSpan" role="group" aria-label="<?php _e('Zaman aralığı'); ?>">
                    <button type="button" data-span="24h"><?php _e('24 saat'); ?></button>
                    <button type="button" data-span="7d"><?php _e('7 gün'); ?></button>
                    <button type="button" data-span="30d"><?php _e('30 gün'); ?></button>
                </div>
            </div>
            <div class="ch-grid" id="chGrid">
            <section class="ch" data-ch="agents" aria-label="<?php _e('Bağlı ajanlar'); ?>"><div class="ch-h"><span class="ch-t"><?php _e('Bağlı ajanlar'); ?></span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b" data-loading="<?php _e('Yükleniyor…'); ?>"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="load" aria-label="<?php _e('İşlemci ve bellek'); ?>"><div class="ch-h"><span class="ch-t"><?php _e('İşlemci ve bellek'); ?></span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b" data-loading="<?php _e('Yükleniyor…'); ?>"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="requests" aria-label="<?php _e('API istekleri'); ?>"><div class="ch-h"><span class="ch-t"><?php _e('API istekleri'); ?></span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b" data-loading="<?php _e('Yükleniyor…'); ?>"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="tasks" aria-label="<?php _e('İşlemler'); ?>"><div class="ch-h"><span class="ch-t"><?php _e('İşlemler'); ?></span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b" data-loading="<?php _e('Yükleniyor…'); ?>"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="events" aria-label="<?php _e('Olaylar'); ?>"><div class="ch-h"><span class="ch-t"><?php _e('Olaylar'); ?></span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b" data-loading="<?php _e('Yükleniyor…'); ?>"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="storage" aria-label="<?php _e('Veritabanı ve disk'); ?>"><div class="ch-h"><span class="ch-t"><?php _e('Veritabanı ve disk'); ?></span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b" data-loading="<?php _e('Yükleniyor…'); ?>"></div><div class="ch-x"></div></section>
            </div>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="updates">
        <div class="sys-col">
<section class="sec" id="secServer" aria-labelledby="hServer">
                <div class="sec-h"><h2 id="hServer"><?php _e('Sunucu'); ?></h2><span class="st" id="srvState"></span></div>
                <div class="set" id="srvSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secAgents" aria-labelledby="hAgents">
                <div class="sec-h"><h2 id="hAgents"><?php _e('Ajanlar'); ?></h2><span class="st" id="agState"></span></div>
                <div class="set" id="agSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
                <div class="set" id="rollout" hidden></div>
                <?php if (!$sysSuper): ?><div class="set-note"><?php _e('Güncelleme, sağlık, yedek, kayıt ve güvenlik ayarları yalnızca süper admin içindir.'); ?></div><?php endif; ?>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="security">
        <div class="sys-col">
<section class="sec" id="secEnroll" aria-labelledby="hEnroll">
                <div class="sec-h"><h2 id="hEnroll"><?php _e('Ajan kaydı ve kimlik'); ?></h2><span class="st" id="enState"></span></div>
                <div class="set" id="enSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
                <div class="set" id="tokSet" hidden></div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secCaps" aria-labelledby="hCaps">
                <div class="sec-h"><h2 id="hCaps"><?php _e('Cihaz yetenekleri'); ?></h2><span class="st" id="capState"></span></div>
                <div class="set" id="capSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
            </section>
<section class="sec" id="secAudit" aria-labelledby="hAudit">
                <div class="sec-h"><h2 id="hAudit"><?php _e('Kayıt bütünlüğü'); ?></h2><span class="st" id="auState"></span></div>
                <div class="set" id="auSet"></div>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="health">
        <div class="sys-col">
<section class="sec" id="secHealth" aria-labelledby="hHealth">
                <div class="sec-h"><h2 id="hHealth"><?php _e('Sağlık'); ?></h2><span class="st" id="hlState"></span></div>
                <div id="hlBody"><div class="set"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div></div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secBackup" aria-labelledby="hBackup">
                <div class="sec-h"><h2 id="hBackup"><?php _e('Yedekler'); ?></h2><span class="st" id="bkState"></span></div>
                <div class="set" id="bkSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="notify">
        <div class="sys-col">
<section class="sec" id="secNotify" aria-labelledby="hNotify">
                <div class="sec-h"><h2 id="hNotify"><?php _e('Bildirimler'); ?></h2><span class="st" id="ntState"></span></div>
                <div class="set" id="ntSet">
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Dışarıya gönder'); ?></div><div class="d"><?php _e('Önemli olaylar Bildirimler\'de her zaman görünür; açıkken e-posta ve webhook ile de gönderilir. Aynı olay 10 dakikada bir kez gider.'); ?></div></div>
                        <label class="switch"><input type="checkbox" id="ntEnabled" aria-label="<?php _e('Bildirimleri dışarıya gönder'); ?>"><span></span></label>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('En az önem'); ?></div><div class="d"><?php _e('Bu ve daha önemli olaylar gönderilir.'); ?></div></div>
                        <select id="ntSev" aria-label="<?php _e('En az önem'); ?>" style="width:auto">
                            <option value="critical"><?php _e('Kritik'); ?></option>
                            <option value="high"><?php _e('Yüksek'); ?></option>
                            <option value="medium"><?php _e('Orta'); ?></option>
                            <option value="info"><?php _e('Bilgi (her şey)'); ?></option>
                        </select>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('E-posta alıcıları'); ?></div><div class="d" id="ntSmtp"><?php _e('Virgülle ayırın.'); ?></div></div>
                        <input type="text" id="ntEmail" class="wide" placeholder="<?php _e('ornek@okul.k12.tr'); ?>" aria-label="<?php _e('E-posta alıcıları'); ?>" autocomplete="off">
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t">Webhook</div><div class="d"><?php _e('Slack, Discord, Teams ya da kendi sisteminiz.'); ?></div></div>
                        <input type="url" id="ntWebhook" class="wide" placeholder="https://…" aria-label="<?php _e('Webhook adresi'); ?>" autocomplete="off">
                    </div>
                    <div class="srow">
                        <div class="grow"></div>
                        <div class="acts">
                            <button type="button" class="btn secondary" id="ntTest"><?php echo pops_icon('send', 'sm'); ?><?php _e('Test gönder'); ?></button>
                            <button type="button" class="btn secondary" id="ntSave"><?php _e('Kaydet'); ?></button>
                        </div>
                    </div>
                </div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secRetention" aria-labelledby="hRetention">
                <div class="sec-h"><h2 id="hRetention"><?php _e('Saklama süreleri'); ?></h2></div>
                <div class="set" id="rtSet">
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Ajan olay kayıtları'); ?></div><div class="d"><?php _e('Oturum açma, kural ihlali ve benzeri olaylar.'); ?></div></div>
                        <span class="num"><input type="number" id="rtLogs" min="0" max="3650" step="1" aria-label="<?php _e('Ajan olay kayıtları, gün'); ?>"><span class="unit"><?php _e('gün'); ?></span></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Sonuçlanmış görevler'); ?></div><div class="d"><?php _e('Bitmiş görevler ve çıktıları; sıradakilere dokunulmaz.'); ?></div></div>
                        <span class="num"><input type="number" id="rtTasks" min="0" max="3650" step="1" aria-label="<?php _e('Sonuçlanmış görevler, gün'); ?>"><span class="unit"><?php _e('gün'); ?></span></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Okunmuş bildirimler'); ?></div></div>
                        <span class="num"><input type="number" id="rtNotif" min="0" max="3650" step="1" aria-label="<?php _e('Okunmuş bildirimler, gün'); ?>"><span class="unit"><?php _e('gün'); ?></span></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="d"><?php _e('0 süresiz saklar. Eski kayıtlar her gece silinir. Denetim zinciri ve uzak ekran oturumları silinmez.'); ?></div></div>
                        <button type="button" class="btn secondary" id="rtSave" disabled><?php _e('Kaydet'); ?></button>
                    </div>
                </div>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="modules">
        <div class="sys-col">
<section class="sec" id="secModules" aria-labelledby="hModules">
                <div class="sec-h"><h2 id="hModules"><?php _e('Modüller'); ?></h2><span class="st" id="mdState"></span></div>
                <div class="set" id="mdSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
                <p class="md-foot"><?php _e('Kapalı modülün sayfaları ve uçları kullanılamaz; ajanlar değişikliği en geç bir dakikada alır. Cihazlar, sınıflar, kayıt, ajan güncellemesi, denetim kaydı, kullanıcılar, bildirimler ve sunucu sağlığı her zaman açıktır.'); ?></p>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secProfile" aria-labelledby="hProfile">
                <div class="sec-h"><h2 id="hProfile"><?php _e('Kurulum profili'); ?></h2><span class="st" id="pfState"></span></div>
                <div class="set" id="pfSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="integrations">
        <div class="sys-col">
<section class="sec" id="secGlpi" aria-labelledby="hGlpi">
                <div class="sec-h"><h2 id="hGlpi">GLPI</h2><span class="st" id="glState"></span></div>
                <div class="set" id="glSet">
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Dışa aktarım'); ?></div><div class="d"><?php _e('Açıkken bilgisayarlar, kurulu yazılımlar ve destek talepleri GLPI\'ye gönderilir; kapalıyken hiçbir şey gönderilmez. GLPI\'den POps\'a bir şey alınmaz.'); ?></div></div>
                        <label class="switch"><input type="checkbox" id="glEnabled" aria-label="<?php _e('GLPI\'ye dışa aktarım'); ?>"><span></span></label>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('GLPI adresi'); ?></div><div class="d"><?php _e('GLPI\'nin adresi; apirest.php kendiliğinden eklenir.'); ?></div></div>
                        <input type="url" id="glUrl" class="wide" placeholder="https://glpi.okul.k12.tr" aria-label="<?php _e('GLPI adresi'); ?>" autocomplete="off" spellcheck="false">
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Uygulama jetonu'); ?></div><div class="d"><?php _e('GLPI\'de Kurulum → Genel → API → API istemcisi.'); ?></div></div>
                        <input type="password" id="glApp" class="wide" aria-label="<?php _e('Uygulama jetonu'); ?>" autocomplete="new-password" spellcheck="false">
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Kullanıcı jetonu'); ?></div><div class="d"><?php _e('Yalnızca gereken yetkileri olan bir GLPI kullanıcısının API jetonu.'); ?></div></div>
                        <input type="password" id="glUser" class="wide" aria-label="<?php _e('Kullanıcı jetonu'); ?>" autocomplete="new-password" spellcheck="false">
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Varlık'); ?></div><div class="d"><?php _e('Kayıtların oluşturulacağı GLPI varlığının kimliği (0: kök varlık).'); ?></div></div>
                        <span class="num"><input type="number" id="glEntity" min="0" step="1" aria-label="<?php _e('Varlık kimliği'); ?>"></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Eşitleme aralığı'); ?></div><div class="d"><?php _e('Yalnızca değişen bilgisayarlar ve yeni talepler gönderilir.'); ?></div></div>
                        <select id="glInterval" aria-label="<?php _e('Eşitleme aralığı'); ?>" style="width:auto">
                            <option value="24"><?php _e('Günde bir'); ?></option>
                            <option value="12"><?php _e('12 saatte bir'); ?></option>
                            <option value="6"><?php _e('6 saatte bir'); ?></option>
                            <option value="0"><?php _e('Yalnızca elle'); ?></option>
                        </select>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Gönderilecekler'); ?></div>
                            <div class="gl-checks">
                                <label class="check"><input type="checkbox" id="glComp"> <?php _e('Bilgisayarlar'); ?></label>
                                <label class="check"><input type="checkbox" id="glSoft"> <?php _e('Kurulu yazılımlar'); ?></label>
                                <label class="check"><input type="checkbox" id="glTick"> <?php _e('Destek talepleri'); ?></label>
                                <label class="check"><input type="checkbox" id="glRep"> <?php _e('Talebi bildirenin adı'); ?></label>
                            </div>
                        </div>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t"><?php _e('Talepler'); ?></div><div class="d"><?php _e('Bu tarihten itibaren açılan talepler bir kez gönderilir; sonraki açık yanıtlar takip olarak eklenir.'); ?></div></div>
                        <input type="date" id="glSince" aria-label="<?php _e('Taleplerin başlangıç tarihi'); ?>" style="width:auto">
                    </div>
                    <div class="srow">
                        <div class="grow"></div>
                        <div class="acts">
                            <button type="button" class="btn secondary" id="glTest"><?php echo pops_icon('zap', 'sm'); ?><?php _e('Bağlantıyı sına'); ?></button>
                            <button type="button" class="btn secondary" id="glSave"><?php _e('Kaydet'); ?></button>
                        </div>
                    </div>
                </div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secGlpiRun" aria-labelledby="hGlpiRun">
                <div class="sec-h"><h2 id="hGlpiRun"><?php _e('Eşitleme'); ?></h2></div>
                <div class="set" id="glRun"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
                <p class="md-foot"><?php _e('GLPI\'ye gönderim yeni bir kişisel veri aktarımıdır; okulun KVKK aydınlatma metninde belirtilmelidir. Oturumdaki kullanıcı, ekran görüntüsü ve olay kayıtları gönderilmez. GLPI\'deki bir kayıt POps\'tan silinmez.'); ?></p>
            </section>
        </div>
    </div>
    <?php else: ?>
    <div class="sys-grid one">
        <div class="sys-col">
<section class="sec" id="secServer" aria-labelledby="hServer">
                <div class="sec-h"><h2 id="hServer"><?php _e('Sunucu'); ?></h2><span class="st" id="srvState"></span></div>
                <div class="set" id="srvSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
            </section>
<section class="sec" id="secAgents" aria-labelledby="hAgents">
                <div class="sec-h"><h2 id="hAgents"><?php _e('Ajanlar'); ?></h2><span class="st" id="agState"></span></div>
                <div class="set" id="agSet"><div class="loading-state" role="status"><span class="spinner"></span><?php _e('Yükleniyor…'); ?></div></div>
                <div class="set" id="rollout" hidden></div>
                <?php if (!$sysSuper): ?><div class="set-note"><?php _e('Güncelleme, sağlık, yedek, kayıt ve güvenlik ayarları yalnızca süper admin içindir.'); ?></div><?php endif; ?>
            </section>
        </div>
    </div>
    <?php endif; ?>
</div>

<?php if ($sysSuper): ?>
<div id="uploadModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><?php _e('Ajan paketini elle yükle'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <p class="card-desc"><?php _e('İnternetsiz sunucu için. GitHub sürüm sayfasından şu üç dosyayı indirip birlikte seçin:'); ?> <code>manifest.json</code>, <code>manifest.json.sig</code>, <code>POps-Agent-…-win-x64.msi</code>. <?php _e('İmza GitHub\'dan indirmedeki gibi doğrulanır.'); ?></p>
            <div class="upload-zone" id="uz">
                <?php echo pops_icon('upload'); ?>
                <div style="margin-top:6px"><?php _e('Dosyaları seçin ya da buraya bırakın'); ?></div>
                <input type="file" id="upFiles" multiple aria-label="<?php _e('Paket dosyaları'); ?>">
            </div>
            <ul class="file-list" id="upList"></ul>
            <label class="check" style="margin-top:12px"><input type="checkbox" id="upForce"> <?php _e('Aynı ya da daha eski sürümü de kabul et'); ?></label>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="upBtn" disabled><?php _e('Doğrula ve yükle'); ?></button>
        </div>
    </div>
</div>

<div id="tokenModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title"><?php _e('Kayıt jetonu üret'); ?></div>
            <button type="button" class="modal-close" data-close-modal aria-label="<?php _e('Kapat'); ?>"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <p class="card-desc"><?php _e('Yeni kurulumda MSI\'a {prop} olarak verilir; ajan ilk bağlanışta kendine özel bir anahtar alır. Çok kullanımlık jeton bir sınıfa tek MSI ile toplu kurulum içindir.', ['prop' => 'ENROLL_TOKEN']); ?></p>
            <div class="form-grid">
                <div class="field"><label for="tkLab"><?php _e('Sınıf'); ?></label><input type="text" id="tkLab" list="tkLabs" placeholder="<?php _e('Bütün sınıflar'); ?>" autocomplete="off"><datalist id="tkLabs"></datalist><div class="field-hint"><?php _e('Boş bırakılırsa bilgisayar atanmamış olarak gelir.'); ?></div></div>
                <div class="field"><label for="tkNote"><?php _e('Not'); ?></label><input type="text" id="tkNote" maxlength="200" placeholder="<?php _e('İsteğe bağlı'); ?>" autocomplete="off"></div>
                <div class="field"><label for="tkUses"><?php _e('Kullanım sayısı'); ?></label><input type="number" id="tkUses" min="1" max="10000" value="1"></div>
                <div class="field"><label for="tkTtl"><?php _e('Geçerlilik (saat)'); ?></label><input type="number" id="tkTtl" min="1" max="720" value="72"></div>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal><?php _e('Vazgeç'); ?></button>
            <button type="button" class="btn" id="tkCreate"><?php _e('Jeton üret'); ?></button>
        </div>
    </div>
</div>
<?php endif; ?>

<script>
(function () {

    // ---- Sekmeler ve Genel bakış (yalnızca süper admin): kutucuklar bölüm başlıklarındaki durumu aynen gösterir
    let ovReady = false;   // grafikler sayfa betiği yüklenince çizilir (sekme açılışta seçilirken henüz değil)
    const sysTabs = document.getElementById('sysTabs') ? POps.pageTabs(document.getElementById('sysTabs'), {
        def: 'overview', onChange: (name) => { if (ovReady && name === 'overview') loadOverview(); }
    }) : null;
    if (sysTabs) {
        document.querySelectorAll('#ovGrid .ov').forEach(tile => {
            const src = document.getElementById(tile.dataset.src);
            const out = tile.querySelector('.ov-s');
            if (!src) { tile.hidden = true; return; }
            const copy = () => out.replaceChildren(...[...src.childNodes].map(n => n.cloneNode(true)));
            copy();
            new MutationObserver(copy).observe(src, { childList: true, subtree: true, characterData: true });
            tile.addEventListener('click', () => sysTabs.set(tile.dataset.go, { scroll: true }));
        });
    }
    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const IS_SUPER = window.USER_ROLE === 'superadmin';

    // ---- Genel bakış grafikleri: GET /api/system/overview?span=24h|7d|30d. Ölçümler (ajan, işlemci, bellek, istek,
    // veritabanı) sunucunun dakikalık örneklerinden; işlemler, olaylar ve güncellemeler kayıtlardan. Dakikada bir tazelenir.
    const SPAN_KEY = 'pops_sys_span_v1';
    let ovSpan = '24h', ovData = null, ovErr = null, ovSeq = 0;
    try { const v = localStorage.getItem(SPAN_KEY); if (['24h', '7d', '30d'].includes(v)) ovSpan = v; } catch (e) { /* tarayıcı belleği kapalı */ }
    const nf = (n, d) => Number(n || 0).toLocaleString(POps.locale, { maximumFractionDigits: d || 0 });
    const pct = (v) => (typeof v === 'number' ? POps.pct(nf(v)) : '—');
    const mb = (v) => (typeof v !== 'number' ? '—' : v >= 1024 ? nf(v / 1024, 1) + ' GB' : nf(v, v < 10 ? 1 : 0) + ' MB');
    const gb = (bytes) => nf(bytes / 1073741824, bytes < 10737418240 ? 1 : 0) + ' GB';
    const nums = (pts, k) => pts.map(p => p[k]).filter(v => typeof v === 'number');
    const total = (pts, k) => pts.reduce((a, p) => a + (typeof p[k] === 'number' ? p[k] : 0), 0);
    function when(t, len, kind) {
        // kind: 'axis' (eksen), 'tip' (ipucu: aralığın başı ve sonu)
        const a = new Date(t * 1000), b = new Date((t + len) * 1000);
        const hm = (x) => x.toLocaleTimeString(POps.locale, { hour: '2-digit', minute: '2-digit' });
        const day = (x) => x.toLocaleDateString(POps.locale, { day: 'numeric', month: 'short' });
        if (kind === 'axis') return ovSpan === '24h' ? hm(a) : day(a);
        if (len >= 86400) return a.toLocaleDateString(POps.locale, { day: 'numeric', month: 'long', weekday: 'long' });
        return day(a) + ' ' + hm(a) + '–' + hm(b);
    }
    function keysTo(el, keys) {
        el.replaceChildren(...keys.filter(Boolean).map(k => POps.el('span', { className: 'ch-key' + (k.cls ? ' ' + k.cls : '') }, [
            k.cls ? POps.el('i') : null, document.createTextNode(k.name + (k.value !== undefined ? ' ' : '')),
            k.value !== undefined ? POps.el('b', { text: k.value }) : null
        ])));
    }
    function numTo(el, value, unit) { el.replaceChildren(document.createTextNode(value), unit ? POps.el('small', { text: unit }) : ''); }
    function chartTo(c, kind, points, cfg, topText, emptyText) {
        const has = points.some(p => cfg.series.some(s => typeof p[s.key] === 'number'));
        if (!has && emptyText) { c.b.replaceChildren(POps.el('div', { className: 'ch-empty', text: emptyText })); c.x.replaceChildren(); return; }
        const top = POps.chart[kind](c.b, points, cfg);
        c.b.append(POps.el('span', { className: 'ch-top', text: topText(top) }));
        const n = points.length;
        c.x.replaceChildren(...(n ? [points[0], points[Math.floor((n - 1) / 2)], points[n - 1]] : []).map(p => POps.el('span', { text: when(p.t, 0, 'axis') })));
    }
    function renderOverview() {
        if (!$('chGrid')) return;
        const card = (k) => { const el = document.querySelector(`#chGrid [data-ch="${k}"]`); return { n: el.querySelector('.ch-n'), d: el.querySelector('.ch-d'), b: el.querySelector('.ch-b'), x: el.querySelector('.ch-x') }; };
        document.querySelectorAll('#ovSpan [data-span]').forEach(b => { const on = b.dataset.span === ovSpan; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        const d = ovData;
        if (!d) {
            if (ovErr) document.querySelectorAll('#chGrid .ch-b').forEach(b => b.replaceChildren(POps.el('div', { className: 'ch-empty', text: POps.t('Grafik verisi alınamadı: {error}', { error: POps.errorMessage(ovErr) }) })));
            return;
        }
        const pts = d.series || [], step = d.step, bar = d.bar;
        const WAIT = POps.t('Ölçümler toplanıyor. Sunucu dakikada bir ölçüm alır; grafik birkaç dakika içinde dolmaya başlar.');
        const NONE = POps.t('Ölçüm yok');
        const tipP = (p, text) => when(p.t, step, 'tip') + '\n' + text;
        const late = d.latest || {};

        // Bağlı ajanlar
        let c = card('agents');
        const ag = nums(pts, 'agents');
        numTo(c.n, nf(d.agents_connected), '/ ' + nf((d.devices || {}).total));
        const up = d.updates || {}, upN = (up.success || 0) + (up.rolled_back || 0) + (up.failed || 0);
        keysTo(c.d, [
            ag.length ? { name: POps.t('en çok'), value: nf(Math.max(...ag)) } : null,
            ag.length ? { name: POps.t('en az'), value: nf(Math.min(...ag)) } : null,
            upN ? { name: POps.t('ajan güncellemesi'), value: POps.t('{n} başarılı', { n: nf(up.success) }) + (upN - up.success ? ' · ' + POps.t('{n} sorunlu', { n: nf(upN - up.success) }) : '') } : null
        ]);
        chartTo(c, 'line', pts, { series: [{ key: 'agents', cls: 'ch-c1' }], area: true,
            tip: p => tipP(p, typeof p.agents === 'number' ? POps.t('{n} ajan bağlı', { n: nf(p.agents) }) : NONE) }, nf, WAIT);

        // İşlemci ve bellek
        c = card('load');
        numTo(c.n, pct(late.cpu_pct), POps.t('işlemci'));
        keysTo(c.d, [{ name: POps.t('İşlemci'), value: pct(late.cpu_pct), cls: 'ch-c1' }, { name: POps.t('Bellek'), value: pct(late.mem_pct), cls: 'ch-c2' },
            nums(pts, 'cpu').length ? { name: POps.t('en yüksek işlemci'), value: pct(Math.max(...nums(pts, 'cpu'))) } : null]);
        chartTo(c, 'line', pts, { series: [{ key: 'cpu', cls: 'ch-c1' }, { key: 'mem', cls: 'ch-c2' }], max: 100,
            tip: p => tipP(p, typeof p.cpu === 'number' || typeof p.mem === 'number' ? POps.t('İşlemci {cpu} · Bellek {mem}', { cpu: pct(p.cpu), mem: pct(p.mem) }) : NONE) }, pct, WAIT);

        // API istekleri (5xx kırmızı)
        c = card('requests');
        const rq = pts.map(p => ({ t: p.t, ok: typeof p.requests === 'number' ? Math.max(0, p.requests - (p.errors || 0)) : null, errors: p.errors, requests: p.requests }));
        const rqN = total(pts, 'requests'), erN = total(pts, 'errors');
        numTo(c.n, nf(rqN), POps.t('istek'));
        keysTo(c.d, [{ name: POps.t('Başarılı'), value: nf(rqN - erN), cls: 'ch-c1' }, { name: POps.t('Sunucu hatası (5xx)'), value: nf(erN), cls: 'ch-c4' }]);
        chartTo(c, 'bars', rq, { series: [{ key: 'ok', cls: 'ch-c1' }, { key: 'errors', cls: 'ch-c4' }],
            tip: p => tipP(p, typeof p.requests === 'number' ? POps.t('{n} istek', { n: nf(p.requests) }) + (p.errors ? ' · ' + POps.t('{n} hata', { n: nf(p.errors) }) : '') : NONE) }, nf, WAIT);

        // İşlemler (görev sonuçları)
        c = card('tasks');
        const tk = d.tasks || [];
        const TK = [['ok', 'Başarılı', 'ch-c3'], ['failed', 'Başarısız', 'ch-c4'], ['denied', 'Reddedildi', 'ch-c5'], ['other', 'Diğer', 'ch-c6']];
        const tkN = TK.reduce((a, [k]) => a + total(tk, k), 0);
        numTo(c.n, nf(tkN), POps.t('işlem'));
        keysTo(c.d, tkN ? TK.filter(([k]) => total(tk, k)).map(([k, name, cls]) => ({ name: POps.t(name), value: nf(total(tk, k)), cls })) : [{ name: POps.t('Bu aralıkta işlem yok') }]);
        chartTo(c, 'bars', tk, { series: TK.map(([key, , cls]) => ({ key, cls })),
            tip: p => when(p.t, bar, 'tip') + '\n' + (TK.filter(([k]) => p[k]).map(([k, name]) => POps.t(name) + ' ' + nf(p[k])).join(' · ') || POps.t('İşlem yok')) }, nf);

        // Olaylar (risk düzeyine göre)
        c = card('events');
        const ev = d.events || [];
        const EV = [['high', 'Yüksek ve kritik', 'ch-c4'], ['medium', 'Orta', 'ch-c5'], ['info', 'Bilgi', 'ch-c6']];
        const evN = EV.reduce((a, [k]) => a + total(ev, k), 0);
        numTo(c.n, nf(evN), POps.t('olay'));
        keysTo(c.d, evN ? EV.map(([k, name, cls]) => ({ name: POps.t(name), value: nf(total(ev, k)), cls })) : [{ name: POps.t('Bu aralıkta olay yok') }]);
        chartTo(c, 'bars', ev, { series: EV.map(([key, , cls]) => ({ key, cls })),
            tip: p => when(p.t, bar, 'tip') + '\n' + (EV.filter(([k]) => p[k]).map(([k, name]) => POps.t(name) + ' ' + nf(p[k])).join(' · ') || POps.t('Olay yok')) }, nf);

        // Veritabanı ve disk
        c = card('storage');
        numTo(c.n, mb(late.db_mb), POps.t('veritabanı'));
        const disks = (d.disk || []).filter(x => x && x.total_bytes);
        const fullest = disks.sort((a, b) => a.free_percent - b.free_percent)[0];
        keysTo(c.d, [{ name: POps.t('Veritabanı'), value: mb(late.db_mb), cls: 'ch-c1' },
            fullest ? { name: POps.t('Disk'), value: POps.t('{used} dolu · {free} boş', { used: pct(100 - fullest.free_percent), free: gb(fullest.free_bytes) }) } : null]);
        chartTo(c, 'line', pts, { series: [{ key: 'db_mb', cls: 'ch-c1' }], area: true,
            tip: p => tipP(p, typeof p.db_mb === 'number' ? POps.t('Veritabanı {size}', { size: mb(p.db_mb) }) + (typeof p.disk === 'number' ? ' · ' + POps.t('disk {pct} dolu', { pct: pct(p.disk) }) : '') : NONE) }, mb, WAIT);
    }
    async function loadOverview() {
        if (!$('chGrid')) return;
        const seq = ++ovSeq;
        try {
            const data = await POps.get('/api/system/overview?span=' + encodeURIComponent(ovSpan));
            if (seq !== ovSeq) return;
            ovData = data; ovErr = null;
        } catch (e) {
            if (seq !== ovSeq) return;
            ovErr = e;
            if (ovData && ovData.span !== ovSpan) ovData = null;
        }
        renderOverview();
    }
    if ($('ovSpan')) $('ovSpan').addEventListener('click', (e) => {
        const b = e.target.closest('[data-span]');
        if (!b || b.dataset.span === ovSpan) return;
        ovSpan = b.dataset.span;
        try { localStorage.setItem(SPAN_KEY, ovSpan); } catch (err) { /* tarayıcı belleği kapalı */ }
        ovData = null;
        document.querySelectorAll('#chGrid .ch-b').forEach(x => x.replaceChildren());
        renderOverview();
        loadOverview();
    });
    const ME = <?php echo json_encode((string) ($_SESSION['username'] ?? ''), JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_INVALID_UTF8_SUBSTITUTE); ?>;
    const S = { ver: null, su: null, diag: null, diagErr: null, notes: null, checkedAt: null, fetching: false, suBusy: false, suPoll: null, suBefore: '', tokens: null, notify: null, retention: null };
    const RKEY = 'pops_agent_rollout_v1', AKEY = 'pops_audit_verify_v1', SKEY = 'pops_selfupdate_req_v1';

    const fmtV = (v) => !v ? '—' : (/^v/i.test(v) ? v : 'v' + v);
    const normV = (v) => String(v || '').trim().replace(/^v/i, '');
    const isOn = (d) => !POps.isOffline(d);
    const devs = () => state.devices || [];
    const store = {
        get(k, ss) { try { return JSON.parse((ss ? sessionStorage : localStorage).getItem(k) || 'null'); } catch (e) { return null; } },
        set(k, v, ss) { try { const s = ss ? sessionStorage : localStorage; if (v == null) s.removeItem(k); else s.setItem(k, JSON.stringify(v)); } catch (e) { /* özel pencere */ } }
    };
    const wordHtml = (k, t) => `<span class="word ${escapeHtml(k)}">${escapeHtml(t)}</span>`;
    const stateHtml = (k, t) => `<span class="dot ${escapeHtml(k)}"></span>${escapeHtml(t)}`;
    const fmtBytes = (n) => { n = Number(n) || 0; return n >= 1073741824 ? (n / 1073741824).toFixed(1) + ' GB' : n >= 1048576 ? (n / 1048576).toFixed(1) + ' MB' : Math.round(n / 1024) + ' KB'; };
    const fmtNum = (n) => Number(n || 0).toLocaleString(POps.locale);
    const ago = (sec) => POps.t('{time} önce', { time: POps.duration(sec) });
    function setState(id, k, t) { const el = $(id); if (el) el.innerHTML = t ? stateHtml(k, t) : ''; }
    function sectionError(el, e) { if (el) POps.setError(el, e, { compact: true }); }

    // ---- Ortak ayrıntı paneli üst kısmı
    function drawerHeadHtml(title, subHtml, icon, cls) {
        return `<div class="drawer-head"><div class="drawer-title"><span class="drawer-ico ${escapeHtml(cls || '')}">${POps.iconHtml(icon, 'lg')}</span>`
            + `<div style="min-width:0"><h2>${escapeHtml(title)}</h2><div class="sub">${subHtml}</div></div></div>`
            + `<button type="button" class="ibtn sm" data-act="close" data-tip="${escapeHtml(POps.t('Kapat (Esc)'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paneli kapat'))}">${POps.iconHtml('x', 'sm')}</button></div>`;
    }
    const DR = {};   // drawer anahtarı önekine göre tıklama işleyicisi
    let drawerWired = false;
    function openDrawer(key, handler, onClose) {
        const body = POps.drawer.open(key, { onClose });
        DR[key.split(':')[0]] = handler;
        if (!drawerWired) {
            drawerWired = true;
            body.addEventListener('click', (e) => {
                const k = POps.drawer.key();
                if (!k || !DR[k.split(':')[0]]) return;
                const b = e.target.closest('[data-act]');
                if (!b || b.disabled) return;
                if (b.dataset.act === 'close') { POps.drawer.close(); return; }
                DR[k.split(':')[0]](b, e);
            });
        }
        return body;
    }

    // =================================================================
    // SUNUCU
    // =================================================================
    function serverInfo() {
        const v = S.ver, su = S.su;
        const srv = (v && v.server) || null;
        const rel = !srv || (srv.channel || 'release') === 'release';
        const st = su && su.status;
        const busy = !!(S.suBusy || (su && (su.pending || (st && st.state === 'running'))));
        let latest = '—', latestSub = '', avail = false;
        if (v && !srv) { latest = '?'; latestSub = POps.t('Güncelleme sorgusu için sunucuyu bir kez güncelleyin.'); avail = true; }
        else if (srv && rel) {
            if (!srv.checked) { latest = '?'; latestSub = POps.t("GitHub'a ulaşılamadı"); }
            else {
                latest = fmtV(srv.latest_release); avail = !!srv.update_available;
                const same = String(srv.latest_release || '').replace(/^v/, '') === String((v && v.running) || '').replace(/^v/, '');
                latestSub = POps.t(avail ? 'Kurulabilir' : same ? 'Çalışan sürümle aynı' : 'Çalışan sürüm daha yeni');
            }
        } else if (srv) {
            if (!srv.rev) { latest = '?'; latestSub = POps.t('Bir kez panelden güncellenince izlenir.'); }
            else if (!srv.checked) { latest = '?'; latestSub = POps.t("GitHub'a ulaşılamadı"); }
            else if (srv.update_available) { latest = POps.tn('+{n} değişiklik', srv.ahead_by); avail = true; latestSub = srv.version_changed ? POps.t('Yeni sürüm numarası içeriyor') : ''; }
            else { latest = POps.t('Güncel'); latestSub = srv.ahead_by > 0 ? POps.tn("main'de {n} değişiklik var, sunucuyu etkilemiyor", srv.ahead_by) : ''; }
        }
        let k, word;
        if (!v) { k = 'bad'; word = POps.t('Sürüm bilgisi alınamadı'); }
        else if (busy) { k = 'run'; word = POps.t('Güncelleniyor'); }
        else if (srv && srv.last_state === 'failed') { k = 'bad'; word = POps.t('Son güncelleme başarısız'); }
        else if (avail) { k = 'run'; word = POps.t('Yeni sürüm var'); }
        else if (latest === '?') { k = 'off'; word = srv && !srv.checked ? POps.t("GitHub'a ulaşılamadı") : POps.t('Durum bilinmiyor'); }
        else { k = 'ok'; word = POps.t('Güncel'); }
        return { v, su, srv, rel, st, busy, latest, latestSub, avail, k, word, configured: !!(su && su.configured) };
    }

    function lastAttemptHtml(st) {
        if (!st || !st.state || st.state === 'running') return '';
        const ok = st.state === 'ok';
        const metaHtml = POps.timeHtml(st.at) + (st.rev && st.rev !== 'unknown' ? ' · commit ' + escapeHtml(st.rev) : '') + (st.target ? ' · ' + escapeHtml(st.target === 'origin/main' ? 'main' : st.target) : '');
        return `<div class="act"><div class="res ${ok ? 'ok' : 'bad'}">${POps.iconHtml(ok ? 'check' : 'x')}</div>
            <div style="min-width:0"><div class="what">${POps.tHtml('Son sunucu güncellemesi')}</div><div class="meta">${metaHtml}</div>
            ${ok ? '' : `<div class="why">${POps.tHtml('Güncelleme tamamlanamadı; önceki kod çalışıyor (sağlık kontrolü geri döndü).')}${st.message ? ' ' + POps.tHtml('Ayrıntı: {detail}', { detail: st.message }) : ''}</div>`}</div>
            <div class="side">${wordHtml(ok ? 'ok' : 'bad', POps.t(ok ? 'Tamamlandı' : 'Başarısız'))}</div></div>`;
    }

    function renderServer() {
        const I = serverInfo();
        setState('srvState', I.k, I.word);
        const box = $('srvSet');
        if (!I.v) { POps.setError(box, new Error(POps.t('Sürüm bilgisi alınamadı. Sayfayı yenileyin.')), { compact: true }); return; }
        const srv = I.srv || {};
        const deployedHtml = srv.deployed_at ? POps.tHtml('Panelden güncellendi {time}', null, { time: POps.timeHtml(srv.deployed_at) }) + (srv.rev ? ' · commit ' + escapeHtml(srv.rev) : '') : POps.tHtml('Henüz panelden güncellenmedi');
        const commitsHtml = !I.rel && (srv.commits || []).length ? ` <button type="button" class="lnk" data-act="commits">${POps.tHtml('Değişiklikleri göster')}</button>` : '';
        let actHtml;
        if (I.busy) {
            const req = store.get(SKEY, true);
            const whoHtml = req && req.at && Date.now() - req.at < 30 * 60 * 1000 ? escapeHtml(req.by || '?') + ' · ' + POps.timeHtml(req.at) + ' · ' : '';
            actHtml = `<div class="srow"><span class="res-spin"><span class="spinner"></span></span>
                <div class="grow"><div class="t">${POps.tHtml('Sunucu güncelleniyor')}</div><div class="d">${whoHtml}${POps.tHtml('Birkaç saniye bağlantı kopabilir; sağlık kontrolü geçmezse önceki koda döner.')}</div>
                <div class="pbar indet" style="margin-top:10px"><i class="run"></i></div></div></div>`;
        } else {
            const what = I.rel ? POps.t('Yayımlanmış son sürümü kurar, geri gitmez.') : POps.t("GitHub main'deki kodu kurar.");
            const title = !I.v.server ? POps.t('Sunucu güncellemesi gerekli') : I.avail ? POps.t('{version} kurulabilir', { version: I.latest }) : POps.t('Sunucu güncel');
            const desc = !I.configured ? POps.t('Panelden güncelleme bu sunucuda kurulu değil (docs/self-update.md).') : what + ' ' + POps.t('Sağlık kontrolü geçmezse önceki koda kendiliğinden döner.');
            actHtml = `<div class="srow"><div class="grow"><div class="t">${escapeHtml(title)}</div><div class="d">${escapeHtml(desc)}</div></div>
                ${IS_SUPER ? `<button type="button" class="btn${I.avail ? '' : ' secondary'}" data-act="selfupdate" ${I.configured ? '' : 'disabled'}>${POps.iconHtml('download', 'sm')}${POps.tHtml('Sunucuyu güncelle')}</button>` : ''}</div>`;
        }
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Çalışan sürüm')}</div><div class="d">${deployedHtml}</div></div><div class="v">${escapeHtml(fmtV(I.v.running))}</div></div>
            <div class="srow"><div class="grow"><div class="t">${POps.tHtml('Güncelleme kanalı')}</div><div class="d">${I.rel ? POps.tHtml('Yalnızca yayımlanmış sürümler kurulur.') : POps.tHtml('GitHub main dalı; geliştirme sunucusu içindir.')}</div></div><div class="v">${I.rel ? POps.tHtml('Sürüm') : POps.tHtml('Geliştirme (main)')}</div></div>
            <div class="srow"><div class="grow"><div class="t">${I.rel ? POps.tHtml("GitHub'daki son sürüm") : 'GitHub main'}</div><div class="d">${escapeHtml(I.latestSub)}${commitsHtml}</div></div><div class="v">${escapeHtml(I.latest)}</div></div>
            ${actHtml}${lastAttemptHtml(I.st)}`;
    }

    $('srvSet').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b || b.disabled) return;
        if (b.dataset.act === 'selfupdate') selfUpdate(b);
        else if (b.dataset.act === 'commits') openCommits();
    });

    function openCommits() {
        const srv = (S.ver && S.ver.server) || {};
        const body = openDrawer('sysc:commits', () => {});
        const listHtml = (srv.commits || []).map(c => `<li>${escapeHtml(c)}</li>`).join('');
        body.innerHTML = drawerHeadHtml(POps.t('Gelecek değişiklikler'), POps.tnHtml('main dalında {n} değişiklik', Number(srv.ahead_by || 0)), 'list', '')
            + `<div class="notes-sec"><ul>${listHtml}</ul></div><div class="dr-note">${POps.tHtml("Commit başlıkları GitHub'dan alınır (son 15).")}</div>`;
    }

    async function loadSelfUpdate() {
        try { S.su = await POps.get('/api/system/self-update/status'); } catch (e) { S.su = S.su || null; }
    }

    async function selfUpdate(btn) {
        const I = serverInfo();
        const title = I.rel ? (I.latest && I.latest !== '?' && I.latest !== '—' ? POps.t('Sunucu {version} sürümüne güncellensin mi?', { version: I.latest }) : POps.t('Sunucu yayımlanmış son sürüme güncellensin mi?'))
            : POps.t("Sunucu GitHub main'deki koda güncellensin mi?");
        const ok = await POps.confirm({
            title,
            message: POps.t('Birkaç saniye bağlantı kopabilir. Sağlık kontrolü geçmezse sunucu önceki koda kendiliğinden döner.'),
            confirmText: POps.t('Sunucuyu güncelle'), icon: 'download'
        });
        if (!ok) return;
        S.suBefore = (S.su && S.su.status && S.su.status.at) || '';
        try {
            await POps.busy(btn, () => POps.post('/api/system/self-update'));
        } catch (e) { POps.toast('error', POps.t('Güncelleme başlatılamadı: {error}', { error: POps.errorMessage(e) })); return; }
        store.set(SKEY, { at: Date.now(), by: ME }, true);
        S.suBusy = true;
        renderServer(); renderSummary();
        watchSelfUpdate();
    }
    // Güncelleme sürerken durum 3 sn'de bir okunur (servis yeniden başlarken birkaç istek düşebilir; sorun değil)
    function watchSelfUpdate() {
        if (S.suPoll) return;
        const started = Date.now();
        S.suPoll = setInterval(async () => {
            await loadSelfUpdate();
            const st = S.su && S.su.status;
            const done = st && st.at !== S.suBefore && (st.state === 'ok' || st.state === 'failed') && !(S.su && S.su.pending);
            if (done || Date.now() - started > 6 * 60 * 1000) {
                clearInterval(S.suPoll); S.suPoll = null; S.suBusy = false;
                store.set(SKEY, null, true);
                if (st && st.state === 'ok' && done) POps.toast('success', POps.t('Sunucu güncellendi.'));
                else if (st && st.state === 'failed' && done) POps.toast('error', POps.t('Sunucu güncellemesi başarısız; önceki kod çalışıyor.'));
                else POps.toast('warning', POps.t('Güncellemenin sonucu alınamadı; sayfayı birazdan yenileyin.'));
                await loadAll(true);
            } else {
                renderServer(); renderSummary();
            }
        }, 3000);
    }

    // =================================================================
    // SÜRÜM NOTLARI (GitHub'daki CHANGELOG)
    // =================================================================
    // CHANGELOG maddeleri: **kalın** ve `kod` dışında biçim yok; önce kaçırılır.
    const KIND = { Added: 'Eklenenler', Changed: 'Değişenler', Fixed: 'Düzeltmeler', Security: 'Güvenlik', Removed: 'Kaldırılanlar', Deprecated: 'Kullanımdan kalkacaklar' };
    const md = (t) => escapeHtml(t).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/`([^`]+)`/g, '<code>$1</code>');
    function noteItemHtml(t) {
        const m = String(t).match(/^\*\*(.+?)\*\*\s*(.*)$/);
        const headHtml = m ? `<strong>${escapeHtml(m[1])}</strong> ` : '';
        const rest = m ? m[2] : String(t);
        const first = rest.split(/(?<=\.)\s/)[0];
        if (first.length >= rest.length - 1 || rest.length < 180) return `<li>${headHtml}${md(rest)}</li>`;
        return `<li><details><summary>${headHtml}${md(first)} <span class="more">${POps.tHtml('devamı')}</span></summary>${md(rest.slice(first.length))}</details></li>`;
    }
    function noteSecHtml(sec, title) {
        const label = title || (sec.version === 'Unreleased' ? POps.t('Henüz sürüm numarası almamış yenilikler') : fmtV(sec.version) + (sec.date ? ' · ' + sec.date : ''));
        return `<div class="notes-sec"><div class="ver">${escapeHtml(label)}</div>${sec.intro ? `<div class="intro">${md(sec.intro)}</div>` : ''}`
            + sec.groups.map(g => `<div class="kind">${escapeHtml(KIND[g.kind] ? POps.t(KIND[g.kind]) : g.kind)}</div><ul>${g.items.map(noteItemHtml).join('')}</ul>`).join('') + '</div>';
    }
    async function openNotes() {
        const body = openDrawer('sysn:notes', () => {});
        body.innerHTML = drawerHeadHtml(POps.t('Sürüm notları'), POps.tHtml('GitHub’daki değişiklik günlüğü'), 'file', '') + '<div class="loading-state" role="status"><span class="spinner"></span>' + POps.tHtml('Yükleniyor…') + '</div>';
        let d = S.notes;
        if (!d) {
            try { d = S.notes = await POps.get('/api/system/release-notes'); }
            catch (e) { if (POps.drawer.isOpen('sysn:notes')) POps.setError(body.lastElementChild, e, { compact: true }); return; }
        }
        if (!POps.drawer.isOpen('sysn:notes')) return;
        const headHtml = drawerHeadHtml(POps.t('Sürüm notları'), POps.tHtml('GitHub’daki değişiklik günlüğü'), 'file', '');
        if (!d.available) { body.innerHTML = headHtml + '<div class="empty-state compact">' + POps.iconHtml('wifi') + '<p>' + POps.tHtml('GitHub’a ulaşılamadığı için notlar gösterilemiyor.') + '</p></div>'; return; }
        const srv = (S.ver && S.ver.server) || {};
        let html = '';
        if (srv.update_available && (d.incoming || []).length) html += '<div><h3>' + POps.tHtml('Güncellemeyle gelecekler') + '</h3>' + d.incoming.map(sec => noteSecHtml(sec)).join('') + '</div>';
        if ((d.installed || []).length) {
            const [cur, ...older] = d.installed;
            html += `<div><h3>${POps.tHtml('Bu sunucuda ({version})', { version: fmtV(d.running) + (d.rev ? ' · ' + d.rev : '') })}</h3>${noteSecHtml(cur)}`
                + (older.length ? `<details class="notes-old"><summary>${POps.tnHtml('Önceki {n} sürüm', older.length)}</summary>${older.map(sec => noteSecHtml(sec)).join('')}</details>` : '') + '</div>';
        }
        if (d.agent && (d.agent.groups || []).length) html += `<div><h3>${POps.tHtml('Ajan paketi {version}', { version: fmtV(d.agent.version) })}</h3>${noteSecHtml(d.agent, ' ')}</div>`;
        body.innerHTML = headHtml + (html || '<div class="empty-state compact">' + POps.iconHtml('file') + '<p>' + POps.tHtml('Gösterilecek not yok.') + '</p></div>')
            + '<div class="dr-note">' + POps.tHtml('Notlar GitHub’daki CHANGELOG’dan alınır (İngilizce).') + '</div>';
    }
    $('notesBtn').addEventListener('click', openNotes);

    // =================================================================
    // AJANLAR: sürüm dağılımı, paket, güncelleme ve ilerleme
    // =================================================================
    // İzlenen gönderimde güncellemesi süren bilgisayarlar (sonuç bekleniyor; servisi kurulumda kapalı olabilir)
    function updatingPcs() {
        const out = new Set();
        (rItems || []).forEach(it => { if (it.pending && !it.on_target && itemState(it).k === 'run') out.add(it.pc); });
        return out;
    }
    function agentInfo() {
        const v = S.ver || {};
        const staged = v.staged_version || '';
        const all = devs();
        const target = staged || v.latest || dev.newestVersion() || '';
        const groups = new Map();
        all.forEach(d => { const ver = normV(dev.version(d)); groups.set(ver, (groups.get(ver) || 0) + 1); });
        const old = staged ? all.filter(d => normV(dev.version(d)) !== normV(staged)) : [];
        const oldOn = old.filter(isOn);
        return { v, staged, target, all, groups, old, oldOn };
    }
    function distHtml(A) {
        const total = A.all.length;
        if (!total) return '<div class="d" style="margin-top:6px">' + POps.tHtml('Henüz kayıtlı ajan yok.') + '</div>';
        const vers = [...A.groups.keys()].sort((a, b) => (!a) - (!b) || dev.cmpVersion(b, a));
        let oldIdx = 0;
        const parts = vers.map(ver => {
            const n = A.groups.get(ver);
            let cls = 'off', op = 1;
            if (!ver) cls = 'off';
            else if (!A.target || dev.cmpVersion(ver, A.target) >= 0) cls = 'ok';
            else { cls = 'run'; op = Math.max(0.35, 1 - 0.22 * oldIdx++); }
            return { ver, n, cls, op };
        });
        const segHtml = parts.filter(p => p.cls !== 'off').map(p => `<i class="${escapeHtml(p.cls)}" style="width:${(p.n / total * 100).toFixed(2)}%;opacity:${Number(p.op)}"></i>`).join('');
        const lgdHtml = parts.map(p => `<span><span class="dot ${escapeHtml(p.cls)}" style="opacity:${Number(p.op)}"></span>${escapeHtml(p.ver ? fmtV(p.ver) : POps.t('Bilinmiyor'))} <b>${Number(p.n)}</b></span>`).join('');
        return `<div class="pbar dist" role="img" aria-label="${escapeHtml(POps.t('Ajan sürüm dağılımı'))}">${segHtml}</div><div class="lgd">${lgdHtml}</div>`;
    }
    let agHash = '';
    function renderAgents(force) {
        if (!S.ver) return;
        const A = agentInfo();
        const v = A.v;
        // Güncellemesi süren eski ajan (gönderildi, sonucu gelmedi): kurulumda servisi durur; "kapalı" değil "güncelleniyor"
        const upd = updatingPcs();
        const oldUpd = A.old.filter(d => upd.has(d.hostname)).length;
        const onIdle = A.oldOn.filter(d => !upd.has(d.hostname)).length;
        const sig = JSON.stringify([v.staged_version, v.latest, v.release_available, v.checked_github, v.update_peer_cache, S.fetching, A.all.map(d => [d.hostname, dev.version(d), d.status]), [...upd]]);
        if (!force && sig === agHash) return;
        agHash = sig;
        // Durum
        let k, word;
        if (v.release_available) { k = 'run'; word = POps.t('Yeni paket: {version}', { version: fmtV(v.latest) }); }
        else if (!A.staged) { k = 'off'; word = POps.t('Paket yok'); }
        else if (A.old.length) { k = 'run'; word = POps.tn('{n} eski ajan', A.old.length); }
        else { k = 'ok'; word = POps.t('Hepsi güncel'); }
        setState('agState', k, word);
        // Paket satırı
        let pkgD;
        if (S.fetching) pkgD = POps.t("GitHub'dan indiriliyor ve imzası doğrulanıyor…");
        else if (!A.staged) pkgD = v.latest ? POps.t("Henüz paket yok. GitHub'da {version} var.", { version: fmtV(v.latest) }) : (v.checked_github ? POps.t('Henüz paket yok.') : POps.t("Henüz paket yok; GitHub'a ulaşılamadı."));
        else pkgD = POps.t('İmzası doğrulandı.') + (v.release_available ? ' ' + POps.t("GitHub'da daha yeni {version} var.", { version: fmtV(v.latest) }) : v.latest ? ' ' + POps.t("GitHub'daki son sürümle aynı.") : '');
        const fetchHtml = IS_SUPER && v.release_available ? `<button type="button" class="btn secondary" data-act="fetch">${POps.iconHtml('download', 'sm')}${POps.tHtml('{version} paketini indir', { version: fmtV(v.latest) })}</button>` : '';
        const pkgMoreHtml = `<button type="button" class="ibtn sm" data-act="pkgmore" data-tip="${escapeHtml(POps.t('Paket işlemleri'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Paket işlemleri'))}" aria-haspopup="menu">${POps.iconHtml('more')}</button>`;
        // Güncelleme satırı
        let upT, upD, upBtn = '';
        const offOld = A.old.length - onIdle - oldUpd;
        if (!A.staged) { upT = POps.t('Ajan güncellemesi'); upD = POps.t('Önce ajan paketini indirin.'); }
        else if (v.release_available) { upT = POps.tn('{n} ajan {version} sürümünde değil', A.old.length, { version: fmtV(A.staged) }); upD = POps.t('Gönderilecek paket {staged}; önce {latest} paketini indirin.', { staged: fmtV(A.staged), latest: fmtV(v.latest) }); }
        else if (!A.old.length) { upT = POps.t('Bütün ajanlar {version} sürümünde', { version: fmtV(A.staged) }); upD = POps.t('Gönderilecek güncelleme yok.'); }
        else {
            upT = POps.tn('{n} eski ajan', A.old.length);
            const parts = [];
            if (oldUpd) parts.push(POps.tn('{n} güncelleniyor.', oldUpd));
            if (offOld && onIdle) parts.push(POps.t('{on} açık, {off} kapalı (kapalılar açılınca gönderilebilir).', { on: onIdle, off: offOld }));
            else if (onIdle) parts.push(POps.tn('{n} açık.', onIdle));
            else if (offOld) parts.push(oldUpd ? POps.tn('{n} kapalı.', offOld) : POps.t('Hepsi kapalı.'));
            upD = parts.join(' ') + ' ' + POps.t('Yeni sürüm açılmazsa ajan önceki sürüme kendiliğinden döner.');
            if (IS_SUPER) upBtn = `<button type="button" class="btn" data-act="deploy-old" ${onIdle ? '' : 'disabled'}>${POps.iconHtml('arrow-up', 'sm')}${POps.tnHtml('{n} eski ajanı güncelle', onIdle)}</button>`;
        }
        const upMoreHtml = IS_SUPER && A.staged && !v.release_available ? `<button type="button" class="ibtn sm" data-act="upmore" data-tip="${escapeHtml(POps.t('Hedef seç'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Başka hedefe gönder'))}" aria-haspopup="menu">${POps.iconHtml('more')}</button>` : '';
        $('agSet').innerHTML = `<div class="srow block"><div style="display:flex;justify-content:space-between;gap:12px"><div class="t">${POps.tHtml('Sürüm dağılımı')}</div><div class="v">${POps.tnHtml('{n} ajan', A.all.length)}</div></div>${distHtml(A)}</div>
            <div class="srow"><div class="grow"><div class="t">${A.staged ? POps.tHtml('Ajan paketi {version}', { version: fmtV(A.staged) }) : POps.tHtml('Ajan paketi')}</div><div class="d">${escapeHtml(pkgD)}</div></div><div class="acts">${fetchHtml}${IS_SUPER ? pkgMoreHtml : ''}</div></div>
            <div class="srow"><div class="grow"><div class="t">${escapeHtml(upT)}</div><div class="d">${escapeHtml(upD)}</div></div><div class="acts">${upBtn}${upMoreHtml}</div></div>${peerRowHtml(v)}`;
        if (S.fetching) { const fb = $('agSet').querySelector('[data-act="fetch"]'); if (fb) { fb.disabled = true; fb.classList.add('is-loading'); } }
    }

    // Sınıf içi eş önbelleği (ajan 0.1.23+ peer_cache): sınıfta önce bir bilgisayar (tohum) güncellenir, diğerleri
    // paketi ondan alır. İmza ve SHA-256 her bilgisayarda yine doğrulanır.
    function peerRowHtml(v) {
        if (!IS_SUPER || typeof v.update_peer_cache !== 'boolean') return '';
        const on = v.update_peer_cache;
        return `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Sınıf içinde eşten dağıt')}</div><div class="d">${POps.tHtml(on ? 'Her sınıfta önce bir bilgisayar paketi sunucudan indirir, diğerleri onun güncellemesi bitince paketi yerel ağdan alır. Paketin imzası ve özeti her bilgisayarda yine doğrulanır.' : 'Kapalı: bütün bilgisayarlar paketi sunucudan aynı anda indirir.')}</div></div>
                <label class="switch"><input type="checkbox" id="peerSw" ${on ? 'checked' : ''} aria-label="${escapeHtml(POps.t('Sınıf içinde eşten dağıt'))}"><span></span></label></div>`;
    }
    $('agSet').addEventListener('change', async (e) => {
        if (e.target.id !== 'peerSw') return;
        const sw = e.target, turnOn = sw.checked;
        try {
            const d = await POps.busy(sw, () => POps.post('/api/system/update-peer-cache', { enabled: turnOn }));
            S.ver.update_peer_cache = d.update_peer_cache;
            POps.toast('success', POps.t(d.update_peer_cache ? 'Eşten dağıtım açıldı.' : 'Eşten dağıtım kapatıldı; bekleyen bilgisayarlara güncelleme gönderildi.'));
        } catch (err) { sw.checked = !turnOn; POps.toast('error', POps.errorMessage(err)); }
        renderAgents(true);
    });

    $('agSet').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b || b.disabled) return;
        const A = agentInfo();
        switch (b.dataset.act) {
            case 'fetch': fetchRelease(); break;
            case 'deploy-old': {
                // Güncellemesi sürenlere yeniden gönderilmez
                const upd = updatingPcs();
                const idle = A.oldOn.filter(d => !upd.has(d.hostname));
                deploy(idle.map(d => d.hostname), b, A.old.length - idle.length);
                break;
            }
            case 'pkgmore': POps.menu(b, [
                { label: POps.t('Paketi elle yükle…'), icon: 'upload', onClick: () => openModal('uploadModal') },
                { label: POps.t('Sürüm notları'), icon: 'file', onClick: openNotes }
            ]); break;
            case 'upmore': POps.menu(b, [
                { header: POps.t('{version} nereye gönderilsin?', { version: fmtV(A.staged) }) },
                { label: POps.t('Bir sınıfa…'), icon: 'labs', onClick: () => openTarget('lab') },
                { label: POps.t('Seçili bilgisayarlara…'), icon: 'monitor', onClick: () => openTarget('pc') }
            ]); break;
        }
    });

    async function fetchRelease() {
        const tag = (S.ver && S.ver.latest) || null;
        S.fetching = true; renderAgents(true);
        try {
            const d = await POps.post('/api/system/fetch-release', { tag });
            POps.toast('success', POps.t('{version} indirildi ve imzası doğrulandı.', { version: fmtV(d.version) }));
            S.fetching = false;
            await loadAll(false);
        } catch (e) {
            POps.toast('error', POps.t('Paket indirilemedi: {error}', { error: POps.errorMessage(e) }));
        } finally { S.fetching = false; renderAgents(true); }
    }

    // ---- Paketi elle yükle (internetsiz sunucu)
    let chosen = [];
    function renderFiles() {
        $('upList').innerHTML = chosen.map(f => `<li>${escapeHtml(f.name)} <span>${Math.round(f.size / 1024)} KB</span></li>`).join('');
        $('upBtn').disabled = !chosen.length;
    }
    if (IS_SUPER) {
        const uz = $('uz');
        $('upFiles').addEventListener('change', (e) => { chosen = Array.from(e.target.files); renderFiles(); });
        ['dragover', 'dragenter'].forEach(ev => uz.addEventListener(ev, (e) => { e.preventDefault(); uz.classList.add('dragover'); }));
        ['dragleave', 'drop'].forEach(ev => uz.addEventListener(ev, () => uz.classList.remove('dragover')));
        uz.addEventListener('drop', (e) => { e.preventDefault(); chosen = Array.from(e.dataTransfer.files); renderFiles(); });
        $('upBtn').addEventListener('click', async function () {
            const fd = new FormData();
            chosen.forEach(f => fd.append('files', f));
            fd.append('force', $('upForce').checked ? 'true' : 'false');
            try {
                const d = await POps.busy(this, () => POps.api('/api/system/upload-release', { method: 'POST', body: fd }));
                POps.toast('success', POps.t('{version} doğrulandı ve kaydedildi.', { version: fmtV(d.version) }));
                closeModal('uploadModal');
                chosen = []; $('upFiles').value = ''; renderFiles();
                await loadAll(false);
            } catch (e) { POps.toast('error', POps.t('Paket doğrulanamadı: {error}', { error: POps.errorMessage(e) })); }
        });
    }

    // ---- Gönder
    async function deploy(hosts, btn, skippedOff) {
        const staged = S.ver && S.ver.staged_version;
        hosts = [...new Set(hosts)];
        if (!staged || !hosts.length) return POps.toast('warning', POps.t('Güncellenecek açık bilgisayar yok.'));
        const n = hosts.length;
        const names = hosts.slice(0, 5).map(dev.name).join(', ');
        const ok = await POps.confirm({
            title: n === 1 ? POps.t('{name} {version} sürümüne güncellensin mi?', { name: dev.name(hosts[0]), version: fmtV(staged) }) : POps.tn('{n} ajan {version} sürümüne güncellensin mi?', n, { version: fmtV(staged) }),
            message: (n > 1 ? (n > 5 ? POps.tn('{names} ve {n} bilgisayar daha', n - 5, { names }) : names) + '\n' : '')
                + POps.t('Ajan paketi indirip imzasını kendisi doğrular; yeni sürüm açılmazsa önceki sürüme döner. Bilgisayar birkaç dakika bağlantısız kalabilir.'),
            note: skippedOff ? POps.tn('Kapalı {n} bilgisayar atlanacak.', skippedOff) : '',
            confirmText: n === 1 ? POps.t('Ajanı güncelle') : POps.tn('{n} ajanı güncelle', n), icon: 'arrow-up'
        });
        if (!ok) return false;
        const since = Math.floor(Date.now() / 1000) - 30;
        let d;
        try { d = await POps.busy(btn, () => POps.post('/api/system/deploy-update', { target_mode: 'PC', targets: hosts })); }
        catch (e) { POps.toast('error', POps.t('Güncelleme gönderilemedi: {error}', { error: POps.errorMessage(e) })); return false; }
        // already_pending: aynı sürüm son 15 dk içinde gönderilmiş, kurulum sürüyor; yeniden gönderilmedi ama izlenir
        const sent = d.dispatched || [], off = d.skipped_offline || [], dup = d.already_pending || [], wait = d.waiting_for_seed || [];
        if (!sent.length && !dup.length && !wait.length) { POps.toast('warning', POps.t('Hiçbir bilgisayar bağlı değildi; güncelleme gönderilmedi.')); return false; }
        const said = [];
        if (sent.length) said.push(POps.tn('{version} {n} bilgisayara gönderildi.', sent.length, { version: fmtV(d.version) }));
        if (dup.length) said.push(POps.tn('{n} bilgisayara zaten gönderildi, kurulum sürüyor.', dup.length));
        if (off.length) said.push(POps.tn('{n} kapalı bilgisayar atlandı.', off.length));
        if (wait.length) said.push(POps.tn('{n} bilgisayar sınıfının tohumunu bekliyor.', wait.length));
        POps.toast(sent.length ? 'success' : 'info', said.join(' '));
        rollout = { version: d.version, pcs: [...sent, ...dup, ...wait], skipped: off, since, at: Date.now(), by: ME, doneAt: null };
        store.set(RKEY, rollout);
        rItems = null; rPeer = []; rShowAll = false;
        pollRollout();
        return true;
    }

    // ---- Hedef seç (sınıf ya da seçili bilgisayarlar): sağdaki panel
    const T = { mode: 'lab', lab: '', sel: new Set(), q: '' };
    function targetList() {
        if (T.mode === 'lab') return devs().filter(d => T.lab && d.lab === T.lab);
        return devs().filter(d => T.sel.has(d.hostname));
    }
    function renderTarget() {
        const body = POps.drawer.body();
        const staged = S.ver && S.ver.staged_version;
        const labs = dev.labs();
        if (T.mode === 'lab' && !labs.includes(T.lab)) T.lab = labs[0] || '';
        const list = targetList();
        const sendable = list.filter(d => isOn(d) && normV(dev.version(d)) !== normV(staged));
        const offN = list.filter(d => !isOn(d)).length;
        const sameN = list.filter(d => isOn(d) && normV(dev.version(d)) === normV(staged)).length;
        const segHtml = `<div class="segmented block" role="group" aria-label="${escapeHtml(POps.t('Hedef'))}"><button type="button" data-act="mode" data-mode="lab" aria-pressed="${T.mode === 'lab'}">${POps.tHtml('Bir sınıf')}</button><button type="button" data-act="mode" data-mode="pc" aria-pressed="${T.mode === 'pc'}">${POps.tHtml('Seçili bilgisayarlar')}</button></div>`;
        let pickHtml;
        if (T.mode === 'lab') {
            pickHtml = `<select id="tgLab" aria-label="${escapeHtml(POps.t('Sınıf'))}">${labs.map(l => `<option value="${escapeHtml(l)}" ${l === T.lab ? 'selected' : ''}>${escapeHtml(l)}</option>`).join('')}</select>`;
        } else {
            const q = T.q.toLocaleLowerCase('tr');
            const rows = devs().filter(d => !q || [POps.deviceName(d), d.hostname, d.lab].some(x => String(x || '').toLocaleLowerCase('tr').includes(q)))
                .sort((a, b) => (isOn(b) - isOn(a)) || POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
            const rowsHtml = rows.map(d => {
                const on = isOn(d), ver = dev.version(d), old = normV(ver) !== normV(staged);
                return `<label class="check${on ? '' : ' off'}"><input type="checkbox" data-host="${escapeHtml(d.hostname)}" ${on ? '' : 'disabled'} ${T.sel.has(d.hostname) ? 'checked' : ''}>`
                    + `<span class="dot ${on ? 'on' : 'off'}"></span><span class="nm">${escapeHtml(POps.deviceName(d))}<span class="vv"> · ${escapeHtml(d.lab && d.lab !== dev.UNASSIGNED ? d.lab : POps.t('Atanmamış'))}</span></span>`
                    + `<span class="vv">${escapeHtml(ver ? fmtV(ver) : '—')}${old ? ' ↑' : ''}</span></label>`;
            }).join('') || '<div class="dr-note" style="padding:10px 0">' + POps.tHtml('Süzgece uyan bilgisayar yok.') + '</div>';
            pickHtml = `<div class="search-field" style="flex:none;min-width:0">${POps.iconHtml('search', 'sm')}<input type="search" id="tgSearch" value="${escapeHtml(T.q)}" placeholder="${escapeHtml(POps.t('Bilgisayar ya da sınıf'))}" aria-label="${escapeHtml(POps.t('Bilgisayar ara'))}"></div>`
                + `<div class="dr-list" id="tgList">${rowsHtml}</div>`
                + `<div class="dr-actions"><button type="button" class="lnk" data-act="sel-old">${POps.tHtml('Eski sürümdeki açıkları seç')}</button><span class="faint">·</span><button type="button" class="lnk" data-act="sel-none">${POps.tHtml('Seçimi temizle')}</button></div>`;
        }
        let sum;
        if (!list.length) sum = T.mode === 'lab' ? POps.t('Bu sınıfta bilgisayar yok.') : POps.t('Yalnızca açık bilgisayarlar seçilebilir. Önce tek bilgisayarda denemek iyi olur.');
        else if (!sendable.length) sum = offN === list.length ? POps.t('Seçimdeki bilgisayarların hepsi kapalı.') : POps.t('Seçimdeki açık bilgisayarlar zaten {version} sürümünde.', { version: fmtV(staged) });
        else sum = POps.tn('{n} açık bilgisayar {version} sürümüne güncellenecek.', sendable.length, { version: fmtV(staged) })
            + (sameN ? ' ' + POps.tn('{n} bilgisayar zaten bu sürümde.', sameN) : '') + (offN ? ' ' + POps.tn('{n} kapalı bilgisayar atlanacak.', offN) : '');
        body.innerHTML = drawerHeadHtml(POps.t('Ajan güncellemesi'), POps.tHtml('{version} paketi', { version: fmtV(staged) }), 'arrow-up', '')
            + segHtml + pickHtml
            + `<div class="dr-sum">${escapeHtml(sum)}</div>`
            + `<div><button type="button" class="btn" data-act="send" ${sendable.length ? '' : 'disabled'}>${sendable.length ? (sendable.length === 1 ? POps.tHtml('Ajanı güncelle') : POps.tnHtml('{n} ajanı güncelle', sendable.length)) : POps.tHtml('Güncelle')}</button></div>`;
        const ls = $('tgLab'); if (ls) ls.addEventListener('change', (e) => { T.lab = e.target.value; renderTarget(); });
        const sf = $('tgSearch');
        if (sf) { sf.addEventListener('input', (e) => { T.q = e.target.value; const pos = e.target.selectionStart; renderTarget(); const nf = $('tgSearch'); nf.focus(); try { nf.setSelectionRange(pos, pos); } catch (er) { /* yok */ } }); }
        const tl = $('tgList'); if (tl) tl.addEventListener('change', (e) => { const h = e.target.dataset.host; if (!h) return; e.target.checked ? T.sel.add(h) : T.sel.delete(h); const st = tl.scrollTop; renderTarget(); const nl = $('tgList'); if (nl) nl.scrollTop = st; });
    }
    function openTarget(mode) {
        T.mode = mode; T.q = '';
        openDrawer('syst:target', async (b) => {
            const act = b.dataset.act;
            if (act === 'mode') { T.mode = b.dataset.mode; renderTarget(); }
            else if (act === 'sel-old') { const staged = normV(S.ver && S.ver.staged_version); T.sel = new Set(devs().filter(d => isOn(d) && normV(dev.version(d)) !== staged).map(d => d.hostname)); renderTarget(); }
            else if (act === 'sel-none') { T.sel.clear(); renderTarget(); }
            else if (act === 'send') {
                const staged = normV(S.ver && S.ver.staged_version);
                const list = targetList();
                const hosts = list.filter(d => isOn(d) && normV(dev.version(d)) !== staged).map(d => d.hostname);
                if (await deploy(hosts, b, list.filter(d => !isOn(d)).length)) POps.drawer.close();
            }
        });
        renderTarget();
    }

    // ---- Gönderim ilerlemesi: POST /api/system/update-progress (sayfa yenilense de bu tarayıcıda sürer)
    let rollout = store.get(RKEY);
    let rItems = null, rTimer = null, rErr = null, rShowAll = false, rNow = null, rPeer = [];
    const BAD_RES = ['rollback_failed', 'failed', 'reverted_by_freeze', 'error', 'rejected'];
    // Ajanın bildirdiği adım (update_progress, 0.1.22+). 0.1.21 ve öncesi adım bildirmez; onlarda eski davranış sürer.
    // Değerler Türkçe anahtardır; gösterilirken POps.t ile çevrilir.
    const STAGE_WORDS = new Map([['received', 'Alındı'], ['downloaded', 'İndirildi'], ['verified', 'Doğrulandı'],
        ['updater_started', 'Kurulum başladı'], ['waiting_installer', 'Bekleniyor'], ['installing', 'Kuruluyor'], ['ignored_busy', 'Kuruluyor']]);
    const QUIET_AFTER = 180;   // sn: gönderimden bu kadar sonra hiç adım gelmediyse ajan ilerleme bildirmiyor
    function stageNote(it) {
        const notes = [];
        if (it.stage === 'waiting_installer') notes.push(it.attempt && it.of ? POps.t('Windows Installer meşgul, bekleniyor ({attempt}/{of}).', { attempt: Number(it.attempt), of: Number(it.of) }) : POps.t('Windows Installer meşgul, bekleniyor.'));
        if (it.stage === 'ignored_busy') notes.push(POps.t('Önceki güncelleme sürüyor; bu gönderim yok sayıldı.'));
        if (!it.online) notes.push(['updater_started', 'waiting_installer', 'installing'].includes(it.stage) ? POps.t('Ajan şu an bağlı değil; kurulumda servis yeniden başlar.') : POps.t('Ajan şu an bağlı değil.'));
        return notes.join(' ');
    }
    function itemState(it) {
        const st = baseState(it);
        // Sınıfın tohumu: paketi sunucudan ilk o indirir, sınıfın geri kalanı ondan alır
        if (it.peer && it.peer.role === 'seed' && st.k === 'run') st.note = [POps.t('Sınıfın tohumu: diğerleri paketi bundan alacak.'), st.note].filter(Boolean).join(' ');
        return st;
    }
    function baseState(it) {
        const r = it.result || null;
        const s = String((r && r.status) || '');
        if (it.on_target) return { k: 'ok', w: POps.t('Güncellendi') };
        if (r && r.agent_state === 'unmanaged') return { k: 'bad', w: POps.t('Elle kurulum gerekli'), why: POps.t('Ajan güncellemeden sonra çalışmıyor; bilgisayarda yeniden kurulmalı.') };
        if (r && s === 'rejected') return { k: 'bad', w: POps.tx('Reddedildi', 'update'), why: r.detail ? POps.t('Reddedildi: {reason}', { reason: r.detail }) : POps.t('Reddedildi: ajan sebep bildirmedi') };
        const more = r && r.detail ? ' ' + POps.t('Ajanın bildirdiği: {detail}', { detail: r.detail }) : '';
        if (r && BAD_RES.includes(s)) return { k: 'bad', w: POps.t('Başarısız'), why: POps.t(s === 'rollback_failed' ? 'Güncelleme ve geri dönüş başarısız.' : s === 'reverted_by_freeze' ? 'Dondurma yazılımı (Deep Freeze vb.) güncellemeyi geri aldı.' : 'Güncelleme başarısız.') + more };
        if (r && s === 'rolled_back') return { k: 'warn', w: POps.t('Geri alındı'), why: POps.t('Yeni sürüm sağlıklı açılmadı; önceki sürüme dönüldü.') + more };
        if (r && s === 'install_failed') return { k: 'warn', w: POps.t('Başlatılamadı'), why: POps.t('Kurulum başlatılamadı; bilgisayar değişmedi.') + more };
        if (r && /pending_reboot/.test(s)) return { k: 'run', w: POps.t('Yeniden başlatma bekliyor') };
        if (!it.known) return { k: 'warn', w: POps.t('Kayıtlı değil') };
        if (!it.pending && it.peer && it.peer.role === 'waiting') return { k: 'run', w: POps.t('Tohum bekleniyor'), note: it.online ? '' : POps.t('Ajan şu an bağlı değil; tohum hazır olunca bağlıysa gönderilir.') };
        if (it.pending && STAGE_WORDS.has(it.stage)) return { k: 'run', w: POps.t(STAGE_WORDS.get(it.stage)), at: it.stage_at, note: stageNote(it) };
        if (it.pending && it.online) {
            const quiet = rNow != null && it.sent_at != null && rNow - it.sent_at > QUIET_AFTER;
            return { k: 'run', w: POps.t('Kuruluyor'), note: quiet ? POps.t('Ajan ilerleme bildirmiyor (eski sürüm olabilir).') : '' };
        }
        if (it.pending) return { k: 'run', w: POps.t('Yeniden bağlanıyor') };
        if (!it.online) return { k: 'run', w: POps.t('Kapalı') };
        return { k: 'warn', w: POps.t('Sonuç gelmedi'), why: POps.t('Ajan güncellemeyi aldı ama sonuç bildirmedi; sürümü değişmedi.') };
    }
    function rCounts() {
        const c = { ok: 0, bad: 0, warn: 0, run: 0, total: rollout ? rollout.pcs.length : 0 };
        if (!rItems) { c.run = c.total; return c; }
        rItems.forEach(it => { c[itemState(it).k] += 1; });
        return c;
    }
    // Sınıf başına eş gönderimi (update-progress peer_labs): tohum, adımı ve eşten dağıtılan bilgisayar sayısı
    function peerLabLine(L) {
        const seed = L.seed ? dev.name(L.seed) : '';
        if (L.state === 'fallback') return POps.tn('tohum bulunamadı; {n} bilgisayara sunucudan gönderildi', L.without_peers);
        if (L.state === 'released') {
            return L.via_peers ? POps.tn('tohum: {seed}, doğrulandı; {n} bilgisayara eşten dağıtılıyor', L.via_peers, { seed })
                : POps.t('tohum: {seed}, doğrulandı', { seed });
        }
        const stage = L.seed_stage && STAGE_WORDS.has(L.seed_stage) ? POps.t(STAGE_WORDS.get(L.seed_stage)).toLocaleLowerCase(POps.locale) : POps.t('gönderildi');
        return POps.tn('tohum: {seed}, {stage}; {n} bilgisayar bekliyor', L.waiting, { seed, stage });
    }
    function peerLabsHtml() {
        if (!rPeer.length) return '';
        return `<div class="ro-peer">${rPeer.map(L => `<div class="d">${POps.iconHtml('labs', 'sm')}<span><b>${escapeHtml(L.lab)}</b> · ${escapeHtml(peerLabLine(L))}</span></div>`).join('')}</div>`;
    }
    function renderRollout() {
        const box = $('rollout');
        if (!rollout) { box.hidden = true; box.innerHTML = ''; return; }
        box.hidden = false;
        const c = rCounts();
        const running = c.run > 0 && !rollout.doneAt;
        const done = c.ok + c.bad + c.warn;
        const k = running ? 'run' : c.bad ? 'bad' : c.warn ? 'warn' : 'ok';
        const word = running ? POps.t('Sürüyor') : c.bad || c.warn ? POps.t('{n} sorunlu', { n: c.bad + c.warn }) : POps.t('Tamamlandı');
        const seg = (cls, n) => n ? `<i class="${escapeHtml(cls)}" style="width:${(n / c.total * 100).toFixed(2)}%"></i>` : '';
        const metaHtml = escapeHtml(rollout.by || '?') + ' · ' + POps.timeHtml(rollout.at) + ((rollout.skipped || []).length ? ' · ' + POps.tnHtml('{n} kapalı bilgisayar atlandı', rollout.skipped.length) : '');
        const order = { bad: 0, warn: 1, run: 2, ok: 3 };
        const items = (rItems || rollout.pcs.map(pc => ({ pc, known: true, online: true, pending: true }))).map(it => ({ it, s: itemState(it) }))
            .sort((a, b) => order[a.s.k] - order[b.s.k] || dev.name(a.it.pc).localeCompare(dev.name(b.it.pc), 'tr', { numeric: true }));
        const shown = rShowAll ? items : items.slice(0, 6);
        const rowsHtml = shown.map(({ it, s }) => {
            const d = dev.find(it.pc);
            const icon = s.k === 'ok' ? 'check' : s.k === 'bad' ? 'x' : s.k === 'warn' ? 'alert' : 'clock';
            const metaRow = [d && d.lab && d.lab !== dev.UNASSIGNED ? d.lab : '', it.version ? POps.t('çalışan {version}', { version: fmtV(it.version) }) : ''].filter(Boolean).join(' · ');
            return `<div class="act"><div class="res ${escapeHtml(s.k)}">${POps.iconHtml(icon)}</div>
                <div style="min-width:0"><div class="what">${escapeHtml(dev.name(it.pc))}</div><div class="meta">${escapeHtml(metaRow || it.pc)}</div>
                ${s.note ? `<div class="note">${escapeHtml(s.note)}</div>` : ''}
                ${s.why ? `<div class="why${s.k === 'warn' ? ' warn' : ''}">${escapeHtml(s.why)}</div>` : ''}</div>
                <div class="side">${wordHtml(s.k, s.w)}${s.at ? `<span class="when">${POps.timeHtml(s.at)}</span>` : ''}</div></div>`;
        }).join('');
        box.innerHTML = `<div class="ro-head"><div class="res ${escapeHtml(k)}" style="width:28px;height:28px;border-radius:99px;display:flex;align-items:center;justify-content:center">${running ? '<span class="spinner sm"></span>' : POps.iconHtml(k === 'ok' ? 'check' : 'alert', 'sm')}</div>
                <div class="grow"><div class="t" style="font-weight:var(--fw-medium)">${POps.tHtml('{version} gönderimi', { version: fmtV(rollout.version) })}</div><div class="d" style="font-size:var(--text-xs);color:var(--text-muted)">${metaHtml}</div></div>
                <span class="cnt"><b>${Number(done)}</b>/${Number(c.total)}</span>${wordHtml(k, word)}
                <button type="button" class="ibtn sm" data-act="ro-close" data-tip="${escapeHtml(POps.t(running ? 'İzlemeyi bırak' : 'Kapat'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t(running ? 'İzlemeyi bırak' : 'Kapat'))}">${POps.iconHtml('x', 'sm')}</button></div>
            <div class="ro-bar"><div class="pbar">${seg('ok', c.ok)}${seg('warn', c.warn)}${seg('bad', c.bad)}${seg('run', c.run)}</div>${rErr ? `<div class="dr-note" style="margin-top:6px">${POps.tHtml('İlerleme okunamadı: {error}', { error: POps.errorMessage(rErr) })}</div>` : ''}</div>
            ${peerLabsHtml()}
            <div class="ro-list">${rowsHtml}</div>
            ${items.length > 6 ? `<div class="ro-more"><button type="button" class="lnk" data-act="ro-all">${rShowAll ? POps.tHtml('Daha az göster') : POps.tHtml('Tümünü göster ({n})', { n: items.length })}</button></div>` : ''}`;
    }
    $('rollout').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        if (b.dataset.act === 'ro-all') { rShowAll = !rShowAll; renderRollout(); }
        else if (b.dataset.act === 'ro-close') { clearTimeout(rTimer); rollout = null; rItems = null; rPeer = []; store.set(RKEY, null); renderRollout(); }
    });
    async function pollRollout() {
        clearTimeout(rTimer);
        if (!rollout) { renderRollout(); return; }
        try {
            const r = await POps.post('/api/system/update-progress', { pcs: rollout.pcs, version: normV(rollout.version), since: rollout.since });
            rItems = r.items || []; rErr = null; rNow = typeof r.now === 'number' ? r.now : null; rPeer = r.peer_labs || [];
        } catch (e) { rErr = e; }
        if (!rollout) return;
        const c = rCounts();
        if (!rErr && !c.run && !rollout.doneAt) {
            rollout.doneAt = Date.now(); store.set(RKEY, rollout);
            POps.toast(c.bad || c.warn ? 'warning' : 'success', c.bad || c.warn ? POps.t('{version}: {ok} güncellendi, {bad} sorunlu.', { version: fmtV(rollout.version), ok: c.ok, bad: c.bad + c.warn })
                : POps.tn('{version}: {n} bilgisayar güncellendi.', c.total, { version: fmtV(rollout.version) }));
        }
        renderRollout();
        renderAgents(false);   // "güncelleniyor" sayısı ilerlemeyle birlikte değişir
        // 45 dk sonra (ya da hepsi sonuçlanınca) izleme durur; kapalı bilgisayarlar o zamana dek "Kapalı" görünür
        if (c.run && Date.now() - rollout.at < 45 * 60 * 1000) rTimer = setTimeout(pollRollout, document.hidden ? 15000 : 4000);
        else if (c.run && !rollout.doneAt) { rollout.doneAt = Date.now(); store.set(RKEY, rollout); renderRollout(); }
    }

    // =================================================================
    // SAĞLIK + YEDEK
    // =================================================================
    function backupState(b) {
        if (!b || !b.at) return 'none';
        if (!b.ok || !b.verified) return 'failed';
        return (Date.now() - Date.parse(b.at)) / 1000 > 2 * 86400 ? 'old' : 'ok';
    }
    function healthIssues(d) {
        const out = [];
        if (!d) return out;
        const errs = ((d.log_counts || {}).ERROR || 0) + ((d.log_counts || {}).CRITICAL || 0);
        if (errs) out.push(POps.tn('{n} hata', errs));
        const tick = d.scheduler_last_tick_age;
        if (tick === null || tick === undefined || tick > 120) out.push(POps.t('Zamanlayıcı durdu'));
        (d.disk || []).filter(x => x.level !== 'ok').slice(0, 1).forEach(() => out.push(POps.t('Disk dolmak üzere')));
        (d.tls || []).filter(x => x.level === 'high' || x.level === 'critical').slice(0, 1).forEach(() => out.push(POps.t('Sertifika bitiyor')));
        return out;
    }
    function statHtml(label, val, subHtml, warn) {
        return `<div class="hstat"><div class="l">${warn ? '<span class="dot warn"></span>' : ''}${escapeHtml(label)}</div><div class="val">${escapeHtml(String(val))}</div><div class="s">${subHtml}</div></div>`;
    }
    function renderHealth() {
        const box = $('hlBody');
        if (!box) return;
        const d = S.diag;
        if (!d) { if (S.diagErr) { setState('hlState', 'off', POps.t('Okunamadı')); POps.setError(box, S.diagErr, { compact: true }); } return; }
        const issues = healthIssues(d);
        setState('hlState', issues.length ? 'warn' : 'ok', issues.length ? issues.join(' · ') : POps.t('Sağlıklı'));
        const lc = d.log_counts || {};
        const errs = (lc.ERROR || 0) + (lc.CRITICAL || 0);
        const tick = d.scheduler_last_tick_age;
        const tickBad = tick === null || tick === undefined || tick > 120;
        const pool = d.db_pool || {};
        const dv = d.devices || {};
        const disk = (d.disk || []).slice().sort((a, b) => a.free_percent - b.free_percent)[0];
        const tiles = [
            statHtml(POps.t('Açık kalma'), POps.duration(d.uptime_seconds), POps.tHtml('Bellek {size}', { size: d.rss_mb != null ? d.rss_mb + ' MB' : '—' })),
            statHtml(POps.t('Bağlı ajan'), fmtNum(d.agents_connected), POps.tHtml('{online} çevrimiçi / {total} kayıtlı', { online: fmtNum(dv.online), total: fmtNum(dv.total) })),
            statHtml(POps.t('Veritabanı'), `${(pool.size || 0) - (pool.idle || 0)} / ${pool.max || '—'}`, POps.tHtml('kullanımda / en çok')),
            statHtml(POps.t('Hata'), fmtNum(errs), POps.tHtml('{errors} sunucu hatası, {warnings} uyarı', { errors: fmtNum(d.http_5xx), warnings: fmtNum(lc.WARNING) }), errs > 0),
            statHtml(POps.t('Zamanlayıcı'), tick === null || tick === undefined ? POps.t('Başlamadı') : ago(tick), tickBad ? POps.tHtml('30 sn’de bir çalışmalı') : POps.tHtml('son tur'), tickBad),
            disk ? statHtml(POps.t('Disk'), POps.t('{pct} boş', { pct: POps.pct(disk.free_percent) }), escapeHtml(`${fmtBytes(disk.free_bytes)} / ${fmtBytes(disk.total_bytes)}`), disk.level !== 'ok')
                 : statHtml(POps.t('Uzak ekran oturumu'), fmtNum(d.vision_sessions), POps.tnHtml('{n} açık panel', Number(d.panels_connected) || 0))
        ].join('');
        const tlsHtml = (d.tls || []).map(c => {
            const k = c.level === 'ok' ? 'ok' : c.level === 'unknown' ? 'off' : c.level === 'critical' ? 'bad' : 'warn';
            const w = c.level === 'unknown' ? POps.t('Okunamadı') : POps.tn('{n} gün kaldı', Math.floor(c.days_left));
            return `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Sertifika')}</div><div class="d">${escapeHtml(c.name)}${c.not_after ? ' · ' + POps.tHtml('bitiş {time}', null, { time: POps.timeHtml(c.not_after) }) : ''}</div></div>${wordHtml(k, w)}</div>`;
        }).join('');
        const errList = d.recent_errors || [];
        box.innerHTML = `<div class="hstats">${tiles}</div>
            <div class="set">${tlsHtml}
                <div class="srow click" data-act="errors" role="button" tabindex="0"><div class="grow"><div class="t">${POps.tHtml('Son hatalar')}</div><div class="d">${errList.length ? POps.tHtml('Sorun bildirirken istek kimliğini verin.') : POps.tHtml('Sunucu açıldığından beri hata yok.')}</div></div><span class="v">${Number(errList.length)}</span>${POps.iconHtml('right', 'sm ico-lead')}</div>
                <div class="srow click" data-act="tech" role="button" tabindex="0"><div class="grow"><div class="t">${POps.tHtml('Teknik ayrıntılar')}</div><div class="d">${POps.tHtml('Yük, yavaş istekler, disk ve izleme.')}</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>
            </div>`;
    }
    function renderBackup() {
        const box = $('bkSet');
        if (!box) return;
        if (!S.diag) { if (S.diagErr) { setState('bkState', 'off', POps.t('Okunamadı')); POps.setError(box, S.diagErr, { compact: true }); } return; }
        const b = S.diag.backup;
        const st = backupState(b);
        // Durum sözcükleri Türkçe anahtardır, gösterilirken çevrilir
        const K = { ok: ['ok', 'Tamam'], old: ['warn', '2 günden eski'], failed: ['bad', 'Başarısız'], none: ['bad', 'Yedek yok'] }[st];
        const kWord = POps.t(K[1]);
        setState('bkState', K[0], kWord);
        let rowHtml;
        if (st === 'none') {
            rowHtml = `<div class="act"><div class="res bad">${POps.iconHtml('x')}</div><div style="min-width:0"><div class="what">${POps.tHtml('Henüz yedek alınmadı')}</div><div class="meta">${POps.tHtml('Gece yedeği kurulu değil.')}</div><div class="why">${POps.tHtml('Veritabanı yedeklenmiyor. Kurulum için docs/backup.md (pops-backup.timer).')}</div></div><div class="side">${wordHtml('bad', kWord)}</div></div>`;
        } else {
            const metaHtml = POps.timeHtml(b.at) + (b.bytes ? ' · ' + escapeHtml(fmtBytes(b.bytes)) : '') + (b.verified ? ' · ' + POps.tHtml('geri yükleme sınandı') : '');
            const whyHtml = st === 'failed' ? `<div class="why">${escapeHtml(b.message || POps.t('Yedek alınamadı ya da geri yükleme sınaması geçmedi.'))}</div>` : st === 'old' ? '<div class="why warn">' + POps.tHtml('Son başarılı yedek 2 günden eski; gece yedeği çalışmıyor olabilir.') + '</div>' : '';
            rowHtml = `<div class="act"><div class="res ${escapeHtml(K[0])}">${POps.iconHtml(st === 'ok' ? 'check' : st === 'old' ? 'clock' : 'x')}</div><div style="min-width:0"><div class="what">${POps.tHtml('Son veritabanı yedeği')}</div><div class="meta">${metaHtml}</div>${whyHtml}</div><div class="side">${wordHtml(K[0], kWord)}</div></div>`;
        }
        box.innerHTML = rowHtml + '<div class="srow"><div class="grow"><div class="d">' + POps.tHtml('Yedek her gece alınır ve geri yüklenerek sınanır. Yalnızca son yedeğin sonucu tutulur.') + '</div></div></div>';
    }
    async function loadDiag() {
        if (!IS_SUPER) return;
        try { S.diag = await POps.get('/api/system/diagnostics'); S.diagErr = null; }
        catch (e) { S.diagErr = e; }
        renderHealth(); renderBackup(); renderSummary();
    }
    if ($('hlBody')) $('hlBody').addEventListener('click', (e) => {
        const r = e.target.closest('[data-act]');
        if (!r) return;
        if (r.dataset.act === 'errors') openErrors(); else if (r.dataset.act === 'tech') openTech();
    });
    if ($('hlBody')) $('hlBody').addEventListener('keydown', (e) => {
        const r = e.target.closest('.srow.click[data-act]');
        if (r && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); r.click(); }
    });
    function openErrors() {
        const list = (S.diag && S.diag.recent_errors) || [];
        const body = openDrawer('syse:errors', () => {});
        const rowsHtml = list.map(e => `<div class="act"><div class="res bad">${POps.iconHtml('x')}</div><div style="min-width:0"><div class="what">${escapeHtml(e.msg)}</div>
            <div class="meta">${POps.timeHtml(e.ts)} · ${escapeHtml(e.logger || '')}${e.request_id ? ' · ' + POps.tHtml('istek {id}', { id: e.request_id }) : ''}</div>${e.exc ? `<div class="out">${escapeHtml(e.exc)}</div>` : ''}</div><div class="side"></div></div>`).join('');
        body.innerHTML = drawerHeadHtml(POps.t('Son hatalar'), list.length ? POps.tnHtml('Sunucu açıldığından beri {n} hata', list.length) : POps.tHtml('Hata yok'), 'alert', '')
            + (list.length ? `<div>${rowsHtml}</div>` : '<div class="empty-state compact">' + POps.iconHtml('check') + '<p>' + POps.tHtml('Sunucu açıldığından beri hata kaydı yok.') + '</p></div>');
    }
    function openTech() {
        const d = S.diag || {};
        const l = d.load || {};
        const techRowHtml = (k, v, mono) => v === null || v === undefined || v === '' ? '' : `<div class="grow"><span>${escapeHtml(k)}</span><span${mono ? ' class="mono"' : ''}>${escapeHtml(String(v))}</span></div>`;
        const factsHtml = [techRowHtml(POps.t('Sürüm'), d.version), techRowHtml(POps.t('Süreç'), d.pid, true), techRowHtml(POps.t('Bellek'), d.rss_mb != null ? d.rss_mb + ' MB' : ''), techRowHtml(POps.t('Açık panel'), d.panels_connected),
            techRowHtml(POps.t('Uzak ekran oturumu'), d.vision_sessions), techRowHtml(POps.t('Sonucu beklenen ajan güncellemesi'), d.pending_updates), techRowHtml(POps.t('Karantinadaki cihaz'), (d.devices || {}).quarantined),
            techRowHtml(POps.t('Yakalanmamış hata'), d.unhandled_errors)].join('');
        const loadHtml = [techRowHtml(POps.t('Kalp atışı'), fmtNum(l.heartbeats)), techRowHtml(POps.t('Kalp atışı başına sorgu'), l.queries_per_heartbeat), techRowHtml(POps.t('Veritabanı yazma (sn’de, son 1 dk)'), l.db_writes_per_second),
            techRowHtml(POps.t('Veritabanı okuma / yazma'), `${fmtNum(l.db_reads)} / ${fmtNum(l.db_writes)}`), techRowHtml(POps.t('Görev dağıtımı p95'), l.task_dispatch_p95_seconds != null ? POps.t('{n} sn', { n: l.task_dispatch_p95_seconds }) : ''),
            techRowHtml(POps.t('Komut gönderme p95'), l.command_send_p95_ms != null ? l.command_send_p95_ms + ' ms' : '')].join('');
        const slowHtml = (d.slowest_routes || []).map(r => techRowHtml(r.avg_ms + ' ms · ' + POps.t('{n} istek', { n: fmtNum(r.count) }), r.route, true)).join('');
        const diskHtml = (d.disk || []).map(x => techRowHtml(POps.t('{pct} boş', { pct: POps.pct(x.free_percent) }) + ` · ${fmtBytes(x.free_bytes)} / ${fmtBytes(x.total_bytes)}`, x.path, true)).join('');
        const body = openDrawer('sysx:tech', () => {});
        body.innerHTML = drawerHeadHtml(POps.t('Teknik ayrıntılar'), POps.tHtml('Sunucu açıldığından beri'), 'cpu', '')
            + `<div class="glist">${factsHtml}</div>`
            + (loadHtml ? `<div><h3>${POps.tHtml('Yük')}</h3><div class="glist">${loadHtml}</div></div>` : '')
            + (slowHtml ? `<div><h3>${POps.tHtml('En yavaş istekler (ortalama)')}</h3><div class="glist">${slowHtml}</div></div>` : '')
            + (diskHtml ? `<div><h3>${POps.tHtml('Disk')}</h3><div class="glist">${diskHtml}</div></div>` : '')
            + `<div class="dr-note">${d.metrics_enabled ? POps.tHtml('Prometheus /metrics açık.') : POps.tHtml('Prometheus ile izlemek için sunucunun .env dosyasında METRICS_TOKEN tanımlayın.')} ${POps.tHtml('Güncelleme kanalı sunucudaki /etc/pops/selfupdate.conf dosyasından okunur.')}</div>`;
    }

    // =================================================================
    // KAYIT BÜTÜNLÜĞÜ (denetim zinciri)
    // =================================================================
    function renderAudit() {
        const box = $('auSet');
        if (!box) return;
        const r = store.get(AKEY);
        let k = 'off', w = POps.t('Doğrulanmadı'), resHtml = '';
        if (r) {
            k = r.ok ? 'ok' : 'bad'; w = r.ok ? POps.t('Sağlam') : POps.t('Kırık');
            const metaHtml = escapeHtml(r.by || '?') + ' · ' + POps.timeHtml(r.at) + ' · ' + POps.tHtml('{n} kayıt denetlendi', { n: fmtNum(r.checked) });
            resHtml = `<div class="act"><div class="res ${escapeHtml(k)}">${POps.iconHtml(r.ok ? 'check' : 'x')}</div><div style="min-width:0"><div class="what">${r.ok ? POps.tHtml('Zincir sağlam') : POps.tHtml('Zincir kırık')}</div><div class="meta">${metaHtml}</div>
                ${r.ok ? '' : `<div class="why">${POps.tHtml('#{id} numaralı kayıtta zincir kopuyor: bu kayıt ya da öncesi değiştirilmiş veya silinmiş. Veritabanı yedeğiyle karşılaştırın.', { id: r.first_broken_id })}</div>`}</div><div class="side">${wordHtml(k, w)}</div></div>`;
        }
        setState('auState', k, w);
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Denetim zinciri')}</div><div class="d">${POps.tHtml('Yönetici işlemleri ve güncelleme sonuçları birbirine bağlı kaydedilir; silinen ya da değiştirilen kayıt zinciri bozar.')}</div></div>
            <button type="button" class="btn secondary" id="auVerify">${POps.iconHtml('shield', 'sm')}${POps.tHtml('Doğrula')}</button></div>${resHtml}`;
        $('auVerify').addEventListener('click', async function () {
            try {
                const d = await POps.busy(this, () => POps.get('/api/system/audit-verify'));
                store.set(AKEY, Object.assign({ at: Date.now(), by: ME }, d));
                POps.toast(d.ok ? 'success' : 'error', d.ok ? POps.t('Denetim zinciri sağlam ({n} kayıt).', { n: fmtNum(d.checked) }) : POps.t('Denetim zinciri #{id} numaralı kayıtta kırık.', { id: d.first_broken_id }));
                renderAudit(); renderSummary();
            } catch (e) { POps.toast('error', POps.t('Doğrulanamadı: {error}', { error: POps.errorMessage(e) })); }
        });
    }

    // =================================================================
    // CİHAZ YETENEKLERİ (uzak komut / uzak ekran kalıcı kapatma)
    // =================================================================
    // Kapalı|switch: bir özelliğin kapalı olması (düz "Kapalı" cihazın çevrimdışı olmasıdır)
    function capWord(enabled, requested) {
        if (requested) return { k: 'bad', w: POps.tx('Kapalı', 'switch'), how: POps.t('Panelden kalıcı kapatıldı') };
        if (enabled === false) return { k: 'bad', w: POps.tx('Kapalı', 'switch'), how: POps.t('Kurulumda kapatılmış') };
        if (enabled === true) return { k: 'ok', w: POps.t('Açık'), how: '' };
        return { k: 'off', w: POps.t('Bildirilmedi'), how: POps.t('Ajan bu bilgiyi göndermiyor (0.1.4 öncesi)') };
    }
    let capHash = '';
    function renderCaps(force) {
        const box = $('capSet');
        if (!box || !state.devicesLoaded) return;
        const list = devs().filter(d => d.cap_terminal_enabled === false || d.cap_vision_enabled === false || d.cap_terminal_disable_requested || d.cap_vision_disable_requested)
            .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
        const sig = JSON.stringify([devs().map(d => [d.hostname, POps.deviceName(d), d.status]), list.map(d => [d.hostname, d.cap_terminal_enabled, d.cap_vision_enabled, d.cap_terminal_disable_requested, d.cap_vision_disable_requested])]);
        if (!force && sig === capHash) return;
        capHash = sig;
        setState('capState', list.length ? 'off' : 'ok', list.length ? POps.tn('{n} bilgisayarda kısıtlı', list.length) : POps.t('Hepsinde açık'));
        const opts = devs().slice().sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }))
            .map(d => `<option value="${escapeHtml(d.hostname)}">${escapeHtml(POps.deviceName(d))}${d.lab && d.lab !== dev.UNASSIGNED ? ' · ' + escapeHtml(d.lab) : ''}</option>`).join('');
        const rowsHtml = list.map(d => {
            const t = capWord(d.cap_terminal_enabled, d.cap_terminal_disable_requested), v = capWord(d.cap_vision_enabled, d.cap_vision_disable_requested);
            const parts = [t.k === 'bad' ? POps.t('Uzak komut kapalı') : '', v.k === 'bad' ? POps.t('Uzak ekran kapalı') : ''].filter(Boolean).join(' · ');
            const what = d.cap_terminal_disable_requested || d.cap_vision_disable_requested ? POps.t('{what} (panelden)', { what: parts }) : POps.t('{what} (kurulumda)', { what: parts });
            return `<div class="srow click" data-host="${escapeHtml(d.hostname)}" role="button" tabindex="0"><span class="dot ${escapeHtml(dev.state(d).cls)}"></span><div class="grow"><div class="t">${escapeHtml(POps.deviceName(d))}</div><div class="d">${escapeHtml(what)}</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>`;
        }).join('');
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Uzak komut ve uzak ekran')}</div><div class="d">${POps.tHtml('Bir bilgisayarda kalıcı kapatılır; sunucu ele geçirilse bile o bilgisayarda çalışmaz.')}</div></div>
            <select id="capPick" aria-label="${escapeHtml(POps.t('Bilgisayar seç'))}" class="wide" style="width:min(240px,100%)"><option value="">${POps.tHtml('Bilgisayar seç…')}</option>${opts}</select></div>${rowsHtml}`;
        $('capPick').addEventListener('change', (e) => { if (e.target.value) { openCap(e.target.value); e.target.value = ''; } });
    }
    if ($('capSet')) {
        $('capSet').addEventListener('click', (e) => { const r = e.target.closest('.srow[data-host]'); if (r) openCap(r.dataset.host); });
        $('capSet').addEventListener('keydown', (e) => { const r = e.target.closest('.srow[data-host]'); if (r && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openCap(r.dataset.host); } });
    }

    function capRowHtml(which, d) {
        const enabled = which === 'terminal' ? d.cap_terminal_enabled : d.cap_vision_enabled;
        const requested = which === 'terminal' ? d.cap_terminal_disable_requested : d.cap_vision_disable_requested;
        const c = capWord(enabled, requested);
        const flag = which === 'terminal' ? 'TERMINAL_ENABLED=1' : 'VISION_ENABLED=1';
        const name = which === 'terminal' ? POps.t('Uzak komut') : POps.t('Uzak ekran');
        let btnHtml, hint;
        if (requested) { btnHtml = `<button type="button" class="btn secondary sm" data-act="cap-on" data-which="${escapeHtml(which)}">${POps.tHtml('İzin ver')}</button>`; hint = POps.t('İzin vermek kalıcı kapatmayı kaldırır; yetenek ancak ajan kurulumu onu açık bildirirse geri gelir.'); }
        else if (enabled === false) { btnHtml = `<button type="button" class="btn danger-soft sm" data-act="cap-off" data-which="${escapeHtml(which)}">${POps.tHtml('Kapalı tut')}</button>`; hint = POps.t('Uzaktan açılamaz; açmak için ajanı {flag} ile yeniden kurun. "Kapalı tut" yeniden kurulsa bile kapalı kalmasını sağlar.', { flag }); }
        else { btnHtml = `<button type="button" class="btn danger-soft sm" data-act="cap-off" data-which="${escapeHtml(which)}">${escapeHtml(POps.tx('Kapat', 'switch'))}</button>`; hint = POps.t('Kapatma kalıcıdır; çevrimdışı bilgisayara bağlanınca uygulanır.'); }
        return `<div class="srow"><div class="grow"><div class="t">${escapeHtml(name)}${wordHtml(c.k, c.w)}</div><div class="d">${escapeHtml(c.how ? c.how + '. ' + hint : hint)}</div></div>${btnHtml}</div>`;
    }
    let capSig = '';
    function renderCapDrawer(host, onlyIfChanged) {
        const d = dev.find(host);
        const sig = JSON.stringify(d ? [d.status, POps.deviceName(d), d.lab, dev.version(d), d.cap_terminal_enabled, d.cap_vision_enabled, d.cap_terminal_disable_requested, d.cap_vision_disable_requested, d.cap_server_ca, d.bypass_key, d.last_disconnect_reason, d.agent_health] : null);
        if (onlyIfChanged && sig === capSig) return;
        capSig = sig;
        const body = POps.drawer.body();
        if (!d) { body.innerHTML = drawerHeadHtml(host, POps.tHtml('Bulunamadı'), 'monitor', '') ; return; }
        const st = dev.state(d);
        const h = d.agent_health;
        const ag = (t) => t ? ago(Date.now() / 1000 - t) : POps.t('henüz yok');
        const factHtml = (k, vHtml) => `<div class="grow"><span>${escapeHtml(k)}</span><span>${vHtml}</span></div>`;
        const ca = d.cap_server_ca === 'custom' ? POps.t('Kurum sertifikası') : d.cap_server_ca === 'system' ? POps.t('Sistem deposu') : POps.t('Bildirilmedi');
        const bypass = d.bypass_key === 'device' ? POps.t('Cihaza özel') : d.bypass_key === 'pending' ? POps.t('Gönderildi, onay bekleniyor') : POps.t('Ortak anahtar (eski)');
        let factsHtml = factHtml(POps.t('Ajan'), escapeHtml(fmtV(dev.version(d)))) + factHtml(POps.t('Sunucu sertifikası'), escapeHtml(ca)) + factHtml(POps.t('Çevrimdışı açma anahtarı'), escapeHtml(bypass));
        if (d.last_disconnect_reason) factsHtml += factHtml(POps.t('Son bağlantı kopması'), escapeHtml(d.last_disconnect_reason) + (d.last_disconnect_at ? ' · ' + POps.timeHtml(d.last_disconnect_at) : ''));
        let noteHtml = '';
        if (!h) noteHtml = `<div class="dr-note" style="margin-top:8px">${POps.tHtml('Bu ajan ({version}) durum bildirmiyor; bildirim 0.1.12 ile geldi.', { version: fmtV(dev.version(d)) })}</div>`;
        else {
            const vision = { off: POps.tx('Kapalı', 'switch'), idle: POps.t('Boşta'), connected: POps.t('Bağlı') }[h.vision_channel] || POps.t('Bilinmiyor');
            const errs = Number(h.loop_errors_1h) || 0;
            factsHtml += factHtml(POps.t('Açılış'), escapeHtml(ag(h.started_at))) + factHtml(POps.t('Politika eşitleme'), escapeHtml(ag(h.last_policy_sync))) + factHtml(POps.t('Envanter'), escapeHtml(ag(h.last_inventory_upload)))
                + factHtml(POps.t('Tepsi uygulaması'), wordHtml(h.tray_connected ? 'ok' : 'bad', h.tray_connected ? POps.t('Bağlı') : POps.t('Bağlı değil'))) + factHtml(POps.t('Uzak ekran kanalı'), escapeHtml(vision))
                + factHtml(POps.t('Son 1 saatte hata'), errs ? wordHtml('bad', String(errs)) : escapeHtml('0'));
            if (h.screen_locked) factsHtml += factHtml(POps.t('Karantina kilidi'), wordHtml('bad', POps.t('Açık'))) + factHtml(POps.t('Ağ yalıtımı'), h.network_isolated ? wordHtml('ok', POps.t('Uygulandı')) : wordHtml('bad', POps.t('Uygulanamadı')));
            noteHtml = (errs && h.last_error ? `<div class="issue err" style="margin-top:8px">${POps.tHtml('Son hata: {error}', { error: h.last_error })}</div>` : '')
                + (h.screen_locked && !h.network_isolated && h.isolation_error ? `<div class="issue err" style="margin-top:8px">${POps.tHtml('Yalıtım hatası: {error}', { error: h.isolation_error })}</div>` : '');
        }
        const subHtml = `<span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}${d.lab && d.lab !== dev.UNASSIGNED ? ' · ' + escapeHtml(d.lab) : ''}`;
        body.innerHTML = drawerHeadHtml(POps.deviceName(d), subHtml, 'monitor', st.cls)
            + `<div><h3>${POps.tHtml('Yetenekler')}</h3><div class="set">${capRowHtml('terminal', d)}${capRowHtml('vision', d)}</div>`
            + (d.cap_terminal_enabled == null && d.cap_vision_enabled == null ? `<div class="dr-note" style="margin-top:8px">${POps.tHtml('Bu ajan yetenek durumunu bildirmiyor (bildirim 0.1.4 ile geldi). Kapatma şimdi kaydedilebilir, güncellemeden sonra uygulanır.')}</div>` : '') + '</div>'
            + `<div><h3>${isOn(d) ? POps.tHtml('Ajan durumu') : POps.tHtml('Ajan durumu (son bilinen)')}</h3><div class="glist">${factsHtml}</div>${noteHtml}</div>`
            + `<a href="devices?pc=${encodeURIComponent(d.hostname)}" style="font-size:var(--text-sm)">${POps.tHtml('Cihazlar sayfasında aç')}</a>`;
    }
    let capHost = null;
    function openCap(host) {
        capHost = host;
        openDrawer('syscap:' + host, (b) => {
            if (b.dataset.act === 'cap-off') capSet(capHost, b.dataset.which, false, b);
            else if (b.dataset.act === 'cap-on') capSet(capHost, b.dataset.which, true, b);
        }, () => { capHost = null; });
        renderCapDrawer(host);
    }
    async function capSet(host, which, enable, btn) {
        const d = dev.find(host) || {};
        const name = POps.deviceName(d);
        const term = which === 'terminal';
        const alreadyOff = (term ? d.cap_terminal_enabled : d.cap_vision_enabled) === false;
        if (!enable && !await POps.confirm({
            title: alreadyOff ? (term ? POps.t('{name} bilgisayarında uzak komut kapalı tutulsun mu?', { name }) : POps.t('{name} bilgisayarında uzak ekran kapalı tutulsun mu?', { name }))
                : (term ? POps.t('{name} bilgisayarında uzak komut kapatılsın mı?', { name }) : POps.t('{name} bilgisayarında uzak ekran kapatılsın mı?', { name })),
            message: alreadyOff ? POps.t('Kurulumda kapatılmış; ajan açık kurulsa bile kapalı kalır. Geri açmak için "İzin ver" ve ajanın yeniden kurulması gerekir.')
                : POps.t('Kalıcıdır: geri açmak için "İzin ver" ve ajanın bilgisayarda yeniden kurulması gerekir.'),
            confirmText: alreadyOff ? POps.t('Kapalı tut') : (term ? POps.t('Uzak komutu kapat') : POps.t('Uzak ekranı kapat')), danger: true, icon: 'lock'
        })) return;
        const body = { pc_name: host };
        body[which === 'terminal' ? 'terminal_enabled' : 'vision_enabled'] = !!enable;
        try {
            const r = await POps.busy(btn, () => POps.post('/api/system/set-capabilities', body));
            POps.toast('success', enable ? POps.t('Kalıcı kapatma kaldırıldı. Yetenek, ajan kurulumu onu açık bildirince geri gelir.')
                : (r.delivered_online ? POps.t('Kapatma gönderildi.') : POps.t('Kapatma kaydedildi; bilgisayar bağlanınca uygulanacak.')));
            await POps.loadDevices().catch(() => {});
        } catch (e) { POps.toast('error', POps.errorMessage(e)); }
    }

    // =================================================================
    // AJAN KAYDI VE KİMLİK
    // =================================================================
    function renderEnroll() {
        const box = $('enSet');
        if (!box || !S.ver) return;
        const v = S.ver;
        const on = !!v.enforce_agent_auth, total = v.agents_total || 0, enr = v.agents_enrolled || 0;
        setState('enState', enr === total ? 'ok' : 'off', POps.t('{enrolled}/{total} ajan kayıtlı', { enrolled: enr, total }));
        const hint = on ? POps.t('Açık: anahtarı olmayan ajanlar bağlanamaz.') : (enr < total ? POps.tn('Geçiş modu: kayıtsız ajanlar da bağlanabilir. Açmadan önce {n} ajan kaydolmalı.', total - enr) : POps.t('Kapalı. Bütün ajanlar kayıtlı; açılabilir.'));
        const toks = S.tokens || [];
        const valid = toks.filter(t => !t.expired && !t.is_used).length;
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Kimlik zorlaması')}</div><div class="d">${escapeHtml(hint)}</div></div>
                <label class="switch"><input type="checkbox" id="enforceSw" ${on ? 'checked' : ''} aria-label="${escapeHtml(POps.t('Kimlik zorlaması'))}"><span></span></label></div>
            <div class="srow"><div class="grow"><div class="t">${POps.tHtml('Kayıt jetonları')}</div><div class="d">${S.tokens === null ? POps.tHtml('Yükleniyor…') : toks.length ? POps.tHtml('{valid} geçerli, {used} kullanılmış ya da süresi dolmuş', { valid, used: toks.length - valid }) : POps.tHtml('Henüz jeton yok. Yeni kurulumda MSI’a ENROLL_TOKEN olarak verilir.')}</div></div>
                <button type="button" class="btn secondary" id="tokNew">${POps.iconHtml('plus', 'sm')}${POps.tHtml('Jeton üret')}</button></div>`;
        $('enforceSw').addEventListener('change', (e) => setEnforce(e.target));
        $('tokNew').addEventListener('click', () => {
            $('tkLabs').innerHTML = dev.labs().map(l => `<option value="${escapeHtml(l)}"></option>`).join('');
            openModal('tokenModal');
        });
        renderTokens();
    }
    function renderTokens() {
        const box = $('tokSet');
        if (!box) return;
        const toks = (S.tokens || []).slice(0, 20);
        box.hidden = !toks.length;
        box.innerHTML = toks.map(t => {
            const k = t.expired ? 'off' : t.is_used ? 'off' : 'ok';
            const w = POps.t(t.expired ? 'Süresi doldu' : t.is_used ? 'Tükendi' : 'Geçerli');
            const metaHtml = escapeHtml(t.lab_name || POps.t('Bütün sınıflar')) + ' · ' + POps.tHtml('{used}/{max} kullanım', { used: Number(t.use_count || 0), max: Number(t.max_uses || 1) }) + ' · ' + POps.timeHtml(t.created_at)
                + (!t.expired && !t.is_used && t.expires_at ? ' · ' + POps.tHtml('bitiş {time}', null, { time: POps.timeHtml(t.expires_at) }) : '') + (t.note ? ' · ' + escapeHtml(t.note) : '');
            return `<div class="act"><div class="res">${POps.iconHtml('key')}</div><div style="min-width:0"><div class="what"><code>${escapeHtml(t.token_hint || '')}…</code></div><div class="meta">${metaHtml}</div></div>
                <div class="side" style="flex-direction:row;align-items:center;gap:8px">${wordHtml(k, w)}<button type="button" class="ibtn sm" data-tok="${escapeHtml(t.id)}" data-hint="${escapeHtml(t.token_hint || '')}" data-tip="${escapeHtml(POps.t('Jeton işlemleri'))}" data-tip-pos="left" aria-label="${escapeHtml(POps.t('Jeton işlemleri'))}" aria-haspopup="menu">${POps.iconHtml('more')}</button></div></div>`;
        }).join('');
    }
    if ($('tokSet')) $('tokSet').addEventListener('click', (e) => {
        const b = e.target.closest('[data-tok]');
        if (!b) return;
        POps.menu(b, [{ label: POps.t('Jetonu sil'), icon: 'trash', danger: true, onClick: async () => {
            if (!await POps.confirm({ title: POps.t('{hint}… jetonu silinsin mi?', { hint: b.dataset.hint }), message: POps.t('Bu jetonla henüz kaydolmamış kurulumlar kaydolamaz. Kayıtlı bilgisayarlar etkilenmez.'), confirmText: POps.t('Jetonu sil'), danger: true, icon: 'trash' })) return;
            if (await POps.act(null, () => POps.del('/api/system/enroll-token/' + encodeURIComponent(b.dataset.tok)), { success: POps.t('Jeton silindi.') })) loadTokens();
        } }]);
    });
    async function loadTokens() {
        if (!IS_SUPER) return;
        try { S.tokens = await POps.get('/api/system/enroll-tokens') || []; } catch (e) { S.tokens = []; POps.toast('error', POps.t('Jetonlar alınamadı: {error}', { error: POps.errorMessage(e) })); }
        renderEnroll();
    }
    if ($('tkCreate')) $('tkCreate').addEventListener('click', async function () {
        const body = { lab_name: $('tkLab').value.trim() || null, note: $('tkNote').value.trim() || null, ttl_hours: parseInt($('tkTtl').value, 10) || 72, max_uses: parseInt($('tkUses').value, 10) || 1 };
        let d;
        try { d = await POps.busy(this, () => POps.post('/api/system/enroll-token', body)); }
        catch (e) { POps.toast('error', POps.t('Jeton üretilemedi: {error}', { error: POps.errorMessage(e) })); return; }
        closeModal('tokenModal');
        ['tkLab', 'tkNote'].forEach(id => { $(id).value = ''; });
        loadTokens();
        await POps.alert({
            title: POps.t('Kayıt jetonu hazır'), icon: 'key', codes: [d.token], confirmText: POps.t('Kapat'),
            message: (d.lab_name || POps.t('Bütün sınıflar')) + ' · ' + POps.tn('{n} kullanım', d.max_uses) + ' · ' + POps.tn('{n} saat geçerli', d.ttl_hours),
            note: POps.t('MSI kurulumunda ENROLL_TOKEN olarak verin. Jeton yalnızca şimdi gösterilir, sunucuda saklanmaz; şimdi kopyalayın.')
        });
    });
    async function setEnforce(sw) {
        const turnOn = sw.checked;
        const v = S.ver || {};
        const missing = (v.agents_total || 0) - (v.agents_enrolled || 0);
        if (turnOn && !await POps.confirm({
            title: POps.t('Kimlik zorlaması açılsın mı?'), danger: missing > 0, icon: 'lock',
            message: missing > 0 ? POps.tn('{n} ajan kayıtlı değil; zorlama açılınca bağlantılarını kaybederler.', missing) : POps.t('Anahtarı olmayan ajan artık bağlanamaz.'),
            confirmText: missing > 0 ? POps.tn('Aç, {n} ajan kopsun', missing) : POps.t('Zorlamayı aç')
        })) { sw.checked = false; return; }
        try {
            const d = await POps.busy(sw, () => POps.post('/api/system/enforce-auth', { enabled: turnOn }));
            S.ver.enforce_agent_auth = d.enforce_agent_auth;
            POps.toast('success', d.enforce_agent_auth ? POps.t('Kimlik zorlaması açıldı.') : POps.t('Kimlik zorlaması kapatıldı.'));
        } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        renderEnroll();
    }

    // =================================================================
    // BİLDİRİMLER
    // =================================================================
    const ntBody = () => ({ enabled: $('ntEnabled').checked, min_severity: $('ntSev').value, email_to: $('ntEmail').value.trim(), webhook_url: $('ntWebhook').value.trim() });
    function renderNotify(d) {
        S.notify = d;
        $('ntEnabled').checked = !!d.enabled;
        $('ntSev').value = d.min_severity || 'high';
        $('ntEmail').value = d.email_to || '';
        $('ntWebhook').value = d.webhook_url || '';
        $('ntSmtp').textContent = d.smtp_configured ? POps.t('Virgülle ayırın. Sunucuda SMTP ayarlı.') : POps.t('E-posta için sunucunun .env dosyasında SMTP_HOST ve SMTP_FROM tanımlanmalı. Webhook ek ayar gerektirmez.');
        const on = d.enabled && (d.email_to || d.webhook_url);
        setState('ntState', on ? 'ok' : 'off', on ? POps.t('Dışarıya gönderiliyor') : POps.t('Yalnızca panelde'));
        ntDirty();
    }
    async function loadNotify() {
        if (!IS_SUPER) return;
        try { renderNotify(await POps.get('/api/system/notify-settings')); }
        catch (e) { setState('ntState', 'off', POps.t('Okunamadı')); }
    }
    function ntDirty() {
        const d = S.notify, b = ntBody();
        const dirty = !!d && (b.enabled !== !!d.enabled || b.min_severity !== (d.min_severity || 'high') || b.email_to !== (d.email_to || '') || b.webhook_url !== (d.webhook_url || ''));
        $('ntSave').className = dirty ? 'btn' : 'btn secondary';
    }
    if ($('ntSave')) {
        $('ntSet').addEventListener('input', ntDirty);
        $('ntSet').addEventListener('change', ntDirty);
        $('ntSave').addEventListener('click', async function () {
            try { renderNotify(await POps.busy(this, () => POps.post('/api/system/notify-settings', ntBody()))); POps.toast('success', POps.t('Bildirim ayarları kaydedildi.')); }
            catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
        $('ntTest').addEventListener('click', async function () {
            try {
                const d = await POps.busy(this, () => POps.post('/api/system/notify-test', ntBody()));
                if (d.error) POps.toast('error', (d.channels || []).length ? POps.t('Gönderilemedi: {error} (başarılı: {channels})', { error: d.error, channels: d.channels.join(', ') }) : POps.t('Gönderilemedi: {error}', { error: d.error }));
                else POps.toast('success', POps.t('Test bildirimi gönderildi: {channels}', { channels: (d.channels || []).join(', ') }));
            } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
    }

    // =================================================================
    // SAKLAMA SÜRELERİ
    // =================================================================
    // [alan, ayar, onayda silinecekleri anlatan metin (Türkçe anahtar; {n} gün)]
    const RT = [['rtLogs', 'retention_days_logs', '{n} günden eski ajan olay kayıtları'], ['rtTasks', 'retention_days_tasks', '{n} günden eski sonuçlanmış görevler'],
        ['rtNotif', 'retention_days_notifications', '{n} günden eski okunmuş bildirimler']];
    function renderRetention(d) {
        S.retention = d;
        RT.forEach(([id, key]) => { $(id).value = d[key]; });
        $('rtSave').disabled = true; $('rtSave').className = 'btn secondary';
    }
    async function loadRetention() {
        if (!IS_SUPER) return;
        try { renderRetention(await POps.get('/api/system/retention')); } catch (e) { /* bölüm boş kalır */ }
    }
    if ($('rtSet')) {
        $('rtSet').addEventListener('input', () => {
            const dirty = S.retention && RT.some(([id, key]) => String(parseInt($(id).value, 10)) !== String(S.retention[key]));
            $('rtSave').disabled = !dirty; $('rtSave').className = dirty ? 'btn' : 'btn secondary';
        });
        $('rtSave').addEventListener('click', async function () {
            const body = {};
            for (const [id, key] of RT) {
                const n = parseInt($(id).value, 10);
                if (isNaN(n) || n < 0 || n > 3650) { POps.toast('warning', POps.t('Süre 0 ile 3650 gün arasında olmalı.')); $(id).focus(); return; }
                body[key] = n;
            }
            // Kısalan süre bu gece kayıt siler: onay
            const shorter = RT.filter(([, key]) => body[key] > 0 && (S.retention[key] === 0 || body[key] < S.retention[key]));
            if (shorter.length && !await POps.confirm({
                title: POps.t('Saklama süreleri kısaltılsın mı?'), icon: 'clock',
                message: POps.t('{list} bu gece silinir. Silinen kayıt geri gelmez.', { list: shorter.map(([, key, label]) => POps.tn(label, body[key])).join(', ') }),
                confirmText: POps.t('Kısalt ve kaydet'), danger: true
            })) return;
            try { renderRetention(await POps.busy(this, () => POps.post('/api/system/retention', body))); POps.toast('success', POps.t('Saklama süreleri kaydedildi.')); }
            catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
    }

    // =================================================================
    // MODÜLLER: kurum geneli ve sınıf bazında açma/kapama, bağımlılıklar, kurulum profilleri (GET /api/modules)
    // =================================================================
    // Kapalı modülün çalışan işlere etkisi (docs/api.md "Modules and install profiles", docs/agent.md "Modules")
    const MOD_OFF = {
        vision: 'Açık uzak ekran oturumları hemen kapanır; canlı ekran, önizleme ve uzaktan kontrol açılamaz.',
        terminal: 'Bekleyen ve duraklatılmış komut görevleri reddedilir; kuyruk bu bilgisayarlara komut göndermez, ajan gelen komutu çalıştırmaz. Güç komutları ve mesajlar etkilenmez.',
        deploy: 'Paket kitaplığı, dosya yükleme ve dağıtım kullanılamaz; bekleyen winget kurulumları reddedilir.',
        files: 'Dosya gönderilemez ve alınamaz; başlamamış aktarımlar iptal edilir.',
        exam: 'Sınav modu başlatılamaz; süren sınavlar biter.',
        schedules: 'Zamanlanmış görevler bu bilgisayarlarda çalıştırılmaz.',
        patches: 'Günlük Windows Update taraması ve sonucu alınmaz; tarama ve kurma istenemez.',
        software: 'Yazılım listeleri toplanmaz ve saklanmaz.',
        licenses: 'Lisans sayfası kapanır; aşım ve süre bildirimleri durur.',
        helpdesk: 'Tepsideki "Sorun bildir" gizlenir; yeni talep açılamaz ve talepler panelde görünmez.',
        dns_policy: 'Ajana alan adı listesi gönderilmez; DNS uyarıları saklanmaz ve eşikte karantina olmaz.',
        quarantine: 'Yeni karantina uygulanamaz; kaldırma ve çevrimdışı açma kodu çalışmaya devam eder.',
        wol: 'Kapalı bilgisayarlar ağdan uyandırılamaz.',
        reports: 'Raporlar sayfası ve CSV dışa aktarma kapanır.'
    };
    const MD = { data: null, err: null, open: null };
    const hasOwn = (o, k) => Object.prototype.hasOwnProperty.call(o || {}, k);
    const modById = (id) => ((MD.data && MD.data.modules) || []).find(m => m.id === id) || null;
    const modName = (id) => { const m = modById(id); return POps.t(m ? m.name : id); };
    const labLabel = (l) => (l === dev.UNASSIGNED ? POps.t('Atanmamış') : l);
    // Modülün kendi ayarı (bağımlılığa bakmadan): sınıf istisnası > kurum ayarı > açık; etkin durum sunucudan
    const ownOn = (m, lab) => (lab != null && hasOwn(m.lab_overrides, lab) ? !!m.lab_overrides[lab] : m.setting !== false);
    const effOn = (m, lab) => (lab == null ? !!m.enabled : !!(m.lab_enabled || {})[lab]);
    function blockedBy(m, lab) {
        const off = (ids) => ids.filter(id => { const d = modById(id); return d && !effOn(d, lab); });
        const all = off(m.depends || []);
        if (all.length) return all;
        const any = m.depends_any || [];
        return any.length && off(any).length === any.length ? any.slice() : [];
    }
    function modWord(m, lab) {
        if (effOn(m, lab)) return { k: 'ok', w: POps.t('Açık'), why: '' };
        if (!ownOn(m, lab)) return { k: 'bad', w: POps.tx('Kapalı', 'switch'), why: '' };
        return { k: 'warn', w: POps.tx('Kapalı', 'switch'), why: POps.t('{modules} kapalı olduğu için çalışmaz.', { modules: blockedBy(m, lab).map(modName).join(', ') }) };
    }
    function depText(m) {
        if ((m.depends || []).length) return POps.t('Gerekir: {modules}', { modules: m.depends.map(modName).join(', ') });
        if ((m.depends_any || []).length > 1) return POps.t('Şunlardan biri gerekir: {modules}', { modules: m.depends_any.map(modName).join(', ') });
        if ((m.depends_any || []).length) return POps.t('Gerekir: {modules}', { modules: m.depends_any.map(modName).join(', ') });
        return '';
    }
    const dependents = (id) => ((MD.data && MD.data.modules) || []).filter(m => (m.depends || []).includes(id) || (m.depends_any || []).includes(id));

    function renderModules() {
        const box = $('mdSet');
        if (!box) return;
        const d = MD.data;
        if (!d) { if (MD.err) { sectionError(box, MD.err); sectionError($('pfSet'), MD.err); } return; }
        const mods = d.modules || [];
        const offN = mods.filter(m => !m.enabled).length;
        setState('mdState', offN ? 'off' : 'ok', offN ? POps.t('{on} açık, {off} kapalı', { on: mods.length - offN, off: offN }) : POps.t('Hepsi açık'));
        box.innerHTML = mods.map(m => {
            const w = modWord(m, null);
            const diff = (d.labs || []).filter(l => effOn(m, l) !== !!m.enabled).length;
            const meta = [depText(m), diff ? (m.enabled ? POps.tn('{n} sınıfta kapalı', diff) : POps.tn('{n} sınıfta açık', diff)) : ''].filter(Boolean).join(' · ');
            const label = POps.t('{module}, kurum geneli', { module: POps.t(m.name) });
            return `<div class="srow click md-row" data-mod="${escapeHtml(m.id)}" role="button" tabindex="0">
                <div class="grow"><div class="t">${escapeHtml(POps.t(m.name))}${wordHtml(w.k, w.w)}</div><div class="d">${escapeHtml(POps.t(m.description))}</div>
                ${w.why ? `<div class="d md-why">${escapeHtml(w.why)}</div>` : ''}${meta ? `<div class="d">${escapeHtml(meta)}</div>` : ''}</div>
                <label class="switch"><input type="checkbox" data-sw="${escapeHtml(m.id)}" ${m.setting !== false ? 'checked' : ''} aria-label="${escapeHtml(label)}"><span></span></label></div>`;
        }).join('');
        renderProfiles();
        if (MD.open && POps.drawer.isOpen('sysmod:' + MD.open)) renderModDrawer(MD.open);
    }

    function renderProfiles() {
        const box = $('pfSet'), d = MD.data;
        if (!box || !d) return;
        const names = d.profile_names || {};
        const cur = d.profile;
        let k, desc;
        if (!cur) { k = 'warn'; desc = POps.t('Yeni kurulum: kurumunuza uyan profili seçin. Seçilene kadar bütün modüller açık.'); }
        else if (cur === 'custom') { k = 'off'; desc = POps.t('Modüller elle değiştirildi ya da kurulum profilden önce vardı.'); }
        else { k = 'ok'; desc = POps.t('Profilin modül ayarları uygulandı.'); }
        const curName = cur ? POps.t(names[cur] || cur) : POps.t('Seçilmedi');
        setState('pfState', k, curName);
        const INTRO = { school: POps.t('Sınıfta ders ve sınav: uzak ekran, uzak komut, dağıtım ve DNS politikası.'),
            org: POps.t('Ofis ve idari bilgisayarlar: envanter, lisanslar, yardım masası ve raporlar.') };
        const rowsHtml = ['school', 'org'].map(p => {
            const off = (d.modules || []).filter(m => m.profiles && m.profiles[p] === false).map(m => POps.t(m.name));
            const text = INTRO[p] + ' ' + (off.length ? POps.t('Kapalı kalanlar: {modules}.', { modules: off.join(', ') }) : POps.t('Bütün modüller açık.'));
            return `<div class="srow click" data-prof="${escapeHtml(p)}" role="button" tabindex="0"><div class="grow"><div class="t">${escapeHtml(POps.t(names[p] || p))}${cur === p ? wordHtml('ok', POps.t('Uygulandı')) : ''}</div>
                <div class="d">${escapeHtml(text)}</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>`;
        }).join('');
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">${escapeHtml(POps.t('Şu anki profil'))}</div><div class="d">${escapeHtml(desc)}</div></div><div class="v">${escapeHtml(curName)}</div></div>${rowsHtml}`;
    }

    async function loadModules() {
        if (!IS_SUPER || !$('mdSet')) return;
        try { MD.data = await POps.get('/api/modules'); MD.err = null; }
        catch (e) { MD.err = e; if (MD.data) { POps.toast('error', POps.errorMessage(e)); return; } }
        renderModules();
    }

    // Değişikliğin etkisi önce sunucuya sorulur (hiçbir şey yazmaz); bir şey kapanacaksa onay istenir
    const previewPath = (id, value, lab) => '/api/modules/' + encodeURIComponent(id) + '/preview?'
        + new URLSearchParams(Object.assign(value === null ? {} : { enabled: String(value) }, lab ? { lab } : {})).toString();
    function effectNotes(r) {
        const out = [];
        if (r.vision_sessions_closed) out.push(POps.tn('Açık {n} uzak ekran oturumu kapanır.', r.vision_sessions_closed));
        if (r.tasks_denied) out.push(POps.tn('Bekleyen ya da duraklatılmış {n} görev reddedilir.', r.tasks_denied));
        if (r.transfers_cancelled) out.push(POps.tn('Başlamamış {n} dosya aktarımı iptal edilir.', r.transfers_cancelled));
        if (r.exams_ended) out.push(POps.tn('Süren {n} sınav biter.', r.exams_ended));
        return out;
    }
    function effectDone(r) {
        const out = [];
        if (r.vision_sessions_closed) out.push(POps.tn('{n} uzak ekran oturumu kapatıldı.', r.vision_sessions_closed));
        if (r.tasks_denied) out.push(POps.tn('{n} görev reddedildi.', r.tasks_denied));
        if (r.transfers_cancelled) out.push(POps.tn('{n} dosya aktarımı iptal edildi.', r.transfers_cancelled));
        if (r.exams_ended) out.push(POps.tn('{n} sınav bitirildi.', r.exams_ended));
        return out.join(' ');
    }
    async function setModule(id, lab, value, ctl) {
        const name = modName(id);
        let pv;
        try { pv = await POps.busy(ctl, () => POps.get(previewPath(id, value, lab))); }
        catch (e) { POps.toast('error', POps.errorMessage(e)); renderModules(); return false; }
        const offs = (pv.changes || []).filter(c => !c.to);
        if (offs.length || pv.vision_sessions_closed || pv.tasks_denied || pv.transfers_cancelled || pv.exams_ended) {
            const others = [...new Set(offs.map(c => c.id).filter(x => x !== id))];
            const notes = (others.length ? [POps.t('Bağımlı oldukları için bunlar da kapanır: {modules}.', { modules: others.map(modName).join(', ') })] : []).concat(effectNotes(pv));
            const ids = [...new Set(offs.map(c => c.id))];
            const title = value === null
                ? POps.t('{lab} sınıfı {module} için kurum ayarına dönsün mü?', { lab: labLabel(lab), module: name })
                : (lab ? POps.t('{module} {lab} sınıfında kapatılsın mı?', { module: name, lab: labLabel(lab) }) : POps.t('{module} kurum genelinde kapatılsın mı?', { module: name }));
            if (!await POps.confirm({
                title, icon: 'lock', danger: true,
                message: ids.map(x => (MOD_OFF[x] ? modName(x) + ': ' + POps.t(MOD_OFF[x]) : '')).filter(Boolean).join(' '),
                note: notes.concat([POps.t('Ajanlar değişikliği en geç bir dakikada alır.')]).join(' '),
                confirmText: value === null ? POps.t('Kurum ayarına dön') : POps.t('Modülü kapat')
            })) { renderModules(); return false; }
        }
        try {
            const r = await POps.busy(ctl, () => POps.post('/api/modules/' + encodeURIComponent(id), { enabled: value, lab: lab || null }));
            const what = value === null ? POps.t('{module}: {lab} sınıfı kurum ayarına döndü.', { module: name, lab: labLabel(lab) })
                : value ? (lab ? POps.t('{module} {lab} sınıfında açıldı.', { module: name, lab: labLabel(lab) }) : POps.t('{module} açıldı.', { module: name }))
                    : (lab ? POps.t('{module} {lab} sınıfında kapatıldı.', { module: name, lab: labLabel(lab) }) : POps.t('{module} kapatıldı.', { module: name }));
            POps.toast('success', [what, effectDone(r)].filter(Boolean).join(' '));
        } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        await loadModules();
        return true;
    }

    // ---- Modülün ayrıntısı: kurum ayarı, bağımlılıklar, kapalıyken ne olur, sınıf istisnaları
    function segHtml(group, cur, choices, act, lab) {
        return `<div class="segmented" role="group" aria-label="${escapeHtml(group)}">` + choices.map(([v, t]) =>
            `<button type="button" data-act="${escapeHtml(act)}" data-lab="${escapeHtml(lab || '')}" data-val="${escapeHtml(v)}" aria-pressed="${cur === v ? 'true' : 'false'}">${escapeHtml(t)}</button>`).join('') + '</div>';
    }
    function renderModDrawer(id) {
        const m = modById(id), d = MD.data;
        const body = POps.drawer.body();
        if (!m || !body) return;
        const w = modWord(m, null);
        const find = body.querySelector('.md-find');
        const q = find ? find.value : '', refocus = !!find && document.activeElement === find;
        const orgHtml = `<div class="srow"><div class="grow"><div class="t">${escapeHtml(POps.t('Kurum geneli'))}${wordHtml(w.k, w.w)}</div>`
            + `<div class="d">${escapeHtml(w.why || POps.t('İstisnası olmayan sınıflar bu ayarı izler.'))}</div></div>`
            + segHtml(POps.t('Kurum geneli'), m.setting === false ? 'off' : 'on', [['on', POps.t('Açık')], ['off', POps.tx('Kapalı', 'switch')]], 'org', '') + '</div>';
        const deps = depText(m), deb = dependents(id).map(x => POps.t(x.name));
        const depHtml = deps || deb.length ? `<div><h3>${escapeHtml(POps.t('Bağımlılıklar'))}</h3><div class="glist">`
            + (deps ? `<div class="grow"><span>${escapeHtml(POps.t('Bu modül'))}</span><span>${escapeHtml(deps)}</span></div>` : '')
            + (deb.length ? `<div class="grow"><span>${escapeHtml(POps.t('Buna bağlı'))}</span><span>${escapeHtml(deb.join(', '))}</span></div>` : '') + '</div></div>' : '';
        const labs = d.labs || [];
        const labRowsHtml = labs.map(l => {
            const lw = modWord(m, l);
            const own = hasOwn(m.lab_overrides, l) ? (m.lab_overrides[l] ? 'on' : 'off') : 'inherit';
            const how = own === 'inherit' ? POps.t('Kurum ayarını izliyor.') : POps.t('Bu sınıfa özel ayar.');
            const hide = q && !labLabel(l).toLocaleLowerCase('tr').includes(q.toLocaleLowerCase('tr'));
            return `<div class="srow" data-labrow="${escapeHtml(l)}"${hide ? ' hidden' : ''}><div class="grow"><div class="t">${escapeHtml(labLabel(l))}${wordHtml(lw.k, lw.w)}</div><div class="d">${escapeHtml(lw.why ? how + ' ' + lw.why : how)}</div></div>`
                + segHtml(labLabel(l), own, [['inherit', POps.t('Kurum ayarı')], ['on', POps.t('Açık')], ['off', POps.tx('Kapalı', 'switch')]], 'lab', l) + '</div>';
        }).join('');
        const labsHtml = `<div class="md-labs"><h3>${escapeHtml(POps.t('Sınıflar'))}</h3>`
            + (labs.length > 8 ? `<input type="search" class="md-find" placeholder="${escapeHtml(POps.t('Sınıf ara…'))}" aria-label="${escapeHtml(POps.t('Sınıf ara'))}" value="${escapeHtml(q)}" style="margin:6px 0 8px">` : '')
            + (labs.length ? `<div class="set">${labRowsHtml}</div>` : `<div class="dr-note">${escapeHtml(POps.t('Henüz sınıf yok.'))}</div>`) + '</div>';
        const subHtml = `<span class="dot ${escapeHtml(w.k === 'ok' ? 'ok' : 'off')}"></span>${escapeHtml(POps.t(m.description))}`;
        body.innerHTML = drawerHeadHtml(POps.t(m.name), subHtml, 'sliders', w.k === 'ok' ? 'on' : '')
            + `<div class="set">${orgHtml}</div>`
            + (MOD_OFF[id] ? `<div><h3>${escapeHtml(POps.t('Kapalıyken'))}</h3><p class="dr-sum">${escapeHtml(POps.t(MOD_OFF[id]))}</p></div>` : '')
            + depHtml + labsHtml;
        const f = body.querySelector('.md-find');
        if (f) {
            f.addEventListener('input', () => body.querySelectorAll('[data-labrow]').forEach(r => { r.hidden = !!f.value && !labLabel(r.dataset.labrow).toLocaleLowerCase('tr').includes(f.value.toLocaleLowerCase('tr')); }));
            if (refocus) f.focus();
        }
    }
    function openModule(id) {
        // Önceki panelin onClose'u açılışta çalışır: MD.open yeni panel açıldıktan sonra yazılır
        openDrawer('sysmod:' + id, (b) => {
            if (b.getAttribute('aria-pressed') === 'true') return;
            const v = b.dataset.val;
            if (b.dataset.act === 'org') setModule(id, null, v === 'on', b);
            else if (b.dataset.act === 'lab') setModule(id, b.dataset.lab, v === 'inherit' ? null : v === 'on', b);
        }, () => { if (MD.open === id) MD.open = null; });
        MD.open = id;
        renderModDrawer(id);
    }
    if ($('mdSet')) {
        $('mdSet').addEventListener('change', (e) => {
            const sw = e.target.closest('[data-sw]');
            if (sw) setModule(sw.dataset.sw, null, sw.checked, sw);
        });
        $('mdSet').addEventListener('click', (e) => {
            if (e.target.closest('.switch')) return;
            const r = e.target.closest('[data-mod]');
            if (r) openModule(r.dataset.mod);
        });
        $('mdSet').addEventListener('keydown', (e) => {
            const r = e.target.closest('[data-mod]');
            if (r && e.target === r && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openModule(r.dataset.mod); }
        });
    }

    // ---- Kurulum profili: önizleme (değişecek kurum ayarları, kapanacak oturum ve reddedilecek görevler), sonra uygula
    async function renderProfDrawer(name, reset) {
        const d = MD.data, names = (d && d.profile_names) || {};
        const title = POps.t(names[name] || name);
        const headHtml = drawerHeadHtml(title, escapeHtml(POps.t('Kurulum profili')), 'sliders', '');
        const body = POps.drawer.body();
        let pv;
        try { pv = await POps.get('/api/system/install-profile/' + encodeURIComponent(name) + (reset ? '?reset_labs=true' : '')); }
        catch (e) { if (POps.drawer.isOpen('sysprof:' + name)) { body.innerHTML = headHtml + '<div></div>'; POps.setError(body.lastElementChild, e, { compact: true }); } return; }
        if (!POps.drawer.isOpen('sysprof:' + name)) return;
        const chHtml = (pv.changes || []).map(c => `<div class="srow"><div class="grow"><div class="t">${escapeHtml(POps.t(c.name))}</div></div>`
            + `${wordHtml(c.from ? 'ok' : 'bad', c.from ? POps.t('Açık') : POps.tx('Kapalı', 'switch'))}${POps.iconHtml('right', 'sm ico-lead')}${wordHtml(c.to ? 'ok' : 'bad', c.to ? POps.t('Açık') : POps.tx('Kapalı', 'switch'))}</div>`).join('');
        const notes = effectNotes(pv);
        const resetHtml = pv.lab_overrides ? `<label class="check"><input type="checkbox" data-act="reset" ${reset ? 'checked' : ''}> ${escapeHtml(POps.tn('{n} sınıf istisnasını da sil', pv.lab_overrides))}</label>`
            + `<div class="dr-note">${escapeHtml(reset ? POps.t('Bütün sınıflar kurum ayarını izler.') : POps.t('Sınıflara özel ayarlar korunur ve profilden üstün gelir.'))}</div>` : '';
        body.innerHTML = headHtml
            + `<p class="dr-sum">${escapeHtml(POps.t('Profilin modül ayarları kurum geneline yazılır; sonra her modül tek tek değiştirilebilir.'))}</p>`
            + `<div><h3>${escapeHtml(POps.t('Kurum genelinde değişecekler'))}</h3>` + (chHtml ? `<div class="set">${chHtml}</div>` : `<div class="dr-note">${escapeHtml(POps.t('Kurum ayarlarında değişiklik yok.'))}</div>`) + '</div>'
            + (resetHtml ? `<div>${resetHtml}</div>` : '')
            + (notes.length ? notes.map(t => `<div class="issue upd">${POps.iconHtml('alert', 'sm')}${escapeHtml(t)}</div>`).join('') : '')
            + `<div class="dr-actions"><button type="button" class="btn" data-act="apply">${escapeHtml(POps.t('Profili uygula'))}</button><button type="button" class="btn secondary" data-act="close">${escapeHtml(POps.t('Vazgeç'))}</button></div>`;
    }
    function openProfile(name) {
        openDrawer('sysprof:' + name, async (b) => {
            if (b.dataset.act === 'reset') renderProfDrawer(name, b.checked);
            else if (b.dataset.act === 'apply') {
                // Ekranda görünen seçim uygulanır (önizleme yenilenirken tıklansa da)
                const box = POps.drawer.body().querySelector('[data-act="reset"]');
                const reset = !!(box && box.checked);
                try {
                    const r = await POps.busy(b, () => POps.post('/api/system/install-profile', { profile: name, reset_labs: reset }));
                    const names = (MD.data && MD.data.profile_names) || {};
                    POps.toast('success', [POps.t('{profile} profili uygulandı.', { profile: POps.t(names[name] || name) }), effectDone(r)].filter(Boolean).join(' '));
                    POps.drawer.close();
                } catch (e) { POps.toast('error', POps.errorMessage(e)); }
                await loadModules();
            }
        });
        POps.drawer.body().innerHTML = drawerHeadHtml(POps.t('Kurulum profili'), '', 'sliders', '') + '<div class="loading-state" role="status"><span class="spinner"></span>' + POps.tHtml('Yükleniyor…') + '</div>';
        renderProfDrawer(name, false);
    }
    if ($('pfSet')) {
        $('pfSet').addEventListener('click', (e) => { const r = e.target.closest('[data-prof]'); if (r) openProfile(r.dataset.prof); });
        $('pfSet').addEventListener('keydown', (e) => { const r = e.target.closest('[data-prof]'); if (r && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openProfile(r.dataset.prof); } });
    }

    // =================================================================
    // ENTEGRASYONLAR: GLPI'ye dışa aktarım (GET/POST /api/system/glpi, /test, /sync). Jetonlar sunucudan hiç gelmez:
    // kayıtlıysa alan boş kalır ve "kayıtlı" yazar; yalnızca yazılırsa gönderilir.
    // =================================================================
    const GL = { d: null, poll: null };
    const glSaved = (d) => ({ enabled: !!d.enabled, url: d.url || '', entity: Number(d.entity) || 0, interval_hours: Number(d.interval_hours) || 0,
        sync: Object.assign({}, d.sync), tickets_since: d.tickets_since || null });
    function glBody() {
        const b = {
            enabled: $('glEnabled').checked, url: $('glUrl').value.trim(), entity: Math.max(0, parseInt($('glEntity').value, 10) || 0),
            interval_hours: parseInt($('glInterval').value, 10) || 0,
            sync: { computers: $('glComp').checked, software: $('glSoft').checked, tickets: $('glTick').checked, ticket_reporter: $('glRep').checked },
            tickets_since: $('glSince').value || null
        };
        if ($('glApp').value) b.app_token = $('glApp').value;
        if ($('glUser').value) b.user_token = $('glUser').value;
        return b;
    }
    function glDirty() {
        if (!GL.d) return;
        const b = glBody(), s = glSaved(GL.d);
        const dirty = !!(b.app_token || b.user_token) || JSON.stringify([b.enabled, b.url, b.entity, b.interval_hours, b.sync, b.tickets_since])
            !== JSON.stringify([s.enabled, s.url, s.entity, s.interval_hours, s.sync, s.tickets_since]);
        $('glSave').className = dirty ? 'btn' : 'btn secondary';
        $('glSoft').disabled = !$('glComp').checked;
        $('glRep').disabled = !$('glTick').checked;
    }
    function glStateWord(d) {
        const r = d.last_run || {};
        if (d.running) return ['run', POps.t('Eşitleniyor')];
        if (!d.enabled) return ['off', POps.tx('Kapalı', 'switch')];
        if (!r.at) return ['off', POps.t('Henüz eşitlenmedi')];
        return r.ok ? ['ok', POps.t('Açık')] : ['bad', POps.t('Son eşitleme başarısız')];
    }
    function glCountsText(n) {
        const parts = [];
        const add = (k, text) => { if (n[k]) parts.push(POps.tn(text, n[k])); };
        add('computers_created', '{n} bilgisayar oluşturuldu'); add('computers_linked', '{n} bilgisayar bağlandı');
        add('computers_updated', '{n} bilgisayar güncellendi'); add('software_added', '{n} yazılım bağlantısı eklendi');
        add('software_removed', '{n} yazılım bağlantısı kaldırıldı'); add('tickets_created', '{n} talep gönderildi');
        add('followups_created', '{n} yanıt gönderildi'); add('deferred', '{n} kayıt süre dolduğu için sonraki tura kaldı');
        return parts.length ? parts.join(' · ') : POps.t('Gönderilecek değişiklik yoktu.');
    }
    function renderGlpi(d, keepForm) {
        GL.d = d;
        if (!keepForm) {
            $('glEnabled').checked = !!d.enabled;
            $('glUrl').value = d.url || '';
            $('glEntity').value = Number(d.entity) || 0;
            $('glInterval').value = String(d.interval_hours);
            $('glComp').checked = !!d.sync.computers; $('glSoft').checked = !!d.sync.software;
            $('glTick').checked = !!d.sync.tickets; $('glRep').checked = !!d.sync.ticket_reporter;
            $('glSince').value = d.tickets_since || '';
            $('glApp').value = ''; $('glUser').value = '';
        }
        $('glApp').placeholder = d.app_token_set ? POps.t('Kayıtlı · değiştirmek için yazın') : POps.t('Gerekmiyorsa boş');
        $('glUser').placeholder = d.user_token_set ? POps.t('Kayıtlı · değiştirmek için yazın') : '';
        const [k, w] = glStateWord(d);
        setState('glState', k, w);
        glDirty();
        const r = d.last_run || {};
        let lastHtml;
        if (d.running) {
            lastHtml = `<div class="srow"><span class="res-spin"><span class="spinner"></span></span><div class="grow"><div class="t">${POps.tHtml('Eşitleniyor')}</div>`
                + `<div class="d">${POps.tHtml('Yalnızca değişenler gönderilir; büyük bir ilk eşitleme birkaç dakika sürebilir.')}</div></div></div>`;
        } else if (r.at) {
            const how = r.trigger === 'manual' ? POps.t('elle') : POps.t('zamanlanmış');
            const metaHtml = POps.timeHtml(r.at) + ' · ' + escapeHtml(how) + (typeof r.duration === 'number' ? ' · ' + escapeHtml(POps.t('{s} sn', { s: r.duration })) : '');
            const errsHtml = (r.errors || []).length ? `<div class="why warn">${escapeHtml(POps.tn('{n} kayıt gönderilemedi', (r.counts || {}).item_errors || r.errors.length) + ': ' + r.errors.join(' · '))}</div>` : '';
            lastHtml = `<div class="act"><div class="res ${r.ok ? 'ok' : 'bad'}">${POps.iconHtml(r.ok ? 'check' : 'x')}</div><div style="min-width:0"><div class="what">${POps.tHtml('Son eşitleme')}</div>`
                + `<div class="meta">${metaHtml}</div>`
                + (r.ok ? `<div class="meta">${escapeHtml(glCountsText(r.counts || {}))}</div>` : `<div class="why">${escapeHtml(POps.t(r.error || 'Bilinmeyen hata'))}</div>`)
                + `${errsHtml}</div><div class="side">${wordHtml(r.ok ? 'ok' : 'bad', r.ok ? POps.t('Başarılı') : POps.t('Başarısız'))}</div></div>`;
        } else {
            lastHtml = `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Henüz eşitlenmedi')}</div><div class="d">${POps.tHtml('Ayarları kaydedip bağlantıyı sınayın, sonra ilk eşitlemeyi başlatın.')}</div></div></div>`;
        }
        const links = d.links || {};
        const locN = Object.keys(d.locations || {}).length;
        const probs = d.problems || [];
        $('glRun').innerHTML = lastHtml
            + `<div class="srow"><div class="grow"><div class="t">${POps.tHtml('Şimdi eşitle')}</div><div class="d">${escapeHtml(POps.t('GLPI\'de bağlı: {c} bilgisayar, {t} talep.', { c: Number(links.computers) || 0, t: Number(links.tickets) || 0 }))}</div></div>`
            + `<button type="button" class="btn secondary" data-act="sync" ${d.running || !d.enabled ? 'disabled' : ''}>${POps.iconHtml('refresh', 'sm')}${POps.tHtml('Şimdi eşitle')}</button></div>`
            + `<div class="srow click" data-act="loc" role="button" tabindex="0"><div class="grow"><div class="t">${POps.tHtml('Sınıf → GLPI konumu')}</div>`
            + `<div class="d">${escapeHtml(locN ? POps.tn('{n} sınıf eşlendi; eşlenmemiş sınıfın konumuna dokunulmaz.', locN) : POps.t('Eşleme yok: bilgisayarların GLPI konumuna dokunulmaz.'))}</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>`
            + (probs.length ? `<div class="srow click" data-act="probs" role="button" tabindex="0"><div class="grow"><div class="t">${escapeHtml(POps.tn('{n} bilgisayar eşleşmedi', probs.length))}${wordHtml('warn', POps.t('Bakılmalı'))}</div>`
                + `<div class="d">${POps.tHtml('GLPI\'de birden çok eşleşen ya da GLPI\'den silinmiş bilgisayarlar.')}</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>` : '');
        if (POps.drawer.isOpen('sysglp:list')) renderGlProblems();
    }
    async function loadGlpi(quiet) {
        if (!IS_SUPER || !$('glSet')) return;
        let d;
        try { d = await POps.get('/api/system/glpi'); }
        catch (e) { if (!quiet) { setState('glState', 'off', POps.t('Okunamadı')); sectionError($('glRun'), e); } return; }
        renderGlpi(d, !!GL.d);
        if (d.running) glWatch();
    }
    function glWatch() {
        if (GL.poll) return;
        GL.poll = setInterval(async () => {
            let d;
            try { d = await POps.get('/api/system/glpi'); } catch (e) { return; }
            if (!d.running) {
                clearInterval(GL.poll); GL.poll = null;
                const r = d.last_run || {};
                POps.toast(r.ok ? 'success' : 'error', r.ok ? POps.t('GLPI eşitlemesi bitti: {what}', { what: glCountsText(r.counts || {}) }) : POps.t('GLPI eşitlemesi başarısız: {error}', { error: POps.t(r.error || '') }));
            }
            renderGlpi(d, true);
        }, 2500);
    }
    function renderGlProblems() {
        const body = POps.drawer.body();
        const probs = (GL.d && GL.d.problems) || [];
        const rowsHtml = probs.map(p => `<div class="act"><div class="res warn">${POps.iconHtml('alert')}</div><div style="min-width:0"><div class="what">${escapeHtml(p.display_name || p.hostname || p.pc_name)}</div>`
            + `<div class="meta">${escapeHtml(p.pc_name)}${p.glpi_id ? ' · GLPI #' + Number(p.glpi_id) : ''}</div><div class="why warn">${escapeHtml(p.error || '')}</div></div>`
            + `<div class="side"><button type="button" class="btn secondary sm" data-act="forget" data-pc="${escapeHtml(p.pc_name)}">${POps.tHtml('Bağlantıyı unut')}</button></div></div>`).join('');
        body.innerHTML = drawerHeadHtml(POps.t('Eşleşmeyen bilgisayarlar'), POps.tHtml('GLPI ile eşleşme sorunları'), 'alert', '')
            + `<p class="dr-sum">${POps.tHtml('Birden çok eşleşmede POps tahmin yapmaz: GLPI\'de kopyaları birleştirin ya da silin. GLPI\'den silinmiş bir bilgisayar kendiliğinden yeniden oluşturulmaz. "Bağlantıyı unut" sonraki eşitlemede cihazı yeniden aratır.')}</p>`
            + (rowsHtml ? `<div class="set" style="padding:0 12px">${rowsHtml}</div>` : `<div class="dr-note">${POps.tHtml('Sorun yok.')}</div>`);
    }
    function renderGlLocations() {
        const body = POps.drawer.body();
        const map = (GL.d && GL.d.locations) || {};
        const labs = dev.labs();
        const rowsHtml = labs.map(l => `<div class="srow"><div class="grow"><div class="t">${escapeHtml(l)}</div></div>`
            + `<input type="number" min="1" step="1" data-lab="${escapeHtml(l)}" value="${map[l] ? Number(map[l]) : ''}" placeholder="—" aria-label="${escapeHtml(POps.t('{lab} için GLPI konum kimliği', { lab: l }))}"></div>`).join('');
        body.innerHTML = drawerHeadHtml(POps.t('Sınıf → GLPI konumu'), POps.tHtml('GLPI\'deki konumun kimliği'), 'labs', '')
            + `<p class="dr-sum">${POps.tHtml('Eşlenen sınıftaki bilgisayarların konumu GLPI\'de bu konum olur. Eşlenmemiş sınıfın konumuna dokunulmaz; GLPI\'de konum oluşturulmaz.')}</p>`
            + (labs.length ? `<div class="set gl-loc">${rowsHtml}</div>` : `<div class="dr-note">${POps.tHtml('Henüz sınıf yok.')}</div>`)
            + `<div class="dr-actions"><button type="button" class="btn" data-act="locsave">${POps.tHtml('Kaydet')}</button><button type="button" class="btn secondary" data-act="close">${POps.tHtml('Vazgeç')}</button></div>`;
    }
    async function glSaveLocations(btn) {
        const locations = {};
        POps.drawer.body().querySelectorAll('input[data-lab]').forEach(i => { const n = parseInt(i.value, 10); if (n > 0) locations[i.dataset.lab] = n; });
        try {
            renderGlpi(await POps.busy(btn, () => POps.post('/api/system/glpi', Object.assign(glSaved(GL.d), { locations }))), true);
            POps.toast('success', POps.t('Konum eşlemesi kaydedildi.'));
            POps.drawer.close();
        } catch (e) { POps.toast('error', POps.errorMessage(e)); }
    }
    if ($('glSet')) {
        $('glSet').addEventListener('input', glDirty);
        $('glSet').addEventListener('change', glDirty);
        $('glSave').addEventListener('click', async function () {
            try {
                renderGlpi(await POps.busy(this, () => POps.post('/api/system/glpi', glBody())));
                POps.toast('success', POps.t('GLPI ayarları kaydedildi.'));
            } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
        $('glTest').addEventListener('click', async function () {
            const b = glBody(), t = { url: b.url };
            if (b.app_token) t.app_token = b.app_token;
            if (b.user_token) t.user_token = b.user_token;
            try {
                const r = await POps.busy(this, () => POps.post('/api/system/glpi/test', t));
                if (r.ok) POps.toast('success', POps.t('GLPI\'ye bağlanıldı: {user} · {entity}', { user: r.user || '?', entity: r.entity || '?' }) + (r.plain_http ? ' ' + POps.t('Bağlantı şifresiz (http).') : ''));
                else POps.toast('error', POps.t('GLPI\'ye bağlanılamadı: {error}', { error: POps.t(r.error || '') }));
            } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
        $('glRun').addEventListener('click', async (e) => {
            const b = e.target.closest('[data-act]');
            if (!b || b.disabled) return;
            if (b.dataset.act === 'sync') {
                try {
                    const r = await POps.busy(b, () => POps.post('/api/system/glpi/sync', {}));
                    if (!r.started) POps.toast('info', POps.t('Bir eşitleme zaten sürüyor.'));
                    GL.d.running = true; renderGlpi(GL.d, true); glWatch();
                } catch (err) { POps.toast('error', POps.errorMessage(err)); }
            } else if (b.dataset.act === 'loc') {
                openDrawer('sysgll:map', (x) => { if (x.dataset.act === 'locsave') glSaveLocations(x); });
                renderGlLocations();
            } else if (b.dataset.act === 'probs') {
                openDrawer('sysglp:list', async (x) => {
                    if (x.dataset.act !== 'forget') return;
                    if (await POps.act(x, () => POps.del('/api/system/glpi/links/' + encodeURIComponent(x.dataset.pc)), { success: POps.t('Bağlantı unutuldu; sonraki eşitlemede yeniden aranır.') })) loadGlpi(true);
                });
                renderGlProblems();
            }
        });
        $('glRun').addEventListener('keydown', (e) => {
            const r = e.target.closest('.srow.click[data-act]');
            if (r && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); r.click(); }
        });
    }

    // =================================================================
    // ÖZET + YÜKLEME
    // =================================================================
    function renderSummary() {
        if (!S.ver) return;
        const I = serverInfo();
        const A = agentInfo();
        const boldHtml = (x) => `<b>${Number(x)}</b>`;
        let html = `<span class="sum"><span class="dot ${escapeHtml(I.k)}"></span>${POps.tHtml('Sunucu {version}', null, { version: `<b>${escapeHtml(fmtV(S.ver.running))}</b>` })}</span>`
            + `<span class="sum">${POps.tnHtml('{n} ajan', A.all.length, null, { n: boldHtml(A.all.length) })}</span>`
            + (A.old.length ? `<span class="sum"><span class="dot run"></span>${POps.tnHtml('{n} eski ajan', A.old.length, null, { n: boldHtml(A.old.length) })}</span>` : '');
        if (S.diag) {
            const n = healthIssues(S.diag).length + (backupState(S.diag.backup) === 'ok' ? 0 : 1);
            html += n ? `<span class="sum"><span class="dot warn"></span>${POps.tnHtml('{n} sorun', n, null, { n: boldHtml(n) })}</span>` : '<span class="sum"><span class="dot ok"></span>' + POps.tHtml('Sağlıklı') + '</span>';
        }
        const au = store.get(AKEY);
        if (au && !au.ok) html += '<span class="sum"><span class="dot bad"></span>' + POps.tHtml('Denetim zinciri kırık') + '</span>';
        $('sysSummary').innerHTML = html;
    }

    async function loadAll(check) {
        const [ver] = await Promise.all([
            POps.get('/api/system/version' + (check ? '?check=true' : '')).catch(() => null),
            loadSelfUpdate()
        ]);
        if (ver) S.ver = ver;
        if (!S.ver) {
            const err = new Error(POps.t('Sürüm bilgisi alınamadı. Sayfayı yenileyin.'));
            renderServer();
            ['agSet', 'enSet'].forEach(id => { if ($(id)) POps.setError($(id), err, { compact: true }); });
            $('sysSummary').innerHTML = '<span class="sum"><span class="dot bad"></span>' + POps.tHtml('Sürüm bilgisi alınamadı') + '</span>';
            return;
        }
        if (S.su && (S.su.pending || (S.su.status && S.su.status.state === 'running')) && !S.suPoll) { S.suBusy = true; S.suBefore = ''; watchSelfUpdate(); }
        S.checkedAt = new Date();
        $('checkBtn').dataset.tip = POps.t('Güncellemeleri denetle · son {time}', { time: S.checkedAt.toLocaleTimeString(POps.locale, { hour: '2-digit', minute: '2-digit' }) });
        renderServer();
        renderAgents(true);
        renderEnroll();
        renderSummary();
        if (check) S.notes = null;
    }

    $('checkBtn').addEventListener('click', async function () {
        await POps.busy(this, () => Promise.all([loadAll(true), loadDiag()]));
        POps.toast('info', POps.t('Güncellemeler denetlendi.'));
    });

    document.addEventListener('pops_data_updated', () => {
        renderAgents(false); renderCaps(false); renderSummary();
        if (capHost && POps.drawer.isOpen('syscap:' + capHost)) renderCapDrawer(capHost, true);
        if (POps.drawer.isOpen('syst:target')) { /* seçim panelindeki liste kendi tazelenir (yazarken bozulmasın) */ }
    });

    POps.watchDevices();
    ovReady = true;
    if (sysTabs && sysTabs.current() === 'overview') { renderOverview(); loadOverview(); }
    if (sysTabs) popsPoll(() => (sysTabs.current() === 'overview' ? loadOverview() : null), 60000);
    loadAll(false);
    loadDiag();
    loadTokens();
    loadNotify();
    loadRetention();
    loadModules();
    loadGlpi();
    renderAudit();
    if (rollout) pollRollout();
    if (state.devicesLoaded) { renderCaps(true); }
    popsTwofaNudge();
})();
</script>

<?php include 'includes/footer.php'; ?>
