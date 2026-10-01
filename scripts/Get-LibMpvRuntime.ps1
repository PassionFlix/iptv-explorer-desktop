[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$VerifyOnly,
    [string]$ManifestPath,
    [string]$TargetDirectory,
    [scriptblock]$DownloadAction,
    [scriptblock]$ExtractAction
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ($Force -and $VerifyOnly) { throw '-Force et -VerifyOnly sont incompatibles.' }
if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'build\libmpv-runtime.json' }
if (-not $TargetDirectory) { $TargetDirectory = Join-Path $repoRoot 'src\IPTVExplorer.Desktop\native\mpv' }

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

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Manifest libmpv introuvable : $ManifestPath"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
foreach ($property in @('assetName', 'downloadUrl', 'sha256', 'expectedLibrary', 'librarySha256')) {
    if ([string]::IsNullOrWhiteSpace([string]$manifest.$property)) { throw "Manifest libmpv incomplet : $property est requis." }
}
$targetDll = Join-Path $TargetDirectory ([string]$manifest.expectedLibrary)
$expectedLibraryHash = ([string]$manifest.librarySha256).ToLowerInvariant()

function Test-ExpectedLibrary([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -le 0) { return $false }
    return (Get-Sha256 $Path) -eq $expectedLibraryHash
}

$runtimeValid = Test-ExpectedLibrary $targetDll
if ($runtimeValid -and -not $Force) {
    Write-Host "Runtime libmpv vérifié : $targetDll"
    return
}
if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath $targetDll)) { throw "Runtime libmpv absent : $targetDll" }
    throw "Runtime libmpv invalide ou non conforme au manifeste : $targetDll"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('iptv-explorer-libmpv-' + [Guid]::NewGuid().ToString('N'))
$archive = Join-Path $tempRoot $manifest.assetName
$extract = Join-Path $tempRoot 'extract'
$stagedRuntime = Join-Path $tempRoot 'runtime'

try {
    New-Item -ItemType Directory -Force -Path $tempRoot, $extract, $stagedRuntime | Out-Null
    Write-Host "Téléchargement de $($manifest.assetName)..."
    if ($DownloadAction) { & $DownloadAction ([string]$manifest.downloadUrl) $archive }
    else { Invoke-WebRequest -Uri $manifest.downloadUrl -OutFile $archive -UseBasicParsing }

    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Archive libmpv absente après téléchargement.' }

    $actualHash = Get-Sha256 $archive
    $expectedHash = [string]$manifest.sha256
    if ($actualHash -ne $expectedHash.ToLowerInvariant()) {
        throw "SHA-256 libmpv invalide. Attendu: $expectedHash ; obtenu: $actualHash"
    }

    if ($ExtractAction) {
        & $ExtractAction $archive $extract
    }
    else {
        $sevenZipCandidates = [System.Collections.Generic.List[string]]::new()
        $sevenZipCommand = Get-Command 7z -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($sevenZipCommand -and $sevenZipCommand.Source) { $sevenZipCandidates.Add($sevenZipCommand.Source) }
        foreach ($candidate in @(
            (Join-Path $env:ProgramFiles '7-Zip\7z.exe'),
            (Join-Path ${env:ProgramFiles(x86)} '7-Zip\7z.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\7-Zip\7z.exe')
        )) {
            if ($candidate) { $sevenZipCandidates.Add($candidate) }
        }
        $sevenZip = $sevenZipCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique | Select-Object -First 1
        if (-not $sevenZip) { throw "7-Zip x64 est requis pour extraire le runtime libmpv. Installez 7-Zip ou ajoutez 7z.exe au PATH." }
        & $sevenZip x $archive "-o$extract" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Échec de l'extraction libmpv (code $LASTEXITCODE)." }
    }

    $library = Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $manifest.expectedLibrary | Select-Object -First 1
    if (-not $library) { throw "$($manifest.expectedLibrary) absent de l'archive téléchargée." }
    if ($library.Length -le 0) { throw "$($manifest.expectedLibrary) est vide dans l'archive téléchargée." }
    $actualLibraryHash = Get-Sha256 $library.FullName
    if ($actualLibraryHash -ne $expectedLibraryHash) {
        throw "SHA-256 de $($manifest.expectedLibrary) invalide. Attendu: $expectedLibraryHash ; obtenu: $actualLibraryHash"
    }

    Get-ChildItem -Path $library.Directory.FullName -File -Filter '*.dll' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stagedRuntime $_.Name) -Force
    }

    New-Item -ItemType Directory -Force -Path $TargetDirectory | Out-Null
    Get-ChildItem -LiteralPath $TargetDirectory -File -Filter '*.dll' -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $stagedRuntime -File -Filter '*.dll' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $TargetDirectory $_.Name) -Force
    }
    if (-not (Test-ExpectedLibrary $targetDll)) { throw "Le runtime libmpv installé n'est pas conforme au manifeste : $targetDll" }

    Write-Host "Runtime libmpv téléchargé et vérifié : $targetDll"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
