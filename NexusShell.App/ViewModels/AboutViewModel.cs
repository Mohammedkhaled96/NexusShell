using System.Reflection;

namespace NexusShell.App.ViewModels
{
    public class AboutViewModel : ViewModelBase
    {
        private string _appVersion;
        public string AppVersion
        {
            get => _appVersion;
            set => SetProperty(ref _appVersion, value);
        }

        public string AboutText => GetAboutText();

        public AboutViewModel()
        {
            _appVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        }

        private string GetAboutText()
        {
            return @"PRODUCT INFORMATION
--------------------------------------------------
Product Name:    NexusShell
Version:         4.1.1
Architecture:    .NET 10 / WPF / Windows ConPTY / WebView2

WHAT'S NEW IN 4.1.1
--------------------------------------------------
- Authentic Terminal Experience (Input & Output): NexusShell now operates
  as a true, standards-compliant terminal host. The Command Input box streams
  raw input directly to PowerShell, CMD, and interactive AI agents/brokers,
  providing an authentic terminal environment paired with clean, accessible rendering.
- Shift + Enter Multi-Line Input: Create and draft multi-line commands
  and scripts directly in the Command Input box before executing.
- Decoupled, Quiet Typing: Removed UI binding echo loops so your screen
  reader only announces your typing cleanly without stuttering.
- Enhanced Screen Navigation: Reliable escape and cancellation handling
  in interactive command-line sessions.
- Dynamic PowerShell Autocompletion: High-speed, out-of-process runspace
  providing authentic parameter and path suggestions.
- Isolated Audio Feedback: Typing sounds run on background threads
  to ensure they never clip or interrupt screen reader speech.

CONTINUED IN 4.1.1
--------------------------------------------------
- Clean, accessible HTML command blocks rendered in WebView2 with xterm.js.
- Bilingual & RTL Arabic layout support with preserved reading order.
- Integrated AI assistance (Groq) and built-in HTTP API Tester.
- Full suite of managers: SSH, aliases, snippets, environment variables,
  shortcuts, and themes.

ABOUT THE PROJECT
--------------------------------------------------
NexusShell is a professional-grade terminal environment engineered for performance, accessibility, and modern workflow integration. It bridges the gap between legacy command-line interfaces and modern, screen-reader-first user experience standards.

DEVELOPMENT TEAM
--------------------------------------------------
Lead Developers:
- Mohammed Khaled
- Farid Mohammed

This software is provided 'as-is', without any express or implied warranty.
";
        }

    }
}