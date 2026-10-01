# remote-heavylifter

This project moves Cove's heaviest per-video work onto other machines. Generating **covers, preview clips and sprite sheets (with their VTT)** runs on one or more remote *generation workers*, and Cove puts the results in its own `generated` folder.

It has three parts:

| Part | Path | What it is |
|---|---|---|
| Worker | [worker/](worker/) | A .NET service that runs ffmpeg. It reads each video from Cove over HTTP and uploads the results back, so it needs no access to the media library. |
| Cove extension | [extension/](extension/) | A C# backend and a React settings UI. It adds a **Remote Generation** settings tab (workers, pending workers, and a Generate panel), a job in Cove's Jobs drawer, and a "Remote generate" entry in Cove's task list. |
| Protocol | [protocol/](protocol/) | The JSON-RPC messages and constants shared by the two. |

```
 Cove + extension                                         worker
 ────────────────                                         ──────
 one WebSocket per worker, JSON-RPC (StreamJsonRpc) ◀────▶ either side may dial:
   worker has a URL  → Cove dials ws(s)://worker:8750/rpc
   no URL            → worker dials wss://cove/api/ext/com.cove.remote-heavylifter/worker/ws
 submit(task)  ────────────────────────────────────────▶ ffmpeg/ffprobe read the source URL directly
 GET/HEAD …/worker/tasks/{task}/source  ◀──────────────── (Range requests, X-Heavylifter-Token header)
 PUT …/worker/tasks/{task}/artifacts/{kind} ◀──────────── upload, size + sha256 checked
 taskFinished  ◀──────────────────────────────────────── 
 atomic move into generated/…
```

### Auth

- **One token per worker.** Each worker has a single token, set with `HL_WORKER_TOKEN` or generated on first start and saved in its data folder. The worker prints a short **worker ID** derived from it, so the token itself never has to be shown.
- **The same token works in both directions:**
  - when Cove dials the worker, Cove presents the token and the worker checks it;
  - when the worker dials Cove, it presents the token and Cove looks it up among its workers.
- **Unknown tokens wait for approval.** A worker that dials in with a token Cove doesn't know appears under **Pending workers** until you **Trust** it.
- **Worker HTTP access is narrow.** The worker endpoints skip Cove's own auth (a worker is not a Cove user) and accept only a trusted worker token. Even then, a worker can only read the source and upload the artifacts of a task currently assigned to it. It never holds a Cove API token.
- **Token storage in Cove.** Cove keeps the plain token only for workers it dials. For workers that dial in, it keeps only a hash.

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
  - Perceptual hash (phash) is Cove's video phash, which is Stash's and goimagehash's:
    - 25 frames at 160 px, taken evenly over the middle 90% of the video;
    - laid out in a 5×5 grid and hashed with the 64×64 DCT;
    - no hash at all if any frame fails to decode.

    It is stored as the primary file's `phash` fingerprint. A video counts as hashed when any of its files has one, the same rule Cove uses. On the local test library, the remote hashes are bit-for-bit identical to the ones Cove computes.
  - VR videos get Cove's one-eye flat reprojection.
- **Parity pinning.** Expected values derived from Cove's source live in [contract/parity/cove-parity.json](contract/parity/cove-parity.json) and are checked by both test suites.

## Running a worker

With Docker (recommended; the image bundles the same BtbN ffmpeg builds Cove uses). From the repository root:

```sh
docker build -f worker/Dockerfile -t remote-heavylifter-worker .

# The worker connects to Cove (works behind NAT; nothing listens):
docker run -d --name hl-worker -v hl-worker-data:/var/lib/heavylifter \
  -e HL_COVE_URL=http://192.168.1.10:5073 -e HL_WORKER_NAME=gpu-box remote-heavylifter-worker

# Or Cove connects to the worker:
docker run -d --name hl-worker -p 8750:8750 -v hl-worker-data:/var/lib/heavylifter \
  -e HL_LISTEN_URL=http://0.0.0.0:8750 -e HL_WORKER_NAME=gpu-box remote-heavylifter-worker
```

