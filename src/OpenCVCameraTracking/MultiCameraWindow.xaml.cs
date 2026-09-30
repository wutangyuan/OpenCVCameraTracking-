using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenCVCameraTracking.Configuration;
using OpenCVCameraTracking.Core;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Detection;
using OpenCVCameraTracking.Core.Logging;
using OpenCVCameraTracking.Localization;

namespace OpenCVCameraTracking;

public partial class MultiCameraWindow : Window
{
    private readonly ApplicationSettings _settings;
    private readonly string? _mainPreviewSourceKey;
    private readonly CameraTrackingEngine? _mainPreviewEngine;
    private readonly ObservableCollection<TileModel> _tiles = [];
    private Point _dragStart;
    private TileModel? _fullscreenTile;
    private UniformGrid? _gridPanel;
    private WindowStyle _previousWindowStyle;
    private WindowState _previousWindowState;
    private bool _isClosing;
    private bool _closingCleanupInProgress;
    private bool _allowClose;

    public MultiCameraWindow(
        IReadOnlyList<MultiPreviewSource> sources,
        ApplicationSettings settings,
        string? mainPreviewSourceKey,
        CameraTrackingEngine? mainPreviewEngine)
    {
        InitializeComponent();
        _settings = settings;
        _mainPreviewSourceKey = mainPreviewSourceKey;
        _mainPreviewEngine = mainPreviewEngine;
        TilesBox.ItemsSource = _tiles;
        Loaded += (_, _) => { _gridPanel = FindVisualChild<UniformGrid>(TilesBox); SetLayout(SelectedLayout); };
        SelectLayout(settings.SelectedLayout);

        var ordered = sources
            .OrderBy(source => settings.LayoutStreamIds.IndexOf(source.Key) < 0 ? int.MaxValue : settings.LayoutStreamIds.IndexOf(source.Key))
            .ThenBy(source => source.Name)
            .ToList();
        foreach (var source in ordered)
        {
            var tile = new TileModel(source);
            _tiles.Add(tile);
            if (UsesMainPreviewFrame(source))
            {
                StartSharedMainPreviewTile(tile);
            }
            else
            {
                _ = StartTileAsync(tile);
            }
        }

    }

    public string SelectedLayout { get; private set; } = "Single";
    public IReadOnlyList<string> OrderedSourceKeys => _tiles.Select(tile => tile.Source.Key).ToArray();

    private bool UsesMainPreviewFrame(MultiPreviewSource source) =>
        _mainPreviewEngine is { IsRunning: true } &&
        string.Equals(source.Key, _mainPreviewSourceKey, StringComparison.OrdinalIgnoreCase);

    private void StartSharedMainPreviewTile(TileModel tile)
    {
        var engine = _mainPreviewEngine;
        if (engine is not { IsRunning: true })
        {
            _ = StartTileAsync(tile);
            return;
        }

        tile.Status = LocalizationManager.Get("Status_Connecting");
        EventHandler<FrameReadyEventArgs> frameReadyHandler = (_, args) => QueueTileFrameUpdate(tile, args);
        EventHandler<VideoSourceStatusEventArgs> sourceStatusHandler = (_, args) =>
            QueueTileStatusUpdate(tile, args);
        tile.AttachSharedEngine(engine, frameReadyHandler, sourceStatusHandler);
        engine.FrameReady += frameReadyHandler;
        engine.SourceStatusChanged += sourceStatusHandler;
    }

    private async Task StartTileAsync(TileModel tile)
    {
        try
        {
            tile.Status = LocalizationManager.Get("Status_Connecting");
            tile.Engine = new CameraTrackingEngine(new EmptyObjectDetector());
            tile.Engine.FrameReady += (_, args) =>
            {
                QueueTileFrameUpdate(tile, args);
            };
            tile.Engine.SourceStatusChanged += (_, args) =>
            {
                QueueTileStatusUpdate(tile, args);
            };
            await tile.Engine.StartAsync(tile.Source.Options);
        }
        catch (Exception exception)
        {
            tile.Status = LocalizationManager.GetExceptionMessage(exception);
            AppLogger.Error($"Multi-camera tile failed: {tile.Source.Name}", exception);
        }
    }

