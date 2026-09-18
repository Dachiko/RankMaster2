#Requires -Version 5.1
# Pull master and install Rank Master 3 (tray + PC client) into C:\Utils\RankMaster v3.
# Does not touch the frozen Rank Master 2 tree. Does not start the PC client (fullscreen).
#
#   powershell -File deploy.ps1
#   powershell -File deploy.ps1 -SkipPull
[CmdletBinding()]
param(
    [switch]$SkipPull
)

$ErrorActionPreference = "Stop"
$repo = $PSScriptRoot
$install = "C:\Utils\RankMaster v3"
$trayDir = Join-Path $install "tray"
$pcDir = Join-Path $install "pc"
$pcDist = Join-Path $repo "pc\dist\RankMaster2-pc"

function Say([string]$msg) { Write-Host $msg }

Set-Location $repo

# ---------------------------------------------------------------- git
if (-not $SkipPull) {
    $dirty = git status --porcelain --untracked-files=no
    if ($dirty) {
        Write-Error "Working tree has local edits. Commit, stash, or pass -SkipPull.`n$dirty"
        exit 1
    }
    git fetch origin
    git checkout master
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    git pull origin master
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$sha = (git rev-parse --short HEAD).Trim()
$version = ([regex]::Match((Get-Content (Join-Path $repo "Directory.Build.props") -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
if (-not $version) { Write-Error "Could not read Version from Directory.Build.props"; exit 1 }
Say "deploy.ps1: $version ($sha) -> $install"

# ---------------------------------------------------------------- stop what we are about to overwrite
Get-Process RankMaster2.Tray, RankMaster2.Server -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process RankMaster2 -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and $_.Path.StartsWith($install, [StringComparison]::OrdinalIgnoreCase)
} | Stop-Process -Force
Start-Sleep -Milliseconds 400

# ---------------------------------------------------------------- tray (keeps appsettings.json if publish leaves it)
$settingsPath = Join-Path $trayDir "appsettings.json"
$settingsBackup = $null
if (Test-Path $settingsPath) { $settingsBackup = Get-Content $settingsPath -Raw }

& (Join-Path $repo "publish-tray.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($settingsBackup) {
    Set-Content -Path $settingsPath -Value $settingsBackup -Encoding utf8 -NoNewline
} elseif (-not (Test-Path $settingsPath)) {
    Write-Error "No appsettings.json beside the tray. The server will bind 127.0.0.1 and the phone cannot reach it. Write ListenAddress once, then deploy again."
    exit 1
}

# ---------------------------------------------------------------- PC client
if (Test-Path $pcDist) { Remove-Item $pcDist -Recurse -Force }
dotnet publish (Join-Path $repo "pc\src\RankMaster2.Pc") `
    -c Release -r win-x64 --self-contained `
    -o $pcDist -nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Remove-Item (Join-Path $pcDist "createdump.exe") -Force -ErrorAction SilentlyContinue

foreach ($f in @("RankMaster2.exe", "RankMaster2.dll", "libSkiaSharp.dll", "libvlc\win-x64\libvlc.dll")) {
    if (-not (Test-Path (Join-Path $pcDist $f))) {
        Write-Error "PC publish missing $f"
        exit 1
    }
}

if (Test-Path $pcDir) {
    $old = "$pcDir.old"
    if (Test-Path $old) { Remove-Item $old -Recurse -Force }
    Rename-Item $pcDir $old
}
Move-Item $pcDist $pcDir
if (Test-Path "$pcDir.old") { Remove-Item "$pcDir.old" -Recurse -Force }

$stamp = "{0} {1} {2:yyyy-MM-ddTHH:mm:ssZ}" -f $version, $sha, [DateTime]::UtcNow
Set-Content (Join-Path $pcDir "VERSION.txt") $stamp

# ---------------------------------------------------------------- start the tray in this Windows session
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $trayDir "RankMaster2.Tray.exe"
$psi.WorkingDirectory = $trayDir
$psi.UseShellExecute = $true
[void][System.Diagnostics.Process]::Start($psi)

Say "deploy.ps1: done. $stamp"
Say "  tray  $trayDir\RankMaster2.Tray.exe"
Say "  pc    $pcDir\RankMaster2.exe"
