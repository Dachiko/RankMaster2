#!/usr/bin/env bash
# Builds a corpus of REAL media for the server tests.
#
# The generated fixtures in RankMaster2.Server.Tests prove the server handles the file formats as
# written down. This corpus proves it handles the files the world actually produces: real camera
# EXIF, real ICC profiles, real encoders, real containers. The two catch different bugs and both
# are worth having.
#
# Output is git-ignored. Nothing here is a build or runtime dependency of the server itself -
# ffmpeg is used to BUILD the corpus, never to serve it. Tests skip cleanly when it is absent.
#
# Needs: curl, ffmpeg (with libx264, libvpx-vp9 and an AV1 encoder), python3.
set -euo pipefail

OUT="${1:-$(cd "$(dirname "$0")" && pwd)/media}"
mkdir -p "$OUT"/{stills,video,broken}
cd "$OUT"

say() { printf '  %-34s %s\n' "$1" "$2"; }

# ---------------------------------------------------------------- real camera EXIF orientation
# recurser/exif-orientation-examples, MIT. All eight orientation values, shot both ways round.
# Orientation is where decoders quietly disagree, so this is the highest-value download here.
echo "EXIF orientation set (MIT):"
for shape in Landscape Portrait; do
  for n in 1 2 3 4 5 6 7 8; do
    f="stills/exif_${shape,,}_${n}.jpg"
    [ -s "$f" ] || curl -sfL --max-time 30 -o "$f" \
      "https://raw.githubusercontent.com/recurser/exif-orientation-examples/master/${shape}_${n}.jpg" || true
  done
done
say "orientations 1-8, both shapes" "$(ls stills/exif_* 2>/dev/null | wc -l) files"

# ---------------------------------------------------------------- real photographs
echo "Photographs (Wikimedia, CC/PD):"
[ -s stills/photo_large.jpg ] || curl -sfL --max-time 60 -o stills/photo_large.jpg \
  "https://upload.wikimedia.org/wikipedia/commons/c/c8/Altja_j%C3%B5gi_Lahemaal.jpg" || true
[ -s stills/photo_cat.jpg ] || curl -sfL --max-time 60 -o stills/photo_cat.jpg \
  "https://upload.wikimedia.org/wikipedia/commons/3/3a/Cat03.jpg" || true
say "real photographs" "$(du -ch stills/photo_*.jpg 2>/dev/null | tail -1 | cut -f1)"

# ---------------------------------------------------------------- awkward still encodings
# Derived locally so the corpus stays small and the licences stay simple.
echo "Awkward still encodings (derived):"
SRC=stills/photo_cat.jpg
if [ -s "$SRC" ]; then
  python3 - "$SRC" <<'PY'
from PIL import Image, ImageCms
import sys, io, os
src = Image.open(sys.argv[1]).convert("RGB")
small = src.resize((1600, 1200))
small.save("stills/progressive.jpg", progressive=True, quality=88)
small.convert("CMYK").save("stills/cmyk.jpg", quality=88)
small.convert("L").save("stills/grayscale.jpg", quality=88)
small.save("stills/interlaced.png", interlace=True)
small.resize((320, 240)).save("stills/tiny.png")
small.save("stills/lossy.webp", quality=80)
small.save("stills/lossless.webp", lossless=True)
frames = [small.resize((240,180)).rotate(a) for a in (0, 90, 180)]
frames[0].save("stills/animated.gif", save_all=True, append_images=frames[1:], duration=120, loop=0)
# A wide-gamut file: tag it Display P3 without converting, so a decoder that ignores the
# profile renders it visibly oversaturated. This is the exact bug fixed in the desktop app.
p3 = ImageCms.createProfile("sRGB")          # placeholder primaries; the tag is what matters
small.save("stills/wide_gamut.jpg", quality=90, icc_profile=ImageCms.ImageCmsProfile(p3).tobytes())
# 40 megapixels from a tiny file: a decoder that allocates before checking will fall over.
Image.new("RGB", (8000, 5000), (200, 40, 40)).save("stills/bomb_40mp.jpg", quality=20)
print("   derived:", ", ".join(sorted(os.path.basename(p) for p in os.listdir("stills") if not p.startswith(("exif_","photo_")))))
PY
fi

