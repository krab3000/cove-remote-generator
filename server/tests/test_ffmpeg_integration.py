from __future__ import annotations

import asyncio
import os
import subprocess

import pytest
from PIL import Image

from heavylifter.media import MediaContext, MediaError
from heavylifter.media.cover import generate_cover
from heavylifter.media.pipeline import probe_duration
from heavylifter.media.preview import generate_preview
from heavylifter.media.sprite import generate_sprite
from heavylifter.models import CoverSpec, PreviewSpec, SpriteSpec

from .conftest import requires_ffmpeg

pytestmark = [pytest.mark.ffmpeg, requires_ffmpeg]
CTX = MediaContext()


def make_clip(path, seconds: float, audio: bool = False) -> str:
    args = ["ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", f"testsrc=size=320x180:rate=25:duration={seconds}"]
    if audio:
        args += ["-f", "lavfi", "-i", f"sine=frequency=440:duration={seconds}", "-c:a", "aac", "-shortest"]
    args += ["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", str(path)]
    subprocess.run(args, check=True)
    return str(path)


def ffprobe_stream(path: str) -> tuple[int, int, float]:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height:format=duration",
         "-of", "default=nw=1:nk=1", path], check=True, capture_output=True, text=True).stdout.split()
    return int(out[0]), int(out[1]), float(out[2])


@pytest.fixture(scope="module")
def clip30(tmp_path_factory):
    return make_clip(tmp_path_factory.mktemp("clips") / "30s.mp4", 30)


def test_probe_and_cover(clip30, tmp_path):
    assert asyncio.run(probe_duration(CTX, clip30)) == pytest.approx(30, abs=0.1)
    out = str(tmp_path / "cover.jpg")
    asyncio.run(generate_cover(CTX, clip30, 30, os.path.getsize(clip30), CoverSpec(), out))
    with Image.open(out) as img:
        assert img.size == (320, 180)


def test_cover_filter_falls_back(clip30, tmp_path):
    out = str(tmp_path / "cover.jpg")
    spec = CoverSpec(filter="definitely_not_a_filter=1")
    asyncio.run(generate_cover(CTX, clip30, 30, 1, spec, out))
    assert os.path.getsize(out) > 0
    with pytest.raises(MediaError):
        asyncio.run(generate_cover(CTX, clip30, 30, 1, spec.model_copy(update={"fallback_without_filter": False}), out + "2"))


def test_spliced_preview(clip30, tmp_path):
    out = str(tmp_path / "preview.mp4")
    asyncio.run(generate_preview(CTX, clip30, 30, PreviewSpec(preset="ultrafast"), str(tmp_path), out))
    width, height, duration = ffprobe_stream(out)
    assert (width, height) == (640, 360)
    assert duration == pytest.approx(12 * 0.75, abs=0.3)


def test_short_preview_and_audio_chunks(tmp_path):
    short = make_clip(tmp_path / "2s.mp4", 2)
    out = str(tmp_path / "short.mp4")
    asyncio.run(generate_preview(CTX, short, 2, PreviewSpec(preset="ultrafast"), str(tmp_path), out))
    assert ffprobe_stream(out)[2] == pytest.approx(2, abs=0.3)

    loud = make_clip(tmp_path / "12s.mp4", 12, audio=True)
    out = str(tmp_path / "audio.mp4")
    spec = PreviewSpec(preset="ultrafast", audio=True, segments=4)
    asyncio.run(generate_preview(CTX, loud, 12, spec, str(tmp_path / "w"), out))
    assert ffprobe_stream(out)[2] == pytest.approx(4 * 0.75, abs=0.4)


def test_sprite_and_vtt(clip30, tmp_path):
    sprite, vtt = str(tmp_path / "s.jpg"), str(tmp_path / "t.vtt")
    asyncio.run(generate_sprite(CTX, clip30, 30, SpriteSpec(sprite_filename="9_sprite.jpg"), str(tmp_path), sprite, vtt))
    with Image.open(sprite) as img:
        assert img.size == (160 * 4, 90 * 4)
    text = open(vtt, encoding="utf-8").read()
    assert text.startswith("WEBVTT\n\n00:00:00.000 --> 00:00:02.000\n9_sprite.jpg#xywh=0,0,160,90\n")
    assert text.count("#xywh=") == 15


def test_tiny_clip_sprite(tmp_path):
    tiny = make_clip(tmp_path / "half.mp4", 0.5)
    sprite, vtt = str(tmp_path / "s.jpg"), str(tmp_path / "t.vtt")
    asyncio.run(generate_sprite(CTX, tiny, 0.5, SpriteSpec(sprite_filename="1_sprite.jpg"), str(tmp_path), sprite, vtt))
    with Image.open(sprite) as img:
        assert img.size == (160, 90)
