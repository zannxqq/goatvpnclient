namespace RemnaVpn.Services
{
    public interface ILocalizationService
    {
        void SetLanguage(string languageCode);
        string GetCurrentLanguage();
    }
}
