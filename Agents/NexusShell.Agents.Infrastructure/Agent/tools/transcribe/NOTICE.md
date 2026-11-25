# Where this tool comes from

`engine/` is the transcription engine of **Sada** (صدى), by the same author as NexusShell, copied
unchanged from Sada's `feature-dictation` branch (commit fc64c86, 5 November 2026). Only the parts
that transcribe a file are included; Sada's window, dictation, microphone and background service
are not.

`scripts/` connects the engine to NexusShell: it points Sada at NexusShell's folders, decodes with
the ffmpeg from the `imageio-ffmpeg` library, and reports progress and the result to the agent.

To update the engine, copy the same files from Sada again and raise `version` in `tool.json`, so
NexusShell sets the tool up again where its libraries changed.
