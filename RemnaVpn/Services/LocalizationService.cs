using System;
using System.Linq;
using System.Windows;

namespace RemnaVpn.Services
{
    public class LocalizationService : ILocalizationService
    {
        private string _currentLanguage = "ru";

        public void SetLanguage(string languageCode)
        {
            if (string.IsNullOrWhiteSpace(languageCode))
                languageCode = "en";

            languageCode = languageCode.ToLowerInvariant();
            _currentLanguage = languageCode;

            try
            {
                if (System.Windows.Application.Current != null)
                {
                    var merged = System.Windows.Application.Current.Resources.MergedDictionaries;
                    
                    // Remove all dictionaries starting with Locales/Strings. except the baseline English
                    var toRemove = merged.Where(d => d.Source != null && 
                                                     d.Source.OriginalString.StartsWith("Locales/Strings.") && 
                                                     !d.Source.OriginalString.EndsWith("Strings.en.xaml")).ToList();
                    foreach (var dict in toRemove)
                    {
                        merged.Remove(dict);
                    }

                    // Ensure English baseline fallback dictionary is always present at the base
                    var hasEnglish = merged.Any(d => d.Source != null && d.Source.OriginalString.EndsWith("Strings.en.xaml"));
                    if (!hasEnglish)
                    {
                        var enUri = new Uri("Locales/Strings.en.xaml", UriKind.Relative);
                        merged.Insert(0, new ResourceDictionary { Source = enUri });
                    }

                    // If target language is not English, load it as an override
                    if (languageCode != "en")
                    {
                        var dictPath = $"Locales/Strings.{languageCode}.xaml";
                        var uri = new Uri(dictPath, UriKind.Relative);
                        merged.Add(new ResourceDictionary { Source = uri });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error switching language: {ex.Message}");
            }
        }

        public string GetCurrentLanguage() => _currentLanguage;
    }
}
