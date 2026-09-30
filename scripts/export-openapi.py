"""Regenerate contract/openapi.json from the FastAPI app. Run from anywhere with the server venv:

    server/.venv/Scripts/python scripts/export-openapi.py      (Windows)
    server/.venv/bin/python scripts/export-openapi.py          (Linux/macOS)
"""

from __future__ import annotations

import json
import sys
import tempfile
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "server"))

from heavylifter.config import Settings  # noqa: E402
from heavylifter.main import create_app  # noqa: E402

with tempfile.TemporaryDirectory() as work:
    spec = create_app(Settings(api_keys="export", work_dir=Path(work))).openapi()

target = REPO / "contract" / "openapi.json"
target.write_text(json.dumps(spec, indent=2, sort_keys=False) + "\n", encoding="utf-8")
print(f"wrote {target}")
