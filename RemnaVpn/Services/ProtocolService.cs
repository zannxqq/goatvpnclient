using Microsoft.Win32;
using System;

namespace RemnaVpn.Services
{
    public static class ProtocolService
    {
        private static readonly string[] SupportedSchemes = { "goatvpn", "goatweb", "remnawave", "remnavpn", "v2ray", "xray" };

        public static void RegisterProtocolHandlers()
        {
            try
            {
                string? processPath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(processPath)) return;

                foreach (var scheme in SupportedSchemes)
                {
                    try
                    {
                        using RegistryKey key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{scheme}", true);
                        key.SetValue("", $"URL:GoatVPN Protocol ({scheme})");
                        key.SetValue("URL Protocol", "");

                        using RegistryKey cmdKey = key.CreateSubKey(@"shell\open\command", true);
                        cmdKey.SetValue("", $"\"{processPath}\" \"%1\"");
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static string NormalizeSubscriptionUrl(string? inputUrl)
        {
            if (string.IsNullOrWhiteSpace(inputUrl)) return string.Empty;
            string url = inputUrl.Trim();

            int schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx > 0)
            {
                string scheme = url.Substring(0, schemeIdx).ToLowerInvariant();
                if (scheme != "http" && scheme != "https")
                {
                    string remainder = url.Substring(schemeIdx + 3).Trim();

                    if (remainder.Contains("url=http", StringComparison.OrdinalIgnoreCase))
                    {
                        int urlParamIdx = remainder.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
                        string paramVal = remainder.Substring(urlParamIdx + 4);
                        int ampersandIdx = paramVal.IndexOf('&');
                        if (ampersandIdx > 0)
                        {
                            paramVal = paramVal.Substring(0, ampersandIdx);
                        }
                        try
                        {
                            string decoded = Uri.UnescapeDataString(paramVal);
                            if (decoded.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                decoded.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            {
                                return decoded;
                            }
                        }
                        catch { }
                    }

                    if (remainder.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        remainder.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return remainder;
                    }

                    return "https://" + remainder;
                }
                return url;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + url;
            }

            return url;
        }
    }
}