Keep the data volume: it holds the worker's token, which is its identity.

### Without Docker

You need ffmpeg and ffprobe, either on `PATH` or given with `HL_FFMPEG` / `HL_FFPROBE`:

| OS | Install ffmpeg |
|---|---|
| macOS | `brew install ffmpeg` |
| Debian/Ubuntu | `sudo apt install ffmpeg` |
| Windows | `winget install Gyan.FFmpeg` |

Hardware encoding (`HL_H264_ENCODER=h264_nvenc` and so on) needs an ffmpeg build that includes it.

**From source** (needs the .NET 10 SDK):

```sh
# The worker connects to Cove:
HL_COVE_URL=http://192.168.1.10:5073 HL_WORKER_NAME=my-laptop \
  dotnet run --project worker/RemoteHeavylifter.Worker -c Release
```

```powershell
# Windows (PowerShell)
$env:HL_COVE_URL = "http://192.168.1.10:5073"; $env:HL_WORKER_NAME = "my-pc"
dotnet run --project worker/RemoteHeavylifter.Worker -c Release
```

**As a standalone binary.** No .NET is needed on the target machine:

1. Build:
   ```sh
   scripts/package-worker.sh osx-arm64          # or linux-x64, linux-arm64, win-x64; no argument builds all four
   ```
   The binaries go to `artifacts/worker/<rid>/`, and a zip is written to `artifacts/remote-heavylifter-worker-<version>-<rid>.zip`.
2. Copy the binary to the worker machine and run it there:
   ```sh
   HL_COVE_URL=http://192.168.1.10:5073 HL_WORKER_NAME=gpu-box HL_DATA_DIR=~/.heavylifter \
     ./RemoteHeavylifter.Worker
   ```
   Use `RemoteHeavylifter.Worker.exe` on Windows. On macOS, a binary copied from another machine may first need `xattr -d com.apple.quarantine RemoteHeavylifter.Worker`.

**What happens on startup:**

- The worker prints its **worker ID**, for example `worker ID TJILXX6ZUKTD (token from ./data/worker.token (new))`.
- It then waits until you **Trust** that ID in Cove (Settings → Remote Generation → Pending workers), retrying every 10 s.
- The token lives in `HL_DATA_DIR` (default `./data`, relative to the current directory). Run the worker from the same place, or set `HL_DATA_DIR`, so it keeps the same identity.
- Deleting `worker.token` makes it a new, untrusted worker.

**To let Cove connect to the worker instead,** set `HL_LISTEN_URL=http://0.0.0.0:8750` (instead of, or as well as, `HL_COVE_URL`). Then add the worker in Cove with:

- its URL, `ws://<worker-ip>:8750/rpc`;
- its token, the contents of `worker.token` or the `HL_WORKER_TOKEN` you set.

Stop the worker with Ctrl+C. Tasks it was running are re-queued by Cove on another worker.

| Variable | Default | Meaning |
|---|---|---|
| `HL_COVE_URL` | – | Dial Cove at this `http(s)://` address. |
| `HL_LISTEN_URL` | – | Listen for Cove here, for example `http://0.0.0.0:8750`; Cove dials `ws://host:8750/rpc`. Set at least one of `HL_COVE_URL` and `HL_LISTEN_URL`, or both. |
| `HL_WORKER_TOKEN` | generated | A fixed token, at least 16 characters. Without it, the worker generates one and keeps it in `HL_DATA_DIR/worker.token`. |
| `HL_WORKER_NAME` | host name | Shown in Cove. |
| `HL_DATA_DIR` | `./data` | Token and scratch. |
| `HL_MAX_CONCURRENCY` | `cpu/4` | Videos generated at once, shared by every Cove connected to the worker. Cove also caps this per worker. |
| `HL_H264_ENCODER` | `libx264` | Or `h264_nvenc`, `h264_qsv`, `h264_vaapi`, and so on. If a hardware encode fails, the worker falls back to libx264. |
| `HL_FFMPEG_INPUT_ARGS` | – | Extra ffmpeg input arguments, for example hwaccel decode flags. |
| `HL_FFMPEG`, `HL_FFPROBE` | `ffmpeg`, `ffprobe` | Paths to the binaries. |

