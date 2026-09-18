$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
# Rank Master 3 ships beside the frozen Rank Master 2 tree, not inside it.
$out = Join-Path "C:\Utils\RankMaster v3" "tray"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

# Ships both hosts: RankMaster2.Tray.exe is the notification-area one and is what you run;
# RankMaster2.Server.exe comes along as the console host, which is the one to start when you
# want to watch the log live.
dotnet publish "$root\src\RankMaster2.Tray\RankMaster2.Tray.csproj" `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Same reason as the server publish: libSkiaSharp is native and must sit beside the exe.
$required = @(
  "RankMaster2.Tray.exe",
  "libSkiaSharp.dll"
)
foreach ($f in $required) {
  $path = Join-Path $out $f
  if (-not (Test-Path $path)) {
    Write-Error "Missing after ship: $f"
    exit 1
  }
}

Get-Item ($required | ForEach-Object { Join-Path $out $_ }) |
  Select-Object FullName, Length, LastWriteTime
