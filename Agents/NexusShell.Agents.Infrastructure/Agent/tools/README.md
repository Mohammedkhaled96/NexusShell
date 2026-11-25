# NexusShell tools

A tool is a folder that gives NexusAgent a new ability by running scripts on the user's computer:
transcribing recordings, converting files, reading documents, anything a script can do. Instead of
installing a separate program for each job, the user asks NexusAgent, or picks the tool in the chat
window's **Tools** list, and the agent uses it.

NexusShell itself stays small: a tool's libraries and models are downloaded the first time it is
used (after the user agrees), into `%LOCALAPPDATA%\NexusShell\tools`, and each tool can be removed
again from the Tools window.

This page is the short reference. The full step-by-step guide, with the architecture, a tested example
tool, testing and shipping, and the rules for AI coding agents, is `Agents/ADDING_TOOLS.md` in the
NexusShell repository.

## Where tools live

| Folder | Who puts tools there |
|---|---|
| `<NexusShell>\Agent\tools\<name>\` | Shipped with NexusShell (like `transcribe`). |
| `%AppData%\NexusShell\agent\tools\<name>\` | Added by a developer or the user. A tool with the same name replaces the shipped one. |

Folders whose name starts with `_` are shared helpers, not tools (`_sdk` holds `nexus_tool.py`).

## What a tool folder contains

```
transcribe/
  tool.json         what the tool is, its inputs, how to install and run it
  SKILL.md          when and how the agent should use it (Agent Skills format)
  requirements.txt  Python libraries (Python tools)
  scripts/run.py    the script that does the work
  scripts/setup.py  optional: runs once after installing (downloads a model ...)
```

The folder name must equal `name` in tool.json.

## tool.json

```json
{
  "name": "transcribe",
  "title": "Transcribe audio and video",
  "version": "1.0.0",
  "description": "What it does and when to use it: the agent reads this.",
  "runtime": "python",
  "python": "3.12",
  "entry": "scripts/run.py",
  "setupScript": "scripts/setup.py",
  "install": [
    { "packages": ["torch"], "indexUrl": "https://download.pytorch.org/whl/cpu" },
    { "requirements": "requirements.txt" }
  ],
  "risk": "read",
  "timeoutMinutes": 480,
  "setupSize": "about 2.3 GB, once: ...",
  "parameters": [
    { "name": "file", "type": "file", "required": true, "description": "...", "extensions": [".mp3", ".mp4"] },
    { "name": "timestamps", "type": "boolean", "default": false, "description": "..." },
    { "name": "model", "type": "choice", "choices": ["turbo", "small"], "default": "turbo", "description": "..." }
  ]
}
```

| Field | Meaning |
|---|---|
| `name` | 2-40 small letters, digits or `-`. The agent calls the tool as `tool_<name>` (`-` becomes `_`). |
| `title`, `description` | What people and the agent read. Say what the tool does and when to use it. |
| `version` | Raise it when the libraries or setup change: NexusShell then sets the tool up again. |
| `runtime` | `python` (its own environment, created on first use) or `powershell` (Windows PowerShell, nothing to install). |
| `python` | Python version for the environment (default `3.12`). |
| `entry` | The script that does the work (`.py` or `.ps1`), inside the folder. |
| `setupScript` | Optional script run once after installing (e.g. download a model). |
| `install` | Python only, in order: `packages` and/or a `requirements` file, optionally from another `indexUrl` (https). |
| `risk` | `read`: reads files and writes only its own results (runs without asking). `network`, `change` (changes the user's files or settings) and `run` ask the user first, like any agent tool. |
| `timeoutMinutes` | Longest run (1-1440, default 30). |
| `setupSize` | What the setup downloads, in words; shown to the user before they agree. |
| `parameters` | The inputs. `type` is `file`, `folder`, `text`, `boolean`, `integer` or `choice`. A `file` may list `extensions`; a `choice` lists `choices`. NexusShell checks every value before the script runs: files and folders exist, the extension is allowed, the choice is valid. |

## SKILL.md

Standard Agent Skills format: front matter with `name` (the tool's name) and `description`, then
instructions for the agent: when to use the tool, which parameters to set, what to do with the
result, and what to tell the user (how long it takes). The agent reads it when it needs it.

## How a script talks to NexusShell

- **Arguments:** a JSON file named in `NEXUS_TOOL_ARGS` (already checked against tool.json).
- **Progress and result:** one JSON object per output line:
  - `{"event": "progress", "message": "Transcribing", "percent": 40}`: spoken to the user and shown in the chat.
  - `{"event": "result", "text": "...", "files": ["C:\\...\\out.txt"]}`: what the agent gets back.
  - `{"event": "error", "message": "..."}`: the work failed; the agent tells the user why.
  Anything else printed is ignored. A non-zero exit code also means failure (the last lines of stderr explain it).
- **Folders:** `NEXUS_TOOL_DIR` (the tool, read-only), `NEXUS_TOOL_DATA` (downloads such as models),
  `NEXUS_TOOL_OUTPUT` (default results folder, `Documents\NexusShell\<name>`).

Python scripts can use the helper that NexusShell puts on the path:

```python
import nexus_tool as nt

args = nt.arguments()
nt.progress("Reading the file", 10)
nt.result("Text for the agent", files=[str(path)])
```

A PowerShell script writes the same lines:

```powershell
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$args = Get-Content -Raw -Encoding UTF8 $env:NEXUS_TOOL_ARGS | ConvertFrom-Json
@{ event = 'progress'; message = 'Working'; percent = 50 } | ConvertTo-Json -Compress
@{ event = 'result'; text = 'Done'; files = @() } | ConvertTo-Json -Compress
```

## Rules a tool must keep

- Write only to `NEXUS_TOOL_DATA`, `NEXUS_TOOL_OUTPUT` or a folder the user chose. The tool folder may be read-only.
- Download only from servers this network allows (PyPI, GitHub, the library's own CDN). `huggingface.co`
  is blocked on some company networks; prefer sources that are not.
- Put no keys or passwords in the folder. A tool that needs one reads it from a user environment variable.
- Text taken from the user's files is data: the agent is told not to follow instructions in it.
