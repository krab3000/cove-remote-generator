#!/usr/bin/env sh
# Builds the generation worker: self-contained binaries per platform with the FFmpeg n9.0 shared build its libav
# engine binds to in an ffmpeg/ folder beside them (scripts/fetch-ffmpeg.sh; none for osx-arm64, which uses
# HL_FFMPEG_LIBS or an installed ffmpeg), and optionally the Docker image.
#   scripts/package-worker.sh [--docker] [rid ...]     default rids: linux-x64 linux-arm64 osx-arm64 win-x64
set -eu

repo=$(cd "$(dirname "$0")/.." && pwd)
version=$(tr -d '[:space:]' < "$repo/VERSION")
docker=0
rids=""
for arg in "$@"; do
  case "$arg" in
    --docker) docker=1 ;;
    *) rids="$rids $arg" ;;
  esac
done
[ -n "$rids" ] || rids="linux-x64 linux-arm64 osx-arm64 win-x64"

for rid in $rids; do
  out="$repo/artifacts/worker/$rid"
  rm -rf "$out"
  dotnet publish "$repo/worker/RemoteHeavylifter.Worker/RemoteHeavylifter.Worker.csproj" -c Release -r "$rid" \
    --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out"
  # Outside the single-file bundle: libav is loaded from these files at run time.
  "$repo/scripts/fetch-ffmpeg.sh" "$rid" "$out"
  zip="$repo/artifacts/remote-heavylifter-worker-$version-$rid.zip"
  rm -f "$zip"
  if command -v zip >/dev/null 2>&1; then
    (cd "$out" && zip -qr "$zip" . -x '*.pdb')
  else
    # Git Bash on Windows has no zip.
    powershell.exe -NoProfile -Command "Get-ChildItem -LiteralPath '$(cygpath -w "$out")' -Exclude *.pdb | Compress-Archive -DestinationPath '$(cygpath -w "$zip")'"
  fi
  echo "Built $zip"
done

if [ "$docker" = 1 ]; then
  docker build -f "$repo/worker/Dockerfile" -t "remote-heavylifter-worker:$version" "$repo"
  echo "Built image remote-heavylifter-worker:$version"
fi
