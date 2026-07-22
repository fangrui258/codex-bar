param(
    [ValidateSet('win-x64','win-arm64')] [string]$Runtime = 'win-x64',
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$publishDir = Join-Path $projectRoot 'publish'
$distDir = Join-Path $projectRoot 'dist'

$selfContained = if ($Portable) { 'true' } else { 'false' }
dotnet publish (Join-Path $projectRoot 'CodexBar.csproj') -c Release -r $Runtime --self-contained $selfContained -p:PublishSingleFile=true -o $publishDir

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir 'CodexBar.exe') -Destination (Join-Path $distDir 'CodexBar.exe') -Force
Write-Host "Built $distDir\CodexBar.exe"
if (-not $Portable) { Write-Host 'Requires the .NET 10 Desktop Runtime. Use -Portable to bundle it.' }
