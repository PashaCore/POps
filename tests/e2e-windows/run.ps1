# POps Windows ajanının uçtan uca testi, barındırılan (atılacak) windows-latest runner'ında. 0.1.24 ajan
# düzeltmelerini (PR #196/#197) sahada doğrular: T3 güç, T4 sınav saati, T5 kaldırma, T6 sabitlenmiş CA, T7 eş
# önbelleği. Kurulum, kaldırma, güvenlik duvarı, saat ve servis işlemleri runner'da serbesttir; yine de runner asla
# gerçekten kapanmaz/yeniden başlamaz ve her yalıtım penceresi kısadır (giden trafik ~90 sn kesilirse iş düşer),
# finally ve arka plan safety.ps1 bütün POps güvenlik duvarı kurallarını kaldırır.
#
# Her adım PASS/FAIL/SKIP ile results.md'ye (artifact ve iş özeti) yazılır. Tek bir adımın hatası ötekileri düşürmez.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Python betiklerimiz Türkçe basar; runner'ın konsol kod sayfası cp1252, bu yüzden UTF-8'e zorla (yoksa "ı" gibi
# karakterlerde UnicodeEncodeError). Çocuk süreçler (certs.py, sign_release.py, backend, control.py) env'i miras alır.
$env:PYTHONUTF8 = "1"
$env:PYTHONIOENCODING = "utf-8"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$script:here = $PSScriptRoot
$script:repo = (Resolve-Path "$here\..\..").Path
$script:work = if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP "e2e" } else { Join-Path $env:TEMP "pops-e2e" }
# artifacts: yalnızca loglar ve sonuç (yüklenir); build: derleme çıktısı ve paketler (yüklenmez, ~300 MB)
$script:artifacts = Join-Path $script:work "artifacts"
$script:build = Join-Path $script:work "build"
$script:tls = Join-Path $script:work "tls"
$script:keys = Join-Path $script:work "keys"
$script:sign = Join-Path $script:work "sign"
$script:logs = "C:\POpsLogs"
$script:secure = "C:\POpsData\secure"
$script:port = 8443
$script:serverUrl = "https://localhost:$($script:port)"
$script:verA = "0.1.90-e2e"
$script:verB = "0.1.91-e2e"
$script:lab = "E2E-LAB"
$script:py = "python"
$script:backend = $null
$script:backendStarts = 0
$script:results = @()
$script:hw = $null
$script:seed = "HW-E2E-SEED"
New-Item -ItemType Directory -Force -Path $script:work, $script:artifacts, $script:build, $script:tls, $script:keys, $script:sign | Out-Null

function Say($m) { Write-Host "=== $m" }
function Record($name, $pass, $evidence) {
    # "$true -eq 'skip'" PowerShell'de doğrudur (sağ taraf bool'a çevrilir): önce türe bakılır
    $state = if ($pass -is [string] -and $pass -eq 'skip') { 'SKIP' } elseif ($pass) { 'PASS' } else { 'FAIL' }
    $script:results += [pscustomobject]@{ Step = $name; Result = $state; Evidence = $evidence }
    Write-Host ("[{0}] {1}: {2}" -f $state, $name, $evidence)
}

function Ctl { & $script:py (Join-Path $script:here "control.py") @args }
function CtlJson {
    $out = Ctl @args
    try { return ($out | Select-Object -Last 1 | ConvertFrom-Json) } catch { return [pscustomobject]@{ raw = ($out -join "`n") } }
}

function AgentLogText {
    $f = Get-ChildItem "$($script:logs)\POps_*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime
    if (-not $f) { return "" }
    return ($f | Get-Content -Raw -ErrorAction SilentlyContinue) -join "`n"
}

function LogMark { return (AgentLogText).Length }
# Yalnızca $since karakterinden SONRA yazılan satırlarda ara (önceki testlerin satırları yanıltmasın)
function WaitLogSince($pattern, $timeout, $since) {
    $deadline = (Get-Date).AddSeconds($timeout)
    while ((Get-Date) -lt $deadline) {
        $t = AgentLogText
        if ($t.Substring([Math]::Min($since, $t.Length)) -match $pattern) { return $true }
        Start-Sleep -Milliseconds 800
    }
    return $false
}
function WaitLog($pattern, $timeout) { return WaitLogSince $pattern $timeout 0 }

function PopsRules {
    $groups = @('POps Isolation', 'POps Exam', 'POps Peer Cache')
    return @(Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { ($groups -contains $_.Group) -or ($_.DisplayName -like 'POps_Isolation_*') })
}
function RuleCount($group) { return @(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue).Count }
function GroupCounts { return "Isolation=$(RuleCount 'POps Isolation') Exam=$(RuleCount 'POps Exam') PeerCache=$(RuleCount 'POps Peer Cache')" }
# MSI logundaki (UTF-16, pwsh BOM'dan çözer) "POps: ..." satırı; yoksa boş
function MsiPopsLine($text, $pattern) {
    $hit = (($text -split "`n") | Where-Object { $_ -match "POps: .*$pattern" } | Select-Object -First 1)
    if ($hit) { return ($hit.Trim() -replace '^.*?(POps: )', '$1') } else { return "" }
}

