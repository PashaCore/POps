<?php include 'includes/header.php'; ?>

<style>
    .deploy-container { display: grid; grid-template-columns: 300px 1fr 320px; gap: var(--space-4); min-height: 720px; align-items: stretch; }
    .deploy-panel { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-5); display: flex; flex-direction: column; box-shadow: var(--shadow-xs); }
    .panel-header { font-size: var(--text-md); font-weight: var(--fw-semibold); margin-bottom: var(--space-4); padding-bottom: var(--space-3); border-bottom: 1px solid var(--border-subtle); display: flex; align-items: center; justify-content: space-between; color: var(--text-primary); }
    .repo-tabs { display: flex; margin-bottom: var(--space-3); background: var(--bg-surface-2); border-radius: var(--radius-md); padding: 0.25rem; }
    .repo-tab { flex: 1; padding: 0.4375rem; text-align: center; font-size: var(--text-xs); font-weight: var(--fw-semibold); cursor: pointer; border-radius: var(--radius-sm); color: var(--text-tertiary); transition: all 0.15s; }
    .repo-tab.active { background: var(--bg-surface); color: var(--primary-600); box-shadow: var(--shadow-xs); }
    .repo-list { display: flex; flex-direction: column; gap: 0.5rem; overflow-y: auto; flex: 1; padding-right: 0.25rem; }

    .repo-item { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); padding: 0.75rem; border-radius: var(--radius-md); cursor: grab; display: flex; align-items: center; gap: 0.625rem; transition: border-color 0.15s, transform 0.1s; }
    .repo-item:hover { border-color: var(--primary-500); }
    .repo-item.package { border-left: 3px solid var(--info-solid); }
    .repo-item.script { border-left: 3px solid var(--warning-solid); }
    .repo-item .icon { font-size: 1.125rem; color: var(--text-tertiary); width: 28px; text-align: center; }
    .repo-item .details { display: flex; flex-direction: column; flex: 1; overflow: hidden; }
    .repo-item .title { font-weight: var(--fw-semibold); font-size: var(--text-sm); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .repo-item .meta { font-size: 0.6875rem; color: var(--text-tertiary); margin-top: 0.125rem; }

    .workflow-zone { flex: 1; background: var(--bg-surface-2); border: 2px dashed var(--border-default); border-radius: var(--radius-md); padding: var(--space-4); overflow-y: auto; display: flex; flex-direction: column; gap: 0.5rem; position: relative; }
    .workflow-zone.drag-over { border-color: var(--primary-500); background: var(--primary-50); }
    .workflow-empty { position: absolute; top: 50%; left: 50%; transform: translate(-50%, -50%); text-align: center; color: var(--text-tertiary); pointer-events: none; }
    .workflow-empty i { font-size: 1.5rem; opacity: 0.4; display: block; margin-bottom: 0.5rem; }

    .task-node { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); display: flex; align-items: stretch; box-shadow: var(--shadow-xs); animation: slideUp 0.2s ease; }
    .task-step { width: 40px; display: flex; align-items: center; justify-content: center; font-weight: var(--fw-bold); background: var(--bg-surface-2); border-right: 1px solid var(--border-subtle); border-radius: var(--radius-md) 0 0 var(--radius-md); color: var(--text-tertiary); }
    .task-content { flex: 1; padding: 0.75rem 1rem; display: flex; align-items: center; justify-content: space-between; gap: 0.75rem; }
    .task-info { display: flex; align-items: center; gap: 0.75rem; min-width: 0; }
    .task-icon { font-size: 1.25rem; }
    .task-type-badge { font-size: 0.6875rem; padding: 0.125rem 0.5rem; border-radius: var(--radius-sm); text-transform: uppercase; font-weight: var(--fw-semibold); margin-bottom: 0.25rem; display: inline-block; }
    .task-type-badge.pkg { background: var(--info-bg); color: var(--info-text); }
    .task-type-badge.scr { background: var(--warning-bg); color: var(--warning-text); }
    .btn-remove-task { background: transparent; border: none; color: var(--text-tertiary); cursor: pointer; font-size: 1rem; padding: 0.25rem; }
    .btn-remove-task:hover { color: var(--danger-text); }
    .task-connector { width: 2px; height: 12px; background: var(--primary-500); margin: 0 auto; opacity: 0.5; }

    .target-section { display: flex; flex-direction: column; gap: 0.75rem; margin-bottom: var(--space-4); }
    .target-label { font-size: 0.75rem; font-weight: var(--fw-semibold); color: var(--text-tertiary); text-transform: uppercase; letter-spacing: 0.05em; }
    .target-mode-selector { display: flex; background: var(--bg-surface-2); border-radius: var(--radius-md); padding: 0.25rem; border: 1px solid var(--border-subtle); }
    .target-btn { flex: 1; padding: 0.4375rem 0.25rem; font-size: 0.75rem; border: none; background: transparent; color: var(--text-tertiary); cursor: pointer; border-radius: var(--radius-sm); font-weight: var(--fw-semibold); transition: all 0.15s; }
    .target-btn:hover { color: var(--text-primary); }
    .target-btn.active { background: var(--bg-surface); color: var(--primary-600); box-shadow: var(--shadow-xs); }

    .target-dynamic-area { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); display: flex; flex-direction: column; max-height: 220px; overflow: hidden; }
    .target-search { padding: 0.5rem 0.75rem; background: var(--bg-surface-2); border: none; border-bottom: 1px solid var(--border-subtle); font-size: var(--text-sm); }
    .target-list { overflow-y: auto; padding: 0.25rem; flex: 1; }
    .target-group-header { padding: 0.5rem 0.625rem; background: var(--primary-50); color: var(--primary-600); font-weight: var(--fw-semibold); font-size: 0.6875rem; text-transform: uppercase; border-radius: var(--radius-sm); margin: 0.25rem 0; border-left: 3px solid var(--primary-500); }
    .target-item { display: flex; align-items: center; gap: 0.5rem; padding: 0.4375rem 0.625rem; cursor: pointer; border-radius: var(--radius-sm); transition: background-color 0.15s; font-size: var(--text-sm); }
    .target-item:hover { background: var(--bg-surface-2); }

    .queue-limit-box { background: var(--warning-bg); border: 1px solid var(--warning-border); padding: 0.75rem; border-radius: var(--radius-md); display: flex; justify-content: space-between; align-items: center; }
    .queue-limit-box input { width: 60px; padding: 0.25rem; text-align: center; font-weight: var(--fw-semibold); background: var(--bg-surface); }
    .btn-massive { width: 100%; padding: 0.875rem; font-size: var(--text-md); font-weight: var(--fw-semibold); border-radius: var(--radius-md); display: flex; justify-content: center; align-items: center; gap: 0.5rem; background: var(--primary-500); color: white; border: none; cursor: pointer; transition: all 0.15s; box-shadow: var(--shadow-sm); }
    .btn-massive:hover { background: var(--primary-600); box-shadow: var(--shadow-md); transform: translateY(-1px); }

    .live-tracker-area { margin-top: var(--space-4); background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: 0.625rem; display: none; flex-direction: column; max-height: 200px; overflow-y: auto; }
    .tracker-header { font-size: 0.75rem; font-weight: var(--fw-semibold); color: var(--primary-600); margin-bottom: 0.5rem; text-transform: uppercase; letter-spacing: 0.05em; padding-bottom: 0.25rem; border-bottom: 1px solid var(--border-subtle); }
    .tracker-item { background: var(--bg-surface); padding: 0.5rem 0.625rem; border-radius: var(--radius-sm); margin-bottom: 0.375rem; border-left: 3px solid var(--text-muted); font-size: 0.75rem; }
    .tracker-item.running { border-left-color: var(--warning-solid); }
    .tracker-item.completed { border-left-color: var(--success-solid); }
    .tracker-progress-bg { width: 100%; height: 4px; background: var(--bg-surface-2); border-radius: var(--radius-full); overflow: hidden; margin-top: 0.25rem; }
    .tracker-progress-fill { height: 100%; width: 0%; background: var(--text-muted); transition: width 0.5s ease; }
    .tracker-item.running .tracker-progress-fill { width: 50%; background: var(--warning-solid); animation: pulse 2s infinite; }
    .tracker-item.completed .tracker-progress-fill { width: 100%; background: var(--success-solid); }

    .info-alert { background: var(--info-bg); border-left: 3px solid var(--info-solid); padding: 0.75rem; border-radius: var(--radius-sm); margin-bottom: 0.75rem; font-size: var(--text-xs); color: var(--info-text); display: flex; gap: 0.5rem; align-items: flex-start; }
    .file-upload-wrapper { position: relative; width: 100%; padding: 0.875rem; background: var(--bg-surface-2); border: 1px dashed var(--border-default); border-radius: var(--radius-md); display: flex; align-items: center; justify-content: center; cursor: pointer; transition: all 0.15s; margin-bottom: 0.75rem; }
    .file-upload-wrapper:hover { border-color: var(--primary-500); background: var(--primary-50); }
    .file-upload-wrapper input[type="file"] { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .file-upload-text { color: var(--text-tertiary); font-size: var(--text-sm); pointer-events: none; }

    .deploy-guide { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); padding: 0.75rem; border-radius: var(--radius-sm); margin-bottom: 0.75rem; font-size: 0.75rem; color: var(--text-secondary); }
    .deploy-guide strong { color: var(--primary-600); }
    .deploy-guide ul { margin-top: 0.25rem; padding-left: 1.25rem; }
    .deploy-guide li { margin-bottom: 0.125rem; }

    .modal-section { margin-bottom: 1rem; }
    .modal-section label { display: block; font-size: 0.75rem; color: var(--text-tertiary); font-weight: var(--fw-semibold); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 0.375rem; }

    @media (max-width: 1200px) { .deploy-container { grid-template-columns: 1fr; } }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-layer-group"></i> Orkestrasyon & Dağıtım Merkezi</h1>
        <p>Modülleri sürükleyerek sıralı dağıtım senaryoları oluşturun</p>
    </div>
