from __future__ import annotations

import json

from heavylifter import API_VERSION
from heavylifter.config import Settings
from heavylifter.main import create_app
from heavylifter.models import InfoResponse, TaskRequest, TaskStatus

from .conftest import CONTRACT


def test_fixtures_validate(fixtures_dir):
    TaskRequest.model_validate_json((fixtures_dir / "task_request.json").read_text(encoding="utf-8"))
    TaskStatus.model_validate_json((fixtures_dir / "task_status.json").read_text(encoding="utf-8"))
    info = InfoResponse.model_validate_json((fixtures_dir / "info.json").read_text(encoding="utf-8"))
    assert info.api_version == API_VERSION


def test_openapi_matches_contract(tmp_path):
    """contract/openapi.json is the published contract; regenerate it with scripts/export-openapi.py."""
    app = create_app(Settings(api_keys="k", work_dir=tmp_path))
    published = json.loads((CONTRACT / "openapi.json").read_text(encoding="utf-8"))
    assert app.openapi() == published
