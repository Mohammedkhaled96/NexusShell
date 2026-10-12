<div align="center">

# ⚡ NexusShell v4.1.1

### The Modern, Accessible, AI-Powered Windows Terminal

*A smarter way to work with PowerShell, CMD, and WSL — all in one beautiful, accessible interface.*

[![CI](https://github.com/Mohammedkhaled96/NexusShell/actions/workflows/ci.yml/badge.svg)](https://github.com/Mohammedkhaled96/NexusShell/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Mohammedkhaled96/NexusShell/actions/workflows/codeql.yml/badge.svg)](https://github.com/Mohammedkhaled96/NexusShell/actions/workflows/codeql.yml)
[![Security](https://github.com/Mohammedkhaled96/NexusShell/actions/workflows/security.yml/badge.svg)](https://github.com/Mohammedkhaled96/NexusShell/actions/workflows/security.yml)
[![Latest release](https://img.shields.io/github/v/release/Mohammedkhaled96/NexusShell?style=flat-square)](https://github.com/Mohammedkhaled96/NexusShell/releases/latest)

[![Platform](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D6?style=flat-square&logo=windows)](https://github.com/Mohammedkhaled96/NexusShell)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Docker Hub](https://img.shields.io/docker/v/mohammedkhaledmahmoud/nexusshell?style=flat-square&logo=docker&logoColor=white&label=Docker%20Hub&color=2496ED)](https://hub.docker.com/r/mohammedkhaledmahmoud/nexusshell)
[![License](https://img.shields.io/badge/License-BSL--1.0-green?style=flat-square)](LICENSE)
[![GitHub Issues](https://img.shields.io/github/issues/Mohammedkhaled96/NexusShell?style=flat-square)](https://github.com/Mohammedkhaled96/NexusShell/issues)

</div>

---

## 🚀 What's New in v4.1.1?

Version 4.1.1 elevates NexusShell into a true, standards-compliant terminal host—combining real-time ConPTY input streaming for modern AI agents and CLI tools, dynamic PowerShell autocompletion, multi-line command drafting, and isolated background audio.

---

### 🖥️ 1. Authentic Terminal Experience (Input & Output Streaming)
NexusShell now operates as a true, standards-compliant terminal host. The Command Input box streams raw input, navigation keys, and control sequences directly to PowerShell, CMD, and interactive AI agents/brokers (such as `agy`, `claude`, `inquirer`, and `fzf`), providing an authentic terminal environment paired with clean, accessible rendering.

* **Live Interactive CLI & TUI Speech:** As you navigate interactive CLI menus, model pickers, slash commands, or checkboxes using the **Arrow Keys**, NexusShell automatically extracts and announces the active selection in real time for NVDA, JAWS, and Narrator.

### ⚡ 2. Dynamic PowerShell Autocompletion
Powered by a high-speed, out-of-process PowerShell runspace (`PowerShellCompletionService`) that queries native `CommandCompletion` in real time.

* **Accessible Suggestion Navigation:** Browse authentic command, cmdlet, parameter, and file path suggestions using **Up/Down Arrows** with clear positional speech feedback (e.g., `"Item, 1 of 12"`), and press **Tab** or **Enter** to apply or **Escape** to dismiss.

### ✍️ 3. `Shift + Enter` Multi-Line Input & Quiet Decoupled Typing
* **Multi-Line Command Drafting:** Press `Shift + Enter` in the Command Input box to draft and edit multi-line commands, scripts, or queries cleanly before pressing `Enter` to execute.
* **Decoupled, Quiet Typing:** Removed UI binding echo loops so your screen reader announces your typing naturally without stuttering or repetition.
* **Enhanced Session Navigation:** Reliable escape, dismissal, and cancellation handling in interactive command-line sessions.

### 🔊 4. Isolated Audio Feedback
* **Non-Interrupting Sound Engine:** Typing clicks (`KeyType`) and status chimes run on dedicated background threads so they never duck, clip, or interrupt screen reader speech synthesis.

### 🎵 5. Non-Intrusive Sound Effects
Dynamic sound cues play on command execution, output updates, and opening/closing windows. These clicks and chimes are subtle, fast, and volume-scaled, providing valuable spatial awareness without interfering with speech synthesizers.

---

## 📥 Installation

### Option 1 — Windows Package Manager (Recommended)
```powershell
winget install MF.NexusShell
```

### Option 2 — Manual Installer
1. Download **`NexusShell_Setup.exe`** from the [GitHub Releases Page](https://github.com/Mohammedkhaled96/NexusShell/releases).
2. Run the installer to set up the program, create desktop shortcuts, and optionally add it to your system PATH.
3. For silent installations, download **`NexusShell_Silent_Setup.exe`** which installs the app completely in the background.

### Option 3 — Docker

📦 **[`mohammedkhaledmahmoud/nexusshell`](https://hub.docker.com/r/mohammedkhaledmahmoud/nexusshell)** on Docker Hub

> **NexusShell does not run inside the container.** It is a WPF desktop app — it
> drives Win32 ConPTY, hosts Chromium through WebView2, encrypts your key with
> DPAPI and speaks to NVDA and JAWS. A container has none of that. The image
> **carries** the published `win-x64` build with a SHA-256 for every file, so a
> release can be pulled, pinned by digest and verified from any machine. For
> everyday use, Option 1 is simpler.
>
> This is for CI runners, scripted installs, offline mirrors and anyone who
> would rather pin a digest than download a zip.

**Get the build onto this machine** — it lands in `.\out`, then run it normally:

```powershell
docker run --rm -v "${PWD}\out:/out" mohammedkhaledmahmoud/nexusshell:latest
.\out\NexusShell.App.exe
```

```bat
REM cmd.exe
docker run --rm -v "%CD%\out:/out" mohammedkhaledmahmoud/nexusshell:latest
```

```bash
# Linux/macOS — to stage or mirror a release; the binaries need Windows to run
docker run --rm -v "$PWD/out:/out" mohammedkhaledmahmoud/nexusshell:latest
```

**Other commands:**

```powershell
docker run --rm mohammedkhaledmahmoud/nexusshell:latest verify   # re-hash every shipped file
docker run --rm mohammedkhaledmahmoud/nexusshell:latest list     # print SHA256SUMS
docker run --rm mohammedkhaledmahmoud/nexusshell:latest version  # print the version
docker run --rm mohammedkhaledmahmoud/nexusshell:latest help     # usage
```

**Tags:** `latest`, or a release such as `4.1.1`. For anything you intend to keep,
pin the digest instead — a tag can be moved, a digest cannot:

```powershell
docker pull mohammedkhaledmahmoud/nexusshell@sha256:<digest>
```

Check what you extracted at any time, against the `SHA256SUMS` that came with it:

```powershell
Get-Content .\out\SHA256SUMS | ForEach-Object {
    $expected, $relative = $_ -split '\s+', 2
    $actual = (Get-FileHash ".\out\$($relative.TrimStart('./'))" -Algorithm SHA256).Hash.ToLower()
    if ($actual -ne $expected) { "MISMATCH: $relative" }
}
```

Running NexusShell still needs **Windows 10 1903+ (x64)** and the
[WebView2 runtime](https://developer.microsoft.com/microsoft-edge/webview2/). The
build is self-contained, so .NET does not need to be installed. Building and
publishing the image yourself is covered under
[Docker — distribution, not a runtime](#docker--distribution-not-a-runtime).

---

## 🚀 Feature Overview

* **Multi-Shell Support:** Run PowerShell, CMD, and WSL inside a unified interface.
* **Multi-Tab Sessions:** Open multiple independent tabs for different workflows.
* **Integrated AI Assistance (NexusAI):** Generate commands from natural language, explain terminal errors, and chat about your output using Groq's high-speed API.
* **SSH Manager:** Save and connect to remote servers with a single click.
* **Alias & Snippet Managers:** Eliminate repetitive typing with custom shortcuts and commands.
* **Environment Variable Editor:** Edit process variables in real-time.
* **Theme Manager:** Customize colors with instant live previews (Nexus Dark, Matrix, PowerShell Blue, Solarized).
* **Customizable Shortcut Manager:** Change all global hotkeys to match your personal preferences.

---

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Action |
|----------|--------|
| `Enter` | Run command (Terminal) / Send message (AI Chat) |
<<<<<<< HEAD
| `F1` | Open Help |
| `F2 / Alt+S` | Open Settings |
=======
| `Shift + Enter` | Insert new line (Multi-line Terminal & AI Chat input) |
| `F1` | Open Help documentation |
| `F2 / Alt+S` | Open Settings categories |
>>>>>>> c5026e0 (docs(release): align README and release notes with Help and About documentation)
| `F6 / Escape` | Focus Command Input box / Toggle focus between terminal and input |
| `Ctrl + T` | Open new terminal tab |
| `Ctrl + L` | Clear terminal output |
| `Ctrl + Shift + S` | Toggle Execute / Stop command |
| `Ctrl + F` | Search terminal output |
<<<<<<< HEAD
| `Arrow Keys` | Navigate options (Interactive Dialog) |
| `Space / Enter` | Select option (Interactive Dialog) |
| `Escape` | Dismiss dialog (Interactive Dialog) |
=======
| `Arrow Keys` | Navigate options (Interactive Menus & Suggestions) |
| `Space / Enter` | Select & confirm option (Interactive Menus & Suggestions) |
| `Escape` | Cancel / Dismiss (Interactive Menus & Suggestions) |
>>>>>>> c5026e0 (docs(release): align README and release notes with Help and About documentation)

---

## 🛠️ Build from Source

**Requirements:** Windows 10 1903+ (x64), [.NET 10 SDK](https://dotnet.microsoft.com/download), [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).

```powershell
git clone https://github.com/Mohammedkhaled96/NexusShell.git
cd NexusShell
dotnet build NexusShell.sln -c Release
# app.manifest requests elevation; for development run through the .NET host:
dotnet NexusShell.App\bin\Release\net10.0-windows10.0.19041.0\NexusShell.App.dll
```

### One-command environment setup

On a fresh machine, let WinGet install the SDK, WebView2 Runtime and Git for you
(run in an elevated terminal from the repository root):

```powershell
winget configure .\.configurations\configuration.dsc.yaml
```

### Everyone builds the same bits

A desktop app cannot run in a container, so reproducibility is enforced by the
build instead — the same packages and compiler settings on every machine:

| File | What it pins |
|---|---|
| `global.json` | The .NET SDK version, so a different SDK fails fast instead of building differently |
| `NexusShell.App/packages.lock.json` | The full resolved NuGet graph, including transitive packages |
| `Directory.Build.props` | Deterministic builds, warnings as errors, NuGet vulnerability auditing |
| `NuGet.config` | nuget.org only, so a private or local feed cannot alter a restore |

After changing a `PackageReference`, commit the updated `packages.lock.json`
alongside it. CI restores with `--locked-mode` and fails if the two disagree.

### Docker — distribution, not a runtime

Because the app cannot run in a container, the Docker image does not try to run
it. It **carries** the published `win-x64` build with a SHA-256 checksum for every
shipped file, so a release can be pulled and verified by tag or pinned by digest
from any machine that can reach a registry. The published image is
[`mohammedkhaledmahmoud/nexusshell`](https://hub.docker.com/r/mohammedkhaledmahmoud/nexusshell);
see [Option 3 — Docker](#option-3--docker) for how to use it. This section is
about building and publishing it.

**To publish a new version:** bump `<Version>` in `NexusShell.App.csproj`, then
double-click `publish-docker.bat`. It signs in, builds, runs the image test suite
and pushes `X.Y.Z` + `latest` only if every check passes — so the tag can never
disagree with the assembly inside. To rehearse without touching the registry:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docker\publish.ps1 `
    -DockerHubUser mohammedkhaledmahmoud -NoPush
```

| File | Purpose |
|---|---|
| `docker/Dockerfile` | The published image: Alpine carrying the `win-x64` payload plus `SHA256SUMS`. Built in Docker's default Linux mode |
| `docker/Dockerfile.build` | Optional: compiles from source inside a **Windows** container, so the host needs no .NET SDK |
| `docker/entrypoint.sh` | The image's `extract` / `verify` / `list` / `version` commands |
| `docker-compose.yml` | `docker compose run --rm extract` and `… verify`; `builder` sits behind the `build` profile |
| `publish-docker.bat` | Double-click to sign in, build, test and push. Reads the version from the `.csproj` |
| `docker/publish.ps1` | The build-test-push logic. Refuses to push unless every image check passes |
| `docker/DOCKERHUB.md` | The Docker Hub repository description (paste by hand — it is not uploaded with the image) |

WPF cannot be compiled on Linux, so `publish.ps1` checks which engine is active:
in Linux mode the host runs `dotnet publish` and the image carries the result; in
Windows containers mode it builds inside the container. Either way the image ends
up with the same contract — mount a directory, get the build.

### Project layout

| Path | Purpose |
|---|---|
| `NexusShell.App/App.xaml.cs` | Composition root — Generic Host, DI registration, Serilog |
| `NexusShell.App/Services`, `Interfaces` | ConPTY terminal sessions, AI (Groq), config, sounds, notifications |
| `NexusShell.App/ViewModels`, `Views` | MVVM windows and view models |
| `NexusShell.App/WebTerminal` | WebView2 renderer: hidden xterm.js parser → accessible HTML blocks |
| `packaging/winget` | Windows Package Manager manifests |
| `AGENT.md` | Architecture & contribution guide for humans and AI coding agents |

### CI/CD

| Workflow | Runs on | What it does |
|---|---|---|
| **CI** | push, pull request | Restore, build and test on `windows-latest` |
| **CodeQL** | push, pull request, weekly | Security analysis of C# and JavaScript |
| **Security** | push, pull request | TruffleHog secret scan, vulnerable NuGet check, dependency review |
| **Release** | `v*` tag | Self-contained win-x64 build, zip + SHA256 attached to the GitHub release |
| **Publish to WinGet** | release published | Opens a pull request on `microsoft/winget-pkgs` updating `MF.NexusShell` with `NexusShell_Silent_Setup.exe` |

Dependabot keeps NuGet packages and GitHub Actions up to date.

---

## 🔒 Security

- Your AI API key is **encrypted with Windows DPAPI** in `%AppData%\NexusShell\settings.json` — it is never stored in plain text or committed to the repository.
- No telemetry; terminal input and secrets are never logged.
- Report vulnerabilities privately — see [SECURITY.md](SECURITY.md).

---

## 👥 Developers

* **Farid Mohammed** (Core Developer)
* **Mohammed Khaled** (Core Developer)

---

<div align="center">

**NexusShell — Making the terminal work for everyone.**

</div>
