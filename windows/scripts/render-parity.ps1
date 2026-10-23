# Renders every page of the Windows app on the demo PC, the way the macOS harness
# renders the Mac app, for pixel comparison. PowerShell 5.1 compatible.
#
#   python windows\scripts\make-demo-fixture.py          # once: builds C:\DemoPC
#   powershell -File windows\scripts\render-parity.ps1 -Out C:\parity\win
#   powershell -File windows\scripts\render-parity.ps1 -Pages Overview -Popups   # + overview-*-tray/-risky/-rest.png
#
# Each page is one launch: the app scans the demo (logical sizes, fixed 500 GB volume),
# opens the page, saves the content area at 1440x900 @2x and quits. It runs un-elevated
# (RunAsInvoker) and never writes the user's history or settings.
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\..\dist\parity\win'),
    [string]$Demo = 'C:\DemoPC',
    [string[]]$Themes = @('light', 'dark'),
    [string[]]$Pages = @(),
    [string]$Size = '1440x900',
    [int]$Settle = 2500,
    [int]$TimeoutSeconds = 240,
    # Also render the popups next to the Overview shot: tray flyout, risky warning, "Where's the rest?".
    [switch]$Popups
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$exe = Join-Path $repo 'windows\app\DiskMap.App\bin\Debug\net10.0-windows\DiskMap.App.exe'
if (-not (Test-Path $exe)) { throw "Build first: dotnet build windows\DiskMap.Win.slnx ($exe not found)" }
if (-not (Test-Path $Demo)) { throw "Make the demo first: python windows\scripts\make-demo-fixture.py $Demo" }
# `powershell -File` passes -Pages 'A','B' as one string "A,B"; split it back.
$Pages = @($Pages | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim(" '`"") } | Where-Object { $_ })
if ($Pages.Count -eq 0) {
    $Pages = @('Overview', 'Find', 'Biggest Files', 'Biggest Folders', 'Forgotten Files', 'Duplicates',
        'Safe to Review', 'Caches', 'Old Downloads', 'Large Media', 'File Browser', 'Visualize',
        'Developer Storage', 'Applications', 'Snapshots', 'Cleanup')
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
# A clean, private state folder: the user's settings (text size, appearance), scan cache,
# history and Put Back record never leak into a render, and renders never write them.
$state = Join-Path $env:TEMP 'freedisk-render-state'
if (Test-Path $state) { Remove-Item -Recurse -Force $state }
New-Item -ItemType Directory -Force -Path $state | Out-Null

foreach ($theme in $Themes) {
    foreach ($page in $Pages) {
        $key = ($page -replace '[^A-Za-z0-9]', '').ToLowerInvariant()
        $shot = Join-Path $Out ("{0}-{1}.png" -f $key, $theme)
        $trace = Join-Path $Out ("_trace-{0}-{1}.txt" -f $key, $theme)
        $info = New-Object System.Diagnostics.ProcessStartInfo
        $info.FileName = $exe
        $info.UseShellExecute = $false
        $vars = @{
            '__COMPAT_LAYER' = 'RunAsInvoker'; 'DISKMAP_HOME' = $Demo; 'DISKMAP_AUTOSCAN' = $Demo;
            'DISKMAP_PAGE' = $page; 'DISKMAP_SHOT' = $shot; 'DISKMAP_SHOT_SETTLE' = "$Settle";
            'DISKMAP_SHOT_SCALE' = '2'; 'DISKMAP_SIZE' = $Size; 'DISKMAP_THEME' = $theme;
            'DISKMAP_SIZE_BASIS' = 'logical'; 'DISKMAP_DETERMINISTIC' = '1'; 'DISKMAP_TRACE' = $trace;
            'DISKMAP_STATE_DIR' = $state
        }
        if ($Popups -and $page -eq 'Overview') { $vars['DISKMAP_SHOT_POPUPS'] = '1' }
        foreach ($name in $vars.Keys) { $info.EnvironmentVariables[$name] = $vars[$name] }
        $started = Get-Date
        $process = [System.Diagnostics.Process]::Start($info)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill()
            Write-Output ("{0,-18} {1,-5} TIMEOUT" -f $page, $theme)
            continue
        }
        $seconds = [int]((Get-Date) - $started).TotalSeconds
        if (Test-Path $shot) { $status = 'ok' } else { $status = 'NO SHOT' }
        Write-Output ("{0,-18} {1,-5} {2,-7} {3}s" -f $page, $theme, $status, $seconds)
    }
}