    private void QueueTileFrameUpdate(TileModel tile, FrameReadyEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosing)
            {
                tile.UpdateFrame(args);
            }
        });
    }

    private void QueueTileStatusUpdate(TileModel tile, VideoSourceStatusEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isClosing)
            {
                tile.Status = args.Status == "Connected"
                    ? $"{args.Width}×{args.Height}"
                    : LocalizeSourceStatus(args.Status);
            }
        });
    }

    private static string LocalizeSourceStatus(string status) => status switch
    {
        "Connecting" => LocalizationManager.Get("Status_Connecting"),
        "Reconnecting" => LocalizationManager.Get("Status_Reconnecting"),
        "Stopped" => LocalizationManager.Get("Status_Stopped"),
        "FileEnded" => LocalizationManager.Get("Status_FileEnded"),
        _ => status
    };

    private async Task StopTilesAsync()
    {
        foreach (var tile in _tiles)
        {
            tile.DetachSharedEngine();
            if (tile.Engine is not null)
            {
                await tile.Engine.DisposeAsync();
                tile.Engine = null;
            }
        }
    }

    private void LayoutBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            SetLayout(tag);
        }
    }

    private void SelectLayout(string layout)
    {
        var item = LayoutBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, layout))
            ?? LayoutBox.Items.OfType<ComboBoxItem>().First();
        LayoutBox.SelectedItem = item;
        SetLayout(item.Tag?.ToString() ?? "Single");
    }

    private void SetLayout(string layout)
    {
        SelectedLayout = layout;
        _gridPanel ??= FindVisualChild<UniformGrid>(TilesBox);
        if (_gridPanel is null)
        {
            return;
        }

        var side = layout switch
        {
            "Grid2" => 2,
            "Grid3" => 3,
            "Grid4" => 4,
            _ => 1
        };
        _gridPanel.Rows = side;
        _gridPanel.Columns = side;
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void TilesBox_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is DependencyObject source &&
            FindTile(source) is { } tile &&
            (Math.Abs(e.GetPosition(this).X - _dragStart.X) > SystemParameters.MinimumHorizontalDragDistance ||
             Math.Abs(e.GetPosition(this).Y - _dragStart.Y) > SystemParameters.MinimumVerticalDragDistance))
        {
            DragDrop.DoDragDrop((DependencyObject)sender, tile, DragDropEffects.Move);
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
        {
            _dragStart = e.GetPosition(this);
        }
    }

    private void TilesBox_OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(TileModel)) is not TileModel sourceTile ||
            e.OriginalSource is not DependencyObject source ||
            FindTile(source) is not { } targetTile ||
            ReferenceEquals(sourceTile, targetTile))
        {
            return;
        }

        var sourceIndex = _tiles.IndexOf(sourceTile);
        var targetIndex = _tiles.IndexOf(targetTile);
        if (sourceIndex >= 0 && targetIndex >= 0)
        {
            _tiles.Move(sourceIndex, targetIndex);
        }
    }

    private TileModel? FindTile(DependencyObject source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement element && element.DataContext is TileModel tile)
            {
                return tile;
            }
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private void Tile_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2)
        {
            return;
        }
        if (sender is FrameworkElement element && element.DataContext is TileModel tile)
        {
            if (_fullscreenTile is null)
            {
                _fullscreenTile = tile;
                _previousWindowStyle = WindowStyle;
                _previousWindowState = WindowState;
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                foreach (var other in _tiles.Where(item => !ReferenceEquals(item, tile)))
                {
                    other.IsVisible = false;
                }
            }
            else
            {
                WindowStyle = _previousWindowStyle;
                WindowState = _previousWindowState;
                foreach (var other in _tiles)
                {
                    other.IsVisible = true;
                }
                _fullscreenTile = null;
            }
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

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
        try
        {
            await StopTilesAsync();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Multi-camera shutdown cleanup failed", exception);
        }
        finally
        {
            _allowClose = true;
            _closingCleanupInProgress = false;
            // Avoid re-entering WPF's active Closing notification when all
            // preview engines have already stopped synchronously.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
        }
    }

    public sealed class TileModel : INotifyPropertyChanged
    {
        private BitmapSource? _image;
        private string _status = "Waiting";
        private bool _isVisible = true;

        public TileModel(MultiPreviewSource source) => Source = source;
        public MultiPreviewSource Source { get; }
        public CameraTrackingEngine? Engine { get; set; }
        private CameraTrackingEngine? SharedEngine { get; set; }
        private EventHandler<FrameReadyEventArgs>? SharedFrameReadyHandler { get; set; }
        private EventHandler<VideoSourceStatusEventArgs>? SharedSourceStatusHandler { get; set; }
        public string DisplayName => Source.Name;
        public BitmapSource? Image { get => _image; private set => Set(ref _image, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public Visibility PlaceholderVisibility => Image is null ? Visibility.Visible : Visibility.Collapsed;
        public bool IsVisible { get => _isVisible; set { if (Set(ref _isVisible, value)) OnPropertyChanged(nameof(TileVisibility)); } }
        public Visibility TileVisibility => IsVisible ? Visibility.Visible : Visibility.Collapsed;

        public void AttachSharedEngine(
            CameraTrackingEngine engine,
            EventHandler<FrameReadyEventArgs> frameReadyHandler,
            EventHandler<VideoSourceStatusEventArgs> sourceStatusHandler)
        {
            SharedEngine = engine;
            SharedFrameReadyHandler = frameReadyHandler;
            SharedSourceStatusHandler = sourceStatusHandler;
        }

        public void DetachSharedEngine()
        {
            if (SharedEngine is not null)
            {
                if (SharedFrameReadyHandler is not null)
                {
                    SharedEngine.FrameReady -= SharedFrameReadyHandler;
                }

                if (SharedSourceStatusHandler is not null)
                {
                    SharedEngine.SourceStatusChanged -= SharedSourceStatusHandler;
                }
            }

            SharedEngine = null;
            SharedFrameReadyHandler = null;
            SharedSourceStatusHandler = null;
        }

        public void UpdateFrame(FrameReadyEventArgs args)
        {
            var bitmap = new WriteableBitmap(args.Width, args.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, args.Width, args.Height), args.Pixels, args.Stride, 0);
            bitmap.Freeze();
            Image = bitmap;
            Status = $"{args.FramesPerSecond:F1} FPS · {args.Width}×{args.Height}";
            OnPropertyChanged(nameof(PlaceholderVisibility));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; OnPropertyChanged(name); return true;
        }
        private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class EmptyObjectDetector : IObjectDetector
    {
        public IReadOnlyList<Detection> Detect(OpenCvSharp.Mat frame) => [];
        public void Dispose() { }
    }
}
