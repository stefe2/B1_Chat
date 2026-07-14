# ============================================================================
#  release.ps1 — releases the B1 Chat console to GitHub (stefe2/B1_Chat, tag vX.Y.Z)
#
#  dotnet publish (self-contained exe) + makensis (installer) then, with
#  -Publish, tags git vX.Y.Z + creates a GitHub release with the installer as an asset.
#  Shared repo with the firmware (tag "fw-vX.Y.Z"): the tag prefix
#  distinguishes the two release trains within the same GitHub repo.
#
#  Usage:  .\console\installer\release.ps1 [-Notes "release notes"] [-Publish]
#  Prerequisite: NSIS (makensis); for -Publish: gh auth login (once).
#  The version comes from <VersionPrefix> in b1-chat-console.csproj.
# ============================================================================
param(
    [string]$Notes = "",
    [switch]$Publish
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

# --- Version from the csproj -------------------------------------------------
$csproj = Get-Content "b1-chat-console.csproj" -Raw
if ($csproj -notmatch '<VersionPrefix>([^<]+)</VersionPrefix>') { throw "VersionPrefix not found in the csproj" }
$version = $Matches[1]
Write-Host "Console v$version" -ForegroundColor Cyan

# --- espflash bundle: copied from tools/ (repo root) if missing --------
$espflashSrc = Join-Path $repo "..\tools\espflash.exe"
if (-not (Test-Path "tools\espflash.exe") -and (Test-Path $espflashSrc)) {
    New-Item -ItemType Directory -Force "tools" | Out-Null
    Copy-Item $espflashSrc "tools\espflash.exe"
    Write-Host "tools\espflash.exe copied from tools/ (bundle)." -ForegroundColor Cyan
}

# --- Publish + installer ------------------------------------------------------
Write-Host "dotnet publish (self-contained exe)..." -ForegroundColor Cyan
$publishOutput = dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true 2>&1
if ($LASTEXITCODE -ne 0) { $publishOutput | Write-Host; throw "publish failed" }
$publishOutput | Select-Object -Last 1 | Write-Host

$makensis = @("makensis", "C:\Program Files (x86)\NSIS\makensis.exe", "C:\Program Files\NSIS\makensis.exe") |
    Where-Object { Get-Command $_ -ErrorAction SilentlyContinue } | Select-Object -First 1
if (-not $makensis) { throw "makensis not found (winget install NSIS.NSIS)" }

Write-Host "makensis (installer v$version)..." -ForegroundColor Cyan
$nsisOutput = & $makensis "/DAPPVERSION=$version" "installer\b1-chat-console.nsi" 2>&1
if ($LASTEXITCODE -ne 0) { $nsisOutput | Write-Host; throw "makensis failed" }
$nsisOutput | Select-Object -Last 2 | Write-Host

$setup = "installer\b1-chat-console-setup-$version.exe"
if (-not (Test-Path $setup)) { throw "expected installer not found: $setup" }
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Installer ready: $setup" -ForegroundColor Green
Write-Host "SHA-256: $sha"

# --- GitHub publishing --------------------------------------------------------
if ($Publish) {
    $tag = "v$version"
    git tag $tag 2>$null
    git push origin main --tags
    gh release create $tag $setup `
        --title "B1 Chat Console v$version" `
        --notes ($Notes ? "$Notes`n`nSHA-256: $sha" : "Release v$version.`n`nSHA-256: $sha")
    Write-Host "Release $tag published to GitHub." -ForegroundColor Green
} else {
    Write-Host "Build only (add -Publish to tag + publish the GitHub release)." -ForegroundColor Yellow
}
