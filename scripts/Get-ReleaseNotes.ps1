[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$ChangelogPath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $ChangelogPath) { $ChangelogPath = Join-Path $repoRoot 'CHANGELOG.md' }
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot "dist\release-notes-$Version.md" }

$ChangelogPath = [System.IO.Path]::GetFullPath($ChangelogPath)
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $ChangelogPath -PathType Leaf)) {
    throw "Changelog introuvable : $ChangelogPath"
}

$lines = [System.IO.File]::ReadAllLines($ChangelogPath, [System.Text.Encoding]::UTF8)
$escapedVersion = [System.Text.RegularExpressions.Regex]::Escape($Version)
$headingPattern = "^## \[$escapedVersion\](?: - \d{4}-\d{2}-\d{2})?\s*$"
$headingIndexes = @()
for ($index = 0; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match $headingPattern) { $headingIndexes += $index }
}
if ($headingIndexes.Count -eq 0) { throw "Aucune section CHANGELOG trouvée pour la version $Version." }
if ($headingIndexes.Count -gt 1) { throw "Plusieurs sections CHANGELOG existent pour la version $Version." }

$start = [int]$headingIndexes[0]
$end = $lines.Count
for ($index = $start + 1; $index -lt $lines.Count; $index++) {
    if ($lines[$index] -match '^## \[[^]]+\](?:\s+-\s+.*)?\s*$') {
        $end = $index
        break
    }
}

$body = if ($start + 1 -lt $end) { @($lines[($start + 1)..($end - 1)]) } else { @() }
$meaningful = @($body | Where-Object {
    $trimmed = $_.Trim()
    $trimmed -and $trimmed -notmatch '^#{1,6}\s+' -and $trimmed -match '[\p{L}\p{N}]'
})
if ($meaningful.Count -eq 0) { throw "La section CHANGELOG de la version $Version est vide." }

$section = @($lines[$start..($end - 1)])
while ($section.Count -gt 0 -and [string]::IsNullOrWhiteSpace($section[-1])) {
    if ($section.Count -eq 1) { $section = @(); break }
    $section = @($section[0..($section.Count - 2)])
}
$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory) { [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null }
$content = ($section -join "`n") + "`n"
[System.IO.File]::WriteAllText($OutputPath, $content, [System.Text.UTF8Encoding]::new($false))
Write-Output $OutputPath
