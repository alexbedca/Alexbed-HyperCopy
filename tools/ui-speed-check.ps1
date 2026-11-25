<#
.SYNOPSIS
  Measures how fast the HyperCopy window opens and whether it freezes with large folders.
  Uses the HYPERCOPY_PERF_LOG test hook of the built exe in dist\. Part of the pre-release checks.

.EXAMPLE
  pwsh tools/ui-speed-check.ps1 -Work C:\hc-bench\ui-speed
#>
param(
    [string]$Work = 'C:\hc-bench\ui-speed',
    [string]$Exe = (Join-Path $PSScriptRoot '..\dist\AlexbedHyperCopy.exe'),
    [int]$Starts = 5,
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if ([IO.Path]::GetFullPath($Work).TrimEnd('\') -eq [IO.Path]::GetPathRoot($Work).TrimEnd('\')) { throw 'Use a folder below the drive root.' }
New-Item -ItemType Directory -Force $Work | Out-Null

function Run-App([string]$name, [hashtable]$envs, [string[]]$appArgs, [int]$delay = 1500) {
    $log = Join-Path $Work "$name.log"
    Remove-Item $log -ErrorAction SilentlyContinue
    $psi = [Diagnostics.ProcessStartInfo]::new($Exe)
    foreach ($a in $appArgs) { $psi.ArgumentList.Add($a) }
    $psi.Environment['HYPERCOPY_PERF_LOG'] = $log
    $psi.Environment['HYPERCOPY_SNAPSHOT'] = (Join-Path $Work "$name.png")
    $psi.Environment['HYPERCOPY_SNAPSHOT_DELAY'] = "$delay"
    foreach ($k in $envs.Keys) { $psi.Environment[$k] = $envs[$k] }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [Diagnostics.Process]::Start($psi)
    if (-not $p.WaitForExit(600000)) { $p.Kill(); throw "$name timed out" }
    [pscustomobject]@{ Name = $name; Wall = $sw.ElapsedMilliseconds; Log = @(Get-Content $log) }
}
function Ms($lines, $pattern) { $l = $lines | Where-Object { $_ -match $pattern } | Select-Object -First 1; if ($l) { [double]($l.Trim() -split '\s+')[0] } else { $null } }

# Test data: 20,000 small files in 40 folders
$many = Join-Path $Work 'src\Many files'
if (-not (Test-Path $many)) {
    Write-Host 'Creating 20,000 test files...'
    $buf = [byte[]]::new(2048); [Random]::new(1).NextBytes($buf)
    for ($d = 0; $d -lt 40; $d++) {
        $dir = Join-Path $many ("folder {0:00}" -f $d); New-Item -ItemType Directory -Force $dir | Out-Null
        for ($i = 0; $i -lt 500; $i++) { [IO.File]::WriteAllBytes((Join-Path $dir "file $i.dat"), $buf) }
    }
}
$all = Get-ChildItem $many -Recurse -File | Select-Object -ExpandProperty FullName

$rows = @()
# 1. Start-up
$first = @(); $loaded = @()
for ($i = 1; $i -le $Starts; $i++) {
    $r = Run-App "start$i" @{} @()
    $first += Ms $r.Log 'first frame drawn'; $loaded += Ms $r.Log 'window loaded'
}
$med = { param($a) ($a | Sort-Object)[[int][Math]::Floor($a.Count / 2)] }
$rows += [pscustomobject]@{ Check = "Window visible (median of $Starts starts)"; Result = '{0:0} ms' -f (& $med $first); Detail = ($first | ForEach-Object { '{0:0}' -f $_ }) -join ', ' }
(Run-App 'start-detail' @{} @()).Log | ForEach-Object { Write-Host "   $_" }

# 1b. First click into the Destination box (typing/IME support starts only there)
$r = Run-App 'focus-dest' @{ HYPERCOPY_FOCUS_DEST = '1' } @() 2500
$t = (Ms $r.Log 'Destination box focused') - (Ms $r.Log 'focusing the Destination')
$stall = ($r.Log | Where-Object { $_ -match 'longest' }) -replace '.*longest ', ''
$rows += [pscustomobject]@{ Check = 'First click into the Destination box'; Result = '{0:0} ms' -f $t; Detail = "longest freeze $stall" }

# 2. Adding many files at once (like dropping them on the window)
foreach ($n in 1000, 5000, 20000) {
    $list = Join-Path $Work "list$n.txt"; $all | Select-Object -First $n | Set-Content $list -Encoding utf8
    $r = Run-App "add$n" @{ HYPERCOPY_ADD_LIST = $list } @() 1000
    $t = (Ms $r.Log 'sources added') - (Ms $r.Log 'adding \d+ sources')
    $stall = ($r.Log | Where-Object { $_ -match 'longest' }) -replace '.*longest ', ''
    $rows += [pscustomobject]@{ Check = "Add $n files at once"; Result = '{0:0} ms' -f $t; Detail = "longest freeze $stall" }
}

# 3. A 20,000-file folder through a full copy (scan, copy, finish)
$out = Join-Path $Work 'out'; if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$r = Run-App 'copy20k' @{ HYPERCOPY_AUTOSTART = '1' } @($many, '--to', $out) 45000
$stalls = @($r.Log | Where-Object { $_ -match 'not responding' })
$copied = (Get-ChildItem $out -Recurse -File -ErrorAction SilentlyContinue).Count
$rows += [pscustomobject]@{ Check = 'Copy a 20,000-file folder'; Result = "$($stalls.Count) freezes > 100 ms"; Detail = (($r.Log | Where-Object { $_ -match 'longest' }) -replace '.*stalls', 'stalls') + "; $copied files copied" }

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if (-not $Keep) { Remove-Item $Work -Recurse -Force }
