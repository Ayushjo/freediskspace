param([string]$Version = (Get-Content (Join-Path $PSScriptRoot '..\..\VERSION') -Raw).Trim())
# Builds windows/dist/freedisk.space-setup-<version>-x64.exe: a normal Windows
# installer (Program Files, Start menu entry, uninstall from Settings → Apps).
# Needs Inno Setup 6: winget install JRSoftware.InnoSetup
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'dist\installer-layout'
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'ISCC.exe not found; run: winget install JRSoftware.InnoSetup' }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'app\DiskMap.App') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:Version=$Version -o $publish
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publish" (Join-Path $root 'installer\freedisk.iss')
if ($LASTEXITCODE) { throw 'ISCC failed' }
