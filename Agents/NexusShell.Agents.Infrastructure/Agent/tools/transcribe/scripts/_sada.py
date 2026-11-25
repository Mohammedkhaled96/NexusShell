"""What run.py and setup.py share: Sada's engine pointed at NexusShell's folders."""
from __future__ import annotations

import os
import sys
import time
from pathlib import Path

import nexus_tool as nt

TOOL = Path(__file__).resolve().parent.parent
if str(TOOL) not in sys.path:
    sys.path.insert(0, str(TOOL))           # makes `engine` (Sada's engine) importable

DEFAULT_MODEL = "turbo"


def configure(out_dir: Path | None = None) -> None:
    """Sada keeps models, state and output where NexusShell says, never in the tool folder.

    Models already downloaded by Sada itself (%LOCALAPPDATA%\\Sada\\models) are found
    by engine.paths and reused, so nothing is downloaded twice.
    """
    data = nt.data_dir()
    os.environ["SADA_MODELS_DIR"] = str(data / "models")
    os.environ["SADA_STATE_DIR"] = str(data / "state")
    if out_dir is not None:
        os.environ["SADA_OUT_DIR"] = str(out_dir)


def use_bundled_ffmpeg() -> None:
    """Decode with the ffmpeg inside the imageio-ffmpeg wheel instead of a separate download."""
    import imageio_ffmpeg

    from engine import audio

    exe = imageio_ffmpeg.get_ffmpeg_exe()
    audio.find_ffmpeg = lambda name="ffmpeg": exe


def ensure_model(model_key: str) -> None:
    """Download a speech model once (from OpenAI's CDN, checked against its SHA-256)."""
    from engine import models

    info = models.info(model_key)
    if info.installed:
        return
    label = f"Downloading the {info.checkpoint} speech model ({info.size_text})"
    nt.progress(label, 0)
    last = [0.0]

    def on_progress(done: int, total: int) -> None:
        now = time.time()
        if now - last[0] < 2 and done < total:
            return
        last[0] = now
        nt.progress(label, done / total * 100 if total else None)

    models.download(model_key, on_progress=on_progress)
