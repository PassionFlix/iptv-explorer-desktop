[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$manifestPath = Join-Path $repoRoot 'build\libmpv-runtime.json'
$targetDir = Join-Path $repoRoot 'src\IPTVExplorer.Desktop\native\mpv'
$targetDll = Join-Path $targetDir 'libmpv-2.dll'

function Get-Sha256([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $algorithm.Dispose()
        $stream.Dispose()
    }
}

if ((Test-Path -LiteralPath $targetDll) -and -not $Force) {
    Write-Host "libmpv déjà présent : $targetDll"
    return
}

if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Manifest libmpv introuvable : $manifestPath"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$sevenZipCandidates = [System.Collections.Generic.List[string]]::new()
$sevenZipCommand = Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -First 1
if ($sevenZipCommand -and $sevenZipCommand.Source) {
    $sevenZipCandidates.Add($sevenZipCommand.Source)
}
foreach ($candidate in @(
    (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
    (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe')
)) {
    if ($candidate) { $sevenZipCandidates.Add($candidate) }
}
$sevenZip = $sevenZipCandidates |
    Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
    Select-Object -Unique |
    Select-Object -First 1
if (-not $sevenZip) {
    throw "7-Zip x64 est requis pour extraire le runtime libmpv. Installez 7-Zip ou ajoutez 7z.exe au PATH."
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('iptv-explorer-libmpv-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $tempRoot $manifest.assetName
$extract = Join-Path $tempRoot 'extract'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot, $extract, $targetDir | Out-Null
    Write-Host "Téléchargement de $($manifest.assetName)..."
    Invoke-WebRequest -Uri $manifest.downloadUrl -OutFile $archive -UseBasicParsing

    $actualHash = Get-Sha256 $archive
    $expectedHash = [string]$manifest.sha256
    if ($actualHash -ne $expectedHash.ToLowerInvariant()) {
        throw "SHA-256 libmpv invalide. Attendu: $expectedHash ; obtenu: $actualHash"
    }

    & $sevenZip x $archive "-o$extract" -y | Out-Null
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
