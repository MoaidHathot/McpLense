#Requires -Version 7.0

<#
.SYNOPSIS
    Build, pack, and (optionally) push the McpLense .NET tool to a NuGet feed.

.DESCRIPTION
    Runs `dotnet pack` against src/McpLense/McpLense.csproj and produces a
    .nupkg under the output directory. With -Push, the packed file is uploaded
    via `dotnet nuget push`.

    The API key resolves in this order:
      1. -ApiKey parameter
      2. $env:NUGET_API_KEY environment variable
    A push fails fast if neither is set.

.PARAMETER Configuration
    MSBuild configuration. Defaults to Release.

.PARAMETER OutputDirectory
    Directory to write the .nupkg into. Defaults to '<repo>/artifacts'.

.PARAMETER ApiKey
    NuGet API key. Falls back to $env:NUGET_API_KEY when omitted.

.PARAMETER Source
    NuGet feed URL. Defaults to https://api.nuget.org/v3/index.json.

.PARAMETER Push
    Upload the produced .nupkg to the feed after packing.

.PARAMETER NoBuild
    Pass --no-build to dotnet pack (assumes a prior build is current).

.PARAMETER NoRestore
    Pass --no-restore to dotnet pack.

.EXAMPLE
    ./pack.ps1
    Builds and packs to ./artifacts. Does not push.

.EXAMPLE
    ./pack.ps1 -Push
    Builds, packs, and pushes using $env:NUGET_API_KEY.

.EXAMPLE
    ./pack.ps1 -Push -ApiKey 'oy2...'
    Builds, packs, and pushes using the supplied API key.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$ApiKey,
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [switch]$Push,
    [switch]$NoBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSCommandPath
$projectPath = Join-Path $repoRoot 'src/McpLense/McpLense.csproj'

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project not found: $projectPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts'
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

# Remove any stale McpLense package from the output directory so we never
# accidentally push a previous version.
Get-ChildItem -LiteralPath $OutputDirectory -Filter 'McpLense.*.nupkg' -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem -LiteralPath $OutputDirectory -Filter 'McpLense.*.snupkg' -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host "Packing McpLense ($Configuration) -> $OutputDirectory" -ForegroundColor Cyan

$packArgs = @(
    'pack', $projectPath,
    '-c', $Configuration,
    '-o', $OutputDirectory
)
if ($NoBuild)   { $packArgs += '--no-build' }
if ($NoRestore) { $packArgs += '--no-restore' }

& dotnet @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack failed (exit $LASTEXITCODE)."
}

$package = Get-ChildItem -LiteralPath $OutputDirectory -Filter 'McpLense.*.nupkg' |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if (-not $package) {
    throw "No McpLense .nupkg produced under '$OutputDirectory'."
}

Write-Host "Created: $($package.FullName)" -ForegroundColor Green

if (-not $Push) {
    Write-Host "Skipping push (use -Push to upload to '$Source')." -ForegroundColor Yellow
    return
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = $env:NUGET_API_KEY
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'Push requested but no API key was provided. Pass -ApiKey, or set the NUGET_API_KEY environment variable.'
}

Write-Host "Pushing $($package.Name) -> $Source" -ForegroundColor Cyan

& dotnet nuget push $package.FullName `
    --api-key $ApiKey `
    --source $Source `
    --skip-duplicate

if ($LASTEXITCODE -ne 0) {
    throw "dotnet nuget push failed (exit $LASTEXITCODE)."
}

Write-Host "Push complete." -ForegroundColor Green
