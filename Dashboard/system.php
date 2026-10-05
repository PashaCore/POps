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
    .ch-b:empty::before { content: 'Yükleniyor…'; position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; font-size: var(--text-sm); color: var(--text-muted); }
    .ch-x { display: flex; justify-content: space-between; gap: 8px; margin-top: 6px; font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
    .ov { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; text-align: left; padding: 16px 18px; background: var(--bg-surface); border-radius: 16px; box-shadow: 0 0 0 1px var(--border-subtle), 0 1px 2px rgba(0, 0, 0, 0.03); min-height: 112px; transition: box-shadow 0.12s; }
    .ov:hover { box-shadow: 0 0 0 1px var(--border-default), 0 4px 14px rgba(0, 0, 0, 0.06); }
    .ov:focus-visible { outline: none; box-shadow: var(--focus-ring); }
    .ov-t { font-weight: var(--fw-semibold); font-size: var(--text-md); }
    .ov-s { display: inline-flex; align-items: center; gap: 7px; font-size: var(--text-sm); color: var(--text-primary); min-height: 20px; }
    .ov-s:empty::before { content: 'Yükleniyor…'; color: var(--text-muted); }
    .ov-d { font-size: var(--text-xs); color: var(--text-muted); margin-top: auto; }
    .file-list li { display: flex; gap: 8px; padding: 3px 0; overflow-wrap: anywhere; }
    .file-list li span { color: var(--text-muted); white-space: nowrap; }
</style>

<div class="page-header">
    <div>
        <h1>Sistem</h1>
        <div class="summary" id="sysSummary"><span class="sum">Yükleniyor…</span></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="notesBtn" data-tip="Sürüm notları" aria-label="Sürüm notları"><?php echo pops_icon('file'); ?></button>
        <button type="button" class="ibtn boxed" id="checkBtn" data-tip="Güncellemeleri denetle" data-tip-pos="left" aria-label="Güncellemeleri denetle"><?php echo pops_icon('refresh'); ?></button>
    </div>
</div>

<?php if ($sysSuper): ?>
<div class="tabs sys-tabs" id="sysTabs" aria-label="Sistem bölümleri">
    <button type="button" class="tab" data-tab="overview">Genel bakış</button>
    <button type="button" class="tab" data-tab="updates">Güncellemeler</button>
    <button type="button" class="tab" data-tab="security">Güvenlik</button>
    <button type="button" class="tab" data-tab="health">Sağlık ve yedek</button>
    <button type="button" class="tab" data-tab="notify">Bildirimler ve saklama</button>
</div>
<?php endif; ?>

<div class="sys-wrap">
    <?php if ($sysSuper): ?>
    <div class="sys-pane" data-pane="overview">
        <div class="ov-grid" id="ovGrid">
            <button type="button" class="ov" data-go="updates" data-src="srvState"><span class="ov-t">Sunucu</span><span class="ov-s"></span><span class="ov-d">Sürüm ve sunucu güncellemesi</span></button>
            <button type="button" class="ov" data-go="updates" data-src="agState"><span class="ov-t">Ajanlar</span><span class="ov-s"></span><span class="ov-d">Sürüm dağılımı, ajan paketi ve gönderim</span></button>
            <button type="button" class="ov" data-go="health" data-src="hlState"><span class="ov-t">Sağlık</span><span class="ov-s"></span><span class="ov-d">Bağlı ajanlar, veritabanı, hatalar, disk</span></button>
            <button type="button" class="ov" data-go="health" data-src="bkState"><span class="ov-t">Yedekler</span><span class="ov-s"></span><span class="ov-d">Gece alınan veritabanı yedeği</span></button>
            <button type="button" class="ov" data-go="security" data-src="enState"><span class="ov-t">Ajan kaydı ve kimlik</span><span class="ov-s"></span><span class="ov-d">Kimlik zorlaması ve kayıt jetonları</span></button>
            <button type="button" class="ov" data-go="security" data-src="capState"><span class="ov-t">Cihaz yetenekleri</span><span class="ov-s"></span><span class="ov-d">Uzak komut ve uzak ekranın kapatıldığı bilgisayarlar</span></button>
            <button type="button" class="ov" data-go="security" data-src="auState"><span class="ov-t">Kayıt bütünlüğü</span><span class="ov-s"></span><span class="ov-d">Denetim zincirinin doğrulanması</span></button>
            <button type="button" class="ov" data-go="notify" data-src="ntState"><span class="ov-t">Bildirimler</span><span class="ov-s"></span><span class="ov-d">E-posta ve webhook, saklama süreleri</span></button>
        </div>
        <div class="ov-trend">
            <div class="ov-trend-h">
                <h2>Eğilimler</h2>
                <div class="segmented" id="ovSpan" role="group" aria-label="Zaman aralığı">
                    <button type="button" data-span="24h">24 saat</button>
                    <button type="button" data-span="7d">7 gün</button>
                    <button type="button" data-span="30d">30 gün</button>
                </div>
            </div>
            <div class="ch-grid" id="chGrid">
            <section class="ch" data-ch="agents" aria-label="Bağlı ajanlar"><div class="ch-h"><span class="ch-t">Bağlı ajanlar</span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="load" aria-label="İşlemci ve bellek"><div class="ch-h"><span class="ch-t">İşlemci ve bellek</span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="requests" aria-label="API istekleri"><div class="ch-h"><span class="ch-t">API istekleri</span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="tasks" aria-label="İşlemler"><div class="ch-h"><span class="ch-t">İşlemler</span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="events" aria-label="Olaylar"><div class="ch-h"><span class="ch-t">Olaylar</span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b"></div><div class="ch-x"></div></section>
            <section class="ch" data-ch="storage" aria-label="Veritabanı ve disk"><div class="ch-h"><span class="ch-t">Veritabanı ve disk</span><span class="ch-n"></span></div><div class="ch-d"></div><div class="ch-b"></div><div class="ch-x"></div></section>
            </div>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="updates">
        <div class="sys-col">
<section class="sec" id="secServer" aria-labelledby="hServer">
                <div class="sec-h"><h2 id="hServer">Sunucu</h2><span class="st" id="srvState"></span></div>
                <div class="set" id="srvSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secAgents" aria-labelledby="hAgents">
                <div class="sec-h"><h2 id="hAgents">Ajanlar</h2><span class="st" id="agState"></span></div>
                <div class="set" id="agSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
                <div class="set" id="rollout" hidden></div>
                <?php if (!$sysSuper): ?><div class="set-note">Güncelleme, sağlık, yedek, kayıt ve güvenlik ayarları yalnızca süper admin içindir.</div><?php endif; ?>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="security">
        <div class="sys-col">
<section class="sec" id="secEnroll" aria-labelledby="hEnroll">
                <div class="sec-h"><h2 id="hEnroll">Ajan kaydı ve kimlik</h2><span class="st" id="enState"></span></div>
                <div class="set" id="enSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
                <div class="set" id="tokSet" hidden></div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secCaps" aria-labelledby="hCaps">
                <div class="sec-h"><h2 id="hCaps">Cihaz yetenekleri</h2><span class="st" id="capState"></span></div>
                <div class="set" id="capSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
            </section>
<section class="sec" id="secAudit" aria-labelledby="hAudit">
                <div class="sec-h"><h2 id="hAudit">Kayıt bütünlüğü</h2><span class="st" id="auState"></span></div>
                <div class="set" id="auSet"></div>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="health">
        <div class="sys-col">
<section class="sec" id="secHealth" aria-labelledby="hHealth">
                <div class="sec-h"><h2 id="hHealth">Sağlık</h2><span class="st" id="hlState"></span></div>
                <div id="hlBody"><div class="set"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div></div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secBackup" aria-labelledby="hBackup">
                <div class="sec-h"><h2 id="hBackup">Yedekler</h2><span class="st" id="bkState"></span></div>
                <div class="set" id="bkSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
            </section>
        </div>
    </div>
    <div class="sys-pane sys-grid" data-pane="notify">
        <div class="sys-col">
<section class="sec" id="secNotify" aria-labelledby="hNotify">
                <div class="sec-h"><h2 id="hNotify">Bildirimler</h2><span class="st" id="ntState"></span></div>
                <div class="set" id="ntSet">
                    <div class="srow">
                        <div class="grow"><div class="t">Dışarıya gönder</div><div class="d">Önemli olaylar Bildirimler'de her zaman görünür; açıkken e-posta ve webhook ile de gönderilir. Aynı olay 10 dakikada bir kez gider.</div></div>
                        <label class="switch"><input type="checkbox" id="ntEnabled" aria-label="Bildirimleri dışarıya gönder"><span></span></label>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t">En az önem</div><div class="d">Bu ve daha önemli olaylar gönderilir.</div></div>
                        <select id="ntSev" aria-label="En az önem" style="width:auto">
                            <option value="critical">Kritik</option>
                            <option value="high">Yüksek</option>
                            <option value="medium">Orta</option>
                            <option value="info">Bilgi (her şey)</option>
                        </select>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t">E-posta alıcıları</div><div class="d" id="ntSmtp">Virgülle ayırın.</div></div>
                        <input type="text" id="ntEmail" class="wide" placeholder="ornek@okul.k12.tr" aria-label="E-posta alıcıları" autocomplete="off">
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t">Webhook</div><div class="d">Slack, Discord, Teams ya da kendi sisteminiz.</div></div>
                        <input type="url" id="ntWebhook" class="wide" placeholder="https://…" aria-label="Webhook adresi" autocomplete="off">
                    </div>
                    <div class="srow">
                        <div class="grow"></div>
                        <div class="acts">
                            <button type="button" class="btn secondary" id="ntTest"><?php echo pops_icon('send', 'sm'); ?>Test gönder</button>
                            <button type="button" class="btn secondary" id="ntSave">Kaydet</button>
                        </div>
                    </div>
                </div>
            </section>
        </div>
        <div class="sys-col">
<section class="sec" id="secRetention" aria-labelledby="hRetention">
                <div class="sec-h"><h2 id="hRetention">Saklama süreleri</h2></div>
                <div class="set" id="rtSet">
                    <div class="srow">
                        <div class="grow"><div class="t">Ajan olay kayıtları</div><div class="d">Oturum açma, kural ihlali ve benzeri olaylar.</div></div>
                        <span class="num"><input type="number" id="rtLogs" min="0" max="3650" step="1" aria-label="Ajan olay kayıtları, gün"><span class="unit">gün</span></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t">Sonuçlanmış görevler</div><div class="d">Bitmiş görevler ve çıktıları; sıradakilere dokunulmaz.</div></div>
                        <span class="num"><input type="number" id="rtTasks" min="0" max="3650" step="1" aria-label="Sonuçlanmış görevler, gün"><span class="unit">gün</span></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="t">Okunmuş bildirimler</div></div>
                        <span class="num"><input type="number" id="rtNotif" min="0" max="3650" step="1" aria-label="Okunmuş bildirimler, gün"><span class="unit">gün</span></span>
                    </div>
                    <div class="srow">
                        <div class="grow"><div class="d">0 süresiz saklar. Eski kayıtlar her gece silinir. Denetim zinciri ve uzak ekran oturumları silinmez.</div></div>
                        <button type="button" class="btn secondary" id="rtSave" disabled>Kaydet</button>
                    </div>
                </div>
            </section>
        </div>
    </div>
    <?php else: ?>
    <div class="sys-grid one">
        <div class="sys-col">
<section class="sec" id="secServer" aria-labelledby="hServer">
                <div class="sec-h"><h2 id="hServer">Sunucu</h2><span class="st" id="srvState"></span></div>
                <div class="set" id="srvSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
            </section>
<section class="sec" id="secAgents" aria-labelledby="hAgents">
                <div class="sec-h"><h2 id="hAgents">Ajanlar</h2><span class="st" id="agState"></span></div>
                <div class="set" id="agSet"><div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div></div>
                <div class="set" id="rollout" hidden></div>
                <?php if (!$sysSuper): ?><div class="set-note">Güncelleme, sağlık, yedek, kayıt ve güvenlik ayarları yalnızca süper admin içindir.</div><?php endif; ?>
            </section>
        </div>
    </div>
    <?php endif; ?>
</div>

<?php if ($sysSuper): ?>
<div id="uploadModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title">Ajan paketini elle yükle</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <p class="card-desc">İnternetsiz sunucu için. GitHub sürüm sayfasından <code>manifest.json</code>, <code>manifest.json.sig</code> ve <code>POps-Agent-…-win-x64.msi</code> dosyalarını indirip birlikte seçin. İmza GitHub'dan indirmedeki gibi doğrulanır.</p>
            <div class="upload-zone" id="uz">
                <?php echo pops_icon('upload'); ?>
                <div style="margin-top:6px">Dosyaları seçin ya da buraya bırakın</div>
                <input type="file" id="upFiles" multiple aria-label="Paket dosyaları">
            </div>
            <ul class="file-list" id="upList"></ul>
            <label class="check" style="margin-top:12px"><input type="checkbox" id="upForce"> Aynı ya da daha eski sürümü de kabul et</label>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="upBtn" disabled>Doğrula ve yükle</button>
        </div>
    </div>
</div>

<div id="tokenModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title">Kayıt jetonu üret</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <p class="card-desc">Yeni kurulumda MSI'a <code>ENROLL_TOKEN</code> olarak verilir; ajan ilk bağlanışta kendine özel bir anahtar alır. Çok kullanımlık jeton bir sınıfa tek MSI ile toplu kurulum içindir.</p>
            <div class="form-grid">
                <div class="field"><label for="tkLab">Sınıf</label><input type="text" id="tkLab" list="tkLabs" placeholder="Bütün sınıflar" autocomplete="off"><datalist id="tkLabs"></datalist><div class="field-hint">Boş bırakılırsa bilgisayar atanmamış olarak gelir.</div></div>
                <div class="field"><label for="tkNote">Not</label><input type="text" id="tkNote" maxlength="200" placeholder="İsteğe bağlı" autocomplete="off"></div>
                <div class="field"><label for="tkUses">Kullanım sayısı</label><input type="number" id="tkUses" min="1" max="10000" value="1"></div>
                <div class="field"><label for="tkTtl">Geçerlilik (saat)</label><input type="number" id="tkTtl" min="1" max="720" value="72"></div>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="tkCreate">Jeton üret</button>
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
    const nf = (n, d) => Number(n || 0).toLocaleString('tr-TR', { maximumFractionDigits: d || 0 });
    const pct = (v) => (typeof v === 'number' ? '%' + nf(v) : '—');
    const mb = (v) => (typeof v !== 'number' ? '—' : v >= 1024 ? nf(v / 1024, 1) + ' GB' : nf(v, v < 10 ? 1 : 0) + ' MB');
    const gb = (bytes) => nf(bytes / 1073741824, bytes < 10737418240 ? 1 : 0) + ' GB';
    const nums = (pts, k) => pts.map(p => p[k]).filter(v => typeof v === 'number');
    const total = (pts, k) => pts.reduce((a, p) => a + (typeof p[k] === 'number' ? p[k] : 0), 0);
    function when(t, len, kind) {
        // kind: 'axis' (eksen), 'tip' (ipucu: aralığın başı ve sonu)
        const a = new Date(t * 1000), b = new Date((t + len) * 1000);
        const hm = (x) => x.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' });
        const day = (x) => x.toLocaleDateString('tr-TR', { day: 'numeric', month: 'short' });
        if (kind === 'axis') return ovSpan === '24h' ? hm(a) : day(a);
        if (len >= 86400) return a.toLocaleDateString('tr-TR', { day: 'numeric', month: 'long', weekday: 'long' });
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
            if (ovErr) document.querySelectorAll('#chGrid .ch-b').forEach(b => b.replaceChildren(POps.el('div', { className: 'ch-empty', text: 'Grafik verisi alınamadı: ' + POps.errorMessage(ovErr) })));
            return;
        }
        const pts = d.series || [], step = d.step, bar = d.bar;
        const WAIT = 'Ölçümler toplanıyor. Sunucu dakikada bir ölçüm alır; grafik birkaç dakika içinde dolmaya başlar.';
        const tipP = (p, text) => when(p.t, step, 'tip') + '\n' + text;
        const late = d.latest || {};

        // Bağlı ajanlar
        let c = card('agents');
        const ag = nums(pts, 'agents');
        numTo(c.n, nf(d.agents_connected), '/ ' + nf((d.devices || {}).total));
        const up = d.updates || {}, upN = (up.success || 0) + (up.rolled_back || 0) + (up.failed || 0);
        keysTo(c.d, [
            ag.length ? { name: 'en çok', value: nf(Math.max(...ag)) } : null,
            ag.length ? { name: 'en az', value: nf(Math.min(...ag)) } : null,
            upN ? { name: 'ajan güncellemesi', value: nf(up.success) + ' başarılı' + (upN - up.success ? ' · ' + nf(upN - up.success) + ' sorunlu' : '') } : null
        ]);
        chartTo(c, 'line', pts, { series: [{ key: 'agents', cls: 'ch-c1' }], area: true,
            tip: p => tipP(p, typeof p.agents === 'number' ? nf(p.agents) + ' ajan bağlı' : 'Ölçüm yok') }, nf, WAIT);

        // İşlemci ve bellek
        c = card('load');
        numTo(c.n, pct(late.cpu_pct), 'işlemci');
        keysTo(c.d, [{ name: 'İşlemci', value: pct(late.cpu_pct), cls: 'ch-c1' }, { name: 'Bellek', value: pct(late.mem_pct), cls: 'ch-c2' },
            nums(pts, 'cpu').length ? { name: 'en yüksek işlemci', value: pct(Math.max(...nums(pts, 'cpu'))) } : null]);
        chartTo(c, 'line', pts, { series: [{ key: 'cpu', cls: 'ch-c1' }, { key: 'mem', cls: 'ch-c2' }], max: 100,
            tip: p => tipP(p, typeof p.cpu === 'number' || typeof p.mem === 'number' ? 'İşlemci ' + pct(p.cpu) + ' · Bellek ' + pct(p.mem) : 'Ölçüm yok') }, pct, WAIT);

        // API istekleri (5xx kırmızı)
        c = card('requests');
        const rq = pts.map(p => ({ t: p.t, ok: typeof p.requests === 'number' ? Math.max(0, p.requests - (p.errors || 0)) : null, errors: p.errors, requests: p.requests }));
        const rqN = total(pts, 'requests'), erN = total(pts, 'errors');
        numTo(c.n, nf(rqN), 'istek');
        keysTo(c.d, [{ name: 'Başarılı', value: nf(rqN - erN), cls: 'ch-c1' }, { name: 'Sunucu hatası (5xx)', value: nf(erN), cls: 'ch-c4' }]);
        chartTo(c, 'bars', rq, { series: [{ key: 'ok', cls: 'ch-c1' }, { key: 'errors', cls: 'ch-c4' }],
            tip: p => tipP(p, typeof p.requests === 'number' ? nf(p.requests) + ' istek' + (p.errors ? ' · ' + nf(p.errors) + ' hata' : '') : 'Ölçüm yok') }, nf, WAIT);

        // İşlemler (görev sonuçları)
        c = card('tasks');
        const tk = d.tasks || [];
        const TK = [['ok', 'Başarılı', 'ch-c3'], ['failed', 'Başarısız', 'ch-c4'], ['denied', 'Reddedildi', 'ch-c5'], ['other', 'Diğer', 'ch-c6']];
        const tkN = TK.reduce((a, [k]) => a + total(tk, k), 0);
        numTo(c.n, nf(tkN), 'işlem');
        keysTo(c.d, tkN ? TK.filter(([k]) => total(tk, k)).map(([k, name, cls]) => ({ name, value: nf(total(tk, k)), cls })) : [{ name: 'Bu aralıkta işlem yok' }]);
        chartTo(c, 'bars', tk, { series: TK.map(([key, , cls]) => ({ key, cls })),
            tip: p => when(p.t, bar, 'tip') + '\n' + (TK.filter(([k]) => p[k]).map(([k, name]) => name + ' ' + nf(p[k])).join(' · ') || 'İşlem yok') }, nf);

        // Olaylar (risk düzeyine göre)
        c = card('events');
        const ev = d.events || [];
        const EV = [['high', 'Yüksek ve kritik', 'ch-c4'], ['medium', 'Orta', 'ch-c5'], ['info', 'Bilgi', 'ch-c6']];
        const evN = EV.reduce((a, [k]) => a + total(ev, k), 0);
        numTo(c.n, nf(evN), 'olay');
        keysTo(c.d, evN ? EV.map(([k, name, cls]) => ({ name, value: nf(total(ev, k)), cls })) : [{ name: 'Bu aralıkta olay yok' }]);
        chartTo(c, 'bars', ev, { series: EV.map(([key, , cls]) => ({ key, cls })),
            tip: p => when(p.t, bar, 'tip') + '\n' + (EV.filter(([k]) => p[k]).map(([k, name]) => name + ' ' + nf(p[k])).join(' · ') || 'Olay yok') }, nf);

        // Veritabanı ve disk
        c = card('storage');
        numTo(c.n, mb(late.db_mb), 'veritabanı');
        const disks = (d.disk || []).filter(x => x && x.total_bytes);
        const fullest = disks.sort((a, b) => a.free_percent - b.free_percent)[0];
        keysTo(c.d, [{ name: 'Veritabanı', value: mb(late.db_mb), cls: 'ch-c1' },
            fullest ? { name: 'Disk', value: pct(100 - fullest.free_percent) + ' dolu · ' + gb(fullest.free_bytes) + ' boş' } : null]);
        chartTo(c, 'line', pts, { series: [{ key: 'db_mb', cls: 'ch-c1' }], area: true,
            tip: p => tipP(p, typeof p.db_mb === 'number' ? 'Veritabanı ' + mb(p.db_mb) + (typeof p.disk === 'number' ? ' · disk ' + pct(p.disk) + ' dolu' : '') : 'Ölçüm yok') }, mb, WAIT);
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
    const fmtNum = (n) => Number(n || 0).toLocaleString('tr-TR');
    const ago = (sec) => POps.duration(sec) + ' önce';
    function setState(id, k, t) { const el = $(id); if (el) el.innerHTML = t ? stateHtml(k, t) : ''; }
    function sectionError(el, e) { if (el) POps.setError(el, e, { compact: true }); }

    // ---- Ortak ayrıntı paneli üst kısmı
    function drawerHeadHtml(title, subHtml, icon, cls) {
        return `<div class="drawer-head"><div class="drawer-title"><span class="drawer-ico ${escapeHtml(cls || '')}">${POps.iconHtml(icon, 'lg')}</span>`
            + `<div style="min-width:0"><h2>${escapeHtml(title)}</h2><div class="sub">${subHtml}</div></div></div>`
            + `<button type="button" class="ibtn sm" data-act="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button></div>`;
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
        if (v && !srv) { latest = '?'; latestSub = 'Güncelleme sorgusu için sunucuyu bir kez güncelleyin.'; avail = true; }
        else if (srv && rel) {
            if (!srv.checked) { latest = '?'; latestSub = "GitHub'a ulaşılamadı"; }
            else {
                latest = fmtV(srv.latest_release); avail = !!srv.update_available;
                const same = String(srv.latest_release || '').replace(/^v/, '') === String((v && v.running) || '').replace(/^v/, '');
                latestSub = avail ? 'Kurulabilir' : same ? 'Çalışan sürümle aynı' : 'Çalışan sürüm daha yeni';
            }
        } else if (srv) {
            if (!srv.rev) { latest = '?'; latestSub = 'Bir kez panelden güncellenince izlenir.'; }
            else if (!srv.checked) { latest = '?'; latestSub = "GitHub'a ulaşılamadı"; }
            else if (srv.update_available) { latest = `+${srv.ahead_by} değişiklik`; avail = true; latestSub = srv.version_changed ? 'Yeni sürüm numarası içeriyor' : ''; }
            else { latest = 'Güncel'; latestSub = srv.ahead_by > 0 ? `main'de ${srv.ahead_by} değişiklik var, sunucuyu etkilemiyor` : ''; }
        }
        let k, word;
        if (!v) { k = 'bad'; word = 'Sürüm bilgisi alınamadı'; }
        else if (busy) { k = 'run'; word = 'Güncelleniyor'; }
        else if (srv && srv.last_state === 'failed') { k = 'bad'; word = 'Son güncelleme başarısız'; }
        else if (avail) { k = 'run'; word = 'Yeni sürüm var'; }
        else if (latest === '?') { k = 'off'; word = srv && !srv.checked ? "GitHub'a ulaşılamadı" : 'Durum bilinmiyor'; }
        else { k = 'ok'; word = 'Güncel'; }
        return { v, su, srv, rel, st, busy, latest, latestSub, avail, k, word, configured: !!(su && su.configured) };
    }

    function lastAttemptHtml(st) {
        if (!st || !st.state || st.state === 'running') return '';
        const ok = st.state === 'ok';
        const metaHtml = POps.timeHtml(st.at) + (st.rev && st.rev !== 'unknown' ? ' · commit ' + escapeHtml(st.rev) : '') + (st.target ? ' · ' + escapeHtml(st.target === 'origin/main' ? 'main' : st.target) : '');
        return `<div class="act"><div class="res ${ok ? 'ok' : 'bad'}">${POps.iconHtml(ok ? 'check' : 'x')}</div>
            <div style="min-width:0"><div class="what">Son sunucu güncellemesi</div><div class="meta">${metaHtml}</div>
            ${ok ? '' : `<div class="why">Güncelleme tamamlanamadı; önceki kod çalışıyor (sağlık kontrolü geri döndü).${st.message ? ' Ayrıntı: ' + escapeHtml(st.message) : ''}</div>`}</div>
            <div class="side">${wordHtml(ok ? 'ok' : 'bad', ok ? 'Tamamlandı' : 'Başarısız')}</div></div>`;
    }

    function renderServer() {
        const I = serverInfo();
        setState('srvState', I.k, I.word);
        const box = $('srvSet');
        if (!I.v) { POps.setError(box, new Error('Sürüm bilgisi alınamadı. Sayfayı yenileyin.'), { compact: true }); return; }
        const srv = I.srv || {};
        const deployedHtml = srv.deployed_at ? 'Panelden güncellendi ' + POps.timeHtml(srv.deployed_at) + (srv.rev ? ' · commit ' + escapeHtml(srv.rev) : '') : 'Henüz panelden güncellenmedi';
        const commitsHtml = !I.rel && (srv.commits || []).length ? ` <button type="button" class="lnk" data-act="commits">Değişiklikleri göster</button>` : '';
        let actHtml;
        if (I.busy) {
            const req = store.get(SKEY, true);
            const whoHtml = req && req.at && Date.now() - req.at < 30 * 60 * 1000 ? escapeHtml(req.by || '?') + ' · ' + POps.timeHtml(req.at) + ' · ' : '';
            actHtml = `<div class="srow"><span class="res-spin"><span class="spinner"></span></span>
                <div class="grow"><div class="t">Sunucu güncelleniyor</div><div class="d">${whoHtml}Birkaç saniye bağlantı kopabilir; sağlık kontrolü geçmezse önceki koda döner.</div>
                <div class="pbar indet" style="margin-top:10px"><i class="run"></i></div></div></div>`;
        } else {
            const what = I.rel ? 'Yayımlanmış son sürümü kurar, geri gitmez.' : "GitHub main'deki kodu kurar.";
            const title = !I.v.server ? 'Sunucu güncellemesi gerekli' : I.avail ? (I.rel ? `${I.latest} kurulabilir` : `${I.latest} kurulabilir`) : 'Sunucu güncel';
            const desc = !I.configured ? 'Panelden güncelleme bu sunucuda kurulu değil (docs/self-update.md).' : what + ' Sağlık kontrolü geçmezse önceki koda kendiliğinden döner.';
            actHtml = `<div class="srow"><div class="grow"><div class="t">${escapeHtml(title)}</div><div class="d">${escapeHtml(desc)}</div></div>
                ${IS_SUPER ? `<button type="button" class="btn${I.avail ? '' : ' secondary'}" data-act="selfupdate" ${I.configured ? '' : 'disabled'}>${POps.iconHtml('download', 'sm')}Sunucuyu güncelle</button>` : ''}</div>`;
        }
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">Çalışan sürüm</div><div class="d">${deployedHtml}</div></div><div class="v">${escapeHtml(fmtV(I.v.running))}</div></div>
            <div class="srow"><div class="grow"><div class="t">Güncelleme kanalı</div><div class="d">${I.rel ? 'Yalnızca yayımlanmış sürümler kurulur.' : 'GitHub main dalı; geliştirme sunucusu içindir.'}</div></div><div class="v">${I.rel ? 'Sürüm' : 'Geliştirme (main)'}</div></div>
            <div class="srow"><div class="grow"><div class="t">${I.rel ? "GitHub'daki son sürüm" : 'GitHub main'}</div><div class="d">${escapeHtml(I.latestSub)}${commitsHtml}</div></div><div class="v">${escapeHtml(I.latest)}</div></div>
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
        body.innerHTML = drawerHeadHtml('Gelecek değişiklikler', escapeHtml(`main dalında ${Number(srv.ahead_by || 0)} değişiklik`), 'list', '')
            + `<div class="notes-sec"><ul>${listHtml}</ul></div><div class="dr-note">Commit başlıkları GitHub'dan alınır (son 15).</div>`;
    }

    async function loadSelfUpdate() {
        try { S.su = await POps.get('/api/system/self-update/status'); } catch (e) { S.su = S.su || null; }
    }

    async function selfUpdate(btn) {
        const I = serverInfo();
        const target = I.rel ? (I.latest && I.latest !== '?' && I.latest !== '—' ? I.latest + ' sürümüne' : 'yayımlanmış son sürüme') : "GitHub main'deki koda";
        const ok = await POps.confirm({
            title: `Sunucu ${target} güncellensin mi?`,
            message: 'Birkaç saniye bağlantı kopabilir. Sağlık kontrolü geçmezse sunucu önceki koda kendiliğinden döner.',
            confirmText: 'Sunucuyu güncelle', icon: 'download'
        });
        if (!ok) return;
        S.suBefore = (S.su && S.su.status && S.su.status.at) || '';
        try {
            await POps.busy(btn, () => POps.post('/api/system/self-update'));
        } catch (e) { POps.toast('error', 'Güncelleme başlatılamadı: ' + POps.errorMessage(e)); return; }
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
                if (st && st.state === 'ok' && done) POps.toast('success', 'Sunucu güncellendi.');
                else if (st && st.state === 'failed' && done) POps.toast('error', 'Sunucu güncellemesi başarısız; önceki kod çalışıyor.');
                else POps.toast('warning', 'Güncellemenin sonucu alınamadı; sayfayı birazdan yenileyin.');
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
        return `<li><details><summary>${headHtml}${md(first)} <span class="more">devamı</span></summary>${md(rest.slice(first.length))}</details></li>`;
    }
    function noteSecHtml(sec, title) {
        const label = title || (sec.version === 'Unreleased' ? 'Henüz sürüm numarası almamış yenilikler' : fmtV(sec.version) + (sec.date ? ' · ' + sec.date : ''));
        return `<div class="notes-sec"><div class="ver">${escapeHtml(label)}</div>${sec.intro ? `<div class="intro">${md(sec.intro)}</div>` : ''}`
            + sec.groups.map(g => `<div class="kind">${escapeHtml(KIND[g.kind] || g.kind)}</div><ul>${g.items.map(noteItemHtml).join('')}</ul>`).join('') + '</div>';
    }
    async function openNotes() {
        const body = openDrawer('sysn:notes', () => {});
        body.innerHTML = drawerHeadHtml('Sürüm notları', 'GitHub’daki değişiklik günlüğü', 'file', '') + '<div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div>';
        let d = S.notes;
        if (!d) {
            try { d = S.notes = await POps.get('/api/system/release-notes'); }
            catch (e) { if (POps.drawer.isOpen('sysn:notes')) POps.setError(body.lastElementChild, e, { compact: true }); return; }
        }
        if (!POps.drawer.isOpen('sysn:notes')) return;
        const headHtml = drawerHeadHtml('Sürüm notları', 'GitHub’daki değişiklik günlüğü', 'file', '');
        if (!d.available) { body.innerHTML = headHtml + '<div class="empty-state compact">' + POps.iconHtml('wifi') + '<p>GitHub’a ulaşılamadığı için notlar gösterilemiyor.</p></div>'; return; }
        const srv = (S.ver && S.ver.server) || {};
        let html = '';
        if (srv.update_available && (d.incoming || []).length) html += '<div><h3>Güncellemeyle gelecekler</h3>' + d.incoming.map(sec => noteSecHtml(sec)).join('') + '</div>';
        if ((d.installed || []).length) {
            const [cur, ...older] = d.installed;
            html += `<div><h3>Bu sunucuda (${escapeHtml(fmtV(d.running))}${d.rev ? ' · ' + escapeHtml(d.rev) : ''})</h3>${noteSecHtml(cur)}`
                + (older.length ? `<details class="notes-old"><summary>Önceki ${older.length} sürüm</summary>${older.map(sec => noteSecHtml(sec)).join('')}</details>` : '') + '</div>';
        }
        if (d.agent && (d.agent.groups || []).length) html += `<div><h3>Ajan paketi ${escapeHtml(fmtV(d.agent.version))}</h3>${noteSecHtml(d.agent, ' ')}</div>`;
        body.innerHTML = headHtml + (html || '<div class="empty-state compact">' + POps.iconHtml('file') + '<p>Gösterilecek not yok.</p></div>')
            + '<div class="dr-note">Notlar GitHub’daki CHANGELOG’dan alınır (İngilizce).</div>';
    }
    $('notesBtn').addEventListener('click', openNotes);

    // =================================================================
    // AJANLAR: sürüm dağılımı, paket, güncelleme ve ilerleme
    // =================================================================
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
        if (!total) return '<div class="d" style="margin-top:6px">Henüz kayıtlı ajan yok.</div>';
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
        const lgdHtml = parts.map(p => `<span><span class="dot ${escapeHtml(p.cls)}" style="opacity:${Number(p.op)}"></span>${escapeHtml(p.ver ? fmtV(p.ver) : 'Bilinmiyor')} <b>${Number(p.n)}</b></span>`).join('');
        return `<div class="pbar dist" role="img" aria-label="Ajan sürüm dağılımı">${segHtml}</div><div class="lgd">${lgdHtml}</div>`;
    }
    let agHash = '';
    function renderAgents(force) {
        if (!S.ver) return;
        const A = agentInfo();
        const v = A.v;
        const sig = JSON.stringify([v.staged_version, v.latest, v.release_available, v.checked_github, S.fetching, A.all.map(d => [d.hostname, dev.version(d), d.status])]);
        if (!force && sig === agHash) return;
        agHash = sig;
        // Durum
        let k, word;
        if (v.release_available) { k = 'run'; word = `Yeni paket: ${fmtV(v.latest)}`; }
        else if (!A.staged) { k = 'off'; word = 'Paket yok'; }
        else if (A.old.length) { k = 'run'; word = `${A.old.length} eski ajan`; }
        else { k = 'ok'; word = 'Hepsi güncel'; }
        setState('agState', k, word);
        // Paket satırı
        let pkgD;
        if (S.fetching) pkgD = "GitHub'dan indiriliyor ve imzası doğrulanıyor…";
        else if (!A.staged) pkgD = v.latest ? `Henüz paket yok. GitHub'da ${fmtV(v.latest)} var.` : (v.checked_github ? 'Henüz paket yok.' : "Henüz paket yok; GitHub'a ulaşılamadı.");
        else pkgD = `İmzası doğrulandı.${v.release_available ? ` GitHub'da daha yeni ${fmtV(v.latest)} var.` : v.latest ? " GitHub'daki son sürümle aynı." : ''}`;
        const fetchHtml = IS_SUPER && v.release_available ? `<button type="button" class="btn secondary" data-act="fetch">${POps.iconHtml('download', 'sm')}${escapeHtml(fmtV(v.latest))} paketini indir</button>` : '';
        const pkgMoreHtml = `<button type="button" class="ibtn sm" data-act="pkgmore" data-tip="Paket işlemleri" data-tip-pos="left" aria-label="Paket işlemleri" aria-haspopup="menu">${POps.iconHtml('more')}</button>`;
        // Güncelleme satırı
        let upT, upD, upBtn = '';
        const offOld = A.old.length - A.oldOn.length;
        if (!A.staged) { upT = 'Ajan güncellemesi'; upD = 'Önce ajan paketini indirin.'; }
        else if (v.release_available) { upT = `${A.old.length} ajan ${fmtV(A.staged)} sürümünde değil`; upD = `Gönderilecek paket ${fmtV(A.staged)}; önce ${fmtV(v.latest)} paketini indirin.`; }
        else if (!A.old.length) { upT = `Bütün ajanlar ${fmtV(A.staged)} sürümünde`; upD = 'Gönderilecek güncelleme yok.'; }
        else {
            upT = `${A.old.length} eski ajan`;
            upD = (A.oldOn.length ? `${A.oldOn.length} açık` : 'Hepsi kapalı') + (offOld && A.oldOn.length ? `, ${offOld} kapalı (kapalılar açılınca gönderilebilir)` : '') + '. Yeni sürüm açılmazsa ajan önceki sürüme kendiliğinden döner.';
            if (IS_SUPER) upBtn = `<button type="button" class="btn" data-act="deploy-old" ${A.oldOn.length ? '' : 'disabled'}>${POps.iconHtml('arrow-up', 'sm')}${A.oldOn.length} eski ajanı güncelle</button>`;
        }
        const upMoreHtml = IS_SUPER && A.staged && !v.release_available ? `<button type="button" class="ibtn sm" data-act="upmore" data-tip="Hedef seç" data-tip-pos="left" aria-label="Başka hedefe gönder" aria-haspopup="menu">${POps.iconHtml('more')}</button>` : '';
        $('agSet').innerHTML = `<div class="srow block"><div style="display:flex;justify-content:space-between;gap:12px"><div class="t">Sürüm dağılımı</div><div class="v">${A.all.length} ajan</div></div>${distHtml(A)}</div>
            <div class="srow"><div class="grow"><div class="t">Ajan paketi${A.staged ? ' ' + escapeHtml(fmtV(A.staged)) : ''}</div><div class="d">${escapeHtml(pkgD)}</div></div><div class="acts">${fetchHtml}${IS_SUPER ? pkgMoreHtml : ''}</div></div>
            <div class="srow"><div class="grow"><div class="t">${escapeHtml(upT)}</div><div class="d">${escapeHtml(upD)}</div></div><div class="acts">${upBtn}${upMoreHtml}</div></div>`;
        if (S.fetching) { const fb = $('agSet').querySelector('[data-act="fetch"]'); if (fb) { fb.disabled = true; fb.classList.add('is-loading'); } }
    }

    $('agSet').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b || b.disabled) return;
        const A = agentInfo();
        switch (b.dataset.act) {
            case 'fetch': fetchRelease(); break;
            case 'deploy-old': deploy(A.oldOn.map(d => d.hostname), b, A.old.length - A.oldOn.length); break;
            case 'pkgmore': POps.menu(b, [
                { label: 'Paketi elle yükle…', icon: 'upload', onClick: () => openModal('uploadModal') },
                { label: 'Sürüm notları', icon: 'file', onClick: openNotes }
            ]); break;
            case 'upmore': POps.menu(b, [
                { header: `${fmtV(A.staged)} nereye gönderilsin?` },
                { label: 'Bir sınıfa…', icon: 'labs', onClick: () => openTarget('lab') },
                { label: 'Seçili bilgisayarlara…', icon: 'monitor', onClick: () => openTarget('pc') }
            ]); break;
        }
    });

    async function fetchRelease() {
        const tag = (S.ver && S.ver.latest) || null;
        S.fetching = true; renderAgents(true);
        try {
            const d = await POps.post('/api/system/fetch-release', { tag });
            POps.toast('success', `${fmtV(d.version)} indirildi ve imzası doğrulandı.`);
            S.fetching = false;
            await loadAll(false);
        } catch (e) {
            POps.toast('error', 'Paket indirilemedi: ' + POps.errorMessage(e));
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
                POps.toast('success', `${fmtV(d.version)} doğrulandı ve kaydedildi.`);
                closeModal('uploadModal');
                chosen = []; $('upFiles').value = ''; renderFiles();
                await loadAll(false);
            } catch (e) { POps.toast('error', 'Paket doğrulanamadı: ' + POps.errorMessage(e)); }
        });
    }

    // ---- Gönder
    async function deploy(hosts, btn, skippedOff) {
        const staged = S.ver && S.ver.staged_version;
        hosts = [...new Set(hosts)];
        if (!staged || !hosts.length) return POps.toast('warning', 'Güncellenecek açık bilgisayar yok.');
        const n = hosts.length;
        const ok = await POps.confirm({
            title: n === 1 ? `${dev.name(hosts[0])} ${fmtV(staged)} sürümüne güncellensin mi?` : `${n} ajan ${fmtV(staged)} sürümüne güncellensin mi?`,
            message: (n > 1 ? hosts.slice(0, 5).map(dev.name).join(', ') + (n > 5 ? ` ve ${n - 5} bilgisayar daha` : '') + '\n' : '')
                + 'Ajan paketi indirip imzasını kendisi doğrular; yeni sürüm açılmazsa önceki sürüme döner. Bilgisayar birkaç dakika bağlantısız kalabilir.',
            note: skippedOff ? `Kapalı ${skippedOff} bilgisayar atlanacak.` : '',
            confirmText: n === 1 ? 'Ajanı güncelle' : `${n} ajanı güncelle`, icon: 'arrow-up'
        });
        if (!ok) return false;
        const since = Math.floor(Date.now() / 1000) - 30;
        let d;
        try { d = await POps.busy(btn, () => POps.post('/api/system/deploy-update', { target_mode: 'PC', targets: hosts })); }
        catch (e) { POps.toast('error', 'Güncelleme gönderilemedi: ' + POps.errorMessage(e)); return false; }
        // already_pending: aynı sürüm son 15 dk içinde gönderilmiş, kurulum sürüyor; yeniden gönderilmedi ama izlenir
        const sent = d.dispatched || [], off = d.skipped_offline || [], dup = d.already_pending || [];
        if (!sent.length && !dup.length) { POps.toast('warning', 'Hiçbir bilgisayar bağlı değildi; güncelleme gönderilmedi.'); return false; }
        const said = [];
        if (sent.length) said.push(`${fmtV(d.version)} ${sent.length} bilgisayara gönderildi`);
        if (dup.length) said.push(`${dup.length} bilgisayara zaten gönderildi, kurulum sürüyor`);
        if (off.length) said.push(`${off.length} kapalı bilgisayar atlandı`);
        POps.toast(sent.length ? 'success' : 'info', said.join(', ') + '.');
        rollout = { version: d.version, pcs: [...sent, ...dup], skipped: off, since, at: Date.now(), by: ME, doneAt: null };
        store.set(RKEY, rollout);
        rItems = null; rShowAll = false;
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
        const segHtml = `<div class="segmented block" role="group" aria-label="Hedef"><button type="button" data-act="mode" data-mode="lab" aria-pressed="${T.mode === 'lab'}">Bir sınıf</button><button type="button" data-act="mode" data-mode="pc" aria-pressed="${T.mode === 'pc'}">Seçili bilgisayarlar</button></div>`;
        let pickHtml;
        if (T.mode === 'lab') {
            pickHtml = `<select id="tgLab" aria-label="Sınıf">${labs.map(l => `<option value="${escapeHtml(l)}" ${l === T.lab ? 'selected' : ''}>${escapeHtml(l)}</option>`).join('')}</select>`;
        } else {
            const q = T.q.toLocaleLowerCase('tr');
            const rows = devs().filter(d => !q || [POps.deviceName(d), d.hostname, d.lab].some(x => String(x || '').toLocaleLowerCase('tr').includes(q)))
                .sort((a, b) => (isOn(b) - isOn(a)) || POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
            const rowsHtml = rows.map(d => {
                const on = isOn(d), ver = dev.version(d), old = normV(ver) !== normV(staged);
                return `<label class="check${on ? '' : ' off'}"><input type="checkbox" data-host="${escapeHtml(d.hostname)}" ${on ? '' : 'disabled'} ${T.sel.has(d.hostname) ? 'checked' : ''}>`
                    + `<span class="dot ${on ? 'on' : 'off'}"></span><span class="nm">${escapeHtml(POps.deviceName(d))}<span class="vv"> · ${escapeHtml(d.lab && d.lab !== dev.UNASSIGNED ? d.lab : 'Atanmamış')}</span></span>`
                    + `<span class="vv">${escapeHtml(ver ? fmtV(ver) : '—')}${old ? ' ↑' : ''}</span></label>`;
            }).join('') || '<div class="dr-note" style="padding:10px 0">Süzgece uyan bilgisayar yok.</div>';
            pickHtml = `<div class="search-field" style="flex:none;min-width:0">${POps.iconHtml('search', 'sm')}<input type="search" id="tgSearch" value="${escapeHtml(T.q)}" placeholder="Bilgisayar ya da sınıf" aria-label="Bilgisayar ara"></div>`
                + `<div class="dr-list" id="tgList">${rowsHtml}</div>`
                + `<div class="dr-actions"><button type="button" class="lnk" data-act="sel-old">Eski sürümdeki açıkları seç</button><span class="faint">·</span><button type="button" class="lnk" data-act="sel-none">Seçimi temizle</button></div>`;
        }
        let sum;
        if (!list.length) sum = T.mode === 'lab' ? 'Bu sınıfta bilgisayar yok.' : 'Yalnızca açık bilgisayarlar seçilebilir. Önce tek bilgisayarda denemek iyi olur.';
        else if (!sendable.length) sum = offN === list.length ? 'Seçimdeki bilgisayarların hepsi kapalı.' : `Seçimdeki açık bilgisayarlar zaten ${fmtV(staged)} sürümünde.`;
        else sum = `${sendable.length} açık bilgisayar ${fmtV(staged)} sürümüne güncellenecek.` + (sameN ? ` ${sameN} bilgisayar zaten bu sürümde.` : '') + (offN ? ` ${offN} kapalı bilgisayar atlanacak.` : '');
        body.innerHTML = drawerHeadHtml('Ajan güncellemesi', escapeHtml(`${fmtV(staged)} paketi`), 'arrow-up', '')
            + segHtml + pickHtml
            + `<div class="dr-sum">${escapeHtml(sum)}</div>`
            + `<div><button type="button" class="btn" data-act="send" ${sendable.length ? '' : 'disabled'}>${sendable.length ? (sendable.length === 1 ? 'Ajanı güncelle' : `${sendable.length} ajanı güncelle`) : 'Güncelle'}</button></div>`;
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
    let rItems = null, rTimer = null, rErr = null, rShowAll = false, rNow = null;
    const BAD_RES = ['rollback_failed', 'failed', 'reverted_by_freeze', 'error', 'rejected'];
    // Ajanın bildirdiği adım (update_progress, 0.1.22+). 0.1.21 ve öncesi adım bildirmez; onlarda eski davranış sürer.
    const STAGE_WORDS = new Map([['received', 'Alındı'], ['downloaded', 'İndirildi'], ['verified', 'Doğrulandı'],
        ['updater_started', 'Kurulum başladı'], ['waiting_installer', 'Bekleniyor'], ['installing', 'Kuruluyor'], ['ignored_busy', 'Kuruluyor']]);
    const QUIET_AFTER = 180;   // sn: gönderimden bu kadar sonra hiç adım gelmediyse ajan ilerleme bildirmiyor
    function stageNote(it) {
        const notes = [];
        if (it.stage === 'waiting_installer') notes.push('Windows Installer meşgul, bekleniyor' + (it.attempt && it.of ? ` (${Number(it.attempt)}/${Number(it.of)})` : '') + '.');
        if (it.stage === 'ignored_busy') notes.push('Önceki güncelleme sürüyor; bu gönderim yok sayıldı.');
        if (!it.online) notes.push(['updater_started', 'waiting_installer', 'installing'].includes(it.stage) ? 'Ajan şu an bağlı değil; kurulumda servis yeniden başlar.' : 'Ajan şu an bağlı değil.');
        return notes.join(' ');
    }
    function itemState(it) {
        const r = it.result || null;
        const s = String((r && r.status) || '');
        if (it.on_target) return { k: 'ok', w: 'Güncellendi' };
        if (r && r.agent_state === 'unmanaged') return { k: 'bad', w: 'Elle kurulum gerekli', why: 'Ajan güncellemeden sonra çalışmıyor; bilgisayarda yeniden kurulmalı.' };
        if (r && s === 'rejected') return { k: 'bad', w: 'Reddedildi', why: 'Reddedildi: ' + (r.detail || 'ajan sebep bildirmedi') };
        const more = r && r.detail ? ' Ajanın bildirdiği: ' + r.detail : '';
        if (r && BAD_RES.includes(s)) return { k: 'bad', w: 'Başarısız', why: (s === 'rollback_failed' ? 'Güncelleme ve geri dönüş başarısız.' : s === 'reverted_by_freeze' ? 'Dondurma yazılımı (Deep Freeze vb.) güncellemeyi geri aldı.' : 'Güncelleme başarısız.') + more };
        if (r && s === 'rolled_back') return { k: 'warn', w: 'Geri alındı', why: 'Yeni sürüm sağlıklı açılmadı; önceki sürüme dönüldü.' + more };
        if (r && s === 'install_failed') return { k: 'warn', w: 'Başlatılamadı', why: 'Kurulum başlatılamadı; bilgisayar değişmedi.' + more };
        if (r && /pending_reboot/.test(s)) return { k: 'run', w: 'Yeniden başlatma bekliyor' };
        if (!it.known) return { k: 'warn', w: 'Kayıtlı değil' };
        if (it.pending && STAGE_WORDS.has(it.stage)) return { k: 'run', w: STAGE_WORDS.get(it.stage), at: it.stage_at, note: stageNote(it) };
        if (it.pending && it.online) {
            const quiet = rNow != null && it.sent_at != null && rNow - it.sent_at > QUIET_AFTER;
            return { k: 'run', w: 'Kuruluyor', note: quiet ? 'Ajan ilerleme bildirmiyor (eski sürüm olabilir).' : '' };
        }
        if (it.pending) return { k: 'run', w: 'Yeniden bağlanıyor' };
        if (!it.online) return { k: 'run', w: 'Kapalı' };
        return { k: 'warn', w: 'Sonuç gelmedi', why: 'Ajan güncellemeyi aldı ama sonuç bildirmedi; sürümü değişmedi.' };
    }
    function rCounts() {
        const c = { ok: 0, bad: 0, warn: 0, run: 0, total: rollout ? rollout.pcs.length : 0 };
        if (!rItems) { c.run = c.total; return c; }
        rItems.forEach(it => { c[itemState(it).k] += 1; });
        return c;
    }
    function renderRollout() {
        const box = $('rollout');
        if (!rollout) { box.hidden = true; box.innerHTML = ''; return; }
        box.hidden = false;
        const c = rCounts();
        const running = c.run > 0 && !rollout.doneAt;
        const done = c.ok + c.bad + c.warn;
        const k = running ? 'run' : c.bad ? 'bad' : c.warn ? 'warn' : 'ok';
        const word = running ? 'Sürüyor' : c.bad || c.warn ? `${c.bad + c.warn} sorunlu` : 'Tamamlandı';
        const seg = (cls, n) => n ? `<i class="${escapeHtml(cls)}" style="width:${(n / c.total * 100).toFixed(2)}%"></i>` : '';
        const metaHtml = escapeHtml(rollout.by || '?') + ' · ' + POps.timeHtml(rollout.at) + ((rollout.skipped || []).length ? ` · ${rollout.skipped.length} kapalı bilgisayar atlandı` : '');
        const order = { bad: 0, warn: 1, run: 2, ok: 3 };
        const items = (rItems || rollout.pcs.map(pc => ({ pc, known: true, online: true, pending: true }))).map(it => ({ it, s: itemState(it) }))
            .sort((a, b) => order[a.s.k] - order[b.s.k] || dev.name(a.it.pc).localeCompare(dev.name(b.it.pc), 'tr', { numeric: true }));
        const shown = rShowAll ? items : items.slice(0, 6);
        const rowsHtml = shown.map(({ it, s }) => {
            const d = dev.find(it.pc);
            const icon = s.k === 'ok' ? 'check' : s.k === 'bad' ? 'x' : s.k === 'warn' ? 'alert' : 'clock';
            const metaRow = [d && d.lab && d.lab !== dev.UNASSIGNED ? d.lab : '', it.version ? 'çalışan ' + fmtV(it.version) : ''].filter(Boolean).join(' · ');
            return `<div class="act"><div class="res ${escapeHtml(s.k)}">${POps.iconHtml(icon)}</div>
                <div style="min-width:0"><div class="what">${escapeHtml(dev.name(it.pc))}</div><div class="meta">${escapeHtml(metaRow || it.pc)}</div>
                ${s.note ? `<div class="note">${escapeHtml(s.note)}</div>` : ''}
                ${s.why ? `<div class="why${s.k === 'warn' ? ' warn' : ''}">${escapeHtml(s.why)}</div>` : ''}</div>
                <div class="side">${wordHtml(s.k, s.w)}${s.at ? `<span class="when">${POps.timeHtml(s.at)}</span>` : ''}</div></div>`;
        }).join('');
        box.innerHTML = `<div class="ro-head"><div class="res ${escapeHtml(k)}" style="width:28px;height:28px;border-radius:99px;display:flex;align-items:center;justify-content:center">${running ? '<span class="spinner sm"></span>' : POps.iconHtml(k === 'ok' ? 'check' : 'alert', 'sm')}</div>
                <div class="grow"><div class="t" style="font-weight:var(--fw-medium)">${escapeHtml(fmtV(rollout.version))} gönderimi</div><div class="d" style="font-size:var(--text-xs);color:var(--text-muted)">${metaHtml}</div></div>
                <span class="cnt"><b>${Number(done)}</b>/${Number(c.total)}</span>${wordHtml(k, word)}
                <button type="button" class="ibtn sm" data-act="ro-close" data-tip="${running ? 'İzlemeyi bırak' : 'Kapat'}" data-tip-pos="left" aria-label="${running ? 'İzlemeyi bırak' : 'Kapat'}">${POps.iconHtml('x', 'sm')}</button></div>
            <div class="ro-bar"><div class="pbar">${seg('ok', c.ok)}${seg('warn', c.warn)}${seg('bad', c.bad)}${seg('run', c.run)}</div>${rErr ? `<div class="dr-note" style="margin-top:6px">İlerleme okunamadı: ${escapeHtml(POps.errorMessage(rErr))}</div>` : ''}</div>
            <div class="ro-list">${rowsHtml}</div>
            ${items.length > 6 ? `<div class="ro-more"><button type="button" class="lnk" data-act="ro-all">${rShowAll ? 'Daha az göster' : `Tümünü göster (${items.length})`}</button></div>` : ''}`;
    }
    $('rollout').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        if (b.dataset.act === 'ro-all') { rShowAll = !rShowAll; renderRollout(); }
        else if (b.dataset.act === 'ro-close') { clearTimeout(rTimer); rollout = null; rItems = null; store.set(RKEY, null); renderRollout(); }
    });
    async function pollRollout() {
        clearTimeout(rTimer);
        if (!rollout) { renderRollout(); return; }
        try {
            const r = await POps.post('/api/system/update-progress', { pcs: rollout.pcs, version: normV(rollout.version), since: rollout.since });
            rItems = r.items || []; rErr = null; rNow = typeof r.now === 'number' ? r.now : null;
        } catch (e) { rErr = e; }
        if (!rollout) return;
        const c = rCounts();
        if (!rErr && !c.run && !rollout.doneAt) {
            rollout.doneAt = Date.now(); store.set(RKEY, rollout);
            POps.toast(c.bad || c.warn ? 'warning' : 'success', c.bad || c.warn ? `${fmtV(rollout.version)}: ${c.ok} güncellendi, ${c.bad + c.warn} sorunlu.` : `${fmtV(rollout.version)}: ${c.total} bilgisayar güncellendi.`);
        }
        renderRollout();
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
        if (errs) out.push(`${errs} hata`);
        const tick = d.scheduler_last_tick_age;
        if (tick === null || tick === undefined || tick > 120) out.push('Zamanlayıcı durdu');
        (d.disk || []).filter(x => x.level !== 'ok').slice(0, 1).forEach(() => out.push('Disk dolmak üzere'));
        (d.tls || []).filter(x => x.level === 'high' || x.level === 'critical').slice(0, 1).forEach(() => out.push('Sertifika bitiyor'));
        return out;
    }
    function statHtml(label, val, subHtml, warn) {
        return `<div class="hstat"><div class="l">${warn ? '<span class="dot warn"></span>' : ''}${escapeHtml(label)}</div><div class="val">${escapeHtml(String(val))}</div><div class="s">${subHtml}</div></div>`;
    }
    function renderHealth() {
        const box = $('hlBody');
        if (!box) return;
        const d = S.diag;
        if (!d) { if (S.diagErr) { setState('hlState', 'off', 'Okunamadı'); POps.setError(box, S.diagErr, { compact: true }); } return; }
        const issues = healthIssues(d);
        setState('hlState', issues.length ? 'warn' : 'ok', issues.length ? issues.join(' · ') : 'Sağlıklı');
        const lc = d.log_counts || {};
        const errs = (lc.ERROR || 0) + (lc.CRITICAL || 0);
        const tick = d.scheduler_last_tick_age;
        const tickBad = tick === null || tick === undefined || tick > 120;
        const pool = d.db_pool || {};
        const dv = d.devices || {};
        const disk = (d.disk || []).slice().sort((a, b) => a.free_percent - b.free_percent)[0];
        const tiles = [
            statHtml('Açık kalma', POps.duration(d.uptime_seconds), escapeHtml('Bellek ' + (d.rss_mb != null ? d.rss_mb + ' MB' : '—'))),
            statHtml('Bağlı ajan', fmtNum(d.agents_connected), escapeHtml(`${fmtNum(dv.online)} çevrimiçi / ${fmtNum(dv.total)} kayıtlı`)),
            statHtml('Veritabanı', `${(pool.size || 0) - (pool.idle || 0)} / ${pool.max || '—'}`, 'kullanımda / en çok'),
            statHtml('Hata', fmtNum(errs), escapeHtml(`${fmtNum(d.http_5xx)} sunucu hatası, ${fmtNum(lc.WARNING)} uyarı`), errs > 0),
            statHtml('Zamanlayıcı', tick === null || tick === undefined ? 'Başlamadı' : ago(tick), tickBad ? '30 sn’de bir çalışmalı' : 'son tur', tickBad),
            disk ? statHtml('Disk', '%' + disk.free_percent + ' boş', escapeHtml(`${fmtBytes(disk.free_bytes)} / ${fmtBytes(disk.total_bytes)}`), disk.level !== 'ok')
                 : statHtml('Uzak ekran oturumu', fmtNum(d.vision_sessions), escapeHtml(`${fmtNum(d.panels_connected)} açık panel`))
        ].join('');
        const tlsHtml = (d.tls || []).map(c => {
            const k = c.level === 'ok' ? 'ok' : c.level === 'unknown' ? 'off' : c.level === 'critical' ? 'bad' : 'warn';
            const w = c.level === 'unknown' ? 'Okunamadı' : `${Math.floor(c.days_left)} gün kaldı`;
            return `<div class="srow"><div class="grow"><div class="t">Sertifika</div><div class="d">${escapeHtml(c.name)}${c.not_after ? ' · bitiş ' + POps.timeHtml(c.not_after) : ''}</div></div>${wordHtml(k, w)}</div>`;
        }).join('');
        const errList = d.recent_errors || [];
        box.innerHTML = `<div class="hstats">${tiles}</div>
            <div class="set">${tlsHtml}
                <div class="srow click" data-act="errors" role="button" tabindex="0"><div class="grow"><div class="t">Son hatalar</div><div class="d">${errList.length ? 'Sorun bildirirken istek kimliğini verin.' : 'Sunucu açıldığından beri hata yok.'}</div></div><span class="v">${Number(errList.length)}</span>${POps.iconHtml('right', 'sm ico-lead')}</div>
                <div class="srow click" data-act="tech" role="button" tabindex="0"><div class="grow"><div class="t">Teknik ayrıntılar</div><div class="d">Yük, yavaş istekler, disk ve izleme.</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>
            </div>`;
    }
    function renderBackup() {
        const box = $('bkSet');
        if (!box) return;
        if (!S.diag) { if (S.diagErr) { setState('bkState', 'off', 'Okunamadı'); POps.setError(box, S.diagErr, { compact: true }); } return; }
        const b = S.diag.backup;
        const st = backupState(b);
        const K = { ok: ['ok', 'Tamam'], old: ['warn', '2 günden eski'], failed: ['bad', 'Başarısız'], none: ['bad', 'Yedek yok'] }[st];
        setState('bkState', K[0], K[1]);
        let rowHtml;
        if (st === 'none') {
            rowHtml = `<div class="act"><div class="res bad">${POps.iconHtml('x')}</div><div style="min-width:0"><div class="what">Henüz yedek alınmadı</div><div class="meta">Gece yedeği kurulu değil.</div><div class="why">Veritabanı yedeklenmiyor. Kurulum için docs/backup.md (pops-backup.timer).</div></div><div class="side">${wordHtml('bad', 'Yedek yok')}</div></div>`;
        } else {
            const metaHtml = POps.timeHtml(b.at) + (b.bytes ? ' · ' + escapeHtml(fmtBytes(b.bytes)) : '') + (b.verified ? ' · geri yükleme sınandı' : '');
            const whyHtml = st === 'failed' ? `<div class="why">${escapeHtml(b.message || 'Yedek alınamadı ya da geri yükleme sınaması geçmedi.')}</div>` : st === 'old' ? '<div class="why warn">Son başarılı yedek 2 günden eski; gece yedeği çalışmıyor olabilir.</div>' : '';
            rowHtml = `<div class="act"><div class="res ${escapeHtml(K[0])}">${POps.iconHtml(st === 'ok' ? 'check' : st === 'old' ? 'clock' : 'x')}</div><div style="min-width:0"><div class="what">Son veritabanı yedeği</div><div class="meta">${metaHtml}</div>${whyHtml}</div><div class="side">${wordHtml(K[0], K[1])}</div></div>`;
        }
        box.innerHTML = rowHtml + '<div class="srow"><div class="grow"><div class="d">Yedek her gece alınır ve geri yüklenerek sınanır. Yalnızca son yedeğin sonucu tutulur.</div></div></div>';
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
            <div class="meta">${POps.timeHtml(e.ts)} · ${escapeHtml(e.logger || '')}${e.request_id ? ' · istek ' + escapeHtml(e.request_id) : ''}</div>${e.exc ? `<div class="out">${escapeHtml(e.exc)}</div>` : ''}</div><div class="side"></div></div>`).join('');
        body.innerHTML = drawerHeadHtml('Son hatalar', escapeHtml(list.length ? `Sunucu açıldığından beri ${list.length} hata` : 'Hata yok'), 'alert', '')
            + (list.length ? `<div>${rowsHtml}</div>` : '<div class="empty-state compact">' + POps.iconHtml('check') + '<p>Sunucu açıldığından beri hata kaydı yok.</p></div>');
    }
    function openTech() {
        const d = S.diag || {};
        const l = d.load || {};
        const techRowHtml = (k, v, mono) => v === null || v === undefined || v === '' ? '' : `<div class="grow"><span>${escapeHtml(k)}</span><span${mono ? ' class="mono"' : ''}>${escapeHtml(String(v))}</span></div>`;
        const factsHtml = [techRowHtml('Sürüm', d.version), techRowHtml('Süreç', d.pid, true), techRowHtml('Bellek', d.rss_mb != null ? d.rss_mb + ' MB' : ''), techRowHtml('Açık panel', d.panels_connected),
            techRowHtml('Uzak ekran oturumu', d.vision_sessions), techRowHtml('Sonucu beklenen ajan güncellemesi', d.pending_updates), techRowHtml('Karantinadaki cihaz', (d.devices || {}).quarantined),
            techRowHtml('Yakalanmamış hata', d.unhandled_errors)].join('');
        const loadHtml = [techRowHtml('Kalp atışı', fmtNum(l.heartbeats)), techRowHtml('Kalp atışı başına sorgu', l.queries_per_heartbeat), techRowHtml('Veritabanı yazma (sn’de, son 1 dk)', l.db_writes_per_second),
            techRowHtml('Veritabanı okuma / yazma', `${fmtNum(l.db_reads)} / ${fmtNum(l.db_writes)}`), techRowHtml('Görev dağıtımı p95', l.task_dispatch_p95_seconds != null ? l.task_dispatch_p95_seconds + ' sn' : ''),
            techRowHtml('Komut gönderme p95', l.command_send_p95_ms != null ? l.command_send_p95_ms + ' ms' : '')].join('');
        const slowHtml = (d.slowest_routes || []).map(r => techRowHtml(`${r.avg_ms} ms · ${fmtNum(r.count)} istek`, r.route, true)).join('');
        const diskHtml = (d.disk || []).map(x => techRowHtml(`%${x.free_percent} boş · ${fmtBytes(x.free_bytes)} / ${fmtBytes(x.total_bytes)}`, x.path, true)).join('');
        const body = openDrawer('sysx:tech', () => {});
        body.innerHTML = drawerHeadHtml('Teknik ayrıntılar', 'Sunucu açıldığından beri', 'cpu', '')
            + `<div class="glist">${factsHtml}</div>`
            + (loadHtml ? `<div><h3>Yük</h3><div class="glist">${loadHtml}</div></div>` : '')
            + (slowHtml ? `<div><h3>En yavaş istekler (ortalama)</h3><div class="glist">${slowHtml}</div></div>` : '')
            + (diskHtml ? `<div><h3>Disk</h3><div class="glist">${diskHtml}</div></div>` : '')
            + `<div class="dr-note">${d.metrics_enabled ? 'Prometheus /metrics açık.' : 'Prometheus ile izlemek için sunucunun .env dosyasında METRICS_TOKEN tanımlayın.'} Güncelleme kanalı sunucudaki /etc/pops/selfupdate.conf dosyasından okunur.</div>`;
    }

    // =================================================================
    // KAYIT BÜTÜNLÜĞÜ (denetim zinciri)
    // =================================================================
    function renderAudit() {
        const box = $('auSet');
        if (!box) return;
        const r = store.get(AKEY);
        let k = 'off', w = 'Doğrulanmadı', resHtml = '';
        if (r) {
            k = r.ok ? 'ok' : 'bad'; w = r.ok ? 'Sağlam' : 'Kırık';
            const metaHtml = escapeHtml(r.by || '?') + ' · ' + POps.timeHtml(r.at) + ' · ' + escapeHtml(`${fmtNum(r.checked)} kayıt denetlendi`);
            resHtml = `<div class="act"><div class="res ${escapeHtml(k)}">${POps.iconHtml(r.ok ? 'check' : 'x')}</div><div style="min-width:0"><div class="what">${r.ok ? 'Zincir sağlam' : 'Zincir kırık'}</div><div class="meta">${metaHtml}</div>
                ${r.ok ? '' : `<div class="why">${escapeHtml(`#${r.first_broken_id} numaralı kayıtta zincir kopuyor: bu kayıt ya da öncesi değiştirilmiş veya silinmiş. Veritabanı yedeğiyle karşılaştırın.`)}</div>`}</div><div class="side">${wordHtml(k, w)}</div></div>`;
        }
        setState('auState', k, w);
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">Denetim zinciri</div><div class="d">Yönetici işlemleri ve güncelleme sonuçları birbirine bağlı kaydedilir; silinen ya da değiştirilen kayıt zinciri bozar.</div></div>
            <button type="button" class="btn secondary" id="auVerify">${POps.iconHtml('shield', 'sm')}Doğrula</button></div>${resHtml}`;
        $('auVerify').addEventListener('click', async function () {
            try {
                const d = await POps.busy(this, () => POps.get('/api/system/audit-verify'));
                store.set(AKEY, Object.assign({ at: Date.now(), by: ME }, d));
                POps.toast(d.ok ? 'success' : 'error', d.ok ? `Denetim zinciri sağlam (${fmtNum(d.checked)} kayıt).` : `Denetim zinciri #${d.first_broken_id} numaralı kayıtta kırık.`);
                renderAudit(); renderSummary();
            } catch (e) { POps.toast('error', 'Doğrulanamadı: ' + POps.errorMessage(e)); }
        });
    }

    // =================================================================
    // CİHAZ YETENEKLERİ (uzak komut / uzak ekran kalıcı kapatma)
    // =================================================================
    function capWord(enabled, requested) {
        if (requested) return { k: 'bad', w: 'Kapalı', how: 'Panelden kalıcı kapatıldı' };
        if (enabled === false) return { k: 'bad', w: 'Kapalı', how: 'Kurulumda kapatılmış' };
        if (enabled === true) return { k: 'ok', w: 'Açık', how: '' };
        return { k: 'off', w: 'Bildirilmedi', how: 'Ajan bu bilgiyi göndermiyor (0.1.4 öncesi)' };
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
        setState('capState', list.length ? 'off' : 'ok', list.length ? `${list.length} bilgisayarda kısıtlı` : 'Hepsinde açık');
        const opts = devs().slice().sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }))
            .map(d => `<option value="${escapeHtml(d.hostname)}">${escapeHtml(POps.deviceName(d))}${d.lab && d.lab !== dev.UNASSIGNED ? ' · ' + escapeHtml(d.lab) : ''}</option>`).join('');
        const rowsHtml = list.map(d => {
            const t = capWord(d.cap_terminal_enabled, d.cap_terminal_disable_requested), v = capWord(d.cap_vision_enabled, d.cap_vision_disable_requested);
            const parts = [t.k === 'bad' ? 'Uzak komut kapalı' : '', v.k === 'bad' ? 'Uzak ekran kapalı' : ''].filter(Boolean).join(' · ');
            const how = d.cap_terminal_disable_requested || d.cap_vision_disable_requested ? 'panelden' : 'kurulumda';
            return `<div class="srow click" data-host="${escapeHtml(d.hostname)}" role="button" tabindex="0"><span class="dot ${escapeHtml(dev.state(d).cls)}"></span><div class="grow"><div class="t">${escapeHtml(POps.deviceName(d))}</div><div class="d">${escapeHtml(parts + ' (' + how + ')')}</div></div>${POps.iconHtml('right', 'sm ico-lead')}</div>`;
        }).join('');
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">Uzak komut ve uzak ekran</div><div class="d">Bir bilgisayarda kalıcı kapatılır; sunucu ele geçirilse bile o bilgisayarda çalışmaz.</div></div>
            <select id="capPick" aria-label="Bilgisayar seç" class="wide" style="width:min(240px,100%)"><option value="">Bilgisayar seç…</option>${opts}</select></div>${rowsHtml}`;
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
        const name = which === 'terminal' ? 'Uzak komut' : 'Uzak ekran';
        let btnHtml, hint;
        if (requested) { btnHtml = `<button type="button" class="btn secondary sm" data-act="cap-on" data-which="${escapeHtml(which)}">İzin ver</button>`; hint = `İzin vermek kalıcı kapatmayı kaldırır; yetenek ancak ajan kurulumu onu açık bildirirse geri gelir.`; }
        else if (enabled === false) { btnHtml = `<button type="button" class="btn danger-soft sm" data-act="cap-off" data-which="${escapeHtml(which)}">Kapalı tut</button>`; hint = `Uzaktan açılamaz; açmak için ajanı ${flag} ile yeniden kurun. "Kapalı tut" yeniden kurulsa bile kapalı kalmasını sağlar.`; }
        else { btnHtml = `<button type="button" class="btn danger-soft sm" data-act="cap-off" data-which="${escapeHtml(which)}">Kapat</button>`; hint = 'Kapatma kalıcıdır; çevrimdışı bilgisayara bağlanınca uygulanır.'; }
        return `<div class="srow"><div class="grow"><div class="t">${escapeHtml(name)}${wordHtml(c.k, c.w)}</div><div class="d">${escapeHtml(c.how ? c.how + '. ' + hint : hint)}</div></div>${btnHtml}</div>`;
    }
    let capSig = '';
    function renderCapDrawer(host, onlyIfChanged) {
        const d = dev.find(host);
        const sig = JSON.stringify(d ? [d.status, POps.deviceName(d), d.lab, dev.version(d), d.cap_terminal_enabled, d.cap_vision_enabled, d.cap_terminal_disable_requested, d.cap_vision_disable_requested, d.cap_server_ca, d.bypass_key, d.last_disconnect_reason, d.agent_health] : null);
        if (onlyIfChanged && sig === capSig) return;
        capSig = sig;
        const body = POps.drawer.body();
        if (!d) { body.innerHTML = drawerHeadHtml(host, 'Bulunamadı', 'monitor', '') ; return; }
        const st = dev.state(d);
        const h = d.agent_health;
        const ag = (t) => t ? ago(Date.now() / 1000 - t) : 'henüz yok';
        const factHtml = (k, vHtml) => `<div class="grow"><span>${escapeHtml(k)}</span><span>${vHtml}</span></div>`;
        const ca = d.cap_server_ca === 'custom' ? 'Kurum sertifikası' : d.cap_server_ca === 'system' ? 'Sistem deposu' : 'Bildirilmedi';
        const bypass = d.bypass_key === 'device' ? 'Cihaza özel' : d.bypass_key === 'pending' ? 'Gönderildi, onay bekleniyor' : 'Ortak anahtar (eski)';
        let factsHtml = factHtml('Ajan', escapeHtml(fmtV(dev.version(d)))) + factHtml('Sunucu sertifikası', escapeHtml(ca)) + factHtml('Çevrimdışı açma anahtarı', escapeHtml(bypass));
        if (d.last_disconnect_reason) factsHtml += factHtml('Son bağlantı kopması', escapeHtml(d.last_disconnect_reason) + (d.last_disconnect_at ? ' · ' + POps.timeHtml(d.last_disconnect_at) : ''));
        let noteHtml = '';
        if (!h) noteHtml = `<div class="dr-note" style="margin-top:8px">Bu ajan (${escapeHtml(fmtV(dev.version(d)))}) durum bildirmiyor; bildirim 0.1.12 ile geldi.</div>`;
        else {
            const vision = { off: 'Kapalı', idle: 'Boşta', connected: 'Bağlı' }[h.vision_channel] || 'Bilinmiyor';
            const errs = Number(h.loop_errors_1h) || 0;
            factsHtml += factHtml('Açılış', escapeHtml(ag(h.started_at))) + factHtml('Politika eşitleme', escapeHtml(ag(h.last_policy_sync))) + factHtml('Envanter', escapeHtml(ag(h.last_inventory_upload)))
                + factHtml('Tepsi uygulaması', wordHtml(h.tray_connected ? 'ok' : 'bad', h.tray_connected ? 'Bağlı' : 'Bağlı değil')) + factHtml('Uzak ekran kanalı', escapeHtml(vision))
                + factHtml('Son 1 saatte hata', errs ? wordHtml('bad', String(errs)) : escapeHtml('0'));
            if (h.screen_locked) factsHtml += factHtml('Karantina kilidi', wordHtml('bad', 'Açık')) + factHtml('Ağ yalıtımı', h.network_isolated ? wordHtml('ok', 'Uygulandı') : wordHtml('bad', 'Uygulanamadı'));
            noteHtml = (errs && h.last_error ? `<div class="issue err" style="margin-top:8px">${escapeHtml('Son hata: ' + h.last_error)}</div>` : '')
                + (h.screen_locked && !h.network_isolated && h.isolation_error ? `<div class="issue err" style="margin-top:8px">${escapeHtml('Yalıtım hatası: ' + h.isolation_error)}</div>` : '');
        }
        const subHtml = `<span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}${d.lab && d.lab !== dev.UNASSIGNED ? ' · ' + escapeHtml(d.lab) : ''}`;
        body.innerHTML = drawerHeadHtml(POps.deviceName(d), subHtml, 'monitor', st.cls)
            + `<div><h3>Yetenekler</h3><div class="set">${capRowHtml('terminal', d)}${capRowHtml('vision', d)}</div>`
            + (d.cap_terminal_enabled == null && d.cap_vision_enabled == null ? `<div class="dr-note" style="margin-top:8px">Bu ajan yetenek durumunu bildirmiyor (bildirim 0.1.4 ile geldi). Kapatma şimdi kaydedilebilir, güncellemeden sonra uygulanır.</div>` : '') + '</div>'
            + `<div><h3>Ajan durumu${isOn(d) ? '' : ' (son bilinen)'}</h3><div class="glist">${factsHtml}</div>${noteHtml}</div>`
            + `<a href="devices?pc=${encodeURIComponent(d.hostname)}" style="font-size:var(--text-sm)">Cihazlar sayfasında aç</a>`;
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
        const label = which === 'terminal' ? 'uzak komut' : 'uzak ekran';
        const alreadyOff = (which === 'terminal' ? d.cap_terminal_enabled : d.cap_vision_enabled) === false;
        if (!enable && !await POps.confirm({
            title: alreadyOff ? `${POps.deviceName(d)} bilgisayarında ${label} kapalı tutulsun mu?` : `${POps.deviceName(d)} bilgisayarında ${label} kapatılsın mı?`,
            message: alreadyOff ? `Kurulumda kapatılmış; ajan açık kurulsa bile kapalı kalır. Geri açmak için "İzin ver" ve ajanın yeniden kurulması gerekir.`
                : `Kalıcıdır: geri açmak için "İzin ver" ve ajanın bilgisayarda yeniden kurulması gerekir.`,
            confirmText: alreadyOff ? 'Kapalı tut' : (which === 'terminal' ? 'Uzak komutu kapat' : 'Uzak ekranı kapat'), danger: true, icon: 'lock'
        })) return;
        const body = { pc_name: host };
        body[which === 'terminal' ? 'terminal_enabled' : 'vision_enabled'] = !!enable;
        try {
            const r = await POps.busy(btn, () => POps.post('/api/system/set-capabilities', body));
            POps.toast('success', enable ? 'Kalıcı kapatma kaldırıldı. Yetenek, ajan kurulumu onu açık bildirince geri gelir.'
                : (r.delivered_online ? 'Kapatma gönderildi.' : 'Kapatma kaydedildi; bilgisayar bağlanınca uygulanacak.'));
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
        setState('enState', enr === total ? 'ok' : 'off', `${enr}/${total} ajan kayıtlı`);
        const hint = on ? 'Açık: anahtarı olmayan ajanlar bağlanamaz.' : (enr < total ? `Geçiş modu: kayıtsız ajanlar da bağlanabilir. Açmadan önce ${total - enr} ajan kaydolmalı.` : 'Kapalı. Bütün ajanlar kayıtlı; açılabilir.');
        const toks = S.tokens || [];
        const valid = toks.filter(t => !t.expired && !t.is_used).length;
        box.innerHTML = `<div class="srow"><div class="grow"><div class="t">Kimlik zorlaması</div><div class="d">${escapeHtml(hint)}</div></div>
                <label class="switch"><input type="checkbox" id="enforceSw" ${on ? 'checked' : ''} aria-label="Kimlik zorlaması"><span></span></label></div>
            <div class="srow"><div class="grow"><div class="t">Kayıt jetonları</div><div class="d">${S.tokens === null ? 'Yükleniyor…' : toks.length ? escapeHtml(`${valid} geçerli, ${toks.length - valid} kullanılmış ya da süresi dolmuş`) : 'Henüz jeton yok. Yeni kurulumda MSI’a ENROLL_TOKEN olarak verilir.'}</div></div>
                <button type="button" class="btn secondary" id="tokNew">${POps.iconHtml('plus', 'sm')}Jeton üret</button></div>`;
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
            const w = t.expired ? 'Süresi doldu' : t.is_used ? 'Tükendi' : 'Geçerli';
            const metaHtml = escapeHtml(t.lab_name || 'Bütün sınıflar') + ' · ' + escapeHtml(`${Number(t.use_count || 0)}/${Number(t.max_uses || 1)} kullanım`) + ' · ' + POps.timeHtml(t.created_at)
                + (!t.expired && !t.is_used && t.expires_at ? ' · bitiş ' + POps.timeHtml(t.expires_at) : '') + (t.note ? ' · ' + escapeHtml(t.note) : '');
            return `<div class="act"><div class="res">${POps.iconHtml('key')}</div><div style="min-width:0"><div class="what"><code>${escapeHtml(t.token_hint || '')}…</code></div><div class="meta">${metaHtml}</div></div>
                <div class="side" style="flex-direction:row;align-items:center;gap:8px">${wordHtml(k, w)}<button type="button" class="ibtn sm" data-tok="${escapeHtml(t.id)}" data-hint="${escapeHtml(t.token_hint || '')}" data-tip="Jeton işlemleri" data-tip-pos="left" aria-label="Jeton işlemleri" aria-haspopup="menu">${POps.iconHtml('more')}</button></div></div>`;
        }).join('');
    }
    if ($('tokSet')) $('tokSet').addEventListener('click', (e) => {
        const b = e.target.closest('[data-tok]');
        if (!b) return;
        POps.menu(b, [{ label: 'Jetonu sil', icon: 'trash', danger: true, onClick: async () => {
            if (!await POps.confirm({ title: `${b.dataset.hint}… jetonu silinsin mi?`, message: 'Bu jetonla henüz kaydolmamış kurulumlar kaydolamaz. Kayıtlı bilgisayarlar etkilenmez.', confirmText: 'Jetonu sil', danger: true, icon: 'trash' })) return;
            if (await POps.act(null, () => POps.del('/api/system/enroll-token/' + encodeURIComponent(b.dataset.tok)), { success: 'Jeton silindi.' })) loadTokens();
        } }]);
    });
    async function loadTokens() {
        if (!IS_SUPER) return;
        try { S.tokens = await POps.get('/api/system/enroll-tokens') || []; } catch (e) { S.tokens = []; POps.toast('error', 'Jetonlar alınamadı: ' + POps.errorMessage(e)); }
        renderEnroll();
    }
    if ($('tkCreate')) $('tkCreate').addEventListener('click', async function () {
        const body = { lab_name: $('tkLab').value.trim() || null, note: $('tkNote').value.trim() || null, ttl_hours: parseInt($('tkTtl').value, 10) || 72, max_uses: parseInt($('tkUses').value, 10) || 1 };
        let d;
        try { d = await POps.busy(this, () => POps.post('/api/system/enroll-token', body)); }
        catch (e) { POps.toast('error', 'Jeton üretilemedi: ' + POps.errorMessage(e)); return; }
        closeModal('tokenModal');
        ['tkLab', 'tkNote'].forEach(id => { $(id).value = ''; });
        loadTokens();
        await POps.alert({
            title: 'Kayıt jetonu hazır', icon: 'key', codes: [d.token], confirmText: 'Kapat',
            message: `${d.lab_name || 'Bütün sınıflar'} · ${d.max_uses} kullanım · ${d.ttl_hours} saat geçerli`,
            note: 'MSI kurulumunda ENROLL_TOKEN olarak verin. Jeton yalnızca şimdi gösterilir, sunucuda saklanmaz; şimdi kopyalayın.'
        });
    });
    async function setEnforce(sw) {
        const turnOn = sw.checked;
        const v = S.ver || {};
        const missing = (v.agents_total || 0) - (v.agents_enrolled || 0);
        if (turnOn && !await POps.confirm({
            title: 'Kimlik zorlaması açılsın mı?', danger: missing > 0, icon: 'lock',
            message: missing > 0 ? `${missing} ajan kayıtlı değil; zorlama açılınca bağlantılarını kaybederler.` : 'Anahtarı olmayan ajan artık bağlanamaz.',
            confirmText: missing > 0 ? `Aç, ${missing} ajan kopsun` : 'Zorlamayı aç'
        })) { sw.checked = false; return; }
        try {
            const d = await POps.busy(sw, () => POps.post('/api/system/enforce-auth', { enabled: turnOn }));
            S.ver.enforce_agent_auth = d.enforce_agent_auth;
            POps.toast('success', d.enforce_agent_auth ? 'Kimlik zorlaması açıldı.' : 'Kimlik zorlaması kapatıldı.');
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
        $('ntSmtp').textContent = d.smtp_configured ? 'Virgülle ayırın. Sunucuda SMTP ayarlı.' : 'E-posta için sunucunun .env dosyasında SMTP_HOST ve SMTP_FROM tanımlanmalı. Webhook ek ayar gerektirmez.';
        const on = d.enabled && (d.email_to || d.webhook_url);
        setState('ntState', on ? 'ok' : 'off', on ? 'Dışarıya gönderiliyor' : 'Yalnızca panelde');
        ntDirty();
    }
    async function loadNotify() {
        if (!IS_SUPER) return;
        try { renderNotify(await POps.get('/api/system/notify-settings')); }
        catch (e) { setState('ntState', 'off', 'Okunamadı'); }
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
            try { renderNotify(await POps.busy(this, () => POps.post('/api/system/notify-settings', ntBody()))); POps.toast('success', 'Bildirim ayarları kaydedildi.'); }
            catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
        $('ntTest').addEventListener('click', async function () {
            try {
                const d = await POps.busy(this, () => POps.post('/api/system/notify-test', ntBody()));
                if (d.error) POps.toast('error', 'Gönderilemedi: ' + d.error + ((d.channels || []).length ? ' (başarılı: ' + d.channels.join(', ') + ')' : ''));
                else POps.toast('success', 'Test bildirimi gönderildi: ' + (d.channels || []).join(', '));
            } catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
    }

    // =================================================================
    // SAKLAMA SÜRELERİ
    // =================================================================
    const RT = [['rtLogs', 'retention_days_logs', 'ajan olay kayıtları'], ['rtTasks', 'retention_days_tasks', 'sonuçlanmış görevler'], ['rtNotif', 'retention_days_notifications', 'okunmuş bildirimler']];
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
                if (isNaN(n) || n < 0 || n > 3650) { POps.toast('warning', 'Süre 0 ile 3650 gün arasında olmalı.'); $(id).focus(); return; }
                body[key] = n;
            }
            // Kısalan süre bu gece kayıt siler: onay
            const shorter = RT.filter(([, key]) => body[key] > 0 && (S.retention[key] === 0 || body[key] < S.retention[key]));
            if (shorter.length && !await POps.confirm({
                title: 'Saklama süreleri kısaltılsın mı?', icon: 'clock',
                message: shorter.map(([, key, label]) => `${body[key]} günden eski ${label}`).join(', ') + ' bu gece silinir. Silinen kayıt geri gelmez.',
                confirmText: 'Kısalt ve kaydet', danger: true
            })) return;
            try { renderRetention(await POps.busy(this, () => POps.post('/api/system/retention', body))); POps.toast('success', 'Saklama süreleri kaydedildi.'); }
            catch (e) { POps.toast('error', POps.errorMessage(e)); }
        });
    }

    // =================================================================
    // ÖZET + YÜKLEME
    // =================================================================
    function renderSummary() {
        if (!S.ver) return;
        const I = serverInfo();
        const A = agentInfo();
        let html = `<span class="sum"><span class="dot ${escapeHtml(I.k)}"></span>Sunucu <b>${escapeHtml(fmtV(S.ver.running))}</b></span>`
            + `<span class="sum"><b>${A.all.length}</b> ajan</span>`
            + (A.old.length ? `<span class="sum"><span class="dot run"></span><b>${A.old.length}</b> eski ajan</span>` : '');
        if (S.diag) {
            const n = healthIssues(S.diag).length + (backupState(S.diag.backup) === 'ok' ? 0 : 1);
            html += n ? `<span class="sum"><span class="dot warn"></span><b>${Number(n)}</b> sorun</span>` : '<span class="sum"><span class="dot ok"></span>Sağlıklı</span>';
        }
        const au = store.get(AKEY);
        if (au && !au.ok) html += '<span class="sum"><span class="dot bad"></span>Denetim zinciri kırık</span>';
        $('sysSummary').innerHTML = html;
    }

    async function loadAll(check) {
        const [ver] = await Promise.all([
            POps.get('/api/system/version' + (check ? '?check=true' : '')).catch(() => null),
            loadSelfUpdate()
        ]);
        if (ver) S.ver = ver;
        if (!S.ver) {
            const err = new Error('Sürüm bilgisi alınamadı. Sayfayı yenileyin.');
            renderServer();
            ['agSet', 'enSet'].forEach(id => { if ($(id)) POps.setError($(id), err, { compact: true }); });
            $('sysSummary').innerHTML = '<span class="sum"><span class="dot bad"></span>Sürüm bilgisi alınamadı</span>';
            return;
        }
        if (S.su && (S.su.pending || (S.su.status && S.su.status.state === 'running')) && !S.suPoll) { S.suBusy = true; S.suBefore = ''; watchSelfUpdate(); }
        S.checkedAt = new Date();
        $('checkBtn').dataset.tip = 'Güncellemeleri denetle · son ' + S.checkedAt.toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' });
        renderServer();
        renderAgents(true);
        renderEnroll();
        renderSummary();
        if (check) S.notes = null;
    }

    $('checkBtn').addEventListener('click', async function () {
        await POps.busy(this, () => Promise.all([loadAll(true), loadDiag()]));
        POps.toast('info', 'Güncellemeler denetlendi.');
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
    renderAudit();
    if (rollout) pollRollout();
    if (state.devicesLoaded) { renderCaps(true); }
    popsTwofaNudge();
})();
</script>

<?php include 'includes/footer.php'; ?>
