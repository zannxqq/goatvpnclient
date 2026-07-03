using System.Collections.Generic;

namespace RemnaVpn.Models
{
    public class AppSettings
    {
        public string SubscriptionUrl { get; set; } = string.Empty;
        public string SelectedServerId { get; set; } = string.Empty;
        public string SelectedServerName { get; set; } = string.Empty;
        public bool AutoStart { get; set; } = false;
        public string VpnMode { get; set; } = "SystemProxy"; // "SystemProxy" or "TUN"
        public bool SplitTunnelingEnabled { get; set; } = false;
        public List<string> BypassDomains { get; set; } = new();
        public List<string> BypassProcesses { get; set; } = new();
        public bool KillSwitchEnabled { get; set; } = false;
        public bool MuxEnabled { get; set; } = false;
        public bool AllowLan { get; set; } = false;
        public string RemoteDns { get; set; } = "8.8.8.8";
        public string SubscriptionRouteJson { get; set; } = string.Empty;
        public string SubscriptionDnsJson { get; set; } = string.Empty;
        public bool IsFullConfig { get; set; } = false;
        public string FullConfigJson { get; set; } = string.Empty;
        public IncyRoutingProfile? RoutingProfile { get; set; }
        public string Language { get; set; } = "ru"; // "ru" or "en"
        public bool LoggingEnabled { get; set; } = true;
    }
}
