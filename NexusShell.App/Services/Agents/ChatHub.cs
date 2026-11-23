namespace NexusShell.App.Services.Agents
{
    /// <summary>
    /// Tells every open chat window that the saved chats changed (another window saved or
    /// renamed one, or a scheduled task wrote its result), so their chat history lists refresh.
    /// </summary>
    public static class ChatHub
    {
        /// <summary>Raised with the sender (a chat window's view model, or the scheduler); may come from any thread.</summary>
        public static event Action<object?>? ChatsChanged;

        public static void RaiseChatsChanged(object? sender) => ChatsChanged?.Invoke(sender);
    }
}
