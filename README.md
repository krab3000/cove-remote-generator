# remote-heavylifter

This project moves Cove's heaviest per-video work onto other machines. Generating **covers, preview clips and sprite sheets (with their VTT)** runs on one or more remote *generation workers*, and Cove puts the results in its own `generated` folder.

It has three parts:

| Part | Path | What it is |
|---|---|---|---|
| Worker | [worker/](worker/) | A .NET service that runs ffmpeg. It reads each video from Cove over HTTP and uploads the results back, so it needs no access to the media library. |
| Cove extension | [extension/](extension/) | A C# backend and a React settings UI. It adds a **Remote Generation** settings tab (workers, pending workers, and a Generate panel), a job in Cove's Jobs drawer, and a "Remote generate" entry in Cove's task list. |
| Protocol | [protocol/](protocol/) | The JSON-RPC messages and constants shared by the two. |

```
 Cove + extension                                         worker
 ────────────────                                         ──────
 one WebSocket per worker, JSON-RPC (StreamJsonRpc) ◀────▶ either side may dial:
   worker has a URL  → Cove dials ws(s)://worker:8750/rpc
   no URL            → worker dials wss://cove/api/ext/com.cove.remote-heavylifter/worker/ws
 submit(task)  ────────────────────────────────────────▶ libav reads the source URL (or a mapped file)
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

### Media engine

The worker reads and encodes video with libav only:

- FFmpeg's libraries run inside the worker process, through the [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) 9.0 bindings. Each step opens its video once and keeps one decoder (on its slot's GPU for codecs that GPU decodes). Between sprite and phash timestamps it decodes on instead of seeking whenever a seek would decode the same GOP again, and frames reach the image code in memory. Seeks, filters, autorotation and encoder settings mirror Cove's ffmpeg command line.
  - The bindings need exactly FFmpeg 9.0's libraries (avcodec 63, avformat 63, avfilter 12, avutil 61, swscale 10, swresample 7). The Docker image and the standalone packages bundle BtbN's `n9.0` GPL shared build, which includes NVENC, NVDEC/CUDA, QSV and AMF.
  - Outputs match Cove's ffmpeg command line closely, not bit for bit: a previewed segment can differ by a frame, and sprite frames skip a JPEG round trip.
- The worker never starts ffmpeg or ffprobe and does not start without these libraries. The tests still run Cove's ffmpeg command lines (from `PATH`) as the reference the libav output is compared with.

A native crash inside libav takes the whole worker down. Run the worker under a restart policy (Docker `--restart unless-stopped`, a service manager), and use `--hwaccel-skip-codecs` for a codec a GPU decoder misbehaves on.

### Docker

With Docker (recommended; the image bundles BtbN's FFmpeg n9.0 shared build). From the repository root:

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

The standalone packages below bundle FFmpeg's libraries in an `ffmpeg` folder next to the worker; on Windows and Linux nothing else is needed. Run from source, or on macOS (BtbN has no macOS build), the worker needs FFmpeg 9.0's shared libraries in `HL_FFMPEG_LIBS` (or where it looks by default); running the tests also needs `ffmpeg` and `ffprobe` on `PATH`:

| OS | Install ffmpeg |
|---|---|
| macOS | `brew install ffmpeg` |
| Debian/Ubuntu | `sudo apt install ffmpeg` |
| Windows | `winget install Gyan.FFmpeg` |

**macOS.** Homebrew's `ffmpeg` (9.x) works for both engines, with no `HL_FFMPEG_LIBS`. The libav engine finds its shared libraries in `/opt/homebrew/opt/ffmpeg/lib` (Apple Silicon) or `/usr/local/opt/ffmpeg/lib` (Intel), and the build includes Apple's VideoToolbox:

- **The worker gets an `apple0` slot pool** that encodes previews with `h264_videotoolbox` and decodes nothing, plus its CPU slots. Cove sends `apple0` previews only, and covers, sprites and phashes to the CPU slots.
- **Why it doesn't decode on the GPU:** on an M1 Pro, a 1080p H.264 source took 9.3 s for all four artifacts with CPU decoding and VideoToolbox encoding, against 9.9 s all on the CPU, and 49 s with VideoToolbox decoding, because the multithreaded CPU decoder is several times faster than VideoToolbox's plus the copy back from GPU memory.
- **Slots:** `--hv-apple-slots=0,2` sets how many previews encode at once (default 2), `--cpu-slots` the rest.

To run a worker on an Apple Silicon Mac:

1. Install Homebrew's ffmpeg, then build the worker:
   ```sh
   brew install ffmpeg
   scripts/package-worker.sh osx-arm64          # → artifacts/worker/osx-arm64/RemoteHeavylifter.Worker
   ```
2. Start it, with your Cove's address in `HL_COVE_URL`:
   ```sh
   HL_COVE_URL=https://cove.example.lan \
   HL_WORKER_NAME=$(scutil --get LocalHostName) \
   HL_CPU_SLOTS=4 \
   HL_DATA_DIR="$HOME/.heavylifter" \
   artifacts/worker/osx-arm64/RemoteHeavylifter.Worker
   ```

On startup it should log `Media engine: libav (FFmpeg 9.x) from /opt/homebrew/opt/ffmpeg/lib` and `Slots apple0 (Apple media engine, videotoolbox): 2 slots, h264_videotoolbox, decodes in software (no GPU decoders)`.

On first start it prints a worker ID and waits until you **Trust** that ID in Cove (Settings → Remote Generation → Pending workers). `HL_DATA_DIR` holds the worker's token, so keep it the same between runs.

If you copy the binary to another Mac, it needs `brew install ffmpeg` there too. Clear macOS's download quarantine once with `xattr -d com.apple.quarantine RemoteHeavylifter.Worker`.

If Cove is served over HTTPS with a private CA, trust that CA in the macOS keychain. If Cove's sign-in is off, add the address's host name to Cove's `Auth.TrustedHosts`, or the worker's requests will make Cove turn sign-in on.

Hardware decoding and encoding (VA-API, CUDA/NVDEC, NVENC) need an ffmpeg build that includes them. For the libav engine from source, put the shared libraries where the worker looks: `scripts/fetch-ffmpeg.sh win-x64 artifacts/ffmpeg/win-x64` and `HL_FFMPEG_LIBS=artifacts/ffmpeg/win-x64/ffmpeg` (the tests find that folder by themselves).

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
   The binaries go to `artifacts/worker/<rid>/` with FFmpeg in `artifacts/worker/<rid>/ffmpeg/` (`scripts/fetch-ffmpeg.sh` downloads it once into `artifacts/cache/`), and a zip is written to `artifacts/remote-heavylifter-worker-<version>-<rid>.zip`. The FFmpeg libraries make a package about 120 MB; they are GPL builds, which makes the package GPL too.
2. Copy the binary and its `ffmpeg` folder (or the zip) to the worker machine and run it there:
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

Every setting is an environment variable and a command-line flag named after it (`HL_COVE_URL` is `--cove-url http://…` or `--cove-url=http://…`); a flag wins over the variable. `--help` lists them all.

