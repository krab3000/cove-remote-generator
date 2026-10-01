# Local end-to-end test bed

This runs Cove and one or two generation workers in Docker, with the extension installed automatically. Workers have no media mounts: they read videos from Cove and upload results over HTTP.

```powershell
cd local-test
./setup.ps1                 # Windows / PowerShell 7
./setup.sh                  # Linux / macOS (needs python3)
```

The script:

1. Builds `artifacts/remote-heavylifter-<version>.zip` with `scripts/package.*`.
2. Unpacks the zip into `.data/cove/config/extensions/com.cove.remote-heavylifter/`. Cove loads extensions from `/config/extensions` at startup, so nothing needs to be clicked.
3. Downloads sample videos into `.data/media`:
   - *Big Buck Bunny*
   - *Elephants Dream*
   - a 10-second clip
   - 8 "episodes" cut from Big Buck Bunny

   These are Blender open movies (CC-BY), about 230 MB, and are cached in `.downloads/`. Add `-Large` / `--large` for 1080p Big Buck Bunny and *Sintel* (about 1.5 GB more).
4. Starts `ghcr.io/yourcove/cove:latest` and `worker-1`, which is built from `../worker/Dockerfile`. On first start, Cove seeds `/media` as a library path. Auth is off, and only `localhost` is trusted; the workers count as local because they reach Cove as `http://cove:5073` on Docker's private network.
5. Sets the **Cove URL for workers** to `http://cove:5073` and registers the workers, using the tokens in `.env`:
   - `worker-1` dials Cove, so it is registered without a URL;
   - `worker-2` (`-TwoServers`) listens, so it is registered with `ws://worker-2:8750/rpc` and Cove dials it.

   It then waits until they are live.
6. Starts a library scan.

After that, open <http://localhost:5073/settings/remote-generation>, wait for the scan to finish in the Jobs drawer, and press **Generate → Run**. Generated files appear under `.data/cove/generated/`.

| Command | |
|---|---|
| `./setup.ps1 -TwoServers` | Adds `worker-2`, which Cove dials, for testing distribution and failover. Try `docker compose --profile multi stop worker-2` in the middle of a run; Cove re-dials it when it is back. |
| `./setup.ps1 -CoveFromSource` | Builds Cove from `../../cove` instead of pulling the image. |
| `./setup.ps1 reinstall` | Rebuilds the extension and restarts Cove with it. |
| `./setup.ps1 logs` / `down` | Follow the logs / stop, keeping the data. |
| `./setup.ps1 reset` | Deletes the containers, volumes, `.data` and `.env`. Downloads in `.downloads/` are kept. |

The bash script takes the same options as `--two-servers` (or `--two-workers`), `--cove-from-source` and `--large`.

Ports are Cove `5073` and worker-2 `8751`; worker-1 opens no port. Override them with `COVE_PORT` and `HL2_PORT` in `.env`, which also holds `WORKER1_TOKEN` and `WORKER2_TOKEN`.

To try the pending/trust flow, start a worker with a fresh token that only dials Cove. It appears under **Pending workers** with the ID it prints:

```sh
docker run --rm --network heavylifter-local_default -e HL_COVE_URL=http://cove:5073 -e HL_WORKER_NAME=extra remote-heavylifter-worker:local
```
