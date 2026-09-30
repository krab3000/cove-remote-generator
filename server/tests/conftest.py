from __future__ import annotations

import json
import os
import shutil
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
CONTRACT = REPO / "contract"


@pytest.fixture(scope="session")
def parity() -> dict:
    return json.loads((CONTRACT / "parity" / "cove-parity.json").read_text(encoding="utf-8"))


@pytest.fixture(scope="session")
def fixtures_dir() -> Path:
    return CONTRACT / "fixtures"


def ffmpeg_available() -> bool:
    return shutil.which("ffmpeg") is not None and shutil.which("ffprobe") is not None


requires_ffmpeg = pytest.mark.skipif(
    not ffmpeg_available() or os.environ.get("HL_SKIP_FFMPEG_TESTS") == "1",
    reason="ffmpeg/ffprobe not on PATH",
)
