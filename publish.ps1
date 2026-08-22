$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Split-Path $root -Parent

dotnet publish "$root\src\RankMaster2.App\RankMaster2.App.csproj" `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true `
  -p:DebugType=None `
  -o $out\publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Ship layout: everything dotnet publish produced goes to the parent folder —
# RankMaster2.exe (.NET runtime bundled inside), the five WPF native dlls, and
# libvlc\ next to it. Do NOT embed the native bits (IncludeNativeLibrariesForSelf-
# Extract): that made the first run after every publish take ~12 s to unpack.
robocopy "$out\publish" "$out" /E /MOV /NFL /NDL /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Write-Error "robocopy failed with $LASTEXITCODE"; exit 1 }
if (Test-Path "$out\publish") { Remove-Item "$out\publish" -Recurse -Force }

# Verify the shipped set explicitly — a missing WPF native dll crashes at startup.
$required = @(
  "RankMaster2.exe",
  "wpfgfx_cor3.dll",
  "PresentationNative_cor3.dll",
  "PenImc_cor3.dll",
  "D3DCompiler_47_cor3.dll",
  "vcruntime140_cor3.dll",
  "libvlc\win-x64\libvlc.dll"
)
foreach ($f in $required) {
  if (-not (Test-Path (Join-Path $out $f))) {
    Write-Error "Missing after ship: $f"
    exit 1
  }
}

Get-Item ($required | ForEach-Object { Join-Path $out $_ }) |
  Select-Object FullName, Length, LastWriteTime
