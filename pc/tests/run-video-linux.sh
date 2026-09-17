#!/usr/bin/env bash
# D-video.md § 6.2: real LibVLC on Linux, in a container. LibVLC is not installed on the build box
# and installing it there is a system change outside this part's authority; it is not needed on the
# host either, because these tests run here instead.
#
# The SDK image is Debian 12, whose VLC is 3.0.23 -- the same major/minor as the Windows payload
# (VideoLAN.LibVLC.Windows 3.0.21); the exact patch level is printed by dpkg below and is recorded in
# the phase report (measured here: 3.0.23-0+deb12u1). LibVLCSharp on Linux finds the system libvlc
# with a bare Core.Initialize() (no argument); LibVlcBackend only calls the path-taking overload when
# a libvlc\win-x64 folder exists next to the executable, which is never true here.
#
# Measured, not assumed: Debian's libvlc5 runtime package ships only the *versioned* shared object
# (libvlc.so.5 / libvlccore.so.9); it does not include the unversioned libvlc.so / libvlccore.so
# symlink that .NET's DllImport resolution needs to find "libvlc" by name (that symlink normally
# ships in a -dev package this image does not have). Without it, Core.Initialize() throws
# DllNotFoundException and every LibVlc-category test reports EngineUnavailable — not a skip, a
# real, wrong failure. The two `ln -s` lines below are the fix, applied once per container.
#
# D-owned (D-video.md § 2). Run from the repo root or anywhere; REPO is resolved below.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

docker run --rm -v "$REPO":/src -w /src mcr.microsoft.com/dotnet/sdk:8.0 bash -c '
  set -euo pipefail
  apt-get update -qq
  apt-get install -y -qq libvlc5 vlc-plugin-base >/dev/null

  echo "libvlc packages:"
  dpkg -l | grep -i vlc || true

  echo "plugin check (both must be present, else the pruned-set assumption in D-video.md is wrong for this image):"
  dpkg -L vlc-plugin-base | grep -E "vmem|dav1d"

  echo "unversioned .so symlinks for DllImport resolution (see header comment above):"
  ln -sf /usr/lib/x86_64-linux-gnu/libvlc.so.5 /usr/lib/x86_64-linux-gnu/libvlc.so
  ln -sf /usr/lib/x86_64-linux-gnu/libvlccore.so.9 /usr/lib/x86_64-linux-gnu/libvlccore.so
  ldconfig

  echo "generating video fixtures (host has ffmpeg with dav1d/svt-av1; the container does not need it,"
  echo "these were generated on the build box and are git-ignored, so generate them here too if absent):"
  if [ ! -d pc/tests/fixtures/video ] || [ -z "$(ls -A pc/tests/fixtures/video 2>/dev/null)" ]; then
    if command -v ffmpeg >/dev/null 2>&1; then
      bash pc/tests/make-video-fixtures.sh
    else
      echo "  (no ffmpeg in this image and no pre-built fixtures on the mounted repo -- video-corpus"
      echo "   tests will skip via Fixtures.AllVideos()/BrokenVideos() finding nothing)"
    fi
  fi

  echo "row 9 (Constructing_the_engine_does_not_load_libvlc) alone first: its own comment says why --"
  echo "once any other LibVlc-category test in this process has touched LibVlcProbe.Available or played"
  echo "a real clip, libvlc.so is mapped for the rest of the process and this row will (truthfully) find"
  echo "it, though for a reason unrelated to what it proves. Run before anything else has loaded it:"
  dotnet test pc/tests/RankMaster2.Pc.Tests --filter "FullyQualifiedName~Constructing_the_engine_does_not_load_libvlc"

  echo "the rest of Category=LibVlc:"
  dotnet test pc/tests/RankMaster2.Pc.Tests --filter "Category=LibVlc&FullyQualifiedName!~Constructing_the_engine_does_not_load_libvlc"
'
