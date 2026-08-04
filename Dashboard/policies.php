<?php
session_start();
if (!isset($_SESSION['username'])) { header('Location: login.php'); exit; }
$pageTitle = 'Adil Kullanım ve Politikalar';
$pageIcon = 'fa-shield-halved';
include 'includes/header.php';
?>
<style>
    .policy-container {
        display: grid;
        grid-template-columns: 1fr 1fr;
        gap: 2rem;
        max-width: 1400px;
        margin: 0 auto;
        padding: 1rem 0;
    }
    
    .policy-card {
        background: var(--bg-surface-1);
        border: 1px solid var(--border-default);
        border-radius: var(--radius-lg);
        padding: 2rem;
        box-shadow: var(--shadow-sm);
        display: flex;
        flex-direction: column;
    }
    
    .policy-card h2 {
        font-size: 1.25rem;
        margin-top: 0;
        margin-bottom: 1.5rem;
        color: var(--text-primary);
        display: flex;
        align-items: center;
        gap: 0.5rem;
    }
    
    .policy-section {
        margin-bottom: 2rem;
    }
    
    .policy-section label {
        display: block;
        margin-bottom: 0.5rem;
        color: var(--text-secondary);
        font-weight: 500;
    }
    
    .category-list {
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
    }
    
    .checkbox-item {
        display: flex;
        align-items: center;
        gap: 0.75rem;
        padding: 0.75rem 1rem;
        background: var(--bg-surface-2);
        border-radius: var(--radius-md);
        border: 1px solid var(--border-subtle);
        transition: all 0.2s;
    }
    
    .checkbox-item:hover {
        border-color: var(--primary-500);
        background: rgba(99, 102, 241, 0.05);
    }
    
    .checkbox-item input[type="checkbox"] {
        width: 1.25rem;
        height: 1.25rem;
        accent-color: var(--primary-500);
        cursor: pointer;
    }
    
    textarea.policy-text {
        width: 100%;
        height: 250px;
        background: var(--bg-surface-2);
        border: 1px solid var(--border-default);
        border-radius: var(--radius-md);
        padding: 1rem;
        color: var(--text-primary);
        font-family: var(--font-body);
        font-size: 0.95rem;
        resize: vertical;
        line-height: 1.5;
    }
    
    textarea.policy-text:focus {
        outline: none;
        border-color: var(--primary-500);
        box-shadow: 0 0 0 2px rgba(99,102,241,0.2);
    }
    
    .switch-group {
        display: flex;
        justify-content: space-between;
        align-items: center;
        padding: 1rem;
        background: var(--bg-surface-2);
        border-radius: var(--radius-md);
        border: 1px solid var(--border-subtle);
        margin-bottom: 1rem;
    }
    
    .action-bar {
        margin-top: auto;
        padding-top: 1.5rem;
        border-top: 1px solid var(--border-default);
        display: flex;
        justify-content: flex-end;
    }
    
    .info-box {
        background: rgba(6, 182, 212, 0.1);
        border-left: 3px solid #06b6d4;
        padding: 1rem;
        border-radius: 0 var(--radius-md) var(--radius-md) 0;
        margin-bottom: 1.5rem;
        font-size: 0.9rem;
        color: var(--text-secondary);
        line-height: 1.5;
    }
    
    @media (max-width: 1024px) {
        .policy-container { grid-template-columns: 1fr; }
    }
</style>

<div class="page-header">
    <div>
        <h1><i class="fas fa-shield-halved"></i> Adil Kullanım & Politikalar</h1>
        <p>Ağ bazlı içerik filtreleme kategorilerini ve son kullanıcı aydınlatma metnini yönetin.</p>
    </div>
</div>

