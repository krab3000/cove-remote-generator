from __future__ import annotations

import hmac

from fastapi import Request
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from .errors import ApiError

_bearer = HTTPBearer(auto_error=False)


async def require_api_key(request: Request) -> None:
    settings = request.app.state.settings
    keys: list[str] = settings.api_key_list
    if not keys and settings.allow_no_auth:
        return
    credentials: HTTPAuthorizationCredentials | None = await _bearer(request)
    if credentials is None:
        raise ApiError(401, "unauthorized", "missing bearer token")
    supplied = credentials.credentials.encode()
    if not any(hmac.compare_digest(supplied, key.encode()) for key in keys):
        raise ApiError(403, "forbidden", "invalid API key")
