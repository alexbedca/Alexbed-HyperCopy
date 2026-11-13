# Alexbed HyperCopy benchmark: HyperCopy vs Robocopy vs Windows CopyFileEx (Explorer's engine).
# Usage: pwsh tools/benchmark.ps1 -SrcRoot C:\hc-bench -SsdDest E:\hc-bench -HddDest G:\hc-bench
param(
    [string]$SrcRoot = "C:\hc-bench",
    [string]$SsdDest = "E:\hc-bench",
    [string]$HddDest = "G:\hc-bench",
    [int]$LargeGB = 36,
    [int]$SmallCount = 20000
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$hc = Join-Path $repo "src\Alexbed.HyperCopy.Cli\bin\Release\net10.0-windows\hypercopy.exe"
$results = [System.Collections.Generic.List[object]]::new()

function Reset-Dir($p) { if (Test-Path $p) { cmd /c rmdir /s /q "$p" | Out-Null } }

function Measure-Run($test, $tool, $bytes, [scriptblock]$run, $dst) {
    Reset-Dir $dst
    New-Item -ItemType Directory -Force $dst | Out-Null
    $t = Measure-Command { & $run | Out-Null }
    $mbps = [math]::Round($bytes / 1MB / $t.TotalSeconds, 0)
    $row = [pscustomobject]@{ Test = $test; Tool = $tool; Seconds = [math]::Round($t.TotalSeconds, 1); MBps = $mbps }
    $results.Add($row); $row | Format-Table -HideTableHeaders | Out-String | Write-Host
    Reset-Dir $dst
}

# ── Test data ──
$large = Join-Path $SrcRoot "large\big-$LargeGB.bin"
if (-not (Test-Path $large)) {
    Write-Host "Generating $LargeGB GB file (larger than RAM so nothing is served from cache)..."
    New-Item -ItemType Directory -Force (Split-Path $large) | Out-Null
    python -I -c "import os,sys
blk=os.urandom(64<<20)
with open(sys.argv[1],'wb') as f:
    for i in range(int(sys.argv[2])*16): k=(i*4096)%len(blk); f.write(blk[k:]+blk[:k])" $large $LargeGB
}
$small = Join-Path $SrcRoot "small"
if (-not (Test-Path $small)) {
    Write-Host "Generating $SmallCount small files..."
    python -I -c "import os,sys,random
r=random.Random(7); root=sys.argv[1]; n=int(sys.argv[2])
for i in range(n):
    d=os.path.join(root,'d%03d'%(i//200)); os.makedirs(d,exist_ok=True)
    open(os.path.join(d,'f%05d.dat'%i),'wb').write(os.urandom(r.randint(1024,64*1024)))" $small $SmallCount
}
$medium = Join-Path $SrcRoot "src\video-6gb.bin"

$largeBytes = (Get-Item $large).Length
$smallBytes = (Get-ChildItem $small -Recurse -File | Measure-Object Length -Sum).Sum
$mediumBytes = (Get-Item $medium).Length

# ── 1. Large file SSD → SSD ──
$d = Join-Path $SsdDest "large"
Measure-Run "Large $LargeGB GB  SSD→SSD" "HyperCopy"          $largeBytes { & $hc copy $large $d --quiet --conflict overwrite } $d
Measure-Run "Large $LargeGB GB  SSD→SSD" "Robocopy"           $largeBytes { robocopy (Split-Path $large) $d (Split-Path $large -Leaf) /NP /NFL /NDL /NJH /NJS } $d
Measure-Run "Large $LargeGB GB  SSD→SSD" "Robocopy /J"        $largeBytes { robocopy (Split-Path $large) $d (Split-Path $large -Leaf) /J /NP /NFL /NDL /NJH /NJS } $d
Measure-Run "Large $LargeGB GB  SSD→SSD" "CopyFileEx (Explorer)" $largeBytes { [IO.File]::Copy($large, (Join-Path $d "big.bin"), $true) } $d

# ── 2. 20k small files SSD → SSD ──
$d = Join-Path $SsdDest "small"
Measure-Run "$SmallCount small files SSD→SSD" "HyperCopy"      $smallBytes { & $hc copy $small $d --quiet --conflict overwrite } $d
Measure-Run "$SmallCount small files SSD→SSD" "Robocopy"       $smallBytes { robocopy $small $d /E /NP /NFL /NDL /NJH /NJS } $d
Measure-Run "$SmallCount small files SSD→SSD" "Robocopy /MT:16" $smallBytes { robocopy $small $d /E /MT:16 /NP /NFL /NDL /NJH /NJS } $d

# ── 3. 6 GB SSD → HDD ──
$d = Join-Path $HddDest "medium"
Measure-Run "6 GB SSD→HDD" "HyperCopy"   $mediumBytes { & $hc copy $medium $d --quiet --conflict overwrite } $d
Measure-Run "6 GB SSD→HDD" "Robocopy"    $mediumBytes { robocopy (Split-Path $medium) $d (Split-Path $medium -Leaf) /NP /NFL /NDL /NJH /NJS } $d
Measure-Run "6 GB SSD→HDD" "CopyFileEx (Explorer)" $mediumBytes { [IO.File]::Copy($medium, (Join-Path $d "v.bin"), $true) } $d

# ── 4. small files SSD → HDD ──
$d = Join-Path $HddDest "small"
Measure-Run "$SmallCount small files SSD→HDD" "HyperCopy"      $smallBytes { & $hc copy $small $d --quiet --conflict overwrite } $d
Measure-Run "$SmallCount small files SSD→HDD" "Robocopy /MT:16" $smallBytes { robocopy $small $d /E /MT:16 /NP /NFL /NDL /NJH /NJS } $d

$out = Join-Path $repo "docs\benchmark-results.md"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$md = @("| Test | Tool | Time (s) | Throughput (MB/s) |", "|---|---|---:|---:|")
$md += $results | ForEach-Object { "| $($_.Test) | $($_.Tool) | $($_.Seconds) | $($_.MBps) |" }
$md | Set-Content $out -Encoding utf8
Write-Host "`nSaved $out"
$results | Format-Table -AutoSize | Out-String | Write-Host
