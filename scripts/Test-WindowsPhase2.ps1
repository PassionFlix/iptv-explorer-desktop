param(
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Step,

        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @()
    )

    & $FilePath @ArgumentList
    $nativeExitCode = $LASTEXITCODE
    if ($nativeExitCode -ne 0) {
        [Console]::Error.WriteLine("Step '{0}' failed with exit code {1}." -f $Step, $nativeExitCode)
        exit $nativeExitCode
    }
}

Invoke-CheckedCommand -Step 'dotnet --info' -FilePath 'dotnet' -ArgumentList @('--info')
Invoke-CheckedCommand -Step 'dotnet restore' -FilePath 'dotnet' -ArgumentList @('restore', '.\IPTVExplorer.Desktop.sln')
Invoke-CheckedCommand -Step 'dotnet build' -FilePath 'dotnet' -ArgumentList @('build', '.\IPTVExplorer.Desktop.sln', '-c', 'Release', '--no-restore')
Invoke-CheckedCommand -Step 'dotnet test' -FilePath 'dotnet' -ArgumentList @('test', '.\IPTVExplorer.Desktop.sln', '-c', 'Release', '--no-build', '--logger', 'console;verbosity=normal')

if ($Launch) {
    Invoke-CheckedCommand -Step 'application launch' -FilePath 'dotnet' -ArgumentList @('run', '--project', '.\src\IPTVExplorer.Desktop\IPTVExplorer.Desktop.csproj', '-c', 'Release', '--no-build')
}
