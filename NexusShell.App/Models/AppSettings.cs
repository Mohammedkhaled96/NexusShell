using System.Collections.Generic;
using System.Windows.Input;

namespace NexusShell.App.Models
{
    public class ShellProfile
    {
        public string Name { get; set; } = "Default";
        public string Command { get; set; } = "powershell.exe";
        public string Arguments { get; set; } = "-NoLogo -NoProfile";
        public string IconPath { get; set; } = ""; // Future use
    }

    public class AIConfig
    {
        public string ApiKey { get; set; } = "";
        public string Model { get; set; } = "auto";
        public string Language { get; set; } = "English";

        /// <summary>Speak each NexusAgent step (experts, tools, approvals) through the screen reader while waiting.</summary>
        public bool AnnounceAgentSteps { get; set; } = true;

        // NexusAgent window choices (restored when the chat opens). No secrets here:
        // model references, expert ids, a folder path and capability names only.

        /// <summary>"provider/model" or "auto" (best available model, automatic failover).</summary>
        public string AgentModel { get; set; } = "auto";

        /// <summary>Expert id or "auto" (mixture of experts).</summary>
        public string AgentExpert { get; set; } = "auto";

        /// <summary>Ask, AutoApproveEdits or ReadOnly.</summary>
        public string AgentPermission { get; set; } = "Ask";

        public string AgentWorkingDirectory { get; set; } = "";

        public List<string> AgentDisabledTools { get; set; } = new();

        public List<string> AgentDisabledSkills { get; set; } = new();

        public List<string> AgentDisabledMcpServers { get; set; } = new();

        /// <summary>Folder where chats are saved (one sub-folder per chat). Empty = %AppData%\NexusShell\chats.</summary>
        public string ChatsDirectory { get; set; } = "";
    }

    public class AliasConfig
    {
        public string Trigger { get; set; } = "";
        public string Command { get; set; } = "";
    }

    public class Snippet
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Category { get; set; } = "General";
    }

    public class TerminalTheme
    {
        public string Name { get; set; } = "Default";
        public string Background { get; set; } = "#1E1E1E";
        public string Foreground { get; set; } = "#CCCCCC";
        public string Accent { get; set; } = "#007ACC";
    }

    public class SSHConnection
    {
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string User { get; set; } = "";
    }

    public class AppSettings
    {
        public const string DefaultShellArgs = "-NoLogo -NoProfile";
        public string ShellArgs { get; set; } = DefaultShellArgs;
        
        public List<ShellProfile> Profiles { get; set; } = new List<ShellProfile>
        {
            new ShellProfile { Name = "PowerShell", Command = "powershell.exe", Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass" },
            new ShellProfile { Name = "CMD", Command = "cmd.exe", Arguments = "/K" },
            new ShellProfile { Name = "WSL", Command = "wsl.exe", Arguments = "" }
        };

        public string DefaultProfileName { get; set; } = "PowerShell";

        public AIConfig AI { get; set; } = new AIConfig();

        public List<AliasConfig> Aliases { get; set; } = new List<AliasConfig>
        {
            new AliasConfig { Trigger = "gs", Command = "git status" },
            new AliasConfig { Trigger = "ll", Command = "ls -la" }
        };

        public List<Snippet> Snippets { get; set; } = new List<Snippet>();

        public List<SSHConnection> SSHConnections { get; set; } = new List<SSHConnection>();

        public List<TerminalTheme> Themes { get; set; } = new List<TerminalTheme>
        {
            new TerminalTheme { Name = "Nexus Dark", Background = "#1E1E1E", Foreground = "#CCCCCC", Accent = "#007ACC" },
            new TerminalTheme { Name = "Matrix", Background = "#000000", Foreground = "#00FF00", Accent = "#008F11" },
            new TerminalTheme { Name = "PowerShell Blue", Background = "#012456", Foreground = "#EEEEEE", Accent = "#CCCCCC" },
            new TerminalTheme { Name = "Solarized", Background = "#002B36", Foreground = "#839496", Accent = "#268BD2" }
        };

        public string CurrentThemeName { get; set; } = "Nexus Dark";

        public OutputDisplayMode DisplayMode { get; set; } = OutputDisplayMode.FullScreen;
        public string FontFamily { get; set; } = "Consolas";
        public double FontSize { get; set; } = 18.0;
        public string TextColor { get; set; } = "#CCCCCC";
        public string BackgroundColor { get; set; } = "#1E1E1E";
        
        public List<string> NoiseBlacklist { get; set; } = new List<string>
        {
            "Using: 1 GEMINI.md",
            "esc to cancel",
            @"Queued \(press",
            "Termination Reason",
            "^>$",
            @"^D:\\.*NexusShell"
        };

        public bool AutoCheckUpdates { get; set; } = true;
        public bool EnableSuggestions { get; set; } = true;
        public bool ShowShellHeader { get; set; } = true;
        public bool RunOnStartup { get; set; } = false;
        public bool IsAlwaysOnTop { get; set; } = false;
        public bool QuakeMode { get; set; } = false;
        public bool EnableDirectoryContext { get; set; } = true;
        public bool EnableSoundEffects { get; set; } = true;
        public double SoundEffectsVolume { get; set; } = 0.6;
        public bool EnableVisualEffects { get; set; } = true;
        public System.DateTime NextUpdateCheck { get; set; } = System.DateTime.MinValue;

        public List<Shortcut> Shortcuts { get; set; } = new List<Shortcut>
        {
            new Shortcut(Key.F1, ModifierKeys.None, "ViewHelpCommand"),
            new Shortcut(Key.F2, ModifierKeys.None, "OpenSettingsCommand"),
            new Shortcut(Key.C, ModifierKeys.Control | ModifierKeys.Shift, "CopyOutputCommand"),
            new Shortcut(Key.Left, ModifierKeys.Control, "PreviousOutputCommand"),
            new Shortcut(Key.Right, ModifierKeys.Control, "NextOutputCommand"),
            new Shortcut(Key.P, ModifierKeys.Control, "PreviousCommand"),
            new Shortcut(Key.N, ModifierKeys.Control, "NextCommand"),
            new Shortcut(Key.S, ModifierKeys.Control | ModifierKeys.Shift, "StopCommand"),
            // Accessibility: re-read the interactive screen (or latest output) aloud for
            // screen-reader users. Ctrl+Shift+R avoids the Insert key, which JAWS/NVDA reserve.
            new Shortcut(Key.R, ModifierKeys.Control | ModifierKeys.Shift, "ReadScreenCommand")
        };
    }
}



