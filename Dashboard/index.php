<?php include 'includes/header.php'; ?>
<?php $canAdmin = in_array($_SESSION['role'] ?? '', ['admin', 'superadmin'], true); ?>

<script src="https://cdn.jsdelivr.net/npm/chart.js"></script>

<style>
    /* ============ DASHBOARD v2 — PROFESYONEL KURUMSAL ============ */

    /* === Bölüm başlıkları === */
    .section-title {
        display: flex;
        align-items: center;
        gap: 10px;
        font-size: 0.8125rem;
        font-weight: 600;
        color: var(--text-secondary);
        text-transform: uppercase;
        letter-spacing: 0.08em;
        margin: 0 0 16px;
        padding-bottom: 12px;
        border-bottom: 1px solid var(--border-subtle);
    }
    .section-title .ico {
        width: 26px; height: 26px;
        border-radius: 7px;
        display: inline-flex; align-items: center; justify-content: center;
        font-size: 0.75rem;
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
    }
    .section-title .ico.gold  { color: var(--warning-solid); }
    .section-title .ico.blue  { color: var(--primary-500); }
    .section-title .ico.green { color: var(--success-500); }
    .section-title .ico.purple { color: #8b5cf6; }

    /* === ÜST METRİK KARTLARI === */
    .metric-grid {
        display: grid;
        grid-template-columns: repeat(4, 1fr);
        gap: 20px;
        margin-bottom: 32px;
    }
    .metric-card {
        position: relative;
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: 14px;
        padding: 22px 24px;
        display: flex;
        align-items: flex-start;
        gap: 18px;
        overflow: hidden;
        transition: transform .18s ease, box-shadow .18s ease, border-color .18s ease;
    }
    .metric-card:hover { border-color: var(--border-default); }
    .metric-card::before {
        content: "";
        position: absolute; left: 0; top: 0; bottom: 0;
        width: 3px;
        background: var(--primary-500);
    }
    .metric-card.mc-success::before { background: var(--success-500); }
    .metric-card.mc-warning::before { background: var(--warning-500); }
    .metric-card.mc-purple::before  { background: #8b5cf6; }

    .metric-icon {
        width: 46px; height: 46px;
        border-radius: 11px;
        display: flex; align-items: center; justify-content: center;
        font-size: 1.125rem;
        background: rgba(37, 99, 235, 0.10);
        color: var(--primary-500);
        flex-shrink: 0;
    }
    .metric-icon.mi-success { background: rgba(16, 185, 129, 0.10); color: var(--success-500); }
    .metric-icon.mi-warning { background: rgba(245, 158, 11, 0.10); color: var(--warning-500); }
    .metric-icon.mi-purple  { background: rgba(139, 92, 246, 0.10); color: #8b5cf6; }

    .metric-content { flex: 1; min-width: 0; }
    .metric-label {
        display: block;
        font-size: 0.6875rem;
        font-weight: 600;
        color: var(--text-tertiary);
        text-transform: uppercase;
        letter-spacing: 0.08em;
        margin-bottom: 6px;
    }
    .metric-value {
        display: block;
        font-size: 1.875rem;
        font-weight: 700;
        color: var(--text-primary);
        line-height: 1.1;
        font-feature-settings: "tnum";
        font-variant-numeric: tabular-nums;
    }
    .metric-sub {
        display: flex; align-items: center; gap: 6px;
        font-size: 0.75rem;
        color: var(--text-tertiary);
        margin-top: 6px;
    }
    .metric-sub .dot {
        width: 6px; height: 6px; border-radius: 50%;
        background: var(--text-tertiary);
    }
    .metric-sub .dot.live {
        background: var(--success-500);
        box-shadow: 0 0 0 0 rgba(16,185,129,.5);
        animation: pulseDot 1.6s infinite;
    }
    @keyframes pulseDot {
        0%   { box-shadow: 0 0 0 0 rgba(16,185,129,.5); }
        70%  { box-shadow: 0 0 0 8px rgba(16,185,129,0); }
        100% { box-shadow: 0 0 0 0 rgba(16,185,129,0); }
    }

    /* === HIZLI AKSİYONLAR === */
    .quick-actions {
        display: grid;
        grid-template-columns: repeat(5, 1fr);
        gap: 14px;
        margin-bottom: 32px;
    }
    .quick-card {
        position: relative;
        display: flex; align-items: center; gap: 14px;
        padding: 18px 20px;
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: 12px;
        text-decoration: none;
        color: var(--text-primary);
        transition: all .18s ease;
        overflow: hidden;
    }
    .quick-card::after {
        content: "\f061";
        font-family: "Font Awesome 6 Free";
        font-weight: 900;
        position: absolute;
        right: 16px; top: 50%;
        transform: translateY(-50%) translateX(-4px);
        opacity: 0;
        color: var(--text-tertiary);
        font-size: 0.75rem;
        transition: all .18s ease;
    }
    .quick-card:hover {
        border-color: var(--primary-500);
        transform: translateY(-2px);
        box-shadow: 0 8px 20px rgba(0,0,0,0.05);
    }
    .quick-card:hover::after {
        opacity: 1;
        transform: translateY(-50%) translateX(0);
    }
    .quick-card .qa-icon {
        width: 40px; height: 40px;
        border-radius: 10px;
        display: flex; align-items: center; justify-content: center;
        font-size: 1rem;
        flex-shrink: 0;
    }
    .quick-card .qa-text { min-width: 0; }
    .quick-card .qa-label {
        display: block;
        font-size: 0.875rem;
        font-weight: 600;
        color: var(--text-primary);
        line-height: 1.2;
    }
    .quick-card .qa-desc {
        display: block;
        font-size: 0.75rem;
        color: var(--text-tertiary);
        margin-top: 3px;
        line-height: 1.3;
    }
    .quick-card.vision .qa-icon  { background: rgba(239, 68, 68, 0.10);  color: var(--danger-text); }
    .quick-card.terminal .qa-icon{ background: rgba(59, 130, 246, 0.10); color: var(--info-text); }
    .quick-card.wake .qa-icon    { background: rgba(16, 185, 129, 0.10); color: var(--success-text); }
    .quick-card.deploy .qa-icon  { background: rgba(245, 158, 11, 0.10); color: var(--warning-text); }
    .quick-card.logger .qa-icon  { background: rgba(139, 92, 246, 0.10); color: #8b5cf6; }

    /* === ANA IZGARA (12-col) === */
    .dash-layout {
        display: grid;
        grid-template-columns: repeat(12, 1fr);
        gap: 20px;
        margin-bottom: 24px;
    }
    .span-8  { grid-column: span 8; }
    .span-6  { grid-column: span 6; }
    .span-4  { grid-column: span 4; }
    .span-12 { grid-column: span 12; }

    @media (max-width: 1180px) {
        .span-8 { grid-column: span 12; }
    }
    @media (max-width: 900px) {
        .span-4, .span-6 { grid-column: span 12; }
        .metric-grid { grid-template-columns: repeat(2, 1fr); }
        .quick-actions { grid-template-columns: repeat(2, 1fr); }
    }
    @media (max-width: 520px) {
        .metric-grid { grid-template-columns: 1fr; }
        .quick-actions { grid-template-columns: 1fr; }
    }

    /* === DASHBOARD KARTI (yenilenmiş) === */
    .dash-card {
        background: var(--bg-surface);
        border: 1px solid var(--border-subtle);
        border-radius: 14px;
        display: flex;
        flex-direction: column;
        overflow: hidden;
        min-height: 100%;
    }
    .dash-card-head {
        display: flex; align-items: center; justify-content: space-between;
        gap: 12px;
        padding: 16px 20px;
        border-bottom: 1px solid var(--border-subtle);
        background: var(--bg-surface);
    }
    .dash-card-title {
        display: flex; align-items: center; gap: 10px;
        font-size: 0.9375rem;
        font-weight: 600;
        color: var(--text-primary);
        min-width: 0;
    }
    .dash-card-title .ti {
        width: 28px; height: 28px;
        border-radius: 8px;
        display: inline-flex; align-items: center; justify-content: center;
        font-size: 0.8125rem;
        background: var(--bg-body);
        color: var(--text-secondary);
        flex-shrink: 0;
    }
    .dash-card-title .ti.t-info    { color: var(--info-text); background: rgba(59,130,246,0.10); }
    .dash-card-title .ti.t-success { color: var(--success-text); background: rgba(16,185,129,0.10); }
    .dash-card-title .ti.t-warning { color: var(--warning-text); background: rgba(245,158,11,0.10); }
    .dash-card-title .ti.t-primary { color: var(--primary-500); background: rgba(37,99,235,0.10); }
    .dash-card-actions { display: flex; gap: 8px; align-items: center; }
    .dash-card-body { flex: 1; }

    .badge-mini {
        display: inline-flex; align-items: center; gap: 4px;
        padding: 2px 8px;
        font-size: 0.6875rem;
        font-weight: 600;
        color: var(--info-text);
        background: var(--bg-surface-2);
        border: 1px solid var(--info-border);
        border-radius: 999px;
        font-family: var(--font-mono);
    }

    /* === LİST SATIRLARI === */
    .list-row {
        display: flex; align-items: center; gap: 12px;
        padding: 12px 20px;
        border-bottom: 1px solid var(--border-subtle);
        font-size: 0.875rem;
        transition: background .12s;
    }
    .list-row:last-child { border-bottom: none; }
    .list-row:hover { background: var(--bg-surface-2); }
    .list-row .lr-icon {
        width: 30px; height: 30px;
        border-radius: 8px;
        display: flex; align-items: center; justify-content: center;
        font-size: 0.75rem;
        background: var(--bg-body);
        color: var(--text-secondary);
        flex-shrink: 0;
    }
    .list-row .lr-main { flex: 1; min-width: 0; }
    .list-row .lr-main .t {
        display: block;
        font-weight: 500;
        color: var(--text-primary);
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .list-row .lr-main .m {
        display: block;
        font-size: 0.75rem;
        color: var(--text-tertiary);
        margin-top: 2px;
    }
    .list-row .lr-end {
        font-size: 0.75rem;
        color: var(--text-tertiary);
        font-family: var(--font-mono);
        flex-shrink: 0;
    }

    /* === OPERASYON GEÇMİŞİ (özel) === */
    .op-row {
        display: grid;
        grid-template-columns: 64px 180px 1fr;
        align-items: center;
        gap: 14px;
        padding: 12px 20px;
        border-bottom: 1px solid var(--border-subtle);
        font-size: 0.8125rem;
        transition: background .12s;
    }
    .op-row:last-child { border-bottom: none; }
    .op-row:hover { background: var(--bg-surface-2); }
    .op-row .op-ts {
        font-family: var(--font-mono);
        font-size: 0.75rem;
        color: var(--text-tertiary);
    }
    .op-row .op-target {
        display: flex; align-items: center; gap: 6px;
        font-weight: 600;
        color: var(--warning-text);
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .op-row .op-cmd {
        font-family: var(--font-mono);
        font-size: 0.75rem;
        color: var(--text-secondary);
        white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .op-row .op-cmd::before {
        content: "$";
        color: var(--text-tertiary);
        margin-right: 6px;
        font-weight: 700;
    }
    .op-row.signal-row { grid-template-columns: 48px auto 1fr; }
    .op-row .op-cmd.plain { font-family: inherit; font-size: 0.8125rem; white-space: normal; }
    .op-row .op-cmd.plain::before { content: none; }
    .op-row .op-cmd.plain strong { color: var(--text-primary); }
    .list-row .lr-main .t.mono { font-family: var(--font-mono); font-size: 0.8125rem; }
    @media (max-width: 720px) {
        .op-row { grid-template-columns: 56px 1fr; }
        .op-row .op-target { grid-column: 1 / -1; }
        .op-row.signal-row { grid-template-columns: 44px 1fr; }
        .op-row.signal-row .op-cmd { grid-column: 1 / -1; }
    }

    /* === Sinyal pill === */
    .signal-pill {
        display: inline-flex; align-items: center; gap: 0.375rem;
        padding: 0.25rem 0.625rem;
        border-radius: 999px;
        font-size: 0.6875rem;
        font-weight: 600;
        text-transform: uppercase;
        letter-spacing: 0.04em;
        background: var(--bg-surface-3); color: var(--text-tertiary); border: 1px solid var(--border-subtle);
    }
    .signal-pill.online  { background: var(--success-bg); color: var(--success-text); border: 1px solid var(--success-border); }
    .signal-pill.offline { background: var(--danger-bg);  color: var(--danger-text);  border: 1px solid var(--danger-border); }

    /* === Boş durum === */
    .empty-mini {
        padding: 36px 20px;
        text-align: center;
        color: var(--text-tertiary);
        font-size: 0.8125rem;
        display: flex; flex-direction: column; align-items: center; gap: 8px;
    }
    .empty-mini i { font-size: 1.5rem; opacity: 0.35; }

    /* === Log pill === */
    .log-pill {
        display: inline-flex; align-items: center; gap: 0.375rem;
        padding: 0.1875rem 0.5rem;
        border-radius: 6px;
        font-size: 0.6875rem;
        font-weight: 700;
        text-transform: uppercase;
        letter-spacing: 0.04em;
    }
    .log-pill.success { background: var(--success-bg); color: var(--success-text); }
    .log-pill.danger  { background: var(--danger-bg);  color: var(--danger-text); }
    .log-pill.warning { background: var(--warning-bg); color: var(--warning-text); }

    /* === Grafik kartı === */
    .chart-wrap { padding: 20px 24px; height: 280px; position: relative; }
    .charts-row {
        display: grid;
        grid-template-columns: 1.4fr 1fr;
        gap: 20px;
        margin-bottom: 24px;
    }
    .charts-row .chart-wrap { height: 260px; }

    .chart-card-grid {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 24px;
        padding: 8px 8px 8px 8px;
    }
    .chart-card-grid .chart-wrap { height: 240px; padding: 12px; }

    @media (max-width: 900px) {
        .charts-row, .chart-card-grid { grid-template-columns: 1fr; }
    }

    /* === küçük detay: kaydırılabilir alan === */
    .scrollable {
        max-height: 380px;
        overflow-y: auto;
    }
    .scrollable::-webkit-scrollbar { width: 6px; }
    .scrollable::-webkit-scrollbar-thumb { background: var(--border-subtle); border-radius: 3px; }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-house"></i> Sistem Özeti</h1>
        <p>Cihazlar, görevler ve sunucu durumu; 5 saniyede bir yenilenir</p>
    </div>
    <div class="page-header-actions">
        <span class="signal-pill" id="serverSignal"><span class="spinner sm"></span> Bağlanıyor</span>
    </div>
</div>

<!-- ÜST METRİKLER -->
<section class="metric-grid">
    <div class="metric-card">
        <div class="metric-icon"><i class="fas fa-desktop"></i></div>
        <div class="metric-content">
            <span class="metric-label">Toplam Ajan PC</span>
            <span class="metric-value" id="dashTotal">—</span>
            <span class="metric-sub"><span class="dot"></span> Ağa kayıtlı cihaz</span>
        </div>
    </div>

    <div class="metric-card mc-success">
        <div class="metric-icon mi-success"><i class="fas fa-circle-check"></i></div>
        <div class="metric-content">
            <span class="metric-label">Çevrimiçi</span>
            <span class="metric-value" id="dashActive" style="color:var(--success-text);">—</span>
            <span class="metric-sub"><span class="dot live"></span> Aktif sinyal alınıyor</span>
        </div>
    </div>

    <div class="metric-card mc-warning">
        <div class="metric-icon mi-warning"><i class="fas fa-hourglass-half"></i></div>
        <div class="metric-content">
            <span class="metric-label">Aktif Görev</span>
            <span class="metric-value" id="dashTasks" style="color:var(--warning-text);">—</span>
            <span class="metric-sub"><span class="dot"></span> Çalışan & bekleyen</span>
        </div>
    </div>

    <div class="metric-card mc-purple">
        <div class="metric-icon mi-purple"><i class="fas fa-satellite-dish"></i></div>
        <div class="metric-content">
            <span class="metric-label">Sunucu Sinyali</span>
            <span class="metric-value" id="dashServerStatus" style="font-size:0.875rem;color:var(--text-tertiary);font-weight:600;line-height:1.6;">Aranıyor...</span>
        </div>
    </div>
</section>

<?php if (($_SESSION['role'] ?? '') !== 'viewer'): ?>
<!-- HIZLI AKSİYONLAR -->
<h2 class="section-title"><span class="ico gold"><i class="fas fa-bolt"></i></span> Hızlı Aksiyonlar</h2>
<section class="quick-actions">
    <a href="vision.php" class="quick-card vision">
        <span class="qa-icon"><i class="fas fa-eye"></i></span>
        <span class="qa-text">
            <span class="qa-label">POpsVision</span>
            <span class="qa-desc">Ekranları canlı izle</span>
        </span>
    </a>
    <a href="terminal.php" class="quick-card terminal">
        <span class="qa-icon"><i class="fas fa-terminal"></i></span>
        <span class="qa-text">
            <span class="qa-label">Terminal</span>
            <span class="qa-desc">PC'lere komut gönder</span>
        </span>
    </a>
    <a href="labs.php" class="quick-card wake">
        <span class="qa-icon"><i class="fas fa-power-off"></i></span>
        <span class="qa-text">
            <span class="qa-label">Ağ Yönetimi</span>
            <span class="qa-desc">Cihaz yerleşimi & güç</span>
        </span>
    </a>
    <a href="deploy.php" class="quick-card deploy">
        <span class="qa-icon"><i class="fas fa-cloud-arrow-up"></i></span>
        <span class="qa-text">
            <span class="qa-label">Dosya Dağıtımı</span>
            <span class="qa-desc">Uygulama & script yolla</span>
        </span>
    </a>
    <a href="logger.php" class="quick-card logger">
        <span class="qa-icon"><i class="fas fa-clipboard-list"></i></span>
        <span class="qa-text">
            <span class="qa-label">Loglar</span>
            <span class="qa-desc">Geçmiş kayıtlar</span>
        </span>
    </a>
</section>
<?php endif; ?>

<!-- ANA IZGARA: operasyonlar (8) + sinyaller (4) -->
<h2 class="section-title"><span class="ico blue"><i class="fas fa-cubes"></i></span> Detaylı İstatistikler</h2>
<section class="dash-layout">

    <!-- Yönetici Operasyon Geçmişi — büyük kart -->
    <div class="dash-card span-8">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-info"><i class="fas fa-terminal"></i></span>
                Yönetici Operasyon Geçmişi
            </div>
            <?php if ($canAdmin): ?>
            <div class="dash-card-actions">
                <button type="button" class="btn ghost sm" id="clearHistoryBtn" title="Görev geçmişini temizle"><i class="fas fa-trash"></i> Temizle</button>
            </div>
            <?php endif; ?>
        </div>
        <div class="dash-card-body">
            <div id="adminCommandsList" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>

    <!-- Ajanlardan Gelen Son Sinyaller -->
    <div class="dash-card span-4">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-success"><i class="fas fa-satellite-dish pulse"></i></span>
                Son Sinyaller
            </div>
        </div>
        <div class="dash-card-body">
            <div id="dashLogsList" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>

    <!-- Ajan Sürümleri -->
    <div class="dash-card span-4">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-primary"><i class="fas fa-code-branch"></i></span>
                Ajan Sürümleri
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetAgentVersions" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>

    <!-- Görev Kuyruğu -->
    <div class="dash-card span-4">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-warning"><i class="fas fa-tasks"></i></span>
                Görev Kuyruğu
                <span class="badge-mini">Son 5</span>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetRecentTasks" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>

    <!-- Son Eklenen 5 Cihaz -->
    <div class="dash-card span-4">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-primary"><i class="fas fa-laptop"></i></span>
                Son Cihazlar
                <span class="badge-mini">5</span>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetRecentDevices" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>
</section>

<!-- GRAFİKLER -->
<h2 class="section-title"><span class="ico green"><i class="fas fa-chart-area"></i></span> Grafikler & Analitik</h2>
<section class="charts-row">
    <div class="dash-card">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-success"><i class="fas fa-chart-line"></i></span>
                Ağdaki PC Sayısı <span class="badge-mini">Canlı</span>
            </div>
        </div>
        <div class="chart-wrap"><canvas id="pcCountChart"></canvas></div>
    </div>
    <div class="dash-card">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-info"><i class="fas fa-database"></i></span>
                Log Boyutu
                <span id="logSizeDisplayBadge" class="badge-mini" style="margin-left:8px;">-- MB Yük</span>
            </div>
            <div class="dash-card-actions">
                <a href="logger.php" class="btn ghost sm"><i class="fas fa-magnifying-glass"></i> İncele</a>
            </div>
        </div>
        <div class="chart-wrap"><canvas id="logSizeChart"></canvas></div>
    </div>
</section>

<!-- Laboratuvar + Depolama — geniş kart -->
<section class="dash-layout" style="margin-bottom:24px;">
    <div class="dash-card span-12">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-primary"><i class="fas fa-chart-pie"></i></span>
                Laboratuvar Dağılımı & Depolama
                <span class="badge-mini">Kota 20 GB</span>
            </div>
        </div>
        <div class="chart-card-grid">
            <div class="chart-wrap"><canvas id="labDistributionChart"></canvas></div>
            <div class="chart-wrap"><canvas id="storageChart"></canvas></div>
        </div>
    </div>
</section>

<!-- ALT WIDGETLAR -->
<h2 class="section-title"><span class="ico purple"><i class="fas fa-box-archive"></i></span> Son Eklenen İçerikler</h2>
<section class="dash-layout">
    <div class="dash-card span-6">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-info"><i class="fas fa-box-open"></i></span>
                Son Dosyalar
                <span class="badge-mini">5</span>
            </div>
            <div class="dash-card-actions">
                <a href="deploy.php" class="btn ghost sm" title="Dosya Dağıtımı'na git"><i class="fas fa-arrow-up-right-from-square"></i> Tümü</a>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetRecentPackages" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>

    <div class="dash-card span-6">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-warning"><i class="fas fa-terminal"></i></span>
                Son Betikler
                <span class="badge-mini">5</span>
            </div>
            <div class="dash-card-actions">
                <a href="deploy.php" class="btn ghost sm" title="Dosya Dağıtımı'na git"><i class="fas fa-arrow-up-right-from-square"></i> Tümü</a>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetRecentScripts" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>
</section>

<section class="dash-layout" style="margin-bottom: 32px;">
    <div class="dash-card span-12">
        <div class="dash-card-head">
            <div class="dash-card-title">
                <span class="ti t-primary"><i class="fas fa-network-wired"></i></span>
                Aktif Laboratuvarlar
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetTopLabs" class="scrollable">
                <div class="skeleton-list" aria-hidden="true"><span class="skeleton skeleton-line"></span><span class="skeleton skeleton-line mid"></span><span class="skeleton skeleton-line short"></span></div>
            </div>
        </div>
    </div>
</section>


<script>
document.addEventListener('DOMContentLoaded', () => {
    let dashboardChart = null, pcCountChart = null, logSizeChart = null, storageChart = null;
    let lastLogsHash = '', lastHistoryHash = '';
    const pcHistoryData = [], pcHistoryLabels = [];
    let deviceNames = {};
    const css = (name, fallback) => getComputedStyle(document.documentElement).getPropertyValue(name).trim() || fallback;

    function formatBytes(bytes, decimals = 1) {
        if (!bytes) return '0 B';
        const k = 1024, sizes = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(decimals)) + ' ' + sizes[i];
    }

    function initChart() {
        if (typeof Chart === 'undefined') return;   // grafik kütüphanesi yüklenemediyse kartlar boş kalır, sayfa çalışır
        Chart.defaults.font.family = css('--font-sans', 'Inter, sans-serif');
        const ctxDoughnut = document.getElementById('labDistributionChart');
        if (ctxDoughnut) {
            dashboardChart = new Chart(ctxDoughnut.getContext('2d'), {
                type: 'doughnut',
                data: { labels: ['Veri bekleniyor'], datasets: [{ data: [1], backgroundColor: ['#e2e8f0'], borderWidth: 0 }] },
                options: { responsive: true, maintainAspectRatio: false, cutout: '70%',
                    plugins: { legend: { position: 'right', labels: { color: css('--text-secondary', '#475569'), font: { size: 11 }, boxWidth: 12, padding: 8 } } } }
            });
        }
        const ctxLine = document.getElementById('pcCountChart');
        if (ctxLine) {
            pcCountChart = new Chart(ctxLine.getContext('2d'), {
                type: 'line',
                data: { labels: pcHistoryLabels, datasets: [{ label: 'Toplam cihaz', data: pcHistoryData, borderColor: '#10b981', backgroundColor: 'rgba(16, 185, 129, 0.1)', borderWidth: 2, fill: true, tension: 0.4, pointRadius: 0, pointHoverRadius: 4 }] },
                options: { responsive: true, maintainAspectRatio: false, plugins: { legend: { display: false } },
                    scales: { x: { display: false }, y: { beginAtZero: false, grid: { color: css('--border-subtle', '#e5e7eb') }, ticks: { precision: 0, color: css('--text-tertiary', '#94a3b8') } } } }
            });
        }
        const ctxLog = document.getElementById('logSizeChart');
        if (ctxLog) {
            logSizeChart = new Chart(ctxLog.getContext('2d'), {
                type: 'bar',
                data: { labels: [], datasets: [{ label: 'Log kaydı', data: [], backgroundColor: 'rgba(59, 130, 246, 0.7)', borderWidth: 0, borderRadius: 4 }] },
                options: { responsive: true, maintainAspectRatio: false, plugins: { legend: { display: false } },
                    scales: { x: { grid: { display: false }, ticks: { color: css('--text-tertiary', '#94a3b8') } },
                              y: { beginAtZero: true, grid: { color: css('--border-subtle', '#e5e7eb') }, ticks: { precision: 0, color: css('--text-tertiary', '#94a3b8') } } } }
            });
        }
        const ctxStorage = document.getElementById('storageChart');
        if (ctxStorage) {
            storageChart = new Chart(ctxStorage.getContext('2d'), {
                type: 'doughnut',
                data: { labels: ['Kullanılan (GB)', 'Boş (GB)'], datasets: [{ data: [0, 20], backgroundColor: ['#f59e0b', '#e5e7eb'], borderWidth: 0 }] },
                options: { responsive: true, maintainAspectRatio: false, cutout: '70%',
                    plugins: { legend: { position: 'bottom', labels: { color: css('--text-secondary', '#475569'), font: { size: 11 }, boxWidth: 12, padding: 8 } } } }
            });
        }
    }
    initChart();

    function setServerState(online) {
        const pill = online ? '<span class="signal-pill online"><i class="fas fa-wifi"></i> Çevrimiçi</span>'
                            : '<span class="signal-pill offline"><i class="fas fa-triangle-exclamation"></i> Bağlantı yok</span>';
        document.getElementById('dashServerStatus').innerHTML = pill;
        document.getElementById('serverSignal').outerHTML = online
            ? '<span class="signal-pill online" id="serverSignal"><i class="fas fa-wifi"></i> Çevrimiçi</span>'
            : '<span class="signal-pill offline" id="serverSignal"><i class="fas fa-triangle-exclamation"></i> Bağlantı yok</span>';
    }

    // 5 sn'de bir: cihazlar ve görevler
    async function fetchCoreData() {
        let devices, tasks;
        try {
            [devices, tasks] = await Promise.all([POps.get('/api/devices'), POps.get('/api/tasks?limit=50').catch(() => [])]);
        } catch (e) {
            setServerState(false);
            throw e;
        }
        devices = Array.isArray(devices) ? devices : [];
        tasks = Array.isArray(tasks) ? tasks : [];
        deviceNames = {};
        devices.forEach(d => { deviceNames[d.hostname] = POps.deviceName(d); });
        document.getElementById('dashTotal').textContent = devices.length;
        document.getElementById('dashActive').textContent = devices.filter(POps.isOnline).length;
        document.getElementById('dashTasks').textContent = tasks.filter(t => t.status === 'Running' || t.status === 'Pending').length;
        setServerState(true);
        renderDevices(devices);
        renderTasks(tasks);
        renderAdminHistory(tasks);
    }

    function renderDevices(allDevices) {
        const recentDevices = allDevices.slice(-5).reverse();
        document.getElementById('widgetRecentDevices').innerHTML = recentDevices.length ? recentDevices.map(d => {
            const isOnline = POps.isOnline(d);
            return `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-laptop"></i></span>
                <div class="lr-main">
                    <span class="t">${escapeHtml(POps.deviceName(d))}</span>
                    <span class="m">${escapeHtml(d.ip || d.hw_id || '')}</span>
                </div>
                <span class="lr-end"><span class="signal-pill ${isOnline ? 'online' : 'offline'}">${isOnline ? 'Açık' : 'Kapalı'}</span></span>
            </div>`;
        }).join('') : '<div class="empty-mini"><i class="fas fa-laptop"></i>Henüz kayıtlı cihaz yok</div>';

        const labCounts = {};
        allDevices.forEach(d => { const lab = d.lab && d.lab !== 'Atanmamis_Cihazlar' ? d.lab : 'Atanmamış'; labCounts[lab] = (labCounts[lab] || 0) + 1; });
        const sortedLabs = Object.entries(labCounts).sort((a, b) => b[1] - a[1]);
        document.getElementById('widgetTopLabs').innerHTML = sortedLabs.length ? sortedLabs.slice(0, 5).map((l, idx) => `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-network-wired"></i></span>
                <div class="lr-main">
                    <span class="t">${escapeHtml(l[0])}</span>
                    <span class="m">${idx === 0 ? 'En kalabalık sınıf' : 'Sınıf'}</span>
                </div>
                <span class="lr-end">${Number(l[1])} cihaz</span>
            </div>`).join('') : '<div class="empty-mini"><i class="fas fa-network-wired"></i>Sınıf yok</div>';

        const labels = Object.keys(labCounts), data = Object.values(labCounts);
        const colors = ['#2563eb', '#10b981', '#f59e0b', '#8b5cf6', '#ec4899', '#06b6d4', '#f43f5e', '#84cc16'];
        if (dashboardChart && labels.length) {
            const hash = labels.join('|') + data.join('|');
            if (dashboardChart.canvas.dataset.hash !== hash) {
                dashboardChart.data.labels = labels;
                dashboardChart.data.datasets[0].data = data;
                dashboardChart.data.datasets[0].backgroundColor = labels.map((_, i) => colors[i % colors.length]);
                dashboardChart.update();
                dashboardChart.canvas.dataset.hash = hash;
            }
        }

        const now = new Date();
        pcHistoryLabels.push(now.toLocaleTimeString('tr-TR'));
        pcHistoryData.push(allDevices.length);
        if (pcHistoryLabels.length > 30) { pcHistoryLabels.shift(); pcHistoryData.shift(); }
        if (pcCountChart) {
            const minVal = Math.min(...pcHistoryData), maxVal = Math.max(...pcHistoryData);
            if (minVal === maxVal) { pcCountChart.options.scales.y.min = Math.max(0, minVal - 1); pcCountChart.options.scales.y.max = maxVal + 1; }
            else { delete pcCountChart.options.scales.y.min; delete pcCountChart.options.scales.y.max; }
            pcCountChart.update('none');
        }

        const verCounts = {};
        allDevices.forEach(d => { const v = d.agent_version || 'Bilinmiyor'; verCounts[v] = (verCounts[v] || 0) + 1; });
        const sortedVers = Object.entries(verCounts).sort((a, b) => b[1] - a[1]).slice(0, 5);
        document.getElementById('widgetAgentVersions').innerHTML = sortedVers.length ? sortedVers.map(v => {
            const raw = String(v[0]);
            const shown = (raw.toLowerCase() === 'bilinmiyor' || /^v/i.test(raw)) ? raw : 'v' + raw;
            return `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-tag"></i></span>
                <div class="lr-main"><span class="t">${escapeHtml(shown)}</span><span class="m">Ajan sürümü</span></div>
                <span class="lr-end">${Number(v[1])} cihaz</span>
            </div>`;
        }).join('') : '<div class="empty-mini"><i class="fas fa-tag"></i>Veri yok</div>';
    }

    function renderTasks(tasks) {
        const active = tasks.filter(t => t.status === 'Running' || t.status === 'Pending').slice(0, 5);
        document.getElementById('widgetRecentTasks').innerHTML = active.length ? active.map(t => `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-terminal"></i></span>
                <div class="lr-main">
                    <span class="t mono" title="${escapeHtml(t.script_path)}">${escapeHtml(String(t.script_path || '').slice(0, 60))}</span>
                    <span class="m">${escapeHtml(deviceNames[t.target_pc] || t.target_pc || '')}${t.target_lab && t.target_lab !== 'Atanmamis_Cihazlar' ? ' · ' + escapeHtml(t.target_lab) : ''}</span>
                </div>
                <span class="lr-end" style="color:var(--warning-text);font-weight:600;">${t.status === 'Running' ? 'Çalışıyor' : 'Sırada'}</span>
            </div>`).join('') : '<div class="empty-mini"><i class="fas fa-circle-check"></i>Bekleyen ya da çalışan görev yok</div>';
    }

    function renderAdminHistory(tasks) {
        const list = document.getElementById('adminCommandsList');
        const hash = JSON.stringify(tasks.slice(0, 6));
        if (hash === lastHistoryHash) return;
        lastHistoryHash = hash;
        if (!tasks.length) {
            list.innerHTML = '<div class="empty-mini"><i class="fas fa-clock-rotate-left"></i>Henüz kaydedilmiş bir işlem yok.</div>';
            return;
        }
        const unique = [], seen = new Set();
        for (const t of tasks) {
            const k = t.script_path + t.created_at;
            if (!seen.has(k)) { seen.add(k); unique.push(t); }
            if (unique.length >= 6) break;
        }
        list.innerHTML = unique.map(t => {
            const target = (t.target_lab && t.target_lab !== 'Atanmamis_Cihazlar') ? t.target_lab : (deviceNames[t.target_pc] || t.target_pc);
            const time = t.created_at ? String(t.created_at).split(' ')[1] || t.created_at : '-';
            return `<div class="op-row">
                <span class="op-ts">${escapeHtml(time)}</span>
                <span class="op-target"><i class="fas fa-crosshairs"></i>${escapeHtml(target || '-')}</span>
                <span class="op-cmd" title="${escapeHtml(t.script_path)}">${escapeHtml(t.script_path)}</span>
            </div>`;
        }).join('');
    }

    // 30 sn'de bir: depolama, paketler ve önemli ajan olayları (ağır sorgular sık çekilmez)
    async function fetchExtras() {
        const [storageData, packages, logs] = await Promise.all([
            POps.get('/api/storage').catch(() => null),
            POps.get('/api/packages').catch(() => []),
            POps.get('/api/logs?limit=200').catch(() => null)
        ]);
        if (storageData && storageData.status === 'success') {
            document.getElementById('logSizeDisplayBadge').textContent = formatBytes(storageData.log_bytes);
            if (storageChart) {
                storageChart.data.datasets[0].data = [
                    parseFloat((storageData.used_bytes / 1073741824).toFixed(2)),
                    parseFloat((storageData.free_bytes / 1073741824).toFixed(2))
                ];
                storageChart.update();
            }
            if (logSizeChart && Array.isArray(storageData.log_trend)) {
                const labels = storageData.log_trend.map(t => { const p = String(t.day).split('-'); return p.length === 3 ? p[2] + '/' + p[1] : t.day; });
                const data = storageData.log_trend.map(t => t.count);
                const hash = labels.join('|') + data.join('|');
                if (logSizeChart.canvas.dataset.hash !== hash) {
                    logSizeChart.data.labels = labels;
                    logSizeChart.data.datasets[0].data = data;
                    logSizeChart.update();
                    logSizeChart.canvas.dataset.hash = hash;
                }
            }
        }
        const pkgs = Array.isArray(packages) ? packages : [];
        const filesList = pkgs.filter(p => p.type === 'package').slice(-5).reverse();
        const scriptsList = pkgs.filter(p => p.type === 'script').slice(-5).reverse();
        document.getElementById('widgetRecentPackages').innerHTML = filesList.length ? filesList.map(f => `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-box"></i></span>
                <div class="lr-main"><span class="t" title="${escapeHtml(f.name)}">${escapeHtml(f.name)}</span><span class="m">${escapeHtml(String(f.meta || '').split('|')[1] || 'Paket')}</span></div>
                <span class="lr-end">Paket</span>
            </div>`).join('') : '<div class="empty-mini"><i class="fas fa-box"></i>Depoda paket yok</div>';
        document.getElementById('widgetRecentScripts').innerHTML = scriptsList.length ? scriptsList.map(s => `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-terminal"></i></span>
                <div class="lr-main"><span class="t" title="${escapeHtml(s.name)}">${escapeHtml(s.name)}</span><span class="m">${escapeHtml(String(s.meta || '').split('|')[1] || 'Betik')}</span></div>
                <span class="lr-end" style="color:var(--warning-text);font-weight:600;">Betik</span>
            </div>`).join('') : '<div class="empty-mini"><i class="fas fa-terminal"></i>Depoda betik yok</div>';
        if (Array.isArray(logs)) renderSignals(logs);
    }

    function signalPill(kind) {
        if (kind === 'critical') return '<span class="log-pill danger"><i class="fas fa-shield-halved"></i> Kritik</span>';
        if (kind === 'high') return '<span class="log-pill danger"><i class="fas fa-shield-halved"></i> Yüksek</span>';
        if (kind === 'error') return '<span class="log-pill danger"><i class="fas fa-shield-halved"></i> Hata</span>';
        if (kind === 'medium' || kind === 'security') return '<span class="log-pill warning"><i class="fas fa-triangle-exclamation"></i> Orta</span>';
        return '<span class="log-pill success"><i class="fas fa-circle-check"></i> Görev</span>';
    }

    // Önemli ajan olayları: orta/yüksek/kritik riskliler ve görev çalıştırmaları (eski şemada log_type)
    function renderSignals(logs) {
        const important = logs.filter(l => {
            const risk = String(l.risk_level || '').toLowerCase();
            if (risk) return risk !== 'info' || l.category === 'system_maintenance';
            return ['Deploy', 'Security', 'Error'].includes(l.log_type);
        });
        const grouped = {};
        important.forEach(log => {
            const time = String(log.timestamp || '').substring(11, 16);
            const kind = String(log.risk_level || log.log_type || '').toLowerCase();
            const k = time + '_' + kind + '_' + String(log.message || '').substring(0, 20);
            if (!grouped[k]) grouped[k] = { time, kind, pcs: new Set(), msg: String(log.message || '') };
            grouped[k].pcs.add(log.pc_name);
        });
        const entries = Object.values(grouped).slice(0, 6);
        const hash = JSON.stringify(entries.map(g => [g.time, g.kind, g.msg, g.pcs.size]));
        if (hash === lastLogsHash) return;
        lastLogsHash = hash;
        const list = document.getElementById('dashLogsList');
        if (!entries.length) {
            list.innerHTML = '<div class="empty-mini"><i class="fas fa-satellite-dish"></i>Son 200 kayıtta önemli bir olay yok.</div>';
            return;
        }
        list.innerHTML = entries.map(g => `<div class="op-row signal-row">
                <span class="op-ts">${escapeHtml(g.time)}</span>
                ${signalPill(g.kind)}
                <span class="op-cmd plain"><strong>${g.pcs.size} cihaz</strong> · ${escapeHtml(g.msg.substring(0, 60))}${g.msg.length > 60 ? '…' : ''}</span>
            </div>`).join('');
    }

    const clearBtn = document.getElementById('clearHistoryBtn');
    if (clearBtn) clearBtn.addEventListener('click', async () => {
        const ok = await POps.confirm({
            title: 'Görev geçmişi silinsin mi?',
            message: 'Sıradaki, çalışan ve biten bütün görev kayıtları silinir. Silme işlemi denetim kaydına yazılır; geri alınamaz.',
            confirmText: 'Tümünü sil', danger: true, icon: 'fa-trash'
        });
        if (!ok) return;
        if (await POps.act(clearBtn, () => POps.post('/api/flush_queue'), { success: 'Görev geçmişi temizlendi.' })) {
            lastHistoryHash = '';
            fetchCoreData().catch(() => {});
        }
    });

    fetchCoreData().catch(() => {});
    fetchExtras().catch(() => {});
    // Arka planda durur, boşta yavaşlar (bkz. header.php popsPoll)
    popsPoll(fetchCoreData, 5000);
    popsPoll(fetchExtras, 30000);
});
</script>

<?php include 'includes/footer.php'; ?>
