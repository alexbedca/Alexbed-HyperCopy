<#
.SYNOPSIS
  Regenerates the 4 README screenshots (docs/screenshots) and the social card (docs/social-preview.png)
  from the built exe in dist\. Run for EVERY release (the images show the version number), then look at each one.

.EXAMPLE
  pwsh tools/readme-screenshots.ps1
#>
param(
    [string]$Demo = 'C:\Users\Public\hc-demo',
    [string]$HddDest = 'G:\hc-demo\Backup',
    [switch]$KeepDemoData
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'dist\AlexbedHyperCopy.exe'
$out = Join-Path $repo 'docs\screenshots'
$rng = [Random]::new(2026)
function New-File($p, $s) {
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    $b = [byte[]]::new([Math]::Min($s, 8MB)); $f = [IO.File]::Create($p); $l = $s
    while ($l -gt 0) { $rng.NextBytes($b); $n = [int][Math]::Min($l, $b.Length); $f.Write($b, 0, $n); $l -= $n }; $f.Dispose()
}
if (-not (Test-Path "$Demo\Photos 2026")) {
    $months = 'January','February','March','April','May','June','July','August','September','October','November','December'
    for ($i = 0; $i -lt 400; $i++) { New-File ("$Demo\Photos 2026\{0}\IMG_{1:0000}.jpg" -f $months[$i % 12], (1000 + $i)) ($rng.Next(3000, 12000) * 1KB) }
    for ($i = 0; $i -lt 4; $i++) { New-File "$Demo\Photos 2026\Videos\VID_$i.mp4" (180MB + $rng.Next(0, 50MB)) }
}
if (-not (Test-Path "$Demo\Holiday 2026.mp4")) { New-File "$Demo\Holiday 2026.mp4" 6GB }

function Shot($name, $delay, [hashtable]$envs, [string[]]$a) {
    $psi = [Diagnostics.ProcessStartInfo]::new($exe)
    foreach ($x in $a) { $psi.ArgumentList.Add($x) }
    $psi.Environment['HYPERCOPY_SNAPSHOT'] = "$out\$name.png"
    $psi.Environment['HYPERCOPY_SNAPSHOT_DELAY'] = "$delay"
    foreach ($k in $envs.Keys) { $psi.Environment[$k] = $envs[$k] }
    $p = [Diagnostics.Process]::Start($psi); $p.WaitForExit(); "$name done"
}
foreach ($d in "$Demo\Backup", $HddDest) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

Shot 'welcome' 1500 @{} @()
Shot 'setup' 1500 @{} @("$Demo\Photos 2026", "$Demo\Holiday 2026.mp4", '--to', $HddDest)
Shot 'copying' 12000 @{ HYPERCOPY_AUTOSTART = '1'; HYPERCOPY_VERIFY = '1' } @("$Demo\Holiday 2026.mp4", '--to', "$Demo\Backup")
Remove-Item "$Demo\Backup" -Recurse -Force -ErrorAction SilentlyContinue
Shot 'complete' 60000 @{ HYPERCOPY_AUTOSTART = '1'; HYPERCOPY_VERIFY = '1' } @("$Demo\Photos 2026", '--to', "$Demo\Backup")

python -I (Join-Path $PSScriptRoot 'make_social_preview.py') (Join-Path $repo 'assets\logo.png') "$out\complete.png" (Join-Path $repo 'docs\social-preview.png')
if (-not $KeepDemoData) { foreach ($d in $Demo, $HddDest) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } } }
