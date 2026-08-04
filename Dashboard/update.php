<?php include 'includes/header.php'; ?>

<style>
    .update-wrapper { display: flex; justify-content: center; padding: var(--space-5); }
    .update-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); width: 100%; max-width: 880px; padding: var(--space-8); box-shadow: var(--shadow-sm); }

    .section-title { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin-bottom: var(--space-4); margin-top: var(--space-6); display: flex; align-items: center; gap: 0.625rem; padding-bottom: 0.75rem; border-bottom: 1px solid var(--border-subtle); }
    .section-title:first-child { margin-top: 0; }
    .section-title i { color: var(--primary-500); }

    .option-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: var(--space-3); margin-bottom: var(--space-4); }
    .option-card { background: var(--bg-surface-2); border: 2px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-5); cursor: pointer; transition: all 0.15s; display: flex; flex-direction: column; align-items: center; gap: var(--space-3); text-align: center; }
    .option-card:hover { border-color: var(--primary-500); background: var(--bg-surface); transform: translateY(-1px); }
    .option-card.active { border-color: var(--primary-500); background: var(--primary-50); box-shadow: var(--shadow-sm); }
    .option-card.disabled { opacity: 0.5; pointer-events: none; }
    .option-card input { display: none; }
    .option-icon { font-size: 1.75rem; color: var(--text-tertiary); transition: color 0.15s; }
    .option-card.active .option-icon { color: var(--primary-500); }
    .option-title { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); }
    .option-desc { font-size: var(--text-xs); color: var(--text-tertiary); }

    .upload-zone { border: 2px dashed var(--border-default); border-radius: var(--radius-md); padding: 2.5rem 1.25rem; text-align: center; cursor: pointer; position: relative; transition: all 0.15s; background: var(--bg-surface-2); margin-top: 0.5rem; }
    .upload-zone:hover, .upload-zone.dragover { border-color: var(--primary-500); background: var(--primary-50); }
    .upload-zone i { font-size: 2.5rem; color: var(--text-tertiary); margin-bottom: 0.75rem; transition: color 0.15s; }
    .upload-zone:hover i { color: var(--primary-500); }
    .upload-zone h3 { color: var(--text-primary); margin-bottom: 0.25rem; font-size: var(--text-md); }
    .upload-zone input[type="file"] { position: absolute; inset: 0; opacity: 0; cursor: pointer; }

    #file-name-display { display: none; margin-top: 0.75rem; font-weight: var(--fw-semibold); color: var(--success-text); font-size: var(--text-md); background: var(--success-bg); padding: 0.5rem 0.75rem; border-radius: var(--radius-md); border: 1px solid var(--success-border); }

    .target-selectors { display: flex; gap: 1rem; margin-top: 0.75rem; background: var(--bg-surface-2); padding: var(--space-4); border-radius: var(--radius-md); border: 1px solid var(--border-subtle); }
    .select-group { flex: 1; display: flex; flex-direction: column; gap: 0.5rem; }
    .select-group label { color: var(--text-tertiary); font-size: 0.75rem; font-weight: var(--fw-semibold); text-transform: uppercase; letter-spacing: 0.05em; }
    .custom-select { width: 100%; padding: 0.625rem 0.875rem; font-size: var(--text-sm); }

    .btn-deploy { width: 100%; padding: 0.875rem; background: var(--primary-500); color: white; border: none; border-radius: var(--radius-md); font-size: var(--text-md); font-weight: var(--fw-semibold); cursor: pointer; transition: all 0.15s; display: flex; justify-content: center; align-items: center; gap: 0.625rem; margin-top: var(--space-6); letter-spacing: 0.05em; text-transform: uppercase; }
    .btn-deploy:hover { background: var(--primary-600); transform: translateY(-1px); box-shadow: var(--shadow-md); }
    .btn-deploy:disabled { background: var(--bg-surface-2); color: var(--text-muted); border: 1px solid var(--border-subtle); cursor: not-allowed; transform: none; box-shadow: none; }

    .status-msg { margin-top: 1rem; padding: 0.75rem 1rem; border-radius: var(--radius-md); text-align: center; display: none; font-weight: var(--fw-semibold); font-size: var(--text-md); border-left: 4px solid transparent; }
    .status-success { background: var(--success-bg); color: var(--success-text); border-color: var(--success-solid); }
    .status-error { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-solid); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-shuttle-space"></i> Ajan Güncelleme Merkezi</h1>
        <p>Ajan yazılımlarını ağ üzerinde güvenle güncelleyin veya test edin</p>
    </div>
