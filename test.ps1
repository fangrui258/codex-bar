$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$buildState = Join-Path $projectRoot '.build'

$env:DOTNET_CLI_HOME = Join-Path $buildState 'dotnet-cli'
$env:NUGET_PACKAGES = Join-Path $buildState 'nuget-packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

dotnet run `
    --project (Join-Path $projectRoot 'tests\CodexBar.Tests.csproj') `
    --configuration Release `
    --property:NuGetAudit=false

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
