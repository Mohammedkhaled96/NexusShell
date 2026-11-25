"""Helpers for NexusShell tool scripts (Python runtime).

NexusShell puts this folder on PYTHONPATH, so a tool script can simply:

    import nexus_tool as nt

    args = nt.arguments()                 # the agent's arguments, already checked
    nt.progress("Reading the file", 10)   # spoken to the user, shown in the chat
    ...
    nt.result("text the agent reads", files=[output_path])

Everything a script prints that is not one of these JSON lines is ignored, so
libraries that print are harmless. Exit with a non-zero code, or call
nt.error("..."), when the work failed; the message reaches the agent.

Environment (set by NexusShell for every run):
    NEXUS_TOOL_ARGS    JSON file with the arguments
    NEXUS_TOOL_DIR     the tool's folder (read-only: it may be under Program Files)
    NEXUS_TOOL_DATA    a folder the tool may keep downloads in (models ...)
    NEXUS_TOOL_OUTPUT  default folder for results (Documents\\NexusShell\\<tool>)
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path


def arguments() -> dict:
    """The arguments the agent passed, as declared in tool.json (missing optional ones are absent)."""
    path = os.environ.get("NEXUS_TOOL_ARGS")
    if not path:
        return {}
    return json.loads(Path(path).read_text(encoding="utf-8")) or {}


def _emit(event: dict) -> None:
    sys.stdout.write(json.dumps(event, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def progress(message: str, percent: float | None = None) -> None:
    """A step of the work. NexusShell speaks a new step at once and a percentage at most every 15 seconds."""
    event = {"event": "progress", "message": str(message)}
    if percent is not None:
        event["percent"] = max(0.0, min(100.0, float(percent)))
    _emit(event)


def result(text: str = "", files=()) -> None:
    """What the agent gets back: a text (summary, transcript ...) and the files written."""
    _emit({"event": "result", "text": str(text), "files": [str(f) for f in files]})


def error(message: str) -> None:
    """The work failed; the message is shown to the agent, which tells the user."""
    _emit({"event": "error", "message": str(message)})


def log(message: str) -> None:
    """For developers: goes to stderr, which NexusShell keeps only to explain a failure."""
    print(message, file=sys.stderr, flush=True)


def tool_dir() -> Path:
    return Path(os.environ.get("NEXUS_TOOL_DIR") or Path(sys.argv[0]).resolve().parent)


def data_dir() -> Path:
    folder = Path(os.environ.get("NEXUS_TOOL_DATA") or (tool_dir() / ".data"))
    folder.mkdir(parents=True, exist_ok=True)
    return folder


def output_dir(chosen: str | None = None) -> Path:
    folder = Path(chosen) if chosen else Path(os.environ.get("NEXUS_TOOL_OUTPUT") or (Path.home() / "Documents" / "NexusShell"))
    folder.mkdir(parents=True, exist_ok=True)
    return folder
