$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Split-Path $root -Parent
dotnet publish "$root\src\RankMaster2.App\RankMaster2.App.csproj" `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:DebugType=None `
  -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-Item "$out\RankMaster2.exe" | Select-Object FullName, Length, LastWriteTime
