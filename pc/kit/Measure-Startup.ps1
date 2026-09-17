#Requires -Version 5.1
# A-startup-and-shell.md § 6.3. The external clock: T1_ext for every candidate, fresh + 5 warm
# launches, plus the vlc-init/vlc-cache/scan/decode/play block from rm2probe (§ 6.4). Used by the
# Phase A0 kit (kit.ps1) directly, and the same shape of measurement is embedded in install.ps1
# for every later build.
[CmdletBinding()]
param(
    [string]$KitRoot = $PSScriptRoot,
    [hashtable]$Candidates,
    [int]$Warm = 5,
    [switch]$Cold,
    [string]$Out = (Join-Path $PSScriptRoot "results.txt"),
    [string]$LastFolder
)

$ErrorActionPreference = "Stop"

function Measure-Launch {
    param([string]$ExePath)
    if (-not (Test-Path $ExePath)) { return "missing" }
    $p = Start-Process -FilePath $ExePath -PassThru -WorkingDirectory (Split-Path $ExePath)
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

function Median([array]$values) {
    $nums = $values | Where-Object { $_ -is [int] } | Sort-Object
    if ($nums.Count -eq 0) { return "--" }
    return $nums[[int][Math]::Floor($nums.Count / 2)]
}

function Max2([array]$values) {
    $nums = $values | Where-Object { $_ -is [int] }
    if ($nums.Count -eq 0) { return "--" }
    return ($nums | Measure-Object -Maximum).Maximum
}

if (-not $Candidates) {
    $Candidates = @{
        "shipped"        = "C:\Utils\rank-master-2\RankMaster2.exe"
        "old-r2r"        = Join-Path $KitRoot "old-r2r\RankMaster2.exe"
        "empty-wpf"      = Join-Path $KitRoot "empty-wpf\EmptyWpf.exe"
        "empty-avalonia" = Join-Path $KitRoot "empty-avalonia\EmptyAvalonia.exe"
    }
}

# ---------------------------------------------------------------- preconditions
Write-Host "Measure-Startup: preconditions"
try {
    $os = Get-CimInstance Win32_OperatingSystem
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
    $ramGb = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 0)
    Write-Host "  $($os.Caption) | $cpu | $ramGb GB"
} catch { Write-Host "  (Win32_OperatingSystem/Win32_Processor unavailable)" }

try {
    $disk = Get-PhysicalDisk -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($disk) { Write-Host "  drive: $($disk.MediaType)" }
} catch { }

$shippedPlugins = "C:\Utils\rank-master-2\libvlc\win-x64\plugins\plugins.dat"
Write-Host "  shipped plugins.dat: $(if (Test-Path $shippedPlugins) { 'yes' } else { 'no' })"
Write-Host "  tray running: $((Get-Process -Name RankMaster2.Tray -ErrorAction SilentlyContinue) -ne $null)"

# ---------------------------------------------------------------- launches
$results = [ordered]@{}
foreach ($name in $Candidates.Keys) {
    $exe = $Candidates[$name]
    Write-Host "measuring $name ..."
    $fresh = Measure-Launch $exe
    $warmRuns = 1..$Warm | ForEach-Object { Measure-Launch $exe }
    $results[$name] = [pscustomobject]@{
        Fresh = $fresh
        WarmMedian = Median $warmRuns
        WarmMax = Max2 $warmRuns
        Note = ""
    }

    if ($name -eq "old-r2r") {
        $vlcDir = Join-Path (Split-Path $exe) "libvlc\win-x64"
        $probe = Join-Path $KitRoot "rm2probe\rm2probe.exe"
        if ((Test-Path $probe) -and (Test-Path $vlcDir)) {
            Write-Host "  building plugins.dat for old-r2r's own libvlc\ copy..."
            & $probe vlc-cache $vlcDir | Out-Null
            $cacheFresh = Measure-Launch $exe
            $cacheWarm = 1..$Warm | ForEach-Object { Measure-Launch $exe }
            $results["old-r2r-cache"] = [pscustomobject]@{
                Fresh = $cacheFresh
                WarmMedian = Median $cacheWarm
                WarmMax = Max2 $cacheWarm
                Note = "plugins.dat built by rm2probe"
            }
        }
    }
}

if ($Cold) {
    Write-Host "-Cold: measuring one launch per candidate only (no re-extraction; reboot first for a real cold number)"
}

# ---------------------------------------------------------------- report
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("RM2 startup  $(Get-Date -Format 'yyyy-MM-dd')")
$lines.Add("")
$lines.Add(("{0,-16} {1,8} {2,10} {3,9}   {4}" -f "candidate", "fresh", "warm-med", "warm-max", "note"))
foreach ($name in $results.Keys) {
    $r = $results[$name]
    $lines.Add(("{0,-16} {1,8} {2,10} {3,9}   {4}" -f $name, $r.Fresh, $r.WarmMedian, $r.WarmMax, $r.Note))
}

$probe = Join-Path $KitRoot "rm2probe\rm2probe.exe"
if (Test-Path $probe) {
    $lines.Add("")
    $oldVlc = Join-Path $KitRoot "old-r2r\libvlc\win-x64"
    $prunedVlc = Join-Path $KitRoot "libvlc-pruned\win-x64"
    if (Test-Path $oldVlc) {
        $lines.Add("vlc-init 320 plugins, no cache : " + ((& $probe vlc-init $oldVlc --runs 3) -join " / "))
    }
    if (Test-Path $prunedVlc) {
        $lines.Add("vlc-init  27 plugins, no cache : " + ((& $probe vlc-init $prunedVlc --runs 3) -join " / "))
        & $probe vlc-cache $prunedVlc | Out-Null
        $lines.Add("vlc-init  27 plugins, cached   : " + ((& $probe vlc-init $prunedVlc --runs 3) -join " / "))
    }
    if ($LastFolder -and (Test-Path $LastFolder)) {
        $lines.Add((& $probe scan $LastFolder))
        $lines.Add((& $probe decode $LastFolder))
        if (Test-Path $prunedVlc) {
            $lines.Add((& $probe play $LastFolder --libvlc $prunedVlc --seconds 3))
        }
    }
}

$report = $lines -join "`r`n"
$report | Out-File -FilePath $Out -Encoding utf8
Write-Host ""
Write-Host $report
try { $report | Set-Clipboard } catch { }
