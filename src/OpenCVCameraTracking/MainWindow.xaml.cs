using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CvRect = OpenCvSharp.Rect;
using OpenCVCameraTracking.Core;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Detection;
using OpenCVCameraTracking.Core.Recognition;
using OpenCVCameraTracking.Core.Tracking;
using OpenCVCameraTracking.Core.Logging;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Core.Notifications;
using OpenCVCameraTracking.Localization;
using OpenCVCameraTracking.Themes;
using Microsoft.Win32;
using OpenCVCameraTracking.Updates;
using System.Diagnostics;
using System.Media;

namespace OpenCVCameraTracking;

public partial class MainWindow : Window
{
    private ApplicationSettings _settings;
    private readonly WhitelistRecognitionService _whitelistRecognition = new();
    private readonly RecognitionEventStore _recognitionEventStore = new();
    private readonly NotificationService _notificationService = new();
    private readonly Dictionary<int, string> _recognitionStates = [];
    private readonly List<RecentRecognitionEvent> _recentRecognitionEvents = [];
    private readonly StoreUpdateChecker _storeUpdateChecker = new();
    private StoreUpdateInfo? _pendingStoreUpdate;
    private string? _dismissedStoreUpdateVersionForSession;
    private readonly DispatcherTimer _storeUpdateTimer;
    private bool _storeUpdateCheckRunning;
    private readonly DispatcherTimer _unknownAlertTimer;
    private readonly DispatcherTimer _restrictedZoneAlertTimer;
    private CameraTrackingEngine? _engine;
    private CameraSourceOptions? _activeSourceOptions;
    private WriteableBitmap? _previewBitmap;
    private int _renderPending;
    private bool _isClosing;
    private bool _closingCleanupInProgress;
    private bool _allowClose;
    private bool _applyingSettings;
    private int _latestFrameWidth;
    private int _latestFrameHeight;

    public MainWindow()
    {
        InitializeComponent();
        _unknownAlertTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _unknownAlertTimer.Tick += (_, _) =>
        {
            _unknownAlertTimer.Stop();
            UnknownAlertBanner.Visibility = Visibility.Collapsed;
        };
        _restrictedZoneAlertTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _restrictedZoneAlertTimer.Tick += (_, _) =>
        {
            _restrictedZoneAlertTimer.Stop();
            RestrictedZoneAlertBanner.Visibility = Visibility.Collapsed;
        };
        _storeUpdateTimer = new DispatcherTimer
        {
            // Microsoft Store permits update availability checks no more often than every 30 minutes.
            Interval = TimeSpan.FromMinutes(30)
        };
        _storeUpdateTimer.Tick += StoreUpdateTimerOnTick;
        _settings = ((App)Application.Current).Settings;
        ApplySettingsToUi();
        RefreshVersionDisplay();
        Loaded += MainWindowOnLoaded;
    }

