using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Localization;

namespace OpenCVCameraTracking;

public partial class EventLibraryWindow : Window
{
    private readonly CameraEventStore _store;
    private readonly Action<int> _retentionChanged;
    private readonly ObservableCollection<EventListItem> _events = [];
    private bool _initializing = true;

    public EventLibraryWindow(CameraEventStore store, int retentionDays, Action<int> retentionChanged)
    {
        _store = store;
        _retentionChanged = retentionChanged;
        InitializeComponent();
        RetentionBox.SelectedValue = retentionDays.ToString();
        EventListBox.ItemsSource = _events;
        _initializing = false;
        LoadEvents();
    }

    private void LoadEvents()
    {
        StopPlayback();
        _events.Clear();
        try
        {
            foreach (var record in _store.Load(GetSelectedRetentionDays()))
            {
                _events.Add(new EventListItem(record, _store));
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this,
                LocalizationManager.Format("EventOperationFailed", exception.Message),
                LocalizationManager.Get("EventLibraryTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        EmptyEventsText.Visibility = _events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EventListBox.SelectedIndex = _events.Count == 0 ? -1 : 0;
    }

    private int GetSelectedRetentionDays() =>
        int.TryParse((RetentionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var days) ? days : 7;

    private void EventListBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPlayback();
        if (EventListBox.SelectedItem is not EventListItem item)
        {
            SnapshotImage.Source = null;
            EventDetailsText.Text = LocalizationManager.Get("EventSelectPrompt");
            SetMediaButtonsEnabled(false);
            DeleteButton.IsEnabled = false;
            return;
        }

        SnapshotImage.Source = LoadSnapshot(item.SnapshotPath);
        var triggers = string.Join(Environment.NewLine, item.Record.Triggers);
        var mediaError = string.IsNullOrWhiteSpace(item.Record.MediaError)
            ? string.Empty
            : Environment.NewLine + LocalizationManager.Get("EventMediaError") + " " + item.Record.MediaError;
        EventDetailsText.Text = LocalizationManager.Format(
            "EventDetailsFormat",
            item.Record.Timestamp.ToLocalTime(),
            Environment.NewLine,
            item.Record.SourceName,
            triggers,
            item.Record.DurationSeconds) + mediaError;

        if (item.ClipPath is not null)
        {
            EventVideo.Source = new Uri(item.ClipPath);
        }

        SetMediaButtonsEnabled(item.ClipPath is not null);
        DeleteButton.IsEnabled = true;
    }

    private static BitmapImage? LoadSnapshot(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (exception is IOException or UriFormatException or NotSupportedException)
        {
            return null;
        }
    }

    private void SetMediaButtonsEnabled(bool enabled)
    {
        PlayButton.IsEnabled = enabled;
        PauseButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled;
    }

    private void StopPlayback()
    {
        EventVideo.Stop();
        EventVideo.Close();
        EventVideo.Source = null;
    }

    private void RetentionBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || !int.TryParse((RetentionBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var days))
        {
            return;
        }

        _retentionChanged(days);
        LoadEvents();
    }

    private void RefreshButton_OnClick(object sender, RoutedEventArgs e) => LoadEvents();

    private void PlayButton_OnClick(object sender, RoutedEventArgs e) => EventVideo.Play();

    private void PauseButton_OnClick(object sender, RoutedEventArgs e) => EventVideo.Pause();

    private void ExportButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (EventListBox.SelectedItem is not EventListItem { ClipPath: { } clipPath } item)
        {
            return;
        }

        var extension = Path.GetExtension(clipPath);
        var dialog = new SaveFileDialog
        {
            Title = LocalizationManager.Get("EventExportTitle"),
            FileName = $"event-{item.Record.Timestamp.ToLocalTime():yyyyMMdd-HHmmss}{extension}",
            DefaultExt = extension,
            Filter = $"{extension.TrimStart('.').ToUpperInvariant()} video (*{extension})|*{extension}|{LocalizationManager.Get("AllFilesFilter")} (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            File.Copy(clipPath, dialog.FileName, overwrite: true);
            MessageBox.Show(this, LocalizationManager.Get("EventExportDone"),
                LocalizationManager.Get("EventLibraryTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, LocalizationManager.Format("EventOperationFailed", exception.Message),
                LocalizationManager.Get("EventLibraryTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (EventListBox.SelectedItem is not EventListItem item)
        {
            return;
        }

        if (MessageBox.Show(this,
                LocalizationManager.Get("EventDeleteConfirm"),
                LocalizationManager.Get("EventDeleteTitle"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        StopPlayback();
        try
        {
            _store.Delete(item.Record.Id);
            LoadEvents();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, LocalizationManager.Format("EventOperationFailed", exception.Message),
                LocalizationManager.Get("EventLibraryTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        base.OnClosed(e);
    }

    private sealed class EventListItem
    {
        public EventListItem(CameraEventRecord record, CameraEventStore store)
        {
            Record = record;
            SnapshotPath = store.ResolveMediaPath(record.SnapshotFile);
            ClipPath = store.ResolveMediaPath(record.ClipFile);
            SnapshotUri = SnapshotPath is null ? null : new Uri(SnapshotPath);
            TimeText = record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SourceName = record.SourceName;
            Summary = record.Triggers.Count == 0 ? LocalizationManager.Get("EventNoSummary") : string.Join(" · ", record.Triggers);
        }

        public CameraEventRecord Record { get; }
        public string? SnapshotPath { get; }
        public string? ClipPath { get; }
        public Uri? SnapshotUri { get; }
        public string TimeText { get; }
        public string SourceName { get; }
        public string Summary { get; }
    }
}
