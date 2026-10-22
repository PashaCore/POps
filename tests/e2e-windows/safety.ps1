# Runner'ı kurtaran temizlik: POps'un bütün güvenlik duvarı kuralları silinir, bekleyen kapatma iptal edilir, saat
# eşitlemesi yeniden açılır. Barındırılan runner'da bütün giden trafik ~90 sn kesilirse iş düşer; karantina ve sınav
# kuralları da POps sunucusu dışında her şeyi keser. Bu yüzden iki kipte çalışır:
#   safety.ps1                         hemen temizle (testin finally'si ve iş akışının always() adımı)
#   safety.ps1 -After 60 -Marker <f>   N sn sonra, işaret dosyası hâlâ duruyorsa servis durdurulup temizlenir
#                                      (her yalıtım penceresinden önce arka planda başlatılır; pencere zamanında
#                                      kapanınca test işareti siler ve bu kip hiçbir şey yapmaz)
param(
    [int] $After = 0,
    [string] $Marker = ""
)
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'

if ($After -gt 0) {
    Start-Sleep -Seconds $After
    if (-not $Marker -or -not (Test-Path -LiteralPath $Marker)) { exit 0 }
    Write-Host "GÜVENLİK: yalıtım penceresi $After sn içinde kapanmadı; servis durduruluyor, kurallar siliniyor."
    "timeout $(Get-Date -Format o)" | Set-Content -LiteralPath "$Marker.fired"
    # Servis kuralları yeniden kurmasın
    Stop-Service POpsAgent -Force -ErrorAction SilentlyContinue
}

$groups = @('POps Isolation', 'POps Exam', 'POps Peer Cache')
$rules = @(Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { ($groups -contains $_.Group) -or ($_.DisplayName -like 'POps_Isolation_*') })
if ($rules.Count -gt 0) {
    $rules | Remove-NetFirewallRule -ErrorAction SilentlyContinue
    Write-Host "GÜVENLİK: $($rules.Count) POps güvenlik duvarı kuralı silindi."
}
if ($After -gt 0 -and $Marker) { Remove-Item -LiteralPath $Marker -ErrorAction SilentlyContinue; exit 0 }

# Bekleyen kapatma (T3) iptal edilir; yoksa shutdown /a 1116 döner, sorun değil
& "$env:SystemRoot\System32\shutdown.exe" /a 2>$null | Out-Null
# Saat testi (T4) yarıda kaldıysa eşitleme yeniden açılır
foreach ($svc in 'vmictimesync', 'w32time') { Start-Service $svc -ErrorAction SilentlyContinue }
& w32tm /resync /force 2>$null | Out-Null
exit 0
