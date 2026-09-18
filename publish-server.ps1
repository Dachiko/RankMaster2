$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
# Rank Master 3 ships beside the frozen Rank Master 2 tree, not inside it.
$out = Join-Path "C:\Utils\RankMaster v3" "server"
New-Item -ItemType Directory -Force -Path (Split-Path $out) | Out-Null

dotnet publish "$root\src\RankMaster2.Server\RankMaster2.Server.csproj" `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# SkiaSharp's native dll must sit next to the exe (same reason libvlc is not
# embedded in the desktop publish). web.config is an IIS leftover; ignore it.
$required = @(
  "RankMaster2.Server.exe",
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
