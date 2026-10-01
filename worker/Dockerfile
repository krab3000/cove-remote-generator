# Remote Heavylifter worker: .NET runtime + BtbN static ffmpeg (the same builds Cove's images use).
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
COPY --from=build /out ./
RUN mkdir -p /var/lib/heavylifter && chown "$APP_UID" /var/lib/heavylifter

# The worker token is generated into HL_DATA_DIR on first start; keep the volume to keep the worker's identity.
ENV HL_DATA_DIR=/var/lib/heavylifter \
    HL_FFMPEG=/usr/local/bin/ffmpeg \
    HL_FFPROBE=/usr/local/bin/ffprobe

USER $APP_UID
VOLUME ["/var/lib/heavylifter"]
EXPOSE 8750
ENTRYPOINT ["dotnet", "RemoteHeavylifter.Worker.dll"]
