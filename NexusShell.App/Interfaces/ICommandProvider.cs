using System.Collections.Generic;

namespace NexusShell.App.Interfaces
{
    public interface ICommandProvider
    {
        IEnumerable<string> GetAvailableCommands();
    }
}