</div>

<div class="deploy-container">
    <div class="deploy-panel">
        <div class="panel-header">
            <span><i class="fas fa-box-open"></i> Depo Merkezi</span>
            <button class="btn secondary sm" onclick="openPkgModal()"><i class="fas fa-upload"></i> Modül Yükle</button>
        </div>
        <div class="repo-tabs">
            <div class="repo-tab active" onclick="switchRepoTab('packages')" id="tab-pkg">Uygulamalar</div>
            <div class="repo-tab" onclick="switchRepoTab('scripts')" id="tab-scr">Betikler</div>
        </div>
        <div class="repo-list" id="repoList"></div>
    </div>

    <div class="deploy-panel">
        <div class="panel-header">
            <span><i class="fas fa-diagram-project"></i> Görev Zinciri</span>
            <span class="badge" id="taskCountBadge">0 Adım</span>
        </div>
        <p style="font-size:var(--text-sm);color:var(--text-tertiary);margin-bottom:0.75rem;">Depodan modülleri aşağıya sürükleyerek sıralı senaryo oluşturun.</p>
        <div class="workflow-zone" id="workflowZone">
            <div class="workflow-empty" id="workflowEmpty">
                <i class="fas fa-arrow-down"></i>Modülleri buraya sürükleyin
            </div>
        </div>
    </div>

    <div class="deploy-panel">
        <div class="panel-header"><i class="fas fa-bullseye"></i> Hedefleme & Kurallar</div>

        <div class="target-section">
            <div class="queue-limit-box">
                <div>
                    <div style="font-size:0.8125rem;font-weight:var(--fw-semibold);color:var(--warning-text);"><i class="fas fa-random"></i> Akıllı Kuyruk Limiti</div>
                    <div style="font-size:0.6875rem;color:var(--text-tertiary);">Eşzamanlı kurulum sayısı</div>
                </div>
                <input type="number" id="queueLimitInput" value="5" min="1" max="100" onchange="updateQueueLimit(this.value)">
            </div>
        </div>

        <div class="target-section">
            <div class="target-label">Dağıtım Kapsamı</div>
            <div class="target-mode-selector">
                <button class="target-btn active" data-mode="ALL" onclick="switchTargetMode('ALL')">Tüm Ağ</button>
                <button class="target-btn" data-mode="LAB" onclick="switchTargetMode('LAB')">Sınıflar</button>
                <button class="target-btn" data-mode="PC" onclick="switchTargetMode('PC')">Özel PC</button>
            </div>
            <div id="targetDynamicArea" class="target-dynamic-area" style="display:none;margin-top:0.75rem;">
                <input type="text" id="targetSearchInput" class="target-search" placeholder="Ara..." oninput="renderTargetList()">
                <div class="target-list" id="targetListContainer"></div>
            </div>
        </div>

        <button class="btn-massive" onclick="executeDeployment(this)">
            <i class="fas fa-rocket"></i> Dağıtımı Başlat
        </button>

        <div class="live-tracker-area" id="liveTrackerArea">
            <div class="tracker-header"><i class="fas fa-satellite-dish"></i> Canlı Operasyon İzleme</div>
            <div id="trackerList"></div>
        </div>
    </div>
