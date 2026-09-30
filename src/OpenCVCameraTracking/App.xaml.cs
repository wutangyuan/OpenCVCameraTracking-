using System.Windows;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Core.Logging;
using OpenCVCameraTracking.Localization;
using OpenCVCameraTracking.Themes;

namespace OpenCVCameraTracking;

public partial class App : Application
{
    public ApplicationSettings Settings { get; set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        AppLogger.Initialize();
        AppLogger.Info("Application startup");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        Settings = SettingsStore.Load();
        AppLogger.Info($"Settings loaded: language={Settings.Language}, theme={Settings.ThemeMode}, sourceKind={Settings.SelectedSourceKind}");
        ThemeManager.Initialize(this, Settings.ThemeMode);
        LocalizationManager.Apply(Settings.Language);
        SettingsStore.ApplyLocalizedDefaults(Settings);
        base.OnStartup(e);
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.Shutdown();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled dispatcher exception", e.Exception);
        e.Handled = true;
        MessageBox.Show(
            LocalizationManager.Format(
                "UnhandledUiException",
                LocalizationManager.GetExceptionMessage(e.Exception)),
            LocalizationManager.Get("AppTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.Error("Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLogger.Error("Unhandled AppDomain exception", exception);
        }
    }
}
