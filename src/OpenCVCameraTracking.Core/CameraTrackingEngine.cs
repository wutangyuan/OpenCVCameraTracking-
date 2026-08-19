using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCVCameraTracking.Core.Camera;
using OpenCVCameraTracking.Core.Detection;
using OpenCVCameraTracking.Core.Tracking;
using OpenCvSharp;

namespace OpenCVCameraTracking.Core;

public sealed class FrameReadyEventArgs(
    byte[] pixels,
    int width,
    int height,
    int stride,
    IReadOnlyList<TrackedObject> objects,
    double framesPerSecond) : EventArgs
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Stride { get; } = stride;
    public IReadOnlyList<TrackedObject> Objects { get; } = objects;
    public double FramesPerSecond { get; } = framesPerSecond;
}

public sealed class CameraTrackingEngine : IAsyncDisposable
{
    private static readonly object FfmpegEnvironmentLock = new();
    private readonly IObjectDetector _detector;
    private readonly IouMultiObjectTracker _tracker;
    private readonly int _detectionInterval;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private bool _disposed;

    public CameraTrackingEngine(
        IObjectDetector detector,
        int detectionInterval = 2,
        IouMultiObjectTracker? tracker = null)
    {
        _detector = detector;
        _detectionInterval = Math.Max(1, detectionInterval);
        _tracker = tracker ?? new IouMultiObjectTracker();
    }

    public event EventHandler<FrameReadyEventArgs>? FrameReady;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<Exception>? Faulted;

    public bool IsRunning => _worker is { IsCompleted: false };

    public Task StartAsync(CameraSourceOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            throw new InvalidOperationException("The camera tracking engine is already running.");
        }

        Validate(options);
        _tracker.Reset();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => CaptureLoop(options, _cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
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
            StatusChanged?.Invoke(this, "Stopped");
        }
    }

    private void CaptureLoop(CameraSourceOptions options, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Kind == CameraSourceKind.Stream && options.LowLatencyMode)
            {
                LowLatencyStreamLoop(options, cancellationToken);
            }
            else
            {
                SequentialCaptureLoop(options, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
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

        while (!cancellationToken.IsCancellationRequested)
        {
            using var capture = OpenCapture(options);
            StatusChanged?.Invoke(this, "Connected");
            var consecutiveFailures = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty())
                {
                    if (++consecutiveFailures < 8)
                    {
                        Thread.Sleep(30);
                        continue;
                    }

                    if (options.Kind == CameraSourceKind.File)
                    {
                        StatusChanged?.Invoke(this, "FileEnded");
                        return;
                    }

                    StatusChanged?.Invoke(this, "Reconnecting");
                    break;
                }

                consecutiveFailures = 0;
                ProcessFrame(frame, ref frameNumber, ref frameCounter, ref measuredFps, fpsTimer);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    private void LowLatencyStreamLoop(CameraSourceOptions options, CancellationToken cancellationToken)
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
                using var frame = frameBuffer.Take(cancellationToken);
                if (frame is null)
                {
                    break;
                }

                ProcessFrame(frame, ref frameNumber, ref frameCounter, ref measuredFps, fpsTimer);
            }
        }
        finally
        {
            localCancellation.Cancel();
            try
            {
                reader.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
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
            while (!cancellationToken.IsCancellationRequested)
            {
                using var capture = OpenCapture(options);
                StatusChanged?.Invoke(this, "Connected");
                var consecutiveFailures = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!capture.Read(frame) || frame.Empty())
                    {
                        if (++consecutiveFailures < 8)
                        {
                            Thread.Sleep(15);
                            continue;
                        }

                        StatusChanged?.Invoke(this, "Reconnecting");
                        break;
                    }

                    consecutiveFailures = 0;
                    frameBuffer.Publish(frame);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(500));
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
        Stopwatch fpsTimer)
    {
        frameNumber++;
        frameCounter++;
        var objects = frameNumber % _detectionInterval == 0 || _tracker.Current.Count == 0
            ? _tracker.Update(_detector.Detect(frame))
            : _tracker.Current;

        DrawTracks(frame, objects);
        if (fpsTimer.ElapsedMilliseconds >= 1_000)
        {
            measuredFps = frameCounter * 1_000d / fpsTimer.ElapsedMilliseconds;
            frameCounter = 0;
            fpsTimer.Restart();
        }

        RaiseFrame(frame, objects, measuredFps);
    }

    private static VideoCapture OpenCapture(CameraSourceOptions options)
    {
        VideoCapture capture;
        if (options.Kind == CameraSourceKind.Device)
        {
            capture = new VideoCapture(options.DeviceIndex, VideoCaptureAPIs.DSHOW);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                capture = new VideoCapture(options.DeviceIndex, VideoCaptureAPIs.MSMF);
            }
        }
        else
        {
            var address = options.Address!;
            capture = OpenFfmpeg(address, options);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                capture = new VideoCapture(address, VideoCaptureAPIs.ANY);
            }
        }

        if (!capture.IsOpened())
        {
            capture.Dispose();
            throw new InvalidOperationException("Unable to open the video source. Check the device, URL, credentials and network.");
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
            var color = ColorForId(item.Id);
            Cv2.Rectangle(frame, item.Box, color, 2, LineTypes.AntiAlias);
            var caption = $"{item.Label} #{item.Id}  {item.Confidence:P0}";
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

    private static Scalar ColorForId(int id)
    {
        var hue = (id * 67) % 180;
        using var hsv = new Mat(1, 1, MatType.CV_8UC3, new Scalar(hue, 210, 245));
        using var bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        var pixel = bgr.At<Vec3b>(0, 0);
        return new Scalar(pixel.Item0, pixel.Item1, pixel.Item2);
    }

    private void RaiseFrame(Mat bgrFrame, IReadOnlyList<TrackedObject> objects, double framesPerSecond)
    {
        using var bgra = new Mat();
        Cv2.CvtColor(bgrFrame, bgra, ColorConversionCodes.BGR2BGRA);
        var stride = checked((int)bgra.Step());
        var pixels = new byte[checked(stride * bgra.Height)];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        FrameReady?.Invoke(
            this,
            new FrameReadyEventArgs(pixels, bgra.Width, bgra.Height, stride, objects, framesPerSecond));
    }

    private static void Validate(CameraSourceOptions options)
    {
        if (options.Kind != CameraSourceKind.Device && string.IsNullOrWhiteSpace(options.Address))
        {
            throw new ArgumentException("A network stream or video file requires an address.", nameof(options));
        }
    }

    private sealed class LatestFrameBuffer : IDisposable
    {
        private readonly object _gate = new();
        private readonly AutoResetEvent _frameAvailable = new(false);
        private Mat? _latest;
        private Exception? _error;
        private bool _completed;

        public void Publish(Mat source)
        {
            var copy = source.Clone();
            Mat? previous;
            lock (_gate)
            {
                if (_completed)
                {
                    copy.Dispose();
                    return;
                }

                previous = _latest;
                _latest = copy;
            }

            previous?.Dispose();
            _frameAvailable.Set();
        }

        public Mat? Take(CancellationToken cancellationToken)
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
                            throw new InvalidOperationException("RTSP reader failed.", _error);
                        }

                        return null;
                    }
                }

                var signaled = WaitHandle.WaitAny([_frameAvailable, cancellationToken.WaitHandle]);
                if (signaled == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
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
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _detector.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
