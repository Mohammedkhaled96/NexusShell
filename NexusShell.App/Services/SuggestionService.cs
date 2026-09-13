using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NexusShell.App.Services
{
    public interface ISuggestionService
    {
        Task InitializeAsync();
        IEnumerable<string> GetSuggestions(string input, int skip = 0, int take = 10);
    }

    public class SuggestionService : ISuggestionService
    {
        private readonly List<string> _commandCache = new List<string>();
        private bool _isInitialized = false;

        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            await Task.Run(() =>
            {
                var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1. Scan PATH environment variable
                var paths = Environment.GetEnvironmentVariable("PATH")
                    ?.Split(Path.PathSeparator)
                    .Where(Directory.Exists);

                if (paths != null)
                {
                    foreach (var path in paths)
                    {
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(path, "*.exe"))
                            {
                                commands.Add(Path.GetFileNameWithoutExtension(file));
                            }
                            // Also .cmd and .bat?
                            foreach (var file in Directory.EnumerateFiles(path, "*.cmd"))
                            {
                                commands.Add(Path.GetFileNameWithoutExtension(file));
                            }
                             foreach (var file in Directory.EnumerateFiles(path, "*.bat"))
                            {
                                commands.Add(Path.GetFileNameWithoutExtension(file));
                            }
                        }
                        catch { }
                    }
                }

                // 2. Add common internal shell commands (basic list for now)
                var commonShellCommands = new[]
                {
                    "cd", "dir", "ls", "cls", "clear", "echo", "type", "copy", "move", "del", "mkdir", "rmdir", 
                    "exit", "help", "gemini", "git", "npm", "dotnet", "docker", "kubectl", "winget", "choco"
                };
                
                foreach(var cmd in commonShellCommands)
                {
                    commands.Add(cmd);
                }

                _commandCache.AddRange(commands.OrderBy(x => x));
                _isInitialized = true;
            });
        }

        public IEnumerable<string> GetSuggestions(string input, int skip = 0, int take = 10)
        {
            if (string.IsNullOrWhiteSpace(input) || !_isInitialized)
            {
                return Enumerable.Empty<string>();
            }

            // Only suggest for the FIRST word (command)
            // If user types "git c", we are not suggesting "commit" yet (scope limitation for V1).
            // We only autocomplete the command executable.
            
            var parts = input.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) return Enumerable.Empty<string>(); 

            string query = parts[0];

            // Filter
            return _commandCache
                .Where(c => c.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .Skip(skip)
                .Take(take); 
        }
    }
}