| Variable | Flag | Default | Meaning |
|---|---|---|---|
| `HL_COVE_URL` | `--cove-url` | – | Dial Cove at this `http(s)://` address. |
| `HL_LISTEN_URL` | `--listen-url` | – | Listen for Cove here, for example `http://0.0.0.0:8750`; Cove dials `ws://host:8750/rpc`. Set at least one of `HL_COVE_URL` and `HL_LISTEN_URL`, or both. |
| `HL_WORKER_TOKEN` | `--worker-token` | generated | A fixed token, at least 16 characters. Without it, the worker generates one and keeps it in `HL_DATA_DIR/worker.token`. |
| `HL_WORKER_NAME` | `--worker-name` | host name | Shown in Cove. |
| `HL_DATA_DIR` | `--data-dir` | `./data` | Token and scratch. A step's scratch files are deleted as soon as that step ends. |
| `HL_HV_INTEL_SLOTS` | `--hv-intel-slots` | every detected Intel GPU: 2 | `gpu_idx,slots`: videos at once on that Intel GPU (Quick Sync decode and `h264_qsv` encode on its render node; see `HL_INTEL_API`). Repeat the flag for more GPUs, or `0,2;1,2`; `slots` 0 leaves a GPU out. Once any `--hv-*-slots` is given, only the GPUs listed are used. GPUs are numbered per vendor in PCI order (Intel GPUs on Windows: DXGI adapter order), as the startup log lists them. |
| `HL_HV_NVIDIA_SLOTS` | `--hv-nvidia-slots` | every detected NVIDIA GPU: 2 | As `--hv-intel-slots`, for NVIDIA GPUs (CUDA/NVDEC decode, `h264_nvenc` encode on the same GPU). |
| `HL_HV_APPLE_SLOTS` | `--hv-apple-slots` | `0,2` on macOS | As `--hv-intel-slots`, for the Mac's media engine (previews only, see macOS above). |
| `HL_CPU_SLOTS` | `--cpu-slots` | `1` | Videos at once decoded and encoded in software. Cove sends these videos no GPU decodes, and with "Let CPU slots help" also overflow from busy GPUs. |
| `HL_INTEL_API` | `--intel-api` | `qsv` | How Intel GPUs decode and encode. `qsv`: Quick Sync, with codecs that have a Quick Sync decoder; VC-1 decodes in software, because FFmpeg's `vc1_qsv` crashes the worker. `vaapi` (Linux only): VA-API decode (VC-1 included) and `h264_vaapi`. On a UHD 770 (1080p, 2 at once) Quick Sync did 5.5 videos/min at 20% CPU; VA-API kept failing to copy frames off the GPU and fell back to software (3.5 videos/min, 62% CPU). |
| `HL_SOURCE_CACHE_MB` | `--source-cache-mb` | `1024` | RAM for caching video bytes read from Cove, shared by all running videos. ffmpeg reads each video through a loopback endpoint in the worker, so its many seeks reuse one download of the header and of each byte range. `0` makes ffmpeg read from Cove directly. After each step the log shows the cache's hit rate and how much was read from Cove. |
| `HL_HWACCEL_SKIP_CODECS` | `--hwaccel-skip-codecs` | – | Comma-separated codecs (libav names: `vc1`, `mpeg2video`, `hevc` …) decoded in software on every GPU, whatever the GPU reports. |
| `HL_FFMPEG_INPUT_ARGS` | `--ffmpeg-input-args` | – | Extra demuxer/protocol options for every source, written as ffmpeg input options (`-rw_timeout 30000000`). |
| `HL_FFMPEG_LIBS` | `--ffmpeg-libs` | bundled | Directory with the FFmpeg 9.0 shared libraries. By default the bundled `ffmpeg` folder (`ffmpeg/lib` on Linux), then `/opt/ffmpeg/lib`, then on macOS Homebrew's `/opt/homebrew/opt/ffmpeg/lib` and `/usr/local/opt/ffmpeg/lib`. |
| `HL_SPRITE_SEEK` | `--sprite-seek` | `exact` | How sprite frames are found. `exact`: the frame at each timestamp, as Cove does. `keyframe`: the keyframe at or before it (`-noaccurate_seek -skip_frame nokey` on the command line); only keyframes are decoded, so sprites of long-GOP or 4K videos are many times faster (a 4K HEVC film: 14 s → 0.6 s per sprite with 8 videos at once), and a thumbnail can be up to one GOP (a few seconds) earlier than its time slot. The cover and phash always seek exactly. |
| `HL_DECODE_THREADS` | `--decode-threads` | auto | Decoder threads per video with libav. By default a video's share of the CPU (cores ÷ all slots, at most 16) for cover, sprite and phash frames, and libav's own choice for previews. |

