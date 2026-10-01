"""Fail when the component versions drift from VERSION (stdlib only).

The worker and protocol projects read VERSION directly at build time; the extension manifest and the
frontend package carry their own copy.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
expected = (REPO / "VERSION").read_text(encoding="utf-8").strip()

found = {
    "extension/backend/RemoteHeavylifter/extension.json":
        json.loads((REPO / "extension/backend/RemoteHeavylifter/extension.json").read_text(encoding="utf-8"))["version"],
    "extension/frontend/package.json":
        json.loads((REPO / "extension/frontend/package.json").read_text(encoding="utf-8"))["version"],
}

bad = {path: version for path, version in found.items() if version != expected}
for path, version in bad.items():
    print(f"{path}: {version} (VERSION says {expected})")
sys.exit(1 if bad else 0)
