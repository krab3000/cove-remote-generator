from __future__ import annotations

import logging
import os
from collections.abc import Callable

from ..models import PreviewSpec
from . import MediaContext, MediaError, commit_output, remove_quietly
from .encoders import SOFTWARE_ENCODER, input_args_for_encoder, upload_chain, video_encode_args
from .runner import run_process
from .timing import PreviewPlan, fixed, normalize_preset, preview_plan

log = logging.getLogger(__name__)

PROFILE_ARGS = ["-profile:v", "high", "-level", "4.2"]


def scale_filter(spec: PreviewSpec) -> str:
    return spec.scale_filter or f"scale={spec.width}:-2"


def build_single_args(
    input_args: list[str], encoder: str, source: str, duration: float, plan: PreviewPlan, spec: PreviewSpec, output: str
) -> list[str]:
    seek = ["-ss", fixed(plan.usable_start, 2)] if plan.usable_start > 0 else []
    limit = ["-t", fixed(plan.usable_duration, 2)] if plan.usable_duration < duration else []
    audio = [] if spec.audio else ["-an"]
    return [
        *input_args, "-v", "error", "-y", *input_args_for_encoder(encoder),
        *seek, "-i", source, *limit, "-max_muxing_queue_size", "1024",
        *video_encode_args(encoder, spec.crf, normalize_preset(spec.preset)),
        "-vf", scale_filter(spec) + upload_chain(encoder),
        *PROFILE_ARGS, *audio, output,
    ]


def spliced_filter(segment_count: int, scale: str) -> str:
    parts = [f"[{i}:v:0]{scale},setsar=1,setpts=PTS-STARTPTS[v{i}];" for i in range(segment_count)]
    parts.extend(f"[v{i}]" for i in range(segment_count))
    parts.append(f"concat=n={segment_count}:v=1:a=0[spliced];[spliced]null")
    return "".join(parts)


def build_spliced_args(
    input_args: list[str], encoder: str, source: str, plan: PreviewPlan, spec: PreviewSpec, output: str
) -> list[str]:
    inputs: list[str] = []
    for seek in plan.seek_times:
        inputs += ["-ss", fixed(seek, 2), "-t", fixed(plan.segment_duration, 2), "-i", source]
    graph = spliced_filter(plan.segment_count, scale_filter(spec)) + upload_chain(encoder) + "[preview]"
    return [
        *input_args, "-v", "error", "-y", *input_args_for_encoder(encoder), *inputs,
        "-max_muxing_queue_size", "1024", "-filter_complex", graph,
        "-map", "[preview]", *video_encode_args(encoder, spec.crf, normalize_preset(spec.preset)),
        *PROFILE_ARGS, "-an", output,
    ]


def build_chunk_args(
    input_args: list[str], encoder: str, source: str, seek: float, plan: PreviewPlan, spec: PreviewSpec, output: str
) -> list[str]:
    audio = [] if spec.audio else ["-an"]
    return [
        *input_args, "-v", "error", "-y", *input_args_for_encoder(encoder),
        "-ss", fixed(seek, 2), "-i", source, "-t", fixed(plan.segment_duration, 2),
        "-max_muxing_queue_size", "1024", *video_encode_args(encoder, spec.crf, normalize_preset(spec.preset)),
        "-vf", scale_filter(spec) + upload_chain(encoder), *PROFILE_ARGS, *audio, output,
    ]


def build_concat_args(list_path: str, output: str) -> list[str]:
    return ["-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", list_path, "-c:v", "copy", output]


async def _encode(ctx: MediaContext, build: Callable[[str], list[str]], output: str, timeout: float) -> None:
    """Hardware encode first when configured; any failure falls back to libx264 with a fresh command line."""
    if ctx.encoder != SOFTWARE_ENCODER:
        result = await run_process([ctx.ffmpeg, *build(ctx.encoder)], timeout)
        if result.ok and os.path.exists(output) and os.path.getsize(output) > 0:
            return
        log.debug("hardware encode (%s) failed for %s; falling back to libx264: %s",
                  ctx.encoder, os.path.basename(output), result.summary(200))
        remove_quietly(output)
    result = await run_process([ctx.ffmpeg, *build(SOFTWARE_ENCODER)], timeout)
    if not result.ok:
        raise MediaError(f"preview: {result.summary()}")


async def generate_preview(ctx: MediaContext, source: str, duration: float, spec: PreviewSpec, work_dir: str, output: str) -> None:
    if duration <= 0:
        raise MediaError("preview: unknown duration")
    plan = preview_plan(duration, spec.segments, spec.segment_duration, spec.exclude_start, spec.exclude_end, spec.audio)
    if plan is None:
        raise MediaError("preview: nothing left after start/end exclusions")

    temp = os.path.join(work_dir, "preview.tmp.mp4")
    try:
        if plan.mode == "single":
            await _encode(ctx, lambda enc: build_single_args(ctx.input_args, enc, source, duration, plan, spec, temp), temp, 300)
        elif plan.mode == "spliced":
            await _encode(ctx, lambda enc: build_spliced_args(ctx.input_args, enc, source, plan, spec, temp), temp, 300)
        else:
            await _chunked(ctx, source, plan, spec, work_dir, temp)
        commit_output(temp, output)
    finally:
        remove_quietly(temp)


async def _chunked(ctx: MediaContext, source: str, plan: PreviewPlan, spec: PreviewSpec, work_dir: str, output: str) -> None:
    chunk_dir = os.path.join(work_dir, "chunks")
    os.makedirs(chunk_dir, exist_ok=True)
    chunks: list[str] = []
    for i, seek in enumerate(plan.seek_times):
        chunk = os.path.join(chunk_dir, f"chunk_{i:03d}.mp4")
        chunks.append(chunk)
        try:
            await _encode(ctx, lambda enc, s=seek, c=chunk: build_chunk_args(ctx.input_args, enc, source, s, plan, spec, c), chunk, 60)
        except MediaError as ex:
            log.debug("preview chunk %d failed: %s", i, ex)

    valid = [c for c in chunks if os.path.exists(c) and os.path.getsize(c) > 0]
    if not valid:
        raise MediaError("preview: no usable chunks")
    list_path = os.path.join(chunk_dir, "concat.txt")
    with open(list_path, "w", encoding="utf-8") as handle:
        handle.write("\n".join(
            "file '" + os.path.abspath(c).replace("\\", "/").replace("'", "'\\''") + "'" for c in valid
        ))
    result = await run_process([ctx.ffmpeg, *build_concat_args(list_path, output)], 30)
    if not result.ok:
        raise MediaError(f"preview concat: {result.summary()}")
