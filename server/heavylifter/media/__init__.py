from __future__ import annotations

import os
from dataclasses import dataclass, field


class MediaError(Exception):
    """A generation step failed. `code` is machine readable and travels to Cove as error_code."""

    def __init__(self, message: str, code: str = "generation_failed") -> None:
        super().__init__(message)
        self.code = code


@dataclass(frozen=True)
class MediaContext:
    ffmpeg: str = "ffmpeg"
    ffprobe: str = "ffprobe"
    input_args: list[str] = field(default_factory=list)
    encoder: str = "libx264"


def commit_output(temp_path: str, final_path: str) -> None:
    """Move a finished output into place; an empty or missing file is a failure (as in Cove)."""
    if not os.path.exists(temp_path) or os.path.getsize(temp_path) == 0:
        raise MediaError("ffmpeg produced no output")
    os.replace(temp_path, final_path)


def remove_quietly(path: str) -> None:
    try:
        os.remove(path)
    except OSError:
        pass
