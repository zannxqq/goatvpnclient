using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using RemnaVpn.Helpers;
using RemnaVpn.Models;
using RemnaVpn.Services;

namespace RemnaVpn.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly IXrayService _xrayService;
        private readonly ISettingsService _settingsService;
        private readonly IRemnawaveService _remnawaveService;
        private readonly IAutoStartService _autoStartService;
        private readonly ILocalizationService? _localizationService;
        private readonly ICryptographyService _cryptographyService;
        private readonly SemaphoreSlim _pingSemaphore = new(8, 8);
        private readonly Queue<string> _logLines = new();

        [ObservableProperty]
        private ObservableCollection<Server> _servers = new();

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
        private Server? _selectedServer;

        partial void OnSelectedServerChanged(Server? value)
        {
            if (value != null && _settingsService?.Settings != null)
            {
                _settingsService.Settings.SelectedServerId = value.Id;
                _settingsService.Settings.SelectedServerName = value.Name;
                _settingsService.Save();
            }
        }

        [ObservableProperty]
        private string _status = "Disconnected";

        [ObservableProperty]
        private string _logs = string.Empty;

        [ObservableProperty]
        private bool _isCoreDownloading;

        [ObservableProperty]
        private double _coreDownloadProgress;

        [ObservableProperty]
        private string _coreDownloadStatusText = string.Empty;

        // Onboarding / Subscription fields
        [ObservableProperty]
        private bool _isSubscriptionConfigured;

        [ObservableProperty]
        private string _subscriptionInputUrl = string.Empty;

        [ObservableProperty]
        private string _subscriptionStatusMessage = string.Empty;

        [ObservableProperty]
        private bool _isSubscriptionImporting;

        [ObservableProperty]
        private SubscriptionInfo _subscriptionInfo = new();

        // Settings fields
        [ObservableProperty]
        private bool _autoStart;

        [ObservableProperty]
        private string _vpnMode = "SystemProxy";

        [ObservableProperty]
        private bool _splitTunnelingEnabled;

        [ObservableProperty]
        private bool _killSwitchEnabled;

        [ObservableProperty]
        private bool _loggingEnabled = true;

        [ObservableProperty]
        private string _bypassDomainsText = string.Empty;

        [ObservableProperty]
        private string _bypassProcessesText = string.Empty;

        [ObservableProperty]
        private bool _showSettingsDrawer;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsConnectionPage))]
        [NotifyPropertyChangedFor(nameof(IsServersPage))]
        [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
        [NotifyPropertyChangedFor(nameof(IsLogsPage))]
        private string _currentPage = "Connection";

        public bool IsConnectionPage => CurrentPage == "Connection";
        public bool IsServersPage => CurrentPage == "Servers";
        public bool IsSettingsPage => CurrentPage == "Settings";
        public bool IsLogsPage => CurrentPage == "Logs";

        [ObservableProperty]
        private bool _muxEnabled;

        [ObservableProperty]
        private bool _allowLan;

        [ObservableProperty]
        private string _remoteDns = "8.8.8.8";

        public List<string> VpnModes { get; } = new() { "SystemProxy", "TUN" };
        public List<string> DnsPresets { get; } = new() { "8.8.8.8", "1.1.1.1", "94.140.14.14" };
        public List<string> AvailableLanguages { get; } = new() { "Русский (RU)", "English (EN)" };

        [ObservableProperty]
        private string _selectedLanguage = "Русский (RU)";

        partial void OnSelectedLanguageChanged(string value)
        {
            if (_localizationService == null) return;
            string langCode = value.Contains("EN") ? "en" : "ru";
            _localizationService.SetLanguage(langCode);
            var settings = _settingsService.Settings;
            settings.Language = langCode;
            _settingsService.Save();

            // Refresh localized properties
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ConnectionStatusDisplay));
            OnPropertyChanged(nameof(ConnectionButtonText));
            OnPropertyChanged(nameof(ConnectionButtonSubtext));
        }

        public string StatusText => Status switch
        {
            "Connected" => LocalizationHelper.GetString("Str_StatusTextDisconnect", "Disconnect"),
            "Connecting" => LocalizationHelper.GetString("Str_StatusTextConnecting", "Connecting..."),
            "Disconnected" => LocalizationHelper.GetString("Str_StatusTextConnect", "Connect"),
            "Error" => LocalizationHelper.GetString("Str_StatusTextRetryConnect", "Retry Connect"),
            _ => LocalizationHelper.GetString("Str_StatusTextConnect", "Connect")
        };

        public string ConnectionStatusDisplay
        {
            get
            {
                return Status switch
                {
                    "Connected" => LocalizationHelper.GetString("Str_Status_Connected", "Connected"),
                    "Connecting" => LocalizationHelper.GetString("Str_Status_Connecting", "Connecting"),
                    "Disconnecting" => LocalizationHelper.GetString("Str_Status_Disconnecting", "Disconnecting"),
                    "Disconnected" => LocalizationHelper.GetString("Str_Status_Disconnected", "Disconnected"),
                    "Error" => LocalizationHelper.GetString("Str_Status_Error", "Error"),
                    _ => Status
                };
            }
        }

        public string ConnectionButtonText => Status switch
        {
            "Connected" => LocalizationHelper.GetString("Str_ButtonTextConnected", "CONNECTED"),
            "Connecting" => LocalizationHelper.GetString("Str_ButtonTextConnecting", "CONNECTING..."),
            "Disconnecting" => LocalizationHelper.GetString("Str_ButtonTextDisconnecting", "DISCONNECTING..."),
            "Error" => LocalizationHelper.GetString("Str_ButtonTextError", "ERROR"),
            _ => LocalizationHelper.GetString("Str_ButtonTextDisconnected", "DISCONNECTED")
        };

        public string ConnectionButtonSubtext => Status switch
        {
            "Connected" => string.Empty,
            "Connecting" => LocalizationHelper.GetString("Str_ButtonSubtextWait", "PLEASE WAIT..."),
            "Disconnecting" => LocalizationHelper.GetString("Str_ButtonSubtextWait", "PLEASE WAIT..."),
            "Error" => LocalizationHelper.GetString("Str_ButtonSubtextRetry", "CLICK TO RETRY"),
            _ => string.Empty
        };

        public string StatusColor => Status switch
        {
            "Connected" => "#4CAF50",  // Green
            "Connecting" => "#FF9800", // Orange
            "Disconnecting" => "#FF9800", // Orange
            "Disconnected" => "#FF5E7E", // Pink/Red
            "Error" => "#E81123",      // Red
            _ => "#FF5E7E"
        };

        public MainViewModel(
            IXrayService xrayService, 
            ISettingsService settingsService, 
            IRemnawaveService remnawaveService,
            IAutoStartService autoStartService,
            ILocalizationService localizationService,
            ICryptographyService cryptographyService)
        {
            _xrayService = xrayService;
            _settingsService = settingsService;
            _remnawaveService = remnawaveService;
            _autoStartService = autoStartService;
            _localizationService = localizationService;
            _cryptographyService = cryptographyService;

            // Hook Xray events
            _xrayService.LogReceived += OnLogReceived;
            _xrayService.StatusChanged += OnXrayStatusChanged;

            // Load settings
            _settingsService.Load();
            var settings = _settingsService.Settings;

            // Initialize localization
            _selectedLanguage = settings.Language == "en" ? "English (EN)" : "Русский (RU)";
            _localizationService?.SetLanguage(settings.Language);

            // Bind values
            _autoStart = settings.AutoStart;
            _vpnMode = settings.VpnMode == "TUN" ? "TUN" : "SystemProxy";
            _splitTunnelingEnabled = settings.SplitTunnelingEnabled;
            _killSwitchEnabled = settings.KillSwitchEnabled;
            _loggingEnabled = settings.LoggingEnabled;
            _muxEnabled = settings.MuxEnabled;
            _allowLan = settings.AllowLan;
            _remoteDns = !string.IsNullOrWhiteSpace(settings.RemoteDns) ? settings.RemoteDns : "8.8.8.8";
            _bypassDomainsText = string.Join(Environment.NewLine, settings.BypassDomains);
            _bypassProcessesText = string.Join(Environment.NewLine, settings.BypassProcesses);

            IsSubscriptionConfigured = !string.IsNullOrWhiteSpace(settings.SubscriptionUrl);
            if (IsSubscriptionConfigured)
            {
                SubscriptionInputUrl = ProtocolService.NormalizeSubscriptionUrl(settings.SubscriptionUrl);
            }

            LoadCachedServers();

            // Run initialization (downloading core if needed and silent update)
            Task.Run(InitializeAppAsync);
        }

        // Hooking setting changes to auto-save immediately
        partial void OnAutoStartChanged(bool value)
        {
            _settingsService.Settings.AutoStart = value;
            _settingsService.Save();
            _autoStartService.SetAutoStart(value);
            string fmt = LocalizationHelper.GetString("Str_LogAutoStartChanged", "[System] AutoStart set to {0}");
            AppendLog(string.Format(fmt, value));
        }

        partial void OnVpnModeChanged(string value)
        {
            _settingsService.Settings.VpnMode = value;
            _settingsService.Save();
            string fmt = LocalizationHelper.GetString("Str_LogVpnModeChanged", "[System] VPN Mode changed to {0}");
            AppendLog(string.Format(fmt, value));
        }

        partial void OnSplitTunnelingEnabledChanged(bool value)
        {
            _settingsService.Settings.SplitTunnelingEnabled = value;
            _settingsService.Save();
            string fmt = LocalizationHelper.GetString("Str_LogSplitTunnelingChanged", "[System] Split Tunneling set to {0}");
            AppendLog(string.Format(fmt, value));
        }

        partial void OnKillSwitchEnabledChanged(bool value)
        {
            _settingsService.Settings.KillSwitchEnabled = value;
            _settingsService.Save();
            string fmt = LocalizationHelper.GetString("Str_LogKillSwitchChanged", "[System] Kill Switch set to {0}");
            AppendLog(string.Format(fmt, value));
        }

        partial void OnMuxEnabledChanged(bool value)
        {
            _settingsService.Settings.MuxEnabled = value;
            _settingsService.Save();
            string fmt = LocalizationHelper.GetString("Str_LogMuxChanged", "[System] Mux set to {0}");
            AppendLog(string.Format(fmt, value));
        }

        partial void OnLoggingEnabledChanged(bool value)
        {
            _settingsService.Settings.LoggingEnabled = value;
            _settingsService.Save();
            if (!value)
            {
                _logLines.Clear();
                Logs = string.Empty;
            }
            else
            {
                AppendLog("[System] Logging enabled.");
            }
        }

        partial void OnAllowLanChanged(bool value)
        {
            _settingsService.Settings.AllowLan = value;
            _settingsService.Save();
            string fmt = LocalizationHelper.GetString("Str_LogAllowLanChanged", "[System] Allow LAN set to {0}");
            AppendLog(string.Format(fmt, value));
        }

        partial void OnRemoteDnsChanged(string value)
        {
            _settingsService.Settings.RemoteDns = value;
            _settingsService.Save();
            string fmt = LocalizationHelper.GetString("Str_LogRemoteDnsChanged", "[System] Remote DNS changed to {0}");
            AppendLog(string.Format(fmt, value));
        }

        [RelayCommand]
        private void ApplyBypassRules()
        {
            try
            {
                var settings = _settingsService.Settings;
                settings.BypassDomains.Clear();
                var domains = BypassDomainsText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var d in domains)
                {
                    if (!string.IsNullOrWhiteSpace(d))
                        settings.BypassDomains.Add(d.Trim());
                }

                settings.BypassProcesses.Clear();
                var processes = BypassProcessesText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in processes)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                        settings.BypassProcesses.Add(p.Trim());
                }

                _settingsService.Save();
                AppendLog(LocalizationHelper.GetString("Str_LogBypassRulesApplied", "[System] Routing bypass rules applied and saved."));
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_LogBypassRulesError", "[System] Error applying rules: {0}");
                AppendLog(string.Format(fmt, ex.Message));
            }
        }

        private async Task InitializeAppAsync()
        {
            // 1. Core check and download if missing
            if (!_xrayService.IsCoreAvailable())
            {
                IsCoreDownloading = true;
                CoreDownloadStatusText = LocalizationHelper.GetString("Str_StatusDownloadingCoreSimple", "Downloading Xray core...");
                
                var progress = new Progress<double>(p =>
                {
                    CoreDownloadProgress = p;
                    string fmt = LocalizationHelper.GetString("Str_StatusDownloadingCore", "Downloading Xray core... {0}");
                    CoreDownloadStatusText = string.Format(fmt, p.ToString("P0"));
                });

                try
                {
                    await _xrayService.InitializeAsync(progress);
                    AppendLog(LocalizationHelper.GetString("Str_LogCoreReady", "[System] Xray core is ready."));
                }
                catch (Exception ex)
                {
                    string fmt = LocalizationHelper.GetString("Str_LogCoreDownloadFailed", "[System] Failed to download Xray core: {0}");
                    AppendLog(string.Format(fmt, ex.Message));
                    CoreDownloadStatusText = LocalizationHelper.GetString("Str_StatusCoreInstallError", "Error installing core. Check logs.");
                    return;
                }
                finally
                {
                    IsCoreDownloading = false;
                }
            }

            // 2. Silent automatic subscription update at cold launch
            if (IsSubscriptionConfigured)
            {
                AppendLog(LocalizationHelper.GetString("Str_LogAutoUpdatingSub", "[System] Auto-updating subscription..."));
                try
                {
                    await UpdateSubscriptionInternalAsync(_settingsService.Settings.SubscriptionUrl, true);
                }
                catch (Exception ex)
                {
                    string fmt = LocalizationHelper.GetString("Str_LogSilentSubUpdateFailed", "[System] Silent subscription update failed: {0}. Using cached servers.");
                    AppendLog(string.Format(fmt, ex.Message));
                }
            }

            // 3. Start background 6-hour auto-update timer
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromHours(6));
                    if (IsSubscriptionConfigured && !string.IsNullOrWhiteSpace(_settingsService.Settings.SubscriptionUrl))
                    {
                        AppendLog(LocalizationHelper.GetString("Str_LogPeriodicSubUpdate", "[System] Periodic 6-hour background subscription update..."));
                        try
                        {
                            await UpdateSubscriptionInternalAsync(_settingsService.Settings.SubscriptionUrl, true);
                        }
                        catch (Exception ex)
                        {
                            string fmt = LocalizationHelper.GetString("Str_LogPeriodicUpdateFailed", "[System] Periodic update failed: {0}");
                            AppendLog(string.Format(fmt, ex.Message));
                        }
                    }
                }
            });
        }

        public async Task HandleDeeplinkAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            string normalized = ProtocolService.NormalizeSubscriptionUrl(url);

            App.Current?.Dispatcher?.Invoke(() =>
            {
                SubscriptionInputUrl = normalized;
            });

            _settingsService.Settings.SubscriptionUrl = normalized;
            _settingsService.Save();

            AppendLog($"[System] Received deeplink: {url}. Updating subscription...");
            try
            {
                await UpdateSubscriptionInternalAsync(normalized, false);
                IsSubscriptionConfigured = true;
            }
            catch (Exception ex)
            {
                AppendLog($"[Error] Deeplink import failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ImportSubscriptionAsync()
        {
            if (string.IsNullOrWhiteSpace(SubscriptionInputUrl))
            {
                SubscriptionStatusMessage = LocalizationHelper.GetString("Str_StatusUrlEmpty", "⚠️ Subscription URL cannot be empty.");
                return;
            }

            string normalized = ProtocolService.NormalizeSubscriptionUrl(SubscriptionInputUrl);
            SubscriptionInputUrl = normalized;

            IsSubscriptionImporting = true;
            SubscriptionStatusMessage = LocalizationHelper.GetString("Str_StatusFetching", "Fetching subscription...");

            try
            {
                await UpdateSubscriptionInternalAsync(normalized, false);
                IsSubscriptionConfigured = true;
                SubscriptionStatusMessage = string.Empty;
                ShowSettingsDrawer = false; 
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_StatusError", "❌ Error: {0}");
                SubscriptionStatusMessage = string.Format(fmt, ex.Message);
            }
            finally
            {
                IsSubscriptionImporting = false;
            }
        }

        [RelayCommand]
        private async Task RefreshSubscriptionAsync()
        {
            if (string.IsNullOrEmpty(_settingsService.Settings.SubscriptionUrl)) return;
            AppendLog(LocalizationHelper.GetString("Str_LogRefreshingSub", "[System] Refreshing subscription..."));
            try
            {
                await UpdateSubscriptionInternalAsync(_settingsService.Settings.SubscriptionUrl, false);
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_LogRefreshSubFailed", "[Error] Failed to refresh subscription: {0}");
                AppendLog(string.Format(fmt, ex.Message));
            }
        }

        [RelayCommand]
        private void OpenSupport()
        {
            string url = SubscriptionInfo.EffectiveSupportUrl;
            if (string.IsNullOrWhiteSpace(url) || url == "https://t.me/remnawave")
            {
                var subUrl = _settingsService.Settings.SubscriptionUrl;
                if (!string.IsNullOrWhiteSpace(subUrl))
                {
                    try
                    {
                        var uri = new Uri(subUrl);
                        url = $"{uri.Scheme}://{uri.Host}";
                    }
                    catch { }
                }
            }
            if (string.IsNullOrWhiteSpace(url))
            {
                url = "https://t.me/remnawave";
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
                string fmt = LocalizationHelper.GetString("Str_LogSupportOpened", "[System] Opened support link: {0}");
                AppendLog(string.Format(fmt, url));
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_LogSupportFailed", "[Error] Could not open support link: {0}");
                AppendLog(string.Format(fmt, ex.Message));
            }
        }

        [RelayCommand]
        private async Task ClearSubscriptionAsync()
        {
            if (Status == "Connected" || Status == "Connecting")
            {
                await _xrayService.DisconnectAsync();
            }

            try
            {
                // Clear settings
                _settingsService.Settings.SubscriptionUrl = string.Empty;
                _settingsService.Settings.SelectedServerId = string.Empty;
                _settingsService.Settings.SelectedServerName = string.Empty;
                _settingsService.Settings.SubscriptionRouteJson = string.Empty;
                _settingsService.Settings.SubscriptionDnsJson = string.Empty;
                _settingsService.Settings.IsFullConfig = false;
                _settingsService.Settings.FullConfigJson = string.Empty;
                _settingsService.Settings.RoutingProfile = null;
                _settingsService.Save();

                // Clear cached servers file
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string folder = Path.Combine(appData, "RemnaVpn");
                string serversPath = Path.Combine(folder, "servers.json");
                string infoPath = Path.Combine(folder, "sub_info.json");
                if (File.Exists(serversPath))
                {
                    File.Delete(serversPath);
                }
                if (File.Exists(infoPath))
                {
                    File.Delete(infoPath);
                }

                // Clear list
                Servers.Clear();
                SelectedServer = null;
                SubscriptionInfo = new SubscriptionInfo();
                SubscriptionInputUrl = string.Empty;
                IsSubscriptionConfigured = false;
                SubscriptionStatusMessage = string.Empty;
                AppendLog(LocalizationHelper.GetString("Str_LogSubReset", "[System] Subscription reset."));
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_LogSubClearError", "[System] Error clearing subscription: {0}");
                AppendLog(string.Format(fmt, ex.Message));
            }
        }

        private async Task UpdateSubscriptionInternalAsync(string url, bool silent)
        {
            url = ProtocolService.NormalizeSubscriptionUrl(url);
            var subResult = await _remnawaveService.FetchServersAsync(url);
            var servers = subResult.Servers;
            var info = subResult.Info;
            
            // Save to disk (Encrypted with DPAPI)
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string folder = Path.Combine(appData, "RemnaVpn");
            string serversPath = Path.Combine(folder, "servers.json");
            string infoPath = Path.Combine(folder, "sub_info.json");
            
            string json = JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true });
            string encryptedJson = _cryptographyService.Encrypt(json);
            await File.WriteAllTextAsync(serversPath, encryptedJson, new System.Text.UTF8Encoding(false));

            string infoJson = JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true });
            string encryptedInfoJson = _cryptographyService.Encrypt(infoJson);
            await File.WriteAllTextAsync(infoPath, encryptedInfoJson, new System.Text.UTF8Encoding(false));

            // Update in-memory settings
            _settingsService.Settings.SubscriptionUrl = url;
            if (!string.IsNullOrEmpty(subResult.RouteJson))
            {
                _settingsService.Settings.SubscriptionRouteJson = subResult.RouteJson;
            }
            if (!string.IsNullOrEmpty(subResult.DnsJson))
            {
                _settingsService.Settings.SubscriptionDnsJson = subResult.DnsJson;
            }
            _settingsService.Settings.IsFullConfig = subResult.IsFullConfig;
            _settingsService.Settings.FullConfigJson = subResult.FullConfigJson;
            _settingsService.Settings.RoutingProfile = subResult.RoutingProfile;
            _settingsService.Save();

            // Refresh UI list on UI thread
            App.Current?.Dispatcher?.Invoke(() =>
            {
                SubscriptionInfo = info;
                Servers.Clear();
                foreach (var s in servers)
                {
                    Servers.Add(s);
                }

                // Restore selected server
                RestoreSelectedServer();
            });

            if (!silent)
            {
                string fmt = LocalizationHelper.GetString("Str_LogImportSuccess", "[System] Imported {0} servers successfully.");
                AppendLog(string.Format(fmt, servers.Count));
            }

            // Run ping checks
            _ = Task.Run(CheckAllPingsAsync);
        }

        private void LoadCachedServers()
        {
            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string folder = Path.Combine(appData, "RemnaVpn");
                string serversPath = Path.Combine(folder, "servers.json");
                string infoPath = Path.Combine(folder, "sub_info.json");

                SubscriptionInfo? cachedInfo = null;
                if (File.Exists(infoPath))
                {
                    string infoContent = File.ReadAllText(infoPath).Trim();
                    if (infoContent.StartsWith("{"))
                    {
                        cachedInfo = JsonSerializer.Deserialize<SubscriptionInfo>(infoContent);
                    }
                    else
                    {
                        string decrypted = _cryptographyService.Decrypt(infoContent);
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            cachedInfo = JsonSerializer.Deserialize<SubscriptionInfo>(decrypted);
                        }
                    }

                    if (cachedInfo != null)
                    {
                        SubscriptionInfo = cachedInfo;
                    }
                }

                if (File.Exists(serversPath))
                {
                    string serversContent = File.ReadAllText(serversPath).Trim();
                    List<Server>? cachedList = null;
                    if (serversContent.StartsWith("["))
                    {
                        cachedList = JsonSerializer.Deserialize<List<Server>>(serversContent);
                    }
                    else
                    {
                        string decrypted = _cryptographyService.Decrypt(serversContent);
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            cachedList = JsonSerializer.Deserialize<List<Server>>(decrypted);
                        }
                    }
                    
                    if (cachedList != null)
                    {
                        Servers.Clear();
                        foreach (var s in cachedList)
                        {
                            if (cachedInfo != null)
                            {
                                s.HasJsonModule = cachedInfo.HasImportedJson;
                            }
                            Servers.Add(s);
                        }

                        // Restore selected server
                        RestoreSelectedServer();
                    }
                }
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_LogLoadCacheError", "[System] Error loading cached servers: {0}");
                AppendLog(string.Format(fmt, ex.Message));
            }
        }

        private void RestoreSelectedServer()
        {
            Server? toSelect = null;
            if (!string.IsNullOrEmpty(_settingsService.Settings.SelectedServerId))
            {
                toSelect = Servers.FirstOrDefault(s => s.Id == _settingsService.Settings.SelectedServerId);
            }
            if (toSelect == null && !string.IsNullOrEmpty(_settingsService.Settings.SelectedServerName))
            {
                toSelect = Servers.FirstOrDefault(s => string.Equals(s.Name, _settingsService.Settings.SelectedServerName, StringComparison.OrdinalIgnoreCase));
            }
            if (toSelect == null && !string.IsNullOrEmpty(_settingsService.Settings.SelectedServerName))
            {
                toSelect = Servers.FirstOrDefault(s => string.Equals(s.CleanName, _settingsService.Settings.SelectedServerName, StringComparison.OrdinalIgnoreCase));
            }
            if (toSelect == null && Servers.Count > 0)
            {
                toSelect = Servers[0];
            }
            SelectedServer = toSelect;
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private bool TryElevateToAdmin()
        {
            bool userAgreed = false;
            App.Current?.Dispatcher?.Invoke(() =>
            {
                var result = System.Windows.MessageBox.Show(
                    LocalizationHelper.GetString("Str_AdminRestartPromptText", "TUN mode requires administrator privileges.\n\nRestart the application as Administrator?"),
                    LocalizationHelper.GetString("Str_AdminRestartPromptTitle", "Elevation Required"),
                    System.Windows.MessageBoxButton.OKCancel,
                    System.Windows.MessageBoxImage.Question);
                userAgreed = (result == System.Windows.MessageBoxResult.OK || result == System.Windows.MessageBoxResult.Yes);
            });

            if (!userAgreed)
            {
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(startInfo);
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    App.Current.Shutdown();
                });
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User canceled UAC prompt (clicked No / Cancel)
                App.Current?.Dispatcher?.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        LocalizationHelper.GetString("Str_AdminWarningText", "TUN Mode (system-wide VPN) requires administrator privileges.\n\nPlease run this application as Administrator, or switch to System Proxy Mode in settings."),
                        LocalizationHelper.GetString("Str_AdminWarningTitle", "Elevation Required"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                });
                return false;
            }
            catch (Exception ex)
            {
                string fmt = LocalizationHelper.GetString("Str_LogVpnStartError", "[System] Error starting VPN: {0}");
                AppendLog(string.Format(fmt, ex.Message));
                return false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanToggleConnection))]
        private async Task ToggleConnectionAsync()
        {
            if (Status == "Connected" || Status == "Connecting")
            {
                await _xrayService.DisconnectAsync();
            }
            else
            {
                if (SelectedServer == null)
                {
                    AppendLog(LocalizationHelper.GetString("Str_StatusSelectServerFirst", "[System] Please select a server first."));
                    return;
                }

                _settingsService.Settings.SelectedServerId = SelectedServer.Id;
                _settingsService.Settings.SelectedServerName = SelectedServer.Name;
                _settingsService.Save();

                if (_settingsService.Settings.VpnMode == "TUN" && !IsAdministrator())
                {
                    TryElevateToAdmin();
                    return;
                }

                try
                {
                    await _xrayService.ConnectAsync(SelectedServer, _settingsService.Settings);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!TryElevateToAdmin())
                    {
                        string fmt = LocalizationHelper.GetString("Str_LogVpnStartError", "[System] Error starting VPN: {0}");
                        AppendLog(string.Format(fmt, ex.Message));
                    }
                }
                catch (Exception ex)
                {
                    string fmt = LocalizationHelper.GetString("Str_LogVpnConnectError", "[System] Error establishing connection: {0}");
                    AppendLog(string.Format(fmt, ex.Message));
                }
            }
        }

        private bool CanToggleConnection()
        {
            return SelectedServer != null && !IsCoreDownloading && Status != "Disconnecting";
        }

        [RelayCommand]
        private async Task CheckPingsAsync()
        {
            var targetServer = SelectedServer ?? Servers.FirstOrDefault();
            if (targetServer == null) return;

            string checkingFmt = LocalizationHelper.GetString("Str_LogPingChecking", "[System] Checking latency for {0}...");
            AppendLog(string.Format(checkingFmt, targetServer.Name));
            targetServer.PingStatus = "pinging...";

            try
            {
                var sw = Stopwatch.StartNew();
                bool isCurrentConnected = (Status == "Connected" && SelectedServer != null && SelectedServer.Id == targetServer.Id);

                if (isCurrentConnected)
                {
                    var handler = new HttpClientHandler
                    {
                        Proxy = new System.Net.WebProxy($"http://127.0.0.1:{_xrayService.ActiveHttpPort}"),
                        UseProxy = true
                    };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                    using var response = await client.GetAsync("http://www.gstatic.com/generate_204");
                    sw.Stop();
                    if (response.IsSuccessStatusCode || (int)response.StatusCode == 204)
                    {
                        targetServer.PingTimeMs = sw.ElapsedMilliseconds;
                        targetServer.PingStatus = $"{sw.ElapsedMilliseconds} ms";
                    }
                    else
                    {
                        targetServer.PingTimeMs = null;
                        targetServer.PingStatus = $"HTTP {(int)response.StatusCode}";
                    }
                }
                else
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    var connectTask = tcp.ConnectAsync(targetServer.Address, targetServer.Port);
                    var timeoutTask = Task.Delay(2500);
                    var completed = await Task.WhenAny(connectTask, timeoutTask);
                    sw.Stop();

                    if (completed == connectTask && tcp.Connected)
                    {
                        targetServer.PingTimeMs = sw.ElapsedMilliseconds;
                        targetServer.PingStatus = $"{sw.ElapsedMilliseconds} ms";
                    }
                    else
                    {
                        targetServer.PingTimeMs = null;
                        targetServer.PingStatus = "Timeout";
                    }
                }
            }
            catch (Exception)
            {
                targetServer.PingTimeMs = null;
                targetServer.PingStatus = "Offline";
            }

            string completeFmt = LocalizationHelper.GetString("Str_LogPingComplete", "[System] Latency check complete: {0}.");
            AppendLog(string.Format(completeFmt, targetServer.FormattedPing));
        }

        [RelayCommand]
        private async Task CheckAllPingsAsync()
        {
            if (Servers == null || Servers.Count == 0) return;
            string checkingFmt = LocalizationHelper.GetString("Str_LogPingAllChecking", "[System] Checking latency for all {0} servers...");
            AppendLog(string.Format(checkingFmt, Servers.Count));

            var serverList = Servers.ToList();
            int batchSize = 5;
            for (int i = 0; i < serverList.Count; i += batchSize)
            {
                var batch = serverList.Skip(i).Take(batchSize);
                var tasks = batch.Select(async server =>
                {
                    await _pingSemaphore.WaitAsync();
                    App.Current?.Dispatcher?.Invoke(() => server.PingStatus = "pinging...", System.Windows.Threading.DispatcherPriority.Background);
                    try
                    {
                        var sw = Stopwatch.StartNew();
                        bool isCurrentConnected = (Status == "Connected" && SelectedServer != null && SelectedServer.Id == server.Id);
                        long? timeMs = null;
                        string statusStr;

                        if (isCurrentConnected)
                        {
                            var handler = new HttpClientHandler
                            {
                                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{_xrayService.ActiveHttpPort}"),
                                UseProxy = true
                            };
                            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
                            using var response = await client.GetAsync("http://www.gstatic.com/generate_204");
                            sw.Stop();
                            if (response.IsSuccessStatusCode || (int)response.StatusCode == 204)
                            {
                                timeMs = sw.ElapsedMilliseconds;
                                statusStr = $"{sw.ElapsedMilliseconds} ms";
                            }
                            else
                            {
                                statusStr = $"HTTP {(int)response.StatusCode}";
                            }
                        }
                        else
                        {
                            using var tcp = new System.Net.Sockets.TcpClient();
                            var connectTask = tcp.ConnectAsync(server.Address, server.Port);
                            var timeoutTask = Task.Delay(2500);
                            var completed = await Task.WhenAny(connectTask, timeoutTask);
                            sw.Stop();

                            if (completed == connectTask && tcp.Connected)
                            {
                                timeMs = sw.ElapsedMilliseconds;
                                statusStr = $"{sw.ElapsedMilliseconds} ms";
                            }
                            else
                            {
                                statusStr = "Timeout";
                            }
                        }

                        App.Current?.Dispatcher?.Invoke(() =>
                        {
                            server.PingTimeMs = timeMs;
                            server.PingStatus = statusStr;
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    catch (Exception)
                    {
                        App.Current?.Dispatcher?.Invoke(() =>
                        {
                            server.PingTimeMs = null;
                            server.PingStatus = "Offline";
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                    finally
                    {
                        _pingSemaphore.Release();
                    }
                });

                await Task.WhenAll(tasks);
                await Task.Delay(50); // Small breathing pause for UI responsiveness
            }

            AppendLog(LocalizationHelper.GetString("Str_LogPingAllComplete", "[System] Mass latency check completed."));
        }

        [RelayCommand]
        private void ToggleSettingsDrawer()
        {
            ShowSettingsDrawer = !ShowSettingsDrawer;
        }

        [RelayCommand]
        private void Navigate(string page)
        {
            CurrentPage = page;
        }

        private void OnLogReceived(string log)
        {
            AppendLog(log);
        }

        private void OnXrayStatusChanged(string newStatus)
        {
            App.Current?.Dispatcher?.Invoke(() =>
            {
                Status = newStatus;
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusColor));
                OnPropertyChanged(nameof(ConnectionStatusDisplay));
                OnPropertyChanged(nameof(ConnectionButtonText));
                OnPropertyChanged(nameof(ConnectionButtonSubtext));
                ToggleConnectionCommand.NotifyCanExecuteChanged();
            });
        }

        [RelayCommand]
        private void ClearLogs()
        {
            _logLines.Clear();
            Logs = string.Empty;
        }

        [RelayCommand]
        private void CopyLogs()
        {
            try
            {
                if (!string.IsNullOrEmpty(Logs))
                {
                    System.Windows.Clipboard.SetText(Logs);
                    AppendLog("[System] Logs copied to clipboard.");
                }
            }
            catch { }
        }

        private void AppendLog(string message)
        {
            if (!LoggingEnabled) return;
            App.Current?.Dispatcher?.Invoke(() =>
            {
                _logLines.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
                while (_logLines.Count > 500)
                {
                    _logLines.Dequeue();
                }
                Logs = string.Join(Environment.NewLine, _logLines);
            });
        }

        public void Dispose()
        {
            if (_xrayService != null)
            {
                _xrayService.LogReceived -= OnLogReceived;
                _xrayService.StatusChanged -= OnXrayStatusChanged;
            }
            _pingSemaphore?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
