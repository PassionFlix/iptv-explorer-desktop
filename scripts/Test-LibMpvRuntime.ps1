[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtimeScript = Join-Path $PSScriptRoot 'Get-LibMpvRuntime.ps1'
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('iptv-explorer-libmpv-tests-' + [Guid]::NewGuid().ToString('N'))
$passed = 0

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-Hash([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant()
}

function New-Fixture([string]$Name, [string]$ArchiveContent = 'fixture-runtime') {
    $root = Join-Path $temporaryRoot $Name
    $target = Join-Path $root 'target'
    $archive = Join-Path $root 'fixture.7z'
    $manifest = Join-Path $root 'manifest.json'
    New-Item -ItemType Directory -Force -Path $root, $target | Out-Null
    Set-Content -LiteralPath $archive -Value $ArchiveContent -NoNewline
    $hash = Get-Hash $archive
    @{
        sourceRepository = 'https://example.invalid/fixture'
        releaseTag = 'fixture'
        assetName = 'fixture.7z'
        downloadUrl = $archive
        sha256 = $hash
        expectedLibrary = 'libmpv-2.dll'
        librarySha256 = $hash
        licenseLabel = 'fixture'
    } | ConvertTo-Json | Set-Content -LiteralPath $manifest
    return [pscustomobject]@{ Root = $root; Target = $target; Archive = $archive; Manifest = $manifest; Hash = $hash }
}

$download = { param($source, $destination) Copy-Item -LiteralPath $source -Destination $destination -Force }
$extract = {
    param($archive, $destination)
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Copy-Item -LiteralPath $archive -Destination (Join-Path $destination 'libmpv-2.dll') -Force
}

try {
    New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null

    $fixture = New-Fixture 'absent'
    & $runtimeScript -ManifestPath $fixture.Manifest -TargetDirectory $fixture.Target -DownloadAction $download -ExtractAction $extract
    Assert-True (Test-Path -LiteralPath (Join-Path $fixture.Target 'libmpv-2.dll')) "Le runtime absent n’a pas été installé."
    $passed++

    $fixture = New-Fixture 'valid'
    Copy-Item -LiteralPath $fixture.Archive -Destination (Join-Path $fixture.Target 'libmpv-2.dll')
    & $runtimeScript -ManifestPath $fixture.Manifest -TargetDirectory $fixture.Target `
        -DownloadAction { throw 'Un runtime valide ne doit pas être téléchargé.' } `
        -ExtractAction { throw 'Un runtime valide ne doit pas être extrait.' }
    $passed++

    $fixture = New-Fixture 'corrupt'
    Set-Content -LiteralPath (Join-Path $fixture.Target 'libmpv-2.dll') -Value 'corrupt' -NoNewline
    & $runtimeScript -ManifestPath $fixture.Manifest -TargetDirectory $fixture.Target -DownloadAction $download -ExtractAction $extract
    Assert-True ((Get-Hash (Join-Path $fixture.Target 'libmpv-2.dll')) -eq $fixture.Hash) "Le runtime corrompu n’a pas été remplacé."
    $passed++

    $fixture = New-Fixture 'force'
    Copy-Item -LiteralPath $fixture.Archive -Destination (Join-Path $fixture.Target 'libmpv-2.dll')
    $marker = $fixture.Archive + '.downloaded'
    $forceDownload = { param($source, $destination) Set-Content -LiteralPath ($source + '.downloaded') -Value 'yes'; Copy-Item -LiteralPath $source -Destination $destination -Force }
    & $runtimeScript -Force -ManifestPath $fixture.Manifest -TargetDirectory $fixture.Target -DownloadAction $forceDownload -ExtractAction $extract
    Assert-True (Test-Path -LiteralPath $marker) "-Force n’a pas retéléchargé le runtime."
    $passed++

    $fixture = New-Fixture 'bad-archive'
    $manifest = Get-Content -LiteralPath $fixture.Manifest -Raw | ConvertFrom-Json
    $manifest.sha256 = ('0' * 64)
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $fixture.Manifest
    $thrown = $false
    try { & $runtimeScript -ManifestPath $fixture.Manifest -TargetDirectory $fixture.Target -DownloadAction $download -ExtractAction $extract }
    catch { $thrown = $_.Exception.Message -like 'SHA-256 libmpv invalide*' }
    Assert-True $thrown 'Une archive au mauvais SHA-256 a été acceptée.'
    $passed++

    $fixture = New-Fixture 'missing-library'
    $thrown = $false
    try {
        & $runtimeScript -ManifestPath $fixture.Manifest -TargetDirectory $fixture.Target -DownloadAction $download `
            -ExtractAction { param($archive, $destination) New-Item -ItemType Directory -Force -Path $destination | Out-Null }
    }
    catch { $thrown = $_.Exception.Message -like '*absent de l*archive*' }
    Assert-True $thrown 'Une archive sans DLL a été acceptée.'
    $passed++

    $publish = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Publish-Release.ps1') -Raw
    Assert-True ($publish.Contains("if (`$DownloadLibMpv) { & (Join-Path `$PSScriptRoot 'Get-LibMpvRuntime.ps1') -Force }")) "Le packaging -DownloadLibMpv ne force pas un runtime propre."
    Assert-True ($publish.Contains("else { & (Join-Path `$PSScriptRoot 'Get-LibMpvRuntime.ps1') -VerifyOnly }")) "Le packaging sans téléchargement ne refuse pas un runtime inconnu."
    $passed++

    Write-Host "Tests intégrité libmpv : $passed réussis."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
