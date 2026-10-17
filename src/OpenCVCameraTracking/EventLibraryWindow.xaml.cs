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
    private readonly EventRecordingService? _recordingService;
    private readonly Action<int> _retentionChanged;
    private readonly ObservableCollection<EventListItem> _events = [];
    private bool _initializing = true;
    private bool _closed;

    public EventLibraryWindow(
        CameraEventStore store,
        int retentionDays,
        Action<int> retentionChanged,
        EventRecordingService? recordingService)
    {
        _store = store;
        _recordingService = recordingService;
        _retentionChanged = retentionChanged;
        InitializeComponent();
        RetentionBox.SelectedValue = retentionDays.ToString();
        EventListBox.ItemsSource = _events;
        _initializing = false;
        if (_recordingService is not null)
        {
            _recordingService.RecordsChanged += RecordingService_OnRecordsChanged;
        }

        LoadEvents();
    }

    private void LoadEvents()
    {
        var selectedId = (EventListBox.SelectedItem as EventListItem)?.Record.Id;
        try
        {
            // Read in-memory recordings first. A save may finish between these two reads;
            // reading the store second keeps that event visible during the handoff.
            var pendingRecords = _recordingService?.GetPendingRecords() ?? [];
            var storedRecords = _store.Load(GetSelectedRetentionDays());
            var storedIds = storedRecords.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            var records = storedRecords.Select(record => (Record: record, IsPending: false))
                .Concat(pendingRecords
                    .Where(record => !storedIds.Contains(record.Id))
                    .Select(record => (Record: record, IsPending: true)))
                .OrderByDescending(item => item.Record.Timestamp)
                .ToArray();

            for (var index = 0; index < records.Length; index++)
            {
                var (record, isPending) = records[index];
                if (index >= _events.Count || _events[index].Record.Id != record.Id)
                {
                    var existingIndex = -1;
                    for (var candidate = index + 1; candidate < _events.Count; candidate++)
                    {
                        if (_events[candidate].Record.Id == record.Id)
                        {
                            existingIndex = candidate;
                            break;
                        }
                    }

                    if (existingIndex >= 0)
                    {
                        _events.Move(existingIndex, index);
                    }
                    else
                    {
                        _events.Insert(index, new EventListItem(record, _store, isPending));
                        continue;
                    }
                }

                if (!_events[index].Matches(record, isPending))
                {
                    _events[index] = new EventListItem(record, _store, isPending);
                }
            }

            while (_events.Count > records.Length)
            {
                _events.RemoveAt(_events.Count - 1);
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
        if (selectedId is not null)
        {
            var previousSelection = _events.FirstOrDefault(item => item.Record.Id == selectedId);
            if (previousSelection is not null && !ReferenceEquals(EventListBox.SelectedItem, previousSelection))
            {
                EventListBox.SelectedItem = previousSelection;
            }
        }

        if (EventListBox.SelectedIndex < 0 && _events.Count > 0)
        {
            EventListBox.SelectedIndex = 0;
        }
    }

    private void RecordingService_OnRecordsChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closed)
            {
                LoadEvents();
            }
        }));
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
        var mediaErrorText = item.Record.MediaError switch
        {
            "EventClipSkippedDueToLoad" => LocalizationManager.Get("EventClipSkippedDueToLoad"),
            _ => item.Record.MediaError
        };
        var mediaError = string.IsNullOrWhiteSpace(mediaErrorText)
            ? string.Empty
            : Environment.NewLine + LocalizationManager.Get("EventMediaError") + " " + mediaErrorText;
        EventDetailsText.Text = item.IsPending
            ? LocalizationManager.Format(
                "EventPendingDetailsFormat",
                item.Record.Timestamp.ToLocalTime(),
                Environment.NewLine,
                item.Record.SourceName,
                triggers) + Environment.NewLine + LocalizationManager.Get("EventRecordingPending")
            : LocalizationManager.Format(
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
        DeleteButton.IsEnabled = !item.IsPending;
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

    private static BitmapImage? LoadThumbnail(string? path)
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
            image.DecodePixelWidth = 192;
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

    private void ThumbnailImage_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            UpdateThumbnailSource(image);
        }
    }

    private void ThumbnailImage_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Image { IsLoaded: true } image)
        {
            UpdateThumbnailSource(image);
        }
    }

    private void ThumbnailImage_OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Source = null;
        }
    }

    private static void UpdateThumbnailSource(Image image)
    {
        image.Source = image.DataContext is EventListItem item
            ? LoadThumbnail(item.SnapshotPath)
            : null;
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
        if (EventListBox.SelectedItem is not EventListItem { IsPending: false } item)
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
        _closed = true;
        if (_recordingService is not null)
        {
            _recordingService.RecordsChanged -= RecordingService_OnRecordsChanged;
        }

        StopPlayback();
        SnapshotImage.Source = null;
        EventListBox.ItemsSource = null;
        _events.Clear();
        base.OnClosed(e);
    }

    private sealed class EventListItem
    {
        public EventListItem(CameraEventRecord record, CameraEventStore store, bool isPending)
        {
            Record = record;
            IsPending = isPending;
            SnapshotPath = store.ResolveMediaPath(record.SnapshotFile);
            ClipPath = store.ResolveMediaPath(record.ClipFile);
            TimeText = record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            SourceName = record.SourceName;
            Summary = record.Triggers.Count == 0 ? LocalizationManager.Get("EventNoSummary") : string.Join(" · ", record.Triggers);
            if (isPending)
            {
                Summary += " · " + LocalizationManager.Get("EventRecordingStatus");
            }
        }

        public CameraEventRecord Record { get; }
        public bool IsPending { get; }
        public string? SnapshotPath { get; }
        public string? ClipPath { get; }
        public string TimeText { get; }
        public string SourceName { get; }
        public string Summary { get; }

        public bool Matches(CameraEventRecord record, bool isPending) =>
            IsPending == isPending &&
            Record.Id == record.Id &&
            Record.SnapshotFile == record.SnapshotFile &&
            Record.ClipFile == record.ClipFile &&
            Record.DurationSeconds == record.DurationSeconds &&
            Record.MediaError == record.MediaError &&
            Record.Triggers.SequenceEqual(record.Triggers);
    }
}