# Yalıtım penceresinden önce: işaret dosyası hâlâ duruyorsa N sn sonra servisi durdurup kuralları silen arka plan nöbeti
function StartSafety($seconds) {
    $marker = Join-Path $script:work ("iso-{0}.marker" -f ([guid]::NewGuid().ToString('N')))
    "window $(Get-Date -Format o)" | Set-Content -LiteralPath $marker
    $p = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList @(
        "-NoProfile", "-File", (Join-Path $script:here "safety.ps1"), "-After", "$seconds", "-Marker", $marker)
    return [pscustomobject]@{ Marker = $marker; Proc = $p }
}
function SafetyFired($s) { return [bool]($s -and (Test-Path "$($s.Marker).fired")) }
# Nöbeti kapatır; dönen: nöbet zaten tetiklendi mi (o zaman kuralları ürün değil nöbet silmiştir, adım FAIL sayılır)
function EndSafety($s) {
    if (-not $s) { return $false }
    Remove-Item -LiteralPath $s.Marker -ErrorAction SilentlyContinue
    $fired = SafetyFired $s
    if ($fired) { Write-Host "UYARI: güvenlik nöbeti tetiklendi (pencere zamanında kapanmadı)" }
    return $fired
}

# msiexec'i bekleyerek çalıştırır; yol ve özellikleri boşluksuz, Start-Process kendi tırnaklar (runner yolları boşluksuz)
function Msiexec($msArgs, $logName) {
    $log = Join-Path $script:artifacts $logName
    $p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList ($msArgs + @("/l*v", $log))
    return [pscustomobject]@{ Exit = $p.ExitCode; Log = $log; Text = (Get-Content -Raw $log -ErrorAction SilentlyContinue) }
}
function InstallA($enrollToken) {
    $a = Join-Path $script:build "pkg\A\POps-Agent-$($script:verA)-win-x64.msi"
    $msArgs = @("/i", $a, "/qn", "SERVER_URL=$($script:serverUrl)", "SERVER_CA_CERT=$($script:tls)\ca.pem")
    if ($enrollToken) { $msArgs += "ENROLL_TOKEN=$enrollToken" }
    return Msiexec $msArgs ("msi-install-A-{0}.log" -f (Get-Date -Format HHmmss))
}
function InstallBUpgrade {
    $b = Join-Path $script:build "pkg\B\POps-Agent-$($script:verB)-win-x64.msi"
    return Msiexec @("/i", $b, "/qn") ("msi-upgrade-B-{0}.log" -f (Get-Date -Format HHmmss))
}
function ProductCode {
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                      'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        $k = Get-ChildItem $root -ErrorAction SilentlyContinue |
            Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -eq 'POps Agent' } |
            Select-Object -First 1
        if ($k) { return $k.PSChildName }   # {ProductCode GUID}
    }
    return $null
}
function UninstallAgent {
    $code = ProductCode
    if ($code) { return Msiexec @("/x", $code, "/qn") ("msi-uninstall-{0}.log" -f (Get-Date -Format HHmmss)) }
    throw "POps Agent ürün kodu bulunamadı (kaldırılamadı)"
}
function AgentInstalled {
    return [bool](Get-Service POpsAgent -ErrorAction SilentlyContinue)
}
# Ajan backend'e (yeniden) bağlanana kadar bekle: her backend yeniden başlatmasından sonra bir sonraki deploy/lockdown
# öncesi ajan çevrimiçi olmalı (yoksa hedef çevrimdışı sayılır)
function WaitAgentOnline($timeout = 60) {
    $d = CtlJson wait-device --online --timeout $timeout --exclude $script:seed
    return [bool]$d.hw_id
}

function StartBackend {
    $env:DB_HOST = "127.0.0.1"; $env:DB_PORT = "5432"; $env:DB_USER = "postgres"; $env:DB_PASS = "root"; $env:DB_NAME = "pops_e2e"
    $env:JWT_SECRET = "e2e-windows-secret-0123456789abcdef"
    $env:CORS_ALLOWED_ORIGINS = ""
    $env:PEER_CACHE_SEED_TIMEOUT_SECONDS = "8"
    $env:POPS_VERSION = $script:verA
    $env:POPS_IT_URL = $script:serverUrl
    $env:POPS_IT_CA = "$($script:tls)\ca.pem"
    # Her başlatmanın logu ayrı dosyada (Start-Process yönlendirmesi dosyanın üzerine yazar)
    $script:backendStarts++
    $out = Join-Path $script:work ("backend-{0}-stdout.log" -f $script:backendStarts)
    $script:backendLog = Join-Path $script:work ("backend-{0}.log" -f $script:backendStarts)
    $script:backend = Start-Process $script:py -PassThru -WorkingDirectory (Join-Path $script:repo "Backend") `
        -RedirectStandardOutput $out -RedirectStandardError $script:backendLog -WindowStyle Hidden `
        -ArgumentList @("-m", "uvicorn", "server:app", "--host", "127.0.0.1", "--port", "$($script:port)",
            "--ssl-certfile", "$($script:tls)\server.crt", "--ssl-keyfile", "$($script:tls)\server.key")
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        try {
            $r = Invoke-WebRequest "$($script:serverUrl)/api/health" -SkipCertificateCheck -TimeoutSec 5 -UseBasicParsing
            if ($r.StatusCode -eq 200) { return $true }
        } catch { Start-Sleep -Milliseconds 800 }
    }
    return $false
}
function StopBackend {
    if ($script:backend -and -not $script:backend.HasExited) {
        Stop-Process -Id $script:backend.Id -Force -ErrorAction SilentlyContinue
        try { Wait-Process -Id $script:backend.Id -Timeout 10 -ErrorAction SilentlyContinue } catch {}
    }
    $script:backend = $null
}

