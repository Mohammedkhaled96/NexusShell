using NexusShell.App.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NexusShell.App.Interfaces
{
    public interface IApiTesterService
    {
        Task<ApiResponse> SendRequestAsync(ApiRequest request, ApiEnvironment? environment = null, CancellationToken cancellationToken = default);
        string ResolveVariables(string? input, ApiEnvironment? environment = null);

        // Persistence
        void SaveCollections(IEnumerable<ApiCollection> collections);
        List<ApiCollection> LoadCollections();

        void SaveEnvironments(IEnumerable<ApiEnvironment> environments);
        List<ApiEnvironment> LoadEnvironments();

        void SaveHistory(IEnumerable<ApiHistoryEntry> history);
        List<ApiHistoryEntry> LoadHistory();

        // Import
        ApiCollection ImportPostmanCollection(string json);
        ApiCollection ImportOpenApi(string content);

        // Code generation
        string GenerateCurl(ApiRequest request, ApiEnvironment? environment = null);
        string GenerateCSharp(ApiRequest request, ApiEnvironment? environment = null);
        string GenerateJavaScript(ApiRequest request, ApiEnvironment? environment = null);
        string GeneratePython(ApiRequest request, ApiEnvironment? environment = null);
        string GenerateTypeScript(ApiRequest request, ApiEnvironment? environment = null);
    }
}
