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
compatibility: ".NET 10 SDK (Windows Desktop) · TFM net10.0-windows10.0.19041.0 · WPF · WebView2 runtime · Windows 10 1903+ · x64 · v4.0.0"
---

# NexusShell — AI Agent Guide

> Single WPF desktop project. There is **no** web API, database/EF, message broker,
> or microservice topology — do not invent them. The sections below map the usual
> backend concepts onto this app's real equivalents.

## 1. Project Overview

NexusShell (`NexusShell.App`) is a Windows terminal host: it spawns real shells
(PowerShell/cmd/WSL) through **Win32 ConPTY**, and renders their output as an
**accessible HTML command-block log** inside a **WebView2** — a **hidden xterm.js**
instance resolves the raw VT stream (cursor moves / `\r` / ANSI all collapse to
clean text), which is shown as `<pre role="log" aria-live>` blocks that NVDA/JAWS
read. It layers on AI assistance (Groq), an HTTP API tester, manager windows
(SSH/alias/snippet/env/shortcut/theme), UI sound effects, and Windows toast
notifications.

**Architecture:** single-process **WPF + MVVM**, wired by the
`Microsoft.Extensions.Hosting` Generic Host (DI + Serilog). Internals are
**service-oriented** (interface in `Interfaces/`, implementation in `Services/`)
and the terminal data path is **event-driven** (plain `event Action<string>`),
with the VT-resolution + duplication-removal done by xterm.js inside the WebView
(there is **no** C# output-filter pipeline — it was removed in v4.0.0).

```
                        ┌─────────────────────── Generic Host (App.xaml.cs) ──────────────────────┐
                        │  DI container · Serilog · IConfigManager · IHttpClientFactory("groq")    │
                        └─────────────────────────────────────────────────────────────────────────┘
 input (WPF box) ─► MainWindow ─► MainViewModel ─► TerminalTabViewModel ─► ITerminalSession ─► ConPTY ─► shell.exe
                                                       ▲ raw VT bytes (reader Thread)        │
        WebTerminal/terminal.html  ◄── RawOutputReceived / CommandStarted ◄── TerminalTabViewModel.OnTerminalOutput
        (WebView2: hidden xterm.js → accessible <pre role=log aria-live> command-blocks; squashes blank runs)

 Side services: AIService ─HTTP─► api.groq.com   ·   ConfigManager ◄─► %AppData%\NexusShell\settings.json
                SoundService(MediaPlayer)        ·   NotificationService(toast)  ·  WindowService(modeless windows)
```

> **Terminal I/O model (v4.0.0):** OUTPUT lives in the WebView2 renderer; INPUT stays
> in the WPF command box (`MainWindow`). `TerminalTabViewModel` forwards the **raw**
> ConPTY stream to the page via `RawOutputReceived`, signals a new block on submit via
> `CommandStarted`, and keeps an ANSI-stripped copy for the AI explain/summarize
> features. The C#↔page bridge is a first-char protocol over WebView2 messages
> (`o`=output, `c`=command-start, `x`=system, `k`=clear, `s`=shell, `d`=prompt-detected-by-js, `i`=interactive-prompt-config-or-result).
>
> **Interactive Prompts (TUI) & Bidi RTL:** TUI detection (e.g. `agy` menus) is now fully implemented
> in JavaScript (`terminal.html`) reading from the clean `xterm.js` buffer. When detected, JS sends `d` to C#,
> C# coordinates state, and sends `i` back to JS to display an accessible HTML `<dialog>` overlay.
> Terminal output lines are wrapped in `<div dir="auto">` allowing the browser to natively handle
> Visual RTL alignment for Arabic/mixed text without breaking logical text for screen readers.

## 2. Codebase Navigation

Project root: `NexusShell.App/` (solution `NexusShell.sln`).

| Folder | Purpose |
|---|---|
| `App.xaml(.cs)` | Composition root: DI registration + startup. **All wiring lives here.** |
| `Commands/` | `RelayCommand` (manual `ICommand`). |
| `Interfaces/` | `I*` service contracts (DI abstractions). |
| `Services/` | Service/Manager implementations (contracts in `Interfaces/`). |
| `ViewModels/` | `*ViewModel : ViewModelBase`. Business/UI logic. |
| `Views/` | `*Window.xaml(.cs)`; `TerminalView` is a **WebView2 host**; `InteractiveScreenView`. Thin code-behind. |
| `WebTerminal/` | xterm.js renderer assets copied to output: `terminal.html` (accessible HTML-block renderer + hidden xterm parser) + `xterm.js`. Served to WebView2 via a virtual host. |
| `Models/` | POCOs (e.g. `AppSettings`, `ShellProfile`, `DetectedPrompt`). |
| `Helpers/` | WPF value converters, `RichTextBoxHelper`. |
| `Audios/` | `*.wav` sound assets (copied to output). |

**Find by role:** "controllers/endpoints" → `Views/*Window` + their `*ViewModel`;
"services" → `Services/` (contract in `Interfaces/`); "repository/DTOs" →
`Services/ConfigManager.cs` + `Models/`; "config" → `Models/AppSettings.cs` +
`settings.json`; **"tests" → none exist yet** (see §9).

**Naming conventions (MUST follow):**
- Interfaces start with `I`: `IConfigManager`, `ISoundService`.
- Services end with `Service` or `Manager` and implement an `I*` interface.
- ViewModels end with `ViewModel` and inherit `ViewModelBase`.
- Windows end with `Window`; embedded controls end with `View`.
- `ICommand` properties end with `Command`; their handlers are `Execute<Name>(object?)`.

## 3. Tech Stack & Dependencies

- **Runtime:** .NET 10, `net10.0-windows10.0.19041.0`, `WinExe`, `Nullable=enable`,
  `ImplicitUsings=enable`, `AllowUnsafeBlocks=true` (ConPTY P/Invoke), `app.manifest`.
- **Key packages** (exact versions from `NexusShell.App.csproj`):

| Package | Ver | Role |
|---|---|---|
| `Microsoft.Web.WebView2` | 1.0.2792.45 | Chromium host for the xterm.js accessible-HTML terminal renderer (`TerminalView`). Requires the evergreen WebView2 runtime. |
| `Microsoft.Extensions.Hosting` | 10.0.8 | Generic Host: DI + lifetime. |
| `CommunityToolkit.Mvvm` | 8.4.2 | `ObservableObject` base; `[ObservableProperty]`/`[RelayCommand]` generators. |
| `Microsoft.Extensions.Http.Resilience` | 10.6.0 | Polly v8 standard pipeline for the `"groq"` client. |
| `Serilog.Extensions.Hosting` (+ Sinks.Async 2.1.0, File 7.0.0, Console 6.1.1) | 10.0.0 | Structured logging to file. |
| `Microsoft.Windows.CsWin32` | 0.3.275 | **Build-time** P/Invoke source-gen (`NativeMethods`). |
| `Microsoft.Toolkit.Uwp.Notifications` | 7.1.3 | Action-Center toasts (`NotificationService`). |
| `Newtonsoft.Json` 13.0.4 / `System.Text.Json` | — | JSON (STJ for settings + Groq; Newtonsoft used by older modules). |
| `Autoupdater.NET.Official` | 1.9.2 | Self-update check. |

- **Shared "kernel"** (no separate library — these are the reuse points): `ViewModelBase`,
  `RelayCommand`, `IConfigManager`/`ConfigManager`, `IWindowService`/`WindowService`,
  `ISoundService`, and the WebView2 renderer (`WebTerminal/terminal.html`).

## 4. Development Patterns (CRITICAL)

### 4.1 Dependency injection registration
**Where:** `App.ConfigureServices(IServiceCollection)` in `App.xaml.cs` — the ONLY
place services are registered.
```csharp
services.AddSingleton<IConfigManager, ConfigManager>();      // shared, stateless-ish
services.AddTransient<ITerminalSession, TerminalSession>();  // per-tab: owns a ConPTY session
services.AddSingleton<MainViewModel>();                      // app-lifetime root VM
services.AddTransient<SettingsWindow>();                     // new instance per open
services.AddHttpClient("groq", c => c.Timeout = TimeSpan.FromSeconds(60))
        .AddStandardResilienceHandler();                     // retries+breaker+timeout
```
**Rule:** stateless/app-wide → `AddSingleton`; anything holding per-tab or per-window
state (`ITerminalSession`, every manager VM/Window) → `AddTransient`.
**Anti-pattern:** `new SomeService(...)` in a VM, or a Singleton that caches mutable
per-tab state (caused real cross-tab bleed — that is why `ITerminalSession` is Transient).

### 4.2 "Endpoint" creation = ViewModel + Window + WindowService
There are no HTTP endpoints; a user-facing feature is a **Window + ViewModel** opened
through `IWindowService`.
```csharp
// WindowService.cs — resolve from DI, never `new` the window directly.
public void ShowThemeManagerWindow()
{
    PlaySound(AppSound.OpenManager);
    var window = _serviceProvider.GetRequiredService<ThemeManagerWindow>();
    window.Owner = Application.Current.MainWindow;
    window.Closed += (_, _) => PlaySound(AppSound.CloseWindow);
    window.Show();
}
```
**Anti-pattern:** constructing windows in a ViewModel — VMs must stay UI-toolkit-free
beyond `ICommand`/`Dispatcher`; window lifecycle belongs to `WindowService`.

### 4.3 Service layer
Contract in `Interfaces/`, impl in `Services/`, constructor-inject `ILogger<T>`.
```csharp
public sealed class SuggestionService : ISuggestionService
{
    private readonly ILogger<SuggestionService> _logger;
    public SuggestionService(ILogger<SuggestionService> logger) => _logger = logger;
}
```
Then register in `ConfigureServices`. **Anti-pattern:** static singletons for stateful
logic (`AIAudioPlayer`/`SecretProtector` are static **only** because they are stateless
helpers).

### 4.4 ViewModel + Command pattern
`ViewModelBase` is `CommunityToolkit.Mvvm.ComponentModel.ObservableObject`.
```csharp
private bool _isBusy;
public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

public ICommand SaveCommand { get; }
// in ctor:
SaveCommand = new RelayCommand(ExecuteSave, _ => SelectedTab != null);
```
`RelayCommand.CanExecuteChanged` is wired to `CommandManager.RequerySuggested`, so WPF
re-queries automatically. **Anti-pattern:** raising `PropertyChanged` from a background
thread without `Dispatcher` marshaling (see §4.9).

### 4.5 "Data access" = `ConfigManager` (JSON, not a DB)
The single source of persisted state is `%AppData%\NexusShell\settings.json` via
`IConfigManager`. **ALWAYS Load-modify-Save** — never start from a blank object.
```csharp
var settings = _configManager.Load();   // preserves Aliases, Snippets, Themes, SSH, ...
settings.FontSize = (int)FontSize;       // mutate only your fields
_configManager.Save(settings);           // atomic temp-file + File.Replace
```
**NEVER** do `_configManager.Save(new AppSettings { FontSize = x })` — it wipes every
field you did not set. Saving is crash-safe (writes `.tmp`, then `File.Replace`); a
corrupt file is backed up to `.bak` and defaults are used.

### 4.6 Error handling & response formatting
- **Services degrade, they don't throw to the UI.** `AIService` returns a friendly
  `string` on HTTP failure; `SoundService`/`AIAudioPlayer` swallow all playback errors.
- **Last-resort UI safety net:** `App.OnDispatcherUnhandledException` logs `Fatal`, shows
  one `MessageBox`, and shuts down. Do not let exceptions escape async UI handlers.
```csharp
if (!response.IsSuccessStatusCode)
{
    _logger.LogError("Groq API error {Status}: {Body}", response.StatusCode, err);
    return "API Error... Check your API key in Settings.";   // user-readable, not an exception
}
```

### 4.7 Validation
Guard clauses + `CanExecute` predicates; no FluentValidation.
`if (string.IsNullOrWhiteSpace(CurrentCommand)) return;` and
`new RelayCommand(ExecuteSend, CanExecuteSend)`.

### 4.8 Logging (Serilog)
Inject `ILogger<T>`; use **structured** templates (named tokens, not interpolation).
```csharp
_logger.LogInformation("Terminal session started. Process ID: {ProcessId}", pid);
```
**NEVER log secrets or raw terminal input** — see `TerminalSession.WriteInput` which
logs only the byte count: `"Writing {ByteCount} bytes of input."`.

### 4.9 Configuration access (no Options pattern)
This app **re-reads** config on demand via `IConfigManager.Load()` rather than binding
`IOptions<T>`. Call `Load()` when you need current values; do not cache `AppSettings`
long-term in a Singleton.

### 4.10 Inter-component communication
Plain **events** (`event Action<string>? OutputReceived;`) and direct DI calls. Cross-
thread results MUST hop to the UI thread:
```csharp
Application.Current?.Dispatcher.BeginInvoke(() => IsCommandRunning = false);
```

### 4.11 Background work
Use `Task.Run` for fire-and-forget init and a dedicated `Thread` for the blocking ConPTY
read loop. The reader raises `OutputReceived`; `TerminalTabViewModel.OnTerminalOutput`
forwards the raw chunk to the WebView (`PostWebMessageAsString`, marshalled to the UI
thread inside `TerminalView`). **Anti-pattern:** blocking the UI thread on shell I/O, or
calling `MediaPlayer` / WebView2 off the `Dispatcher`.

## 5. UI / "API" Conventions
- **Commands, not routes:** expose features as `ICommand` props on a VM; bind from XAML.
- **Accessibility is mandatory:** every actionable control sets
  `AutomationProperties.Name` (menus, buttons, text boxes already do — match this).
- **Mnemonics:** menu headers use `_` access keys (`"_Settings"`); keep them unique per menu.
- **External HTTP** (the only "API" surface) is consumed, not served: always through the
  named `"groq"` `HttpClient` with Bearer auth and `System.Text.Json`.

## 6. Data Rules (`settings.json`)
- **Load-modify-Save** every time (§4.5). The in-memory `AppSettings` holds **plaintext**
  secrets; the file holds DPAPI **ciphertext** (`enc:v1:` prefix) — `ConfigManager` does
  the encrypt-on-save / decrypt-on-load transparently.
- **"Migrations" run inside `ConfigManager.Load()`** — there is no migration tool. To
  evolve the schema, add a guarded fix-up there (e.g. it removes the legacy `Terminal`
  profile and re-points shortcuts). Keep migrations idempotent and non-destructive.
- **Caching:** `OutputHistoryService` / per-tab `StringBuilder` buffers are the only
  caches. `TerminalTabViewModel` keeps two: `_outputHistory` (ANSI-stripped, for the AI
  features) and `_rawHistory` (raw VT, replayed into a recreated WebView). Both are
  size-capped (4–5 MB rolling) — respect the caps.

## 7. Integration Patterns
- **Add an external API:** register a named client in `ConfigureServices`, then build a
  service that injects `IHttpClientFactory`:
```csharp
services.AddHttpClient("myapi", c => c.Timeout = TimeSpan.FromSeconds(30))
        .AddStandardResilienceHandler();          // Polly v8: retry + circuit-breaker + timeout
// ...
var http = _httpClientFactory.CreateClient("myapi");
```
  Model this on `AIService.CallGroqAsync` (auth header, `StringContent`, `JsonDocument`).
- **Message brokers / webhooks:** N/A. The nearest "callback" pattern is
  `InteractivePromptDetector` raising `PromptDetected` → `InteractiveScreenService`.

## 8. Observability
- **Logging is the only telemetry surface.** Serilog writes async to
  `%AppData%\NexusShell\logs\log-<date>.txt` (daily roll), template
  `{Timestamp ...} [{Level:u3}] {Message:lj}{NewLine}{Exception}`. Config is in the
  `App` constructor.
- **What to log:** lifecycle (start/stop, session create/dispose), external call
  failures, recovered exceptions — at `Information`/`Warning`/`Error`. No PII, no secrets.
- **No metrics/Grafana/health endpoints.** "Health" verification for an agent = build is
  clean **and** the launched process is `Responding=True` with a `MainWindowTitle` set,
  plus no `[ERR]/[FTL]` in today's log.

## 9. Testing Requirements
**There is no test project today.** When adding tests:
- Create `NexusShell.App.Tests/` (xUnit) as a sibling project; add it to `NexusShell.sln`.
- Prefer **pure units** that need no WPF Dispatcher: `SuggestionService`, `CommandHistory`,
  `SecretProtector`, `ConfigManager` migrations, `InteractivePromptDetector`.
- Mock collaborators through their `I*` interfaces (Moq or NSubstitute).
```csharp
[Fact]
public void History_navigation_returns_previous_command()
{
    var h = new CommandHistory();
    h.Add("ls"); h.Add("pwd");
    Assert.Equal("pwd", h.GetPrevious());
}
```
- UI-thread code (`SoundService`, WebView2) is hard to unit-test — keep logic in testable
  services and leave Views thin so they need no tests. The VT-resolution that used to live
  in C# filter stages is now done by xterm.js inside `WebTerminal/terminal.html`.

## 10. PR & Code Quality Checklist
- [ ] `dotnet build NexusShell.sln -c Debug` → **0 warnings, 0 errors** (the bar is zero).
- [ ] New service registered in `App.ConfigureServices` with the correct lifetime.
- [ ] `Load`-modify-`Save` used for any settings change (no blank-object saves).
- [ ] No secrets / raw input in logs; structured templates used.
- [ ] All cross-thread UI mutations marshalled via `Dispatcher`.
- [ ] New controls have `AutomationProperties.Name`; menu mnemonics unique.
- [ ] Audio/IO failures swallowed (never crash the app for a non-critical effect).
- [ ] App launches (`Responding=True`, window title set, clean log) — see §12 run note.

## 11. Common Tasks (step-by-step)

**Add a service**
1. Add `IFooService` to `Interfaces/`. 2. Implement `FooService : IFooService` in
`Services/` (inject `ILogger<FooService>`). 3. Register in `ConfigureServices`
(`AddSingleton` if stateless, else `AddTransient`). 4. Constructor-inject where needed.

**Add a manager window (feature)**
1. `Views/FooManagerWindow.xaml(.cs)` + `ViewModels/FooManagerViewModel.cs`.
2. Register both `AddTransient` in DI. 3. Add `ShowFooManagerWindow()` to
`IWindowService`/`WindowService` (DI-resolve, set `Owner`, sounds on open/close).
4. Add a `RelayCommand` on `MainViewModel` calling it. 5. Bind a `MenuItem` in
`MainWindow.xaml` with `AutomationProperties.Name`.

**Add a settings field** (Input: new bool `EnableX`)
1. Add property to `Models/AppSettings.cs` with a default. 2. Surface it in
`SettingsViewModel` (`SetProperty`) + `SettingsWindow.xaml`. 3. In `ExecuteSave`:
`var s = _configManager.Load(); s.EnableX = EnableX; _configManager.Save(s);`
(Output: persisted in `settings.json`, surviving every other field.)

**Add a "migration"**
Add an idempotent guarded fix-up inside `ConfigManager.Load()` after deserialize (model
on the existing profile/shortcut migrations). No CLI, no EF.

**Integrate a new external API** → §7.

**Add a background job**
Wrap blocking work in `Task.Run`; stream results through an `event`; marshal UI updates
with `Dispatcher.BeginInvoke`. Stop/dispose on teardown (model on `TerminalSession`).

**Add a sound effect**
1. Drop `foo.wav` in `Audios/`. 2. Add an `AppSound.Foo` enum value + `Map` entry in
`SoundService` (file, volume scale, min-gap ms). 3. Call `_soundService.Play(AppSound.Foo)`
at the trigger (inject `ISoundService`).

**Add localization**
No framework today (strings are inline, some Arabic comments). To add: introduce `.resx`
+ `ResourceManager` (or a `IStringsService`) and replace literals; do it incrementally.

## 12. Guardrails & Constraints

**NEVER**
- NEVER `Save(new AppSettings{...})` — it erases unowned data. Load-modify-Save only.
- NEVER log secrets, API keys, or raw terminal input (DPAPI-protect at rest via `SecretProtector`).
- NEVER touch `MediaPlayer`, WebView2 (`CoreWebView2`/`PostWebMessageAsString`), or `INotifyPropertyChanged` from a non-UI thread without `Dispatcher`.
- NEVER `new` a service/window in a ViewModel — resolve via DI / `IWindowService`.
- NEVER block the UI thread on ConPTY/HTTP I/O; keep it async or off-thread.
- NEVER show xterm.js itself (a canvas terminal is unreadable to screen readers). xterm.js
  is a HIDDEN parser only; the VISIBLE output is the accessible HTML command-blocks. The
  terminal does NOT host live full-screen TUIs — output is xterm-RESOLVED clean text and
  input is the WPF command box.

**ALWAYS**
- ALWAYS register new services in `App.ConfigureServices` with the right lifetime.
- ALWAYS inject `ILogger<T>` and use structured logging.
- ALWAYS make per-tab / per-window types (`ITerminalSession`, manager VMs/Windows) `Transient` (they hold state).
- ALWAYS marshal background→UI updates through `Application.Current.Dispatcher`.
- ALWAYS keep the build at **0 warnings / 0 errors**.

**Security:** secrets use Windows DPAPI (`CurrentUser` scope) via `SecretProtector`;
`RegistryManager` writes (context-menu/startup) silently no-op without elevation.

**Performance:** output streaming MUST stay non-blocking (raw chunks are posted to the
WebView off the reader thread, marshalled to the `Dispatcher` only for the `PostWebMessage`
call); high-frequency effects (output ticks, key sounds) MUST be throttled (`SoundService`
`MinGapMs`); output buffers (`_outputHistory`/`_rawHistory`) MUST stay size-capped.

**Build & run (agent/CI note):** `app.manifest` sets `requireAdministrator`, so launching
`NexusShell.App.exe` from a non-interactive shell fails on UAC. Build with
`dotnet build NexusShell.App\NexusShell.App.csproj -c Debug`; **launch via the .NET host**
from the output dir — `dotnet NexusShell.App.dll` (runs as-invoker; process name is
`dotnet`). End users double-click the elevated `.exe`.
