<#
.SYNOPSIS
  Captures the HyperCopy window in every important state (pre-release UI check). Look at every PNG afterwards:
  correct version, no overlapping or cut-off text, every number true.

.EXAMPLE
  pwsh tools/ui-states.ps1 -Out C:\hc-bench\ui-shots
#>
param(
    [Parameter(Mandatory)] [string]$Out,
    [string]$Work = 'C:\hc-bench\ui',
    [string]$HddWork = 'G:\hc-bench\ui',
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\AlexbedHyperCopy.exe')
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
foreach ($d in $Work, $HddWork) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
New-Item -ItemType Directory -Force $Out | Out-Null
$rng = [Random]::new(7)
function New-File($p, $s) {
    New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
    $b = [byte[]]::new([Math]::Min([Math]::Max($s, 1), 4MB)); $f = [IO.File]::Create($p); $l = $s
    while ($l -gt 0) { $rng.NextBytes($b); $n = [int][Math]::Min($l, $b.Length); $f.Write($b, 0, $n); $l -= $n }; $f.Dispose()
}
$app = "$Work\src\Alexbed Light Media Player (ALMP)"
for ($i = 0; $i -lt 60; $i++) { New-File "$app\PyQt6\Qt$i.pyd" ($rng.Next(10, 900) * 1KB) }
New-File "$app\runtime\mpv-2.dll" 90MB
New-File "$app\PIL\_avif.pyd" 300KB

function Shot($name, $delay, [hashtable]$envs, [string[]]$a) {
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($x in $a) { $psi.ArgumentList.Add($x) }
    $psi.Environment['HYPERCOPY_SNAPSHOT'] = "$Out\$name.png"
    $psi.Environment['HYPERCOPY_SNAPSHOT_DELAY'] = "$delay"
    foreach ($k in $envs.Keys) { $psi.Environment[$k] = $envs[$k] }
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit(120000)) { $p.Kill(); "TIMEOUT $name" } else { "$name -> $(Test-Path "$Out\$name.png")" }
}

Shot '1-welcome' 1200 @{} @()
Shot '2-same-folder-warning' 1500 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_CONFLICT = 'Overwrite'; HYPERCOPY_AUTOSTART = '1' } @($app, '--to', "$Work\src")
Shot '3-copy-verify-pass' 9000 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_CONFLICT = 'Overwrite'; HYPERCOPY_AUTOSTART = '1' } @($app, '--to', "$Work\backup")
Shot '4-rerun-skip-identical' 5000 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_AUTOSTART = '1' } @($app, '--to', "$Work\backup")
New-File "$app\PIL\_avif.pyd" 333KB
Shot '5-replace-verify-pass' 9000 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_CONFLICT = 'Overwrite'; HYPERCOPY_AUTOSTART = '1' } @($app, '--to', "$Work\backup")
$lock = [IO.File]::Open("$app\runtime\mpv-2.dll", 'Open', 'Read', 'None')
try { Shot '6-all-failed' 9000 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_AUTOSTART = '1' } @("$app\runtime\mpv-2.dll", '--to', "$Work\locked") } finally { $lock.Dispose() }
$lock = [IO.File]::Open("$app\PIL\_avif.pyd", 'Open', 'Read', 'None')
try { Shot '7-partial-fail' 9000 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_AUTOSTART = '1' } @($app, '--to', "$Work\partial") } finally { $lock.Dispose() }
New-File "$Work\big\movie.mkv" 3GB
Shot '8-in-progress' 4000 @{ HYPERCOPY_VERIFY = '1'; HYPERCOPY_AUTOSTART = '1' } @("$Work\big\movie.mkv", '--to', $HddWork)
Shot '9-move-same-drive' 5000 @{ HYPERCOPY_MODE = 'Move'; HYPERCOPY_AUTOSTART = '1' } @("$Work\backup\Alexbed Light Media Player (ALMP)", '--to', "$Work\moved")
# Many files in the list (as after dropping 2,000 files)
$list = "$Work\list.txt"; Get-ChildItem "$Work\moved" -Recurse -File | Select-Object -ExpandProperty FullName | Set-Content $list
Shot '10-many-sources' 2500 @{ HYPERCOPY_ADD_LIST = $list } @('--to', "$Work\many-out")
"moved ok: $(Test-Path "$Work\moved\Alexbed Light Media Player (ALMP)\runtime\mpv-2.dll"), source gone: $(-not (Test-Path "$Work\backup\Alexbed Light Media Player (ALMP)"))"
"no partial file after cancel: $(-not (Test-Path "$HddWork\movie.mkv"))"
foreach ($d in $Work, $HddWork) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }
