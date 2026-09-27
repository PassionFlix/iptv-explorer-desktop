[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $repoRoot 'build\libmpv-runtime.json'
$targetDir = Join-Path $repoRoot 'src\IPTVExplorer.Desktop\native\mpv'
$targetDll = Join-Path $targetDir 'libmpv-2.dll'

if ((Test-Path -LiteralPath $targetDll) -and -not $Force) {
    Write-Host "libmpv déjà présent : $targetDll"
    return
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Manifest libmpv introuvable : $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
if (-not $sevenZip) {
    throw "7-Zip (commande 7z) est requis pour extraire le runtime libmpv."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('iptv-explorer-libmpv-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $tempRoot $manifest.assetName
$extract = Join-Path $tempRoot 'extract'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot, $extract, $targetDir | Out-Null
    Write-Host "Téléchargement de $($manifest.assetName)..."
    Invoke-WebRequest -Uri $manifest.downloadUrl -OutFile $archive -UseBasicParsing

    $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedHash = [string]$manifest.sha256
    if ($actualHash -ne $expectedHash.ToLowerInvariant()) {
        throw "SHA-256 libmpv invalide. Attendu: $expectedHash ; obtenu: $actualHash"
    }

    & $sevenZip.Source x $archive "-o$extract" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Échec de l'extraction libmpv (code $LASTEXITCODE)." }

    $library = Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $manifest.expectedLibrary | Select-Object -First 1
    if (-not $library) { throw "$($manifest.expectedLibrary) absent de l'archive téléchargée." }

    Get-ChildItem -Path $library.Directory.FullName -File -Filter '*.dll' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $targetDir $_.Name) -Force
    }

    Write-Host "Runtime libmpv prêt : $targetDll"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