</div>

<div class="update-wrapper">
    <div class="update-card">
        <div class="section-title"><i class="fas fa-box-open"></i> 1. Güncelleme Paketini Seçin</div>
        <div class="option-grid">
            <label class="option-card active" id="card-pkg-new" onclick="UI.selectPackage('new')">
                <input type="radio" name="pkgSource" value="new" checked>
                <i class="fas fa-cloud-arrow-up option-icon"></i>
                <div><div class="option-title">Yeni Paket Yükle</div><div class="option-desc">Bilgisayarınızdan .zip seçin</div></div>
            </label>
            <label class="option-card disabled" id="card-pkg-exist" onclick="UI.selectPackage('exist')">
                <input type="radio" name="pkgSource" value="exist">
                <i class="fas fa-server option-icon"></i>
                <div><div class="option-title">Son Paketi Kullan</div><div class="option-desc" id="latestPkgInfo"><i class="fas fa-arrows-rotate fa-spin"></i> Aranıyor...</div></div>
            </label>
        </div>

        <div class="deploy-guide" style="background:var(--info-bg, rgba(59,130,246,0.1)); border-left:3px solid var(--info-solid, #3b82f6); padding:0.75rem; border-radius:var(--radius-sm); margin-bottom:1rem;">
            <strong style="color:var(--info-text, #2563eb); display:block; margin-bottom:0.5rem;">
                <i class="fas fa-circle-info"></i> ZIP Paketi İçerik Kuralları
            </strong>
            <ul style="margin:0; padding-left:1.25rem; font-size:var(--text-sm); color:var(--text-secondary); line-height:1.5;">
                <li>ZIP dosyasının <strong>ana dizininde</strong> (alt klasör içinde değil) güncel <code>POpsAgent.exe</code> ve <code>POpsUpdater.exe</code> dosyaları mutlaka bulunmalıdır.</li>
                <li>Güncelleme başladığında hedef cihaz önce kendini kapatır, <code>POpsUpdater.exe</code> devreye girerek yeni <code>POpsAgent.exe</code> dosyasını mevcut olanın üzerine yazar ve ajanı tekrar başlatır.</li>
                <li>Eğer ajanla birlikte taşınması gereken ek dosyalar (örn. DLL'ler, config'ler) varsa, onları da bu ZIP'in ana dizinine ekleyebilirsiniz. Sistem ZIP içindeki tüm dosyaları ajanın kök dizinine çıkartacaktır.</li>
            </ul>
        </div>

        <div class="upload-zone" id="uploadZone">
            <i class="fas fa-file-zipper"></i>
            <h3>Güncelleme Paketi (.zip) Seçin</h3>
            <p style="color:var(--text-tertiary);font-size:var(--text-sm);margin-top:0.25rem;">Hazırladığınız .zip dosyasını buraya sürükleyin veya tıklayarak seçin.</p>
            <input type="file" id="updateFile" accept=".zip" onchange="UI.handleFile()">
            <div id="file-name-display"></div>
        </div>

        <div class="section-title"><i class="fas fa-crosshairs"></i> 2. Hedef Kitleyi Belirleyin</div>
        <div class="option-grid">
            <label class="option-card" id="card-tgt-all" onclick="UI.selectTarget('all')">
                <input type="radio" name="targetMode" value="all">
                <i class="fas fa-globe option-icon"></i>
                <div><div class="option-title">Tüm Ağ (Broadcast)</div><div class="option-desc">Online tüm cihazlara fırlat</div></div>
            </label>
            <label class="option-card" id="card-tgt-lab" onclick="UI.selectTarget('lab')">
                <input type="radio" name="targetMode" value="lab">
                <i class="fas fa-network-wired option-icon"></i>
                <div><div class="option-title">Belirli Bir Sınıf</div><div class="option-desc">Sadece seçili laboratuvar</div></div>
            </label>
            <label class="option-card active" id="card-tgt-single" onclick="UI.selectTarget('single')">
                <input type="radio" name="targetMode" value="single" checked>
                <i class="fas fa-laptop option-icon"></i>
                <div><div class="option-title">Tekil Test Cihazı</div><div class="option-desc">Güvenli test için tek PC</div></div>
            </label>
        </div>

        <div class="target-selectors" id="targetSelectors" style="display:flex;">
            <div class="select-group">
                <label>Laboratuvar Seçimi</label>
                <select id="labSelect" class="custom-select" onchange="UI.onLabChange()">
                    <option value="">— Önce Sınıf Seçin —</option>
                </select>
            </div>
            <div class="select-group" id="pcSelectGroup">
                <label>Cihaz Seçimi (A-Z Sıralı)</label>
                <select id="pcSelect" class="custom-select" onchange="UI.validateForm()">
                    <option value="">— Sınıf Seçimi Bekleniyor —</option>
                </select>
            </div>
        </div>

        <button class="btn-deploy" id="btnUpdate" onclick="Deployment.start()" disabled>
            <i class="fas fa-rocket"></i> Ajanları Güncelle
        </button>

        <div id="statusAlert" class="status-msg"></div>
    </div>
