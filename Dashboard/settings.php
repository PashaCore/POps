<?php include 'includes/header.php'; ?>

<style>
    .settings-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(380px, 1fr)); gap: var(--space-5); margin-bottom: var(--space-8); align-items: start; }
    .setting-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-5); box-shadow: var(--shadow-xs); }
    .setting-header { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin-bottom: var(--space-4); display: flex; align-items: center; gap: 0.625rem; padding-bottom: var(--space-3); border-bottom: 1px solid var(--border-subtle); }
    .setting-header i { color: var(--primary-500); }

    .setting-group { margin-bottom: var(--space-4); }
    .setting-label { display: block; font-size: 0.75rem; color: var(--text-tertiary); font-weight: var(--fw-semibold); margin-bottom: 0.5rem; text-transform: uppercase; letter-spacing: 0.05em; }
    .setting-input { font-family: var(--font-mono); }
    .setting-input:disabled { opacity: 0.6; cursor: not-allowed; background: var(--bg-surface-2); }

    .status-box { padding: 0.75rem 1rem; border-radius: var(--radius-md); display: flex; align-items: center; gap: 0.625rem; font-weight: var(--fw-semibold); margin-top: 1rem; font-size: var(--text-sm); }
    .status-box.online { background: var(--success-bg); border: 1px solid var(--success-border); color: var(--success-text); }
    .status-box.offline { background: var(--danger-bg); border: 1px solid var(--danger-border); color: var(--danger-text); }
    .status-box.warning { background: var(--warning-bg); border: 1px solid var(--warning-border); color: var(--warning-text); }

    .info-alert { background: var(--info-bg); border-left: 3px solid var(--info-solid); padding: 0.75rem 1rem; border-radius: var(--radius-sm); font-size: var(--text-sm); color: var(--info-text); margin-bottom: var(--space-4); display: flex; gap: 0.5rem; align-items: flex-start; line-height: 1.5; }

    .user-table-wrapper { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-4); box-shadow: var(--shadow-xs); }
    .perm-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0.5rem; font-size: var(--text-sm); }
    .perm-grid label { display: flex; align-items: center; gap: 0.5rem; padding: 0.4375rem 0.625rem; background: var(--bg-surface-2); border-radius: var(--radius-sm); cursor: pointer; transition: background-color 0.1s; font-weight: var(--fw-regular); color: var(--text-primary); }
    .perm-grid label:hover { background: var(--bg-surface); }
    .perm-grid input { width: 16px; height: 16px; accent-color: var(--primary-500); }

    .modal-section { margin-bottom: 1rem; }
    .modal-section label { display: block; font-size: 0.75rem; color: var(--text-tertiary); font-weight: var(--fw-semibold); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 0.375rem; }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-gear"></i> Sistem Ayarları</h1>
        <p>Merkez sunucu bağlantıları ve orkestrasyon kuralları</p>
    </div>
</div>

