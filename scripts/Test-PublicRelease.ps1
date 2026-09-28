[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
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
    if ($LASTEXITCODE -eq 0 -and $legacyVersionHits) {
        throw "Référence $legacyVersion encore présente :`n$legacyVersionHits"
    }

    [xml]$props = Get-Content -LiteralPath 'Directory.Build.props' -Raw
    if ([string]$props.Project.PropertyGroup.Version -ne '1.0.0') {
        throw "Directory.Build.props n'est pas en version 1.0.0."
    }

    Write-Host 'Audit courant public-release : OK'
    Write-Host "IMPORTANT : exécuter aussi un scanner de secrets sur tout l'historique Git avant de rendre le dépôt public."
}
finally {
    Pop-Location
}
