from __future__ import annotations

from typing import Literal

from fastapi import APIRouter, Depends, Request, Response
from fastapi.responses import FileResponse

from .. import API_VERSION, __version__
from ..auth import require_api_key
from ..errors import ApiError
from ..models import ErrorResponse, InfoResponse, PathCheckRequest, PathCheckResult, TaskRequest, TaskStatus
from ..pathsafety import check_path
from ..tasks.manager import SubmitError, TaskManager
from ..tasks.task import ARTIFACT_MEDIA_TYPES

public = APIRouter()
v1 = APIRouter(prefix="/v1", dependencies=[Depends(require_api_key)])

ERRORS = {status: {"model": ErrorResponse} for status in (400, 401, 403, 404, 409, 422, 429, 507)}


def _manager(request: Request) -> TaskManager:
    return request.app.state.manager


@public.get("/healthz", tags=["health"])
async def healthz() -> dict[str, str]:
    return {"status": "ok"}


@v1.get("/info", response_model=InfoResponse, responses=ERRORS, tags=["info"])
async def info(request: Request) -> InfoResponse:
    manager = _manager(request)
    settings = request.app.state.settings
    return InfoResponse(
        api_version=API_VERSION,
        server_version=__version__,
        ffmpeg_version=request.app.state.ffmpeg_version,
        encoder=settings.h264_encoder,
        capacity=max(1, settings.max_concurrency),
        running=manager.running_count,
        queued=manager.queued_count,
        max_queue=settings.max_queue,
        disk_free_bytes=manager.disk_free_bytes(),
        media_roots=settings.media_root_list,
    )


@v1.post("/paths/check", response_model=list[PathCheckResult], responses=ERRORS, tags=["info"])
async def paths_check(body: PathCheckRequest, request: Request) -> list[PathCheckResult]:
    roots = request.app.state.settings.media_root_list
    results = []
    for path in body.paths:
        allowed, exists, readable = check_path(path, roots)
        results.append(PathCheckResult(path=path, allowed=allowed, exists=exists, readable=readable))
    return results


@v1.post("/tasks", response_model=TaskStatus, status_code=202, responses=ERRORS, tags=["tasks"])
async def submit_task(body: TaskRequest, request: Request, response: Response) -> TaskStatus:
    if body.cover is None and body.preview is None and body.sprite is None:
        raise ApiError(400, "invalid_request", "request at least one of cover, preview, sprite")
    try:
        task, created = _manager(request).submit(body)
    except SubmitError as ex:
        raise ApiError(ex.status, ex.code, str(ex)) from ex
    if not created:
        response.status_code = 200
    return task.to_status()


@v1.get("/tasks/{task_id}", response_model=TaskStatus, responses=ERRORS, tags=["tasks"])
async def get_task(task_id: str, request: Request) -> TaskStatus:
    task = _manager(request).get(task_id)
    if task is None:
        raise ApiError(404, "not_found", "unknown task")
    return task.to_status()


@v1.get(
    "/tasks/{task_id}/artifacts/{kind}",
    response_class=FileResponse,
    responses={**ERRORS, 200: {"content": {"application/octet-stream": {}}}},
    tags=["tasks"],
)
async def download_artifact(task_id: str, kind: Literal["cover", "preview", "sprite", "vtt"], request: Request) -> FileResponse:
    task = _manager(request).get(task_id)
    if task is None:
        raise ApiError(404, "not_found", "unknown task")
    artifact = task.artifacts.get(kind)
    if artifact is None or artifact.status != "succeeded":
        raise ApiError(409, "artifact_unavailable", f"artifact '{kind}' is not available")
    headers = {"ETag": f'"{artifact.sha256}"'} if artifact.sha256 else None
    return FileResponse(task.artifact_path(kind), media_type=ARTIFACT_MEDIA_TYPES[kind], headers=headers)


@v1.delete("/tasks/{task_id}", status_code=204, responses=ERRORS, tags=["tasks"])
async def delete_task(task_id: str, request: Request) -> Response:
    if not await _manager(request).delete(task_id):
        raise ApiError(404, "not_found", "unknown task")
    return Response(status_code=204)
