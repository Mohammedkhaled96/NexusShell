namespace NexusShell.App.Interfaces
{
    public interface IOutputHistoryService
    {
        void Add(string output);
        string GetPrevious();
        string GetNext();
        void Clear();
    }
}


