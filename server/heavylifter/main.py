from __future__ import annotations

import asyncio
import contextlib
import logging
from collections.abc import AsyncIterator

from fastapi import FastAPI

from . import __version__
from .api.routes import public, v1
from .config import Settings
from .errors import install_error_handlers
from .media import MediaContext
from .media.pipeline import FfmpegExecutor, ffmpeg_version
from .tasks.manager import Executor, TaskManager

log = logging.getLogger("heavylifter")


def create_app(settings: Settings | None = None, executor: Executor | None = None) -> FastAPI:
    settings = settings or Settings()
    if not settings.api_key_list and not settings.allow_no_auth:
        raise RuntimeError("HL_API_KEYS is empty. Set at least one API key (or HL_ALLOW_NO_AUTH=true for local testing).")

    ctx = MediaContext(settings.ffmpeg, settings.ffprobe, settings.ffmpeg_input_arg_list, settings.h264_encoder)

    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI) -> AsyncIterator[None]:
        if not settings.media_root_list:
            log.warning("HL_MEDIA_ROOTS is empty: every task will be rejected")
        if not settings.api_key_list:
            log.warning("running WITHOUT authentication (HL_ALLOW_NO_AUTH=true)")
        app.state.ffmpeg_version = await ffmpeg_version(ctx)
        if app.state.ffmpeg_version is None:
            log.warning("ffmpeg not found or not runnable at %r", settings.ffmpeg)
        manager = TaskManager(settings, executor or FfmpegExecutor(ctx))
        await manager.start()
        app.state.manager = manager
        janitor = asyncio.create_task(_janitor(manager, settings.janitor_interval_seconds))
        try:
            yield
        finally:
            janitor.cancel()
            await asyncio.gather(janitor, return_exceptions=True)
            await manager.shutdown()

    app = FastAPI(title="remote-heavylifter", version=__version__, lifespan=lifespan)
    app.state.settings = settings
    install_error_handlers(app)
    app.include_router(public)
    app.include_router(v1)
    return app


async def _janitor(manager: TaskManager, interval: float) -> None:
    while True:
        await asyncio.sleep(interval)
        try:
            removed = await manager.cleanup()
            if removed:
                log.info("janitor removed %d expired task(s)", removed)
        except Exception:  # noqa: BLE001
            log.exception("janitor run failed")


def run() -> None:
    import uvicorn

    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")
    settings = Settings()
    uvicorn.run(create_app(settings), host=settings.host, port=settings.port, log_level="info")


if __name__ == "__main__":
    run()
