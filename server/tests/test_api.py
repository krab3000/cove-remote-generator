from __future__ import annotations

import asyncio
import json
import os
import time
from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient

from heavylifter.config import Settings
from heavylifter.main import create_app
from heavylifter.media import MediaError
from heavylifter.models import TaskRequest, TaskStatus
from heavylifter.pathsafety import is_allowed
from heavylifter.tasks.manager import TaskManager

KEY = "test-key"
AUTH = {"Authorization": f"Bearer {KEY}"}


class FakeExecutor:
    """Writes tiny artifacts; can be told to block or to fail."""

    def __init__(self) -> None:
        self.gate: asyncio.Event | None = None
        self.fail_preview = False
        self.fail_all: MediaError | None = None
        self.started = 0

    async def execute(self, task, persist):
        self.started += 1
        if self.gate is not None:
            await self.gate.wait()
        if self.fail_all:
            raise self.fail_all
        os.makedirs(task.output_dir, exist_ok=True)
        for name, info in task.artifacts.items():
            if name == "preview" and self.fail_preview:
                info.status, info.error = "failed", "preview: boom"
                continue
            path = task.artifact_path(name)
            with open(path, "wb") as handle:
                handle.write(name.encode() * 10)
            info.status, info.size, info.sha256 = "succeeded", os.path.getsize(path), "abc"
        task.progress = 1.0
        await persist()


@pytest.fixture
def media(tmp_path):
    root = tmp_path / "media"
    root.mkdir()
    (root / "a.mp4").write_bytes(b"x")
    return root


@pytest.fixture
def settings(tmp_path, media):
    return Settings(api_keys=KEY, work_dir=tmp_path / "work", media_roots=str(media), max_concurrency=2,
                    min_free_gb=0, janitor_interval_seconds=3600, ffmpeg="ffmpeg-not-installed")


def body(media, client_id="c1", **extra):
    data = {"client_task_id": client_id, "video_id": 7, "source_path": str(media / "a.mp4"), "duration": 10,
            "cover": {}, "sprite": {"sprite_filename": "7_sprite.jpg"}}
    data.update(extra)
    return data


