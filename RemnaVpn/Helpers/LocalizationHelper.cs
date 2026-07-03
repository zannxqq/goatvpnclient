using System.Windows;

namespace RemnaVpn.Helpers
{
    public static class LocalizationHelper
    {
        public static string GetString(string key, string defaultValue)
        {
            try
            {
                if (System.Windows.Application.Current != null && System.Windows.Application.Current.Resources.Contains(key))
                {
                    return System.Windows.Application.Current.Resources[key] as string ?? defaultValue;
                }
            }
            catch
            {
                // Подавляем исключения при обращении к ресурсам в фоновом потоке или при выключении
            }
            return defaultValue;
        }
    }
}
