from __future__ import annotations

import os


def _canonical(path: str) -> str:
    return os.path.normcase(os.path.realpath(path))


def is_allowed(path: str, roots: list[str]) -> bool:
    """True when `path` (after resolving symlinks and '..') lies inside one of the configured media roots."""
    if not roots or not path:
        return False
    target = _canonical(path)
    for root in roots:
        base = _canonical(root)
        try:
            if os.path.commonpath([target, base]) == base:
                return True
        except ValueError:  # different drives on Windows
            continue
    return False


def check_path(path: str, roots: list[str]) -> tuple[bool, bool, bool]:
    """(allowed, exists, readable). Existence is only reported for allowed paths."""
    allowed = is_allowed(path, roots)
    if not allowed:
        return False, False, False
    exists = os.path.isfile(path)
    readable = exists and os.access(path, os.R_OK)
    return True, exists, readable
