#!/usr/bin/env bash
# A-startup-and-shell.md § 4.4. Run from the repository root by the agent; no `git` commands
# inside it except the one read-only `rev-parse` for VERSION.txt (skippable with --no-sha) —
# the executor runs every other git command separately, per the parts' rules.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

export PATH="$HOME/.dotnet:$PATH"

NO_SHA=0
NO_UPLOAD=0
for arg in "$@"; do
  case "$arg" in
    --no-sha) NO_SHA=1 ;;
    --no-upload) NO_UPLOAD=1 ;;
    *) echo "publish.sh: unknown argument '$arg'" >&2; exit 2 ;;
  esac
done

say() { printf '%s\n' "$*"; }
fail() { printf 'publish.sh: FATAL: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- 1-2: version and sha
# G-audit-remediation.md § 1 (K2): pc/Directory.Build.props stopped overriding <Version> once the
# root caught up (a second place to edit is a second place to forget) — it only chains the import
# now — so the one place this number lives is the root's Directory.Build.props.
VERSION="$(grep -oP '(?<=<Version>)[^<]+' Directory.Build.props | head -1)"
[ -n "$VERSION" ] || fail "could not read <Version> from Directory.Build.props"

SHA="nogit"
if [ "$NO_SHA" -eq 0 ]; then
  SHA="$(git rev-parse --short HEAD 2>/dev/null || echo nogit)"
fi
say "publish.sh: RankMaster2 pc $VERSION ($SHA)"

# ---------------------------------------------------------------- 3-4: publish
DIST="pc/dist/RankMaster2-pc"
rm -rf "$DIST"
say "publish.sh: dotnet publish -r win-x64 --self-contained ..."
dotnet publish pc/src/RankMaster2.Pc -c Release -r win-x64 --self-contained \
  -o "$DIST" -nologo -v minimal

# The self-contained runtime always copies createdump.exe (core-dump capture); this app has its own
# crash file (App/CrashLog.cs) and no supported MSBuild switch removes this cleanly, so it is
# deleted here rather than shipped unused (§ 4.4 step 5's shape check treats its presence as fatal).
rm -f "$DIST/createdump.exe"

# ---------------------------------------------------------------- 5: shape check
say "publish.sh: verifying publish shape..."

require_file() { [ -f "$1" ] || fail "missing $1"; }
require_file "$DIST/RankMaster2.exe"
require_file "$DIST/RankMaster2.dll"
require_file "$DIST/RankMaster2.pdb"
require_file "$DIST/av_libglesv2.dll"
require_file "$DIST/libSkiaSharp.dll"
require_file "$DIST/libHarfBuzzSharp.dll"
require_file "$DIST/libvlc/win-x64/libvlc.dll"
require_file "$DIST/libvlc/win-x64/libvlccore.dll"

MANIFEST="pc/libvlc/plugins.keep.txt"
MANIFEST_COUNT=$(grep -Ev '^\s*(#|\s*$)' "$MANIFEST" | wc -l)
FOUND_COUNT=$(find "$DIST/libvlc/win-x64/plugins" -name '*.dll' | wc -l)
[ "$MANIFEST_COUNT" -eq "$FOUND_COUNT" ] || fail "plugin count mismatch: manifest has $MANIFEST_COUNT, publish has $FOUND_COUNT"

while IFS= read -r line; do
  [[ "$line" =~ ^# ]] && continue
  [[ -z "$line" ]] && continue
  [ -f "$DIST/libvlc/win-x64/$line" ] || fail "manifest names a file the publish does not have: $line"
done < "$MANIFEST"

FORBIDDEN=$(find "$DIST" \( -iname '*.lib' -o -iname 'plugins.dat' -o -iname '*.xml' -o -iname 'createdump.exe' \) -o -type d \( -iname 'lua' -o -iname 'hrtfs' \))
[ -z "$FORBIDDEN" ] || fail "forbidden files/dirs present: $FORBIDDEN"

# rm2probe is-r2r: the R2R shape check. Built here too (linux-x64), which the harness also needs.
say "publish.sh: building rm2probe (linux-x64, for the shape check and the harness)..."
dotnet publish pc/tools/rm2probe -c Release -r linux-x64 --self-contained \
  -o pc/dist/probe-linux -nologo -v minimal

R2R_CHECK() {
  pc/dist/probe-linux/rm2probe is-r2r "$1" >/tmp/rm2probe-r2r.out 2>&1 || {
    cat /tmp/rm2probe-r2r.out
    fail "$1 is not a ReadyToRun image"
  }
  cat /tmp/rm2probe-r2r.out
}
R2R_CHECK "$DIST/RankMaster2.dll"
R2R_CHECK "$DIST/Avalonia.Base.dll"

TOTAL_SIZE_MB=$(du -sm "$DIST" | cut -f1)
FILE_COUNT=$(find "$DIST" -type f | wc -l)
say "publish.sh: shape OK — $FILE_COUNT files, ${TOTAL_SIZE_MB} MB"
[ "$TOTAL_SIZE_MB" -lt 150 ] || fail "publish is ${TOTAL_SIZE_MB} MB, over the 150 MB budget (§ 4.4 step 5)"

# ---------------------------------------------------------------- 6-7: VERSION.txt, install.ps1
UTC_NOW="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "$VERSION $SHA $UTC_NOW" > "$DIST/VERSION.txt"
cp pc/install.ps1 "$DIST/../install.ps1" 2>/dev/null || true
cp pc/install.ps1 "$DIST/install.ps1"

# ---------------------------------------------------------------- 8: zip
# Uses `zip` when present (deterministic order); falls back to Python's zipfile (stdlib, always
# present) on a box without it — this one included. Either way the archive holds exactly the
# RankMaster2-pc/ folder.
ZIP_NAME="RankMaster2-pc-$VERSION.zip"
rm -f "pc/dist/$ZIP_NAME"
if command -v zip >/dev/null 2>&1; then
  ( cd pc/dist && zip -qr "$ZIP_NAME" RankMaster2-pc )
else
  python3 - "$ZIP_NAME" <<'PY'
import sys, zipfile, pathlib
zip_name = sys.argv[1]
root = pathlib.Path("pc/dist")
src = root / "RankMaster2-pc"
files = sorted(p for p in src.rglob("*") if p.is_file())
with zipfile.ZipFile(root / zip_name, "w", zipfile.ZIP_DEFLATED) as z:
    for f in files:
        z.write(f, f.relative_to(root))
PY
fi
ZIP_SIZE_MB=$(du -sm "pc/dist/$ZIP_NAME" | cut -f1)
say "publish.sh: zip $ZIP_NAME (${ZIP_SIZE_MB} MB)"

# ---------------------------------------------------------------- 9: upload
if [ "$NO_UPLOAD" -eq 0 ]; then
  WEB_DIR="/var/www/bormin/web/rm2"
  mkdir -p "$WEB_DIR"
  cp "pc/dist/$ZIP_NAME" "$WEB_DIR/$ZIP_NAME"
  echo "$VERSION" > "$WEB_DIR/latest.txt"
  cp pc/install.ps1 "$WEB_DIR/install.ps1"
  say "publish.sh: uploaded to $WEB_DIR"
fi

# ---------------------------------------------------------------- 10: the owner's one line
say ""
say "publish.sh: done. $VERSION built ($SHA), $FILE_COUNT files, ${TOTAL_SIZE_MB} MB, zip ${ZIP_SIZE_MB} MB."
if [ "$NO_UPLOAD" -eq 0 ]; then
  say "URL: https://bormin.fintebtc.de/rm2/$ZIP_NAME"
  say ""
  say "Owner runs (installs and measures in one line):"
  say "  irm https://bormin.fintebtc.de/rm2/install.ps1 | iex"
fi