    private async void MainWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindowOnLoaded;
        await RefreshDevicesAsync();
        await CheckForStoreUpdateAsync();
        _storeUpdateTimer.Start();
    }

    private async void StoreUpdateTimerOnTick(object? sender, EventArgs e)
    {
        await CheckForStoreUpdateAsync();
    }

    private async Task CheckForStoreUpdateAsync()
    {
        if (_isClosing || _storeUpdateCheckRunning)
        {
            return;
        }

        _storeUpdateCheckRunning = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            StoreUpdateInfo? update;
            try
            {
                update = await _storeUpdateChecker.CheckAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // A slow/offline network must never interrupt the camera UI.
                return;
            }

            if (update is null)
            {
                return;
            }

            if (IsUpdateDismissed(update))
            {
                return;
            }

            var isNewUpdate = _pendingStoreUpdate is null ||
                              update.AvailableVersion > _pendingStoreUpdate.AvailableVersion;
            _pendingStoreUpdate = update;
            RefreshVersionDisplay(update);
            UpdateBanner.Visibility = Visibility.Visible;
            if (isNewUpdate)
            {
                AppLogger.Info($"Microsoft Store update available: current={update.CurrentVersion}, available={update.AvailableVersion}");
            }
        }
        finally
        {
            _storeUpdateCheckRunning = false;
        }
    }

    private void RefreshVersionDisplay(StoreUpdateInfo? update = null)
    {
        var version = update?.CurrentVersion ?? StoreUpdateChecker.GetCurrentVersion();
        VersionText.Text = LocalizationManager.Format("VersionLabel", version.ToString(4));
        if (update is not null)
        {
            UpdateVersionText.Text = LocalizationManager.Format("VersionLabel", update.AvailableVersion.ToString(4));
        }
    }

    private void OpenStoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _pendingStoreUpdate?.StoreUrl ?? "ms-windows-store://search/?query=CameraTracking",
                UseShellExecute = true
            });
            AppLogger.Info("User opened Microsoft Store update link");
        }
        catch (Exception exception)
        {
            AppLogger.Error("Unable to open Microsoft Store", exception);
        }
    }

    private void DismissUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pendingStoreUpdate is null)
        {
            UpdateBanner.Visibility = Visibility.Collapsed;
            return;
        }

        _dismissedStoreUpdateVersionForSession = _pendingStoreUpdate.AvailableVersion.ToString(4);
        UpdateBanner.Visibility = Visibility.Collapsed;
        AppLogger.Info($"User dismissed Microsoft Store update for this session: version={_dismissedStoreUpdateVersionForSession}");
    }

    private bool IsUpdateDismissed(StoreUpdateInfo update) =>
        string.Equals(
            _dismissedStoreUpdateVersionForSession,
            update.AvailableVersion.ToString(4),
            StringComparison.OrdinalIgnoreCase);

    private async Task RefreshDevicesAsync()
    {
        StatusText.Text = LocalizationManager.Get("Status_Enumerating");
        try
        {
            var devices = (await Task.Run(DirectShowCameraEnumerator.GetVideoInputDevices))
                .Select(device => string.IsNullOrWhiteSpace(device.Name)
                    ? device with { Name = LocalizationManager.Format("CameraIndex", device.Index) }
                    : device)
                .ToList();
            var configured = devices.Select(device =>
            {
                var profile = _settings.CameraDevices.FirstOrDefault(item => item.DeviceIndex == device.Index);
                return profile is null
                    ? device
                    : device with
                    {
                        Name = string.IsNullOrWhiteSpace(profile.Name) ? device.Name : profile.Name,
                        Group = profile.Group,
                        Notes = profile.Notes,
                        IsEnabled = profile.Enabled
                    };
            }).ToList();
            foreach (var device in configured)
            {
                if (_settings.CameraDevices.All(profile => profile.DeviceIndex != device.Index))
                {
                    _settings.CameraDevices.Add(new CameraDeviceProfile
                    {
                        DeviceIndex = device.Index,
                        Name = device.Name
                    });
                }
            }
            SaveSettings();
            DeviceBox.ItemsSource = configured;
            DeviceBox.SelectedIndex = configured.FindIndex(device => device.IsEnabled);
            StatusText.Text = configured.Count > 0
                ? LocalizationManager.Format("Status_FoundCameras", configured.Count)
                : LocalizationManager.Get("Status_NoCamera");
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationManager.Format(
                "Status_EnumerationFailed",
                LocalizationManager.GetExceptionMessage(exception));
        }
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppLogger.Info("User clicked start tracking");
            await StopEngineAsync();
            PersistUiSelection();
            var detector = CreateDetector();
            var tracker = new IouMultiObjectTracker(minimumIou: 0.18f, maximumMisses: 8, smoothing: 0.72f);
            _recognitionStates.Clear();
            _recentRecognitionEvents.Clear();
            _engine = new CameraTrackingEngine(
                detector,
                detectionInterval: 1,
                tracker: tracker,
                whitelistRecognition: _whitelistRecognition,
                restrictedZone: ToCoreRestrictedZone(_settings.RestrictedZone));
            _engine.FrameReady += EngineOnFrameReady;
            _engine.StatusChanged += EngineOnStatusChanged;
            _engine.SourceStatusChanged += EngineOnSourceStatusChanged;
            _engine.Faulted += EngineOnFaulted;
            _engine.RestrictedZoneAlerted += EngineOnRestrictedZoneAlerted;

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SetConfigurationEnabled(false);
            StatusText.Text = LocalizationManager.Get("Status_Connecting");
            _activeSourceOptions = CreateSourceOptions();
            await _engine.StartAsync(_activeSourceOptions);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Unable to start tracking", exception);
            await StopEngineAsync();
            MessageBox.Show(
                this,
                LocalizationManager.GetExceptionMessage(exception),
                LocalizationManager.Get("UnableToStart"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs e) => await StopEngineAsync();

    private async void ReconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_engine is null || _activeSourceOptions is null)
        {
            return;
        }

        try
        {
            StatusText.Text = LocalizationManager.Get("Status_Reconnecting");
            await _engine.ReconnectAsync(_activeSourceOptions);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Manual video source reconnect failed", exception);
            StatusText.Text = LocalizationManager.Format(
                "Status_Error",
                LocalizationManager.GetExceptionMessage(exception));
        }
    }

    private IObjectDetector CreateDetector()
    {
        var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
        return SelectedTag(DetectionModeBox) switch
        {
            "Face" => new YuNetFaceDetector(
                Path.Combine(modelDirectory, "face_detection_yunet_2023mar.onnx"),
                _settings.FaceConfidence),
            "Haar" => new HaarFaceDetector(
                Path.Combine(modelDirectory, "haarcascade_frontalface_default.xml")),
            "Animal" => CreateAnimalDetector(),
            "PersonAnimal" => new CompositeObjectDetector(
            [
                new YuNetFaceDetector(
                    Path.Combine(modelDirectory, "face_detection_yunet_2023mar.onnx"),
                    _settings.FaceConfidence),
                CreateAnimalDetector()
            ]),
            _ => throw new InvalidOperationException(LocalizationManager.Get("UnknownDetectionMode"))
        };
    }

    private IObjectDetector CreateAnimalDetector()
    {
        if (SelectedTag(AnimalModelBox) == "BuiltIn")
        {
            var modelDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
            return new YoloXOnnxDetector(
                Path.Combine(modelDirectory, "object_detection_yolox_2022nov_int8.onnx"),
                confidenceThreshold: _settings.AnimalConfidence);
        }

        return CreateCustomAnimalDetector();
    }

    private IObjectDetector CreateCustomAnimalDetector()
    {
        var modelPath = ModelPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new InvalidOperationException(LocalizationManager.Get("SelectModelError"));
        }

        return new YoloOnnxDetector(
            modelPath,
            labels: YoloOnnxDetector.CocoLabels,
            allowedLabels: YoloOnnxDetector.CocoAnimalLabels,
            confidenceThreshold: _settings.AnimalConfidence);
    }

    private CameraSourceOptions CreateSourceOptions()
    {
        var kind = SelectedTag(SourceKindBox);
        if (kind == "Device")
        {
            var selectedDevice = DeviceBox.SelectedItem as CameraDeviceInfo
                ?? throw new InvalidOperationException(LocalizationManager.Get("SelectCameraError"));
            if (!selectedDevice.IsEnabled)
            {
                throw new InvalidOperationException(LocalizationManager.Get("CameraDisabled"));
            }
            return new CameraSourceOptions
            {
                Kind = CameraSourceKind.Device,
                DeviceIndex = selectedDevice.Index,
                PreferredBackend = _settings.PreferredBackend
            };
        }

        var address = AddressBox.Text.Trim();
        if (kind == "Stream" && !address.Contains("://", StringComparison.Ordinal))
        {
            address = "rtsp://" + address;
            AddressBox.Text = address;
        }

        return new CameraSourceOptions
        {
            Kind = kind == "File" ? CameraSourceKind.File : CameraSourceKind.Stream,
            Address = address,
            SubAddress = (StreamProfileBox.SelectedItem as StreamChoice)?.SubAddress,
            UseSubStream = SelectedTag(StreamVariantBox) == "Sub",
            PreferredBackend = _settings.PreferredBackend,
            PreferTcpForRtsp = true,
            LowLatencyMode = _settings.RtspLowLatency,
            OpenTimeoutMilliseconds = 5_000,
            ReadTimeoutMilliseconds = 3_000
        };
    }

    private void EngineOnFrameReady(object? sender, FrameReadyEventArgs e)
    {
        if (Interlocked.Exchange(ref _renderPending, 1) == 1)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            try
            {
                // A frame may already be queued when Stop is clicked. Ignore
                // callbacks from that disposed engine so the preview stays blank.
                if (_isClosing || !ReferenceEquals(sender, _engine))
                {
                    return;
                }

                if (_previewBitmap is null ||
                    _previewBitmap.PixelWidth != e.Width ||
                    _previewBitmap.PixelHeight != e.Height)
                {
                    _previewBitmap = new WriteableBitmap(
                        e.Width,
                        e.Height,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null);
                    PreviewImage.Source = _previewBitmap;
                    PreviewPlaceholder.Visibility = Visibility.Collapsed;
                }

                _latestFrameWidth = e.Width;
                _latestFrameHeight = e.Height;

                _previewBitmap.WritePixels(
                    new Int32Rect(0, 0, e.Width, e.Height),
                    e.Pixels,
                    e.Stride,
                    0);
                MetricsText.Text = LocalizationManager.Format(
                    "SourceMetricsFormat",
                    e.FramesPerSecond,
                    e.Objects.Count,
                    e.SourceLatencyMilliseconds,
                    e.Width,
                    e.Height);
                HandleRecognitionEvents(e.Objects);
            }
            finally
            {
                Interlocked.Exchange(ref _renderPending, 0);
            }
        }, DispatcherPriority.Render);
    }

    private void EngineOnStatusChanged(object? sender, string statusCode) =>
        _ = Dispatcher.InvokeAsync(() => StatusText.Text = LocalizationManager.Get($"Status_{statusCode}"));

    private void EngineOnSourceStatusChanged(object? sender, VideoSourceStatusEventArgs e) =>
        _ = Dispatcher.InvokeAsync(() =>
        {
            var status = LocalizationManager.Get($"Status_{e.Status}");
            if (e.ReconnectAttempt > 0)
            {
                status += $" ({e.ReconnectAttempt})";
            }

            if (!string.IsNullOrWhiteSpace(e.Detail))
            {
                status += $" {LocalizationManager.GetOrOriginal(e.Detail)}";
            }

            StatusText.Text = status;
        });

    private void EngineOnFaulted(object? sender, Exception exception)
    {
        if (_isClosing)
        {
            return;
        }

        var operation = Dispatcher.InvokeAsync(() => HandleEngineFaultAsync(exception));
        _ = ObserveBackgroundTaskAsync(operation.Task.Unwrap(), "Tracking fault cleanup failed");
    }

    private void EngineOnRestrictedZoneAlerted(object? sender, RestrictedZoneAlertEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_isClosing || !ReferenceEquals(sender, _engine))
            {
                return;
            }

            var subject = e.Target.Label.Equals("cat", StringComparison.OrdinalIgnoreCase)
                ? LocalizationManager.Get("RestrictedZoneCat")
                : LocalizationManager.Get("RestrictedZonePerson");
            var description = LocalizationManager.Format("RestrictedZoneAlert", subject);
            RestrictedZoneAlertText.Text = description;
            RestrictedZoneAlertBanner.Visibility = Visibility.Visible;
            _restrictedZoneAlertTimer.Stop();
            _restrictedZoneAlertTimer.Start();
            SystemSounds.Exclamation.Play();

            if (RecognitionLogBox.Items.Count == 1 &&
                RecognitionLogBox.Items[0] is ListBoxItem placeholder &&
                !placeholder.IsHitTestVisible)
            {
                RecognitionLogBox.Items.Clear();
            }

            RecognitionLogBox.Items.Insert(
                0,
                LocalizationManager.Format("RecognitionEventFormat", e.Timestamp.ToLocalTime(), description));
            while (RecognitionLogBox.Items.Count > 30)
            {
                RecognitionLogBox.Items.RemoveAt(RecognitionLogBox.Items.Count - 1);
            }

            var notificationBody = LocalizationManager.Format(
                "RestrictedZoneNotificationBody",
                description,
                e.Timestamp.ToLocalTime(),
                e.Target.Id);
            var notificationTask = SendAlertNotificationsAsync(
                NotificationEventCatalog.RestrictedZoneEntered,
                new NotificationMessage(LocalizationManager.Get("RestrictedZoneNotificationTitle"), notificationBody));
            _ = ObserveBackgroundTaskAsync(notificationTask, "Restricted-zone notification dispatch failed");
        });
    }

    private async Task SendAlertNotificationsAsync(string eventKey, NotificationMessage message)
    {
        var results = await _notificationService.SendAlertAsync(_settings.NotificationChannels, message, eventKey);
        foreach (var delivery in results.Where(delivery => !delivery.Result.Success))
        {
            AppLogger.Warn(
                $"Notification channel failed: kind={delivery.Channel.Kind}, name={delivery.Channel.Name}, detail={delivery.Result.Detail}");
        }
    }

    private async Task HandleEngineFaultAsync(Exception exception)
    {
        if (_isClosing)
        {
            return;
        }

        AppLogger.Error("Tracking engine reported a fault", exception);
        var message = LocalizationManager.GetExceptionMessage(exception.InnerException ?? exception);
        StatusText.Text = LocalizationManager.Format("Status_Error", message);
        MessageBox.Show(
            this,
            message,
            LocalizationManager.Get("ProcessingError"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        await StopEngineAsync();
    }

    private static async Task ObserveBackgroundTaskAsync(Task task, string message)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLogger.Error(message, exception);
        }
    }

    private async Task StopEngineAsync()
    {
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            AppLogger.Info("Stopping tracking engine");
            engine.FrameReady -= EngineOnFrameReady;
            engine.StatusChanged -= EngineOnStatusChanged;
            engine.SourceStatusChanged -= EngineOnSourceStatusChanged;
            engine.Faulted -= EngineOnFaulted;
            engine.RestrictedZoneAlerted -= EngineOnRestrictedZoneAlerted;
            await engine.DisposeAsync();
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetConfigurationEnabled(true);
        _previewBitmap = null;
        _activeSourceOptions = null;
        _latestFrameWidth = 0;
        _latestFrameHeight = 0;
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        _restrictedZoneAlertTimer.Stop();
        RestrictedZoneAlertBanner.Visibility = Visibility.Collapsed;
        MetricsText.Text = LocalizationManager.Format("MetricsFormat", 0d, 0);
    }

    private void SetConfigurationEnabled(bool enabled)
    {
        SourceKindBox.IsEnabled = enabled;
        DeviceBox.IsEnabled = enabled;
        StreamProfileBox.IsEnabled = enabled;
        StreamVariantBox.IsEnabled = enabled;
        AddressBox.IsEnabled = enabled;
        DetectionModeBox.IsEnabled = enabled;
        AnimalModelBox.IsEnabled = enabled;
        ModelPathBox.IsEnabled = enabled;
    }

    private RestrictedZoneSettings? SelectRestrictedZoneFromCurrentFrame(Window owner)
    {
        if (_previewBitmap is null || _latestFrameWidth <= 0 || _latestFrameHeight <= 0)
        {
            MessageBox.Show(owner, LocalizationManager.Get("RestrictedZoneNoFrame"),
                LocalizationManager.Get("Information"), MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var window = new RestrictedZoneSelectionWindow(_previewBitmap, _latestFrameWidth, _latestFrameHeight)
        {
            Owner = owner
        };
        if (window.ShowDialog() != true || window.SelectedRegion is not { } region)
        {
            return null;
        }

        return RestrictedZoneSettings.FromPixelRect(
            region,
            _latestFrameWidth,
            _latestFrameHeight);
    }

    private void SourceKindBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || SourceKindBox.SelectedItem is null)
        {
            return;
        }

        _settings.SelectedSourceKind = SelectedTag(SourceKindBox);
        UpdateSourcePanels();
        SaveSettings();
    }

    private void UpdateSourcePanels()
    {
        var tag = SelectedTag(SourceKindBox);
        DevicePanel.Visibility = tag == "Device" ? Visibility.Visible : Visibility.Collapsed;
        AddressPanel.Visibility = tag == "Device" ? Visibility.Collapsed : Visibility.Visible;
        SavedStreamPanel.Visibility = tag == "Stream" ? Visibility.Visible : Visibility.Collapsed;
        BrowseVideoButton.Visibility = tag == "File" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "Stream" && string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            AddressBox.Text = _settings.LastStreamAddress;
        }
    }

    private void StreamProfileBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || StreamProfileBox.SelectedItem is not StreamChoice choice)
        {
            return;
        }

        _settings.SelectedStreamId = choice.Id;
        if (!string.IsNullOrWhiteSpace(choice.Address))
        {
            AddressBox.Text = choice.Address;
            _settings.LastStreamAddress = choice.Address;
        }

        _settings.SelectedStreamVariant = "Main";
        SelectComboTag(StreamVariantBox, "Main", "Main");
        StreamVariantBox.IsEnabled = !string.IsNullOrWhiteSpace(choice.SubAddress);

        SaveSettings();
    }

    private void StreamVariantBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || StreamProfileBox.SelectedItem is not StreamChoice choice)
        {
            return;
        }

        AddressBox.Text = SelectedTag(StreamVariantBox) == "Sub" && !string.IsNullOrWhiteSpace(choice.SubAddress)
            ? choice.SubAddress
            : choice.Address;
        _settings.SelectedStreamVariant = SelectedTag(StreamVariantBox);
        SaveSettings();
    }

    private async void DiagnoseButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            PersistUiSelection();
            var detector = new EmptyObjectDetector();
            await using var engine = new CameraTrackingEngine(detector);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var result = await engine.DiagnoseAsync(CreateSourceOptions(), timeout.Token);
            var detail = result.Success
                ? $"{result.Width}×{result.Height}, {result.OpenDuration.TotalMilliseconds:F0} ms, {result.Backend}"
                : result.Status == "ReadFailed"
                    ? LocalizationManager.Get("SourceReadFailed")
                    : result.Status == "OpenFailed" && string.IsNullOrWhiteSpace(result.Detail)
                        ? LocalizationManager.Get("SourceOpenFailed")
                        : LocalizationManager.GetOrOriginal(result.Detail ?? result.Status);
            StatusText.Text = LocalizationManager.Format(
                result.Success ? "Status_DiagnosticOk" : "Status_DiagnosticFailed", detail);
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationManager.Format(
                "Status_DiagnosticFailed",
                LocalizationManager.GetExceptionMessage(exception));
            AppLogger.Error("Video source diagnostic failed", exception);
        }
    }

    private void MultiCameraButton_OnClick(object sender, RoutedEventArgs e)
    {
        var sources = BuildMultiPreviewSources(out var currentSourceKey);
        if (sources.Count == 0)
        {
            MessageBox.Show(this, LocalizationManager.Get("NoSourcesForGrid"),
                LocalizationManager.Get("Information"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedKeys = _settings.MultiPreviewSourceKeys
            .Where(key => sources.Any(source => string.Equals(source.Key, key, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (selectedKeys.Count == 0 && currentSourceKey is not null)
        {
            selectedKeys.Add(currentSourceKey);
        }
        if (selectedKeys.Count == 0)
        {
            selectedKeys.Add(sources[0].Key);
        }

        var selectionWindow = new MultiCameraSourceSelectionWindow(sources, selectedKeys) { Owner = this };
        if (selectionWindow.ShowDialog() != true)
        {
            return;
        }

        var selectedSources = selectionWindow.SelectedSources;
        _settings.MultiPreviewSourceKeys = selectedSources.Select(source => source.Key).ToList();
        var currentPreviewEngine = _engine is { IsRunning: true } engine ? engine : null;
        var window = new MultiCameraWindow(selectedSources, _settings, currentSourceKey, currentPreviewEngine) { Owner = this };
        window.ShowDialog();
        _settings.SelectedLayout = window.SelectedLayout;
        _settings.LayoutStreamIds = window.OrderedSourceKeys.ToList();
        SaveSettings();
    }

    private List<MultiPreviewSource> BuildMultiPreviewSources(out string? currentSourceKey)
    {
        var sources = new Dictionary<string, MultiPreviewSource>(StringComparer.OrdinalIgnoreCase);
        currentSourceKey = null;
        void Add(MultiPreviewSource source) => sources.TryAdd(source.Key, source);

        try
        {
            var current = CreateSourceOptions();
            currentSourceKey = current.Kind switch
            {
                CameraSourceKind.Device => $"device:{current.DeviceIndex}",
                CameraSourceKind.File => "current-file",
                _ when (StreamProfileBox.SelectedItem as StreamChoice)?.Id is { } id => $"stream:{id}",
                _ => "current-stream"
            };
            var description = current.Kind switch
            {
                CameraSourceKind.Device => LocalizationManager.Get("LocalPreviewSource"),
                CameraSourceKind.File => LocalizationManager.Get("FilePreviewSource"),
                _ => LocalizationManager.Get("NetworkPreviewSource")
            };
            Add(new MultiPreviewSource(
                currentSourceKey,
                LocalizationManager.Get("CurrentPreviewSource"),
                description,
                current));
        }
        catch (InvalidOperationException)
        {
            // A current source is optional; the saved sources are still selectable.
        }

        if (DeviceBox.ItemsSource is IEnumerable<CameraDeviceInfo> devices)
        {
            foreach (var device in devices.Where(device => device.IsEnabled))
            {
                Add(new MultiPreviewSource(
                    $"device:{device.Index}",
                    device.Name,
                    string.IsNullOrWhiteSpace(device.Group)
                        ? LocalizationManager.Get("LocalPreviewSource")
                        : $"{LocalizationManager.Get("LocalPreviewSource")} · {device.Group}",
                    new CameraSourceOptions
                    {
                        Kind = CameraSourceKind.Device,
                        DeviceIndex = device.Index,
                        PreferredBackend = _settings.PreferredBackend
                    }));
            }
        }

        foreach (var profile in _settings.Streams.Where(profile => profile.Enabled))
        {
            Add(new MultiPreviewSource(
                $"stream:{profile.Id}",
                profile.Name,
                string.IsNullOrWhiteSpace(profile.Group)
                    ? LocalizationManager.Get("NetworkPreviewSource")
                    : $"{LocalizationManager.Get("NetworkPreviewSource")} · {profile.Group}",
                new CameraSourceOptions
                {
                    Kind = CameraSourceKind.Stream,
                    Address = profile.Address,
                    SubAddress = profile.SubAddress,
                    PreferredBackend = _settings.PreferredBackend,
                    LowLatencyMode = _settings.RtspLowLatency,
                    PreferTcpForRtsp = true
                }));
        }

        return sources.Values.OrderBy(source => source.Name, StringComparer.CurrentCulture).ToList();
    }

    private void AddressBox_OnLostFocus(object sender, RoutedEventArgs e)
    {
        if (SelectedTag(SourceKindBox) == "Stream")
        {
            _settings.LastStreamAddress = AddressBox.Text.Trim();
            SaveSettings();
        }
    }

    private void DetectionModeBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || DetectionModeBox.SelectedItem is null)
        {
            return;
        }

        _settings.SelectedDetectionMode = SelectedTag(DetectionModeBox);
        UpdateModelPanels();
        SaveSettings();
    }

    private void AnimalModelBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || AnimalModelBox.SelectedItem is null)
        {
            return;
        }

        _settings.AnimalModelChoice = SelectedTag(AnimalModelBox);
        UpdateModelPanels();
        SaveSettings();
    }

    private void UpdateModelPanels()
    {
        var detectionTag = SelectedTag(DetectionModeBox);
        var animalMode = detectionTag is "Animal" or "PersonAnimal";
        ModelPanel.Visibility = animalMode ? Visibility.Visible : Visibility.Collapsed;
        CustomModelPanel.Visibility = animalMode && SelectedTag(AnimalModelBox) == "Custom"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void RefreshDevicesButton_OnClick(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private void BrowseVideoButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.Get("SelectVideoFile"),
            Filter = LocalizationManager.Get("VideoFileFilter")
        };
        if (dialog.ShowDialog(this) == true)
        {
            AddressBox.Text = dialog.FileName;
        }
    }

    private void BrowseModelButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = LocalizationManager.Get("SelectOnnxModel"),
            Filter = LocalizationManager.Get("OnnxFileFilter")
        };
        if (dialog.ShowDialog(this) == true)
        {
            ModelPathBox.Text = dialog.FileName;
            _settings.CustomAnimalModelPath = dialog.FileName;
            SaveSettings();
        }
    }

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(
            _settings,
            SelectRestrictedZoneFromCurrentFrame,
            ClearRestrictedZoneImmediately,
            SendTestNotificationAsync,
            PersistNotificationChannelsImmediately)
        {
            Owner = this
        };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings = window.Result;
        AppLogger.Info($"User saved settings: language={_settings.Language}, theme={_settings.ThemeMode}, sourceKind={_settings.SelectedSourceKind}");
        ((App)Application.Current).Settings = _settings;
        LocalizationManager.Apply(_settings.Language);
        ThemeManager.Apply(_settings.ThemeMode);
        SettingsStore.ApplyLocalizedDefaults(_settings);
        SettingsStore.Save(_settings);
        ApplySettingsToUi();
        RefreshVersionDisplay(_pendingStoreUpdate);
    }

    private Task<NotificationSendResult> SendTestNotificationAsync(NotificationChannelSettings channel) =>
        _notificationService.SendTestAsync(
            channel,
            new NotificationMessage(
                LocalizationManager.Get("NotificationTestTitle"),
                LocalizationManager.Format("NotificationTestBody", DateTimeOffset.Now)));

    private void ClearRestrictedZoneImmediately()
    {
        _settings.RestrictedZone = null;
        SaveSettings();
        _engine?.SetRestrictedZone(null);
        _restrictedZoneAlertTimer.Stop();
        RestrictedZoneAlertBanner.Visibility = Visibility.Collapsed;
    }

    private void PersistNotificationChannelsImmediately(IReadOnlyList<NotificationChannelSettings> channels)
    {
        _settings.NotificationChannels = channels.Select(channel => channel.DeepClone()).ToList();
        SaveSettings();
    }

    private void WhitelistButton_OnClick(object sender, RoutedEventArgs e)
    {
        var window = new WhitelistWindow(
            _whitelistRecognition,
            (name, kind) => _engine?.EnrollCurrentTargetAsync(name, kind)
                ?? Task.FromResult(new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.NoFrame)),
            SelectRegionFromCurrentFrame,
            (region, name, kind) => _engine?.EnrollCurrentRegionAsync(region, name, kind)
                ?? Task.FromResult(new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.NoFrame)),
            UpdateWhitelistSummary)
        {
            Owner = this
        };
        window.ShowDialog();
        UpdateWhitelistSummary();
    }

    private RegionSelectionResult? SelectRegionFromCurrentFrame()
    {
        if (_previewBitmap is null || _latestFrameWidth <= 0 || _latestFrameHeight <= 0)
        {
            MessageBox.Show(this, LocalizationManager.Get("WhitelistNoFrame"),
                LocalizationManager.Get("Information"), MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        var window = new RegionSelectionWindow(_previewBitmap, _latestFrameWidth, _latestFrameHeight)
        {
            Owner = this
        };
        return window.ShowDialog() == true && window.SelectedRegion is { } region
            ? new RegionSelectionResult(region, window.SelectedName)
            : null;
    }

    private void HandleRecognitionEvents(IReadOnlyList<TrackedObject> objects)
    {
        foreach (var item in objects.Where(candidate => candidate.IsKnown.HasValue))
        {
            var kind = string.Equals(item.Label, "cat", StringComparison.OrdinalIgnoreCase)
                ? WhitelistSubjectKind.Cat
                : WhitelistSubjectKind.Face;
            var state = item.IsKnown == true
                ? $"known:{item.IdentityName}"
                : "unknown";
            if (_recognitionStates.TryGetValue(item.Id, out var previousState) && previousState == state)
            {
                continue;
            }

            _recognitionStates[item.Id] = state;
            if (IsDuplicateRecognitionEvent(item, state))
            {
                continue;
            }

            RememberRecognitionEvent(item, state);
            var typeName = kind == WhitelistSubjectKind.Face
                ? LocalizationManager.Get("WhitelistFace")
                : LocalizationManager.Get("WhitelistCat");
            var description = item.IsKnown == true
                ? LocalizationManager.Format("KnownRecognition", item.IdentityName ?? string.Empty, typeName)
                : LocalizationManager.Get(kind == WhitelistSubjectKind.Face
                    ? "UnknownFaceAlert"
                    : "UnknownCatAlert");

            var eventRecord = new RecognitionEventRecord(
                DateTimeOffset.Now,
                kind,
                item.IdentityName,
                item.IsKnown == true,
                item.Id,
                item.RecognitionDistance,
                item.RecognitionSimilarity);
            _ = Task.Run(() =>
            {
                try
                {
                    _recognitionEventStore.Append(eventRecord);
                }
                catch (IOException)
                {
                    // Recognition must keep running if the local event log is busy.
                }
            });

            if (RecognitionLogBox.Items.Count == 1 &&
                RecognitionLogBox.Items[0] is ListBoxItem placeholder &&
                !placeholder.IsHitTestVisible)
            {
                RecognitionLogBox.Items.Clear();
            }

            RecognitionLogBox.Items.Insert(
                0,
                LocalizationManager.Format("RecognitionEventFormat", DateTimeOffset.Now, description));
            while (RecognitionLogBox.Items.Count > 30)
            {
                RecognitionLogBox.Items.RemoveAt(RecognitionLogBox.Items.Count - 1);
            }

            if (item.IsKnown != true)
            {
                UnknownAlertText.Text = description;
                UnknownAlertBanner.Visibility = Visibility.Visible;
                _unknownAlertTimer.Stop();
                _unknownAlertTimer.Start();

                var notificationBody = LocalizationManager.Format(
                    "AlertNotificationBody",
                    description,
                    DateTimeOffset.Now,
                    item.Id);
                var notificationTask = SendAlertNotificationsAsync(
                    NotificationEventCatalog.UnknownTargetDetected,
                    new NotificationMessage(LocalizationManager.Get("UnknownRecognitionNotificationTitle"), notificationBody));
                _ = ObserveBackgroundTaskAsync(notificationTask, "Unknown-recognition notification dispatch failed");
            }
        }
    }

    private bool IsDuplicateRecognitionEvent(TrackedObject item, string state)
    {
        var now = DateTimeOffset.Now;
        _recentRecognitionEvents.RemoveAll(candidate => now - candidate.Timestamp > TimeSpan.FromSeconds(5));
        return _recentRecognitionEvents.Any(candidate =>
            string.Equals(candidate.Label, item.Label, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.State, state, StringComparison.Ordinal) &&
            IntersectionOverUnion(candidate.Box, item.Box) >= 0.15f);
    }

    private void RememberRecognitionEvent(TrackedObject item, string state) =>
        _recentRecognitionEvents.Add(new RecentRecognitionEvent(
            DateTimeOffset.Now,
            item.Label,
            state,
            item.Box));

    private static float IntersectionOverUnion(CvRect first, CvRect second)
    {
        var intersection = first & second;
        if (intersection.Width <= 0 || intersection.Height <= 0)
        {
            return 0;
        }

        var intersectionArea = intersection.Width * intersection.Height;
        var unionArea = first.Width * first.Height + second.Width * second.Height - intersectionArea;
        return unionArea <= 0 ? 0 : (float)intersectionArea / unionArea;
    }

    private void UpdateWhitelistSummary() =>
        WhitelistSummaryText.Text = LocalizationManager.Format(
            "WhitelistSummary",
            _whitelistRecognition.GetProfiles().Count);

    private sealed record RecentRecognitionEvent(DateTimeOffset Timestamp, string Label, string State, CvRect Box);

    private void ApplySettingsToUi()
    {
        _applyingSettings = true;
        try
        {
            SelectComboTag(SourceKindBox, _settings.SelectedSourceKind, "Device");
            SelectComboTag(DetectionModeBox, _settings.SelectedDetectionMode, "Face");
            SelectComboTag(AnimalModelBox, _settings.AnimalModelChoice, "BuiltIn");
            ModelPathBox.Text = _settings.CustomAnimalModelPath;
            RefreshStreamProfiles();
            UpdateSourcePanels();
            UpdateModelPanels();
            if (_engine is null)
            {
                StatusText.Text = LocalizationManager.Get("Ready");
            }

            MetricsText.Text = LocalizationManager.Format("MetricsFormat", 0d, 0);
            UpdateWhitelistSummary();
            _engine?.SetRestrictedZone(ToCoreRestrictedZone(_settings.RestrictedZone));
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void RefreshStreamProfiles()
    {
        var choices = new List<StreamChoice>
        {
            new(null, LocalizationManager.Get("ManualAddress"), string.Empty)
        };
        choices.AddRange(_settings.Streams.Where(profile => profile.Enabled)
            .Select(profile => new StreamChoice(profile.Id, profile.Name, profile.Address, profile.SubAddress)));
        StreamProfileBox.ItemsSource = choices;
        var selected = choices.FirstOrDefault(choice => choice.Id == _settings.SelectedStreamId) ?? choices[0];
        StreamProfileBox.SelectedItem = selected;
        AddressBox.Text = string.IsNullOrWhiteSpace(selected.Address)
            ? _settings.LastStreamAddress
            : selected.Address;
        SelectComboTag(StreamVariantBox, _settings.SelectedStreamVariant, "Main");
        StreamVariantBox.IsEnabled = !string.IsNullOrWhiteSpace(selected.SubAddress);
    }

    private void PersistUiSelection()
    {
        _settings.SelectedSourceKind = SelectedTag(SourceKindBox);
        _settings.SelectedDetectionMode = SelectedTag(DetectionModeBox);
        _settings.AnimalModelChoice = SelectedTag(AnimalModelBox);
        _settings.CustomAnimalModelPath = ModelPathBox.Text.Trim();
        if (_settings.SelectedSourceKind == "Stream")
        {
            _settings.LastStreamAddress = AddressBox.Text.Trim();
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        ((App)Application.Current).Settings = _settings;
        SettingsStore.Save(_settings);
        AppLogger.Info("Application settings persisted");
    }

    private static RestrictedZone? ToCoreRestrictedZone(RestrictedZoneSettings? settings) =>
        settings is { IsValid: true } ? settings.ToCore() : null;

    private static string SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static void SelectComboTag(ComboBox comboBox, string tag, string fallbackTag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == fallbackTag);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closingCleanupInProgress)
        {
            return;
        }

        _closingCleanupInProgress = true;
        _isClosing = true;
        _storeUpdateTimer.Stop();
        _unknownAlertTimer.Stop();
        _restrictedZoneAlertTimer.Stop();
        try
        {
            PersistUiSelection();
            await StopEngineAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Application shutdown cleanup failed", exception);
        }
        finally
        {
            _whitelistRecognition.Dispose();
            _allowClose = true;
            _closingCleanupInProgress = false;
            // OnClosing can complete synchronously when no capture is active.
            // Closing again on the same call stack re-enters WPF's closing
            // transition and throws InvalidOperationException. Queue the final
            // close after this notification has returned to the dispatcher.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }

    private sealed record StreamChoice(string? Id, string Name, string Address, string SubAddress = "");

    private sealed class EmptyObjectDetector : IObjectDetector
    {
        public IReadOnlyList<Detection> Detect(OpenCvSharp.Mat frame) => [];
        public void Dispose() { }
    }
}
