using System;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.AccessControl;
using RemnaVpn.Helpers;
using RemnaVpn.Models;

namespace RemnaVpn.Services
{
    public class XrayService : IXrayService, IDisposable
    {
        private readonly ISystemProxyService _systemProxyService;
        private readonly string _coreDir;
        private readonly string _exePath;
        private Process? _process;
        private Server? _activeServer;
        private AppSettings? _activeSettings;
        private SubscriptionResult? _activeSubscriptionResult;
        private int _activeHttpPort = 10809;
        private bool _isReconnecting;
        private DateTime _lastConnectedTime = DateTime.MinValue;
        private static readonly TimeSpan _networkChangeDebounce = TimeSpan.FromSeconds(15);
        private readonly SemaphoreSlim _connectionSemaphore = new(1, 1);
        private bool _isDisposed;

        // Windows Job Objects P/Invokes to prevent zombie processes
        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 ActiveProcessLimit;
            public Int64 Affinity;
            public UInt32 LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public UInt32 ActiveProcessLimit2;
            public UIntPtr Affinity2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public UInt64 ReadOperationCount;
            public UInt64 WriteOperationCount;
            public UInt64 OtherOperationCount;
            public UInt64 ReadTransferCount;
            public UInt64 WriteTransferCount;
            public UInt64 OtherTransferCount;
        }

        private enum JobObjectInfoClass
        {
            ExtendedLimitInformation = 9
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoClass jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private static IntPtr _jobHandle = IntPtr.Zero;
        private static readonly object JobLock = new();

        public event Action<string>? LogReceived;
        public event Action<string>? StatusChanged;

        public string Status { get; private set; } = "Disconnected";
        public int ActiveHttpPort => _activeHttpPort;

