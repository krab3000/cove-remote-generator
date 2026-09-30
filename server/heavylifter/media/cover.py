from __future__ import annotations

from ..models import CoverSpec
from . import MediaContext, MediaError, commit_output, remove_quietly
from .runner import run_process
from .timing import cover_seek, fixed, frame_decode_timeout


def build_cover_args(input_args: list[str], source: str, seek: float, video_filter: str | None, output: str) -> list[str]:
    return [
        *input_args, "-v", "error", "-fflags", "+discardcorrupt", "-err_detect", "ignore_err", "-y",
        "-ss", fixed(seek, 2), "-i", source,
        *(["-vf", video_filter] if video_filter else []),
        "-vframes", "1", "-q:v", "2", "-f", "image2", output,
    ]


async def generate_cover(
    ctx: MediaContext, source: str, duration: float, source_size: int, spec: CoverSpec, output: str
) -> None:
    seek = cover_seek(duration, spec.seek_seconds)
    timeout = frame_decode_timeout(source_size)
    temp = output + ".tmp.jpg"
    try:
        result = await run_process([ctx.ffmpeg, *build_cover_args(ctx.input_args, source, seek, spec.filter, temp)], timeout)
        # A build without v360, or a layout the filter rejects, still gets the full frame.
        if not result.ok and spec.filter and spec.fallback_without_filter:
            result = await run_process([ctx.ffmpeg, *build_cover_args(ctx.input_args, source, seek, None, temp)], timeout)
        if not result.ok:
            raise MediaError(f"cover: {result.summary()}")
        commit_output(temp, output)
    finally:
        remove_quietly(temp)
