<?php include 'includes/header.php'; ?>

<style>
    .sys-wrap { display: flex; flex-direction: column; gap: var(--space-5); max-width: 900px; margin: 0 auto; padding: var(--space-4) 0; }
    .sys-card { background: var(--bg-surface); border: 1px solid var(--border-subtle); border-radius: var(--radius-lg); padding: var(--space-6); box-shadow: var(--shadow-sm); }
    .sys-card h2 { font-size: var(--text-md); font-weight: var(--fw-semibold); color: var(--text-primary); margin: 0 0 var(--space-4); display: flex; align-items: center; gap: 0.5rem; }
    .sys-card h2 i { color: var(--primary-500); }
    .ver-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: var(--space-3); }
    .ver-tile { background: var(--bg-surface-2); border: 1px solid var(--border-subtle); border-radius: var(--radius-md); padding: var(--space-4); }
    .ver-tile .lbl { font-size: 0.6875rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-tertiary); font-weight: var(--fw-semibold); }
    .ver-tile .val { font-size: var(--text-lg); font-weight: var(--fw-semibold); color: var(--text-primary); margin-top: 0.25rem; font-variant-numeric: tabular-nums; }
    .badge { display: inline-flex; align-items: center; gap: 0.375rem; padding: 0.25rem 0.625rem; border-radius: var(--radius-md); font-size: var(--text-xs); font-weight: var(--fw-semibold); }
    .badge.ok { background: var(--success-bg); color: var(--success-text); }
    .badge.warn { background: var(--warning-bg, #fef3c7); color: var(--warning-text, #92400e); }
    .badge.muted { background: var(--bg-surface-2); color: var(--text-tertiary); }
    .btn { padding: 0.625rem 1rem; border-radius: var(--radius-md); font-size: var(--text-sm); font-weight: var(--fw-semibold); cursor: pointer; border: 1px solid var(--border-default); background: var(--bg-surface-2); color: var(--text-primary); display: inline-flex; align-items: center; gap: 0.5rem; transition: all 0.15s; }
    .btn:hover { background: var(--bg-app); }
    .btn.primary { background: var(--primary-500); color: #fff; border-color: var(--primary-500); }
    .btn.primary:hover { background: var(--primary-600); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .upload-zone { border: 2px dashed var(--border-default); border-radius: var(--radius-md); padding: 2rem 1.25rem; text-align: center; cursor: pointer; position: relative; background: var(--bg-surface-2); transition: all 0.15s; }
    .upload-zone:hover, .upload-zone.dragover { border-color: var(--primary-500); background: var(--primary-50); }
    .upload-zone i { font-size: 2rem; color: var(--text-tertiary); }
    .upload-zone input[type=file] { position: absolute; inset: 0; opacity: 0; cursor: pointer; }
    .file-list { list-style: none; margin: 0.75rem 0 0; padding: 0; display: flex; flex-direction: column; gap: 0.25rem; }
    .file-list li { font-size: var(--text-sm); color: var(--text-secondary); display: flex; align-items: center; gap: 0.5rem; }
    .file-list li i { color: var(--text-tertiary); }
    .note { font-size: var(--text-sm); color: var(--text-secondary); background: var(--info-bg, rgba(59,130,246,0.1)); border-left: 3px solid var(--info-solid, #3b82f6); padding: 0.75rem 1rem; border-radius: var(--radius-sm); }
    .status-msg { margin-top: 1rem; padding: 0.75rem 1rem; border-radius: var(--radius-md); display: none; font-weight: var(--fw-semibold); font-size: var(--text-sm); border-left: 4px solid transparent; }
    .status-success { background: var(--success-bg); color: var(--success-text); border-color: var(--success-solid); display: block; }
    .status-error { background: var(--danger-bg); color: var(--danger-text); border-color: var(--danger-solid); display: block; }
    .row { display: flex; align-items: center; gap: 0.75rem; flex-wrap: wrap; }
    .muted-text { color: var(--text-tertiary); font-size: var(--text-sm); }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-server"></i> Sistem &amp; Sürüm</h1>
        <p>Sunucu sürümünü görüntüleyin, güncelleme olup olmadığını kontrol edin ve imzalı bir paketi çevrimdışı yükleyin</p>
    </div>
</div>

<div class="sys-wrap">
    <div class="sys-card">
        <h2><i class="fas fa-code-branch"></i> Sürüm</h2>
        <div class="ver-grid">
            <div class="ver-tile"><div class="lbl">Çalışan sürüm</div><div class="val" id="v-running">…</div></div>
            <div class="ver-tile"><div class="lbl">GitHub'daki son sürüm</div><div class="val" id="v-latest">…</div></div>
            <div class="ver-tile"><div class="lbl">Yüklenip doğrulanan (çevrimdışı)</div><div class="val" id="v-staged">—</div></div>
        </div>
        <div class="row" style="margin-top: var(--space-4);">
            <button class="btn primary" id="btn-check"><i class="fas fa-arrows-rotate"></i> Güncellemeleri kontrol et</button>
            <span id="v-badge"></span>
        </div>
    </div>

    <div class="sys-card">
        <h2><i class="fas fa-file-signature"></i> Çevrimdışı imzalı paket yükle</h2>
        <p class="muted-text" style="margin-top:-0.5rem;margin-bottom:0.75rem;">
            GitHub Release'inden indirdiğiniz <code>manifest.json</code>, <code>manifest.json.sig</code> ve
            paket dosyalarını (ör. <code>pops-server-*.tar.gz</code>, <code>POps-Agent-*.zip</code>) birlikte seçin.
            Sunucu ed25519 imzasını ve SHA-256 özetlerini doğrular; doğrulanırsa paket saklanır.
        </p>
        <div class="upload-zone" id="uz">
            <i class="fas fa-file-shield"></i>
            <h3 style="margin:0.5rem 0 0;color:var(--text-primary);font-size:var(--text-md);">Dosyaları seçin veya sürükleyin</h3>
            <p class="muted-text">manifest.json + manifest.json.sig + paket(ler)</p>
            <input type="file" id="files" multiple>
        </div>
        <ul class="file-list" id="file-list"></ul>
        <div class="row" style="margin-top: var(--space-4);">
            <button class="btn primary" id="btn-upload" disabled><i class="fas fa-upload"></i> Doğrula ve yükle</button>
            <label class="muted-text" style="display:flex;align-items:center;gap:0.4rem;">
                <input type="checkbox" id="force"> aynı/eski sürümü zorla (downgrade)
            </label>
        </div>
        <div class="status-msg" id="upload-status"></div>
        <p class="note" style="margin-top:var(--space-4);">
            <i class="fas fa-circle-info"></i>
            Yükleme yalnızca <strong>doğrular ve saklar</strong>. Sunucunun/ajanların bu paketle güncellenmesi
            sonraki aşamada eklenecek.
        </p>
    </div>
</div>

<?php include 'includes/footer.php'; ?>
<script>
(function () {
    const $ = (id) => document.getElementById(id);

    function setBadge(data) {
        const b = $('v-badge');
        if (data.staged_version) {
            b.innerHTML = '<span class="badge ok"><i class="fas fa-box-check"></i> Doğrulanmış paket hazır: ' + escapeHtml(data.staged_version) + '</span>';
        } else if (data.update_available) {
            b.innerHTML = '<span class="badge warn"><i class="fas fa-circle-up"></i> Yeni sürüm mevcut</span>';
        } else if (data.checked_github) {
            b.innerHTML = '<span class="badge ok"><i class="fas fa-check"></i> Güncel</span>';
        } else {
            b.innerHTML = '<span class="badge muted"><i class="fas fa-wifi"></i> GitHub kontrol edilmedi (çevrimdışı olabilir)</span>';
        }
    }

    async function loadVersion(check) {
        try {
            const res = await fetch('/api/system/version' + (check ? '?check=true' : ''));
            if (!res.ok) throw new Error('HTTP ' + res.status);
            const d = await res.json();
            $('v-running').textContent = d.running || '—';
            $('v-latest').textContent = d.latest || (d.checked_github ? '—' : 'bilinmiyor');
            $('v-staged').textContent = d.staged_version || '—';
            setBadge(d);
        } catch (e) {
            $('v-running').textContent = 'hata';
            $('v-badge').innerHTML = '<span class="badge warn">Sürüm alınamadı</span>';
        }
    }

    // dosya seçimi
    let chosen = [];
    function renderFiles() {
        const ul = $('file-list');
        ul.innerHTML = '';
        chosen.forEach(f => {
            const li = document.createElement('li');
            li.innerHTML = '<i class="fas fa-file"></i> ' + escapeHtml(f.name) + ' <span class="muted-text">(' + Math.round(f.size / 1024) + ' KB)</span>';
            ul.appendChild(li);
        });
        $('btn-upload').disabled = chosen.length === 0;
    }
    $('files').addEventListener('change', (e) => { chosen = Array.from(e.target.files); renderFiles(); });
    const uz = $('uz');
    ['dragover', 'dragenter'].forEach(ev => uz.addEventListener(ev, (e) => { e.preventDefault(); uz.classList.add('dragover'); }));
    ['dragleave', 'drop'].forEach(ev => uz.addEventListener(ev, () => uz.classList.remove('dragover')));
    uz.addEventListener('drop', (e) => { e.preventDefault(); chosen = Array.from(e.dataTransfer.files); renderFiles(); });

    async function upload() {
        const st = $('upload-status');
        st.className = 'status-msg';
        st.style.display = 'block';
        st.textContent = 'Doğrulanıyor ve yükleniyor…';
        const fd = new FormData();
        chosen.forEach(f => fd.append('files', f));
        fd.append('force', $('force').checked ? 'true' : 'false');
        try {
            const res = await fetch('/api/system/upload-release', { method: 'POST', body: fd });
            const d = await res.json().catch(() => ({}));
            if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
            st.className = 'status-msg status-success';
            st.innerHTML = '<i class="fas fa-circle-check"></i> Doğrulandı ve saklandı: <strong>' + escapeHtml(d.version) +
                '</strong> (' + escapeHtml((d.artifacts_present || []).join(', ') || 'paket yok') + ')';
            loadVersion(false);
        } catch (e) {
            st.className = 'status-msg status-error';
            st.innerHTML = '<i class="fas fa-circle-xmark"></i> ' + escapeHtml(e.message);
        }
    }

    $('btn-check').addEventListener('click', async function () {
        this.disabled = true;
        const icon = this.querySelector('i');
        icon.classList.add('fa-spin');
        await loadVersion(true);
        icon.classList.remove('fa-spin');
        this.disabled = false;
    });
    $('btn-upload').addEventListener('click', upload);

    loadVersion(false);
})();
</script>