function Cleanup {
    Say "temizlik"
    try { & pwsh -NoProfile -File (Join-Path $script:here "safety.ps1") } catch {}
    try { if (AgentInstalled) { UninstallAgent | Out-Null } } catch {}
    StopBackend
    # Sonuç tablosu (kanıt kısaltılmadan) artifact'a ve iş özetine; loglar artifact'a
    $lines = @("## POps Windows agent end-to-end", "", "Run: $(Get-Date -Format o)", "",
        "| Step | Result | Evidence |", "|---|---|---|")
    foreach ($r in $script:results) { $lines += "| $($r.Step) | $($r.Result) | $((($r.Evidence -replace '\|', '/') -replace '\r?\n', ' ')) |" }
    $md = $lines -join "`n"
    $md | Set-Content -LiteralPath (Join-Path $script:artifacts "results.md") -Encoding utf8
    if ($env:GITHUB_STEP_SUMMARY) { $md | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8 }
    Copy-Item "$($script:logs)\POps_*.log" $script:artifacts -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $script:work "backend-*.log") $script:artifacts -ErrorAction SilentlyContinue
    Write-Host "`n$md"
}

# ── Hazırlık ────────────────────────────────────────────────────────────────────
function StartPostgres {
    $svc = Get-Service postgresql-x64-17 -ErrorAction SilentlyContinue
    if (-not $svc) { throw "postgresql-x64-17 servisi yok" }
    if ((Get-Service postgresql-x64-17).Status -eq 'Running') { return "zaten çalışıyor" }
    Set-Service postgresql-x64-17 -StartupType Manual -ErrorAction SilentlyContinue
    for ($i = 1; $i -le 4; $i++) {
        try { Start-Service postgresql-x64-17 -ErrorAction Stop; return "service" } catch { }
        if ((Get-Service postgresql-x64-17).Status -eq 'Running') { return "service" }
        Start-Sleep -Seconds 4
    }
    # Hâlâ başlamadıysa tanılama bas ve dur
    Write-Host "PG servisi başlamadı; tanılama:"
    Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-17'" |
        Format-List Name, State, StartName, PathName, StartMode | Out-String | Write-Host
    $pgdata = $env:PGDATA
    Write-Host "PGDATA=$pgdata var=$([bool](Test-Path $pgdata)) PG_VERSION=$([bool](Test-Path (Join-Path $pgdata 'PG_VERSION')))"
    Get-ChildItem (Join-Path $pgdata "log") -ErrorAction SilentlyContinue | Sort-Object LastWriteTime |
        Select-Object -Last 1 | Get-Content -Tail 40 -ErrorAction SilentlyContinue | Out-String | Write-Host
    throw "PostgreSQL başlatılamadı"
}

function Prepare {
    Say "PostgreSQL"
    StartPostgres | Out-Null
    $env:PGPASSWORD = "root"
    $psql = Join-Path $env:PGBIN "psql.exe"
    # Boş test veritabanı (migrate.py şemayı kurar)
    & $psql -U postgres -h 127.0.0.1 -c "DROP DATABASE IF EXISTS pops_e2e;" postgres 2>&1 | Out-Null
    & $psql -U postgres -h 127.0.0.1 -c "CREATE DATABASE pops_e2e;" postgres 2>&1 | Out-Null

    Say "test CA + sunucu sertifikası"
    & $script:py (Join-Path $script:here "certs.py") $script:tls
    if ($LASTEXITCODE -ne 0) { throw "sertifika üretilemedi" }

    Say "atılacak release imza anahtarı"
    $gen = & $script:py (Join-Path $script:repo "tools\sign_release.py") genkey --out-dir $script:keys
    $raw = ($gen | Select-String "ham 32 bayt").ToString().Split(":")[-1].Trim()
    if (-not $raw) { throw "ham açık anahtar alınamadı: $gen" }
    $script:rawPub = $raw
    # Backend bu açık anahtarı keys/'te arar (BASE_DIR/keys önce); gerçek anahtarın yerine test anahtarı
    New-Item -ItemType Directory -Force -Path (Join-Path $script:repo "Backend\keys") | Out-Null
    Copy-Item (Join-Path $script:keys "pops_release_ed25519.pub.pem") (Join-Path $script:repo "Backend\keys\pops_release_ed25519.pub.pem") -Force

    Say "ajan paketleri A ve B (aynı koddan; gömülü anahtar test anahtarı)"
    & (Join-Path $script:here "build.ps1") -OutDir $script:build -PublicKeyRawBase64 $script:rawPub -VersionA $script:verA -VersionB $script:verB
    if ($LASTEXITCODE -ne 0) { throw "ajan derlenemedi" }

    Say "B paketini imzala (ed25519) ve stage için hazırla"
    Copy-Item (Join-Path $script:build "pkg\B\POps-Agent-$($script:verB)-win-x64.msi") $script:sign -Force
    & $script:py (Join-Path $script:repo "tools\sign_release.py") sign --dir $script:sign --version $script:verB --tag "v$($script:verB)" --key-pem (Join-Path $script:keys "pops_release_ed25519.key.pem")
    if ($LASTEXITCODE -ne 0) { throw "imzalama başarısız" }
    & $script:py (Join-Path $script:repo "tools\sign_release.py") verify --dir $script:sign --pub-pem (Join-Path $script:keys "pops_release_ed25519.pub.pem")

    Say "backend (uvicorn, TLS)"
    if (-not (StartBackend)) { throw "backend açılmadı; log: $(Get-Content -Raw $script:backendLog -ErrorAction SilentlyContinue)" }

    Say "süper admin / admin / sınıf"
    Ctl setup --lab $script:lab | Out-Null
    $tok = CtlJson enroll-token --lab $script:lab
    if (-not $tok.token) { throw "enroll token alınamadı: $($tok | ConvertTo-Json)" }

    Say "ajan A kurulumu (SERVER_CA_CERT ile sabitlenmiş CA; T6'nın kurulum yarısı)"
    $ins = InstallA $tok.token
    if ($ins.Exit -ne 0) { throw "MSI kurulum çıkışı $($ins.Exit); log $($ins.Log)" }
    $dev = CtlJson wait-device --online --timeout 120
    if (-not $dev.hw_id) { throw "ajan cihazı görünmedi" }
    $script:hw = $dev.hw_id
    Ctl lab-of --hw $script:hw --lab $script:lab | Out-Null

    Say "imzalı B'yi bir kez stage et (deploy'lar bunu kullanır; downgrade koruması tek stage ister)"
    $manifest = Get-Content -Raw (Join-Path $script:sign "manifest.json") | ConvertFrom-Json
    $script:shaB = ($manifest.artifacts | Where-Object { $_.name -like "POps-Agent-*-win-x64.msi" } | Select-Object -First 1).sha256
    $st = CtlJson stage-release --dir $script:sign --msi "POps-Agent-$($script:verB)-win-x64.msi"
    if (-not $st.ok) { throw "stage-release başarısız: $($st | ConvertTo-Json -Compress)" }

    Record "setup" $true "ajan kuruldu, hw_id=$($script:hw), sürüm=$($dev.version), B stage edildi"
}

