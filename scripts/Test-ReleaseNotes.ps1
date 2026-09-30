[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$extractor = Join-Path $PSScriptRoot 'Get-ReleaseNotes.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("iptv-release-notes-" + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Échec du test release notes : $Message" }
}

function Assert-Throws([scriptblock]$Action, [string]$ExpectedMessage) {
    $didThrow = $false
    try {
        & $Action
    }
    catch {
        $didThrow = $true
        if ($_.Exception.Message -notlike "*$ExpectedMessage*") { throw }
    }
    if (-not $didThrow) { throw "Échec du test release notes : erreur attendue contenant '$ExpectedMessage'." }
}

try {
    # 1. La section historique réelle 1.0.0 est extractible.
    $oneOutput = Join-Path $temporaryRoot '1.0.0.md'
    & $extractor -Version '1.0.0' -ChangelogPath (Join-Path $repoRoot 'CHANGELOG.md') -OutputPath $oneOutput | Out-Null
    $one = Get-Content -LiteralPath $oneOutput -Raw
    Assert-True ($one -match '(?m)^## \[1\.0\.0\] - 2026-09-28$') 'section 1.0.0 absente ou datée incorrectement.'
    Assert-True ($one -notmatch '(?m)^## \[Unreleased\]') 'la section précédente a été incluse.'

    $fixture = Join-Path $temporaryRoot 'fixture.md'
    [System.IO.File]::WriteAllText($fixture, @'
# Changelog

## [9.9.9] - 2099-01-02

### Added

- Fonction fictive destinée au test.

## [8.8.8] - 2098-01-02

### Fixed

- Cette section ne doit pas être incluse.

## [7.7.7] - 2097-01-02

### Added
'@, [System.Text.UTF8Encoding]::new($false))

    # 2. Une version fictive est extraite avec son titre et son contenu.
    $fixtureOutput = Join-Path $temporaryRoot '9.9.9.md'
    & $extractor -Version '9.9.9' -ChangelogPath $fixture -OutputPath $fixtureOutput | Out-Null
    $fiction = Get-Content -LiteralPath $fixtureOutput -Raw
    Assert-True ($fiction -match '(?m)^## \[9\.9\.9\] - 2099-01-02$') 'titre fictif absent.'
    Assert-True ($fiction -match 'Fonction fictive') 'contenu fictif absent.'

    # 3. L'extraction s'arrête avant la section suivante.
    Assert-True ($fiction -notmatch '8\.8\.8|Cette section ne doit pas') 'la section suivante a été incluse.'

    # 4. Une version inexistante échoue explicitement.
    Assert-Throws { & $extractor -Version '6.6.6' -ChangelogPath $fixture -OutputPath (Join-Path $temporaryRoot 'missing.md') | Out-Null } 'Aucune section CHANGELOG'

    # 5. Une section ne contenant que des sous-titres est considérée vide.
    Assert-Throws { & $extractor -Version '7.7.7' -ChangelogPath $fixture -OutputPath (Join-Path $temporaryRoot 'empty.md') | Out-Null } 'est vide'

    # 6. Le workflow ne peut plus revenir aux notes 1.0.0 codées en dur.
    $workflow = Get-Content -LiteralPath (Join-Path $repoRoot '.github\workflows\release.yml') -Raw
    Assert-True (-not $workflow.Contains('release-notes-v1.0.0.md', [StringComparison]::OrdinalIgnoreCase)) 'ancienne note codée en dur encore référencée.'
    Assert-True ($workflow.Contains('Get-ReleaseNotes.ps1', [StringComparison]::Ordinal)) 'extracteur absent du workflow.'

    Write-Host 'Tests changelog/release notes : 6 réussis.'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
