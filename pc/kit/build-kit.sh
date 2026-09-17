#!/usr/bin/env bash
# A-startup-and-shell.md § 4.6, Phase A0. Builds every candidate the kit needs into pc/dist/kit/,
# zips it, and (unless --no-upload) uploads it beside install.ps1's client zip.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO"
export PATH="$HOME/.dotnet:$PATH"

NO_UPLOAD=0
for arg in "$@"; do
  case "$arg" in
    --no-upload) NO_UPLOAD=1 ;;
    *) echo "build-kit.sh: unknown argument '$arg'" >&2; exit 2 ;;
  esac
done

say() { printf 'build-kit.sh: %s\n' "$*"; }
fail() { printf 'build-kit.sh: FATAL: %s\n' "$*" >&2; exit 1; }

KITDIST="pc/dist/kit"
rm -rf "$KITDIST"
mkdir -p "$KITDIST"

# ---------------------------------------------------------------- old-r2r
say "old-r2r (the frozen app, R2R, folder layout)..."
dotnet publish src/RankMaster2.App -c Release -r win-x64 --self-contained \
  -p:EnableWindowsTargeting=true -p:PublishReadyToRun=true -p:PublishSingleFile=false -p:DebugType=None \
  -o "$KITDIST/old-r2r" -nologo -v minimal
# The NuGet package's own build target copied all ~100 MB of the unpruned libvlc\ next to it; the
# owner's PC gets his own shipped libvlc\ copied in by kit.ps1 instead (same files, ~40 MB less to
# download). Remove what publish put there so the kit doesn't ship it twice.
rm -rf "$KITDIST/old-r2r/libvlc"

# ---------------------------------------------------------------- empty-wpf
say "empty-wpf ..."
dotnet publish pc/kit/EmptyWpf -c Release -r win-x64 --self-contained \
  -p:EnableWindowsTargeting=true -p:PublishReadyToRun=true -p:DebugType=None \
  -o "$KITDIST/empty-wpf" -nologo -v minimal

# ---------------------------------------------------------------- empty-avalonia
say "empty-avalonia ..."
dotnet publish pc/kit/EmptyAvalonia -c Release -r win-x64 --self-contained \
  -p:PublishReadyToRun=true -o "$KITDIST/empty-avalonia" -nologo -v minimal

# ---------------------------------------------------------------- rm2probe (win-x64, for the kit)
say "rm2probe (win-x64) ..."
dotnet publish pc/tools/rm2probe -c Release -r win-x64 --self-contained \
  -p:PublishReadyToRun=true -o "$KITDIST/rm2probe" -nologo -v minimal

# ---------------------------------------------------------------- the pruned libvlc set
# Reuses the client's own publish + CopyPrunedLibVlc target so the kit's set IS the manifest.
say "libvlc-pruned (publishing the real client once, to reuse its CopyPrunedLibVlc output) ..."
dotnet publish pc/src/RankMaster2.Pc -c Release -r win-x64 --self-contained \
  -o "$KITDIST/_client-for-libvlc" -nologo -v minimal
mkdir -p "$KITDIST/libvlc-pruned"
cp -r "$KITDIST/_client-for-libvlc/libvlc/win-x64" "$KITDIST/libvlc-pruned/win-x64"
rm -rf "$KITDIST/_client-for-libvlc"

# ---------------------------------------------------------------- scripts
cp pc/kit/Measure-Startup.ps1 "$KITDIST/"
cp pc/kit/Measure.cmd "$KITDIST/"

VERSION="kit-$(date -u +%Y%m%d)"
echo "$VERSION" > "$KITDIST/../kit-latest.txt" 2>/dev/null || true

ZIP_NAME="rm2-startup-kit-$(date -u +%Y%m%d).zip"
rm -f "pc/dist/$ZIP_NAME"
if command -v zip >/dev/null 2>&1; then
  ( cd pc/dist && zip -qr "$ZIP_NAME" kit )
else
  python3 - "$ZIP_NAME" <<'PY'
import sys, zipfile, pathlib
zip_name = sys.argv[1]
root = pathlib.Path("pc/dist")
src = root / "kit"
files = sorted(p for p in src.rglob("*") if p.is_file())
with zipfile.ZipFile(root / zip_name, "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(f, f.relative_to(root))
PY
fi
SIZE_MB=$(du -sm "pc/dist/$ZIP_NAME" | cut -f1)
say "zip $ZIP_NAME (${SIZE_MB} MB)"

if [ "$NO_UPLOAD" -eq 0 ]; then
  WEB_DIR="/var/www/bormin/web/rm2"
  mkdir -p "$WEB_DIR"
  cp "pc/dist/$ZIP_NAME" "$WEB_DIR/$ZIP_NAME"
  DATE_ONLY="$(date -u +%Y%m%d)"
  echo "$DATE_ONLY" > "$WEB_DIR/kit-latest.txt"
  cp pc/kit/kit.ps1 "$WEB_DIR/kit.ps1"
  say "uploaded to $WEB_DIR"
  say "owner runs: irm https://bormin.fintebtc.de/rm2/kit.ps1 | iex"
fi
