using System.Collections.Generic;

namespace NexusShell.App.Interfaces
{
    public interface ICommandHistory
    {
        void Add(string command);
        string? GetPrevious();
        string GetNext();
        IEnumerable<string> GetAll();
    }
}
