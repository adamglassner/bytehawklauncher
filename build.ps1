# Builds Bytehawk Studio Launcher:
#   1. publishes the self-contained single-file exe -> dist\BytehawkStudioLauncher.exe
#   2. compiles the Inno Setup installer           -> dist\BytehawkStudioLauncher-<version>-Setup.exe
# Requirements: .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)
#
#   .\build.ps1                     # version from the .csproj (1.0.0)
#   .\build.ps1 -Version 1.0.1      # override (CI passes the release tag)
#   .\build.ps1 -SkipInstaller      # exe only
param(
    [string]$Version,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\BytehawkStudioLauncher\BytehawkStudioLauncher.csproj'
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist 'publish'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')" }
Write-Host "Version $Version"

# Find the .NET SDK (also works right after a fresh install, before PATH is refreshed)
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $dotnet = @("$env:ProgramFiles\dotnet\dotnet.exe", "$env:USERPROFILE\.dotnet\dotnet.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $dotnet) { throw '.NET 8 SDK not found. Install it with: winget install Microsoft.DotNet.SDK.8' }

Write-Host '== Publishing single-file exe ==' -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -p:AssemblyVersion="$Version.0" -p:FileVersion="$Version.0" -p:InformationalVersion=$Version `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item (Join-Path $publish 'BytehawkStudioLauncher.exe') (Join-Path $dist 'BytehawkStudioLauncher.exe') -Force
Write-Host "Built $dist\BytehawkStudioLauncher.exe"

if ($SkipInstaller) { return }

Write-Host '== Compiling installer ==' -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc "/DPublishDir=$publish" "/DMyAppVersion=$Version" (Join-Path $root 'installer\BytehawkStudioLauncher.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compile failed' }
Write-Host "Built $dist\BytehawkStudioLauncher-$Version-Setup.exe" -ForegroundColor Green
