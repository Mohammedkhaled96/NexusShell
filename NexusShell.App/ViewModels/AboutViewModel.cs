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
Version:         4.0.0
Architecture:    .NET 10 / WPF / Windows ConPTY / WebView2

WHAT'S NEW IN 4.0.0
--------------------------------------------------
- Rebuilt Terminal Output Engine: Command output now renders as
  clean, accurate text with no duplication and no leftover control
  characters. A modern terminal engine resolves the raw shell stream
  exactly as a true terminal would.
- Accessible Output by Design: Output is presented as labelled
  command blocks (heading + live region) that NVDA and JAWS read
  naturally — navigate by command, review, and copy with ease.
- Cleaner Reading Experience: Excess blank lines from full-screen
  command-line tools are automatically reduced, while paragraph
  spacing is preserved.
- Smarter Scrolling: New output no longer pulls you away while you
  are reading earlier results; auto-follow resumes at the bottom.
- Streamlined & Modernised: Legacy output-processing code has been
  removed for a faster, simpler, and more reliable application.

CONTINUED IN 4.0.0
--------------------------------------------------
- Interactive Screen: a fully accessible overlay — navigate with the
  arrow keys, confirm with Enter, cancel with Escape (NVDA / JAWS).
- Integrated AI assistance (Groq) and a built-in HTTP API Tester.
- Managers for SSH, aliases, snippets, environment variables,
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