</div>

<div class="modal-overlay" id="addPackageModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="modalTitleText">Sisteme Yeni Modül Yükle</div>
            <button class="modal-close" onclick="closePkgModal()"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <input type="hidden" id="pkgEditId" value="">

            <div class="modal-section">
                <label>Modül Tipi</label>
                <select id="pkgType" onchange="togglePkgInputs()">
                    <option value="package">📦 Uygulama Paketi (.exe, .msi, .zip)</option>
                    <option value="script">⚙️ Sistem Betiği (PowerShell, CMD)</option>
                </select>
            </div>

            <div class="modal-section">
                <label>Başlık (Görünür İsim)</label>
                <input type="text" id="pkgName" placeholder="Örn: SumatraPDF Kurulum">
            </div>

            <div id="fileUploadArea">
                <div class="info-alert">
                    <i class="fas fa-info-circle" style="margin-top:0.125rem;"></i>
                    <div><strong>Adli Loglama Aktif:</strong> Tüm kurulum süreci ve hata raporları, hedef bilgisayardaki yerel log dizinine (deploy_trace.txt) kaydedilir.</div>
                </div>
                <div class="deploy-guide" style="background:var(--warning-bg); border-left:3px solid var(--warning-solid); padding:0.75rem; border-radius:var(--radius-sm); margin-bottom:1rem;">
                    <strong style="color:var(--warning-text); display:block; margin-bottom:0.5rem;">
                        <i class="fas fa-triangle-exclamation"></i> ZIP Kuralları (Önemli!)
                    </strong>
                    <ul style="margin:0; padding-left:1.25rem; font-size:var(--text-sm); color:var(--text-secondary); line-height:1.5;">
                        <li>ZIP dosyasının <strong>ana dizininde</strong> (alt klasör içinde değil) mutlaka <code>install.bat</code> adlı bir komut dosyası bulunmalıdır.</li>
                        <li>POps, ZIP'i hedef cihaza çıkarttıktan sonra doğrudan bu <code>install.bat</code> dosyasını (belirttiğiniz ek parametrelerle) yönetici haklarıyla çalıştırır.</li>
                        <li>Kurulumun arka planda sessizce (kullanıcıya pencere açmadan) tamamlanabilmesi için <code>install.bat</code> içerisindeki setup komutlarınıza <em>Sessiz Kurulum (Silent Install)</em> parametrelerini (örneğin <code>/S</code>, <code>/qn</code>, <code>/quiet</code>) eklemeyi unutmayın.</li>
                        <li style="margin-top:0.5rem; list-style:none; margin-left:-1.25rem;">
                            <div style="background:var(--bg-surface-2); padding:0.5rem; border-radius:4px; font-family:var(--font-mono); font-size:0.75rem; border:1px solid var(--border-subtle);">
                                <span style="color:var(--text-tertiary);">:: install.bat Örnek İçeriği:</span><br>
                                <span style="color:var(--text-primary);">setup.exe /S</span><br>
                                <span style="color:var(--text-primary);">msiexec.exe /i program.msi /qn /norestart</span>
                            </div>
                        </li>
                    </ul>
                </div>
                <label>Kurulum Dosyası</label>
                <div id="existingFileInfo" style="display:none;font-size:var(--text-sm);color:var(--success-text);margin-bottom:0.5rem;background:var(--success-bg);padding:0.5rem;border-radius:var(--radius-sm);border:1px solid var(--success-border);">
                    <i class="fas fa-check-circle"></i> <strong id="existingFileName">Mevcut Dosya</strong> sunucuda kayıtlı.
                </div>
                <div class="file-upload-wrapper" id="fileUploadWrapper">
                    <input type="file" id="pkgFileInput" onchange="updateFileName(this)">
                    <span class="file-upload-text" id="pkgFileText"><i class="fas fa-cloud-arrow-up"></i> Dosya seçmek için tıklayın</span>
                </div>
                <label>Sessiz Kurulum Parametreleri (Örn: /S)</label>
                <input type="text" id="pkgParams" placeholder="/S">
            </div>

            <div id="scriptCodeArea" style="display:none;">
                <label>Çalıştırılacak Kod</label>
                <textarea id="pkgCode" placeholder="Yazmaya başlayın..." style="height:140px;background:var(--bg-app);color:var(--success-text);font-family:var(--font-mono);"></textarea>
            </div>

            <label style="display:flex;align-items:center;gap:0.5rem;background:var(--bg-surface-2);padding:0.5rem;border-radius:var(--radius-md);border:1px solid var(--border-subtle);cursor:pointer;">
                <input type="checkbox" id="requireReboot" style="width:16px;height:16px;accent-color:var(--danger-solid);">
                <span style="font-size:var(--text-sm);">İşlem bitince PC'yi yeniden başlat</span>
            </label>
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="closePkgModal()">İptal</button>
            <button class="btn" id="btnSavePackage" onclick="savePackage()"><i class="fas fa-save"></i> Modülü Kaydet</button>
        </div>
    </div>