# ── T3: güç ──────────────────────────────────────────────────────────────────────
function Test-T3 {
    Say "T3a: bekleyen kapatma varken power shutdown/restart -> 1190, görev Failed, makine açık kalır"
    $shutdownExe = "$env:SystemRoot\System32\shutdown.exe"
    $parts = @(); $allOk = $true
    try {
        # 50 dk sonraya bir kapatma planla; ikinci planlama 1190 vermeli (gerçekten bekleyen kapatma var). Yoksa ajanın
        # "/t 0" çağrısı runner'ı kapatırdı: o durumda güç emri hiç gönderilmez.
        & $shutdownExe /s /t 3000 /c "e2e pending" 2>$null | Out-Null
        & $shutdownExe /s /t 3000 /c "e2e pending check" 2>$null | Out-Null
        $pending = $LASTEXITCODE -eq 1190
        if (-not $pending) { Record "T3a" $false "bekleyen kapatma doğrulanamadı (ikinci shutdown çıkışı $LASTEXITCODE); güç emri gönderilmedi"; return }
        foreach ($op in 'shutdown', 'restart') {
            $mark = LogMark
            $r = CtlJson power --hw $script:hw --op $op --delay 0
            $tid = if ($r.task_ids) { $r.task_ids[0] } else { $null }
            $logOk = WaitLogSince "Güç işlemi yapılamadı: $op .*shutdown\.exe 1190" 30 $mark
            $out = $null
            for ($i = 0; $i -lt 20 -and $tid; $i++) {
                $task = CtlJson tasks --id $tid
                $out = if ($task.tasks) { $task.tasks[0] } else { $null }
                if ($out -and $out.status -ne 'Running' -and $out.status -ne 'Pending') { break }
                Start-Sleep -Seconds 1
            }
            $ok = $logOk -and $out -and $out.status -eq 'Failed' -and ($out.output -match '^\[HATA\] shutdown\.exe 1190: ')
            $allOk = $allOk -and $ok
            $parts += "$op -> görev $tid $($out.status), exit $($out.exit_code), '$($out.output)'"
        }
        # Makine açık: bu satır çalışıyor; ayrıca kapatma hâlâ bekliyor (ajan iptal etmedi, yerine yenisini koymadı)
        & $shutdownExe /s /t 3000 /c "e2e pending check" 2>$null | Out-Null
        $stillPending = $LASTEXITCODE -eq 1190
        Record "T3a" ($allOk -and $stillPending) (($parts -join '; ') + "; bekleyen kapatma hâlâ bekliyor=$stillPending, runner açık")
    } catch {
        Record "T3a" $false "istisna: $($_.Exception.Message)"
    } finally {
        & $shutdownExe /a 2>$null | Out-Null
    }

    Say "T3b: oturum açık kullanıcı yok -> -6 (runner'da konsol oturumu var; atlanır)"
    # Barındırılan runner'da konsol oturumu (session 2) runneradmin ile AÇIK ve Runner.Worker da orada; gerçek bir
    # lock/logoff o oturumu kapatıp işi düşürür. Bu yüzden -6 yolu runner'ı öldürmeden tekrarlanamaz.
    $sess = (query session 2>&1 | Out-String)
    Record "T3b" 'skip' "konsol oturumu açık (runneradmin, session 2 = Runner.Worker); lock/logoff işi düşürür, çalıştırılmadı. query session: $($sess.Trim() -replace '\s+',' ')"
}

# Runner'ın GitHub/altyapı ile açık bağlantılarının uzak IP'leri: sınav izin listesine konur ki sınav modunun giden
# engeli işi düşürmesin (ölçülen gerçek: her şeyi engelleyince iş ~90 sn'de iptal olur)
function RunnerRemoteIps {
    $ids = @(Get-Process Runner.* -ErrorAction SilentlyContinue | ForEach-Object Id)
    $ips = @(Get-NetTCPConnection -State Established -ErrorAction SilentlyContinue |
        Where-Object { $ids -contains $_.OwningProcess } | ForEach-Object RemoteAddress)
    $ips += @("168.63.129.16", "169.254.169.254")   # Azure platform / metadata
    return @($ips | Where-Object { $_ -and $_ -notmatch '^(127\.|::1$|0\.0\.0\.0$)' } | Sort-Object -Unique | Select-Object -First 45)
}

