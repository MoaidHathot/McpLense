#Requires -Version 7.0

<#
.SYNOPSIS
    Build, pack, and (optionally) push both McpLense (library) and McpLense.Cli (tool)
    NuGet packages.

.DESCRIPTION
    Runs `dotnet pack` against:
      - src/McpLense/McpLense.csproj      (library, consumed via PackageReference)
      - src/McpLense.Cli/McpLense.Cli.csproj  (dotnet tool, installed via `dotnet tool install`)

    Both produce .nupkg files under the output directory. With -Push, each is uploaded
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
    Upload the produced .nupkg files to the feed after packing.

.PARAMETER NoBuild
    Pass --no-build to dotnet pack (assumes a prior build is current).

.PARAMETER NoRestore
    Pass --no-restore to dotnet pack.

.PARAMETER LibraryOnly
    Pack only the library; skip the CLI tool. Useful when iterating on extension API.

.PARAMETER CliOnly
    Pack only the CLI tool; skip the library. Useful when iterating on CLI UX.

.EXAMPLE
    ./pack.ps1
    Builds and packs both library + CLI to ./artifacts. Does not push.

.EXAMPLE
    ./pack.ps1 -Push
    Builds, packs, and pushes BOTH packages using $env:NUGET_API_KEY.

.EXAMPLE
    ./pack.ps1 -LibraryOnly
    Pack only the library (for extension authors iterating against a local feed).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [string]$ApiKey,
    [string]$Source = 'https://api.nuget.org/v3/index.json',
    [switch]$Push,
    [switch]$NoBuild,
    [switch]$NoRestore,
    [switch]$LibraryOnly,
    [switch]$CliOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSCommandPath
$libraryProject = Join-Path $repoRoot 'src/McpLense/McpLense.csproj'
$cliProject = Join-Path $repoRoot 'src/McpLense.Cli/McpLense.Cli.csproj'

if ($LibraryOnly -and $CliOnly) {
    throw 'Specify at most one of -LibraryOnly / -CliOnly.'
}

$packLibrary = -not $CliOnly
$packCli = -not $LibraryOnly

if ($packLibrary -and -not (Test-Path -LiteralPath $libraryProject)) {
    throw "Library project not found: $libraryProject"
}

if ($packCli -and -not (Test-Path -LiteralPath $cliProject)) {
    throw "CLI project not found: $cliProject"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts'
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
}

# Remove stale packages so we never accidentally push a previous version.
Get-ChildItem -LiteralPath $OutputDirectory -Filter 'McpLense.*.nupkg' -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem -LiteralPath $OutputDirectory -Filter 'McpLense.*.snupkg' -ErrorAction SilentlyContinue |
    Remove-Item -Force

function Invoke-Pack {
    param(
        [Parameter(Mandatory)] [string]$ProjectPath,
        [Parameter(Mandatory)] [string]$Label
    )

    Write-Host "Packing $Label ($Configuration) -> $OutputDirectory" -ForegroundColor Cyan

    $packArgs = @(
        'pack', $ProjectPath,
        '-c', $Configuration,
        '-o', $OutputDirectory
    )
    if ($NoBuild)   { $packArgs += '--no-build' }
    if ($NoRestore) { $packArgs += '--no-restore' }

    & dotnet @packArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet pack failed for ${Label} (exit $LASTEXITCODE)."
    }
}

if ($packLibrary) { Invoke-Pack -ProjectPath $libraryProject -Label 'McpLense (library)' }
if ($packCli)     { Invoke-Pack -ProjectPath $cliProject     -Label 'McpLense.Cli (dotnet tool)' }

$produced = Get-ChildItem -LiteralPath $OutputDirectory -Filter 'McpLense*.nupkg' |
    Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
    Sort-Object LastWriteTimeUtc -Descending

if (-not $produced -or $produced.Count -eq 0) {
    throw "No McpLense .nupkg produced under '$OutputDirectory'."
}

foreach ($pkg in $produced) {
    Write-Host "Created: $($pkg.FullName)" -ForegroundColor Green
}

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

foreach ($pkg in $produced) {
    Write-Host "Pushing $($pkg.Name) -> $Source" -ForegroundColor Cyan

    & dotnet nuget push $pkg.FullName `
        --api-key $ApiKey `
        --source $Source `
        --skip-duplicate

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet nuget push failed for $($pkg.Name) (exit $LASTEXITCODE)."
    }
}

Write-Host "Push complete." -ForegroundColor Green
