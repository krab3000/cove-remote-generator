from __future__ import annotations

import asyncio
import os
import signal
import sys
from dataclasses import dataclass


@dataclass(frozen=True)
class ProcessResult:
    returncode: int | None
    stdout: str
    stderr: str
    timed_out: bool

    @property
    def ok(self) -> bool:
        return not self.timed_out and self.returncode == 0

    def summary(self, limit: int = 500) -> str:
        text = (self.stderr or self.stdout or "").strip()
        if self.timed_out:
            text = f"timed out. {text}".strip()
        elif not text:
            text = f"exit code {self.returncode}"
        return text[:limit]


def _kill(proc: asyncio.subprocess.Process) -> None:
    if proc.returncode is not None:
        return
    try:
        if sys.platform != "win32":
            os.killpg(proc.pid, signal.SIGKILL)
        else:
            proc.kill()
    except (ProcessLookupError, PermissionError, OSError):
        pass


async def run_process(args: list[str], timeout: float) -> ProcessResult:
    """Run a command (never through a shell). Timeouts and task cancellation kill the whole process group."""
    kwargs: dict = {}
    if sys.platform != "win32":
        kwargs["start_new_session"] = True
    proc = await asyncio.create_subprocess_exec(
        *args,
        stdin=asyncio.subprocess.DEVNULL,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
        **kwargs,
    )
    try:
        stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=timeout)
        return ProcessResult(
            proc.returncode,
            stdout.decode("utf-8", "replace"),
            stderr.decode("utf-8", "replace"),
            False,
        )
    except TimeoutError:
        _kill(proc)
        await proc.wait()
        return ProcessResult(proc.returncode, "", "", True)
    except asyncio.CancelledError:
        _kill(proc)
        try:
            await asyncio.shield(proc.wait())
        except BaseException:
            pass
        raise
