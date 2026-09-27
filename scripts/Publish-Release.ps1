[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot,
    [switch]$DownloadLibMpv,
    [switch]$BuildInstaller,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'dist' }
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$project = Join-Path $repoRoot 'src\IPTVExplorer.Desktop\IPTVExplorer.Desktop.csproj'
$solution = Join-Path $repoRoot 'IPTVExplorer.Desktop.sln'
$mpvDll = Join-Path $repoRoot 'src\IPTVExplorer.Desktop\native\mpv\libmpv-2.dll'
$packageName = "IPTV-Explorer-$Version-$Runtime"
$publishDir = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot ($packageName + '.zip')
$checksumsPath = Join-Path $OutputRoot 'SHA256SUMS.txt'

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version invalide : $Version" }

[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$declaredVersion = [string]$props.Project.PropertyGroup.Version
if ($declaredVersion -ne $Version) {
    throw "La version demandée ($Version) ne correspond pas à Directory.Build.props ($declaredVersion)."
}

if ($DownloadLibMpv -and -not (Test-Path -LiteralPath $mpvDll)) {
    & (Join-Path $PSScriptRoot 'Get-LibMpvRuntime.ps1')
}
if (-not (Test-Path -LiteralPath $mpvDll)) {
    throw "libmpv-2.dll est requis avant le packaging. Exécutez scripts\Get-LibMpvRuntime.ps1."
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
if (Test-Path -LiteralPath $checksumsPath) { Remove-Item -LiteralPath $checksumsPath -Force }

Push-Location $repoRoot
try {
    if (-not $SkipTests) {
        dotnet restore $solution
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore a échoué." }

        dotnet build $solution -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet build a échoué." }

        dotnet test $solution -c $Configuration --no-build --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw "dotnet test a échoué." }
    }

    dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish a échoué." }

    $publishedExe = Join-Path $publishDir 'IPTVExplorer.Desktop.exe'
    $publishedMpv = Join-Path $publishDir 'native\mpv\libmpv-2.dll'
    foreach ($required in @($publishedExe, $publishedMpv)) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Artefact publié manquant : $required" }
    }

    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $publishDir -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination $publishDir -Force
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDir -Force

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $releaseFiles = [System.Collections.Generic.List[string]]::new()
    $releaseFiles.Add($zipPath)

    if ($BuildInstaller) {
        $isccCandidates = [System.Collections.Generic.List[string]]::new()

        $isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($isccCommand -and $isccCommand.Source) {
            $isccCandidates.Add($isccCommand.Source)
        }

        foreach ($candidate in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
        )) {
            if ($candidate) { $isccCandidates.Add($candidate) }
        }

        $localPrograms = Join-Path $env:LOCALAPPDATA 'Programs'
        if (Test-Path -LiteralPath $localPrograms) {
            Get-ChildItem -LiteralPath $localPrograms -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending |
                ForEach-Object {
                    $candidate = Join-Path $_.FullName 'ISCC.exe'
                    if (Test-Path -LiteralPath $candidate) { $isccCandidates.Add($candidate) }
                }
        }

        $iscc = $isccCandidates |
            Where-Object { $_ -and (Test-Path -LiteralPath $_) } |
            Select-Object -Unique |
            Select-Object -First 1

        if (-not $iscc) {
            throw "Inno Setup 6 (ISCC.exe) est requis pour -BuildInstaller. Le script vérifie PATH, Program Files et LOCALAPPDATA\Programs."
        }

        Write-Host "Inno Setup détecté : $iscc"

        $setupIconPath = Join-Path $OutputRoot 'IPTVExplorer.Setup.ico'
        if (Test-Path -LiteralPath $setupIconPath) { Remove-Item -LiteralPath $setupIconPath -Force }

        Add-Type -AssemblyName System.Drawing
        $setupIcon = [System.Drawing.Icon]::ExtractAssociatedIcon($publishedExe)
        if (-not $setupIcon) { throw 'Impossible d’extraire l’icône intégrée de l’application publiée.' }
        try {
            $iconStream = [System.IO.File]::Create($setupIconPath)
            try {
                $setupIcon.Save($iconStream)
            }
            finally {
                $iconStream.Dispose()
            }
        }
        finally {
            $setupIcon.Dispose()
        }

        if (-not (Test-Path -LiteralPath $setupIconPath) -or (Get-Item -LiteralPath $setupIconPath).Length -lt 64) {
            throw 'L’icône temporaire de l’installateur est invalide.'
        }

        $installerScript = Join-Path $repoRoot 'installer\IPTVExplorer.iss'
        try {
            & $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$OutputRoot" $installerScript
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup a échoué (code $LASTEXITCODE)." }
        }
        finally {
            if (Test-Path -LiteralPath $setupIconPath) { Remove-Item -LiteralPath $setupIconPath -Force }
        }

        $setupPath = Join-Path $OutputRoot "IPTV-Explorer-Setup-$Version-x64.exe"
        if (-not (Test-Path -LiteralPath $setupPath)) { throw "Installateur attendu introuvable : $setupPath" }
        $releaseFiles.Add($setupPath)
    }

    $checksumLines = foreach ($file in $releaseFiles) {
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $([System.IO.Path]::GetFileName($file))"
    }
    Set-Content -LiteralPath $checksumsPath -Value $checksumLines -Encoding ascii

    Write-Host ''
    Write-Host 'Release prête :'
    $releaseFiles | ForEach-Object { Write-Host " - $_" }
    Write-Host " - $checksumsPath"
}
finally {
    Pop-Location
}
