#!/usr/bin/env bash
# Local end-to-end test bed: Cove + generation worker(s), extension pre-installed. Workers read videos from Cove over HTTP.
#
#   ./setup.sh [up]            build extension, install it, create sample videos, start, register workers, scan
#   ./setup.sh up --two-servers   (alias --two-workers): adds worker-2, which Cove dials
#   ./setup.sh reinstall       rebuild the extension zip and hot-swap it into the running Cove
#   ./setup.sh logs | down
#   ./setup.sh reset           delete ALL local test data
#
# Options: --two-servers|--two-workers  --cove-from-source  --skip-build  --large (adds 1080p BBB + Sintel, ~1.5 GB)
# Needs: docker, python3 (and node/npm + dotnet unless --skip-build).
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(dirname "$here")
data="$here/.data"
extension_id="com.cove.remote-heavylifter"
extension_dir="$data/cove/config/extensions/$extension_id"
env_file="$here/.env"
cove_port=${COVE_PORT:-5073}
# 127.0.0.1, not localhost: Docker Desktop's IPv6 port forwarder can accept connections and never answer.
cove="http://127.0.0.1:$cove_port"
owner_user=admin
owner_password=adminadmin
api="$cove/api/ext/$extension_id"

command=up
two_servers=0
from_source=0
skip_build=0
large=0
for arg in "$@"; do
  case "$arg" in
    up|reinstall|logs|down|reset) command=$arg ;;
    --two-servers|--two-workers) two_servers=1 ;;
    --large) large=1 ;;
    --cove-from-source) from_source=1 ;;
    --skip-build) skip_build=1 ;;
    *) echo "unknown argument: $arg" >&2; exit 2 ;;
  esac
done

compose() {
  local files=(-f "$here/docker-compose.yml")
  [ "$from_source" = 1 ] && files+=(-f "$here/docker-compose.cove-source.yml")
  local profiles=()
  [ "$two_servers" = 1 ] && profiles=(--profile multi)
  docker compose --project-directory "$here" "${files[@]}" ${profiles[@]+"${profiles[@]}"} "$@"
}

step() { printf '\033[36m==> %s\033[0m\n' "$*"; }

wait_http() { # url what timeout [json]  -- json: unknown /api paths fall through to the SPA (200 HTML)
  local deadline=$((SECONDS + $3)) result
  while [ $SECONDS -lt $deadline ]; do
    result=$(curl -s -o /dev/null -w '%{http_code} %{content_type}' -m 5 "$1" || true)
    if [[ "$result" == 200* ]] && { [ "${4:-}" != json ] || [[ "$result" == *json* ]]; }; then return 0; fi
    sleep 3
  done
  echo "$2 did not come up within $3 s ($1). Check: ./setup.sh logs" >&2
  exit 1
}

install_extension() {
  if [ "$skip_build" = 0 ]; then
    step "Building the extension package"
    "$repo/scripts/package.sh"
  fi
  zip="$repo/artifacts/remote-heavylifter-$(tr -d '[:space:]' < "$repo/VERSION").zip"
  [ -f "$zip" ] || { echo "$zip not found (run without --skip-build)" >&2; exit 1; }
  step "Installing $(basename "$zip") into Cove's config/extensions"
  rm -rf "$extension_dir"
  mkdir -p "$extension_dir"
  python3 -m zipfile -e "$zip" "$extension_dir"
}

run_ffmpeg() { # media-dir args... (paths relative to the media dir)
  local media=$1; shift
  if command -v ffmpeg >/dev/null; then
    (cd "$media" && ffmpeg "$@")
  else
    docker run --rm --user root --entrypoint /usr/local/bin/ffmpeg -v "$media:/work" -w /work remote-heavylifter-worker:local "$@"
  fi
}

download() { # url -> prints the cached (and unzipped) file path
  local cache="$here/.downloads" url=$1
  mkdir -p "$cache"
  local file="$cache/$(basename "$url")"
  if [ ! -f "$file" ]; then
    echo "    downloading $url" >&2
    curl -fL --retry 3 -o "$file.part" "$url" >&2
    mv "$file.part" "$file"
  fi
  if [[ "$file" == *.zip ]]; then
    local unpacked="${file%.zip}"
    if [ ! -f "$unpacked" ]; then
      rm -rf "$cache/unzip-tmp"
      python3 -m zipfile -e "$file" "$cache/unzip-tmp"
      mv "$(python3 -c 'import pathlib, sys; print(max((p for p in pathlib.Path(sys.argv[1]).rglob("*") if p.is_file()), key=lambda p: p.stat().st_size))' "$cache/unzip-tmp")" "$unpacked"
      rm -rf "$cache/unzip-tmp"
    fi
    file=$unpacked
  fi
  echo "$file"
}

