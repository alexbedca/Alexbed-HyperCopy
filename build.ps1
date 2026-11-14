# Builds the portable release: runs the tests, then publishes single-file exes to dist\.
#   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

dotnet test -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

if (Test-Path dist) { Remove-Item -Recurse -Force dist }
dotnet publish src/Alexbed.HyperCopy.App -c Release -r win-x64 -o dist
if ($LASTEXITCODE -ne 0) { throw "Publishing the app failed" }
dotnet publish src/Alexbed.HyperCopy.Cli -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false -p:DebugType=none -o dist
if ($LASTEXITCODE -ne 0) { throw "Publishing the CLI failed" }
Get-ChildItem dist -Exclude *.exe | Remove-Item -Force

Get-ChildItem dist\*.exe | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content dist\SHA256SUMS.txt -Encoding ascii

Get-ChildItem dist | Format-Table Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
