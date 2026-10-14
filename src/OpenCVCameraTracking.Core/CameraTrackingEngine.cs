using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Detection;
using OpenCVCameraTracking.Core.Recognition;
using OpenCVCameraTracking.Core.Tracking;
using OpenCVCameraTracking.Core.Logging;
using OpenCvSharp;

namespace OpenCVCameraTracking.Core;

public sealed class FrameReadyEventArgs(
    byte[] pixels,
    int width,
    int height,
    int stride,
    IReadOnlyList<TrackedObject> objects,
    double framesPerSecond,
    double sourceLatencyMilliseconds,
    DateTimeOffset? capturedAt = null) : EventArgs
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public IReadOnlyList<TrackedObject> Objects { get; } = objects;
    public double FramesPerSecond { get; } = framesPerSecond;
    public double SourceLatencyMilliseconds { get; } = sourceLatencyMilliseconds;
    public DateTimeOffset CapturedAt { get; } = capturedAt ?? DateTimeOffset.UtcNow;
}

public sealed class VideoSourceStatusEventArgs(
    string status,
    string? detail,
    int reconnectAttempt,
    int width,
    int height) : EventArgs
{
    public string Status { get; } = status;
    public string? Detail { get; } = detail;
    public int ReconnectAttempt { get; } = reconnectAttempt;
    public int Width { get; } = width;
    public int Height { get; } = height;
}

public sealed record VideoSourceDiagnosticResult(
    bool Success,
    string Status,
    string? Detail,
    TimeSpan OpenDuration,
    int Width,
    int Height,
    VideoCaptureBackend Backend);

public sealed class CameraTrackingEngine : IAsyncDisposable
{
    private static readonly object FfmpegEnvironmentLock = new();
    private readonly IObjectDetector _detector;
    private readonly IouMultiObjectTracker _tracker;
    private readonly WhitelistRecognitionService? _whitelistRecognition;
    private readonly int _detectionInterval;
    private readonly object _latestSnapshotGate = new();
    private readonly object _restrictedZoneGate = new();
    private Mat? _latestFrame;
    private IReadOnlyList<TrackedObject> _latestObjects = [];
    private IReadOnlyList<RestrictedZone> _restrictedZones = [];
    private Dictionary<string, HashSet<int>> _restrictedZoneTrackIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private bool _disposed;

    public CameraTrackingEngine(
        IObjectDetector detector,
        int detectionInterval = 2,
        IouMultiObjectTracker? tracker = null,
        WhitelistRecognitionService? whitelistRecognition = null,
        RestrictedZone? restrictedZone = null,
        IEnumerable<RestrictedZone>? restrictedZones = null)
    {
        _detector = detector;
        _detectionInterval = Math.Max(1, detectionInterval);
        _tracker = tracker ?? new IouMultiObjectTracker();
        _whitelistRecognition = whitelistRecognition;
        _restrictedZones = NormalizeRestrictedZones(restrictedZones ?? (restrictedZone is null ? [] : [restrictedZone]));
    }

    public CameraTrackingEngine(
        IEnumerable<IObjectDetector> detectors,
        int detectionInterval = 2,
        IouMultiObjectTracker? tracker = null,
        WhitelistRecognitionService? whitelistRecognition = null,
        RestrictedZone? restrictedZone = null,
        IEnumerable<RestrictedZone>? restrictedZones = null)
        : this(new CompositeObjectDetector(detectors), detectionInterval, tracker, whitelistRecognition, restrictedZone, restrictedZones)
    {
    }

    public event EventHandler<FrameReadyEventArgs>? FrameReady;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<VideoSourceStatusEventArgs>? SourceStatusChanged;
    public event EventHandler<Exception>? Faulted;
    public event EventHandler<RestrictedZoneAlertEventArgs>? RestrictedZoneAlerted;

    public bool IsRunning => _worker is { IsCompleted: false };

