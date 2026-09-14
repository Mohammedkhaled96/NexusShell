---
name: NexusShell WPF Agent
description: >-
  USE THIS for ANY work in the NexusShell codebase — a .NET 10 WPF terminal
  emulator built with MVVM + Microsoft.Extensions.Hosting DI. Triggers on:
  "add a service", "new ViewModel", "new manager window", "register in DI",
  "add a command/shortcut", "terminal/ConPTY/pseudo-console", "terminal output /
  WebView2 / xterm.js renderer", "settings/config/settings.json",
  "AI/Groq/HttpClient", "sound effects", "toast notification", "API tester",
  or anything under `NexusShell.App`. If the task touches this repo, FOLLOW THIS FILE.
compatibility: ".NET 10 SDK (Windows Desktop) · TFM net10.0-windows10.0.19041.0 · WPF · WebView2 runtime · Windows 10 1903+ · x64 · v4.1.1"
---

# NexusShell V4.1.1 — Agent Operational Manual & Architectural Blueprint

> **System Version:** NexusShell v4.1.1  
> **Target Framework:** .NET 10 Windows (TFM: `net10.0-windows10.0.19041.0` / C# 12)  
> **Primary Philosophy:** Blind-First Accessibility (Universal Screen-Reader & Audio UX) + High-Performance Low-Latency ConPTY Emulation + AI-Augmented Developer Workflows.

---

## 1. Executive Architectural Overview

NexusShell (`NexusShell.App`) is an enterprise-grade, blind-first Windows terminal emulator and developer environment. It spawns real shells (PowerShell, CMD, WSL, custom agents) through **Win32 ConPTY**, and renders their output as an **accessible HTML command-block stream** inside **WebView2** using a hidden in-memory **xterm.js** parser.

```
                         ┌─────────────────────── Generic Host (App.xaml.cs) ──────────────────────┐
                         │  DI container · Serilog · IConfigManager · IHttpClientFactory("groq")    │
                         └─────────────────────────────────────────────────────────────────────────┘
  input (WPF box) ─► MainWindow ─► MainViewModel ─► TerminalTabViewModel ─► ITerminalSession ─► ConPTY ─► shell.exe
                                                         ▲ raw VT bytes (reader Thread)        │
          WebTerminal/terminal.html  ◄── RawOutputReceived / CommandStarted ◄── TerminalTabViewModel.OnTerminalOutput
          (WebView2: hidden xterm.js → accessible <pre class=command-output> command-blocks; squashes blank runs)

  Side services: AIService ─HTTP─► api.groq.com   ·   ConfigManager ◄─► %AppData%\NexusShell\settings.json
                 SoundService(Polyphonic)        ·   NotificationService(toast)  ·  WindowService(modeless windows)
                 ScreenReaderAnnouncer           ·   PowerShellCompletionService (Out-of-Process Runspace)
```

---

## 2. Universal Terminal Input & ConPTY Streaming Pipeline

### 2.1 ConPTY Process Initialization & Memory Safety
- **Pseudo Console Creation**: Managed in `TerminalSession.cs` via native Win32 calls: `CreatePipe`, `CreatePseudoConsole`, `InitializeProcThreadAttributeList`, and `UpdateProcThreadAttribute` (`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`).
- **Process Creation**: Spawns child shells using `EXTENDED_STARTUPINFO_PRESENT` (`0x00080000`). Child processes attach directly to the Pseudo Console's input/output handles. Parent immediately closes duplicated handles to prevent deadlocks.
- **DLL Hijacking Mitigation**: When starting a shell session in a folder containing `*.dll` files, `TerminalTabViewModel` initializes the host process from `C:\Windows\System32` and switches directory via startup arguments (`Set-Location` or `@cd /d`) to prevent `0xc0000142` DLL initialization errors.
- **Output Streaming**: Dedicated background reader thread `ReadOutputLoop` pulls 4096-byte buffers and decodes them via stateful `Decoder` to ensure multi-byte UTF-8 sequences across chunk boundaries are never corrupted.

### 2.2 Dual Input Transmission Pipeline
NexusShell maintains two distinct input paths:
1. **Standard Command Input (`WriteInput`)**:
   - Converts input text to UTF-8 bytes.
   - Writes payload via Win32 `WriteFile`.
   - Non-blocking `50ms` delay (`WriteInputDelayMs`) prevents race conditions with ConPTY echo buffers.
   - Appends `\r\n` and forces `FlushFileBuffers`.
   - **Privacy Contract**: Never logs raw input text to Serilog; logs only byte counts to protect credentials, environment secrets, and private keys.
2. **Raw Binary Keystroke Forwarding (`WriteRawInput` / `SendRawInput`)**:
   - Sends exact ANSI escape byte sequences directly to the ConPTY input pipe without modifying text or injecting newlines.
   - Used for TUI navigation (e.g., Up: `0x1B, 0x5B, 0x41`, Down: `0x1B, 0x5B, 0x42`, Enter: `0x0D`, Esc: `0x1B`, Tab: `0x09`, Backspace: `0x08`, Ctrl+C: `0x03`).
   - Supports interactive CLI tools (Inquirer.js, `npm init`, `git rebase -i`, `agy`, `claude-code`).

### 2.3 Shift+Enter Multi-Line Input Architecture
- In `MainWindow.xaml` and `MainWindow.xaml.cs`, `CommandInput` is configured for decoupled, calm native typing.
- Plain `Enter` executes `SendCommand`, while `Shift+Enter` inserts an authentic multi-line newline (`\r\n` / `\n`) into the buffer without executing, streaming `\n` to ConPTY when an interactive CLI tool is running.

---

## 3. Blind-First Screen Reader Accessibility Architecture

### 3.1 Live ConPTY Cursor & Item Reflection
- In `terminal.html`, `xterm.js` parses the active 2D terminal grid.
- **Active Cursor Reflection**: Detects the active line containing pointer glyphs (`>`, `●`, `❯`, `→`, `*`) or at the cursor row.
- **TUI Checkbox State Extraction**:
  - Automatically identifies checked states (`[x]`, `[X]`, `[✔]`, `[✓]`, `(*)`, `(•)`, `☑`, `✔`) $\rightarrow$ announces `"{ItemName}، مربع اختيار محدد"`.
  - Automatically identifies unchecked states (`[ ]`, `( )`, `☐`, `○`) $\rightarrow$ announces `"{ItemName}، مربع اختيار غير محدد"`.
  - Single-choice radio/menu items $\rightarrow$ announces `"{ItemName}"` cleanly.
- **Direct High-Speed Announcer**: `ScreenReaderAnnouncer.cs` integrates directly with:
  - **NVDA Controller API**: P/Invoke to `nvdaController_speakText` (0ms direct speech).
  - **JAWS API**: COM reflection to `FreedomSci.JawsApi`.
  - **UI Automation Fallback**: `UIElementAutomationPeer.RaiseNotificationEvent`.

### 3.2 Autonomous Question & Interactive Prompt Detector
- Detects question lines emitted by ConPTY (`/^[\?؟]\s+\S+/`).
- When a new question prompt appears, it is seamlessly combined with the first option to provide complete verbal context on appearance, without repeating during subsequent Up/Down arrow navigation.

---

## 4. Isolated Audio Architecture (Polyphonic Sound Engine)

### 4.1 Polyphonic Concurrency & Thread Isolation
- `SoundService.cs` maintains independent `MediaPlayer` instances for each `AppSound`, allowing sound effects to overlap naturally without audio clipping.
- **KeyType Background Audio**: Preloads `key.wav` in memory and dispatches typing click audio directly to the background thread pool (`ThreadPool.QueueUserWorkItem`), completely bypassing the UI Dispatcher to prevent audio ducking or speech clipping with NVDA/JAWS.

### 4.2 Complete Audio Mapping & Roles
| AppSound | Audio Asset | Scale | Throttle (MinGap) | Semantic Role & Trigger Source |
| :--- | :--- | :--- | :--- | :--- |
| `KeyType` | `key.wav` | 0.55 | 0 ms | Keystroke click during command typing (isolated background thread). |
| `CommandSent` | `send.wav` | 0.90 | 0 ms | User executes a command or submits a query. |
| `CommandCompleted` | `complete.wav`| 1.00 | 0 ms | Command execution finished (dual-path detection). |
| `Output` | `output.wav` | 0.40 | 220 ms | Streaming output arrival tick (throttled). |
| `OpenManager` | `manager.wav` | 0.90 | 0 ms | Opening Aliases, Snippets, SSH, Env, or API Tester. |
| `OpenAI` | `ai.wav` | 0.95 | 0 ms | AI Chat or AI explanation modal launched. |
| `OpenSettings` | `settings.wav`| 0.90 | 0 ms | Opening Settings window. |
| `Toggle` | `toggle.wav` | 0.70 | 30 ms | Dropdowns, suggestion popups, option changes. |
| `CloseWindow` | `close.wav` | 0.85 | 150 ms | Dismissing windows, cancel buttons, escape actions. |
| `Error` | `error.wav` | 0.95 | 250 ms | Terminal crash, network error, or invalid action. |
| `NewTab` | `tab.wav` | 0.85 | 150 ms | Adding tabs, profiles, or new entities. |
| `Notify` | `notify.wav` | 0.95 | 0 ms | Suggestion acceptance or completion alert. |

---

## 5. Authentic Dynamic PowerShell Autocompletion

### 5.1 Out-of-Process Runspace Completion Architecture
- `PowerShellCompletionService.cs` initializes a dedicated background PowerShell `Runspace`.
- Queries `[System.Management.Automation.CommandCompletion]::CompleteInput` asynchronously with thread-safe `SemaphoreSlim` protection.
- Returns rich `CompletionItem` records containing exact replacement boundaries (`ReplacementIndex`, `ReplacementLength`, `ListItemText`, `ToolTip`).
- Suggestion navigation with Up/Down arrows announces positional feedback (e.g. `"{ItemName}، 1 من 12"`).

---

## 6. Codebase Navigation & Key Files

| Folder / File | Purpose |
|---|---|
| `App.xaml(.cs)` | Composition root: Generic Host DI registration + Serilog + global startup. |
| `Commands/RelayCommand.cs` | Type-safe MVVM `ICommand` implementation. |
| `Interfaces/` | `I*` service contracts (DI abstractions). |
| `Services/TerminalSession.cs` | ConPTY process spawner, Win32 pseudo-console, pipe I/O. |
| `Services/ScreenReaderAnnouncer.cs` | High-speed NVDA/JAWS direct speech API and checkbox announcer. |
| `Services/PowerShellCompletionService.cs` | Dedicated PowerShell Runspace completion engine. |
| `Services/SoundService.cs` | Polyphonic UI audio engine with background thread KeyType isolation. |
| `ViewModels/MainViewModel.cs` | Root coordinator, multi-tab manager, suggestion handler. |
| `ViewModels/TerminalTabViewModel.cs` | Per-tab lifecycle, dual-path completion watchdog, raw streaming. |
| `Views/MainWindow.xaml(.cs)` | Primary UI, input box, suggestion popup, keystroke router. |
| `Views/TerminalView.xaml(.cs)` | WebView2 host control and message dispatcher. |
| `WebTerminal/terminal.html` | Hidden xterm.js VT parser + accessible HTML command-block renderer. |
| `WebTerminal/xterm.js` | High-performance in-memory VT sequence resolution library. |
| `Models/AppSettings.cs` | Application configuration data model (`settings.json`). |
| `Audios/` | PCM WAV sound assets (copied to output). |

---

## 7. Strict Developer Invariants & Regression Protections

1. **Zero Unhandled Exceptions**: All audio, clipboard, screen-reader P/Invoke, and shell startup operations must have isolated error boundaries.
2. **Never Break Screen Reader Experience**:
   - Typing in `CommandInput` must be native and quiet; avoid two-way binding echo loops.
   - Arrow keys during interactive sessions (`IsCommandRunning == true`) must stream raw ANSI bytes (`\x1b[A`, `\x1b[B`) directly to ConPTY.
   - History navigation at the idle prompt must announce the loaded command text immediately.
3. **ConPTY & xterm Dimensions Synchronization**:
   - Both `TerminalSession.cs` and `terminal.html` must synchronize on identical column/row dimensions (default: 120 cols x 30 rows).
4. **Dual-Path Command Completion**:
   - Fast-Path: Detects authentic shell prompt in incoming stream when `!HasChildProcesses()`.
   - Watchdog-Path: `700ms` output silence + `LooksLikePrompt(tail)` fallback for long-running scripts.
