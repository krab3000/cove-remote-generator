#!/usr/bin/env sh
# Puts the FFmpeg build the worker's libav engine is bound to (BtbN's n9.0 GPL shared build: FFmpeg.AutoGen 9.0
# bindings need exactly these library majors) into <dest>/ffmpeg, next to the worker:
#   Windows: ffmpeg/{avcodec-63.dll, …, ffmpeg.exe, ffprobe.exe}   (DLLs must sit next to the executables)
#   Linux:   ffmpeg/lib/libavcodec.so.63 …, ffmpeg/bin/{ffmpeg,ffprobe}   (BtbN's layout: the binaries find ../lib)
# The NVIDIA (NVENC/NVDEC/CUDA), Intel QSV and AMD AMF support in these builds loads the GPU driver at run time.
#   scripts/fetch-ffmpeg.sh <rid> <dest>      rid: win-x64 | linux-x64 | linux-arm64
set -eu

rid=$1
dest=$2
branch=9.0
repo=$(cd "$(dirname "$0")/.." && pwd)
libs="avcodec avformat avfilter avutil swresample swscale"

case "$rid" in
  win-x64) asset="ffmpeg-n$branch-latest-win64-gpl-shared-$branch.zip" ;;
  linux-x64) asset="ffmpeg-n$branch-latest-linux64-gpl-shared-$branch.tar.xz" ;;
  linux-arm64) asset="ffmpeg-n$branch-latest-linuxarm64-gpl-shared-$branch.tar.xz" ;;
  *)
    echo "No BtbN FFmpeg build for $rid: that worker uses Homebrew's ffmpeg on macOS (brew install ffmpeg), or HL_FFMPEG_LIBS (FFmpeg $branch shared libraries), else the ffmpeg command line." >&2
    exit 0 ;;
esac

cache="$repo/artifacts/cache"
mkdir -p "$cache"
if [ ! -f "$cache/$asset" ]; then
  curl -fL --retry 5 --retry-all-errors --retry-delay 5 -o "$cache/$asset.part" \
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset"
  mv "$cache/$asset.part" "$cache/$asset"
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
case "$asset" in
  *.zip)
    if command -v unzip >/dev/null 2>&1; then
      unzip -q "$cache/$asset" -d "$tmp"
    else
      # Git Bash on Windows may lack unzip.
      powershell.exe -NoProfile -Command "Expand-Archive -LiteralPath '$(cygpath -w "$cache/$asset")' -DestinationPath '$(cygpath -w "$tmp")'"
    fi ;;
  *) tar -xJf "$cache/$asset" -C "$tmp" ;;
esac
src=$(find "$tmp" -mindepth 1 -maxdepth 1 -type d | head -n 1)

out="$dest/ffmpeg"
rm -rf "$out"
case "$rid" in
  win-*)
    mkdir -p "$out"
    for lib in $libs; do cp "$src"/bin/"$lib"-*.dll "$out"/; done
    cp "$src/bin/ffmpeg.exe" "$src/bin/ffprobe.exe" "$out"/
    "$out/ffmpeg.exe" -hide_banner -version | head -n 1 ;;
  *)
    mkdir -p "$out/lib" "$out/bin"
    for lib in $libs; do cp -P "$src"/lib/lib"$lib".so* "$out/lib"/; done
    cp "$src/bin/ffmpeg" "$src/bin/ffprobe" "$out/bin"/
    echo "FFmpeg $branch shared libraries for $rid in $out" ;;
esac