# ── T4: sınav saati ───────────────────────────────────────────────────────────────
function Test-T4 {
    Say "T4: sınav saati (backend kapalıyken saat oynatılır; monotonik bitiş)"
    $restoreClock = $false
    $safety = StartSafety 80
    try {
        # Sınav modu giden trafiği keser: runner'ın GitHub bağlantıları izin listesine (iş düşmesin)
        $allow = RunnerRemoteIps
        Say "T4: sınav izin listesi (runner uzak IP'leri): $($allow -join ', ')"
        $examMark = LogMark
        $sw = [System.Diagnostics.Stopwatch]::StartNew()   # monotonik: saat oynatılsa da gerçek geçen süre
        $start = CtlJson exam-start --lab $script:lab --minutes 1 --allow @allow
        if ($start.http -ne 200) { Record "T4" $false "exam-start http=$($start.http) $($start | ConvertTo-Json -Compress)"; return }
        if (-not (WaitLogSince "SINAV MODU AKT" 25 $examMark)) { Record "T4" $false "ajan sınava girmedi"; return }
        $examActive = Test-Path (Join-Path $script:secure "exam.json")
        $logMark = LogMark  # bu noktadan sonraki "bitti" satırlarını arayacağız

        Say "T4: backend durduruluyor (ajan yalnız)"
        StopBackend

        Say "T4: saat +2 saat (sınav bitmemeli)"
        Stop-Service vmictimesync, w32time -ErrorAction SilentlyContinue
        $restoreClock = $true
        $real = Get-Date
        Set-Date -Date $real.AddHours(2) | Out-Null
        $skewPlus = [Math]::Round(((Get-Date) - $real).TotalHours, 2)
        Start-Sleep -Seconds 12
        $t = AgentLogText
        $notEndedPlus = -not ($t.Substring([Math]::Min($logMark, $t.Length)) -match "Sınav modu bitti")
        $stillActivePlus = Test-Path (Join-Path $script:secure "exam.json")

        Say "T4: saat -2 saat (sınav uzamamalı)"
        Set-Date -Date $real.AddHours(-2) | Out-Null
        $skewMinus = [Math]::Round(((Get-Date) - $real).TotalHours, 2)
        Start-Sleep -Seconds 8
        $stillActiveMinus = Test-Path (Join-Path $script:secure "exam.json")

        Say "T4: saat geri alınıyor (runner korunur); monotonik bitiş bekleniyor"
        Set-Date -Date (Get-Date).AddHours(2) | Out-Null   # -2h'den gerçek zamana dön (+2h)
        Start-Service vmictimesync, w32time -ErrorAction SilentlyContinue
        & w32tm /resync /force 2>$null | Out-Null
        $restoreClock = $false

        # Monotonik bitiş: sınav 1 dk, başlangıçtan ~60 sn sonra (saat artık doğru)
        $ended = WaitLogSince "Sınav modu bitti \(until\)" 60 $logMark
        $endSec = [Math]::Round($sw.Elapsed.TotalSeconds)
        $event = $null
        try { $event = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'POps Agent'; Id = 1111 } -MaxEvents 5 -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'source: until' } | Select-Object -First 1 } catch {}
        $rulesGone = (RuleCount 'POps Exam') -eq 0
        # Sınav 60 sn: ajanın 2 sn'lik turu ve kuralları kaldıran PowerShell için pay; saatle bitseydi +2h'de hemen
        # biterdi, -2h'de 2 saat uzardı
        $onTime = $endSec -ge 50 -and $endSec -le 85
        $pass = $examActive -and $notEndedPlus -and $stillActivePlus -and $stillActiveMinus -and $ended -and $onTime -and [bool]$event -and $rulesGone
        $evText = if ($event) { ($event.Message -replace '\r?\n', ' / ') } else { "yok" }
        Record "T4" $pass ("saat +$skewPlus sa: bitmedi=$($notEndedPlus -and $stillActivePlus); saat $skewMinus sa: sürüyor=$stillActiveMinus; '$(LogLine 'Sınav modu bitti' $logMark)' sınav emrinden $endSec sn sonra (60 sn sınav); olay 1111: '$evText'; POps Exam kuralı kalmadı=$rulesGone")
    } catch {
        Record "T4" $false "istisna: $($_.Exception.Message)"
    } finally {
        if ($restoreClock) {
            Start-Service vmictimesync, w32time -ErrorAction SilentlyContinue
            & w32tm /resync /force 2>$null | Out-Null
        }
        EndSafety $safety | Out-Null
        & pwsh -NoProfile -File (Join-Path $script:here "safety.ps1") | Out-Null   # sınav kuralları kalmasın
        if (-not $script:backend) { StartBackend | Out-Null }
        try { Ctl exam-stop --lab $script:lab | Out-Null } catch {}
    }
}

# ── Güncelleme yardımcıları (T6/T7) ───────────────────────────────────────────────
# $since'ten sonra desene uyan ilk log satırı (zaman ve seviye öneki atılmış; tabloya kanıt) ve satır sayısı
function LogLine($pattern, $since) {
    $t = AgentLogText
    $hit = ($t.Substring([Math]::Min($since, $t.Length)) -split "`n") | Where-Object { $_ -match $pattern } | Select-Object -First 1
    if ($hit) { return ($hit.Trim() -replace '^\[[0-9:.]+\] \[[A-Z ]+\] ', '') } else { return "" }
}
function LogCount($pattern, $since) {
    $t = AgentLogText
    return @(($t.Substring([Math]::Min($since, $t.Length)) -split "`n") | Where-Object { $_ -match $pattern }).Count
}