sample_media() {
  local media="$data/media"
  if [ -n "$(find "$media" -type f -print -quit)" ]; then
    step "Sample media already present in $media"
    return
  fi
  step "Fetching sample videos (Blender open movies, CC-BY) into $media"
  local list='Movies/Big Buck Bunny (2008)/Big Buck Bunny.mp4|https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_640x360.m4v.zip
Movies/Elephants Dream (2006)/Elephants Dream.mov|https://download.blender.org/ED/elephantsdream-480-h264-st-aac.mov
Shorts/Big Buck Bunny - 10s clip.mp4|https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/720/Big_Buck_Bunny_720_10s_1MB.mp4'
  if [ "$large" = 1 ]; then
    list+='
Movies/Big Buck Bunny 1080p (2008)/Big Buck Bunny 1080p.mp4|https://download.blender.org/demo/movies/BBB/bbb_sunflower_1080p_30fps_normal.mp4.zip
Movies/Sintel (2010)/Sintel.mkv|https://download.blender.org/durian/movies/Sintel.2010.1080p.mkv'
  fi
  while IFS='|' read -r name url; do
    mkdir -p "$media/$(dirname "$name")"
    local source
    source=$(download "$url")
    if [[ "$source" == *.m4v ]]; then
      # Blender's BigBuckBunny_640x360.m4v ends in a truncated MP4 box that Cove's scanner rejects;
      # a lossless remux writes a clean container.
      cp "$source" "$media/remux-source.m4v"
      run_ffmpeg "$media" -v error -y -i remux-source.m4v -c copy -map 0 -movflags +faststart "$name"
      rm "$media/remux-source.m4v"
    else
      cp "$source" "$media/$name"
    fi
    echo "    $name"
  done <<< "$list"

  # Cut Big Buck Bunny into "episodes" (stream copy, instant) so there are enough videos to spread over workers.
  local episodes="Shows/Big Buck Bunny Chapters/Season 1"
  mkdir -p "$media/$episodes"
  for i in 0 1 2 3 4 5 6 7; do
    local name
    name=$(printf '%s/S01E%02d.mp4' "$episodes" $((i + 1)))
    run_ffmpeg "$media" -v error -y -ss $((i * 70)) -t 70 -i "Movies/Big Buck Bunny (2008)/Big Buck Bunny.mp4" -c copy -map 0 "$name"
    echo "    $name"
  done
}

