<?php include 'includes/header.php'; ?>
<?php $vsRole = $_SESSION['role'] ?? ''; $vsCanAdmin = in_array($vsRole, ['admin', 'superadmin'], true); ?>

<style>
    .vs-bar { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; margin-bottom: 18px; }
    .vs-bar .grow { flex: 1; }
    .vs-bar .search-field { flex: 0 1 240px; min-width: 180px; }
    .vs-bar select { width: auto; min-width: 170px; }
    .vs-scope { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; font-size: var(--text-sm); color: var(--text-tertiary); min-height: 34px; min-width: 0; max-width: 100%; }
    .vs-scope .segmented { max-width: 100%; }
    @media (max-width: 640px) { .vs-bar .search-field { flex: 1 1 100%; } .vs-bar .grow { display: none; } }
    .vs-scope b { color: var(--text-primary); font-weight: var(--fw-semibold); }
    .vs-scope .lnk, .lnk { color: var(--primary-500); font-size: var(--text-sm); }
    .vs-scope .lnk:hover, .lnk:hover { text-decoration: underline; }
    .faint { color: var(--text-muted); }
    .segmented .n { color: var(--text-muted); font-variant-numeric: tabular-nums; font-size: var(--text-xs); }

    /* Ekran duvarı */
    .vs-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(240px, 100%), 1fr)); gap: 16px; }
    @media (max-width: 640px) { .vs-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; } }
    .vs-grid > .empty-state { grid-column: 1 / -1; }
    .scr { background: var(--bg-surface); border-radius: 14px; box-shadow: 0 0 0 1px var(--border-subtle), 0 1px 2px rgba(0, 0, 0, 0.03); overflow: hidden; cursor: pointer; min-width: 0; transition: box-shadow 0.12s; }
    .scr:hover { box-shadow: 0 0 0 1px var(--border-default), 0 4px 14px rgba(0, 0, 0, 0.07); }
    .scr:focus-visible { outline: none; box-shadow: 0 0 0 2px var(--primary-500), var(--focus-ring); }
    .scr .thumb { position: relative; aspect-ratio: 16 / 9; background: var(--bg-surface-3); display: flex; align-items: center; justify-content: center; overflow: hidden; }
    .scr .thumb img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
    .scr .ph { display: flex; flex-direction: column; align-items: center; gap: 6px; color: var(--text-muted); font-size: var(--text-xs); text-align: center; padding: 0 12px; line-height: 1.35; }
    .scr .ph .ico { width: 22px; height: 22px; stroke-width: 1.5; }
    .scr .mk { position: absolute; left: 8px; top: 8px; }
    .scr .age { position: absolute; right: 8px; bottom: 8px; font-size: 11px; line-height: 1; padding: 4px 7px; border-radius: 99px; background: rgba(0, 0, 0, 0.55); color: #fff; }
    .scr .cap { padding: 10px 12px 12px; }
    .scr .nm { display: flex; align-items: center; gap: 7px; font-weight: var(--fw-semibold); font-size: var(--text-sm); color: var(--text-primary); min-width: 0; }
    .scr .nm .t { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .scr .sub { font-size: var(--text-xs); color: var(--text-muted); margin-top: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .scr.dim .thumb { background: var(--bg-surface-2); }
    .scr.dim .ph { color: var(--text-tertiary); }
    .scr.dim .nm { color: var(--text-tertiary); }

    /* Büyük görünüm */
    .vw { overflow: hidden; display: flex; flex-direction: column; }
    .vw-head { display: flex; align-items: center; gap: 10px 14px; padding: 10px 14px; border-bottom: 1px solid var(--border-subtle); flex-wrap: wrap; }
    .vw-title { min-width: 0; flex: 1 1 200px; }
    .vw-title h2 { font-size: var(--text-lg); font-weight: var(--fw-semibold); overflow-wrap: anywhere; }
    .vw-title .sub { font-size: var(--text-sm); color: var(--text-tertiary); display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
    .vw-ctrl { display: flex; align-items: center; gap: 6px 16px; flex-wrap: wrap; }
    .vw-ctrl .switch-field { cursor: pointer; margin: 0; white-space: nowrap; }
    .vw-ctrl .switch-field.off { opacity: 0.5; cursor: not-allowed; }
    .vw-ctrl select { width: auto; min-height: 32px; padding-top: 4px; padding-bottom: 4px; }
    .vw-ctrl .ibtns { display: flex; gap: 2px; }
    .vw-stage { position: relative; background: var(--text-primary); height: calc(100vh - 290px); min-height: 280px; display: flex; align-items: center; justify-content: center; overflow: hidden; }
    .vw-stage img { width: 100%; height: 100%; object-fit: contain; user-select: none; pointer-events: none; display: block; }
    #inputLayer { position: absolute; inset: 0; outline: none; z-index: 3; cursor: crosshair; }
    .vw-wait { position: absolute; inset: 0; z-index: 2; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 10px; text-align: center; padding: 0 24px; color: rgba(255, 255, 255, 0.82); font-size: var(--text-md); background: rgba(0, 0, 0, 0.45); }
    .vw-wait.solid { background: transparent; }
    .vw-wait small { font-size: var(--text-sm); color: rgba(255, 255, 255, 0.6); }
    .vw-wait .spinner { border-color: rgba(255, 255, 255, 0.25); border-top-color: #fff; }
    .vw-badge { position: absolute; top: 12px; right: 12px; z-index: 4; font-size: var(--text-xs); font-weight: var(--fw-semibold); padding: 5px 10px; border-radius: 99px; background: rgba(0, 0, 0, 0.5); color: #fff; display: inline-flex; align-items: center; gap: 6px; pointer-events: none; }
    .vw-badge .dot { background: rgba(255, 255, 255, 0.6); }
    .vw-badge.live .dot { background: var(--danger-solid); }
    .vw-badge.ctrl { background: var(--warning-solid); }
    .vw-badge.ctrl .dot { background: #fff; }
    .vw-foot { padding: 10px 16px; font-size: var(--text-sm); color: var(--text-tertiary); display: flex; gap: 6px 10px; align-items: center; flex-wrap: wrap; min-height: 42px; }
    .vw-foot b { color: var(--text-primary); font-weight: var(--fw-medium); }
    .vw-foot .tm { font-variant-numeric: tabular-nums; }
    .vw.full { position: fixed; inset: 0; z-index: var(--z-modal); border-radius: 0; }
    .vw.full .vw-stage { height: auto; flex: 1; }
    .vw:fullscreen { border-radius: 0; }
    .vw:fullscreen .vw-stage { height: auto; flex: 1; }
    @media (max-width: 640px) { .vw-stage { height: 56vw; min-height: 200px; } }

    /* Teşhis */
    .diag-out { background: var(--text-primary); color: #e5e5ea; border-radius: 10px; padding: 10px 12px; font-family: var(--font-mono); font-size: 12px; white-space: pre-wrap; overflow-wrap: anywhere; height: 220px; overflow: auto; margin-bottom: 14px; }
    .diag-cmds { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
    .diag-cmds .btn { justify-content: flex-start; }
    @media (max-width: 480px) { .diag-cmds { grid-template-columns: minmax(0, 1fr); } }
</style>

<div class="page-header">
    <div>
        <h1>Uzak ekran</h1>
        <div class="summary" id="vsSummary"></div>
    </div>
    <div class="page-header-actions">
        <button type="button" class="ibtn boxed" id="histBtn" data-tip="Oturum geçmişi" aria-label="Oturum geçmişi"><?php echo pops_icon('clock'); ?></button>
        <button type="button" class="ibtn boxed" id="refreshBtn" data-tip="Ekranları tazele" data-tip-pos="left" aria-label="Ekranları tazele"><?php echo pops_icon('refresh'); ?></button>
    </div>
</div>

<div id="vsMain">
    <div class="vs-bar">
        <div class="vs-scope" id="vsScope"></div>
        <span class="grow"></span>
        <?php if ($vsCanAdmin): ?><div class="actionbar" id="vsActions" role="toolbar" aria-label="Bilgisayar işlemleri"></div><?php endif; ?>
        <div class="search-field">
            <i class="fas fa-search" aria-hidden="true"></i>
            <input type="search" id="vsSearch" placeholder="Bilgisayar, kullanıcı ya da IP" aria-label="Bilgisayar, kullanıcı, IP ya da MAC ara">
        </div>
    </div>
    <div class="vs-grid" id="vsGrid"><div class="loading-state" role="status" style="grid-column:1/-1"><span class="spinner"></span>Yükleniyor…</div></div>
</div>

<div class="card vw" id="viewer" hidden>
    <div class="vw-head">
        <button type="button" class="ibtn" data-act="back" data-tip="Ekranlara dön (Esc)" aria-label="Ekranlara dön"><?php echo pops_icon('left'); ?></button>
        <div class="vw-title"><h2 id="vwName"></h2><div class="sub" id="vwSub"></div></div>
        <div class="vw-ctrl">
            <label class="switch-field" id="liveField"><span class="switch success"><input type="checkbox" id="liveSw"><span></span></span>Canlı izle</label>
            <label class="switch-field off" id="ctrlField"><span class="switch warning"><input type="checkbox" id="ctrlSw" disabled><span></span></span>Kontrol</label>
            <select id="fpsSel" aria-label="Kare hızı" hidden>
                <option value="1">1 kare/sn</option>
                <option value="2" selected>2 kare/sn</option>
                <option value="5">5 kare/sn</option>
            </select>
            <div class="ibtns">
                <button type="button" class="ibtn" data-act="snap" data-tip="Görüntüyü tazele" aria-label="Görüntüyü tazele"><?php echo pops_icon('refresh'); ?></button>
                <button type="button" class="ibtn" data-act="full" data-tip="Tam ekran" aria-label="Tam ekran"><?php echo pops_icon('expand'); ?></button>
                <button type="button" class="ibtn" data-act="more" data-tip="Diğer işlemler" data-tip-pos="left" aria-label="Diğer işlemler" aria-haspopup="menu"><?php echo pops_icon('more'); ?></button>
            </div>
        </div>
    </div>
    <div class="vw-stage" id="vwStage">
        <img id="liveImg" alt="" draggable="false" hidden>
        <div class="vw-wait solid" id="vwWait"></div>
        <div id="inputLayer" tabindex="0" aria-label="Uzak bilgisayar ekranı; fare ve klavye bu bilgisayara gider" hidden></div>
        <span class="vw-badge" id="vwBadge"><span class="dot"></span><span id="vwBadgeT">Önizleme</span></span>
    </div>
    <div class="vw-foot" id="vwFoot"></div>
</div>

<div id="sessModal" class="modal-overlay">
    <div class="modal-box">
        <div class="modal-header">
            <div class="modal-title" id="sessTitle">Canlı izleme oturumu</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <p class="card-desc" id="sessDesc"></p>
            <?php if ($vsRole !== 'viewer'): ?>
            <div class="field">
                <label>Bağlantı türü</label>
                <div class="segmented block" id="sessType" role="group" aria-label="Bağlantı türü">
                    <button type="button" data-type="routine" class="active" aria-pressed="true">Kullanıcıya sor</button>
                    <button type="button" data-type="mandatory" aria-pressed="false">Zorunlu müdahale</button>
                </div>
                <div class="field-hint" id="sessHint"></div>
            </div>
            <?php endif; ?>
            <div class="field">
                <label for="sessReason">Gerekçe</label>
                <input type="text" id="sessReason" maxlength="300" placeholder="Örn. ağ bağlantı sorununa bakılacak" autocomplete="off">
                <div class="field-error" id="sessErr">Gerekçeyi yazın.</div>
            </div>
        </div>
        <div class="modal-footer">
            <button type="button" class="btn secondary" data-close-modal>Vazgeç</button>
            <button type="button" class="btn" id="sessStart">Oturumu başlat</button>
        </div>
    </div>
</div>

<?php if ($vsCanAdmin): ?>
<div id="diagModal" class="modal-overlay">
    <div class="modal-box lg">
        <div class="modal-header">
            <div class="modal-title" id="diagTitle">Teşhis</div>
            <button type="button" class="modal-close" data-close-modal aria-label="Kapat"><i class="fas fa-xmark"></i></button>
        </div>
        <div class="modal-body">
            <div class="diag-out" id="diagOut" aria-live="polite"></div>
            <div class="diag-cmds" id="diagCmds">
                <button type="button" class="btn secondary" data-cmd="check_process"><?php echo pops_icon('list', 'sm'); ?>POps süreçlerini listele</button>
                <button type="button" class="btn secondary" data-cmd="check_logs"><?php echo pops_icon('file', 'sm'); ?>Son ajan günlüğünü oku</button>
                <button type="button" class="btn secondary" data-cmd="restart_capture"><?php echo pops_icon('eye', 'sm'); ?>Ekran yakalamayı yeniden başlat</button>
                <button type="button" class="btn secondary" data-cmd="sync_time"><?php echo pops_icon('clock', 'sm'); ?>Saati eşitle</button>
                <button type="button" class="btn secondary" data-cmd="restart_agent"><?php echo pops_icon('restart', 'sm'); ?>Ajanı yeniden başlat</button>
                <button type="button" class="btn danger-soft" data-cmd="reboot_pc"><?php echo pops_icon('power', 'sm'); ?>Bilgisayarı yeniden başlat</button>
            </div>
            <p class="field-hint" style="margin-top:12px">Komutlar görev kuyruğundan SYSTEM olarak çalışır; kimin gönderdiği kaydedilir. Bilgisayarda uzak komut kapalıysa ajan reddeder.</p>
        </div>
    </div>
</div>
<?php endif; ?>

<script>
(function () {
    const dev = POps.dev;
    const $ = (id) => document.getElementById(id);
    const CAN_ADMIN = dev.canAdmin;
    const IS_VIEWER = window.USER_ROLE === 'viewer';
    const ME = {
        id: <?php echo isset($_SESSION['admin_id']) ? intval($_SESSION['admin_id']) : 'null'; ?>,
        name: <?php echo json_encode((string) ($_SESSION['username'] ?? ''), JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_INVALID_UTF8_SUBSTITUTE); ?>,
        role: <?php echo json_encode((string) $vsRole, JSON_HEX_TAG | JSON_HEX_AMP | JSON_HEX_APOS | JSON_HEX_QUOT | JSON_INVALID_UTF8_SUBSTITUTE); ?>
    };
    const WS_URL = (typeof OMYO_API !== 'undefined') ? OMYO_API.wsUrl('/ws/panel') : '';
    const UN = '__atanmamis';
    const IMG = 'data:image/jpeg;base64,';
    const B64 = /^[A-Za-z0-9+/=\s]+$/;

    // ?pc=HW-1,HW-2 (Sınıflar ve Cihazlar sayfasından "Ekranları izle"): yalnızca bu bilgisayarlar
    function pcParam() {
        const m = location.search.match(/[?&]pc=([^&#]*)/);
        if (!m) return null;
        const list = m[1].split(',').map(x => { try { return decodeURIComponent(x.replace(/\+/g, ' ')); } catch (e) { return x; } }).map(x => x.trim()).filter(Boolean);
        return list.length ? [...new Set(list)] : null;
    }
    const params = new URLSearchParams(location.search);
    const ui = { pcs: pcParam(), lab: params.get('lab') || (function () { try { return localStorage.getItem('pops_lab') || ''; } catch (e) { return ''; } })(), q: '', hash: '', thumbKey: '' };
    const cache = {};   // bilgisayar -> { img, at }
    const asked = {};   // bilgisayar -> görüntü istendiği an
    let visionMod = null;
    const V = { pc: null, live: false, sid: null, ctrl: false, startedAt: 0, reason: '', mandatory: false, tick: null, cd: null, frames: 0, held: new Map(), type: 'routine' };

    // ---- Kapsam
    function visionOn(lab) {
        if (!visionMod) return true;
        const le = visionMod.lab_enabled || {};
        return Object.prototype.hasOwnProperty.call(le, lab) ? !!le[lab] : visionMod.enabled !== false;
    }
    // Ekran neden gösterilemiyor (boşsa gösterilebilir)
    function blocked(d) {
        if (!d || d.missing) return { icon: 'alert', text: 'Kayıtlı bilgisayar değil' };
        if (POps.isOffline(d)) return { icon: 'power', text: 'Kapalı' };
        if (d.cap_vision_enabled === false) return { icon: 'lock', text: 'Uzak ekran bu bilgisayarda kapalı' };
        if (!visionOn(d.lab || dev.UNASSIGNED)) return { icon: 'lock', text: 'Uzak ekran bu sınıfta kapalı' };
        if (IS_VIEWER) return { icon: 'eye', text: 'Ekran görüntüleri yalnızca yöneticilere gösterilir' };
        return null;
    }
    const unassigned = () => (state.devices || []).filter(d => !d.lab || d.lab === dev.UNASSIGNED);
    function labs() { return dev.labs(); }
    function ensureLab() {
        if (ui.pcs) return;
        const ls = labs();
        if (ui.lab === UN && unassigned().length) return;
        if (!ls.includes(ui.lab)) ui.lab = ls.find(l => (state.devices || []).some(d => d.lab === l)) || ls[0] || (unassigned().length ? UN : '');
    }
    function matches(d) {
        if (!ui.q) return true;
        const q = ui.q.toLocaleLowerCase('tr');
        return [POps.deviceName(d), d.hostname, d.ip, d.mac, dev.user(d), d.lab].some(v => String(v || '').toLocaleLowerCase('tr').includes(q));
    }
    function scopeDevices() {
        const all = state.devices || [];
        let list;
        if (ui.pcs) list = ui.pcs.map(h => all.find(d => d.hostname === h) || { hostname: h, missing: true });
        else if (ui.q) list = all.slice();
        else if (ui.lab === UN) list = unassigned();
        else list = all.filter(d => d.lab === ui.lab);
        if (ui.q) list = list.filter(d => !d.missing && matches(d));
        return list.sort((a, b) => POps.deviceName(a).localeCompare(POps.deviceName(b), 'tr', { numeric: true }));
    }
    function scopeName() {
        if (ui.pcs) return ui.pcs.length === 1 ? dev.name(ui.pcs[0]) : `seçili ${ui.pcs.length} bilgisayar`;
        if (ui.q) return 'süzgece uyanlar';
        return ui.lab === UN ? 'atanmamışlar' : ui.lab;
    }

    // ---- Üst kısım: kapsam satırı, özet, işlem çubuğu
    function renderScope() {
        const box = $('vsScope');
        if (ui.pcs) {
            const missing = ui.pcs.filter(h => !dev.find(h)).length;
            const psig = 'pcs:' + ui.pcs.length + ':' + missing;
            if (box.dataset.sig === psig) return;
            box.dataset.sig = psig;
            box.innerHTML = `<span>Seçili <b>${ui.pcs.length}</b> bilgisayar${missing ? ` (${Number(missing)} tanesi kayıtlı değil)` : ''}</span><span class="faint">·</span><button type="button" class="lnk" data-act="all">Tümünü göster</button>`;
            return;
        }
        const ls = labs();
        const items = ls.map(l => [l, l, (state.devices || []).filter(d => d.lab === l).length]);
        if (unassigned().length) items.push([UN, 'Atanmamış', unassigned().length]);
        if (!items.length) { box.innerHTML = ''; return; }
        const sig = JSON.stringify([items, ui.lab, !!ui.q]);
        if (box.dataset.sig === sig) return;
        box.dataset.sig = sig;
        if (items.length <= 5) {
            box.innerHTML = `<div class="segmented" role="group" aria-label="Sınıf">${items.map(([k, label, n]) => {
                const on = !ui.q && k === ui.lab;
                return `<button type="button" data-lab="${escapeHtml(k)}" class="${on ? 'active' : ''}" aria-pressed="${on ? 'true' : 'false'}">${escapeHtml(label)} <span class="n">${Number(n)}</span></button>`;
            }).join('')}</div>`;
        } else {
            box.innerHTML = `<select id="vsLab" aria-label="Sınıf">${items.map(([k, label, n]) => `<option value="${escapeHtml(k)}" ${k === ui.lab ? 'selected' : ''}>${escapeHtml(label)} (${Number(n)})</option>`).join('')}</select>`;
        }
    }
    $('vsScope').addEventListener('click', (e) => {
        const b = e.target.closest('button');
        if (!b) return;
        if (b.dataset.act === 'all') showAll();
        else if (b.dataset.lab) setLab(b.dataset.lab);
    });
    $('vsScope').addEventListener('change', (e) => { if (e.target.id === 'vsLab') setLab(e.target.value); });
    function setLab(lab) {
        ui.lab = lab; ui.q = ''; $('vsSearch').value = '';
        try { if (lab !== UN) localStorage.setItem('pops_lab', lab); } catch (e) { /* özel pencere */ }
        history.replaceState(null, '', 'vision.php?lab=' + encodeURIComponent(lab));
        render(true);
    }
    function showAll() {
        const first = ui.pcs && dev.find(ui.pcs[0]);
        ui.pcs = null;
        if (first && first.lab) ui.lab = first.lab && first.lab !== dev.UNASSIGNED ? first.lab : UN;
        history.replaceState(null, '', 'vision.php' + (ui.lab ? '?lab=' + encodeURIComponent(ui.lab) : ''));
        $('vsScope').dataset.sig = '';
        render(true);
    }

    function renderSummary(list) {
        const real = list.filter(d => !d.missing);
        const on = real.filter(d => !POps.isOffline(d)).length;
        const capOff = real.filter(d => !POps.isOffline(d) && blocked(d)).length;
        $('vsSummary').innerHTML = `<span class="sum"><b>${list.length}</b> ekran</span>`
            + `<span class="sum"><span class="dot on"></span><b>${Number(on)}</b> açık</span>`
            + `<span class="sum"><span class="dot off"></span><b>${Number(real.length - on)}</b> kapalı</span>`
            + (capOff && !IS_VIEWER ? `<span class="sum"><span class="dot"></span><b>${Number(capOff)}</b> izlenemiyor</span>` : '')
            + (V.live ? `<span class="sum"><span class="dot bad"></span>${escapeHtml(dev.name(V.pc))} canlı izleniyor</span>` : '');
    }

    function renderActions(list) {
        const bar = $('vsActions');
        if (!bar) return;
        const n = list.filter(d => !d.missing).length;
        const label = ui.pcs ? 'Seçili' : ui.q ? 'Süzgeçteki' : (ui.lab === UN ? 'Atanmamış' : 'Bu sınıf');
        const tgt = `${n} bilgisayar`;
        bar.innerHTML = `<span class="scope">${escapeHtml(label)} <b>${Number(n)}</b></span><span class="sep"></span>`
            + `<button type="button" class="ibtn" data-act="wake" data-tip="Uyandır · ${escapeHtml(tgt)}" aria-label="Uyandır">${POps.iconHtml('zap')}</button>`
            + `<button type="button" class="ibtn" data-act="restart" data-tip="Yeniden başlat · ${escapeHtml(tgt)}" aria-label="Yeniden başlat">${POps.iconHtml('restart')}</button>`
            + `<button type="button" class="ibtn danger" data-act="shutdown" data-tip="Kapat · ${escapeHtml(tgt)}" aria-label="Kapat">${POps.iconHtml('power')}</button>`
            + `<span class="sep"></span><button type="button" class="ibtn" data-act="more" data-tip="Diğer işlemler" data-tip-pos="left" aria-label="Diğer işlemler" aria-haspopup="menu">${POps.iconHtml('more')}</button>`;
    }
    if ($('vsActions')) $('vsActions').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b) return;
        const hosts = scopeDevices().filter(d => !d.missing).map(d => d.hostname);
        const o = { btn: b, source: 'vision', scopeLabel: scopeName(), lab: !ui.pcs && !ui.q && ui.lab !== UN ? ui.lab : null, wholeLab: !ui.pcs && !ui.q && ui.lab !== UN };
        const act = b.dataset.act;
        if (act === 'wake' || act === 'restart' || act === 'shutdown') dev.power(act, hosts, o);
        else if (act === 'more') {
            const all = (state.devices || []).map(d => d.hostname);
            POps.menu(b, [
                { label: 'Mesaj gönder', icon: 'message', onClick: () => dev.message(hosts, o) },
                { label: 'Uzak komut', icon: 'terminal', onClick: () => { const on = hosts.filter(h => !POps.isOffline(dev.find(h))); if (!on.length) return POps.toast('warning', 'Açık bilgisayar yok.'); location.href = dev.commandUrl(on.slice(0, 200)); } },
                '-',
                { header: 'Bütün ağ' },
                { label: 'Ağdaki hepsini uyandır', icon: 'zap', onClick: () => window.wakeUpCommand('ALL', null, b) },
                { label: 'Ağdaki hepsini kapat', icon: 'power', danger: true, onClick: () => dev.power('shutdown', all, { btn: b, source: 'vision', scopeLabel: 'bütün ağ' }) }
            ]);
        }
    });

    // ---- Ekran kartları
    function cardHtml(d) {
        const r = blocked(d);
        const st = d.missing ? { cls: 'off', word: 'Bulunamadı' } : dev.state(d);
        const name = d.missing ? d.hostname : POps.deviceName(d);
        let sub;
        if (d.missing) sub = 'Kayıtlı değil';
        else if (st.cls === 'off') sub = st.since ? 'Son görülme ' + POps.relTime(st.since) : 'Kapalı';
        else sub = dev.user(d) || (st.cls === 'idle' ? 'Boşta' : 'Oturum yok');
        if ((ui.pcs || ui.q) && !d.missing) sub += ' · ' + (d.lab && d.lab !== dev.UNASSIGNED ? d.lab : 'Atanmamış');
        const phHtml = r ? `${POps.iconHtml(r.icon)}<span>${escapeHtml(r.text)}</span>` : `${POps.iconHtml('monitor')}<span class="pt">Görüntü bekleniyor</span>`;
        const markHtml = d.is_quarantined ? `<span class="mk">${dev.markHtml({ kind: 'lock', icon: 'lock', text: 'Karantinada: kullanıcı ekranı kilitli' })}</span>` : '';
        return `<div class="scr${r ? ' dim' : ''}" data-host="${escapeHtml(d.hostname)}" role="button" tabindex="0" aria-label="${escapeHtml(name + ' · ' + (r ? r.text : st.word))}">
            <div class="thumb"><img alt="" hidden><div class="ph">${phHtml}</div>${markHtml}<span class="age" hidden></span></div>
            <div class="cap"><div class="nm"><span class="dot ${escapeHtml(st.cls)}" title="${escapeHtml(st.word)}"></span><span class="t">${escapeHtml(name)}</span></div><div class="sub">${escapeHtml(sub)}</div></div>
        </div>`;
    }
    function applyImage(card) {
        const h = card.dataset.host, c = cache[h];
        const img = card.querySelector('img'), ph = card.querySelector('.ph'), age = card.querySelector('.age');
        if (card.classList.contains('dim')) return;
        if (c) {
            if (img.dataset.at !== String(c.at)) { img.src = IMG + c.img; img.dataset.at = String(c.at); }
            img.hidden = false; ph.hidden = true;
            const sec = (Date.now() - c.at) / 1000;
            age.hidden = sec < 60;
            if (sec >= 60) age.textContent = POps.relTime(c.at);
        } else {
            const pt = ph.querySelector('.pt');
            if (pt) pt.textContent = asked[h] && Date.now() - asked[h] > 10000 ? 'Görüntü gelmedi' : 'Görüntü bekleniyor';
        }
    }
    function applyImages() { document.querySelectorAll('#vsGrid .scr[data-host]').forEach(applyImage); }

    function render(force) {
        if (!state.devicesLoaded) return;
        ensureLab();
        renderScope();
        const list = scopeDevices();
        renderSummary(list);
        const sig = JSON.stringify([ui.pcs, ui.lab, ui.q, IS_VIEWER, visionMod && [visionMod.enabled, visionMod.lab_enabled], list.map(d => [d.hostname, d.status, POps.deviceName(d), dev.user(d), d.lab, d.cap_vision_enabled, d.is_quarantined, POps.isOffline(d) ? d.last_seen : ''])]);
        if (!force && sig === ui.hash) return;
        ui.hash = sig;
        renderActions(list);
        const grid = $('vsGrid');
        if (!list.length) {
            const any = (state.devices || []).length;
            POps.setEmpty(grid, ui.q ? { icon: 'fa-filter', title: 'Süzgece uyan bilgisayar yok', text: 'Aramayı değiştirin.' }
                : !any ? { icon: 'fa-desktop', title: 'Henüz bilgisayar yok', text: 'Ajan kurulan bilgisayarlar bağlandıkça burada görünür.' }
                : { icon: 'fa-desktop', title: 'Bu sınıfta bilgisayar yok', text: 'Başka bir sınıf seçin.' });
            return;
        }
        grid.innerHTML = list.map(cardHtml).join('');
        applyImages();
        // Kapsam değişince (ilk açılış, sınıf, seçim) görüntüler bir kez istenir
        const key = JSON.stringify([ui.pcs, ui.lab, ui.q]);
        if (key !== ui.thumbKey && !V.pc) { ui.thumbKey = key; requestThumbs(list); }
    }

    $('vsGrid').addEventListener('click', (e) => {
        const c = e.target.closest('.scr[data-host]');
        if (!c || e.target.closest('.mark')) return;
        openCard(c);
    });
    $('vsGrid').addEventListener('keydown', (e) => {
        const c = e.target.closest('.scr[data-host]');
        if (c && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); openCard(c); }
    });
    function openCard(c) {
        const h = c.dataset.host;
        const d = dev.find(h);
        if (!d) return POps.toast('warning', 'Bu bilgisayar kayıtlı değil.');
        if (blocked(d)) { dev.open(h, { source: 'vision' }); return; }
        openViewer(h);
    }

    let qt = null;
    $('vsSearch').addEventListener('input', (e) => { clearTimeout(qt); qt = setTimeout(() => { ui.q = e.target.value.trim(); $('vsScope').dataset.sig = ''; render(true); }, 150); });

    // ---- Panel WebSocket'i: önizleme ve canlı kareler, teşhis çıktısı, ret bildirimi
    const WS = { ws: null, queue: [], backoff: 2000 };
    function connectWS() {
        if (!WS_URL) return;
        let ws;
        try { ws = new WebSocket(WS_URL); } catch (e) { setTimeout(connectWS, WS.backoff); WS.backoff = Math.min(30000, WS.backoff * 2); return; }
        WS.ws = ws;
        ws.onopen = () => { WS.backoff = 2000; while (WS.queue.length && ws.readyState === WebSocket.OPEN) ws.send(WS.queue.shift()); };
        ws.onmessage = onMessage;
        ws.onclose = () => { if (WS.ws === ws) WS.ws = null; setTimeout(connectWS, WS.backoff); WS.backoff = Math.min(30000, WS.backoff * 2); };
    }
    const wsOpen = () => !!(WS.ws && WS.ws.readyState === WebSocket.OPEN);
    function wsSend(obj) {
        const t = JSON.stringify(obj);
        if (wsOpen()) { WS.ws.send(t); return true; }
        WS.queue.push(t);
        if (WS.queue.length > 100) WS.queue.shift();
        return false;
    }
    function onMessage(ev) {
        let data;
        try { data = JSON.parse(ev.data); } catch (e) { return; }
        if (data.type === 'terminal_output' && data.id === V.pc && $('diagModal') && $('diagModal').classList.contains('open')) {
            diagLog('\n' + String(data.output == null ? '' : data.output));
        } else if (data.type === 'vision_rejected') {
            const who = data.hw_id || data.pc_name || data.device;
            if (V.live && (!who || who === V.pc)) { POps.toast('warning', 'Kullanıcı bağlantıyı reddetti; oturum kapatıldı.'); stopLive(); }
        } else if ((data.type === 'thumbnail' || data.type === 'stream_frame') && data.hw_id && typeof data.image === 'string' && B64.test(data.image.slice(0, 200))) {
            cache[data.hw_id] = { img: data.image, at: Date.now() };
            const card = document.querySelector(`#vsGrid .scr[data-host="${CSS.escape(data.hw_id)}"]`);
            if (card) applyImage(card);
            if (V.pc === data.hw_id) {
                if (data.type === 'stream_frame') { V.frames += 1; if (V.live) showFrame(data.image, true); }
                else if (!V.live || !V.frames) showFrame(data.image, false);
            }
        }
    }
    // Önizleme isteği: WebSocket açıksa onunla, değilse HTTP ile (yanıt 5 sn bekler; aynı anda en çok 4)
    function requestThumbs(list) {
        if (IS_VIEWER) return;
        const hosts = list.filter(d => !blocked(d)).map(d => d.hostname);
        const now = Date.now();
        hosts.forEach(h => { asked[h] = now; });
        if (wsOpen()) hosts.forEach(h => wsSend({ type: 'remote_input', device: h, action: 'get_thumbnail' }));
        else {
            const queue = hosts.slice();
            const worker = async () => {
                while (queue.length) {
                    const h = queue.shift();
                    try {
                        const r = await POps.get('/api/thumbnail/' + encodeURIComponent(h));
                        if (r && typeof r.image === 'string' && r.image && B64.test(r.image.slice(0, 200))) {
                            onMessage({ data: JSON.stringify({ type: 'thumbnail', hw_id: h, image: r.image }) });
                        }
                    } catch (e) { /* tek bilgisayar: kartta "Görüntü gelmedi" görünür */ }
                }
            };
            for (let i = 0; i < Math.min(4, queue.length); i++) worker();
        }
        setTimeout(applyImages, 10500);
    }
    $('refreshBtn').addEventListener('click', function () {
        if (V.pc) { snapshot(); return; }
        const list = scopeDevices();
        if (!list.filter(d => !blocked(d)).length) return POps.toast('info', 'İzlenebilecek açık bilgisayar yok.');
        requestThumbs(list);
        POps.toast('info', 'Ekran görüntüleri istendi.');
    });

    // =================================================================
    // BÜYÜK GÖRÜNÜM (canlı izleme ve kontrol)
    // =================================================================
    function showFrame(b64, live) {
        const img = $('liveImg');
        img.src = IMG + b64; img.hidden = false;
        if (live || !V.live) setWait('');
    }
    function setWait(text, sub, spin) {
        const w = $('vwWait');
        if (!text) { w.hidden = true; w.textContent = ''; return; }
        w.hidden = false;
        w.classList.toggle('solid', $('liveImg').hidden);
        w.replaceChildren();
        if (spin) w.append(POps.el('span', { className: 'spinner' }));
        w.append(POps.el('div', { text }));
        if (sub) w.append(POps.el('small', { text: sub }));
    }
    function renderViewerHead() {
        const d = dev.find(V.pc);
        if (!d) return;
        const st = dev.state(d);
        $('vwName').textContent = POps.deviceName(d);
        const subHtml = `<span class="dot ${escapeHtml(st.cls)}"></span>${escapeHtml(st.word)}` + (dev.user(d) ? ' · ' + escapeHtml(dev.user(d)) : '')
            + (d.lab && d.lab !== dev.UNASSIGNED ? ' · ' + escapeHtml(d.lab) : '') + (d.is_quarantined ? ' · karantinada' : '');
        $('vwSub').innerHTML = subHtml;
    }
    function renderViewerState() {
        $('liveSw').checked = V.live;
        $('liveSw').disabled = IS_VIEWER;
        $('liveField').classList.toggle('off', IS_VIEWER);
        $('ctrlSw').disabled = !V.live || IS_VIEWER;
        $('ctrlSw').checked = V.ctrl;
        $('ctrlField').classList.toggle('off', !V.live || IS_VIEWER);
        $('fpsSel').hidden = !V.live;
        $('inputLayer').hidden = !V.ctrl;
        const badge = $('vwBadge');
        badge.className = 'vw-badge' + (V.ctrl ? ' ctrl' : V.live ? ' live' : '');
        $('vwBadgeT').textContent = V.ctrl ? 'Kontrol açık' : V.live ? 'Canlı' : 'Önizleme';
        renderFoot();
    }
    function renderFoot() {
        const foot = $('vwFoot');
        if (V.live) {
            const sec = Math.max(0, Math.round((Date.now() - V.startedAt) / 1000));
            const mm = String(Math.floor(sec / 60)).padStart(2, '0') + ':' + String(sec % 60).padStart(2, '0');
            foot.innerHTML = `<span class="dot bad"></span><b>${V.mandatory ? 'Zorunlu müdahale' : 'Canlı izleme'}</b><span>· ${escapeHtml(ME.name || '?')}</span><span>· gerekçe: ${escapeHtml(V.reason)}</span><span class="tm">· ${escapeHtml(mm)}</span>`;
        } else if (IS_VIEWER) {
            foot.textContent = 'Canlı izleme ve kontrol yalnızca yöneticilere açıktır.';
        } else {
            const c = cache[V.pc];
            foot.textContent = (c ? 'Önizleme · son görüntü ' + POps.relTime(c.at) + '. ' : 'Önizleme. ') + 'Canlı izlemek için oturum açılır; kim, ne zaman ve hangi gerekçeyle bağlandığı kaydedilir.';
        }
    }
    function openViewer(h) {
        V.pc = h; V.live = false; V.sid = null; V.ctrl = false; V.frames = 0;
        $('vsMain').hidden = true;
        $('viewer').hidden = false;
        renderViewerHead();
        const c = cache[h];
        if (c) showFrame(c.img, false);
        else { $('liveImg').hidden = true; setWait('Görüntü bekleniyor…', '', true); }
        renderViewerState();
        snapshot();
        window.scrollTo(0, 0);
        const back = document.querySelector('#viewer [data-act="back"]');
        if (back) back.focus();
        render(false);
    }
    function snapshot() {
        if (!V.pc || IS_VIEWER) return;
        const h = V.pc;
        asked[h] = Date.now();
        if (wsOpen()) wsSend({ type: 'remote_input', device: h, action: 'get_thumbnail' });
        else POps.get('/api/thumbnail/' + encodeURIComponent(h)).then(r => {
            if (r && typeof r.image === 'string' && r.image) onMessage({ data: JSON.stringify({ type: 'thumbnail', hw_id: h, image: r.image }) });
            else if (V.pc === h && !cache[h] && !V.live) setWait('Görüntü alınamadı', 'Bilgisayardaki ajan önizleme göndermedi. Biraz sonra tazeleyin.');
        }).catch((e) => {
            // Sunucu 200 + status:error döner: ajan şu an bağlı değil
            if (V.pc === h && !cache[h] && !V.live) setWait('Görüntü alınamadı', e && e.status === 200 ? 'Bilgisayardaki ajan şu an sunucuya bağlı değil.' : POps.errorMessage(e));
        });
        setTimeout(() => { if (V.pc === h && !cache[h] && !V.live && !$('vwWait').hidden && $('liveImg').hidden && $('vwWait').textContent.startsWith('Görüntü bekleniyor')) setWait('Görüntü gelmedi', 'Bilgisayardaki ajan önizleme göndermedi. Biraz sonra tazeleyin.'); }, 10000);
    }
    async function closeViewer() {
        if (!V.pc) return;
        await stopLive();
        if (document.fullscreenElement) { try { await document.exitFullscreen(); } catch (e) { /* yok */ } }
        $('viewer').classList.remove('full');
        V.pc = null;
        $('viewer').hidden = true;
        $('vsMain').hidden = false;
        ui.hash = '';
        render(true);
    }

    // ---- Oturum (rıza/gerekçe/denetim): /api/audit/session/start → canlı yayın; bitince /api/stream/stop + /api/audit/session/end
    function setSessType(t) {
        V.type = t;
        const seg = $('sessType');
        if (seg) seg.querySelectorAll('button').forEach(b => { const on = b.dataset.type === t; b.classList.toggle('active', on); b.setAttribute('aria-pressed', on ? 'true' : 'false'); });
        const hint = $('sessHint');
        if (hint) hint.textContent = t === 'mandatory'
            ? 'Kullanıcıya kısa bir uyarı gösterilir, süre dolunca onay beklemeden bağlanılır. Gerekçe zorunludur.'
            : 'Kullanıcının ekranında onay sorulur; kabul edince görüntü başlar.';
    }
    function openSession() {
        if (!V.pc || IS_VIEWER) return;
        exitFull();
        $('sessDesc').textContent = `${dev.name(V.pc)} ekranı canlı izlenecek. Oturum kim, ne zaman ve hangi gerekçeyle açıldığıyla birlikte denetim kaydına yazılır.`;
        $('sessReason').value = '';
        $('sessReason').closest('.field').classList.remove('has-error');
        setSessType('routine');
        openModal('sessModal');
    }
    if ($('sessType')) $('sessType').addEventListener('click', (e) => { const b = e.target.closest('button[data-type]'); if (b) setSessType(b.dataset.type); });
    $('sessReason').addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); $('sessStart').click(); } });
    $('sessReason').addEventListener('input', () => $('sessReason').closest('.field').classList.remove('has-error'));
    $('sessStart').addEventListener('click', async function () {
        const reason = $('sessReason').value.trim();
        if (!reason) { $('sessReason').closest('.field').classList.add('has-error'); $('sessReason').focus(); return; }
        const pc = V.pc;
        if (!pc) return;
        let data;
        try {
            data = await POps.busy(this, () => POps.post('/api/audit/session/start', {
                admin_id: ME.id, admin_name: ME.name, admin_role: ME.role, target_pc: pc, reason, is_mandatory: V.type === 'mandatory'
            }));
            if (!data || !data.session_id) throw new Error('Sunucu oturum açmadı.');
        } catch (e) { POps.toast('error', 'Oturum başlatılamadı: ' + POps.errorMessage(e)); return; }
        closeModal('sessModal');
        if (V.pc !== pc) { endSession(pc, data.session_id); return; }
        V.sid = data.session_id; V.reason = reason; V.mandatory = V.type === 'mandatory';
        startLive(Number(data.countdown_seconds) || 0);
    });
    function startLive(countdown) {
        V.live = true; V.frames = 0; V.startedAt = Date.now();
        clearInterval(V.cd); clearInterval(V.tick);
        if (countdown > 0) {
            let c = countdown;
            const show = () => setWait('Zorunlu müdahale başlatıldı', `Kullanıcıya ${c} saniye süre verildi.`, true);
            show();
            V.cd = setInterval(() => {
                c -= 1;
                if (!V.live || V.frames) { clearInterval(V.cd); return; }
                if (c <= 0) { clearInterval(V.cd); setWait('Görüntü aktarımı başlıyor…', '', true); } else show();
            }, 1000);
        } else {
            setWait('Kullanıcının onayı bekleniyor…', 'Kullanıcı kabul edince görüntü başlar.', true);
        }
        V.tick = setInterval(() => { if (V.live) { renderFoot(); } }, 1000);
        renderViewerState();
        render(false);
    }
    function endSession(pc, sid, keepalive) {
        const o = keepalive ? { keepalive: true } : undefined;
        const a = POps.post('/api/stream/stop', { pc_name: pc }, o).catch(() => {});
        const b = sid ? POps.post('/api/audit/session/end', { session_id: sid, status: 'Ended' }, o).catch(() => {}) : Promise.resolve();
        return Promise.all([a, b]);
    }
    async function stopLive() {
        if (!V.pc) return;
        const wasLive = V.live, sid = V.sid, pc = V.pc;
        if (V.ctrl) setCtrl(false);
        V.live = false; V.sid = null; V.frames = 0;
        clearInterval(V.cd); clearInterval(V.tick);
        if (!$('liveImg').hidden) setWait(''); else if (wasLive) setWait('Görüntü bekleniyor…', '', true);
        renderViewerState();
        render(false);
        if (wasLive || sid) await endSession(pc, sid);
    }
    $('liveSw').addEventListener('change', (e) => {
        if (e.target.checked) { e.target.checked = false; openSession(); }
        else stopLive();
    });
    $('fpsSel').addEventListener('change', () => {
        if (V.pc && V.live) wsSend({ type: 'remote_input', device: V.pc, action: 'set_fps', fps: parseInt($('fpsSel').value, 10) });
    });

    // ---- Kontrol: fare ve klavye (yalnızca açık oturumda; sunucu da oturumu denetler)
    function setCtrl(on) {
        V.ctrl = !!on && V.live;
        if (!V.ctrl) releaseHeldKeys();
        renderViewerState();
        if (V.ctrl) $('inputLayer').focus();
    }
    $('ctrlSw').addEventListener('change', (e) => setCtrl(e.target.checked));
    function sendInput(type, action, e) {
        if (!V.pc || !V.ctrl || !wsOpen()) return;
        const display = $('liveImg');
        if (type === 'mouse') {
            const rect = display.getBoundingClientRect();
            const nw = display.naturalWidth, nh = display.naturalHeight;
            if (!nw || !nh) return;
            const fit = Math.min(rect.width / nw, rect.height / nh);
            const rw = nw * fit, rh = nh * fit;
            const ox = (rect.width - rw) / 2, oy = (rect.height - rh) / 2;
            const xN = Math.max(0, Math.min(1, (e.clientX - rect.left - ox) / rw));
            const yN = Math.max(0, Math.min(1, (e.clientY - rect.top - oy) / rh));
            // Kare %75 ölçekte gelir: koordinat ekranın gerçek çözünürlüğüne çevrilir
            const x = Math.round(xN * (nw / 0.75)), y = Math.round(yN * (nh / 0.75));
            if (action === 'down' || action === 'move') WS.ws.send(JSON.stringify({ type: 'remote_input', device: V.pc, input_type: 'mouse_move', x, y, relative: false }));
            if (action === 'down' || action === 'up') {
                const btn = e.button === 1 ? 'middle' : e.button === 2 ? 'right' : 'left';
                WS.ws.send(JSON.stringify({ type: 'remote_input', device: V.pc, input_type: 'mouse_click', button: btn, is_down: action === 'down', double: false }));
            }
        } else if (type === 'keyboard') {
            // code (fiziksel tuş) ve değiştiriciler de gider: ajan Türkçe Q/F düzenini, AltGr ile yazılanları ve kısayolları ayırt eder
            const id = e.code || e.key;
            if (action === 'down') V.held.set(id, { key: e.key, code: e.code }); else V.held.delete(id);
            WS.ws.send(JSON.stringify({ type: 'remote_input', device: V.pc, input_type: 'keyboard', key: e.key, code: e.code,
                ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, meta: e.metaKey,
                altgr: !!(e.getModifierState && e.getModifierState('AltGraph')), is_down: action === 'down' }));
        }
    }
    // Odak giderse keyup gelmez: basılı kalan tuşlar uzak bilgisayarda bırakılır (son basılan önce)
    function releaseHeldKeys() {
        const held = [...V.held.values()].reverse();
        V.held.clear();
        if (!held.length || !V.pc || !wsOpen()) return;
        held.forEach(k => WS.ws.send(JSON.stringify({ type: 'remote_input', device: V.pc, input_type: 'keyboard', key: k.key, code: k.code, ctrl: false, alt: false, shift: false, meta: false, altgr: false, is_down: false })));
    }
    (function wireInput() {
        const layer = $('inputLayer');
        layer.addEventListener('mousemove', e => sendInput('mouse', 'move', e));
        layer.addEventListener('mousedown', e => sendInput('mouse', 'down', e));
        layer.addEventListener('mouseup', e => sendInput('mouse', 'up', e));
        layer.addEventListener('contextmenu', e => e.preventDefault());
        layer.addEventListener('wheel', e => {
            if (!V.ctrl || !wsOpen()) return;
            e.preventDefault();
            WS.ws.send(JSON.stringify({ type: 'remote_input', device: V.pc, input_type: 'mouse_wheel', delta: e.deltaY > 0 ? -120 : 120, horizontal: false }));
        }, { passive: false });
        layer.addEventListener('keydown', e => { if (V.ctrl) { e.preventDefault(); e.stopPropagation(); sendInput('keyboard', 'down', e); } });
        layer.addEventListener('keyup', e => { if (V.ctrl) { e.preventDefault(); e.stopPropagation(); sendInput('keyboard', 'up', e); } });
        layer.addEventListener('blur', releaseHeldKeys);
        window.addEventListener('blur', releaseHeldKeys);
        document.addEventListener('visibilitychange', () => { if (document.hidden) releaseHeldKeys(); });
        $('liveImg').addEventListener('contextmenu', e => e.preventDefault());
    })();

    // ---- Görünüm üst çubuğu
    function exitFull() { if (document.fullscreenElement) document.exitFullscreen().catch(() => {}); $('viewer').classList.remove('full'); }
    function toggleFull() {
        const el = $('viewer');
        if (document.fullscreenElement || el.classList.contains('full')) { exitFull(); return; }
        if (el.requestFullscreen) el.requestFullscreen().catch(() => el.classList.add('full'));
        else el.classList.add('full');
    }
    document.addEventListener('fullscreenchange', () => { if (!document.fullscreenElement) $('viewer').classList.remove('full'); });
    document.querySelector('#viewer .vw-head').addEventListener('click', (e) => {
        const b = e.target.closest('[data-act]');
        if (!b || b.disabled) return;
        const act = b.dataset.act;
        if (act === 'back') closeViewer();
        else if (act === 'snap') snapshot();
        else if (act === 'full') toggleFull();
        else if (act === 'more') {
            const h = V.pc, d = dev.find(h) || {};
            POps.menu(b, [
                { label: 'Bilgisayar ayrıntıları', icon: 'info', onClick: () => { exitFull(); dev.open(h, { source: 'vision' }); } },
                CAN_ADMIN ? { label: 'Teşhis komutları', icon: 'terminal', onClick: openDiag } : null,
                { label: 'Oturum geçmişi', icon: 'clock', onClick: () => { exitFull(); openHistory(); } },
                CAN_ADMIN ? '-' : null,
                CAN_ADMIN && d.is_quarantined ? { label: 'Karantinayı kaldır', icon: 'unlock', onClick: () => { exitFull(); dev.unquarantine([h], { source: 'vision' }); } } : null,
                CAN_ADMIN && !d.is_quarantined ? { label: 'Karantinaya al', icon: 'lock', danger: true, onClick: () => { exitFull(); dev.quarantine([h], { source: 'vision' }); } } : null
            ]);
        }
    });

    // ---- Teşhis: görev kuyruğundan (Vision oturumu gerekmez; kimin gönderdiği kaydedilir), çıktı terminal_output ile döner
    const DIAG = {
        check_process: ['POps süreçleri', 'tasklist /FI "IMAGENAME eq POps*"'],
        check_logs: ['Son ajan günlüğü', `powershell.exe -NoProfile -Command "$log = Get-ChildItem 'C:\\POpsLogs\\*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1; if($log){ Get-Content $log.FullName -Tail 20 }else{ 'Log bulunamadi.' }"`],
        restart_capture: ['Ekran yakalamayı yeniden başlat', 'taskkill /F /IM POpsTray.exe'],
        sync_time: ['Saati eşitle', 'w32tm /resync'],
        restart_agent: ['Ajanı yeniden başlat', 'taskkill /F /IM POpsAgent.exe'],
        reboot_pc: ['Yeniden başlat', 'shutdown /r /t 5']
    };
    function diagLog(text) { const o = $('diagOut'); o.textContent += text; o.scrollTop = o.scrollHeight; }
    function openDiag() {
        if (!V.pc) return;
        exitFull();
        $('diagTitle').textContent = 'Teşhis · ' + dev.name(V.pc);
        $('diagOut').textContent = 'Konsol hazır. Çıktılar geldikçe burada görünür.';
        openModal('diagModal');
    }
    if ($('diagCmds')) $('diagCmds').addEventListener('click', async (e) => {
        const b = e.target.closest('[data-cmd]');
        if (!b || !V.pc) return;
        const cmd = b.dataset.cmd, item = DIAG[cmd], pc = V.pc, name = dev.name(pc);
        if (cmd === 'reboot_pc' && !await POps.confirm({ title: `${name} yeniden başlatılsın mı?`, message: '5 saniye içinde yeniden başlar; kaydedilmemiş işler kaybolabilir. Bağlantı kısa süre kopar.', confirmText: 'Yeniden başlat', danger: true, icon: 'fa-rotate-right' })) return;
        if (cmd === 'restart_agent' && !await POps.confirm({ title: `${name} üzerindeki ajan yeniden başlatılsın mı?`, message: 'Bağlantı birkaç saniye kopar; Windows hizmeti ajanı kendiliğinden yeniden açar.', confirmText: 'Ajanı yeniden başlat', icon: 'fa-rotate' })) return;
        diagLog(`\n\n> ${item[1]}\nKuyruğa eklendi, ajanın yanıtı bekleniyor…`);
        try {
            await POps.busy(b, () => POps.post('/api/deploy_orchestration', {
                target_mode: 'PC', targets: [pc], taskSequence: [{ name: 'Teşhis: ' + item[0], type: 'CMD', command: item[1] }],
                title: 'Teşhis: ' + item[0], source: 'vision'
            }, { jobTitle: 'Teşhis: ' + item[0] + ' · ' + name }));
            if (cmd === 'restart_agent' || cmd === 'reboot_pc') diagLog('\nBu komut çıktı döndürmez; ajan birkaç saniye içinde yeniden bağlanır.');
        } catch (err) { diagLog('\nGönderilemedi: ' + POps.errorMessage(err)); }
    });

    // ---- Oturum geçmişi (kim, ne zaman, ne kadar, gerekçe): cihaz başına /api/devices/{pc}/activity
    const HIST_MAX = 40;
    async function openHistory() {
        const hosts = V.pc ? [V.pc] : scopeDevices().filter(d => !d.missing).map(d => d.hostname);
        const scope = V.pc ? dev.name(V.pc) : (ui.pcs ? `Seçili ${hosts.length} bilgisayar` : ui.q ? `Süzgeçteki ${hosts.length} bilgisayar` : `${ui.lab === UN ? 'Atanmamış' : ui.lab} · ${hosts.length} bilgisayar`);
        const body = POps.drawer.open('vhist:' + hosts.join(',').slice(0, 200));
        const headHtml = `<div class="drawer-head"><div class="drawer-title"><span class="drawer-ico">${POps.iconHtml('clock', 'lg')}</span><div style="min-width:0"><h2>Oturum geçmişi</h2><div class="sub">${escapeHtml(scope)}</div></div></div>`
            + `<button type="button" class="ibtn sm" data-vact="close" data-tip="Kapat (Esc)" data-tip-pos="left" aria-label="Paneli kapat">${POps.iconHtml('x', 'sm')}</button></div>`;
        body.innerHTML = headHtml + '<div class="loading-state" role="status"><span class="spinner"></span>Yükleniyor…</div>';
        const key = POps.drawer.key();
        if (!hosts.length) { body.innerHTML = headHtml + '<div class="empty-state compact"><i class="fas fa-clock-rotate-left"></i><p>Bu kapsamda bilgisayar yok.</p></div>'; return; }
        const res = await Promise.allSettled(hosts.slice(0, HIST_MAX).map(h => POps.get('/api/devices/' + encodeURIComponent(h) + '/activity?limit=25').then(r => (r.items || []).filter(i => i.kind === 'vision').map(i => Object.assign({ pc: h }, i)))));
        if (POps.drawer.key() !== key) return;
        const items = res.filter(r => r.status === 'fulfilled').flatMap(r => r.value).sort((a, b) => String(b.at || '').localeCompare(String(a.at || ''))).slice(0, 60);
        const failed = res.filter(r => r.status === 'rejected').length;
        const rowsHtml = items.map(a => {
            const start = POps.toDate(a.at), end = POps.toDate(a.ended_at);
            const open = !end;
            const stale = open && start && Date.now() - start > 2 * 3600 * 1000;
            const k = open ? (stale ? 'warn' : 'run') : '';
            const w = open ? (stale ? 'Kapatılmadı' : 'Sürüyor') : 'Bitti';
            const dur = start && end ? POps.duration((end - start) / 1000) : '';
            const metaHtml = escapeHtml(a.by || '?') + ' · ' + POps.timeHtml(a.at) + (dur ? ' · ' + escapeHtml(dur) : '') + ' · ' + escapeHtml(a.mandatory ? 'zorunlu müdahale' : 'kullanıcıya soruldu');
            return `<div class="act"><div class="res${k ? ' ' + escapeHtml(k) : ''}">${POps.iconHtml('eye')}</div>
                <div style="min-width:0"><div class="what">${escapeHtml(dev.name(a.pc))}</div><div class="meta">${metaHtml}</div>
                ${a.reason ? `<div class="meta">Gerekçe: ${escapeHtml(a.reason)}</div>` : ''}
                ${stale ? '<div class="why warn">Oturum panelden kapatılmadı (sekme kapandı ya da bağlantı koptu); izleme yetkisi sunucuda süre dolunca düşer.</div>' : ''}</div>
                <div class="side"><span class="word${k ? ' ' + escapeHtml(k) : ''}" title="${escapeHtml(POps.fullTime(a.ended_at))}">${escapeHtml(w)}</span></div></div>`;
        }).join('');
        const noteHtml = (hosts.length > HIST_MAX ? `<div class="dr-note">İlk ${HIST_MAX} bilgisayarın kayıtları gösteriliyor.</div>` : '')
            + (failed ? `<div class="dr-note">${Number(failed)} bilgisayarın kaydı okunamadı.</div>` : '');
        body.innerHTML = headHtml + (items.length ? `<div>${rowsHtml}</div>` : '<div class="empty-state compact"><i class="fas fa-eye"></i><p>Bu kapsamda uzak ekran oturumu yok.</p></div>') + noteHtml
            + '<div class="dr-note" style="font-size:var(--text-xs);color:var(--text-muted)">Her oturum açan kişi, gerekçe ve süreyle denetim kaydına yazılır; bilgisayar başına son 25 kayıt.</div>';
    }
    $('histBtn').addEventListener('click', openHistory);
    document.addEventListener('click', (e) => {
        const b = e.target.closest('#popsDrawer [data-vact="close"]');
        if (b) POps.drawer.close();
    });

    // ---- Klavye: Esc ekranlara döner (kontrol açıkken Esc uzak bilgisayara gider)
    // Üstte bir katman (panel, pencere, menü) varken basılan Esc onu kapatır; durum, katmanlar kendini kapatmadan önce
    // (window yakalama evresinde) okunur
    const LAYERS = '.pops-dialog-overlay, .modal-overlay.open, .pops-menu, .palette-overlay';
    let escBusy = false;
    window.addEventListener('keydown', (e) => { if (e.key === 'Escape') escBusy = POps.drawer.isOpen() || !!document.querySelector(LAYERS); }, true);
    document.addEventListener('keydown', (e) => {
        if (e.key !== 'Escape' || e.defaultPrevented || !V.pc || V.ctrl || document.fullscreenElement) return;
        if (escBusy || POps.drawer.isOpen() || document.querySelector(LAYERS)) return;
        closeViewer();
    });
    // Sayfadan çıkarken açık oturum kapatılır (keepalive: sekme kapansa da istek gider)
    window.addEventListener('pagehide', () => { if (V.pc && (V.live || V.sid)) endSession(V.pc, V.sid, true); });

    // ---- Veri
    document.addEventListener('pops_data_updated', (e) => {
        if (e.detail && e.detail.error && !state.devicesLoaded) { POps.setError($('vsGrid'), e.detail.error); return; }
        render(false);
        if (V.pc) {
            renderViewerHead();
            const d = dev.find(V.pc);
            if (V.live && d && POps.isOffline(d)) { POps.toast('warning', `${POps.deviceName(d)} bağlantısı koptu; oturum kapatıldı.`); stopLive(); }
        }
    });
    async function loadModules() {
        try { const r = await POps.get('/api/modules'); visionMod = (r.modules || []).find(m => m.id === 'vision') || null; ui.hash = ''; render(false); }
        catch (e) { /* modül listesi okunamazsa hepsi açık sayılır; sunucu yine de denetler */ }
    }
    setInterval(() => { if (!document.hidden) { applyImages(); if (V.pc && !V.live) renderFoot(); } }, 30000);
    window.popsPoll(loadModules, 60000);
    loadModules();
    connectWS();
    POps.watchDevices({ inventory: true });
    if (state.devicesLoaded) render(true);
})();
</script>

<?php include 'includes/footer.php'; ?>
