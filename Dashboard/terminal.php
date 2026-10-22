<?php include 'includes/header.php'; ?>

<style>
    /* Uzak komut: hedef (bilgisayar ve sınıf kutucukları), komut satırı ve çıktı */
    .page-header-actions .btn { gap: 7px; }
    .tm-target { background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle); padding: 12px 14px; margin-bottom: 14px; }
    .tm-row { display: flex; align-items: center; gap: 8px 12px; flex-wrap: wrap; }
    .tm-h { font-size: var(--text-sm); font-weight: var(--fw-semibold); color: var(--text-secondary); }
    .tm-chips { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; min-width: 0; }
    .tm-chips .tm-none { font-size: var(--text-sm); color: var(--text-muted); }
    .tchip { display: inline-flex; align-items: center; gap: 6px; height: 28px; padding: 0 3px 0 10px; border-radius: 99px; background: var(--bg-app); box-shadow: inset 0 0 0 1px var(--border-subtle); font-size: var(--text-sm); max-width: 260px; min-width: 0; }
    .tchip a { color: var(--text-primary); font-weight: var(--fw-medium); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
    .tchip a:hover { color: var(--primary-500); }
    .tchip .n { font-size: var(--text-xs); color: var(--text-tertiary); font-variant-numeric: tabular-nums; white-space: nowrap; }
    .tchip .x { width: 22px; height: 22px; border-radius: 99px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-tertiary); flex: none; }
    .tchip .x:hover { background: var(--bg-hover); color: var(--text-primary); }
    .tchip .x .ico { width: 13px; height: 13px; }
    .tchip.lab { background: var(--primary-50); box-shadow: inset 0 0 0 1px var(--primary-100); }
    .tchip.lab > .ico { width: 14px; height: 14px; color: var(--primary-600); flex: none; }
    .tchip.more { padding: 0 10px; color: var(--text-secondary); font-weight: var(--fw-medium); }
    .tchip.more:hover { color: var(--text-primary); }
    .tm-add { position: relative; flex: 1 1 240px; max-width: 320px; min-width: 200px; }
    .tm-add .search-field { width: 100%; max-width: none; min-width: 0; }
    .tm-sug { position: absolute; left: 0; right: 0; top: calc(100% + 6px); z-index: 30; min-width: 260px; background: var(--bg-surface); border-radius: 12px; box-shadow: 0 0 0 1px var(--border-subtle), 0 10px 30px rgba(0, 0, 0, 0.12); max-height: 340px; overflow-y: auto; padding: 4px; }
    .tm-sug .h { font-size: var(--text-xs); color: var(--text-muted); padding: 8px 10px 4px; }
    .tm-sug .o { display: flex; align-items: center; gap: 8px; width: 100%; padding: 7px 10px; border-radius: 8px; text-align: left; font-size: var(--text-sm); color: var(--text-primary); }
    .tm-sug .o:hover, .tm-sug .o.on { background: var(--bg-surface-2); }
    .tm-sug .o > .ico { width: 14px; height: 14px; color: var(--primary-600); flex: none; }
    .tm-sug .o .t { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tm-sug .o .d { font-size: var(--text-xs); color: var(--text-muted); white-space: nowrap; }
    .tm-sug .none { padding: 10px; font-size: var(--text-sm); color: var(--text-muted); }
    .tm-meta { display: flex; align-items: center; gap: 8px 16px; flex-wrap: wrap; margin-top: 10px; padding-top: 10px; border-top: 1px solid var(--border-subtle); font-size: var(--text-sm); color: var(--text-tertiary); min-height: 46px; }
    .tm-meta .sum { display: inline-flex; align-items: center; gap: 6px; white-space: nowrap; }
    .tm-meta b { color: var(--text-primary); font-weight: var(--fw-semibold); font-variant-numeric: tabular-nums; }
    .tm-reason { display: flex; align-items: center; gap: 8px; margin-left: auto; flex: 0 1 400px; min-width: 240px; }
    .tm-reason label { margin: 0; white-space: nowrap; font-size: var(--text-sm); color: var(--text-secondary); font-weight: var(--fw-medium); }
    .tm-reason input { flex: 1; min-width: 0; }
    .tm-note { display: flex; flex-direction: column; gap: 4px; margin-top: 8px; font-size: var(--text-xs); color: var(--text-tertiary); }
    .tm-note:empty { display: none; }
    .tm-note span { display: inline-flex; align-items: center; gap: 6px; }

    .tm-console { position: relative; background: #1d1d1f; border-radius: 16px; display: flex; flex-direction: column; height: calc(100vh - 300px); min-height: 380px; overflow: hidden; color: #e5e5ea; }
    .tm-bar { display: flex; align-items: center; gap: 4px; padding: 6px 8px 6px 18px; border-bottom: 1px solid rgba(255, 255, 255, 0.08); }
    .tm-bar .tt { flex: 1; min-width: 0; color: #8e8e93; font-size: var(--text-xs); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tm-bar button { display: inline-flex; align-items: center; gap: 6px; height: 28px; padding: 0 10px; border-radius: 8px; color: #c7c7cc; font-size: var(--text-xs); font-weight: var(--fw-medium); flex: none; }
    .tm-bar button:hover { background: #2c2c2e; color: #fff; }
    .tm-bar button:focus-visible { outline: none; box-shadow: var(--focus-ring); }
    .tm-out { flex: 1; min-height: 0; overflow-y: auto; padding: 14px 18px; cursor: text; font-family: var(--font-mono); font-size: 13px; }
    .pops-terminal-screen { line-height: 1.55; }
    .pops-terminal-screen .cmd-block { color: #fff; }
    .pops-terminal-screen .cmd-output { display: flex; gap: 10px; margin-bottom: 8px; background: rgba(255, 255, 255, 0.04); padding: 8px 12px; border-radius: 10px; }
    .pops-terminal-screen .cmd-output .pc { color: #64d2ff; font-weight: 600; white-space: nowrap; }
    .pops-terminal-screen .cmd-output a.pc:hover { text-decoration: underline; }
    .pops-terminal-screen .cmd-output pre { margin: 0; color: #e2e8f0; font-family: var(--font-mono); font-size: 13px; white-space: pre-wrap; word-wrap: break-word; min-width: 0; }
    .pops-terminal-screen .info { color: #8e8e93; font-size: 12px; }
    .pops-terminal-screen .err { color: #ff6961; }
    .pops-terminal-screen .warn { color: #ffd60a; }
    .pops-terminal-screen .ok { color: #30d158; }
    .pops-terminal-screen .gap { margin-top: 0.75rem; margin-bottom: 0.5rem; }
    .cmd-input-line { display: flex; align-items: center; padding: 12px 18px; border-top: 1px solid rgba(255, 255, 255, 0.08); font-family: var(--font-mono); font-size: 13px; }
    .cmd-prefix { margin-right: 8px; color: #fff; font-weight: 600; white-space: nowrap; max-width: 45%; overflow: hidden; text-overflow: ellipsis; }
    .cmd-input { background: transparent; border: none; color: #fff; font-family: var(--font-mono); font-size: 13px; flex: 1; outline: none; padding: 0; box-shadow: none; min-height: 0 !important; }
    .cmd-input:focus { box-shadow: none; }
    .cmd-input::placeholder { color: #636366; }
    @media (max-width: 640px) {
        .tm-add { max-width: none; flex-basis: 100%; }
        .tm-reason { margin-left: 0; flex-basis: 100%; }
        .tm-console { height: calc(100vh - 360px); }
        .tm-bar .tt { display: none; }
        .tm-bar { justify-content: flex-end; }
    }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Uzak komut'); ?></h1>
    </div>
    <div class="page-header-actions">
        <button type="button" class="btn secondary" id="quickBtn" aria-haspopup="menu"><?php echo pops_icon('zap', 'sm'); ?><?php _e('Hızlı komutlar'); ?><?php echo pops_icon('chev', 'sm'); ?></button>
        <button type="button" class="btn secondary" id="historyBtn"><?php echo pops_icon('clock', 'sm'); ?><?php _e('Komut geçmişi'); ?></button>
    </div>
</div>

<section class="tm-target" aria-labelledby="tmTargetH">
    <div class="tm-row">
        <span class="tm-h" id="tmTargetH"><?php _e('Hedef'); ?></span>
        <div class="tm-chips" id="tmChips"></div>
        <div class="tm-add">
            <div class="search-field">
                <?php echo pops_icon('plus', 'sm'); ?>
                <input type="search" id="tmAdd" placeholder="<?php _e('Bilgisayar ya da sınıf ekle'); ?>" aria-label="<?php _e('Hedefe bilgisayar ya da sınıf ekle'); ?>" role="combobox" aria-expanded="false" aria-controls="tmSug" aria-autocomplete="list" autocomplete="off" spellcheck="false">
            </div>
            <div class="tm-sug" id="tmSug" role="listbox" aria-label="<?php _e('Eklenebilecek hedefler'); ?>" hidden></div>
        </div>
    </div>
    <div class="tm-meta">
        <span class="sum" id="tmCount"></span>
        <button type="button" class="btn ghost sm" id="tmClear" hidden><?php echo pops_icon('x', 'sm'); ?><?php _e('Hedefi temizle'); ?></button>
        <div class="tm-reason">
            <label for="tmReason"><?php _e('Gerekçe'); ?></label>
            <input type="text" id="tmReason" maxlength="300" autocomplete="off" placeholder="<?php _e('İsteğe bağlı; denetim kaydına yazılır'); ?>">
        </div>
    </div>
    <div class="tm-note" id="tmNote" role="status"></div>
</section>

<div class="tm-console" id="terminalContainer">
    <div class="tm-bar">
        <span class="tt" id="tmHint"><?php _e("Komutlar Windows'ta SYSTEM hesabıyla cmd.exe ile, Linux'ta root olarak /bin/sh ile çalışır."); ?></span>
        <button type="button" id="copyTerminalBtn"><?php echo pops_icon('copy', 'sm'); ?><?php _e('Çıktıyı kopyala'); ?></button>
        <button type="button" id="clearTerminalBtn" title="<?php _e('Ekranı temizle (cls)'); ?>"><?php echo pops_icon('trash', 'sm'); ?><?php _e('Ekranı temizle'); ?></button>
    </div>
    <div class="tm-out" id="tmOut">
        <div class="pops-terminal-screen" id="popsTerminalScreen" aria-live="polite"></div>
    </div>
    <div class="cmd-input-line" id="cmdInputLine">
        <span id="cmdPrefix" class="cmd-prefix">POps:\&gt;</span>
        <input type="text" id="terminalCommand" class="cmd-input" autocomplete="off" spellcheck="false" placeholder="<?php _e("Komut yazın ve Enter'a basın"); ?>" aria-label="<?php _e('Komut'); ?>">
    </div>
</div>

<script>
// Hedef: seçilen sınıflar ve bilgisayarlar (Sınıflar / Bilgisayarlar sayfasından gelen seçim: terminal?pc=HW-1,HW-2)
const urlHosts = (new URLSearchParams(location.search).get('pc') || '').split(',').map(s => s.trim()).filter(Boolean).slice(0, 500);
const tgt = { labs: [], pcs: [...new Set(urlHosts)], showAll: false };
let terminalHistory = [];
const processedResponses = new Set();
// Windows bilgisayar adı: en çok 15 karakter, harf, rakam ve tire (komut satırına tırnak ya da boşluk girmesin)
const PC_NAME_RE = /^[A-Za-z0-9][A-Za-z0-9-]{0,14}$/;
// Hedefin işletim sistemi: windows (cmd.exe, SYSTEM), linux (/bin/sh, root) ya da ikisi (mixed). Hızlı komutlar,
// yeniden adlandırma ve uygulama kapatma Windows komutudur; hedefte Linux bilgisayar varsa sunulmaz.
let targetOs = 'windows';
const promptMark = () => targetOs === 'linux' ? '#' : targetOs === 'mixed' ? '>' : ':\\>';
function windowsOnly() {
    if (targetOs === 'windows') return true;
    POps.toast('warning', POps.t('Bu komut yalnızca Windows içindir; hedefte Linux bilgisayar var.'));
    return false;
}

function initTerminalWebSocket() {
    if (typeof POPS_API === 'undefined') return;
    let ws;
    try { ws = new WebSocket(POPS_API.wsUrl('/ws/panel')); } catch (e) { return; }
    ws.onmessage = (event) => {
        let payload;
        try { payload = JSON.parse(event.data); } catch (e) { return; }
        if (payload.type !== 'terminal_output') return;
        const uniqueId = `${payload.task_id || '0'}_${payload.id || '?'}`;
        if (processedResponses.has(uniqueId)) return;
        processedResponses.add(uniqueId);
        // id: bilgisayarın kimliği (pc?id=), pc_name: Windows'taki adı
        const host = String(payload.id || '');
        const shown = host && POps.dev.find(host) ? POps.dev.name(host) : String(payload.pc_name || payload.id || POps.t('Bilinmeyen bilgisayar'));
        const pcHtml = host ? `<a class="pc" href="pc?id=${encodeURIComponent(host)}">[${escapeHtml(shown)}]</a>` : `<div class="pc">[${escapeHtml(shown)}]</div>`;
        appendToTerminal(`<div class="cmd-output">${pcHtml}<pre>${escapeHtml(String(payload.output || POps.t('Çıktı yok.')).trim())}</pre></div>`);
    };
    // Bağlantı koparsa birkaç saniye sonra yeniden kurulur (sunucu güncellemesi, ağ kesintisi)
    ws.onclose = () => setTimeout(initTerminalWebSocket, 5000);
}

// Hızlı komutlar. Ajan her komutu bir .bat dosyasına yazıp cmd.exe /c ile SYSTEM olarak çalıştırır; buradaki
// metinler o .bat satırının kendisidir (PowerShell sarmalayıcısı yok). Komutlar '&' ile ayrılır; .bat içinde '%'
// değişken açar, bu yüzden yalnızca bilerek kullanılan %windir% var. Sıfırdan farklı çıkış kodu görevi Başarısız
// yapar: son komut işin başarısını söyler. name görev adıdır (sunucuya Türkçe gider; panelde POps.taskName ile gösterilir).
const QUICK_ACTIONS = {
    dns: { name: 'DNS önbelleğini temizle', cmd: 'ipconfig /flushdns' },
    // Sanal/statik IP'li ya da kablosu takılı olmayan bağdaştırıcı yüzünden release/renew hata kodu dönebilir;
    // başarı ölçüsü yenilemeden sonra 169.254 dışı bir IPv4 adresinin olmasıdır (findstr bulursa 0 döner)
    network: { name: 'Ağ bağlantısını yenile', cmd: 'ipconfig /release & ipconfig /renew & ipconfig | findstr /c:"IPv4" | findstr /v /c:"169.254."' },
    spooler: { name: 'Yazıcı kuyruğunu sıfırla', cmd: 'net stop spooler /y & del /f /s /q "%windir%\\System32\\spool\\PRINTERS\\*.*" & net start spooler' },
    // C:\Windows\Temp ve her kullanıcının AppData\Local\Temp'i. Çalışan görevin kendi .bat'ı (pops_task_*.bat)
    // silinmez, kullanımdaki dosyalar atlanır. Bağlantılar (junction/symlink) izlenmez: kullanıcı kendi Temp'ini
    // başka bir klasöre yönlendirip SYSTEM'e orayı sildiremesin. Tek satır olmalı; içinde " ve % yok.
    temp: { name: 'Geçici dosyaları temizle', cmd: 'powershell -NoProfile -ExecutionPolicy Bypass -Command "' + [
        "$c=@{n=0;s=0}",
        "function L($p){ $x=Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue; (-not $x) -or [bool]($x.Attributes -band 1024) }",
        "function C($d){ foreach($i in @(Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)){ if($i.Attributes -band 1024){ continue }; " +
            "if($i.PSIsContainer){ C $i.FullName; try{ $i.Delete() }catch{} } " +
            "elseif($i.Name -notlike 'pops_task_*.bat'){ try{ if($i.IsReadOnly){ $i.IsReadOnly=$false }; $i.Delete(); $c.n++ }catch{ $c.s++ } } } }",
        "$w=Join-Path $env:windir 'Temp'; if(-not (L $w)){ C $w }",
        "foreach($u in @(Get-ChildItem -LiteralPath (Join-Path $env:SystemDrive 'Users') -Directory -Force -ErrorAction SilentlyContinue)){ " +
            "$a=Join-Path $u.FullName 'AppData'; $b=Join-Path $a 'Local'; $t=Join-Path $b 'Temp'; " +
            "if(-not ((L $u.FullName) -or (L $a) -or (L $b) -or (L $t))){ C $t } }",
        "Write-Output ('Silinen dosya: '+$c.n+', kullanimda oldugu icin atlanan: '+$c.s)",
        "exit 0",
    ].join('; ') + '"' },
    gpupdate: { name: 'Grup ilkesini güncelle', cmd: 'gpupdate /force' },
};

// ---- Hedef
const labPcs = (lab) => state.devices.filter(d => d.lab === lab).map(d => d.hostname);
function targetHosts() { return [...new Set(tgt.labs.flatMap(labPcs).concat(tgt.pcs))]; }
// Tek bilgisayar (yeniden adlandırma) ya da tek sınıf (toplu adlandırma, sınıfı uyandırma)
function singlePc() {
    if (tgt.labs.length || tgt.pcs.length !== 1) return null;
    return { id: tgt.pcs[0], name: POps.dev.name(tgt.pcs[0]) };
}
function singleLab() { return !tgt.pcs.length && tgt.labs.length === 1 ? tgt.labs[0] : null; }
function targetLabel() {
    if (!tgt.pcs.length) return tgt.labs.length === 1 ? tgt.labs[0] : POps.tn('{n} sınıf', tgt.labs.length);
    const n = targetHosts().length;
    return n === 1 ? POps.dev.name(tgt.pcs[0]) : POps.tn('{n} bilgisayar', n);
}

// Yalnızca sınıflar: LAB (sunucu sınıftaki bilgisayarları gönderim anında bulur); bilgisayar da varsa PC. Yoksa uyarı ve null.
function currentTarget() {
    if (!tgt.pcs.length && !tgt.labs.length) { POps.toast('warning', POps.t('Önce bir hedef ekleyin.')); return null; }
    if (!tgt.pcs.length) return { mode: 'LAB', targets: tgt.labs.slice(), label: tgt.labs.length === 1 ? POps.t('Sınıf: {lab}', { lab: tgt.labs[0] }) : POps.tn('{n} sınıf', tgt.labs.length) };
    const hosts = targetHosts();
    return { mode: 'PC', targets: hosts, label: hosts.length === 1 ? POps.dev.name(hosts[0]) : POps.tn('{n} bilgisayar', hosts.length) };
}
const reasonText = () => document.getElementById('tmReason').value.trim();

async function sendTasks(target, taskSequence, btn, reason) {
    try {
        const title = taskSequence[0] && taskSequence[0].name;
        const r = await POps.busy(btn, () => POps.post('/api/deploy_orchestration',
            { target_mode: target.mode, targets: target.targets, taskSequence, title, source: 'terminal', reason: reason || null },
            { jobTitle: (title === 'Komut' ? POps.t('Komut: {command}', { command: (taskSequence[0].command || '').slice(0, 40) }) : POps.taskName(title)) + ' · ' + (target.label || '') }));
        const created = (r && r.created) || 0;
        appendToTerminal(`<div class="info">[i] ${POps.tnHtml('{n} görev kuyruğa eklendi, çıktı bekleniyor…', Number(created))}${r && r.skipped_module_closed ? ' ' + POps.tnHtml('({n} bilgisayarda uzak komut kapalı)', Number(r.skipped_module_closed)) : ''}</div>`);
        return true;
    } catch (e) {
        appendToTerminal(`<div class="err">[-] ${POps.tHtml('Gönderilemedi: {error}', { error: POps.errorMessage(e) })}</div>`);
        POps.toast('error', POps.errorMessage(e));
        return false;
    }
}

window.runQuickAction = async function(key, btn) {
    if (!windowsOnly()) return;
    const action = QUICK_ACTIONS[key];
    const target = action && currentTarget();
    if (!target) return;
    const reason = await POps.prompt({ title: POps.taskName(action.name), message: POps.t('{target} için çalıştırılacak.', { target: target.label }), label: POps.t('Gerekçe (denetim kaydına yazılır)'), defaultValue: reasonText(), required: true, maxLength: 300, confirmText: POps.t('Çalıştır') });
    if (reason === null) return;
    appendToTerminal(`<div class="cmd-block warn gap">[*] ${escapeHtml(POps.taskName(action.name))} · ${escapeHtml(target.label)} · ${POps.tHtml('Gerekçe: {reason}', { reason })}</div>`);
    await sendTasks(target, [{ name: action.name, type: 'CMD', command: action.cmd }], btn, reason);
};

function renameCommand(newName) {
    // newName PC_NAME_RE ile doğrulanmıştır: tırnak, boşluk ya da $ içermez
    return `powershell -Command "$newName='${newName}'; (Get-WmiObject Win32_ComputerSystem).Rename($newName); $oldUser=(Get-LocalUser | Where-Object {$_.Enabled -and $_.Name -notmatch 'Administrator|Guest|DefaultAccount|WDAGUtilityAccount|system'} | Select-Object -First 1).Name; if($oldUser){ Rename-LocalUser -Name $oldUser -NewName $newName; Set-LocalUser -Name $newName -FullName $newName; }; $i=1; Get-NetAdapter | Where-Object {$_.Name -notmatch 'Baglanti_'} | ForEach-Object { Rename-NetAdapter -Name $_.Name -NewName ('Baglanti_'+$i); $i++ }; Write-Output 'Isim ${newName} olarak degistirildi.'; shutdown /r /t 5"`;
}

async function renameSingle(newName, btn) {
    if (!windowsOnly()) return;
    const d = singlePc();
    if (!d) { POps.toast('warning', POps.t('Yeniden adlandırmak için hedefte yalnızca bir bilgisayar olmalı.')); return; }
    if (!PC_NAME_RE.test(newName)) { POps.toast('error', POps.t('Geçersiz ad: en çok 15 karakter; harf, rakam ve tire.')); return; }
    if (!await POps.confirm({ title: POps.t('Bilgisayar yeniden adlandırılsın mı?'), message: POps.t('{old} → {name}. Bilgisayar 5 saniye sonra yeniden başlar; yerel kullanıcı ve ağ bağdaştırıcıları da yeniden adlandırılır.', { old: d.name, name: newName }), confirmText: POps.t('Adlandır ve yeniden başlat'), danger: true })) return;
    appendToTerminal(`<div class="cmd-block gap"><span class="warn">[*]</span> ${escapeHtml(d.name)} → '${escapeHtml(newName)}'</div>`);
    await sendTasks({ mode: 'PC', targets: [d.id], label: d.name }, [{ name: 'Yeniden adlandır', type: 'CMD', command: renameCommand(newName) }], btn, reasonText());
}

async function renameLab(baseName, limit, btn) {
    const lab = singleLab();
    if (!lab) { POps.toast('warning', POps.t('Toplu adlandırma için hedefte yalnızca bir sınıf olmalı.')); return; }
    if (!windowsOnly()) return;
    if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,12}$/.test(baseName)) { POps.toast('error', POps.t('Geçersiz önek: en çok 13 karakter; harf, rakam ve tire (iki haneli numara eklenir).')); return; }
    let devs = state.devices.filter(d => d.lab === lab)
        .filter(d => { const n = String(d.real_hostname || '').toUpperCase(); return !(n.includes('PC00') || n.includes('ANA') || n.includes('OGR')); })
        .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true, sensitivity: 'base' }));
    if (limit > 0 && limit < devs.length) devs = devs.slice(0, limit);
    if (!devs.length) { POps.toast('warning', POps.t('Bu sınıfta adlandırılacak bilgisayar yok.')); return; }
    const plan = devs.map((d, i) => [d, baseName + String(i + 1).padStart(2, '0')]);
    const list = plan.slice(0, 6).map(([d, n]) => `${POps.deviceName(d)} → ${n}`).join(', ') + (plan.length > 6 ? ' …' : '');
    if (!await POps.confirm({ title: POps.tn('{n} bilgisayar yeniden adlandırılsın mı?', devs.length), message: POps.t('{list}. Her bilgisayar 5 saniye sonra yeniden başlar.', { list }), confirmText: POps.t('Adlandır'), danger: true })) return;
    appendToTerminal(`<div class="cmd-block warn gap">[*] ${POps.tnHtml('Toplu adlandırma: {n} bilgisayar', devs.length)}</div>`);
    const reason = reasonText() || null;
    await POps.busy(btn, async () => {
        for (const [d, n] of plan) {
            try {
                await POps.post('/api/deploy_orchestration', { target_mode: 'PC', targets: [d.hostname], taskSequence: [{ name: 'Toplu adlandırma', type: 'CMD', command: renameCommand(n) }], title: 'Toplu adlandırma', source: 'terminal', reason }, { jobTitle: POps.taskName('Toplu adlandırma') + ' · ' + n });
                appendToTerminal(`<div class="ok">→ <a class="pc" href="pc?id=${encodeURIComponent(d.hostname)}">${escapeHtml(POps.deviceName(d))}</a> → ${escapeHtml(n)}</div>`);
            } catch (e) {
                appendToTerminal(`<div class="err">[-] ${escapeHtml(POps.deviceName(d))}: ${escapeHtml(POps.errorMessage(e))}</div>`);
            }
        }
    });
}

window.promptSingleRename = async function(btn) {
    if (!singlePc()) { POps.toast('warning', POps.t('Yeniden adlandırmak için hedefte yalnızca bir bilgisayar olmalı.')); return; }
    const name = await POps.prompt({ title: POps.t('Bilgisayarı yeniden adlandır'), label: POps.t('Yeni ad'), placeholder: 'LAB1-PC05', required: true, maxLength: 15,
        validate: (v) => PC_NAME_RE.test(v.trim()) ? null : POps.t('En çok 15 karakter; harf, rakam ve tire.') });
    if (name !== null) renameSingle(name.trim(), btn);
};

window.promptAutoRename = async function(btn) {
    if (!singleLab()) { POps.toast('warning', POps.t('Toplu adlandırma için hedefte yalnızca bir sınıf olmalı.')); return; }
    const base = await POps.prompt({ title: POps.t('Sınıfı toplu adlandır'), message: POps.t('Ada iki haneli sıra numarası eklenir (ör. LAB1-PC01, LAB1-PC02).'), label: POps.t('Önek'), placeholder: 'LAB1-PC', required: true, maxLength: 13,
        validate: (v) => /^[A-Za-z0-9][A-Za-z0-9-]{0,12}$/.test(v.trim()) ? null : POps.t('En çok 13 karakter; harf, rakam ve tire.') });
    if (base !== null) renameLab(base.trim(), 0, btn);
};

window.promptTaskkill = async function(btn) {
    if (!windowsOnly()) return;
    const target = currentTarget();
    if (!target) return;
    const exe = await POps.prompt({ title: POps.t('Uygulamayı kapat'), message: POps.t('{target} üzerinde bu adla çalışan bütün süreçler zorla kapatılır.', { target: target.label }), label: POps.t('Program adı'), placeholder: 'msedge.exe', required: true, maxLength: 100,
        validate: (v) => /^[^"%\r\n\\/:*?<>|]+$/.test(v.trim()) ? null : POps.t('Yalnızca program adı yazın (ör. msedge.exe); " % ve yol kullanılamaz.') });
    if (exe === null) return;
    const reason = await POps.prompt({ title: POps.t('Gerekçe'), label: POps.t('Gerekçe (denetim kaydına yazılır)'), defaultValue: reasonText(), required: true, maxLength: 300, confirmText: POps.t('Kapat') });
    if (reason === null) return;
    appendToTerminal(`<div class="cmd-block warn gap">[*] ${POps.tHtml('Uygulama kapatılıyor: {app}', { app: exe.trim() })} · ${escapeHtml(target.label)} · ${POps.tHtml('Gerekçe: {reason}', { reason })}</div>`);
    await sendTasks(target, [{ name: 'Uygulamayı kapat: ' + exe.trim(), type: 'CMD', command: `taskkill /F /IM "${exe.trim()}"` }], btn, reason);
};

// ---- Hedef kutucukları, sayısı ve göndermeden önce bilinmesi gerekenler
function labChipHtml(lab) {
    const pcs = state.devices.filter(d => d.lab === lab);
    const on = pcs.filter(d => !POps.isOffline(d)).length;
    return `<span class="tchip lab" title="${escapeHtml(POps.t('Sınıf: {lab}', { lab }))}">${POps.iconHtml('labs', 'sm')}`
        + `<a href="labs?lab=${encodeURIComponent(lab)}">${escapeHtml(lab)}</a><span class="n">${Number(on)}/${Number(pcs.length)}</span>`
        + `<button type="button" class="x" data-rm-lab="${escapeHtml(lab)}" aria-label="${escapeHtml(POps.t('{name} hedeften çıkar', { name: lab }))}">${POps.iconHtml('x', 'sm')}</button></span>`;
}
function pcChipHtml(h) {
    const d = POps.dev.find(h);
    // Liste gelmeden "kayıtlı değil" denmez
    const st = d ? POps.dev.state(d) : state.devicesLoaded ? { cls: 'bad', word: POps.t('Kayıtlı değil') } : { cls: 'off', word: POps.t('Yükleniyor…') };
    const name = d ? POps.deviceName(d) : h;
    return `<span class="tchip" title="${escapeHtml(name + ' · ' + st.word)}"><span class="dot ${escapeHtml(st.cls)}"></span>`
        + `<a href="pc?id=${encodeURIComponent(h)}">${escapeHtml(name)}</a>`
        + `<button type="button" class="x" data-rm-pc="${escapeHtml(h)}" aria-label="${escapeHtml(POps.t('{name} hedeften çıkar', { name }))}">${POps.iconHtml('x', 'sm')}</button></span>`;
}
const CHIP_MAX = 12;
function renderTarget() {
    const chips = document.getElementById('tmChips');
    const items = tgt.labs.map(l => ['lab', l]).concat(tgt.pcs.map(h => ['pc', h]));
    const shown = tgt.showAll ? items : items.slice(0, CHIP_MAX);
    chips.innerHTML = items.length
        ? shown.map(([k, v]) => (k === 'lab' ? labChipHtml(v) : pcChipHtml(v))).join('')
            + (items.length > shown.length ? `<button type="button" class="tchip more" data-more="1">${POps.tHtml('+{n} daha', { n: items.length - shown.length })}</button>` : '')
        : `<span class="tm-none">${POps.tHtml('Henüz hedef eklenmedi')}</span>`;
    document.getElementById('tmClear').hidden = items.length < 2;

    const hosts = targetHosts();
    const devs = hosts.map(h => POps.dev.find(h)).filter(Boolean);
    const linuxN = devs.filter(POps.dev.isLinux).length;
    targetOs = linuxN && linuxN < devs.length ? 'mixed' : linuxN ? 'linux' : 'windows';
    const any = items.length > 0;
    document.getElementById('cmdPrefix').textContent = any ? `${targetLabel()}${promptMark()}` : POps.t('Hedef seçin') + ':\\>';
    const onN = devs.filter(x => !POps.isOffline(x)).length;
    const offN = devs.length - onN;
    const unknownN = state.devicesLoaded ? hosts.length - devs.length : 0;
    const capOff = devs.filter(x => x.cap_terminal_enabled === false);
    const count = document.getElementById('tmCount');
    const note = document.getElementById('tmNote');
    if (!any) { count.replaceChildren(); note.replaceChildren(); return; }
    // Sayı kalın: yer tutucuya HTML parçası
    const boldHtml = (n) => `<b>${Number(n)}</b>`;
    count.innerHTML = POps.tnHtml('{n} bilgisayar', hosts.length, null, { n: boldHtml(hosts.length) })
        + ` <span class="dot on"></span>${POps.tHtml('{n} açık', null, { n: boldHtml(onN) })}`
        + (offN ? ` <span class="dot off"></span>${POps.tHtml('{n} kapalı', null, { n: boldHtml(offN) })}` : '');
    const dot = (cls) => POps.el('span', { className: 'dot ' + cls });
    const notes = [];
    if (unknownN) notes.push(POps.el('span', {}, [dot('bad'), document.createTextNode(POps.tn('{n} bilgisayar kayıtlı değil; hedeften çıkarmadan komut gönderilemez.', unknownN))]));
    if (offN) notes.push(POps.el('span', {}, [dot('warn'), document.createTextNode(POps.tn('Kapalı {n} bilgisayarda komut, açıldığında çalışır.', offN))]));
    if (targetOs === 'linux') notes.push(POps.el('span', {}, [dot('on'), document.createTextNode(POps.t('Linux: komut root olarak /bin/sh ile çalışır; Windows hızlı komutları gösterilmez.'))]));
    if (targetOs === 'mixed') notes.push(POps.el('span', {}, [dot('warn'), document.createTextNode(POps.tn('Hedefte {n} Linux bilgisayar var: komut Windows\'ta cmd.exe, Linux\'ta sh ile çalışır; ikisinde de geçerli olmalı.', linuxN))]));
    if (capOff.length) notes.push(POps.el('span', {}, [dot('bad'), document.createTextNode(POps.t('Uzak komut kapalı olduğu için reddedilecek: {names}', { names: capOff.slice(0, 4).map(POps.deviceName).join(', ') + (capOff.length > 4 ? ' +' + (capOff.length - 4) : '') }))]));
    note.replaceChildren(...notes);
}

// ---- Hedef ekleme: arama kutusu ve öneri listesi (sınıflar üstte, sonra bilgisayarlar)
const sug = { open: false, idx: 0, items: [] };
function sugItems(q) {
    const ql = q.toLocaleLowerCase('tr');
    const hit = (...vals) => !ql || vals.some(v => String(v || '').toLocaleLowerCase('tr').includes(ql));
    const labs = POps.dev.labs().filter(l => !tgt.labs.includes(l) && hit(l)).slice(0, 8).map(v => ({ kind: 'lab', v }));
    const pcs = state.devices.filter(d => !tgt.pcs.includes(d.hostname) && hit(POps.deviceName(d), d.hostname, d.lab, d.ip, POps.dev.user(d)))
        .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }))
        .slice(0, 40).map(d => ({ kind: 'pc', v: d.hostname }));
    return labs.concat(pcs);
}
function sugOptionHtml(it, active) {
    if (it.kind === 'lab') {
        const pcs = state.devices.filter(d => d.lab === it.v);
        const on = pcs.filter(d => !POps.isOffline(d)).length;
        return `<button type="button" class="o${active ? ' on' : ''}" role="option" aria-selected="${active ? 'true' : 'false'}" data-lab="${escapeHtml(it.v)}" tabindex="-1">${POps.iconHtml('labs', 'sm')}`
            + `<span class="t">${escapeHtml(it.v)}</span><span class="d">${escapeHtml(POps.tn('{n} bilgisayar', pcs.length) + ' · ' + POps.t('{n} açık', { n: on }))}</span></button>`;
    }
    const d = POps.dev.find(it.v);
    const st = POps.dev.state(d);
    const where = d.lab && d.lab !== POps.dev.UNASSIGNED ? d.lab : POps.t('Atanmamış');
    return `<button type="button" class="o${active ? ' on' : ''}" role="option" aria-selected="${active ? 'true' : 'false'}" data-pc="${escapeHtml(it.v)}" tabindex="-1"><span class="dot ${escapeHtml(st.cls)}"></span>`
        + `<span class="t">${escapeHtml(POps.deviceName(d))}</span><span class="d">${escapeHtml(where + ' · ' + st.word)}</span></button>`;
}
function renderSug() {
    const box = document.getElementById('tmSug');
    const input = document.getElementById('tmAdd');
    input.setAttribute('aria-expanded', sug.open ? 'true' : 'false');
    if (!sug.open) { box.hidden = true; return; }
    sug.items = sugItems(input.value.trim());
    sug.idx = Math.max(0, Math.min(sug.idx, sug.items.length - 1));
    box.hidden = false;
    if (!sug.items.length) { box.innerHTML = `<div class="none">${POps.tHtml('Eşleşen bilgisayar ya da sınıf yok.')}</div>`; return; }
    const labsHtml = sug.items.filter(it => it.kind === 'lab').map(it => sugOptionHtml(it, sug.items.indexOf(it) === sug.idx)).join('');
    const pcsHtml = sug.items.filter(it => it.kind === 'pc').map(it => sugOptionHtml(it, sug.items.indexOf(it) === sug.idx)).join('');
    box.innerHTML = (labsHtml ? `<div class="h">${POps.tHtml('Sınıflar')}</div>${labsHtml}` : '') + (pcsHtml ? `<div class="h">${POps.tHtml('Bilgisayarlar')}</div>${pcsHtml}` : '');
    const cur = box.querySelector('.o.on');
    if (cur) cur.scrollIntoView({ block: 'nearest' });
}
function addTarget(kind, v) {
    if (kind === 'lab' && !tgt.labs.includes(v)) tgt.labs.push(v);
    if (kind === 'pc' && !tgt.pcs.includes(v)) tgt.pcs.push(v);
    document.getElementById('tmAdd').value = '';
    renderTarget();
    renderSug();
}

function appendToTerminal(html) {
    const out = document.getElementById('popsTerminalScreen');
    const box = document.getElementById('tmOut');
    terminalHistory.push(html);
    if (out) { out.insertAdjacentHTML('beforeend', html); box.scrollTop = box.scrollHeight; }
}

function clearTerminal() {
    terminalHistory = [];
    processedResponses.clear();
    document.getElementById('popsTerminalScreen').replaceChildren();
}

async function copyText(txt) {
    try { await navigator.clipboard.writeText(txt); return true; } catch (e) { /* http bağlantısında pano API'si yok */ }
    const ta = POps.el('textarea', { style: 'position:fixed;left:-9999px;top:0', 'aria-hidden': 'true' });
    ta.value = txt;
    document.body.appendChild(ta);
    ta.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
    ta.remove();
    return ok;
}

document.addEventListener('DOMContentLoaded', () => {
    initTerminalWebSocket();
    clearTerminal();
    POps.watchDevices();
    let devicesKey = '';
    document.addEventListener('pops_data_updated', () => {
        const key = state.devices.map(d => d.hostname + '|' + d.lab + '|' + POps.deviceName(d) + '|' + d.status + '|' + d.cap_terminal_enabled).join(';');
        if (key !== devicesKey) { devicesKey = key; renderTarget(); if (sug.open) renderSug(); }
    });

    const termInput = document.getElementById('terminalCommand');
    const addInput = document.getElementById('tmAdd');
    const sugBox = document.getElementById('tmSug');
    addInput.addEventListener('focus', () => { sug.open = true; renderSug(); });
    addInput.addEventListener('input', () => { sug.open = true; sug.idx = 0; renderSug(); });
    addInput.addEventListener('keydown', (e) => {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            if (!sug.open) { sug.open = true; renderSug(); return; }
            const n = sug.items.length;
            if (n) { sug.idx = (sug.idx + (e.key === 'ArrowDown' ? 1 : n - 1)) % n; renderSug(); }
        } else if (e.key === 'Enter') {
            e.preventDefault();
            const it = sug.open && sug.items[sug.idx];
            if (it) addTarget(it.kind, it.v);
        } else if (e.key === 'Escape' && sug.open) {
            e.preventDefault(); e.stopPropagation();
            sug.open = false; renderSug();
        }
    });
    addInput.addEventListener('blur', () => { sug.open = false; renderSug(); });
    // Listeye tıklamak odağı arama kutusundan almasın (art arda ekleme)
    sugBox.addEventListener('mousedown', (e) => e.preventDefault());
    sugBox.addEventListener('click', (e) => {
        const b = e.target.closest('[data-lab], [data-pc]');
        if (b) addTarget(b.dataset.lab !== undefined ? 'lab' : 'pc', b.dataset.lab !== undefined ? b.dataset.lab : b.dataset.pc);
    });
    document.getElementById('tmChips').addEventListener('click', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        if (b.dataset.more) tgt.showAll = true;
        else if (b.dataset.rmLab !== undefined) tgt.labs = tgt.labs.filter(l => l !== b.dataset.rmLab);
        else if (b.dataset.rmPc !== undefined) tgt.pcs = tgt.pcs.filter(h => h !== b.dataset.rmPc);
        else return;
        renderTarget();
    });
    document.getElementById('tmClear').addEventListener('click', () => { tgt.labs = []; tgt.pcs = []; tgt.showAll = false; renderTarget(); addInput.focus(); });

    document.getElementById('copyTerminalBtn').addEventListener('click', async () => {
        const ok = await copyText(document.getElementById('popsTerminalScreen').innerText);
        POps.toast(ok ? 'success' : 'error', ok ? POps.t('Terminal çıktısı kopyalandı.') : POps.t('Kopyalanamadı; metni seçip elle kopyalayın.'));
    });
    document.getElementById('clearTerminalBtn').addEventListener('click', () => { clearTerminal(); termInput.focus(); });

    document.getElementById('quickBtn').addEventListener('click', (e) => {
        const btn = e.currentTarget;
        const wake = () => {
            const t = currentTarget();
            if (!t) return;
            const lab = singleLab();
            if (lab) POps.dev.power('wake', labPcs(lab), { btn, lab, wholeLab: true });
            else POps.dev.power('wake', targetHosts(), { btn });
        };
        const windowsItems = targetOs !== 'windows' ? [{ header: POps.t('Hızlı komutlar Windows içindir; hedefte Linux bilgisayar var.') }] : [
            ...Object.keys(QUICK_ACTIONS).map(k => ({ label: POps.taskName(QUICK_ACTIONS[k].name), icon: { dns: 'wifi', network: 'refresh', spooler: 'file', temp: 'trash', gpupdate: 'shield' }[k], onClick: () => window.runQuickAction(k, btn) })),
            '-',
            { label: POps.t('Uygulamayı kapat…'), icon: 'x', onClick: () => window.promptTaskkill(btn) },
            singlePc() ? { label: POps.t('Bilgisayarı yeniden adlandır…'), icon: 'edit', onClick: () => window.promptSingleRename(btn) } : null,
            singleLab() ? { label: POps.t('Sınıfı toplu adlandır…'), icon: 'edit', onClick: () => window.promptAutoRename(btn) } : null
        ];
        POps.menu(btn, [
            ...windowsItems,
            '-',
            { label: POps.t('Hedefi uyandır'), icon: 'zap', onClick: wake }
        ]);
    });

    // Komut geçmişi: bu sayfadan gönderilen işler (kim, ne zaman, hedef, sonuç)
    document.getElementById('historyBtn').addEventListener('click', async () => {
        const body = POps.drawer.open('terminal-history');
        body.innerHTML = '<div class="drawer-head"><div class="drawer-title"><span class="drawer-ico">' + POps.iconHtml('clock', 'lg') + '</span><div><h2>' + POps.tHtml('Komut geçmişi') + '</h2><div class="sub">' + POps.tHtml('Uzak komut sayfasından gönderilenler') + '</div></div></div><button type="button" class="ibtn sm" data-close="1" aria-label="' + escapeHtml(POps.t('Paneli kapat')) + '">' + POps.iconHtml('x', 'sm') + '</button></div><div id="tmHist"></div>';
        body.onclick = (ev) => {
            if (ev.target.closest('[data-close]')) POps.drawer.close();
            const row = ev.target.closest('[data-cmd]');
            if (row && row.dataset.cmd && !ev.target.closest('a')) { termInput.value = row.dataset.cmd; POps.drawer.close(); termInput.focus(); }
        };
        const box = body.querySelector('#tmHist');
        POps.setLoading(box);
        let tasks;
        try { tasks = await POps.get('/api/tasks?limit=1000'); } catch (err) { POps.setError(box, err); return; }
        const mine = (tasks || []).filter(t => t.source === 'terminal' || (!t.source && (t.title === 'Terminal' || t.title === 'Komut')));
        const groups = new Map();
        mine.forEach(t => { const k = t.batch_id || [t.created_at, t.created_by, t.script_path].join('|'); if (!groups.has(k)) groups.set(k, { key: k, t, list: [] }); groups.get(k).list.push(t); });
        const rows = [...groups.values()].slice(0, 40);
        if (!rows.length) { POps.setEmpty(box, { icon: 'terminal', title: POps.t('Henüz komut gönderilmedi'), compact: true }); return; }
        box.innerHTML = rows.map(g => {
            const c = { ok: 0, bad: 0, run: 0 };
            g.list.forEach(t => { c[POps.taskState(t.status)] += 1; });
            const k = c.run ? 'run' : c.bad ? 'bad' : 'ok';
            const whereHtml = g.list.length === 1
                ? `<a href="pc?id=${encodeURIComponent(g.t.target_pc)}">${escapeHtml(POps.dev.name(g.t.target_pc))}</a>`
                : escapeHtml(POps.tn('{n} bilgisayar', g.list.length));
            const free = !g.t.title || g.t.title === 'Terminal' || g.t.title === 'Komut';
            const cmd = free ? g.t.script_path : POps.taskName(g.t.title);
            return `<div class="act${free ? ' clickable' : ''}" data-cmd="${escapeHtml(free ? g.t.script_path || '' : '')}">
                <div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div>
                <div style="min-width:0"><div class="what mono" style="font-size:12px">${escapeHtml(String(cmd || '').slice(0, 160))}</div>
                <div class="meta">${escapeHtml(POps.tPattern(g.t.created_by || '?'))} · ${POps.timeHtml(g.t.created_at)} · ${whereHtml}${g.t.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: g.t.reason }) : ''}</div></div>
                <div class="side"><span class="word ${escapeHtml(k)}">${c.run ? POps.tHtml('Sürüyor') : c.bad ? POps.tHtml('{n} başarısız', { n: Number(c.bad) }) : POps.tHtml('Tamam')}</span><a class="when" href="tasks?job=${encodeURIComponent(g.key)}">${POps.tHtml('ayrıntı')}</a></div>
            </div>`;
        }).join('') + '<div class="set-note">' + POps.tHtml('Bir satıra tıklayınca komut yeniden yazılır (gönderilmez).') + '</div>';
    });
    document.getElementById('tmOut').addEventListener('click', (e) => { if (!e.target.closest('a, button') && !window.getSelection().toString()) termInput.focus(); });
    termInput.addEventListener('keydown', async (e) => {
        if (e.key !== 'Enter' || e.isComposing) return;
        e.preventDefault();
        const command = termInput.value.trim();
        if (!command) return;
        const lower = command.toLowerCase();
        if (lower === 'cls' || lower === 'clear') { termInput.value = ''; clearTerminal(); return; }
        if (lower.startsWith('/setname ')) { termInput.value = ''; renameSingle(command.split(/\s+/)[1] || '', null); return; }
        if (lower.startsWith('/otorename ')) {
            const parts = command.split(/\s+/);
            termInput.value = '';
            renameLab(parts[1] || '', parseInt(parts[2], 10) || 0, null);
            return;
        }
        const target = currentTarget();
        if (!target) { addInput.focus(); return; }
        termInput.value = '';
        termInput.disabled = true;
        appendToTerminal(`<div style="margin-top:1rem;margin-bottom:0.5rem;"><span class="warn">${escapeHtml(target.label + promptMark())}</span> <span class="cmd-block">${escapeHtml(command)}</span></div>`);
        await sendTasks(target, [{ name: 'Komut', type: 'CMD', command }], null, reasonText());
        termInput.disabled = false;
        termInput.focus();
    });
    renderTarget();
});
</script>

<?php include 'includes/footer.php'; ?>
