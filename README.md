<div align="center">

# ⚡ NexusShell v4.0.0

### The Modern, Accessible, AI-Powered Windows Terminal

*A smarter way to work with PowerShell, CMD, and WSL — all in one beautiful, accessible interface.*

[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=flat-square&logo=windows)](https://github.com/Mohammedkhaled96/NexusShell)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)
[![GitHub Issues](https://img.shields.io/github/issues/Mohammedkhaled96/NexusShell?style=flat-square)](https://github.com/Mohammedkhaled96/NexusShell/issues)

</div>

---

## 🚀 What's New in v4.0.0?

Version 4.0.0 marks a revolutionary leap forward in performance, visual presentation, and accessibility. We have completely rewritten our core output engine to bridge the gap between traditional CLI tools and modern screen-reader interfaces.

---

### 🌐 1. The WebView2 & xterm.js Revolution
We have replaced the traditional text control with an embedded Chromium host (`WebView2`). In the background, a hidden, high-performance `xterm.js` instance processes the raw shell (ConPTY) stream. This resolved text is then rendered into accessible HTML command blocks (`<h3>` headings for commands and `<pre role="log" aria-live="polite">` log regions).

* **Benefit:** Absolute flexibility and ease of navigation for NVDA and JAWS users. Screen readers treat the terminal output as a clean web page, allowing line-by-line reading and heading navigation ('H').

### 🤖 2. Goodbye AI Filters (Zero Duplications)
In previous versions, complex C# text-filtering logic was used to clean up AI outputs, which often resulted in duplicated lines, double-readings, and screen reader stuttering. In v4.0.0, the C# filter pipeline is completely removed. By leveraging `xterm.js` as a background parser, the terminal output is rendered clean, free from ANSI codes, stray backspaces, or double-reads.

### 🎭 3. Fully Accessible Interactive Screen (TUIs)
Command-line interactive prompts (such as selecting models inside `agy /model` or answering prompts) are now fully intercepted. The WebView2 displays a beautiful, keyboard-accessible HTML `<dialog>` overlay. 

* **Navigation:** Focus is automatically trapped inside the dialog. Use **Arrow Keys** to navigate, **Space/Enter** to confirm, and **Escape** to cancel and resume the terminal session.

### ✍️ 4. Intelligent Bilingual & RTL Alignment
Traditional Windows consoles struggle with Right-to-Left (RTL) languages like Arabic, visually reversing words or breaking mixed English/Arabic layouts. NexusShell v4.0.0 implements per-line directionality checking (`dir="auto"`). Arabic and mixed lines are visually aligned to the right and ordered correctly for sighted users, while keeping the logical sequence untouched so screen readers pronounce them perfectly.

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
| `F1` | Open Help |
| `F2 / Alt+S` | Open Settings |
| `F6 / Escape` | Focus Command Input box / Toggle focus between terminal and input |
| `Ctrl + T` | Open new terminal tab |
| `Ctrl + L` | Clear terminal output |
| `Ctrl + Shift + S` | Stop running command |
| `Ctrl + F` | Search terminal output |
| `Arrow Keys` | Navigate options (Interactive Dialog) |
| `Space / Enter` | Select option (Interactive Dialog) |
| `Escape` | Dismiss dialog (Interactive Dialog) |

---

## 👥 Developers

* **Farid Mohammed** (Core Developer)
* **Mohammed Khaled** (Core Developer)

---

<div align="center">

**NexusShell — Making the terminal work for everyone.**

</div>
