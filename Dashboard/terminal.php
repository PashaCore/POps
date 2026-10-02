<?php include 'includes/header.php'; ?>

<style>
    .terminal-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); display: flex; flex-direction: column; min-height: 650px; box-shadow: var(--shadow-xs); }
    .terminal-toolbar { display: flex; flex-direction: column; gap: 0.75rem; padding: var(--space-4); border-bottom: 1px solid var(--border-subtle); }
    .terminal-toolbar-row { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 0.75rem; }
    .mode-switcher { display: flex; background: var(--bg-surface-2); padding: 0.25rem; border-radius: var(--radius-md); border: 1px solid var(--border-subtle); }
    .mode-switcher button { padding: 0.375rem 0.75rem; border: none; background: transparent; color: var(--text-tertiary); font-size: var(--text-xs); font-weight: var(--fw-semibold); border-radius: var(--radius-sm); cursor: pointer; display: inline-flex; align-items: center; gap: 0.375rem; }
    .mode-switcher button.active { background: var(--bg-surface); color: var(--primary-600); box-shadow: var(--shadow-xs); }

    .toolbar-actions { display: flex; gap: 0.5rem; flex-wrap: wrap; }

    .target-area { display: flex; gap: 0.5rem; align-items: center; flex-wrap: wrap; }
    .search-input-wrap { position: relative; width: 220px; }
    .search-input-wrap i { position: absolute; left: 0.625rem; top: 50%; transform: translateY(-50%); color: var(--text-tertiary); font-size: 0.75rem; pointer-events: none; }
    .search-input-wrap input { padding-left: 2rem; height: 36px; }

    .terminal-container { background: #0a0e1a; padding: var(--space-5); flex: 1; overflow-y: auto; font-family: var(--font-mono); color: #4ade80; font-size: 0.875rem; cursor: text; display: flex; flex-direction: column; box-shadow: inset 0 2px 12px rgba(0,0,0,0.3); border-radius: 0 0 var(--radius-lg) var(--radius-lg); }
    .pops-terminal-screen { line-height: 1.5; }
    .pops-terminal-screen .header { color: #cbd5e1; margin-bottom: var(--space-3); }
    .pops-terminal-screen .header .title { color: #f1f5f9; font-weight: var(--fw-semibold); }
    .pops-terminal-screen .tip { color: #fbbf24; }
    .pops-terminal-screen .cmd-block { color: #f1f5f9; }
    .pops-terminal-screen .cmd-output { display: flex; gap: 0.5rem; margin-bottom: 0.5rem; background: rgba(16, 185, 129, 0.05); padding: 0.5rem 0.75rem; border-left: 3px solid var(--success-solid); border-radius: 0 var(--radius-sm) var(--radius-sm) 0; }
    .pops-terminal-screen .cmd-output .pc { color: #60a5fa; font-weight: var(--fw-semibold); }
    .pops-terminal-screen .info { color: #94a3b8; font-style: italic; font-size: 0.8125rem; }
    .pops-terminal-screen .err { color: #f87171; }
    .pops-terminal-screen .warn { color: #fbbf24; }

    .cmd-input-line { display: flex; align-items: center; margin-top: 0.75rem; border-top: 1px dashed rgba(255,255,255,0.1); padding-top: 0.5rem; }
    .cmd-prefix { margin-right: 0.5rem; color: #f1f5f9; font-weight: var(--fw-bold); letter-spacing: 0.05em; }
    .cmd-input { background: transparent; border: none; color: #4ade80; font-family: var(--font-mono); font-size: 0.875rem; flex: 1; outline: none; padding: 0; box-shadow: none; }
    .cmd-input:focus { box-shadow: none; }

    .lab-btn { padding: 0.3125rem 0.625rem; border-radius: var(--radius-sm); border: 1px solid var(--border-default); font-size: 0.6875rem; background: var(--bg-surface-2); color: var(--text-secondary); cursor: pointer; font-weight: var(--fw-semibold); margin-bottom: 0.25rem; display: inline-flex; align-items: center; gap: 0.25rem; }
    .lab-btn:hover { background: var(--bg-surface); color: var(--text-primary); }
    .lab-btn.active { background: var(--primary-500); color: white; border-color: var(--primary-500); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-terminal"></i> Etkileşimli Terminal</h1>
        <p>PC'lere doğrudan komut gönder, çıktıları canlı izle</p>
    </div>
</div>

<div class="terminal-card">
    <div class="terminal-toolbar">
        <div class="terminal-toolbar-row">
            <div class="mode-switcher">
                <button id="btnModeSingle" class="active"><i class="fas fa-desktop"></i> Tekil Cihaz</button>
                <button id="btnModeLab"><i class="fas fa-network-wired"></i> Toplu (Lab)</button>
            </div>
            <div class="toolbar-actions">
                <button id="wakeUpBtn" class="btn success"><i class="fas fa-bolt"></i> <span id="wakeUpText">Uyandır</span></button>
                <button id="copyTerminalBtn" class="btn secondary"><i class="fas fa-copy"></i> Kopyala</button>
                <button id="clearTerminalBtn" class="btn secondary"><i class="fas fa-eraser"></i> Temizle</button>
            </div>
        </div>
        <div class="target-area">
            <div id="areaSingle" class="target-area" style="flex:1;">
                <div class="search-input-wrap">
                    <i class="fas fa-filter"></i>
                    <input type="text" id="terminalDeviceSearch" placeholder="Ad, sınıf ya da IP ile ara">
                </div>
                <select id="terminalDeviceSelect" style="flex:1;max-width:350px;height:36px;">
                    <option value="">Cihaz Seçin...</option>
                </select>
            </div>
            <div id="areaLab" class="target-area" style="display:none;flex:1;"></div>
        </div>
        <!-- Hızlı İşlemler -->
        <div class="quick-actions-bar" style="display:flex; gap:0.5rem; margin-top:0.75rem; flex-wrap:wrap; border-top:1px dashed var(--border-subtle); padding-top:0.75rem;">
            <span style="font-size:0.75rem; color:var(--text-tertiary); display:flex; align-items:center; margin-right:0.25rem;"><i class="fas fa-bolt"></i> Hızlı Komutlar:</span>
            <button type="button" class="lab-btn" data-quick="dns"><i class="fas fa-globe"></i> DNS Temizle</button>
            <button type="button" class="lab-btn" data-quick="network"><i class="fas fa-network-wired"></i> Ağı Yenile</button>
            <button type="button" class="lab-btn" data-quick="spooler"><i class="fas fa-print"></i> Yazıcı Kuyruğu</button>
            <button type="button" class="lab-btn" data-quick="temp"><i class="fas fa-broom"></i> Temp Temizle</button>
            <button type="button" class="lab-btn" data-quick="gpupdate"><i class="fas fa-shield-halved"></i> GPUpdate</button>
            <div style="width:1px; background:var(--border-subtle); margin:0 0.25rem;"></div>
            <button type="button" class="lab-btn" id="btnQuickSingleRename" onclick="window.promptSingleRename(this)"><i class="fas fa-tag"></i> Yeniden adlandır</button>
            <button type="button" class="lab-btn" id="btnQuickAutoRename" style="display:none;" onclick="window.promptAutoRename(this)"><i class="fas fa-tags"></i> Toplu adlandır</button>
            <button type="button" class="lab-btn" onclick="window.promptTaskkill(this)"><i class="fas fa-xmark"></i> Uygulamayı kapat</button>
        </div>
    </div>

    <div class="terminal-container" id="terminalContainer">
        <div class="pops-terminal-screen" id="popsTerminalScreen"></div>
        <div class="cmd-input-line" id="cmdInputLine">
            <span id="cmdPrefix" class="cmd-prefix">POps:\&gt;</span>
            <input type="text" id="terminalCommand" class="cmd-input" autocomplete="off" spellcheck="false" placeholder="Komut yazın...">
        </div>
    </div>
</div>

<script>
const TERMINAL_ADMIN = <?php echo json_encode($_SESSION['username'] ?? 'Admin', JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_INVALID_UTF8_SUBSTITUTE); ?>;
let currentMode = 'single';
let selectedLab = null;
let terminalHistory = [];
const processedResponses = new Set();
// Windows bilgisayar adı: en çok 15 karakter, harf, rakam ve tire (komut satırına tırnak ya da boşluk girmesin)
const PC_NAME_RE = /^[A-Za-z0-9][A-Za-z0-9-]{0,14}$/;

function initTerminalWebSocket() {
    if (typeof OMYO_API === 'undefined') return;
    let ws;
    try { ws = new WebSocket(OMYO_API.wsUrl('/ws/panel')); } catch (e) { return; }
    ws.onmessage = (event) => {
        let payload;
        try { payload = JSON.parse(event.data); } catch (e) { return; }
        if (payload.type !== 'terminal_output') return;
        const uniqueId = `${payload.task_id || '0'}_${payload.id || '?'}`;
        if (processedResponses.has(uniqueId)) return;
        processedResponses.add(uniqueId);
        const pcName = payload.pc_name || payload.id || 'Bilinmeyen bilgisayar';
        appendToTerminal(`<div class="cmd-output"><div class="pc">[${escapeHtml(pcName)}]</div><pre style="margin:0;color:#e2e8f0;font-family:var(--font-mono);font-size:0.8125rem;white-space:pre-wrap;word-wrap:break-word;">${escapeHtml(String(payload.output || 'Çıktı yok.').trim())}</pre></div>`);
    };
    // Bağlantı koparsa birkaç saniye sonra yeniden kurulur (sunucu güncellemesi, ağ kesintisi)
    ws.onclose = () => setTimeout(initTerminalWebSocket, 5000);
}

// Hızlı komutlar. Ajan her komutu bir .bat dosyasına yazıp cmd.exe /c ile SYSTEM olarak çalıştırır; buradaki
// metinler o .bat satırının kendisidir (PowerShell sarmalayıcısı yok). Komutlar '&' ile ayrılır; .bat içinde '%'
// değişken açar, bu yüzden yalnızca bilerek kullanılan %windir% var. Sıfırdan farklı çıkış kodu görevi Başarısız
// yapar: son komut işin başarısını söyler.
const QUICK_ACTIONS = {
    dns: { name: 'DNS Temizle', cmd: 'ipconfig /flushdns' },
    // Sanal/statik IP'li ya da kablosu takılı olmayan bağdaştırıcı yüzünden release/renew hata kodu dönebilir;
    // başarı ölçüsü yenilemeden sonra 169.254 dışı bir IPv4 adresinin olmasıdır (findstr bulursa 0 döner)
    network: { name: 'Ağı Yenile', cmd: 'ipconfig /release & ipconfig /renew & ipconfig | findstr /c:"IPv4" | findstr /v /c:"169.254."' },
    spooler: { name: 'Yazıcı Kuyruğu Sıfırlandı', cmd: 'net stop spooler /y & del /f /s /q "%windir%\\System32\\spool\\PRINTERS\\*.*" & net start spooler' },
    // C:\Windows\Temp ve her kullanıcının AppData\Local\Temp'i. Çalışan görevin kendi .bat'ı (pops_task_*.bat)
    // silinmez, kullanımdaki dosyalar atlanır. Bağlantılar (junction/symlink) izlenmez: kullanıcı kendi Temp'ini
    // başka bir klasöre yönlendirip SYSTEM'e orayı sildiremesin. Tek satır olmalı; içinde " ve % yok.
    temp: { name: 'Temp Temizle', cmd: 'powershell -NoProfile -ExecutionPolicy Bypass -Command "' + [
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
    gpupdate: { name: 'Grup İlkesi Güncellendi', cmd: 'gpupdate /force' },
};

function selectedDevice() {
    const sel = document.getElementById('terminalDeviceSelect');
    const d = state.devices.find(x => x.hostname === sel.value);
    return d ? { id: d.hostname, name: POps.deviceName(d) } : null;
}

// Hedef: tek bilgisayar ya da seçili sınıf. Yoksa uyarı ve null.
function currentTarget() {
    if (currentMode === 'single') {
        const d = selectedDevice();
        if (!d) { POps.toast('warning', 'Önce bir bilgisayar seçin.'); return null; }
        return { mode: 'PC', targets: [d.id], label: d.name };
    }
    if (!selectedLab) { POps.toast('warning', 'Önce bir sınıf seçin.'); return null; }
    return { mode: 'LAB', targets: [selectedLab], label: 'Sınıf: ' + selectedLab };
}

async function sendTasks(target, taskSequence, btn) {
    try {
        const r = await POps.busy(btn, () => POps.post('/api/deploy_orchestration', { target_mode: target.mode, targets: target.targets, taskSequence }));
        const created = (r && r.created) || 0;
        appendToTerminal(`<div class="info">[i] ${escapeHtml(String(created))} görev kuyruğa eklendi, çıktı bekleniyor…${r && r.skipped_module_closed ? ' (' + escapeHtml(String(r.skipped_module_closed)) + ' bilgisayarda uzak komut kapalı)' : ''}</div>`);
        return true;
    } catch (e) {
        appendToTerminal(`<div class="err">[-] Gönderilemedi: ${escapeHtml(POps.errorMessage(e))}</div>`);
        POps.toast('error', POps.errorMessage(e));
        return false;
    }
}

window.runQuickAction = async function(key, btn) {
    const action = QUICK_ACTIONS[key];
    const target = action && currentTarget();
    if (!target) return;
    const reason = await POps.prompt({ title: action.name, message: `${target.label} için çalıştırılacak.`, label: 'Gerekçe (denetim kaydına yazılır)', required: true, maxLength: 300, confirmText: 'Çalıştır' });
    if (reason === null) return;
    appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] ${escapeHtml(action.name)} · ${escapeHtml(target.label)} · Gerekçe: ${escapeHtml(reason)}</div>`);
    await sendTasks(target, [{ name: action.name, type: 'CMD', command: action.cmd }], btn);
};

function renameCommand(newName) {
    // newName PC_NAME_RE ile doğrulanmıştır: tırnak, boşluk ya da $ içermez
    return `powershell -Command "$newName='${newName}'; (Get-WmiObject Win32_ComputerSystem).Rename($newName); $oldUser=(Get-LocalUser | Where-Object {$_.Enabled -and $_.Name -notmatch 'Administrator|Guest|DefaultAccount|WDAGUtilityAccount|system'} | Select-Object -First 1).Name; if($oldUser){ Rename-LocalUser -Name $oldUser -NewName $newName; Set-LocalUser -Name $newName -FullName $newName; }; $i=1; Get-NetAdapter | Where-Object {$_.Name -notmatch 'Baglanti_'} | ForEach-Object { Rename-NetAdapter -Name $_.Name -NewName ('Baglanti_'+$i); $i++ }; Write-Output 'Isim ${newName} olarak degistirildi.'; shutdown /r /t 5"`;
}

async function renameSingle(newName, btn) {
    const d = selectedDevice();
    if (currentMode !== 'single' || !d) { POps.toast('warning', 'Tek bilgisayar modunda bir bilgisayar seçin.'); return; }
    if (!PC_NAME_RE.test(newName)) { POps.toast('error', 'Geçersiz ad: en çok 15 karakter; harf, rakam ve tire.'); return; }
    if (!await POps.confirm({ title: 'Bilgisayar yeniden adlandırılsın mı?', message: `${d.name} → ${newName}. Bilgisayar 5 saniye sonra yeniden başlar; yerel kullanıcı ve ağ bağdaştırıcıları da yeniden adlandırılır.`, confirmText: 'Adlandır ve yeniden başlat', danger: true })) return;
    appendToTerminal(`<div class="cmd-block" style="margin-top:0.75rem;margin-bottom:0.5rem;"><span class="warn">[*]</span> ${escapeHtml(d.name)} → '${escapeHtml(newName)}'</div>`);
    await sendTasks({ mode: 'PC', targets: [d.id] }, [{ name: 'Yeniden adlandır', type: 'CMD', command: renameCommand(newName) }], btn);
}

async function renameLab(baseName, limit, btn) {
    if (currentMode !== 'lab' || !selectedLab) { POps.toast('warning', 'Sınıf modunda bir sınıf seçin.'); return; }
    if (!/^[A-Za-z0-9][A-Za-z0-9-]{0,12}$/.test(baseName)) { POps.toast('error', 'Geçersiz önek: en çok 13 karakter; harf, rakam ve tire (iki haneli numara eklenir).'); return; }
    let devs = state.devices.filter(d => d.lab === selectedLab)
        .filter(d => { const n = String(d.real_hostname || '').toUpperCase(); return !(n.includes('PC00') || n.includes('ANA') || n.includes('OGR')); })
        .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true, sensitivity: 'base' }));
    if (limit > 0 && limit < devs.length) devs = devs.slice(0, limit);
    if (!devs.length) { POps.toast('warning', 'Bu sınıfta adlandırılacak bilgisayar yok.'); return; }
    const plan = devs.map((d, i) => [d, baseName + String(i + 1).padStart(2, '0')]);
    if (!await POps.confirm({ title: `${devs.length} bilgisayar yeniden adlandırılsın mı?`, message: plan.slice(0, 6).map(([d, n]) => `${POps.deviceName(d)} → ${n}`).join(', ') + (plan.length > 6 ? ' …' : '') + '. Her bilgisayar 5 saniye sonra yeniden başlar.', confirmText: 'Adlandır', danger: true })) return;
    appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] Toplu adlandırma: ${escapeHtml(String(devs.length))} bilgisayar</div>`);
    await POps.busy(btn, async () => {
        for (const [d, n] of plan) {
            try {
                await POps.post('/api/deploy_orchestration', { target_mode: 'PC', targets: [d.hostname], taskSequence: [{ name: 'Toplu adlandırma', type: 'CMD', command: renameCommand(n) }] });
                appendToTerminal(`<div style="color:#4ade80;margin-bottom:0.25rem;">→ ${escapeHtml(POps.deviceName(d))} → ${escapeHtml(n)}</div>`);
            } catch (e) {
                appendToTerminal(`<div class="err">[-] ${escapeHtml(POps.deviceName(d))}: ${escapeHtml(POps.errorMessage(e))}</div>`);
            }
        }
    });
}

window.promptSingleRename = async function(btn) {
    if (currentMode !== 'single' || !selectedDevice()) { POps.toast('warning', 'Tek bilgisayar modunda bir bilgisayar seçin.'); return; }
    const name = await POps.prompt({ title: 'Bilgisayarı yeniden adlandır', label: 'Yeni ad', placeholder: 'LAB1-PC05', required: true, maxLength: 15,
        validate: (v) => PC_NAME_RE.test(v.trim()) ? null : 'En çok 15 karakter; harf, rakam ve tire.' });
    if (name !== null) renameSingle(name.trim(), btn);
};

window.promptAutoRename = async function(btn) {
    if (currentMode !== 'lab' || !selectedLab) { POps.toast('warning', 'Sınıf modunda bir sınıf seçin.'); return; }
    const base = await POps.prompt({ title: 'Sınıfı toplu adlandır', message: 'Ada iki haneli sıra numarası eklenir (ör. LAB1-PC01, LAB1-PC02).', label: 'Önek', placeholder: 'LAB1-PC', required: true, maxLength: 13,
        validate: (v) => /^[A-Za-z0-9][A-Za-z0-9-]{0,12}$/.test(v.trim()) ? null : 'En çok 13 karakter; harf, rakam ve tire.' });
    if (base !== null) renameLab(base.trim(), 0, btn);
};

window.promptTaskkill = async function(btn) {
    const target = currentTarget();
    if (!target) return;
    const exe = await POps.prompt({ title: 'Uygulamayı kapat', message: `${target.label} üzerinde bu adla çalışan bütün süreçler zorla kapatılır.`, label: 'Program adı', placeholder: 'msedge.exe', required: true, maxLength: 100,
        validate: (v) => /^[^"%\r\n\\/:*?<>|]+$/.test(v.trim()) ? null : 'Yalnızca program adı yazın (ör. msedge.exe); " % ve yol kullanılamaz.' });
    if (exe === null) return;
    const reason = await POps.prompt({ title: 'Gerekçe', label: 'Gerekçe (denetim kaydına yazılır)', required: true, maxLength: 300, confirmText: 'Kapat' });
    if (reason === null) return;
    appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] Uygulama kapatılıyor: ${escapeHtml(exe.trim())} · ${escapeHtml(target.label)} · Gerekçe: ${escapeHtml(reason)}</div>`);
    await sendTasks(target, [{ name: 'Uygulamayı kapat: ' + exe.trim(), type: 'CMD', command: `taskkill /F /IM "${exe.trim()}"` }], btn);
};

function renderTerminal() {
    const select = document.getElementById('terminalDeviceSelect');
    const term = (document.getElementById('terminalDeviceSearch').value || '').toLowerCase();
    const list = state.devices.filter(d => [POps.deviceName(d), d.hostname, d.lab, d.ip].join(' ').toLowerCase().includes(term))
        .sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
    const current = select.value;
    select.replaceChildren(POps.el('option', { value: '', text: `Bilgisayar seçin (${list.length})` }),
        ...list.map(d => POps.el('option', { value: d.hostname, text: `${POps.deviceName(d)} · ${d.lab === 'Atanmamis_Cihazlar' ? 'Atanmamış' : (d.lab || '-')}${POps.isOffline(d) ? ' · çevrimdışı' : ''}` })));
    if (current && list.some(d => d.hostname === current)) select.value = current;

    const labArea = document.getElementById('areaLab');
    const labs = [...new Set(state.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar').sort((a, b) => a.localeCompare(b, 'tr'));
    labArea.replaceChildren(...(labs.length ? labs.map(lab => {
        const b = POps.el('button', { type: 'button', className: 'lab-btn' + (selectedLab === lab ? ' active' : ''), 'aria-pressed': selectedLab === lab ? 'true' : 'false' },
            [POps.icon('fa-users'), document.createTextNode(` ${lab} (${state.devices.filter(d => d.lab === lab).length})`)]);
        b.addEventListener('click', () => { selectedLab = selectedLab === lab ? null : lab; renderTerminal(); });
        return b;
    }) : [POps.el('span', { className: 'text-sm text-muted', text: 'Henüz sınıf yok.' })]));

    const d = selectedDevice();
    const prefix = document.getElementById('cmdPrefix');
    const wake = document.getElementById('wakeUpText');
    if (currentMode === 'single') {
        prefix.textContent = d ? `${d.name}:\\>` : 'POps:\\>';
        wake.textContent = d ? `Uyandır (${d.name})` : 'Uyandır';
    } else {
        prefix.textContent = selectedLab ? `${selectedLab}:\\>` : 'Sınıf seçin:\\>';
        wake.textContent = selectedLab ? `Sınıfı uyandır (${selectedLab})` : 'Uyandır';
    }
    const auto = document.getElementById('btnQuickAutoRename');
    if (auto) auto.style.display = currentMode === 'lab' ? 'inline-flex' : 'none';
    const single = document.getElementById('btnQuickSingleRename');
    if (single) single.style.display = currentMode === 'single' ? 'inline-flex' : 'none';
}

function terminalHeaderHtml() {
    return `<div class="header">
        <div class="title">POps komut satırı</div>
        <div>Yönetici: ${escapeHtml(TERMINAL_ADMIN)} · Komutlar hedefte SYSTEM hesabıyla, cmd.exe ile çalışır.</div>
    </div>
    <div class="tip">[İpucu] Rutin işler için yukarıdaki hızlı komutları kullanabilirsiniz. Ekranı temizlemek için: cls</div>`;
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

    const btnSingle = document.getElementById('btnModeSingle');
    const btnLab = document.getElementById('btnModeLab');
    const termInput = document.getElementById('terminalCommand');
    const setMode = (mode) => {
        currentMode = mode;
        if (mode === 'single') selectedLab = null;
        btnSingle.classList.toggle('active', mode === 'single');
        btnLab.classList.toggle('active', mode === 'lab');
        document.getElementById('areaSingle').style.display = mode === 'single' ? 'flex' : 'none';
        document.getElementById('areaLab').style.display = mode === 'lab' ? 'flex' : 'none';
        renderTerminal();
    };
    btnSingle.addEventListener('click', () => setMode('single'));
    btnLab.addEventListener('click', () => setMode('lab'));
    document.getElementById('terminalDeviceSearch').addEventListener('input', renderTerminal);
    document.getElementById('terminalDeviceSelect').addEventListener('change', renderTerminal);

    document.getElementById('copyTerminalBtn').addEventListener('click', async () => {
        const ok = await copyText(document.getElementById('popsTerminalScreen').innerText);
        POps.toast(ok ? 'success' : 'error', ok ? 'Terminal çıktısı kopyalandı.' : 'Kopyalanamadı; metni seçip elle kopyalayın.');
    });
    document.getElementById('clearTerminalBtn').addEventListener('click', () => { clearTerminal(); termInput.focus(); });

    document.getElementById('wakeUpBtn').addEventListener('click', (e) => {
        const btn = e.currentTarget;
        if (currentMode === 'single') {
            const d = selectedDevice();
            if (!d) { POps.toast('warning', 'Uyandırmak için bir bilgisayar seçin.'); return; }
            window.wakeUpCommand('PC', d.id, btn);
        } else {
            if (!selectedLab) { POps.toast('warning', 'Uyandırmak için bir sınıf seçin.'); return; }
            window.wakeUpCommand('LAB', selectedLab, btn);
        }
    });

    document.querySelectorAll('[data-quick]').forEach(b => b.addEventListener('click', () => window.runQuickAction(b.dataset.quick, b)));
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
        appendToTerminal(`<div style="margin-top:1rem;margin-bottom:0.5rem;"><span class="warn">${escapeHtml(target.label)}:\\&gt;</span> <span class="cmd-block">${escapeHtml(command)}</span></div>`);
        await sendTasks(target, [{ name: 'Terminal', type: 'CMD', command }], null);
        termInput.disabled = false;
        termInput.focus();
    });
    renderTerminal();
});
</script>

<?php include 'includes/footer.php'; ?>