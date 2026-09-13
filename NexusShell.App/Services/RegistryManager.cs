using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace NexusShell.App.Services
{
    public interface IRegistryManager
    {
        void RegisterAsHandler();
        void UnregisterAsHandler();
        bool IsRegistered();
        
        void RegisterDirectoryContext();
        void UnregisterDirectoryContext();
        bool IsDirectoryContextRegistered();

        void SetStartup(bool enable);
    }

    public class RegistryManager : IRegistryManager
    {
        private const string AppId = "NexusShell.App";
        private const string AppName = "NexusShell";
        private readonly string _executablePath;

        public RegistryManager()
        {
            // Use Process.GetCurrentProcess().MainModule.FileName for the actual .exe path
            _executablePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        }

        public void RegisterAsHandler()
        {
            if (string.IsNullOrEmpty(_executablePath)) return;

            try
            {
                // Register application in HKCU\Software\Classes\Applications
                using (var appKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{Path.GetFileName(_executablePath)}"))
                {
                    appKey.SetValue("", AppName);
                    appKey.SetValue("FriendlyAppName", AppName);

                    using (var shellKey = appKey.CreateSubKey(@"shell\open\command"))
                    {
                        shellKey.SetValue("", $"\"{_executablePath}\" \"%1\"");
                    }
                }

                // Register file associations for .bat and .cmd
                RegisterExtension(".bat");
                RegisterExtension(".cmd");
            }
            catch (Exception ex)
            {
                // Handle or log error
                throw new InvalidOperationException("Failed to register application keys.", ex);
            }
        }

        public void RegisterDirectoryContext()
        {
            if (string.IsNullOrEmpty(_executablePath)) return;

            try
            {
                // Register for Directory (when clicking ON a folder)
                RegisterShellContextMenu(@"Directory\shell\NexusShell", "Open in NexusShell");
                
                // Register for Directory Background (when clicking INSIDE a folder)
                RegisterShellContextMenu(@"Directory\Background\shell\NexusShell", "Open in NexusShell");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to register directory context menu.", ex);
            }
        }

        private void RegisterShellContextMenu(string keyPath, string menuText)
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{keyPath}"))
            {
                key.SetValue("", menuText);
                key.SetValue("Icon", _executablePath);

                using (var commandKey = key.CreateSubKey("command"))
                {
                    // Use double quotes for executable and single quotes around argument placeholders
                    // Windows will substitute %V or %1 with the path.
                    string arg = keyPath.Contains("Background") ? "%V" : "%1";
                    commandKey.SetValue("", $"\"{_executablePath}\" \"{arg}\"");
                }
            }
        }

        private void RegisterExtension(string extension)
        {
            // Note: This registers in the "OpenWithList" for the extension in HKCU
            // This is safer and doesn't require Admin rights usually.
            string keyPath = $@"Software\Classes\{extension}\OpenWithList\{Path.GetFileName(_executablePath)}";
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                // Just creating the key is often enough for it to appear in "Other programs"
                key.SetValue("", ""); 
            }
        }

        public void UnregisterAsHandler()
        {
            try
            {
                // Remove application registration
                Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\Applications\{Path.GetFileName(_executablePath)}", false);

                // Remove from OpenWithList
                UnregisterExtension(".bat");
                UnregisterExtension(".cmd");
            }
            catch (Exception)
            {
                // Ignore errors during cleanup
            }
        }

        public void UnregisterDirectoryContext()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\shell\NexusShell", false);
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\NexusShell", false);
            }
            catch { }
        }

        private void UnregisterExtension(string extension)
        {
            string keyPath = $@"Software\Classes\{extension}\OpenWithList\{Path.GetFileName(_executablePath)}";
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        }

        public bool IsRegistered()
        {
            if (string.IsNullOrEmpty(_executablePath)) return false;
            
            var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\Applications\{Path.GetFileName(_executablePath)}");
            return key != null;
        }

        public bool IsDirectoryContextRegistered()
        {
            if (string.IsNullOrEmpty(_executablePath)) return false;

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\NexusShell\command"))
                {
                    if (key == null) return false;
                    
                    string? registeredCommand = key.GetValue("") as string;
                    if (string.IsNullOrEmpty(registeredCommand)) return false;

                    // Verify if the registered path matches our current executable
                    return registeredCommand.Contains(_executablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public void SetStartup(bool enable)
        {
            if (string.IsNullOrEmpty(_executablePath)) return;
            
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (enable)
                    {
                        key?.SetValue(AppName, $"\"{_executablePath}\"");
                    }
                    else
                    {
                        key?.DeleteValue(AppName, false);
                    }
                }
            }
            catch (Exception)
            {
                // Handle or log error
            }
        }
    }
}