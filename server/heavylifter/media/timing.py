"""Pure timing math ported one-for-one from Cove's ThumbnailService / VideoFrameBatchExtractor.

Source: yourcove/cove src/Cove.Api/Services/ThumbnailService.cs (commit f4cd955e).
Any change here must keep contract/parity/*.json passing.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from decimal import ROUND_HALF_UP, Decimal
from typing import Literal

DEFAULT_PREVIEW_SEGMENTS = 12
DEFAULT_PREVIEW_SEGMENT_DURATION = 0.75
FALLBACK_PREVIEW_PRESET = "fast"
_PRESETS = {"ultrafast", "veryfast", "fast", "medium", "slow", "slower", "veryslow"}

SPRITE_MIN_DECODED_RATIO = 0.9
SPRITE_BATCH_SIZE = 24
SPRITE_MAX_COMMAND_LINE = 24000


def fixed(value: float, digits: int) -> str:
    """Format like .NET's ToString("F<n>") with the invariant culture (round half away from zero)."""
    quantum = Decimal(1).scaleb(-digits)
    rounded = Decimal(value).quantize(quantum, rounding=ROUND_HALF_UP)
    if rounded == 0:
        rounded = abs(rounded)
    return f"{rounded:.{digits}f}"


def normalize_preset(preset: str | None) -> str:
    candidate = (preset or "").strip().lower()
    return candidate if candidate in _PRESETS else FALLBACK_PREVIEW_PRESET


def parse_exclusion(value: str | None, duration: float) -> float:
    if value is None or not value.strip() or duration <= 0:
        return 0.0
    trimmed = value.strip()
    if trimmed.endswith("%"):
        try:
            percent = float(trimmed[:-1].strip())
            return min(max(duration * (percent / 100.0), 0.0), duration)
        except ValueError:
            pass
    try:
        seconds = float(trimmed)
    except ValueError:
        return 0.0
    return min(max(seconds, 0.0), duration)


def cover_seek(duration: float, requested: float | None = None) -> float:
    seek = requested if requested is not None else duration * 0.2
    return 1.0 if seek <= 0 else seek


def frame_decode_timeout(size_bytes: int) -> float:
    gigabytes = size_bytes / float(1 << 30)
    return min(max(30.0 + 20.0 * gigabytes, 30.0), 150.0)


@dataclass(frozen=True)
class PreviewPlan:
    mode: Literal["single", "spliced", "chunks"]
    segment_count: int
    segment_duration: float
    usable_start: float
    usable_end: float
    usable_duration: float
    seek_times: tuple[float, ...]


def preview_plan(
    duration: float,
    segments: int,
    segment_duration: float,
    exclude_start: str | None,
    exclude_end: str | None,
    audio: bool,
) -> PreviewPlan | None:
    """None when there is nothing usable to encode (Cove fails the preview in that case)."""
    segment_count = min(max(DEFAULT_PREVIEW_SEGMENTS if segments <= 0 else segments, 1), 100)
    seg_duration = min(
        max(DEFAULT_PREVIEW_SEGMENT_DURATION if segment_duration <= 0 else segment_duration, 0.1), 30.0
    )
    ex_start = parse_exclusion(exclude_start, duration)
    ex_end = parse_exclusion(exclude_end, duration)
    usable_start = min(ex_start, max(0.0, duration - 0.1))
    usable_end = max(usable_start, duration - ex_end)
    usable = usable_end - usable_start
    if usable <= 0:
        return None

    if usable < seg_duration * segment_count:
        return PreviewPlan("single", segment_count, seg_duration, usable_start, usable_end, usable, ())

    interval = usable / segment_count
    seeks: list[float] = []
    for i in range(segment_count):
        seek = usable_start + interval * i + interval * 0.5
        if seek + seg_duration > usable_end:
            seek = usable_end - seg_duration
        if seek < usable_start:
            seek = usable_start
        seeks.append(seek)
    mode: Literal["spliced", "chunks"] = "chunks" if audio else "spliced"
    return PreviewPlan(mode, segment_count, seg_duration, usable_start, usable_end, usable, tuple(seeks))


@dataclass(frozen=True)
class SpritePlan:
    frame_count: int
    cols: int
    rows: int
    interval: float
    timestamps: tuple[float, ...]


def sprite_plan(duration: float, max_frames: int = 81) -> SpritePlan:
    frame_count = min(max_frames, max(1, int(duration / 2)))
    cols = math.ceil(math.sqrt(frame_count))
    rows = math.ceil(frame_count / cols)
    interval = duration / frame_count
    return SpritePlan(frame_count, cols, rows, interval, tuple(interval * (i + 0.5) for i in range(frame_count)))


def plan_batches(per_frame_arg_length: int, count: int, batch_size: int = SPRITE_BATCH_SIZE) -> list[tuple[int, int]]:
    affordable = max(1, SPRITE_MAX_COMMAND_LINE // max(1, per_frame_arg_length))
    effective = min(max(1, batch_size), affordable)
    return [(start, min(effective, count - start)) for start in range(0, count, effective)]


def fill_gaps(present: list[bool]) -> list[int] | None:
    """For each requested frame, the index of the decoded frame it shows; None when too few decoded.

    Mirrors SpriteFrameGapFiller: nearest decoded frame, the earlier one winning a tie.
    """
    requested = len(present)
    decoded = sum(present)
    if requested == 0 or decoded == 0 or decoded < math.ceil(requested * SPRITE_MIN_DECODED_RATIO):
        return None
    sources: list[int] = []
    for i in range(requested):
        if present[i]:
            sources.append(i)
            continue
        for distance in range(1, requested):
            before, after = i - distance, i + distance
            if before >= 0 and present[before]:
                sources.append(before)
                break
            if after < requested and present[after]:
                sources.append(after)
                break
    return sources


def vtt_time(seconds: float) -> str:
    # TimeSpan.FromSeconds truncates to 100ns ticks; the formatter then truncates to whole milliseconds.
    ticks = int(seconds * 10_000_000)
    total_ms = ticks // 10_000
    hours, rem = divmod(total_ms, 3_600_000)
    minutes, rem = divmod(rem, 60_000)
    secs, millis = divmod(rem, 1000)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}.{millis:03d}"


def build_vtt(
    sprite_filename: str,
    frame_count: int,
    cols: int,
    interval: float,
    frame_width: int,
    frame_height: int,
    duration: float,
) -> str:
    lines = ["WEBVTT", ""]
    for i in range(frame_count):
        start = i * interval
        end = min((i + 1) * interval, duration)
        x = (i % cols) * frame_width
        y = (i // cols) * frame_height
        lines.append(f"{vtt_time(start)} --> {vtt_time(end)}")
        lines.append(f"{sprite_filename}#xywh={x},{y},{frame_width},{frame_height}")
        lines.append("")
    return "\n".join(lines) + "\n"
