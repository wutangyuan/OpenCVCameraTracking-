using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Onvif;
using OpenCVCameraTracking.Core.Logging;
using OpenCVCameraTracking.Core.Notifications;
using System.Net;
using OpenCVCameraTracking.Localization;
using OpenCVCameraTracking.Themes;

namespace OpenCVCameraTracking;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<StreamProfile> _profiles;
    private readonly ObservableCollection<CameraDeviceProfile> _cameraProfiles;
    private readonly OnvifDiscoveryService _onvif = new();
    private readonly ObservableCollection<OnvifChoice> _onvifDevices = [];
    private readonly ObservableCollection<OnvifPresetInfo> _presets = [];
    private readonly Func<Window, RestrictedZoneSettings?>? _selectRestrictedZone;
    private readonly Action? _clearRestrictedZoneImmediately;
    private readonly ObservableCollection<NotificationChannelSettings> _notificationChannels;
    private readonly Func<NotificationChannelSettings, Task<NotificationSendResult>>? _testNotification;
    private readonly Action<IReadOnlyList<NotificationChannelSettings>>? _persistNotificationChannelsImmediately;
    private readonly string _initialThemeMode;
    private bool _initializingTheme;

    public SettingsWindow(
        ApplicationSettings settings,
        Func<Window, RestrictedZoneSettings?>? selectRestrictedZone = null,
        Action? clearRestrictedZoneImmediately = null,
        Func<NotificationChannelSettings, Task<NotificationSendResult>>? testNotification = null,
        Action<IReadOnlyList<NotificationChannelSettings>>? persistNotificationChannelsImmediately = null)
    {
        InitializeComponent();
        Result = settings.DeepClone();
        _initialThemeMode = ThemeManager.Normalize(settings.ThemeMode);
        _selectRestrictedZone = selectRestrictedZone;
        _clearRestrictedZoneImmediately = clearRestrictedZoneImmediately;
        _testNotification = testNotification;
        _persistNotificationChannelsImmediately = persistNotificationChannelsImmediately;
        _profiles = new ObservableCollection<StreamProfile>(Result.Streams);
        _cameraProfiles = new ObservableCollection<CameraDeviceProfile>(Result.CameraDevices);
        _notificationChannels = new ObservableCollection<NotificationChannelSettings>(Result.NotificationChannels);
        ProfilesBox.ItemsSource = _profiles;
        CameraProfilesBox.ItemsSource = _cameraProfiles;
        NotificationChannelsBox.ItemsSource = _notificationChannels;
        OnvifDevicesBox.ItemsSource = _onvifDevices;
        PresetsBox.ItemsSource = _presets;
        SelectComboTag(LanguageBox, Result.Language);
        _initializingTheme = true;
        FollowSystemThemeBox.IsChecked = Result.ThemeMode == ThemeManager.SystemMode;
        LightThemeRadioButton.IsChecked = Result.ThemeMode == ThemeManager.Light;
        DarkThemeRadioButton.IsChecked = Result.ThemeMode == ThemeManager.Dark;
        UpdateThemeOptionsEnabled();
        _initializingTheme = false;
        SelectComboTag(BackendBox, Result.PreferredBackend.ToString());
        SelectComboTag(LayoutBox, Result.SelectedLayout);
        LowLatencyBox.IsChecked = Result.RtspLowLatency;
        FaceConfidenceSlider.Value = Result.FaceConfidence;
        AnimalConfidenceSlider.Value = Result.AnimalConfidence;
        RefreshDefaultStreams();
        var selectedProfile = _profiles.FirstOrDefault(profile => profile.Id == Result.SelectedStreamId);
        if (selectedProfile is not null)
        {
            ProfilesBox.SelectedItem = selectedProfile;
        }
        else if (!string.IsNullOrWhiteSpace(Result.LastStreamAddress))
        {
            ProfileAddressBox.Text = Result.LastStreamAddress;
        }

        UpdateConfidenceText();
        UpdateRestrictedZoneSummary();
        UpdateNotificationSummary();
    }

    public ApplicationSettings Result { get; }

    private void ThemeModeControl_OnChanged(object sender, RoutedEventArgs e)
    {
        UpdateThemeOptionsEnabled();
        if (_initializingTheme)
        {
            return;
        }

        if (FollowSystemThemeBox.IsChecked != true &&
            LightThemeRadioButton.IsChecked != true &&
            DarkThemeRadioButton.IsChecked != true)
        {
            LightThemeRadioButton.IsChecked = true;
            return;
        }

        ThemeManager.Apply(GetSelectedThemeMode());
    }

    private string GetSelectedThemeMode() => FollowSystemThemeBox.IsChecked == true
        ? ThemeManager.SystemMode
        : LightThemeRadioButton.IsChecked == true
            ? ThemeManager.Light
            : ThemeManager.Dark;

    private void UpdateThemeOptionsEnabled()
    {
        if (ThemeOptionsPanel is not null)
        {
            ThemeOptionsPanel.IsEnabled = FollowSystemThemeBox.IsChecked != true;
        }
    }

    private void ProfilesBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesBox.SelectedItem is not StreamProfile profile)
        {
            return;
        }

        ProfileNameBox.Text = profile.Name;
        ProfileAddressBox.Text = profile.Address;
        ProfileSubAddressBox.Text = profile.SubAddress;
        ProfileGroupBox.Text = profile.Group;
        ProfileNotesBox.Text = profile.Notes;
        ProfileEnabledBox.IsChecked = profile.Enabled;
    }

    private void AddOrUpdateButton_OnClick(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        var address = ProfileAddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
        {
            ShowInformation("ProfileRequired");
            return;
        }

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("rtsp" or "http" or "https"))
        {
            ShowInformation("InvalidStreamAddress");
            return;
        }

        if (ProfilesBox.SelectedItem is StreamProfile selected)
        {
            selected.Name = name;
            selected.Address = address;
            selected.SubAddress = ProfileSubAddressBox.Text.Trim();
            selected.Group = ProfileGroupBox.Text.Trim();
            selected.Notes = ProfileNotesBox.Text.Trim();
            selected.Enabled = ProfileEnabledBox.IsChecked == true;
            ProfilesBox.Items.Refresh();
        }
        else
        {
            var profile = new StreamProfile
            {
                Name = name,
                Address = address,
                SubAddress = ProfileSubAddressBox.Text.Trim(),
                Group = ProfileGroupBox.Text.Trim(),
                Notes = ProfileNotesBox.Text.Trim(),
                Enabled = ProfileEnabledBox.IsChecked == true
            };
            _profiles.Add(profile);
            ProfilesBox.SelectedItem = profile;
        }

        RefreshDefaultStreams();
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ProfilesBox.SelectedItem is not StreamProfile selected)
        {
            return;
        }

        _profiles.Remove(selected);
        ProfileNameBox.Clear();
        ProfileAddressBox.Clear();
        ProfileSubAddressBox.Clear();
        ProfileGroupBox.Clear();
        ProfileNotesBox.Clear();
        ProfileEnabledBox.IsChecked = true;
        RefreshDefaultStreams();
    }

    private void RefreshDefaultStreams()
    {
        var selectedId = (DefaultStreamBox.SelectedItem as DefaultStreamChoice)?.Id ?? Result.SelectedStreamId;
        var choices = new List<DefaultStreamChoice>
        {
            new(null, LocalizationManager.Get("NoDefaultStream"))
        };
        choices.AddRange(_profiles.Select(profile => new DefaultStreamChoice(profile.Id, profile.Name)));
        DefaultStreamBox.ItemsSource = choices;
        DefaultStreamBox.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId) ?? choices[0];
    }

    private void ConfidenceSlider_OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        UpdateConfidenceText();

    private void UpdateConfidenceText()
    {
        if (FaceConfidenceText is null || AnimalConfidenceText is null)
        {
            return;
        }

        FaceConfidenceText.Text = $"{FaceConfidenceSlider.Value:P0}";
        AnimalConfidenceText.Text = $"{AnimalConfidenceSlider.Value:P0}";
    }

    private void SetRestrictedZoneButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectRestrictedZone is null)
        {
            ShowInformation("RestrictedZoneNoFrame");
            return;
        }

        var zone = _selectRestrictedZone(this);
        if (zone is null)
        {
            return;
        }

        Result.RestrictedZone = zone;
        UpdateRestrictedZoneSummary();
    }

    private void ClearRestrictedZoneButton_OnClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            LocalizationManager.Get("RestrictedZoneClearConfirm"),
            LocalizationManager.Get("RestrictedZoneClearTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Result.RestrictedZone = null;
        _clearRestrictedZoneImmediately?.Invoke();
        UpdateRestrictedZoneSummary();
    }

    private void UpdateRestrictedZoneSummary()
    {
        RestrictedZoneSummaryText.Text = Result.RestrictedZone is { IsValid: true }
            ? LocalizationManager.Get("RestrictedZoneConfigured")
            : LocalizationManager.Get("RestrictedZoneNotSet");
        SetRestrictedZoneButton.IsEnabled = _selectRestrictedZone is not null;
        ClearRestrictedZoneButton.IsEnabled = Result.RestrictedZone is { IsValid: true };
    }

    private void NotificationChannelsBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateNotificationSummary();

    private void NotificationChannelsBox_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(NotificationChannelsBox, source) is not ListBoxItem)
        {
            return;
        }

        EditNotificationButton_OnClick(sender, e);
        e.Handled = true;
    }

    private void UpdateNotificationSummary()
    {
        var enabledCount = _notificationChannels.Count(channel => channel.Enabled);
        NotificationChannelsSummaryText.Text = LocalizationManager.Format(
            "NotificationChannelsSummary",
            _notificationChannels.Count,
            enabledCount);
    }

    private void AddNotificationButton_OnClick(object sender, RoutedEventArgs e)
    {
        var channel = new NotificationChannelSettings
        {
            Name = LocalizationManager.Get("NotificationDefaultName")
        };
        var window = new NotificationEditorWindow(channel, _testNotification) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        _notificationChannels.Add(window.Result);
        NotificationChannelsBox.SelectedItem = window.Result;
        UpdateNotificationSummary();
        PersistNotificationChannelsImmediately();
    }

    private void EditNotificationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (NotificationChannelsBox.SelectedItem is not NotificationChannelSettings selected)
        {
            ShowInformation("NotificationNoSelection");
            return;
        }

        var window = new NotificationEditorWindow(selected, _testNotification) { Owner = this };
        if (window.ShowDialog() != true)
        {
            return;
        }

        var index = _notificationChannels.IndexOf(selected);
        if (index >= 0)
        {
            _notificationChannels[index] = window.Result;
            NotificationChannelsBox.SelectedItem = window.Result;
        }
        UpdateNotificationSummary();
        PersistNotificationChannelsImmediately();
    }

    private void DeleteNotificationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (NotificationChannelsBox.SelectedItem is not NotificationChannelSettings selected)
        {
            ShowInformation("NotificationNoSelection");
            return;
        }

        var result = MessageBox.Show(
            this,
            LocalizationManager.Format("NotificationDeleteConfirm", selected.Name),
            LocalizationManager.Get("DeleteNotification"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _notificationChannels.Remove(selected);
        UpdateNotificationSummary();
        PersistNotificationChannelsImmediately();
    }

    private void PersistNotificationChannelsImmediately()
    {
        _persistNotificationChannelsImmediately?.Invoke(
            _notificationChannels.Select(channel => channel.DeepClone()).ToList());
    }

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result.Language = SelectedTag(LanguageBox);
        Result.ThemeMode = GetSelectedThemeMode();
        Result.PreferredBackend = Enum.TryParse<VideoCaptureBackend>(SelectedTag(BackendBox), out var backend)
            ? backend
            : VideoCaptureBackend.Auto;
        Result.SelectedLayout = SelectedTag(LayoutBox);
        Result.RtspLowLatency = LowLatencyBox.IsChecked == true;
        Result.FaceConfidence = (float)FaceConfidenceSlider.Value;
        Result.AnimalConfidence = (float)AnimalConfidenceSlider.Value;
        Result.Streams = _profiles.ToList();
        Result.CameraDevices = _cameraProfiles.ToList();
        Result.NotificationChannels = _notificationChannels.ToList();
        Result.SelectedStreamId = (DefaultStreamBox.SelectedItem as DefaultStreamChoice)?.Id;
        DialogResult = true;
    }

    private async void DiscoverOnvifButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _onvifDevices.Clear();
            var user = OnvifUserBox.Text.Trim();
            var password = OnvifPasswordBox.Password;
            var credential = string.IsNullOrWhiteSpace(user) ? null : new NetworkCredential(user, password);
            var devices = await _onvif.DiscoverAsync(TimeSpan.FromSeconds(4), credential);
            foreach (var device in devices)
            {
                foreach (var profile in device.Profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.StreamUri)))
                {
                    _onvifDevices.Add(new OnvifChoice(
                        $"{device.Name ?? device.DeviceUri} · {profile.Name}",
                        profile.StreamUri!,
                        device,
                        profile));
                }
            }
            if (_onvifDevices.Count == 0)
            {
                ShowInformation("OnvifNoneFound");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Error("ONVIF discovery failed", exception);
            ShowInformation("OnvifDiscoveryFailed", MessageBoxImage.Error);
        }
    }

    private void ImportOnvifButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OnvifDevicesBox.SelectedItem is not OnvifChoice choice)
        {
            ShowInformation("OnvifSelect");
            return;
        }

        var profile = new StreamProfile
        {
            Name = choice.DisplayName,
            Address = choice.Address,
            Group = "ONVIF",
            Notes = choice.Device.SupportsProfileT ? "Profile T" : "Profile S / legacy"
        };
        _profiles.Add(profile);
        ProfilesBox.SelectedItem = profile;
        RefreshDefaultStreams();
    }

    private void CameraProfilesBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CameraProfilesBox.SelectedItem is not CameraDeviceProfile profile)
        {
            return;
        }

        CameraProfileGroupBox.Text = profile.Group;
        CameraProfileNotesBox.Text = profile.Notes;
        CameraProfileEnabledBox.IsChecked = profile.Enabled;
    }

    private void SaveCameraProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (CameraProfilesBox.SelectedItem is not CameraDeviceProfile profile)
        {
            return;
        }

        profile.Group = CameraProfileGroupBox.Text.Trim();
        profile.Notes = CameraProfileNotesBox.Text.Trim();
        profile.Enabled = CameraProfileEnabledBox.IsChecked == true;
        CameraProfilesBox.Items.Refresh();
    }

    private async void PtzLeftButton_OnClick(object sender, RoutedEventArgs e) => await SendPtzAsync(-0.35f, 0, 0);
    private async void PtzRightButton_OnClick(object sender, RoutedEventArgs e) => await SendPtzAsync(0.35f, 0, 0);
    private async void PtzVerticalButton_OnClick(object sender, RoutedEventArgs e) => await SendPtzAsync(0, 0.35f, 0);
    private async void PtzStopButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OnvifDevicesBox.SelectedItem is not OnvifChoice choice) return;
        try
        {
            await _onvif.StopAsync(choice.Device.DeviceUri, choice.Profile.Token, CreateOnvifCredential());
        }
        catch (Exception exception)
        {
            AppLogger.Warn($"ONVIF PTZ stop failed: {exception.Message}");
        }
    }

    private async Task SendPtzAsync(float pan, float tilt, float zoom)
    {
        if (OnvifDevicesBox.SelectedItem is not OnvifChoice choice || !choice.Device.SupportsPtz)
        {
            ShowInformation("OnvifPtzUnavailable");
            return;
        }

        try
        {
            await _onvif.ContinuousMoveAsync(
                choice.Device.DeviceUri,
                choice.Profile.Token,
                pan,
                tilt,
                zoom,
                TimeSpan.FromMilliseconds(600),
                CreateOnvifCredential());
        }
        catch (Exception exception)
        {
            AppLogger.Warn($"ONVIF PTZ command failed: {exception.Message}");
            ShowInformation("OnvifPtzCommandFailed", MessageBoxImage.Error);
        }
    }

    private NetworkCredential? CreateOnvifCredential()
    {
        var user = OnvifUserBox.Text.Trim();
        return string.IsNullOrWhiteSpace(user) ? null : new NetworkCredential(user, OnvifPasswordBox.Password);
    }

    private async void OnvifDevicesBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) => await RefreshPresetsAsync();

    private async void RefreshPresetsButton_OnClick(object sender, RoutedEventArgs e) => await RefreshPresetsAsync();

    private async Task RefreshPresetsAsync()
    {
        _presets.Clear();
        if (OnvifDevicesBox.SelectedItem is not OnvifChoice choice || !choice.Device.SupportsPtz)
        {
            return;
        }

        try
        {
            var presets = await _onvif.GetPresetsAsync(
                choice.Device.DeviceUri,
                choice.Profile.Token,
                CreateOnvifCredential());
            foreach (var preset in presets) _presets.Add(preset);
        }
        catch (Exception exception)
        {
            AppLogger.Warn($"ONVIF preset query failed: {exception.Message}");
        }
    }

    private async void GotoPresetButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (OnvifDevicesBox.SelectedItem is not OnvifChoice choice || PresetsBox.SelectedItem is not OnvifPresetInfo preset)
        {
            return;
        }

        try
        {
            await _onvif.GotoPresetAsync(choice.Device.DeviceUri, choice.Profile.Token, preset.Token, CreateOnvifCredential());
        }
        catch (Exception exception)
        {
            AppLogger.Warn($"ONVIF goto preset failed: {exception.Message}");
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DialogResult != true)
        {
            ThemeManager.Apply(_initialThemeMode);
        }

        base.OnClosing(e);
    }

    private void ShowInformation(string resourceKey, MessageBoxImage image = MessageBoxImage.Information) =>
        MessageBox.Show(
            this,
            LocalizationManager.Get(resourceKey),
            LocalizationManager.Get("Information"),
            MessageBoxButton.OK,
            image);

    private static string SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedIndex = comboBox.SelectedIndex < 0 ? 0 : comboBox.SelectedIndex;
    }

    private sealed record DefaultStreamChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
    private sealed record OnvifChoice(string DisplayName, string Address, OnvifDeviceInfo Device, OnvifProfileInfo Profile);
}
