#Requires -Version 5.1
# A-startup-and-shell.md § 4.5. The owner's one line:
#   irm https://bormin.fintebtc.de/rm2/install.ps1 | iex
# Downloads the latest client, swaps it into C:\Utils\RankMaster v3\pc, builds the libvlc plugin
# index (§ 4.3), then measures the result and puts the report block on the clipboard.
#
# Never touches C:\Utils\rank-master-2 (the frozen Rank Master 2 app), or
# %LOCALAPPDATA%\RankMaster2\server.
[CmdletBinding()]
param(
    [switch]$NoMeasure,
    [string]$BaseUrl = "https://bormin.fintebtc.de/rm2"
)

$ErrorActionPreference = "Stop"

function Say([string]$msg) { Write-Host $msg }
function Warn([string]$msg) { Write-Host $msg -ForegroundColor Red }

# ---------------------------------------------------------------- step 1: paths
$root = "C:\Utils\RankMaster v3"
$dst = Join-Path $root "pc"
$tmp = Join-Path $env:TEMP "rm2pc"
Say "install.ps1: target $dst"

# ---------------------------------------------------------------- step 2: download
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory -Path $tmp | Out-Null

$version = (Invoke-WebRequest -Uri "$BaseUrl/latest.txt" -UseBasicParsing).Content.Trim()
$zipName = "RankMaster2-pc-$version.zip"
$zipPath = Join-Path $tmp $zipName
Say "install.ps1: downloading $zipName ..."
Invoke-WebRequest -Uri "$BaseUrl/$zipName" -OutFile $zipPath -UseBasicParsing
$sizeMb = [Math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Say "install.ps1: downloaded $zipName ($sizeMb MB)"

# ---------------------------------------------------------------- step 3: extract
$newDir = Join-Path $tmp "new"
Expand-Archive -Path $zipPath -DestinationPath $newDir -Force
$extractedRoot = Join-Path $newDir "RankMaster2-pc"
if (-not (Test-Path $extractedRoot)) {
    # the zip's top-level folder name, whatever it is
    $extractedRoot = (Get-ChildItem $newDir -Directory | Select-Object -First 1).FullName
}

# ---------------------------------------------------------------- step 4: stop the client only
Get-Process -Name RankMaster2 -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($dst, [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object {
    Say "install.ps1: stopping running client (pid $($_.Id))"
    Stop-Process -Id $_.Id -Force
}

# ---------------------------------------------------------------- step 5: swap
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (Test-Path $dst) {
    $old = "$dst.old"
    if (Test-Path $old) { Remove-Item $old -Recurse -Force }
    Rename-Item $dst $old
}
Move-Item $extractedRoot $dst
if (Test-Path "$dst.old") { Remove-Item "$dst.old" -Recurse -Force }
Say "install.ps1: $version installed at $dst"

# ---------------------------------------------------------------- step 6: build the plugin index
$exe = Join-Path $dst "RankMaster2.exe"
$indexPath = Join-Path $dst "libvlc\win-x64\plugins\plugins.dat"
$p = Start-Process -FilePath $exe -ArgumentList "--build-vlc-cache" -Wait -PassThru -NoNewWindow
if ($p.ExitCode -eq 0 -and (Test-Path $indexPath)) {
    $bytes = (Get-Item $indexPath).Length
    Say "install.ps1: index: $bytes bytes"
} else {
    Warn "install.ps1: plugin index was not built (exit $($p.ExitCode)) — the app will build it itself, more slowly, at first wake"
}

# ---------------------------------------------------------------- steps 7-9: measure and report
if ($NoMeasure) {
    Say "install.ps1: -NoMeasure — skipping the measurement block"
    exit 0
}

function Measure-Launch {
    param([string]$ExePath, [string]$WorkingDirectory)
    $p = Start-Process -FilePath $ExePath -WorkingDirectory $WorkingDirectory -PassThru
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        Start-Sleep -Milliseconds 2
        $p.Refresh()
        if ($p.HasExited) { return "crashed(exit $($p.ExitCode))" }
        if ($p.MainWindowHandle -ne 0) { break }
        if ($sw.ElapsedMilliseconds -gt 20000) { return "timeout" }
    }
    $seen = Get-Date
    $t1 = [int]($seen - $p.StartTime).TotalMilliseconds
    Start-Sleep -Milliseconds 400
    if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    Start-Sleep -Milliseconds 300
    return $t1
}

Say "install.ps1: measuring (1 fresh + 5 warm launches)..."
$fresh = Measure-Launch -ExePath $exe -WorkingDirectory $dst
$warm = 1..5 | ForEach-Object { Measure-Launch -ExePath $exe -WorkingDirectory $dst }
$numericWarm = $warm | Where-Object { $_ -is [int] }
$warmMed = if ($numericWarm) { ($numericWarm | Sort-Object)[[int]([Math]::Floor($numericWarm.Count / 2))] } else { "--" }
$warmMax = if ($numericWarm) { ($numericWarm | Measure-Object -Maximum).Maximum } else { "--" }

$logPath = Join-Path $env:LOCALAPPDATA "RankMaster2\pc\startup.log"
$logTail = if (Test-Path $logPath) { Get-Content $logPath -Tail 6 } else { @("(no startup.log yet)") }

$block = @()
$block += "RM2 startup  $(Get-Date -Format 'yyyy-MM-dd')  $version"
$block += ""
$block += ("{0,-16} {1,8} {2,10} {3,9}" -f "candidate", "fresh", "warm-med", "warm-max")
$block += ("{0,-16} {1,8} {2,10} {3,9}" -f "pc $version", $fresh, $warmMed, $warmMax)
$block += ""
$block += "startup.log (last lines):"
$block += $logTail
$blockText = $block -join "`r`n"

Say ""
Say $blockText
try {
    $blockText | Set-Clipboard
    Say ""
    Say "install.ps1: copied — paste it into the chat"
} catch {
    Warn "install.ps1: could not set the clipboard ($($_.Exception.Message)) — copy the block above by hand"
}
