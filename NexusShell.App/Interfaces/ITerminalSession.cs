using System;
namespace NexusShell.App.Interfaces
{
    public interface ITerminalSession : IDisposable
    {
        event Action<string>? OutputReceived;
        event Action<Exception>? UnrecoverableError;
        void Start(string commandLine, string? workingDirectory = null);
        void Resize(int columns, int rows);
        void WriteInput(string input);
        /// <summary>
        /// Sends raw bytes to the child process stdin without appending \\r\\n.
        /// Used for sending arrow-key escape sequences and single keystrokes
        /// to TUI applications.
        /// </summary>
        void WriteRawInput(byte[] data);
        void Refresh();
    }
}