<div class="settings-grid">
    <div class="setting-card">
        <div class="setting-header">
            <i class="fas fa-network-wired"></i> Merkez API Bağlantısı
        </div>
        <div class="info-alert">
            <i class="fas fa-shield-halved"></i>
            <div><strong>Güvenlik:</strong> Adresler kurulum sihirbazı tarafından mühürlenmiştir. Değiştirmek için web_install.php çalıştırılmalıdır.</div>
        </div>
        <div class="setting-group">
            <label class="setting-label">REST API Adresi (HTTP)</label>
            <input type="text" class="setting-input" id="dispHttpUrl" disabled value="Yükleniyor...">
        </div>
        <div class="setting-group">
            <label class="setting-label">WebSocket Adresi (WSS)</label>
            <input type="text" class="setting-input" id="dispWsUrl" disabled value="Yükleniyor...">
        </div>
        <div id="apiStatus" class="status-box warning">
            <i class="fas fa-arrows-rotate fa-spin"></i> Bağlantı sınanıyor...
        </div>
    </div>

    <div class="setting-card">
        <div class="setting-header">
            <i class="fas fa-rocket" style="color:var(--warning-solid);"></i> Orkestrasyon Performansı
        </div>
        <p style="font-size:var(--text-sm);color:var(--text-tertiary);margin-bottom:1rem;line-height:1.5;">
            POps ağın çökmemesi için görevleri (dosya indirme, kurulum) paketler halinde gönderir.
        </p>
        <div class="setting-group">
            <label class="setting-label">Akıllı Kuyruk Limiti (Eşzamanlı)</label>
            <div style="display:flex;gap:0.5rem;align-items:center;">
                <input type="number" class="setting-input" id="queueLimitInput" min="1" max="100" placeholder="Örn: 5">
                <span style="color:var(--text-tertiary);font-size:var(--text-sm);white-space:nowrap;">cihaz / eşzamanlı</span>
            </div>
            <div style="font-size:0.75rem;color:var(--warning-text);margin-top:0.5rem;"><i class="fas fa-info-circle"></i> 1 Gbit ağlar için maksimum 15 önerilir.</div>
        </div>
        <button class="btn" id="saveOrchestrationBtn" style="width:100%;padding:0.75rem;" onclick="saveLimits()">
            <i class="fas fa-save"></i> Performans Ayarlarını Kaydet
        </button>
    </div>

    <div class="setting-card">
        <div class="setting-header">
            <i class="fas fa-shield-halved" style="color:var(--success-solid);"></i> İki Adımlı Doğrulama (2FA)
        </div>
        <p style="font-size:var(--text-sm);color:var(--text-tertiary);margin-bottom:1rem;line-height:1.5;">
            Girişte şifreye ek olarak authenticator uygulamasından (Google Authenticator, Authy, Microsoft Authenticator) 6 haneli kod ister. Yalnızca <strong>kendi hesabınız</strong> için geçerlidir.
        </p>
        <div id="twofaStatus" class="status-box warning"><i class="fas fa-arrows-rotate fa-spin"></i> Durum yükleniyor...</div>

        <div id="twofaSetup" style="display:none;margin-top:1rem;">
            <div id="twofaQr" style="display:flex;justify-content:center;margin:1rem 0;background:#fff;padding:0.75rem;border-radius:var(--radius-md);"></div>
            <div class="setting-group">
                <label class="setting-label">Manuel Anahtar (QR okutamazsanız)</label>
                <input type="text" class="setting-input" id="twofaSecret" readonly onclick="this.select()" style="font-size:0.8rem;">
            </div>
            <div class="setting-group">
                <label class="setting-label">Uygulamadaki 6 Haneli Kod</label>
                <input type="text" class="setting-input" id="twofaEnableCode" inputmode="numeric" maxlength="6" placeholder="123456">
            </div>
            <button class="btn" style="width:100%;padding:0.75rem;" onclick="twofaEnable()"><i class="fas fa-check"></i> Etkinleştir</button>
        </div>

        <div style="margin-top:1rem;">
            <button class="btn" id="twofaSetupBtn" style="width:100%;padding:0.75rem;display:none;" onclick="twofaSetup()"><i class="fas fa-plus"></i> 2FA Kur</button>
            <button class="btn danger" id="twofaDisableBtn" style="width:100%;padding:0.75rem;display:none;" onclick="twofaDisable()"><i class="fas fa-shield-xmark"></i> 2FA'yı Devre Dışı Bırak</button>
        </div>
    </div>
</div>

<!-- QR üretimi tarayıcıda yapılır (gizli anahtar dışarı gitmez). Kütüphane yerelde
     barındırılır → çevrimdışı okullarda da çalışır, harici CDN'e bağımlı değildir. -->
<script src="assets/vendor/qrcode.min.js"></script>

<div class="page-header" style="margin-top:var(--space-8);margin-bottom:var(--space-4);">
    <div><h2 style="font-size:var(--text-2xl);"><i class="fas fa-users" style="color:var(--primary-500);"></i> Kullanıcı Yönetimi</h2></div>
    <button class="btn" onclick="openUserModal()"><i class="fas fa-user-plus"></i> Yeni Kullanıcı Ekle</button>
</div>

<div class="user-table-wrapper">
    <table class="data-table" id="usersTable">
        <thead>
            <tr><th style="width:60px;">ID</th><th>Kullanıcı Adı</th><th>Rol</th><th>Son Giriş</th><th style="text-align:right;width:120px;">İşlem</th></tr>
        </thead>
        <tbody><tr><td colspan="5" style="text-align:center;padding:2rem;color:var(--text-tertiary);">Yükleniyor...</td></tr></tbody>
    </table>
</div>

