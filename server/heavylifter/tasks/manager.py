from __future__ import annotations

import asyncio
import logging
import os
import shutil
import time
import uuid
from collections.abc import Callable
from datetime import UTC, datetime, timedelta
from typing import Protocol

from ..config import Settings
from ..media import MediaError
from ..models import TERMINAL_STATES, TaskRequest
from ..pathsafety import is_allowed
from .task import Task

log = logging.getLogger(__name__)
TASK_FILE = "task.json"


class Executor(Protocol):
    async def execute(self, task: Task, persist: Callable) -> None: ...


class SubmitError(Exception):
    def __init__(self, status: int, code: str, message: str) -> None:
        super().__init__(message)
        self.status = status
        self.code = code


def _now() -> datetime:
    return datetime.now(UTC)


class TaskManager:
    def __init__(
        self,
        settings: Settings,
        executor: Executor,
        clock: Callable[[], datetime] = _now,
        disk_free: Callable[[str], int] | None = None,
    ) -> None:
        self.settings = settings
        self.executor = executor
        self.clock = clock
        self._disk_free = disk_free or (lambda path: shutil.disk_usage(path).free)
        self.tasks_dir = os.path.abspath(os.path.join(str(settings.work_dir), "tasks"))
        self.tasks: dict[str, Task] = {}
        self.by_client: dict[str, str] = {}
        self._runners: dict[str, asyncio.Task] = {}
        self._semaphore = asyncio.Semaphore(max(1, settings.max_concurrency))
        self._running = 0

    # ---- lifecycle -------------------------------------------------------------------------

    async def start(self) -> None:
        os.makedirs(self.tasks_dir, exist_ok=True)
        for name in os.listdir(self.tasks_dir):
            directory = os.path.join(self.tasks_dir, name)
            path = os.path.join(directory, TASK_FILE)
            if not os.path.isfile(path):
                continue
            try:
                with open(path, encoding="utf-8") as handle:
                    task = Task.from_json(handle.read(), directory)
            except Exception:  # noqa: BLE001 - a corrupt manifest is simply dropped
                log.warning("dropping unreadable task directory %s", directory)
                shutil.rmtree(directory, ignore_errors=True)
                continue
            if task.status not in TERMINAL_STATES:
                task.status = "failed"
                task.error = "server restarted"
                task.error_code = "server_restarted"
                task.finished_at = self.clock()
                self._persist_sync(task)
            self.tasks[task.task_id] = task
            self.by_client[task.request.client_task_id] = task.task_id
        log.info("loaded %d finished task(s) from %s", len(self.tasks), self.tasks_dir)

    async def shutdown(self) -> None:
        runners = list(self._runners.values())
        for runner in runners:
            runner.cancel()
        await asyncio.gather(*runners, return_exceptions=True)

    # ---- queries ---------------------------------------------------------------------------

    @property
    def running_count(self) -> int:
        return self._running

    @property
    def queued_count(self) -> int:
        return sum(1 for t in self.tasks.values() if t.status == "queued")

    def disk_free_bytes(self) -> int:
        try:
            return int(self._disk_free(self.tasks_dir))
        except OSError:
            return 0

    def get(self, task_id: str) -> Task | None:
        return self.tasks.get(task_id)

    # ---- commands --------------------------------------------------------------------------

    def submit(self, request: TaskRequest) -> tuple[Task, bool]:
        """Returns (task, created). The same client_task_id always maps to the same task."""
        existing = self.by_client.get(request.client_task_id)
        if existing and existing in self.tasks:
            return self.tasks[existing], False

        if not is_allowed(request.source_path, self.settings.media_root_list):
            raise SubmitError(422, "path_not_allowed", "source_path is outside the configured media roots")
        if self.queued_count >= self.settings.max_queue:
            raise SubmitError(429, "queue_full", "the task queue is full")
        min_free = int(self.settings.min_free_gb * (1 << 30))
        if min_free > 0 and self.disk_free_bytes() < min_free:
            raise SubmitError(507, "disk_full", "not enough free space in the work directory")

        task_id = uuid.uuid4().hex
        directory = os.path.join(self.tasks_dir, task_id)
        os.makedirs(directory, exist_ok=True)
        task = Task.create(task_id, request, directory, self.clock())
        self.tasks[task_id] = task
        self.by_client[request.client_task_id] = task_id
        self._persist_sync(task)
        self._runners[task_id] = asyncio.create_task(self._run(task), name=f"task-{task_id}")
        return task, True

    async def delete(self, task_id: str) -> bool:
        task = self.tasks.pop(task_id, None)
        runner = self._runners.pop(task_id, None)
        if runner is not None and not runner.done():
            runner.cancel()
            await asyncio.gather(runner, return_exceptions=True)
        if task is None:
            return False
        if self.by_client.get(task.request.client_task_id) == task_id:
            del self.by_client[task.request.client_task_id]
        await asyncio.to_thread(shutil.rmtree, task.dir, True)
        return True

    async def cleanup(self) -> int:
        """Remove finished tasks older than the TTL and orphaned directories. Returns how many were removed."""
        now = self.clock()
        cutoff = now - timedelta(hours=self.settings.artifact_ttl_hours)
        expired = [t.task_id for t in self.tasks.values()
                   if t.status in TERMINAL_STATES and (t.finished_at or t.created_at) < cutoff]
        for task_id in expired:
            await self.delete(task_id)

        removed = len(expired)
        known = {t.dir for t in self.tasks.values()}
        orphan_age = time.time() - 3600
        for name in os.listdir(self.tasks_dir):
            directory = os.path.join(self.tasks_dir, name)
            if directory in known or not os.path.isdir(directory):
                continue
            if os.path.getmtime(directory) < orphan_age:
                await asyncio.to_thread(shutil.rmtree, directory, True)
                removed += 1
        return removed

    # ---- execution -------------------------------------------------------------------------

    async def _run(self, task: Task) -> None:
        try:
            async with self._semaphore:
                self._running += 1
                try:
                    task.status = "running"
                    task.started_at = self.clock()
                    await self._persist(task)
                    try:
                        await self.executor.execute(task, lambda: self._persist(task))
                        task.finalize()
                    except MediaError as ex:
                        task.status, task.error, task.error_code = "failed", str(ex), ex.code
                    except asyncio.CancelledError:
                        task.status = "cancelled"
                        raise
                    except Exception as ex:  # noqa: BLE001 - reported to the client, never crashes the server
                        log.exception("task %s crashed", task.task_id)
                        task.status, task.error, task.error_code = "failed", f"internal error: {ex}", "internal_error"
                    task.finished_at = self.clock()
                    if self.tasks.get(task.task_id) is task:
                        await self._persist(task)
                finally:
                    self._running -= 1
        finally:
            if self._runners.get(task.task_id) is asyncio.current_task():
                self._runners.pop(task.task_id, None)

    async def _persist(self, task: Task) -> None:
        await asyncio.to_thread(self._persist_sync, task)

    def _persist_sync(self, task: Task) -> None:
        if not os.path.isdir(task.dir):
            return  # deleted meanwhile
        path = os.path.join(task.dir, TASK_FILE)
        temp = path + ".tmp"
        try:
            with open(temp, "w", encoding="utf-8") as handle:
                handle.write(task.to_json())
            os.replace(temp, path)
        except OSError:
            log.debug("could not persist task %s", task.task_id, exc_info=True)
