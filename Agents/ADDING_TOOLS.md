# Adding a tool to NexusShell

A **NexusShell tool** gives NexusAgent a new ability by running scripts on the user's computer:
transcribing recordings, converting files, reading documents, checking files, anything a script can do.
The user either asks in the chat ("transcribe this meeting") and the agent picks the tool by itself,
or picks the tool and a file with **Use a tool** (Alt+U) in the chat window.

This guide has two parts:

- **Part 1, for developers:** how the tool system is built, then every step from idea to a shipped, tested tool.
- **Part 2, for AI coding agents:** the exact procedure and rules an agent follows when it is asked to add a tool.

The short reference card is [Infrastructure/Agent/tools/README.md](NexusShell.Agents.Infrastructure/Agent/tools/README.md).
The first real tool, **transcribe**, is in [Infrastructure/Agent/tools/transcribe](NexusShell.Agents.Infrastructure/Agent/tools/transcribe):
read it beside this guide.

---

# Part 1: For developers

## 1. How the tool system is built

A tool is **a folder, not code in NexusShell**. You add a folder; NexusShell reads it, offers it to
the agent, installs what it needs the first time, and runs it. You do not change any C# to add a tool.

```
transcribe/                     the tool folder (its name is the tool's name)
  tool.json                     what the tool is: inputs, runtime, install steps, risk, time limit
  SKILL.md                      instructions for the agent: when to use it, how, what to do with the result
  requirements.txt              Python libraries (Python tools only)
  scripts/run.py                the script that does the work ("entry")
  scripts/setup.py              optional: runs once after installing (downloads a model ...)
  engine/ ...                   any other code the scripts import
```

How the layers use it (clean architecture, inner layers know nothing of outer ones):

| Layer | Type | What it does with a tool |
|---|---|---|
| Domain | `Domain/ToolPackages/ToolPackage.cs` | `ToolPackage`, `ToolParameter`, `ToolInstallStep`: the tool as data. No I/O. |
| Application | `Application/ToolPackages/ToolPackages.cs` | Ports `IToolPackageCatalog` and `IToolPackageRuntime`; `ToolPackageToolProvider` turns every tool into an agent function `tool_<name>` plus `setup_tool`; `ToolPackageFunction` checks arguments, runs the tool, reports progress; `ToolArguments` validates and converts arguments; `IToolPackageService` serves the Tools window. |
| Application | `Agents/ExpertAgentFactory.cs` | Gives every expert the tool functions (read fresh each time experts are built). |
| Infrastructure | `ToolPackages/FileToolPackageCatalog.cs` | Finds tool folders, reads and validates `tool.json`. A wrong tool is skipped and listed as a problem, never half-loaded. |
| Infrastructure | `ToolPackages/ToolPackageRuntime.cs` | Creates each Python tool's own environment with uv, installs its libraries, runs its scripts, reads their output lines. `UvBootstrapper` gets uv once (checked against its SHA-256). |
| Infrastructure | `Skills/FileSkillsProviderFactory.cs` | Makes the tool's `SKILL.md` a skill the agent reads on demand, and hides the tool's scripts from the generic skill runner. |
| App (WPF) | `Views/AgentToolsWindow.xaml`, `ViewModels/AgentToolsViewModel.cs` | The Tools window: list, Set up, Use with a file, Remove downloads. Opened from **Use a tool** (Alt+U) or `/use` in the chat. |

What happens when the agent uses a tool:

```
User: "transcribe C:\rec\meeting.mp3 with timestamps"
  -> the expert sees the function tool_transcribe (schema built from tool.json's parameters)
  -> it may read the tool's SKILL.md for guidance
  -> it calls tool_transcribe { "file": "C:\\rec\\meeting.mp3", "timestamps": true }
  -> ToolPackageFunction:
       not set up?        -> tells the agent the download size and to call setup_tool (the user approves it)
       wrong arguments?   -> "Not run: ..." (nothing starts)
       otherwise          -> ToolPackageRuntime runs scripts/run.py in the tool's own Python environment
  -> the script prints progress lines (spoken in the chat) and one result line
  -> the agent gets the text and the list of files, marked as data, and answers the user
```

Where things are kept on the user's computer:

