<#
.SYNOPSIS
  Pre-release function check for Alexbed HyperCopy. Runs the REAL built exe (dist\hypercopy.exe)
  through every option on real drives and checks every copied byte, timestamp and attribute.

.DESCRIPTION
  Required before every release (see .claude/skills/hypercopy-release and CONTRIBUTING.md).
  Pass the drives you have; each root must be a folder BELOW a drive root (e.g. E:\hc-test).
  The script creates a test "app build" (~330 MB, 200+ files) and deletes all test folders at the end.

.EXAMPLE
  pwsh tools/release-check.ps1 -Ssd C:\hc-test -Ssd2 E:\hc-test -Hdd G:\hc-test [-Usb H:\hc-test]
#>
param(
    [Parameter(Mandatory)] [string]$Ssd,     # source drive (system SSD)
    [string]$Ssd2,                           # second SSD (cross-volume)
    [string]$Hdd,                            # hard disk
    [string]$Usb,                            # USB stick / exFAT / FAT32
    [string]$Dist = (Join-Path $PSScriptRoot '..\dist'),
    [switch]$Keep
)
$ErrorActionPreference = 'Stop'
$cli = (Resolve-Path (Join-Path $Dist 'hypercopy.exe')).Path
$results = [System.Collections.Generic.List[object]]::new()
$roots = @($Ssd, $Ssd2, $Hdd, $Usb) | Where-Object { $_ }
foreach ($r in $roots) {
    $full = [IO.Path]::GetFullPath($r).TrimEnd('\')
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw "Use a folder below the drive root, not $r" }
}

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    $status = if ($ok) { 'PASS' } elseif ($detail -like 'NOT TESTED*') { 'NOT TESTED' } else { 'FAIL' }
    $results.Add([pscustomobject]@{ Test = $name; Result = $status; Detail = $detail })
    $c = @{ 'PASS' = 'Green'; 'FAIL' = 'Red'; 'NOT TESTED' = 'Yellow' }[$status]
    Write-Host ("[{0}] {1} {2}" -f $status, $name, $detail) -ForegroundColor $c
}

function Hc([string[]]$a) {
    $out = & $cli @a --quiet 2>&1 | Out-String
    [pscustomobject]@{ Code = $LASTEXITCODE; Out = $out }
}

$rng = [Random]::new(42)
function New-File([string]$path, [long]$size) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $fs = [IO.File]::Create($path)
    try {
        $buf = [byte[]]::new([Math]::Min([Math]::Max($size, 1), 4MB)); $left = $size
        while ($left -gt 0) { $rng.NextBytes($buf); $n = [int][Math]::Min($left, $buf.Length); $fs.Write($buf, 0, $n); $left -= $n }
    } finally { $fs.Dispose() }
    # Distinct, old timestamps so we can check they are kept.
    $t = [DateTime]::new(2021, 3, 4, 5, 6, 8, [DateTimeKind]::Utc).AddMinutes($rng.Next(0, 100000))
    [IO.File]::SetCreationTimeUtc($path, $t.AddDays(-30)); [IO.File]::SetLastWriteTimeUtc($path, $t)
}

function New-AppBuild([string]$root) {
    for ($i = 0; $i -lt 150; $i++) { New-File "$root\bin\mod$i.dll" ($rng.Next(1, 200) * 1KB + $rng.Next(0, 1000)) }
    for ($i = 0; $i -lt 30; $i++) { New-File "$root\lib\PyQt6\Qt$i.pyd" ($rng.Next(512, 3072) * 1KB) }
    New-File "$root\runtime\mpv-2.dll" 60MB
    New-File "$root\data\video.bin" (120MB + 4097)
    New-File "$root\data\exact4mb.bin" 4MB
    New-File "$root\data\empty.txt" 0
    New-File "$root\ünïcødé 文件 😀.txt" 1234
    New-File "$root\readonly.cfg" 3000; Set-ItemProperty "$root\readonly.cfg" IsReadOnly $true
    New-File "$root\hidden.ini" 100; (Get-Item "$root\hidden.ini" -Force).Attributes = 'Hidden, Archive'
    New-Item -ItemType Directory -Force "$root\empty\dir" | Out-Null
}

function Snap([string]$root) {
    $root = (Resolve-Path -LiteralPath $root).Path
    $files = Get-ChildItem -LiteralPath $root -Recurse -Force -File | Sort-Object FullName | ForEach-Object {
        $a = $_.Attributes -band ([IO.FileAttributes]'ReadOnly, Hidden')
        '{0}|{1}|{2}|{3}|{4}|{5}' -f $_.FullName.Substring($root.Length), $_.Length,
            (Get-FileHash -LiteralPath $_.FullName -Algorithm MD5).Hash,
            $_.LastWriteTimeUtc.Ticks, $_.CreationTimeUtc.Ticks, $a
    }
    $dirs = Get-ChildItem -LiteralPath $root -Recurse -Force -Directory | ForEach-Object { 'D|' + $_.FullName.Substring($root.Length) } | Sort-Object
    @($files) + @($dirs)
}
function Same($a, $b) { $d = Compare-Object @($a) @($b); if ($d) { ($d | Select-Object -First 3 | Out-String).Trim() } else { $null } }
function Count-Files($root) { (Get-ChildItem -LiteralPath $root -Recurse -Force -File).Count }

