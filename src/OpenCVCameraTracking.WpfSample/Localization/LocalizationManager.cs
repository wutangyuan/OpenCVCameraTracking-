using System.Globalization;
using System.Windows;

namespace OpenCVCameraTracking.WpfSample.Localization;

public static class LocalizationManager
{
    private const string DictionaryPrefix = "Languages/Strings.";

    public static string CurrentLanguage { get; private set; } = "zh-CN";

    public static void Apply(string language)
    {
        CurrentLanguage = language is "en-US" ? "en-US" : "zh-CN";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var oldDictionary = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString.Contains(DictionaryPrefix, StringComparison.OrdinalIgnoreCase) == true);
        if (oldDictionary is not null)
        {
            dictionaries.Remove(oldDictionary);
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"Languages/Strings.{CurrentLanguage}.xaml", UriKind.Relative)
        });

        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string Get(string key) =>
        Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
