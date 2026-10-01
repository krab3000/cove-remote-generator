# Remote Heavylifter worker: .NET runtime + BtbN's FFmpeg n9.0 GPL shared build in /opt/ffmpeg. The worker runs
# libav in-process from /opt/ffmpeg/lib (its FFmpeg.AutoGen 9.0 bindings need exactly these library versions) and
# keeps /opt/ffmpeg/bin/ffmpeg for HL_MEDIA_ENGINE=cli.
# Build from the repository root: docker build -f worker/Dockerfile .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY VERSION ./
COPY protocol/RemoteHeavylifter.Protocol/ protocol/RemoteHeavylifter.Protocol/
COPY worker/RemoteHeavylifter.Worker/ worker/RemoteHeavylifter.Worker/
RUN dotnet publish worker/RemoteHeavylifter.Worker -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG TARGETARCH
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends ca-certificates curl xz-utils libva2 libva-drm2 libvulkan1; \
    case "${TARGETARCH:-amd64}" in \
        amd64) FFMPEG_ASSET="ffmpeg-n9.0-latest-linux64-gpl-shared-9.0.tar.xz" ;; \
        arm64) FFMPEG_ASSET="ffmpeg-n9.0-latest-linuxarm64-gpl-shared-9.0.tar.xz" ;; \
        *) echo "Unsupported arch: ${TARGETARCH}"; exit 1 ;; \
    esac; \
    curl -fL --retry 5 --retry-all-errors --retry-delay 5 -o /tmp/ffmpeg.tar.xz \
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/${FFMPEG_ASSET}"; \
    mkdir -p /opt/ffmpeg; \
    tar -Jx --strip-components=1 -C /opt/ffmpeg -f /tmp/ffmpeg.tar.xz --wildcards '*/bin/ffmpeg' '*/bin/ffprobe' '*/lib/*.so*'; \
    rm -f /tmp/ffmpeg.tar.xz; \
    apt-get purge -y xz-utils; apt-get autoremove -y; rm -rf /var/lib/apt/lists/*; \
    LD_LIBRARY_PATH=/opt/ffmpeg/lib /opt/ffmpeg/bin/ffmpeg -hide_banner -version | head -n1

WORKDIR /app
COPY --from=build /out ./
RUN mkdir -p /var/lib/heavylifter && chown "$APP_UID" /var/lib/heavylifter

# The worker token is generated into HL_DATA_DIR on first start; keep the volume to keep the worker's identity.
ENV HL_DATA_DIR=/var/lib/heavylifter \
    LD_LIBRARY_PATH=/opt/ffmpeg/lib \
    HL_FFMPEG_LIBS=/opt/ffmpeg/lib \
    HL_FFMPEG=/opt/ffmpeg/bin/ffmpeg \
    HL_FFPROBE=/opt/ffmpeg/bin/ffprobe

USER $APP_UID
VOLUME ["/var/lib/heavylifter"]
EXPOSE 8750
ENTRYPOINT ["dotnet", "RemoteHeavylifter.Worker.dll"]
