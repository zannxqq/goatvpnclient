using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RemnaVpn.Models;

namespace RemnaVpn.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly ICryptographyService _cryptographyService;
        private readonly string _filePath;

        public AppSettings Settings { get; private set; } = new();

        public SettingsService(ICryptographyService cryptographyService)
        {
            _cryptographyService = cryptographyService;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "RemnaVpn");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "settings.json");
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    string json = File.ReadAllText(_filePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        Settings = settings;
                        // Decrypt the subscription URL from storage to in-memory plain text
                        if (!string.IsNullOrEmpty(Settings.SubscriptionUrl))
                        {
                            Settings.SubscriptionUrl = _cryptographyService.Decrypt(Settings.SubscriptionUrl);
                        }
                        if (Settings.RoutingProfile == null && !string.IsNullOrEmpty(Settings.SubscriptionRouteJson))
                        {
                            try
                            {
                                Settings.RoutingProfile = JsonSerializer.Deserialize<IncyRoutingProfile>(Settings.SubscriptionRouteJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            }
                            catch { }
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex}");
            }
            Settings = new AppSettings();
        }

        public void Save()
        {
            try
            {
                // Clone settings synchronously on the calling (UI) thread to avoid multi-threaded mutation issues
                var clone = new AppSettings
                {
                    SubscriptionUrl = Settings.SubscriptionUrl,
                    SelectedServerId = Settings.SelectedServerId,
                    AutoStart = Settings.AutoStart,
                    VpnMode = Settings.VpnMode,
                    SplitTunnelingEnabled = Settings.SplitTunnelingEnabled,
                    BypassDomains = new System.Collections.Generic.List<string>(Settings.BypassDomains),
                    BypassProcesses = new System.Collections.Generic.List<string>(Settings.BypassProcesses),
                    KillSwitchEnabled = Settings.KillSwitchEnabled,
                    MuxEnabled = Settings.MuxEnabled,
                    AllowLan = Settings.AllowLan,
                    RemoteDns = Settings.RemoteDns,
                    SubscriptionRouteJson = Settings.SubscriptionRouteJson,
                    SubscriptionDnsJson = Settings.SubscriptionDnsJson,
                    IsFullConfig = Settings.IsFullConfig,
                    FullConfigJson = Settings.FullConfigJson,
                    RoutingProfile = Settings.RoutingProfile,
                    Language = Settings.Language
                };

                // Offload DPAPI encryption, serialization, and file I/O to a background thread to prevent UI lag
                Task.Run(() =>
                {
                    try
                    {
                        clone.SubscriptionUrl = _cryptographyService.Encrypt(clone.SubscriptionUrl);
                        string json = JsonSerializer.Serialize(clone, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(_filePath, json, new System.Text.UTF8Encoding(false));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error writing settings to disk: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cloning settings for save: {ex}");
            }
        }
    }
}
