using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCVCameraTracking.Core;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Detection;
using OpenCVCameraTracking.Core.Tracking;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Localization;
using Microsoft.Win32;

namespace OpenCVCameraTracking;

public partial class MainWindow : Window
{
    private ApplicationSettings _settings;
    private CameraTrackingEngine? _engine;
    private WriteableBitmap? _previewBitmap;
    private int _renderPending;
    private bool _isClosing;
    private bool _applyingSettings;

    public MainWindow()
    {
        InitializeComponent();
        _settings = ((App)Application.Current).Settings;
        ApplySettingsToUi();
        Loaded += async (_, _) => await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        StatusText.Text = LocalizationManager.Get("Status_Enumerating");
        try
        {
            var devices = await Task.Run(DirectShowCameraEnumerator.GetVideoInputDevices);
            DeviceBox.ItemsSource = devices;
            DeviceBox.SelectedIndex = devices.Count > 0 ? 0 : -1;
            StatusText.Text = devices.Count > 0
                ? LocalizationManager.Format("Status_FoundCameras", devices.Count)
                : LocalizationManager.Get("Status_NoCamera");
        }
        catch (Exception exception)
        {
            StatusText.Text = LocalizationManager.Format("Status_EnumerationFailed", exception.Message);
        }
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await StopEngineAsync();
            PersistUiSelection();
            var detector = CreateDetector();
            var tracker = new IouMultiObjectTracker(minimumIou: 0.18f, maximumMisses: 12, smoothing: 0.72f);
            _engine = new CameraTrackingEngine(detector, detectionInterval: 1, tracker);
            _engine.FrameReady += EngineOnFrameReady;
            _engine.StatusChanged += EngineOnStatusChanged;
            _engine.Faulted += EngineOnFaulted;

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SetConfigurationEnabled(false);
            StatusText.Text = LocalizationManager.Get("Status_Connecting");
            await _engine.StartAsync(CreateSourceOptions());
        }
        catch (Exception exception)
        {
            await StopEngineAsync();
            MessageBox.Show(
                this,
                exception.Message,
                LocalizationManager.Get("UnableToStart"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void StopButton_OnClick(object sender, RoutedEventArgs e) => await StopEngineAsync();

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
            _ => throw new InvalidOperationException("Unknown detection mode.")
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
            return new CameraSourceOptions
            {
                Kind = CameraSourceKind.Device,
                DeviceIndex = selectedDevice.Index
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

                _previewBitmap.WritePixels(
                    new Int32Rect(0, 0, e.Width, e.Height),
                    e.Pixels,
                    e.Stride,
                    0);
                MetricsText.Text = LocalizationManager.Format("MetricsFormat", e.FramesPerSecond, e.Objects.Count);
            }
            finally
            {
                Interlocked.Exchange(ref _renderPending, 0);
            }
        }, DispatcherPriority.Render);
    }

    private void EngineOnStatusChanged(object? sender, string statusCode) =>
        _ = Dispatcher.InvokeAsync(() => StatusText.Text = LocalizationManager.Get($"Status_{statusCode}"));

    private void EngineOnFaulted(object? sender, Exception exception) =>
        _ = Dispatcher.InvokeAsync(async () =>
        {
            var message = exception.InnerException?.Message ?? exception.Message;
            StatusText.Text = LocalizationManager.Format("Status_Error", message);
            if (!_isClosing)
            {
                MessageBox.Show(
                    this,
                    message,
                    LocalizationManager.Get("ProcessingError"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            await StopEngineAsync();
        });

    private async Task StopEngineAsync()
    {
        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            engine.FrameReady -= EngineOnFrameReady;
            engine.StatusChanged -= EngineOnStatusChanged;
            engine.Faulted -= EngineOnFaulted;
            await engine.DisposeAsync();
        }

        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetConfigurationEnabled(true);
    }

    private void SetConfigurationEnabled(bool enabled)
    {
        SourceKindBox.IsEnabled = enabled;
        DeviceBox.IsEnabled = enabled;
        StreamProfileBox.IsEnabled = enabled;
        AddressBox.IsEnabled = enabled;
        DetectionModeBox.IsEnabled = enabled;
        AnimalModelBox.IsEnabled = enabled;
        ModelPathBox.IsEnabled = enabled;
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

        SaveSettings();
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
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _settings = window.Result;
        ((App)Application.Current).Settings = _settings;
        SettingsStore.Save(_settings);
        LocalizationManager.Apply(_settings.Language);
        ApplySettingsToUi();
    }

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
        choices.AddRange(_settings.Streams.Select(profile => new StreamChoice(profile.Id, profile.Name, profile.Address)));
        StreamProfileBox.ItemsSource = choices;
        var selected = choices.FirstOrDefault(choice => choice.Id == _settings.SelectedStreamId) ?? choices[0];
        StreamProfileBox.SelectedItem = selected;
        AddressBox.Text = string.IsNullOrWhiteSpace(selected.Address)
            ? _settings.LastStreamAddress
            : selected.Address;
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
    }

    private static string SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

    private static void SelectComboTag(ComboBox comboBox, string tag, string fallbackTag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().First(item => item.Tag?.ToString() == fallbackTag);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        PersistUiSelection();
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engine = null;
        base.OnClosing(e);
    }

    private sealed record StreamChoice(string? Id, string Name, string Address);
}
