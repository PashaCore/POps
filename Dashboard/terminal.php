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
                <button id="wakeUpBtn" class="btn success"><i class="fas fa-bolt"></i> <span id="wakeUpText">Uyandır (WOL)</span></button>
                <button id="copyTerminalBtn" class="btn secondary"><i class="fas fa-copy"></i> Kopyala</button>
                <button id="clearTerminalBtn" class="btn secondary"><i class="fas fa-eraser"></i> Temizle</button>
            </div>
        </div>
        <div class="target-area">
            <div id="areaSingle" class="target-area" style="flex:1;">
                <div class="search-input-wrap">
                    <i class="fas fa-filter"></i>
                    <input type="text" id="terminalDeviceSearch" placeholder="Hostname veya IP Ara...">
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
            <button class="lab-btn" onclick="window.sendQuickAction('ipconfig /flushdns', 'DNS Temizle')"><i class="fas fa-globe"></i> DNS Temizle</button>
            <button class="lab-btn" onclick="window.sendQuickAction('ipconfig /release; ipconfig /renew', 'Ağı Yenile')"><i class="fas fa-network-wired"></i> Ağı Yenile</button>
            <button class="lab-btn" onclick="window.sendQuickAction('Stop-Service -Name Spooler -Force; Remove-Item -Path \'$env:windir\\System32\\spool\\PRINTERS\\*.*\' -Force -Recurse; Start-Service -Name Spooler', 'Yazıcı Kuyruğu Sıfırlandı')"><i class="fas fa-print"></i> Yazıcı Kuyruğu</button>
            <button class="lab-btn" onclick="window.sendQuickAction('Remove-Item -Path \'$env:TEMP\\*\' -Recurse -Force -ErrorAction SilentlyContinue', 'Temp Temizle')"><i class="fas fa-broom"></i> Temp Temizle</button>
            <button class="lab-btn" onclick="window.sendQuickAction('gpupdate /force', 'Grup İlkesi Güncellendi')"><i class="fas fa-shield-halved"></i> GPUpdate</button>
            <div style="width:1px; background:var(--border-subtle); margin:0 0.25rem;"></div>
            <button class="lab-btn" onclick="window.promptSingleRename()"><i class="fas fa-tag"></i> Tekil İsimlendir</button>
            <button class="lab-btn" id="btnQuickAutoRename" style="display:none;" onclick="window.promptAutoRename()"><i class="fas fa-tags"></i> Toplu İsimlendir</button>
            <button class="lab-btn" onclick="window.promptTaskkill()"><i class="fas fa-skull"></i> Görev Sonlandır</button>
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
window.state = window.state || { devices: [], terminalHistory: [] };
let currentMode = 'single';
let selectedLab = null;
const processedResponses = new Set();

let panelWs = null;
function initTerminalWebSocket() {
    if (typeof OMYO_API === 'undefined') return;
    try {
        panelWs = new WebSocket(OMYO_API.WS_URL + '/panel');
        panelWs.onmessage = (event) => {
            try {
                const payload = JSON.parse(event.data);
                if (payload.type === 'terminal_output') {
                    const pcName = payload.pc_name || payload.id || 'Bilinmeyen PC';
                    const outputText = payload.output || 'Çıktı alınamadı.';
                    const taskId = payload.task_id || '0';
                    const hwId = payload.id || 'HW-UNKNOWN';
                    const uniqueId = `${taskId}_${hwId}`;
                    if (processedResponses.has(uniqueId)) return;
                    processedResponses.add(uniqueId);
                    appendToTerminal(`<div class="cmd-output"><div class="pc">[${escapeHtml(pcName)}]</div><pre style="margin:0;color:#e2e8f0;font-family:var(--font-mono);font-size:0.8125rem;white-space:pre-wrap;word-wrap:break-word;">${escapeHtml((outputText || '').trim())}</pre></div>`);
                }
            } catch (e) { console.error('WS parse error', e); }
        };
    } catch(e) {}
}

