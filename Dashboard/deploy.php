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
    }
</style>

<div class="page-header">
    <div>
        <h1>Dağıtım</h1>
        <div class="summary" id="depSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="chainBtn" data-tip="Görev zinciri oluştur" aria-label="Görev zinciri oluştur"><?php echo pops_icon('list'); ?></button>
        <button type="button" class="ibtn boxed" id="limitBtn" data-tip="Eşzamanlı kurulum sınırı" aria-label="Eşzamanlı kurulum sınırı"><?php echo pops_icon('sliders'); ?></button>
        <button type="button" class="btn" id="uploadBtn"><?php echo pops_icon('upload', 'sm'); ?>Paket yükle</button>
    </div>
</div>

<div class="dep-bar">
    <div class="segmented" id="depFilter" role="group" aria-label="Türe göre süz"></div>
    <span class="grow"></span>
    <div class="search-field">
        <?php echo pops_icon('search', 'sm'); ?>
        <input type="search" id="depSearch" placeholder="Ad, dosya, özet ya da yükleyen" aria-label="Paket ara">
    </div>
</div>

<div class="table-wrap">
    <table class="data-table dep-table">
        <thead><tr><th>Ad</th><th class="c-size">Boyut</th><th class="c-hash">SHA-256</th><th class="c-added">Eklendi</th><th class="c-last">Son dağıtım</th></tr></thead>
        <tbody id="depBody"><tr><td colspan="5"><div class="loading-state" role="status"><span class="spinner"></span>Paketler yükleniyor…</div></td></tr></tbody>
    </table>
</div>

<!-- Paket / betik ekleme ve düzenleme -->
<div class="modal-overlay" id="pkgModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="pkgTitle">Paket yükle</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <div class="segmented block" id="pkgType" role="group" aria-label="Tür" style="margin-bottom:18px">
                <button type="button" data-t="package" class="active" aria-pressed="true">Kurulum paketi</button>
                <button type="button" data-t="script" aria-pressed="false">Betik</button>
            </div>
            <div class="field">
                <label for="pkgName">Ad</label>
                <input type="text" id="pkgName" maxlength="120" placeholder="Örn. SumatraPDF" autocomplete="off">
            </div>
            <div id="pkgFileArea">
                <div class="field">
                    <span class="field-label">Kurulum dosyası</span>
                    <label class="drop" id="pkgDrop">
                        <input type="file" id="pkgFile" aria-label="Kurulum dosyası">
                        <span class="drop-ico"><?php echo pops_icon('upload'); ?></span>
                        <span class="drop-t" id="pkgDropT">Dosya seçin ya da buraya bırakın</span>
                        <span class="drop-s" id="pkgDropS">.exe, .msi ya da install.bat içeren .zip</span>
                    </label>
                    <div class="up-prog" id="pkgProg" hidden><div class="pbar"><i class="run" id="pkgProgBar" style="width:0%"></i></div><span id="pkgProgTxt">%0</span></div>
                </div>
                <div class="field">
                    <label for="pkgArgs">Sessiz kurulum parametreleri</label>
                    <input type="text" id="pkgArgs" class="mono" placeholder="/S" autocomplete="off" spellcheck="false">
                    <div class="field-hint">MSI için /qn /norestart kendiliğinden eklenir. Kurulum adımları hedefte C:\POpsLogs\deploy_trace.txt dosyasına yazılır.</div>
                </div>
                <details class="zip-help">
                    <summary>ZIP paketi nasıl hazırlanır?</summary>
                    <ul>
                        <li>ZIP'in ana dizininde (alt klasörde değil) install.bat bulunmalı.</li>
                        <li>POps ZIP'i hedefte açar ve install.bat'ı yukarıdaki parametrelerle yönetici olarak çalıştırır.</li>
                        <li>Kurulum pencere açmadan bitsin diye install.bat içindeki komutlara sessiz kurulum parametrelerini (/S, /qn, /quiet) ekleyin.</li>
                    </ul>
                    <pre>:: install.bat örneği
setup.exe /S
msiexec.exe /i program.msi /qn /norestart</pre>
                </details>
            </div>
            <div id="pkgCodeArea" hidden>
                <div class="field">
                    <label for="pkgCode">Komut</label>
                    <textarea id="pkgCode" rows="7" class="mono" spellcheck="false" placeholder="Örn. w32tm /resync"></textarea>
                    <div class="field-hint">PowerShell ya da CMD komutları; hedefte SYSTEM hesabıyla çalışır.</div>
                </div>
            </div>
            <div class="opt-row">
                <div class="grow"><div class="t">Bitince yeniden başlat</div><div class="d" id="pkgRebootHint">Başarılı kurulumdan 15 saniye sonra bilgisayar yeniden başlar.</div></div>
                <label class="switch"><input type="checkbox" id="pkgReboot" aria-label="Bitince yeniden başlat"><span></span></label>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="pkgSave">Yükle ve ekle</button>
        </div>
    </div>
