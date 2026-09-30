# remote-heavylifter generation server: Python + BtbN static ffmpeg (the same builds Cove's images use).
FROM python:3.12-slim

ARG TARGETARCH
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl xz-utils libva2 libva-drm2 libvulkan1; \
    case "${TARGETARCH:-amd64}" in \
        amd64) FFMPEG_ASSET="ffmpeg-master-latest-linux64-gpl.tar.xz" ;; \
        arm64) FFMPEG_ASSET="ffmpeg-master-latest-linuxarm64-gpl.tar.xz" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}"; exit 1 ;; \
    esac; \
    curl -fL --retry 5 --retry-all-errors --retry-delay 5 -o /tmp/ffmpeg.tar.xz \
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FFMPEG_ASSET}"; \
    tar -Jx --strip-components=2 -C /usr/local/bin/ --wildcards '*/bin/ffmpeg' '*/bin/ffprobe' -f /tmp/ffmpeg.tar.xz; \
    rm -f /tmp/ffmpeg.tar.xz; \
    chmod +x /usr/local/bin/ffmpeg /usr/local/bin/ffprobe; \
    apt-get purge -y xz-utils; apt-get autoremove -y; rm -rf /var/lib/apt/lists/*; \
    ffmpeg -hide_banner -version | head -n1

WORKDIR /app
COPY pyproject.toml ./
COPY heavylifter ./heavylifter
RUN pip install --no-cache-dir . \
    && useradd --system --uid 10001 --create-home heavylifter \
    && mkdir -p /var/lib/heavylifter \
    && chown heavylifter /var/lib/heavylifter

ENV HL_WORK_DIR=/var/lib/heavylifter \
    HL_PORT=8750 \
    HL_FFMPEG=/usr/local/bin/ffmpeg \
    HL_FFPROBE=/usr/local/bin/ffprobe \
    PYTHONUNBUFFERED=1

USER heavylifter
VOLUME ["/var/lib/heavylifter"]
EXPOSE 8750
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS "http://127.0.0.1:${HL_PORT}/healthz" || exit 1

ENTRYPOINT ["heavylifter"]
