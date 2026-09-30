from __future__ import annotations

import asyncio
import hashlib
import os
from collections.abc import Awaitable, Callable
from typing import TYPE_CHECKING

from . import MediaContext, MediaError
from .cover import generate_cover
from .preview import generate_preview
from .runner import run_process
from .sprite import generate_sprite

if TYPE_CHECKING:
    from ..tasks.task import Task

# Rough relative cost of each artifact, used only to shape the progress bar.
WEIGHTS = {"cover": 1.0, "preview": 4.0, "sprite": 3.0}

Persist = Callable[[], Awaitable[None]]


def sha256_file(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


async def probe_duration(ctx: MediaContext, source: str) -> float:
    result = await run_process(
        [ctx.ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", source], 30
    )
    try:
        return float(result.stdout.strip()) if result.ok else 0.0
    except ValueError:
        return 0.0


async def ffmpeg_version(ctx: MediaContext) -> str | None:
    try:
        result = await run_process([ctx.ffmpeg, "-hide_banner", "-version"], 10)
    except OSError:
        return None
    first = (result.stdout or "").splitlines()[:1]
    return first[0].strip() if result.ok and first else None


class FfmpegExecutor:
    """Runs the requested artifacts of one task, one after another, recording each outcome on the task."""

    def __init__(self, ctx: MediaContext) -> None:
        self.ctx = ctx

    async def execute(self, task: Task, persist: Persist) -> None:
        request = task.request
        source = request.source_path
        if not os.path.isfile(source):
            raise MediaError("source file not found on this server (check the path mapping)", "source_not_found")
        size = os.path.getsize(source)
        duration = request.duration if request.duration > 0 else await probe_duration(self.ctx, source)

        out_dir = task.output_dir
        os.makedirs(out_dir, exist_ok=True)
        scratch = os.path.join(task.dir, "scratch")
        os.makedirs(scratch, exist_ok=True)

        steps = [k for k in ("cover", "preview", "sprite") if getattr(request, k) is not None]
        total = sum(WEIGHTS[k] for k in steps) or 1.0
        done = 0.0
        for kind in steps:
            names = ["sprite", "vtt"] if kind == "sprite" else [kind]
            for name in names:
                task.artifacts[name].status = "running"
            await persist()
            try:
                if kind == "cover":
                    await generate_cover(self.ctx, source, duration, size, request.cover, task.artifact_path("cover"))  # type: ignore[arg-type]
                elif kind == "preview":
                    await generate_preview(self.ctx, source, duration, request.preview, scratch, task.artifact_path("preview"))  # type: ignore[arg-type]
                else:
                    await generate_sprite(
                        self.ctx, source, duration, request.sprite, scratch,  # type: ignore[arg-type]
                        task.artifact_path("sprite"), task.artifact_path("vtt"),
                    )
                for name in names:
                    path = task.artifact_path(name)
                    info = task.artifacts[name]
                    info.size = os.path.getsize(path)
                    info.sha256 = await asyncio.to_thread(sha256_file, path)
                    info.status = "succeeded"
            except MediaError as ex:
                for name in names:
                    task.artifacts[name].status = "failed"
                    task.artifacts[name].error = str(ex)
            done += WEIGHTS[kind]
            task.progress = round(done / total, 4)
            await persist()
