# Builds dist\SnagitCraft-Setup-<version>.exe
#   1. dotnet publish (Release, win-x64, self-contained → no .NET needed on the target PC)
#   2. Inno Setup compiler (ISCC) on installer\SnagitCraft.iss
# Requires: .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup).
param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proj = Join-Path $root 'SnagitAnything.csproj'
if (-not $Version) { $Version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$found = Get-Command dotnet -ErrorAction SilentlyContinue
if ($found) { $dotnet = $found.Source }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

$publish = Join-Path $root 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $dotnet publish $proj -c Release -r win-x64 --self-contained true -p:Version=$Version -p:DebugType=none -o $publish -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'SnagitCraft.iss')
if ($LASTEXITCODE) { throw "ISCC failed ($LASTEXITCODE)" }

$setup = Join-Path $root "dist\SnagitCraft-Setup-$Version.exe"
"Installer: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