<div class="modal-overlay" id="userModal">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="modalTitle">Yeni Kullanıcı Ekle</div>
            <button class="modal-close" onclick="closeModal('userModal')"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <input type="hidden" id="modalUserId">
            <div class="modal-section">
                <label>Kullanıcı Adı</label>
                <input type="text" id="modalUsername">
            </div>
            <div class="modal-section">
                <label>Şifre <span style="font-size:0.6875rem;color:var(--text-muted);">(Değiştirmek istemiyorsanız boş bırakın)</span></label>
                <input type="password" id="modalPassword">
            </div>
            <div class="modal-section">
                <label>Yetki Rolü</label>
                <select id="modalRole" onchange="togglePermissionsDiv()">
                    <option value="admin">Standart Yönetici</option>
                    <option value="superadmin">Süper Admin (Tüm Yetkiler)</option>
                </select>
            </div>
            <div class="modal-section" id="permissionsDiv">
                <label>Erişebileceği Sayfalar</label>
                <div class="perm-grid">
                    <label><input type="checkbox" class="perm-cb" value="devices"> Cihaz Yönetimi</label>
                    <label><input type="checkbox" class="perm-cb" value="labs"> Lab Yönetimi</label>
                    <label><input type="checkbox" class="perm-cb" value="vision"> POpsVision</label>
                    <label><input type="checkbox" class="perm-cb" value="tasks"> Görev Kuyruğu</label>
                    <label><input type="checkbox" class="perm-cb" value="deploy"> Dosya Dağıtımı</label>
                    <label><input type="checkbox" class="perm-cb" value="update"> Ajan Güncelleme</label>
                    <label><input type="checkbox" class="perm-cb" value="logger"> Log & Envanter</label>
                    <label><input type="checkbox" class="perm-cb" value="terminal"> Orkestratör</label>
                    <label><input type="checkbox" class="perm-cb" value="settings"> Sistem Ayarları</label>
                </div>
            </div>
        </div>
        <div class="modal-footer">
            <button class="btn secondary" onclick="closeModal('userModal')">İptal</button>
            <button class="btn" onclick="saveUser()">Kaydet</button>
        </div>
    </div>
</div>

<script>
const apiBase = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : '';

document.addEventListener('DOMContentLoaded', () => {
    if (typeof OMYO_API !== 'undefined') {
        document.getElementById('dispHttpUrl').value = OMYO_API.HTTP_URL;
        document.getElementById('dispWsUrl').value = OMYO_API.WS_URL;
        testApiConnection();
        fetchCurrentLimits();
        loadUsers();
        loadTwofaStatus();
    } else {
        document.getElementById('dispHttpUrl').value = 'HATA: api_config.js okunamadı!';
        document.getElementById('dispWsUrl').value = 'HATA: api_config.js okunamadı!';
        document.getElementById('apiStatus').className = 'status-box offline';
        document.getElementById('apiStatus').innerHTML = '<i class="fas fa-xmark-circle"></i> Sistem Konfigürasyonu Bulunamadı!';
    }
});

async function testApiConnection() {
    const box = document.getElementById('apiStatus');
    try {
        const start = Date.now();
        const res = await fetch(`${apiBase}/api/devices`, { method: 'GET', cache: 'no-cache' });
        if (res.ok) {
            const ms = Date.now() - start;
            box.className = 'status-box online';
            box.innerHTML = `<i class="fas fa-check-circle"></i> Sistem Aktif (Gecikme: ${ms}ms)`;
        } else { throw new Error('HTTP ' + res.status); }
    } catch (e) {
        box.className = 'status-box offline';
        box.innerHTML = '<i class="fas fa-triangle-exclamation"></i> Merkez Sunucuya Ulaşılamıyor!';
    }
}

async function fetchCurrentLimits() {
    if (!apiBase) return;
    try {
        const res = await fetch(`${apiBase}/api/get_concurrent_limit`);
        if (res.ok) { const data = await res.json(); document.getElementById('queueLimitInput').value = data.limit || 5; }
    } catch (e) {}
}

async function saveLimits() {
    if (!apiBase) return;
    const btn = document.getElementById('saveOrchestrationBtn');
    const limitValue = parseInt(document.getElementById('queueLimitInput').value);
    if (!limitValue || limitValue < 1 || limitValue > 200) return showToast('1-200 arası geçerli bir limit girin.', 'warning');
    const oldText = btn.innerHTML;
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Kaydediliyor...'; btn.disabled = true;
    try {
        const res = await fetch(`${apiBase}/api/set_concurrent_limit`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ limit: limitValue }) });
        if (res.ok) showToast('Orkestrasyon kuralları güncellendi.', 'success');
        else throw new Error();
    } catch (e) { showToast('Sunucuya ulaşılamadı.', 'error'); }
    finally { btn.innerHTML = oldText; btn.disabled = false; }
}

