param(
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot

dotnet --info
dotnet restore .\IPTVExplorer.Desktop.sln
dotnet build .\IPTVExplorer.Desktop.sln -c Release --no-restore
dotnet test .\IPTVExplorer.Desktop.sln -c Release --no-build --logger 'console;verbosity=normal'

if ($Launch) {
    dotnet run --project .\src\IPTVExplorer.Desktop\IPTVExplorer.Desktop.csproj -c Release --no-build
}
