using System.Windows;
using OpenCVCameraTracking.WpfSample.Configuration;
using OpenCVCameraTracking.WpfSample.Localization;

namespace OpenCVCameraTracking.WpfSample;

public partial class App : Application
{
    public ApplicationSettings Settings { get; set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = SettingsStore.Load();
        LocalizationManager.Apply(Settings.Language);
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