        public XrayService(ISystemProxyService systemProxyService)
        {
            _systemProxyService = systemProxyService;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _coreDir = Path.Combine(appData, "RemnaVpn", "core");
            Directory.CreateDirectory(_coreDir);
            _exePath = Path.Combine(_coreDir, "xray.exe");

            // Handle network change event for auto-reconnect
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        private static void EnsureJobObjectCreated()
        {
            lock (JobLock)
            {
                if (_jobHandle != IntPtr.Zero) return;

                _jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero) return;

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (!SetInformationJobObject(_jobHandle, JobObjectInfoClass.ExtendedLimitInformation, infoPtr, (uint)length))
                    {
                        Debug.WriteLine("Failed to set Job Object information.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }
            }
        }

        public bool IsCoreAvailable()
        {
            return File.Exists(_exePath) && File.Exists(Path.Combine(_coreDir, "geoip.dat")) && File.Exists(Path.Combine(_coreDir, "geosite.dat"));
        }

        public async Task InitializeAsync(IProgress<double>? downloadProgress = null)
        {
            if (IsCoreAvailable())
            {
                LogReceived?.Invoke("[System] Xray-core is already present.");
                await EnsureWintunAsync();
                await DownloadGeoFilesIfNeededAsync(null, downloadProgress);
                return;
            }

            LogReceived?.Invoke("[System] Xray-core not found. Starting download...");

            string zipPath = Path.Combine(_coreDir, "xray.zip");
            string tempExtractDir = Path.Combine(_coreDir, "temp_extract");

            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GoatWebVPN", "1.0"));

                // Resolve latest release or fallback to v1.8.24
                string version = "1.8.24";
                try
                {
                    var releaseJson = await client.GetStringAsync("https://api.github.com/repos/XTLS/Xray-core/releases/latest");
                    using var doc = JsonDocument.Parse(releaseJson);
                    if (doc.RootElement.TryGetProperty("tag_name", out var tagProp))
                    {
                        string tag = tagProp.GetString() ?? "";
                        if (!string.IsNullOrEmpty(tag))
                        {
                            version = tag.TrimStart('v');
                        }
                    }
                }
                catch
                {
                    LogReceived?.Invoke("[System] Failed to check GitHub API for latest Xray release, using default v1.8.24.");
                }

                string url = $"https://github.com/XTLS/Xray-core/releases/download/v{version}/Xray-windows-64.zip";
                LogReceived?.Invoke($"[System] Downloading Xray v{version} from {url}...");

                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();
                using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                    if (totalBytes.HasValue)
                    {
                        double progress = (double)totalRead / totalBytes.Value * 85.0; // 85% for Xray
                        downloadProgress?.Report(progress);
                    }
                }
                fileStream.Close();

                LogReceived?.Invoke("[System] Extracting Xray-core files...");
                if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                Directory.CreateDirectory(tempExtractDir);

                ZipFile.ExtractToDirectory(zipPath, tempExtractDir);

                string[] requiredFiles = { "xray.exe", "geoip.dat", "geosite.dat" };
                foreach (var file in requiredFiles)
                {
                    var sourceFile = Directory.GetFiles(tempExtractDir, file, SearchOption.AllDirectories).FirstOrDefault();
                    if (sourceFile != null)
                    {
                        File.Copy(sourceFile, Path.Combine(_coreDir, file), true);
                    }
                    else
                    {
                        throw new FileNotFoundException($"Required file {file} was not found in the Xray archive.");
                    }
                }

                downloadProgress?.Report(90.0);
                await EnsureWintunAsync();
                await DownloadGeoFilesIfNeededAsync(null, downloadProgress);
                downloadProgress?.Report(100.0);

                LogReceived?.Invoke("[System] Xray-core installed successfully.");
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[System] Error installing Xray-core: {ex.Message}");
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, true);
                }
                catch { }
            }
        }

        private async Task EnsureWintunAsync()
        {
            string wintunPath = Path.Combine(_coreDir, "wintun.dll");
            if (File.Exists(wintunPath)) return;

            LogReceived?.Invoke("[System] wintun.dll not found. Downloading WireGuard wintun driver...");
            string zipPath = Path.Combine(_coreDir, "wintun.zip");
            string tempDir = Path.Combine(_coreDir, "temp_wintun");

            try
            {
                using var client = new HttpClient();
                string url = "https://www.wintun.net/builds/wintun-0.14.1.zip";
                byte[] zipBytes = await client.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(zipPath, zipBytes);

                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);
                ZipFile.ExtractToDirectory(zipPath, tempDir);

                string archFolder = RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.Arm64 => "arm64",
                    Architecture.X86 => "x86",
                    _ => "amd64"
                };

                var dllSource = Directory.GetFiles(tempDir, "wintun.dll", SearchOption.AllDirectories)
                    .FirstOrDefault(f => f.Contains(archFolder, StringComparison.OrdinalIgnoreCase))
                    ?? Directory.GetFiles(tempDir, "wintun.dll", SearchOption.AllDirectories).FirstOrDefault();

                if (dllSource != null)
                {
                    File.Copy(dllSource, wintunPath, true);
                    LogReceived?.Invoke("[System] wintun.dll installed successfully.");
                }
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke($"[System] Warning: Could not download wintun.dll automatically ({ex.Message}). TUN mode may not work.");
            }
            finally
            {
                try
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                }
                catch { }
            }
        }

        private static string ComputeSha256Hash(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public async Task DownloadGeoFilesIfNeededAsync(IncyRoutingProfile? profile = null, IProgress<double>? progress = null)
        {
            string[] geoFiles = { "geoip.dat", "geosite.dat" };
            foreach (var file in geoFiles)
            {
                try
                {
                    string filePath = Path.Combine(_coreDir, file);
                    string targetUrl = $"https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/{file}";
                    string targetSha256 = string.Empty;

                    if (profile?.GeoFiles != null && profile.GeoFiles.Count > 0)
                    {
                        var entry = profile.GeoFiles.FirstOrDefault(g => g.Name.Equals(file, StringComparison.OrdinalIgnoreCase) ||
                                                                         g.Name.Equals(Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase));
                        if (entry != null)
                        {
                            if (!string.IsNullOrEmpty(entry.Url)) targetUrl = entry.Url;
                            if (!string.IsNullOrEmpty(entry.Sha256)) targetSha256 = entry.Sha256.ToLowerInvariant();
                        }
                    }

                    bool needsDownload = false;
                    if (!File.Exists(filePath))
                    {
                        needsDownload = true;
                        LogReceived?.Invoke($"[System] {file} not found. Preparing to download...");
                    }
                    else
                    {
                        string localHash = ComputeSha256Hash(filePath);
                        if (!string.IsNullOrEmpty(targetSha256))
                        {
                            if (!localHash.Equals(targetSha256, StringComparison.OrdinalIgnoreCase))
                            {
                                needsDownload = true;
                                LogReceived?.Invoke($"[System] {file} SHA-256 mismatch (Expected: {targetSha256.Substring(0, Math.Min(8, targetSha256.Length))}..., Local: {localHash.Substring(0, 8)}...). Updating...");
                            }
                            else
                            {
                                LogReceived?.Invoke($"[System] {file} SHA-256 verified ({localHash.Substring(0, 8)}...).");
                            }
                        }
                        else
                        {
                            var fi = new FileInfo(filePath);
                            if (fi.Length < 100)
                            {
                                needsDownload = true;
                                LogReceived?.Invoke($"[System] {file} appears corrupted. Downloading...");
                            }
                            else
                            {
                                LogReceived?.Invoke($"[System] {file} is present (SHA-256: {localHash.Substring(0, 8)}...).");
                            }
                        }
                    }

                    if (needsDownload)
                    {
                        LogReceived?.Invoke($"[System] Downloading {file} from {targetUrl}...");
                        using var client = new HttpClient();
                        using var response = await client.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead);
                        response.EnsureSuccessStatusCode();

                        long? totalBytes = response.Content.Headers.ContentLength;
                        using var contentStream = await response.Content.ReadAsStreamAsync();
                        string tempPath = filePath + ".tmp";
                        using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            totalRead += bytesRead;
                            if (totalBytes.HasValue)
                            {
                                double p = (double)totalRead / totalBytes.Value * 100.0;
                                progress?.Report(p);
                            }
                        }
                        fileStream.Close();

                        string downloadedHash = ComputeSha256Hash(tempPath);
                        if (!string.IsNullOrEmpty(targetSha256) && !downloadedHash.Equals(targetSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            LogReceived?.Invoke($"[System] Warning: Downloaded {file} SHA-256 ({downloadedHash}) does not match target ({targetSha256}). Installing anyway.");
                        }
                        else
                        {
                            LogReceived?.Invoke($"[System] Downloaded {file} verified (SHA-256: {downloadedHash.Substring(0, 8)}...).");
                        }

                        File.Move(tempPath, filePath, true);
                    }
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[System] Warning: Error checking/downloading {file}: {ex.Message}");
                }
            }
        }

        private void KillGhostProcesses()
        {
            try
            {
                string[] targetNames = { "xray", "v2ray", "wintun", "RemnaVpn" };
                int currentId = Process.GetCurrentProcess().Id;
                foreach (var name in targetNames)
                {
                    foreach (var proc in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            if (proc.Id == currentId) continue;
                            proc.Kill(true);
                            proc.WaitForExit(1000);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        private static bool IsPortInUse(int port)
        {
            try
            {
                var ipProperties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
                var tcpListeners = ipProperties.GetActiveTcpListeners();
                foreach (var endpoint in tcpListeners)
                {
                    if (endpoint.Port == port)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static int GetAvailablePort(int startPort)
        {
            for (int port = startPort; port < 65535; port++)
            {
                if (!IsPortInUse(port))
                    return port;
            }
            return startPort;
        }

        public async Task ConnectAsync(Server server, AppSettings settings, SubscriptionResult? subscriptionResult = null)
        {
            await _connectionSemaphore.WaitAsync();
            try
            {
                await DisconnectInternalAsync();

                _activeServer = server;
                _activeSettings = settings;
                _activeSubscriptionResult = subscriptionResult;

                Status = "Connecting";
                StatusChanged?.Invoke("Connecting");
                LogReceived?.Invoke($"[System] Connecting to {server.Name} ({server.Address}:{server.Port})...");

                if (settings.VpnMode == "TUN")
                {
                    if (!IsAdministrator())
                    {
                        Status = "Disconnected";
                        StatusChanged?.Invoke("Disconnected");
                        throw new UnauthorizedAccessException("TUN Mode requires Administrator privileges.");
                    }
                }

                // Step 1: Kill ALL xray zombie processes to free ports 10808/10809,
                // then do a broader ghost process cleanup.
                await Task.Run(() =>
                {
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("xray"))
                        {
                            try { p.Kill(); p.WaitForExit(500); } catch { }
                        }
                    }
                    catch { }
                    KillGhostProcesses();
                });

                var activeProfile = subscriptionResult?.RoutingProfile ?? settings.RoutingProfile;
                if (activeProfile?.GeoFiles != null && activeProfile.GeoFiles.Count > 0)
                {
                    await DownloadGeoFilesIfNeededAsync(activeProfile);
                }

                int socksPort = 10808;
                int httpPort = 10809;
                await Task.Run(() =>
                {
                    socksPort = GetAvailablePort(10808);
                    httpPort = GetAvailablePort(10809);
                    if (httpPort == socksPort) httpPort = GetAvailablePort(socksPort + 1);
                });
                _activeHttpPort = httpPort;

                if (socksPort != 10808 || httpPort != 10809)
                {
                    LogReceived?.Invoke($"[System] Default proxy ports (10808/10809) are busy. Automatically falling back to free ports: SOCKS={socksPort}, HTTP={httpPort}.");
                }

                string configPath = Path.Combine(_coreDir, "config.json");
                if ((subscriptionResult?.IsFullConfig == true && !string.IsNullOrEmpty(subscriptionResult.FullConfigJson)) ||
                    (settings.IsFullConfig && !string.IsNullOrEmpty(settings.FullConfigJson)))
                {
                    string fullConfig = !string.IsNullOrEmpty(subscriptionResult?.FullConfigJson) ? subscriptionResult.FullConfigJson : settings.FullConfigJson;
                    if (socksPort != 10808 || httpPort != 10809)
                    {
                        fullConfig = fullConfig.Replace("10808", socksPort.ToString()).Replace("10809", httpPort.ToString());
                    }
                    WriteSecureFile(configPath, fullConfig);
                    LogReceived?.Invoke("[System] Using Xray Full Config from subscription.");
                }
                else
                {
                    GenerateConfig(server, settings, configPath, subscriptionResult?.RoutingProfile ?? settings.RoutingProfile, socksPort, httpPort);
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = "run -c config.json",
                    WorkingDirectory = _coreDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                _process = new Process { StartInfo = startInfo };
                _process.EnableRaisingEvents = true;

                _process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogReceived?.Invoke(e.Data);
                    }
                };

                _process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        LogReceived?.Invoke(e.Data);
                    }
                };

                _process.Exited += (s, e) =>
                {
                    if (Status == "Connected" || Status == "Connecting")
                    {
                        Status = "Disconnected";
                        StatusChanged?.Invoke("Disconnected");
                        int exitCode = -1;
                        try { exitCode = _process?.ExitCode ?? -1; } catch { }
                        LogReceived?.Invoke($"[System] Xray core process exited (Exit Code: {exitCode}).");

                        if (_activeSettings?.VpnMode == "SystemProxy")
                        {
                            Task.Run(() => _systemProxyService.DisableProxy());
                            LogReceived?.Invoke("[System] System proxy disabled.");
                        }
                    }
                };

                _process.Start();

                // Bind to Job Object to avoid leak/zombies on crash
                EnsureJobObjectCreated();
                if (_jobHandle != IntPtr.Zero)
                {
                    AssignProcessToJobObject(_jobHandle, _process.Handle);
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                // Wait 1.5 seconds to see if it starts successfully
                await Task.Delay(1500);

                if (_process == null || _process.HasExited)
                {
                    Status = "Error";
                    StatusChanged?.Invoke("Error");
                    int exitCode = -1;
                    try { exitCode = _process?.ExitCode ?? -1; } catch { }
                    LogReceived?.Invoke($"[System] Xray core process failed to start or crashed on startup (Exit Code: {exitCode}). Check Logs tab for details.");
                }
                else
                {
                    Status = "Connected";
                    StatusChanged?.Invoke("Connected");
                    _lastConnectedTime = DateTime.UtcNow; // Start debounce window
                    LogReceived?.Invoke("[System] Connection established successfully with Xray-core.");

                    if (settings.VpnMode == "SystemProxy")
                    {
                        await Task.Run(() => _systemProxyService.EnableProxy("127.0.0.1", _activeHttpPort));
                        LogReceived?.Invoke($"[System] Windows system proxy redirected to 127.0.0.1:{_activeHttpPort} (HTTP).");
                    }
                }
            }
            catch (Exception ex)
            {
                Status = "Error";
                StatusChanged?.Invoke("Error");
                LogReceived?.Invoke($"[System] Error starting VPN: {ex.Message}");
                throw;
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        public async Task DisconnectAsync()
        {
            await _connectionSemaphore.WaitAsync();
            try
            {
                await DisconnectInternalAsync();
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private async Task DisconnectInternalAsync()
        {
            if (Status == "Disconnected" && _process == null)
            {
                return;
            }

            LogReceived?.Invoke("[System] Disconnecting...");

            await Task.Run(() =>
            {
                try
                {
                    if (_process != null && !_process.HasExited)
                    {
                        try
                        {
                            _process.CloseMainWindow();
                            if (!_process.WaitForExit(3000))
                            {
                                _process.Kill(true);
                            }
                        }
                        catch
                        {
                            _process.Kill(true);
                        }
                    }
                    _process = null;
                }
                catch (Exception ex)
                {
                    LogReceived?.Invoke($"[System] Error stopping Xray process: {ex.Message}");
                }

                KillGhostProcesses();

                if (_activeSettings?.VpnMode == "SystemProxy")
                {
                    try { _systemProxyService.DisableProxy(); } catch { }
                }
            });

            if (_activeSettings?.VpnMode == "SystemProxy")
            {
                LogReceived?.Invoke("[System] System proxy disabled.");
            }

            Status = "Disconnected";
            StatusChanged?.Invoke("Disconnected");
            LogReceived?.Invoke("[System] Disconnected.");
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void GenerateConfig(Server server, AppSettings settings, string outputPath, IncyRoutingProfile? routingProfile = null, int socksPort = 10808, int httpPort = 10809)
        {
            var inbounds = new List<object>();

            string listenIp = settings.AllowLan ? "0.0.0.0" : "127.0.0.1";

            // 1. SOCKS5 Inbound
            inbounds.Add(new
            {
                tag = "socks-in",
                port = socksPort,
                listen = listenIp,
                protocol = "socks",
                settings = new
                {
                    auth = "noauth",
                    udp = true
                },
                sniffing = new
                {
                    enabled = true,
                    destOverride = new[] { "http", "tls", "quic" },
                    routeOnly = true
                }
            });

            // 2. HTTP Inbound
            inbounds.Add(new
            {
                tag = "http-in",
                port = httpPort,
                listen = listenIp,
                protocol = "http",
                settings = new
                {
                    timeout = 360
                },
                sniffing = new
                {
                    enabled = true,
                    destOverride = new[] { "http", "tls", "quic" }
                }
            });

            // 3. TUN Inbound (if enabled)
            if (settings.VpnMode == "TUN")
            {
                inbounds.Add(new
                {
                    tag = "tun-in",
                    port = 0,
                    protocol = "tun",
                    settings = new
                    {
                        name = "wintun",
                        mtu = 1500,
                        address = new[] { "172.19.0.1/30" },
                        autoRoute = true,
                        strictRoute = settings.KillSwitchEnabled
                    },
                    sniffing = new
                    {
                        enabled = true,
                        destOverride = new[] { "http", "tls", "quic" }
                    }
                });
            }

            // Outbounds
            var outbounds = new List<object>();

            // Proxy Outbound (Index 0)
            var proxyOutbound = BuildProxyOutbound(server, settings);
            outbounds.Add(proxyOutbound);

            // Direct Outbound (Index 1)
            outbounds.Add(new
            {
                protocol = "freedom",
                tag = "direct"
            });

            // Block Outbound (Index 2)
            outbounds.Add(new
            {
                protocol = "blackhole",
                tag = "block"
            });

            // Routing rules
            var rules = new List<object>();

            // Base DNS routing rule
            rules.Add(new
            {
                type = "field",
                inboundTag = new[] { "socks-in", "http-in", "tun-in" },
                port = 53,
                outboundTag = "direct"
            });

            // Split tunneling bypass domains
            if (settings.SplitTunnelingEnabled && settings.BypassDomains.Count > 0)
            {
                rules.Add(new
                {
                    type = "field",
                    domain = settings.BypassDomains,
                    outboundTag = "direct"
                });
            }

            // Split tunneling bypass processes
            if (settings.SplitTunnelingEnabled && settings.BypassProcesses.Count > 0)
            {
                rules.Add(new
                {
                    type = "field",
                    process = settings.BypassProcesses,
                    outboundTag = "direct"
                });
            }

            // Default GeoIP LAN bypass
            rules.Add(new
            {
                type = "field",
                ip = new[] { "geoip:private" },
                outboundTag = "direct"
            });

            // Convert IncyRoutingProfile rules
            string domainStrategy = "IPIfNonMatch";
            if (routingProfile != null)
            {
                if (!string.IsNullOrEmpty(routingProfile.DomainStrategy))
                {
                    domainStrategy = routingProfile.DomainStrategy;
                }

                if (routingProfile.Rules != null && routingProfile.Rules.Count > 0)
                {
                    foreach (var r in routingProfile.Rules)
                    {
                        string rawTag = r.OutboundTag ?? r.Target ?? r.Outbound ?? "proxy";
                        string targetTag = "proxy";
                        if (rawTag.Equals("direct", StringComparison.OrdinalIgnoreCase) || rawTag.Equals("out-direct", StringComparison.OrdinalIgnoreCase) || rawTag.Equals("freedom", StringComparison.OrdinalIgnoreCase))
                            targetTag = "direct";
                        else if (rawTag.Equals("block", StringComparison.OrdinalIgnoreCase) || rawTag.Equals("out-block", StringComparison.OrdinalIgnoreCase) || rawTag.Equals("blackhole", StringComparison.OrdinalIgnoreCase))
                            targetTag = "block";

                        var ruleDict = new System.Collections.Generic.Dictionary<string, object>
                        {
                            { "type", r.Type ?? "field" },
                            { "outboundTag", targetTag }
                        };

                        if (r.Domain != null && r.Domain.Count > 0) ruleDict["domain"] = r.Domain;
                        if (r.Ip != null && r.Ip.Count > 0) ruleDict["ip"] = r.Ip;
                        if (!string.IsNullOrEmpty(r.Port)) ruleDict["port"] = r.Port;
                        if (!string.IsNullOrEmpty(r.Network)) ruleDict["network"] = r.Network;
                        if (r.Protocol != null && r.Protocol.Count > 0) ruleDict["protocol"] = r.Protocol;
                        if (r.InboundTag != null && r.InboundTag.Count > 0) ruleDict["inboundTag"] = r.InboundTag;

                        rules.Add(ruleDict);
                    }
                }
            }

            var config = new
            {
                log = new
                {
                    loglevel = settings.LoggingEnabled ? "info" : "warning"
                },
                inbounds = inbounds,
                outbounds = outbounds,
                routing = new
                {
                    domainStrategy = domainStrategy,
                    rules = rules
                },
                dns = new
                {
                    servers = new object[]
                    {
                        new { address = settings.RemoteDns, port = 53 },
                        "localhost"
                    }
                }
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(config, options);
            WriteSecureFile(outputPath, json);
        }

        private static void WriteSecureFile(string filePath, string content)
        {
            File.WriteAllText(filePath, content, new System.Text.UTF8Encoding(false));
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    var fileSecurity = fileInfo.GetAccessControl();
                    fileSecurity.SetSecurityDescriptorSddlForm("D:P(A;;FA;;;SY)(A;;FA;;;OW)");
                    fileInfo.SetAccessControl(fileSecurity);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to set ACL on {filePath}: {ex.Message}");
                }
            }
        }

        private object BuildProxyOutbound(Server server, AppSettings settings)
        {
            string protocol = string.IsNullOrEmpty(server.Type) ? "vless" : server.Type.ToLowerInvariant();
            
            // Stream Settings
            var streamSettings = new Dictionary<string, object>();
            string transport = string.IsNullOrEmpty(server.Transport) ? "tcp" : server.Transport.ToLowerInvariant();
            streamSettings["network"] = transport;

            if (string.Equals(server.Security, "reality", StringComparison.OrdinalIgnoreCase))
            {
                streamSettings["security"] = "reality";
                streamSettings["realitySettings"] = new
                {
                    show = false,
                    fingerprint = string.IsNullOrEmpty(server.Fingerprint) ? "chrome" : server.Fingerprint,
                    serverName = server.Sni,
                    publicKey = server.PublicKey,
                    shortId = server.ShortId,
                    spiderX = ""
                };
            }
            else if (string.Equals(server.Security, "tls", StringComparison.OrdinalIgnoreCase))
            {
                streamSettings["security"] = "tls";
                streamSettings["tlsSettings"] = new
                {
                    serverName = server.Sni,
                    fingerprint = string.IsNullOrEmpty(server.Fingerprint) ? "chrome" : server.Fingerprint
                };
            }

            // Transport settings
            if (transport == "ws")
            {
                streamSettings["wsSettings"] = new
                {
                    path = string.IsNullOrEmpty(server.Flow) ? "/" : server.Flow,
                    headers = new { Host = server.Sni }
                };
            }
            else if (transport == "grpc")
            {
                streamSettings["grpcSettings"] = new
                {
                    serviceName = string.IsNullOrEmpty(server.Flow) ? "" : server.Flow,
                    multiMode = false
                };
            }

            // Mux
            object? muxSetting = null;
            if (settings.MuxEnabled && !string.Equals(server.Flow, "xtls-rprx-vision", StringComparison.OrdinalIgnoreCase))
            {
                muxSetting = new
                {
                    enabled = true,
                    concurrency = 8
                };
            }

            if (protocol == "vless")
            {
                var vuser = new Dictionary<string, object>
                {
                    ["id"] = server.Uuid,
                    ["encryption"] = "none",
                    ["level"] = 0
                };

                if (!string.IsNullOrEmpty(server.Flow))
                {
                    vuser["flow"] = server.Flow;
                }

                return new
                {
                    protocol = "vless",
                    tag = "proxy",
                    settings = new
                    {
                        vnext = new[]
                        {
                            new
                            {
                                address = server.Address,
                                port = server.Port,
                                users = new[] { vuser }
                            }
                        }
                    },
                    streamSettings = streamSettings,
                    mux = muxSetting
                };
            }
            else if (protocol == "vmess")
            {
                var vmessUser = new Dictionary<string, object>
                {
                    ["id"] = server.Uuid,
                    ["alterId"] = 0,
                    ["security"] = "auto",
                    ["level"] = 0
                };

                return new
                {
                    protocol = "vmess",
                    tag = "proxy",
                    settings = new
                    {
                        vnext = new[]
                        {
                            new
                            {
                                address = server.Address,
                                port = server.Port,
                                users = new[] { vmessUser }
                            }
                        }
                    },
                    streamSettings = streamSettings,
                    mux = muxSetting
                };
            }
            else if (protocol == "trojan")
            {
                return new
                {
                    protocol = "trojan",
                    tag = "proxy",
                    settings = new
                    {
                        servers = new[]
                        {
                            new
                            {
                                address = server.Address,
                                port = server.Port,
                                password = server.Uuid,
                                level = 0
                            }
                        }
                    },
                    streamSettings = streamSettings,
                    mux = muxSetting
                };
            }
            else
            {
                // Shadowsocks / fallback
                string cipher = !string.IsNullOrEmpty(server.Security) ? server.Security : "aes-128-gcm";
                return new
                {
                    protocol = "shadowsocks",
                    tag = "proxy",
                    settings = new
                    {
                        servers = new[]
                        {
                            new
                            {
                                address = server.Address,
                                port = server.Port,
                                password = server.Uuid,
                                method = cipher,
                                level = 0
                            }
                        }
                    },
                    streamSettings = streamSettings,
                    mux = muxSetting
                };
            }
        }

        private async void OnNetworkAddressChanged(object? sender, EventArgs e)
        {
            // Guard 1: Only react when actually connected and not already reconnecting
            if (Status != "Connected" || _isReconnecting || _activeServer == null || _activeSettings == null)
                return;

            // Guard 2: Debounce – ignore events within 15 seconds of a successful connection.
            // This prevents the wintun/TUN adapter from triggering an infinite reconnect loop
            // since it fires NetworkAddressChanged immediately after the VPN interface comes up.
            if ((DateTime.UtcNow - _lastConnectedTime) < _networkChangeDebounce)
            {
                LogReceived?.Invoke("[System] Network change detected but ignored (debounce window active after TUN adapter init).");
                return;
            }

            _isReconnecting = true;
            string msg = LocalizationHelper.GetString("Str_LogNetworkChangeDetected", "[System] Network address change detected. Auto-reconnecting in 2 seconds...");
            LogReceived?.Invoke(msg);

            await Task.Delay(2000);

            try
            {
                if (Status == "Connected")
                {
                    await ConnectAsync(_activeServer, _activeSettings, _activeSubscriptionResult);
                }
            }
            catch (Exception ex)
            {
                string errFmt = LocalizationHelper.GetString("Str_LogNetworkChangeFailed", "[System] Network change auto-reconnect failed: {0}");
                LogReceived?.Invoke(string.Format(errFmt, ex.Message));
            }
            finally
            {
                _isReconnecting = false;
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _connectionSemaphore.Dispose();

            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(true);
                    _process.Dispose();
                }
            }
            catch { }

            KillGhostProcesses();
        }
    }
}
