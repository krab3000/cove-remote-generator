#!/usr/bin/env sh
# Builds the generation worker: self-contained binaries per platform (ffmpeg/ffprobe are not bundled — install
# them, or point HL_FFMPEG / HL_FFPROBE at them), and optionally the Docker image (which bundles ffmpeg).
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
  zip="$repo/artifacts/remote-heavylifter-worker-$version-$rid.zip"
  rm -f "$zip"
  (cd "$out" && zip -qr "$zip" . -x '*.pdb')
  echo "Built $zip"
done

if [ "$docker" = 1 ]; then
  docker build -f "$repo/worker/Dockerfile" -t "remote-heavylifter-worker:$version" "$repo"
  echo "Built image remote-heavylifter-worker:$version"
fi
