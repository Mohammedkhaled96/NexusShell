using Microsoft.Extensions.Logging;
using NexusShell.App.Interfaces;
using NexusShell.App.Models;
using System;
using System.IO;
using System.Text.Json;
namespace NexusShell.App.Services
{
    public class ConfigManager : IConfigManager
    {
        private readonly string _configPath;
        private readonly ILogger<ConfigManager> _logger;
        public ConfigManager(ILogger<ConfigManager> logger)
        {
            _logger = logger;
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataPath, "NexusShell");
            Directory.CreateDirectory(appFolder);
            _configPath = Path.Combine(appFolder, "settings.json");
            _logger.LogInformation("Configuration path set to: {ConfigPath}", _configPath);

            // Clean up any stale .tmp file left by a previous crashed save.
            string staleTmp = _configPath + ".tmp";
            if (File.Exists(staleTmp))
            {
                try { File.Delete(staleTmp); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete stale temp file {TmpPath}", staleTmp); }
            }
        }
        public AppSettings Load()
        {
            _logger.LogInformation("Attempting to load settings from {ConfigPath}", _configPath);
            if (File.Exists(_configPath))
            {
                try
                {
                    string json = File.ReadAllText(_configPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        // Ensure Profiles list exists
                        if (settings.Profiles == null) settings.Profiles = new System.Collections.Generic.List<ShellProfile>();

                        // MIGRATION: Remove 'Terminal' profile and ensure PowerShell is default
                        settings.Profiles.RemoveAll(p => p.Name == "Terminal");

                        if (!settings.Profiles.Exists(p => p.Name == "PowerShell"))
                        {
                            settings.Profiles.Insert(0, new ShellProfile { Name = "PowerShell", Command = "powershell.exe", Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass" });
                        }

                        if (string.IsNullOrEmpty(settings.DefaultProfileName) || settings.DefaultProfileName == "Terminal")
                        {
                            settings.DefaultProfileName = "PowerShell";
                        }

                        // MIGRATION: Force update navigation shortcuts to Ctrl+Arrows if they are still PageUp/Down
                        // This handles the user's request to "change navigate" without deleting their whole config
                        var prev = settings.Shortcuts.Find(s => s.CommandName == "PreviousOutputCommand");
                        if (prev != null) { prev.Key = System.Windows.Input.Key.Left; prev.Modifiers = System.Windows.Input.ModifierKeys.Control; }
                        
                        var next = settings.Shortcuts.Find(s => s.CommandName == "NextOutputCommand");
                        if (next != null) { next.Key = System.Windows.Input.Key.Right; next.Modifiers = System.Windows.Input.ModifierKeys.Control; }

                        // MIGRATION: ensure the accessibility "read screen" shortcut exists
                        // and uses Ctrl+Shift+R. Also repair any legacy Insert+B entry — after
                        // the Insert modifier was removed it would otherwise be a bare "B"
                        // binding that swallows the letter B while typing.
                        var readScreen = settings.Shortcuts.Find(s => s.CommandName == "ReadScreenCommand");
                        if (readScreen == null)
                        {
                            settings.Shortcuts.Add(new Shortcut(
                                System.Windows.Input.Key.R,
                                System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift,
                                "ReadScreenCommand"));
                        }
                        else if (readScreen.Modifiers == System.Windows.Input.ModifierKeys.None
                                 && readScreen.Key == System.Windows.Input.Key.B)
                        {
                            readScreen.Key = System.Windows.Input.Key.R;
                            readScreen.Modifiers = System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift;
                        }

                        // Decrypt protected secrets in-memory (file keeps ciphertext)
                        if (settings.AI != null && !string.IsNullOrEmpty(settings.AI.ApiKey)
                            && SecretProtector.IsProtected(settings.AI.ApiKey))
                        {
                            if (SecretProtector.TryUnprotect(settings.AI.ApiKey, out var plain))
                            {
                                settings.AI.ApiKey = plain;
                            }
                            else
                            {
                                _logger.LogWarning("Could not decrypt stored API key (different Windows user or corrupted ciphertext). Clearing.");
                                settings.AI.ApiKey = string.Empty;
                            }
                        }

                        _logger.LogInformation("Settings loaded successfully.");
                        return settings;
                    }
                }
                catch (JsonException ex)
                {
                    string backupPath = _configPath + ".bak";
                    if (File.Exists(_configPath))
                        File.Move(_configPath, backupPath, overwrite: true);
                    _logger.LogError(ex, "settings.json was corrupt. Backed up to .bak, using defaults.");
                }
                catch (IOException ex)
                {
                    _logger.LogError(ex, "Failed to read settings file.");
                    throw; 
                }
            }
            _logger.LogInformation("Settings file not found or corrupt. Creating default settings.");
            var defaultSettings = new AppSettings();
            Save(defaultSettings);
            return defaultSettings;
        }
        public void Save(AppSettings settings)
        {
            // Encrypt-then-revert: serialize ciphertext to disk but leave the
            // in-memory object holding plaintext for subsequent consumers (AIService etc.).
            string? plainApiKey = settings.AI?.ApiKey;
            bool encryptedForSave = !string.IsNullOrEmpty(plainApiKey)
                                    && !SecretProtector.IsProtected(plainApiKey);

            if (encryptedForSave)
            {
                settings.AI!.ApiKey = SecretProtector.Protect(plainApiKey!);
            }

            string tempPath = _configPath + ".tmp";
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(tempPath, json, System.Text.Encoding.UTF8);
                // File.Replace requires the destination to exist; fall back to Move on first save.
                if (File.Exists(_configPath))
                    File.Replace(tempPath, _configPath, null);
                else
                    File.Move(tempPath, _configPath);
                _logger.LogInformation("Settings saved successfully to {ConfigPath}", _configPath);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Failed to save settings to {ConfigPath}", _configPath);
                throw;
            }
            finally
            {
                if (encryptedForSave)
                {
                    settings.AI!.ApiKey = plainApiKey!;
                }
            }
        }
    }
}