</div>

<!-- Dağıtım: adımlar, hedefler, gerekçe -->
<div class="modal-overlay" id="runModal">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="runTitle">Dağıt</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><?php echo pops_icon('x'); ?></button>
        </div>
        <div class="modal-body">
            <section class="run-sec">
                <div class="run-h"><span>Adımlar</span><span class="faint" id="runStepsNote">Hedefte sırayla çalışır</span></div>
                <div class="run-steps" id="runSteps"></div>
                <select id="runAdd" class="run-add" aria-label="Adım ekle"></select>
            </section>
            <section class="run-sec">
                <div class="run-h">
                    <span>Hedef</span>
                    <div class="segmented" id="runMode" role="group" aria-label="Hedef türü">
                        <button type="button" data-m="lab" class="active" aria-pressed="true">Sınıflar</button>
                        <button type="button" data-m="pc" aria-pressed="false">Bilgisayarlar</button>
                    </div>
                </div>
                <div class="search-field" style="max-width:none">
                    <?php echo pops_icon('search', 'sm'); ?>
                    <input type="search" id="runSearch" placeholder="Sınıf ya da bilgisayar ara" aria-label="Hedef ara" autofocus>
                </div>
                <div class="run-list" id="runList"></div>
                <div class="run-sum" id="runSum" aria-live="polite"></div>
            </section>
            <div class="field">
                <label for="runReason">Gerekçe <span class="faint">(isteğe bağlı)</span></label>
                <input type="text" id="runReason" maxlength="300" placeholder="Örn. bilişim dersi için PDF okuyucu" autocomplete="off">
                <div class="field-hint">İşlem geçmişine ve denetim kaydına yazılır.</div>
            </div>
        </div>
        <div class="modal-footer">
            <span class="run-foot-note">Adımlar SYSTEM hesabıyla çalışır.</span>
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="runGo" disabled>Dağıt</button>
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
    const KIND = { msi: 'MSI paketi', exe: 'Kurulum (EXE)', zip: 'ZIP · install.bat' };
    function info(p) {
        const meta = String(p.meta || '');
        const parts = meta.replace(/\s*\(Reboot\)/g, '').split('|').map(s => s.trim());
        const o = {
            id: String(p.id || ''), name: p.name || '(adsız)', type: p.type === 'script' ? 'script' : 'package', command: String(p.command || ''),
            reboot: meta.includes('(Reboot)'), file: '', size: '', by: parts[2] || '', hash: '', args: '', url: '', path: '', code: '', at: null, payload: null, meta
        };
        const m = /^mod-(\d{12,14})$/.exec(o.id);
        if (m) o.at = Number(m[1]);
        if (o.type === 'package') {
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
            o.kind = KIND[(o.file.split('.').pop() || '').toLowerCase()] || 'Kurulum dosyası';
        } else {
            o.code = scriptCode(o.command);
            o.kind = 'Betik';
        }
        return o;
    }
    const shortHash = (h) => h.length > 16 ? h.slice(0, 8) + '…' + h.slice(-6) : h;
    const firstLine = (s) => { const l = String(s || '').split('\n').find(x => x.trim()) || ''; return l.length > 70 ? l.slice(0, 67) + '…' : l; };
    function sizeText(bytes) {
        const n = Number(bytes) || 0;
        const fmt = (v, d) => v.toLocaleString('tr-TR', { maximumFractionDigits: d, minimumFractionDigits: d });
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
    async function copyText(text, what) {
        let ok = false;
        try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); ok = true; } } catch (e) { ok = false; }
        if (!ok) ok = legacyCopy(text);
        if (ok) POps.toast('success', `${what} kopyalandı.`);
        else POps.toast('warning', 'Kopyalanamadı; metni seçip elle kopyalayın.');
    }

    // ================= Dağıtım geçmişi (görev kayıtlarından) =================
    // Aynı istekte açılan görevler tek dağıtımdır (batch_id); eski kayıtlarda zaman + kişi + komut (İşlemler sayfasıyla aynı anahtar)
    const tsOf = (s) => (s ? String(s).replace(' ', 'T') : s);
    const jobKey = (t) => t.batch_id || ['legacy', t.created_at, t.created_by, t.script_path, t.retry_of ? 'r' : ''].join('|');
    const WAITING = ['Pending', 'Paused'];
    // Görev pakete komutuyla bağlanır; aynı komutlu iki paket varsa görevin başlığı (adımın adı) ayırır. Paket yeniden
    // yüklenince komut değişir: Dağıtım sayfasından açılmış görev adıyla da eşleşir.
    function belongs(t, o) {
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
        if (j.c.run) return j.c.wait === j.c.run && !j.tasks.some(t => t.status === 'Running') ? (j.tasks.some(t => t.status === 'Paused') ? 'Duraklatıldı' : 'Sırada') : 'Sürüyor';
        if (j.c.bad) return j.c.bad === j.c.total ? (j.c.total === 1 ? dev.statusWord(j.tasks[0].status) : 'Başarısız') : `${j.c.bad} başarısız`;
        return 'Tamamlandı';
    }
    function targetText(j) {
        if (j.c.total === 1) return dev.name(j.tasks[0].target_pc);
        return (j.labs.length === 1 ? j.labs[0] + ' · ' : j.labs.length > 1 ? j.labs.length + ' sınıf · ' : '') + j.c.total + ' bilgisayar';
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
            return [o.name, o.file, o.hash, o.args, o.code, o.by, o.kind].some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
        });
    }
    function rowHtml(o) {
        const j = (ui.deps.get(o.id) || [])[0];
        const subText = (o.type === 'package' ? [o.kind, o.file].filter(Boolean).join(' · ') : 'Betik · ' + firstLine(o.code)) + (o.reboot ? ' · yeniden başlatır' : '');
        const markHtml = o.type === 'package' && !o.hash ? dev.markHtml({ kind: 'upd', glyph: '!', text: 'Dosya özeti yok: indirilen dosya doğrulanmaz' }) : '';
        const hashHtml = o.hash
            ? `<button type="button" class="hash" data-copy="${escapeHtml(o.hash)}" title="Tam özeti kopyala" aria-label="SHA-256 özetini kopyala">${escapeHtml(shortHash(o.hash))}${POps.iconHtml('copy')}</button>`
            : '<span class="faint">—</span>';
        const addedHtml = o.at || o.by ? (o.by ? escapeHtml(o.by) + (o.at ? ' · ' : '') : '') + (o.at ? POps.timeHtml(o.at) : '') : '<span class="faint">—</span>';
        const lastHtml = j
            ? `<span class="word ${escapeHtml(j.state)}">${escapeHtml(jobWord(j))}${j.state === 'run' ? ' · ' + Number(j.c.ok + j.c.bad) + '/' + Number(j.c.total) : ''}</span><div class="cell-sub">${POps.timeHtml(tsOf(j.at))} · ${escapeHtml(targetText(j))}</div>${j.state === 'run' && j.c.total > 1 ? pbarHtml(j.c) : ''}`
            : '<span class="faint">Henüz dağıtılmadı</span>';
        return `<tr data-id="${escapeHtml(o.id)}" tabindex="0" class="${ui.focus === o.id ? 'is-focus' : ''}">
            <td><div class="dep-name"><span class="dep-ico">${POps.iconHtml(o.type === 'package' ? 'package' : 'terminal')}</span><div class="tx"><div class="t">${escapeHtml(o.name)}${markHtml}</div><div class="s" title="${escapeHtml(subText)}">${escapeHtml(subText)}</div></div></div></td>
            <td class="c-size">${o.size ? escapeHtml(o.size) : '<span class="faint">—</span>'}</td>
            <td class="c-hash">${hashHtml}</td>
            <td class="c-added">${addedHtml}</td>
            <td class="c-last">${lastHtml}</td>
        </tr>`;
    }
    function renderFilter() {
        const n = { all: ui.pkgs.length, package: ui.pkgs.filter(o => o.type === 'package').length, script: ui.pkgs.filter(o => o.type === 'script').length };
        const btnHtml = (k, label) => `<button type="button" data-f="${escapeHtml(k)}" class="${ui.f === k ? 'active' : ''}" aria-pressed="${ui.f === k ? 'true' : 'false'}">${escapeHtml(label)} <span class="n">${Number(n[k])}</span></button>`;
        $('depFilter').innerHTML = btnHtml('all', 'Tümü') + btnHtml('package', 'Paketler') + btnHtml('script', 'Betikler');
    }
    function renderSummary() {
        const nPkg = ui.pkgs.filter(o => o.type === 'package').length;
        const nScr = ui.pkgs.length - nPkg;
        const seen = new Map();
        ui.deps.forEach(list => list.forEach(j => seen.set(j.key, j)));
        const jobs = [...seen.values()];
        const run = jobs.filter(j => j.state === 'run').length;
        const badToday = jobs.filter(j => j.c.bad && isToday(j.at)).length;
        const limitHtml = ui.limit === null ? '' : ui.limit === 0
            ? '<span class="sum"><b>Sınırsız</b> eşzamanlı kurulum</span>'
            : `<span class="sum"><b>${Number(ui.limit)}</b> eşzamanlı kurulum</span>`;
        $('depSummary').innerHTML = `<span class="sum"><b>${Number(nPkg)}</b> paket</span><span class="sum"><b>${Number(nScr)}</b> betik</span>`
            + (run ? `<span class="sum"><span class="dot run"></span><b>${Number(run)}</b> dağıtım sürüyor</span>` : '')
            + (badToday ? `<span class="sum"><span class="dot bad"></span><b>${Number(badToday)}</b> sorunlu dağıtım (bugün)</span>` : '')
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
                POps.setEmpty(tbody, { tag: 'tr', colspan: 5, icon: 'filter', title: 'Süzgece uyan paket yok', text: 'Arama ya da süzgeci değiştirin.' });
            } else {
                POps.setEmpty(tbody, { tag: 'tr', colspan: 5, icon: 'package', title: 'Henüz paket yok', text: 'Kurulum dosyası ya da betik ekleyin; buradan sınıflara dağıtılır.' });
                const b = POps.el('button', { type: 'button', className: 'btn', text: 'Paket yükle' });
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
            else POps.toast('error', 'Paketler alınamadı: ' + POps.errorMessage(e));
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
        const failText = failed.slice(0, 6).map(t => dev.name(t.target_pc) + ': ' + (dev.failReason(t) || dev.statusWord(t.status))).join('\n') + (failed.length > 6 ? `\n+${failed.length - 6} bilgisayar daha` : '');
        const facts = [['Gönderen', j.by || 'sistem'], ['Kaynak', dev.sourceText(j.source)], ['Zaman', POps.fullTime(tsOf(j.at))], ['IP', j.ip], ['Gerekçe', j.reason], ['Hedef', targetText(j)]];
        const detailHtml = open
            ? '<div class="dp-detail">' + (failed.length && j.c.total > 1 ? `<div class="why">${escapeHtml(failText)}</div>` : '')
              + `<div class="facts">${facts.filter(f => f[1]).map(f => `<span>${escapeHtml(f[0])}</span><span>${escapeHtml(f[1])}</span>`).join('')}</div>`
              + `<a class="dp-more" href="tasks?job=${encodeURIComponent(j.key)}">İşlemler sayfasında aç</a></div>`
            : '';
        return `<div class="act clickable" data-job="${escapeHtml(j.key)}" role="button" tabindex="0" aria-expanded="${open ? 'true' : 'false'}">
            <div class="res ${escapeHtml(j.state)}">${POps.iconHtml(icon)}</div>
            <div style="min-width:0"><div class="what">${escapeHtml(targetText(j))}</div>
                <div class="meta">${escapeHtml(j.by || 'sistem')} · ${POps.timeHtml(tsOf(j.at))}${j.reason ? ' · gerekçe: ' + escapeHtml(j.reason) : ''}</div>
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
            body.innerHTML = `<div class="drawer-head"><div class="drawer-title"><h2>Paket bulunamadı</h2></div><button type="button" class="ibtn sm" data-act="close" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button></div><p class="dp-empty">Silinmiş olabilir.</p>`;
            return;
        }
        const scroll = keep ? body.scrollTop : 0;
        const deps = ui.deps.get(id) || [];
        const subText = o.kind + (o.size ? ' · ' + o.size : '');
        const facts = o.type === 'package'
            ? [['Dosya', o.file, 'mono'], ['Boyut', o.size], ['Sessiz kurulum', o.payload ? (o.args || 'yok') : '', o.args ? 'mono' : ''], ['Bitince', o.reboot ? 'Yeniden başlatır' : 'Yeniden başlatmaz'], ['Yükleyen', o.by]]
            : [['Bitince', o.reboot ? 'Yeniden başlatır' : 'Yeniden başlatmaz'], ['Ekleyen', o.by]];
        const factsHtml = '<div class="glist">'
            + facts.filter(f => f[1]).map(f => `<div class="grow"><span>${escapeHtml(f[0])}</span><span class="${escapeHtml(f[2] || '')}">${escapeHtml(f[1])}</span></div>`).join('')
            + (o.at ? `<div class="grow"><span>Eklendi</span><span>${POps.timeHtml(o.at)}</span></div>` : '')
            + (o.hash ? `<div class="grow stack"><span>SHA-256</span><button type="button" class="hash full" data-copy="${escapeHtml(o.hash)}" title="Kopyala" aria-label="SHA-256 özetini kopyala">${escapeHtml(o.hash)}${POps.iconHtml('copy')}</button></div>` : '')
            + `<div class="grow"><span>Kimlik</span><span class="mono">${escapeHtml(o.id)}</span></div>`
            + '</div>';
        const issueHtml = o.type === 'package' && !o.hash
            ? `<div class="issue upd">${POps.iconHtml('alert', 'sm')}<span style="flex:1">${o.payload ? 'Dosya özeti kayıtlı değil: bilgisayarlar indirdikleri dosyayı doğrulamadan çalıştırır. Dosyayı yeniden yükleyin.' : 'Paketin dosya bilgisi okunamadı. Düzenleyip dosyayı yeniden yükleyin.'}</span></div>`
            : '';
        const codeHtml = o.type === 'script' ? `<div><h3>Komut</h3><pre class="dp-code">${escapeHtml(o.code)}</pre></div>` : '';
        const depsHtml = deps.length
            ? deps.slice(0, 8).map(depRowHtml).join('') + (deps.length > 8 ? `<a class="dp-more" href="tasks">${Number(deps.length - 8)} dağıtım daha · İşlemler</a>` : '')
            : `<div class="dp-empty">${ui.tasksAt ? 'Henüz dağıtılmadı.' : 'Yükleniyor…'}</div>`;
        body.innerHTML = `<div class="drawer-head">
                <div class="drawer-title"><span class="drawer-ico">${POps.iconHtml(o.type === 'package' ? 'package' : 'terminal', 'lg')}</span>
                    <div style="min-width:0"><h2>${escapeHtml(o.name)}</h2><div class="sub">${escapeHtml(subText)}</div></div></div>
                <button type="button" class="ibtn sm" data-act="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button>
            </div>
            <div class="dp-actions">
                <button type="button" class="btn" data-act="deploy">${POps.iconHtml('send', 'sm')}Dağıt</button>
                <button type="button" class="btn secondary" data-act="edit">${POps.iconHtml('edit', 'sm')}Düzenle</button>
                <span class="grow"></span>
                <button type="button" class="ibtn" data-act="more" data-tip="Diğer işlemler" data-tip-pos="left" aria-label="Diğer işlemler" aria-haspopup="menu">${POps.iconHtml('more')}</button>
            </div>
            ${issueHtml}${factsHtml}${codeHtml}
            <div><h3>Son dağıtımlar</h3><div class="dp-deps">${depsHtml}</div></div>`;
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
            if (copy) { copyText(copy.dataset.copy, 'Özet'); return; }
            const b = e.target.closest('[data-act]');
            if (b) {
                const act = b.dataset.act;
                if (act === 'close') POps.drawer.close();
                else if (!o) return;
                else if (act === 'deploy') openRun([o]);
                else if (act === 'edit') openPkgModal(o);
                else if (act === 'more') POps.menu(b, [
                    o.hash ? { label: 'Özeti kopyala', icon: 'copy', onClick: () => copyText(o.hash, 'Özet') } : null,
                    o.url ? { label: 'İndirme adresini kopyala', icon: 'download', onClick: () => copyText(o.url, 'Adres') } : null,
                    o.type === 'script' ? { label: 'Komutu kopyala', icon: 'copy', onClick: () => copyText(o.code, 'Komut') } : null,
                    { label: 'Görev zincirine ekle', icon: 'list', onClick: () => openRun([o], true) },
                    '-',
                    { label: o.type === 'package' ? 'Paketi sil' : 'Betiği sil', icon: 'trash', danger: true, onClick: () => removePkg(o) }
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
            title: `${o.name} silinsin mi?`,
            message: 'Kütüphaneden kaldırılır. Gönderilmiş görevler ve dağıtım geçmişi etkilenmez.',
            confirmText: o.type === 'package' ? 'Paketi sil' : 'Betiği sil', danger: true, icon: 'trash'
        });
        if (!ok) return;
        if (await POps.act(null, () => POps.post('/api/delete_package', { id: o.id }), { success: `${o.name} silindi.` })) {
            if (POps.drawer.isOpen('pkg:' + o.id)) POps.drawer.close();
            run.steps = run.steps.filter(s => s.id !== o.id);
            loadPackages();
        }
    }

    // ================= Paket / betik ekleme ve düzenleme =================
    const up = { edit: null, type: 'package', xhr: null };
    function setType(t) {
        up.type = t;
        $('pkgType').querySelectorAll('button').forEach(b => { const on = b.dataset.t === t; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        $('pkgFileArea').hidden = t !== 'package';
        $('pkgCodeArea').hidden = t !== 'script';
        $('pkgRebootHint').textContent = t === 'package' ? 'Başarılı kurulumdan 15 saniye sonra bilgisayar yeniden başlar.' : 'Komut bittikten 15 saniye sonra bilgisayar yeniden başlar.';
        if (!up.edit) $('pkgTitle').textContent = t === 'package' ? 'Paket yükle' : 'Betik ekle';
    }
    function showFile() {
        const f = $('pkgFile').files && $('pkgFile').files[0];
        const drop = $('pkgDrop');
        drop.classList.toggle('has', !!f);
        if (f) { $('pkgDropT').textContent = f.name; $('pkgDropS').textContent = sizeText(f.size) + ' · değiştirmek için yeniden seçin'; }
        else if (up.edit && up.edit.type === 'package' && up.edit.file) { $('pkgDropT').textContent = up.edit.file; $('pkgDropS').textContent = (up.edit.size ? up.edit.size + ' · ' : '') + 'sunucuda kayıtlı; değiştirmek için yeni dosya seçin'; }
        else { $('pkgDropT').textContent = 'Dosya seçin ya da buraya bırakın'; $('pkgDropS').textContent = '.exe, .msi ya da install.bat içeren .zip'; }
    }
    function setProgress(frac) {
        const p = $('pkgProg');
        if (frac === null) { p.hidden = true; return; }
        p.hidden = false;
        const pct = Math.max(0, Math.min(100, Math.round(frac * 100)));
        $('pkgProgBar').style.width = pct + '%';
        $('pkgProgTxt').textContent = pct >= 100 ? 'Özet hesaplanıyor…' : '%' + pct;
    }
    function openPkgModal(o, type) {
        up.edit = o || null;
        $('pkgName').value = o ? o.name : '';
        $('pkgArgs').value = o ? o.args : '';
        $('pkgCode').value = o && o.type === 'script' ? o.code : '';
        $('pkgReboot').checked = !!(o && o.reboot);
        $('pkgFile').value = '';
        setProgress(null);
        setType(o ? o.type : (type || 'package'));
        if (o) $('pkgTitle').textContent = o.type === 'package' ? 'Paketi düzenle' : 'Betiği düzenle';
        $('pkgSave').textContent = o ? 'Kaydet' : 'Yükle ve ekle';
        showFile();
        openModal('pkgModal');
    }
    // Dosya, ilerleme gösterilebilsin diye XMLHttpRequest ile yüklenir (POps.api ile aynı başlık ve hata metni)
    function uploadFile(file) {
        return new Promise((resolve, reject) => {
            const xhr = new XMLHttpRequest();
            up.xhr = xhr;
            xhr.open('POST', ((typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '') + '/api/upload');
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
                const msg = typeof d === 'string' ? d : (data && data.message) || ({ 413: 'Dosya çok büyük.', 403: 'Bu işlem için yetkiniz yok.' }[xhr.status]) || `Sunucu hatası (HTTP ${xhr.status}).`;
                reject(new POps.ApiError(msg, xhr.status, data));
            };
            xhr.onerror = () => { up.xhr = null; reject(new POps.ApiError('Sunucuya ulaşılamadı. Bağlantınızı kontrol edin.', 0, null)); };
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
        if (!name) { POps.toast('warning', 'Bir ad verin.'); $('pkgName').focus(); return; }
        let command, meta;
        try {
            await POps.busy(btn, async () => {
                if (type === 'package') {
                    let U, F, H, fileName, size, by;
                    const old = o && o.type === 'package' ? o.payload : null;
                    if (file) {
                        setProgress(0);
                        const r = await uploadFile(file);
                        // İmzalı adres (sunucu /download'u imzasız vermez) ve dosyanın SHA-256 özeti
                        U = OMYO_API.DOWNLOAD_URL + '/' + encodeURIComponent(r.filename) + '?sig=' + encodeURIComponent(r.sig);
                        F = 'C:\\POpsLogs\\' + r.filename;
                        H = r.sha256 || '';
                        fileName = r.filename;
                        size = sizeText(file.size);
                        by = ME;
                    } else if (old && old.U && old.F) {
                        U = old.U; F = old.F; H = old.H || '';
                        fileName = o.file; size = o.size; by = o.by;
                    } else {
                        throw new Error(o && o.type === 'package' ? 'Bu paketin dosya bilgisi okunamadı; kurulum dosyasını yeniden seçin.' : 'Kurulum dosyasını seçin.');
                    }
                    command = packageCommand({ U, F, A: $('pkgArgs').value.trim(), R: reboot ? 1 : 0, H });
                    meta = buildMeta('package', fileName, size, reboot, by);
                } else {
                    const code = $('pkgCode').value.trim();
                    if (!code) throw new Error('Çalıştırılacak komutu yazın.');
                    command = code + (reboot ? SCRIPT_REBOOT : '');
                    meta = buildMeta('script', '', '', reboot, o && o.type === 'script' ? o.by : ME);
                }
                await POps.post('/api/add_package', {
                    id: o ? o.id : 'mod-' + Date.now(), name, type, meta, command,
                    icon: type === 'package' ? 'package' : 'terminal', color: type === 'package' ? '#3b82f6' : '#f59e0b'
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
        POps.toast('success', o ? `${name} güncellendi.` : `${name} eklendi.`);
        await loadPackages();
        // Açık dağıtım penceresindeki adımlar yeni haliyle gider
        run.steps = run.steps.map(s => ui.pkgs.find(x => x.id === s.id) || s);
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
    const labOf = (d) => (d.lab && d.lab !== dev.UNASSIGNED ? d.lab : dev.UNASSIGNED);
    const labName = (l) => (l === dev.UNASSIGNED ? 'Atanmamış' : l);
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
    function stepsTitle() {
        if (!run.steps.length) return '';
        return run.steps.length === 1 ? run.steps[0].name : `${run.steps[0].name} +${run.steps.length - 1} adım`;
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
        $('runTitle').textContent = !n || run.chain || multi ? 'Görev zinciri' : 'Dağıt · ' + run.steps[0].name;
        $('runStepsNote').textContent = multi ? 'Hedefte bu sırayla çalışır' : n ? 'Zincir için adım ekleyebilirsiniz' : '';
        $('runSteps').innerHTML = n
            ? run.steps.map((s, i) => `<div class="step"><span class="n">${Number(i + 1)}</span>
                <span class="tx"><b>${escapeHtml(s.name)}</b><small>${escapeHtml(s.kind + (s.size ? ' · ' + s.size : '') + (s.reboot ? ' · yeniden başlatır' : ''))}</small></span>
                ${multi ? `<button type="button" class="ibtn sm" data-step="up" data-i="${Number(i)}" title="Yukarı taşı" aria-label="Yukarı taşı" ${i === 0 ? 'disabled' : ''}>${POps.iconHtml('arrow-up', 'sm')}</button><button type="button" class="ibtn sm" data-step="down" data-i="${Number(i)}" title="Aşağı taşı" aria-label="Aşağı taşı" ${i === n - 1 ? 'disabled' : ''}>${POps.iconHtml('arrow-down', 'sm')}</button>` : ''}
                <button type="button" class="ibtn sm danger" data-step="rm" data-i="${Number(i)}" title="Adımı çıkar" aria-label="Adımı çıkar">${POps.iconHtml('x', 'sm')}</button></div>`).join('')
            : '<div class="step empty">Henüz adım yok. Aşağıdan paket ya da betik ekleyin.</div>';
        const optHtml = (list) => list.map(o => `<option value="${escapeHtml(o.id)}">${escapeHtml(o.name)}</option>`).join('');
        const pk = ui.pkgs.filter(o => o.type === 'package'), sc = ui.pkgs.filter(o => o.type === 'script');
        $('runAdd').innerHTML = `<option value="">${n ? 'Adım ekle…' : 'Paket ya da betik seçin…'}</option>`
            + (pk.length ? `<optgroup label="Paketler">${optHtml(pk)}</optgroup>` : '')
            + (sc.length ? `<optgroup label="Betikler">${optHtml(sc)}</optgroup>` : '');
    }
    function renderRunList(force) {
        const box = $('runList');
        $('runMode').querySelectorAll('button').forEach(b => { const on = b.dataset.m === run.mode; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        if (!state.devicesLoaded) { POps.setLoading(box, 'Bilgisayarlar yükleniyor…'); return; }
        const devs = state.devices || [];
        const sig = JSON.stringify([run.mode, run.q, run.all, [...run.labs], [...run.pcs], devs.map(d => [d.hostname, d.lab, d.status, POps.deviceName(d), d.current_user])]);
        if (!force && sig === run.sig) return;
        run.sig = sig;
        const q = run.q.toLocaleLowerCase('tr');
        const hit = (...vals) => !q || vals.some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
        const labs = runLabs();
        let html = '';
        if (run.mode === 'lab') {
            const countText = (list) => { const on = list.filter(d => !POps.isOffline(d)).length; return list.length ? `${on} açık · ${list.length - on} kapalı` : 'boş'; };
            if (!q) html += `<label class="pick all"><input type="checkbox" data-all="1" ${run.all ? 'checked' : ''}><span class="tx"><b>Bütün ağ</b><small>${escapeHtml(countText(devs))}</small></span></label>`;
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
                html += `<div class="pick-h"><span>${escapeHtml(labName(l))}</span>${viaLab ? '<span>sınıfla seçili</span>' : `<button type="button" class="linkbtn" data-group="${escapeHtml(l)}">${allSel ? 'Hiçbirini seçme' : 'Hepsini seç'}</button>`}</div>`;
                html += list.map(d => {
                    const st = dev.state(d);
                    const on = viaLab || run.pcs.has(d.hostname);
                    return `<label class="pick${st.cls === 'off' ? ' off' : ''}"><input type="checkbox" data-pc="${escapeHtml(d.hostname)}" data-g="${escapeHtml(l)}" ${on ? 'checked' : ''} ${viaLab ? 'disabled' : ''}>
                        <span class="tx"><b>${escapeHtml(POps.deviceName(d))}</b><small>${escapeHtml(dev.subline(d))}</small></span>
                        <span class="st"><span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.cls === 'off' ? 'Kapalı' : st.word)}</span></label>`;
                }).join('');
            });
        }
        const top = box.scrollTop;
        if (html) box.innerHTML = html;
        else box.innerHTML = `<div class="run-none">${q ? 'Aramaya uyan hedef yok.' : 'Henüz bilgisayar yok.'}</div>`;
        box.scrollTop = top;
    }
    function renderRunSum() {
        const t = runTargets();
        const total = t.on.length + t.off.length;
        const n = t.send.length;
        $('runSum').innerHTML = total
            ? `<span class="sum"><b>${Number(total)}</b> bilgisayar seçildi</span>`
              + (t.off.length ? `<span class="sum"><span class="dot off"></span><b>${Number(t.off.length)}</b> kapalı · ${run.incOff ? 'açılınca kuracak' : 'atlanacak'}</span>`
                + `<label class="switch-field"><span class="switch"><input type="checkbox" id="runIncOff" ${run.incOff ? 'checked' : ''}><span></span></span>Kapalılar açılınca kursun</label>` : '')
            : '<span>Hedef seçilmedi.</span>';
        const go = $('runGo');
        go.disabled = !n || !run.steps.length;
        go.textContent = n ? `${n} bilgisayara dağıt` : 'Dağıt';
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
        if (!run.steps.length) { POps.toast('warning', 'Önce bir adım ekleyin.'); return; }
        if (!t.send.length) { POps.toast('warning', t.off.length ? 'Seçilen bilgisayarların hepsi kapalı.' : 'En az bir hedef seçin.'); return; }
        const title = stepsTitle();
        const n = t.send.length;
        const reason = $('runReason').value.trim();
        const steps = run.steps.slice();
        let r;
        try {
            r = await POps.busy(btn, () => POps.post('/api/deploy_orchestration', {
                target_mode: 'PC', targets: t.send,
                taskSequence: steps.map(s => ({ name: s.name, type: s.type, command: s.command })),
                title: title.slice(0, 200), source: 'deploy', reason: reason || null
            }, { jobTitle: `${title} · ${n} bilgisayar` }));
        } catch (err) { POps.toast('error', 'Dağıtım başlatılamadı: ' + POps.errorMessage(err)); return; }
        if (!r) return;
        closeModal('runModal');
        const pcs = Math.round((r.created || 0) / steps.length);
        const closed = r.skipped_module_closed ? ` ${r.skipped_module_closed} bilgisayarda dosya dağıtımı modülü kapalı; atlandı.` : '';
        POps.toast(r.duplicate ? 'info' : 'success', (r.duplicate ? 'Aynı dağıtım az önce gönderilmişti.' : `${title} ${pcs} bilgisayara gönderildi.`) + closed);
        loadTasks();
    });
    $('chainBtn').addEventListener('click', () => {
        if (!ui.loaded) return;
        if (!ui.pkgs.length) { POps.toast('info', 'Önce bir paket ya da betik ekleyin.'); return; }
        openRun([], true);
    });

    // ================= Eşzamanlı kurulum sınırı =================
    $('limitBtn').addEventListener('click', async () => {
        const v = await POps.prompt({
            title: 'Eşzamanlı kurulum sınırı',
            message: 'Aynı anda en çok kaç bilgisayarda görev çalışsın? Fazlası sırada bekler; büyük dağıtımlar ağı ve sunucuyu tıkamaz.',
            label: 'Bilgisayar sayısı', inputType: 'number', inputMode: 'numeric', defaultValue: ui.limit || 5, confirmText: 'Kaydet', icon: 'sliders',
            validate: (s) => { const n = Number(s); return Number.isInteger(n) && n >= 1 && n <= 100 ? '' : '1 ile 100 arasında bir sayı girin.'; }
        });
        if (v === null) return;
        const lim = Number(v);
        if (await POps.act(null, () => POps.post('/api/set_concurrent_limit', { limit: lim }), { success: `Aynı anda en çok ${lim} bilgisayarda görev çalışacak.`, error: 'Kaydedilemedi.' })) {
            ui.limit = lim;
            renderSummary();
        }
    });

    // ================= Liste etkileşimi =================
    tbody.addEventListener('click', (e) => {
        const copy = e.target.closest('[data-copy]');
        if (copy) { e.stopPropagation(); copyText(copy.dataset.copy, 'Özet'); return; }
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
        const m = ['runModal', 'pkgModal'].map(id => document.getElementById(id)).find(x => x && x.classList.contains('open'));
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