let usersList = [];

async function loadUsers() {
    try {
        const res = await fetch(`${apiBase}/api/admin/users`);
        if (res.ok) { const data = await res.json(); usersList = data.users || []; renderUsersTable(); }
    } catch (e) { console.error('Kullanıcılar yüklenemedi', e); }
}

function renderUsersTable() {
    const tbody = document.querySelector('#usersTable tbody');
    tbody.innerHTML = '';
    if (usersList.length === 0) { tbody.innerHTML = '<tr><td colspan="5" style="text-align:center;padding:2rem;color:var(--text-tertiary);">Kayıtlı kullanıcı bulunamadı.</td></tr>'; return; }
    usersList.forEach(u => {
        const roleStr = u.role === 'superadmin' ? '<span class="badge success">Süper Admin</span>' : '<span class="badge warning">Admin</span>';
        tbody.innerHTML += `<tr>
            <td style="color:var(--text-tertiary);">#${escapeHtml(u.id)}</td>
            <td><strong>${escapeHtml(u.username)}</strong></td>
            <td>${roleStr}</td>
            <td style="color:var(--text-tertiary);font-size:var(--text-sm);">${escapeHtml(u.last_login || '-')}</td>
            <td style="text-align:right;">
                <button class="btn sm secondary" onclick="editUser(${jsArg(u.id)})" style="padding:0.25rem 0.5rem;"><i class="fas fa-pen"></i></button>
                ${u.role !== 'superadmin' ? `<button class="btn sm danger" onclick="deleteUser(${jsArg(u.id)})" style="padding:0.25rem 0.5rem;margin-left:0.25rem;"><i class="fas fa-trash"></i></button>` : ''}
            </td>
        </tr>`;
    });
}

function openUserModal() {
    document.getElementById('modalTitle').innerText = 'Yeni Kullanıcı Ekle';
    document.getElementById('modalUserId').value = '';
    document.getElementById('modalUsername').value = '';
    document.getElementById('modalPassword').value = '';
    document.getElementById('modalRole').value = 'admin';
    document.querySelectorAll('.perm-cb').forEach(cb => cb.checked = false);
    togglePermissionsDiv();
    openModal('userModal');
}

function togglePermissionsDiv() {
    document.getElementById('permissionsDiv').style.display = document.getElementById('modalRole').value === 'superadmin' ? 'none' : 'block';
}

function editUser(id) {
    const user = usersList.find(u => u.id === id);
    if (!user) return;
    document.getElementById('modalTitle').innerText = 'Kullanıcı Düzenle';
    document.getElementById('modalUserId').value = user.id;
    document.getElementById('modalUsername').value = user.username;
    document.getElementById('modalPassword').value = '';
    document.getElementById('modalRole').value = user.role;
    document.querySelectorAll('.perm-cb').forEach(cb => cb.checked = false);
    if (user.permissions) {
        try {
            const perms = JSON.parse(user.permissions);
            document.querySelectorAll('.perm-cb').forEach(cb => { if (perms.includes(cb.value)) cb.checked = true; });
        } catch (e) {}
    }
    togglePermissionsDiv();
    openModal('userModal');
}

async function saveUser() {
    const id = document.getElementById('modalUserId').value;
    const username = document.getElementById('modalUsername').value;
    const password = document.getElementById('modalPassword').value;
    const role = document.getElementById('modalRole').value;
    const perms = [];
    document.querySelectorAll('.perm-cb').forEach(cb => { if (cb.checked) perms.push(cb.value); });
    if (!username || (!id && !password)) return showToast('Kullanıcı adı ve şifre girin.', 'warning');
    const payload = { username, password: password || undefined, role, permissions: JSON.stringify(perms) };
    const method = id ? 'PUT' : 'POST';
    const url = id ? `${apiBase}/api/admin/users/${id}` : `${apiBase}/api/admin/users`;
    try {
        const res = await fetch(url, { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload) });
        if (res.ok) { closeModal('userModal'); loadUsers(); showToast('Kullanıcı kaydedildi.', 'success'); }
        else showToast(await apiErrorMessage(res, 'Kaydedilemedi.'), 'error');
    } catch (e) { showToast('Bağlantı hatası.', 'error'); }
}