foreach ($r in $roots) { if (Test-Path $r) { Get-ChildItem $r -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }; Remove-Item $r -Recurse -Force } }
foreach ($r in $roots) { New-Item -ItemType Directory -Force $r | Out-Null }

Write-Host "Creating test app build in $Ssd\src\App ..."
$src = "$Ssd\src\App"
New-AppBuild $src
$orig = Snap $src
$n = Count-Files $src
Write-Host "$n files"

# 1. Copy SSD -> SSD/HDD/USB with verification
foreach ($pair in @(@('SSD same drive', "$Ssd\dst"), @('SSD -> SSD2', $(if ($Ssd2) { "$Ssd2\dst" })),
                    @('SSD -> HDD', $(if ($Hdd) { "$Hdd\dst" })), @('SSD -> USB', $(if ($Usb) { "$Usb\dst" })))) {
    if (-not $pair[1]) { Check "Copy $($pair[0])" $false 'NOT TESTED (drive not given)'; continue }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Hc @('copy', $src, $pair[1], '--verify')
    $diff = Same $orig (Snap "$($pair[1])\App")
    Check "Copy $($pair[0]) + verify" ($r.Code -eq 0 -and $r.Out -match "PASS — $n file" -and -not $diff) "exit $($r.Code), $([int]$sw.Elapsed.TotalSeconds) s $diff"
}

$dst = "$Ssd\dst"
# 2. Run again: skip identical
$r = Hc @('copy', $src, $dst, '--verify')
Check 'Re-run, Skip identical' ($r.Code -eq 0 -and $r.Out -match "copied 0, skipped $n" -and $r.Out -match 'nothing to check') "exit $($r.Code)"

# 3. New build: 2 changed + 1 new file, Replace + verify (the user's real scenario)
New-File "$src\bin\mod3.dll" 77777; New-File "$src\runtime\mpv-2.dll" (61MB); New-File "$src\bin\new.dll" 1000
$orig = Snap $src; $n = Count-Files $src
$r = Hc @('copy', $src, $dst, '--verify', '--conflict', 'overwrite')
$diff = Same $orig (Snap "$dst\App")
Check 'New build over old, Replace + verify' ($r.Code -eq 0 -and $r.Out -match "copied $n," -and $r.Out -match "PASS — $n file" -and -not $diff) "exit $($r.Code) $diff"

# 4. Skip identical only copies what changed
New-File "$src\bin\mod4.dll" 5555
$r = Hc @('copy', $src, $dst, '--verify')
Check 'Skip identical copies only the changed file' ($r.Code -eq 0 -and $r.Out -match "copied 1, skipped $($n-1)" -and $r.Out -match 'PASS — 1 file') "exit $($r.Code)"

# 5. Skip never touches existing files
New-File "$src\bin\mod5.dll" 6666
$before = (Get-FileHash "$dst\App\bin\mod5.dll").Hash
$r = Hc @('copy', $src, $dst, '--conflict', 'skip')
Check 'Skip leaves existing files alone' ($r.Code -eq 0 -and $r.Out -match "copied 0" -and (Get-FileHash "$dst\App\bin\mod5.dll").Hash -eq $before) "exit $($r.Code)"

# 6. Keep both
$r = Hc @('copy', "$src\bin\mod5.dll", "$dst\App\bin", '--conflict', 'keep-both', '--verify')
Check 'Keep both writes "name (2).ext"' ($r.Code -eq 0 -and (Get-FileHash "$dst\App\bin\mod5 (2).dll").Hash -eq (Get-FileHash "$src\bin\mod5.dll").Hash -and (Get-FileHash "$dst\App\bin\mod5.dll").Hash -eq $before) "exit $($r.Code)"