Networking notes:

- **The worker always needs HTTP access to Cove,** even when Cove dials the worker: it reads videos and uploads results through Cove. Set **Cove URL for workers** in the extension to an address the workers can reach. A worker that dialed in uses the address it dialed unless that setting, or its own override, is set.
- **Cove's auth-off lockdown.** If Cove's sign-in is off, Cove only trusts requests from local network addresses that also use a local host name (or one listed in `Auth.TrustedHosts`). A worker request from anywhere else makes Cove switch sign-in on to protect itself. Workers on other networks therefore need Cove's sign-in turned on.
- **Reverse proxies.** A proxy in front of Cove (or the worker) must pass WebSocket upgrades (`Upgrade` / `Connection` headers) and allow long-lived connections. Its upload size limit must allow previews of several MB.

## Installing the extension

```powershell
pwsh scripts/package.ps1          # or scripts/package.sh
```

This produces `artifacts/remote-heavylifter-<version>.zip`. Install it in Cove from **Settings → Extensions → Install from ZIP** (requires Cove ≥ 1.5.0). Then:

1. Open **Settings → Remote Generation** and set **Cove URL for workers**.
2. Add workers. Either way, the worker's card shows its connection, worker ID and status:
   - **A worker that dials Cove:** start it with `HL_COVE_URL`. It appears under **Pending workers** with the ID it printed. Check the ID matches, then press **Trust**. You can also add it up front: paste its token and leave the URL empty.
   - **A worker Cove dials:** add it with its `ws://…/rpc` URL and its token.
3. Press **Test** on a worker. It checks the connection, then has the worker read a real video from Cove.
4. In **Generate**:
   - pick artifacts, the live workers to use, optional folders, and whether to overwrite;
   - press **Run**.
5. Follow progress in Cove's Jobs drawer, which shows one unit per video.

During a run:

- Each worker works on up to *min(its max parallel videos, its reported capacity)* videos at once.
- A worker that disconnects, or keeps failing, is taken out of the run. Its videos move to the other workers, and it rejoins when it reconnects.
- Cancelling the job cancels the in-flight tasks and revokes their access.

Permissions:

- Managing workers requires `extensions.configure`.
- Starting a run requires both `jobs.run` and `extensions.configure`.
- Viewing worker status requires either one.

## Development

```sh
dotnet test RemoteHeavylifter.slnx      # extension + worker tests (worker ffmpeg tests run when ffmpeg is on PATH)
cd extension/frontend && npm ci && npm run typecheck && npm run build
python scripts/check-versions.py        # VERSION must match the extension manifest and frontend package
```

The extension builds against the published Cove.Sdk NuGet package by default; pass `-p:UseLocalCoveSdk=true` to build against the sibling `../cove` checkout. For an end-to-end run with Cove and two workers, see [local-test/](local-test/).

## Known limitations

- **Duplicated Cove internals.** The generated-file layout and ffmpeg arguments are copies of private Cove code, pinned by the parity fixtures to Cove commit `f4cd955e`. Re-check them when Cove changes its generator.
- **No shared lock with Cove.** Cove's per-video generation lock is not available to extensions. Don't run Cove's own Generate task over the same videos at the same time. Commits are atomic renames, but the last writer wins.
- **Out of scope:** segment thumbnails and previews, stereo VR cards and previews, and MD5 checksums.
- **Worker tokens** of workers Cove dials are stored in Cove's extension data table. They are never returned to the browser, but they are not encrypted at rest. Workers that dial in are stored as a hash only.
- **Tokens on the worker's command line.** ffmpeg receives the worker token as a `-headers` argument, so it is visible in the worker machine's process list.
