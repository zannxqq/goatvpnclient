using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using RemnaVpn.Models;

namespace RemnaVpn.Services
{
    public class RemnawaveService : IRemnawaveService
    {
        private static readonly HttpClient HttpClientInstance = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly ICryptographyService _cryptographyService;

        public RemnawaveService(ICryptographyService cryptographyService)
        {
            _cryptographyService = cryptographyService ?? new CryptographyService();
        }

        public RemnawaveService() : this(new CryptographyService())
        {
        }

        public async Task<SubscriptionResult> FetchServersAsync(string subscriptionUrl)
        {
            var result = new SubscriptionResult();
            if (string.IsNullOrWhiteSpace(subscriptionUrl))
            {
                return result;
            }

            // Normalise URL (handles deeplinks like goatvpn://)
            subscriptionUrl = ProtocolService.NormalizeSubscriptionUrl(subscriptionUrl);

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, subscriptionUrl);
                // Crucial for Remnawave to return the correct profile/data format
                request.Headers.UserAgent.Clear();
                string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : (RuntimeInformation.OSArchitecture == Architecture.X86 ? "x86" : "x64");
                request.Headers.UserAgent.ParseAdd($"GoatWebVPN/Xray (Windows; {arch})");
                request.Headers.Add("X-Goat-Client", "Xray");
                request.Headers.Add("x-hwid", _cryptographyService.GenerateHwid());
                request.Headers.Add("x-device-os", "windows");

                using var response = await HttpClientInstance.SendAsync(request);
                response.EnsureSuccessStatusCode();

                if (response.Headers.TryGetValues("Profile-Title", out var titleValues))
                {
                    result.Info.Title = DecodeHeaderValue(string.Join(" ", titleValues));
                }
                if (response.Headers.TryGetValues("Announce", out var announceValues))
                {
                    result.Info.Announce = DecodeHeaderValue(string.Join(" ", announceValues));
                }
                if (response.Headers.TryGetValues("Subscription-Userinfo", out var userinfoValues))
                {
                    ParseUserInfo(string.Join(" ", userinfoValues), result.Info);
                }
                if (response.Headers.TryGetValues("Support-Url", out var supportValues))
                {
                    result.Info.SupportUrl = string.Join(" ", supportValues);
                }
                if (response.Headers.TryGetValues("Profile-Web-Page-Url", out var webPageValues))
                {
                    result.Info.WebPageUrl = string.Join(" ", webPageValues);
                }
                if (response.Headers.TryGetValues("Profile-Update-Interval", out var intervalValues))
                {
                    if (int.TryParse(string.Join(" ", intervalValues), out int interval))
                    {
                        result.Info.UpdateIntervalHours = interval;
                    }
                }

                string? routingUrl = null;
                if (response.Headers.TryGetValues("X-Incy-Routing-Url", out var incyRoutVals))
                    routingUrl = string.Join(" ", incyRoutVals);
                else if (response.Headers.TryGetValues("Incy-Routing-Url", out var incyRoutVals2))
                    routingUrl = string.Join(" ", incyRoutVals2);
                else if (response.Headers.TryGetValues("X-Routing-Url", out var incyRoutVals3))
                    routingUrl = string.Join(" ", incyRoutVals3);
                else if (response.Headers.TryGetValues("Profile-Routing-Url", out var incyRoutVals4))
                    routingUrl = string.Join(" ", incyRoutVals4);

                string responseBody = await response.Content.ReadAsStringAsync();
                result.Servers = ParseContent(responseBody, result);

                await ProcessIncyRoutingAsync(result, routingUrl);

                bool hasImportedJson = !string.IsNullOrEmpty(result.RouteJson) ||
                                       !string.IsNullOrEmpty(result.DnsJson) ||
                                       result.IsFullConfig ||
                                       !string.IsNullOrEmpty(result.FullConfigJson) ||
                                       result.RoutingProfile != null ||
                                       (result.Servers != null && System.Linq.Enumerable.Any(result.Servers, s => s.HasJsonModule));

                result.Info.HasImportedJson = hasImportedJson;
                if (result.Servers != null)
                {
                    foreach (var s in result.Servers)
                    {
                        s.HasJsonModule = hasImportedJson;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching subscription: {ex}");
                throw;
            }
        }

