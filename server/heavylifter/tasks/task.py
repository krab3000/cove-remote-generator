from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from datetime import datetime

from ..models import ArtifactInfo, TaskRequest, TaskState, TaskStatus

ARTIFACT_FILES = {"cover": "cover.jpg", "preview": "preview.mp4", "sprite": "sprite.jpg", "vtt": "thumbs.vtt"}
ARTIFACT_MEDIA_TYPES = {"cover": "image/jpeg", "preview": "video/mp4", "sprite": "image/jpeg", "vtt": "text/vtt"}


def requested_artifacts(request: TaskRequest) -> list[str]:
    kinds: list[str] = []
    if request.cover is not None:
        kinds.append("cover")
    if request.preview is not None:
        kinds.append("preview")
    if request.sprite is not None:
        kinds += ["sprite", "vtt"]
    return kinds


@dataclass
class Task:
    task_id: str
    request: TaskRequest
    dir: str
    created_at: datetime
    status: TaskState = "queued"
    progress: float = 0.0
    error: str | None = None
    error_code: str | None = None
    started_at: datetime | None = None
    finished_at: datetime | None = None
    artifacts: dict[str, ArtifactInfo] = field(default_factory=dict)

    @classmethod
    def create(cls, task_id: str, request: TaskRequest, directory: str, now: datetime) -> Task:
        return cls(task_id, request, directory, now, artifacts={k: ArtifactInfo() for k in requested_artifacts(request)})

    @property
    def output_dir(self) -> str:
        return os.path.join(self.dir, "out")

    def artifact_path(self, kind: str) -> str:
        return os.path.join(self.output_dir, ARTIFACT_FILES[kind])

    def finalize(self) -> None:
        """Derive the terminal status from the per-artifact outcomes."""
        outcomes = [a.status for a in self.artifacts.values()]
        if outcomes and all(s == "succeeded" for s in outcomes):
            self.status = "succeeded"
        elif any(s == "succeeded" for s in outcomes):
            self.status = "partial"
        else:
            self.status = "failed"
            errors = sorted({a.error for a in self.artifacts.values() if a.error})
            self.error = self.error or "; ".join(errors) or "no artifacts were produced"
            self.error_code = self.error_code or "generation_failed"

    def to_status(self) -> TaskStatus:
        return TaskStatus(
            task_id=self.task_id,
            client_task_id=self.request.client_task_id,
            video_id=self.request.video_id,
            status=self.status,
            progress=self.progress,
            error=self.error,
            error_code=self.error_code,
            artifacts={k: v.model_copy() for k, v in self.artifacts.items()},
            created_at=self.created_at,
            started_at=self.started_at,
            finished_at=self.finished_at,
        )

    def to_json(self) -> str:
        return json.dumps({"request": self.request.model_dump(mode="json"),
                           "state": self.to_status().model_dump(mode="json")})

    @classmethod
    def from_json(cls, text: str, directory: str) -> Task:
        data = json.loads(text)
        request = TaskRequest.model_validate(data["request"])
        state = TaskStatus.model_validate(data["state"])
        return cls(
            task_id=state.task_id,
            request=request,
            dir=directory,
            created_at=state.created_at,
            status=state.status,
            progress=state.progress,
            error=state.error,
            error_code=state.error_code,
            started_at=state.started_at,
            finished_at=state.finished_at,
            artifacts=state.artifacts,
        )
