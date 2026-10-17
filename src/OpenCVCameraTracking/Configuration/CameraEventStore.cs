using System.IO;
using System.Globalization;
using System.Text.Json;
using OpenCVCameraTracking.Core.Logging;

namespace OpenCVCameraTracking.Configuration;

public sealed record CameraEventRecord(
    string Id,
    DateTimeOffset Timestamp,
    string SourceName,
    IReadOnlyList<string> Triggers,
    string? SnapshotFile,
    string? ClipFile,
    double DurationSeconds,
    string? MediaError);

public sealed class CameraEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly object _orphanSweepGate = new();
    private DateTime _lastOrphanSweepUtc = DateTime.MinValue;
    private bool _orphanSweepScheduled;

    public string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCVCameraTracking",
        "Events");

    private string IndexPath => Path.Combine(RootDirectory, "events.json");

    public IReadOnlyList<CameraEventRecord> Load(int retentionDays)
    {
        IReadOnlyList<CameraEventRecord> result;
        lock (_gate)
        {
            var records = ReadIndex();
            var normalizedDays = NormalizeRetentionDays(retentionDays);
            RemoveExpiredLocked(records, normalizedDays);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-normalizedDays);
            result = records.Where(record => record.Timestamp >= cutoff)
                .OrderByDescending(record => record.Timestamp)
                .ToArray();
        }

        ScheduleOrphanCleanup();
        return result;
    }

    public void Append(CameraEventRecord record)
    {
        lock (_gate)
        {
            var records = ReadIndex();
            records.RemoveAll(existing => string.Equals(existing.Id, record.Id, StringComparison.Ordinal));
            records.Add(record);
            WriteIndex(records);
        }
    }

    public bool Delete(string eventId)
    {
        lock (_gate)
        {
            var records = ReadIndex();
            var removed = records.Where(record => string.Equals(record.Id, eventId, StringComparison.Ordinal)).ToArray();
            if (removed.Length == 0)
            {
                return false;
            }

            records.RemoveAll(record => string.Equals(record.Id, eventId, StringComparison.Ordinal));
            WriteIndex(records);
            foreach (var record in removed)
            {
                DeleteMedia(record.SnapshotFile);
                DeleteMedia(record.ClipFile);
            }
        }

        ScheduleOrphanCleanup(force: true);
        return true;
    }

    public string? ResolveMediaPath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(RootDirectory) + Path.DirectorySeparatorChar;
            var path = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
            return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? path : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    public string CreateRelativeMediaPath(DateTimeOffset timestamp, string eventId, string extension)
    {
        var dayDirectory = timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        return Path.Combine(dayDirectory, $"{eventId}{extension}");
    }

    public string GetWritableMediaPath(string relativePath)
    {
        var root = Path.GetFullPath(RootDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(RootDirectory, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Event media path must remain inside the event storage directory.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private List<CameraEventRecord> ReadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<CameraEventRecord>>(File.ReadAllText(IndexPath), JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Unable to read event index: {exception.Message}");
            return [];
        }
    }

    private void WriteIndex(List<CameraEventRecord> records)
    {
        Directory.CreateDirectory(RootDirectory);
        var temporaryPath = IndexPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(records, JsonOptions));
        File.Move(temporaryPath, IndexPath, overwrite: true);
    }

    private void RemoveExpiredLocked(List<CameraEventRecord> records, int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        var expired = records.Where(record => record.Timestamp < cutoff).ToArray();
        if (expired.Length == 0)
        {
            return;
        }

        var changed = false;
        foreach (var record in expired)
        {
            var snapshotDeleted = DeleteMedia(record.SnapshotFile);
            var clipDeleted = DeleteMedia(record.ClipFile);
            if (snapshotDeleted && clipDeleted)
            {
                records.Remove(record);
                changed = true;
            }
        }

        if (changed)
        {
            WriteIndex(records);
        }
    }

    private void CleanupOrphanedMediaLocked(IReadOnlyCollection<CameraEventRecord> records)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return;
        }

        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            AddReferencedPath(referencedPaths, record.SnapshotFile);
            AddReferencedPath(referencedPaths, record.ClipFile);
        }

        var orphanCutoff = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var dayDirectory in Directory.EnumerateDirectories(RootDirectory))
            {
                var directoryName = Path.GetFileName(dayDirectory);
                if (!DateTime.TryParseExact(directoryName, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out _) ||
                    (File.GetAttributes(dayDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(dayDirectory))
                {
                    var extension = Path.GetExtension(path);
                    if (!string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(extension, ".avi", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                        referencedPaths.Contains(Path.GetFullPath(path)) ||
                        File.GetLastWriteTimeUtc(path) >= orphanCutoff)
                    {
                        continue;
                    }

                    DeleteMedia(Path.GetRelativePath(RootDirectory, path));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Unable to scan orphaned event media: {exception.Message}");
        }
    }

    private void ScheduleOrphanCleanup(bool force = false)
    {
        lock (_orphanSweepGate)
        {
            if (_orphanSweepScheduled ||
                (!force && DateTime.UtcNow - _lastOrphanSweepUtc < TimeSpan.FromHours(24)))
            {
                return;
            }

            _orphanSweepScheduled = true;
            _lastOrphanSweepUtc = DateTime.UtcNow;
        }

        _ = Task.Run(() =>
        {
            try
            {
                lock (_gate)
                {
                    var records = TryReadIndexForCleanup();
                    if (records is not null)
                    {
                        CleanupOrphanedMediaLocked(records);
                    }
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warn($"Unable to clean orphaned event media: {exception.Message}");
            }
            finally
            {
                lock (_orphanSweepGate)
                {
                    _orphanSweepScheduled = false;
                }
            }
        });
    }

    private List<CameraEventRecord>? TryReadIndexForCleanup()
    {
        if (!File.Exists(IndexPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<CameraEventRecord>>(File.ReadAllText(IndexPath), JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Skipping orphan cleanup because the event index could not be read: {exception.Message}");
            return null;
        }
    }

    private void AddReferencedPath(HashSet<string> paths, string? relativePath)
    {
        var path = ResolveMediaPath(relativePath);
        if (path is not null)
        {
            paths.Add(path);
        }
    }

    private bool DeleteMedia(string? relativePath)
    {
        var path = ResolveMediaPath(relativePath);
        if (path is null)
        {
            return true;
        }

        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Unable to remove event media; cleanup will be retried: {exception.Message}");
            return false;
        }
    }

    private static int NormalizeRetentionDays(int retentionDays) =>
        retentionDays is 3 or 7 or 14 or 30 ? retentionDays : 7;
}
