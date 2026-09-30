using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CvRect = OpenCvSharp.Rect;
using OpenCVCameraTracking.Core.Recognition;
using OpenCVCameraTracking.Core.Logging;
using OpenCVCameraTracking.Localization;

namespace OpenCVCameraTracking;

public partial class WhitelistWindow : Window
{
    private readonly WhitelistRecognitionService _service;
    private readonly Func<string, WhitelistSubjectKind, Task<WhitelistEnrollmentResult>> _enrollCurrent;
    private readonly Func<RegionSelectionResult?>? _selectRegion;
    private readonly Func<CvRect, string, WhitelistSubjectKind, Task<WhitelistEnrollmentResult>>? _enrollRegion;
    private readonly Action? _profilesChanged;

    public WhitelistWindow(
        WhitelistRecognitionService service,
        Func<string, WhitelistSubjectKind, Task<WhitelistEnrollmentResult>> enrollCurrent,
        Func<RegionSelectionResult?>? selectRegion = null,
        Func<CvRect, string, WhitelistSubjectKind, Task<WhitelistEnrollmentResult>>? enrollRegion = null,
        Action? profilesChanged = null)
    {
        InitializeComponent();
        _service = service;
        _enrollCurrent = enrollCurrent;
        _selectRegion = selectRegion;
        _enrollRegion = enrollRegion;
        _profilesChanged = profilesChanged;
        StoragePathText.Text = service.StorageDirectory;
        RefreshProfiles();
    }

    private async void SelectRegionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_selectRegion is null || _enrollRegion is null)
        {
            ShowInformation(LocalizationManager.Get("WhitelistNoFrame"));
            return;
        }

        var region = _selectRegion();
        if (region is null)
        {
            return;
        }

        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Cat"
            ? WhitelistSubjectKind.Cat
            : WhitelistSubjectKind.Face;
        NameBox.Text = region.Name;
        var result = await _enrollRegion(region.Region, region.Name, kind);
        if (result.Status == WhitelistEnrollmentStatus.Success)
        {
            NameBox.Clear();
            RefreshProfiles();
            _profilesChanged?.Invoke();
            ShowInformation(LocalizationManager.Format(
                "WhitelistEnrollSuccess",
                result.Profile!.Name,
                result.Profile.SampleCount));
            return;
        }

        var resourceKey = result.Status switch
        {
            WhitelistEnrollmentStatus.InvalidName => "WhitelistNameRequired",
            WhitelistEnrollmentStatus.NoFrame => "WhitelistNoFrame",
            _ => "WhitelistUnavailable"
        };
        ShowInformation(LocalizationManager.Get(resourceKey));
    }

    private async void EnrollButton_OnClick(object sender, RoutedEventArgs e)
    {
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Cat"
            ? WhitelistSubjectKind.Cat
            : WhitelistSubjectKind.Face;
        var result = await _enrollCurrent(NameBox.Text, kind);
        if (result.Status == WhitelistEnrollmentStatus.Success)
        {
            NameBox.Clear();
            RefreshProfiles();
            _profilesChanged?.Invoke();
            ShowInformation(LocalizationManager.Format(
                "WhitelistEnrollSuccess",
                result.Profile!.Name,
                result.Profile.SampleCount));
            return;
        }

        var resourceKey = result.Status switch
        {
            WhitelistEnrollmentStatus.InvalidName => "WhitelistNameRequired",
            WhitelistEnrollmentStatus.NoFrame => "WhitelistNoFrame",
            WhitelistEnrollmentStatus.NoMatchingTarget => kind == WhitelistSubjectKind.Face
                ? "WhitelistNoFace"
                : "WhitelistNoCat",
            _ => "WhitelistUnavailable"
        };
        ShowInformation(LocalizationManager.Get(resourceKey));
    }

    private bool _isDeleting;

    private async void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isDeleting)
        {
            return;
        }

        if (ProfileList.SelectedItem is not ProfileItem selected)
        {
            ShowInformation(LocalizationManager.Get("WhitelistSelectDelete"));
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            LocalizationManager.Format("WhitelistDeleteConfirm", selected.Name),
            LocalizationManager.Get("WhitelistTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        // Images in the list used to be URI-backed and could keep a PNG handle
        // open. Clear the visual tree before deleting; thumbnails are also loaded
        // into memory by LoadBitmap below.
        ProfileList.SelectedItem = null;
        ProfileList.ItemsSource = null;
        ProfileList.UpdateLayout();
        _isDeleting = true;
        DeleteButton.IsEnabled = false;
        AppLogger.Info($"User requested whitelist deletion: profile={selected.Name}");
        try
        {
            // Deletion, retries, persistence and model rebuilding all run away
            // from the WPF dispatcher.
            var deleted = await _service.DeleteAsync(selected.Id);
            if (deleted)
            {
                RefreshProfiles();
                _profilesChanged?.Invoke();
            }
        }
        catch (IOException)
        {
            RefreshProfiles();
            ShowInformation(LocalizationManager.Get("WhitelistDeleteFailed"));
        }
        catch (UnauthorizedAccessException)
        {
            RefreshProfiles();
            ShowInformation(LocalizationManager.Get("WhitelistDeleteFailed"));
        }
        finally
        {
            _isDeleting = false;
            DeleteButton.IsEnabled = true;
        }
    }

    private void RefreshProfiles()
    {
        var profiles = _service.GetProfiles()
            .Select(profile => new ProfileItem(
                profile.Id,
                profile.Name,
                profile.Kind == WhitelistSubjectKind.Face
                    ? LocalizationManager.Get("WhitelistFace")
                    : LocalizationManager.Get("WhitelistCat"),
                LocalizationManager.Format("WhitelistSampleCount", profile.SampleCount),
                _service.GetSamplePaths(profile.Id)
                    .Select(path => (Path: path, Bitmap: TryLoadBitmap(path)))
                    .Where(item => item.Bitmap is not null)
                    .Select(item => new SampleItem(item.Path, item.Bitmap!))
                    .ToArray()))
            .ToArray();
        ProfileList.ItemsSource = profiles;
    }

    private void Sample_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2 || sender is not Image { DataContext: SampleItem sample })
        {
            return;
        }

        var image = new Image
        {
            Source = sample.Thumbnail,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(12)
        };
        var viewer = new Window
        {
            Title = LocalizationManager.Get("SamplePreview"),
            Owner = this,
            Width = 420,
            Height = 420,
            Content = image,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        viewer.SetResourceReference(Window.BackgroundProperty, "PreviewBackgroundBrush");
        viewer.ShowDialog();
        e.Handled = true;
    }

    private void ShowInformation(string message) => MessageBox.Show(
        this,
        message,
        LocalizationManager.Get("Information"),
        MessageBoxButton.OK,
        MessageBoxImage.Information);

    private BitmapSource? TryLoadBitmap(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                AppLogger.Warn($"Whitelist sample file is missing: {path ?? "<null>"}");
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bitmap = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            AppLogger.Warn($"Unable to load whitelist sample: {path}; {exception.Message}");
            return null;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private sealed record ProfileItem(
        string Id,
        string Name,
        string TypeText,
        string SampleText,
        IReadOnlyList<SampleItem> Samples);

    private sealed record SampleItem(string Path, BitmapSource Thumbnail);
}
