using NexusShell.App.Models;
using System.Collections.Generic;

namespace NexusShell.App.Interfaces
{
    public interface IWindowService
    {
        void ShowSettingsWindow(string? category = null);
        void CloseSettingsWindow();
        void ShowAboutWindow();
        void ShowHelpWindow();
        void ShowShortcutManagerWindow(System.Action? onClosed = null);
        void ShowSupportWindow();
        void ShowSnippetManagerWindow();
        void ShowEnvEditorWindow();
        void ShowSSHManagerWindow();
        void ShowAliasManagerWindow();
        void ShowThemeManagerWindow();
        void ShowFlowsManagerWindow();
        void ShowAIResponseWindow(string title, string response);
        void ShowAIChatWindow(string initialContext = "", string contextLabel = "");
        void ShowApiTesterWindow();
        Shortcut? ShowAddEditShortcutWindow(List<string> availableCommands, Shortcut? shortcut = null);
        EnvVariable? ShowAddEditEnvWindow(EnvVariable? variable = null);
    }
}