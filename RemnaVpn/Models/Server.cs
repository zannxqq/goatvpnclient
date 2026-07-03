using CommunityToolkit.Mvvm.ComponentModel;
using RemnaVpn.Helpers;

namespace RemnaVpn.Models
{
    public partial class Server : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private string _id = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private string _name = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private string _address = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        private int _port;

        [ObservableProperty]
        private string _uuid = string.Empty;

        [ObservableProperty]
        private string _type = "vless";

        [ObservableProperty]
        private string _sni = string.Empty;

        [ObservableProperty]
        private string _publicKey = string.Empty;

        [ObservableProperty]
        private string _shortId = string.Empty;

        [ObservableProperty]
        private string _fingerprint = "chrome";

        [ObservableProperty]
        private string _flow = "";

        [ObservableProperty]
        private string _security = "";

        [ObservableProperty]
        private string _transport = "tcp";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FormattedPing))]
        private long? _pingTimeMs;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        [NotifyPropertyChangedFor(nameof(FormattedPing))]
        private string _pingStatus = "— ms";

        public string DisplayName => $"{Name} ({Address}:{Port}) [{PingStatus}]";

        public string FormattedPing => PingTimeMs.HasValue ? $"{PingTimeMs}ms" : PingStatus;
        
        public bool HasJsonModule { get; set; }

        public string ProtocolBadge => string.Equals(Security, "reality", StringComparison.OrdinalIgnoreCase) 
            ? "VLESS REALITY" 
            : (!string.IsNullOrEmpty(Type) ? Type.ToUpper() : "VLESS");
        
        public string TransportBadge => !string.IsNullOrEmpty(Transport) ? Transport.ToUpper() : "TCP";
        
        public int ServerLoad { get; set; } = new System.Random().Next(15, 75);

        public string CountryFlag
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return "🌐";
                var span = Name.AsSpan().Trim();
                if (span.IsEmpty) return "🌐";

                int index = 0;
                while (index < span.Length)
                {
                    char c = span[index];
                    if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c) || 
                        c >= '\u2100' || c == '\uFE0F' || c == '\u200D')
                    {
                        index++;
                    }
                    else if (index > 0 && char.IsWhiteSpace(c))
                    {
                        break;
                    }
                    else if (index == 0)
                    {
                        return "🌐";
                    }
                    else
                    {
                        break;
                    }
                }

                if (index > 0 && index <= span.Length)
                {
                    string flag = span.Slice(0, index).ToString().Trim();
                    return !string.IsNullOrEmpty(flag) ? flag : "🌐";
                }
                return "🌐";
            }
        }

        public string CleanName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Name)) return LocalizationHelper.GetString("Str_VlessServerDefaultName", "VLESS Server");
                string flag = CountryFlag;
                if (flag != "🌐" && Name.StartsWith(flag))
                {
                    string clean = Name.Substring(flag.Length).Trim();
                    return !string.IsNullOrEmpty(clean) ? clean : Name;
                }
                return Name.Trim();
            }
        }
    }
}
