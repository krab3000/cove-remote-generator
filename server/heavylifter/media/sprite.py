from __future__ import annotations

import logging
import os

from PIL import Image

from ..models import SpriteSpec
from . import MediaContext, MediaError, commit_output, remove_quietly
from .runner import run_process
from .timing import build_vtt, fill_gaps, fixed, plan_batches, sprite_plan

log = logging.getLogger(__name__)


def frame_path(frame_dir: str, index: int) -> str:
    return os.path.join(frame_dir, f"frame_{index:04d}.jpg")


def per_frame_arg_length(source: str, frame_dir: str, count: int, scale_width: int, pre_filter: str | None) -> int:
    last = frame_path(frame_dir, max(0, count - 1))
    length = len(source) + 28 + len(last) + 72 + (22 if scale_width > 0 else 0)
    return length + (len(pre_filter) + 1 if pre_filter is not None else 0)


def build_batch_args(
    source: str,
    frame_dir: str,
    timestamps: list[float] | tuple[float, ...],
    start: int,
    count: int,
    scale_width: int,
    pre_filter: str | None,
) -> list[str]:
    args = ["-v", "error", "-y"]
    for offset in range(count):
        seconds = max(0.0, timestamps[start + offset])
        args += ["-threads", "1", "-ss", fixed(seconds, 3), "-i", source]
    filters = [f for f in (pre_filter, f"scale={scale_width}:-2" if scale_width > 0 else None) if f]
    for offset in range(count):
        args += ["-map", f"{offset}:v:0", "-an", "-frames:v", "1"]
        if filters:
            args += ["-vf", ",".join(filters)]
        args += ["-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p", frame_path(frame_dir, start + offset)]
    return args


async def extract_frames(
    ctx: MediaContext, source: str, timestamps: tuple[float, ...], scale_width: int, pre_filter: str | None, frame_dir: str
) -> list[str | None]:
    os.makedirs(frame_dir, exist_ok=True)
    length = per_frame_arg_length(source, frame_dir, len(timestamps), scale_width, pre_filter)
    frames: list[str | None] = [None] * len(timestamps)
    for start, count in plan_batches(length, len(timestamps)):
        args = build_batch_args(source, frame_dir, timestamps, start, count, scale_width, pre_filter)
        result = await run_process([ctx.ffmpeg, *args], 60 + 6 * count)
        if not result.ok:
            # Frames that did decode are still on disk and still count.
            log.debug("sprite batch %d-%d: %s", start, start + count - 1, result.summary(200))
        for index in range(start, start + count):
            path = frame_path(frame_dir, index)
            if os.path.exists(path) and os.path.getsize(path) > 0:
                frames[index] = path
    return frames


async def generate_sprite(
    ctx: MediaContext, source: str, duration: float, spec: SpriteSpec, work_dir: str, sprite_out: str, vtt_out: str
) -> None:
    if duration <= 0:
        raise MediaError("sprite: unknown duration")
    plan = sprite_plan(duration, spec.max_frames)
    frame_dir = os.path.join(work_dir, "frames")
    frames = await extract_frames(ctx, source, plan.timestamps, spec.frame_width, spec.pre_filter, frame_dir)

    sources = fill_gaps([f is not None for f in frames])
    if sources is None:
        decoded = sum(f is not None for f in frames)
        raise MediaError(f"sprite: only {decoded}/{plan.frame_count} frames could be decoded")

    images: dict[int, Image.Image] = {}
    try:
        for index in set(sources):
            with Image.open(frames[index]) as img:  # type: ignore[arg-type]
                images[index] = img.convert("RGB")
        fw, fh = images[sources[0]].size
        sheet = Image.new("RGB", (fw * plan.cols, fh * plan.rows))
        for i, src in enumerate(sources):
            sheet.paste(images[src], (fw * (i % plan.cols), fh * (i // plan.cols)))
        sprite_tmp = os.path.join(work_dir, "sprite.tmp.jpg")
        vtt_tmp = os.path.join(work_dir, "thumbs.tmp.vtt")
        try:
            sheet.save(sprite_tmp, format="JPEG", quality=75)
            with open(vtt_tmp, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(build_vtt(spec.sprite_filename, plan.frame_count, plan.cols, plan.interval, fw, fh, duration))
            commit_output(sprite_tmp, sprite_out)
            commit_output(vtt_tmp, vtt_out)
        finally:
            remove_quietly(sprite_tmp)
            remove_quietly(vtt_tmp)
    finally:
        for img in images.values():
            img.close()
