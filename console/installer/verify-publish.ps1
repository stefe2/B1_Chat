param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory
)

$ErrorActionPreference = "Stop"

$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$helpRoot = Join-Path $publishRoot "Help"
$docsRoot = Join-Path $helpRoot "docs"
$manifestPath = Join-Path $helpRoot "manifest.json"
$appPath = Join-Path $publishRoot "b1-chat-console.exe"
$espflashPath = Join-Path $publishRoot "tools\espflash.exe"
$vcRuntimePath = Join-Path $publishRoot "tools\vcruntime140.dll"
$vcManifestPath = Join-Path $publishRoot "tools\vc-runtime-manifest.json"

foreach ($required in @(
    $appPath,
    $espflashPath,
    $vcRuntimePath,
    $vcManifestPath,
    $manifestPath
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Publish verification failed: missing required file '$required'."
    }
}

$vcManifest = Get-Content -LiteralPath $vcManifestPath -Raw | ConvertFrom-Json
if ($vcManifest.architecture -ne "x64" -or $vcManifest.runtime -ne "Microsoft.VC143.CRT") {
    throw "Publish verification failed: invalid VC runtime manifest metadata."
}
$vcFiles = @($vcManifest.files)
if ($vcFiles.Count -eq 0) {
    throw "Publish verification failed: VC runtime manifest contains no DLLs."
}
foreach ($file in $vcFiles) {
    $name = [string]$file.name
    if ([IO.Path]::GetFileName($name) -ne $name -or -not $name.EndsWith(".dll", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish verification failed: invalid VC runtime filename '$name'."
    }
    $path = Join-Path (Split-Path -Parent $espflashPath) $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Publish verification failed: missing VC runtime DLL '$name'."
    }
    $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$file.sha256).ToLowerInvariant()) {
        throw "Publish verification failed: VC runtime DLL hash mismatch for '$name'."
    }
}

function Get-PeMachine([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw "'$Path' is not a Windows executable." }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x4550) { throw "'$Path' has an invalid PE header." }
        return $reader.ReadUInt16()
    } finally {
        $stream.Dispose()
    }
}

foreach ($executable in @($appPath, $espflashPath)) {
    if ((Get-PeMachine $executable) -ne 0x8664) {
        throw "Publish verification failed: '$executable' is not an x64 executable."
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$pages = @($manifest.sections | ForEach-Object { $_.pages })
if ($pages.Count -eq 0) {
    throw "Publish verification failed: Help manifest contains no pages."
}

$imageCount = 0
foreach ($page in $pages) {
    $relativePage = ([string]$page.file).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $pagePath = [IO.Path]::GetFullPath((Join-Path $docsRoot $relativePage))
    if (-not $pagePath.StartsWith($docsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Publish verification failed: Help page escapes docs directory ('$($page.file)')."
    }
    if (-not (Test-Path -LiteralPath $pagePath -PathType Leaf)) {
        throw "Publish verification failed: missing Help page '$($page.file)'."
    }

    $markdown = Get-Content -LiteralPath $pagePath -Raw
    foreach ($match in [regex]::Matches($markdown, '!\[[^\]]*\]\((?<src>[^)]+)\)')) {
        $source = $match.Groups['src'].Value.Trim()
        if ($source -match '^(?i:https?://|data:)') { continue }

        # Help image references currently contain no optional Markdown title. Decode URI escapes
        # so a valid local filename containing spaces is checked correctly.
        $source = [Uri]::UnescapeDataString($source).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $imagePath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $pagePath) $source))
        if (-not $imagePath.StartsWith($docsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish verification failed: Help image escapes docs directory ('$source')."
        }
        if (-not (Test-Path -LiteralPath $imagePath -PathType Leaf)) {
            throw "Publish verification failed: missing Help image '$source' referenced by '$($page.file)'."
        }
        $imageCount++
    }
}

$appProcess = Start-Process -FilePath $appPath -ArgumentList "--verify-install" -WindowStyle Hidden -Wait -PassThru
if ($appProcess.ExitCode -ne 0) {
    throw "Publish verification failed: application self-check exited with $($appProcess.ExitCode)."
}

$espflashOutput = & $espflashPath --version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Publish verification failed: espflash self-check exited with $LASTEXITCODE. $espflashOutput"
}

Write-Host "Publish verified: x64 app/runtime, $($pages.Count) Help pages, $imageCount local image reference(s), $espflashOutput, $($vcFiles.Count) local VC runtime DLLs (v$($vcManifest.version))." -ForegroundColor Green