    public RestrictedZone? GetRestrictedZone()
    {
        lock (_restrictedZoneGate)
        {
            return _restrictedZones.FirstOrDefault();
        }
    }

    public IReadOnlyList<RestrictedZone> GetRestrictedZones()
    {
        lock (_restrictedZoneGate)
        {
            return _restrictedZones.ToArray();
        }
    }

    /// <summary>Updates the zone without requiring the video source to restart.</summary>
    public void SetRestrictedZone(RestrictedZone? zone)
        => SetRestrictedZones(zone is null ? [] : [zone]);

    public void SetRestrictedZones(IEnumerable<RestrictedZone>? zones)
    {
        var normalizedZones = NormalizeRestrictedZones(zones ?? []);
        lock (_restrictedZoneGate)
        {
            if (!HasSameZoneGeometry(_restrictedZones, normalizedZones))
            {
                _restrictedZoneTrackIds = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            }

            _restrictedZones = normalizedZones;
        }
    }

    private static bool HasSameZoneGeometry(
        IReadOnlyList<RestrictedZone> current,
        IReadOnlyList<RestrictedZone> next) =>
        current.Count == next.Count && current.All(zone =>
        {
            var match = next.FirstOrDefault(candidate => candidate.Id == zone.Id);
            return match is not null && match.X == zone.X && match.Y == zone.Y &&
                match.Width == zone.Width && match.Height == zone.Height;
        });

    private static IReadOnlyList<RestrictedZone> NormalizeRestrictedZones(IEnumerable<RestrictedZone> zones) =>
        zones.Where(zone => zone is not null && zone.IsValid)
            .Take(20)
            .Select((zone, index) => zone with
            {
                Id = string.IsNullOrWhiteSpace(zone.Id) ? $"zone-{index + 1}" : zone.Id,
                Name = string.IsNullOrWhiteSpace(zone.Name) ? $"Zone {index + 1}" : zone.Name.Trim()
            })
            .GroupBy(zone => zone.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

    /// <summary>
    /// Opens a source and reads one frame without starting tracking. This is used by
    /// the settings/diagnostics UI so connectivity tests never block the WPF thread.
    /// </summary>
    public Task<VideoSourceDiagnosticResult> DiagnoseAsync(
        CameraSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(options);
        return Task.Run(() => Diagnose(options, cancellationToken), cancellationToken);
    }

    private static VideoSourceDiagnosticResult Diagnose(
        CameraSourceOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var capture = OpenCapture(options);
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = new Mat();
            if (!capture.Read(frame) || frame.Empty())
            {
                return new VideoSourceDiagnosticResult(
                    false,
                    "ReadFailed",
                    "The source opened but did not return a frame.",
                    started.Elapsed,
                    0,
                    0,
                    ResolveBackend(options));
            }

            return new VideoSourceDiagnosticResult(
                true,
                "Ok",
                null,
                started.Elapsed,
                frame.Width,
                frame.Height,
                ResolveBackend(options));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new VideoSourceDiagnosticResult(
                false,
                "OpenFailed",
                exception.Message,
                started.Elapsed,
                0,
                0,
                ResolveBackend(options));
        }
    }

    public Task StartAsync(CameraSourceOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new CoreException(CoreErrorCode.EngineAlreadyRunning);
        }

        Validate(options);
        AppLogger.Info($"Tracking start requested: source={options.Kind}, detector={_detector.GetType().Name}, interval={_detectionInterval}");
        _tracker.Reset();
        _whitelistRecognition?.ResetTracking();
        ClearLatestSnapshot();
        var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation = captureCancellation;
        _worker = Task.Run(() => CaptureLoopAsync(options, captureCancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the current capture and starts it again using the same source options.</summary>
    public async Task ReconnectAsync(CameraSourceOptions options, CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        var worker = _worker;
        if (worker is null)
        {
            return;
        }

        _cancellation?.Cancel();
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _worker = null;
            _cancellation?.Dispose();
            _cancellation = null;
            ClearLatestSnapshot();
            StatusChanged?.Invoke(this, "Stopped");
            AppLogger.Info("Tracking stopped");
        }
    }

    public async Task<WhitelistEnrollmentResult> EnrollCurrentTargetAsync(
        string name,
        WhitelistSubjectKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_whitelistRecognition is null)
        {
            return new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.RecognitionUnavailable);
        }

