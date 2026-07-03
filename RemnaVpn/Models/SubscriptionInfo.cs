using System;
using CommunityToolkit.Mvvm.ComponentModel;
using RemnaVpn.Helpers;

namespace RemnaVpn.Models
{
    public class SubscriptionInfo : ObservableObject
    {
        private string _title = string.Empty;
        public string Title
        {
            get => _title;
            set
            {
                if (SetProperty(ref _title, value))
                {
                    OnPropertyChanged(nameof(HasTitle));
                }
            }
        }

        private string _announce = string.Empty;
        public string Announce
        {
            get => _announce;
            set
            {
                if (SetProperty(ref _announce, value))
                {
                    OnPropertyChanged(nameof(HasAnnounce));
                }
            }
        }

        private long _upload;
        public long Upload
        {
            get => _upload;
            set
            {
                if (SetProperty(ref _upload, value))
                {
                    OnPropertyChanged(nameof(FormattedTraffic));
                    OnPropertyChanged(nameof(TrafficProgress));
                }
            }
        }

        private long _download;
        public long Download
        {
            get => _download;
            set
            {
                if (SetProperty(ref _download, value))
                {
                    OnPropertyChanged(nameof(FormattedTraffic));
                    OnPropertyChanged(nameof(TrafficProgress));
                }
            }
        }

        private long _total;
        public long Total
        {
            get => _total;
            set
            {
                if (SetProperty(ref _total, value))
                {
                    OnPropertyChanged(nameof(FormattedTraffic));
                    OnPropertyChanged(nameof(TrafficProgress));
                }
            }
        }

        private long _expire;
        public long Expire
        {
            get => _expire;
            set
            {
                if (SetProperty(ref _expire, value))
                {
                    OnPropertyChanged(nameof(FormattedDaysLeft));
                }
            }
        }

        public string SupportUrl { get; set; } = string.Empty;
        public string WebPageUrl { get; set; } = string.Empty;
        public int UpdateIntervalHours { get; set; }

        private bool _hasImportedJson;
        public bool HasImportedJson
        {
            get => _hasImportedJson;
            set => SetProperty(ref _hasImportedJson, value);
        }

        public string EffectiveSupportUrl
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(SupportUrl)) return SupportUrl.Trim();
                if (!string.IsNullOrWhiteSpace(WebPageUrl)) return WebPageUrl.Trim();
                return "https://t.me/remnawave";
            }
        }

        public bool HasTitle => !string.IsNullOrWhiteSpace(Title);

        public bool HasAnnounce => !string.IsNullOrWhiteSpace(Announce);

        public string FormattedDaysLeft
        {
            get
            {
                if (Expire <= 0) return LocalizationHelper.GetString("Str_DaysLeftUnlimited", "Unlimited");
                try
                {
                    var expDate = DateTimeOffset.FromUnixTimeSeconds(Expire).ToLocalTime();
                    var remaining = expDate - DateTimeOffset.Now;
                    if (remaining.TotalDays < 0) return LocalizationHelper.GetString("Str_DaysLeftExpired", "Expired");
                    int days = (int)Math.Ceiling(remaining.TotalDays);
                    return string.Format(LocalizationHelper.GetString("Str_DaysLeftCount", "{0} days left"), days);
                }
                catch
                {
                    return LocalizationHelper.GetString("Str_DaysLeftUnlimited", "Unlimited");
                }
            }
        }

        public string FormattedTraffic
        {
            get
            {
                long used = Upload + Download;
                string usedStr = FormatBytes(used);
                if (Total <= 0)
                {
                    return $"{usedStr} / ∞";
                }
                string totalStr = FormatBytes(Total);
                return $"{usedStr} / {totalStr}";
            }
        }

        public double TrafficProgress
        {
            get
            {
                if (Total <= 0) return 0.0;
                long used = Upload + Download;
                double pct = ((double)used / Total) * 100.0;
                return Math.Min(100.0, Math.Max(0.0, pct));
            }
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double val = bytes;
            int unitIdx = 0;
            while (val >= 1000 && unitIdx < units.Length - 1)
            {
                val /= 1024;
                unitIdx++;
            }
            return $"{val:0.##} {units[unitIdx]}";
        }
    }
}
