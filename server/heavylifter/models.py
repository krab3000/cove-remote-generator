from __future__ import annotations

from datetime import datetime
from typing import Literal

from pydantic import BaseModel, Field

ArtifactKind = Literal["cover", "preview", "sprite", "vtt"]
ArtifactState = Literal["pending", "running", "succeeded", "failed"]
TaskState = Literal["queued", "running", "succeeded", "partial", "failed", "cancelled"]
TERMINAL_STATES: frozenset[str] = frozenset({"succeeded", "partial", "failed", "cancelled"})


class CoverSpec(BaseModel):
    seek_seconds: float | None = None
    filter: str | None = None
    fallback_without_filter: bool = True


class PreviewSpec(BaseModel):
    segments: int = Field(default=12, ge=1, le=100)
    segment_duration: float = Field(default=0.75, ge=0.1, le=30)
    exclude_start: str = "0"
    exclude_end: str = "0"
    preset: str = "slow"
    audio: bool = False
    crf: int = Field(default=21, ge=0, le=51)
    width: int = Field(default=640, ge=16, le=7680)
    scale_filter: str | None = None


class SpriteSpec(BaseModel):
    max_frames: int = Field(default=81, ge=1, le=400)
    frame_width: int = Field(default=160, ge=16, le=1920)
    pre_filter: str | None = None
    sprite_filename: str = Field(min_length=1, max_length=255, pattern=r"^[^/\\]+$")


class TaskRequest(BaseModel):
    client_task_id: str = Field(min_length=1, max_length=200, pattern=r"^[A-Za-z0-9._:\-]+$")
    video_id: int
    source_path: str = Field(min_length=1)
    duration: float = 0
    cover: CoverSpec | None = None
    preview: PreviewSpec | None = None
    sprite: SpriteSpec | None = None


class ArtifactInfo(BaseModel):
    status: ArtifactState = "pending"
    size: int | None = None
    sha256: str | None = None
    error: str | None = None


class TaskStatus(BaseModel):
    task_id: str
    client_task_id: str
    video_id: int
    status: TaskState
    progress: float = 0
    error: str | None = None
    error_code: str | None = None
    artifacts: dict[str, ArtifactInfo] = Field(default_factory=dict)
    created_at: datetime
    started_at: datetime | None = None
    finished_at: datetime | None = None


class InfoResponse(BaseModel):
    api_version: int
    server_version: str
    ffmpeg_version: str | None
    encoder: str
    capacity: int
    running: int
    queued: int
    max_queue: int
    disk_free_bytes: int
    media_roots: list[str]


class PathCheckRequest(BaseModel):
    paths: list[str] = Field(max_length=200)


class PathCheckResult(BaseModel):
    path: str
    allowed: bool
    exists: bool
    readable: bool


class ErrorResponse(BaseModel):
    code: str
    message: str