<div class="policy-container">
    <div class="policy-card">
        <h2><i class="fas fa-filter" style="color:var(--primary-500);"></i> Web Filtreleme (DNS İzleme)</h2>
        <div class="info-box">
            <i class="fas fa-info-circle"></i> <strong>Sıfır Keylogger Prensibi:</strong> POps, klavye vuruşlarını veya şifreleri kaydetmez. İçerik filtreleme tamamen DNS ve ağ paketleri üzerinden (Davranışsal) gerçekleştirilir. Bu yöntem KVKK standartlarına tamamen uygundur.
        </div>
        
        <div class="policy-section">
            <label>İzlenecek ve Engellenecek Kategoriler</label>
            <div class="category-list" id="dnsCategoriesList">
                <label class="checkbox-item">
                    <input type="checkbox" value="pornografi" class="policy-cat-chk">
                    <div>
                        <div style="font-weight:600;color:var(--text-primary);">Pornografik İçerik</div>
                        <div style="font-size:0.75rem;color:var(--text-muted);">Yetişkinlere yönelik web siteleri ve materyaller</div>
                    </div>
                </label>
                <label class="checkbox-item">
                    <input type="checkbox" value="yasadisi_bahis" class="policy-cat-chk">
                    <div>
                        <div style="font-weight:600;color:var(--text-primary);">Yasadışı Bahis & Kumar</div>
                        <div style="font-size:0.75rem;color:var(--text-muted);">Lisanssız kumar ve bahis platformları</div>
                    </div>
                </label>
                <label class="checkbox-item">
                    <input type="checkbox" value="teror_siddet" class="policy-cat-chk">
                    <div>
                        <div style="font-weight:600;color:var(--text-primary);">Terör ve Şiddet Propagandası</div>
                        <div style="font-size:0.75rem;color:var(--text-muted);">Radikal oluşumlar ve şiddet içerikli platformlar</div>
                    </div>
                </label>
                <label class="checkbox-item">
                    <input type="checkbox" value="zararli_yazilim" class="policy-cat-chk">
                    <div>
                        <div style="font-weight:600;color:var(--text-primary);">Malware & Phishing</div>
                        <div style="font-size:0.75rem;color:var(--text-muted);">Zararlı yazılım ve kimlik avı (C2) domainleri</div>
                    </div>
                </label>
            </div>
        </div>

        <div class="policy-section">
            <label>Otomatik Karantina Aksiyonu</label>
            <div class="switch-group">
                <div>
                    <div style="font-weight:600;color:var(--text-primary);">İhlalde Karantinaya Al</div>
                    <div style="font-size:0.75rem;color:var(--text-muted);">Kullanıcı eşiği aştığında cihazı otomatik kilitler.</div>
                </div>
                <input type="checkbox" id="autoQuarantineSwitch" style="width:1.25rem;height:1.25rem;accent-color:var(--danger-500);">
            </div>
            
            <div>
                <label>Karantina Eşiği (İhlal Sayısı)</label>
                <input type="number" id="quarantineThreshold" min="1" max="20" value="3" style="width:100%;padding:0.75rem;background:var(--bg-surface-2);border:1px solid var(--border-default);color:var(--text-primary);border-radius:var(--radius-md);">
                <div style="font-size:0.75rem;color:var(--text-muted);margin-top:0.5rem;">Cihaz belirlenen ihlal sayısına ulaştığında Karantina protokolü devreye girer.</div>
            </div>
        </div>
    </div>

    <div class="policy-card">
        <h2><i class="fas fa-file-contract" style="color:var(--primary-500);"></i> Adil Kullanım & Şeffaflık</h2>
        <div class="info-box">
            <i class="fas fa-lightbulb"></i> Ajan yazılımı, cihaz açıldığında kullanıcıya bu metni göstererek rıza/onay alacaktır. Tam bir kurumsal şeffaflık politikası sunulur.
        </div>
        
        <div class="policy-section" style="flex-grow:1;">
            <label>Kullanıcı Aydınlatma Metni</label>
            <textarea class="policy-text" id="fairUseText" placeholder="Örn: Bu cihaz kurumumuz tarafından izlenmektedir..."></textarea>
        </div>
        
        <div class="policy-section">
            <div style="background:rgba(239, 68, 68, 0.1);border-left:3px solid #ef4444;padding:1rem;border-radius:0 var(--radius-md) var(--radius-md) 0;font-size:0.85rem;color:var(--text-secondary);">
                <i class="fas fa-exclamation-triangle" style="color:#ef4444;margin-right:0.5rem;"></i>
                <strong>Not (Ajan Durumu):</strong> Ajan kaynak kodlarına entegrasyon tamamlanana kadar (DNS modülü aktifleşene kadar) bu sayfa <strong>Hazır ama Pasif</strong> durumdadır. Konfigürasyonlar backend'e kaydedilir.
            </div>
        </div>

        <?php if(($_SESSION['role'] ?? '') !== 'viewer'): ?>
        <div class="action-bar">
            <button class="mystic-btn" id="savePoliciesBtn" onclick="savePolicies()">
                <i class="fas fa-save"></i> Politikaları Kaydet & Uygula
            </button>
        </div>
        <?php endif; ?>
    </div>
</div>

<script>
function getApiBase() { return (typeof OMYO_API !== 'undefined') ? OMYO_API.HTTP_URL : ''; }

async function fetchPolicies() {
    if (!getApiBase()) return;
    try {
        const res = await fetch(`${getApiBase()}/api/agent_policies`);
        if (res.ok) {
            const data = await res.json();
            document.getElementById('fairUseText').value = data.fair_use_text || '';
            document.getElementById('autoQuarantineSwitch').checked = data.auto_quarantine || false;
            document.getElementById('quarantineThreshold').value = data.quarantine_threshold || 3;
            
            const cats = data.dns_categories || [];
            document.querySelectorAll('.policy-cat-chk').forEach(chk => {
                chk.checked = cats.includes(chk.value);
            });
        }
    } catch (e) {
        console.warn('Politikalar çekilemedi.');
    }
}

async function savePolicies() {
    if (!getApiBase()) return;
    
    const btn = document.getElementById('savePoliciesBtn');
    btn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Kaydediliyor...';
    btn.disabled = true;
    
    const cats = [];
    document.querySelectorAll('.policy-cat-chk:checked').forEach(chk => cats.push(chk.value));
    
    const payload = {
        fair_use_text: document.getElementById('fairUseText').value,
        dns_categories: cats,
        auto_quarantine: document.getElementById('autoQuarantineSwitch').checked,
        quarantine_threshold: parseInt(document.getElementById('quarantineThreshold').value) || 3
    };
    
    try {
        const res = await fetch(`${getApiBase()}/api/agent_policies`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });
        
        if (res.ok) {
            showToast('Politikalar başarıyla kaydedildi.', 'success');
        } else {
            throw new Error('API Hatası');
        }
    } catch (e) {
        showToast('Kaydetme başarısız.', 'error');
    } finally {
        btn.innerHTML = '<i class="fas fa-save"></i> Politikaları Kaydet & Uygula';
        btn.disabled = false;
    }
}

document.addEventListener('DOMContentLoaded', () => {
    setTimeout(fetchPolicies, 500);
    
    if (window.USER_ROLE === 'viewer') {
        document.querySelectorAll('input, select, textarea').forEach(el => {
            el.disabled = true;
        });
    }
});
</script>

<?php include 'includes/footer.php'; ?>