# 7. The v1.0.2 bug: source folder copied into its own parent must be refused at once
$orig = Snap $src
foreach ($mode in 'copy', 'move') {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $r = Hc @($mode, $src, "$Ssd\src", '--verify', '--conflict', 'overwrite'); $sw.Stop()
    Check "Refuses $mode into own parent folder" ($r.Code -eq 1 -and $r.Out -match 'already in the destination folder' -and $sw.Elapsed.TotalSeconds -lt 5 -and -not (Same $orig (Snap $src))) "exit $($r.Code), $([math]::Round($sw.Elapsed.TotalSeconds,1)) s"
}
$r = Hc @('copy', "$src\bin\mod1.dll", "$src\bin", '--conflict', 'overwrite')
Check 'Refuses file copied into its own folder' ($r.Code -eq 1 -and $r.Out -match 'already in the destination') "exit $($r.Code)"
$r = Hc @('copy', $src, "$src\sub")
Check 'Refuses destination inside source' ($r.Code -eq 1 -and $r.Out -match 'inside the source' -and -not (Test-Path "$src\sub")) "exit $($r.Code)"

# 8. Locked files: reported, never "verified"
$l1 = [IO.File]::Open("$src\bin\mod7.dll", 'Open', 'Read', 'None')
$l2 = [IO.File]::Open("$src\data\video.bin", 'Open', 'Read', 'None')
try {
    $r = Hc @('copy', $src, "$Ssd\locked", '--verify')
    Check 'Locked files fail, the rest pass' ($r.Code -eq 1 -and $r.Out -match 'failed 2' -and $r.Out -match "PASS — $($n-2) file" -and $r.Out -match '2 failed to copy, not checked') "exit $($r.Code)"
    $r = Hc @('copy', "$src\data\video.bin", "$Ssd\locked2", '--verify')
    Check 'All files locked: checksums NOT RUN (no fake PASS)' ($r.Code -eq 1 -and $r.Out -match 'NOT RUN' -and $r.Out -notmatch 'PASS') "exit $($r.Code)"
} finally { $l1.Dispose(); $l2.Dispose() }

# 9. Forced profiles and buffered I/O
$orig = Snap $src
foreach ($opt in @(@('--profile', 'ssd'), @('--profile', 'hdd'), @('--buffered'))) {
    $target = if ($Hdd) { "$Hdd\p$($opt[-1])" } else { "$Ssd\p$($opt[-1])" }
    $r = Hc (@('copy', $src, $target, '--verify') + $opt)
    $diff = Same $orig (Snap "$target\App")
    Check "Copy with $($opt -join ' ')" ($r.Code -eq 0 -and -not $diff) "exit $($r.Code) $diff"
}

# 10. Move on the same drive (instant rename) and to another drive (copy, verify, delete)
$r = Hc @('copy', $src, "$Ssd\mv1"); $m1 = Snap "$Ssd\mv1\App"
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Hc @('move', "$Ssd\mv1\App", "$Ssd\mv1dst")
Check 'Move, same drive (rename)' ($r.Code -eq 0 -and -not (Test-Path "$Ssd\mv1\App") -and -not (Same $m1 (Snap "$Ssd\mv1dst\App"))) "exit $($r.Code), $([math]::Round($sw.Elapsed.TotalSeconds,2)) s"
$other = @($Ssd2, $Hdd, $Usb) | Where-Object { $_ } | Select-Object -First 1
if ($other) {
    $r = Hc @('copy', $src, "$Ssd\mv2"); $m2 = Snap "$Ssd\mv2\App"
    $r = Hc @('move', "$Ssd\mv2\App", "$other\mvdst", '--verify')
    Check "Move to another drive + verify ($other)" ($r.Code -eq 0 -and -not (Test-Path "$Ssd\mv2\App") -and -not (Same $m2 (Snap "$other\mvdst\App")) -and $r.Out -match 'PASS') "exit $($r.Code)"
} else { Check 'Move to another drive' $false 'NOT TESTED (no second drive)' }

# 11. Errors and arguments
$r = Hc @('copy', "$Ssd\does-not-exist", "$Ssd\x")
Check 'Missing source is reported' ($r.Code -eq 1 -and $r.Out -match 'not found') "exit $($r.Code)"
$out = & $cli 2>&1 | Out-String
Check 'No arguments prints help, exit 2' ($LASTEXITCODE -eq 2 -and $out -match 'hypercopy copy') ''

# Report
$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$fail = @($results | Where-Object Result -eq 'FAIL').Count
$untested = @($results | Where-Object Result -eq 'NOT TESTED').Count
Write-Host ("{0} checks, {1} failed, {2} not tested (name these in the release notes)" -f $results.Count, $fail, $untested) -ForegroundColor $(if ($fail) { 'Red' } elseif ($untested) { 'Yellow' } else { 'Green' })
$csv = Join-Path ([IO.Path]::GetTempPath()) 'hypercopy-release-check.csv'
$results | Export-Csv $csv -NoTypeInformation -Encoding utf8
Write-Host "Results saved to $csv"

if (-not $Keep) {
    foreach ($r in $roots) { if (Test-Path $r) { Get-ChildItem $r -Recurse -Force -File | ForEach-Object { $_.IsReadOnly = $false }; Remove-Item $r -Recurse -Force } }
}
exit $fail