GPU slots: at startup the worker finds its GPUs (Linux: Intel through VA-API on its render node, run with Quick Sync by default, NVIDIA through CUDA/NVDEC; macOS: the media engine; Windows: Intel through D3D11 video decoding on its DXGI adapter, run with Quick Sync, NVIDIA through CUDA/NVDEC) and what each decodes, and logs one line per slot pool, e.g. `Slots nvidia0 (NVIDIA GeForce GTX 1660 Ti, cuda 0, 1 video engine): 2 slots, h264_nvenc@0, decodes h264 hevc …`. Cove treats the slots of all workers as one pool:

- A video some GPU decodes (by its codec and size) runs whole on a GPU slot that decodes it.
- A video no GPU decodes (WMV2, Xvid …) is split: its preview goes to a GPU slot (decoded in software, encoded on the GPU), its cover, sprite and phash to a CPU slot.
- Each slot takes the oldest task it may run as soon as it is free, so every slot stays busy while there is work for it; a codec only one GPU decodes waits for that GPU.
- **Let CPU slots help** (Remote Generation settings): when every GPU slot is busy, an idle CPU slot does a waiting video's cover, sprite and phash; its preview stays on a GPU.

`bench` measures each slot pool and suggests slots: `RemoteHeavylifter.Worker bench [--bench-counts 1,2,3,4] [--bench-clips 1080p,4k]` downloads Big Buck Bunny into `HL_DATA_DIR/bench` once, runs the full pipeline with 1, 2, 3 … videos at once on every GPU and the CPU, and prints videos/min, seconds per video and CPU use, then the fewest videos at once reaching 90% of each pool's best, as flags to paste (e.g. `--hv-nvidia-slots=0,2 --cpu-slots=2`).

Reading sources directly: a worker that sees Cove's video folders on its own disk (on the same machine as Cove, or through a network share) can read source videos from there instead of over HTTP. In Cove, under the worker's settings, choose **Source videos → Directly from the worker's disk** and map each Cove folder (as Cove stores the path, for example `/media` inside Cove's container) to the same folder on the worker (for example `/srv/media`). Cove then sends each task the worker's path. A video outside the mapped folders, or one the worker cannot open at the mapped path, is read over HTTP (the worker logs a warning), and **Test** reports which way the sample video was read. Uploads always go over HTTP, and the RAM source cache is used only for HTTP reads.

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

- Each slot pool of each worker (a GPU, or the CPU) works on up to the number of slots the worker reports for it; Cove routes each video to a slot that can run it (see "GPU slots" above).
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
