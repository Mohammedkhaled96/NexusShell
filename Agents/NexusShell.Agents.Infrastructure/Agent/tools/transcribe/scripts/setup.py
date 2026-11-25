"""One-time setup of "transcribe", after its libraries are installed: the default speech model."""
from __future__ import annotations

import sys

import nexus_tool as nt
import _sada


def main() -> int:
    _sada.configure()
    _sada.use_bundled_ffmpeg()          # proves ffmpeg is in place before the first real run
    _sada.ensure_model(_sada.DEFAULT_MODEL)
    nt.result(f"The {_sada.DEFAULT_MODEL} speech model is ready.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        import traceback

        traceback.print_exc()
        nt.error(f"{type(exc).__name__}: {exc}")
        sys.exit(1)