register_workers() {
  step "Waiting for the extension's API"
  if ! curl -fsS "$cove/api/extensions" | grep -q "\"$extension_id\""; then
    echo "Cove did not load $extension_id (it must be built against a Cove.Sdk no newer than the running Cove); see ./setup.sh logs" >&2
    exit 1
  fi
  wait_http "$api/workers" "The remote-heavylifter extension" 120 json

  # Workers reach Cove on Docker's network by its service name.
  curl -fsS -X PUT -H 'Content-Type: application/json' --data '{"coveUrlForWorkers":"http://cove:5073","coveAuthEnabled":false}' \
    "$api/settings" >/dev/null

  local body
  body=$(curl -fsS "$api/workers" | T1="$worker1_token" T2="$worker2_token" TWO="$two_servers" python3 -c '
import json, os, sys
workers = [dict(w, token=None) for w in json.load(sys.stdin)]
for w in workers:
    for k in ("hasToken", "tokenHint", "workerTokenId", "connection"):
        w.pop(k, None)
wanted = [("worker-1", os.environ["T1"], None)]
if os.environ["TWO"] == "1":
    wanted.append(("worker-2", os.environ["T2"], "ws://worker-2:8750/rpc"))
names = {w["name"] for w in workers}
added = [x for x in wanted if x[0] not in names]
workers += [{"id": None, "name": n, "token": t, "url": u, "coveUrlOverride": None, "enabled": True, "maxConcurrency": 2}
            for n, t, u in added]
print(json.dumps(workers) if added else "")
')
  if [ -n "$body" ]; then
    step "Registering generation worker(s)"
    curl -fsS -X PUT -H 'Content-Type: application/json' --data "$body" "$api/workers" >/dev/null
  else
    step "Generation workers already registered"
  fi

  step "Waiting for the workers to connect"
  local deadline=$((SECONDS + 90)) states=""
  while [ $SECONDS -lt $deadline ]; do
    states=$(curl -fsS "$api/workers/health?refresh=true" | python3 -c '
import json, sys
for w in json.load(sys.stdin):
    print("    %-9s %-12s %s" % (w["name"], w["state"], w["connection"]) + (" - %s" % w["error"] if w["error"] else ""))')
    if ! grep -qvE ' live ' <<< "$states"; then break; fi
    sleep 3
  done
  echo "$states"
}

complete_cove_setup() {
  # Setup is complete once an owner exists and a library path is configured (compose seeds /media).
  # Until an owner exists, auth-disabled requests run without any permissions.
  if curl -fsS "$cove/api/auth/bootstrap-status" | grep -q '"ownerExists":false'; then
    step "Creating the owner account ($owner_user / $owner_password)"
    curl -fsS -X POST -H 'Content-Type: application/json'       --data "{\"username\":\"$owner_user\",\"password\":\"$owner_password\"}" "$cove/api/auth/bootstrap-owner" >/dev/null
  else
    step "Owner account already exists"
  fi
  curl -fsS "$cove/api/system/config" | grep -q '"path":"/media"'     || echo "WARNING: Cove has no /media library path; add it in Settings -> Library." >&2
  step "Waiting for requests to run as the owner"
  wait_http "$cove/api/jobs" "Owner permissions" 60 json
}

case "$command" in
  logs) compose logs -f --tail 100; exit ;;
  down) compose down; exit ;;
  reset)
    compose --profile multi down -v
    rm -rf "$data" "$env_file"
    step "Local test data removed"
    exit ;;
  reinstall)
    install_extension
    step "Restarting Cove to load the new build"
    compose restart cove
    wait_http "$cove/health" "Cove" 300
    step "Done - reload the browser tab"
    exit ;;
esac

docker info >/dev/null 2>&1 || { echo "Docker is not running." >&2; exit 1; }
mkdir -p "$data/media" "$data/cove/config/extensions" "$data/cove/generated" "$data/cove/backups"

new_token() { python3 -c 'import secrets; print(secrets.token_urlsafe(32))'; }
[ -f "$env_file" ] || printf 'COVE_PORT=%s\n' "$cove_port" > "$env_file"
grep -q '^WORKER1_TOKEN=' "$env_file" || printf 'WORKER1_TOKEN=%s\n' "$(new_token)" >> "$env_file"
grep -q '^WORKER2_TOKEN=' "$env_file" || printf 'WORKER2_TOKEN=%s\n' "$(new_token)" >> "$env_file"
worker1_token=$(grep '^WORKER1_TOKEN=' "$env_file" | cut -d= -f2-)
worker2_token=$(grep '^WORKER2_TOKEN=' "$env_file" | cut -d= -f2-)

install_extension
step "Building the generation worker image"
compose build worker-1
sample_media

step "Starting Cove and the generation worker(s)"
cove_was_running=$(compose ps --status running -q cove 2>/dev/null || true)
compose up -d --remove-orphans
if [ -n "$cove_was_running" ]; then
  step "Restarting Cove to load the freshly installed extension"
  compose restart cove
fi
step "Waiting for Cove (the first start runs database migrations; this can take a few minutes)"
wait_http "$cove/health" "Cove" 600

complete_cove_setup
register_workers

step "Scanning the sample library"
curl -fsS -X POST -H 'Content-Type: application/json' --data '{}' "$cove/api/metadata/scan" >/dev/null \
  || echo "Could not start the scan automatically; run it from Settings -> Tasks." >&2

cat <<EOF

Ready.
  Cove:                $cove   (owner: $owner_user / $owner_password; auth is off for local requests)
  Remote Generation:   $cove/settings/remote-generation
  Extension zip:       $zip
  Workers:             worker-1 dials Cove; worker-2 (--two-servers) is dialed by Cove (tokens in local-test/.env)
  Sample media:        $data/media   (Cove: /media; workers read it from Cove over HTTP)
  Generated files:     $data/cove/generated

Wait for the scan to finish (Jobs drawer), then Settings -> Remote Generation -> Generate -> Run.
EOF
