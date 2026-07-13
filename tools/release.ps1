# ============================================================================
#  release.ps1 — release firmware B1 sur GitHub (stefe2/B1_Chat)
#
#  Compile les DEUX rôles (IS_MASTER 1 puis 0), produit dist/ avec les .bin
#  nommés par version + firmware_manifest.json (SHA-256, modèle KyberEditor),
#  puis (avec -Publish) tag git fw-vX.Y.Z + release GitHub avec les assets.
#
#  Usage :  .\tools\release.ps1 [-Notes "notes de version"] [-Publish]
#  Prérequis pour -Publish : gh auth login (une fois).
#  La version vient de FW_VERSION dans src/config.h (source de vérité).
# ============================================================================
param(
    [string]$Notes = "",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$pio = Join-Path $env:USERPROFILE ".platformio\penv\Scripts\pio.exe"
$configH = "src/config.h"

# --- Version depuis config.h ------------------------------------------------
$config = Get-Content $configH -Raw
if ($config -notmatch '#define\s+FW_VERSION\s+"([^"]+)"') { throw "FW_VERSION introuvable dans $configH" }
$version = $Matches[1]
Write-Host "Firmware v$version" -ForegroundColor Cyan

# --- Compile un rôle et copie le binaire -------------------------------------
$dist = Join-Path $repo "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

# Mémorise le rôle actuel pour le restaurer à la fin (il vit dans config.h).
if ($config -notmatch '#define\s+IS_MASTER\s+(\d)') { throw "IS_MASTER introuvable dans $configH" }
$originalRole = $Matches[1]

function Build-Role([int]$isMaster, [string]$roleName) {
    $c = Get-Content $configH -Raw
    $c = $c -replace '#define\s+IS_MASTER\s+\d', "#define IS_MASTER $isMaster"
    Set-Content $configH $c -NoNewline
    Write-Host "Compilation $roleName (IS_MASTER $isMaster)..." -ForegroundColor Cyan
    $buildOutput = & $pio run -e b1 2>&1
    if ($LASTEXITCODE -ne 0) { $buildOutput | Write-Host; throw "echec de compilation ($roleName)" }
    $buildOutput | Select-Object -Last 1 | Write-Host
    $out = Join-Path $dist "b1-$roleName-v$version.bin"
    Copy-Item ".pio/build/b1/firmware.bin" $out -Force
    return $out
}

try {
    $masterBin = Build-Role 1 "master"
    $slaveBin  = Build-Role 0 "slave"
} finally {
    # Restaure le rôle d'origine.
    $c = Get-Content $configH -Raw
    $c = $c -replace '#define\s+IS_MASTER\s+\d', "#define IS_MASTER $originalRole"
    Set-Content $configH $c -NoNewline
}

# --- Manifeste (modèle KyberEditor : version, fichiers, sha256, tailles) -----
function FileEntry([string]$path, [string]$role) {
    $f = Get-Item $path
    [ordered]@{
        role   = $role
        file   = $f.Name
        sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        size   = $f.Length
    }
}

$manifest = [ordered]@{
    version = $version
    date    = (Get-Date -Format "yyyy-MM-dd")
    notes   = $Notes
    files   = @((FileEntry $masterBin "master"), (FileEntry $slaveBin "slave"))
}
$manifestPath = Join-Path $dist "firmware_manifest.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding utf8
Write-Host "dist/ pret :" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length -AutoSize | Out-String | Write-Host

# --- Publication GitHub -------------------------------------------------------
if ($Publish) {
    $tag = "fw-v$version"
    git tag $tag 2>$null
    git push origin main --tags
    $relNotes = if ($Notes) { $Notes } else { "Release firmware v$version (maitre + esclave)." }
    gh release create $tag $masterBin $slaveBin $manifestPath `
        --title "Firmware B1 v$version" `
        --notes $relNotes
    Write-Host "Release $tag publiee sur GitHub." -ForegroundColor Green
} else {
    Write-Host "Compilation seulement (ajoute -Publish pour tagger + publier la release GitHub)." -ForegroundColor Yellow
}