# Ajanı A'ya döndür: kaldır; eş önbelleği klasörünü ve indirilen paketleri sil (kaldırma C:\POpsData'yı bilerek korur,
# önbellekte kalan imzalı paket bir sonraki varyantı "Paket indirildi (cache)" ile kısa yoldan geçirirdi); backend'i
# yeniden başlat (eş önbelleği rollout durumu bellekte); A'yı kur ve ajan taze backend'e A ile bağlanana kadar bekle.
function ResetToA($enrollToken) {
    if (AgentInstalled) { UninstallAgent | Out-Null }
    foreach ($d in 'C:\POpsData\cache', 'C:\POpsData\updates') { Remove-Item $d -Recurse -Force -ErrorAction SilentlyContinue }
    StopBackend; StartBackend | Out-Null
    Ctl clear-pending --targets $script:hw $script:seed | Out-Null
    # Yedek nöbet: yeniden kurulan ajan beklenmedik biçimde yalıtılırsa giden trafik en geç 60 sn'de açılır
    $safety = StartSafety 60
    InstallA $enrollToken | Out-Null
    $dev = CtlJson wait-device --online --timeout 120 --exclude $script:seed --version $script:verA
    if ($dev.hw_id) { $script:hw = $dev.hw_id; Ctl lab-of --hw $script:hw --lab $script:lab | Out-Null }
    Start-Sleep -Seconds 4
    $isolated = ((RuleCount 'POps Isolation') + (RuleCount 'POps Exam')) -gt 0
    if ($isolated) {
        Write-Host "UYARI: yeniden kurulan ajan bağlanınca yalıtıldı ($(GroupCounts)); sunucu durumu temizleniyor"
        ClearServerIsolation $true
        & pwsh -NoProfile -File (Join-Path $script:here "safety.ps1") | Out-Null
    }
    EndSafety $safety | Out-Null
    return ([bool]$dev.hw_id -and -not $isolated)
}
# Güncelleme kuruldu mu: ajan B ile yeniden bağlanana kadar (POpsUpdater: durdur, msiexec, sağlık; ~1 dk)
function WaitVersionB { return [bool](CtlJson wait-device --online --timeout 150 --exclude $script:seed --version $script:verB).hw_id }

# ── T6: sabitlenmiş CA ile sunucudan güncelleme ───────────────────────────────────
function Test-T6 {
    Say "T6: SERVER_CA_CERT ile kurulu ajan; imzalı güncelleme sunucudan iner (BITS atlanır)"
    try {
        if (-not (WaitAgentOnline 60)) { Record "T6" $false "ajan backend'e (T4 sonrası) bağlanmadı"; return }
        $logMark = LogMark
        Ctl clear-pending --targets $script:hw | Out-Null
        $dep = CtlJson deploy --targets $script:hw
        if ($dep.http -ne 200) { Record "T6" $false "deploy http=$($dep.http) $($dep | ConvertTo-Json -Compress)"; return }
        $bits = WaitLogSince "BITS kullanılmadı: sunucu sertifikası kurum sertifikasıyla \(server-ca\.pem\)" 60 $logMark
        $server = WaitLogSince "Paket indirildi \(server\)" 60 $logMark
        $installed = WaitVersionB
        Record "T6" ($bits -and $server -and $installed) ("'$(LogLine 'BITS kullanılmadı' $logMark)' + '$(LogLine 'Paket indirildi' $logMark)'; B kuruldu ve bağlandı=$installed")
    } catch {
        Record "T6" $false "istisna: $($_.Exception.Message)"
    }
}

# ── T7: eş önbelleği ────────────────────────────────────────────────────────────
function RunSeed($ip, $logName) {
    $stop = Join-Path $script:work "seed.stop"
    Remove-Item $stop -ErrorAction SilentlyContinue
    $log = Join-Path $script:artifacts $logName
    Remove-Item $log -ErrorAction SilentlyContinue
    Ctl seed-secret --hw $script:seed | Out-Null
    $p = Start-Process $script:py -PassThru -WindowStyle Hidden -ArgumentList @(
        (Join-Path $script:here "seed_agent.py"), "--hw", $script:seed, "--ip", $ip, "--port", "8818",
        "--old", $script:verA, "--new", $script:verB, "--log", $log, "--stop", $stop, "--timeout", "150")
    return [pscustomobject]@{ Proc = $p; Stop = $stop; Log = $log }
}
function StopSeed($s) {
    if (-not $s) { return }
    "stop" | Set-Content -LiteralPath $s.Stop
    Start-Sleep -Seconds 2
    if ($s.Proc -and -not $s.Proc.HasExited) { Stop-Process -Id $s.Proc.Id -Force -ErrorAction SilentlyContinue }
}
function RunPeer($bind, $wrong) {
    $peerArgs = @((Join-Path $script:here "peer_server.py"), "--bind", $bind, "--port", "8818",
        "--msi", (Join-Path $script:sign "POps-Agent-$($script:verB)-win-x64.msi"), "--sha", $script:shaB)
    if ($wrong) { $peerArgs += "--wrong" }
    $err = Join-Path $script:artifacts ("peer-{0}.log" -f (Get-Date -Format HHmmss))
    return (Start-Process $script:py -PassThru -WindowStyle Hidden -RedirectStandardError $err -ArgumentList $peerArgs)
}

function Test-T7 {
    Say "T7: eş önbelleği (B sha256 = $($script:shaB))"
    $lanip = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4Address.IPAddress
    if (-not $lanip) { $lanip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '10.*' } | Select-Object -First 1).IPAddress }
    Say "T7: runner LAN IP = $lanip, B sha256 = $($script:shaB)"

    $tok = CtlJson enroll-token --lab $script:lab

    $peerLine = "Paket indirildi \(peer $([regex]::Escape($script:seed))\)"
    # T7a: doğru eş -> eşten iner, kurar
    Peer-Variant "T7a" $lanip $false $false $tok.token @("Eşten indiriliyor: $([regex]::Escape($script:seed))", $peerLine)
    # T7b: bozuk bayt -> SHA uyuşmazlığı, sunucuya düşer
    Peer-Variant "T7b" $lanip $true $false $tok.token @("Eşten alınamadı \($([regex]::Escape($script:seed))\): imzalı manifest", "Paket indirildi \(server\)")
    # T7c: alt ağ dışı eş -> bir uyarı, sunucudan iner
    Peer-Variant "T7c" "198.51.100.7" $false $true $tok.token @("eş bu bilgisayarın alt ağlarından hiçbirinde değil", "Paket indirildi \(server\)")
}

