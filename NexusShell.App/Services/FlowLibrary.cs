using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Loads/saves the flow library from %AppData%\NexusShell\flows.json. Built-in flows are
    /// seeded on first run and re-added if missing (so upgrades gain new built-ins and built-ins
    /// can't be permanently deleted); user flows persist alongside them.
    /// </summary>
    public sealed class FlowLibrary : IFlowLibrary
    {
        private readonly ILogger<FlowLibrary> _logger;
        private readonly string _path;
        private List<Flow> _flows = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public FlowLibrary(ILogger<FlowLibrary> logger)
        {
            _logger = logger;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusShell");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "flows.json");
            Reload();
        }

        public IReadOnlyList<Flow> GetFlows() => _flows;

        public Flow? GetById(string id) =>
            _flows.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

        public void Reload()
        {
            List<Flow> loaded = new();
            try
            {
                if (File.Exists(_path))
                    loaded = JsonSerializer.Deserialize<List<Flow>>(File.ReadAllText(_path), JsonOpts) ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "flows.json was unreadable; starting from built-in defaults.");
                loaded = new();
            }

            // Re-add any built-in whose Id is missing, so the starter library is always present.
            var byId = new HashSet<string>(loaded.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var builtIn in BuiltInFlows())
            {
                if (!byId.Contains(builtIn.Id))
                    loaded.Add(builtIn);
            }

            _flows = loaded;
            if (!File.Exists(_path)) Save(_flows); // persist the seeded defaults on first run
        }

        public void Save(IEnumerable<Flow> flows)
        {
            _flows = flows.ToList();
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_flows, JsonOpts));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save flows.json.");
            }
        }

        // ── Built-in starter library (commands validated from vendor docs) ──────────
        private const string WingetFlags = " --accept-source-agreements --accept-package-agreements";

        private static List<Flow> BuiltInFlows()
        {
            var nodePrereq = new FlowPrerequisite
            {
                ToolName = "Node.js",
                DetectCommand = "node --version",
                InstallFlowId = "node.install"
            };

            return new List<Flow>
            {
                // ── Node.js (prerequisite for npm-based tools) ──────────────────────
                new Flow
                {
                    Id = "node.install", Tool = "Node.js", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Node.js (LTS)",
                    Description = "Installs the Node.js LTS runtime via winget. Required by npm-based CLIs.",
                    Steps =
                    {
                        new FlowStep
                        {
                            Title = "Install Node.js LTS",
                            DetectCommand = "node --version",
                            PowerShellCommand = "winget install -e --id OpenJS.NodeJS.LTS" + WingetFlags,
                            CmdCommand        = "winget install -e --id OpenJS.NodeJS.LTS" + WingetFlags,
                        }
                    }
                },
                new Flow
                {
                    Id = "node.uninstall", Tool = "Node.js", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Node.js",
                    Steps = { new FlowStep { Title = "Uninstall Node.js", PowerShellCommand = "winget uninstall -e --id OpenJS.NodeJS.LTS", CmdCommand = "winget uninstall -e --id OpenJS.NodeJS.LTS" } }
                },

                // ── Claude Code ─────────────────────────────────────────────────────
                new Flow
                {
                    Id = "claude.install.native", Tool = "Claude Code", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Claude Code (native, no Node)",
                    Description = "Anthropic's recommended native install. After it finishes, run 'claude' once to log in.",
                    Steps =
                    {
                        new FlowStep
                        {
                            Title = "Install Claude Code",
                            DetectCommand = "claude --version",
                            PowerShellCommand = "irm https://claude.ai/install.ps1 | iex",
                            CmdCommand        = "winget install -e --id Anthropic.ClaudeCode" + WingetFlags,
                        }
                    }
                },
                new Flow
                {
                    Id = "claude.install.npm", Tool = "Claude Code", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Claude Code (npm)",
                    Description = "Installs Claude Code through npm. Requires Node.js 18+.",
                    Prerequisites = { nodePrereq },
                    Steps =
                    {
                        new FlowStep
                        {
                            Title = "Install Claude Code via npm",
                            DetectCommand = "claude --version",
                            PowerShellCommand = "npm install -g @anthropic-ai/claude-code",
                            CmdCommand        = "npm install -g @anthropic-ai/claude-code",
                        }
                    }
                },
                new Flow
                {
                    Id = "claude.uninstall", Tool = "Claude Code", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Claude Code",
                    Description = "Removes Claude Code installed via winget and/or npm.",
                    Steps =
                    {
                        new FlowStep { Title = "Remove winget package", ContinueOnError = true, PowerShellCommand = "winget uninstall -e --id Anthropic.ClaudeCode", CmdCommand = "winget uninstall -e --id Anthropic.ClaudeCode" },
                        new FlowStep { Title = "Remove npm package",    ContinueOnError = true, PowerShellCommand = "npm uninstall -g @anthropic-ai/claude-code", CmdCommand = "npm uninstall -g @anthropic-ai/claude-code" },
                    }
                },

                // ── Antigravity CLI (agy) ───────────────────────────────────────────
                new Flow
                {
                    Id = "antigravity.install", Tool = "Antigravity CLI", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Antigravity CLI (agy)",
                    Description = "Installs Google's Antigravity terminal agent (binary 'agy').",
                    Steps =
                    {
                        new FlowStep
                        {
                            Title = "Install Antigravity CLI",
                            DetectCommand = "agy --version",
                            PowerShellCommand = "irm https://antigravity.google/cli/install.ps1 | iex",
                            CmdCommand        = "curl -fsSL https://antigravity.google/cli/install.cmd -o %TEMP%\\agy_install.cmd && %TEMP%\\agy_install.cmd",
                        }
                    }
                },
                new Flow
                {
                    Id = "antigravity.uninstall", Tool = "Antigravity CLI", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Antigravity CLI (agy)",
                    Description = "Antigravity has no CLI uninstaller, so this removes its binary and config folders.",
                    Steps =
                    {
                        new FlowStep
                        {
                            Title = "Remove Antigravity files",
                            ContinueOnError = true,
                            PowerShellCommand = "Remove-Item -Recurse -Force \"$env:LOCALAPPDATA\\agy\" -ErrorAction SilentlyContinue; Remove-Item -Recurse -Force \"$env:APPDATA\\Antigravity\" -ErrorAction SilentlyContinue; Remove-Item -Recurse -Force \"$env:LOCALAPPDATA\\Antigravity\" -ErrorAction SilentlyContinue; Write-Host 'Antigravity files removed.'",
                            CmdCommand        = "rmdir /s /q \"%LOCALAPPDATA%\\agy\" & rmdir /s /q \"%APPDATA%\\Antigravity\" & rmdir /s /q \"%LOCALAPPDATA%\\Antigravity\" & echo Antigravity files removed.",
                        }
                    }
                },

                // ── Ollama ──────────────────────────────────────────────────────────
                new Flow
                {
                    Id = "ollama.install", Tool = "Ollama", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Ollama",
                    Description = "Installs Ollama for running local models. Try 'ollama pull llama3' afterward.",
                    Steps =
                    {
                        new FlowStep
                        {
                            Title = "Install Ollama",
                            DetectCommand = "ollama --version",
                            PowerShellCommand = "winget install -e --id Ollama.Ollama" + WingetFlags,
                            CmdCommand        = "winget install -e --id Ollama.Ollama" + WingetFlags,
                        }
                    }
                },
                new Flow
                {
                    Id = "ollama.uninstall", Tool = "Ollama", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Ollama",
                    Steps = { new FlowStep { Title = "Uninstall Ollama", PowerShellCommand = "winget uninstall -e --id Ollama.Ollama", CmdCommand = "winget uninstall -e --id Ollama.Ollama" } }
                },

                // ── Gemini CLI (npm) ────────────────────────────────────────────────
                new Flow
                {
                    Id = "gemini.install", Tool = "Gemini CLI", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Gemini CLI",
                    Description = "Installs Google's Gemini CLI via npm. Requires Node.js 18+.",
                    Prerequisites = { nodePrereq },
                    Steps = { new FlowStep { Title = "Install Gemini CLI via npm", DetectCommand = "gemini --version", PowerShellCommand = "npm install -g @google/gemini-cli", CmdCommand = "npm install -g @google/gemini-cli" } }
                },
                new Flow
                {
                    Id = "gemini.uninstall", Tool = "Gemini CLI", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Gemini CLI",
                    Steps = { new FlowStep { Title = "Uninstall Gemini CLI", PowerShellCommand = "npm uninstall -g @google/gemini-cli", CmdCommand = "npm uninstall -g @google/gemini-cli" } }
                },

                // ── GitHub CLI (gh) ─────────────────────────────────────────────────
                new Flow
                {
                    Id = "gh.install", Tool = "GitHub CLI", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install GitHub CLI (gh)",
                    Description = "Installs the GitHub command-line tool via winget.",
                    Steps = { new FlowStep { Title = "Install GitHub CLI", DetectCommand = "gh --version", PowerShellCommand = "winget install -e --id GitHub.cli" + WingetFlags, CmdCommand = "winget install -e --id GitHub.cli" + WingetFlags } }
                },
                new Flow
                {
                    Id = "gh.uninstall", Tool = "GitHub CLI", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall GitHub CLI",
                    Steps = { new FlowStep { Title = "Uninstall GitHub CLI", PowerShellCommand = "winget uninstall -e --id GitHub.cli", CmdCommand = "winget uninstall -e --id GitHub.cli" } }
                },

                // ── Git ─────────────────────────────────────────────────────────────
                new Flow
                {
                    Id = "git.install", Tool = "Git", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Git",
                    Description = "Installs Git for Windows via winget.",
                    Steps = { new FlowStep { Title = "Install Git", DetectCommand = "git --version", PowerShellCommand = "winget install -e --id Git.Git" + WingetFlags, CmdCommand = "winget install -e --id Git.Git" + WingetFlags } }
                },
                new Flow
                {
                    Id = "git.uninstall", Tool = "Git", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Git",
                    Steps = { new FlowStep { Title = "Uninstall Git", PowerShellCommand = "winget uninstall -e --id Git.Git", CmdCommand = "winget uninstall -e --id Git.Git" } }
                },

                // ── Python ──────────────────────────────────────────────────────────
                new Flow
                {
                    Id = "python.install", Tool = "Python", Kind = FlowKind.Install, IsBuiltIn = true,
                    Name = "Install Python 3.12",
                    Description = "Installs Python 3.12 via winget.",
                    Steps = { new FlowStep { Title = "Install Python 3.12", DetectCommand = "python --version", PowerShellCommand = "winget install -e --id Python.Python.3.12" + WingetFlags, CmdCommand = "winget install -e --id Python.Python.3.12" + WingetFlags } }
                },
                new Flow
                {
                    Id = "python.uninstall", Tool = "Python", Kind = FlowKind.Uninstall, IsBuiltIn = true,
                    Name = "Uninstall Python 3.12",
                    Steps = { new FlowStep { Title = "Uninstall Python 3.12", PowerShellCommand = "winget uninstall -e --id Python.Python.3.12", CmdCommand = "winget uninstall -e --id Python.Python.3.12" } }
                },

                // ── Status checks ───────────────────────────────────────────────────
                new Flow
                {
                    Id = "status.tools", Tool = "Status", Kind = FlowKind.Status, IsBuiltIn = true,
                    Name = "Check installed tool versions",
                    Description = "Reports the version of each known CLI (or that it's not installed).",
                    Steps =
                    {
                        new FlowStep { Title = "winget",        ContinueOnError = true, PowerShellCommand = "winget --version", CmdCommand = "winget --version" },
                        new FlowStep { Title = "Node.js",       ContinueOnError = true, PowerShellCommand = "node --version",   CmdCommand = "node --version" },
                        new FlowStep { Title = "Claude Code",   ContinueOnError = true, PowerShellCommand = "claude --version", CmdCommand = "claude --version" },
                        new FlowStep { Title = "Antigravity",   ContinueOnError = true, PowerShellCommand = "agy --version",    CmdCommand = "agy --version" },
                        new FlowStep { Title = "Gemini CLI",    ContinueOnError = true, PowerShellCommand = "gemini --version", CmdCommand = "gemini --version" },
                        new FlowStep { Title = "Ollama",        ContinueOnError = true, PowerShellCommand = "ollama --version", CmdCommand = "ollama --version" },
                        new FlowStep { Title = "GitHub CLI",    ContinueOnError = true, PowerShellCommand = "gh --version",     CmdCommand = "gh --version" },
                        new FlowStep { Title = "Git",           ContinueOnError = true, PowerShellCommand = "git --version",    CmdCommand = "git --version" },
                        new FlowStep { Title = "Python",        ContinueOnError = true, PowerShellCommand = "python --version", CmdCommand = "python --version" },
                    }
                },
            };
        }
    }
}
