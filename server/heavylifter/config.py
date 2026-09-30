from __future__ import annotations

import os
import re
from functools import cached_property
from pathlib import Path

from pydantic_settings import BaseSettings, SettingsConfigDict


def _split(value: str) -> list[str]:
    return [part.strip() for part in re.split(r"[,;\n]", value or "") if part.strip()]


class Settings(BaseSettings):
    """Server configuration, read from HL_* environment variables (or a .env file)."""

    model_config = SettingsConfigDict(env_prefix="HL_", env_file=".env", extra="ignore")

    api_keys: str = ""
    allow_no_auth: bool = False
    host: str = "0.0.0.0"
    port: int = 8750
    work_dir: Path = Path("./work")
    media_roots: str = ""
    max_concurrency: int = max(1, (os.cpu_count() or 4) // 4)
    max_queue: int = 2000
    artifact_ttl_hours: float = 6.0
    ffmpeg: str = "ffmpeg"
    ffprobe: str = "ffprobe"
    ffmpeg_input_args: str = ""
    h264_encoder: str = "libx264"
    min_free_gb: float = 2.0
    janitor_interval_seconds: float = 300.0

    @cached_property
    def api_key_list(self) -> list[str]:
        return _split(self.api_keys)

    @cached_property
    def media_root_list(self) -> list[str]:
        return _split(self.media_roots)

    @cached_property
    def ffmpeg_input_arg_list(self) -> list[str]:
        return self.ffmpeg_input_args.split() if self.ffmpeg_input_args.strip() else []