window.sendQuickAction = function(cmd, actionName) {
    const reason = prompt(`'${actionName}' işlemi için bir neden belirtin (Zorunlu):`);
    if (!reason || reason.trim() === '') return showToast('Neden belirtmek zorunludur!', 'error');

    if (currentMode === 'single') {
        const sel = document.getElementById('terminalDeviceSelect').value;
        if (!sel) return showToast('Lütfen bir cihaz seçin.', 'error');
        appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] Hızlı İşlem: ${actionName} (Tekil) - Neden: ${escapeHtml(reason)}</div>`);
        apiRequest('/api/deploy_orchestration', { method: 'POST', body: JSON.stringify({ target_mode: 'PC', targets: [sel], taskSequence: [{ name: actionName, type: 'CMD', command: `powershell -Command "${cmd}"` }], reason: reason.trim() }) });
    } else {
        if (!selectedLab) return showToast('Lütfen bir laboratuvar seçin.', 'error');
        appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] Hızlı İşlem: ${actionName} (Lab: ${selectedLab}) - Neden: ${escapeHtml(reason)}</div>`);
        apiRequest('/api/deploy_orchestration', { method: 'POST', body: JSON.stringify({ target_mode: 'LAB', targets: [selectedLab], taskSequence: [{ name: actionName, type: 'CMD', command: `powershell -Command "${cmd}"` }], reason: reason.trim() }) });
    }
};

window.promptSingleRename = function() {
    if (currentMode !== 'single') return showToast('Tekil modda olmalısınız.', 'warning');
    const newName = prompt("Yeni PC ismini girin (Örn: LAB1_PC05):");
    if (newName) {
        document.getElementById('terminalCommand').value = `/setname ${newName}`;
        document.getElementById('terminalCommand').dispatchEvent(new KeyboardEvent('keypress', { key: 'Enter' }));
    }
};

window.promptAutoRename = function() {
    if (currentMode !== 'lab') return showToast('Toplu (Lab) modunda olmalısınız.', 'warning');
    const baseName = prompt("Sınıf önekini girin (Örn: LAB1_PC):");
    if (baseName) {
        document.getElementById('terminalCommand').value = `/otorename ${baseName}`;
        document.getElementById('terminalCommand').dispatchEvent(new KeyboardEvent('keypress', { key: 'Enter' }));
    }
};

window.promptTaskkill = function() {
    const exeName = prompt("Kapatılacak uygulamanın tam adını girin (Örn: msedge.exe):");
    if (exeName) {
        window.sendQuickAction(`taskkill /F /IM ${exeName}`, `Görev Sonlandır: ${exeName}`);
    }
};