        private List<Server> ParseContent(string content, SubscriptionResult result)
        {
            content = content.Trim();
            if (string.IsNullOrEmpty(content))
            {
                return new List<Server>();
            }

            // 1. Check if raw JSON
            if (content.StartsWith("{") || content.StartsWith("["))
            {
                return ParseJsonConfig(content, result);
            }

            // 2. Check if raw VLESS links (text list) or comment lines
            if (content.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) || content.StartsWith("#") || content.Contains("vless://", StringComparison.OrdinalIgnoreCase))
            {
                return ParseVlessLinks(content, result);
            }

            // 3. Try to decode from Base64
            try
            {
                byte[] decodedBytes = Convert.FromBase64String(content);
                string decodedText = Encoding.UTF8.GetString(decodedBytes).Trim();

                if (decodedText.StartsWith("{") || decodedText.StartsWith("["))
                {
                    return ParseJsonConfig(decodedText, result);
                }
                
                return ParseVlessLinks(decodedText, result);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to parse content as Base64/Links/JSON: {ex}");
                throw new FormatException("Subscription format not recognised.", ex);
            }
        }

        private List<Server> ParseJsonConfig(string json, SubscriptionResult result)
        {
            var servers = new List<Server>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                
                // Extract routing rules if present
                if (doc.RootElement.TryGetProperty("route", out var routeElement))
                {
                    result.RouteJson = routeElement.GetRawText();
                }
                else if (doc.RootElement.TryGetProperty("routing", out var routingElement))
                {
                    result.RouteJson = routingElement.GetRawText();
                }

                if (doc.RootElement.TryGetProperty("dns", out var dnsElement))
                {
                    result.DnsJson = dnsElement.GetRawText();
                }

                // Step 3: Check for Full Config (contains both inbounds and outbounds)
                if (doc.RootElement.TryGetProperty("inbounds", out var _) &&
                    doc.RootElement.TryGetProperty("outbounds", out var _))
                {
                    result.IsFullConfig = true;

                    // Use JsonNode for patching
                    var rootNode = JsonNode.Parse(json)?.AsObject();
                    if (rootNode != null)
                    {
                        // Patch inbounds ports to 10808 (socks) and 10809 (http)
                        if (rootNode.TryGetPropertyValue("inbounds", out var inboundsNode) && inboundsNode is JsonArray inboundsArray)
                        {
                            foreach (var inbound in inboundsArray)
                            {
                                if (inbound is JsonObject inboundObj)
                                {
                                    string protocol = inboundObj["protocol"]?.ToString()?.ToLowerInvariant() ?? "";
                                    string tag = inboundObj["tag"]?.ToString()?.ToLowerInvariant() ?? "";

                                    if (protocol == "socks" || tag.Contains("socks"))
                                    {
                                        inboundObj["port"] = 10808;
                                    }
                                    else if (protocol == "http" || tag.Contains("http"))
                                    {
                                        inboundObj["port"] = 10809;
                                    }
                                }
                            }
                        }

                        // Patch burstObservatory / stats
                        if (rootNode.ContainsKey("burstObservatory") && !rootNode.ContainsKey("stats"))
                        {
                            rootNode["stats"] = new JsonObject();
                        }

                        var options = new JsonSerializerOptions { WriteIndented = true };
                        result.FullConfigJson = rootNode.ToJsonString(options);
                    }
                }

                if (doc.RootElement.TryGetProperty("outbounds", out var outboundsElement) && 
                    outboundsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var outbound in outboundsElement.EnumerateArray())
                    {
                        string protocol = "";
                        if (outbound.TryGetProperty("protocol", out var protoProp))
                            protocol = protoProp.GetString()?.ToLowerInvariant() ?? "";
                        else if (outbound.TryGetProperty("type", out var typeProp))
                            protocol = typeProp.GetString()?.ToLowerInvariant() ?? "";

                        if (protocol == "vless" || protocol == "trojan" || protocol == "vmess" || protocol == "shadowsocks")
                        {
                            var server = new Server
                            {
                                Id = Guid.NewGuid().ToString(),
                                Type = protocol,
                                HasJsonModule = true,
                                Name = outbound.TryGetProperty("tag", out var tagProp) ? tagProp.GetString() ?? $"{protocol.ToUpper()} Server" : $"{protocol.ToUpper()} Server",
                                Address = "",
                                Port = 0,
                                Uuid = ""
                            };

                            if (outbound.TryGetProperty("settings", out var settingsProp) && settingsProp.ValueKind == JsonValueKind.Object)
                            {
                                if (settingsProp.TryGetProperty("vnext", out var vnextProp) && vnextProp.ValueKind == JsonValueKind.Array && vnextProp.GetArrayLength() > 0)
                                {
                                    var v0 = vnextProp[0];
                                    if (v0.TryGetProperty("address", out var addrProp)) server.Address = addrProp.GetString() ?? "";
                                    if (v0.TryGetProperty("port", out var portProp) && portProp.ValueKind == JsonValueKind.Number) server.Port = portProp.GetInt32();
                                    if (v0.TryGetProperty("users", out var usersProp) && usersProp.ValueKind == JsonValueKind.Array && usersProp.GetArrayLength() > 0)
                                    {
                                        var u0 = usersProp[0];
                                        if (u0.TryGetProperty("id", out var idProp)) server.Uuid = idProp.GetString() ?? "";
                                        if (u0.TryGetProperty("flow", out var flowProp)) server.Flow = flowProp.GetString() ?? "";
                                    }
                                }
                                else if (settingsProp.TryGetProperty("servers", out var srvsProp) && srvsProp.ValueKind == JsonValueKind.Array && srvsProp.GetArrayLength() > 0)
                                {
                                    var s0 = srvsProp[0];
                                    if (s0.TryGetProperty("address", out var addrProp)) server.Address = addrProp.GetString() ?? "";
                                    if (s0.TryGetProperty("port", out var portProp) && portProp.ValueKind == JsonValueKind.Number) server.Port = portProp.GetInt32();
                                    if (s0.TryGetProperty("password", out var pwdProp)) server.Uuid = pwdProp.GetString() ?? "";
                                }
                            }
                            else
                            {
                                if (outbound.TryGetProperty("server", out var serverProp)) server.Address = serverProp.GetString() ?? "";
                                if (outbound.TryGetProperty("server_port", out var portProp) && portProp.ValueKind == JsonValueKind.Number) server.Port = portProp.GetInt32();
                                if (outbound.TryGetProperty("uuid", out var uuidProp)) server.Uuid = uuidProp.GetString() ?? "";
                            }

                            if (outbound.TryGetProperty("flow", out var flowPropTop))
                                server.Flow = flowPropTop.GetString() ?? server.Flow;

                            if (outbound.TryGetProperty("streamSettings", out var streamProp) && streamProp.ValueKind == JsonValueKind.Object)
                            {
                                if (streamProp.TryGetProperty("network", out var netProp)) server.Transport = netProp.GetString() ?? "tcp";
                                if (streamProp.TryGetProperty("security", out var secProp)) server.Security = secProp.GetString() ?? "";

                                if (streamProp.TryGetProperty("realitySettings", out var realProp) && realProp.ValueKind == JsonValueKind.Object)
                                {
                                    server.Security = "reality";
                                    if (realProp.TryGetProperty("serverName", out var sniProp)) server.Sni = sniProp.GetString() ?? "";
                                    if (realProp.TryGetProperty("publicKey", out var pbkProp)) server.PublicKey = pbkProp.GetString() ?? "";
                                    if (realProp.TryGetProperty("shortId", out var sidProp)) server.ShortId = sidProp.GetString() ?? "";
                                    if (realProp.TryGetProperty("fingerprint", out var fpProp)) server.Fingerprint = fpProp.GetString() ?? "chrome";
                                }
                                else if (streamProp.TryGetProperty("tlsSettings", out var tlsSettingsProp) && tlsSettingsProp.ValueKind == JsonValueKind.Object)
                                {
                                    if (tlsSettingsProp.TryGetProperty("serverName", out var sniProp)) server.Sni = sniProp.GetString() ?? "";
                                    if (tlsSettingsProp.TryGetProperty("fingerprint", out var fpProp)) server.Fingerprint = fpProp.GetString() ?? "chrome";
                                }
                            }
                            else if (outbound.TryGetProperty("tls", out var tlsProp) && tlsProp.ValueKind == JsonValueKind.Object)
                            {
                                if (tlsProp.TryGetProperty("server_name", out var sniProp)) server.Sni = sniProp.GetString() ?? "";
                                if (tlsProp.TryGetProperty("reality", out var realityProp) && realityProp.ValueKind == JsonValueKind.Object)
                                {
                                    server.Security = "reality";
                                    if (realityProp.TryGetProperty("public_key", out var pbkProp)) server.PublicKey = pbkProp.GetString() ?? "";
                                    if (realityProp.TryGetProperty("short_id", out var sidProp)) server.ShortId = sidProp.GetString() ?? "";
                                }
                                if (tlsProp.TryGetProperty("utls", out var utlsProp) && utlsProp.ValueKind == JsonValueKind.Object)
                                {
                                    if (utlsProp.TryGetProperty("fingerprint", out var fpProp)) server.Fingerprint = fpProp.GetString() ?? "chrome";
                                }
                            }

                            if (outbound.TryGetProperty("transport", out var transProp) && transProp.ValueKind == JsonValueKind.Object)
                            {
                                if (transProp.TryGetProperty("type", out var transTypeProp)) server.Transport = transTypeProp.GetString() ?? "tcp";
                            }

                            if (!string.IsNullOrEmpty(server.Address) || result.IsFullConfig)
                            {
                                server.Id = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{server.Name}|{server.Address}|{server.Port}|{server.Uuid}"))).ToLowerInvariant();
                                servers.Add(server);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error parsing JSON Xray profile: {ex}");
            }
            return servers;
        }

        private async Task ProcessIncyRoutingAsync(SubscriptionResult result, string? routingUrl)
        {
            try
            {
                // If RouteJson itself is a direct URL to Incy routing profile
                if (string.IsNullOrEmpty(routingUrl) && !string.IsNullOrEmpty(result.RouteJson))
                {
                    string trimmedRoute = result.RouteJson.Trim('"', ' ', '\r', '\n');
                    if (trimmedRoute.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        trimmedRoute.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        routingUrl = trimmedRoute;
                    }
                }

                // 1. Try fetching from external URL if header or body provided one
                if (!string.IsNullOrEmpty(routingUrl))
                {
                    string json = await HttpClientInstance.GetStringAsync(routingUrl);
                    result.RouteJson = json;
                    var profile = JsonSerializer.Deserialize<IncyRoutingProfile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (profile != null && ((profile.Rules?.Count ?? 0) > 0 || (profile.GeoFiles?.Count ?? 0) > 0 || !string.IsNullOrEmpty(profile.DomainStrategy)))
                    {
                        result.RoutingProfile = profile;
                        return;
                    }
                }

                // 2. Try parsing embedded RouteJson as IncyRoutingProfile
                if (!string.IsNullOrEmpty(result.RouteJson) && result.RouteJson.TrimStart().StartsWith("{"))
                {
                    var profile = JsonSerializer.Deserialize<IncyRoutingProfile>(result.RouteJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (profile != null && ((profile.Rules?.Count ?? 0) > 0 || (profile.GeoFiles?.Count ?? 0) > 0 || !string.IsNullOrEmpty(profile.DomainStrategy)))
                    {
                        result.RoutingProfile = profile;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error processing Incy routing profile: {ex}");
            }
        }

        private List<Server> ParseVlessLinks(string linksText, SubscriptionResult result)
        {
            var servers = new List<Server>();
            foreach (ReadOnlySpan<char> rawLine in linksText.AsSpan().EnumerateLines())
            {
                ReadOnlySpan<char> trimmedSpan = rawLine.Trim();
                if (trimmedSpan.IsEmpty) continue;

                if (trimmedSpan.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        servers.Add(ParseVlessUrl(trimmedSpan.ToString()));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error parsing individual VLESS URL: {ex}");
                    }
                }
                else if (trimmedSpan.StartsWith("#"))
                {
                    string comment = trimmedSpan.Slice(1).Trim().ToString();
                    var kv = comment.Split(new[] { ':', '=' }, 2);
                    if (kv.Length == 2)
                    {
                        string key = kv[0].Trim().ToLowerInvariant();
                        string val = kv[1].Trim();

                        switch (key)
                        {
                            case "routing":
                            case "route":
                            case "routing-url":
                            case "incy-routing":
                                result.RouteJson = val;
                                break;
                            case "dns":
                            case "dns-url":
                                result.DnsJson = val;
                                break;
                            case "update-interval":
                            case "interval":
                                if (int.TryParse(val, out int interval)) result.Info.UpdateIntervalHours = interval;
                                break;
                            case "title":
                            case "profile-title":
                            case "name":
                                result.Info.Title = DecodeHeaderValue(val);
                                break;
                            case "support-url":
                            case "support":
                                result.Info.SupportUrl = val;
                                break;
                            case "web-page":
                            case "web-url":
                                result.Info.WebPageUrl = val;
                                break;
                        }
                    }
                }
            }
            return servers;
        }

        private static Server ParseVlessUrl(string url)
        {
            // Format: vless://uuid@host:port?security=reality&sni=sni.com&fp=chrome&pbk=public_key&sid=short_id&type=tcp#ServerName
            string working = url.Substring("vless://".Length);
            string name = "VLESS Server";

            int hashIdx = working.IndexOf('#');
            if (hashIdx >= 0)
            {
                name = Uri.UnescapeDataString(working.Substring(hashIdx + 1));
                working = working.Substring(0, hashIdx);
            }

            int atIdx = working.IndexOf('@');
            if (atIdx < 0)
            {
                throw new FormatException("Missing '@' in VLESS URL.");
            }

            string uuid = working.Substring(0, atIdx);
            string connectionInfo = working.Substring(atIdx + 1);

            string hostAndPort = connectionInfo;
            string query = string.Empty;

            int qIdx = connectionInfo.IndexOf('?');
            if (qIdx >= 0)
            {
                hostAndPort = connectionInfo.Substring(0, qIdx);
                query = connectionInfo.Substring(qIdx + 1);
            }

            string host = hostAndPort;
            int port = 443;
            int colonIdx = hostAndPort.IndexOf(':');
            if (colonIdx >= 0)
            {
                host = hostAndPort.Substring(0, colonIdx);
                if (int.TryParse(hostAndPort.Substring(colonIdx + 1), out int parsedPort))
                {
                    port = parsedPort;
                }
            }

            var server = new Server
            {
                Id = Guid.NewGuid().ToString(),
                Name = name,
                Address = host,
                Port = port,
                Uuid = uuid,
                Type = "vless"
            };

            if (!string.IsNullOrEmpty(query))
            {
                var parts = query.Split('&');
                foreach (var part in parts)
                {
                    var keyValue = part.Split('=');
                    if (keyValue.Length == 2)
                    {
                        string key = keyValue[0].ToLowerInvariant();
                        string val = Uri.UnescapeDataString(keyValue[1]);

                        switch (key)
                        {
                            case "security":
                                server.Security = val;
                                break;
                            case "sni":
                                server.Sni = val;
                                break;
                            case "fp":
                                server.Fingerprint = val;
                                break;
                            case "pbk":
                                server.PublicKey = val;
                                break;
                            case "sid":
                                server.ShortId = val;
                                break;
                            case "flow":
                                server.Flow = val;
                                break;
                            case "type":
                                server.Transport = val;
                                break;
                            case "json":
                            case "module":
                            case "config":
                                server.HasJsonModule = true;
                                break;
                        }
                    }
                }
            }

            if (server.Name.Contains("json", StringComparison.OrdinalIgnoreCase) ||
                query.Contains("json=", StringComparison.OrdinalIgnoreCase) ||
                query.Contains("module=", StringComparison.OrdinalIgnoreCase))
            {
                server.HasJsonModule = true;
            }

            server.Id = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{server.Name}|{server.Address}|{server.Port}|{server.Uuid}"))).ToLowerInvariant();
            return server;
        }

        private static string DecodeHeaderValue(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return string.Empty;
            val = val.Trim();
            if (val.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string b64 = val.Substring("base64:".Length).Trim();
                    byte[] bytes = Convert.FromBase64String(b64);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    return val;
                }
            }
            try
            {
                return Uri.UnescapeDataString(val);
            }
            catch
            {
                return val;
            }
        }

        private static void ParseUserInfo(string userInfoStr, SubscriptionInfo info)
        {
            var parts = userInfoStr.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Trim().Split(new[] { '=' }, 2);
                if (kv.Length == 2)
                {
                    string key = kv[0].Trim().ToLowerInvariant();
                    string val = kv[1].Trim();
                    if (long.TryParse(val, out long num))
                    {
                        switch (key)
                        {
                            case "upload": info.Upload = num; break;
                            case "download": info.Download = num; break;
                            case "total": info.Total = num; break;
                            case "expire": info.Expire = num; break;
                        }
                    }
                }
            }
        }
    }
}
