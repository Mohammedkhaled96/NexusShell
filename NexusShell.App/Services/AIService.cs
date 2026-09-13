using Microsoft.Extensions.Logging;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NexusShell.App.Services
{
    public class AIService : IAIService
    {
        private const int MaxOutputTruncationChars = 12_000;
        private const int MaxChatHistoryMessages   = 22; // system + 10 pairs
        private const int KeepRecentPairs          = 10;

        private const string GroqEndpoint = "https://api.groq.com/openai/v1/chat/completions";
        private const string DefaultModel  = "llama-3.3-70b-versatile";

        private readonly IConfigManager _configManager;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<AIService> _logger;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_configManager.Load().AI.ApiKey);

        // Persistent command-generation chat (reused across requests for context continuity)
        private List<Dictionary<string, string>>? _commandMessages;
        private string? _commandSystemCtx;

        public AIService(
            IConfigManager configManager,
            IHttpClientFactory httpClientFactory,
            ILogger<AIService> logger)
        {
            _configManager = configManager;
            _httpClientFactory = httpClientFactory;
            _logger = logger;

            var settings = _configManager.Load();
            if (string.IsNullOrWhiteSpace(settings.AI.ApiKey))
            {
                _logger.LogWarning("AI configuration incomplete. AI features disabled.");
            }
            else
            {
                _logger.LogInformation("AI Service initialized.");
            }
        }

        private string GetCurrentModel()
        {
            var settings = _configManager.Load();
            var model = settings.AI.Model;
            return (string.IsNullOrEmpty(model) || model == "auto") ? DefaultModel : model;
        }

        private string GetLanguageInstruction()
        {
            var settings = _configManager.Load();
            return $"IMPORTANT: ALWAYS respond in the following language: {settings.AI.Language}.";
        }

        // (Client construction moved to IHttpClientFactory; resilience handler in DI.)

        // ── Core HTTP call ───────────────────────────────────────────────────────

        private async Task<string> CallGroqAsync(
            List<Dictionary<string, string>> messages,
            int maxTokens,
            double temperature,
            CancellationToken ct)
        {
            var settings = _configManager.Load();
            var apiKey = settings.AI.ApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
                return "AI Service is not configured. Please add your Groq API key in Settings.";

            var model = GetCurrentModel();

            var requestBody = new
            {
                model       = model,
                messages    = messages,
                max_tokens  = maxTokens,
                temperature = temperature
            };

            var json = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, GroqEndpoint) { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                var http = _httpClientFactory.CreateClient("groq");
                var response = await http.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(ct);
                    _logger.LogError("Groq API error {Status}: {Body}", response.StatusCode, err);

                    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        return "Invalid API Key. Please check your Groq API key in Settings.";
                    }

                    return $"API Error {(int)response.StatusCode}: {response.ReasonPhrase}. Check your API key and endpoint in Settings.";
                }

                var responseJson = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(responseJson);
                return doc.RootElement
                          .GetProperty("choices")[0]
                          .GetProperty("message")
                          .GetProperty("content")
                          .GetString() ?? "No response from AI.";
            }
            catch (TaskCanceledException)
            {
                return "Request timed out. The server may be busy — please try again.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calling Groq API.");
                return $"Error: {ex.Message}";
            }
        }

        // ── Verified Command Knowledge Base ─────────────────────────────────────
        private static readonly string _verifiedExamples =
            "VERIFIED COMMAND EXAMPLES (tested and correct - always prefer these over your own knowledge):\n" +
            "\n" +
            "## WiFi / Wireless\n" +
            "Q: list saved WiFi networks\n" +
            "A (PS/CMD): netsh wlan show profiles\n" +
            "\n" +
            "Q: show WiFi password for saved network named 'HomeWifi'\n" +
            "A (PS/CMD): netsh wlan show profile name=\"HomeWifi\" key=clear\n" +
            "\n" +
            "Q: show passwords for all saved WiFi networks\n" +
            "A (PS): netsh wlan show profiles | Select-String 'All User Profile' | ForEach-Object { $n=($_ -split ':')[-1].Trim(); $r=netsh wlan show profile name=$n key=clear | Select-String 'Key Content'; \"$n : $(if($r){($r -split ':')[-1].Trim()}else{'(none)'})\" }\n" +
            "\n" +
            "## Network\n" +
            "Q: show my IP address\n" +
            "A (PS/CMD): ipconfig\n" +
            "\n" +
            "Q: show all network adapter details, MAC address, DNS servers\n" +
            "A (PS/CMD): ipconfig /all\n" +
            "\n" +
            "Q: test internet connection, ping google\n" +
            "A (PS/CMD): ping google.com\n" +
            "\n" +
            "Q: show open network connections and listening ports\n" +
            "A (PS/CMD): netstat -ano\n" +
            "\n" +
            "## Audio / Volume\n" +
            "Q: set system audio volume to 50 percent\n" +
            "A (PS): Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class V { [DllImport(\"winmm.dll\")] public static extern int waveOutSetVolume(IntPtr h, uint v); }'; $p=50; $v=[uint32]($p*65535/100); [V]::waveOutSetVolume([IntPtr]::Zero,($v -bor ($v -shl 16)))\n" +
            "\n" +
            "Q: mute audio / set volume to 0\n" +
            "A (PS): Add-Type -TypeDefinition 'using System.Runtime.InteropServices; public class V { [DllImport(\"winmm.dll\")] public static extern int waveOutSetVolume(IntPtr h, uint v); }'; [V]::waveOutSetVolume([IntPtr]::Zero, 0)\n" +
            "\n" +
            "## System Information\n" +
            "Q: show Windows version and build number\n" +
            "A (PS/CMD): winver\n" +
            "\n" +
            "Q: show detailed system information (RAM, CPU, OS version, uptime)\n" +
            "A (PS/CMD): systeminfo\n" +
            "\n" +
            "Q: check disk space on all drives\n" +
            "A (PS): Get-PSDrive -PSProvider FileSystem | Select-Object Name,@{N='Used(GB)';E={[math]::Round($_.Used/1GB,2)}},@{N='Free(GB)';E={[math]::Round($_.Free/1GB,2)}}\n" +
            "A (CMD): wmic logicaldisk get caption,size,freespace\n" +
            "\n" +
            "Q: show RAM usage\n" +
            "A (PS): Get-WmiObject Win32_OperatingSystem | Select-Object @{N='Total_RAM_GB';E={[math]::Round($_.TotalVisibleMemorySize/1MB,2)}},@{N='Free_RAM_GB';E={[math]::Round($_.FreePhysicalMemory/1MB,2)}}\n" +
            "\n" +
            "## Processes\n" +
            "Q: list all running processes\n" +
            "A (PS): Get-Process\n" +
            "A (CMD): tasklist\n" +
            "\n" +
            "Q: show top processes consuming the most CPU\n" +
            "A (PS): Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 Name,CPU,WorkingSet\n" +
            "\n" +
            "Q: kill process named notepad\n" +
            "A (PS): Stop-Process -Name notepad -Force\n" +
            "A (CMD): taskkill /f /im notepad.exe\n" +
            "\n" +
            "Q: kill process by PID 1234\n" +
            "A (PS): Stop-Process -Id 1234 -Force\n" +
            "A (CMD): taskkill /f /pid 1234\n" +
            "\n" +
            "## Files and Folders\n" +
            "Q: list files in current directory\n" +
            "A (PS): Get-ChildItem\n" +
            "A (CMD): dir\n" +
            "\n" +
            "Q: find all .txt files on C drive\n" +
            "A (PS): Get-ChildItem -Path C:\\ -Filter *.txt -Recurse -ErrorAction SilentlyContinue\n" +
            "\n" +
            "Q: get size of current folder\n" +
            "A (PS): Get-ChildItem -Recurse | Measure-Object -Property Length -Sum | Select-Object @{N='Size_MB';E={[math]::Round($_.Sum/1MB,2)}}\n" +
            "\n" +
            "## Environment and User\n" +
            "Q: list all environment variables\n" +
            "A (PS): Get-ChildItem Env:\n" +
            "A (CMD): set\n" +
            "\n" +
            "Q: show current username and domain\n" +
            "A (PS): $env:USERNAME\n" +
            "A (CMD): echo %USERNAME%\n" +
            "\n" +
            "## Power Management\n" +
            "Q: restart the computer immediately\n" +
            "A (PS): Restart-Computer\n" +
            "A (CMD): shutdown /r /t 0\n" +
            "\n" +
            "Q: shut down the computer immediately\n" +
            "A (PS): Stop-Computer -Force\n" +
            "A (CMD): shutdown /s /t 0\n" +
            "\n" +
            "Q: check laptop battery level and percentage\n" +
            "A (PS): Get-WmiObject Win32_Battery | Select-Object EstimatedChargeRemaining,BatteryStatus\n" +
            "\n" +
            "## Windows Services\n" +
            "Q: list all running Windows services\n" +
            "A (PS): Get-Service | Where-Object {$_.Status -eq 'Running'}\n" +
            "\n" +
            "Q: start Windows Update service\n" +
            "A (PS): Start-Service -Name \"wuauserv\"\n" +
            "\n" +
            "Q: stop Windows Update service\n" +
            "A (PS): Stop-Service -Name \"wuauserv\"\n" +
            "\n" +
            "## Installed Software\n" +
            "Q: list installed applications and programs\n" +
            "A (PS): Get-Package | Select-Object Name,Version | Sort-Object Name\n" +
            "\n" +
            "Q: check recently installed Windows updates and hotfixes\n" +
            "A (PS): Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 10\n" +
            "\n" +
            "## Firewall and Security\n" +
            "Q: check Windows Firewall status\n" +
            "A (PS): Get-NetFirewallProfile | Select-Object Name,Enabled\n" +
            "\n" +
            "Q: list firewall rules\n" +
            "A (PS): Get-NetFirewallRule | Where-Object {$_.Enabled -eq 'True'} | Select-Object DisplayName,Direction,Action | Select-Object -First 20\n";

        // ── Generate Command ─────────────────────────────────────────────────────

        public async Task<string> GetCommandFromNaturalLanguageAsync(
            string userRequest, string systemContext, CancellationToken ct = default)
        {
            if (!IsConfigured) return "AI Service is not configured.";

            var systemMessage =
                "You are an expert Windows shell command assistant embedded inside NexusShell terminal.\n" +
                GetLanguageInstruction() + "\n" +
                "Your ONLY output must be a single raw shell command - no explanations, no markdown, no code fences.\n\n" +
                "SYSTEM CONTEXT OF THE USER MACHINE:\n" +
                systemContext + "\n\n" +
                _verifiedExamples + "\n" +
                "STRICT RULES YOU MUST FOLLOW:\n" +
                "1. Output ONLY the raw command text. No surrounding text, no backticks, no code blocks.\n" +
                "2. ALWAYS check the VERIFIED EXAMPLES above first. If the user request matches, use that exact command.\n" +
                "3. The terminal shell is ALREADY RUNNING. Never launch a new shell process.\n" +
                "   BAD: powershell -Command \"Get-ChildItem\"\n" +
                "   GOOD: Get-ChildItem\n" +
                "4. Use the CORRECT command separator for the active shell:\n" +
                "   - PowerShell: use semicolon [;] to chain steps. NEVER use [&&] in PowerShell.\n" +
                "   - CMD: use [&&] or [&] to chain steps.\n" +
                "   - WSL/bash: use [&&] or [;] to chain steps.\n" +
                "5. ONLY use cmdlets and parameters you are CERTAIN exist in Windows.\n" +
                "6. Do NOT add a trailing semicolon at the end of a single command.\n" +
                "7. Chain all steps on ONE line. Never use newlines inside the command.";

            if (_commandMessages == null || _commandSystemCtx != systemContext)
            {
                _commandMessages = new List<Dictionary<string, string>>
                {
                    new() { ["role"] = "system", ["content"] = systemMessage }
                };
                _commandSystemCtx = systemContext;
            }

            _commandMessages.Add(new() { ["role"] = "user", ["content"] = "Generate the exact shell command for: " + userRequest });

            var reply = await CallGroqAsync(_commandMessages, maxTokens: 256, temperature: 0.1, ct);

            _commandMessages.Add(new() { ["role"] = "assistant", ["content"] = reply });

            // Prune: keep system + last KeepRecentPairs*2 messages
            if (_commandMessages.Count > MaxChatHistoryMessages)
            {
                int removeCount = _commandMessages.Count - MaxChatHistoryMessages;
                _commandMessages.RemoveRange(1, removeCount);
            }

            return CleanGeneratedCommand(reply);
        }

        // ── Explain Output ───────────────────────────────────────────────────────

        public async Task<string> ExplainOutputAsync(string output, CancellationToken ct = default)
        {
            if (!IsConfigured) return "AI Service is not configured.";

            if (output.Length > MaxOutputTruncationChars)
                output = output.Substring(0, MaxOutputTruncationChars) + "\n...[output truncated]";

            var messages = new List<Dictionary<string, string>>
            {
                new()
                {
                    ["role"]    = "system",
                    ["content"] =
                        "You are an expert terminal analyst embedded inside NexusShell professional terminal.\n" +
                        GetLanguageInstruction() + "\n" +
                        "Your task is to provide a thorough, detailed technical explanation of terminal output.\n\n" +
                        "Structure your response using these exact sections (translated to the target language if necessary):\n\n" +
                        "## Overview\n" +
                        "A 2-3 paragraph summary of what happened overall — what commands ran, what they were trying to accomplish, and whether they succeeded.\n\n" +
                        "## Command Analysis\n" +
                        "For each command or operation detected in the output:\n" +
                        "- What the command does and its purpose\n" +
                        "- Key parameters or flags used and their meaning\n" +
                        "- What the output for that command indicates\n\n" +
                        "## Results & Key Data\n" +
                        "A detailed breakdown of all significant data, values, metrics, or information present in the output. " +
                        "Include specific numbers, names, paths, statuses, or any notable values.\n\n" +
                        "## Errors & Warnings\n" +
                        "List any errors, warnings, or anomalies found. For each:\n" +
                        "- What it means\n" +
                        "- Why it likely occurred\n" +
                        "- How to fix or investigate it\n" +
                        "If none, write: No errors or warnings detected.\n\n" +
                        "## Recommended Next Steps\n" +
                        "3-5 specific, actionable follow-up commands or actions the user should consider based on this output.\n\n" +
                        "Be thorough and technical. Write at least 250 words. Always preserve original language of the output."
                },
                new()
                {
                    ["role"]    = "user",
                    ["content"] = "Explain this terminal output in full detail:\n\n" + output
                }
            };

            return await CallGroqAsync(messages, maxTokens: 2048, temperature: 0.4, ct);
        }

        // ── Summarize Output ─────────────────────────────────────────────────────

        public async Task<string> SummarizeOutputAsync(string output, CancellationToken ct = default)
        {
            if (!IsConfigured) return "AI Service is not configured.";

            if (output.Length > MaxOutputTruncationChars)
                output = output.Substring(0, MaxOutputTruncationChars) + "\n...[output truncated]";

            var messages = new List<Dictionary<string, string>>
            {
                new()
                {
                    ["role"]    = "system",
                    ["content"] =
                        "You are an expert terminal analyst embedded inside NexusShell professional terminal.\n" +
                        GetLanguageInstruction() + "\n" +
                        "Produce a comprehensive, well-structured summary of the terminal output.\n\n" +
                        "Structure your response using these exact sections (translated to the target language if necessary):\n\n" +
                        "## Executive Summary\n" +
                        "2-3 paragraphs describing what the output represents, what was accomplished, and the overall status.\n\n" +
                        "## Commands Executed\n" +
                        "A numbered list of every command that was run, with a one-line description of each command's purpose.\n\n" +
                        "## Key Data & Metrics\n" +
                        "Bullet list of all important values, numbers, paths, names, statuses, or metrics found in the output. " +
                        "Include the exact values as shown in the output.\n\n" +
                        "## Status & Health\n" +
                        "Overall assessment: Did everything succeed? Were there failures, warnings, or partial results? " +
                        "Rate the overall health: Healthy / Warning / Critical.\n\n" +
                        "## Notable Observations\n" +
                        "Any patterns, anomalies, interesting findings, or items that warrant attention.\n\n" +
                        "## What This Means For You\n" +
                        "Practical interpretation: What does this output tell the user about their system's state, " +
                        "performance, security, or configuration?\n\n" +
                        "Be comprehensive. Include specific data from the output. Write at least 300 words."
                },
                new()
                {
                    ["role"]    = "user",
                    ["content"] = "Provide a comprehensive summary of this terminal output:\n\n" + output
                }
            };

            return await CallGroqAsync(messages, maxTokens: 2048, temperature: 0.4, ct);
        }

        // ── Clean Output (local — instant, no AI call) ───────────────────────────

        private static readonly Regex _ansiCsi    = new(@"\x1b\[[0-9;?]*[A-Za-z]",              RegexOptions.Compiled);
        private static readonly Regex _ansiOsc    = new(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)",   RegexOptions.Compiled);
        private static readonly Regex _ansiOther  = new(@"\x1b[^[]]?",                           RegexOptions.Compiled);
        private static readonly Regex _ctrlChars  = new(@"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]",   RegexOptions.Compiled);
        private static readonly Regex _promptLine = new(@"[>$%#]\s*$",                            RegexOptions.Compiled);
        private static readonly Regex _boxChars   = new(@"[\u2500-\u257F\u2580-\u259F\u25A0-\u25FF\u2800-\u28FF]", RegexOptions.Compiled);
        private static readonly Regex _multiSpace = new(@"[ \t]{2,}",                             RegexOptions.Compiled);

        public Task<string> CleanOutputAsync(string output, bool all, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(output))
                return Task.FromResult("(no content to clean)");

            var clean = _ansiCsi.Replace(output, "");
            clean = _ansiOsc.Replace(clean, "");
            clean = _ansiOther.Replace(clean, "");
            clean = clean.Replace("\r\n", "\n").Replace("\r", "\n");
            clean = _ctrlChars.Replace(clean, "");
            clean = _boxChars.Replace(clean, " ");
            clean = _multiSpace.Replace(clean, " ");

            var rawLines = clean.Split('\n');
            var deduped  = new List<string>(rawLines.Length);
            string? prev = null;
            foreach (var rawLine in rawLines)
            {
                var line = rawLine.TrimEnd();
                if (line == prev) continue;
                deduped.Add(line);
                prev = line;
            }

            var collapsed  = new List<string>(deduped.Count);
            bool lastBlank = false;
            foreach (var line in deduped)
            {
                bool blank = string.IsNullOrWhiteSpace(line);
                if (blank && lastBlank) continue;
                collapsed.Add(line);
                lastBlank = blank;
            }

            while (collapsed.Count > 0 && string.IsNullOrWhiteSpace(collapsed[0]))
                collapsed.RemoveAt(0);
            while (collapsed.Count > 0 && string.IsNullOrWhiteSpace(collapsed[collapsed.Count - 1]))
                collapsed.RemoveAt(collapsed.Count - 1);

            if (collapsed.Count == 0)
                return Task.FromResult("(no visible content after cleaning)");

            if (!all)
                return Task.FromResult(string.Join("\n", collapsed));

            var sections = new List<string>();
            var cur      = new List<string>();

            foreach (var line in collapsed)
            {
                if (_promptLine.IsMatch(line) && cur.Count > 0)
                {
                    var text = string.Join("\n", cur).Trim();
                    if (!string.IsNullOrWhiteSpace(text)) sections.Add(text);
                    cur.Clear();
                }
                cur.Add(line);
            }
            if (cur.Count > 0)
            {
                var text = string.Join("\n", cur).Trim();
                if (!string.IsNullOrWhiteSpace(text)) sections.Add(text);
            }

            if (sections.Count <= 1)
                return Task.FromResult(string.Join("\n", collapsed));

            var sb = new StringBuilder();
            for (int i = 0; i < sections.Count; i++)
            {
                sb.Append($"Output {i + 1}:\n");
                sb.Append(sections[i]);
                if (i < sections.Count - 1) sb.Append("\n\n");
            }
            return Task.FromResult(sb.ToString());
        }

        // ── Chat ─────────────────────────────────────────────────────────────────

        public async Task<string> SendChatMessageAsync(
            List<Dictionary<string, string>> history, CancellationToken ct = default)
        {
            if (!IsConfigured) return "AI Service is not configured.";
            return await CallGroqAsync(history, maxTokens: 2048, temperature: 0.6, ct);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string CleanGeneratedCommand(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            var cmd = raw;
            cmd = Regex.Replace(cmd, @"```[a-zA-Z]*", "");
            cmd = cmd.Replace("`", "").Trim();

            foreach (var line in cmd.Split('\n'))
            {
                var trimmed = line.Trim().TrimEnd(';', ' ');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    return trimmed;
            }

            return cmd.TrimEnd(';', ' ', '\r', '\n');
        }
    }
}
