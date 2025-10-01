# NexusShell: An Accessible PowerShell Terminal

NexusShell is a modern, user-friendly GUI for Windows PowerShell, meticulously crafted to provide a clear, accessible, and powerful command-line experience. It separates command input from output, eliminating the clutter of traditional terminals and making it perfect for users who rely on screen readers or require a more structured workflow.

## 🚀 Key Features

NexusShell isn't just another terminal. It's packed with features designed for clarity, productivity, and accessibility.

### Core Functionality

* **Clutter-Free Interface** - A dedicated, read-only area for command output and a separate multi-line editor for typing your next command. This clean separation is ideal for focusing on one task at a time.

* **Automatic Output Pagination** - Long command outputs are automatically split into navigable pages. No more losing your place in an endless scroll. You can easily move between pages using intuitive buttons or keyboard shortcuts (`PageUp`/`PageDown`).

* **One-Click Full Copy** - Copy the entire output from your last command to the clipboard with a single click (`Ctrl+Shift+C`), regardless of its length. A success message "Command copied successfully" confirms the action.

* **Advanced Command Editor** - Write and edit complex, multi-line scripts with ease using `Shift+Enter` for new lines. Execute the entire block by pressing `Enter`.

* **Seamless Gemini Integration** - Initiate a special interactive session with Google's Gemini by simply typing `gemini`. NexusShell optimizes the interaction for a smooth, conversational command flow.

* **Administrator Mode Awareness** - The interface clearly indicates whether you are running with standard or elevated (Administrator) privileges.

* **Familiar Keyboard Shortcuts** - The command input field supports standard editing shortcuts:
  - `Ctrl+A` - Select All
  - `Ctrl+C` - Copy
  - `Ctrl+V` - Paste
  - `Ctrl+Z` - Undo

## 📥 Installation

Installing NexusShell is straightforward:

1. **Download** the latest release: [NexusShell.zip](https://github.com/user-attachments/files/22636844/NexusShell.zip)
2. **Extract** the downloaded ZIP file to your preferred location
3. **Run** the installer and follow the on-screen instructions
   - Option to create a desktop shortcut
   - Option to add NexusShell to your system's PATH

## 🎯 Getting Started

1. **Launch** NexusShell from the Start Menu or the desktop shortcut
2. The window will open, displaying the PowerShell prompt and indicating your privilege level
3. **Type** your desired command into the **Command Input** box at the bottom
4. **Press** `Enter` to execute the command
5. If the output is long, pagination controls will appear automatically. Use them to review the output at your own pace

## 💡 Example Commands

Try these powerful PowerShell commands in NexusShell to explore your system. Simply select and copy any command below, then paste it into the Command Input box. NexusShell will display "Command copied successfully" when you copy text from the output area.

### 1. System Information
Display detailed information about your computer's hardware and operating system.

```powershell
systeminfo
```

### 2. Network Configuration
View all network adapter configurations including IP addresses, DNS servers, and MAC addresses.

```powershell
ipconfig /all
```

### 3. Running Processes
List all currently running processes with detailed information, sorted by CPU usage.

```powershell
Get-Process | Sort-Object CPU -Descending | Select-Object -First 10
```

### 4. Disk Space Usage
Check available disk space on all drives.

```powershell
Get-PSDrive -PSProvider FileSystem
```

### 5. Installed Software
List all installed applications on your system.

```powershell
Get-WmiObject -Class Win32_Product | Select-Object Name, Version | Sort-Object Name
```

### 6. System Uptime
Display how long your system has been running since the last restart.

```powershell
(Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
```

### 7. Service Status
View the status of all Windows services.

```powershell
Get-Service | Sort-Object Status, Name
```

### 8. Event Logs
Check recent system events (requires Administrator privileges).

```powershell
Get-EventLog -LogName System -Newest 20
```

## ⌨️ Keyboard Shortcuts Reference

| Shortcut | Action |
|----------|---------|
| `Enter` | Execute command |
| `Shift+Enter` | New line in command editor |
| `Ctrl+Shift+C` | Copy entire output (displays "Command copied successfully") |
| `PageUp` | Previous output page |
| `PageDown` | Next output page |
| `Ctrl+A` | Select all text |
| `Ctrl+C` | Copy selected text |
| `Ctrl+V` | Paste from clipboard |
| `Ctrl+Z` | Undo last edit |

## 🔒 Security Considerations

* NexusShell clearly indicates when running with Administrator privileges
* Always review commands before execution, especially when running as Administrator
* Be cautious with scripts from untrusted sources

## 🤝 Contributing

We welcome contributions! Please visit our [GitHub repository](https://github.com/Mohammedkhaled96/NexusShell) to get involved.

## 📄 License

NexusShell is released under the [MIT License](LICENSE).

## 🆘 Support

* **Issues**: Report bugs or request features via [GitHub Issues](https://github.com/Mohammedkhaled96/NexusShell/issues)
* **Documentation**: Visit our [Wiki](https://github.com/Mohammedkhaled96/NexusShell/wiki) for detailed documentation
* **Repository**: View the source code on [GitHub](https://github.com/Mohammedkhaled96/NexusShell)

## 🙏 Acknowledgments

* Built with love for the accessibility community
* Special thanks to all contributors and testers
* Powered by Windows PowerShell

---

*NexusShell - Making PowerShell accessible to everyone*