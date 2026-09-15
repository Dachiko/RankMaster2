$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path (Split-Path $root -Parent) "server"

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
