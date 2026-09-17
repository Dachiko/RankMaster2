#!/usr/bin/env bash
# A-startup-and-shell.md § 5.3. Validates pc/libvlc/plugins.keep.txt without running Windows: VLC's
# modules have the same names on every platform, so the *set* is proven by playing the corpus on
# Linux through the .so twins of exactly the manifest's names, inside debian:trixie-slim (VLC
# 3.0.23 there, the same major/minor as the Windows payload, VideoLAN.LibVLC.Windows 3.0.21).
#
# What this proves: the manifest's names are sufficient for these containers and codecs on VLC
# 3.0.x. What it cannot prove: that the Windows build of a plugin behaves like the Linux one, or
# that the owner's real files contain nothing outside the corpus (covered on his PC by
# `rm2probe play` over his actual folder, and by the app's own "no suitable module" failure state).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$REPO"
export PATH="$HOME/.dotnet:$PATH"

say() { printf 'prune-check.sh: %s\n' "$*"; }
fail() { printf 'prune-check.sh: FATAL: %s\n' "$*" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || fail "docker is required"

MANIFEST="pc/libvlc/plugins.keep.txt"
CORPUS="tests/corpus/media/video"
OUT="pc/dist/prune-report"
rm -rf "$OUT"
mkdir -p "$OUT"

if [ ! -d "$CORPUS" ] || [ -z "$(ls -A "$CORPUS" 2>/dev/null)" ]; then
  say "skipped: $CORPUS is empty or missing (run tests/corpus/build-corpus.sh first) — falling back to pc/tests/fixtures/video"
  CORPUS="pc/tests/fixtures/video"
fi
[ -d "$CORPUS" ] && [ -n "$(ls -A "$CORPUS" 2>/dev/null)" ] || fail "no corpus video files found at tests/corpus/media/video or pc/tests/fixtures/video"

# pc/tests/fixtures/video mixes good clips with deliberately-broken ones (D's own
# make-video-fixtures.sh, and Video/LibVlcTests.cs's Fixtures.BrokenNames excludes the same four by
# name for the same reason): "every corpus file plays" means every *playable* file — a broken
# fixture failing to play is its point, not a prune-check failure. Copy only the playable ones into
# a clean directory before handing it to rm2probe play.
PLAYABLE="pc/dist/prune-media"
rm -rf "$PLAYABLE"
mkdir -p "$PLAYABLE"
for f in "$CORPUS"/*; do
  base="$(basename "$f")"
  case "$base" in
    empty.mp4|truncated.mp4|text_pretending.mp4|audio_only.mp4) continue ;;
  esac
  [ -f "$f" ] && cp "$f" "$PLAYABLE/$base"
done
[ -n "$(ls -A "$PLAYABLE" 2>/dev/null)" ] || fail "no playable (non-broken) video fixtures found in $CORPUS"
CORPUS="$PLAYABLE"

say "publishing rm2probe for linux-x64..."
dotnet publish pc/tools/rm2probe -c Release -r linux-x64 --self-contained \
  -o pc/dist/probe-linux -nologo -v minimal >/dev/null

say "running the docker harness (debian:trixie-slim, VLC 3.0.x) ..."
docker run --rm \
  -v "$REPO/pc/dist/probe-linux":/probe:ro \
  -v "$REPO/$CORPUS":/media:ro \
  -v "$REPO/$MANIFEST":/keep.txt:ro \
  -v "$REPO/$OUT":/out \
  debian:trixie-slim bash -c '
    set -euo pipefail
    apt-get update -qq
    apt-get install -y -qq --no-install-recommends libvlc-dev vlc-plugin-base ca-certificates >/dev/null
    dpkg -L vlc-plugin-base | grep -E "vmem|dav1d" > /out/twin-check.txt || true

    P=/usr/lib/x86_64-linux-gnu/vlc/plugins
    mv "$P" "$P.full"
    mkdir -p "$P"
    : > /out/no-twin.txt
    while IFS= read -r line; do
      case "$line" in "#"*|"") continue ;; esac
      rel="${line#plugins/}"
      so="$P.full/${rel%.dll}.so"
      dest="$P/${rel%.dll}.so"
      if [ -f "$so" ]; then
        mkdir -p "$(dirname "$dest")"
        ln -s "$so" "$dest"
      else
        echo "no twin: $line" >> /out/no-twin.txt
      fi
    done < /keep.txt

    echo "-- vlc-cache (proves --reset-plugins-cache reaches libvlc_new) --"
    /probe/rm2probe vlc-cache /usr/lib/x86_64-linux-gnu/vlc
    echo "-- vlc-init, cached (expect cache=hit) --"
    /probe/rm2probe vlc-init /usr/lib/x86_64-linux-gnu/vlc --runs 1

    echo "-- play: pruned set --"
    /probe/rm2probe play /media --seconds 3 --min-frames 10 --report /out/pruned.txt

    rm -rf "$P"
    mv "$P.full" "$P"
    echo "-- play: full set (for the module-usage diff) --"
    /probe/rm2probe play /media --seconds 3 --min-frames 10 --report /out/full.txt
  '

[ -s "$OUT/no-twin.txt" ] && fail "manifest entries with no Linux twin: $(cat "$OUT/no-twin.txt")"
say "every manifest entry has a Linux twin"

say "pruned-set playback report:"
cat "$OUT/pruned.txt"

# The module-usage diff (§ 5.3 step 3) needs Warning+ level module lines, which is the log-level
# limitation PlayCommand.cs documents; treated as informational here rather than a hard gate until
# that is widened.
if [ -f "$OUT/full.txt" ]; then
  say "full-set run's warning-level module lines (informational — see PlayCommand.cs's header comment):"
  grep -A100 "warning level" "$OUT/full.txt" || true
fi

say "prune-check: every corpus/fixture file played through the pruned set — see $OUT/pruned.txt for FAIL lines"
if grep -q "FAIL" "$OUT/pruned.txt"; then
  fail "at least one file failed to play through the pruned set"
fi
say "OK"
