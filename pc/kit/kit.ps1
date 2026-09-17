#Requires -Version 5.1
# A-startup-and-shell.md § 6.6, Phase A0. The owner's one line, once:
#   irm https://bormin.fintebtc.de/rm2/kit.ps1 | iex
# Downloads the measurement kit, extracts it, copies the owner's real libvlc\ beside old-r2r (so
# the "320 plugins, no cache" number is his actual shipped set, not a fresh download of it), runs
# Measure-Startup.ps1, and opens the result in Notepad.
[CmdletBinding()]
param(
    [switch]$Cold,
    [string]$BaseUrl = "https://bormin.fintebtc.de/rm2"
)

$ErrorActionPreference = "Stop"

$tmp = Join-Path $env:TEMP "rm2kit"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

$latest = (Invoke-WebRequest -Uri "$BaseUrl/kit-latest.txt" -UseBasicParsing).Content.Trim()
$zipName = "rm2-startup-kit-$latest.zip"
$zipPath = Join-Path $tmp $zipName
Write-Host "kit.ps1: downloading $zipName (about 180-220 MB, once) ..."
Invoke-WebRequest -Uri "$BaseUrl/$zipName" -OutFile $zipPath -UseBasicParsing

Write-Host "kit.ps1: extracting ..."
Expand-Archive -Path $zipPath -DestinationPath $tmp -Force

$oldShipped = "C:\Utils\rank-master-2\libvlc"
$oldR2rVlc = Join-Path $tmp "old-r2r\libvlc"
if ((Test-Path $oldShipped) -and -not (Test-Path $oldR2rVlc)) {
    Write-Host "kit.ps1: copying the owner's shipped libvlc\ beside old-r2r (his real 320-file set)"
    Copy-Item $oldShipped $oldR2rVlc -Recurse
}

$measureArgs = @{ KitRoot = $tmp }
if ($Cold) { $measureArgs.Cold = $true }
& (Join-Path $tmp "Measure-Startup.ps1") @measureArgs

$resultsPath = Join-Path $tmp "results.txt"
if (Test-Path $resultsPath) {
    Start-Process notepad.exe $resultsPath
}

Write-Host ""
Write-Host "kit.ps1: done. Paste the Notepad contents (or what's on your clipboard) back into the chat."