| What | Where |
|---|---|
| Tools shipped with NexusShell | `<NexusShell>\Agent\tools\<name>\` (copied from `Agents/NexusShell.Agents.Infrastructure/Agent/tools` at build time; may be read-only) |
| Tools added by a developer or the user | `%AppData%\NexusShell\agent\tools\<name>\` (a tool with the same name replaces the shipped one) |
| uv, the Python runtimes, the download cache | `%LOCALAPPDATA%\NexusShell\tools\runtime`, `...\tools\cache` |
| Each Python tool's environment | `%LOCALAPPDATA%\NexusShell\tools\envs\<name>\` |
| A tool's downloads (models ...) | `%LOCALAPPDATA%\NexusShell\tools\data\<name>\` (`NEXUS_TOOL_DATA`) |
| A tool's results | `Documents\NexusShell\<name>\` (`NEXUS_TOOL_OUTPUT`), unless the user picks a folder |

## 2. Decide that a tool package is the right choice

NexusShell has five ways to extend the agent. Pick the smallest one that does the job:

| You want to ... | Use | Where |
|---|---|---|
| Run scripts, libraries or models on the user's computer (transcribe, convert, OCR, PDF, images, hashing ...) | **A tool package** (this guide) | `Agent/tools/<name>/` |
| Teach the agent knowledge or a procedure, with no code to run | A skill (`SKILL.md` only) | `Agent/skills/<name>/` |
| Work with an online account that needs a sign-in (mail, calendar, GitHub) | A connector (`IConnector`) | `Infrastructure/Connectors/` |
| Use an existing MCP server | An MCP entry or template | `mcp.json`, `McpConnectorPresets` |
| Something tiny, fast and deeply tied to NexusShell (files, shell, system info) | A built-in C# tool (`IAgentToolProvider`) | `Infrastructure/Tools/<Category>/` |

Choose a tool package when the work needs its own libraries, may take minutes, works on the user's
files, or replaces a separate program the user would otherwise install.

## 3. Choose the name, runtime and risk

1. **Name:** 2-40 characters, small letters, digits and `-`, starting with a letter: `transcribe`, `pdf-to-text`, `file-hash`.
   The agent calls it `tool_<name>` with `-` turned into `_` (`tool_pdf_to_text`). The folder must have the same name.
2. **Runtime:**
   - `powershell`: Windows PowerShell 5.1, nothing to install, ready at once. Good for anything Windows and .NET already do.
   - `python`: its own Python environment, created on first use. Good when you need libraries (PyTorch, Whisper, Pillow, PyMuPDF ...).
3. **Risk:** decides whether the user is asked before each run.

   | `risk` | Means | Permissions "Ask" (default) | "Auto-approve edits" | "Plan" (read-only) |
   |---|---|---|---|---|
   | `read` | Reads files and writes only its own results (in `NEXUS_TOOL_OUTPUT`, `NEXUS_TOOL_DATA` or a folder the user chose) | runs | runs | runs |
   | `network` | Sends data over the network while it runs | asks first | asks first | asks first |
   | `change` | Changes or deletes the user's files or settings | asks first | runs | not offered |
   | `run` | Runs arbitrary commands | asks first | asks first | not offered |

   Be honest: if the tool overwrites the user's file, it is `change`, not `read`. The one-time setup (`setup_tool`) always
   asks, whatever the permissions.

## 4. Create the folder

- **While developing:** `%AppData%\NexusShell\agent\tools\<name>\`. NexusShell picks it up when you open the Tools window.
- **To ship it with NexusShell:** `Agents/NexusShell.Agents.Infrastructure/Agent/tools/<name>/` in this repository. Everything
  under `Agent\` is copied next to the program on every build (`Content Include="Agent\**\*"` in the Infrastructure project).

Folders starting with `_` are helpers, not tools (`_sdk` holds `nexus_tool.py`).

## 5. Write tool.json

A complete example (the transcribe tool, shortened):

```json
{
  "name": "transcribe",
  "title": "Transcribe audio and video",
  "version": "1.0.0",
  "description": "Turns the speech in an audio or video file into text and saves .txt, .srt and .json. Use it whenever the user wants a transcript, subtitles, minutes or a summary of a recording.",
  "runtime": "python",
  "python": "3.12",
  "entry": "scripts/run.py",
  "setupScript": "scripts/setup.py",
  "install": [
    { "packages": [ "torch" ], "indexUrl": "https://download.pytorch.org/whl/cpu" },
    { "requirements": "requirements.txt" }
  ],
  "risk": "read",
  "timeoutMinutes": 480,
  "setupSize": "about 2.3 GB, once: Python with its speech libraries (about 0.7 GB) and the turbo speech model (1.6 GB)",
  "parameters": [
    { "name": "file", "type": "file", "required": true, "description": "The recording to transcribe.", "extensions": [ ".mp3", ".wav", ".mp4" ] },
    { "name": "language", "type": "text", "description": "Language code to use instead of detecting it (ar, en ...)." },
    { "name": "timestamps", "type": "boolean", "default": false, "description": "Start every paragraph with its time." },
    { "name": "model", "type": "choice", "choices": [ "turbo", "small" ], "default": "turbo", "description": "Speech model." },
    { "name": "output_folder", "type": "folder", "description": "Folder for the results." }
  ]
}
```

Every field, and the rule NexusShell checks:

| Field | Required | Rule |
|---|---|---|
| `name` | yes | `^[a-z][a-z0-9-]{1,39}$`, equal to the folder name. |
| `title` | yes | Short, for people ("Transcribe audio and video"). Spoken in progress messages. |
| `description` | yes | What it does **and when to use it**. The agent chooses tools from this text, so name the user's words: "transcript, subtitles, minutes". |
| `version` | no (`1.0.0`) | Raise it whenever libraries, the setup script or what the scripts need change. |
| `runtime` | no (`python`) | `python` or `powershell`. |
| `python` | no (`3.12`) | `3.x`; the Python version of the tool's environment. |
| `entry` | yes | The script that does the work, inside the folder: `.py` for python, `.ps1` for powershell. |
| `setupScript` | no | Runs once after installing, same protocol as `entry` (download models here). |
| `install` | no | Python only. Steps in order; each has `packages` (a list of requirement specifiers such as `numpy>=2.0`) and/or `requirements` (a file in the folder), and optionally `indexUrl` (https only). |
| `risk` | no (`read`) | `read`, `network`, `change` or `run` (section 3). |
| `timeoutMinutes` | no (`30`) | 1-1440. The run is stopped (with its child processes) after this. |
| `setupSize` | no | In words, what the first setup downloads. Shown to the user before they agree. Required in practice for any tool with downloads. |
| `parameters` | no | The inputs, below. |

Parameters:

| Key | Rule |
|---|---|
| `name` | `^[a-z][a-z0-9_]{0,39}$`, unique. |
| `type` | `file`, `folder`, `text`, `boolean`, `integer` or `choice`. |
| `description` | What it is, for the agent. NexusShell adds "Full path of a file (.mp3, ...)" and the default for you. |
| `required` | `true` or `false` (default). |
| `default` | Used when the agent leaves the parameter out. Text, number or true/false. |
| `extensions` | For `file`: the allowed extensions; also the filter of the file picker in **Use with a file**. |
| `choices` | For `choice`: the allowed values (required for this type). |

What NexusShell guarantees before your script starts (`ToolArguments`):

- `file`: the path exists (environment variables expanded, quotes removed, relative paths resolved against the chat's
  working folder) and the extension is allowed. You receive the full path.
- `folder`: the folder exists. You receive the full path.
- `boolean`: `true`/`false` (also accepts yes/no, 1/0, on/off). `integer`: a whole number. `choice`: one of `choices`, with
  the exact spelling from tool.json. `text`: at most 4000 characters.
- Unknown parameters are refused; missing required ones are refused; optional ones without a default are left out.

If anything is wrong, the agent gets "Not run: ..." and nothing starts.

## 6. Write the script

### The protocol (any runtime)

| Direction | How |
|---|---|
| Arguments in | A UTF-8 JSON file whose path is in `NEXUS_TOOL_ARGS`: `{"file": "C:\\...", "timestamps": true}`. Nothing comes on stdin. |
| Progress out | One line: `{"event": "progress", "message": "Transcribing", "percent": 40}` (`percent` 0-100, optional). |
| Result out | One line: `{"event": "result", "text": "what the agent reads", "files": ["C:\\...\\out.txt"]}`. |
| Error out | One line: `{"event": "error", "message": "why it failed, for the user"}`, then exit with a non-zero code. |
| Anything else | Other stdout lines are ignored; stderr is kept only to explain a crash (its last lines). |
| Success | Exit code 0 and no error event. |

Environment variables NexusShell sets for every run:

| Variable | Meaning |
|---|---|
| `NEXUS_TOOL_ARGS` | The arguments file (deleted after the run). |
| `NEXUS_TOOL_DIR` | The tool folder. **Read-only**: it may be under Program Files. |
| `NEXUS_TOOL_DATA` | A folder the tool may keep downloads in (models, caches). Kept until the user presses Remove downloads. |
| `NEXUS_TOOL_OUTPUT` | The default results folder, `Documents\NexusShell\<name>`. Create it if missing. |

Python runs also get `PYTHONUTF8=1`, `PYTHONIOENCODING=utf-8`, `PYTHONDONTWRITEBYTECODE=1` (no `__pycache__` in the tool folder),
`PYTHONNOUSERSITE=1`, `PYTHONUNBUFFERED=1`, and `PYTHONPATH` pointing to `_sdk`. The working folder is the tool folder.

How progress reaches the user: a **new step** (the text before the first `:`) is spoken at once; the same step with a new
percentage at most every 15 seconds. So report steps with stable names ("Downloading the model", "Transcribing") and put
changing details in `percent`, not in the step name.

How the result reaches the agent: the text (up to 40,000 characters; longer text is cut and the agent is told the rest is
in the files), preceded by a note that it is data, followed by "Files written:" and the files that exist.

### A Python tool

Use the helper NexusShell puts on the path:

```python
"""NexusShell tool "word-count": counts the words of a text file."""
from __future__ import annotations

