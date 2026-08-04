<?php include 'includes/header.php'; ?>

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
    .metric-card:hover {
        transform: translateY(-2px);
        box-shadow: 0 10px 28px rgba(0,0,0,0.06);
        border-color: var(--border-strong);
    }
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
    [data-theme="dark"] .quick-card.logger .qa-icon { color: #a78bfa; }

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
    @media (max-width: 720px) {
        .op-row { grid-template-columns: 56px 1fr; }
        .op-row .op-target { grid-column: 1 / -1; }
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
        <p><i class="fas fa-circle pulse pulse-dot" style="color:var(--success-solid);font-size:0.5rem;vertical-align:middle;"></i> Gerçek zamanlı ağ senkronizasyonu aktif</p>
    </div>
    <div class="page-header-actions">
        <span class="signal-pill online" id="serverSignal"><i class="fas fa-wifi pulse"></i> Çevrimiçi</span>
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
            <div class="dash-card-actions">
                <button class="btn secondary sm" onclick="window.clearTaskHistory()" title="Temizle"><i class="fas fa-trash"></i></button>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="adminCommandsList" class="scrollable">
                <div class="empty-mini"><i class="fas fa-circle-notch fa-spin"></i>Geçmiş yükleniyor...</div>
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
                <div class="empty-mini"><i class="fas fa-satellite-dish"></i>Sinyaller dinleniyor...</div>
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
                <div class="empty-mini"><i class="fas fa-code-branch"></i>Veri bekleniyor...</div>
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
                <div class="empty-mini"><i class="fas fa-tasks"></i>Veri bekleniyor...</div>
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
                <div class="empty-mini"><i class="fas fa-laptop"></i>Veri bekleniyor...</div>
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
                <a href="logger.php" class="btn secondary sm"><i class="fas fa-search"></i> İncele</a>
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
                <a href="deploy.php" class="btn secondary sm"><i class="fas fa-arrow-up-right-from-square"></i></a>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetRecentPackages" class="scrollable">
                <div class="empty-mini"><i class="fas fa-box-open"></i>Veri bekleniyor...</div>
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
                <a href="terminal.php" class="btn secondary sm"><i class="fas fa-arrow-up-right-from-square"></i></a>
            </div>
        </div>
        <div class="dash-card-body">
            <div id="widgetRecentScripts" class="scrollable">
                <div class="empty-mini"><i class="fas fa-terminal"></i>Veri bekleniyor...</div>
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
                <div class="empty-mini"><i class="fas fa-network-wired"></i>Veri bekleniyor...</div>
            </div>
        </div>
    </div>
</section>

<script>
document.addEventListener('DOMContentLoaded', () => {
    const apiBaseUrl = (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : "";
    
    let dashboardChart = null;
    let pcCountChart = null;
    let logSizeChart = null;
    let storageChart = null;
    
    let lastLogsHash = "";
    let lastHistoryHash = "";
    
    const pcHistoryData = [];
    const pcHistoryLabels = [];
    
    function formatBytes(bytes, decimals = 1) {
        if (bytes === 0) return '0 B';
        const k = 1024;
        const dm = decimals < 0 ? 0 : decimals;
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + ' ' + sizes[i];
    }

    function initChart() {
        // Doughnut Chart (Lab Dağılımı)
        const ctxDoughnut = document.getElementById('labDistributionChart');
        if (ctxDoughnut) {
            dashboardChart = new Chart(ctxDoughnut.getContext('2d'), {
                type: 'doughnut',
                data: {
                    labels: ['Veri Bekleniyor'],
                    datasets: [{ data: [1], backgroundColor: ['#e2e8f0'], borderWidth: 0 }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: { legend: { position: 'right', labels: { color: getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#475569', font: { size: 11 }, boxWidth: 12, padding: 8 } } },
                    cutout: '70%'
                }
            });
        }

        // Line Chart (PC Sayısı - Canlı)
        const ctxLine = document.getElementById('pcCountChart');
        if (ctxLine) {
            pcCountChart = new Chart(ctxLine.getContext('2d'), {
                type: 'line',
                data: {
                    labels: pcHistoryLabels,
                    datasets: [{
                        label: 'Toplam Cihaz',
                        data: pcHistoryData,
                        borderColor: '#10b981', // success color
                        backgroundColor: 'rgba(16, 185, 129, 0.1)',
                        borderWidth: 2,
                        fill: true,
                        tension: 0.4,
                        pointRadius: 0,
                        pointHoverRadius: 4
                    }]
                },
                options: {
                    responsive: true, 
                    maintainAspectRatio: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { display: false },
                        y: { 
                            beginAtZero: false, 
                            grid: { color: 'rgba(255,255,255,0.05)' },
                            ticks: { precision: 0, color: getComputedStyle(document.documentElement).getPropertyValue('--text-tertiary').trim() || '#94a3b8' }
                        }
                    }
                }
            });
        }

        // Bar Chart (Log Boyutu - 7 Days)
        const ctxLog = document.getElementById('logSizeChart');
        if (ctxLog) {
            logSizeChart = new Chart(ctxLog.getContext('2d'), {
                type: 'bar',
                data: {
                    labels: [],
                    datasets: [{
                        label: 'Log Kaydı',
                        data: [],
                        backgroundColor: 'rgba(59, 130, 246, 0.7)', // info color
                        borderWidth: 0,
                        borderRadius: 4
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: { legend: { display: false } },
                    scales: {
                        x: { grid: { display: false }, ticks: { color: getComputedStyle(document.documentElement).getPropertyValue('--text-tertiary').trim() || '#94a3b8' } },
                        y: { beginAtZero: true, grid: { color: 'rgba(255,255,255,0.05)' }, ticks: { precision: 0, color: getComputedStyle(document.documentElement).getPropertyValue('--text-tertiary').trim() || '#94a3b8' } }
                    }
                }
            });
        }

        // Doughnut Chart (Depolama Kullanımı)
        const ctxStorage = document.getElementById('storageChart');
        if (ctxStorage) {
            storageChart = new Chart(ctxStorage.getContext('2d'), {
                type: 'doughnut',
                data: {
                    labels: ['Ayrılmış Alan (Reserved)', 'Boş Alan (Empty)'],
                    datasets: [{
                        data: [0, 20],
                        backgroundColor: ['#f59e0b', '#334155'], // warning for used, slate for free
                        borderWidth: 0
                    }]
                },
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    plugins: { 
                        legend: { 
                            position: 'bottom', 
                            labels: { color: getComputedStyle(document.documentElement).getPropertyValue('--text-secondary').trim() || '#475569', font: { size: 11 }, boxWidth: 12, padding: 8 } 
                        } 
                    },
                    cutout: '70%'
                }
            });
        }
    }
    initChart();

    async function fetchCoreData() {
        if (!apiBaseUrl) return;
        try {
            const opts = { headers: { "Authorization": "Bearer " + sessionJwt } };
            const devReq = fetch(`${apiBaseUrl}/api/devices`, opts).catch(() => null);
            const taskReq = fetch(`${apiBaseUrl}/api/tasks?limit=50`, opts).catch(() => null);
            const storageReq = fetch(`${apiBaseUrl}/api/storage`, opts).catch(() => null);
            const pkgReq = fetch(`${apiBaseUrl}/api/packages`, opts).catch(() => null);
            
            const [devRes, taskRes, storageRes, pkgRes] = await Promise.all([devReq, taskReq, storageReq, pkgReq]);
            if (devRes && devRes.status === 401) {
                window.location.href = '/login.php';
                return;
            }
            if (!devRes || !devRes.ok) throw new Error("Ağ hatası");

            const devices = await devRes.json();
            const tasks = (taskRes && taskRes.ok) ? await taskRes.json() : [];
            const storageData = (storageRes && storageRes.ok) ? await storageRes.json() : null;
            const packages = (pkgRes && pkgRes.ok) ? await pkgRes.json() : [];

            // Top Stats
            document.getElementById('dashTotal').innerText = devices.length;
            document.getElementById('dashActive').innerText = devices.filter(d => (d.status || '').toLowerCase() === 'online').length;
            document.getElementById('dashTasks').innerText = tasks.filter(t => t.status === 'Running' || t.status === 'Pending').length;
            document.getElementById('dashServerStatus').innerHTML = '<span class="signal-pill online"><i class="fas fa-wifi pulse"></i> Çevrimiçi</span>';

            // Process Widgets Data
            renderWidgets(devices, tasks, storageData, packages);
            
            // Admin History (Left Column)
            renderAdminHistory(tasks);
        } catch (e) {
            const ss = document.getElementById('dashServerStatus');
            if (ss) ss.innerHTML = '<span class="signal-pill offline"><i class="fas fa-triangle-exclamation"></i> Çevrimdışı</span>';
        }
    }

    function renderWidgets(devices, tasks, storageData, packages) {
        // 1. Son 5 Cihaz
        const allDevices = Array.isArray(devices) ? devices : [];
        const recentDevices = allDevices.slice(-5).reverse();
        
        const rdHtml = recentDevices.length > 0 ? recentDevices.map(d => {
            const name = d.pc_name || d.ip || d.hw_id || 'Bilinmeyen Cihaz';
            const isOnline = (d.status||'').toLowerCase() === 'online';
            return `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-laptop"></i></span>
                <div class="lr-main">
                    <span class="t">${escapeHtml(name)}</span>
                    <span class="m">${escapeHtml(d.ip || d.hw_id || '')}</span>
                </div>
                <span class="lr-end"><span class="signal-pill ${isOnline ? 'online' : 'offline'}">${isOnline ? 'Online' : 'Offline'}</span></span>
            </div>`;
        }).join('') : '<div class="empty-mini"><i class="fas fa-laptop"></i>Cihaz bulunamadı</div>';
        
        document.getElementById('widgetRecentDevices').innerHTML = rdHtml;

        // 2. Aktif Laboratuvarlar & Grafik Dağılımı
        const labCounts = {};
        allDevices.forEach(d => { const lab = d.lab || 'Atanmamış'; labCounts[lab] = (labCounts[lab] || 0) + 1; });
        const sortedLabs = Object.entries(labCounts).sort((a,b) => b[1] - a[1]);
        
        const tlHtml = sortedLabs.slice(0, 5).length > 0 ? sortedLabs.slice(0, 5).map((l, idx) => `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-network-wired"></i></span>
                <div class="lr-main">
                    <span class="t">${escapeHtml(l[0])}</span>
                    <span class="m">${idx === 0 ? 'En yoğun lab' : 'Aktif laboratuvar'}</span>
                </div>
                <span class="lr-end">${l[1]} cihaz</span>
            </div>
        `).join('') : '<div class="empty-mini"><i class="fas fa-network-wired"></i>Lab bulunamadı</div>';
        document.getElementById('widgetTopLabs').innerHTML = tlHtml;

        // Grafiği de güncelle (Doughnut)
        const labels = Object.keys(labCounts);
        const data = Object.values(labCounts);
        const colors = ['#2563eb', '#10b981', '#f59e0b', '#8b5cf6', '#ec4899', '#06b6d4', '#f43f5e', '#84cc16'];
        if (labels.length > 0) {
            const currentChartHash = labels.join('|') + data.join('|');
            if (dashboardChart && dashboardChart.canvas.dataset.hash !== currentChartHash) {
                dashboardChart.data.labels = labels;
                dashboardChart.data.datasets[0].data = data;
                dashboardChart.data.datasets[0].backgroundColor = colors.slice(0, labels.length);
                dashboardChart.update();
                dashboardChart.canvas.dataset.hash = currentChartHash;
            }
        }

        // Kripto Tarzı Canlı PC Grafiğini Güncelle (Line Chart)
        const now = new Date();
        const timeLabel = now.getHours() + ':' + String(now.getMinutes()).padStart(2, '0') + ':' + String(now.getSeconds()).padStart(2, '0');
        pcHistoryLabels.push(timeLabel);
        pcHistoryData.push(allDevices.length);
        if (pcHistoryLabels.length > 30) {
            pcHistoryLabels.shift();
            pcHistoryData.shift();
        }
        if (pcCountChart) {
            const minVal = Math.min(...pcHistoryData);
            const maxVal = Math.max(...pcHistoryData);
            if (minVal === maxVal && minVal > 0) {
                pcCountChart.options.scales.y.min = minVal - 1;
                pcCountChart.options.scales.y.max = maxVal + 1;
            } else {
                delete pcCountChart.options.scales.y.min;
                delete pcCountChart.options.scales.y.max;
            }
            pcCountChart.update('none');
        }

        // 3. Ajan Sürüm Dağılımı
        const verCounts = {};
        allDevices.forEach(d => { const v = d.agent_version || 'Bilinmiyor'; verCounts[v] = (verCounts[v] || 0) + 1; });
        const sortedVers = Object.entries(verCounts).sort((a,b) => b[1] - a[1]).slice(0, 5);
        const vHtml = sortedVers.length > 0 ? sortedVers.map(v => {
            const rawVer = v[0];
            const displayVer = (rawVer.toLowerCase() === 'bilinmiyor' || rawVer.toLowerCase().startsWith('v')) ? escapeHtml(rawVer) : 'v' + escapeHtml(rawVer);
            return `
            <div class="list-row">
                <span class="lr-icon"><i class="fas fa-tag"></i></span>
                <div class="lr-main">
                    <span class="t">${displayVer}</span>
                    <span class="m">Ajan sürümü</span>
                </div>
                <span class="lr-end">${v[1]} cihaz</span>
            </div>`;
        }).join('') : '<div class="empty-mini"><i class="fas fa-tag"></i>Veri yok</div>';
        document.getElementById('widgetAgentVersions').innerHTML = vHtml;

        // 4. Son Aktif Görevler
        const activeTasks = tasks.filter(t => t.status === 'Running' || t.status === 'Pending').slice(0, 5);
        if (activeTasks.length === 0) {
            document.getElementById('widgetRecentTasks').innerHTML = '<div class="empty-mini"><i class="fas fa-tasks"></i>Aktif görev yok</div>';
        } else {
            document.getElementById('widgetRecentTasks').innerHTML = activeTasks.map(t => `
                <div class="list-row">
                    <span class="lr-icon"><i class="fas fa-terminal"></i></span>
                    <div class="lr-main">
                        <span class="t" title="${escapeHtml(t.script_path)}">${escapeHtml((t.script_path || '').split('/').pop() || t.script_path)}</span>
                        <span class="m">${escapeHtml(t.target_pc || t.target_lab || '')}</span>
                    </div>
                    <span class="lr-end" style="color:var(--warning-text);font-weight:600;">${escapeHtml(t.status)}</span>
                </div>
            `).join('');
        }

        // 5. Depolama ve Log
        if (storageData && storageData.status === 'success') {
            const badge = document.getElementById('logSizeDisplayBadge');
            if (badge) badge.innerText = formatBytes(storageData.log_bytes) + ' Yük';
            
            if (storageChart) {
                const usedGB = (storageData.used_bytes / (1024 * 1024 * 1024)).toFixed(2);
                const freeGB = (storageData.free_bytes / (1024 * 1024 * 1024)).toFixed(2);
                storageChart.data.datasets[0].data = [parseFloat(usedGB), parseFloat(freeGB)];
                storageChart.update();
            }
            
            if (logSizeChart && storageData.log_trend) {
                const trend = storageData.log_trend;
                const labels = trend.map(t => {
                    const parts = t.day.split('-');
                    return parts.length === 3 ? parts[2] + '/' + parts[1] : t.day;
                });
                const data = trend.map(t => t.count);
                
                const currentHash = labels.join('|') + data.join('|');
                if (logSizeChart.canvas.dataset.hash !== currentHash) {
                    logSizeChart.data.labels = labels;
                    logSizeChart.data.datasets[0].data = data;
                    logSizeChart.update();
                    logSizeChart.canvas.dataset.hash = currentHash;
                }
            }
        }
        
        // 6. Paket ve Betikler
        const pkgs = Array.isArray(packages) ? packages : [];
        const filesList = pkgs.filter(p => p.type === 'package').slice(-5).reverse();
        const scriptsList = pkgs.filter(p => p.type === 'script').slice(-5).reverse();
        
        const wPkg = document.getElementById('widgetRecentPackages');
        if (wPkg) {
            wPkg.innerHTML = filesList.length > 0 
                ? filesList.map(f => `
                    <div class="list-row">
                        <span class="lr-icon"><i class="fas fa-box"></i></span>
                        <div class="lr-main">
                            <span class="t" title="${escapeHtml(f.name)}">${escapeHtml(f.name)}</span>
                            <span class="m">${escapeHtml((f.meta || '').split('|')[1] || 'Paket')}</span>
                        </div>
                        <span class="lr-end">Paket</span>
                    </div>
                `).join('') 
                : '<div class="empty-mini"><i class="fas fa-box"></i>Dosya bulunamadı</div>';
        }
            
        const wScr = document.getElementById('widgetRecentScripts');
        if (wScr) {
            wScr.innerHTML = scriptsList.length > 0 
                ? scriptsList.map(s => `
                    <div class="list-row">
                        <span class="lr-icon"><i class="fas fa-terminal"></i></span>
                        <div class="lr-main">
                            <span class="t" title="${escapeHtml(s.name)}">${escapeHtml(s.name)}</span>
                            <span class="m">${escapeHtml((s.meta || '').split('|')[1] || 'Script')}</span>
                        </div>
                        <span class="lr-end" style="color:var(--warning-text);font-weight:600;">Betik</span>
                    </div>
                `).join('') 
                : '<div class="empty-mini"><i class="fas fa-terminal"></i>Betik bulunamadı</div>';
        }
    }

    function renderAdminHistory(tasks) {
        const list = document.getElementById('adminCommandsList');
        const currentHash = JSON.stringify(tasks.slice(0, 6));
        if (currentHash === lastHistoryHash && list.dataset.loaded) return;
        lastHistoryHash = currentHash;
        list.dataset.loaded = "1";

        if (!tasks || tasks.length === 0) {
            list.innerHTML = '<div class="empty-mini"><i class="fas fa-ghost"></i>Henüz kaydedilmiş bir operasyon yok.</div>';
            return;
        }

        const unique = [];
        const seen = new Set();
        for (let t of tasks) {
            const k = t.script_path + t.created_at;
            if (!seen.has(k)) { seen.add(k); unique.push(t); }
            if (unique.length >= 6) break;
        }

        list.innerHTML = unique.map(t => {
            const target = (t.target_lab && t.target_lab !== 'Atanmamis_Cihazlar') ? t.target_lab : t.target_pc;
            const time = t.created_at ? t.created_at.split(' ')[1] : '-';
            return `<div class="op-row">
                <span class="op-ts">${time}</span>
                <span class="op-target"><i class="fas fa-crosshairs"></i>${escapeHtml(target || '-')}</span>
                <span class="op-cmd" title="${escapeHtml(t.script_path)}">${escapeHtml(t.script_path)}</span>
            </div>`;
        }).join('');
    }

    window.clearTaskHistory = async function() {
        if (!confirm('Tüm aktif ve geçmiş görev kuyruğu silinecek. Emin misiniz?')) return;
        try {
            await fetch(apiBaseUrl + '/api/flush_queue', { method: 'POST', headers: { 'Authorization': 'Bearer ' + sessionJwt } });
            lastHistoryHash = "";
            fetchCoreData();
            showToast('Kuyruk temizlendi', 'success');
        } catch (e) { showToast('Sunucuya ulaşılamıyor.', 'error'); }
    };

    async function fetchAgentLogs() {
        if (!apiBaseUrl) return;
        try {
            const res = await fetch(apiBaseUrl + '/api/logs?limit=200', { headers: { 'Authorization': 'Bearer ' + sessionJwt } });
            if (!res.ok) return;
            const logs = await res.json();
            const important = logs.filter(l => l.log_type === 'Deploy' || l.log_type === 'Security' || l.log_type === 'Error');
            const grouped = {};
            important.forEach(log => {
                const timeKey = log.timestamp.substring(11, 16);
                const msgKey = log.message.substring(0, 20).trim();
                const k = timeKey + '_' + log.log_type + '_' + msgKey;
                if (!grouped[k]) grouped[k] = { time: timeKey, type: log.log_type, pcs: new Set(), msg: log.message };
                grouped[k].pcs.add(log.pc_name);
            });
            const currentHash = JSON.stringify(Object.keys(grouped).slice(0, 6));
            if (currentHash === lastLogsHash) return;
            lastLogsHash = currentHash;

            const list = document.getElementById('dashLogsList');
            const entries = Object.values(grouped).slice(0, 6);
            if (entries.length === 0) {
                list.innerHTML = '<div class="empty-mini"><i class="fas fa-satellite-dish"></i>Ajanlardan henüz önemli bir dönüş yok.</div>';
                return;
            }
            list.innerHTML = entries.map(g => {
                const isCrit = g.type === 'Security' || g.type === 'Error';
                const pill = isCrit ? `<span class="log-pill danger"><i class="fas fa-shield-halved"></i> ${g.type === 'Error' ? 'Hata' : 'Güvenlik'}</span>`
                                  : `<span class="log-pill success"><i class="fas fa-circle-check"></i> Sistem</span>`;
                return `<div class="op-row" style="grid-template-columns:60px auto 1fr;">
                    <span class="op-ts">${g.time}</span>
                    ${pill}
                    <span class="op-cmd" style="font-family:inherit;font-size:0.8125rem;"><strong style="color:var(--text-primary);">${g.pcs.size} cihaz</strong> — ${escapeHtml(g.msg.substring(0, 50))}${g.msg.length > 50 ? '…' : ''}</span>
                </div>`;
            }).join('');
        } catch (e) {}
    }

    function escapeHtml(unsafe) {
        if(!unsafe) return "";
        return unsafe.toString().replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#039;");
    }

    fetchCoreData();
    fetchAgentLogs();
    setInterval(() => { fetchCoreData(); fetchAgentLogs(); }, 1000);
});
</script>

<?php include 'includes/footer.php'; ?>
