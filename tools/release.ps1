# ============================================================================
#  release.ps1 — releases B1 firmware to GitHub (stefe2/B1_Chat)
#
#  NOTE: since .github/workflows/firmware-release.yml, publishing is
#  automatic — bump FW_VERSION in src/config.h, commit, push to main, and CI
#  builds both roles + publishes the GitHub release on its own (no gh auth
#  login or local run required). This script remains useful for a local
#  verification build/manifest, or as a manual fallback if CI is unavailable
#  — avoid running -Publish in addition to CI for the same version (duplicate
#  tag/release).
#
#  Builds BOTH roles (IS_MASTER 1 then 0), produces dist/ with .bin files
#  named by version + firmware_manifest.json (SHA-256, KyberEditor-style),
#  then (with -Publish) tags git fw-vX.Y.Z + creates a GitHub release with the assets.
#
#  Usage:  .\tools\release.ps1 [-Notes "release notes"] [-Publish]
#  Prerequisite for -Publish: gh auth login (once).
#  The version comes from FW_VERSION in src/config.h (source of truth).
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

# --- Version from config.h ------------------------------------------------
$config = Get-Content $configH -Raw
if ($config -notmatch '#define\s+FW_VERSION\s+"([^"]+)"') { throw "FW_VERSION not found in $configH" }
$version = $Matches[1]
Write-Host "Firmware v$version" -ForegroundColor Cyan

# --- Builds a role and copies the binary -------------------------------------
$dist = Join-Path $repo "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

# Remembers the current role to restore it at the end (it lives in config.h).
if ($config -notmatch '#define\s+IS_MASTER\s+(\d)') { throw "IS_MASTER not found in $configH" }
$originalRole = $Matches[1]

function Build-Role([int]$isMaster, [string]$roleName) {
    $c = Get-Content $configH -Raw
    $c = $c -replace '#define\s+IS_MASTER\s+\d', "#define IS_MASTER $isMaster"
    Set-Content $configH $c -NoNewline
    Write-Host "Building $roleName (IS_MASTER $isMaster)..." -ForegroundColor Cyan
    $buildOutput = & $pio run -e b1 2>&1
    if ($LASTEXITCODE -ne 0) { $buildOutput | Write-Host; throw "build failed ($roleName)" }
    $buildOutput | Select-Object -Last 1 | Write-Host
    $out = Join-Path $dist "b1-$roleName-v$version.bin"
    Copy-Item ".pio/build/b1/firmware.bin" $out -Force
    return $out
}

try {
    $masterBin = Build-Role 1 "master"
    $slaveBin  = Build-Role 0 "slave"
} finally {
    # Restores the original role.
    $c = Get-Content $configH -Raw
    $c = $c -replace '#define\s+IS_MASTER\s+\d', "#define IS_MASTER $originalRole"
    Set-Content $configH $c -NoNewline
}

# bootloader + partition table are role-independent (IS_MASTER only affects the app): ship ONE
# shared copy of each — needed to flash a virgin board (offsets 0x1000/0x8000), since the
# app-only flash at 0x10000 assumes they're already present. Taken from the last build.
$bootloaderBin = Join-Path $dist "bootloader.bin"
$partitionsBin = Join-Path $dist "partitions.bin"
Copy-Item ".pio/build/b1/bootloader.bin" $bootloaderBin -Force
Copy-Item ".pio/build/b1/partitions.bin" $partitionsBin -Force

# --- Manifest (KyberEditor-style: version, files, sha256, sizes) -----
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
    files   = @(
        (FileEntry $masterBin "master"),
        (FileEntry $slaveBin "slave"),
        (FileEntry $bootloaderBin "bootloader"),
        (FileEntry $partitionsBin "partitions")
    )
}
$manifestPath = Join-Path $dist "firmware_manifest.json"
$manifest | ConvertTo-Json -Depth 4 | Set-Content $manifestPath -Encoding utf8
Write-Host "dist/ ready:" -ForegroundColor Green
Get-ChildItem $dist | Format-Table Name, Length -AutoSize | Out-String | Write-Host

# --- GitHub publishing -------------------------------------------------------
if ($Publish) {
    $tag = "fw-v$version"
    git tag $tag 2>$null
    git push origin main --tags
    $relNotes = if ($Notes) { $Notes } else { "B1 firmware release v$version (master + slave)." }
    gh release create $tag $masterBin $slaveBin $bootloaderBin $partitionsBin $manifestPath `
        --title "Firmware B1 v$version" `
        --notes $relNotes
    Write-Host "Release $tag published to GitHub." -ForegroundColor Green
} else {
    Write-Host "Build only (add -Publish to tag + publish the GitHub release)." -ForegroundColor Yellow
}