</div>

<script>
function getApiBase() { return (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : ''; }
let globalDevices = [];
let latestUpdateUrl = null;

function extractNumber(name) {
    const m = (name || '').match(/\d+$/);
    return m ? parseInt(m[0], 10) : 9999;
}

function escapeHtml(s) { return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

const UI = {
    init: async function() {
        this.bindDragDrop();
        await this.fetchLatestPackage();
        await this.fetchDevices();
        this.selectPackage('new');
        this.selectTarget('single');
    },
    bindDragDrop: function() {
        const zone = document.getElementById('uploadZone');
        zone.addEventListener('dragover', (e) => { e.preventDefault(); zone.classList.add('dragover'); });
        zone.addEventListener('dragleave', () => zone.classList.remove('dragover'));
        zone.addEventListener('drop', (e) => { e.preventDefault(); zone.classList.remove('dragover'); });
    },
    fetchLatestPackage: async function() {
        if (!getApiBase()) return;
        try {
            const res = await fetch(`${getApiBase()}/api/latest_update`);
            const data = await res.json();
            const card = document.getElementById('card-pkg-exist');
            const info = document.getElementById('latestPkgInfo');
            if (data && data.download_url) {
                latestUpdateUrl = data.download_url;
                const filename = latestUpdateUrl.split('/').pop();
                info.innerHTML = `<span style="color:var(--success-text);"><i class="fas fa-check-circle"></i> ${escapeHtml(filename)}</span>`;
                card.classList.remove('disabled');
            } else {
                info.innerHTML = `<span style="color:var(--danger-text);"><i class="fas fa-xmark-circle"></i> Sunucuda paket yok</span>`;
                card.classList.add('disabled');
            }
        } catch (e) { document.getElementById('latestPkgInfo').innerText = 'Bağlantı hatası'; }
    },
    fetchDevices: async function() {
        if (!getApiBase()) return;
        try {
            const res = await fetch(`${getApiBase()}/api/devices`);
            globalDevices = await res.json();
            const online = globalDevices.filter(d => (d.status || '').toLowerCase() === 'online');
            const labs = [...new Set(online.map(d => d.lab))].filter(l => l && l !== 'Atanmamis_Cihazlar').sort();
            const labSel = document.getElementById('labSelect');
            labSel.innerHTML = '<option value="">— Sınıf Seçin —</option>';
            labs.forEach(lab => { labSel.innerHTML += `<option value="${escapeHtml(lab)}">${escapeHtml(lab)} (${online.filter(d => d.lab === lab).length})</option>`; });
        } catch (e) { document.getElementById('labSelect').innerHTML = '<option value="">Ağ Bağlantı Hatası</option>'; }
    },
    selectPackage: function(mode) {
        if (mode === 'exist' && !latestUpdateUrl) return;
        document.getElementById('card-pkg-new').classList.toggle('active', mode === 'new');
        document.getElementById('card-pkg-exist').classList.toggle('active', mode === 'exist');
        document.querySelector(`input[name="pkgSource"][value="${mode}"]`).checked = true;
        document.getElementById('uploadZone').style.display = mode === 'new' ? 'block' : 'none';
        this.validateForm();
    },
    selectTarget: function(mode) {
        document.getElementById('card-tgt-all').classList.toggle('active', mode === 'all');
        document.getElementById('card-tgt-lab').classList.toggle('active', mode === 'lab');
        document.getElementById('card-tgt-single').classList.toggle('active', mode === 'single');
        document.querySelector(`input[name="targetMode"][value="${mode}"]`).checked = true;
        const area = document.getElementById('targetSelectors');
        const pcGroup = document.getElementById('pcSelectGroup');
        if (mode === 'all') area.style.display = 'none';
        else if (mode === 'lab') { area.style.display = 'flex'; pcGroup.style.display = 'none'; }
        else { area.style.display = 'flex'; pcGroup.style.display = 'flex'; }
        this.validateForm();
    },
    onLabChange: function() {
        const lab = document.getElementById('labSelect').value;
        const pcSel = document.getElementById('pcSelect');
        if (!lab) { pcSel.innerHTML = '<option value="">— Sınıf Seçimi Bekleniyor —</option>'; this.validateForm(); return; }
        let pcs = globalDevices.filter(d => d.lab === lab && (d.status || '').toLowerCase() === 'online');
        pcs.sort((a, b) => extractNumber(a.display_name || a.real_hostname || a.hostname) - extractNumber(b.display_name || b.real_hostname || b.hostname));
        pcSel.innerHTML = '<option value="">— Cihaz Seçin —</option>';
        pcs.forEach(pc => { pcSel.innerHTML += `<option value="${pc.hostname}">${escapeHtml(pc.display_name || pc.real_hostname || pc.hostname)}</option>`; });
        this.validateForm();
    },
    handleFile: function() {
        const file = document.getElementById('updateFile').files[0];
        const display = document.getElementById('file-name-display');
        if (file) {
            if (!file.name.toLowerCase().endsWith('.zip')) {
                this.showStatus('Hata: Sadece .zip kabul edilir!', 'error');
                document.getElementById('updateFile').value = '';
                display.style.display = 'none';
            } else {
                display.innerHTML = `<i class="fas fa-check-circle"></i> Yüklemeye Hazır: ${escapeHtml(file.name)} (${(file.size / 1024 / 1024).toFixed(2)} MB)`;
                display.style.display = 'block';
                document.getElementById('statusAlert').style.display = 'none';
            }
        }
        this.validateForm();
    },
    validateForm: function() {
        const pkg = document.querySelector('input[name="pkgSource"]:checked').value;
        const tgt = document.querySelector('input[name="targetMode"]:checked').value;
        const file = document.getElementById('updateFile').files[0];
        const lab = document.getElementById('labSelect').value;
        const pc = document.getElementById('pcSelect').value;
        let ok = true;
        if (pkg === 'new' && !file) ok = false;
        if (pkg === 'exist' && !latestUpdateUrl) ok = false;
        if (tgt === 'lab' && !lab) ok = false;
        if (tgt === 'single' && (!lab || !pc)) ok = false;
        document.getElementById('btnUpdate').disabled = !ok;
    },
    showStatus: function(msg, type) {
        const box = document.getElementById('statusAlert');
        box.innerHTML = msg;
        box.className = `status-msg status-${type}`;
        box.style.display = 'block';
    }
};

const Deployment = {
    start: async function() {
        if (!getApiBase()) return;
        const btn = document.getElementById('btnUpdate');
        const pkg = document.querySelector('input[name="pkgSource"]:checked').value;
        const tgt = document.querySelector('input[name="targetMode"]:checked').value;
        btn.disabled = true;
        try {
            let downloadUrl = latestUpdateUrl;
            if (pkg === 'new') {
                btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Paket Yükleniyor...';
                const fd = new FormData(); fd.append('file', document.getElementById('updateFile').files[0]);
                const res = await fetch(`${getApiBase()}/api/upload_update`, { method: 'POST', body: fd });
                if (!res.ok) throw new Error('Yükleme başarısız.');
                const data = await res.json();
                if (data.status === 'error') throw new Error(data.message);
                downloadUrl = OMYO_API.UPDATE_URL + '/' + data.filename;
                latestUpdateUrl = downloadUrl;
            }
            btn.innerHTML = '<i class="fas fa-paper-plane"></i> Emir Gönderiliyor...';
            let successMessage = '';
            if (tgt === 'all') {
                const bRes = await fetch(`${getApiBase()}/api/broadcast_update`);
                if (!bRes.ok) throw new Error('Broadcast başarısız.');
                successMessage = 'Tüm aktif cihazlara güncelleme emri gönderildi!';
            } else if (tgt === 'single') {
                const target = document.getElementById('pcSelect').value;
                const sRes = await fetch(`${getApiBase()}/api/update_agent/${target}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ download_url: downloadUrl }) });
                if (!sRes.ok) throw new Error('Cihaza erişilemedi.');
                successMessage = `<strong>${escapeHtml(target)}</strong> cihazına test emri gönderildi.`;
            } else if (tgt === 'lab') {
                const targetLab = document.getElementById('labSelect').value;
                const pcs = globalDevices.filter(d => d.lab === targetLab && (d.status || '').toLowerCase() === 'online');
                if (pcs.length === 0) throw new Error('Sınıfta açık cihaz yok!');
                btn.innerHTML = `<i class="fas fa-spinner fa-spin"></i> ${pcs.length} Cihaza İletiliyor...`;
                await Promise.all(pcs.map(pc => fetch(`${getApiBase()}/api/update_agent/${pc.hostname}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ download_url: downloadUrl }) }).catch(() => {})));
                successMessage = `<strong>${escapeHtml(targetLab)}</strong> sınıfındaki ${pcs.length} cihaza güncelleme ateşlendi.`;
            }
            btn.innerHTML = '<i class="fas fa-check-double"></i> Dağıtım Başarılı';
            UI.showStatus(successMessage, 'success');
            setTimeout(() => {
                document.getElementById('updateFile').value = '';
                document.getElementById('file-name-display').style.display = 'none';
                UI.fetchLatestPackage();
                UI.selectPackage('exist');
                btn.innerHTML = '<i class="fas fa-rocket"></i> Ajanları Güncelle';
                UI.validateForm();
            }, 5000);
        } catch (error) {
            UI.showStatus(`Hata: ${error.message}`, 'error');
            btn.disabled = false;
            btn.innerHTML = '<i class="fas fa-rocket"></i> Ajanları Güncelle';
        }
    }
};

document.addEventListener('DOMContentLoaded', () => UI.init());
</script>

<?php include 'includes/footer.php'; ?>