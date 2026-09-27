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
    .fld { padding: 0.5rem 0.75rem; border: 1px solid var(--border-default); border-radius: var(--radius-md); background: var(--bg-surface-2); color: var(--text-primary); font-size: var(--text-sm); }
    .enroll-list code { font-size: 0.75rem; word-break: break-all; }
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
            Yükleme yalnızca <strong>doğrular ve saklar</strong>. Ajanlara göndermek için aşağıdaki
            <strong>"Ajanlara imzalı güncelleme dağıt"</strong> kartını kullanın.
        </p>
    </div>

    <div class="sys-card">
        <h2><i class="fas fa-key"></i> Ajan kayıt jetonu (enroll)</h2>
        <p class="muted-text" style="margin-top:-0.5rem;margin-bottom:0.75rem;">
            Kurulumda MSI'a verilecek jeton (<code>ENROLL_TOKEN</code>). Çok-kullanımlı jeton, bir laba
            tek MSI ile toplu kurulum içindir.
        </p>
        <div class="row">
            <input id="et-lab" class="fld" style="max-width:190px;" placeholder="Lab adı (opsiyonel)">
            <input id="et-note" class="fld" style="max-width:200px;" placeholder="Açıklama (opsiyonel)">
            <input id="et-uses" class="fld" type="number" min="1" value="1" title="Kaç makine kaydolabilir" style="max-width:110px;">
            <input id="et-ttl" class="fld" type="number" min="1" value="72" title="Geçerlilik (saat)" style="max-width:120px;">
            <button class="btn primary" id="btn-enroll"><i class="fas fa-plus"></i> Üret</button>
        </div>
        <div class="status-msg" id="enroll-status"></div>
        <ul class="file-list enroll-list" id="enroll-list" style="margin-top:1rem;"></ul>
    </div>

    <div class="sys-card">
        <h2><i class="fas fa-rocket"></i> Ajanlara imzalı güncelleme dağıt</h2>
        <p class="muted-text" style="margin-top:-0.5rem;margin-bottom:0.75rem;">
            Yüklenip doğrulanan sürümü (<strong id="dep-staged">—</strong>) seçili online ajanlara gönderir;
            ajan MSI'ı sunucudan indirip imzayı kendi doğrular.
        </p>
        <div class="row">
            <select id="dep-mode" class="fld" style="max-width:220px;">
                <option value="ALL">Tüm ajanlar</option>
                <option value="LAB">Belirli lab</option>
                <option value="PC">Belirli cihaz(lar)</option>
            </select>
            <input id="dep-targets" class="fld" style="flex:1;min-width:200px;display:none;" placeholder="Lab adı / HW- kimlikleri (virgülle)">
            <button class="btn primary" id="btn-deploy"><i class="fas fa-paper-plane"></i> Dağıt</button>
        </div>
        <div class="status-msg" id="deploy-status"></div>
    </div>

    <div class="sys-card">
        <h2><i class="fas fa-lock"></i> Ajan kimlik zorlaması</h2>
        <p class="muted-text" style="margin-top:-0.5rem;margin-bottom:0.75rem;">
            Açıkken kimliği doğrulanmayan (secret'sız) ajan bağlantıları reddedilir.
            <strong>Yalnızca tüm ajanlar yeni sürüme geçtikten sonra açın</strong> — yoksa eski ajanlar bağlantıyı kaybeder.
        </p>
        <div class="row">
            <span id="enforce-badge"></span>
            <button class="btn" id="btn-enforce">…</button>
        </div>
        <div class="status-msg" id="enforce-status"></div>
    </div>

    <div class="sys-card">
        <h2><i class="fas fa-server"></i> Sunucu backend'ini güncelle (self-update)</h2>
        <p class="muted-text" style="margin-top:-0.5rem;margin-bottom:0.75rem;">
            SSH gerektirmeden sunucu backend'ini <strong>origin/main</strong>'den günceller. İstek root systemd
            path-unit'ine iletilir; deploy sağlık kontrolü + otomatik geri dönüş yapar. Keyfi kod yürütülmez.
        </p>
        <div class="row">
            <span id="su-badge"></span>
            <button class="btn primary" id="btn-selfupdate"><i class="fas fa-download"></i> Şimdi güncelle</button>
        </div>
        <div class="status-msg" id="su-status"></div>
    </div>

    <div class="sys-card">
        <h2><i class="fas fa-shield-halved"></i> Cihaz yetenekleri (terminal / Vision)</h2>
        <p class="muted-text" style="margin-top:-0.5rem;margin-bottom:0.75rem;">
            Bir cihazda uzaktan <strong>terminal (execute)</strong> ve/veya <strong>Vision</strong>'ı kapatır. Sunucu ele
            geçirilse bile bu yetenekler o cihazda çalışmaz. <strong>Fail-safe:</strong> "Kapat" kalıcıdır — cihaz
            çevrimdışıysa yeniden bağlanınca uygulanır ve ajan yeniden kurulsa bile kapalı kalır. Geri açmak için önce
            <strong>"İzin ver"</strong>e basıp kapatma isteğini kaldırın; yetenek, ajan onu <em>açık</em> bildirdiğinde
            (yeni/temiz kurulum) geri gelir. Ajan, sunucudan gelen "aç" komutuna güvenmez.
        </p>
        <div class="row">
            <select id="cap-device" class="fld" style="flex:1;min-width:220px;"><option value="">Cihaz seçin…</option></select>
            <button class="btn secondary" id="cap-refresh" title="Listeyi yenile"><i class="fas fa-arrows-rotate"></i></button>
        </div>
        <div id="cap-state" class="muted-text" style="margin:0.5rem 0;font-size:var(--text-sm);"></div>
        <div class="row" style="flex-wrap:wrap;gap:0.5rem;">
            <button class="btn danger" id="cap-off-terminal"><i class="fas fa-terminal"></i> Terminali kapat</button>
            <button class="btn secondary" id="cap-on-terminal" title="Terminal kapatma isteğini kaldır"><i class="fas fa-unlock"></i> Terminale izin ver</button>
            <button class="btn danger" id="cap-off-vision"><i class="fas fa-video-slash"></i> Vision'ı kapat</button>
            <button class="btn secondary" id="cap-on-vision" title="Vision kapatma isteğini kaldır"><i class="fas fa-unlock"></i> Vision'a izin ver</button>
        </div>
        <div class="status-msg" id="cap-status"></div>
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
            $('dep-staged').textContent = d.staged_version || '—';
            renderEnforce(!!d.enforce_agent_auth);
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

    // ---- enroll tokens ----
    async function loadEnroll() {
        try {
            const rows = await (await fetch('/api/system/enroll-tokens')).json();
            const ul = $('enroll-list'); ul.innerHTML = '';
            (rows || []).slice(0, 20).forEach(r => {
                const state = r.expired ? ' · süresi doldu' : (r.is_used ? ' · tükendi' : '');
                const li = document.createElement('li');
                li.innerHTML = '<i class="fas fa-key"></i> <code>' + escapeHtml(r.token) + '</code> '
                    + '<span class="muted-text">' + escapeHtml(r.lab_name || '—') + ' · '
                    + (r.use_count || 0) + '/' + (r.max_uses || 1) + ' kullanım' + state + '</span> '
                    + '<button class="btn" style="padding:0.1rem 0.5rem;font-size:0.72rem;" data-id="' + r.id + '">sil</button>';
                ul.appendChild(li);
            });
            ul.querySelectorAll('button[data-id]').forEach(b => b.addEventListener('click', async () => {
                await fetch('/api/system/enroll-token/' + b.dataset.id, { method: 'DELETE' });
                loadEnroll();
            }));
        } catch (e) { /* yoksay */ }
    }
    $('btn-enroll').addEventListener('click', async () => {
        const st = $('enroll-status'); st.className = 'status-msg'; st.style.display = 'block'; st.textContent = 'Üretiliyor…';
        try {
            const body = {
                lab_name: $('et-lab').value || null, note: $('et-note').value || null,
                ttl_hours: parseInt($('et-ttl').value) || 72, max_uses: parseInt($('et-uses').value) || 1
            };
            const res = await fetch('/api/system/enroll-token', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            const d = await res.json(); if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
            st.className = 'status-msg status-success';
            st.innerHTML = '<i class="fas fa-circle-check"></i> MSI kurulumunda: <code>ENROLL_TOKEN=' + escapeHtml(d.token) + '</code>';
            loadEnroll();
        } catch (e) { st.className = 'status-msg status-error'; st.textContent = e.message; }
    });

    // ---- deploy ----
    $('dep-mode').addEventListener('change', () => { $('dep-targets').style.display = $('dep-mode').value === 'ALL' ? 'none' : 'block'; });
    $('btn-deploy').addEventListener('click', async () => {
        const st = $('deploy-status'); st.className = 'status-msg'; st.style.display = 'block'; st.textContent = 'Dağıtılıyor…';
        try {
            const mode = $('dep-mode').value;
            const targets = mode === 'ALL' ? [] : $('dep-targets').value.split(',').map(s => s.trim()).filter(Boolean);
            const res = await fetch('/api/system/deploy-update', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ target_mode: mode, targets }) });
            const d = await res.json(); if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
            st.className = 'status-msg status-success';
            st.innerHTML = '<i class="fas fa-circle-check"></i> ' + escapeHtml(d.version) + ' gönderildi: ' + (d.dispatched || []).length + ' online'
                + ((d.skipped_offline || []).length ? (', ' + d.skipped_offline.length + ' offline atlandı') : '');
        } catch (e) { st.className = 'status-msg status-error'; st.textContent = e.message; }
    });

    // ---- enforce ----
    function renderEnforce(on) {
        $('enforce-badge').innerHTML = on
            ? '<span class="badge ok"><i class="fas fa-lock"></i> Zorlama AÇIK</span>'
            : '<span class="badge warn"><i class="fas fa-lock-open"></i> Zorlama kapalı (accept-both)</span>';
        const b = $('btn-enforce'); b.textContent = on ? 'Kapat' : 'Aç'; b.dataset.on = on ? '1' : '0';
        b.classList.toggle('primary', !on);
    }
    $('btn-enforce').addEventListener('click', async () => {
        const turnOn = $('btn-enforce').dataset.on !== '1';
        if (turnOn && !confirm('Zorlamayı AÇMAK üzeresiniz. Yeni sürüme geçmemiş tüm ajanlar bağlantıyı kaybeder. Emin misiniz?')) return;
        const st = $('enforce-status'); st.className = 'status-msg'; st.style.display = 'block'; st.textContent = '…';
        try {
            const res = await fetch('/api/system/enforce-auth', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ enabled: turnOn }) });
            const d = await res.json(); if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
            renderEnforce(d.enforce_agent_auth);
            st.className = 'status-msg status-success'; st.textContent = d.enforce_agent_auth ? 'Zorlama açıldı.' : 'Zorlama kapatıldı.';
        } catch (e) { st.className = 'status-msg status-error'; st.textContent = e.message; }
    });

    $('btn-check').addEventListener('click', async function () {
        this.disabled = true;
        const icon = this.querySelector('i');
        icon.classList.add('fa-spin');
        await loadVersion(true);
        icon.classList.remove('fa-spin');
        this.disabled = false;
    });
    $('btn-upload').addEventListener('click', upload);

    // ---- sunucu self-update ----
    async function loadSelfUpdate() {
        try {
            const res = await fetch('/api/system/self-update/status');
            if (!res.ok) throw new Error('HTTP ' + res.status);
            const d = await res.json();
            const badge = $('su-badge');
            const btn = $('btn-selfupdate');
            if (!d.configured) {
                badge.innerHTML = '<span class="badge muted"><i class="fas fa-plug"></i> Kurulu değil</span>';
                btn.disabled = true;
                btn.title = 'systemd path-unit etkin değil (bkz. docs/self-update.md)';
                return;
            }
            btn.disabled = false;
            const s = d.status;
            if (d.pending) badge.innerHTML = '<span class="badge warn"><i class="fas fa-hourglass-half"></i> Kuyrukta…</span>';
            else if (s && s.state === 'running') badge.innerHTML = '<span class="badge warn"><i class="fas fa-spinner fa-spin"></i> Çalışıyor…</span>';
            else if (s && s.state === 'ok') badge.innerHTML = '<span class="badge ok"><i class="fas fa-check"></i> Son: ' + escapeHtml(s.rev || '') + '</span>';
            else if (s && s.state === 'failed') badge.innerHTML = '<span class="badge warn"><i class="fas fa-triangle-exclamation"></i> Son deneme başarısız</span>';
            else badge.innerHTML = '<span class="badge ok"><i class="fas fa-server"></i> Hazır</span>';
        } catch (e) {
            $('su-badge').innerHTML = '<span class="badge warn">Durum alınamadı</span>';
        }
    }

    $('btn-selfupdate').addEventListener('click', async function () {
        if (!confirm("Sunucu backend'i origin/main sürümüne güncellenecek. Devam edilsin mi?")) return;
        const st = $('su-status'); st.className = 'status-msg'; st.style.display = 'block'; st.textContent = 'Kuyruklanıyor…';
        const btn = this;
        btn.disabled = true;
        try {
            const res = await fetch('/api/system/self-update', { method: 'POST' });
            const d = await res.json();
            if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
            st.className = 'status-msg status-success'; st.textContent = 'Güncelleme kuyruklandı, uygulanıyor…';
            let tries = 0;
            const poll = setInterval(async () => {
                tries++;
                await loadSelfUpdate();
                const txt = ($('su-badge').textContent || '');
                if (tries > 20 || txt.includes('Son:') || txt.includes('başarısız')) { clearInterval(poll); btn.disabled = false; }
            }, 3000);
        } catch (e) {
            st.className = 'status-msg status-error'; st.textContent = e.message; btn.disabled = false;
        }
    });

    // ---- cihaz yetenekleri (terminal/Vision) ----
    let capDevices = [];
    function capLabel(v) {
        if (v === true) return '<span class="badge ok">açık</span>';
        if (v === false) return '<span class="badge warn">KAPALI</span>';
        return '<span class="badge muted">bilinmiyor (eski ajan)</span>';
    }
    async function loadCapDevices() {
        try {
            const res = await fetch('/api/devices');
            if (!res.ok) throw new Error('HTTP ' + res.status);
            capDevices = await res.json();
            const sel = $('cap-device');
            const prev = sel.value;
            sel.innerHTML = '<option value="">Cihaz seçin…</option>' + capDevices.map(d =>
                `<option value="${escapeHtml(d.hw_id)}">${escapeHtml(d.display_name || d.real_hostname || d.hw_id)} — ${escapeHtml(d.hw_id)}${d.status === 'Online' ? '' : ' (çevrimdışı)'}</option>`
            ).join('');
            if (prev && capDevices.some(d => d.hw_id === prev)) sel.value = prev;
            renderCapState();
        } catch (e) {
            $('cap-state').textContent = 'Cihaz listesi alınamadı.';
        }
    }
    function renderCapState() {
        const hw = $('cap-device').value;
        const d = capDevices.find(x => x.hw_id === hw);
        const box = $('cap-state');
        const offT = $('cap-off-terminal'), offV = $('cap-off-vision');
        const onT = $('cap-on-terminal'), onV = $('cap-on-vision');
        if (!d) { box.innerHTML = ''; [offT, offV, onT, onV].forEach(b => b.disabled = true); return; }
        // "Kapat": kapatma isteği yoksa aktif. "İzin ver": ancak kapatma isteği kayıtlıysa anlamlı.
        offT.disabled = !!d.cap_terminal_disable_requested;
        offV.disabled = !!d.cap_vision_disable_requested;
        onT.disabled = !d.cap_terminal_disable_requested;
        onV.disabled = !d.cap_vision_disable_requested;
        const reqT = d.cap_terminal_disable_requested ? ' <span class="muted-text">(kapatma isteği kayıtlı)</span>' : '';
        const reqV = d.cap_vision_disable_requested ? ' <span class="muted-text">(kapatma isteği kayıtlı)</span>' : '';
        box.innerHTML = `Terminal: ${capLabel(d.cap_terminal_enabled)}${reqT} &nbsp;·&nbsp; Vision: ${capLabel(d.cap_vision_enabled)}${reqV}`;
    }
    async function capSet(which, enabled) {
        const hw = $('cap-device').value;
        if (!hw) return showToast('Önce cihaz seçin.', 'warning');
        const label = which === 'terminal' ? 'terminali (execute)' : "Vision'ı";
        if (!enabled && !confirm(`Bu cihazda ${label} KAPATMAK üzeresiniz. Kalıcıdır; geri açmak için "İzin ver"e basıp ajanı yeniden kurmanız gerekir. Devam?`)) return;
        const body = { pc_name: hw };
        body[which === 'terminal' ? 'terminal_enabled' : 'vision_enabled'] = !!enabled;
        const st = $('cap-status'); st.className = 'status-msg'; st.style.display = 'block'; st.textContent = 'Gönderiliyor…';
        try {
            const res = await fetch('/api/system/set-capabilities', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            const d = await res.json();
            if (!res.ok) throw new Error(d.detail || ('HTTP ' + res.status));
            st.className = 'status-msg status-success';
            if (enabled) {
                st.textContent = 'Kapatma isteği kaldırıldı. Yetenek, ajan onu açık bildirdiğinde (yeni/temiz kurulum) geri gelir.';
            } else {
                st.textContent = d.delivered_online ? 'Kapatma gönderildi ve uygulandı.' : 'Kapatma kaydedildi; cihaz çevrimdışı, yeniden bağlanınca uygulanacak.';
            }
            setTimeout(loadCapDevices, 1200);
        } catch (e) { st.className = 'status-msg status-error'; st.textContent = e.message; }
    }
    $('cap-device').addEventListener('change', renderCapState);
    $('cap-refresh').addEventListener('click', loadCapDevices);
    $('cap-off-terminal').addEventListener('click', () => capSet('terminal', false));
    $('cap-off-vision').addEventListener('click', () => capSet('vision', false));
    $('cap-on-terminal').addEventListener('click', () => capSet('terminal', true));
    $('cap-on-vision').addEventListener('click', () => capSet('vision', true));

    loadVersion(false);
    loadEnroll();
    loadSelfUpdate();
    loadCapDevices();
})();
</script>
