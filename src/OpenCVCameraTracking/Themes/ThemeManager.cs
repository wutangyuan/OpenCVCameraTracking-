using Microsoft.Win32;
using System.IO;
using System.Windows;
using OpenCVCameraTracking.Core.Logging;

namespace OpenCVCameraTracking.Themes;

public static class ThemeManager
{
    public const string SystemMode = "System";
    public const string Light = "Light";
    public const string Dark = "Dark";

    private static Application? _application;
    private static string _themeMode = SystemMode;

    public static void Initialize(Application application, string themeMode)
    {
        _application = application;
        SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;
        Apply(themeMode);
    }

    public static void Shutdown()
    {
        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        _application = null;
    }

    public static string Normalize(string? themeMode) => themeMode switch
    {
        Light => Light,
        Dark => Dark,
        _ => SystemMode
    };

    public static void Apply(string? themeMode)
    {
        _themeMode = Normalize(themeMode);
        var application = _application ?? Application.Current;
        if (application is null)
        {
            return;
        }

        var dictionaryName = ResolveTheme() == Light ? "Theme.Light.xaml" : "Theme.Dark.xaml";
        var dictionaries = application.Resources.MergedDictionaries;
        var currentIndex = dictionaries
            .Select((dictionary, index) => new { dictionary, index })
            .FirstOrDefault(item => item.dictionary.Source?.OriginalString.Contains("Themes/Theme.", StringComparison.OrdinalIgnoreCase) == true)
            ?.index;
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/OpenCVCameraTracking;component/Themes/{dictionaryName}", UriKind.Relative)
        };

        if (currentIndex is int index)
        {
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        AppLogger.Info($"Theme applied: mode={_themeMode}, resolved={ResolveTheme()}");
    }

    private static string ResolveTheme() => _themeMode == SystemMode
        ? IsSystemLightTheme() ? Light : Dark
        : _themeMode;

    private static bool IsSystemLightTheme()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0) is int value && value != 0;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Unable to read the Windows app theme: {exception.Message}");
            return false;
        }
    }

    private static void SystemEvents_OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_themeMode != SystemMode || _application is null)
        {
            return;
        }

        _ = _application.Dispatcher.BeginInvoke(new Action(() => Apply(SystemMode)));
    }
}
