"""NexusShell tool "transcribe": one audio or video file to text, with Sada's engine.

Arguments (tool.json): file, language, timestamps, model, hotwords, output_folder.
Result: the transcript for the agent, and the .txt, .srt and .json files.
"""
from __future__ import annotations

import sys
from pathlib import Path

import nexus_tool as nt
import _sada

STEPS = {
    "decoding": "Reading the audio",
    "detecting": "Detecting the language",
    "transcribing": "Transcribing",
    "polishing": "Tidying the text",
    "writing": "Writing the files",
}


def main() -> int:
    args = nt.arguments()
    source = Path(args["file"])
    model = args.get("model") or _sada.DEFAULT_MODEL
    out_dir = nt.output_dir(args.get("output_folder"))

    _sada.configure(out_dir)
    _sada.use_bundled_ffmpeg()
    _sada.ensure_model(model)

    from engine.backends import load_backend
    from engine.device import pick_device
    from engine.job import run_transcription

    nt.progress("Loading the speech model")
    # The environment has PyTorch's CPU build, so the CPU is what can run it.
    device = pick_device("cpu")
    # openai-whisper reads OpenAI's checkpoints; faster-whisper's weights would need huggingface.co.
    backend = load_backend("openai-whisper", model, device)

    def on_progress(p) -> None:
        step = STEPS.get(p.state)
        if not step:
            return
        if p.state == "transcribing":
            nt.progress(step, p.percent)
        elif p.state == "detecting" and p.language:
            nt.progress(f"{step}: {p.language}")
        else:
            nt.progress(step)

    meta = run_transcription(
        source, backend, out_dir,
        language=(args.get("language") or None),
        timestamps=bool(args.get("timestamps")),
        hotwords=(args.get("hotwords") or None),
        on_progress=on_progress,
    )

    summary = (
        f"Language: {meta.get('language', '?')} ({meta.get('language_confidence', 0):.0%} confidence)"
        f"{', right to left' if meta.get('rtl') else ''}. "
        f"Words: {meta.get('words', 0):,}. Paragraphs: {meta.get('paragraphs', 0)}. "
        f"Speed: {meta.get('realtime_factor', '?')}x real time on {device.label}. Model: {meta.get('model', model)}."
    )
    nt.result(summary + "\n\nTranscript:\n" + (meta.get("text") or "(no speech found)"), files=meta.get("files", []))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except FileNotFoundError as exc:
        nt.error(f"The file was not found: {exc}")
        sys.exit(2)
    except Exception as exc:  # the message reaches the agent; the traceback stays in stderr
        import traceback

        traceback.print_exc()
        nt.error(f"{type(exc).__name__}: {exc}")
        sys.exit(1)
