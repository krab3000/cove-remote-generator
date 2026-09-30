# Local end-to-end test bed

This runs Cove and one or two generation servers in Docker. They share a single media folder, and the extension is installed automatically.

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
4. Starts `ghcr.io/yourcove/cove:latest` and the generation server, which is built from `../server`. On first start, Cove seeds `/media` as a library path. Auth is off, and only `localhost` is trusted.
5. Registers the generation server in the extension. The same folder is mounted at different paths, so the path mapping `/media → /mnt/media` is exercised for real.
6. Starts a library scan.

After that, open <http://localhost:5073/settings/remote-generation>, wait for the scan to finish in the Jobs drawer, and press **Generate → Run**. Generated files appear under `.data/cove/generated/`.

| Command | |
|---|---|
| `./setup.ps1 -TwoServers` | Adds `heavylifter-2`, mounted at `/data/library`, for testing distribution and failover. Try `docker compose stop heavylifter-2` in the middle of a run. |
| `./setup.ps1 -CoveFromSource` | Builds Cove from `../../cove` instead of pulling the image. |
| `./setup.ps1 reinstall` | Rebuilds the extension and restarts Cove with it. |
| `./setup.ps1 logs` / `down` | Follow the logs / stop, keeping the data. |
| `./setup.ps1 reset` | Deletes the containers, volumes, `.data` and `.env`. Downloads in `.downloads/` are kept. |

The bash script takes the same options as `--two-servers`, `--cove-from-source` and `--large`.

Ports are Cove `5073`, heavylifter `8750` and heavylifter-2 `8751`. Override them with `COVE_PORT`, `HL_PORT` and `HL2_PORT` in `.env`. The generation server's API key is also in `.env`.