import sys
from pathlib import Path

import nexus_tool as nt


def main() -> int:
    args = nt.arguments()                            # already checked against tool.json
    source = Path(args["file"])
    out_dir = nt.output_dir(args.get("output_folder"))

    nt.progress("Reading the file")
    words = source.read_text(encoding="utf-8", errors="replace").split()

    report = out_dir / f"{source.stem}-words.txt"
    report.write_text(f"{len(words)} words\n", encoding="utf-8")

    nt.result(f"{source.name} has {len(words):,} words.", files=[report])
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:                         # the message reaches the agent; the traceback stays in stderr
        import traceback
        traceback.print_exc()
        nt.error(f"{type(exc).__name__}: {exc}")
        sys.exit(1)
```

`nexus_tool` gives you `arguments()`, `progress(message, percent)`, `result(text, files)`, `error(message)`, `log(message)`
(stderr), `tool_dir()`, `data_dir()` and `output_dir(chosen)`. Your script's own folder is on `sys.path`, so
`import _helpers` works for a file next to it; add the tool folder yourself for packages beside `scripts/`
(see `transcribe/scripts/_sada.py`).

### A PowerShell tool

This complete tool computes a file's checksum (tested; also in `ToolPackagesTests`):

`file-hash/tool.json`

```json
{
  "name": "file-hash",
  "title": "File checksum",
  "description": "Computes the checksum (SHA256, SHA1 or MD5) of a file, to check a download or compare two copies.",
  "runtime": "powershell",
  "entry": "run.ps1",
  "risk": "read",
  "timeoutMinutes": 10,
  "parameters": [
    { "name": "file", "type": "file", "required": true, "description": "The file to check." },
    { "name": "algorithm", "type": "choice", "choices": [ "SHA256", "SHA1", "MD5" ], "default": "SHA256", "description": "Checksum type." }
  ]
}
```

`file-hash/run.ps1`

```powershell
# NexusShell tool "file-hash": the checksum of a file, to check a download or compare two copies.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8      # Arabic and other names survive

