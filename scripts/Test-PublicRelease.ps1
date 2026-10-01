[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'Versioning.ps1')
Push-Location $repoRoot
try {
    $tracked = @(git ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files a échoué.' }

    $forbidden = $tracked | Where-Object {
        $_ -match '(?i)(^|/)(\.env($|\.)|.*\.(sqlite|sqlite3|db|pfx|p12|pem|key)$)' -or
        $_ -match '(?i)^src/IPTVExplorer\.Desktop/native/mpv/.*\.(dll|exe|pdb|zip|7z)$'
    }
    if ($forbidden) {
        throw "Fichiers sensibles/binaires suivis par Git :`n$($forbidden -join "`n")"
    }

    $required = @(
        'LICENSE',
        'README.md',
        'CHANGELOG.md',
        'SECURITY.md',
        'CONTRIBUTING.md',
        'THIRD_PARTY_NOTICES.md',
        'build/libmpv-runtime.json',
        '.github/workflows/ci.yml',
        '.github/workflows/release.yml'
    )
    foreach ($path in $required) {
        if ($tracked -notcontains $path) { throw "Fichier public-release manquant : $path" }
    }

    $legacyVersion = '0.2.0' + '-dev'
    $legacyVersionHits = git grep -n --fixed-strings $legacyVersion -- ':!docs/phase-*' 2>$null
    $grepExitCode = $LASTEXITCODE
    if ($grepExitCode -gt 1) {
        throw "git grep a échoué avec le code $grepExitCode."
    }
    if ($grepExitCode -eq 0 -and $legacyVersionHits) {
        throw "Référence $legacyVersion encore présente :`n$legacyVersionHits"
    }
    $global:LASTEXITCODE = 0

    $projectVersion = Get-ProjectVersion -PropsPath (Join-Path $repoRoot 'Directory.Build.props')

    # Exercise the same validation with the next plausible minor version without editing the project.
    Assert-SupportedSemanticVersion -Version '1.1.0'
    Assert-RequestedProjectVersion -RequestedVersion '1.1.0' -ProjectVersion '1.1.0'

    $publishScript = Get-Content -LiteralPath 'scripts\Publish-Release.ps1' -Raw
    $libMpvScript = Get-Content -LiteralPath 'scripts\Get-LibMpvRuntime.ps1' -Raw
    $libMpvManifest = Get-Content -LiteralPath 'build\libmpv-runtime.json' -Raw | ConvertFrom-Json
    $installerScript = Get-Content -LiteralPath 'installer\IPTVExplorer.iss' -Raw
    $indexHtml = Get-Content -LiteralPath 'src\IPTVExplorer.Desktop\ui\index.html' -Raw
    $polishScript = Get-Content -LiteralPath 'src\IPTVExplorer.Desktop\ui\v1-polish.js' -Raw
    $appScript = Get-Content -LiteralPath 'src\IPTVExplorer.Desktop\ui\app.js' -Raw

    if ($publishScript -match '\[string\]\$Version\s*=\s*[''\"]') { throw 'Publish-Release.ps1 contient encore une version par défaut codée en dur.' }
    if ($publishScript -notmatch 'Get-ProjectVersion' -or $publishScript -notmatch '/DAppVersion=\$Version' -or $publishScript -notmatch '/DSourceDir=\$publishDir') {
        throw 'Publish-Release.ps1 ne propage pas intégralement la version/provenance vers Inno Setup.'
    }
    if ($publishScript -notmatch 'Get-LibMpvRuntime\.ps1''\) -Force' -or $publishScript -notmatch 'Get-LibMpvRuntime\.ps1''\) -VerifyOnly') {
        throw 'Publish-Release.ps1 doit forcer un téléchargement vérifié ou refuser un runtime local inconnu.'
    }
    foreach ($hashName in @('sha256', 'librarySha256')) {
        $hash = [string]$libMpvManifest.$hashName
        if ($hash -notmatch '^[a-fA-F0-9]{64}$') { throw "Empreinte libmpv invalide ou absente : $hashName" }
    }
    if ($libMpvScript -notmatch 'Get-Sha256' -or $libMpvScript -notmatch 'librarySha256') {
        throw 'Get-LibMpvRuntime.ps1 ne vérifie pas les empreintes attendues.'
    }
    if ($installerScript -match '#define\s+AppVersion\s+"' -or $installerScript -match '#define\s+SourceDir\s+"') {
        throw 'IPTVExplorer.iss contient encore une valeur applicative ou source par défaut silencieuse.'
    }
    if ($installerScript -notmatch 'AppVersion must be supplied' -or $installerScript -notmatch 'SourceDir must be supplied') {
        throw 'IPTVExplorer.iss doit refuser une compilation sans paramètres de packaging.'
    }
    if ($indexHtml.Contains('Desktop · 1.0.0') -or $polishScript.Contains('Desktop · 1.0.0')) {
        throw 'La version UI est encore codée en dur.'
    }
    if ($appScript -notmatch 'state\.app\.version' -or $appScript -notmatch 'Desktop · \$\{state\.app\.version\}') {
        throw 'La version UI ne provient pas de app.getState.'
    }

    Write-Host "Audit courant public-release $projectVersion : OK"
    Write-Host "IMPORTANT : exécuter aussi un scanner de secrets sur tout l'historique Git avant de rendre le dépôt public."
}
finally {
    Pop-Location
}
