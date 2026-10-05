<?php include 'includes/header.php'; ?>

<style>
    .tm-target { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 12px; }
    .tm-area { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; flex: 1; min-width: 0; }
    .tm-area .search-field { flex: 0 1 220px; min-width: 160px; }
    .tm-area select { flex: 0 1 340px; width: auto; min-width: 200px; }
    .tm-area .chip { cursor: pointer; }
    .tm-note { font-size: var(--text-xs); color: var(--text-tertiary); margin: 0 0 12px; display: flex; gap: 14px; flex-wrap: wrap; }
    .tm-note:empty { display: none; }
    .tm-note span { display: inline-flex; align-items: center; gap: 6px; }
    .tm-console { position: relative; background: #1d1d1f; border-radius: 16px; padding: 18px 20px; min-height: 560px; max-height: calc(100vh - 260px); overflow-y: auto; font-family: var(--font-mono); color: #e5e5ea; font-size: 13px; cursor: text; display: flex; flex-direction: column; }
    .tm-tools { position: sticky; top: 0; align-self: flex-end; display: flex; gap: 2px; margin: -8px -10px 0 0; z-index: 2; }
    .tm-tools .ibtn { color: #8e8e93; }
    .tm-tools .ibtn:hover { background: #2c2c2e; color: #fff; }
    .pops-terminal-screen { line-height: 1.55; flex: 1; }
    .pops-terminal-screen .header { color: #aeaeb2; margin-bottom: 10px; }
    .pops-terminal-screen .header .title { color: #fff; font-weight: 600; }
    .pops-terminal-screen .tip { color: #8e8e93; }
    .pops-terminal-screen .cmd-block { color: #fff; }
    .pops-terminal-screen .cmd-output { display: flex; gap: 10px; margin-bottom: 8px; background: rgba(255, 255, 255, 0.04); padding: 8px 12px; border-radius: 10px; }
    .pops-terminal-screen .cmd-output .pc { color: #64d2ff; font-weight: 600; white-space: nowrap; }
    .pops-terminal-screen .info { color: #8e8e93; font-size: 12px; }
    .pops-terminal-screen .err { color: #ff6961; }
    .pops-terminal-screen .warn { color: #ffd60a; }
    .pops-terminal-screen .ok { color: #30d158; }
    .cmd-input-line { display: flex; align-items: center; margin-top: 12px; border-top: 1px solid rgba(255, 255, 255, 0.08); padding-top: 10px; }
    .cmd-prefix { margin-right: 8px; color: #fff; font-weight: 600; white-space: nowrap; max-width: 45%; overflow: hidden; text-overflow: ellipsis; }
    .cmd-input { background: transparent; border: none; color: #fff; font-family: var(--font-mono); font-size: 13px; flex: 1; outline: none; padding: 0; box-shadow: none; min-height: 0 !important; }
    .cmd-input:focus { box-shadow: none; }
    .cmd-input::placeholder { color: #636366; }
</style>

<div class="page-header">
    <div>
        <h1><?php _e('Uzak komut'); ?></h1>
        <div class="summary" id="tmSummary"><span class="sum faint"><?php _e('Hedef seçin'); ?></span></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="btn secondary" id="quickBtn" aria-haspopup="menu"><?php echo pops_icon('zap', 'sm'); ?><?php _e('Hızlı komutlar'); ?><?php echo pops_icon('chev', 'sm'); ?></button>
        <button type="button" class="ibtn boxed" id="historyBtn" data-tip="<?php _e('Komut geçmişi'); ?>" data-tip-pos="left" aria-label="<?php _e('Komut geçmişi'); ?>"><?php echo pops_icon('clock'); ?></button>
    </div>
</div>

<div class="tm-target">
    <div class="segmented" id="tmMode" role="group" aria-label="<?php _e('Hedef türü'); ?>">
        <button type="button" id="btnModeSingle" data-mode="single" class="active" aria-pressed="true"><?php _e('Bilgisayar'); ?></button>
        <button type="button" id="btnModeLab" data-mode="lab" aria-pressed="false"><?php _e('Sınıf'); ?></button>
        <button type="button" id="btnModeMulti" data-mode="multi" aria-pressed="false" hidden><?php _e('Seçili'); ?></button>
    </div>
    <div id="areaSingle" class="tm-area">
        <div class="search-field"><?php echo pops_icon('search', 'sm'); ?><input type="search" id="terminalDeviceSearch" placeholder="<?php _e('Ad, sınıf ya da IP'); ?>" aria-label="<?php _e('Bilgisayar ara'); ?>"></div>
        <select id="terminalDeviceSelect" aria-label="<?php _e('Bilgisayar'); ?>"><option value=""><?php _e('Bilgisayar seçin'); ?></option></select>
    </div>
    <div id="areaLab" class="tm-area chip-row" hidden></div>
    <div id="areaMulti" class="tm-area chip-row" hidden></div>
</div>
<div class="tm-note" id="tmNote" role="status"></div>

<div class="tm-console" id="terminalContainer">
    <div class="tm-tools">
        <button type="button" class="ibtn sm" id="copyTerminalBtn" data-tip="<?php _e('Çıktıyı kopyala'); ?>" data-tip-pos="left" aria-label="<?php _e('Çıktıyı kopyala'); ?>"><?php echo pops_icon('copy', 'sm'); ?></button>
        <button type="button" class="ibtn sm" id="clearTerminalBtn" data-tip="<?php _e('Ekranı temizle (cls)'); ?>" data-tip-pos="left" aria-label="<?php _e('Ekranı temizle'); ?>"><?php echo pops_icon('trash', 'sm'); ?></button>
    </div>
    <div class="pops-terminal-screen" id="popsTerminalScreen"></div>
    <div class="cmd-input-line" id="cmdInputLine">
        <span id="cmdPrefix" class="cmd-prefix">POps:\&gt;</span>
        <input type="text" id="terminalCommand" class="cmd-input" autocomplete="off" spellcheck="false" placeholder="<?php _e("Komut yazın ve Enter'a basın"); ?>" aria-label="<?php _e('Komut'); ?>">
    </div>
</div>

<script>
const TERMINAL_ADMIN = <?php echo json_encode($_SESSION['username'] ?? 'Admin', JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_INVALID_UTF8_SUBSTITUTE); ?>;
let currentMode = 'single';
let selectedLab = null;
// Sınıflar / Cihazlar sayfasından gelen seçim: terminal?pc=HW-1,HW-2
const urlHosts = (new URLSearchParams(location.search).get('pc') || '').split(',').map(s => s.trim()).filter(Boolean).slice(0, 500);
let multiHosts = urlHosts.length > 1 ? urlHosts : [];
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
        const pcName = payload.pc_name || payload.id || POps.t('Bilinmeyen bilgisayar');
        appendToTerminal(`<div class="cmd-output"><div class="pc">[${escapeHtml(pcName)}]</div><pre style="margin:0;color:#e2e8f0;font-family:var(--font-mono);font-size:0.8125rem;white-space:pre-wrap;word-wrap:break-word;">${escapeHtml(String(payload.output || POps.t('Çıktı yok.')).trim())}</pre></div>`);
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

function selectedDevice() {
    const sel = document.getElementById('terminalDeviceSelect');
    const d = state.devices.find(x => x.hostname === sel.value);
    return d ? { id: d.hostname, name: POps.deviceName(d) } : null;
}

// Hedef: tek bilgisayar ya da seçili sınıf. Yoksa uyarı ve null.
function currentTarget() {
    if (currentMode === 'multi') {
        if (!multiHosts.length) { POps.toast('warning', POps.t('Seçili bilgisayar yok.')); return null; }
        return { mode: 'PC', targets: multiHosts.slice(), label: POps.tn('{n} bilgisayar', multiHosts.length) };
    }
    if (currentMode === 'single') {
        const d = selectedDevice();
        if (!d) { POps.toast('warning', POps.t('Önce bir bilgisayar seçin.')); return null; }
        return { mode: 'PC', targets: [d.id], label: d.name };
    }
    if (!selectedLab) { POps.toast('warning', POps.t('Önce bir sınıf seçin.')); return null; }
    return { mode: 'LAB', targets: [selectedLab], label: POps.t('Sınıf: {lab}', { lab: selectedLab }) };
}

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
    const reason = await POps.prompt({ title: POps.taskName(action.name), message: POps.t('{target} için çalıştırılacak.', { target: target.label }), label: POps.t('Gerekçe (denetim kaydına yazılır)'), required: true, maxLength: 300, confirmText: POps.t('Çalıştır') });
    if (reason === null) return;
    appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] ${escapeHtml(POps.taskName(action.name))} · ${escapeHtml(target.label)} · ${POps.tHtml('Gerekçe: {reason}', { reason })}</div>`);
    await sendTasks(target, [{ name: action.name, type: 'CMD', command: action.cmd }], btn, reason);
};

function renameCommand(newName) {
    // newName PC_NAME_RE ile doğrulanmıştır: tırnak, boşluk ya da $ içermez
    return `powershell -Command "$newName='${newName}'; (Get-WmiObject Win32_ComputerSystem).Rename($newName); $oldUser=(Get-LocalUser | Where-Object {$_.Enabled -and $_.Name -notmatch 'Administrator|Guest|DefaultAccount|WDAGUtilityAccount|system'} | Select-Object -First 1).Name; if($oldUser){ Rename-LocalUser -Name $oldUser -NewName $newName; Set-LocalUser -Name $newName -FullName $newName; }; $i=1; Get-NetAdapter | Where-Object {$_.Name -notmatch 'Baglanti_'} | ForEach-Object { Rename-NetAdapter -Name $_.Name -NewName ('Baglanti_'+$i); $i++ }; Write-Output 'Isim ${newName} olarak degistirildi.'; shutdown /r /t 5"`;
}

async function renameSingle(newName, btn) {
    if (!windowsOnly()) return;
    const d = selectedDevice();
    if (currentMode !== 'single' || !d) { POps.toast('warning', POps.t('Tek bilgisayar modunda bir bilgisayar seçin.')); return; }
    if (!PC_NAME_RE.test(newName)) { POps.toast('error', POps.t('Geçersiz ad: en çok 15 karakter; harf, rakam ve tire.')); return; }
    if (!await POps.confirm({ title: POps.t('Bilgisayar yeniden adlandırılsın mı?'), message: POps.t('{old} → {name}. Bilgisayar 5 saniye sonra yeniden başlar; yerel kullanıcı ve ağ bağdaştırıcıları da yeniden adlandırılır.', { old: d.name, name: newName }), confirmText: POps.t('Adlandır ve yeniden başlat'), danger: true })) return;
    appendToTerminal(`<div class="cmd-block" style="margin-top:0.75rem;margin-bottom:0.5rem;"><span class="warn">[*]</span> ${escapeHtml(d.name)} → '${escapeHtml(newName)}'</div>`);
    await sendTasks({ mode: 'PC', targets: [d.id], label: d.name }, [{ name: 'Yeniden adlandır', type: 'CMD', command: renameCommand(newName) }], btn);
}

async function renameLab(baseName, limit, btn) {
    if (currentMode !== 'lab' || !selectedLab) { POps.toast('warning', POps.t('Sınıf modunda bir sınıf seçin.')); return; }
    if (!windowsOnly()) return;
    if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,12}$/.test(baseName)) { POps.toast('error', POps.t('Geçersiz önek: en çok 13 karakter; harf, rakam ve tire (iki haneli numara eklenir).')); return; }
    let devs = state.devices.filter(d => d.lab === selectedLab)
        .filter(d => { const n = String(d.real_hostname || '').toUpperCase(); return !(n.includes('PC00') || n.includes('ANA') || n.includes('OGR')); })
        .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true, sensitivity: 'base' }));
    if (limit > 0 && limit < devs.length) devs = devs.slice(0, limit);
    if (!devs.length) { POps.toast('warning', POps.t('Bu sınıfta adlandırılacak bilgisayar yok.')); return; }
    const plan = devs.map((d, i) => [d, baseName + String(i + 1).padStart(2, '0')]);
    const list = plan.slice(0, 6).map(([d, n]) => `${POps.deviceName(d)} → ${n}`).join(', ') + (plan.length > 6 ? ' …' : '');
    if (!await POps.confirm({ title: POps.tn('{n} bilgisayar yeniden adlandırılsın mı?', devs.length), message: POps.t('{list}. Her bilgisayar 5 saniye sonra yeniden başlar.', { list }), confirmText: POps.t('Adlandır'), danger: true })) return;
    appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] ${POps.tnHtml('Toplu adlandırma: {n} bilgisayar', devs.length)}</div>`);
    await POps.busy(btn, async () => {
        for (const [d, n] of plan) {
            try {
                await POps.post('/api/deploy_orchestration', { target_mode: 'PC', targets: [d.hostname], taskSequence: [{ name: 'Toplu adlandırma', type: 'CMD', command: renameCommand(n) }], title: 'Toplu adlandırma', source: 'terminal' }, { jobTitle: POps.taskName('Toplu adlandırma') + ' · ' + n });
                appendToTerminal(`<div style="color:#4ade80;margin-bottom:0.25rem;">→ ${escapeHtml(POps.deviceName(d))} → ${escapeHtml(n)}</div>`);
            } catch (e) {
                appendToTerminal(`<div class="err">[-] ${escapeHtml(POps.deviceName(d))}: ${escapeHtml(POps.errorMessage(e))}</div>`);
            }
        }
    });
}

window.promptSingleRename = async function(btn) {
    if (currentMode !== 'single' || !selectedDevice()) { POps.toast('warning', POps.t('Tek bilgisayar modunda bir bilgisayar seçin.')); return; }
    const name = await POps.prompt({ title: POps.t('Bilgisayarı yeniden adlandır'), label: POps.t('Yeni ad'), placeholder: 'LAB1-PC05', required: true, maxLength: 15,
        validate: (v) => PC_NAME_RE.test(v.trim()) ? null : POps.t('En çok 15 karakter; harf, rakam ve tire.') });
    if (name !== null) renameSingle(name.trim(), btn);
};

window.promptAutoRename = async function(btn) {
    if (currentMode !== 'lab' || !selectedLab) { POps.toast('warning', POps.t('Sınıf modunda bir sınıf seçin.')); return; }
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
    const reason = await POps.prompt({ title: POps.t('Gerekçe'), label: POps.t('Gerekçe (denetim kaydına yazılır)'), required: true, maxLength: 300, confirmText: POps.t('Kapat') });
    if (reason === null) return;
    appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] ${POps.tHtml('Uygulama kapatılıyor: {app}', { app: exe.trim() })} · ${escapeHtml(target.label)} · ${POps.tHtml('Gerekçe: {reason}', { reason })}</div>`);
    await sendTasks(target, [{ name: 'Uygulamayı kapat: ' + exe.trim(), type: 'CMD', command: `taskkill /F /IM "${exe.trim()}"` }], btn, reason);
};

function renderTerminal() {
    const select = document.getElementById('terminalDeviceSelect');
    const term = (document.getElementById('terminalDeviceSearch').value || '').toLowerCase();
    const list = state.devices.filter(d => [POps.deviceName(d), d.hostname, d.lab, d.ip].join(' ').toLowerCase().includes(term))
        .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
    const current = select.value;
    select.replaceChildren(POps.el('option', { value: '', text: POps.t('Bilgisayar seçin ({n})', { n: list.length }) }),
        ...list.map(d => POps.el('option', { value: d.hostname, text: `${POps.deviceName(d)} · ${d.lab === 'Atanmamis_Cihazlar' ? POps.t('Atanmamış') : (d.lab || '-')}${POps.isOffline(d) ? ' · ' + POps.t('çevrimdışı') : ''}` })));
    if (current && list.some(d => d.hostname === current)) select.value = current;

    const labArea = document.getElementById('areaLab');
    const labs = [...new Set(state.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar').sort((a, b) => a.localeCompare(b, 'tr'));
    labArea.replaceChildren(...(labs.length ? labs.map(lab => {
        const pcs = state.devices.filter(d => d.lab === lab);
        const b = POps.el('button', { type: 'button', className: 'chip' + (selectedLab === lab ? ' active' : ''), 'aria-pressed': selectedLab === lab ? 'true' : 'false' },
            [document.createTextNode(lab), POps.el('span', { className: 'count', text: String(pcs.filter(d => !POps.isOffline(d)).length) + '/' + pcs.length })]);
        b.addEventListener('click', () => { selectedLab = selectedLab === lab ? null : lab; renderTerminal(); });
        return b;
    }) : [POps.el('span', { className: 'text-sm text-muted', text: POps.t('Henüz sınıf yok.') })]));

    const multiArea = document.getElementById('areaMulti');
    multiArea.replaceChildren(...multiHosts.slice(0, 10).map(h => POps.el('span', { className: 'chip', text: POps.dev.name(h) })),
        ...(multiHosts.length > 10 ? [POps.el('span', { className: 'chip', text: `+${multiHosts.length - 10}` })] : []));

    // Hedefin özeti ve göndermeden önce bilinmesi gerekenler
    const d = selectedDevice();
    const prefix = document.getElementById('cmdPrefix');
    let hosts = [];
    let label = '';
    if (currentMode === 'single' && d) { hosts = [d.id]; label = d.name; }
    else if (currentMode === 'lab' && selectedLab) { hosts = state.devices.filter(x => x.lab === selectedLab).map(x => x.hostname); label = selectedLab; }
    else if (currentMode === 'multi') { hosts = multiHosts; label = POps.tn('{n} bilgisayar', multiHosts.length); }
    const devs = hosts.map(h => POps.dev.find(h)).filter(Boolean);
    const linuxN = devs.filter(POps.dev.isLinux).length;
    targetOs = linuxN && linuxN < devs.length ? 'mixed' : linuxN ? 'linux' : 'windows';
    prefix.textContent = label ? `${label}${promptMark()}` : POps.t('Hedef seçin') + ':\\>';
    const onN = devs.filter(x => !POps.isOffline(x)).length;
    const offN = hosts.length - onN;
    const capOff = devs.filter(x => x.cap_terminal_enabled === false);
    const sum = document.getElementById('tmSummary');
    const note = document.getElementById('tmNote');
    if (!hosts.length) {
        sum.replaceChildren(POps.el('span', { className: 'sum faint', text: currentMode === 'lab' ? POps.t('Bir sınıf seçin') : POps.t('Bir bilgisayar seçin') }));
        note.replaceChildren();
        return;
    }
    const dot = (cls) => POps.el('span', { className: 'dot ' + cls });
    sum.replaceChildren(
        POps.el('span', { className: 'sum' }, POps.tNodes('Hedef: {target}', null, { target: POps.el('b', { text: label }) })),
        POps.el('span', { className: 'sum' }, [dot('on'), ...POps.tNodes('{n} açık', { n: onN }, { n: POps.el('b', { text: String(onN) }) })]),
        ...(offN ? [POps.el('span', { className: 'sum' }, [dot('off'), ...POps.tNodes('{n} kapalı', { n: offN }, { n: POps.el('b', { text: String(offN) }) })])] : []));
    const notes = [];
    if (offN) notes.push(POps.el('span', {}, [dot('warn'), document.createTextNode(POps.tn('Kapalı {n} bilgisayarda komut, açıldığında çalışır.', offN))]));
    if (targetOs === 'linux') notes.push(POps.el('span', {}, [dot('on'), document.createTextNode(POps.t('Linux: komut root olarak /bin/sh ile çalışır; Windows hızlı komutları gösterilmez.'))]));
    if (targetOs === 'mixed') notes.push(POps.el('span', {}, [dot('warn'), document.createTextNode(POps.tn('Hedefte {n} Linux bilgisayar var: komut Windows\'ta cmd.exe, Linux\'ta sh ile çalışır; ikisinde de geçerli olmalı.', linuxN))]));
    if (capOff.length) notes.push(POps.el('span', {}, [dot('bad'), document.createTextNode(POps.t('Uzak komut kapalı olduğu için reddedilecek: {names}', { names: capOff.slice(0, 4).map(POps.deviceName).join(', ') + (capOff.length > 4 ? ' +' + (capOff.length - 4) : '') }))]));
    note.replaceChildren(...notes);
}

function terminalHeaderHtml() {
    return `<div class="header">
        <div class="title">${POps.tHtml('POps komut satırı')}</div>
        <div>${POps.tHtml('Yönetici: {name}', { name: TERMINAL_ADMIN })} · ${POps.tHtml('Komutlar Windows\'ta SYSTEM hesabıyla cmd.exe ile, Linux\'ta root olarak /bin/sh ile çalışır.')}</div>
    </div>
    <div class="tip">${POps.tHtml('Rutin işler için sağ üstteki Hızlı komutlar menüsünü kullanın. Ekranı temizlemek için: cls')}</div>`;
}

function appendToTerminal(html) {
    const out = document.getElementById('popsTerminalScreen');
    const c = document.getElementById('terminalContainer');
    terminalHistory.push(html);
    if (out) { out.insertAdjacentHTML('beforeend', html); c.scrollTop = c.scrollHeight; }
}

function clearTerminal() {
    terminalHistory = [];
    processedResponses.clear();
    document.getElementById('popsTerminalScreen').innerHTML = terminalHeaderHtml();
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
        const key = state.devices.map(d => d.hostname + '|' + d.lab + '|' + POps.deviceName(d) + '|' + d.status).join(';');
        if (key !== devicesKey) { devicesKey = key; renderTerminal(); }
    });

    const termInput = document.getElementById('terminalCommand');
    const setMode = (mode) => {
        currentMode = mode;
        if (mode !== 'lab') selectedLab = null;
        document.querySelectorAll('#tmMode [data-mode]').forEach(b => { const on = b.dataset.mode === mode; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        document.getElementById('areaSingle').hidden = mode !== 'single';
        document.getElementById('areaLab').hidden = mode !== 'lab';
        document.getElementById('areaMulti').hidden = mode !== 'multi';
        renderTerminal();
    };
    document.getElementById('tmMode').addEventListener('click', (e) => { const b = e.target.closest('[data-mode]'); if (b) setMode(b.dataset.mode); });
    if (multiHosts.length) {
        const mb = document.getElementById('btnModeMulti');
        mb.hidden = false;
        mb.textContent = POps.t('Seçili {n}', { n: multiHosts.length });
        currentMode = 'multi';
    }
    document.getElementById('terminalDeviceSearch').addEventListener('input', renderTerminal);
    document.getElementById('terminalDeviceSelect').addEventListener('change', renderTerminal);

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
            if (t.mode === 'LAB') POps.dev.power('wake', state.devices.filter(d => d.lab === selectedLab).map(d => d.hostname), { btn, lab: selectedLab, wholeLab: true });
            else POps.dev.power('wake', t.targets, { btn });
        };
        const windowsItems = targetOs !== 'windows' ? [{ header: POps.t('Hızlı komutlar Windows içindir; hedefte Linux bilgisayar var.') }] : [
            ...Object.keys(QUICK_ACTIONS).map(k => ({ label: POps.taskName(QUICK_ACTIONS[k].name), icon: { dns: 'wifi', network: 'refresh', spooler: 'file', temp: 'trash', gpupdate: 'shield' }[k], onClick: () => window.runQuickAction(k, btn) })),
            '-',
            { label: POps.t('Uygulamayı kapat…'), icon: 'x', onClick: () => window.promptTaskkill(btn) },
            currentMode === 'single' ? { label: POps.t('Bilgisayarı yeniden adlandır…'), icon: 'edit', onClick: () => window.promptSingleRename(btn) } : null,
            currentMode === 'lab' ? { label: POps.t('Sınıfı toplu adlandır…'), icon: 'edit', onClick: () => window.promptAutoRename(btn) } : null
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
            if (row && !ev.target.closest('a')) { termInput.value = row.dataset.cmd; POps.drawer.close(); termInput.focus(); }
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
            const where = g.list.length === 1 ? POps.dev.name(g.t.target_pc) : POps.tn('{n} bilgisayar', g.list.length);
            const free = !g.t.title || g.t.title === 'Terminal' || g.t.title === 'Komut';
            const cmd = free ? g.t.script_path : POps.taskName(g.t.title);
            return `<div class="act clickable" data-cmd="${escapeHtml(free ? g.t.script_path || '' : '')}">
                <div class="res ${escapeHtml(k)}">${POps.iconHtml(k === 'ok' ? 'check' : k === 'bad' ? 'x' : 'clock')}</div>
                <div style="min-width:0"><div class="what mono" style="font-size:12px">${escapeHtml(String(cmd || '').slice(0, 160))}</div>
                <div class="meta">${escapeHtml(POps.tPattern(g.t.created_by || '?'))} · ${POps.timeHtml(g.t.created_at)} · ${escapeHtml(where)}${g.t.reason ? ' · ' + POps.tHtml('gerekçe: {reason}', { reason: g.t.reason }) : ''}</div></div>
                <div class="side"><span class="word ${escapeHtml(k)}">${c.run ? POps.tHtml('Sürüyor') : c.bad ? POps.tHtml('{n} başarısız', { n: Number(c.bad) }) : POps.tHtml('Tamam')}</span><a class="when" href="tasks?job=${encodeURIComponent(g.key)}">${POps.tHtml('ayrıntı')}</a></div>
            </div>`;
        }).join('') + '<div class="set-note">' + POps.tHtml('Bir satıra tıklayınca komut yeniden yazılır (gönderilmez).') + '</div>';
    });
    document.getElementById('terminalContainer').addEventListener('click', (e) => { if (!window.getSelection().toString()) termInput.focus(); });
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
        if (!target) return;
        termInput.value = '';
        termInput.disabled = true;
        appendToTerminal(`<div style="margin-top:1rem;margin-bottom:0.5rem;"><span class="warn">${escapeHtml(target.label + promptMark())}</span> <span class="cmd-block">${escapeHtml(command)}</span></div>`);
        await sendTasks(target, [{ name: 'Komut', type: 'CMD', command }], null);
        termInput.disabled = false;
        termInput.focus();
    });
    setMode(currentMode);
    if (urlHosts.length === 1) {
        const sel = document.getElementById('terminalDeviceSelect');
        const pick = () => { if ([...sel.options].some(o => o.value === urlHosts[0])) { sel.value = urlHosts[0]; renderTerminal(); return true; } return false; };
        if (!pick()) document.addEventListener('pops_data_updated', function once() { if (pick()) document.removeEventListener('pops_data_updated', once); });
    }
});
</script>

<?php include 'includes/footer.php'; ?>