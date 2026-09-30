# remote-heavylifter

This project moves Cove's heaviest per-video work onto other machines. Generating **covers, preview clips and sprite sheets (with their VTT)** runs on one or more remote *generation servers*, and Cove puts the results in its own `generated` folder.

It has two parts:

| Part | Path | What it is |
|---|---|---|
| Generation server | [server/](server/) | A Python + FastAPI + ffmpeg service. It generates the artifacts for a video it can read from its own copy of the media library. |
| Cove extension | [extension/](extension/) | A C# backend and a React settings UI. It adds a **Remote Generation** settings tab (server list with path mappings, and a Generate panel), a job that appears in Cove's Jobs drawer, and a "Remote generate" entry in Cove's task list. |

```
 Cove (extension)                                   generation server(s)
 ───────────────                                    ────────────────────
 select videos (folder tree, overwrite rules)
 map each file path per server  ── POST /v1/tasks ─▶ ffmpeg: cover / preview / sprite+vtt
 poll                           ── GET  /v1/tasks/…
 download + sha256 check        ◀─ GET  …/artifacts/{kind}
 atomic move into generated/…
 clean up                       ── DELETE /v1/tasks/…
```

Cove always **pulls**, so the servers never need a network route back to Cove. Each server must see the same media files as Cove, possibly under a different path. Per-server path mappings translate between the two.

## Output parity with Cove

- **File locations.** Artifacts go exactly where Cove's own generate task writes them, so Cove serves them with no further changes:
  - `screenshots/xx/{id}.jpg`
  - `previews/xx/{id}.mp4`
  - `vtt/xx/{id}_sprite.jpg`
  - `vtt/xx/{id}_thumbs.vtt`

  Here `xx` is the first two hex digits of SHA-256 over the video id.
- **Selection.** Videos are chosen the way Cove's generate job chooses them:
  - only the primary file is used, filtered by the folders you selected;
  - videos with a custom cover keep it;
  - a sprite only counts as present when its VTT exists too;
  - existing files are kept unless **Overwrite** is on.
- **ffmpeg settings.** These are ported from Cove:
  - Cover: taken at 20% of the duration, `-q:v 2`.
  - Preview: Cove's preview segments, segment duration, start/end exclusions, preset and audio settings, encoded as H.264 crf 21, high profile, level 4.2, width 640.
  - Sprite: up to 81 frames at 160 px, with gap filling and JPEG quality 75.
  - VR videos get Cove's one-eye flat reprojection.
- **Parity pinning.** Expected values derived from Cove's source live in [contract/parity/cove-parity.json](contract/parity/cove-parity.json) and are checked by both test suites.

## Running a generation server

With Docker (recommended; the image bundles the same BtbN ffmpeg builds Cove uses):

```sh
cd server
HL_API_KEYS=$(openssl rand -hex 24) docker compose -f docker-compose.example.yml up -d --build
```

Edit the volume in `docker-compose.example.yml` so that your library is mounted **read-only** at `/mnt/media`.

Without Docker (needs Python 3.12+, and ffmpeg/ffprobe on `PATH`):

```sh
cd server
python -m venv .venv && .venv/bin/pip install .
HL_API_KEYS=secret HL_MEDIA_ROOTS=/mnt/media .venv/bin/heavylifter
```

| Variable | Default | Meaning |
|---|---|---|
| `HL_API_KEYS` | required | Comma-separated bearer keys. The server refuses to start without one unless `HL_ALLOW_NO_AUTH=true`. |
| `HL_MEDIA_ROOTS` | required | Comma-separated folders the server may read. Task paths outside them are rejected, after symlinks and `..` are resolved. |
| `HL_PORT` / `HL_HOST` | `8750` / `0.0.0.0` | Listen address. |
| `HL_MAX_CONCURRENCY` | `cpu/4` | Videos generated at once. Cove also caps this per server. |
| `HL_WORK_DIR` | `./work` | Scratch and finished artifacts, until Cove collects them. |
| `HL_ARTIFACT_TTL_HOURS` | `6` | Uncollected artifacts are deleted after this long. |
| `HL_H264_ENCODER` | `libx264` | Or `h264_nvenc`, `h264_qsv`, `h264_vaapi`, and so on. If a hardware encode fails, the server falls back to libx264. |
| `HL_FFMPEG_INPUT_ARGS` | – | Extra ffmpeg input arguments, for example hwaccel decode flags. |
| `HL_MAX_QUEUE`, `HL_MIN_FREE_GB` | `2000`, `2` | Back-pressure: a full queue gets HTTP 429, and a disk below the free-space limit gets HTTP 507. |

Put a TLS reverse proxy in front of the server if it is reachable outside a trusted LAN.

## Installing the extension

```powershell
pwsh scripts/package.ps1          # or scripts/package.sh
```

This produces `artifacts/remote-heavylifter-<version>.zip`. Install it in Cove from **Settings → Extensions → Install from ZIP** (requires Cove ≥ 1.5.0). Then:

1. Open **Settings → Remote Generation → Generation servers**.
2. Add each server with its URL, API key, and a mapping for every Cove library folder, for example `D:/media → /mnt/media`.
3. Press **Test**. It connects to the server, then checks a few real video paths through each mapping.
4. In **Generate**:
   - pick artifacts, the live servers to use, optional folders, and whether to overwrite;
   - press **Run**.

   Only live servers can be selected. If none are reachable, the job refuses to start: the Run button is disabled and the API answers 409 `NO_LIVE_SERVERS`.
5. Follow progress in Cove's Jobs drawer, which shows one unit per video.

During a run:

- Each server works on up to *min(its max parallel videos, its reported capacity)* videos at once.
- A server that keeps failing is taken out of the run. Its videos move to the other servers, and it rejoins if it recovers.
- If a server cannot find a file, that video is retried on another server.
- Cancelling the job deletes the in-flight remote tasks.

Permissions:

- Managing servers requires `extensions.configure`.
- Starting a run requires both `jobs.run` and `extensions.configure`.
- Viewing server status requires either one.

## Development

```sh
# server
cd server && python -m venv .venv && .venv/Scripts/pip install -e ".[dev]" && .venv/Scripts/python -m pytest
python scripts/export-openapi.py        # after changing the API: refresh contract/openapi.json

# extension (builds against the sibling ../cove checkout when present, else the Cove.Sdk NuGet package)
cd extension && dotnet test RemoteHeavylifter.slnx
cd extension/frontend && npm ci && npm run typecheck && npm run build

python scripts/check-versions.py        # VERSION must match every component
```

- The ffmpeg integration tests in `server/tests` run when `ffmpeg` and `ffprobe` are on `PATH`.
- `LiveServerTests` in the extension exercises a real server when `HL_IT_URL`, `HL_IT_KEY`, `HL_IT_MEDIA_ROOT` and `HL_IT_CLIP` are set.

## Known limitations

- **Duplicated Cove internals.** The generated-file layout and ffmpeg arguments are copies of private Cove code, pinned by the parity fixtures to Cove commit `f4cd955e`. Re-check them when Cove changes its generator.
- **No shared lock with Cove.** Cove's per-video generation lock is not available to extensions. Don't run Cove's own Generate task over the same videos at the same time. Commits are atomic renames, but the last writer wins.
- **Out of scope:** segment thumbnails and previews, stereo VR cards and previews, and phash/MD5.
- **API keys** are stored in Cove's extension data table. They are never returned to the browser, but they are not encrypted at rest.
