# NexusShell V4.1.1 — Project Mental Topology & Architecture Mindmap

```mermaid
mindmap
  root((NexusShell V4.1.1))
    Host & Bootstrap
      App.xaml / App.xaml.cs
        Host Builder Microsoft.Extensions.Hosting
        Serilog Async File & Console Logging
        Global UI Audio Hooks ButtonBase.Click
        Registry & Directory Context Manager
        Quake Hotkey Registration Ctrl+Backtick
    Presentation Layer (WPF / MVVM)
      Views
        MainWindow.xaml / .cs Universal Input & Keystroke Router
        TerminalView.xaml / .cs WebView2 Host Control
        SettingsWindow.xaml / .cs User Configuration Window
        AIChatWindow.xaml / .cs Interactive AI Dialogs
        ApiTesterWindow.xaml / .cs REST Client Studio
        Manager Windows SSH, Snippets, Aliases, Themes, Env
      ViewModels
        MainViewModel.cs Central State & Tab Coordinator
        TerminalTabViewModel.cs Tab Session & Dual Watchdog
        SettingsViewModel.cs Configuration Engine
        AIChatViewModel.cs Groq / Gemini Dialogues
        ApiTesterViewModel.cs REST Client Engine
    Core ConPTY & Streaming Engine
      TerminalSession.cs
        Win32 CreatePseudoConsole
        Win32 CreatePipe & StartupInfoEx
        DLL Hijack Safe Startup System32 Pivot
        WriteInput UTF-8 + 50ms Delay + Flush
        WriteRawInput Binary Key Forwarding
        ReadOutputLoop 4KB Buffer Loop Stateful UTF-8
      WebTerminal (WebView2)
        terminal.html In-Memory xterm.js + HTML Command Blocks
        xterm.js Pure VT ANSI Sequence Resolution
        Real-time Active TUI Announcer & Checkbox State Extractor
        Generic Question / Prompt Header Reflection
    Accessibility Subsystem (Blind-First)
      Screen Reader Engine
        ScreenReaderAnnouncer.cs
          NVDA Controller API P/Invoke (0ms Direct Speech)
          JAWS API COM Automation
          UIElementAutomationPeer Notification Event Fallback
          Localized Checkbox Announcer (محدد / غير محدد)
        Keyboard & Navigation
          Shift+Enter Multi-line Input Buffer
          Decoupled Calm Typing Engine
          History Navigation (Up/Down) with Immediate Verbalization
          F6 Input to Output Focus Switcher
    Audio Subsystem (Polyphonic)
      SoundService.cs
        Dedicated MediaPlayer Instances per AppSound
        KeyType Isolated Background Thread Audio
        Calling Thread Lock-Free Throttling
        Non-blocking Dispatcher Playback
      AppSound Mapping
        KeyType key.wav
        CommandSent send.wav
        CommandCompleted complete.wav
        Output output.wav (220ms Gap Throttle)
        UI Navigation manager, settings, toggle, tab, close
    Completion Services
      PowerShellCompletionService.cs
        Dedicated Background PowerShell Runspace
        [CommandCompletion]::CompleteInput Query
        Rich CompletionItem Bounds & Positional Announcements
```

---

## Subsystem Responsibility Matrix

| Subsystem | Key Files | Architectural Responsibility |
|:---|:---|:---|
| **Host & Bootstrap** | `App.xaml(.cs)` | Generic Host DI, Serilog logging, global unhandled exception boundaries. |
| **ConPTY Engine** | `TerminalSession.cs` | Win32 ConPTY spawn, handle duplication cleanup, raw pipe I/O. |
| **Terminal Renderer** | `terminal.html`, `xterm.js` | Hidden xterm.js ANSI parser, semantic HTML command blocks, RTL bidi support. |
| **Screen Reader Bridge** | `ScreenReaderAnnouncer.cs` | Direct P/Invoke to NVDA / JAWS API, localized checkbox states, prompt reflection. |
| **Input Engine** | `MainWindow.xaml(.cs)` | Shift+Enter multi-line buffer, raw ANSI keystroke streaming, history verbalization. |
| **Audio Engine** | `SoundService.cs` | Polyphonic `MediaPlayer` pool, isolated background thread `KeyType` playback. |
| **Autocompletion** | `PowerShellCompletionService.cs` | Out-of-process PowerShell completion runspace with non-blocking UI popups. |
