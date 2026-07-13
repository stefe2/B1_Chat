# ============================================================================
#  release.ps1 — release de la console B1 Chat sur GitHub (stefe2/B1_Chat, tag vX.Y.Z)
#
#  dotnet publish (exe autonome) + makensis (installeur) puis, avec -Publish,
#  tag git vX.Y.Z + release GitHub avec l'installeur en asset.
#  Depot unique avec le firmware (tag "fw-vX.Y.Z") : le prefixe de tag distingue
#  les deux trains de release au sein du meme depot GitHub.
#
#  Usage :  .\console\installer\release.ps1 [-Notes "notes de version"] [-Publish]
#  Prérequis : NSIS (makensis) ; pour -Publish : gh auth login (une fois).
#  La version vient de <VersionPrefix> dans b1-chat-console.csproj.
# ============================================================================
param(
    [string]$Notes = "",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# --- Version depuis le csproj -------------------------------------------------
$csproj = Get-Content "b1-chat-console.csproj" -Raw
if ($csproj -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') { throw "VersionPrefix introuvable dans le csproj" }
$version = $Matches[1]
Write-Host "Console v$version" -ForegroundColor Cyan

# --- espflash bundle : copie depuis tools/ (racine du depot) si absent --------
$espflashSrc = Join-Path $repo "..\tools\espflash.exe"
if (-not (Test-Path "tools\espflash.exe") -and (Test-Path $espflashSrc)) {
    New-Item -ItemType Directory -Force "tools" | Out-Null
    Copy-Item $espflashSrc "tools\espflash.exe"
    Write-Host "tools\espflash.exe copie depuis tools/ (bundle)." -ForegroundColor Cyan
}

# --- Publish + installeur ------------------------------------------------------
Write-Host "dotnet publish (exe autonome)..." -ForegroundColor Cyan
$publishOutput = dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true 2>&1
if ($LASTEXITCODE -ne 0) { $publishOutput | Write-Host; throw "echec du publish" }
$publishOutput | Select-Object -Last 1 | Write-Host

$makensis = @("makensis", "C:\Program Files (x86)\NSIS\makensis.exe", "C:\Program Files\NSIS\makensis.exe") |
    Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $makensis) { throw "makensis introuvable (winget install NSIS.NSIS)" }

Write-Host "makensis (installeur v$version)..." -ForegroundColor Cyan
$nsisOutput = & $makensis "/DAPPVERSION=$version" "installer\b1-chat-console.nsi" 2>&1
if ($LASTEXITCODE -ne 0) { $nsisOutput | Write-Host; throw "echec de makensis" }
$nsisOutput | Select-Object -Last 2 | Write-Host

$setup = "installer\b1-chat-console-setup-$version.exe"
if (-not (Test-Path $setup)) { throw "installeur attendu introuvable : $setup" }
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Installeur pret : $setup" -ForegroundColor Green
Write-Host "SHA-256 : $sha"

# --- Publication GitHub --------------------------------------------------------
if ($Publish) {
    $tag = "v$version"
    git tag $tag 2>$null
    git push origin main --tags
    gh release create $tag $setup `
        --title "B1 Chat Console v$version" `
        --notes ($Notes ? "$Notes`n`nSHA-256 : $sha" : "Release v$version.`n`nSHA-256 : $sha")
    Write-Host "Release $tag publiee sur GitHub." -ForegroundColor Green
} else {
    Write-Host "Build seulement (ajoute -Publish pour tagger + publier la release GitHub)." -ForegroundColor Yellow
}
