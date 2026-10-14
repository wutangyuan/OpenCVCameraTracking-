using System.IO;
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

    public string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenCVCameraTracking",
        "Events");

    private string IndexPath => Path.Combine(RootDirectory, "events.json");

    public IReadOnlyList<CameraEventRecord> Load(int retentionDays)
    {
        lock (_gate)
        {
            var records = ReadIndex();
            RemoveExpiredLocked(records, retentionDays);
            return records.OrderByDescending(record => record.Timestamp).ToArray();
        }
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

            return true;
        }
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
        var normalizedDays = retentionDays is 3 or 7 or 14 or 30 ? retentionDays : 7;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-normalizedDays);
        var expired = records.Where(record => record.Timestamp < cutoff).ToArray();
        if (expired.Length == 0)
        {
            return;
        }

        records.RemoveAll(record => record.Timestamp < cutoff);
        foreach (var record in expired)
        {
            DeleteMedia(record.SnapshotFile);
            DeleteMedia(record.ClipFile);
        }

        WriteIndex(records);
    }

    private void DeleteMedia(string? relativePath)
    {
        var path = ResolveMediaPath(relativePath);
        if (path is null)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warn($"Unable to remove expired event media: {exception.Message}");
        }
    }
}