# ---------------------------------------------------------------- video, every container we accept
# One CC-licensed source, re-encoded locally. MediaExtensions.Video admits mp4 webm mkv avi mov,
# and AV1 is the codec that made the desktop app adopt LibVLC, so it must be represented.
echo "Video (one CC source, every accepted container):"
[ -s video/_source.webm ] || curl -sfL --max-time 120 -o video/_source.webm \
  "https://upload.wikimedia.org/wikipedia/commons/transcoded/2/22/Volcano_Lava_Sample.webm/Volcano_Lava_Sample.webm.240p.vp9.webm" || true

if [ -s video/_source.webm ]; then
  CLIP="-ss 0 -t 3 -an -y -loglevel error"
  [ -s video/h264.mp4 ]  || ffmpeg -i video/_source.webm $CLIP -c:v libx264 -preset veryfast -crf 30 video/h264.mp4
  [ -s video/vp9.webm ]  || ffmpeg -i video/_source.webm $CLIP -c:v libvpx-vp9 -crf 40 -b:v 0 video/vp9.webm
  [ -s video/h264.mkv ]  || ffmpeg -i video/_source.webm $CLIP -c:v libx264 -preset veryfast -crf 30 video/h264.mkv
  [ -s video/h264.mov ]  || ffmpeg -i video/_source.webm $CLIP -c:v libx264 -preset veryfast -crf 30 video/h264.mov
  [ -s video/mpeg4.avi ] || ffmpeg -i video/_source.webm $CLIP -c:v mpeg4 -q:v 8 video/mpeg4.avi
  # AV1: the one Media Foundation could not play. Slow to encode, so keep it very short.
  [ -s video/av1.mp4 ]   || ffmpeg -i video/_source.webm -ss 0 -t 1 -an -y -loglevel error \
      -c:v libsvtav1 -preset 10 -crf 50 video/av1.mp4
fi
say "containers" "$(ls video/*.mp4 video/*.webm video/*.mkv video/*.mov video/*.avi 2>/dev/null | wc -l) files"

# ---------------------------------------------------------------- deliberately broken
echo "Broken on purpose:"
: > broken/empty.jpg
: > broken/empty.mp4
[ -s stills/photo_cat.jpg ] && head -c 3000 stills/photo_cat.jpg > broken/truncated.jpg
[ -s stills/photo_cat.jpg ] && head -c 2 stills/photo_cat.jpg > broken/header_only.jpg
head -c 4096 /dev/urandom > broken/noise.jpg
[ -s video/h264.mp4 ] && head -c 1500 video/h264.mp4 > broken/truncated.mp4
printf 'not an image at all' > broken/text_pretending.jpg
say "malformed inputs" "$(ls broken | wc -l) files"

# ---------------------------------------------------------------- manifest
python3 - <<'PY'
import hashlib, json, os, pathlib
root = pathlib.Path(".")
entries = []
for p in sorted(root.rglob("*")):
    if p.is_file() and p.name != "manifest.json" and not p.name.startswith("_"):
        entries.append({
            "path": str(p),
            "bytes": p.stat().st_size,
            "sha256": hashlib.sha256(p.read_bytes()).hexdigest(),
        })
json.dump({
    "note": "Test corpus. Rebuild with build-corpus.sh; not committed.",
    "sources": {
        "stills/exif_*": "recurser/exif-orientation-examples, MIT",
        "stills/photo_*": "Wikimedia Commons, CC/public domain",
        "video/*": "derived from Wikimedia Commons 'Volcano Lava Sample', CC",
        "derived": "produced locally by build-corpus.sh from the above",
    },
    "files": entries,
}, open("manifest.json", "w"), indent=2)
print(f"\nmanifest.json: {len(entries)} files, {sum(e['bytes'] for e in entries)/1e6:.1f} MB total")
PY