function renderTerminal() {
    const select = document.getElementById('terminalDeviceSelect');
    const search = document.getElementById('terminalDeviceSearch');
    const labArea = document.getElementById('areaLab');
    const searchTerm = (search?.value || '').toLowerCase();
    const filtered = state.devices.filter(d => (d.hostname || '').toLowerCase().includes(searchTerm) || (d.lab || '').toLowerCase().includes(searchTerm));
    const currentVal = select.value;
    select.innerHTML = `<option value="">Hedef Cihaz Seçin (${filtered.length})</option>`;
    filtered.forEach(d => {
        const opt = document.createElement('option');
        opt.value = d.hostname;
        opt.dataset.lab = d.lab;
        opt.textContent = `${d.display_name || d.real_hostname || d.hostname} (${d.lab})`;
        select.appendChild(opt);
    });
    if (currentVal) select.value = currentVal;

    const labs = [...new Set(state.devices.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar');
    labArea.innerHTML = '';
    labs.forEach(lab => {
        const count = state.devices.filter(d => d.lab === lab).length;
        const btn = document.createElement('button');
        btn.className = `lab-btn ${selectedLab === lab ? 'active' : ''}`;
        btn.innerHTML = `<i class="fas fa-users"></i> ${escapeHtml(lab)} (${count})`;
        btn.onclick = () => { selectedLab = (selectedLab === lab) ? null : lab; renderTerminal(); };
        labArea.appendChild(btn);
    });

    const prefix = document.getElementById('cmdPrefix');
    const wakeBtn = document.getElementById('wakeUpText');
    if (currentMode === 'single') {
        const txt = select.options[select.selectedIndex]?.text.split(' ')[0] || select.value;
        prefix.innerText = select.value ? `${txt}:\\>` : 'POps:\\>';
        wakeBtn.innerText = select.value ? `Uyandır (${txt})` : 'Uyandır (Seçim Yok)';
    } else {
        prefix.innerText = selectedLab ? `LAB-${selectedLab}:\\>` : 'Lab-Secilmedi:\\>';
        wakeBtn.innerText = selectedLab ? `Lab'ı Uyandır (${selectedLab})` : 'Uyandır (Seçim Yok)';
    }

    const out = document.getElementById('popsTerminalScreen');
    if (out) out.innerHTML = initTerminalHeader() + state.terminalHistory.join('');
    const c = document.getElementById('terminalContainer');
    if (c) c.scrollTop = c.scrollHeight;
    
    const btnQuickAuto = document.getElementById('btnQuickAutoRename');
    if (btnQuickAuto) btnQuickAuto.style.display = (currentMode === 'lab') ? 'inline-flex' : 'none';
}

function initTerminalHeader() {
    return `<div class="header">
        <div class="title">POps Command Line Interface [v4.0.0]</div>
        <div>Yönetici: <?php echo htmlspecialchars($_SESSION['username'] ?? 'Admin'); ?> — Güvenli Bağlantı Aktif</div>
        <div>(c) POps Bilişim Sistemleri. Tüm Hakları Saklıdır.</div>
    </div>
    <div class="tip">[İPUCU] Aşağıdaki Hızlı İşlem butonlarını kullanarak rutin operasyonları anında gerçekleştirebilirsiniz.</div>`;
}

function appendToTerminal(html) {
    const out = document.getElementById('popsTerminalScreen');
    const c = document.getElementById('terminalContainer');
    state.terminalHistory.push(html);
    if (out) { out.insertAdjacentHTML('beforeend', html); c.scrollTop = c.scrollHeight; }
}

function escapeHtml(s) { return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

document.addEventListener('DOMContentLoaded', () => {
    initTerminalWebSocket();
    apiRequest('/api/devices').then(data => { if (data) { state.devices = data; renderTerminal(); } }).catch(() => {});

    if (state.terminalHistory.length === 0) document.getElementById('popsTerminalScreen').innerHTML = initTerminalHeader();

    const btnSingle = document.getElementById('btnModeSingle');
    const btnLab = document.getElementById('btnModeLab');
    const areaSingle = document.getElementById('areaSingle');
    const areaLab = document.getElementById('areaLab');
    const termInput = document.getElementById('terminalCommand');
    const wakeUpBtn = document.getElementById('wakeUpBtn');
    const copyBtn = document.getElementById('copyTerminalBtn');

    copyBtn.onclick = () => {
        const txt = document.getElementById('popsTerminalScreen').innerText;
        navigator.clipboard.writeText(txt).then(() => showToast('Terminal kopyalandı.', 'success')).catch(() => showToast('Kopyalama başarısız.', 'error'));
    };

    btnSingle.onclick = () => { currentMode = 'single'; selectedLab = null; btnSingle.classList.add('active'); btnLab.classList.remove('active'); areaSingle.style.display = 'flex'; areaLab.style.display = 'none'; renderTerminal(); };
    btnLab.onclick = () => { currentMode = 'lab'; btnLab.classList.add('active'); btnSingle.classList.remove('active'); areaSingle.style.display = 'none'; areaLab.style.display = 'flex'; renderTerminal(); };
    document.getElementById('terminalDeviceSearch').oninput = renderTerminal;
    document.getElementById('terminalDeviceSelect').onchange = renderTerminal;

    wakeUpBtn.onclick = async () => {
        if (currentMode === 'lab' && selectedLab) {
            appendToTerminal('<div class="warn" style="margin:0.75rem 0;">[*] WOL Gönderiliyor: LAB-' + escapeHtml(selectedLab) + '...</div>');
            try {
                const res = await apiRequest(`/api/wake_lab/${encodeURIComponent(selectedLab)}`, { method: 'POST' });
                appendToTerminal(`<div style="color:#4ade80;margin-bottom:0.75rem;">[+] ${res.woken_pcs} cihaza uyandırma sinyali gönderildi.</div>`);
            } catch (e) { appendToTerminal('<div class="err" style="margin-bottom:0.75rem;">[-] Hata: Sinyal gönderilemedi.</div>'); }
        } else { showToast('Toplu uyandırma için lab seçin.', 'warning'); }
    };

    if (termInput) {
        document.getElementById('terminalContainer').onclick = () => termInput.focus();
        termInput.addEventListener('keypress', async (e) => {
            if (e.key !== 'Enter') return;
            const command = termInput.value.trim();
            let targetMode = currentMode === 'single' ? 'PC' : 'LAB';
            let deployTargets = [];
            let prefixText = '';

            if (command.toLowerCase().startsWith('/setname ')) {
                if (currentMode !== 'single' || !document.getElementById('terminalDeviceSelect').value) return showToast('Tekil mod ve cihaz gerekli.', 'error');
                const newName = command.split(' ')[1];
                const targetHwId = document.getElementById('terminalDeviceSelect').value;
                const displayHost = document.getElementById('terminalDeviceSelect').options[document.getElementById('terminalDeviceSelect').selectedIndex].text.split(' ')[0];
                const psCmd = `powershell -Command "$newName='${newName}'; (Get-WmiObject Win32_ComputerSystem).Rename($newName); $oldUser=(Get-LocalUser | Where-Object {$_.Enabled -and $_.Name -notmatch 'Administrator|Guest|DefaultAccount|WDAGUtilityAccount|system'} | Select-Object -First 1).Name; if($oldUser){ Rename-LocalUser -Name $oldUser -NewName $newName; Set-LocalUser -Name $newName -FullName $newName; }; $i=1; Get-NetAdapter | Where-Object {$_.Name -notmatch 'Baglanti_'} | ForEach-Object { Rename-NetAdapter -Name $_.Name -NewName ('Baglanti_'+$i); $i++ }; Write-Output 'Isim ${newName} olarak degistirildi.'; shutdown /r /t 5"`;
                termInput.value = '';
                appendToTerminal(`<div class="cmd-block" style="margin-top:0.75rem;margin-bottom:0.5rem;"><span class="warn">[*]</span> ${escapeHtml(displayHost)} → '${escapeHtml(newName)}' atanıyor...</div>`);
                apiRequest('/api/deploy_orchestration', { method: 'POST', body: JSON.stringify({ target_mode: 'PC', targets: [targetHwId], taskSequence: [{ name: 'Rename Single', type: 'CMD', command: psCmd }] }) });
                return;
            }

            if (command.toLowerCase().startsWith('/otorename ')) {
                if (currentMode !== 'lab' || !selectedLab) return showToast('Lab modunda çalışır.', 'error');
                const args = command.split(' ');
                const baseName = args[1];
                const limitInput = args[2] ? parseInt(args[2]) : null;
                let labDevices = state.devices.filter(d => d.lab === selectedLab);
                labDevices = labDevices.filter(d => { const n = (d.real_hostname || '').toUpperCase(); return !(n.includes('PC00') || n.includes('ANA') || n.includes('OGR')); });
                labDevices.sort((a, b) => (a.display_name || a.real_hostname || a.hostname).localeCompare(b.display_name || b.real_hostname || b.hostname, undefined, { numeric: true, sensitivity: 'base' }));
                if (limitInput && limitInput > 0 && limitInput <= labDevices.length) labDevices = labDevices.slice(0, limitInput);
                termInput.value = '';
                appendToTerminal(`<div class="cmd-block warn" style="margin-top:0.75rem;margin-bottom:0.5rem;">[*] OTO-İSİMLENDİRME: ${labDevices.length} cihaz sırada</div>`);
                let counter = 1;
                for (let dev of labDevices) {
                    const targetHwId = dev.hostname;
                    const numStr = counter < 10 ? '0' + counter : counter.toString();
                    const newName = `${baseName}${numStr}`;
                    const psCmd = `powershell -Command "$newName='${newName}'; (Get-WmiObject Win32_ComputerSystem).Rename($newName); $oldUser=(Get-LocalUser | Where-Object {$_.Enabled -and $_.Name -notmatch 'Administrator|Guest|DefaultAccount|WDAGUtilityAccount|system'} | Select-Object -First 1).Name; if($oldUser){ Rename-LocalUser -Name $oldUser -NewName $newName; Set-LocalUser -Name $newName -FullName $newName; }; $i=1; Get-NetAdapter | Where-Object {$_.Name -notmatch 'Baglanti_'} | ForEach-Object { Rename-NetAdapter -Name $_.Name -NewName ('Baglanti_'+$i); $i++ }; shutdown /r /t 5"`;
                    apiRequest('/api/deploy_orchestration', { method: 'POST', body: JSON.stringify({ target_mode: 'PC', targets: [targetHwId], taskSequence: [{ name: 'Oto Rename', type: 'CMD', command: psCmd }] }) });
                    appendToTerminal(`<div style="color:#4ade80;margin-bottom:0.25rem;">→ ${escapeHtml(dev.real_hostname || dev.hostname)} → ${escapeHtml(newName)}</div>`);
                    counter++;
                }
                return;
            }

            if (currentMode === 'single') {
                const host = document.getElementById('terminalDeviceSelect').value;
                const displayHost = document.getElementById('terminalDeviceSelect').options[document.getElementById('terminalDeviceSelect').selectedIndex]?.text.split(' ')[0];
                if (!host) return showToast('Cihaz seçin.', 'error');
                deployTargets.push(host); prefixText = displayHost;
            } else {
                if (!selectedLab) return showToast('Lab seçin.', 'error');
                deployTargets.push(selectedLab); prefixText = `[LAB: ${selectedLab}]`;
            }
            if (!command) return;
            if (command.toLowerCase() === 'cls' || command.toLowerCase() === 'clear') {
                state.terminalHistory = []; processedResponses.clear();
                document.getElementById('popsTerminalScreen').innerHTML = initTerminalHeader();
                termInput.value = ''; return;
            }

            termInput.value = ''; termInput.disabled = true;
            try {
                appendToTerminal(`<div style="margin-top:1rem;margin-bottom:0.5rem;"><span class="warn">${escapeHtml(prefixText)}:\\&gt;</span> <span class="cmd-block">${escapeHtml(command)}</span></div>`);
                const payload = { target_mode: targetMode, targets: deployTargets, taskSequence: [{ name: 'Terminal', type: 'CMD', command }] };
                await apiRequest('/api/deploy_orchestration', { method: 'POST', body: JSON.stringify(payload) });
                appendToTerminal('<div class="info">[i] Komut hedefe fırlatıldı, çıktılar bekleniyor...</div>');
            } catch (err) { showToast('Sunucu hatası.', 'error'); appendToTerminal('<div class="err">[-] Hata: Komut iletilemedi.</div>'); }
            finally { termInput.disabled = false; termInput.focus(); }
        });
    }

    document.getElementById('clearTerminalBtn').onclick = () => {
        state.terminalHistory = []; processedResponses.clear();
        document.getElementById('popsTerminalScreen').innerHTML = initTerminalHeader();
        termInput.focus();
    };
});
</script>

<?php include 'includes/footer.php'; ?>