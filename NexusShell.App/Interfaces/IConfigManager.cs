using NexusShell.App.Models;
namespace NexusShell.App.Interfaces
{
    public interface IConfigManager
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }
}


