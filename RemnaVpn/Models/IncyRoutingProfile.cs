using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RemnaVpn.Models
{
    public class IncyRoutingProfile
    {
        [JsonPropertyName("domainStrategy")]
        public string DomainStrategy { get; set; } = "IPIfNonMatch";

        [JsonPropertyName("rules")]
        public List<IncyRoutingRule> Rules { get; set; } = [];

        [JsonPropertyName("geoFiles")]
        public List<IncyGeoFile> GeoFiles { get; set; } = [];
    }

    public class IncyRoutingRule
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "field";

        [JsonPropertyName("outboundTag")]
        public string OutboundTag { get; set; } = "proxy";

        [JsonPropertyName("target")]
        public string? Target { get; set; }

        [JsonPropertyName("outbound")]
        public string? Outbound { get; set; }

        [JsonPropertyName("domain")]
        public List<string>? Domain { get; set; }

        [JsonPropertyName("ip")]
        public List<string>? Ip { get; set; }

        [JsonPropertyName("port")]
        public string? Port { get; set; }

        [JsonPropertyName("network")]
        public string? Network { get; set; }

        [JsonPropertyName("protocol")]
        public List<string>? Protocol { get; set; }

        [JsonPropertyName("inboundTag")]
        public List<string>? InboundTag { get; set; }
    }

    public class IncyGeoFile
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;
    }
}