# name, seedIp (eşin bildirdiği adres), wrongBytes, outsideSubnet, enrollToken, sırayla görülmesi gereken log satırları
function Peer-Variant($name, $seedIp, $wrong, $outside, $enroll, $patterns) {
    Say "$name hazırlık: A'ya sıfırla, eş önbelleği aç, tohum + sahte eş"
    $seed = $null; $peer = $null
    try {
        if (-not (ResetToA $enroll)) { Record $name $false "ajan A ile yeniden bağlanmadı"; return }
        Ctl peer-cache --on | Out-Null
        Ctl clear-pending --targets $script:hw $script:seed | Out-Null

        # Alt ağ dışı varyantta tohum 198.51.100.7 bildirir; sahte eş yine LAN IP'de dinler (ajan zaten reddedecek)
        $announceIp = if ($outside) { "198.51.100.7" } else { $seedIp }
        $peerBind = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '10.*' } | Select-Object -First 1).IPAddress
        $peer = RunPeer $peerBind $wrong
        $seed = RunSeed $announceIp ("seed-$name.log")
        Start-Sleep -Seconds 3
        # Tohum last_seen'i geleceğe: deploy'da deterministik olarak tohum seçilsin
        Ctl seed-future --hw $script:seed | Out-Null
        Ctl lab-of --hw $script:seed --lab $script:lab | Out-Null

        $logMark = LogMark
        $dep = CtlJson deploy --targets $script:hw $script:seed
        if ($dep.http -ne 200) { Record $name $false "deploy http=$($dep.http) $($dep | ConvertTo-Json -Compress)"; return }

        $seen = @(); $all = $true
        foreach ($pat in $patterns) {
            $ok = WaitLogSince $pat 90 $logMark
            $all = $all -and $ok
            $seen += if ($ok) { "'$(LogLine $pat $logMark)'" } else { "YOK: $pat" }
        }
        $installed = WaitVersionB
        # Önbellekten kısa yol olmamalı (varyant gerçekten eşi/sunucuyu sınadı mı)
        $noCache = (LogCount "Paket indirildi \(cache\)" $logMark) -eq 0
        $extra = ""
        if ($outside) { $n = LogCount "alt ağlarından hiçbirinde değil" $logMark; $extra = "; uyarı sayısı=$n"; $all = $all -and ($n -eq 1) }
        $roles = "tohum=$($dep.seeds -join ',') bekleyen=$($dep.waiting_for_seed -join ',')"
        Record $name ($all -and $installed -and $noCache) ("$($seen -join ' + '); B kuruldu=$installed; önbelleksiz=$noCache$extra; $roles")
    } catch {
        Record $name $false "istisna: $($_.Exception.Message)"
    } finally {
        StopSeed $seed
        if ($peer -and -not $peer.HasExited) { Stop-Process -Id $peer.Id -Force -ErrorAction SilentlyContinue }
        try { Ctl peer-cache --off | Out-Null } catch {}
    }
}

