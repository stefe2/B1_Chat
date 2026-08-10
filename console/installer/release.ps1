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

# --- espflash + app-local Visual C++ runtime bundle ---------------------------
$espflashSrc = Join-Path $repo "..\tools\espflash.exe"
if (-not (Test-Path "tools\espflash.exe") -and (Test-Path $espflashSrc)) {
    New-Item -ItemType Directory -Force "tools" | Out-Null
    Copy-Item $espflashSrc "tools\espflash.exe"
    Write-Host "tools\espflash.exe copied from tools/ (bundle)." -ForegroundColor Cyan
}
if (-not (Test-Path "tools\espflash.exe" -PathType Leaf)) {
    throw "espflash bundle not found: '$espflashSrc'."
}

# The official espflash Windows binary is linked against the MSVC runtime. A developer PC
# normally has it globally, which previously let release verification pass while clean Windows
# installations failed with 0xC0000135 (DLL not found). Deploy the complete x64 VC143 CRT set
# beside espflash so firmware flashing remains self-contained and needs no VC_redist install.
$vcRedistRoots = @()
foreach ($visualStudioRoot in @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022",
    "C:\Program Files\Microsoft Visual Studio\2022"
)) {
    if (-not (Test-Path -LiteralPath $visualStudioRoot -PathType Container)) { continue }
    $vcRedistRoots += Get-ChildItem -LiteralPath $visualStudioRoot -Directory |
        ForEach-Object { Join-Path $_.FullName "VC\Redist\MSVC" } |
        Where-Object { Test-Path -LiteralPath $_ -PathType Container }
}
$vcCrtCandidates = foreach ($root in $vcRedistRoots) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { continue }
    foreach ($versionDirectory in Get-ChildItem -LiteralPath $root -Directory) {
        $candidate = Join-Path $versionDirectory.FullName "x64\Microsoft.VC143.CRT"
        if (Test-Path -LiteralPath (Join-Path $candidate "vcruntime140.dll") -PathType Leaf) {
            $candidate
        }
    }
}
$vcCrtDirectory = $vcCrtCandidates |
    Sort-Object { [version](Get-Item -LiteralPath (Join-Path $_ "vcruntime140.dll")).VersionInfo.FileVersion } -Descending |
    Select-Object -First 1
if (-not $vcCrtDirectory) {
    throw "Microsoft VC143 x64 redistributable files were not found. Install Visual Studio 2022 Build Tools with the C++ runtime before releasing."
}

$toolBundleDirectory = Join-Path $repo "tools"
New-Item -ItemType Directory -Force -Path $toolBundleDirectory | Out-Null
Get-ChildItem -LiteralPath $toolBundleDirectory -Filter "*.dll" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
$vcDlls = @(Get-ChildItem -LiteralPath $vcCrtDirectory -Filter "*.dll" -File)
if ($vcDlls.Count -eq 0) { throw "No VC143 runtime DLLs found in '$vcCrtDirectory'." }
foreach ($dll in $vcDlls) {
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $toolBundleDirectory $dll.Name) -Force
}

$vcRuntimeVersion = (Get-Item -LiteralPath (Join-Path $toolBundleDirectory "vcruntime140.dll")).VersionInfo.FileVersion
$vcManifest = [ordered]@{
    architecture = "x64"
    runtime = "Microsoft.VC143.CRT"
    version = $vcRuntimeVersion
    files = @($vcDlls | Sort-Object Name | ForEach-Object {
        $bundledPath = Join-Path $toolBundleDirectory $_.Name
        [ordered]@{
            name = $_.Name
            sha256 = (Get-FileHash -LiteralPath $bundledPath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}
$vcManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $toolBundleDirectory "vc-runtime-manifest.json") -Encoding utf8
Write-Host "Bundled $($vcDlls.Count) VC143 x64 runtime DLLs (v$vcRuntimeVersion) beside espflash." -ForegroundColor Cyan

# --- Publish + installer ------------------------------------------------------
Write-Host "dotnet publish (self-contained exe)..." -ForegroundColor Cyan
$publishOutput = dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true 2>&1
if ($LASTEXITCODE -ne 0) { $publishOutput | Write-Host; throw "publish failed" }
$publishOutput | Select-Object -Last 1 | Write-Host

# Fail before NSIS if a runtime file read from disk (Help pages/assets, espflash or its CRT) was
# accidentally bundled, omitted, or otherwise not copied to the publish directory.
& "$PSScriptRoot\verify-publish.ps1" `
    -PublishDirectory "bin\Release\net8.0-windows\win-x64\publish"

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
