using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using OpenCVCameraTracking.Core;
using OpenCVCameraTracking.Core.Logging;
using OpenCvSharp;

namespace OpenCVCameraTracking.Configuration;

/// <summary>
/// Samples the live preview into a small asynchronous queue and groups nearby
/// event triggers into local clips. No frame encoding or disk I/O runs inline
/// with camera detection or the WPF dispatcher.
/// </summary>
public sealed class EventRecordingService : IAsyncDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PreEventDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PostEventDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FrameBufferDuration = PreEventDuration + PostEventDuration;
    private static readonly TimeSpan MaximumClipDuration = TimeSpan.FromMinutes(2);
    private const double ClipFramesPerSecond = 5;
    private const int JpegQuality = 82;

    private readonly CameraEventStore _store;
    private readonly string _sourceName;
    private readonly Channel<RawFrame> _pendingFrames = Channel.CreateBounded<RawFrame>(
        new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly object _gate = new();
    private readonly List<EncodedFrame> _preEventFrames = [];
    private readonly object _saveTasksGate = new();
    private readonly HashSet<Task> _saveTasks = [];
    private readonly Task _frameWorker;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private RecordingBuilder? _activeRecording;
    private int _frameWidth;
    private int _frameHeight;
    private bool _disposed;

    public EventRecordingService(CameraEventStore store, string sourceName)
    {
        _store = store;
        _sourceName = string.IsNullOrWhiteSpace(sourceName) ? "Camera" : sourceName.Trim();
        _frameWorker = Task.Run(ProcessFramesAsync);
    }

    public void ObserveFrame(FrameReadyEventArgs frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Stride < frame.Width * 4 ||
            frame.Pixels.Length < (long)frame.Stride * frame.Height)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || frame.CapturedAt - _lastSampleAt < SampleInterval)
            {
                return;
            }

            _lastSampleAt = frame.CapturedAt;
            _pendingFrames.Writer.TryWrite(new RawFrame(
                frame.Pixels,
                frame.Width,
                frame.Height,
                frame.Stride,
                frame.CapturedAt));
        }
    }

    public void TriggerEvent(DateTimeOffset timestamp, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return;
        }

        RecordingBuilder? completedRecording = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var eventTime = timestamp.ToUniversalTime();
            if (_activeRecording is not null &&
                (eventTime >= _activeRecording.Deadline ||
                 eventTime - _activeRecording.StartedAt >= MaximumClipDuration))
            {
                completedRecording = _activeRecording;
                _activeRecording = null;
            }

            if (_activeRecording is null)
            {
                var recording = new RecordingBuilder(eventTime, eventTime + PostEventDuration, _sourceName);
                recording.Triggers.Add(description.Trim());
                recording.Frames.AddRange(_preEventFrames.Where(frame =>
                    frame.Timestamp >= eventTime - PreEventDuration && frame.Timestamp <= recording.Deadline));
                _activeRecording = recording;
            }
            else
            {
                _activeRecording.Triggers.Add(description.Trim());
                var extendedDeadline = eventTime + PostEventDuration;
                if (extendedDeadline > _activeRecording.Deadline)
                {
                    _activeRecording.Deadline = extendedDeadline;
                }

                AppendBufferedFrames(_activeRecording);
            }
        }

        if (completedRecording is not null)
        {
            ScheduleSave(completedRecording);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingFrames.Writer.TryComplete();
        }

        await _frameWorker.ConfigureAwait(false);
        RecordingBuilder? unfinished;
        lock (_gate)
        {
            unfinished = _activeRecording;
            _activeRecording = null;
        }

        if (unfinished is not null)
        {
            ScheduleSave(unfinished);
        }

        while (true)
        {
            Task[] pendingSaves;
            lock (_saveTasksGate)
            {
                pendingSaves = _saveTasks.ToArray();
            }

            if (pendingSaves.Length == 0)
            {
                break;
            }

            await Task.WhenAll(pendingSaves).ConfigureAwait(false);
        }
    }

    private async Task ProcessFramesAsync()
    {
        await foreach (var rawFrame in _pendingFrames.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var encoded = EncodeFrame(rawFrame);
                AddEncodedFrame(encoded);
            }
            catch (Exception exception)
            {
                AppLogger.Error("Unable to encode a camera event frame", exception);
            }
        }
    }

    private void AddEncodedFrame(EncodedFrame frame)
    {
        RecordingBuilder? completedRecording = null;
        lock (_gate)
        {
            if (_frameWidth != frame.Width || _frameHeight != frame.Height)
            {
                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _preEventFrames.Clear();
                completedRecording = _activeRecording;
                _activeRecording = null;
            }

            _preEventFrames.Add(frame);
            var cutoff = frame.Timestamp - FrameBufferDuration;
            _preEventFrames.RemoveAll(candidate => candidate.Timestamp < cutoff);

            if (_activeRecording is not null && frame.Timestamp >= _activeRecording.StartedAt)
            {
                if (frame.Timestamp <= _activeRecording.Deadline &&
                    (_activeRecording.Frames.Count == 0 ||
                    _activeRecording.Frames[^1].Timestamp < frame.Timestamp)
                   )
                {
                    _activeRecording.Frames.Add(frame);
                }

                if (frame.Timestamp >= _activeRecording.Deadline)
                {
                    completedRecording = _activeRecording;
                    _activeRecording = null;
                }
            }
        }

        if (completedRecording is not null)
        {
            ScheduleSave(completedRecording);
        }
    }

    private void AppendBufferedFrames(RecordingBuilder recording)
    {
        var lastTimestamp = recording.Frames.Count == 0
            ? DateTimeOffset.MinValue
            : recording.Frames[^1].Timestamp;
        recording.Frames.AddRange(_preEventFrames.Where(frame =>
                frame.Timestamp > lastTimestamp && frame.Timestamp <= recording.Deadline)
            .OrderBy(frame => frame.Timestamp));
    }

    private static EncodedFrame EncodeFrame(RawFrame rawFrame)
    {
        var pinnedPixels = GCHandle.Alloc(rawFrame.Pixels, GCHandleType.Pinned);
        try
        {
            using var bgra = Mat.FromPixelData(
                rawFrame.Height,
                rawFrame.Width,
                MatType.CV_8UC4,
                pinnedPixels.AddrOfPinnedObject(),
                rawFrame.Stride);
            using var bgr = new Mat();
            Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
            Cv2.ImEncode(
                ".jpg",
                bgr,
                out var jpeg,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, JpegQuality));
            return new EncodedFrame(jpeg, rawFrame.Width, rawFrame.Height, rawFrame.Timestamp);
        }
        finally
        {
            pinnedPixels.Free();
        }
    }

    private void ScheduleSave(RecordingBuilder recording)
    {
        lock (_saveTasksGate)
        {
            var saveTask = Task.Run(() => SaveRecording(recording));
            _saveTasks.Add(saveTask);
            _ = saveTask.ContinueWith(
                completedTask =>
                {
                    lock (_saveTasksGate)
                    {
                        _saveTasks.Remove(completedTask);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void SaveRecording(RecordingBuilder recording)
    {
        var eventId = Guid.NewGuid().ToString("N");
        string? snapshotRelativePath = null;
        string? clipRelativePath = null;
        string? mediaError = null;
        var orderedFrames = recording.Frames
            .OrderBy(frame => frame.Timestamp)
            .GroupBy(frame => frame.Timestamp)
            .Select(group => group.First())
            .ToArray();

        try
        {
            if (orderedFrames.Length > 0)
            {
                var snapshot = orderedFrames
                    .OrderBy(frame => Math.Abs((frame.Timestamp - recording.StartedAt).TotalMilliseconds))
                    .First();
                snapshotRelativePath = _store.CreateRelativeMediaPath(recording.StartedAt, eventId, ".jpg");
                File.WriteAllBytes(_store.GetWritableMediaPath(snapshotRelativePath), snapshot.Jpeg);

                if (orderedFrames.Length >= 2)
                {
                    clipRelativePath = WriteClip(recording.StartedAt, eventId, orderedFrames);
                    if (clipRelativePath is null)
                    {
                        mediaError = "Video encoding is unavailable; the snapshot was saved.";
                    }
                }
                else
                {
                    mediaError = "Not enough video frames were available; the snapshot was saved.";
                }
            }
            else
            {
                mediaError = "No camera frames were available for this event.";
            }
        }
        catch (Exception exception)
        {
            mediaError = exception.Message;
            AppLogger.Error("Unable to save camera event media", exception);
        }

        var record = new CameraEventRecord(
            eventId,
            recording.StartedAt,
            recording.SourceName,
            recording.Triggers.Distinct(StringComparer.Ordinal).ToArray(),
            snapshotRelativePath,
            clipRelativePath,
            orderedFrames.Length / ClipFramesPerSecond,
            mediaError);
        try
        {
            _store.Append(record);
        }
        catch (Exception exception)
        {
            AppLogger.Error("Unable to persist camera event metadata", exception);
        }
    }

    private string? WriteClip(DateTimeOffset timestamp, string eventId, IReadOnlyList<EncodedFrame> frames)
    {
        var mp4RelativePath = _store.CreateRelativeMediaPath(timestamp, eventId, ".mp4");
        var mp4Path = _store.GetWritableMediaPath(mp4RelativePath);
        if (TryWriteClipSafely(mp4Path, frames, VideoWriter.FourCC('m', 'p', '4', 'v')))
        {
            return mp4RelativePath;
        }

        TryDeleteFile(mp4Path);
        var aviRelativePath = _store.CreateRelativeMediaPath(timestamp, eventId, ".avi");
        var aviPath = _store.GetWritableMediaPath(aviRelativePath);
        if (TryWriteClipSafely(aviPath, frames, VideoWriter.FourCC('M', 'J', 'P', 'G')))
        {
            return aviRelativePath;
        }

        TryDeleteFile(aviPath);
        return null;
    }

    private static bool TryWriteClip(string path, IReadOnlyList<EncodedFrame> frames, int codec)
    {
        var first = Cv2.ImDecode(frames[0].Jpeg, ImreadModes.Color);
        if (first.Empty())
        {
            first.Dispose();
            return false;
        }

        using (first)
        using (var writer = new VideoWriter(
            path,
            codec,
            ClipFramesPerSecond,
            new Size(first.Width, first.Height),
            isColor: true))
        {
            if (!writer.IsOpened())
            {
                return false;
            }

            writer.Write(first);
            for (var index = 1; index < frames.Count; index++)
            {
                using var decoded = Cv2.ImDecode(frames[index].Jpeg, ImreadModes.Color);
                if (!decoded.Empty() && decoded.Size() == first.Size())
                {
                    writer.Write(decoded);
                }
            }
        }

        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    private static bool TryWriteClipSafely(string path, IReadOnlyList<EncodedFrame> frames, int codec)
    {
        try
        {
            return TryWriteClip(path, frames, codec);
        }
        catch (Exception exception)
        {
            AppLogger.Warn($"Unable to encode event clip with the requested codec: {exception.Message}");
            TryDeleteFile(path);
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Unable to remove incomplete event clip: {exception.Message}");
        }
    }

    private sealed record RawFrame(byte[] Pixels, int Width, int Height, int Stride, DateTimeOffset Timestamp);

    private sealed record EncodedFrame(byte[] Jpeg, int Width, int Height, DateTimeOffset Timestamp);

    private sealed class RecordingBuilder(DateTimeOffset startedAt, DateTimeOffset deadline, string sourceName)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DateTimeOffset Deadline { get; set; } = deadline;
        public string SourceName { get; } = sourceName;
        public List<string> Triggers { get; } = [];
        public List<EncodedFrame> Frames { get; } = [];
    }
}
