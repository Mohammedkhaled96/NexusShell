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
[![License](https://img.shields.io/badge/License-BSL--1.0-green?style=flat-square)](LICENSE)
[![GitHub Issues](https://img.shields.io/github/issues/Mohammedkhaled96/NexusShell?style=flat-square)](https://github.com/Mohammedkhaled96/NexusShell/issues)

</div>

---

## 🚀 What's New in v4.1.1?

Version 4.1.1 transforms NexusShell into a universal, standards-compliant terminal host with real-time screen-reader accessibility for modern CLI agents, authentic dynamic PowerShell autocompletion, and rock-solid stability.

---

### 🖥️ 1. Universal Terminal Streaming & Interactive CLI Support
The Command Input box now operates as a true, real-time terminal input pipeline. When interactive command-line tools or AI CLI agents (such as `agy`, `claude`, `inquirer`, or `fzf`) are active, keystrokes, navigation keys, and shortcuts stream directly to the ConPTY child process without artificial barriers.

* **Real-Time TUI Speech Feedback:** As you navigate interactive menus, model pickers, slash commands, or checkboxes using the **Up/Down Arrow keys**, NexusShell automatically extracts the active cursor selection and announces it immediately via UI Automation (`LiveRegionChanged`), making full-screen TUI tools 100% accessible to NVDA, JAWS, and Narrator.

### ⚡ 2. Authentic Dynamic PowerShell Autocompletion
Say goodbye to static suggestion lists. NexusShell v4.1.1 features a dedicated out-of-process PowerShell completion engine (`PowerShellCompletionService`) that queries native `CommandCompletion` in real time.

* **Accessible Suggestion Navigation:** Browse cmdlets, parameters, file paths, and variables with **Up/Down Arrows** with clear positional announcements (`"Item, 1 of N"`), and insert completions effortlessly with **Tab** or **Enter** with automatic caret synchronization.

### ⌨️ 3. Dual-Mode Tab Navigation & Multi-Line Drafting
* **Smart `Tab` / `Shift + Tab` Navigation:** Press `Tab` inside the Command Input box to accept an open suggestion, or seamlessly move focus directly to the terminal output area when no suggestions are open. Press `Shift + Tab` from the terminal output to jump right back to the Command Input box.
* **`Shift + Enter` Multi-Line Input:** Draft multi-line scripts and complex commands directly in the Command Input box before pressing `Enter` to execute.
* **Full `Alt` Menu Bar Accessibility:** Pressing `Alt` from anywhere—including directly inside the Command Input box—activates the application menu bar with full vertical and horizontal arrow-key navigation.

### 🔊 4. Isolated Low-Latency Audio & Lock-Free ConPTY Engine
* **Background Audio Threading:** Typing clicks and state chimes execute on isolated background threads so they never clip or interrupt screen reader speech synthesis.
* **Deadlock-Free Pipe I/O:** Eliminated synchronous pipe buffer stalls and hardened cross-thread WebView2 dispatching to guarantee zero UI freezes during heavy CLI output streaming.

---

## 🌟 Core Highlights (from v4.0)

* **WebView2 & xterm.js Engine:** Processes raw ConPTY streams in a hidden `xterm.js` instance and renders clean, navigable HTML command blocks (`<h3>` headings and `<pre role="log">` live regions).
* **Intelligent Bilingual & RTL Alignment:** Automatic per-line directionality (`dir="auto"`) aligns Arabic and mixed English/Arabic text naturally while preserving logical screen-reader reading order.
* **Accessible Interactive Dialogs:** Supports keyboard-trapped modal overlays for structured prompts.

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
| `Shift + Enter` | Insert a new line in the Command Input box |
| `Tab` / `Shift + Tab` | Accept suggestion or toggle focus between Command Input and Terminal Output |
| `F6 / Escape` | Focus Command Input box / Toggle focus between terminal and input |
| `Alt` | Activate the application menu bar from anywhere |
| `Ctrl + Shift + R` | Re-read current terminal screen aloud |
| `F1` | Open Help |
| `F2 / Alt+S` | Open Settings |
| `Ctrl + T` | Open new terminal tab |
| `Ctrl + L` | Clear terminal output |
| `Ctrl + Shift + S` | Stop running command |
| `Ctrl + F` | Search terminal output |
| `Arrow Keys` | Navigate suggestions, interactive TUI menus, or command history |

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
