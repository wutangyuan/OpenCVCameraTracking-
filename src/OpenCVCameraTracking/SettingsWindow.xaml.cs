using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Localization;

namespace OpenCVCameraTracking;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<StreamProfile> _profiles;

    public SettingsWindow(ApplicationSettings settings)
    {
        InitializeComponent();
        Result = settings.DeepClone();
        _profiles = new ObservableCollection<StreamProfile>(Result.Streams);
        ProfilesBox.ItemsSource = _profiles;
        SelectComboTag(LanguageBox, Result.Language);
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
    }

    public ApplicationSettings Result { get; }

    private void ProfilesBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesBox.SelectedItem is not StreamProfile profile)
        {
            return;
        }

        ProfileNameBox.Text = profile.Name;
        ProfileAddressBox.Text = profile.Address;
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
            ProfilesBox.Items.Refresh();
        }
        else
        {
            var profile = new StreamProfile { Name = name, Address = address };
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

    private void SaveButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result.Language = SelectedTag(LanguageBox);
        Result.RtspLowLatency = LowLatencyBox.IsChecked == true;
        Result.FaceConfidence = (float)FaceConfidenceSlider.Value;
        Result.AnimalConfidence = (float)AnimalConfidenceSlider.Value;
        Result.Streams = _profiles.ToList();
        Result.SelectedStreamId = (DefaultStreamBox.SelectedItem as DefaultStreamChoice)?.Id;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowInformation(string resourceKey) =>
        MessageBox.Show(
            this,
            LocalizationManager.Get(resourceKey),
            LocalizationManager.Get("Information"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private static string SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";

    private static void SelectComboTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        comboBox.SelectedIndex = comboBox.SelectedIndex < 0 ? 0 : comboBox.SelectedIndex;
    }

    private sealed record DefaultStreamChoice(string? Id, string Name);
}