async function deleteUser(id) {
    if (!confirm('Kullanıcıyı silmek istediğinize emin misiniz?')) return;
    try {
        const res = await fetch(`${apiBase}/api/admin/users/${id}`, { method: 'DELETE' });
        if (res.ok) { loadUsers(); showToast('Kullanıcı silindi.', 'success'); }
        else showToast(await apiErrorMessage(res, 'Silinemedi.'), 'error');
    } catch (e) { showToast('Silinemedi.', 'error'); }
}

// ============== İKİ ADIMLI DOĞRULAMA (2FA) ==============
async function loadTwofaStatus() {
    try {
        const res = await fetch(`${apiBase}/api/admin/2fa/status`);
        if (!res.ok) throw new Error();
        const data = await res.json();
        renderTwofa(!!data.enabled);
    } catch (e) {
        const box = document.getElementById('twofaStatus');
        box.className = 'status-box offline';
        box.innerHTML = '<i class="fas fa-triangle-exclamation"></i> Durum alınamadı';
    }
}

function renderTwofa(enabled) {
    const box = document.getElementById('twofaStatus');
    document.getElementById('twofaSetup').style.display = 'none';
    document.getElementById('twofaSetupBtn').style.display = enabled ? 'none' : 'block';
    document.getElementById('twofaDisableBtn').style.display = enabled ? 'block' : 'none';
    if (enabled) {
        box.className = 'status-box online';
        box.innerHTML = '<i class="fas fa-lock"></i> 2FA aktif — girişte kod istenir';
    } else {
        box.className = 'status-box warning';
        box.innerHTML = '<i class="fas fa-lock-open"></i> 2FA kapalı';
    }
}

async function twofaSetup() {
    try {
        const res = await fetch(`${apiBase}/api/admin/2fa/setup`, { method: 'POST' });
        if (!res.ok) return showToast(await apiErrorMessage(res, 'Kurulum başlatılamadı.'), 'error');
        const data = await res.json();
        document.getElementById('twofaSecret').value = data.secret || '';
        const qr = document.getElementById('twofaQr');
        qr.innerHTML = '';
        if (typeof QRCode !== 'undefined' && data.otpauth_uri) {
            new QRCode(qr, { text: data.otpauth_uri, width: 180, height: 180 });
        } else {
            qr.innerHTML = '<span style="font-size:var(--text-xs);color:#666;">QR yüklenemedi — aşağıdaki manuel anahtarı kullanın.</span>';
        }
        document.getElementById('twofaSetup').style.display = 'block';
        document.getElementById('twofaSetupBtn').style.display = 'none';
    } catch (e) { showToast('Bağlantı hatası.', 'error'); }
}

async function twofaEnable() {
    const code = (document.getElementById('twofaEnableCode').value || '').trim();
    if (!/^\d{6}$/.test(code)) return showToast('6 haneli kodu girin.', 'warning');
    try {
        const res = await fetch(`${apiBase}/api/admin/2fa/enable`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ otp: code })
        });
        if (res.ok) { showToast('2FA etkinleştirildi.', 'success'); loadTwofaStatus(); }
        else showToast(await apiErrorMessage(res, 'Etkinleştirilemedi.'), 'error');
    } catch (e) { showToast('Bağlantı hatası.', 'error'); }
}

async function twofaDisable() {
    const code = prompt("2FA'yı kapatmak için authenticator kodunu girin:");
    if (code === null) return;
    try {
        const res = await fetch(`${apiBase}/api/admin/2fa/disable`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ otp: (code || '').trim() })
        });
        if (res.ok) { showToast('2FA devre dışı bırakıldı.', 'success'); loadTwofaStatus(); }
        else showToast(await apiErrorMessage(res, 'Kapatılamadı.'), 'error');
    } catch (e) { showToast('Bağlantı hatası.', 'error'); }
}

// Sunucunun döndürdüğü hata açıklamasını (FastAPI 'detail') gösterir
async function apiErrorMessage(res, fallback) {
    try {
        const body = await res.json();
        if (typeof body.detail === 'string') return body.detail;
    } catch (e) {}
    return fallback;
}

function escapeHtml(s) { return String(s || '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }
</script>

<?php include 'includes/footer.php'; ?>