# Uçtan uca test için iki ajan paketi, aynı koddan: A (eski sürüm) ve B (yeni sürüm). Derleme release.yml'deki "agent"
# işiyle aynıdır (dört bileşen tek klasöre, self-contained win-x64, tepsi EN SON; sonra WiX). Farkı: ajanın gömülü
# release anahtarı bu çalışmanın geçici test anahtarıyla değiştirilir (ReleaseVerifier.PublicKeyBase64), böylece
# testin imzaladığı manifest kabul edilir. Gerçek anahtar ve depodaki dosyalar commit edilmez; değişiklik yalnızca
# runner'daki çalışma kopyasındadır.
param(
    [Parameter(Mandatory = $true)] [string] $OutDir,
    [Parameter(Mandatory = $true)] [string] $PublicKeyRawBase64,
    [string] $VersionA = "0.1.90-e2e",
    [string] $VersionB = "0.1.91-e2e"
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path

# Gömülü anahtar: ham 32 bayt, base64 (tools/sign_release.py genkey'in yazdığı satır)
$verifier = Join-Path $repo "Agent\POps.Agent\POps.Agent\ReleaseVerifier.cs"
$src = Get-Content $verifier -Raw
$patched = [regex]::Replace($src, 'public const string PublicKeyBase64 = "[^"]+";', "public const string PublicKeyBase64 = `"$PublicKeyRawBase64`";")
if ($patched -eq $src) { throw "ReleaseVerifier.PublicKeyBase64 bulunamadı" }
Set-Content -Path $verifier -Value $patched -NoNewline -Encoding utf8

$projects = @(
    "Agent/POps.Agent/POps.Agent/POpsAgent.csproj",
    "Agent/POpsWatchdog/POpsWatchDog/POpsWatchDog.csproj",
    "Agent/POpsUpdater/POpsUpdater/POpsUpdater.csproj",
    "Agent/POpsTray/POpsTray.csproj"
)
$run = if ($env:GITHUB_RUN_NUMBER) { [int]$env:GITHUB_RUN_NUMBER % 65000 } else { 1 }
$versionFile = Join-Path $repo "VERSION"
$originalVersion = Get-Content $versionFile -Raw

try {
    foreach ($pkg in @(@{ Label = "A"; Version = $VersionA }, @{ Label = "B"; Version = $VersionB })) {
        $label = $pkg.Label
        $ver = $pkg.Version
        if ($ver -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "sürüm a.b.c ile başlamıyor: $ver" }
        $msiVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$run"
        # Sürüm tek kaynaktan: kök VERSION (Agent/Directory.Build.props okur)
        Set-Content -Path $versionFile -Value $ver -NoNewline -Encoding ascii
        $publish = Join-Path $OutDir "publish\$label"
        foreach ($p in $projects) {
            dotnet publish (Join-Path $repo $p) -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $publish
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish $p ($label) çıkış $LASTEXITCODE" }
        }
        Remove-Item "$publish\*.pdb" -ErrorAction SilentlyContinue
        # release.yml'deki koruma: tepsinin istediği System.Drawing sürümü
        $drawing = [Reflection.AssemblyName]::GetAssemblyName((Resolve-Path "$publish\System.Drawing.dll")).Version
        if ($drawing.Major -lt 10) { throw "System.Drawing.dll $($drawing): tepsi en son yayımlanmamış" }

        $msiOut = Join-Path $OutDir "msi-out\$label"
        # Önceki paketin ara dosyaları yeni sürüme karışmasın
        Remove-Item (Join-Path $repo "Installer\agent\obj") -Recurse -Force -ErrorAction SilentlyContinue
        # PopsPublishDir sondaki ayraçla biter (wixproj "$(PopsPublishDir)POpsAgent.exe" diye birleştirir); release.yml ile aynı
        $publishDir = $publish + "\"
        dotnet build (Join-Path $repo "Installer\agent\POps.Agent.Installer.wixproj") -c Release --no-incremental "-p:PopsPublishDir=$publishDir" "-p:ProductVersion=$msiVersion" -o $msiOut
        if ($LASTEXITCODE -ne 0) { throw "MSI ($label) çıkış $LASTEXITCODE" }
        $dest = Join-Path $OutDir "pkg\$label"
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Move-Item (Join-Path $msiOut "POps-Agent.msi") (Join-Path $dest "POps-Agent-$ver-win-x64.msi") -Force
        Write-Host "Paket $label`: $ver (MSI $msiVersion) -> $dest"
    }
}
finally {
    Set-Content -Path $versionFile -Value $originalVersion -NoNewline -Encoding ascii
}