function Send-Event([hashtable] $Data) { $Data | ConvertTo-Json -Compress -Depth 4 }

try {
    $arguments = Get-Content -Raw -Encoding UTF8 -LiteralPath $env:NEXUS_TOOL_ARGS | ConvertFrom-Json
    $algorithm = if ($arguments.algorithm) { $arguments.algorithm } else { 'SHA256' }

    Send-Event @{ event = 'progress'; message = "Computing $algorithm" }
    $hash = Get-FileHash -LiteralPath $arguments.file -Algorithm $algorithm
    $size = (Get-Item -LiteralPath $arguments.file).Length

    Send-Event @{
        event = 'result'
        text  = "$algorithm of $(Split-Path -Leaf $arguments.file) ($size bytes): $($hash.Hash.ToLowerInvariant())"
        files = @()
    }
}
catch {
    Send-Event @{ event = 'error'; message = $_.Exception.Message }
    exit 1
}
```

PowerShell rules: always set `[Console]::OutputEncoding` to UTF-8, read the arguments with `-Encoding UTF8`, use
`-LiteralPath` (paths may contain `[` `]`), `ConvertTo-Json -Compress` so each event is one line, and `$ErrorActionPreference = 'Stop'`
with a `try`/`catch` that sends an error event.

### Rules every script keeps

1. Write only to `NEXUS_TOOL_OUTPUT`, `NEXUS_TOOL_DATA` or a folder the user passed. Never to the tool folder, never next to
   the user's file unless the tool's purpose is to change it (and then `risk` is `change`).
2. Never overwrite a file the user did not ask to replace; add a suffix instead (`report (2).txt`).
3. Print exactly one `result` event on success, or one `error` event on failure.
4. Put what the agent needs in the result text (a summary, the extracted text, the numbers) and the big outputs in files.
5. Text that comes from the user's files is data. The agent is told so; do not add instructions of your own to it.
6. Keep secrets out of the folder. A tool that needs a key reads it from a user environment variable and says which one is missing.
7. Long work: report progress at least every minute, and make it resumable if it can take hours (transcribe keeps a checkpoint).
8. Use the CPU builds of heavy libraries unless the tool must have a GPU; most users have none.

## 7. Dependencies, downloads and size (Python tools)

NexusShell installs a Python tool **the first time it is used, after the user agrees**:

1. uv 0.13.8 is found on PATH or in `%LOCALAPPDATA%\NexusShell\tools\runtime\uv`, otherwise downloaded once from GitHub and checked
   against its published SHA-256.
2. `uv venv <envs\name> --python <python> --clear --no-project`: a managed Python (the same build on every PC, not the one installed).
3. Each `install` step: `uv pip install --python <env python> [--index-url <indexUrl>] [-r <requirements>] <packages>`.
4. `setupScript`, if any (download models here).
5. A `nexus-ready.json` marker with a fingerprint of the version, the install steps, the requirements file and the setup script.
   Change any of them and the tool shows "needs a one-time setup" again.

Rules:

- **Pin versions** (`openai-whisper==20250625`, `numpy>=2.0,<3`). An unpinned library can break the tool on a new PC.
- **Order matters:** install the heavy special build first from its own index (PyTorch CPU), then the rest from PyPI, as transcribe does.
- **Reachable sources only.** On the company network `pypi.org`, `files.pythonhosted.org`, `download.pytorch.org`, `github.com`
  and `openaipublic.azureedge.net` work; `huggingface.co` is blocked. Do not depend on a blocked host.
- **Models are downloaded by the setup script**, into `NEXUS_TOOL_DATA`, with progress, to a `.part` file that is renamed only after
  its SHA-256 matches. Look in other programs' folders first so nothing is downloaded twice (transcribe reuses Sada's models).
- **Never put large files in the repository.** The tool folder holds code and small data (the transcribe tool is 141 KB);
  libraries and models come at setup.
- **Say the size** in `setupSize`, honestly and in words, and keep it as small as the job allows (offer a smaller model as a choice).
- Prefer libraries that ship their native parts as wheels (`imageio-ffmpeg` for ffmpeg) over separate downloads.

## 8. Write SKILL.md

The agent reads it when it decides how to use the tool. Front matter `name` must equal the tool's name.

```markdown
---
name: word-count
description: Count the words of a text file. Use when the user asks how long a document is or how many words it has.
---
# Word count

