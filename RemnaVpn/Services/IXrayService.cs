using System;
using System.Threading.Tasks;
using RemnaVpn.Models;

namespace RemnaVpn.Services
{
    /// <summary>
    /// Contract for the Xray-core VPN engine service.
    /// Manages download, process lifecycle, and config generation for xray.exe.
    /// </summary>
    public interface IXrayService
    {
        /// <summary>Raised when xray.exe outputs a log line.</summary>
        event Action<string> LogReceived;

        /// <summary>Raised when connection state changes: "Disconnected", "Connecting", "Connected", "Error".</summary>
        event Action<string> StatusChanged;

        /// <summary>Current connection state string.</summary>
        string Status { get; }

        /// <summary>Active HTTP local proxy port (e.g. 10809 or fallback port).</summary>
        int ActiveHttpPort { get; }

        /// <summary>Returns true if xray.exe is present on disk and ready to run.</summary>
        bool IsCoreAvailable();

        /// <summary>Downloads xray.exe, geoip.dat, geosite.dat, and wintun.dll if not present.</summary>
        Task InitializeAsync(IProgress<double>? downloadProgress = null);

        /// <summary>Generates config and starts xray.exe for the given server.</summary>
        Task ConnectAsync(Server server, AppSettings settings, SubscriptionResult? subscriptionResult = null);

        /// <summary>Stops the running xray.exe process and cleans up system proxy.</summary>
        Task DisconnectAsync();

        /// <summary>Checks SHA-256 hashes of geoip.dat and geosite.dat and downloads them if missing or outdated.</summary>
        Task DownloadGeoFilesIfNeededAsync(IncyRoutingProfile? profile = null, IProgress<double>? progress = null);
    }
}
