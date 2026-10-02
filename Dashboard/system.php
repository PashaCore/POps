<?php include 'includes/header.php'; ?>

<style>
    .sys-wrap { display: flex; flex-direction: column; gap: var(--space-5); max-width: 920px; margin: 0 auto; padding: var(--space-4) 0; }
    .sys-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-6); box-shadow: var(--shadow-sm); }
    .card-head { display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; flex-wrap: wrap; margin-bottom: var(--space-4); }
    .card-head h2 { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin: 0; display: flex; align-items: center; gap: 0.5rem; }
    .card-head h2 i { color: var(--primary-500); }
    .card-desc { color: var(--text-tertiary); font-size: var(--text-sm); margin: -0.5rem 0 var(--space-4); line-height: 1.5; }

    .section-title { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); margin: var(--space-6) 0 var(--space-3); display: flex; align-items: center; gap: 0.625rem; padding-bottom: 0.625rem; border-bottom: 1px solid var(--border-subtle); }
    .section-title.first { margin-top: 0; }
    .step { width: 1.5rem; height: 1.5rem; border-radius: 50%; background: var(--primary-50); color: var(--primary-500); display: inline-flex; align-items: center; justify-content: center; font-size: 0.75rem; font-weight: var(--fw-semibold); }

    .ver-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: var(--space-3); }
    .ver-tile { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-4); }
    .ver-tile .lbl, .mini-lbl { font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); }
    .ver-tile .val { font-size: var(--text-lg); font-weight: var(--fw-semibold); color: var(--text-primary); margin-top: 0.25rem; font-variant-numeric: tabular-nums; }
    .ver-tile .sub { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.25rem; }

    .badge { display: inline-flex; align-items: center; gap: 0.375rem; padding: 0.3rem 0.7rem; border-radius: 999px; font-size: var(--text-xs); font-weight: var(--fw-semibold); white-space: nowrap; }
    .badge.ok { background: var(--success-bg); color: var(--success-text); }
    .badge.warn { background: var(--warning-bg); color: var(--warning-text); }
    .badge.bad { background: var(--danger-bg); color: var(--danger-text); }
    .badge.muted { background: var(--bg-surface-2); color: var(--text-tertiary); }

    .btn { padding: 0.625rem 1rem; border-radius: var(--radius-md); font-size: var(--text-sm); font-weight: var(--fw-semibold); cursor: pointer; border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); display: inline-flex; align-items: center; gap: 0.5rem; transition: all 0.15s; max-width: 100%; text-align: left; }
    .btn:hover { background: var(--bg-app); }
    .btn.primary { background: var(--primary-500); color: #fff; border-color: var(--primary-500); }
    .btn.primary:hover { background: var(--primary-600); }
    .btn.danger { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-border, transparent); }
    .btn.small { padding: 0.35rem 0.7rem; font-size: var(--text-xs); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }

    .row { display: flex; align-items: center; gap: 0.75rem; flex-wrap: wrap; }
    .row.between { justify-content: space-between; }
    .mt { margin-top: var(--space-4); }
    .muted-text { color: var(--text-tertiary); font-size: var(--text-sm); }
    .sys-card input[type=checkbox] { width: auto; flex: none; margin: 0; }
    .sys-card label.muted-text { white-space: nowrap; }
    .fld { padding: 0.5rem 0.75rem; border: 1px solid var(--border-default); border-radius: var(--radius-md); background: var(--bg-surface-2); color: var(--text-primary); font-size: var(--text-sm); }

    .changes { margin-top: var(--space-4); background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-3) var(--space-4); }
    .changes ul { margin: 0.5rem 0 0; padding-left: 1.1rem; font-size: var(--text-sm); color: var(--text-secondary); line-height: 1.6; }

    .option-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap: var(--space-3); }
    .option-card { background: var(--bg-surface-2); border: 2px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-4); cursor: pointer; transition: all 0.15s; display: flex; flex-direction: column; align-items: center; gap: 0.5rem; text-align: center; }
    .option-card:hover { border-color: var(--primary-500); background: var(--bg-surface); }
    .option-card.active { border-color: var(--primary-500); background: var(--primary-50); }
    .option-card i { font-size: 1.5rem; color: var(--text-tertiary); }
    .option-card.active i { color: var(--primary-500); }
    .option-title { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .option-desc { font-size: var(--text-xs); color: var(--text-tertiary); }

    .target-box { margin-top: var(--space-3); background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-4); }
    .dev-list { display: flex; flex-direction: column; gap: 0.25rem; max-height: 320px; overflow-y: auto; margin-top: 0.75rem; }
    .dev-row { display: grid; grid-template-columns: auto auto 1fr auto; align-items: center; gap: 0.625rem; padding: 0.5rem 0.625rem; border-radius: var(--radius-md); background: var(--bg-surface); border: 1px solid var(--border-subtle); cursor: pointer; }
    .dev-row.offline { opacity: 0.55; cursor: default; }
    .dev-name { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .dev-meta { font-size: var(--text-xs); color: var(--text-tertiary); }
    .dot { width: 0.5rem; height: 0.5rem; border-radius: 50%; background: var(--text-muted, #94a3b8); }
    .dot.on { background: var(--success-solid); }
    .ver-pill { font-size: var(--text-xs); font-weight: var(--fw-semibold); padding: 0.15rem 0.5rem; border-radius: 999px; background: var(--success-bg); color: var(--success-text); white-space: nowrap; }
    .ver-pill.old { background: var(--warning-bg); color: var(--warning-text); }

    .summary { margin-top: var(--space-4); font-size: var(--text-sm); color: var(--text-secondary); }
    .btn-deploy { width: 100%; padding: 0.875rem; background: var(--primary-500); color: #fff; border: none; border-radius: var(--radius-md); font-size: var(--text-md); font-weight: var(--fw-semibold); cursor: pointer; transition: all 0.15s; display: flex; justify-content: center; align-items: center; gap: 0.625rem; margin-top: var(--space-4); }
    .btn-deploy:hover { background: var(--primary-600); }
    .btn-deploy:disabled { background: var(--bg-surface-2); color: var(--text-muted, #94a3b8); border: 1px solid var(--border-subtle); cursor: not-allowed; }

    details.offline { margin-top: var(--space-4); border: 1px dashed var(--border-default); border-radius: var(--radius-md); padding: 0.625rem 0.875rem; }
    details.offline summary { cursor: pointer; font-size: var(--text-sm); color: var(--text-secondary); font-weight: var(--fw-semibold); }
    .upload-zone { border: 2px dashed var(--border-default); border-radius: var(--radius-md); padding: 1.5rem 1rem; text-align: center; cursor: pointer; position: relative; background: var(--bg-surface-2); margin-top: 0.75rem; transition: all 0.15s; }
    .upload-zone:hover, .upload-zone.dragover { border-color: var(--primary-500); background: var(--primary-50); }
    .upload-zone i { font-size: 1.75rem; color: var(--text-tertiary); }
    .upload-zone input[type=file] { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .file-list { list-style: none; margin: 0.75rem 0 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; }
    .file-list li { font-size: var(--text-sm); color: var(--text-secondary); display: flex; align-items: center; gap: 0.5rem; flex-wrap: wrap; }
    .file-list li i { color: var(--text-tertiary); }
    .enroll-list code { font-size: 0.75rem; word-break: break-all; }

    .status-msg { margin-top: 1rem; padding: 0.75rem 1rem; border-radius: var(--radius-md); display: none; font-weight: var(--fw-semibold); font-size: var(--text-sm); border-left: 4px solid transparent; background: var(--bg-surface-2); color: var(--text-secondary); }
    .status-msg.show { display: block; }
    .status-success { background: var(--success-bg); color: var(--success-text); border-color: var(--success-solid); }
    .status-error { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-solid); }
    .notes { margin-top: var(--space-4); }
    .notes > details, .notes .note-sec { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.625rem 0.875rem; margin-top: 0.5rem; }
    .notes summary { cursor: pointer; font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .notes .ver { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-primary); margin: 0.5rem 0 0.25rem; }
    .notes .intro { font-size: var(--text-sm); color: var(--text-secondary); margin: 0.25rem 0 0.5rem; line-height: 1.5; }
    .notes .kind { font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); margin: 0.625rem 0 0.25rem; }
    .notes ul { margin: 0; padding-left: 1.1rem; }
    .notes li { font-size: var(--text-sm); color: var(--text-secondary); line-height: 1.5; margin-bottom: 0.25rem; }
    .notes li details summary { font-weight: normal; color: var(--text-secondary); list-style: none; }
    .notes li details summary::-webkit-details-marker { display: none; }
    .notes li details[open] summary .more { display: none; }
    .notes li .more { color: var(--primary-500); font-weight: var(--fw-semibold); white-space: nowrap; }
    .notes code { font-size: 0.8em; }
    .notes .lang { font-size: var(--text-xs); color: var(--text-tertiary); margin-top: 0.375rem; }
    .cap-state { margin: 0.75rem 0; font-size: var(--text-sm); color: var(--text-secondary); line-height: 1.6; }

    .err-list { list-style: none; margin: 0.5rem 0 0; padding: 0; display: flex; flex-direction: column; gap: 0.375rem; }
    .err-list li { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.5rem 0.75rem; font-size: var(--text-xs); color: var(--text-secondary); line-height: 1.5; word-break: break-word; }
    .err-list .meta-line { color: var(--text-tertiary); font-variant-numeric: tabular-nums; }
</style>

<div class="page-header" style="display:flex;align-items:flex-end;justify-content:space-between;gap:1rem;flex-wrap:wrap;">
    <div>
        <h1><i class="fas fa-server"></i> Sistem &amp; Sürüm</h1>
        <p>Sunucu ve ajan sürümlerini tek yerden kontrol edin ve güncelleyin</p>
    </div>
    <div class="row">
        <span class="muted-text" id="last-check"></span>
        <button class="btn" id="btn-check"><i class="fas fa-arrows-rotate"></i> Güncellemeleri kontrol et</button>
    </div>
</div>

<div class="sys-wrap">

    <!-- ============ SUNUCU ============ -->
    <div class="sys-card">
        <div class="card-head">
            <h2><i class="fas fa-server"></i> Sunucu</h2>
            <span id="srv-badge"><span class="badge muted">…</span></span>
        </div>
        <div class="ver-grid">
            <div class="ver-tile"><div class="lbl">Çalışan sürüm</div><div class="val" id="srv-version">…</div><div class="sub" id="srv-rev"></div></div>
            <div class="ver-tile"><div class="lbl" id="srv-main-lbl">Son sürüm (GitHub)</div><div class="val" id="srv-main">…</div><div class="sub" id="srv-main-sub"></div></div>
        </div>
        <div class="notes" id="srv-notes"></div>
        <details class="changes" id="srv-changes" style="display:none;">
            <summary class="mini-lbl" style="cursor:pointer;">Teknik ayrıntı: gelecek commit'ler</summary>
            <ul id="srv-commits"></ul>
        </details>
        <div class="row mt">
            <button class="btn" id="btn-selfupdate"><i class="fas fa-download"></i> Sunucuyu güncelle</button>
            <span class="muted-text">Yayımlanmış son sürümü kurar, geri gitmez. Sağlık kontrolü başarısız olursa önceki koda kendiliğinden döner.</span>
        </div>
        <div class="status-msg" id="su-status"></div>
    </div>

    <!-- ============ SUNUCU SAĞLIĞI ============ -->
    <div class="sys-card">
        <div class="card-head">
            <h2><i class="fas fa-heart-pulse"></i> Sunucu sağlığı</h2>
            <span id="dg-badge"><span class="badge muted">…</span></span>
        </div>
        <p class="card-desc">Sunucunun son açılışından bu yana durumu. Hata olursa aşağıda istek kimliğiyle listelenir; sorun bildirirken bu kimliği verin.</p>
        <div class="ver-grid" id="dg-tiles"></div>
        <details id="dg-errors-wrap" style="margin-top:var(--space-4);display:none;">
            <summary class="mini-lbl" style="cursor:pointer;" id="dg-errors-title">Son hatalar</summary>
            <ul class="err-list" id="dg-errors"></ul>
        </details>
        <div class="muted-text" id="dg-foot" style="margin-top:0.75rem;"></div>
    </div>

    <!-- ============ AJAN GÜNCELLEME ============ -->
    <div class="sys-card">
        <div class="card-head">
            <h2><i class="fas fa-laptop"></i> Ajan güncelleme</h2>
            <span id="ag-badge"><span class="badge muted">…</span></span>
        </div>
        <p class="card-desc">Ajanlara yalnızca imzası doğrulanmış paket gönderilir. Ajan MSI'ı sunucudan indirir, imzayı kendisi de doğrular; yeni sürüm açılmazsa önceki sürüme döner.</p>

        <div class="section-title first"><span class="step">1</span> Paket</div>
        <div class="ver-grid">
            <div class="ver-tile"><div class="lbl">GitHub'daki son sürüm</div><div class="val" id="ag-latest">…</div><div class="sub" id="ag-latest-sub"></div></div>
            <div class="ver-tile"><div class="lbl">Gönderilecek paket (doğrulandı)</div><div class="val" id="ag-staged">…</div><div class="sub" id="ag-staged-sub"></div></div>
        </div>
        <div class="notes" id="ag-notes"></div>
        <div class="row mt" id="fetch-row" style="display:none;">
            <button class="btn primary" id="btn-fetch"><i class="fas fa-cloud-arrow-down"></i> <span id="btn-fetch-lbl">GitHub'dan indir ve doğrula</span></button>
            <span class="muted-text">İmza ve özetler doğrulanmadan paket kullanılmaz.</span>
        </div>
        <div class="status-msg" id="fetch-status"></div>

        <details class="offline">
            <summary>İnternetsiz sunucu: paketi elle yükle</summary>
            <p class="muted-text" style="margin:0.75rem 0 0;">
                GitHub Release sayfasından <code>manifest.json</code>, <code>manifest.json.sig</code> ve
                <code>POps-Agent-*-win-x64.msi</code> dosyalarını indirip birlikte seçin. Doğrulama GitHub'dan indirmeyle aynıdır.
            </p>
            <div class="upload-zone" id="uz">
                <i class="fas fa-file-shield"></i>
                <div class="muted-text" style="margin-top:0.4rem;">Dosyaları seçin veya buraya sürükleyin</div>
                <input type="file" id="files" multiple>
            </div>
            <ul class="file-list" id="file-list"></ul>
            <div class="row mt">
                <button class="btn primary" id="btn-upload" disabled><i class="fas fa-upload"></i> Doğrula ve yükle</button>
                <label class="muted-text" style="display:flex;align-items:center;gap:0.4rem;">
                    <input type="checkbox" id="force"> aynı ya da eski sürümü zorla
                </label>
            </div>
            <div class="status-msg" id="upload-status"></div>
        </details>

        <div class="section-title"><span class="step">2</span> Hedef</div>
        <div class="option-grid">
            <div class="option-card active" data-mode="ALL"><i class="fas fa-globe"></i><div class="option-title">Tüm ajanlar</div><div class="option-desc" id="opt-all-desc">…</div></div>
            <div class="option-card" data-mode="LAB"><i class="fas fa-network-wired"></i><div class="option-title">Bir sınıf</div><div class="option-desc">Seçilen laboratuvardaki açık cihazlar</div></div>
            <div class="option-card" data-mode="PC"><i class="fas fa-laptop"></i><div class="option-title">Seçili cihazlar</div><div class="option-desc">Önce tek cihazda denemek için</div></div>
        </div>
        <div class="target-box" id="tgt-lab" style="display:none;">
            <select id="lab-select" class="fld" style="width:100%;"></select>
        </div>
        <div class="target-box" id="tgt-pc" style="display:none;">
            <div class="row between">
                <span class="muted-text">Yalnızca açık (online) cihazlar seçilebilir.</span>
                <button class="btn small" id="btn-select-outdated"><i class="fas fa-check-double"></i> Eski sürümdekileri seç</button>
            </div>
            <div class="dev-list" id="dev-list"></div>
        </div>
        <div class="summary" id="dep-summary"></div>
        <button class="btn-deploy" id="btn-deploy" disabled><i class="fas fa-rocket"></i> <span id="btn-deploy-lbl">Gönder</span></button>
        <div class="status-msg" id="deploy-status"></div>
    </div>

    <!-- ============ YETENEKLER ============ -->
    <div class="sys-card">
        <div class="card-head">
            <h2><i class="fas fa-shield-halved"></i> Cihaz yetenekleri ve ajan durumu</h2>
        </div>
        <p class="card-desc">
            Bir cihazda uzaktan terminali ve/veya Vision'ı (ekran izleme, uzaktan kontrol) kapatır; sunucu ele geçirilse bile
            o cihazda çalışmazlar. Kapatma kalıcıdır: çevrimdışı cihaza bağlanınca, eski ajana güncellenince uygulanır.
            Geri açmak için "İzin ver" kapatma isteğini kaldırır; yetenek ancak ajan kurulumu onu açık bildirirse geri gelir.
        </p>
        <select id="cap-device" class="fld" style="width:100%;"><option value="">Cihaz seçin…</option></select>
        <div class="cap-state" id="cap-state"></div>
        <div class="row">
            <button class="btn danger" id="cap-off-terminal"><i class="fas fa-terminal"></i> Terminali kapat</button>
            <button class="btn" id="cap-on-terminal"><i class="fas fa-unlock"></i> Terminale izin ver</button>
            <button class="btn danger" id="cap-off-vision"><i class="fas fa-video-slash"></i> Vision'ı kapat</button>
            <button class="btn" id="cap-on-vision"><i class="fas fa-unlock"></i> Vision'a izin ver</button>
        </div>
        <div class="status-msg" id="cap-status"></div>
    </div>

    <!-- ============ BİLDİRİMLER ============ -->
    <div class="sys-card">
        <div class="card-head">
            <h2><i class="fas fa-bell"></i> Bildirimler</h2>
            <span id="nt-badge"></span>
        </div>
        <p class="card-desc">
            Önemli olaylar (güncelleme sorunu, kayıtlı cihazın kimliğini ele geçirme girişimi, kural ihlali, karantina, sonucu
            gelmeyen güncelleme…) üstteki zilde her zaman görünür. Burada ayrıca e-posta ve/veya webhook (Slack, Discord, Teams
            ya da kendi sisteminiz) ile gönderilmelerini açabilirsiniz. Aynı olay 10 dakika içinde bir kez gönderilir.
        </p>
        <div class="row">
            <label class="muted-text" style="display:flex;align-items:center;gap:0.4rem;"><input type="checkbox" id="nt-enabled"> Dışarıya gönder</label>
            <label class="muted-text">En az önem
                <select id="nt-sev" class="fld">
                    <option value="critical">Kritik</option>
                    <option value="high">Yüksek</option>
                    <option value="medium">Orta</option>
                    <option value="info">Bilgi (her şey)</option>
                </select>
            </label>
        </div>
        <div class="row mt">
            <input id="nt-email" class="fld" style="flex:1;min-width:220px;" placeholder="E-posta alıcıları (virgülle)">
            <input id="nt-webhook" class="fld" style="flex:1;min-width:220px;" placeholder="Webhook adresi (https://…)">
        </div>
        <div class="muted-text" id="nt-smtp" style="margin-top:0.5rem;"></div>
        <div class="row mt">
            <button class="btn primary" id="nt-save"><i class="fas fa-floppy-disk"></i> Kaydet</button>
            <button class="btn" id="nt-test"><i class="fas fa-paper-plane"></i> Test gönder</button>
        </div>
        <div class="status-msg" id="nt-status"></div>
    </div>

    <!-- ============ KAYIT VE KİMLİK ============ -->
    <div class="sys-card">
        <div class="card-head">
            <h2><i class="fas fa-key"></i> Ajan kaydı ve kimlik</h2>
            <span id="enroll-count"></span>
        </div>

        <div class="section-title first"><i class="fas fa-ticket" style="color:var(--primary-500);"></i> Kayıt jetonu</div>
        <p class="card-desc" style="margin-top:0;">
            Yeni kurulumda MSI'a <code>ENROLL_TOKEN</code> olarak verilir; ajan ilk bağlanışta kendine özel bir anahtar alır.
            Çok kullanımlık jeton, bir sınıfa tek MSI ile toplu kurulum içindir.
        </p>
        <div class="row">
            <input id="et-lab" class="fld" style="flex:1;min-width:150px;" placeholder="Sınıf (isteğe bağlı)">
            <input id="et-note" class="fld" style="flex:1;min-width:150px;" placeholder="Not (isteğe bağlı)">
            <label class="muted-text">Kullanım <input id="et-uses" class="fld" type="number" min="1" value="1" style="width:70px;"></label>
            <label class="muted-text">Saat <input id="et-ttl" class="fld" type="number" min="1" value="72" style="width:80px;"></label>
            <button class="btn primary" id="btn-enroll"><i class="fas fa-plus"></i> Jeton üret</button>
        </div>
        <div class="status-msg" id="enroll-status"></div>
        <ul class="file-list enroll-list" id="enroll-list"></ul>

        <div class="section-title"><i class="fas fa-lock" style="color:var(--primary-500);"></i> Kimlik zorlaması</div>
        <div class="row between">
            <div class="row"><span id="enforce-badge"></span><span class="muted-text" id="enforce-hint"></span></div>
            <button class="btn" id="btn-enforce">…</button>
        </div>
        <div class="status-msg" id="enforce-status"></div>
    </div>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const $ = (id) => document.getElementById(id);
    const S = { ver: null, su: null, devices: [], mode: 'ALL' };

    const fmtV = (v) => !v ? '—' : (/^v/i.test(v) ? v : 'v' + v);
    const normV = (v) => String(v || '').trim().replace(/^v/i, '');
    const fmtDate = (iso) => { try { return new Date(iso).toLocaleString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit' }); } catch (e) { return iso || ''; } };
    const badge = (cls, icon, text) => `<span class="badge ${cls}"><i class="fas ${icon}"></i> ${escapeHtml(text)}</span>`;
    const isOnline = (d) => String(d.status || '').toLowerCase() === 'online';
    const devName = (d) => d.display_name || d.real_hostname || d.hw_id;
    function msg(id, cls, html) { const el = $(id); el.className = 'status-msg show' + (cls ? ' ' + cls : ''); el.innerHTML = html; }
    async function api(path, opts) {
        const res = await fetch(path, opts);
        const d = await res.json().catch(() => ({}));
        if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
        return d;
    }
    const postJson = (path, body) => api(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });

    // ================= SUNUCU =================
    function renderServer() {
        const v = S.ver, su = S.su;
        if (!v) return;
        const srv = v.server || {};
        $('srv-version').textContent = fmtV(v.running);
        const rel = (srv.channel || 'release') === 'release';
        $('srv-main-lbl').textContent = rel ? 'Son sürüm (GitHub)' : 'GitHub (main) · geliştirme kanalı';
        $('srv-rev').textContent = (srv.deployed_at ? 'güncellendi ' + fmtDate(srv.deployed_at) : 'Henüz panelden güncellenmedi') + (srv.rev ? ` · commit ${srv.rev}` : '');

        const busy = su && (su.pending || (su.status && su.status.state === 'running'));
        const btn = $('btn-selfupdate');
        btn.disabled = !(su && su.configured) || busy;
        btn.classList.toggle('primary', !!srv.update_available);
        $('srv-changes').style.display = 'none';

        let b, main = '—', sub = '';
        if (!v.server) {
            // Eski backend bu sorguyu bilmiyor: sayfanın kendisi sunucu güncellemesi bekliyor
            b = badge('warn', 'fa-circle-up', 'Güncelleme var');
            main = '?';
            sub = 'Güncelleme sorgusu için sunucuyu bir kez güncelleyin.';
            btn.classList.add('primary');
        } else if (su && !su.configured) {
            b = badge('muted', 'fa-plug', 'Self-update kurulu değil');
            sub = 'bkz. docs/self-update.md';
        } else if (busy) {
            b = badge('warn', 'fa-spinner fa-spin', 'Güncelleniyor…');
        } else if (srv.last_state === 'failed') {
            b = badge('bad', 'fa-triangle-exclamation', 'Son güncelleme başarısız');
            sub = 'Önceki kod çalışıyor (otomatik geri dönüldü).';
        } else if (rel) {
            // Sürüm kanalı: yalnızca yayımlanmış sürümler sayılır, ara commit'ler değil
            if (!srv.checked) { b = badge('muted', 'fa-wifi', 'GitHub\'a ulaşılamadı'); main = '?'; }
            else if (srv.update_available) {
                b = badge('warn', 'fa-circle-up', `Yeni sürüm: ${fmtV(srv.latest_release)}`);
                main = fmtV(srv.latest_release);
                sub = 'Güncelleme bu sürüme geçirir; notlar aşağıda.';
            } else { b = badge('ok', 'fa-check', 'Güncel'); main = fmtV(srv.latest_release); }
        } else if (!srv.rev) {
            b = badge('muted', 'fa-circle-question', 'Durum bilinmiyor');
            sub = 'Bir kez "Sunucuyu güncelle" ile kurulunca takip edilir.';
        } else if (!srv.checked) {
            b = badge('muted', 'fa-wifi', 'GitHub\'a ulaşılamadı');
            main = '?';
        } else if (srv.update_available) {
            b = badge('warn', 'fa-circle-up', 'Güncelleme var');
            main = `+${srv.ahead_by} değişiklik`;
            sub = srv.version_changed ? 'Yeni sürüm numarası içeriyor.' : '';
            $('srv-commits').innerHTML = (srv.commits || []).map(c => `<li>${escapeHtml(c)}</li>`).join('');
            $('srv-changes').style.display = (srv.commits || []).length ? 'block' : 'none';
        } else {
            b = badge('ok', 'fa-check', 'Güncel');
            main = 'Güncel';
            if (srv.ahead_by > 0) sub = `main'de ${srv.ahead_by} değişiklik var ama sunucuyu etkilemiyor.`;
        }
        $('srv-badge').innerHTML = b;
        $('srv-main').textContent = main;
        $('srv-main-sub').textContent = sub;
    }

    async function loadSelfUpdate() {
        try { S.su = await api('/api/system/self-update/status'); } catch (e) { S.su = null; }
    }

    $('btn-selfupdate').addEventListener('click', async function () {
        if (!confirm("Sunucu, GitHub main'deki koda güncellenecek. Birkaç saniye bağlantı kopabilir. Devam edilsin mi?")) return;
        const before = (S.su && S.su.status && S.su.status.at) || '';
        this.disabled = true;
        msg('su-status', '', '<i class="fas fa-spinner fa-spin"></i> Güncelleme başlatılıyor…');
        try {
            await api('/api/system/self-update', { method: 'POST' });
        } catch (e) { msg('su-status', 'status-error', escapeHtml(e.message)); this.disabled = false; return; }
        let tries = 0;
        const poll = setInterval(async () => {
            tries++;
            await loadSelfUpdate();   // servis yeniden başlarken birkaç istek düşebilir; sorun değil
            const st = S.su && S.su.status;
            const done = st && st.at !== before && (st.state === 'ok' || st.state === 'failed') && !S.su.pending;
            if (done || tries > 40) {
                clearInterval(poll);
                if (st && st.state === 'ok') msg('su-status', 'status-success', '<i class="fas fa-circle-check"></i> Sunucu güncellendi.');
                else if (st && st.state === 'failed') msg('su-status', 'status-error', '<i class="fas fa-circle-xmark"></i> Güncelleme başarısız; önceki koda geri dönüldü.');
                else msg('su-status', 'status-error', 'Sonuç alınamadı; sayfayı yenileyin.');
                await loadAll(true);
            } else {
                renderServer();
            }
        }, 3000);
    });

    // ================= SÜRÜM NOTLARI =================
    // CHANGELOG (Keep a Changelog) maddeleri: **kalın** ve `kod` dışında biçim yok; önce kaçırılır.
    const KIND = { Added: 'Eklenenler', Changed: 'Değişenler', Fixed: 'Düzeltmeler', Security: 'Güvenlik', Removed: 'Kaldırılanlar', Deprecated: 'Kullanımdan kalkacaklar' };
    const md = (t) => escapeHtml(t).replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>').replace(/`([^`]+)`/g, '<code>$1</code>');
    function noteItem(t) {
        const m = t.match(/^\*\*(.+?)\*\*\s*(.*)$/);
        const head = m ? `<strong>${escapeHtml(m[1])}</strong> ` : '';
        const rest = m ? m[2] : t;
        const first = rest.split(/(?<=\.)\s/)[0];
        if (first.length >= rest.length - 1 || rest.length < 180) return `<li>${head}${md(rest)}</li>`;
        return `<li><details><summary>${head}${md(first)} <span class="more">devamı</span></summary>${md(rest.slice(first.length))}</details></li>`;
    }
    function noteSection(sec, title) {
        return `<div class="ver">${escapeHtml(title || (sec.version === 'Unreleased' ? 'Henüz sürüm numarası almamış yenilikler' : fmtV(sec.version) + (sec.date ? ' · ' + sec.date : '')))}</div>`
            + (sec.intro ? `<div class="intro">${md(sec.intro)}</div>` : '')
            + sec.groups.map(g => `<div class="kind">${escapeHtml(KIND[g.kind] || g.kind)}</div><ul>${g.items.map(noteItem).join('')}</ul>`).join('');
    }
    const LANG = '<div class="lang">Sürüm notları GitHub\'daki CHANGELOG\'dan alınır (İngilizce).</div>';
    async function loadNotes() {
        let d;
        try { d = await api('/api/system/release-notes'); } catch (e) { return; }
        if (!d.available) { $('srv-notes').innerHTML = ''; $('ag-notes').innerHTML = ''; return; }
        const srv = (S.ver && S.ver.server) || {};
        if (srv.update_available && d.incoming.length) {
            $('srv-notes').innerHTML = `<div class="note-sec"><div class="mini-lbl">Güncellemeyle gelecek yenilikler</div>${d.incoming.map(sec => noteSection(sec)).join('')}${LANG}</div>`;
        } else if (d.installed.length) {
            $('srv-notes').innerHTML = `<details><summary>Bu sunucuda neler var (${escapeHtml(fmtV(d.running))}${d.rev ? ' · ' + escapeHtml(d.rev) : ''})</summary>${d.installed.map(sec => noteSection(sec)).join('')}${LANG}</details>`;
        } else { $('srv-notes').innerHTML = ''; }
        $('ag-notes').innerHTML = d.agent && d.agent.groups.length
            ? `<details${S.ver && S.ver.release_available ? ' open' : ''}><summary>${escapeHtml(fmtV(d.agent.version))} sürüm notları</summary>${noteSection(d.agent, ' ')}${LANG}</details>` : '';
    }

    // ================= AJAN PAKETİ =================
    function outdated() {
        const staged = normV(S.ver && S.ver.staged_version);
        return S.devices.filter(d => staged && normV(d.agent_version) !== staged);
    }

    function renderAgentPackage() {
        const v = S.ver;
        if (!v) return;
        $('ag-latest').textContent = v.latest ? fmtV(v.latest) : (v.checked_github ? '—' : '?');
        $('ag-latest-sub').textContent = v.latest ? '' : 'GitHub\'a ulaşılamadı';
        $('ag-staged').textContent = fmtV(v.staged_version);
        $('ag-staged-sub').textContent = v.staged_version
            ? (v.release_available ? 'GitHub\'daki son sürüm değil' : 'GitHub\'daki son sürümle aynı')
            : 'Henüz paket yok';

        $('fetch-row').style.display = v.release_available ? 'flex' : 'none';
        $('btn-fetch').dataset.tag = v.latest || '';
        $('btn-fetch-lbl').textContent = `${fmtV(v.latest)} indir ve doğrula`;

        const old = outdated();
        let b;
        if (v.release_available) b = badge('warn', 'fa-circle-up', `Yeni sürüm var: ${fmtV(v.latest)}`);
        else if (!v.staged_version) b = badge('muted', 'fa-box-open', 'Paket yok');
        else if (old.length) {
            const off = old.filter(d => !isOnline(d)).length;
            // Çevrimdışı olanlar eski sürümdekilerin İÇİNDEN sayılır: "1 ajan eski sürümde (1 çevrimdışı)" iki ayrı cihaz gibi okunuyordu
            const offTxt = !off ? '' : off === old.length ? (old.length === 1 ? ', çevrimdışı' : ', hepsi çevrimdışı') : `, ${off} tanesi çevrimdışı`;
            b = badge('warn', 'fa-circle-up', `${old.length} ajan eski sürümde${offTxt}`);
        }
        else b = badge('ok', 'fa-check', 'Tüm ajanlar güncel');
        $('ag-badge').innerHTML = b;
    }

    $('btn-fetch').addEventListener('click', async function () {
        this.disabled = true;
        msg('fetch-status', '', '<i class="fas fa-spinner fa-spin"></i> GitHub\'dan indiriliyor ve doğrulanıyor…');
        try {
            const d = await postJson('/api/system/fetch-release', { tag: this.dataset.tag || null });
            msg('fetch-status', 'status-success', `<i class="fas fa-circle-check"></i> ${escapeHtml(fmtV(d.version))} indirildi ve doğrulandı. Şimdi aşağıdan hedef seçip gönderebilirsiniz.`);
            await loadAll(false);
        } catch (e) {
            msg('fetch-status', 'status-error', '<i class="fas fa-circle-xmark"></i> ' + escapeHtml(e.message));
        } finally { this.disabled = false; }
    });

    let chosen = [];
    function renderFiles() {
        $('file-list').innerHTML = chosen.map(f => `<li><i class="fas fa-file"></i> ${escapeHtml(f.name)} <span class="muted-text">(${Math.round(f.size / 1024)} KB)</span></li>`).join('');
        $('btn-upload').disabled = chosen.length === 0;
    }
    $('files').addEventListener('change', (e) => { chosen = Array.from(e.target.files); renderFiles(); });
    const uz = $('uz');
    ['dragover', 'dragenter'].forEach(ev => uz.addEventListener(ev, (e) => { e.preventDefault(); uz.classList.add('dragover'); }));
    ['dragleave', 'drop'].forEach(ev => uz.addEventListener(ev, () => uz.classList.remove('dragover')));
    uz.addEventListener('drop', (e) => { e.preventDefault(); chosen = Array.from(e.dataTransfer.files); renderFiles(); });
    $('btn-upload').addEventListener('click', async function () {
        this.disabled = true;
        msg('upload-status', '', '<i class="fas fa-spinner fa-spin"></i> Doğrulanıyor…');
        const fd = new FormData();
        chosen.forEach(f => fd.append('files', f));
        fd.append('force', $('force').checked ? 'true' : 'false');
        try {
            const d = await api('/api/system/upload-release', { method: 'POST', body: fd });
            msg('upload-status', 'status-success', `<i class="fas fa-circle-check"></i> ${escapeHtml(fmtV(d.version))} doğrulandı ve kaydedildi.`);
            await loadAll(false);
        } catch (e) {
            msg('upload-status', 'status-error', '<i class="fas fa-circle-xmark"></i> ' + escapeHtml(e.message));
        } finally { this.disabled = chosen.length === 0; }
    });

    // ================= AJAN HEDEF + GÖNDER =================
    document.querySelectorAll('.option-card[data-mode]').forEach(c => c.addEventListener('click', () => {
        S.mode = c.dataset.mode;
        document.querySelectorAll('.option-card[data-mode]').forEach(x => x.classList.toggle('active', x === c));
        $('tgt-lab').style.display = S.mode === 'LAB' ? 'block' : 'none';
        $('tgt-pc').style.display = S.mode === 'PC' ? 'block' : 'none';
        renderDeploy();
    }));

    function renderTargets() {
        const devs = S.devices;
        const online = devs.filter(isOnline);
        $('opt-all-desc').textContent = `${devs.length} cihaz · ${online.length} açık`;

        const labs = [...new Set(devs.map(d => d.lab).filter(Boolean))].sort();
        const labSel = $('lab-select');
        const prevLab = labSel.value;
        labSel.innerHTML = '<option value="">Sınıf seçin…</option>' + labs.map(l => {
            const n = devs.filter(d => d.lab === l), on = n.filter(isOnline).length;
            return `<option value="${escapeHtml(l)}">${escapeHtml(l)} — ${n.length} cihaz, ${on} açık</option>`;
        }).join('');
        if (labs.includes(prevLab)) labSel.value = prevLab;

        const checked = new Set([...document.querySelectorAll('#dev-list input:checked')].map(i => i.value));
        const staged = normV(S.ver && S.ver.staged_version);
        const sorted = [...devs].sort((a, b) => (isOnline(b) - isOnline(a)) || devName(a).localeCompare(devName(b), 'tr', { numeric: true }));
        $('dev-list').innerHTML = sorted.length ? sorted.map(d => {
            const on = isOnline(d);
            const old = staged && normV(d.agent_version) !== staged;
            return `<label class="dev-row${on ? '' : ' offline'}">
                <input type="checkbox" value="${escapeHtml(d.hw_id)}" ${on ? '' : 'disabled'} ${on && checked.has(d.hw_id) ? 'checked' : ''}>
                <span class="dot${on ? ' on' : ''}" title="${on ? 'Açık' : 'Çevrimdışı'}"></span>
                <span><span class="dev-name">${escapeHtml(devName(d))}</span>
                    <span class="dev-meta"> · ${escapeHtml(d.lab || 'sınıfsız')} · ${escapeHtml(d.hw_id)}${on ? '' : ' · çevrimdışı'}</span></span>
                <span class="ver-pill${old ? ' old' : ''}" title="${old ? 'Gönderilecek paketten farklı' : 'Güncel'}">${escapeHtml(fmtV(d.agent_version))}</span>
            </label>`;
        }).join('') : '<div class="muted-text">Kayıtlı cihaz yok.</div>';
        $('dev-list').querySelectorAll('input').forEach(i => i.addEventListener('change', renderDeploy));
    }
    $('lab-select').addEventListener('change', renderDeploy);
    $('btn-select-outdated').addEventListener('click', () => {
        const old = new Set(outdated().filter(isOnline).map(d => d.hw_id));
        $('dev-list').querySelectorAll('input:not(:disabled)').forEach(i => { i.checked = old.has(i.value); });
        renderDeploy();
    });

    function deployTargets() {
        if (S.mode === 'ALL') return { body: { target_mode: 'ALL', targets: [] }, list: S.devices };
        if (S.mode === 'LAB') {
            const lab = $('lab-select').value;
            return { body: { target_mode: 'LAB', targets: lab ? [lab] : [] }, list: lab ? S.devices.filter(d => d.lab === lab) : [] };
        }
        const ids = [...document.querySelectorAll('#dev-list input:checked')].map(i => i.value);
        return { body: { target_mode: 'PC', targets: ids }, list: S.devices.filter(d => ids.includes(d.hw_id)) };
    }

    function renderDeploy() {
        const staged = S.ver && S.ver.staged_version;
        const t = deployTargets();
        const on = t.list.filter(isOnline);
        const old = on.filter(d => normV(d.agent_version) !== normV(staged));
        let reason = '';
        if (!staged) reason = 'Önce 1. adımda bir paket indirin ya da yükleyin.';
        else if (S.ver.release_available) reason = `Gönderilecek paket (${fmtV(staged)}) GitHub'daki son sürüm değil. Önce 1. adımda ${fmtV(S.ver.latest)} sürümünü indirin.`;
        else if (S.mode === 'LAB' && !$('lab-select').value) reason = 'Bir sınıf seçin.';
        else if (S.mode === 'PC' && !t.list.length) reason = 'En az bir cihaz seçin.';
        else if (!on.length) reason = 'Seçimde açık cihaz yok; çevrimdışı cihazlara gönderilemez.';
        else if (!old.length) reason = `Seçimdeki açık cihazların hepsi zaten ${fmtV(staged)} sürümünde.`;
        const same = on.length - old.length, off = t.list.length - on.length;
        $('dep-summary').innerHTML = reason ? escapeHtml(reason)
            : `<strong>${old.length}</strong> açık cihaz ${escapeHtml(fmtV(staged))} sürümüne güncellenecek.`
              + (same ? ` ${same} cihaz zaten bu sürümde.` : '') + (off ? ` ${off} çevrimdışı cihaz atlanacak.` : '');
        $('btn-deploy').disabled = !!reason;
        $('btn-deploy-lbl').textContent = staged ? `${fmtV(staged)} sürümünü gönder` : 'Gönder';
    }

    $('btn-deploy').addEventListener('click', async function () {
        const staged = S.ver && S.ver.staged_version;
        const t = deployTargets();
        const n = t.list.filter(d => isOnline(d) && normV(d.agent_version) !== normV(staged)).length;
        if (!confirm(`${n} cihaz ${fmtV(staged)} sürümüne güncellenecek. Devam edilsin mi?`)) return;
        this.disabled = true;
        msg('deploy-status', '', '<i class="fas fa-spinner fa-spin"></i> Gönderiliyor…');
        try {
            const d = await postJson('/api/system/deploy-update', t.body);
            const off = (d.skipped_offline || []).length;
            msg('deploy-status', 'status-success', `<i class="fas fa-circle-check"></i> ${escapeHtml(fmtV(d.version))} ${(d.dispatched || []).length} cihaza gönderildi`
                + (off ? `, ${off} çevrimdışı cihaz atlandı` : '') + '. Ajanlar birkaç dakika içinde yeniden bağlanıp yeni sürümü bildirir; liste kendiliğinden yenilenir.');
            [30, 90, 180].forEach(s => setTimeout(() => loadAll(false), s * 1000));
        } catch (e) {
            msg('deploy-status', 'status-error', '<i class="fas fa-circle-xmark"></i> ' + escapeHtml(e.message));
        } finally { renderDeploy(); }
    });

    // ================= YETENEKLER =================
    function capLabel(v) {
        if (v === true) return '<span class="badge ok">açık</span>';
        if (v === false) return '<span class="badge bad">kapalı</span>';
        return '<span class="badge muted">bildirilmedi</span>';
    }
    function renderCapDevices() {
        const sel = $('cap-device');
        const prev = sel.value;
        sel.innerHTML = '<option value="">Cihaz seçin…</option>' + S.devices.map(d =>
            `<option value="${escapeHtml(d.hw_id)}">${escapeHtml(devName(d))} — ${escapeHtml(d.hw_id)} · ${escapeHtml(fmtV(d.agent_version))}${isOnline(d) ? '' : ' (çevrimdışı)'}</option>`).join('');
        if (prev && S.devices.some(d => d.hw_id === prev)) sel.value = prev;
        renderCapState();
    }
    function renderCapState() {
        const d = S.devices.find(x => x.hw_id === $('cap-device').value);
        const btns = ['cap-off-terminal', 'cap-on-terminal', 'cap-off-vision', 'cap-on-vision'].map($);
        if (!d) { $('cap-state').innerHTML = ''; btns.forEach(b => b.disabled = true); return; }
        capButtons('terminal', d.cap_terminal_enabled, d.cap_terminal_disable_requested, btns[0], btns[1]);
        capButtons('vision', d.cap_vision_enabled, d.cap_vision_disable_requested, btns[2], btns[3]);
        // Kapalı bir yetenek ya kurulumda (MSI) ya da panelden kapatılmıştır; panelden kapatılan kalıcı kilitlidir
        const req = (r, v) => r ? ' <span class="muted-text">(panelden kalıcı kapatıldı)</span>'
            : v === false ? ' <span class="muted-text">(kurulumda kapatılmış)</span>' : '';
        const caLabel = d.cap_server_ca === 'custom' ? '<span class="badge ok">kurum CA\'sı</span>'
            : d.cap_server_ca === 'system' ? '<span class="badge muted">sistem deposu</span>'
            : '<span class="badge muted">bildirilmedi</span>';
        let html = `Terminal: ${capLabel(d.cap_terminal_enabled)}${req(d.cap_terminal_disable_requested, d.cap_terminal_enabled)} &nbsp;·&nbsp; Vision: ${capLabel(d.cap_vision_enabled)}${req(d.cap_vision_disable_requested, d.cap_vision_enabled)} &nbsp;·&nbsp; Sunucu sertifikası: ${caLabel}`;
        if (d.cap_terminal_enabled == null && d.cap_vision_enabled == null) {
            html += `<br><span class="muted-text"><i class="fas fa-circle-info"></i> Bu cihazdaki ajan (${escapeHtml(fmtV(d.agent_version))}) yetenek durumunu bildirmiyor; bildirim v0.1.4-alpha ile geldi. Ajan güncellenince burada görünür. Kapatma şimdi de kaydedilebilir, güncellemeden sonra uygulanır.</span>`;
        }
        $('cap-state').innerHTML = html + agentHealthHtml(d);
    }
    // Kapat / izin ver düğmeleri: yetenek uzaktan hiçbir zaman AÇILAMAZ. "İzin ver" yalnızca panelden konan kalıcı
    // kapatmayı kaldırır; kurulumda kapatılmış yetenekte "Kapalı tut" ajan açık kurulsa bile kapalı kalmasını sağlar.
    function capButtons(which, enabled, requested, off, on) {
        const flag = which === 'terminal' ? 'TERMINAL_ENABLED=1' : 'VISION_ENABLED=1';
        const offIcon = which === 'terminal' ? 'fa-terminal' : 'fa-video-slash';
        off.disabled = !!requested;
        on.disabled = !requested;
        const obj = which === 'terminal' ? 'Terminali' : "Vision'ı";
        off.innerHTML = `<i class="fas ${offIcon}"></i> ${obj} ` + (enabled === false && !requested ? 'kapalı tut' : 'kapat');
        off.title = requested ? 'Panelden zaten kalıcı kapatılmış.'
            : enabled === false ? `Kurulumda kapatılmış. Kapalı tut: ajan ${flag} ile yeniden kurulsa bile kapalı kalır.` : '';
        on.title = requested ? 'Panelden konan kalıcı kapatmayı kaldırır; yetenek ancak ajan kurulumu onu açık bildirirse geri gelir.'
            : enabled === false ? `Uzaktan açılamaz. Açmak için ajanı ${flag} ile yeniden kurun.` : 'Kaldırılacak bir kapatma yok.';
    }
    // Ajanın heartbeat'te bildirdiği durum (0.1.12+) ve çevrimdışı bypass anahtarı
    function agentHealthHtml(d) {
        const h = d.agent_health;
        const ago = (t) => t ? fmtDur(Date.now() / 1000 - t) + ' önce' : 'henüz yok';
        const bypass = d.bypass_key === 'device' ? '<span class="badge ok">cihaza özel</span>'
            : d.bypass_key === 'pending' ? '<span class="badge muted">gönderildi, onay bekleniyor</span>'
            : '<span class="badge muted">ortak anahtar (eski)</span>';
        let out = `<br>Çevrimdışı bypass anahtarı: ${bypass}`;
        if (!h) {
            return out + `<br><span class="muted-text"><i class="fas fa-circle-info"></i> Bu ajan (${escapeHtml(fmtV(d.agent_version))}) durum bildirmiyor; bildirim v0.1.12-alpha ile geldi.</span>`;
        }
        const vision = { off: 'kapalı', idle: 'boşta', connected: 'bağlı' }[h.vision_channel] || 'bilinmiyor';
        const errs = h.loop_errors_1h || 0;
        out += `<br>Ajan${isOnline(d) ? '' : ' (son bilinen)'}: açılış ${escapeHtml(ago(h.started_at))} &nbsp;·&nbsp; politika eşitleme ${escapeHtml(ago(h.last_policy_sync))}`
            + ` &nbsp;·&nbsp; envanter ${escapeHtml(ago(h.last_inventory_upload))}`
            + ` &nbsp;·&nbsp; tepsi: ${h.tray_connected ? '<span class="badge ok">bağlı</span>' : '<span class="badge bad">bağlı değil</span>'}`
            + ` &nbsp;·&nbsp; Vision kanalı: ${escapeHtml(vision)}`
            + `<br>Son 1 saatte hata: ${errs ? `<span class="badge bad">${errs}</span>` : '<span class="badge ok">0</span>'}`;
        if (errs && h.last_error) out += ` <span class="muted-text">son hata: ${escapeHtml(h.last_error)}</span>`;
        // Karantina (0.1.13+): kilit ekranı ve ağ yalıtımı ayrı ayrı
        if (h.screen_locked) {
            out += `<br>Karantina: kilit ekranı ${'<span class="badge bad">açık</span>'} &nbsp;·&nbsp; ağ yalıtımı `
                + (h.network_isolated ? '<span class="badge ok">uygulandı</span>'
                   : `<span class="badge bad">UYGULANAMADI</span>${h.isolation_error ? ' <span class="muted-text">' + escapeHtml(h.isolation_error) + '</span>' : ''}`);
        }
        return out;
    }
    async function capSet(which, enabled) {
        const hw = $('cap-device').value;
        if (!hw) return;
        const label = which === 'terminal' ? 'terminali' : "Vision'ı";
        const dev = S.devices.find(x => x.hw_id === hw) || {};
        const alreadyOff = (which === 'terminal' ? dev.cap_terminal_enabled : dev.cap_vision_enabled) === false;
        if (!enabled && !confirm(alreadyOff
            ? `Bu cihazda ${label} kurulumda kapatılmış. "Kapalı tut" ile ajan açık kurulsa bile kapalı kalır; geri açmak için "İzin ver" ve ajanın yeniden kurulumu gerekir. Devam edilsin mi?`
            : `Bu cihazda ${label} KAPATMAK üzeresiniz. Kalıcıdır; geri açmak için "İzin ver" ve ajanın yeniden kurulumu gerekir. Devam edilsin mi?`)) return;
        const body = { pc_name: hw };
        body[which === 'terminal' ? 'terminal_enabled' : 'vision_enabled'] = !!enabled;
        msg('cap-status', '', 'Gönderiliyor…');
        try {
            const d = await postJson('/api/system/set-capabilities', body);
            msg('cap-status', 'status-success', enabled
                ? 'Kapatma isteği kaldırıldı. Yetenek, ajan kurulumu onu açık bildirdiğinde geri gelir.'
                : (d.delivered_online ? 'Kapatma gönderildi.' : 'Kapatma kaydedildi; cihaz bağlanınca uygulanacak.'));
            setTimeout(() => loadAll(false), 1200);
        } catch (e) { msg('cap-status', 'status-error', escapeHtml(e.message)); }
    }
    $('cap-device').addEventListener('change', renderCapState);
    $('cap-off-terminal').addEventListener('click', () => capSet('terminal', false));
    $('cap-on-terminal').addEventListener('click', () => capSet('terminal', true));
    $('cap-off-vision').addEventListener('click', () => capSet('vision', false));
    $('cap-on-vision').addEventListener('click', () => capSet('vision', true));

    // ================= KAYIT + ZORLAMA =================
    async function loadEnroll() {
        try {
            const rows = await api('/api/system/enroll-tokens');
            $('enroll-list').innerHTML = (rows || []).slice(0, 20).map(r => {
                const state = r.expired ? 'süresi doldu' : (r.is_used ? 'tükendi' : 'geçerli');
                // Jetonun kendisi saklanmaz: yalnızca ilk karakterleri (tanımak için)
                return `<li><i class="fas fa-ticket"></i> <code>${escapeHtml(r.token_hint || '')}…</code>
                    <span class="muted-text">${escapeHtml(r.lab_name || 'tüm sınıflar')} · ${r.use_count || 0}/${r.max_uses || 1} kullanım · ${state}</span>
                    <button class="btn small" data-id="${r.id}">sil</button></li>`;
            }).join('') || '<li class="muted-text">Jeton yok.</li>';
            $('enroll-list').querySelectorAll('button[data-id]').forEach(b => b.addEventListener('click', async () => {
                if (!confirm('Jeton silinsin mi? Bu jetonla henüz kaydolmamış kurulumlar kaydolamaz.')) return;
                await fetch('/api/system/enroll-token/' + b.dataset.id, { method: 'DELETE' });
                loadEnroll();
            }));
        } catch (e) { $('enroll-list').innerHTML = '<li class="muted-text">Jetonlar alınamadı.</li>'; }
    }
    $('btn-enroll').addEventListener('click', async () => {
        msg('enroll-status', '', 'Üretiliyor…');
        try {
            const d = await postJson('/api/system/enroll-token', {
                lab_name: $('et-lab').value || null, note: $('et-note').value || null,
                ttl_hours: parseInt($('et-ttl').value) || 72, max_uses: parseInt($('et-uses').value) || 1
            });
            msg('enroll-status', 'status-success', `<i class="fas fa-circle-check"></i> MSI kurulumunda kullanın: <code>ENROLL_TOKEN=${escapeHtml(d.token)}</code><br><span class="muted-text">Jeton yalnızca şimdi gösterilir, sunucuda saklanmaz; şimdi kopyalayın.</span>`);
            loadEnroll();
        } catch (e) { msg('enroll-status', 'status-error', escapeHtml(e.message)); }
    });

    function renderEnforce() {
        const v = S.ver;
        if (!v) return;
        const on = !!v.enforce_agent_auth, total = v.agents_total || 0, enr = v.agents_enrolled || 0;
        $('enroll-count').innerHTML = badge(enr === total ? 'ok' : 'muted', 'fa-id-card', `${enr}/${total} ajan kayıtlı`);
        $('enforce-badge').innerHTML = on ? badge('ok', 'fa-lock', 'Açık') : badge('muted', 'fa-lock-open', 'Kapalı');
        $('enforce-hint').textContent = on
            ? 'Anahtarı olmayan ajanlar bağlanamaz.'
            : (enr < total ? `Geçiş modu: kayıtsız ajanlar da bağlanabilir. Açmadan önce ${total - enr} ajan kaydolmalı.` : 'Tüm ajanlar kayıtlı; açılabilir.');
        const b = $('btn-enforce');
        b.textContent = on ? 'Zorlamayı kapat' : 'Zorlamayı aç';
        b.dataset.on = on ? '1' : '0';
        b.classList.toggle('primary', !on && enr === total && total > 0);
    }
    $('btn-enforce').addEventListener('click', async () => {
        const turnOn = $('btn-enforce').dataset.on !== '1';
        const v = S.ver || {};
        const missing = (v.agents_total || 0) - (v.agents_enrolled || 0);
        if (turnOn && !confirm(missing > 0
            ? `${missing} ajan kayıtlı değil ve zorlama açılınca bağlantısını kaybeder. Yine de açılsın mı?`
            : 'Zorlama açılacak: anahtarı olmayan ajan bağlanamaz. Devam edilsin mi?')) return;
        msg('enforce-status', '', '…');
        try {
            const d = await postJson('/api/system/enforce-auth', { enabled: turnOn });
            S.ver.enforce_agent_auth = d.enforce_agent_auth;
            renderEnforce();
            msg('enforce-status', 'status-success', d.enforce_agent_auth ? 'Zorlama açıldı.' : 'Zorlama kapatıldı.');
        } catch (e) { msg('enforce-status', 'status-error', escapeHtml(e.message)); }
    });

    // ================= BİLDİRİMLER =================
    const ntBody = () => ({ enabled: $('nt-enabled').checked, min_severity: $('nt-sev').value,
                            email_to: $('nt-email').value.trim(), webhook_url: $('nt-webhook').value.trim() });
    function renderNotify(d) {
        $('nt-enabled').checked = !!d.enabled;
        $('nt-sev').value = d.min_severity || 'high';
        $('nt-email').value = d.email_to || '';
        $('nt-webhook').value = d.webhook_url || '';
        $('nt-smtp').innerHTML = d.smtp_configured
            ? '<i class="fas fa-circle-check" style="color:var(--success-solid)"></i> Sunucuda SMTP ayarlı; e-posta gönderilebilir.'
            : '<i class="fas fa-circle-info"></i> E-posta için sunucunun <code>.env</code> dosyasında <code>SMTP_HOST</code> ve <code>SMTP_FROM</code> tanımlanmalı. Webhook ek ayar gerektirmez.';
        const on = d.enabled && (d.email_to || d.webhook_url);
        $('nt-badge').innerHTML = on ? badge('ok', 'fa-bell', 'Açık') : badge('muted', 'fa-bell-slash', 'Yalnızca zil');
    }
    async function loadNotify() {
        try { renderNotify(await api('/api/system/notify-settings')); }
        catch (e) { $('nt-badge').innerHTML = badge('muted', 'fa-plug', 'Sunucu güncellemesi gerekli'); }
    }
    $('nt-save').addEventListener('click', async () => {
        msg('nt-status', '', 'Kaydediliyor…');
        try { renderNotify(await postJson('/api/system/notify-settings', ntBody())); msg('nt-status', 'status-success', 'Kaydedildi.'); }
        catch (e) { msg('nt-status', 'status-error', escapeHtml(e.message)); }
    });
    $('nt-test').addEventListener('click', async () => {
        msg('nt-status', '', '<i class="fas fa-spinner fa-spin"></i> Gönderiliyor…');
        try {
            const d = await postJson('/api/system/notify-test', ntBody());
            if (d.error) msg('nt-status', 'status-error', 'Gönderilemedi: ' + escapeHtml(d.error) + (d.channels.length ? ' (başarılı: ' + escapeHtml(d.channels.join(', ')) + ')' : ''));
            else msg('nt-status', 'status-success', 'Test bildirimi gönderildi: ' + escapeHtml(d.channels.join(', ')));
        } catch (e) { msg('nt-status', 'status-error', escapeHtml(e.message)); }
    });

    // ================= SUNUCU SAĞLIĞI =================
    function fmtDur(sec) {
        sec = Math.max(0, Math.round(sec || 0));
        const d = Math.floor(sec / 86400), h = Math.floor(sec % 86400 / 3600), m = Math.floor(sec % 3600 / 60);
        return d ? d + ' gün ' + h + ' sa' : h ? h + ' sa ' + m + ' dk' : m + ' dk';
    }
    const tile = (lbl, val, sub) => `<div class="ver-tile"><div class="lbl">${escapeHtml(lbl)}</div><div class="val">${escapeHtml(String(val))}</div><div class="sub">${sub || ''}</div></div>`;
    function renderDiag(d) {
        const errs = (d.log_counts && d.log_counts.ERROR || 0) + (d.log_counts && d.log_counts.CRITICAL || 0);
        const tickAge = d.scheduler_last_tick_age;
        const tickBad = tickAge === null || tickAge > 120;
        const pool = d.db_pool || {};
        const poolBusy = (pool.size || 0) - (pool.idle || 0);
        const dev = d.devices || {};
        $('dg-tiles').innerHTML = [
            tile('Açık kalma süresi', fmtDur(d.uptime_seconds), 'Bellek: ' + (d.rss_mb != null ? d.rss_mb + ' MB' : '—')),
            tile('Bağlı ajan', d.agents_connected, escapeHtml((dev.online || 0) + ' çevrimiçi / ' + (dev.total || 0) + ' kayıtlı cihaz')),
            tile('Veritabanı bağlantısı', poolBusy + ' / ' + (pool.max || '—'), 'kullanımda / en çok'),
            tile('Hata', errs, escapeHtml((d.http_5xx || 0) + ' sunucu hatası yanıtı, ' + (d.log_counts && d.log_counts.WARNING || 0) + ' uyarı')),
            tile('Zamanlayıcı', tickAge === null ? 'başlamadı' : fmtDur(tickAge) + ' önce', tickBad ? '<span style="color:var(--danger-solid)">30 sn\'de bir çalışmalı</span>' : 'son tur'),
            backupTile(d.backup),
        ].join('');
        const list = d.recent_errors || [];
        $('dg-errors-wrap').style.display = list.length ? '' : 'none';
        $('dg-errors-title').textContent = 'Son hatalar (' + list.length + ')';
        $('dg-errors').innerHTML = list.map(e => `<li><div class="meta-line">${escapeHtml(fmtDate(e.ts))} · ${escapeHtml(e.logger)}${e.request_id ? ' · istek ' + escapeHtml(e.request_id) : ''}</div>${escapeHtml(e.msg)}${e.exc ? '<div class="meta-line">' + escapeHtml(e.exc) + '</div>' : ''}</li>`).join('');
        const bk = backupState(d.backup);
        const bad = errs > 0 || tickBad || bk !== 'ok';
        const why = errs ? errs + ' hata' : tickBad ? 'Zamanlayıcı durdu' : bk === 'none' ? 'Yedek yok' : bk === 'old' ? 'Yedek eski' : 'Yedek başarısız';
        $('dg-badge').innerHTML = bad ? badge('warn', 'fa-triangle-exclamation', why) : badge('ok', 'fa-circle-check', 'Sağlıklı');
        $('dg-foot').innerHTML = d.metrics_enabled
            ? '<i class="fas fa-chart-line"></i> Prometheus <code>/metrics</code> açık.'
            : '<i class="fas fa-circle-info"></i> Prometheus ile izlemek için sunucunun <code>.env</code> dosyasında <code>METRICS_TOKEN</code> tanımlayın.';
    }
    // Son yedek: yok / başarısız / 2 günden eski ise uyarı (gece yedeği pops-backup.timer ile alınır)
    function backupState(b) {
        if (!b || !b.at) return 'none';
        if (!b.ok || !b.verified) return 'failed';
        return (Date.now() - Date.parse(b.at)) / 1000 > 2 * 86400 ? 'old' : 'ok';
    }
    function backupTile(b) {
        const st = backupState(b);
        if (st === 'none') return tile('Son yedek', 'yok', '<span style="color:var(--danger-solid)">Gece yedeği kurulu değil (docs/backup.md)</span>');
        const age = fmtDur((Date.now() - Date.parse(b.at)) / 1000) + ' önce';
        if (st === 'failed') return tile('Son yedek', age, '<span style="color:var(--danger-solid)">' + escapeHtml(b.message || 'başarısız') + '</span>');
        const size = b.bytes ? (b.bytes > 1048576 ? (b.bytes / 1048576).toFixed(1) + ' MB' : Math.round(b.bytes / 1024) + ' KB') : '';
        return tile('Son yedek', age, st === 'old' ? '<span style="color:var(--danger-solid)">2 günden eski</span>' : escapeHtml(size + ' · geri yükleme sınandı'));
    }
    async function loadDiag() {
        try { renderDiag(await api('/api/system/diagnostics')); }
        catch (e) { $('dg-badge').innerHTML = badge('muted', 'fa-plug', 'Sunucu güncellemesi gerekli'); }
    }

    // ================= YÜKLE =================
    async function loadAll(check) {
        const [ver, , devs] = await Promise.all([
            api('/api/system/version' + (check ? '?check=true' : '')).catch(() => null),
            loadSelfUpdate(),
            api('/api/devices').catch(() => null),
        ]);
        if (ver) S.ver = ver;
        if (Array.isArray(devs)) S.devices = devs;
        if (!S.ver) {
            $('srv-badge').innerHTML = badge('bad', 'fa-triangle-exclamation', 'Sürüm bilgisi alınamadı');
            return;
        }
        renderServer();
        renderAgentPackage();
        renderTargets();
        renderDeploy();
        renderCapDevices();
        renderEnforce();
        loadNotes();
        $('last-check').textContent = 'Son kontrol: ' + new Date().toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit' });
    }

    $('btn-check').addEventListener('click', async function () {
        this.disabled = true;
        const i = this.querySelector('i'); i.classList.add('fa-spin');
        await Promise.all([loadAll(true), loadDiag()]);
        i.classList.remove('fa-spin'); this.disabled = false;
    });

    loadAll(false);
    loadEnroll();
    loadNotify();
    loadDiag();
})();
</script>
