using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace RemnaVpn.Services
{
    public class SystemProxyService : ISystemProxyService
    {
        private const string SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        public void EnableProxy(string host, int port)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKeyPath, true) 
                    ?? throw new InvalidOperationException("Registry key for Internet Settings not found.");

                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
                
                RefreshSystemSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error enabling system proxy: {ex}");
            }
        }

        public void DisableProxy()
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(SubKeyPath, true)
                    ?? throw new InvalidOperationException("Registry key for Internet Settings not found.");

                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                RefreshSystemSettings();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disabling system proxy: {ex}");
            }
        }

        public bool IsProxyEnabled()
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SubKeyPath, false);
                if (key != null)
                {
                    object? value = key.GetValue("ProxyEnable");
                    if (value is int intVal)
                    {
                        return intVal == 1;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking proxy status: {ex}");
            }
            return false;
        }

        private static void RefreshSystemSettings()
        {
            // Flush the changes to the system
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
    }
}