# ── T5: kaldırma ──────────────────────────────────────────────────────────────────
function KioskSet { return [bool]((Get-ItemProperty 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Policies\System' -Name DisableTaskMgr -ErrorAction SilentlyContinue).DisableTaskMgr -eq 1) }

function Test-T5 {
    Say "T5a: gerçek karantina -> kaldırma -> kurallar/durum/kiosk gitti, ağ açık"
    T5-Variant "T5a" $false $false

    Say "T5b: sınav sürerken kaldırma"
    T5-Variant "T5b" $true $false

    Say "T5c: karantinada yükseltme kuralları ve karantinayı korur"
    T5-Upgrade

    Say "T5d: karantinadan önce KAPALI profil, kaldırmadan sonra yine KAPALI"
    T5-Variant "T5d" $false $true
}

# name, examDuring, disableProfileFirst
function T5-Variant($name, $examDuring, $disableProfileFirst) {
    $safety = $null
    try {
        if (-not (AgentInstalled)) {
            $tok = CtlJson enroll-token --lab $script:lab
            if (-not (ResetToA $tok.token)) { Record $name $false "ajan A ile temiz bağlanmadı (çevrimdışı ya da yalıtılmış geldi)"; return }
        }
        if (-not (WaitAgentOnline 60)) { Record $name $false "ajan çevrimiçi değil"; return }
        if ($disableProfileFirst) { Set-NetFirewallProfile -Profile Public -Enabled False }
        # Nöbet yalıtımdan ÖNCE başlar: sınav modu da giden trafiği keser, pencere bu andan sayılır
        $safety = StartSafety 50
        if ($examDuring) {
            $exMark = LogMark
            $ex = CtlJson exam-start --lab $script:lab --minutes 8
            WaitLogSince "SINAV MODU AKT" 30 $exMark | Out-Null
        }
        $lkMark = LogMark
        $lk = CtlJson lockdown --hw $script:hw
        $engaged = WaitLogSince "AĞ KARANTİNASI AKTİF" 25 $lkMark
        $before = GroupCounts
        $isoBefore = RuleCount 'POps Isolation'
        $examBefore = RuleCount 'POps Exam'
        $filesBefore = (Test-Path (Join-Path $script:secure "isolation.json")) -and (Test-Path (Join-Path $script:secure "lockdown.json"))
        $kioskBefore = KioskSet
        # Kaldır (karantina/sınav sürerken): RemoveConfig bütün POps kurallarını siler, kiosk'u geri alır
        $un = UninstallAgent
        $rulesAfter = (PopsRules).Count
        $stateFiles = -not (Test-Path (Join-Path $script:secure "isolation.json")) -and `
                      -not (Test-Path (Join-Path $script:secure "exam.json")) -and `
                      -not (Test-Path (Join-Path $script:secure "lockdown.json"))
        $kioskAfter = KioskSet
        $msiLine = MsiPopsLine $un.Text "güvenlik duvarı kuralları kaldırıldı"
        $fired = EndSafety $safety; $safety = $null
        # Ağ açık mı: kurallar gittiyse dışarıya TCP 443 kurulur
        $net = [bool](Test-NetConnection github.com -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue)
        $profileReDisabled = $true
        if ($disableProfileFirst) {
            $profileReDisabled = -not (Get-NetFirewallProfile -Profile Public).Enabled
            Set-NetFirewallProfile -Profile Public -Enabled True   # test düzeni geri
        }
        $engagedOk = $engaged -and $isoBefore -gt 0 -and $filesBefore -and (-not $examDuring -or $examBefore -gt 0)
        $pass = $engagedOk -and ($rulesAfter -eq 0) -and $stateFiles -and $kioskBefore -and (-not $kioskAfter) -and $msiLine -and $net -and $profileReDisabled -and (-not $fired)
        Record $name $pass ("karantina logu=$engaged; önce: $before, isolation.json+lockdown.json=$filesBefore, DisableTaskMgr=$kioskBefore; sonra: kural=$rulesAfter, durum dosyaları yok=$stateFiles, DisableTaskMgr=$kioskAfter, github:443=$net, profil yine kapalı=$profileReDisabled, nöbet tetiklendi=$fired; MSI: '$msiLine'")
    } catch {
        Record $name $false "istisna: $($_.Exception.Message)"
    } finally {
        EndSafety $safety | Out-Null
        & pwsh -NoProfile -File (Join-Path $script:here "safety.ps1") | Out-Null
        ClearServerIsolation $examDuring
    }
}

# Kaldırılan ajanın sunucudaki karantina / sınav durumu da kapatılır: yoksa yeniden kurulan ajan bağlanınca sunucu
# bekleyen karantinayı (ya da süren sınavı) yeniden gönderir ve runner'ın giden trafiği nöbetsiz kesilir
function ClearServerIsolation($exam) {
    if (-not $script:backend) { StartBackend | Out-Null }
    try { Ctl unlock --hw $script:hw | Out-Null } catch {}
    if ($exam) { try { Ctl exam-stop --lab $script:lab | Out-Null } catch {} }
}

function T5-Upgrade {
    $safety = $null
    try {
        if (-not (AgentInstalled)) {
            $tok = CtlJson enroll-token --lab $script:lab
            if (-not (ResetToA $tok.token)) { Record "T5c" $false "ajan A ile temiz bağlanmadı (çevrimdışı ya da yalıtılmış geldi)"; return }
        }
        if (-not (WaitAgentOnline 60)) { Record "T5c" $false "ajan çevrimiçi değil"; return }
        $safety = StartSafety 60
        $lkMark = LogMark
        $lk = CtlJson lockdown --hw $script:hw
        WaitLogSince "AĞ KARANTİNASI AKTİF" 25 $lkMark | Out-Null
        $isoBefore = RuleCount 'POps Isolation'
        $upMark = LogMark
        # Yükseltme (major upgrade): karantina ve kurallar KORUNMALI (UPGRADINGPRODUCTCODE; PopsRemoveConfig atlanır)
        $up = InstallBUpgrade
        $started = WaitLogSince "POps Agent Başlatılıyor \(v$([regex]::Escape($script:verB))\)" 30 $upMark
        Start-Sleep -Seconds 3
        $isoAfter = RuleCount 'POps Isolation'
        $stateKept = (Test-Path (Join-Path $script:secure "isolation.json")) -and (Test-Path (Join-Path $script:secure "lockdown.json"))
        $kioskKept = KioskSet
        $skipped = $up.Text -match "Skipping action: PopsRemoveConfig \(condition is false\)"
        # Nöbet kurmaya devam eder: karantina kaldırma (finally) bitene kadar giden trafik kapalı
        $fired = SafetyFired $safety
        $pass = ($isoBefore -gt 0) -and ($isoAfter -eq $isoBefore) -and $stateKept -and $kioskKept -and $started -and $skipped -and ($up.Exit -eq 0 -or $up.Exit -eq 3010) -and (-not $fired)
        Record "T5c" $pass ("POps Isolation kuralı önce=$isoBefore/sonra=$isoAfter, isolation.json+lockdown.json kaldı=$stateKept, DisableTaskMgr kaldı=$kioskKept, B açıldı=$started, PopsRemoveConfig atlandı=$skipped, msiexec=$($up.Exit), nöbet tetiklendi=$fired")
    } catch {
        Record "T5c" $false "istisna: $($_.Exception.Message)"
    } finally {
        # Önce karantinayı kaldır (ağ açılsın: kaldırma kuralları siler), nöbeti sonra kapat
        try { if (AgentInstalled) { UninstallAgent | Out-Null } } catch {}
        EndSafety $safety | Out-Null
        & pwsh -NoProfile -File (Join-Path $script:here "safety.ps1") | Out-Null
        ClearServerIsolation $false
    }
}

# ── Akış ──────────────────────────────────────────────────────────────────────
try {
    Prepare
    Test-T3
    Test-T4
    Test-T6
    Record "T6b" 'skip' "isteğe bağlı (CA sabitlenmeden, test CA makinenin Root deposunda, BITS yolu) bu işte çalıştırılmadı"
    Test-T7
    Test-T5
} catch {
    Record "fatal" $false "akış durdu: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
} finally {
    Cleanup
}

$failed = @($script:results | Where-Object { $_.Result -eq 'FAIL' })
if ($failed.Count -gt 0) { exit 1 } else { exit 0 }
