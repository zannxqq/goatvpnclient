using System.Collections.Generic;

namespace RemnaVpn.Models
{
    public class SubscriptionResult
    {
        public List<Server> Servers { get; set; } = [];
        public SubscriptionInfo Info { get; set; } = new();
        public string RouteJson { get; set; } = string.Empty;
        public string DnsJson { get; set; } = string.Empty;
        public bool IsFullConfig { get; set; } = false;
        public string FullConfigJson { get; set; } = string.Empty;
        public IncyRoutingProfile? RoutingProfile { get; set; }
    }
}