</div>

<script>
let repository = { packages: [], scripts: [] };
let currentRepoTab = 'packages';
let workflowSequence = [];
let targetMode = 'ALL';
let selectedTargetIds = new Set();
let activeTrackingPCs = {};
let deviceMap = {};
function getApiBase() { return (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : ''; }

function strToBase64UTF16LE(str) {
    let bin = '';
    for (let i = 0; i < str.length; i++) { const c = str.charCodeAt(i); bin += String.fromCharCode(c & 0xff, c >> 8); }
    return btoa(bin);
}

function base64UTF16LEToStr(b64) {
    try { const bin = atob(b64); let s = ''; for (let i = 0; i < bin.length; i += 2) s += String.fromCharCode(bin.charCodeAt(i) | (bin.charCodeAt(i + 1) << 8)); return s; } catch (e) { return ''; }
}

window.loadRepository = async function() {
    try {
        const [packages, limData] = await Promise.all([POps.get('/api/packages'), POps.get('/api/get_concurrent_limit').catch(() => null)]);
        repository.packages = (packages || []).filter(d => d.type === 'package');
        repository.scripts = (packages || []).filter(d => d.type === 'script');
        renderRepoList();
        if (limData && limData.limit) document.getElementById('queueLimitInput').value = limData.limit;
    } catch (e) {
        POps.setError(document.getElementById('repoList'), e, { compact: true });
    }
};

window.updateQueueLimit = async function(newLim) {
    const lim = parseInt(newLim, 10);
    if (!(lim >= 1 && lim <= 100)) { POps.toast('warning', 'Eşzamanlı kurulum sayısı 1 ile 100 arasında olmalı.'); return; }
    await POps.act(null, () => POps.post('/api/set_concurrent_limit', { limit: lim }), { success: 'Eşzamanlı kurulum sayısı ' + lim + ' oldu.', error: 'Kaydedilemedi.' });
}

function openPkgModal(editId = null) {
    const modal = document.getElementById('addPackageModal');
    const titleText = document.getElementById('modalTitleText');
    const btnSave = document.getElementById('btnSavePackage');
    document.getElementById('pkgEditId').value = '';
    document.getElementById('pkgName').value = '';
    document.getElementById('pkgType').value = 'package';
    document.getElementById('pkgParams').value = '';
    document.getElementById('pkgCode').value = '';
    document.getElementById('requireReboot').checked = false;
    document.getElementById('pkgFileInput').value = '';
    updateFileName(document.getElementById('pkgFileInput'));
    document.getElementById('existingFileInfo').style.display = 'none';

    if (editId) {
        titleText.innerText = 'Modülü Düzenle';
        btnSave.innerHTML = '<i class="fas fa-save"></i> Değişiklikleri Kaydet';
        document.getElementById('pkgEditId').value = editId;
        const allItems = [...repository.packages, ...repository.scripts];
        const item = allItems.find(i => i.id === editId);
        if (item) {
            document.getElementById('pkgType').value = item.type;
            document.getElementById('pkgName').value = item.name;
            document.getElementById('requireReboot').checked = item.meta.includes('(Reboot)');
            if (item.type === 'package') {
                document.getElementById('existingFileInfo').style.display = 'block';
                document.getElementById('existingFileName').innerText = item.meta.split('|')[0].trim();
                try {
                    let b64Ps = item.command.split('-EncodedCommand ')[1];
                    if (b64Ps) {
                        let psCode = base64UTF16LEToStr(b64Ps);
                        let m = psCode.match(/FromBase64String\('([^']+)'\)/);
                        if (m && m[1]) {
                            const paramJson = JSON.parse(decodeURIComponent(escape(atob(m[1]))));
                            document.getElementById('pkgParams').value = paramJson.A || '';
                            document.getElementById('pkgEditId').dataset.oldU = paramJson.U;
                            document.getElementById('pkgEditId').dataset.oldF = paramJson.F;
                            document.getElementById('pkgEditId').dataset.oldH = paramJson.H || '';
                            document.getElementById('pkgEditId').dataset.oldMeta = item.meta;
                        }
                    }
                } catch (e) {}
            } else if (item.type === 'script') {
                let rawCode = item.command;
                if (rawCode.includes('ping 127.0.0.1 -n 3')) rawCode = rawCode.split('ping 127.0.0.1 -n 3')[0].trim();
                document.getElementById('pkgCode').value = rawCode;
            }
        }
    } else {
        titleText.innerText = 'Sisteme Yeni Modül Yükle';
        btnSave.innerHTML = '<i class="fas fa-upload"></i> Yükle ve Ekle';
    }
    togglePkgInputs();
    openModal('addPackageModal');
}

function closePkgModal() { closeModal('addPackageModal'); }

function togglePkgInputs() {
    const type = document.getElementById('pkgType').value;
    document.getElementById('fileUploadArea').style.display = type === 'package' ? 'block' : 'none';
    document.getElementById('scriptCodeArea').style.display = type === 'script' ? 'block' : 'none';
}

function updateFileName(input) {
    const textSpan = document.getElementById('pkgFileText');
    if (input.files && input.files.length > 0) {
        textSpan.innerHTML = `<i class="fas fa-file-circle-check" style="color:var(--success-solid);"></i> Seçildi: <strong>${escapeHtml(input.files[0].name)}</strong>`;
    } else {
        textSpan.innerHTML = '<i class="fas fa-cloud-arrow-up"></i> Dosya seçmek için tıklayın';
    }
}

window.savePackage = async function() {
    const editId = document.getElementById('pkgEditId').value;
    const type = document.getElementById('pkgType').value;
    const name = document.getElementById('pkgName').value.trim();
    const needsReboot = document.getElementById('requireReboot').checked;
    if (!name) { POps.toast('warning', 'Modüle bir ad verin.'); document.getElementById('pkgName').focus(); return; }

    let finalCommand = '', metaInfo = '';
    const fileInput = document.getElementById('pkgFileInput');
    const isNewFileSelected = fileInput.files && fileInput.files.length > 0;
    const btn = document.getElementById('btnSavePackage');
    const oldBtnText = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> İşleniyor...'; btn.disabled = true;

    try {
        if (type === 'package') {
            let fileUrl = '', filePath = '', fileHash = '';
            if (!editId || isNewFileSelected) {
                if (!isNewFileSelected) throw new Error('Lütfen kurulum dosyası seçin.');
                const fd = new FormData(); fd.append('file', fileInput.files[0]);
                const upData = await POps.api('/api/upload', { method: 'POST', body: fd });
                // İmzalı adres (sunucu /download'u imzasız vermez) ve dosyanın SHA-256 özeti
                fileUrl = OMYO_API.DOWNLOAD_URL + '/' + encodeURIComponent(upData.filename) + '?sig=' + encodeURIComponent(upData.sig);
                fileHash = upData.sha256 || '';
                filePath = 'C:\\POpsLogs\\' + upData.filename;
                metaInfo = `${upData.filename} | ${(fileInput.files[0].size / (1024 * 1024)).toFixed(1)} MB`;
            } else {
                fileUrl = document.getElementById('pkgEditId').dataset.oldU;
                filePath = document.getElementById('pkgEditId').dataset.oldF;
                fileHash = document.getElementById('pkgEditId').dataset.oldH || '';
                metaInfo = document.getElementById('pkgEditId').dataset.oldMeta.replace(' (Reboot)', '');
            }
            if (needsReboot && !metaInfo.includes('(Reboot)')) metaInfo += ' (Reboot)';
            const params = document.getElementById('pkgParams').value.trim();
            const payloadForPS = { U: fileUrl, F: filePath, A: params, R: needsReboot ? 1 : 0, H: fileHash };
            const b64Params = btoa(unescape(encodeURIComponent(JSON.stringify(payloadForPS))));
            const psCode = `New-Item -ItemType Directory -Force -Path 'C:\\POpsLogs' | Out-Null; $L='C:\\POpsLogs\\deploy_trace.txt'; function T($m){ $d='['+(Get-Date -f 'HH:mm:ss')+'] '+$m; Add-Content $L $d; Write-Output $d }; T '--- OPERASYON BASLADI ---'; try { T '1. Parametreler'; $j=ConvertFrom-Json([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${b64Params}'))); T ('2. URL: '+$j.U); [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; try { (New-Object System.Net.WebClient).DownloadFile($j.U, $j.F); } catch { T '[HATA] Indirilemedi'; exit 1; } if (!(Test-Path $j.F)) { exit 1; } if ($j.H -and ((Get-FileHash -Algorithm SHA256 $j.F).Hash -ne $j.H)) { T '[HATA] Dosya ozeti uyusmuyor; calistirilmadi'; Remove-Item $j.F -Force -ea 0; exit 1 } T '4. Indi.'; Unblock-File $j.F -ea 0; $Ext = [IO.Path]::GetExtension($j.F).ToLower(); if($Ext -eq '.zip'){ Expand-Archive $j.F 'C:\\POpsLogs\\T' -Force; $b=Get-ChildItem 'C:\\POpsLogs\\T' -Filter 'install.bat' -Recurse | Select -First 1; if(!$b){ exit 1 }; $p=Start-Process 'cmd.exe' "/c \`"$($b.FullName)\`" $($j.A)" -Wait -NoNewWindow -PassThru } elseif($Ext -eq '.msi'){ $p=Start-Process 'msiexec.exe' "/i \`"$($j.F)\`" /qn /norestart $($j.A)" -Wait -NoNewWindow -PassThru } else { $p=Start-Process $j.F -ArgumentList $($j.A) -Wait -NoNewWindow -PassThru }; T ('Bitti: '+$p.ExitCode); if($p.ExitCode -in 0,3010){ if($j.R -eq 1){ shutdown -r -t 15 } } else { exit 1 } } catch { T ('[HATA] '+$_); exit 1 }`;
            const encoded = strToBase64UTF16LE(psCode);
            finalCommand = 'powershell.exe -ExecutionPolicy Bypass -NoProfile -WindowStyle Hidden -EncodedCommand ' + encoded;
        } else {
            finalCommand = document.getElementById('pkgCode').value.trim();
            if (!finalCommand) throw new Error('Betik kodu girin.');
            metaInfo = 'Sistem Betiği';
            if (needsReboot) { finalCommand += '\n\nping 127.0.0.1 -n 3 > nul\nshutdown -r -t 15'; metaInfo += ' (Reboot)'; }
        }

        const payload = { id: editId ? editId : 'mod-' + Date.now(), name, type, meta: metaInfo, command: finalCommand, icon: type === 'package' ? 'fa-box-open' : 'fa-terminal', color: type === 'package' ? '#3b82f6' : '#f59e0b' };
        await POps.post('/api/add_package', payload);
        await window.loadRepository();
        workflowSequence.forEach(t => { if (t.id === payload.id) t.name = payload.name; });
        renderWorkflow();
        closePkgModal();
        POps.toast('success', editId ? 'Modül güncellendi.' : `${name} depoya eklendi.`);
    } catch (e) { POps.toast('error', POps.errorMessage(e)); }
    finally { btn.innerHTML = oldBtnText; btn.disabled = false; }
}

window.deletePackage = async function(id, name, btn) {
    if (!await POps.confirm({ title: 'Modül silinsin mi?', message: `"${name}" depodan silinecek. Daha önce kuyruğa eklenmiş görevler etkilenmez.`, confirmText: 'Sil', danger: true })) return;
    if (await POps.act(btn, () => POps.post('/api/delete_package', { id }), { success: 'Modül silindi.' })) {
        await window.loadRepository();
        workflowSequence = workflowSequence.filter(t => t.id !== id);
        renderWorkflow();
    }
};

function renderRepoList() {
    const list = document.getElementById('repoList');
    list.innerHTML = '';
    const items = repository[currentRepoTab];
    if (!items || items.length === 0) { POps.setEmpty(list, { icon: currentRepoTab === 'packages' ? 'fa-box-open' : 'fa-terminal', title: 'Bu depoda henüz öğe yok', text: 'Yeni modül ekleyin.', compact: true }); return; }
    items.forEach(item => {
        const div = document.createElement('div');
        div.className = `repo-item ${item.type}`;
        div.draggable = true;
        div.innerHTML = `
            <i class="fas ${escapeHtml(item.icon)} icon" ${item.color ? `style="color:${escapeHtml(item.color)}"` : ''}></i>
            <div class="details"><span class="title" title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</span><span class="meta">${escapeHtml(item.meta)}</span></div>
            <div style="display:flex;gap:0.25rem;opacity:0;transition:0.2s;" class="repo-actions">
                <button class="btn-remove-task" title="Düzenle" onclick="openPkgModal(${jsArg(item.id)})"><i class="fas fa-pen"></i></button>
                <button class="btn-remove-task" title="Sil" onclick="window.deletePackage(${jsArg(item.id)}, ${jsArg(item.name || '')}, this)"><i class="fas fa-trash"></i></button>
            </div>
            <i class="fas fa-grip-vertical" style="color:var(--text-muted);font-size:0.75rem;"></i>`;
        div.addEventListener('dragstart', (e) => { e.dataTransfer.setData('text/plain', JSON.stringify(item)); div.style.opacity = '0.5'; });
        div.addEventListener('dragend', () => { div.style.opacity = '1'; });
        div.addEventListener('mouseenter', () => { div.querySelector('.repo-actions').style.opacity = '1'; });
        div.addEventListener('mouseleave', () => { div.querySelector('.repo-actions').style.opacity = '0'; });
        list.appendChild(div);
    });
}

function switchRepoTab(tab) {
    currentRepoTab = tab;
    document.getElementById('tab-pkg').classList.toggle('active', tab === 'packages');
    document.getElementById('tab-scr').classList.toggle('active', tab === 'scripts');
    renderRepoList();
}

const workflowZone = document.getElementById('workflowZone');
workflowZone.addEventListener('dragover', (e) => { e.preventDefault(); workflowZone.classList.add('drag-over'); });
workflowZone.addEventListener('dragleave', () => workflowZone.classList.remove('drag-over'));
workflowZone.addEventListener('drop', (e) => {
    e.preventDefault(); workflowZone.classList.remove('drag-over');
    // Yalnızca depodaki modül eklenir: bırakılan metin başka bir sayfadan da gelebilir, komut ondan alınmaz
    let id = null;
    try { id = JSON.parse(e.dataTransfer.getData('text/plain') || '{}').id; } catch (err) {}
    const item = [...repository.packages, ...repository.scripts].find(i => i.id === id);
    if (item) { workflowSequence.push({ ...item, instanceId: Date.now() }); renderWorkflow(); }
});

function removeTask(instanceId) { workflowSequence = workflowSequence.filter(t => t.instanceId !== instanceId); renderWorkflow(); }

function renderWorkflow() {
    const emptyMsg = document.getElementById('workflowEmpty');
    const badge = document.getElementById('taskCountBadge');
    Array.from(workflowZone.children).forEach(child => { if (child.id !== 'workflowEmpty') child.remove(); });
    badge.innerText = `${workflowSequence.length} Adım`;
    if (workflowSequence.length === 0) { emptyMsg.style.display = 'block'; return; }
    emptyMsg.style.display = 'none';
    workflowSequence.forEach((task, index) => {
        if (index > 0) { const c = document.createElement('div'); c.className = 'task-connector'; workflowZone.appendChild(c); }
        const node = document.createElement('div');
        node.className = 'task-node';
        node.innerHTML = `
            <div class="task-step">${index + 1}</div>
            <div class="task-content">
                <div class="task-info">
                    <i class="fas ${escapeHtml(task.icon)} task-icon" style="color: ${escapeHtml(task.color || 'var(--warning-solid)')}"></i>
                    <div style="min-width:0;">
                        <div class="task-type-badge ${task.type === 'package' ? 'pkg' : 'scr'}">${task.type === 'package' ? 'Paket' : 'Betik'}</div>
                        <div style="font-weight:var(--fw-semibold);font-size:var(--text-sm);">${escapeHtml(task.name)}</div>
                        <div style="font-size:0.75rem;color:var(--text-tertiary);">${escapeHtml(task.meta)}</div>
                    </div>
                </div>
                <button class="btn-remove-task" onclick="removeTask(${jsArg(task.instanceId)})"><i class="fas fa-xmark"></i></button>
            </div>`;
        workflowZone.appendChild(node);
    });
}

function switchTargetMode(mode) {
    targetMode = mode;
    selectedTargetIds.clear();
    document.querySelectorAll('.target-btn').forEach(btn => btn.classList.toggle('active', btn.dataset.mode === mode));
    const dyn = document.getElementById('targetDynamicArea');
    if (mode === 'ALL') { dyn.style.display = 'none'; } else { dyn.style.display = 'flex'; document.getElementById('targetSearchInput').value = ''; renderTargetList(); }
}

function renderTargetList() {
    const listContainer = document.getElementById('targetListContainer');
    const searchTerm = document.getElementById('targetSearchInput').value.toLowerCase();
    listContainer.innerHTML = '';
    if (!state.devicesLoaded) { POps.setLoading(listContainer, 'Cihazlar yükleniyor…'); return; }
    if (targetMode === 'LAB') {
        const allLabs = [...new Set(state.devices.map(d => d.lab))];
        const filtered = allLabs.filter(l => l.toLowerCase().includes(searchTerm));
        if (filtered.length === 0) { listContainer.innerHTML = '<div style="text-align:center;padding:1rem;color:var(--text-tertiary);">Sınıf bulunamadı.</div>'; return; }
        filtered.forEach(lab => {
            const div = document.createElement('label');
            div.className = 'target-item';
            div.innerHTML = `<input type="checkbox" value="${escapeHtml(lab)}" ${selectedTargetIds.has(lab) ? 'checked' : ''}><span><i class="fas fa-network-wired" style="color:var(--text-tertiary);"></i> <strong>${escapeHtml(lab)}</strong></span>`;
            div.querySelector('input').addEventListener('change', (e) => { e.target.checked ? selectedTargetIds.add(lab) : selectedTargetIds.delete(lab); });
            listContainer.appendChild(div);
        });
    } else if (targetMode === 'PC') {
        const allLabs = [...new Set(state.devices.map(d => d.lab))];
        let hasAny = false;
        allLabs.forEach(lab => {
            const labDevs = state.devices.filter(d => d.lab === lab && ((d.real_hostname || '').toLowerCase().includes(searchTerm) || d.hostname.toLowerCase().includes(searchTerm)));
            if (labDevs.length > 0) {
                hasAny = true;
                listContainer.innerHTML += `<div class="target-group-header">${escapeHtml(lab)}</div>`;
                labDevs.forEach(d => {
                    const div = document.createElement('label');
                    div.className = 'target-item';
                    div.innerHTML = `<input type="checkbox" value="${escapeHtml(d.hostname)}" ${selectedTargetIds.has(d.hostname) ? 'checked' : ''}><span><i class="fas fa-desktop" style="color:var(--text-tertiary);"></i> ${escapeHtml(d.display_name || d.real_hostname || d.hostname)}</span>`;
                    div.querySelector('input').addEventListener('change', (e) => { e.target.checked ? selectedTargetIds.add(d.hostname) : selectedTargetIds.delete(d.hostname); });
                    listContainer.appendChild(div);
                });
            }
        });
        if (!hasAny) listContainer.innerHTML = '<div style="text-align:center;padding:1rem;color:var(--text-tertiary);">Cihaz bulunamadı.</div>';
    }
}

// Canlı izleme: dağıtımdan önceki en büyük görev kimliğinden sonra bu hedeflere eklenen görevlerin durumu
let tracking = null;
const TRACK_TEXT = { Pending: 'Kuyrukta', Paused: 'Duraklatıldı', Running: 'Çalışıyor', Completed: 'Tamamlandı', 'Completed (Rebooted)': 'Tamamlandı', Failed: 'Hata', Error: 'Hata', Cancelled: 'İptal edildi', Denied: 'Reddedildi', Interrupted: 'Yarıda kaldı', Unknown: 'Bilinmiyor', 'Timed Out': 'Zaman aşımı', Expired: 'Süresi doldu' };
async function refreshTracking() {
    if (!tracking) return;
    let tasks;
    try { tasks = await POps.get('/api/tasks?limit=500'); } catch (e) { return; }
    const mine = (tasks || []).filter(t => t.id > tracking.afterId && tracking.targets.has(t.target_pc));
    tracking.targets.forEach(pc => {
        const list = mine.filter(t => t.target_pc === pc);
        const done = list.filter(t => !['Pending', 'Paused', 'Running'].includes(t.status)).length;
        const bad = list.find(t => ['Failed', 'Error', 'Denied', 'Interrupted', 'Timed Out', 'Expired', 'Cancelled'].includes(t.status));
        const running = list.find(t => t.status === 'Running');
        const el = document.getElementById('track-status-' + CSS.escape(pc));
        const bar = document.querySelector('#track-' + CSS.escape(pc) + ' .tracker-progress-fill');
        if (!el) return;
        el.textContent = !list.length ? 'Atlandı' : bad ? TRACK_TEXT[bad.status] || bad.status : running ? 'Çalışıyor' : done === list.length ? 'Tamamlandı' : 'Kuyrukta';
        el.style.color = bad ? 'var(--danger-text)' : done === list.length && list.length ? 'var(--success-text)' : 'var(--text-tertiary)';
        if (bar) { bar.style.width = (list.length ? Math.round(100 * done / list.length) : 100) + '%'; bar.style.background = bad ? 'var(--danger-solid)' : ''; }
    });
}

window.executeDeployment = async function(btn) {
    if (targetMode !== 'ALL' && selectedTargetIds.size === 0) { POps.toast('warning', 'En az bir hedef seçin.'); return; }
    if (workflowSequence.length === 0) { POps.toast('warning', 'Görev zinciri boş: depodan modül sürükleyin.'); return; }
    const reason = await POps.prompt({ title: 'Dağıtımı başlat', message: `${workflowSequence.length} adımlık zincir ${targetMode === 'ALL' ? 'bütün bilgisayarlara' : selectedTargetIds.size + ' hedefe'} gönderilecek ve SYSTEM hesabıyla çalışacak.`, label: 'Gerekçe (denetim kaydına yazılır)', required: true, maxLength: 300, confirmText: 'Başlat' });
    if (reason === null) return;
    const payload = { target_mode: targetMode, targets: targetMode === 'ALL' ? [] : Array.from(selectedTargetIds), taskSequence: workflowSequence.map(t => ({ name: t.name, type: t.type, command: t.command })), reason: reason.trim() };
    let afterId = 0;
    try { afterId = Math.max(0, ...((await POps.get('/api/tasks?limit=1')) || []).map(t => t.id)); } catch (e) { /* izleme kimlik olmadan da başlar */ }
    let r;
    try { r = await POps.busy(btn, () => POps.post('/api/deploy_orchestration', payload)); }
    catch (e) { POps.toast('error', 'Dağıtım başlatılamadı: ' + POps.errorMessage(e)); return; }
    if (!r) return;
    POps.toast('success', r.duplicate ? 'Aynı dağıtım az önce gönderilmişti.' : `${r.created || 0} görev kuyruğa eklendi` + (r.skipped_module_closed ? `; ${r.skipped_module_closed} bilgisayarda modül kapalı.` : '.'));
    let targets;
    if (targetMode === 'ALL') targets = state.devices.map(d => d.hostname);
    else if (targetMode === 'LAB') targets = state.devices.filter(d => selectedTargetIds.has(d.lab)).map(d => d.hostname);
    else targets = Array.from(selectedTargetIds);
    tracking = { afterId, targets: new Set(targets) };
    document.getElementById('liveTrackerArea').style.display = 'flex';
    document.getElementById('trackerList').innerHTML = targets.map(pc => `<div class="tracker-item" id="track-${escapeHtml(pc)}"><div style="font-weight:var(--fw-semibold);font-size:0.75rem;">${escapeHtml(deviceMap[pc] || pc)} <span style="float:right;color:var(--text-tertiary);" id="track-status-${escapeHtml(pc)}">Kuyrukta</span></div><div class="tracker-progress-bg"><div class="tracker-progress-fill" style="width:0%"></div></div></div>`).join('');
    refreshTracking();
};

document.addEventListener('DOMContentLoaded', () => {
    window.loadRepository();
    // Hedef listeleri ortak cihaz listesinden çizilir; bu sayfa listeyi yoklar
    POps.watchDevices();
    let devicesKey = '';
    document.addEventListener('pops_data_updated', () => {
        state.devices.forEach(d => { deviceMap[d.hostname] = POps.deviceName(d); });
        // Liste yalnızca cihazlar değişince yeniden çizilir (kaydırma ve seçim yerinde kalsın)
        const key = state.devices.map(d => d.hostname + '|' + d.lab + '|' + POps.deviceName(d)).join(';');
        if (key !== devicesKey) { devicesKey = key; if (targetMode !== 'ALL') renderTargetList(); }
    });
    popsPoll(refreshTracking, 4000);
});
</script>

<?php include 'includes/footer.php'; ?>