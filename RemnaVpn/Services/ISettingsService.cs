using RemnaVpn.Models;

namespace RemnaVpn.Services
{
    public interface ISettingsService
    {
        AppSettings Settings { get; }
        void Load();
        void Save();
    }
}