        Mat? frame;
        IReadOnlyList<TrackedObject> objects;
        lock (_latestSnapshotGate)
        {
            if (_latestFrame is null)
            {
                return new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.NoFrame);
            }

            frame = _latestFrame.Clone();
            objects = _latestObjects;
        }

        using (frame)
        {
            var expectedLabel = kind == WhitelistSubjectKind.Face ? "face" : "cat";
            var target = objects
                .Where(item => string.Equals(item.Label, expectedLabel, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Box.Width * item.Box.Height)
                .FirstOrDefault();
            if (target is null)
            {
                return new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.NoMatchingTarget);
            }

            return await _whitelistRecognition.EnrollAsync(frame, target, name, kind).ConfigureAwait(false);
        }
    }

    public async Task<WhitelistEnrollmentResult> EnrollCurrentRegionAsync(
        Rect region,
        string name,
        WhitelistSubjectKind kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_whitelistRecognition is null)
        {
            return new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.RecognitionUnavailable);
        }

        Mat? frame;
        IReadOnlyList<TrackedObject> objects;
        lock (_latestSnapshotGate)
        {
            if (_latestFrame is null)
            {
                return new WhitelistEnrollmentResult(WhitelistEnrollmentStatus.NoFrame);
            }

            frame = _latestFrame.Clone();
            objects = _latestObjects;
        }

        using (frame)
        {
            if (kind == WhitelistSubjectKind.Face)
            {
                var target = objects
                    .Where(item => string.Equals(item.Label, "face", StringComparison.OrdinalIgnoreCase))
                    .Select(item => new { Item = item, Overlap = IntersectionArea(item.Box, region) })
                    .Where(candidate => candidate.Overlap > 0)
                    .OrderByDescending(candidate => candidate.Overlap)
                    .Select(candidate => candidate.Item)
                    .FirstOrDefault();
                if (target is not null)
                {
                    return await _whitelistRecognition.EnrollAsync(frame, target, name, kind).ConfigureAwait(false);
                }
            }

            return await _whitelistRecognition.EnrollAsync(frame, region, name, kind).ConfigureAwait(false);
        }
    }

    private async Task CaptureLoopAsync(CameraSourceOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Kind == CameraSourceKind.Stream && options.LowLatencyMode)
            {
                await LowLatencyStreamLoopAsync(options, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                SequentialCaptureLoop(options, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLogger.Error("Capture loop faulted", exception);
            Faulted?.Invoke(this, exception);
        }
    }

    private void SequentialCaptureLoop(CameraSourceOptions options, CancellationToken cancellationToken)
    {
        var frameNumber = 0L;
        var frameCounter = 0;
        var measuredFps = 0d;
        var fpsTimer = Stopwatch.StartNew();
        using var frame = new Mat();
        var reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var capture = OpenCaptureWithRetry(options, cancellationToken);
            reconnectAttempt = 0;
            RaiseSourceStatus("Connected", null, 0, capture);
            AppLogger.Info($"Capture connected: source={options.Kind}, backend={options.PreferredBackend}");
            var consecutiveFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    if (++consecutiveFailures < 8)
                    {
                        WaitForReconnect(cancellationToken, 30);
                        continue;
                    }

                    if (options.Kind == CameraSourceKind.File)
                    {
                        RaiseSourceStatus("FileEnded", "The video file has no more frames.", 0, capture);
                        return;
                    }

                    RaiseSourceStatus(
                        "Reconnecting",
                        "The video source stopped returning frames.",
                        ++reconnectAttempt,
                        capture);
                    break;
                }

                consecutiveFailures = 0;
                ProcessFrame(frame, ref frameNumber, ref frameCounter, ref measuredFps, fpsTimer, DateTimeOffset.UtcNow);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                WaitForReconnect(cancellationToken, GetReconnectDelay(options, reconnectAttempt));
            }
        }
    }

    private async Task LowLatencyStreamLoopAsync(CameraSourceOptions options, CancellationToken cancellationToken)
    {
        using var localCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var frameBuffer = new LatestFrameBuffer();
        var reader = Task.Run(
            () => ReadLatestFrames(options, frameBuffer, localCancellation.Token),
            CancellationToken.None);

        var frameNumber = 0L;
        var frameCounter = 0;
        var measuredFps = 0d;
        var fpsTimer = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var packet = frameBuffer.Take(cancellationToken);
                if (packet is null)
                {
                    break;
                }

                ProcessFrame(packet.Frame, ref frameNumber, ref frameCounter, ref measuredFps, fpsTimer, packet.CapturedAt);
            }
        }
        finally
        {
            localCancellation.Cancel();
            frameBuffer.Complete();
            await ObserveReaderCompletionAsync(reader).ConfigureAwait(false);
        }
    }

    private static async Task ObserveReaderCompletionAsync(Task reader)
    {
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected result of a user-initiated stop.
        }
    }

    private void ReadLatestFrames(
        CameraSourceOptions options,
        LatestFrameBuffer frameBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var frame = new Mat();
            var reconnectAttempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var capture = OpenCaptureWithRetry(options, cancellationToken);
                reconnectAttempt = 0;
                RaiseSourceStatus("Connected", null, reconnectAttempt, capture);
                AppLogger.Info($"Low-latency capture connected: backend={options.PreferredBackend}");
                var consecutiveFailures = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        if (++consecutiveFailures < 8)
                        {
                            WaitForReconnect(cancellationToken, 15);
                            continue;
                        }

                        RaiseSourceStatus(
                            "Reconnecting",
                            "The video stream stopped returning frames.",
                            ++reconnectAttempt,
                            capture);
                        break;
                    }

                    consecutiveFailures = 0;
                    frameBuffer.Publish(frame, DateTimeOffset.UtcNow);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    WaitForReconnect(cancellationToken, GetReconnectDelay(options, reconnectAttempt));
                }
            }

            frameBuffer.Complete();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            frameBuffer.Complete(exception);
        }
    }

    private void ProcessFrame(
        Mat frame,
        ref long frameNumber,
        ref int frameCounter,
        ref double measuredFps,
        Stopwatch fpsTimer,
        DateTimeOffset capturedAt)
    {
        frameNumber++;
        frameCounter++;
        IReadOnlyList<TrackedObject> objects = frameNumber % _detectionInterval == 0 || _tracker.Current.Count == 0
            ? _tracker.Update(_detector.Detect(frame))
            : _tracker.Current;

        if (_whitelistRecognition is not null)
        {
            objects = _whitelistRecognition.Recognize(frame, objects);
        }

        var restrictedZones = GetRestrictedZones();
        var pixelZones = restrictedZones.Select(zone => zone.ToPixelRect(frame.Width, frame.Height)).ToArray();
        var occupiedZones = UpdateRestrictedZoneState(objects, restrictedZones, pixelZones, capturedAt);
        UpdateLatestSnapshot(frame, objects);
        for (var index = 0; index < pixelZones.Length; index++)
        {
            DrawRestrictedZone(frame, pixelZones[index], occupiedZones[index], index + 1);
        }
        DrawTracks(frame, objects);
        if (fpsTimer.ElapsedMilliseconds >= 1_000)
        {
            measuredFps = frameCounter * 1_000d / fpsTimer.ElapsedMilliseconds;
            frameCounter = 0;
            fpsTimer.Restart();
        }

        RaiseFrame(frame, objects, measuredFps, capturedAt);
    }

    private IReadOnlyList<bool> UpdateRestrictedZoneState(
        IReadOnlyList<TrackedObject> objects,
        IReadOnlyList<RestrictedZone> zones,
        IReadOnlyList<Rect> pixelZones,
        DateTimeOffset capturedAt)
    {
        if (zones.Count == 0)
        {
            lock (_restrictedZoneGate)
            {
                _restrictedZoneTrackIds.Clear();
            }

            return [];
        }

        var occupied = new List<bool>(zones.Count);
        var entered = new List<(TrackedObject Target, RestrictedZone Zone, Rect PixelZone)>();
        lock (_restrictedZoneGate)
        {
            for (var index = 0; index < zones.Count; index++)
            {
                var zone = zones[index];
                var targets = objects
                    .Where(IsRestrictedZoneTarget)
                    .Where(item => IsInsideRestrictedZone(item.Box, pixelZones[index]))
                    .ToArray();
                var currentIds = targets.Select(item => item.Id).ToHashSet();
                _restrictedZoneTrackIds.TryGetValue(zone.Id, out var previousIds);
                previousIds ??= [];
                entered.AddRange(targets
                    .Where(item => !previousIds.Contains(item.Id))
                    .Select(item => (item, zone, pixelZones[index])));
                _restrictedZoneTrackIds[zone.Id] = currentIds;
                occupied.Add(targets.Length > 0);
            }

            var activeIds = zones.Select(zone => zone.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var removedId in _restrictedZoneTrackIds.Keys.Where(id => !activeIds.Contains(id)).ToArray())
            {
                _restrictedZoneTrackIds.Remove(removedId);
            }
        }

        foreach (var (target, zone, pixelZone) in entered)
        {
            try
            {
                RestrictedZoneAlerted?.Invoke(
                    this,
                    new RestrictedZoneAlertEventArgs(target, pixelZone, capturedAt, zone.Name));
            }
            catch (Exception exception)
            {
                // An alert sink must never stop camera processing.
                AppLogger.Error("Restricted-zone alert handler failed", exception);
            }
        }

        return occupied;
    }

    private static bool IsRestrictedZoneTarget(TrackedObject item) =>
        item.Label.Equals("cat", StringComparison.OrdinalIgnoreCase) ||
        item.Label.Equals("face", StringComparison.OrdinalIgnoreCase) ||
        item.Label.Equals("person", StringComparison.OrdinalIgnoreCase) ||
        item.Label.Equals("human", StringComparison.OrdinalIgnoreCase) ||
        YoloOnnxDetector.CocoPersonVehicleLabels.Contains(item.Label, StringComparer.OrdinalIgnoreCase);

    private static bool IsInsideRestrictedZone(Rect target, Rect zone)
    {
        var centre = new Point(
            target.X + target.Width / 2,
            target.Y + target.Height / 2);
        if (zone.Contains(centre))
        {
            return true;
        }

        var intersection = target & zone;
        var intersectionArea = Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
        var targetArea = Math.Max(1, target.Width * target.Height);
        return intersectionArea * 5 >= targetArea;
    }

    private VideoCapture OpenCaptureWithRetry(
        CameraSourceOptions options,
        CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                return OpenCapture(options);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                attempt++;
                if (options.Kind == CameraSourceKind.File)
                {
                    throw;
                }

                RaiseSourceStatus("Reconnecting", exception.Message, attempt, null);
                AppLogger.Warn($"Unable to open video source; retry attempt={attempt}, source={options.Kind}: {exception.Message}");
                WaitForReconnect(cancellationToken, GetReconnectDelay(options, attempt));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new CoreException(CoreErrorCode.SourceOpenCancelled);
    }

    private static VideoCapture OpenCapture(CameraSourceOptions options)
    {
        VideoCapture capture;
        if (options.Kind == CameraSourceKind.Device)
        {
            capture = options.PreferredBackend switch
            {
                VideoCaptureBackend.DirectShow => new VideoCapture(options.DeviceIndex, VideoCaptureAPIs.DSHOW),
                VideoCaptureBackend.MediaFoundation => new VideoCapture(options.DeviceIndex, VideoCaptureAPIs.MSMF),
                _ => OpenDeviceWithFallback(options.DeviceIndex)
            };
        }
        else
        {
            var address = options.UseSubStream && !string.IsNullOrWhiteSpace(options.SubAddress)
                ? options.SubAddress!
                : options.Address!;
            capture = options.PreferredBackend switch
            {
                VideoCaptureBackend.Ffmpeg => OpenFfmpeg(address, options),
                VideoCaptureBackend.MediaFoundation => new VideoCapture(address, VideoCaptureAPIs.MSMF),
                _ => OpenStreamWithFallback(address, options)
            };
        }

        if (!capture.IsOpened())
        {
            capture.Dispose();
            throw new CoreException(CoreErrorCode.SourceOpenFailed);
        }

        capture.Set(VideoCaptureProperties.BufferSize, 1);
        if (options.Width is > 0)
        {
            capture.Set(VideoCaptureProperties.FrameWidth, options.Width.Value);
        }
        if (options.Height is > 0)
        {
            capture.Set(VideoCaptureProperties.FrameHeight, options.Height.Value);
        }
        if (options.FramesPerSecond is > 0)
        {
            capture.Set(VideoCaptureProperties.Fps, options.FramesPerSecond.Value);
        }

        return capture;
    }

    private static VideoCapture OpenDeviceWithFallback(int deviceIndex)
    {
        var capture = new VideoCapture(deviceIndex, VideoCaptureAPIs.DSHOW);
        if (capture.IsOpened())
        {
            return capture;
        }

        capture.Dispose();
        return new VideoCapture(deviceIndex, VideoCaptureAPIs.MSMF);
    }

    private static VideoCapture OpenStreamWithFallback(string address, CameraSourceOptions options)
    {
        var capture = OpenFfmpeg(address, options);
        if (capture.IsOpened())
        {
            return capture;
        }

        capture.Dispose();
        return new VideoCapture(address, VideoCaptureAPIs.ANY);
    }

    private static VideoCaptureBackend ResolveBackend(CameraSourceOptions options)
    {
        if (options.PreferredBackend != VideoCaptureBackend.Auto)
        {
            return options.PreferredBackend;
        }

        return options.Kind == CameraSourceKind.Device
            ? VideoCaptureBackend.DirectShow
            : VideoCaptureBackend.Ffmpeg;
    }

    private static VideoCapture OpenFfmpeg(string address, CameraSourceOptions options)
    {
        const int openTimeoutProperty = 53;
        const int readTimeoutProperty = 54;
        var parameters = new[]
        {
            openTimeoutProperty, options.OpenTimeoutMilliseconds,
            readTimeoutProperty, options.ReadTimeoutMilliseconds
        };

        var isRtsp = address.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
        if (!isRtsp || !options.PreferTcpForRtsp)
        {
            return new VideoCapture(address, VideoCaptureAPIs.FFMPEG, parameters);
        }

        lock (FfmpegEnvironmentLock)
        {
            const string variableName = "OPENCV_FFMPEG_CAPTURE_OPTIONS";
            var previous = Environment.GetEnvironmentVariable(variableName);
            try
            {
                var ffmpegOptions = options.LowLatencyMode
                    ? "rtsp_transport;tcp|fflags;nobuffer|flags;low_delay"
                    : "rtsp_transport;tcp";
                Environment.SetEnvironmentVariable(variableName, ffmpegOptions);
                return new VideoCapture(address, VideoCaptureAPIs.FFMPEG, parameters);
            }
            finally
            {
                Environment.SetEnvironmentVariable(variableName, previous);
            }
        }
    }

    private static void DrawTracks(Mat frame, IReadOnlyList<TrackedObject> objects)
    {
        foreach (var item in objects)
        {
            var color = item.IsKnown switch
            {
                true => new Scalar(70, 200, 90),
                false => new Scalar(70, 70, 230),
                _ => ColorForId(item.Id)
            };
            Cv2.Rectangle(frame, item.Box, color, 2, LineTypes.AntiAlias);
            var identity = item.IsKnown switch
            {
                true => item.IdentityName,
                false => "unknown",
                _ => null
            };
            var detectionScore = $"det {item.Confidence:P0}";
            var matchScore = item.RecognitionSimilarity is { } similarity
                ? $"  match {similarity:P0}"
                : string.Empty;
            var caption = string.IsNullOrWhiteSpace(identity)
                ? $"{item.Label} #{item.Id}  {detectionScore}{matchScore}"
                : $"{item.Label} {identity} #{item.Id}  {detectionScore}{matchScore}";
            var textSize = Cv2.GetTextSize(caption, HersheyFonts.HersheySimplex, 0.55, 1, out var baseline);
            var textTop = Math.Max(0, item.Box.Y - textSize.Height - baseline - 6);
            var background = new Rect(
                item.Box.X,
                textTop,
                Math.Min(textSize.Width + 8, frame.Width - item.Box.X),
                textSize.Height + baseline + 6);
            Cv2.Rectangle(frame, background, color, -1);
            Cv2.PutText(
                frame,
                caption,
                new Point(item.Box.X + 4, textTop + textSize.Height + 1),
                HersheyFonts.HersheySimplex,
                0.55,
                Scalar.White,
                1,
                LineTypes.AntiAlias);
        }
    }

    private static void DrawRestrictedZone(Mat frame, Rect zone, bool occupied, int index)
    {
        var color = occupied
            ? new Scalar(55, 55, 235)
            : new Scalar(0, 180, 255);
        Cv2.Rectangle(frame, zone, color, 3, LineTypes.AntiAlias);
        var caption = occupied ? $"ZONE {index} - ALERT" : $"ZONE {index}";
        var textOrigin = new Point(zone.X + 6, Math.Max(24, zone.Y + 24));
        Cv2.PutText(
            frame,
            caption,
            textOrigin,
            HersheyFonts.HersheySimplex,
            0.65,
            color,
            2,
            LineTypes.AntiAlias);
    }

    private static int IntersectionArea(Rect first, Rect second)
    {
        var intersection = first & second;
        return intersection.Width > 0 && intersection.Height > 0
            ? intersection.Width * intersection.Height
            : 0;
    }

    private static Scalar ColorForId(int id)
    {
        var hue = (id * 67) % 180;
        using var hsv = new Mat(1, 1, MatType.CV_8UC3, new Scalar(hue, 210, 245));
        using var bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        var pixel = bgr.At<Vec3b>(0, 0);
        return new Scalar(pixel.Item0, pixel.Item1, pixel.Item2);
    }

    private void RaiseFrame(
        Mat bgrFrame,
        IReadOnlyList<TrackedObject> objects,
        double framesPerSecond,
        DateTimeOffset capturedAt)
    {
        using var bgra = new Mat();
        Cv2.CvtColor(bgrFrame, bgra, ColorConversionCodes.BGR2BGRA);
        var stride = checked((int)bgra.Step());
        var pixels = new byte[checked(stride * bgra.Height)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        FrameReady?.Invoke(
            this,
            new FrameReadyEventArgs(
                pixels,
                bgra.Width,
                bgra.Height,
                stride,
                objects,
                framesPerSecond,
                Math.Max(0d, (DateTimeOffset.UtcNow - capturedAt).TotalMilliseconds),
                capturedAt));
    }

    private void RaiseSourceStatus(
        string status,
        string? detail,
        int reconnectAttempt,
        VideoCapture? capture)
    {
        var width = capture is null ? 0 : Math.Max(0, (int)capture.Get(VideoCaptureProperties.FrameWidth));
        var height = capture is null ? 0 : Math.Max(0, (int)capture.Get(VideoCaptureProperties.FrameHeight));
        StatusChanged?.Invoke(this, status);
        SourceStatusChanged?.Invoke(
            this,
            new VideoSourceStatusEventArgs(status, detail, reconnectAttempt, width, height));
    }

    private static int GetReconnectDelay(CameraSourceOptions options, int reconnectAttempt)
    {
        var initial = Math.Max(50, options.ReconnectInitialDelayMilliseconds);
        var maximum = Math.Max(initial, options.ReconnectMaximumDelayMilliseconds);
        var exponent = Math.Min(10, Math.Max(0, reconnectAttempt - 1));
        var delay = initial * Math.Pow(2, exponent);
        return (int)Math.Min(maximum, delay);
    }

    private static void WaitForReconnect(CancellationToken cancellationToken, int milliseconds)
    {
        cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(0, milliseconds)));
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void UpdateLatestSnapshot(Mat frame, IReadOnlyList<TrackedObject> objects)
    {
        var copy = frame.Clone();
        Mat? previous;
        lock (_latestSnapshotGate)
        {
            previous = _latestFrame;
            _latestFrame = copy;
            _latestObjects = objects.ToArray();
        }

        previous?.Dispose();
    }

    private void ClearLatestSnapshot()
    {
        Mat? previous;
        lock (_latestSnapshotGate)
        {
            previous = _latestFrame;
            _latestFrame = null;
            _latestObjects = [];
        }

        previous?.Dispose();
    }

    private static void Validate(CameraSourceOptions options)
    {
        if (options.Kind != CameraSourceKind.Device && string.IsNullOrWhiteSpace(options.Address))
        {
            throw new CoreException(CoreErrorCode.SourceAddressRequired);
        }
    }

    private sealed class LatestFrameBuffer : IDisposable
    {
        private readonly object _gate = new();
        private readonly AutoResetEvent _frameAvailable = new(false);
        private FramePacket? _latest;
        private Exception? _error;
        private bool _completed;

        public void Publish(Mat source, DateTimeOffset capturedAt)
        {
            var copy = source.Clone();
            FramePacket? previous;
            lock (_gate)
            {
                if (_completed)
                {
                    copy.Dispose();
                    return;
                }

                previous = _latest;
                _latest = new FramePacket(copy, capturedAt);
            }

            previous?.Dispose();
            _frameAvailable.Set();
        }

        public FramePacket? Take(CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_latest is not null)
                    {
                        var result = _latest;
                        _latest = null;
                        return result;
                    }

                    if (_completed)
                    {
                        if (_error is not null)
                        {
                            throw new CoreException(CoreErrorCode.RtspReaderFailed, _error);
                        }

                        return null;
                    }
                }

                var signaled = WaitHandle.WaitAny([_frameAvailable, cancellationToken.WaitHandle]);
                if (signaled == 1)
                {
                    // StopAsync intentionally signals this handle. Returning a
                    // completed buffer is enough for the processing loop to
                    // leave normally and avoids surfacing an expected
                    // OperationCanceledException in debuggers.
                    return null;
                }
            }
        }

        public void Complete(Exception? error = null)
        {
            lock (_gate)
            {
                _completed = true;
                _error = error;
            }

            _frameAvailable.Set();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _completed = true;
                _latest?.Dispose();
                _latest = null;
            }

            _frameAvailable.Dispose();
        }

        public sealed class FramePacket(Mat frame, DateTimeOffset capturedAt) : IDisposable
        {
            public Mat Frame { get; } = frame;
            public DateTimeOffset CapturedAt { get; } = capturedAt;

            public void Dispose() => Frame.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        ClearLatestSnapshot();
        _detector.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
