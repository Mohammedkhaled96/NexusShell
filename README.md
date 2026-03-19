<div align="center">

# ⚡ NexusShell

### The Modern, Accessible, AI-Powered Windows Terminal

*A smarter way to work with PowerShell, CMD, and WSL — all in one beautiful interface.*

[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=flat-square&logo=windows)](https://github.com/Mohammedkhaled96/NexusShell)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)
[![GitHub Issues](https://img.shields.io/github/issues/Mohammedkhaled96/NexusShell?style=flat-square)](https://github.com/Mohammedkhaled96/NexusShell/issues)

</div>

---

## Screenshot

![NexusShell Interface](/NexusShell.png)

---

## What is NexusShell?

NexusShell is a feature-rich, modern GUI terminal for Windows — built from the ground up for **clarity**, **productivity**, and **accessibility**. Unlike traditional terminals that mix input and output in a single cluttered stream, NexusShell gives each its own dedicated space. The result is a distraction-free experience that works beautifully with screen readers, power users, and everyone in between.

Whether you're a sysadmin managing servers, a developer running scripts, or someone just getting started with the command line, NexusShell adapts to how *you* work.

---

## 📥 Installation

### Option 1 — Windows Package Manager (Recommended)

Install instantly with a single command. No setup wizard, no clicking through prompts.

```
winget install MF.MexusShell
```

> **Tip:** Copy the command above and paste it directly into any terminal to install NexusShell.

### Option 2 — Manual Installer

1. **Download** the latest release: [NexusShell.zip](https://github.com/user-attachments/files/22636844/NexusShell.zip)
2. **Extract** the ZIP file to your preferred location
3. **Run** the installer and follow the on-screen steps
   - Optional: create a desktop shortcut
   - Optional: add NexusShell to your system PATH

---

## 🚀 Feature Overview

NexusShell is packed with capabilities designed for real-world workflows. Here's everything it can do.

---

### 🖥️ Core Terminal Experience

**Multi-Shell Profile Support**
Switch between PowerShell, CMD, and WSL from a single interface. Each profile preserves its own settings and launch arguments — no more opening separate windows for different shells.

**Multi-Tab Terminal**
Run multiple independent shell sessions side by side. Open a new tab, switch instantly, and keep your workflows separated without juggling windows.

**Clutter-Free Separated Interface**
Output lives in a dedicated, read-only panel. Your command input stays in its own space below. No more hunting for your cursor in a wall of text.

**Automatic Output Pagination**
Long command outputs are split into navigable pages automatically. Use `Ctrl+Left` / `Ctrl+Right` to page through results at your own pace — no endless scrolling.

**Multi-Line Command Editor**
Compose complex multi-line scripts directly in the input box. Use `Shift+Enter` to insert a new line and `Enter` to execute the entire block.

**Command History Navigation**
Cycle through previously executed commands using `Ctrl+P` (previous) and `Ctrl+N` (next) — just like classic terminals, but smarter.

**Intelligent Autocomplete Suggestions**
As you type, NexusShell suggests matching commands sourced from your PATH, installed executables, and built-in shell commands. Results load lazily so performance stays snappy even on large systems.

**Stop Running Commands**
Interrupt a long-running process at any time with `Ctrl+Shift+S` without closing the session or losing your history.

**One-Click Full Output Copy**
Copy the complete output of your last command to the clipboard in one keystroke (`Ctrl+Shift+C`), regardless of how many pages it spans.

**Clear & Refresh Terminal**
Instantly wipe the output panel with the clear command, or fully refresh the terminal session to reset state.

**Export Command History**
Save your entire session history to a file for auditing, documentation, or reuse — one click, anytime.

**Open Script Files**
Load and execute external PowerShell script files directly through the UI without manually typing paths.

**Output Search**
Search across terminal output with the built-in search panel to quickly find what you need in large logs.

**Administrator Mode Awareness**
NexusShell prominently displays whether it is running with standard or elevated (Administrator) privileges so you always know your permission level.

---

### 🤖 AI Features — Powered by Groq

NexusShell integrates a full AI assistant (NexusAI) directly into your terminal, powered by Groq's ultra-fast inference API. Configure it once in Settings and use AI across your entire workflow.

**NexusAI Chat**
Open a persistent, context-aware chat window and have a real conversation with an AI that understands your terminal environment. Ask questions, get command suggestions, debug errors, and explore solutions — all without leaving NexusShell.

**Explain Output**
Don't understand what a command produced? Ask NexusAI to explain the full session output or just the latest command's result in plain language.

**Summarize Output**
Get a concise summary of lengthy output — perfect for log files, audit results, or long process outputs.

**Clean Output**
Strip noise, ANSI codes, and irrelevant lines from output to get the signal without the clutter.

**Chat About Output**
Open a focused AI chat session pre-loaded with your terminal output as context. Ask targeted questions and get answers that are grounded in what actually happened in your shell.

**AI-Powered Command Generation**
Ask the AI to generate shell commands for tasks you describe in natural language. Commands are returned in formatted code blocks, ready to copy and run.

**Audio Feedback While Waiting**
A subtle audio cue plays while NexusAI processes your request so you always know when the AI is thinking — no need to watch the screen.

**Multilingual AI Responses**
NexusAI responds in your preferred language. Supported languages include:
English, Arabic, French, Spanish, German, Italian, Japanese, and Chinese.

**Selectable AI Models**
Choose the best Groq model for your needs:
- `llama-3.3-70b-versatile` — highest quality, best for complex reasoning
- `llama-3.1-8b-instant` — ultra-fast, great for quick answers
- `mixtral-8x7b-32768` — large context window
- `gemma2-9b-it` — lightweight and efficient
- `auto` — NexusShell picks the best model automatically

> To enable AI features, add your free [Groq API key](https://console.groq.com/keys) in **Settings → AI**.

---

### 📌 Alias Manager

Create your own command shortcuts to eliminate repetitive typing. Define a short trigger word and map it to any full command.

- **Built-in defaults**: `gs` → `git status`, `ll` → `ls -la`
- Add, edit, and delete aliases through the dedicated Alias Manager window
- All aliases persist across sessions

---

### 📋 Snippet Manager

Save frequently used commands as named snippets, organized by category, and insert them into the command box with a click.

- Name, categorize, and store any command or script
- Access your full snippet library from the Snippet Manager
- Ideal for repetitive workflows, deployment scripts, and common queries

---

### 🔐 SSH Manager

Manage your SSH connections from inside NexusShell without memorizing hostnames or copy-pasting credentials.

- Store named SSH connections (host, port, username)
- Connect to any saved server with a single click — the SSH command is automatically composed and executed
- Add and delete connections through the SSH Manager window

---

### 🌍 Environment Variable Editor

View and modify your process environment variables in real time through a clean editor — no registry hacks or command-line guesswork.

- Browse all current environment variables
- Add new variables, edit values, rename keys, or remove entries
- Changes take effect immediately in the active session

---

### 🎨 Theme Manager

Personalize your terminal's look with built-in themes or configure your own colors.

**Built-in themes:**
| Theme | Background | Description |
|-------|-----------|-------------|
| Nexus Dark | `#1E1E1E` | The default — clean, modern, easy on the eyes |
| Matrix | `#000000` | Classic green-on-black hacker aesthetic |
| PowerShell Blue | `#012456` | Faithful to the classic PowerShell look |
| Solarized | `#002B36` | Warm, low-contrast, great for long sessions |

Switch themes instantly — the entire interface updates live with no restart required.

---

### ⌨️ Shortcut Manager

Customize every keyboard shortcut in NexusShell to match your personal workflow.

- View all configured shortcuts in a single list
- Add new shortcuts, edit existing ones, or delete what you don't use
- **Import** shortcuts from a JSON file to share configs across machines
- **Export** your shortcut configuration to back it up or share it with teammates
- **Reset to defaults** at any time with one click

---

### ⚙️ Settings

A comprehensive settings panel with four organized sections:

**General**
- Choose your default shell profile (PowerShell / CMD / WSL)
- Set custom shell launch arguments
- Enable or disable command autocomplete suggestions
- Toggle the shell header display
- Configure auto-update checking
- Set NexusShell to launch on Windows startup

**Appearance**
- Choose font family from all system fonts
- Set font size
- Select output display mode (full-screen or split view)

**Integration**
- Register NexusShell as the default shell handler
- Always-on-top mode — keep NexusShell above all other windows

**AI**
- Enter your Groq API key
- Choose your preferred AI model
- Set your response language

---

### 🔄 Automatic Update Checks

NexusShell checks for new releases automatically on launch (configurable). When an update is available, a dedicated notification window appears with release information — keeping you current without interrupting your work.

---

## 🎯 Getting Started

1. **Install** NexusShell using winget or the manual installer (see above)
2. **Launch** from the Start Menu or desktop shortcut
3. **Select your shell profile** — PowerShell, CMD, or WSL
4. **Type a command** in the input box at the bottom and press `Enter` to run it
5. **Navigate long output** with `Ctrl+Left` / `Ctrl+Right`
6. **Enable AI** by adding your Groq API key in `Settings → AI`

---

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Action |
|----------|--------|
| `Enter` | Execute command |
| `Shift+Enter` | New line in command editor |
| `Ctrl+Shift+C` | Copy entire output to clipboard |
| `Ctrl+Left` | Previous output page |
| `Ctrl+Right` | Next output page |
| `Ctrl+P` | Previous command in history |
| `Ctrl+N` | Next command in history |
| `Ctrl+Shift+S` | Stop running command |
| `Ctrl+A` | Select all text |
| `Ctrl+C` | Copy selected text |
| `Ctrl+V` | Paste from clipboard |
| `Ctrl+Z` | Undo last edit |
| `F1` | Open Help |
| `F2` | Open Settings |

> All shortcuts are fully customizable via the **Shortcut Manager**.

---

## 💡 Example Commands

Try these commands in NexusShell to see its pagination and output handling in action.

```powershell
# System information
systeminfo

# Network configuration
ipconfig /all

# Top 10 processes by CPU
Get-Process | Sort-Object CPU -Descending | Select-Object -First 10

# Available disk space
Get-PSDrive -PSProvider FileSystem

# All installed software
Get-WmiObject -Class Win32_Product | Select-Object Name, Version | Sort-Object Name

# System uptime since last boot
(Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime

# All Windows services by status
Get-Service | Sort-Object Status, Name

# Recent system events (requires Administrator)
Get-EventLog -LogName System -Newest 20
```

---

## ♿ Accessibility

NexusShell was built with accessibility as a first-class concern — not an afterthought.

- **Screen reader compatible** — structured, predictable UI elements that work with NVDA, JAWS, and Narrator
- **Separated input and output** — clear, non-overlapping regions that are easy to navigate by keyboard
- **High-contrast themes** — built-in options for low-vision users
- **Keyboard-first design** — every feature is reachable without a mouse
- **No rapid flashing or scrolling** — pagination prevents overwhelming displays

---

## 🔒 Security

- Administrator mode is always clearly indicated in the UI — no surprises
- Review commands carefully before executing, especially in elevated sessions
- AI features communicate only with Groq's API using your own key — no data is stored by NexusShell
- Never run scripts from untrusted sources

---

## 🤝 Contributing

We welcome pull requests, bug reports, and feature suggestions.

- **Repository**: [github.com/Mohammedkhaled96/NexusShell](https://github.com/Mohammedkhaled96/NexusShell)
- **Issues**: [Report a bug or request a feature](https://github.com/Mohammedkhaled96/NexusShell/issues)
- **Wiki**: [Detailed documentation](https://github.com/Mohammedkhaled96/NexusShell/wiki)

---

## 📄 License

NexusShell is released under the [MIT License](LICENSE). Free to use, modify, and distribute.

---

## 👨‍💻 Developers

| Developer | Role |
|-----------|------|
| **Farid Mohammed** | Core Developer |
| **Mohammed Khaled** | Core Developer |

---

## 🙏 Acknowledgments

- Built with care for the accessibility and developer communities
- Powered by Windows PowerShell, .NET, and WPF
- AI features powered by [Groq](https://groq.com/)
- Special thanks to all contributors, testers, and early adopters

---

<div align="center">

**NexusShell — Making the terminal work for everyone.**

[⭐ Star on GitHub](https://github.com/Mohammedkhaled96/NexusShell) · [🐛 Report Bug](https://github.com/Mohammedkhaled96/NexusShell/issues) · [💬 Get Support](https://github.com/Mohammedkhaled96/NexusShell/issues)

</div>
