from __future__ import annotations

import pytest

from heavylifter.media.cover import build_cover_args
from heavylifter.media.encoders import video_encode_args
from heavylifter.media.preview import build_single_args, build_spliced_args, spliced_filter
from heavylifter.media.sprite import build_batch_args, frame_path
from heavylifter.media.timing import (
    build_vtt,
    fill_gaps,
    fixed,
    normalize_preset,
    parse_exclusion,
    plan_batches,
    preview_plan,
    sprite_plan,
    vtt_time,
)
from heavylifter.models import PreviewSpec


def test_fixed_formats_like_dotnet(parity):
    for case in parity["fixed"]:
        assert fixed(case["value"], case["digits"]) == case["expected"], case


def test_preview_plans(parity):
    for case in parity["previewPlans"]:
        plan = preview_plan(case["duration"], case["segments"], case["segmentDuration"],
                            case["excludeStart"], case["excludeEnd"], case["audio"])
        if case["mode"] is None:
            assert plan is None, case["name"]
            continue
        assert plan is not None, case["name"]
        assert plan.mode == case["mode"], case["name"]
        assert plan.usable_start == pytest.approx(case["usableStart"]), case["name"]
        assert plan.usable_duration == pytest.approx(case["usableDuration"]), case["name"]
        assert list(plan.seek_times) == pytest.approx(case["seeks"], abs=1e-6), case["name"]


def test_sprite_plans(parity):
    for case in parity["spritePlans"]:
        plan = sprite_plan(case["duration"])
        assert (plan.frame_count, plan.cols, plan.rows) == (case["frameCount"], case["cols"], case["rows"])
        assert plan.interval == pytest.approx(case["interval"], abs=1e-6)
        first = list(plan.timestamps[: len(case["firstTimestamps"])])
        assert first == pytest.approx(case["firstTimestamps"], abs=1e-6)
        assert plan.timestamps[-1] == pytest.approx(case["lastTimestamp"], abs=1e-6)


def test_vtt(parity):
    v = parity["vtt"]
    text = build_vtt(v["spriteFilename"], v["frameCount"], v["cols"], v["interval"], v["frameWidth"], v["frameHeight"], v["duration"])
    assert text == v["expected"]
    for case in parity["vttTimes"]:
        assert vtt_time(case["seconds"]) == case["expected"]


def test_gap_fill(parity):
    for case in parity["gapFill"]:
        assert fill_gaps(case["present"]) == case["expected"], case


def test_cover_args(parity):
    c = parity["coverArgs"]
    assert build_cover_args([], c["source"], c["seek"], c["filter"], c["output"]) == c["expected"]
    with_filter = build_cover_args(["-hwaccel", "auto"], "/s.mp4", 1, "v360=input=e", "/o.jpg")
    assert with_filter[:2] == ["-hwaccel", "auto"]
    assert with_filter[with_filter.index("-vf") + 1] == "v360=input=e"


def test_spliced_filter(parity):
    s = parity["splicedFilter"]
    assert spliced_filter(s["segments"], s["scale"]) == s["expected"]


def test_spliced_args_shape():
    plan = preview_plan(30, 12, 0.75, "0", "0", False)
    args = build_spliced_args([], "libx264", "/m/a.mp4", plan, PreviewSpec(preset="slow"), "/o/p.mp4")
    assert args[:3] == ["-v", "error", "-y"]
    assert args.count("-i") == 12
    assert args[3:9] == ["-ss", "1.25", "-t", "0.75", "-i", "/m/a.mp4"]
    graph = args[args.index("-filter_complex") + 1]
    assert graph.endswith("[spliced]null,format=yuv420p[preview]")
    tail = args[args.index("-map"):]
    assert tail == ["-map", "[preview]", "-c:v", "libx264", "-preset", "slow", "-crf", "21", "-pix_fmt", "yuv420p",
                    "-profile:v", "high", "-level", "4.2", "-an", "/o/p.mp4"]


def test_single_args_trims_only_when_excluded():
    spec = PreviewSpec()
    plain = preview_plan(5, 12, 0.75, "0", "0", False)
    args = build_single_args([], "libx264", "/m/a.mp4", 5, plain, spec, "/o/p.mp4")
    assert "-ss" not in args and "-t" not in args
    trimmed = preview_plan(5, 12, 0.75, "1", "1", False)
    args = build_single_args([], "h264_vaapi", "/m/a.mp4", 5, trimmed, spec, "/o/p.mp4")
    assert args[args.index("-ss") + 1] == "1.00" and args[args.index("-t") + 1] == "3.00"
    assert args[args.index("-vf") + 1] == "scale=640:-2,format=nv12,hwupload"
    assert "-vaapi_device" in args


def test_batch_args():
    args = build_batch_args("/m/a.mp4", "/tmp/f", (1.0, 3.0), 0, 2, 160, None)
    assert args == [
        "-v", "error", "-y",
        "-threads", "1", "-ss", "1.000", "-i", "/m/a.mp4",
        "-threads", "1", "-ss", "3.000", "-i", "/m/a.mp4",
        "-map", "0:v:0", "-an", "-frames:v", "1", "-vf", "scale=160:-2", "-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p", frame_path("/tmp/f", 0),
        "-map", "1:v:0", "-an", "-frames:v", "1", "-vf", "scale=160:-2", "-threads", "1", "-q:v", "3", "-pix_fmt", "yuvj420p", frame_path("/tmp/f", 1),
    ]
    vr = build_batch_args("/m/a.mp4", "/tmp/f", (1.0,), 0, 1, 160, "v360=input=he")
    assert vr[vr.index("-vf") + 1] == "v360=input=he,scale=160:-2"


def test_batch_planning_respects_command_line_budget():
    assert plan_batches(100, 81) == [(0, 24), (24, 24), (48, 24), (72, 9)]
    assert plan_batches(2000, 30) == [(i, min(12, 30 - i)) for i in range(0, 30, 12)]


def test_encoders_and_presets():
    assert video_encode_args("h264_nvenc", 21, "slow") == ["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", "21", "-b:v", "0"]
    assert video_encode_args("h264_videotoolbox", 21, "slow")[-1] == "44"
    assert normalize_preset(" SLOW ") == "slow"
    assert normalize_preset("superfast") == "fast"
    assert parse_exclusion("50%", 30) == 15
    assert parse_exclusion("999", 30) == 30
