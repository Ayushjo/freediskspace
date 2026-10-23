param(
    # The three identity values come from Partner Center → your app →
    # Product management → Product identity. The Store rejects the upload
    # unless they match exactly.
    [Parameter(Mandatory=$true)][string]$IdentityName,
    [Parameter(Mandatory=$true)][string]$Publisher,
    [Parameter(Mandatory=$true)][string]$PublisherDisplayName,
    # Store versions must keep the fourth part 0.
    [string]$Version = (Get-Content (Join-Path $PSScriptRoot '..\..\VERSION') -Raw).Trim() + '.0'
)
# Builds windows/dist/freedisk.space_<version>_x64.msixupload for Microsoft
# Store submission. Unsigned on purpose: the Store signs it on ingestion.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$layout = Join-Path $dist 'msix-layout'
$kit = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\makeappx.exe' |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $kit) { throw 'makeappx.exe not found; install the Windows 10/11 SDK.' }

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
dotnet publish (Join-Path $root 'app\DiskMap.App') -c Release -r win-x64 --self-contained `
    -p:Version=$Version -o $layout
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
$exe = (Get-ChildItem $layout -Filter '*.exe' | Where-Object Name -NotLike 'createdump*' | Select-Object -First 1).Name

# Tile/logo PNGs from the same source as app.ico (see make-icon.ps1).
$source = [System.Drawing.Image]::FromFile((Join-Path $root '..\docs\icon\variant-4.png'))
New-Item -ItemType Directory -Force (Join-Path $layout 'Images') | Out-Null
function Save-Logo([string]$name, [int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'
    $side = [Math]::Min($w, $h)
    $g.DrawImage($source, [int](($w - $side) / 2), [int](($h - $side) / 2), $side, $side)
    $g.Dispose()
    $bmp.Save((Join-Path $layout "Images\$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
try {
    Save-Logo 'StoreLogo' 50 50
    Save-Logo 'Square44x44Logo' 44 44
    Save-Logo 'Square44x44Logo.targetsize-256_altform-unplated' 256 256
    Save-Logo 'Square150x150Logo' 150 150
    Save-Logo 'Wide310x150Logo' 310 150
} finally { $source.Dispose() }

$esc = { param($s) [System.Security.SecurityElement]::Escape($s) }
@"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="$(& $esc $IdentityName)" Publisher="$(& $esc $Publisher)" Version="$Version" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>freedisk.space</DisplayName>
    <PublisherDisplayName>$(& $esc $PublisherDisplayName)</PublisherDisplayName>
    <Logo>Images\StoreLogo.png</Logo>
    <Description>Open-source, fully offline disk-space analyzer.</Description>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources><Resource Language="en-us" /></Resources>
  <Applications>
    <Application Id="FreeDiskSpace" Executable="$exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="freedisk.space" Description="Disk-space analyzer"
          BackgroundColor="transparent" Square150x150Logo="Images\Square150x150Logo.png"
          Square44x44Logo="Images\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Images\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
    <!-- app.manifest is requireAdministrator (WIN-072); packaged apps need this to elevate. -->
    <rescap:Capability Name="allowElevation" />
  </Capabilities>
</Package>
"@ | Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

$msix = Join-Path $dist "freedisk.space_${Version}_x64.msix"
& $kit.FullName pack /o /d $layout /p $msix
if ($LASTEXITCODE) { throw 'makeappx pack failed' }

# .msixupload is a zip around the .msix.
$upload = [IO.Path]::ChangeExtension($msix, '.msixupload')
if (Test-Path $upload) { Remove-Item $upload }
Compress-Archive $msix "$upload.zip"
Move-Item "$upload.zip" $upload
Write-Output "Store package: $upload"
