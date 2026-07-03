using Microsoft.Win32;
using System;

namespace RemnaVpn.Services
{
    public class AutoStartService : IAutoStartService
    {
        private const string SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "RemnaVpn";

        public void SetAutoStart(bool enable)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKeyPath, true)
                    ?? throw new InvalidOperationException("Registry Run key not found.");

                if (enable)
                {
                    string? processPath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(processPath))
                    {
                        // Add quotes around path to handle spaces safely
                        key.SetValue(AppName, $"\"{processPath}\"", RegistryValueKind.String);
                    }
                }
                else
                {
                    key.DeleteValue(AppName, false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error setting auto-start: {ex}");
            }
        }

        public bool IsAutoStartEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubKeyPath, false);
                if (key != null)
                {
                    object? value = key.GetValue(AppName);
                    if (value is string path)
                    {
                        string? processPath = Environment.ProcessPath;
                        return !string.IsNullOrEmpty(processPath) && path.Contains(processPath);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking auto-start status: {ex}");
            }
            return false;
        }
    }
}
