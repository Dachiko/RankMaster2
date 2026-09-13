# Real-media test corpus

The fixtures in `RankMaster2.Server.Tests` are generated in code, deliberately: they are written
against the file-format specifications rather than produced by the same library the server decodes
with, so a bug that cancels out on both sides cannot hide.

This corpus is the other half of that. It is real media — real camera EXIF, real ICC profiles, real
encoders, real containers — because the world produces files that no generator thinks to produce.

**Nothing here is a dependency of the server.** ffmpeg builds the corpus; it never serves it. The
media is git-ignored and tests that use it skip cleanly when it is absent.

```
tests/corpus/build-corpus.sh          # needs curl, ffmpeg, python3 with Pillow
```

## What it contains

| Group | Why it is here |
|---|---|
| `stills/exif_*` (16) | Every EXIF orientation 1–8, landscape and portrait. Orientation is where decoders quietly disagree |
| `stills/photo_*` | Real photographs with real camera metadata |
| `stills/wide_gamut.jpg` | ICC-tagged. A decoder that ignores the profile renders it oversaturated — the exact bug fixed in the desktop app at 1.1.3 |
| `stills/bomb_40mp.jpg` | 8000×5000 from 612 KB. A decoder that allocates before checking dimensions falls over here |
| `stills/cmyk, progressive, grayscale, interlaced, animated, webp` | Encodings a generator would not think to emit |
| `video/*` | One CC-licensed clip in every container `MediaExtensions.Video` accepts: mp4, webm, mkv, avi, mov |
| `video/av1.mp4` | **AV1.** The codec Media Foundation could not play, which is why the desktop app ships LibVLC. If anything is going to fail on a phone, it is this |
| `broken/*` | Empty, truncated, header-only, random noise, and text wearing a `.jpg` extension |

## Provenance

| Path | Source | Licence |
|---|---|---|
| `stills/exif_*` | recurser/exif-orientation-examples | MIT |
| `stills/photo_*` | Wikimedia Commons | CC / public domain |
| `video/*` | Derived from Wikimedia Commons "Volcano Lava Sample" | CC |
| everything else | Derived locally from the above | — |

`manifest.json` records every file with its SHA-256 and size.
