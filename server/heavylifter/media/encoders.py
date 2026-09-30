"""H.264 encoder argument mapping, ported from Cove's FfmpegHwAccel (VideoEncodeArgs / InputArgsForEncoder)."""

from __future__ import annotations

SOFTWARE_ENCODER = "libx264"
KNOWN_ENCODERS = {"libx264", "h264_nvenc", "h264_qsv", "h264_amf", "h264_vaapi", "h264_videotoolbox"}


def video_encode_args(encoder: str, quality: int, software_preset: str) -> list[str]:
    q = str(quality)
    if encoder == "h264_nvenc":
        return ["-c:v", "h264_nvenc", "-rc", "vbr", "-cq", q, "-b:v", "0"]
    if encoder == "h264_qsv":
        return ["-c:v", "h264_qsv", "-global_quality", q]
    if encoder == "h264_amf":
        return ["-c:v", "h264_amf", "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q]
    if encoder == "h264_vaapi":
        return ["-c:v", "h264_vaapi", "-rc_mode", "CQP", "-qp", q]
    if encoder == "h264_videotoolbox":
        return ["-c:v", "h264_videotoolbox", "-q:v", str(min(max(65 - quality, 1), 100))]
    return ["-c:v", "libx264", "-preset", software_preset, "-crf", q, "-pix_fmt", "yuv420p"]


def input_args_for_encoder(encoder: str) -> list[str]:
    return ["-vaapi_device", "/dev/dri/renderD128"] if encoder.endswith("_vaapi") else []


def upload_chain(encoder: str) -> str:
    return ",format=nv12,hwupload" if encoder == "h264_vaapi" else ",format=yuv420p"
