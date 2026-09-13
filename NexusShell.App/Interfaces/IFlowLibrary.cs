using System.Collections.Generic;
using NexusShell.App.Models;

namespace NexusShell.App.Interfaces
{
    /// <summary>
    /// Stores and serves the flow library. Built-in defaults are seeded on first load and
    /// re-added if missing; user-added flows are persisted to %AppData%\NexusShell\flows.json.
    /// </summary>
    public interface IFlowLibrary
    {
        IReadOnlyList<Flow> GetFlows();
        Flow? GetById(string id);
        void Save(IEnumerable<Flow> flows);
        void Reload();
    }
}