def wait_status(client, task_id, *states, timeout=5.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = client.get(f"/v1/tasks/{task_id}", headers=AUTH).json()
        if status["status"] in states:
            return status
        time.sleep(0.02)
    raise AssertionError(f"task {task_id} never reached {states}: {status}")


def test_auth(settings):
    with TestClient(create_app(settings, FakeExecutor())) as client:
        assert client.get("/healthz").json() == {"status": "ok"}
        assert client.get("/v1/info").status_code == 401
        assert client.get("/v1/info", headers={"Authorization": "Bearer nope"}).status_code == 403
        info = client.get("/v1/info", headers=AUTH).json()
        assert info["api_version"] == 1 and info["capacity"] == 2 and info["ffmpeg_version"] is None


def test_refuses_to_start_without_keys(settings):
    with pytest.raises(RuntimeError):
        create_app(settings.model_copy(update={"api_keys": ""}), FakeExecutor())


def test_submit_poll_download_delete(settings, media):
    with TestClient(create_app(settings, FakeExecutor())) as client:
        response = client.post("/v1/tasks", json=body(media), headers=AUTH)
        assert response.status_code == 202, response.text
        task_id = response.json()["task_id"]
        status = wait_status(client, task_id, "succeeded")
        assert set(status["artifacts"]) == {"cover", "sprite", "vtt"}

        artifact = client.get(f"/v1/tasks/{task_id}/artifacts/vtt", headers=AUTH)
        assert artifact.status_code == 200 and artifact.content == b"vtt" * 10
        assert artifact.headers["content-type"].startswith("text/vtt")
        assert client.get(f"/v1/tasks/{task_id}/artifacts/preview", headers=AUTH).status_code == 409

        assert client.delete(f"/v1/tasks/{task_id}", headers=AUTH).status_code == 204
        assert client.get(f"/v1/tasks/{task_id}", headers=AUTH).status_code == 404
        assert client.delete(f"/v1/tasks/{task_id}", headers=AUTH).status_code == 404


def test_idempotent_submit(settings, media):
    with TestClient(create_app(settings, FakeExecutor())) as client:
        first = client.post("/v1/tasks", json=body(media), headers=AUTH)
        again = client.post("/v1/tasks", json=body(media), headers=AUTH)
        assert first.status_code == 202 and again.status_code == 200
        assert first.json()["task_id"] == again.json()["task_id"]


def test_partial_and_failed(settings, media):
    executor = FakeExecutor()
    executor.fail_preview = True
    with TestClient(create_app(settings, executor)) as client:
        task_id = client.post("/v1/tasks", json=body(media, preview={}), headers=AUTH).json()["task_id"]
        status = wait_status(client, task_id, "partial")
        assert status["artifacts"]["preview"]["error"] == "preview: boom"

        executor.fail_all = MediaError("source file not found on this server", "source_not_found")
        task_id = client.post("/v1/tasks", json=body(media, "c2"), headers=AUTH).json()["task_id"]
        status = wait_status(client, task_id, "failed")
        assert status["error_code"] == "source_not_found"


def test_rejections(settings, media, tmp_path):
    outside = tmp_path / "elsewhere.mp4"
    outside.write_bytes(b"x")
    with TestClient(create_app(settings, FakeExecutor())) as client:
        r = client.post("/v1/tasks", json=body(media, source_path=str(outside)), headers=AUTH)
        assert r.status_code == 422 and r.json()["code"] == "path_not_allowed"
        traversal = str(media / ".." / "elsewhere.mp4")
        assert client.post("/v1/tasks", json=body(media, source_path=traversal), headers=AUTH).status_code == 422
        r = client.post("/v1/tasks", json=body(media, cover=None, sprite=None), headers=AUTH)
        assert r.status_code == 400
        r = client.post("/v1/tasks", json=body(media, client_task_id="bad id!"), headers=AUTH)
        assert r.status_code == 400 and r.json()["code"] == "invalid_request"


def test_queue_limit_and_concurrency(settings, media):
    executor = FakeExecutor()
    executor.gate = asyncio.Event()
    small = settings.model_copy(update={"max_queue": 2, "max_concurrency": 1})
    with TestClient(create_app(small, executor)) as client:
        ids = [client.post("/v1/tasks", json=body(media, f"q{i}"), headers=AUTH).json()["task_id"] for i in range(3)]
        wait_status(client, ids[0], "running")
        assert client.get("/v1/info", headers=AUTH).json()["running"] == 1
        r = client.post("/v1/tasks", json=body(media, "q9"), headers=AUTH)
        assert r.status_code == 429
        # Cancelling a running task kills it and frees the slot.
        assert client.delete(f"/v1/tasks/{ids[0]}", headers=AUTH).status_code == 204
        wait_status(client, ids[1], "running")
        client.portal.call(executor.gate.set)
        for task_id in ids[1:]:
            wait_status(client, task_id, "succeeded")


def test_paths_check(settings, media):
    with TestClient(create_app(settings, FakeExecutor())) as client:
        result = client.post("/v1/paths/check", json={"paths": [str(media / "a.mp4"), str(media / "missing.mp4"), "/etc/passwd"]},
                             headers=AUTH).json()
        assert [(r["allowed"], r["exists"]) for r in result] == [(True, True), (True, False), (False, False)]


def test_restart_marks_unfinished_tasks(settings, media):
    app = create_app(settings, FakeExecutor())
    with TestClient(app) as client:
        done = client.post("/v1/tasks", json=body(media, "done"), headers=AUTH).json()["task_id"]
        wait_status(client, done, "succeeded")
    # Fake an interrupted task on disk.
    manager_dir = settings.work_dir / "tasks"
    interrupted = manager_dir / "deadbeef"
    interrupted.mkdir()
    request = TaskRequest.model_validate(body(media, "lost"))
    state = TaskStatus(task_id="deadbeef", client_task_id="lost", video_id=7, status="running",
                       created_at=datetime.now(UTC))
    (interrupted / "task.json").write_text(json.dumps({"request": request.model_dump(mode="json"),
                                                        "state": state.model_dump(mode="json")}))
    with TestClient(create_app(settings, FakeExecutor())) as client:
        assert client.get(f"/v1/tasks/{done}", headers=AUTH).json()["status"] == "succeeded"
        lost = client.get("/v1/tasks/deadbeef", headers=AUTH).json()
        assert lost["status"] == "failed" and lost["error_code"] == "server_restarted"
        again = client.post("/v1/tasks", json=body(media, "lost"), headers=AUTH)
        assert again.json()["task_id"] == "deadbeef"


def test_janitor_removes_expired(settings, media):
    now = [datetime.now(UTC)]

    async def scenario():
        manager = TaskManager(settings, FakeExecutor(), clock=lambda: now[0])
        await manager.start()
        task, _ = manager.submit(TaskRequest.model_validate(body(media)))
        for _ in range(100):
            if task.status == "succeeded":
                break
            await asyncio.sleep(0.01)
        assert await manager.cleanup() == 0
        now[0] += timedelta(hours=settings.artifact_ttl_hours + 1)
        assert await manager.cleanup() == 1
        assert manager.get(task.task_id) is None and not os.path.exists(task.dir)

    asyncio.run(scenario())


def test_path_safety(tmp_path):
    root = tmp_path / "root"
    root.mkdir()
    assert is_allowed(str(root / "x" / "y.mp4"), [str(root)])
    assert not is_allowed(str(tmp_path / "rootless.mp4"), [str(root)])
    assert not is_allowed(str(root / ".." / "z.mp4"), [str(root)])
    assert not is_allowed(str(root / "x.mp4"), [])
