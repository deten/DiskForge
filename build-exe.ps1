<#
    build-exe.ps1 — Produces double-clickable, self-contained EXEs in .\dist
    No Visual Studio / SDK-on-PATH required; uses the user-profile .NET 8 SDK.
    Usage:  right-click > Run with PowerShell, or:  powershell -ExecutionPolicy Bypass -File build-exe.ps1
#>
$ErrorActionPreference = 'Stop'
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$dist = Join-Path $root 'dist'

$common = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false'
)

Write-Host 'Publishing DiskForge (GUI app)...' -ForegroundColor Cyan
dotnet publish 'src/DiskForge.App/DiskForge.App.csproj' @common -o $dist
if ($LASTEXITCODE -ne 0) { throw 'App publish failed' }

Write-Host 'Publishing diskforge (CLI)...' -ForegroundColor Cyan
dotnet publish 'src/DiskForge.Cli/DiskForge.Cli.csproj' @common -o (Join-Path $dist 'cli')
if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed' }

Write-Host ''
Write-Host 'Done. Double-click:' -ForegroundColor Green
Write-Host "  $dist\DiskForge.exe        (dashboard)"
Write-Host "  $dist\cli\diskforge.exe    (text disk report)"