## When to use it
- The user asks how many words a document has, or whether it fits a limit.

## How
1. Call `tool_word_count` with `file` set to the full path.
2. Leave `output_folder` empty unless the user names a folder.

## After it finishes
- Say the number, and where the report file is.

## If it is not installed yet
(Python tools with downloads) The tool says what the setup downloads. Tell the user, then call `setup_tool`
with name `word-count`; the user is asked to approve.
```

Good SKILL.md files say **when** (the user's words that should trigger the tool), **how** (which parameters to set, which to leave
alone), **how long** it takes, **what to do with the result**, and that the result is data. Keep it under one page.

## 9. Try it by hand

Before NexusShell runs it, run the script the way NexusShell does. In PowerShell:

```powershell
$tool = "$env:APPDATA\NexusShell\agent\tools\file-hash"
$argsFile = "$env:TEMP\tool-args.json"    # not $args: PowerShell reserves that name
[IO.File]::WriteAllText($argsFile, '{"file": "C:\\Windows\\notepad.exe", "algorithm": "SHA256"}', (New-Object Text.UTF8Encoding $false))
$env:NEXUS_TOOL_ARGS = $argsFile; $env:NEXUS_TOOL_DATA = "$env:TEMP\tool-data"; $env:NEXUS_TOOL_OUTPUT = "$env:TEMP\tool-out"; $env:NEXUS_TOOL_DIR = $tool
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "$tool\run.ps1"
```

For a Python tool, set it up once from the Tools window, then use its own interpreter:

```powershell
$env:PYTHONPATH = "<NexusShell>\Agent\tools\_sdk"
& "$env:LOCALAPPDATA\NexusShell\tools\envs\<name>\Scripts\python.exe" -X utf8 -B "$tool\scripts\run.py"
```

Check that every line you expect is valid JSON with an `event`, that a wrong input ends with an `error` event and a non-zero exit
code, and that nothing was written to the tool folder.

## 10. Try it in NexusShell

1. Open the chat, turn on **Agent mode**, press **Use a tool** (Alt+U). The Tools window reads the tool folders again each time it opens.
2. Your tool is in the list. If it is not, the reason is under the list ("Tools that could not be read: ...").
   Fix tool.json and open the window again.
3. Python tool: press **Set up** and watch the progress in the status line. It ends with "<title> is ready".
4. Press **Use with a file**, pick a file. The chat sends "Use the <title> tool (tool_<name>) on this file: ...". Type
   instructions in the message box first and they are sent with it.
5. Also ask in plain words without naming the tool ("how many words are in C:\notes\essay.txt?"). If the agent does not
   choose your tool, improve `description` in tool.json and the "When to use it" part of SKILL.md.
6. Try a wrong file type and a missing file: the agent should say why it did not run.
7. Check the screen reader: progress steps are spoken, the result is read, the Tools window's buttons have names.

## 11. Write the automated tests

Tests live in `Agents/NexusShell.Agents.Tests/Application/ToolPackagesTests.cs` and use the real composition root
(`AgentTestHost`): only model calls, approvals and secrets are faked.

1. **The shipped tool is valid** (for a tool in `Agent/tools`):

   ```csharp
   [Fact]
   public void The_shipped_word_count_tool_is_a_valid_package()
   {
       var package = FileToolPackageCatalog.Read(Path.Combine(AppContext.BaseDirectory, "Agent", "tools", "word-count"), bundled: true);
       Assert.Equal("tool_word_count", package.FunctionName);
       Assert.NotNull(package.SkillFile);
   }
   ```

2. **The agent runs it end to end** (a PowerShell tool, or a small PowerShell stand-in for a Python tool's protocol): write the
   tool into the test host with `_host.WriteBundled(Path.Combine("tools", name, "tool.json"), ...)`, build with
   `_host.Build(s => s.AddSingleton(new ToolPackageOptions { Root = ..., OutputRoot = ... }))`, script the model to call
   `tool_<name>`, and assert the reply contains the result, the files exist, and progress arrived as `AgentProgressKind.ToolProgress`.
   `The_agent_runs_a_tool_on_a_file_and_gets_its_result` is the pattern to copy.
3. **Wrong input is refused:** missing required value, wrong extension, unknown choice (pattern: `Wrong_arguments_are_refused_before_anything_runs`).
4. **Failure is explained:** an `error` event and a crash with stderr (patterns: `A_failing_script_explains_why`, `A_crashing_script_reports_its_last_error_lines`).
5. Test the Python logic of a heavy tool with plain Python tests next to it if you need to; NexusShell's test suite does not
   download libraries or models.

Run them:

```powershell
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet test Agents/NexusShell.Agents.Tests/NexusShell.Agents.Tests.csproj --filter "FullyQualifiedName~ToolPackagesTests"
dotnet test Agents/NexusShell.Agents.Tests/NexusShell.Agents.Tests.csproj
```

## 12. Ship it

1. The folder is in `Agents/NexusShell.Agents.Infrastructure/Agent/tools/<name>/` (for shipped tools).
2. Build the app and check the files are next to the program:

   ```powershell
   $env:MSBuildEnableWorkloadResolver = 'false'
   dotnet build NexusShell.App/NexusShell.App.csproj
   Get-ChildItem -Recurse NexusShell.App/bin/Debug/net10.0-windows10.0.19041.0/Agent/tools/<name>
   ```

3. Code taken from another project gets a `NOTICE.md` in the tool folder (where it comes from, which commit, its license), and
   third-party libraries with licenses that require it are added to `THIRD-PARTY-NOTICES.md`.
4. Add one line about the tool to the tools section of `Agents/README.md`.
5. Changing an existing tool: raise `version` when its libraries or setup change, so users are set up again; keep parameter names
   stable (chats and scheduled tasks may use them).
6. Commit in Conventional Commits style, the tool and its tests separately if they are large:
   `feat(tools): word-count tool`, `test(tools): cover the word-count tool`.

## 13. Checklist

- [ ] The folder name equals `name`; `title` and `description` say what the tool does and when to use it.
- [ ] Parameters have descriptions; files list their `extensions`; choices list their values; defaults are set.
- [ ] `risk` is honest; `timeoutMinutes` fits the longest real job.
- [ ] The script reads `NEXUS_TOOL_ARGS`, prints progress steps, one `result` or one `error`, and exits non-zero on failure.
- [ ] It writes only to `NEXUS_TOOL_OUTPUT`, `NEXUS_TOOL_DATA` or the folder the user chose; nothing in the tool folder.
- [ ] UTF-8 everywhere; works with Arabic file names and text.
- [ ] Python: versions pinned; heavy builds from the right index; downloads only from reachable hosts; models checked by SHA-256;
      `setupSize` is honest.
- [ ] No keys, passwords or large binaries in the folder.
- [ ] SKILL.md: name equals the tool's name; when, how, how long, what to do with the result.
- [ ] Tried by hand, in the Tools window, with Use with a file, and by asking in plain words.
- [ ] Tests added and the whole suite passes.

## 14. When something goes wrong

| What you see | Why | Fix |
|---|---|---|
| The tool is missing from the list, and "Tools that could not be read: <name>: ..." | tool.json broke a rule | The message names the field. Fix it and open the Tools window again. |
| "the folder must be named like the tool" | Folder and `name` differ | Rename one of them. |
| "entry must stay inside the tool folder" | A path with `..` or a full path | Use a path relative to the tool folder. |
| "Not run: ... is not a supported file" | The file's extension is not in `extensions` | Add the extension, if the tool really reads it. |
| The agent never picks the tool | Its description does not match how users ask | Name the user's words in `description` and SKILL.md. |
| "Installing failed: ..." during Set up | A library could not be found or built | Check the version pins, the index, and that the host is reachable on this network. |
| "<title> failed: exit code 1" with no reason | The script crashed before printing an error event | Wrap `main` in try/except (Python) or try/catch (PowerShell) and send an `error` event. |
| Arabic text turns into `?` or garbage | Output not UTF-8 | Python: use `nexus_tool`; PowerShell: set `[Console]::OutputEncoding` to UTF-8. |
| The tool shows "needs a one-time setup" again after an update | Its fingerprint changed (version, install steps, requirements or setup script) | Expected; press Set up. |
| "Stopped after N minutes" | `timeoutMinutes` reached | Raise it, or make the work faster. |

---

# Part 2: For AI coding agents

Follow this procedure exactly when you are asked to add or change a NexusShell tool. It keeps every tool consistent with the
architecture and safe for the user.

## Read first

1. This guide, and the reference card `Agents/NexusShell.Agents.Infrastructure/Agent/tools/README.md`.
2. The transcribe tool, as the model of a complete Python tool: `tool.json`, `SKILL.md`, `scripts/run.py`, `scripts/setup.py`, `scripts/_sada.py`.
3. `Agents/NexusShell.Agents.Infrastructure/ToolPackages/FileToolPackageCatalog.cs` (the rules tool.json must pass) and
   `ToolPackageRuntime.cs` (how scripts are run and installed).
4. `Agents/NexusShell.Agents.Tests/Application/ToolPackagesTests.cs` (the test patterns).
5. `AGENT.md` at the repository root: the project's invariants apply to tools too.

## Procedure

1. **Decide the extension point** with the table in section 2. If the job does not need its own scripts or libraries, say so and
   propose a skill, a connector or a built-in tool instead of a tool package.
2. **Plan the tool and tell the user before building:** name, runtime, risk, parameters, where results go, and for Python tools the
   libraries and the honest download size. Wait for agreement when the setup downloads more than about 100 MB.
3. **Create only files inside the tool folder** (`Agents/NexusShell.Agents.Infrastructure/Agent/tools/<name>/`, or the user tools
   folder when the user asks for a personal tool). Do not change the C# tool system to add a tool.
4. **Write `tool.json`** following section 5. Every parameter has a description; files list extensions; choices list values.
5. **Write the scripts** following section 6: arguments from `NEXUS_TOOL_ARGS`, progress steps, exactly one `result` or one `error`,
   non-zero exit on failure, UTF-8, writes only to the output or data folders. Python scripts use `nexus_tool`.
6. **Pin every Python dependency** and use only reachable hosts (section 7). Put model downloads in `setupScript`, verified by SHA-256.
7. **Write `SKILL.md`** following section 8, with `name` equal to the tool's name.
8. **Add tests** following section 11: at least "the shipped tool is valid"; for PowerShell tools also an end-to-end run and a wrong-input case.
9. **Build and test:**

   ```powershell
   $env:MSBuildEnableWorkloadResolver = 'false'
   dotnet build NexusShell.App/NexusShell.App.csproj
   dotnet test Agents/NexusShell.Agents.Tests/NexusShell.Agents.Tests.csproj
   ```

   In Git Bash use `export MSBuildEnableWorkloadResolver=false` instead of the first line. Close NexusShell before building the app;
   a running copy locks its files.
10. **Run it for real** when it needs no large download: run the script by hand (section 9), then open the Tools window and use it
    with a file. When it needs a large download, ask the user before setting it up.
11. **Update the docs:** one line in the tools section of `Agents/README.md`; a `NOTICE.md` for copied code.
12. **Report** what you built, what you tested and how, what you could not test, and anything still to do. Do not claim a real run
    you did not do.

## Rules

- Do not change the protocol, `tool.json` rules or the runtime to fit one tool. If the tool system truly needs a new feature,
  propose it separately; when you build it, update `FileToolPackageCatalog`, `ToolPackageRuntime`, the tests, this guide and the
  reference card together.
- Do not put large binaries, models, keys, tokens or personal data in the repository or the tool folder.
- Do not mark a tool `read` if it changes the user's files, sends data over the network, or runs other programs.
- Do not download or install anything on the user's computer without saying what and how big, and getting a yes.
- Do not depend on `huggingface.co` or any host that is blocked on the user's network; check reachability first.
- Do not write into the tool folder from scripts; do not rely on the current directory being writable.
- Keep the user's language and accessibility in mind: progress steps and results are spoken by screen readers, so keep them
  short, plain and meaningful.
- Commits: Conventional Commits, authored by the repository owner's configured git identity; follow the project's commit rules.

## Definition of done

- The tool appears in the Tools window with the right title, state and download size.
- The agent picks it when the user asks in plain words, and **Use with a file** works.
- Wrong input is refused with a clear reason; failures explain themselves.
- Nothing is written outside the output and data folders.
- The whole test suite passes, and the build copies the tool next to the program.
- The docs mention the tool.
